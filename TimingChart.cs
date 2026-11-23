using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace LogAnalyzer
{
    internal sealed class TimingInterval
    {
        public LogEntry Start;
        public LogEntry End;
        public double ActualMs;
        public double ExpectedMs;
        public string Comparison;
        public bool MissingEnd;
    }

    internal sealed class TimingLane
    {
        public KeywordRule Rule;
        public int Number;
        public List<LogEntry> Entries = new List<LogEntry>();
        public List<TimingInterval> Anomalies = new List<TimingInterval>();
        public HashSet<LogEntry> AbnormalEntries = new HashSet<LogEntry>();
    }

    internal sealed class TimingData
    {
        public const int MinimumAutomaticIntervals = 5;
        public List<TimingLane> Lanes = new List<TimingLane>();
        public DateTime Minimum = DateTime.Today;
        public DateTime Maximum = DateTime.Today.AddSeconds(1);
        public int EventCount;
        public int AnomalyCount;

        // 시간순 로그에서 활성 키워드의 모든 일치 이벤트를 수집한다.
        public static TimingData Build(List<LogEntry> entries, List<KeywordRule> rules, CancellationToken token)
        {
            KeywordSettings.ValidateLinks(rules);
            TimingData data = new TimingData();
            TimingLane[] indexedLanes = new TimingLane[rules.Count];
            bool hasTime = false;
            foreach (LogEntry entry in entries)
            {
                if (!entry.Timestamp.HasValue) { continue; }
                if (!hasTime) { data.Minimum = entry.Timestamp.Value; hasTime = true; }
                data.Maximum = entry.Timestamp.Value;
            }
            if (data.Maximum <= data.Minimum) { data.Maximum = data.Minimum.AddMilliseconds(1); }
            for (int ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                KeywordRule rule = rules[ruleIndex];
                token.ThrowIfCancellationRequested();
                if (!rule.Enabled) { continue; }
                TimingLane lane = new TimingLane { Rule = rule, Number = ruleIndex + 1 };
                indexedLanes[ruleIndex] = lane;
                StringComparison comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                foreach (LogEntry entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Timestamp.HasValue && entry.Text.IndexOf(rule.Text, comparison) >= 0) { lane.Entries.Add(entry); }
                }
                FindIntervalAnomalies(lane, token);
                data.EventCount += lane.Entries.Count;
                data.Lanes.Add(lane);
            }
            foreach (TimingLane lane in data.Lanes)
            {
                if (lane.Rule.LinkedKeywordNumber > 0)
                {
                    int targetIndex = lane.Rule.LinkedKeywordNumber - 1;
                    FindPairAnomalies(lane, rules[targetIndex], indexedLanes[targetIndex], entries, token);
                }
                data.AnomalyCount += lane.Anomalies.Count;
            }
            return data;
        }

        // Task와 종류를 나누지 않고 통합 로그의 등록 키워드 A→B를 FIFO로 연결한다.
        private static void FindPairAnomalies(TimingLane lane, KeywordRule target, TimingLane targetLane, List<LogEntry> entries, CancellationToken token)
        {
            double expected = lane.Rule.LinkedExpectedIntervalMs;
            List<TimingInterval> pairs = CollectKeywordPairs(lane, target, entries, 0, 0, token);
            int unmatchedStarts = lane.Entries.Count - pairs.Count;
            if (expected == 0)
            {
                List<double> positive = new List<double>();
                foreach (TimingInterval pair in pairs)
                { token.ThrowIfCancellationRequested(); if (pair.ActualMs > 0) { positive.Add(pair.ActualMs); } }
                if (positive.Count < MinimumAutomaticIntervals) { return; }
                positive.Sort();
                int middle = positive.Count / 2;
                expected = positive[middle];
                if (positive.Count % 2 == 0) { expected = (positive[middle - 1] + expected) / 2; }
                if (unmatchedStarts > 0)
                {
                    // 누락 뒤로 밀린 FIFO가 자동 기준을 키우지 않도록 역방향 FIFO 중앙값도 비교한다.
                    // 개수가 맞는 로그는 기존 FIFO 중앙값을 그대로 사용한다.
                    Queue<LogEntry> pendingEnds = new Queue<LogEntry>();
                    List<double> reversePositive = new List<double>();
                    StringComparison comparison = target.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    int startIndex = lane.Entries.Count - 1;
                    for (int index = entries.Count - 1; index >= 0; index--)
                    {
                        token.ThrowIfCancellationRequested();
                        LogEntry entry = entries[index];
                        if (!entry.Timestamp.HasValue) { continue; }
                        if (startIndex >= 0 && Object.ReferenceEquals(lane.Entries[startIndex], entry))
                        {
                            startIndex--;
                            if (pendingEnds.Count > 0)
                            {
                                double actual = (pendingEnds.Dequeue().Timestamp.Value - entry.Timestamp.Value).TotalMilliseconds;
                                if (actual > 0) { reversePositive.Add(actual); }
                            }
                        }
                        if (entry.Text.IndexOf(target.Text, comparison) >= 0) { pendingEnds.Enqueue(entry); }
                    }
                    if (reversePositive.Count >= MinimumAutomaticIntervals)
                    {
                        reversePositive.Sort();
                        int reverseMiddle = reversePositive.Count / 2;
                        double reverseExpected = reversePositive[reverseMiddle];
                        if (reversePositive.Count % 2 == 0) { reverseExpected = (reversePositive[reverseMiddle - 1] + reverseExpected) / 2; }
                        expected = Math.Min(expected, reverseExpected);
                    }
                }
            }
            if (unmatchedStarts > 0) { pairs = CollectKeywordPairs(lane, target, entries, expected, unmatchedStarts, token); }
            string description = lane.Number + ". " + lane.Rule.Text + " → " + lane.Rule.LinkedKeywordNumber + ". " + target.Text;
            double allowance = expected * lane.Rule.LinkedTolerancePercent / 100;
            foreach (TimingInterval pair in pairs)
            {
                token.ThrowIfCancellationRequested();
                if (!pair.MissingEnd && Math.Abs(pair.ActualMs - expected) <= allowance) { continue; }
                pair.ExpectedMs = expected; pair.Comparison = description;
                lane.Anomalies.Add(pair); lane.AbnormalEntries.Add(pair.Start); lane.AbnormalEntries.Add(pair.End);
                if (targetLane != null && !pair.MissingEnd) { targetLane.AbnormalEntries.Add(pair.End); }
            }
            lane.Anomalies.Sort(delegate(TimingInterval left, TimingInterval right) { return left.Start.Timestamp.Value.CompareTo(right.Start.Timestamp.Value); });
        }

        // FIFO에서 남는 시작 로그가 있을 때만 허용 범위의 다음 시작 로그로 연결을 복구한다.
        // 복구 중 건너뛴 시작 로그는 종료 시각을 추정하지 않고 해당 지점만 이상으로 남긴다.
        private static List<TimingInterval> CollectKeywordPairs(TimingLane lane, KeywordRule target, List<LogEntry> entries, double expected, int unmatchedStarts, CancellationToken token)
        {
            List<TimingInterval> pairs = new List<TimingInterval>();
            StringComparison comparison = target.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            double allowance = expected * lane.Rule.LinkedTolerancePercent / 100;
            int availableStarts = 0;
            int pendingStart = 0;
            int recoveryIndex = 0;
            foreach (LogEntry entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!entry.Timestamp.HasValue) { continue; }
                bool isStart = availableStarts < lane.Entries.Count && Object.ReferenceEquals(lane.Entries[availableStarts], entry);
                bool isEnd = entry.Text.IndexOf(target.Text, comparison) >= 0;
                // 종료를 먼저 소비하여 같은 행 또는 아직 나타나지 않은 시작 로그와 연결하지 않는다.
                if (isEnd && pendingStart < availableStarts)
                {
                    LogEntry start = lane.Entries[pendingStart];
                    double actual = (entry.Timestamp.Value - start.Timestamp.Value).TotalMilliseconds;
                    if (unmatchedStarts > 0 && expected > 0 && actual > expected + allowance)
                    {
                        // 후보 인덱스도 앞으로만 이동하여 누락이 많아도 전체 검색은 선형으로 유지한다.
                        recoveryIndex = Math.Max(recoveryIndex, pendingStart + 1);
                        while (recoveryIndex < availableStarts)
                        {
                            token.ThrowIfCancellationRequested();
                            double candidateMs = (entry.Timestamp.Value - lane.Entries[recoveryIndex].Timestamp.Value).TotalMilliseconds;
                            if (candidateMs <= expected + allowance) { break; }
                            recoveryIndex++;
                        }
                        if (recoveryIndex < availableStarts)
                        {
                            double candidateMs = (entry.Timestamp.Value - lane.Entries[recoveryIndex].Timestamp.Value).TotalMilliseconds;
                            if (candidateMs >= expected - allowance && recoveryIndex - pendingStart <= unmatchedStarts)
                            {
                                while (pendingStart < recoveryIndex)
                                {
                                    token.ThrowIfCancellationRequested();
                                    LogEntry missing = lane.Entries[pendingStart++];
                                    unmatchedStarts--;
                                    pairs.Add(new TimingInterval { Start = missing, End = missing, MissingEnd = true });
                                }
                                start = lane.Entries[pendingStart];
                                actual = candidateMs;
                            }
                        }
                    }
                    pairs.Add(new TimingInterval { Start = start, End = entry, ActualMs = actual });
                    pendingStart++;
                }
                if (isStart) { availableStarts++; }
            }
            return pairs;
        }

        // 같은 프로세스와 종류의 연속 간격을 중앙값 또는 지정 주기와 비교한다.
        private static void FindIntervalAnomalies(TimingLane lane, CancellationToken token)
        {
            if (!lane.Rule.CheckIntervals) { return; }
            Dictionary<Tuple<string, string>, List<LogEntry>> sources = new Dictionary<Tuple<string, string>, List<LogEntry>>();
            foreach (LogEntry entry in lane.Entries)
            {
                token.ThrowIfCancellationRequested();
                Tuple<string, string> key = Tuple.Create(entry.Process, entry.Kind);
                List<LogEntry> source;
                if (!sources.TryGetValue(key, out source)) { source = new List<LogEntry>(); sources.Add(key, source); }
                source.Add(entry);
            }
            foreach (List<LogEntry> source in sources.Values)
            {
                token.ThrowIfCancellationRequested();
                double expected = lane.Rule.ExpectedIntervalMs;
                if (expected == 0)
                {
                    List<double> positiveIntervals = new List<double>();
                    for (int index = 1; index < source.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        double interval = (source[index].Timestamp.Value - source[index - 1].Timestamp.Value).TotalMilliseconds;
                        if (interval > 0) { positiveIntervals.Add(interval); }
                    }
                    if (positiveIntervals.Count < MinimumAutomaticIntervals) { continue; }
                    positiveIntervals.Sort();
                    int middle = positiveIntervals.Count / 2;
                    expected = positiveIntervals[middle];
                    if (positiveIntervals.Count % 2 == 0) { expected = (positiveIntervals[middle - 1] + expected) / 2; }
                }
                double allowance = expected * lane.Rule.TolerancePercent / 100;
                for (int index = 1; index < source.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    LogEntry start = source[index - 1];
                    LogEntry end = source[index];
                    double actual = (end.Timestamp.Value - start.Timestamp.Value).TotalMilliseconds;
                    if (Math.Abs(actual - expected) <= allowance) { continue; }
                    lane.Anomalies.Add(new TimingInterval { Start = start, End = end, ActualMs = actual, ExpectedMs = expected });
                    lane.AbnormalEntries.Add(start); lane.AbnormalEntries.Add(end);
                }
            }
            lane.Anomalies.Sort(delegate(TimingInterval left, TimingInterval right)
            {
                return left.Start.Timestamp.Value.CompareTo(right.Start.Timestamp.Value);
            });
        }
    }

    internal sealed class TimingChart : Control
    {
        private const int LeftGutter = 194;
        private const int TopGutter = 38;
        private const int LaneHeight = 36;
        private const int BottomGutter = 28;
        private const int ScrollRange = 100000;
        private const int HitRadius = 7;
        private readonly VScrollBar m_vScroll = new VScrollBar();
        private readonly HScrollBar m_hScroll = new HScrollBar();
        private readonly ToolTip m_toolTip = new ToolTip();
        private readonly ContextMenuStrip m_rangeMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem m_menuStart = new ToolStripMenuItem("시작 지점 지정");
        private readonly ToolStripMenuItem m_menuEnd = new ToolStripMenuItem("끝 지점 지정");
        private readonly ToolStripMenuItem m_menuExport = new ToolStripMenuItem("선택 구간 내보내기…");
        private DateTime? m_contextTime;
        private DateTime? m_rangeStart;
        private DateTime? m_rangeEnd;
        private TimingData m_data = new TimingData();
        private double m_dViewStart;
        private double m_dViewSpan = 1;
        private bool m_bDark;
        private bool m_bUpdatingScroll;
        private Point m_ptMouseDown;
        private double m_dDragStart;
        private bool m_bDragging;
        private bool m_bMoved;
        private LogEntry m_hoverEntry;
        private TimingInterval m_hoverInterval;
        private LogEntry m_selectedEntry;
        private Bitmap m_chartImage;
        private bool m_bRedrawChart = true;
        public event Action<LogEntry> EntrySelected;
        public event Action<DateTime, DateTime> ExportRequested;

        // 스크롤 가능한 키워드 레인과 확대 가능한 시간 축을 초기화한다.
        public TimingChart()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            m_vScroll.Dock = DockStyle.Right;
            m_hScroll.Dock = DockStyle.Bottom;
            Controls.Add(m_vScroll);
            Controls.Add(m_hScroll);
            m_vScroll.ValueChanged += delegate { InvalidateChartImage(); };
            m_hScroll.ValueChanged += delegate
            {
                if (m_bUpdatingScroll) { return; }
                m_dViewStart = m_hScroll.Value / (double)ScrollRange * Math.Max(0, TotalSeconds - m_dViewSpan);
                InvalidateChartImage();
            };
            AccessibleName = "타이밍 차트";
            AccessibleDescription = "마우스 휠 확대, 드래그 이동, 이벤트 클릭";
            ToolStripMenuItem clear = new ToolStripMenuItem("구간 선택 해제");
            m_rangeMenu.Items.AddRange(new ToolStripItem[] { m_menuStart, m_menuEnd, new ToolStripSeparator(), m_menuExport, clear });
            ContextMenuStrip = m_rangeMenu;
            m_rangeMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                m_menuStart.Enabled = m_contextTime.HasValue; m_menuEnd.Enabled = m_contextTime.HasValue;
                m_menuExport.Enabled = m_rangeStart.HasValue && m_rangeEnd.HasValue && m_rangeStart.Value <= m_rangeEnd.Value;
                clear.Enabled = m_rangeStart.HasValue || m_rangeEnd.HasValue;
                string time = m_contextTime.HasValue ? " (" + m_contextTime.Value.ToString("HH:mm:ss.fff") + ")" : "";
                m_menuStart.Text = "시작 지점 지정" + time; m_menuEnd.Text = "끝 지점 지정" + time;
            };
            m_menuStart.Click += delegate { if (m_contextTime.HasValue) { SetRangeBoundary(m_contextTime.Value, true); } };
            m_menuEnd.Click += delegate { if (m_contextTime.HasValue) { SetRangeBoundary(m_contextTime.Value, false); } };
            clear.Click += delegate { m_rangeStart = null; m_rangeEnd = null; m_menuExport.Enabled = false; InvalidateChartImage(); };
            m_menuExport.Enabled = false;
            m_menuExport.Click += delegate
            {
                if (m_rangeStart.HasValue && m_rangeEnd.HasValue && m_rangeStart.Value <= m_rangeEnd.Value && ExportRequested != null)
                { ExportRequested(m_rangeStart.Value, m_rangeEnd.Value); }
            };
        }

        // 두 경계는 정확한 로그 시각을 유지하며 역순이면 내보내기를 비활성화한다.
        private void SetRangeBoundary(DateTime timestamp, bool start)
        {
            if (start) { m_rangeStart = timestamp; } else { m_rangeEnd = timestamp; }
            m_menuExport.Enabled = m_rangeStart.HasValue && m_rangeEnd.HasValue && m_rangeStart.Value <= m_rangeEnd.Value;
            InvalidateChartImage();
        }

        private double TotalSeconds { get { return Math.Max(0.001, (m_data.Maximum - m_data.Minimum).TotalSeconds); } }
        private int PlotWidth { get { return Math.Max(1, ClientSize.Width - LeftGutter - m_vScroll.Width - 18); } }
        private int VisibleLanes { get { return Math.Max(1, (ClientSize.Height - TopGutter - BottomGutter - m_hScroll.Height) / LaneHeight); } }

        // 초 오프셋을 틱 단위로 변환해 끝 초가 반올림으로 다음 초에 표시되지 않게 한다.
        private DateTime TimeAtOffset(double seconds)
        {
            if (seconds <= 0) { return m_data.Minimum; }
            if (seconds >= TotalSeconds) { return m_data.Maximum; }
            long ticks = (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
            return m_data.Minimum.AddTicks(Math.Min(ticks, (m_data.Maximum - m_data.Minimum).Ticks));
        }

        // 백그라운드에서 계산한 차트 데이터를 UI 스레드에서 반영한다.
        public void SetData(TimingData data)
        {
            m_data = data;
            m_contextTime = null; m_rangeStart = null; m_rangeEnd = null; m_menuExport.Enabled = false;
            m_vScroll.Value = 0;
            m_selectedEntry = null;
            m_hoverEntry = null; m_hoverInterval = null;
            m_toolTip.SetToolTip(this, "");
            ResetView();
        }

        // 차트 전체 시간 구간을 화면에 맞춘다.
        public void ResetView()
        {
            m_dViewStart = 0;
            m_dViewSpan = TotalSeconds;
            UpdateScroll();
            InvalidateChartImage();
        }

        // 현재 중심을 유지하며 시간 축을 확대하거나 축소한다.
        public void Zoom(double factor)
        {
            ZoomAt(factor, 0.5);
        }

        // 포인터의 시간 위치를 기준으로 확대하여 관심 이벤트를 유지한다.
        private void ZoomAt(double factor, double anchor)
        {
            double next = Math.Max(Math.Min(0.001, TotalSeconds), Math.Min(TotalSeconds, m_dViewSpan * factor));
            m_dViewStart += (m_dViewSpan - next) * anchor;
            m_dViewSpan = next;
            ClampView();
            UpdateScroll();
            InvalidateChartImage();
        }

        // 일반/다크 테마를 적용한다.
        public void ApplyTheme(bool dark)
        {
            m_bDark = dark;
            BackColor = dark ? Color.FromArgb(23, 31, 43) : Color.White;
            ForeColor = dark ? Color.FromArgb(225, 232, 242) : Color.FromArgb(35, 48, 65);
            InvalidateChartImage();
        }

        // 선택 시각을 강조하고 확대 구간 밖이면 확대 배율을 유지한 채 해당 시각으로 이동한다.
        public void SelectEntry(LogEntry entry)
        {
            m_selectedEntry = entry;
            if (entry != null && entry.Timestamp.HasValue)
            {
                double seconds = (entry.Timestamp.Value - m_data.Minimum).TotalSeconds;
                if (seconds < m_dViewStart || seconds > m_dViewStart + m_dViewSpan)
                {
                    m_dViewStart = seconds - m_dViewSpan / 2;
                    ClampView(); UpdateScroll(); m_bRedrawChart = true;
                }
            }
            Invalidate();
        }

        // 구간의 시작을 실제 로그 범위에 제한한다.
        private void ClampView()
        {
            m_dViewStart = Math.Max(0, Math.Min(Math.Max(0, TotalSeconds - m_dViewSpan), m_dViewStart));
        }

        // 레인 수와 확대 범위에 맞춰 스크롤바의 유효 범위를 갱신한다.
        private void UpdateScroll()
        {
            m_bUpdatingScroll = true;
            m_vScroll.LargeChange = VisibleLanes;
            m_vScroll.Maximum = Math.Max(0, m_data.Lanes.Count - 1);
            m_vScroll.Value = Math.Min(m_vScroll.Value, Math.Max(0, m_data.Lanes.Count - VisibleLanes));
            m_vScroll.Enabled = m_data.Lanes.Count > VisibleLanes;
            m_hScroll.LargeChange = 1000;
            m_hScroll.Maximum = ScrollRange + m_hScroll.LargeChange - 1;
            m_hScroll.Enabled = m_dViewSpan < TotalSeconds;
            double remaining = TotalSeconds - m_dViewSpan;
            m_hScroll.Value = remaining > 0 ? Math.Min(ScrollRange, Math.Max(0, (int)(m_dViewStart / remaining * ScrollRange))) : 0;
            m_bUpdatingScroll = false;
        }

        // 크기가 바뀌면 레인 스크롤 범위를 다시 계산한다.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (m_vScroll != null) { UpdateScroll(); }
        }

        // 시간축, 데이터 또는 표시 옵션이 바뀌면 다음 그리기에서 차트 배경을 갱신한다.
        private void InvalidateChartImage()
        {
            m_bRedrawChart = true;
            Invalidate();
        }

        // 글꼴이 변경된 경우 캐시에 이전 레이블이 남지 않게 한다.
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            InvalidateChartImage();
        }

        // 동일 시간축에서는 저장된 차트 위에 선택 커서만 그린다.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) { return; }
            if (m_chartImage == null || m_chartImage.Size != ClientSize || m_chartImage.HorizontalResolution != e.Graphics.DpiX)
            {
                if (m_chartImage != null) { m_chartImage.Dispose(); }
                m_chartImage = new Bitmap(ClientSize.Width, ClientSize.Height);
                m_chartImage.SetResolution(e.Graphics.DpiX, e.Graphics.DpiY);
                m_bRedrawChart = true;
            }
            if (m_bRedrawChart)
            {
                using (Graphics chartGraphics = Graphics.FromImage(m_chartImage))
                {
                    chartGraphics.Clear(BackColor);
                    DrawChart(chartGraphics);
                }
                m_bRedrawChart = false;
            }
            e.Graphics.DrawImageUnscaled(m_chartImage, 0, 0);
            DrawChartLabels(e.Graphics);
            if (m_selectedEntry == null || !m_selectedEntry.Timestamp.HasValue) { return; }
            double offset = (m_selectedEntry.Timestamp.Value - m_data.Minimum).TotalSeconds - m_dViewStart;
            if (offset < 0 || offset > m_dViewSpan) { return; }
            int x = LeftGutter + (int)(offset / m_dViewSpan * PlotWidth);
            using (Pen marker = new Pen(m_bDark ? Color.FromArgb(70, 210, 230) : Color.FromArgb(0, 120, 155)))
            { e.Graphics.DrawLine(marker, x, TopGutter - 3, x, ClientSize.Height - BottomGutter - m_hScroll.Height); }
        }

        // 시간과 키워드 문자는 화면의 실제 글꼴과 DPI로 그린다.
        private void DrawChartLabels(Graphics graphics)
        {
            int divisions = Math.Max(2, PlotWidth / 140);
            for (int index = 0; index <= divisions; index++)
            {
                int x = LeftGutter + PlotWidth * index / divisions;
                DateTime timestamp = TimeAtOffset(m_dViewStart + m_dViewSpan * index / divisions);
                string format = m_dViewSpan < 10 ? "HH:mm:ss.fff" : "HH:mm:ss";
                int labelX = x - 50;
                if (index == divisions) { labelX = x - 100; }
                TextRenderer.DrawText(graphics, timestamp.ToString(format, CultureInfo.InvariantCulture), Font, new Rectangle(labelX, 8, 100, 25), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            int lastLane = Math.Min(m_data.Lanes.Count, m_vScroll.Value + VisibleLanes);
            for (int laneIndex = m_vScroll.Value; laneIndex < lastLane; laneIndex++)
            {
                TimingLane lane = m_data.Lanes[laneIndex];
                int y = TopGutter + (laneIndex - m_vScroll.Value) * LaneHeight + LaneHeight / 2;
                TextRenderer.DrawText(graphics, lane.Number + ". " + lane.Rule.Text, Font, new Rectangle(34, y - 13, LeftGutter - 42, 26), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (m_data.Lanes.Count == 0)
            { TextRenderer.DrawText(graphics, "키워드 없음", Font, new Point(20, TopGutter + 12), ForeColor); }
            string range = TimeAtOffset(m_dViewStart).ToString("yyyy-MM-dd HH:mm:ss.fff") + "  —  " + TimeAtOffset(m_dViewStart + m_dViewSpan).ToString("yyyy-MM-dd HH:mm:ss.fff");
            if (m_rangeStart.HasValue || m_rangeEnd.HasValue)
            {
                range = "선택: " + (m_rangeStart.HasValue ? m_rangeStart.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "시작 미지정") + "  —  "
                    + (m_rangeEnd.HasValue ? m_rangeEnd.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "끝 미지정");
                if (m_rangeStart.HasValue && m_rangeEnd.HasValue && m_rangeStart.Value > m_rangeEnd.Value) { range += " · 시작/끝 역순"; }
            }
            TextRenderer.DrawText(graphics, range, Font, new Rectangle(LeftGutter, ClientSize.Height - BottomGutter - m_hScroll.Height + 2, PlotWidth, 24), ForeColor, TextFormatFlags.EndEllipsis);
        }

        // 시간축 눈금과 이벤트·이상 구간의 도형만 비트맵에 저장한다.
        private void DrawChart(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.None;
            Color gridColor = m_bDark ? Color.FromArgb(53, 65, 82) : Color.FromArgb(222, 228, 236);
            int plotBottom = ClientSize.Height - BottomGutter - m_hScroll.Height;
            if (m_rangeStart.HasValue || m_rangeEnd.HasValue)
            {
                double startRatio = m_rangeStart.HasValue ? ((m_rangeStart.Value - m_data.Minimum).TotalSeconds - m_dViewStart) / m_dViewSpan : 0;
                double endRatio = m_rangeEnd.HasValue ? ((m_rangeEnd.Value - m_data.Minimum).TotalSeconds - m_dViewStart) / m_dViewSpan : 1;
                int startX = LeftGutter + (int)(Math.Max(0, Math.Min(1, startRatio)) * PlotWidth);
                int endX = LeftGutter + (int)(Math.Max(0, Math.Min(1, endRatio)) * PlotWidth);
                using (SolidBrush selection = new SolidBrush(Color.FromArgb(24, 46, 130, 235)))
                using (Pen boundary = new Pen(Color.FromArgb(46, 130, 235), 2) { DashStyle = DashStyle.Dash })
                {
                    if (m_rangeStart.HasValue && m_rangeEnd.HasValue && endX > startX && plotBottom > TopGutter)
                    { graphics.FillRectangle(selection, startX, TopGutter, endX - startX, plotBottom - TopGutter); }
                    if (m_rangeStart.HasValue && startRatio >= 0 && startRatio <= 1) { graphics.DrawLine(boundary, startX, TopGutter, startX, plotBottom); }
                    if (m_rangeEnd.HasValue && endRatio >= 0 && endRatio <= 1) { graphics.DrawLine(boundary, endX, TopGutter, endX, plotBottom); }
                }
            }
            using (Pen grid = new Pen(gridColor))
            {
                int divisions = Math.Max(2, PlotWidth / 140);
                for (int index = 0; index <= divisions; index++)
                {
                    int x = LeftGutter + PlotWidth * index / divisions;
                    graphics.DrawLine(grid, x, TopGutter - 3, x, ClientSize.Height - BottomGutter - m_hScroll.Height);
                }
                int lastLane = Math.Min(m_data.Lanes.Count, m_vScroll.Value + VisibleLanes);
                for (int laneIndex = m_vScroll.Value; laneIndex < lastLane; laneIndex++)
                {
                    TimingLane lane = m_data.Lanes[laneIndex];
                    int y = TopGutter + (laneIndex - m_vScroll.Value) * LaneHeight + LaneHeight / 2;
                    using (SolidBrush brush = new SolidBrush(lane.Rule.Color)) { graphics.FillRectangle(brush, 14, y - 5, 10, 10); }
                    graphics.DrawLine(grid, LeftGutter, y, LeftGutter + PlotWidth, y);
                    DateTime viewStart = TimeAtOffset(m_dViewStart);
                    DateTime viewEnd = TimeAtOffset(m_dViewStart + m_dViewSpan);
                    Color anomalyColor = m_bDark ? Color.FromArgb(255, 104, 111) : Color.FromArgb(204, 32, 48);
                    using (SolidBrush shade = new SolidBrush(Color.FromArgb(42, anomalyColor)))
                    using (Pen intervalPen = new Pen(anomalyColor, 2))
                    {
                        // 같은 화면 픽셀의 겹친 구간을 합쳐 대량의 GDI 호출을 피한다. 원본 이상 목록은 유지한다.
                        int[] coverage = new int[PlotWidth + 3];
                        foreach (TimingInterval interval in lane.Anomalies)
                        {
                            if (interval.Start.Timestamp.Value > viewEnd) { break; }
                            if (interval.End.Timestamp.Value < viewStart) { continue; }
                            double startRatio = (interval.Start.Timestamp.Value - viewStart).TotalSeconds / m_dViewSpan;
                            double endRatio = (interval.End.Timestamp.Value - viewStart).TotalSeconds / m_dViewSpan;
                            int startX = LeftGutter + (int)(Math.Max(0, Math.Min(1, startRatio)) * PlotWidth);
                            int endX = LeftGutter + (int)(Math.Max(0, Math.Min(1, endRatio)) * PlotWidth);
                            coverage[startX - LeftGutter]++;
                            coverage[Math.Min(PlotWidth + 2, Math.Max(startX + 2, endX + 1) - LeftGutter)]--;
                        }
                        int active = 0;
                        int runStart = -1;
                        for (int pixel = 0; pixel < coverage.Length; pixel++)
                        {
                            active += coverage[pixel];
                            if (active > 0 && runStart < 0) { runStart = pixel; }
                            if (active == 0 && runStart >= 0)
                            {
                                graphics.FillRectangle(shade, LeftGutter + runStart, y - 9, pixel - runStart, 18);
                                graphics.DrawLine(intervalPen, LeftGutter + runStart, y + 8, LeftGutter + pixel - 1, y + 8);
                                runStart = -1;
                            }
                        }
                    }
                    int begin = LowerBound(lane.Entries, TimeAtOffset(m_dViewStart));
                    int previousX = -1;
                    int previousAbnormalX = -1;
                    using (Pen eventPen = new Pen(lane.Rule.Color, 3))
                    using (Pen anomalyPen = new Pen(anomalyColor, 3))
                    {
                        for (int eventIndex = begin; eventIndex < lane.Entries.Count; eventIndex++)
                        {
                            double seconds = (lane.Entries[eventIndex].Timestamp.Value - m_data.Minimum).TotalSeconds;
                            if (seconds > m_dViewStart + m_dViewSpan) { break; }
                            int x = LeftGutter + (int)((seconds - m_dViewStart) / m_dViewSpan * PlotWidth);
                            bool abnormal = lane.AbnormalEntries.Contains(lane.Entries[eventIndex]);
                            if (abnormal)
                            {
                                if (x != previousAbnormalX) { graphics.DrawLine(anomalyPen, x, y - 8, x, y + 8); previousAbnormalX = x; }
                            }
                            else if (x != previousX && x != previousAbnormalX) { graphics.DrawLine(eventPen, x, y - 6, x, y + 6); }
                            previousX = x;
                        }
                    }
                }

            }
        }

        // 시간순 배열에서 해당 시각 이상인 첫 이벤트를 이진 탐색한다.
        private static int LowerBound(List<LogEntry> entries, DateTime time)
        {
            int first = 0;
            int last = entries.Count;
            while (first < last)
            {
                int middle = first + (last - first) / 2;
                if (entries[middle].Timestamp.Value < time) { first = middle + 1; }
                else { last = middle; }
            }
            return first;
        }

        // 클릭 위치와 가까운 실제 이벤트를 찾는다.
        private LogEntry HitEvent(Point point)
        {
            if (point.X < LeftGutter || point.X > LeftGutter + PlotWidth || point.Y < TopGutter) { return null; }
            int laneIndex = (point.Y - TopGutter) / LaneHeight + m_vScroll.Value;
            if (laneIndex < 0 || laneIndex >= m_data.Lanes.Count || laneIndex >= m_vScroll.Value + VisibleLanes) { return null; }
            List<LogEntry> entries = m_data.Lanes[laneIndex].Entries;
            double seconds = m_dViewStart + (point.X - LeftGutter) / (double)PlotWidth * m_dViewSpan;
            int index = LowerBound(entries, TimeAtOffset(seconds));
            LogEntry closest = null;
            double distance = Double.MaxValue;
            for (int candidate = Math.Max(0, index - 1); candidate <= Math.Min(entries.Count - 1, index); candidate++)
            {
                double difference = Math.Abs((entries[candidate].Timestamp.Value - m_data.Minimum).TotalSeconds - seconds);
                if (difference < distance) { closest = entries[candidate]; distance = difference; }
            }
            if (distance / m_dViewSpan * PlotWidth <= HitRadius) { return closest; }
            return null;
        }

        // 겹친 이상 구간 중 포인터에 포함되는 가장 짧은 구간을 선택한다.
        private TimingInterval HitInterval(Point point)
        {
            if (point.X < LeftGutter || point.X > LeftGutter + PlotWidth || point.Y < TopGutter) { return null; }
            int laneIndex = (point.Y - TopGutter) / LaneHeight + m_vScroll.Value;
            if (laneIndex >= m_data.Lanes.Count || laneIndex >= m_vScroll.Value + VisibleLanes) { return null; }
            int laneY = TopGutter + (laneIndex - m_vScroll.Value) * LaneHeight + LaneHeight / 2;
            if (Math.Abs(point.Y - laneY) > 12) { return null; }
            double seconds = m_dViewStart + (point.X - LeftGutter) / (double)PlotWidth * m_dViewSpan;
            double slack = HitRadius / (double)PlotWidth * m_dViewSpan;
            TimingInterval selected = null;
            foreach (TimingInterval interval in m_data.Lanes[laneIndex].Anomalies)
            {
                double start = (interval.Start.Timestamp.Value - m_data.Minimum).TotalSeconds;
                if (start > seconds + slack) { break; }
                double end = (interval.End.Timestamp.Value - m_data.Minimum).TotalSeconds;
                if (end < seconds - slack) { continue; }
                if (selected == null || interval.ActualMs < selected.ActualMs) { selected = interval; }
            }
            return selected;
        }

        // 실제 간격과 기준 주기를 툴팁에 표시하여 빨간 구간의 판정 근거를 알린다.
        private static string IntervalText(TimingInterval interval)
        {
            if (interval.MissingEnd)
            {
                return interval.Comparison + " · 연동 대상 없음 · 기준 " + interval.ExpectedMs.ToString("N3") + " ms\n"
                    + interval.Start.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\n"
                    + interval.Start.Process + " · " + interval.Start.Kind;
            }
            string direction = interval.ActualMs < interval.ExpectedMs ? "간격 짧음" : "간격 김";
            if (!String.IsNullOrEmpty(interval.Comparison)) { direction = interval.Comparison + " · " + direction; }
            double deviation = (interval.ActualMs / interval.ExpectedMs - 1) * 100;
            return direction + " · 실제 " + interval.ActualMs.ToString("N3") + " ms / 기준 " + interval.ExpectedMs.ToString("N3") + " ms (" + deviation.ToString("+0.0;-0.0;0.0") + "%)\n"
                + interval.Start.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") + " → " + interval.End.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\n"
                + interval.End.Process + " · " + interval.End.Kind;
        }

        // 드래그 이동의 기준 위치를 기억한다.
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right)
            {
                m_contextTime = null;
                if (e.X >= LeftGutter && e.X <= LeftGutter + PlotWidth && e.Y >= TopGutter && e.Y < ClientSize.Height - BottomGutter - m_hScroll.Height)
                {
                    LogEntry entry = HitEvent(e.Location);
                    if (entry != null) { m_contextTime = entry.Timestamp; }
                    else { m_contextTime = TimeAtOffset(m_dViewStart + (e.X - LeftGutter) / (double)PlotWidth * m_dViewSpan); }
                }
            }
            if (e.Button == MouseButtons.Left && e.X >= LeftGutter)
            {
                m_ptMouseDown = e.Location;
                m_dDragStart = m_dViewStart;
                m_bDragging = true;
                m_bMoved = false;
                Capture = true;
            }
        }

        // 드래그 중에는 시간을 이동하고 정지 시에는 이벤트 원문 툴팁을 표시한다.
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (m_bDragging)
            {
                if (Math.Abs(e.X - m_ptMouseDown.X) > 3) { m_bMoved = true; }
                m_dViewStart = m_dDragStart - (e.X - m_ptMouseDown.X) / (double)PlotWidth * m_dViewSpan;
                ClampView(); UpdateScroll(); InvalidateChartImage();
                return;
            }
            LogEntry entry = HitEvent(e.Location);
            TimingInterval interval = HitInterval(e.Location);
            if (entry == m_hoverEntry && interval == m_hoverInterval) { return; }
            m_hoverEntry = entry;
            m_hoverInterval = interval;
            if (interval != null)
            {
                Cursor = Cursors.Hand;
                m_toolTip.SetToolTip(this, IntervalText(interval));
                return;
            }
            if (entry == null) { m_toolTip.SetToolTip(this, ""); Cursor = Cursors.Default; return; }
            Cursor = Cursors.Hand;
            string text = entry.Text;
            if (text.Length > 400) { text = text.Substring(0, 400) + "…"; }
            m_toolTip.SetToolTip(this, entry.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\n" + entry.Process + " · " + entry.LineNumber + "행\n" + text);
        }

        // 이동하지 않은 클릭만 원문 선택 이벤트로 전달한다.
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!m_bDragging) { return; }
            m_bDragging = false;
            Capture = false;
            if (!m_bMoved)
            {
                LogEntry entry = HitEvent(e.Location);
                if (entry == null)
                {
                    TimingInterval interval = HitInterval(e.Location);
                    if (interval != null) { entry = interval.End; }
                }
                if (entry != null && EntrySelected != null) { EntrySelected(entry); }
            }
        }

        // 휠은 시간 축을, Shift+휠은 키워드 레인을 이동한다.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                m_vScroll.Value = Math.Max(0, Math.Min(Math.Max(0, m_data.Lanes.Count - VisibleLanes), m_vScroll.Value - Math.Sign(e.Delta)));
            }
            else { ZoomAt(e.Delta > 0 ? 0.7 : 1.0 / 0.7, Math.Max(0, Math.Min(1, (e.X - LeftGutter) / (double)PlotWidth))); }
        }

        // 키보드로도 차트 확대, 이동과 전체 범위 복원을 제공한다.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Add: case Keys.Oemplus: Zoom(0.5); e.Handled = true; break;
                case Keys.Subtract: case Keys.OemMinus: Zoom(2); e.Handled = true; break;
                case Keys.Home: ResetView(); e.Handled = true; break;
                case Keys.Left: m_dViewStart -= m_dViewSpan / 5; ClampView(); UpdateScroll(); InvalidateChartImage(); e.Handled = true; break;
                case Keys.Right: m_dViewStart += m_dViewSpan / 5; ClampView(); UpdateScroll(); InvalidateChartImage(); e.Handled = true; break;
            }
        }

        // 차트가 소유한 툴팁 리소스를 해제한다.
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (m_chartImage != null) { m_chartImage.Dispose(); } m_toolTip.Dispose(); m_rangeMenu.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
