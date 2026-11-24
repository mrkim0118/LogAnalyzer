using System;
using System.Drawing;
using System.Windows.Forms;

namespace LogAnalyzer
{
    internal sealed partial class MainForm
    {
        private const int UiButtonHeight = 32;
        private const int UiActionRowHeight = 40;
        private TextBox m_txtFolder;
        private Button m_btnFolder;
        private Button m_btnReload;
        private Button m_btnCancel;
        private Button m_btnSettings;
        private CheckBox m_chkDark;
        private CheckBox m_chkTimeRange;
        private DateTimePicker m_dtStart;
        private DateTimePicker m_dtEnd;
        private Button m_btnAllTimes;
        private TreeView m_treeSources;
        private Label m_lblLoadProgress;
        private ProgressBar m_loadProgress;
        private Button m_btnAll;
        private Button m_btnColor;
        private TimingChart m_chart;
        private FlowLayoutPanel m_keywordChips;
        private LogGrid m_gridLogs;
        private LogGrid m_gridSearch;
        private TableLayoutPanel m_panelLogs;
        private TableLayoutPanel m_panelSearch;
        private TableLayoutPanel m_panelTiming;
        private CheckBox m_chkTaskColors;
        private Button m_btnHighlights;
        private ToolStripMenuItem m_menuHighlight;
        private ToolStripMenuItem m_menuClearHighlights;
        private Button m_btnPopoutLogs;
        private Button m_btnPopoutSearch;
        private Button m_btnPopoutTiming;
        private ComboBox m_cmbKind;
        private TextBox m_txtSearch;
        private CheckBox m_chkCase;
        private CheckBox m_chkRegex;
        private Button m_btnSearch;
        private Button m_btnKeywords;
        private Button m_btnSaveKeywords;
        private ComboBox m_cmbPreset;
        private Button m_btnPresets;
        private Label m_lblScope;
        private Label m_lblEvents;
        private Label m_lblSearchCount;
        private Label m_lblStatus;
        private Button m_btnWarnings;
        private SplitContainer m_splitSources;
        private SplitContainer m_splitTiming;
        private SplitContainer m_splitResults;

        // 선택한 화이트 워크스페이스 시안의 사이드바와 세 개의 수직 뷰어를 구성한다.
        private void InitializeComponent()
        {
            SuspendLayout();
            Text = "LogAnalyzer";
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("맑은 고딕", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1472, 1017);
            MinimumSize = ViewerMinimumSize;
            AutoScroll = true;
            AutoScrollMinSize = new Size(940, 690);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            Controls.Add(root);

            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(12, 4, 12, 4), Tag = "heading" };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            toolbar.Controls.Add(new Label { Text = "로그 폴더", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            m_txtFolder = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 8, 4), AccessibleName = "로그 폴더 경로" };
            m_btnFolder = MakeButton("찾아보기", 114);
            ((ViewerButton)m_btnFolder).IconCode = "\uE8B7";
            m_btnReload = MakeButton("불러오기", 104);
            ((ViewerButton)m_btnReload).IconCode = "\uE768";
            m_btnReload.Tag = "accent";
            m_btnCancel = MakeButton("작업 취소", 94);
            m_btnCancel.Enabled = false;
            m_btnSettings = MakeButton("", 40);
            m_btnSettings.AccessibleName = "로그 형식";
            ((ViewerButton)m_btnSettings).IconCode = "\uE713";
            m_chkDark = new ViewerToggle { Appearance = Appearance.Button, Width = 40, Height = UiButtonHeight, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4), AccessibleName = "다크 모드", IconCode = "\uE708" };
            toolbar.Controls.Add(m_txtFolder, 1, 0);
            toolbar.Controls.Add(m_btnFolder, 2, 0);
            toolbar.Controls.Add(m_btnReload, 3, 0);
            toolbar.Controls.Add(m_btnSettings, 4, 0);
            toolbar.Controls.Add(m_chkDark, 5, 0);
            root.Controls.Add(toolbar, 0, 0);

            m_splitSources = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Size = new Size(1472, 909), FixedPanel = FixedPanel.Panel1, SplitterDistance = 278, Panel1MinSize = 248, Panel2MinSize = 635, SplitterWidth = 6, Margin = Padding.Empty };
            m_splitSources.Panel1.Padding = new Padding(12, 0, 12, 0);
            m_splitSources.Panel1.Tag = "sidebar";
            m_splitSources.Panel2.Padding = new Padding(8, 0, 12, 0);
            root.Controls.Add(m_splitSources, 0, 1);
            TableLayoutPanel sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Margin = Padding.Empty, Tag = "sidebar" };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sidebar.Controls.Add(Heading("프로세스 / 파일"), 0, 0);
            m_treeSources = MakeSourceTree();
            sidebar.Controls.Add(m_treeSources, 0, 1);
            TableLayoutPanel sourceActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Tag = "sidebar" };
            sourceActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); sourceActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            m_btnAll = MakeButton("전체 보기", 100);
            m_btnColor = MakeButton("배경색", 100);
            m_btnAll.Dock = DockStyle.Fill; m_btnColor.Dock = DockStyle.Fill;
            sourceActions.Controls.Add(m_btnAll);
            sourceActions.Controls.Add(m_btnColor);
            m_lblLoadProgress = new Label { Dock = DockStyle.Top, Height = 23, AutoEllipsis = true, Margin = new Padding(4, 3, 4, 0), Visible = false, Tag = "sidebar" };
            m_loadProgress = new ProgressBar { Dock = DockStyle.Top, Height = 12, Margin = new Padding(4, 1, 4, 6), Visible = false, AccessibleName = "로그 로딩 진행률" };
            sidebar.Controls.Add(m_lblLoadProgress, 0, 2);
            sidebar.Controls.Add(m_loadProgress, 0, 3);
            sidebar.Controls.Add(sourceActions, 0, 4);
            m_btnCancel.Visible = false;
            sidebar.Controls.Add(m_btnCancel, 0, 5);
            m_chkTimeRange = new CheckBox { Text = "시간 구간", Dock = DockStyle.Fill, Margin = new Padding(5, 0, 0, 0), Tag = "sidebar" };
            sidebar.Controls.Add(m_chkTimeRange, 0, 6);
            TableLayoutPanel timeBar = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 3, Visible = false, Margin = Padding.Empty, Tag = "sidebar" };
            timeBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            timeBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            timeBar.Controls.Add(new Label { Text = "시작", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
            timeBar.Controls.Add(new Label { Text = "끝", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
            m_dtStart = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", ShowUpDown = true, Dock = DockStyle.Fill, AccessibleName = "시작 날짜와 시간" };
            m_dtEnd = new DateTimePicker { Format = m_dtStart.Format, CustomFormat = m_dtStart.CustomFormat, ShowUpDown = true, Dock = DockStyle.Fill, AccessibleName = "끝 날짜와 시간" };
            m_btnAllTimes = MakeButton("전체 시간", 94);
            timeBar.Controls.Add(m_dtStart, 1, 0); timeBar.Controls.Add(m_dtEnd, 1, 1);
            timeBar.Controls.Add(m_btnAllTimes, 1, 2);
            sidebar.Controls.Add(timeBar, 0, 7);
            m_chkTimeRange.CheckedChanged += delegate { timeBar.Visible = m_chkTimeRange.Checked; };
            m_splitSources.Panel1.Controls.Add(sidebar);

            m_splitTiming = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1162, 909), SplitterDistance = 296, Panel1MinSize = 200, Panel2MinSize = 380, SplitterWidth = 6 };
            m_splitSources.Panel2.Controls.Add(m_splitTiming);
            TableLayoutPanel timing = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            m_panelTiming = timing;
            timing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            timing.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            timing.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight + SystemInformation.HorizontalScrollBarHeight));
            timing.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel timingHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Tag = "heading" };
            timingHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            timingHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            m_lblEvents = Heading("타이밍 차트");
            timingHeader.Controls.Add(m_lblEvents, 0, 0);
            TableLayoutPanel timingActions = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, Tag = "heading" };
            for (int column = 0; column < timingActions.ColumnCount; column++) { timingActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); }
            timingActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            m_btnKeywords = MakeButton("키워드", 76);
            m_btnSaveKeywords = MakeButton("저장", 58);
            m_cmbPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 23, Width = 180, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(3, 5, 3, 5), AccessibleName = "키워드 프리셋" };
            m_btnPresets = MakeButton("프리셋 ▾", 86);
            m_btnPopoutTiming = MakeButton("새 창", 70);
            timingActions.Controls.AddRange(new Control[] { m_cmbPreset, m_btnPresets, m_btnKeywords, m_btnSaveKeywords, m_btnPopoutTiming });
            timingHeader.Controls.Add(timingActions, 1, 0);
            timing.Controls.Add(timingHeader, 0, 0);
            m_keywordChips = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty, Padding = new Padding(6, 0, 0, 0) };
            timing.Controls.Add(m_keywordChips, 0, 1);
            m_chart = new TimingChart { Dock = DockStyle.Fill, Margin = Padding.Empty };
            timing.Controls.Add(m_chart, 0, 2);
            m_splitTiming.Panel1.Controls.Add(timing);

            m_splitResults = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1162, 665), SplitterDistance = 402, Panel1MinSize = 160, Panel2MinSize = 150, SplitterWidth = 6 };
            m_splitTiming.Panel2.Controls.Add(m_splitResults);
            TableLayoutPanel logs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            m_panelLogs = logs;
            logs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logs.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            logs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel logHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, Tag = "heading" };
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            for (int column = 2; column < logHeader.ColumnCount; column++) { logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); }
            m_lblScope = Heading("통합 로그");
            m_cmbKind = new ComboBox { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 5, 3, 5), DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "로그 종류" };
            m_cmbKind.Items.Add("모든 종류"); m_cmbKind.SelectedIndex = 0;
            m_chkTaskColors = new ViewerToggle { Text = "프로세스 구분", Appearance = Appearance.Button, Width = 116, Height = UiButtonHeight, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) };
            m_btnHighlights = MakeButton("강조 ▾", 76);
            ContextMenuStrip highlights = new ContextMenuStrip();
            m_menuHighlight = new ToolStripMenuItem("선택 문자열 색상…") { Enabled = false };
            m_menuClearHighlights = new ToolStripMenuItem("강조 지우기") { Enabled = false };
            highlights.Items.AddRange(new ToolStripItem[] { m_menuHighlight, m_menuClearHighlights });
            m_btnHighlights.Disposed += delegate { highlights.Dispose(); };
            m_btnHighlights.ContextMenuStrip = highlights;
            m_btnHighlights.Click += delegate { highlights.Show(m_btnHighlights, new Point(0, m_btnHighlights.Height)); };
            m_btnPopoutLogs = MakeButton("새 창", 70);
            logHeader.Controls.AddRange(new Control[] { m_lblScope, m_cmbKind, m_chkTaskColors, m_btnHighlights, m_btnPopoutLogs });
            logs.Controls.Add(logHeader, 0, 0);
            m_gridLogs = MakeGrid("통합 로그");
            AddPagePicker(logs, m_gridLogs, m_logPage);
            m_splitResults.Panel1.Controls.Add(logs);

            TableLayoutPanel search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            m_panelSearch = search;
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            search.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            search.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            m_lblSearchCount = Heading("검색 결과");
            TableLayoutPanel searchBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty, Tag = "heading" };
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            m_btnPopoutSearch = MakeButton("새 창", 70);
            m_txtSearch = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 4, 8, 4), AccessibleName = "검색 문자열" };
            m_txtSearch.HandleCreated += delegate { SendMessage(m_txtSearch.Handle, SetCueBanner, new IntPtr(1), "검색할 문자열 입력 (입력을 멈추면 자동 검색)"); };
            m_chkCase = new ViewerToggle { Text = "Aa", Appearance = Appearance.Button, Width = 40, Height = UiButtonHeight, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4), AccessibleName = "대소문자 구분" };
            m_chkRegex = new ViewerToggle { Text = ".*", Appearance = Appearance.Button, Width = 40, Height = UiButtonHeight, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4), AccessibleName = "정규식" };
            m_btnSearch = MakeButton("검색", 70); m_btnSearch.Tag = "accent";
            searchBar.Controls.AddRange(new Control[] { m_lblSearchCount, m_txtSearch, m_chkCase, m_chkRegex, m_btnSearch, m_btnPopoutSearch });
            search.Controls.Add(searchBar, 0, 0);
            m_gridSearch = MakeGrid("검색 결과");
            m_gridSearch.EmptyText = SearchHint;
            AddPagePicker(search, m_gridSearch, m_searchPage);
            m_splitResults.Panel2.Controls.Add(search);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            m_lblStatus = new Label { Text = "준비", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(10, 0, 0, 0) };
            m_btnWarnings = MakeButton("읽기 경고", 88); m_btnWarnings.Visible = false;
            footer.Controls.Add(m_lblStatus, 0, 0); footer.Controls.Add(m_btnWarnings, 1, 0);
            root.Controls.Add(footer, 0, 2);
            ResumeLayout(true);
        }

        // 메인 창과 분리 창에 같은 프로세스/파일 선택 컨트롤을 구성한다.
        private TreeView MakeSourceTree()
        {
            TreeView tree = new TreeView { Dock = DockStyle.Fill, Margin = Padding.Empty, CheckBoxes = true, HideSelection = false, BorderStyle = BorderStyle.None, FullRowSelect = true, ItemHeight = 29, Indent = 16, ShowLines = false, AccessibleName = "프로세스 및 로그 파일", Tag = "sidebar", DrawMode = TreeViewDrawMode.OwnerDrawText };
            tree.DrawNode += DrawSourceNode;
            return tree;
        }

        // 노드 이름부터 행 끝까지 배경을 칠하고, 오른쪽에 줄 수를 흐린 색으로 표시한다.
        private void DrawSourceNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Bounds.IsEmpty) { return; }
            TreeView tree = (TreeView)sender;
            bool dark = tree.BackColor.GetBrightness() < 0.3F;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            Color back = selected ? (dark ? Color.FromArgb(43, 68, 101) : Color.FromArgb(219, 235, 255)) : (e.Node.BackColor.IsEmpty ? tree.BackColor : e.Node.BackColor);
            Color fore = e.Node.ForeColor.IsEmpty ? tree.ForeColor : e.Node.ForeColor;
            if (selected) { fore = Theme.TextOn(back); }
            Rectangle row = Rectangle.FromLTRB(e.Bounds.Left, e.Bounds.Top, tree.ClientSize.Width, e.Bounds.Bottom);
            using (SolidBrush brush = new SolidBrush(back)) { e.Graphics.FillRectangle(brush, row); }
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? tree.Font, e.Bounds, fore, flags);
            LogSource source = e.Node.Tag as LogSource;
            int lines = source == null ? m_entries.Count : source.Lines;
            if (lines <= 0) { return; }
            string text = lines.ToString("N0");
            Rectangle area = Rectangle.FromLTRB(e.Bounds.Right + 8, e.Bounds.Top, tree.ClientSize.Width - 10, e.Bounds.Bottom);
            if (area.Width < TextRenderer.MeasureText(e.Graphics, text, tree.Font, Size.Empty, flags).Width) { return; }
            TextRenderer.DrawText(e.Graphics, text, tree.Font, area, Theme.Mix(back, fore, 0.6), flags | TextFormatFlags.Right);
        }

        // 일관된 조작 버튼의 크기와 여백을 설정한다.
        internal static Button MakeButton(string text, int width)
        {
            return new ViewerButton { Text = text, Width = width, Height = UiButtonHeight, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        }

        // 뷰어 제목을 같은 글꼴과 높이로 표시한다.
        private static Label Heading(string text)
        {
            return new HeadingLabel { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("맑은 고딕", 11F, FontStyle.Bold), Padding = new Padding(6, 0, 0, 0), Tag = "heading" };
        }

        // 대용량일 때만 페이지 번호와 이동 버튼을 표 아래에 표시한다.
        private void AddPagePicker(TableLayoutPanel viewer, LogGrid grid, NumericUpDown picker)
        {
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Visible = false };
            picker.Minimum = 1; picker.Maximum = 1; picker.Width = 76;
            picker.AccessibleName = grid.AccessibleName + " 페이지";
            Button previous = MakeButton("이전", 58);
            Button next = MakeButton("다음", 58);
            previous.Click += delegate { if (picker.Value > 1) { picker.Value--; } };
            next.Click += delegate { if (picker.Value < picker.Maximum) { picker.Value++; } };
            picker.ValueChanged += delegate { if (!m_bUpdatingPage) { DisplayGridPage(grid, (int)picker.Value); } };
            bar.Controls.Add(previous); bar.Controls.Add(picker);
            bar.Controls.Add(new Label { Name = "PageTotal", AutoSize = true, Anchor = AnchorStyles.Left });
            bar.Controls.Add(next);
            viewer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            viewer.Controls.Add(bar, 0, 2);
            Panel host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            host.Controls.Add(grid); host.Controls.Add(grid.LargeView);
            viewer.Controls.Add(host, 0, 1);
            ConfigureLargeList(grid);
        }
        // 로그 행을 가상 모드로 표시하여 DataGridViewRow 객체의 대량 생성을 피한다.
        private static LogGrid MakeGrid(string name)
        {
            LogGrid grid = new LogGrid { ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToOrderColumns = true, AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ColumnHeadersHeight = 32, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, Margin = Padding.Empty, AccessibleName = name };
            grid.RowTemplate.Height = 26;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.DefaultCellStyle.Font = new Font("Consolas", 10F);
            grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "날짜", Width = 110, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "시간", Width = 115, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Process", HeaderText = "프로세스", Width = 182, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "종류", Width = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "로그", Width = 550, MinimumWidth = 250, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "행", Width = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
            return grid;
        }
    }
}
