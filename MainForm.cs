using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LogAnalyzer
{
    internal sealed partial class MainForm : Form, IMessageFilter
    {
        private static readonly Color[] m_arrDefaultColors = new Color[] { Color.FromArgb(225, 215, 255), Color.FromArgb(209, 229, 255), Color.FromArgb(216, 240, 215), Color.FromArgb(255, 237, 195), Color.FromArgb(255, 220, 200), Color.FromArgb(204, 239, 241), Color.FromArgb(219, 233, 250), Color.FromArgb(247, 216, 233) };
        private readonly string m_strSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        private string m_strKeywordPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timing-keywords.ini");
        private readonly string m_strPresetFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timing-presets");
        private bool m_bUpdatingPresets;
        private AppSettings m_settings;
        private List<KeywordRule> m_rules = new List<KeywordRule>();
        private List<LogEntry> m_entries = new List<LogEntry>();
        private List<LogEntry> m_visibleEntries = new List<LogEntry>();
        private List<LogEntry> m_searchEntries = new List<LogEntry>();
        private List<string> m_warnings = new List<string>();
        private List<string> m_sourceFiles = new List<string>();
        private CancellationTokenSource m_loadCancellation;
        private CancellationTokenSource m_viewCancellation;
        private CancellationTokenSource m_exportCancellation;
        private bool m_bUpdatingSources;
        private bool m_bLoading;
        private bool m_bClosing;
        private bool m_bKeywordDirty;
        private string m_strSelectedProcess;
        private string m_strSelectedFile;
        private int m_nFileCount;
        private int m_nMissingTimestampCount;
        private int m_nListedFileCount;
        private static readonly Size ViewerMinimumSize = new Size(400, 300);
        private readonly Dictionary<Form, Rectangle> m_snapRestoreBounds = new Dictionary<Form, Rectangle>();
        private Form m_logWindow;
        private Form m_searchWindow;
        private Form m_timingWindow;
        private readonly List<TreeView> m_sourceTrees = new List<TreeView>();
        private LogGrid m_textSelectionGrid;
        private LogEntry m_selectedTextEntry;
        private const int WheelMessage = 0x020A;
        private string m_strSearchMark;
        private bool m_bSearchMarkCase;
        private Regex m_searchMarkPattern;

        // 일반 그리드와 대용량 가상 목록은 동일한 뷰어 패널과 로그 데이터를 사용한다.
        internal sealed class LogGrid : DataGridView
        {
            public readonly VirtualLogList LargeView = new VirtualLogList();
            // 일반 표에서 스크롤할 때 행을 버퍼에 그린다.
            public LogGrid() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
            public string EmptyText;

            // 행이 없을 때 빈 표 대신 다음에 할 일을 안내한다.
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (RowCount > 0 || String.IsNullOrEmpty(EmptyText)) { return; }
                Rectangle area = Rectangle.FromLTRB(0, ColumnHeadersVisible ? ColumnHeadersHeight : 0, ClientSize.Width, ClientSize.Height);
                Color muted = Theme.Mix(BackgroundColor, DefaultCellStyle.ForeColor, 0.6);
                TextRenderer.DrawText(e.Graphics, EmptyText, Font, area, muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        // 대용량 로그는 현재 페이지와 그 다음 화면에 필요한 행만 Windows 목록에 등록한다.
        internal sealed class VirtualLogList : ListView
        {
            public List<LogEntry> Entries = new List<LogEntry>();
            public int FirstEntry;
            public Action<int> ChangeTopRow;
            public Func<MouseEventArgs, bool> BeginTextDrag;
            public Func<bool> KeepTextSelection;
            public Action<int> MoveTextDrag;
            private readonly BufferedGraphicsContext m_rowBuffer = new BufferedGraphicsContext();
            private int m_nWheelRemainder;
            private long m_pendingRows;
            private bool m_bScrollPosted;

            // 가상 목록을 만들고 Windows가 요청하는 행의 내용만 제공한다.
            public VirtualLogList()
            {
                View = View.Details; VirtualMode = true; FullRowSelect = true;
                MultiSelect = false; HideSelection = false; DoubleBuffered = true;
                BorderStyle = BorderStyle.None; AllowColumnReorder = true;
                Dock = DockStyle.Fill; Visible = false;
            }

            // 마우스 진입으로 행 그리기만 요청돼도 완성된 행을 한 번에 화면에 복사한다.
            protected override void OnDrawItem(DrawListViewItemEventArgs e)
            {
                Rectangle visible = Rectangle.Intersect(e.Bounds, ClientRectangle);
                if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0 || Entries == null || e.ItemIndex < 0 || e.ItemIndex >= VirtualListSize) { return; }
                if (m_rowBuffer.MaximumBuffer.Width < visible.Width || m_rowBuffer.MaximumBuffer.Height < visible.Height)
                { m_rowBuffer.MaximumBuffer = new Size(visible.Width + 1, visible.Height + 1); }
                using (BufferedGraphics frame = m_rowBuffer.Allocate(e.Graphics, visible))
                {
                    Rectangle localRow = e.Bounds;
                    localRow.Offset(-visible.Left, -visible.Top);
                    frame.Graphics.ResetTransform();
                    base.OnDrawItem(new DrawListViewItemEventArgs(frame.Graphics, e.Item, localRow, e.ItemIndex, e.State));
                    frame.Render();
                }
            }

            // 가상 목록을 닫으면 행 그리기에 사용한 그래픽 버퍼를 해제한다.
            protected override void Dispose(bool disposing)
            {
                if (disposing) { m_rowBuffer.Dispose(); }
                base.Dispose(disposing);
            }

            // 연속 휠 입력은 이동량을 합산하고 텍스트 드래그는 한 행 안에서 처리한다.
            protected override void WndProc(ref Message message)
            {
                // 완성된 행 위에 공용 컨트롤이 기본 배경이나 셀을 다시 칠하지 않게 한다.
                const int ReflectedNotify = 0x204E, CustomDraw = -12, ItemPrePaint = 0x10001, SkipDefault = 4;
                if (message.Msg == ReflectedNotify && message.LParam != IntPtr.Zero &&
                    Marshal.ReadIntPtr(message.LParam) == Handle &&
                    Marshal.ReadInt32(message.LParam, 2 * IntPtr.Size) == CustomDraw &&
                    Marshal.ReadInt32(message.LParam, 3 * IntPtr.Size) == ItemPrePaint)
                {
                    base.WndProc(ref message);
                    message.Result = new IntPtr(SkipDefault);
                    return;
                }
                const int LeftDown = 0x0201;
                if (message.Msg == LeftDown && BeginTextDrag != null)
                {
                    Point point = new Point(unchecked((int)message.LParam.ToInt64()));
                    if (BeginTextDrag(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0))) { return; }
                }
                if ((message.Msg == 0x0200 || message.Msg == 0x0202) && Capture && MoveTextDrag != null)
                {
                    Point point = new Point(unchecked((int)message.LParam.ToInt64()));
                    MoveTextDrag(point.X);
                    if (message.Msg == 0x0202) { Capture = false; }
                    return;
                }
                const int RightDown = 0x0204, RightUp = 0x0205;
                if ((message.Msg == RightDown || message.Msg == RightUp) && KeepTextSelection != null && ContextMenuStrip != null)
                {
                    Point point = new Point(unchecked((int)message.LParam.ToInt64()));
                    if (message.Msg == RightDown)
                    {
                        Focus();
                        if (!KeepTextSelection())
                        {
                            ListViewItem item = GetItemAt(point.X, point.Y);
                            if (item != null) { item.Selected = true; item.Focused = true; }
                        }
                    }
                    else
                    {
                        // 우클릭을 직접 처리하므로 선택 영역을 유지한 채 같은 위치에 메뉴를 연다.
                        ContextMenuStrip.Show(this, point);
                    }
                    return;
                }
                const int VerticalScroll = 0x0115, ThumbPosition = 4, ThumbTrack = 5;
                if (message.Msg == VerticalScroll && VirtualListSize > 0)
                {
                    long scroll = message.WParam.ToInt64();
                    int request = (int)(scroll & 0xffff);
                    if (request == ThumbTrack || request == ThumbPosition)
                    {
                        // 한 페이지는 16비트 위치 범위 안에 있어 손잡이의 행 번호를 직접 사용할 수 있다.
                        ScrollToLocalRow((int)((scroll >> 16) & 0xffff));
                        Update();
                        return;
                    }
                }
                if (message.Msg == WheelMessage && VirtualListSize > 0 && (message.WParam.ToInt64() & 12) == 0)
                {
                    const int WheelStep = 120, CountPerPage = 0x1028;
                    m_nWheelRemainder += (short)(message.WParam.ToInt64() >> 16);
                    int lines = SystemInformation.MouseWheelScrollLines;
                    if (lines < 0) { lines = Math.Max(1, SendMessage(Handle, CountPerPage, IntPtr.Zero, IntPtr.Zero).ToInt32()); }
                    m_pendingRows -= (long)(m_nWheelRemainder / WheelStep) * lines;
                    m_nWheelRemainder %= WheelStep;
                    if (m_pendingRows != 0 && !m_bScrollPosted)
                    {
                        m_bScrollPosted = true;
                        BeginInvoke(new Action(ApplyWheelScroll));
                    }
                    return;
                }
                base.WndProc(ref message);
            }

            // 대기 중인 입력의 전체 이동량을 한 번에 적용하고 노출된 행을 즉시 출력한다.
            private void ApplyWheelScroll()
            {
                const int GetTopIndex = 0x1027, CountPerPage = 0x1028;
                long rows = m_pendingRows;
                m_pendingRows = 0; m_bScrollPosted = false;
                if (IsDisposed || !Visible || VirtualListSize == 0 || rows == 0) { return; }
                int localTop = SendMessage(Handle, GetTopIndex, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int visibleRows = Math.Max(1, SendMessage(Handle, CountPerPage, IntPtr.Zero, IntPtr.Zero).ToInt32());
                int target = (int)Math.Max(0L, Math.Min((long)FirstEntry + localTop + rows, Math.Max(0, Entries.Count - visibleRows)));
                if (target < FirstEntry || target - FirstEntry >= GridPageSize)
                {
                    ChangeTopRow(target);
                    Update();
                    return;
                }
                ScrollToLocalRow(target - FirstEntry);
                Update();
            }

            // 현재 페이지의 첫 표시 행을 이동하고 Windows가 노출된 부분만 다시 그리게 한다.
            public void ScrollToLocalRow(int row)
            {
                const int GetTopIndex = 0x1027, ScrollPixels = 0x1014;
                int target = Math.Min(Math.Max(0, row), VirtualListSize - 1);
                if (target < 0) { return; }
                int current = SendMessage(Handle, GetTopIndex, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (current == target) { return; }
                int pixels = (target - current) * GetItemRect(target).Height;
                SendMessage(Handle, ScrollPixels, IntPtr.Zero, new IntPtr(pixels));
            }
        }
        internal const int LargeViewThreshold = 50000;
        internal const int GridPageSize = 5000;
        internal const int PageOverlapRows = 256;
        private readonly NumericUpDown m_logPage = new NumericUpDown();
        private readonly NumericUpDown m_searchPage = new NumericUpDown();
        private List<LogEntry> m_logPageRows = new List<LogEntry>();
        private List<LogEntry> m_searchPageRows = new List<LogEntry>();
        private bool m_bUpdatingPage;
        private int m_nTextRow = -1;
        private int m_nTextAnchor;
        private int m_nTextCaret;
        private bool m_bDraggingText;
        private readonly List<KeyValuePair<string, Color>> m_highlights = new List<KeyValuePair<string, Color>>();
        private const int LogTextColumn = 4;
        private const TextFormatFlags LogTextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
        private readonly System.Windows.Forms.Timer m_filterTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip m_toolTip = new ToolTip();

        private sealed class LogSource
        {
            public string Process;
            public string FilePath;
            public int Lines;
        }

        // 저장된 화면/로그 설정을 복원하고 사용자 조작을 실제 처리에 연결한다.
        public MainForm(string initialFolder)
        {
            InitializeComponent();
            m_sourceTrees.Add(m_treeSources);
            List<string> startupWarnings = new List<string>();
            try { m_settings = AppSettings.Load(m_strSettingsPath); }
            catch (Exception ex) { m_settings = new AppSettings(); startupWarnings.Add("settings.ini: " + ex.Message); }
            try
            {
                InitializePresets();
            }
            catch (Exception ex) { startupWarnings.Add("timing-keywords.ini: " + ex.Message); }
            m_txtFolder.Text = initialFolder ?? m_settings.LastFolder;
            m_chkDark.Checked = m_settings.DarkMode;
            m_btnFolder.Click += async delegate { await ChooseFolder(); };
            m_btnReload.Click += async delegate { await LoadFolder(); };
            m_btnCancel.Click += delegate
            {
                if (m_loadCancellation != null) { m_loadCancellation.Cancel(); }
                if (m_exportCancellation != null) { m_exportCancellation.Cancel(); }
            };
            m_txtFolder.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadFolder(); } };
            m_btnSettings.Click += delegate { EditSettings(); };
            m_chkDark.CheckedChanged += delegate { m_settings.DarkMode = m_chkDark.Checked; ApplyTheme(); SaveSettings(); };
            m_chkTimeRange.CheckedChanged += delegate
            {
                m_dtStart.Enabled = m_chkTimeRange.Checked; m_dtEnd.Enabled = m_chkTimeRange.Checked;
                m_btnAllTimes.Enabled = m_chkTimeRange.Checked; ScheduleRefresh();
            };
            m_dtStart.ValueChanged += delegate { if (m_chkTimeRange.Checked) { ScheduleRefresh(); } };
            m_dtEnd.ValueChanged += delegate { if (m_chkTimeRange.Checked) { ScheduleRefresh(); } };
            m_btnAllTimes.Click += delegate { m_chkTimeRange.Checked = false; };
            m_treeSources.AfterCheck += SourceChecked;
            m_treeSources.AfterSelect += SourceSelected;
            m_btnAll.Click += delegate { ShowAll(); };
            m_btnColor.Click += delegate { ChooseProcessColor(); };
            m_cmbKind.SelectedIndexChanged += delegate { ScheduleRefresh(); };
            m_btnSearch.Click += async delegate { await RefreshView(); };
            m_txtSearch.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RefreshView(); } };
            m_txtSearch.TextChanged += delegate { ScheduleRefresh(); };
            m_chkCase.CheckedChanged += delegate { ScheduleRefresh(); };
            m_chkRegex.CheckedChanged += delegate { ScheduleRefresh(); };
            m_btnKeywords.Click += delegate { EditKeywords(); };
            m_btnSaveKeywords.Click += delegate { SaveKeywords(false); };
            m_cmbPreset.SelectedIndexChanged += delegate { SwitchPreset(); };
            InstallPresetMenu();
            m_btnWarnings.Click += delegate { ShowText("읽기 경고", String.Join(Environment.NewLine, m_warnings)); };
            m_chart.EntrySelected += SelectMainEntry;
            m_chart.ExportRequested += async delegate(DateTime start, DateTime end) { await ExportTimeRange(start, end); };
            m_btnPopoutLogs.Click += delegate { OpenViewerWindow(m_panelLogs); };
            m_btnPopoutSearch.Click += delegate { OpenViewerWindow(m_panelSearch); };
            m_btnPopoutTiming.Click += delegate { OpenViewerWindow(m_panelTiming); };
            m_chkTaskColors.CheckedChanged += delegate { ClearTextSelection(); ColorSourceNodes(); RefreshLogColors(); m_menuClearHighlights.Enabled = !m_chkTaskColors.Checked && m_highlights.Count > 0; };
            m_menuHighlight.Click += delegate { ChooseHighlightColor(); };
            m_menuClearHighlights.Click += delegate { m_highlights.Clear(); m_bKeywordDirty = true; m_btnSaveKeywords.Text = "저장 *"; m_menuClearHighlights.Enabled = false; RefreshLogColors(); };
            foreach (LogGrid grid in new LogGrid[] { m_gridLogs, m_gridSearch })
            {
                grid.CellPainting += PaintLogText;
                grid.MouseDown += LogTextMouseDown;
                grid.MouseMove += LogTextMouseMove;
                grid.MouseUp += delegate { if (grid == m_textSelectionGrid) { m_bDraggingText = false; grid.Capture = false; } };
                grid.MouseCaptureChanged += delegate { if (grid == m_textSelectionGrid && !grid.Capture) { m_bDraggingText = false; } };
                grid.CellClick += delegate { if (grid != m_textSelectionGrid || String.IsNullOrWhiteSpace(SelectedLogText())) { GridSelectionChanged(grid, EventArgs.Empty); } };
                grid.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Control && e.KeyCode == Keys.C && grid == m_textSelectionGrid && SelectedLogText().Length > 0)
                    {
                        try { Clipboard.SetText(SelectedLogText()); } catch (ExternalException ex) { ShowError(ex.Message); }
                        e.Handled = true; e.SuppressKeyPress = true;
                    }
                };
            }
            m_gridLogs.CellValueNeeded += GridValueNeeded;
            m_gridSearch.CellValueNeeded += GridValueNeeded;
            m_gridLogs.CellFormatting += GridCellFormatting;
            m_gridSearch.CellFormatting += GridCellFormatting;
            m_gridLogs.CellToolTipTextNeeded += GridToolTip;
            m_gridSearch.CellToolTipTextNeeded += GridToolTip;
            m_gridLogs.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { ShowLogDetail(m_gridLogs, e.RowIndex); };
            m_gridSearch.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.RowIndex < m_searchPageRows.Count) { SelectMainEntry(m_searchPageRows[e.RowIndex]); }
            };
            m_gridLogs.CurrentCellChanged += GridSelectionChanged;
            m_gridSearch.CurrentCellChanged += GridSelectionChanged;
            m_filterTimer.Interval = 300;
            m_filterTimer.Tick += async delegate { m_filterTimer.Stop(); await RefreshView(); };
            m_toolTip.SetToolTip(m_chart, "휠: 확대/축소 · 드래그: 시간 이동 · 클릭: 원문 선택 · Shift+휠: 레인 이동");
            m_toolTip.SetToolTip(m_btnSettings, "로그 형식");
            m_toolTip.SetToolTip(m_chkDark, "다크 모드");
            m_toolTip.SetToolTip(m_chkCase, "대소문자 구분");
            m_toolTip.SetToolTip(m_chkRegex, "정규식");
            m_toolTip.SetToolTip(m_btnSaveKeywords, m_strKeywordPath);
            m_toolTip.SetToolTip(m_btnColor, "선택한 프로세스의 배경색");
            m_toolTip.SetToolTip(m_dtEnd, "지정한 끝 초의 로그까지 포함");
            m_toolTip.SetToolTip(m_chkTimeRange, "통합 로그·검색 결과·타이밍 차트에 적용. 시간 미인식 로그 제외");
            InstallGridMenu(m_gridLogs);
            InstallGridMenu(m_gridSearch);
            ApplyTheme();
            RebuildKeywordChips();
            Shown += async delegate
            {
                if (startupWarnings.Count > 0) { ShowText("설정 읽기 오류", String.Join(Environment.NewLine, startupWarnings)); }
                if (!String.IsNullOrEmpty(initialFolder)) { await LoadFolder(); }
                else { await RefreshView(); }
            };
            Application.AddMessageFilter(this);
            FormClosing += ClosingForm;
        }

        // .NET Framework 4.8에서 탐색기 주소 표시줄을 제공하는 Windows 셸 대화상자이다.
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        // COM 호출 순서는 Windows SDK의 IFileDialog 선언 순서를 따른다.
        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        // 선택한 셸 항목에서 로더에 전달할 파일 시스템 경로를 얻는다.
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        // 메인 경로 입력란의 폴더를 탐색기의 시작 위치로 변환한다.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

        // 찾아보기에서 선택을 확정한 경우에만 메인 경로를 바꾸고 로그를 읽는다.
        private async Task ChooseFolder()
        {
            const uint PickFolders = 0x20, ForceFileSystem = 0x40, PathMustExist = 0x800, NoChangeDirectory = 0x08;
            const uint FileSystemPath = 0x80058000;
            const int Cancelled = unchecked((int)0x800704C7);
            IFileDialog dialog = null;
            IShellItem initialFolder = null;
            IShellItem selection = null;
            string selectedPath;
            try
            {
                dialog = (IFileDialog)new FileOpenDialog();
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist | NoChangeDirectory);
                dialog.SetTitle("로그 폴더 선택");
                dialog.SetOkButtonLabel("폴더 선택");
                string currentPath = m_txtFolder.Text.Trim().Trim('"');
                if (Directory.Exists(currentPath))
                {
                    Guid iid = typeof(IShellItem).GUID;
                    int status = SHCreateItemFromParsingName(Path.GetFullPath(currentPath), IntPtr.Zero, ref iid, out initialFolder);
                    if (status >= 0) { dialog.SetFolder(initialFolder); }
                }
                int result = dialog.Show(Handle);
                if (result == Cancelled) { return; }
                Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out selection);
                selection.GetDisplayName(FileSystemPath, out selectedPath);
            }
            catch (Exception ex) { ShowError("로그 폴더를 선택할 수 없습니다. " + ex.Message); return; }
            finally
            {
                if (selection != null) { Marshal.ReleaseComObject(selection); }
                if (initialFolder != null) { Marshal.ReleaseComObject(initialFolder); }
                if (dialog != null) { Marshal.ReleaseComObject(dialog); }
            }
            m_txtFolder.Text = selectedPath;
            await LoadFolder();
        }

        // 성공한 읽기 결과만 반영하며 취소/실패 시 기존 뷰어 데이터를 보존한다.
        private async Task LoadFolder()
        {
            if (m_loadCancellation != null || m_exportCancellation != null) { return; }
            string path = m_txtFolder.Text.Trim().Trim('"');
            if (String.IsNullOrEmpty(path)) { ShowError("로그 폴더 경로를 입력하세요."); return; }
            m_filterTimer.Stop();
            if (m_viewCancellation != null) { m_viewCancellation.Cancel(); }
            m_bLoading = true;
            SetLoadControls(false);
            CancellationTokenSource source = new CancellationTokenSource();
            m_loadCancellation = source;
            SetLoadProgress("로딩 중 · 파일 목록 확인", -1);
            try
            {
                Progress<ProgressChangedEventArgs> progress = new Progress<ProgressChangedEventArgs>(delegate(ProgressChangedEventArgs update)
                {
                    if (m_bClosing || !m_bLoading || m_loadCancellation != source || source.IsCancellationRequested) { return; }
                    SetLoadProgress((string)update.UserState, update.ProgressPercentage);
                });
                LogOptions options = m_settings.Options;
                LogReadResult result = await Task.Run(delegate { return LogReader.ReadFolder(path, options, source.Token, progress); }, source.Token);
                if (m_bClosing) { return; }
                source.Token.ThrowIfCancellationRequested();
                m_entries = result.Entries;
                m_nMissingTimestampCount = result.MissingTimestampCount;
                m_sourceFiles = result.SourceFiles;
                if (!m_chkTimeRange.Checked)
                {
                    // 필터를 사용하지 않을 때만 새 폴더의 전체 시간 범위로 입력값을 준비한다.
                    // 정렬된 로그는 시간 미인식 행이 끝에 모인다.
                    int timedRows = m_entries.Count - m_nMissingTimestampCount;
                    if (timedRows > 0)
                    {
                        DateTime minimum = m_entries[0].Timestamp.Value;
                        DateTime maximum = m_entries[timedRows - 1].Timestamp.Value;
                        m_dtStart.Value = new DateTime(Math.Max(m_dtStart.MinDate.Ticks, Math.Min(m_dtStart.MaxDate.Ticks, minimum.Ticks)));
                        m_dtEnd.Value = new DateTime(Math.Max(m_dtEnd.MinDate.Ticks, Math.Min(m_dtEnd.MaxDate.Ticks, maximum.Ticks)));
                    }
                }
                m_nFileCount = result.FileCount;
                m_warnings = result.Warnings;
                m_btnWarnings.Visible = m_warnings.Count > 0;
                m_settings.LastFolder = path;
                m_strSelectedProcess = null;
                m_strSelectedFile = null;
                BuildSourceTree(result.FileRepresentatives);
                SaveSettings();
                // 읽은 데이터를 반영한 뒤 첫 로그와 차트가 표시될 때까지 상태를 유지한다.
                m_bLoading = false;
                m_btnCancel.Enabled = false;
                SetLoadProgress("로딩 중 · 로그와 차트 준비", -1);
                await RefreshView();
            }
            catch (OperationCanceledException) { if (!m_bClosing) { m_lblStatus.Text = "읽기 취소 · 기존 로그 유지"; } }
            catch (Exception ex) { if (!m_bClosing) { ShowError("로그 읽기 실패: " + ex.Message); } }
            finally
            {
                m_loadCancellation = null;
                source.Dispose();
                m_bLoading = false;
                if (!m_bClosing)
                {
                    m_loadProgress.Visible = false;
                    m_lblLoadProgress.Visible = false;
                    SetLoadControls(true);
                }
            }
        }

        // 파일 읽기는 바이트 비율로 표시하고, 진행률을 계산할 수 없는 단계는 움직이는 막대로 표시한다.
        private void SetLoadProgress(string status, int percent)
        {
            m_loadProgress.Visible = true;
            m_lblLoadProgress.Visible = true;
            if (percent < 0)
            {
                m_loadProgress.Style = ProgressBarStyle.Marquee;
                m_lblLoadProgress.Text = status;
            }
            else
            {
                m_loadProgress.Style = ProgressBarStyle.Continuous;
                m_loadProgress.Value = percent;
                m_lblLoadProgress.Text = percent + "% · " + status;
            }
            m_lblStatus.Text = m_lblLoadProgress.Text;
            m_toolTip.SetToolTip(m_lblLoadProgress, m_lblLoadProgress.Text);
        }

        // 읽기 작업 중 설정/중복 읽기를 잠그고 취소만 허용한다.
        private void SetLoadControls(bool enabled)
        {
            m_btnFolder.Enabled = enabled; m_btnReload.Enabled = enabled; m_btnSettings.Enabled = enabled; m_txtFolder.Enabled = enabled;
            m_btnCancel.Enabled = !enabled; m_btnCancel.Visible = !enabled;
        }

        // 현재 통합 로그의 필터 결과를 고정해 선택 구간 원문을 백그라운드로 저장한다.
        private async Task ExportTimeRange(DateTime start, DateTime end)
        {
            if (m_bLoading || m_bClosing || m_exportCancellation != null) { return; }
            if (m_viewCancellation != null || m_filterTimer.Enabled) { m_lblStatus.Text = "조회 계산이 끝난 뒤 내보내세요."; return; }
            string path;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "로그 원문 (*.log)|*.log", DefaultExt = "log", FileName = "logs_" + start.ToString("yyyyMMdd_HHmmss_fff") + "_" + end.ToString("yyyyMMdd_HHmmss_fff") + ".log" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                path = dialog.FileName;
            }
            string destination = Path.GetFullPath(path);
            if (m_sourceFiles.Contains(destination, StringComparer.OrdinalIgnoreCase))
            { ShowError("불러온 원본 로그 파일에는 덮어쓸 수 없습니다."); return; }
            List<LogEntry> snapshot = m_visibleEntries;
            List<string> sourceFiles = m_sourceFiles;
            CancellationTokenSource source = new CancellationTokenSource();
            m_exportCancellation = source; SetLoadControls(false); m_lblStatus.Text = "선택 구간 내보내는 중…";
            try
            {
                int count = await Task.Run(delegate { return LogReader.ExportRange(path, snapshot, start, end, source.Token, sourceFiles); }, source.Token);
                if (!m_bClosing) { m_lblStatus.Text = "내보내기 완료 · " + count.ToString("N0") + "줄 · " + path; }
            }
            catch (OperationCanceledException) { if (!m_bClosing) { m_lblStatus.Text = "내보내기 취소 · 기존 파일 유지"; } }
            catch (Exception ex) { if (!m_bClosing) { ShowError("내보내기 실패: " + ex.Message); } }
            finally
            {
                m_exportCancellation = null; source.Dispose();
                if (!m_bClosing) { SetLoadControls(true); }
            }
        }

        // 실제 읽은 프로세스와 파일로 조회 트리를 구성한다.
        private void BuildSourceTree(List<LogEntry> fileRepresentatives)
        {
            m_bUpdatingSources = true;
            m_treeSources.BeginUpdate();
            m_treeSources.Nodes.Clear();
            TreeNode root = new TreeNode("전체 프로세스") { Checked = true };
            m_treeSources.Nodes.Add(root);
            int index = 0;
            m_nListedFileCount = 0;
            Dictionary<string, int> fileLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (LogEntry entry in m_entries)
            {
                int lines;
                fileLines.TryGetValue(entry.FilePath, out lines);
                fileLines[entry.FilePath] = lines + 1;
            }
            foreach (IGrouping<string, LogEntry> group in fileRepresentatives.GroupBy(delegate(LogEntry entry) { return entry.Process; }).OrderBy(delegate(IGrouping<string, LogEntry> group) { return group.Key; }, StringComparer.OrdinalIgnoreCase))
            {
                if (!m_settings.ProcessColors.ContainsKey(group.Key)) { m_settings.ProcessColors[group.Key] = m_arrDefaultColors[index % m_arrDefaultColors.Length]; }
                index++;
                LogSource processSource = new LogSource { Process = group.Key };
                TreeNode process = new TreeNode(group.Key) { Tag = processSource, Checked = true };
                root.Nodes.Add(process);
                foreach (string file in group.Select(delegate(LogEntry entry) { return entry.FilePath; }).Distinct().OrderBy(delegate(string file) { return file; }, StringComparer.OrdinalIgnoreCase))
                {
                    int lines;
                    fileLines.TryGetValue(file, out lines);
                    processSource.Lines += lines;
                    m_nListedFileCount++; process.Nodes.Add(new TreeNode(Path.GetFileName(file)) { Tag = new LogSource { Process = group.Key, FilePath = file, Lines = lines }, Checked = true });
                }
            }
            root.Expand();
            m_treeSources.SelectedNode = root;
            m_cmbKind.Items.Clear();
            m_cmbKind.Items.Add("모든 종류");
            foreach (string kind in fileRepresentatives.Select(delegate(LogEntry entry) { return entry.Kind; }).Distinct().OrderBy(delegate(string kind) { return kind; })) { m_cmbKind.Items.Add(kind); }
            m_cmbKind.SelectedIndex = 0;
            ColorSourceNodes();
            m_treeSources.EndUpdate();
            m_bUpdatingSources = false;
            SyncSourceTrees(true);
        }

        // 부모 체크 상태를 자식에 적용하고 파일 선택이 부모에도 반영되도록 한다.
        private void SourceChecked(object sender, TreeViewEventArgs e)
        {
            if (m_bUpdatingSources) { return; }
            TreeNode node = FindSourceNode(e.Node, m_treeSources);
            if (node == null) { return; }
            m_bUpdatingSources = true;
            node.Checked = e.Node.Checked;
            SetChildrenChecked(node, node.Checked);
            TreeNode parent = node.Parent;
            while (parent != null)
            {
                parent.Checked = parent.Nodes.Cast<TreeNode>().Any(delegate(TreeNode child) { return child.Checked; });
                parent = parent.Parent;
            }
            m_bUpdatingSources = false;
            SyncSourceTrees(false);
            ScheduleRefresh();
        }

        // 하위 로그 파일의 체크 상태를 함께 변경한다.
        private static void SetChildrenChecked(TreeNode node, bool value)
        {
            foreach (TreeNode child in node.Nodes) { child.Checked = value; SetChildrenChecked(child, value); }
        }

        // 프로세스 또는 파일을 선택하면 해당 항목만 별도로 조회한다.
        private void SourceSelected(object sender, TreeViewEventArgs e)
        {
            if (m_bUpdatingSources) { return; }
            TreeNode node = FindSourceNode(e.Node, m_treeSources);
            if (node == null) { return; }
            m_bUpdatingSources = true;
            m_treeSources.SelectedNode = node;
            m_bUpdatingSources = false;
            LogSource source = node.Tag as LogSource;
            m_strSelectedProcess = source == null ? null : source.Process;
            m_strSelectedFile = source == null ? null : source.FilePath;
            SyncSourceTrees(false);
            ScheduleRefresh();
        }

        // 모든 프로세스와 종류를 선택하여 통합 조회로 돌아간다.
        private void ShowAll()
        {
            if (m_treeSources.Nodes.Count == 0) { return; }
            m_bUpdatingSources = true;
            TreeNode root = m_treeSources.Nodes[0];
            root.Checked = true; SetChildrenChecked(root, true);
            m_treeSources.SelectedNode = root;
            m_strSelectedProcess = null; m_strSelectedFile = null;
            m_cmbKind.SelectedIndex = 0;
            m_bUpdatingSources = false;
            SyncSourceTrees(false);
            ScheduleRefresh();
        }

        // 트리의 노드 인덱스 경로로 다른 창의 같은 프로세스/파일 항목을 찾는다.
        private static TreeNode FindSourceNode(TreeNode source, TreeView target)
        {
            if (source == null) { return null; }
            Stack<int> indexes = new Stack<int>();
            for (TreeNode node = source; node != null; node = node.Parent) { indexes.Push(node.Index); }
            TreeNodeCollection nodes = target.Nodes;
            TreeNode found = null;
            while (indexes.Count > 0)
            {
                int index = indexes.Pop();
                if (index < 0 || index >= nodes.Count) { return null; }
                found = nodes[index]; nodes = found.Nodes;
            }
            return found;
        }

        // 같은 구조의 트리에 체크 상태를 복사한다. 노드는 유지하여 현재 펼침 상태를 보존한다.
        private static void CopySourceChecks(TreeNode source, TreeNode target)
        {
            target.Checked = source.Checked;
            for (int index = 0; index < source.Nodes.Count; index++) { CopySourceChecks(source.Nodes[index], target.Nodes[index]); }
        }

        // 메인 트리의 파일 목록과 필터 선택을 모든 분리 창에 반영하고 재귀 이벤트를 막는다.
        private void SyncSourceTrees(bool rebuild)
        {
            bool updating = m_bUpdatingSources;
            m_bUpdatingSources = true;
            try
            {
                foreach (TreeView tree in m_sourceTrees)
                {
                    if (tree == m_treeSources) { continue; }
                    tree.BeginUpdate();
                    try
                    {
                        if (rebuild)
                        {
                            tree.Nodes.Clear();
                            foreach (TreeNode root in m_treeSources.Nodes) { tree.Nodes.Add((TreeNode)root.Clone()); }
                            if (tree.Nodes.Count > 0) { tree.Nodes[0].Expand(); }
                        }
                        else
                        {
                            for (int index = 0; index < m_treeSources.Nodes.Count; index++) { CopySourceChecks(m_treeSources.Nodes[index], tree.Nodes[index]); }
                        }
                        tree.SelectedNode = FindSourceNode(m_treeSources.SelectedNode, tree);
                        if (tree.SelectedNode != null) { tree.SelectedNode.EnsureVisible(); }
                    }
                    finally { tree.EndUpdate(); }
                }
            }
            finally { m_bUpdatingSources = updating; }
        }

        // 연속 입력은 한 번의 백그라운드 재계산으로 합친다.
        private void ScheduleRefresh()
        {
            if (m_bUpdatingSources || m_bLoading || m_bClosing) { return; }
            if (m_viewCancellation != null) { m_viewCancellation.Cancel(); }
            m_filterTimer.Stop();
            m_filterTimer.Start();
        }

        // UI 상태의 스냅샷으로 필터, 검색, 차트를 함께 계산하고 최신 결과만 반영한다.
        private async Task RefreshView()
        {
            if (m_bLoading || m_bClosing) { return; }
            m_filterTimer.Stop();
            if (m_viewCancellation != null) { m_viewCancellation.Cancel(); }
            bool useTimeRange = m_chkTimeRange.Checked;
            // 입력 표시와 같은 초 경계로 맞추고 끝 초의 마지막 로그까지 포함한다.
            DateTime rangeStart = new DateTime(m_dtStart.Value.Ticks - m_dtStart.Value.Ticks % TimeSpan.TicksPerSecond);
            DateTime rangeEndExclusive = new DateTime(m_dtEnd.Value.Ticks - m_dtEnd.Value.Ticks % TimeSpan.TicksPerSecond).AddSeconds(1);
            if (useTimeRange && rangeStart >= rangeEndExclusive)
            {
                m_lblStatus.Text = "시간 구간 오류 · 시작 시간이 끝 시간보다 늦습니다.";
                return;
            }
            CancellationTokenSource source = new CancellationTokenSource();
            m_viewCancellation = source;
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m_treeSources.Nodes.Count > 0)
            {
                foreach (TreeNode process in m_treeSources.Nodes[0].Nodes)
                { foreach (TreeNode file in process.Nodes) { if (file.Checked) { files.Add(((LogSource)file.Tag).FilePath); } } }
            }
            string selectedProcess = m_strSelectedProcess;
            string selectedFile = m_strSelectedFile;
            string kind = m_cmbKind.SelectedIndex > 0 ? (string)m_cmbKind.SelectedItem : null;
            string query = m_txtSearch.Text;
            bool caseSensitive = m_chkCase.Checked;
            bool useRegex = m_chkRegex.Checked;
            List<LogEntry> entries = m_entries;
            bool showAll = files.Count == m_nListedFileCount && selectedProcess == null && selectedFile == null && kind == null && !useTimeRange;
            List<KeywordRule> rules = m_rules;
            m_lblStatus.Text = "조회 계산 중…";
            try
            {
                Tuple<List<LogEntry>, List<LogEntry>, TimingData, string> result = await Task.Run(delegate
                {
                    List<LogEntry> visible = entries;
                    if (!showAll)
                    {
                        visible = new List<LogEntry>();
                        foreach (LogEntry entry in entries)
                        {
                            source.Token.ThrowIfCancellationRequested();
                            if (!files.Contains(entry.FilePath)) { continue; }
                            if (selectedProcess != null && selectedProcess != entry.Process) { continue; }
                            if (selectedFile != null && selectedFile != entry.FilePath) { continue; }
                            if (kind != null && kind != entry.Kind) { continue; }
                            if (useTimeRange)
                            {
                                if (!entry.Timestamp.HasValue) { continue; }
                                if (entry.Timestamp.Value < rangeStart || entry.Timestamp.Value >= rangeEndExclusive) { continue; }
                            }
                            visible.Add(entry);
                        }
                    }
                    List<LogEntry> matches = new List<LogEntry>();
                    string searchError = null;
                    try { matches = LogReader.Search(visible, query, caseSensitive, useRegex, source.Token); }
                    catch (ArgumentException ex) { searchError = ex.Message; }
                    catch (RegexMatchTimeoutException ex) { searchError = ex.Message; }
                    TimingData timing = TimingData.Build(visible, rules, source.Token);
                    if (useTimeRange) { timing.Minimum = rangeStart; timing.Maximum = rangeEndExclusive.AddTicks(-1); }
                    return Tuple.Create(visible, matches, timing, searchError);
                }, source.Token);
                if (m_bClosing || source.IsCancellationRequested || m_viewCancellation != source) { return; }
                ClearTextSelection();
                bool sameLogView = Object.ReferenceEquals(m_visibleEntries, result.Item1);
                m_visibleEntries = result.Item1; m_searchEntries = result.Item2;
                if (!sameLogView) { DisplayGridPage(m_gridLogs, 1); }
                DisplayGridPage(m_gridSearch, 1);
                m_chart.SetData(result.Item3);
                m_lblEvents.Text = "타이밍 차트 · " + result.Item3.EventCount.ToString("N0") + "건";
                if (result.Item3.AnomalyCount > 0) { m_lblEvents.Text += " · 간격 이상 " + result.Item3.AnomalyCount.ToString("N0"); }
                m_toolTip.SetToolTip(m_lblEvents, "이벤트 " + result.Item3.EventCount.ToString("N0") + "건 · 간격 이상 " + result.Item3.AnomalyCount.ToString("N0") + "구간");
                if (m_bClosing || source.IsCancellationRequested || m_viewCancellation != source) { return; }
                m_lblScope.Text = "통합 로그";
                if (selectedProcess != null) { m_lblScope.Text = selectedProcess; }
                if (selectedFile != null) { m_lblScope.Text = Path.GetFileName(selectedFile); }
                m_lblScope.Text += " · " + m_visibleEntries.Count.ToString("N0") + "줄";
                m_lblSearchCount.Text = "검색 결과 · " + m_searchEntries.Count.ToString("N0") + "건";
                m_gridSearch.EmptyText = result.Item4 != null ? "정규식을 해석할 수 없습니다. 아래 상태 표시줄의 오류를 확인하세요."
                    : String.IsNullOrEmpty(query) ? SearchHint : "일치하는 로그가 없습니다. 검색어나 필터를 바꿔 보세요.";
                m_strSearchMark = result.Item4 == null && !String.IsNullOrEmpty(query) ? query : null;
                m_bSearchMarkCase = caseSensitive;
                m_searchMarkPattern = m_strSearchMark != null && useRegex ? LogReader.CreateRegex(query, caseSensitive) : null;
                m_gridSearch.Invalidate(); m_gridSearch.LargeView.Invalidate();

                m_lblStatus.Text = "파일 " + m_nFileCount.ToString("N0") + "개  ·  전체 " + m_entries.Count.ToString("N0") + "줄  ·  표시 " + m_visibleEntries.Count.ToString("N0") + "줄  ·  시간 미인식 " + m_nMissingTimestampCount.ToString("N0") + "줄  ·  프리셋 " + Path.GetFileName(m_strKeywordPath) + (m_bKeywordDirty ? " *" : "");
                if (result.Item4 != null)
                {
                    m_lblSearchCount.Text = "검색 결과 · 정규식 오류";
                    m_lblStatus.Text = "검색 오류 · " + result.Item4;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!m_bClosing && m_viewCancellation == source)
                {
                    m_lblStatus.Text = "조회 실패 · " + ex.Message;
                    m_searchEntries = new List<LogEntry>(); DisplayGridPage(m_gridSearch, 1);
                    m_lblSearchCount.Text = "검색 결과 · 오류";
                    m_gridSearch.EmptyText = "조회에 실패했습니다. 아래 상태 표시줄의 오류를 확인하세요."; m_gridSearch.Invalidate();
                }
            }
            finally
            {
                if (m_viewCancellation == source) { m_viewCancellation = null; }
                source.Dispose();
            }
        }

        // 각 뷰어의 배치 전 위치를 보관하고 현재 모니터의 작업 영역에 창을 배치한다.
        private void ArrangeViewerWindow(Form window, Keys arrow)
        {
            Rectangle area = Screen.FromHandle(window.Handle).WorkingArea;
            int middle = area.Left + area.Width / 2;
            Rectangle left = Rectangle.FromLTRB(area.Left, area.Top, middle, area.Bottom);
            Rectangle right = Rectangle.FromLTRB(middle, area.Top, area.Right, area.Bottom);
            Rectangle normal = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
            Rectangle original;
            bool snapped = m_snapRestoreBounds.TryGetValue(window, out original) && (normal == left || normal == right);
            switch (arrow)
            {
                case Keys.Left:
                case Keys.Right:
                    if (!snapped) { m_snapRestoreBounds[window] = normal; }
                    window.WindowState = FormWindowState.Normal;
                    window.Bounds = arrow == Keys.Left ? left : right;
                    break;
                case Keys.Up:
                    window.WindowState = FormWindowState.Maximized;
                    break;
                case Keys.Down:
                    if (window.WindowState == FormWindowState.Maximized) { window.WindowState = FormWindowState.Normal; }
                    else if (snapped)
                    {
                        window.Bounds = original;
                        m_snapRestoreBounds.Remove(window);
                    }
                    else { window.WindowState = FormWindowState.Minimized; }
                    break;
            }
        }

        // Alt+방향키는 입력을 받은 뷰어에, 휠은 포인터 아래의 로그 표에 전달한다.
        public bool PreFilterMessage(ref Message message)
        {
            const int SystemKeyDown = 0x0104, AltContext = 1 << 29, PreviousKeyState = 1 << 30;
            if (message.Msg == SystemKeyDown && (message.LParam.ToInt64() & AltContext) != 0)
            {
                Keys arrow = (Keys)message.WParam.ToInt32();
                if (arrow < Keys.Left || arrow > Keys.Down || (Control.ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None) { return false; }
                Control target = Control.FromChildHandle(message.HWnd);
                Form window = target == null ? null : target.FindForm();
                if (window == null || !window.Enabled || (window != this && window != m_logWindow && window != m_searchWindow && window != m_timingWindow)) { return false; }
                ComboBox combo = target as ComboBox;
                if (combo != null && (combo.DroppedDown || arrow == Keys.Down || arrow == Keys.Up)) { return false; }
                if ((message.LParam.ToInt64() & PreviousKeyState) == 0) { ArrangeViewerWindow(window, arrow); }
                return true;
            }
            if (message.Msg != WheelMessage) { return false; }
            Point point = new Point(unchecked((int)message.LParam.ToInt64()));
            Control recipient = Control.FromChildHandle(message.HWnd);
            if (recipient == null) { return false; }
            Control hit = Control.FromChildHandle(WindowFromPoint(point));
            LogGrid grid = m_gridLogs;
            for (int index = 0; index < 2; index++, grid = m_gridSearch)
            {
                Control view = grid.LargeView.Visible ? (Control)grid.LargeView : grid;
                if (!view.Visible || !view.Enabled || view.FindForm() == null || !view.FindForm().Enabled) { continue; }
                if (!view.RectangleToScreen(view.ClientRectangle).Contains(point)) { continue; }
                if (hit != null && hit != view && !view.Contains(hit)) { continue; }
                if (hit == null && recipient.FindForm() != view.FindForm()) { continue; }
                if (message.HWnd == view.Handle) { return false; }
                SendMessage(view.Handle, message.Msg, message.WParam, message.LParam);
                return true;
            }
            return false;
        }

        // 포인터 위치에서 휠 입력 대상 창을 찾는다.
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point position);

        // 원래 휠 델타와 키 상태를 대상 창의 기본 입력 처리기에 전달한다.
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        // 입력란이 비어 있을 때 회색 안내 문구(cue banner)를 표시한다.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);
        private const int SetCueBanner = 0x1501;
        private const string SearchHint = "검색어를 입력하면 일치하는 로그가 여기에 표시됩니다.";

        // 페이지 끝에 다음 화면의 행을 겹쳐 두어 휠로 경계를 넘을 때도 행이 이어지게 한다.
        private void DisplayGridPage(LogGrid grid, int requestedPage, int localTop = 0)
        {
            if (m_bUpdatingPage) { return; }
            bool main = grid == m_gridLogs;
            List<LogEntry> entries = main ? m_visibleEntries : m_searchEntries;
            NumericUpDown picker = main ? m_logPage : m_searchPage;
            VirtualLogList list = grid.LargeView;
            // 필터나 검색 결과 수와 관계없이 두 뷰어에 같은 표시 방식과 행 높이를 사용한다.
            bool large = entries.Count > 0 && m_entries.Count > LargeViewThreshold;
            int pages = large ? (entries.Count - 1) / GridPageSize + 1 : 1;
            int page = Math.Max(1, Math.Min(pages, requestedPage));
            m_bUpdatingPage = true;
            try
            {
                if (m_textSelectionGrid == grid) { ClearTextSelection(); }
                if (main) { m_logPageRows = entries; } else { m_searchPageRows = entries; }
                int first = (page - 1) * GridPageSize;
                if (list.Entries != entries || list.FirstEntry != first || !large)
                {
                    list.SelectedIndices.Clear();
                    list.VirtualListSize = 0;
                    list.FirstEntry = first;
                    list.Entries = large ? entries : null;
                    if (large) { UpdateLargeListStyle(grid); }
                    if (large) { list.VirtualListSize = Math.Min(GridPageSize + PageOverlapRows, entries.Count - first); }
                }
                grid.Visible = !large;
                int gridCount = large ? 0 : entries.Count;
                if (grid.RowCount > gridCount) { grid.RowCount = 0; }
                grid.RowCount = gridCount;
                picker.Maximum = pages; picker.Value = page;
                picker.Parent.Controls["PageTotal"].Text = "/ " + pages.ToString("N0") + " 페이지";
                picker.Parent.Visible = pages > 1;
                list.Visible = large;
                if (large)
                {
                    list.ScrollToLocalRow(localTop);
                    list.Invalidate();
                }
                else { grid.Invalidate(); }
            }
            finally { m_bUpdatingPage = false; }
        }
        // 가상 그리드가 요청한 셀만 원문 데이터에서 가져온다.
        private void GridValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            List<LogEntry> entries = sender == m_gridLogs ? m_logPageRows : m_searchPageRows;
            if (e.RowIndex < 0 || e.RowIndex >= entries.Count) { return; }
            LogEntry entry = entries[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = entry.Timestamp.HasValue ? entry.Timestamp.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "시간 미인식"; break;
                case 1: e.Value = entry.Timestamp.HasValue ? entry.Timestamp.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : ""; break;
                case 2: e.Value = entry.Process; break;
                case 3: e.Value = entry.Kind; break;
                case 4: e.Value = entry.DisplayText; break;
                case 5: e.Value = entry.LineNumber; break;
            }
        }

        // 기본은 테마의 무색 배경이며 프로세스 구분을 켜면 프로세스별 색을 적용한다.
        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            List<LogEntry> entries = sender == m_gridLogs ? m_logPageRows : m_searchPageRows;
            if (e.RowIndex < 0 || e.RowIndex >= entries.Count) { return; }
            Color background = m_settings.DarkMode ? Color.FromArgb(26, 35, 48) : Color.White;
            if (m_chkTaskColors.Checked) { background = ProcessColor(entries[e.RowIndex].Process); }
            e.CellStyle.BackColor = background;
            e.CellStyle.ForeColor = Theme.TextOn(background);
            e.CellStyle.SelectionBackColor = Theme.Mix(background, m_settings.DarkMode ? Color.FromArgb(89, 151, 244) : Color.FromArgb(93, 161, 255), 0.25);
            e.CellStyle.SelectionForeColor = Theme.TextOn(e.CellStyle.SelectionBackColor);
        }

        // 잘린 로그의 원문과 실제 파일 위치를 툴팁으로 제공한다.
        private void GridToolTip(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            List<LogEntry> entries = sender == m_gridLogs ? m_logPageRows : m_searchPageRows;
            if (e.RowIndex < 0 || e.RowIndex >= entries.Count) { return; }
            LogEntry entry = entries[e.RowIndex];
            e.ToolTipText = entry.FilePath + " : " + entry.LineNumber + Environment.NewLine + entry.Text;
        }

        // 검색 결과 또는 차트 이벤트의 같은 원문 행으로 이동한다.
        private void SelectMainEntry(LogEntry entry)
        {
            int index = m_visibleEntries.IndexOf(entry);
            if (index < 0) { return; }
            if (m_gridLogs.LargeView.VirtualListSize > 0)
            {
                VirtualLogList list = m_gridLogs.LargeView;
                if (index < list.FirstEntry || index >= list.FirstEntry + list.VirtualListSize)
                { DisplayGridPage(m_gridLogs, index / GridPageSize + 1, index % GridPageSize); }
                ListViewItem item = list.Items[index - list.FirstEntry];
                item.EnsureVisible(); item.Selected = true; item.Focused = true;
                m_chart.SelectEntry(entry);
                return;
            }
            m_gridLogs.ClearSelection();
            m_gridLogs.CurrentCell = m_gridLogs.Rows[index].Cells[0];
            m_gridLogs.Rows[index].Selected = true;
            m_chart.SelectEntry(entry);
        }

        // 통합/검색 그리드의 클릭과 키보드 선택을 같은 차트 커서에 반영한다.
        private void GridSelectionChanged(object sender, EventArgs e)
        {
            DataGridView grid = (DataGridView)sender;
            if (m_bUpdatingPage || (grid == m_textSelectionGrid && m_bDraggingText)) { return; }
            if (grid == m_textSelectionGrid) { ClearTextSelection(); }
            List<LogEntry> rows = grid == m_gridLogs ? m_logPageRows : m_searchPageRows;
            if (grid.CurrentCell == null || grid.CurrentCell.RowIndex >= rows.Count) { return; }
            m_chart.SelectEntry(rows[grid.CurrentCell.RowIndex]);
        }

        // 로그/검색/차트 뷰어 자체와 도구를 별도 창으로 이동하여 데이터와 선택 상태를 공유한다.
        private void OpenViewerWindow(Control viewer)
        {
            bool timing = viewer == m_panelTiming;
            bool search = viewer == m_panelSearch;
            Form current;
            SplitterPanel parent;
            Button popout;
            string title;
            if (timing)
            {
                current = m_timingWindow; parent = m_splitTiming.Panel1; popout = m_btnPopoutTiming; title = "타이밍 차트";
            }
            else if (search)
            {
                current = m_searchWindow; parent = m_splitResults.Panel2; popout = m_btnPopoutSearch; title = "검색 결과";
            }
            else
            {
                current = m_logWindow; parent = m_splitResults.Panel1; popout = m_btnPopoutLogs; title = "통합 로그";
            }
            if (current != null) { current.Close(); return; }
            Form window = new Form { Text = title, Size = new Size(1280, 760), MinimumSize = ViewerMinimumSize, StartPosition = FormStartPosition.CenterParent, Font = Font, AutoScaleMode = AutoScaleMode.Dpi, Padding = new Padding(8) };
            window.AutoScroll = true;
            window.AutoScrollMinSize = new Size(830, 380);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1248, 705), SplitterDistance = 245, Panel1MinSize = 180, Panel2MinSize = 600, SplitterWidth = 8 };
            TableLayoutPanel sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Tag = "sidebar" };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, UiActionRowHeight));
            TreeView tree = MakeSourceTree();
            tree.AfterCheck += SourceChecked; tree.AfterSelect += SourceSelected;
            sidebar.Controls.Add(Heading("프로세스 / 파일"), 0, 0); sidebar.Controls.Add(tree, 0, 1);
            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            Button all = MakeButton("전체 보기", 100); all.Dock = DockStyle.Fill;
            Button color = MakeButton("배경색", 100); color.Dock = DockStyle.Fill;
            all.Click += delegate { ShowAll(); }; color.Click += delegate { ChooseProcessColor(); };
            actions.Controls.Add(all, 0, 0); actions.Controls.Add(color, 1, 0);
            sidebar.Controls.Add(actions, 0, 2); split.Panel1.Controls.Add(sidebar);
            window.Controls.Add(split);
            int timingHeight = m_splitTiming.SplitterDistance;
            Button restore = null;
            parent.Controls.Remove(viewer);
            if (timing) { m_splitTiming.Panel1Collapsed = true; }
            else
            {
                restore = MakeButton("뷰어 가져오기", 150); restore.Dock = DockStyle.Fill;
                restore.Click += delegate { window.Close(); };
                parent.Controls.Add(restore);
            }
            split.Panel2.Controls.Add(viewer); popout.Text = "원래 위치";
            m_sourceTrees.Add(tree);
            if (timing) { m_timingWindow = window; }
            else if (search) { m_searchWindow = window; }
            else { m_logWindow = window; }
            window.FormClosed += delegate
            {
                if (timing) { m_timingWindow = null; }
                else if (search) { m_searchWindow = null; }
                else { m_logWindow = null; }
                m_snapRestoreBounds.Remove(window);
                m_sourceTrees.Remove(tree);
                if (m_bClosing || IsDisposed) { return; }
                split.Panel2.Controls.Remove(viewer);
                if (restore != null) { parent.Controls.Remove(restore); restore.Dispose(); }
                parent.Controls.Add(viewer); popout.Text = "새 창";
                if (timing)
                {
                    // 분리 전 높이를 복원하되 작아진 메인 창에서도 두 패널의 최소 높이를 보장한다.
                    m_splitTiming.Panel1Collapsed = false;
                    int maximumHeight = m_splitTiming.ClientSize.Height - m_splitTiming.SplitterWidth - m_splitTiming.Panel2MinSize;
                    m_splitTiming.SplitterDistance = Math.Max(m_splitTiming.Panel1MinSize, Math.Min(timingHeight, maximumHeight));
                }
            };
            Theme.Apply(window, m_settings.DarkMode);
            if (timing) { m_chart.ApplyTheme(m_settings.DarkMode); ColorKeywordChips(); }
            SyncSourceTrees(true); ColorSourceNodes();
            window.Show(this);
        }

        // 선택 영역을 초기화하며 다른 행으로 문자열 선택이 이어지지 않게 한다.
        private void ClearTextSelection()
        {
            LogGrid grid = m_textSelectionGrid;
            if (grid == null) { return; }
            m_bDraggingText = false;
            grid.Capture = false; grid.LargeView.Capture = false;
            int localRow = m_nTextRow - grid.LargeView.FirstEntry;
            if (localRow >= 0 && localRow < grid.LargeView.VirtualListSize) { grid.LargeView.RedrawItems(localRow, localRow, true); }
            if (m_nTextRow >= 0 && m_nTextRow < grid.RowCount) { grid.InvalidateCell(LogTextColumn, m_nTextRow); }
            m_textSelectionGrid = null; m_selectedTextEntry = null;
            m_nTextRow = -1; m_nTextAnchor = 0; m_nTextCaret = 0;
            m_menuHighlight.Enabled = false;
        }

        // 드래그 시작 로그 안의 선택 문자열만 반환한다.
        private string SelectedLogText()
        {
            if (m_selectedTextEntry == null || m_chkTaskColors.Checked) { return ""; }
            return m_selectedTextEntry.DisplayText.Substring(Math.Min(m_nTextAnchor, m_nTextCaret), Math.Abs(m_nTextCaret - m_nTextAnchor));
        }

        // 화면에 그리는 것과 같은 글꼴과 옵션으로 원문 접두사의 폭을 구한다.
        private int LogTextWidth(Graphics graphics, string text, int length, Font font)
        {
            if (length == 0) { return 0; }
            return TextRenderer.MeasureText(graphics, text.Substring(0, length), font, new Size(Int32.MaxValue, Int32.MaxValue), LogTextFlags).Width;
        }

        // 마우스의 가로 위치를 선택한 로그 셀의 가장 가까운 문자 경계로 변환한다.
        private int LogCharacterAt(LogGrid grid, int mouseX)
        {
            Rectangle cell = grid.GetColumnDisplayRectangle(LogTextColumn, false);
            int x = mouseX - cell.Left - grid.DefaultCellStyle.Padding.Left;
            using (Graphics graphics = grid.CreateGraphics())
            {
                return LogCharacterIndex(graphics, m_selectedTextEntry.DisplayText, grid.DefaultCellStyle.Font, x);
            }
        }

        // 긴 로그도 화면 위치에 필요한 접두사만 측정하여 문자 경계를 찾는다.
        private int LogCharacterIndex(Graphics graphics, string text, Font font, int x)
        {
            const int InitialMeasureCharacters = 32;
            int low = 0; int high = Math.Min(text.Length, InitialMeasureCharacters);
            while (high < text.Length && LogTextWidth(graphics, text, high, font) < x)
            {
                low = high;
                high = (int)Math.Min(text.Length, (long)high * 2);
            }
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (LogTextWidth(graphics, text, middle, font) < x) { low = middle + 1; }
                else { high = middle; }
            }
            if (low > 0 && x - LogTextWidth(graphics, text, low - 1, font) < LogTextWidth(graphics, text, low, font) - x) { low--; }
            return low;
        }

        // 로그 셀에서만 부분 문자열 드래그를 시작하고 원본 행을 고정한다.
        private void LogTextMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || m_chkTaskColors.Checked) { return; }
            LogGrid grid = (LogGrid)sender;
            DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
            ClearTextSelection();
            if (hit.RowIndex < 0 || hit.ColumnIndex != LogTextColumn) { return; }
            if (grid.CurrentCellAddress != new Point(LogTextColumn, hit.RowIndex))
            { grid.CurrentCell = grid.Rows[hit.RowIndex].Cells[LogTextColumn]; }
            m_textSelectionGrid = grid; m_nTextRow = hit.RowIndex;
            m_selectedTextEntry = (grid == m_gridLogs ? m_logPageRows : m_searchPageRows)[hit.RowIndex];
            m_nTextAnchor = LogCharacterAt(grid, e.X); m_nTextCaret = m_nTextAnchor;
            m_bDraggingText = true; grid.Capture = true;
            m_chart.SelectEntry(m_selectedTextEntry);
        }

        // 세로로 드래그해도 시작 행만 유지하고 가로 좌표로 선택 범위를 갱신한다.
        private void LogTextMouseMove(object sender, MouseEventArgs e)
        {
            LogGrid grid = (LogGrid)sender;
            if (!m_bDraggingText || m_textSelectionGrid != grid) { return; }
            int row = m_nTextRow;
            if (grid.CurrentCellAddress != new Point(LogTextColumn, row))
            { grid.CurrentCell = grid.Rows[row].Cells[LogTextColumn]; }
            m_nTextCaret = LogCharacterAt(grid, e.X);
            m_menuHighlight.Enabled = !String.IsNullOrWhiteSpace(SelectedLogText());
            grid.InvalidateCell(LogTextColumn, row);
        }

        // 선택한 문자열의 모든 일치 영역에 사용할 배경색을 팔레트에서 받는다.
        private void ChooseHighlightColor()
        {
            string selected = SelectedLogText();
            if (String.IsNullOrWhiteSpace(selected)) { return; }
            using (ColorDialog dialog = new ColorDialog { FullOpen = true, Color = Color.Yellow })
            {
                if (dialog.ShowDialog(m_textSelectionGrid.FindForm()) != DialogResult.OK) { return; }
                SetHighlight(selected, dialog.Color);
            }
        }

        // 문자열별 마지막 지정 색을 적용하고 현재 프리셋의 미저장 변경으로 표시한다.
        private void SetHighlight(string text, Color color)
        {
            if (String.IsNullOrWhiteSpace(text)) { return; }
            if (m_highlights.Count >= KeywordSettings.MaximumHighlights && !m_highlights.Any(delegate(KeyValuePair<string, Color> pair) { return pair.Key == text; }))
            { ShowError("문자열 강조는 최대 " + KeywordSettings.MaximumHighlights + "개입니다."); return; }
            m_highlights.RemoveAll(delegate(KeyValuePair<string, Color> pair) { return pair.Key == text; });
            m_highlights.Add(new KeyValuePair<string, Color>(text, color));
            m_bKeywordDirty = true; m_btnSaveKeywords.Text = "저장 *";
            ClearTextSelection(); RefreshLogColors(); m_menuClearHighlights.Enabled = !m_chkTaskColors.Checked;
        }

        // 로그 셀의 문자별 강조색을 계산한다. 겹치는 문자열은 마지막 지정 색을 사용한다.
        private Color[] LogHighlightColors(string text, int visibleLength)
        {
            Color[] colors = new Color[Math.Min(text.Length, visibleLength)];
            foreach (KeyValuePair<string, Color> highlight in m_highlights)
            {
                if (highlight.Key.Length == 0) { continue; }
                int start = 0;
                while (start < colors.Length)
                {
                    int searchLength = (int)Math.Min(text.Length - start, (long)colors.Length - start + highlight.Key.Length - 1);
                    int index = text.IndexOf(highlight.Key, start, searchLength, StringComparison.Ordinal);
                    if (index < 0) { break; }
                    for (int character = index; character < Math.Min(colors.Length, index + highlight.Key.Length); character++) { colors[character] = highlight.Value; }
                    start = index + highlight.Key.Length;
                }
            }
            return colors;
        }

        // 검색 결과 뷰어에서 현재 검색어와 일치하는 부분을 노란색으로 표시한다.
        private void MarkSearchMatches(string text, Color[] colors)
        {
            Color markColor = Color.FromArgb(255, 229, 138);
            if (m_searchMarkPattern != null)
            {
                try
                {
                    foreach (Match match in m_searchMarkPattern.Matches(text))
                    {
                        if (match.Length == 0) { continue; }
                        for (int index = match.Index; index < Math.Min(colors.Length, match.Index + match.Length); index++) { colors[index] = markColor; }
                    }
                }
                catch (RegexMatchTimeoutException) { }
                return;
            }
            StringComparison comparison = m_bSearchMarkCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int start = 0;
            while (start < colors.Length)
            {
                int found = text.IndexOf(m_strSearchMark, start, comparison);
                if (found < 0 || found >= colors.Length) { break; }
                for (int index = found; index < Math.Min(colors.Length, found + m_strSearchMark.Length); index++) { colors[index] = markColor; }
                start = found + m_strSearchMark.Length;
            }
        }

        // 표의 컬럼 배치를 유지하면서 보이는 로그 셀에만 부분 강조와 문자열 선택을 그린다.
        private void PaintLogText(object sender, DataGridViewCellPaintingEventArgs e)
        {
            LogGrid grid = (LogGrid)sender;
            List<LogEntry> rows = grid == m_gridLogs ? m_logPageRows : m_searchPageRows;
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count || e.ColumnIndex != LogTextColumn) { return; }
            LogEntry entry = rows[e.RowIndex];
            Rectangle content = e.CellBounds;
            content.X += e.CellStyle.Padding.Left; content.Width -= e.CellStyle.Padding.Horizontal;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
            DrawHighlightedText(grid, e.Graphics, entry, content, e.ClipBounds, e.CellStyle.Font, e.CellStyle.ForeColor);
            e.Handled = true;
        }

        // 두 뷰어에서 같은 글꼴과 위치 기준으로 강조 문자열과 드래그 영역을 그린다.
        private void DrawHighlightedText(LogGrid grid, Graphics graphics, LogEntry entry, Rectangle content, Rectangle clip, Font font, Color foreground)
        {
            Rectangle visible = Rectangle.Intersect(content, clip);
            if (visible.Width <= 0 || visible.Height <= 0) { return; }
            string fullText = entry.DisplayText;
            int visibleLength = fullText.Length;
            if (fullText.Length > content.Width)
            {
                visibleLength = Math.Min(fullText.Length, LogCharacterIndex(graphics, fullText, font, content.Width) + 1);
            }
            string text = fullText.Substring(0, visibleLength);
            Point origin = new Point(content.Left, content.Top + Math.Max(0, (content.Height - font.Height) / 2));
            GraphicsState state = graphics.Save();
            graphics.SetClip(visible);
            TextRenderer.DrawText(graphics, text, font, origin, foreground, LogTextFlags);
            bool selected = grid == m_textSelectionGrid && entry == m_selectedTextEntry && m_nTextAnchor != m_nTextCaret;
            bool mark = grid == m_gridSearch && m_strSearchMark != null;
            if (!mark && (m_chkTaskColors.Checked || (!selected && m_highlights.Count == 0))) { graphics.Restore(state); return; }
            Color[] colors = m_chkTaskColors.Checked ? new Color[visibleLength] : LogHighlightColors(fullText, visibleLength);
            if (mark) { MarkSearchMatches(fullText, colors); }
            if (selected)
            {
                for (int index = Math.Min(m_nTextAnchor, m_nTextCaret); index < Math.Min(colors.Length, Math.Max(m_nTextAnchor, m_nTextCaret)); index++) { colors[index] = SystemColors.Highlight; }
            }
            int start = 0;
            while (start < colors.Length)
            {
                int end = start + 1;
                while (end < colors.Length && colors[end] == colors[start]) { end++; }
                if (!colors[start].IsEmpty)
                {
                    int left = LogTextWidth(graphics, text, start, font);
                    int right = LogTextWidth(graphics, text, end, font);
                    Rectangle run = Rectangle.Intersect(new Rectangle(content.Left + left, content.Top, right - left, content.Height), Rectangle.Intersect(content, clip));
                    if (run.Width > 0)
                    {
                        graphics.SetClip(run);
                        using (SolidBrush brush = new SolidBrush(colors[start])) { graphics.FillRectangle(brush, run); }
                        TextRenderer.DrawText(graphics, text, font, origin, Theme.TextOn(colors[start]), LogTextFlags);
                    }
                }
                start = end;
            }
            graphics.Restore(state); 
        }

        // 두 로그 뷰어에 현재 프리셋과 테마의 색상을 함께 반영한다.
        private void RefreshLogColors()
        {
            m_gridLogs.Invalidate(); UpdateLargeListStyle(m_gridLogs);
            m_gridSearch.Invalidate(); UpdateLargeListStyle(m_gridSearch);
        }

        // 가상 목록의 배경색과 글자색을 적용한다.
        private void UpdateLargeListStyle(LogGrid grid)
        {
            VirtualLogList list = grid.LargeView;
            list.BackColor = grid.DefaultCellStyle.BackColor;
            list.ForeColor = grid.DefaultCellStyle.ForeColor;
            list.Invalidate();
        }
        // Windows 가상 목록은 요청된 행만 구성하며 선택과 강조는 기존 프리셋 설정을 사용한다.
        private void ConfigureLargeList(LogGrid grid)
        {
            bool main = grid == m_gridLogs;
            VirtualLogList list = grid.LargeView;
            list.AccessibleName = grid.AccessibleName;
            list.Font = grid.DefaultCellStyle.Font;
            list.OwnerDraw = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.ShowItemToolTips = true;
            foreach (DataGridViewColumn column in grid.Columns) { list.Columns.Add(column.HeaderText, column.Width); }
            list.Resize += delegate
            {
                int otherWidth = 0;
                foreach (ColumnHeader column in list.Columns) { if (column.Index != LogTextColumn) { otherWidth += column.Width; } }
                list.Columns[LogTextColumn].Width = Math.Max(grid.Columns[LogTextColumn].MinimumWidth, list.ClientSize.Width - otherWidth - 2);
            };
            const int CacheRows = 128;
            LogEntry[] keys = new LogEntry[CacheRows];
            ListViewItem[] items = new ListViewItem[CacheRows];
            list.RetrieveVirtualItem += delegate(object sender, RetrieveVirtualItemEventArgs e)
            {
                LogEntry entry = list.Entries[list.FirstEntry + e.ItemIndex];
                int slot = e.ItemIndex % CacheRows;
                if (keys[slot] != entry)
                {
                    ListViewItem item = new ListViewItem(new string[] {
                        entry.Timestamp.HasValue ? entry.Timestamp.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "시간 미인식",
                        entry.Timestamp.HasValue ? entry.Timestamp.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "",
                        entry.Process, entry.Kind, entry.DisplayText, entry.LineNumber.ToString() });
                    item.ToolTipText = entry.FilePath + " : " + entry.LineNumber + Environment.NewLine + entry.Text;
                    items[slot] = item; keys[slot] = entry;
                }
                e.Item = items[slot];
            };
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                Color background = grid.ColumnHeadersDefaultCellStyle.BackColor;
                using (SolidBrush brush = new SolidBrush(background)) { e.Graphics.FillRectangle(brush, e.Bounds); }
                Rectangle bounds = Rectangle.Inflate(e.Bounds, -4, 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, grid.Font, bounds, grid.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            ColumnHeader[] displayOrder = new ColumnHeader[list.Columns.Count];
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e)
            {
                LogEntry entry = list.Entries[list.FirstEntry + e.ItemIndex];
                Color background = grid.DefaultCellStyle.BackColor;
                if (m_chkTaskColors.Checked) { background = ProcessColor(entry.Process); }
                if (e.Item.Selected) { background = Theme.Mix(background, Color.FromArgb(93, 161, 255), 0.25); }
                using (SolidBrush brush = new SolidBrush(background)) { e.Graphics.FillRectangle(brush, e.Bounds); }
                Color foreground = Theme.TextOn(background);
                foreach (ColumnHeader column in list.Columns) { displayOrder[column.DisplayIndex] = column; }
                int left = e.Bounds.Left;
                foreach (ColumnHeader column in displayOrder)
                {
                    Rectangle cell = new Rectangle(left, e.Bounds.Top, column.Width, e.Bounds.Height);
                    left = cell.Right;
                    Rectangle content = Rectangle.Inflate(cell, -4, 0);
                    if (content.Width <= 0 || !cell.IntersectsWith(list.ClientRectangle)) { continue; }
                    if (column.Index == LogTextColumn)
                    { DrawHighlightedText(grid, e.Graphics, entry, content, cell, list.Font, foreground); }
                    else { TextRenderer.DrawText(e.Graphics, e.Item.SubItems[column.Index].Text, list.Font, content, foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | LogTextFlags); }
                }
            };
            list.ChangeTopRow = delegate(int target) { DisplayGridPage(grid, target / GridPageSize + 1, target % GridPageSize); };
            list.SelectedIndexChanged += delegate
            {
                if (m_bUpdatingPage || list.SelectedIndices.Count == 0) { return; }
                if (grid == m_textSelectionGrid && !m_bDraggingText) { ClearTextSelection(); }
                m_chart.SelectEntry(list.Entries[list.FirstEntry + list.SelectedIndices[0]]);
            };
            list.DoubleClick += delegate
            {
                int row = SelectedViewerRow(grid);
                if (row < 0) { return; }
                if (main) { ShowLogDetail(grid, row); } else { SelectMainEntry(list.Entries[row]); }
            };
            list.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                int row = SelectedViewerRow(grid);
                if (!e.Control || e.KeyCode != Keys.C || row < 0) { return; }
                string text = grid == m_textSelectionGrid ? SelectedLogText() : "";
                if (text.Length == 0) { text = list.Entries[row].Text; }
                if (text.Length > 0) { try { Clipboard.SetText(text); } catch (ExternalException ex) { ShowError(ex.Message); } }
                e.Handled = true; e.SuppressKeyPress = true;
            };
            list.KeepTextSelection = delegate { return grid == m_textSelectionGrid && !String.IsNullOrWhiteSpace(SelectedLogText()); };
            int textLeft = 0;
            list.BeginTextDrag = delegate(MouseEventArgs e)
            {
                if (m_chkTaskColors.Checked) { return false; }
                ListViewHitTestInfo hit = list.HitTest(e.Location);
                if (hit.Item == null || hit.SubItem != hit.Item.SubItems[LogTextColumn]) { return false; }
                ClearTextSelection(); list.Focus(); hit.Item.Selected = true; hit.Item.Focused = true;
                m_textSelectionGrid = grid; m_nTextRow = list.FirstEntry + hit.Item.Index; m_selectedTextEntry = list.Entries[m_nTextRow];
                textLeft = hit.SubItem.Bounds.Left + 4;
                using (Graphics graphics = list.CreateGraphics())
                { m_nTextAnchor = LogCharacterIndex(graphics, m_selectedTextEntry.DisplayText, list.Font, e.X - textLeft); }
                m_nTextCaret = m_nTextAnchor; m_bDraggingText = true; list.Capture = true;
                m_chart.SelectEntry(m_selectedTextEntry);
                return true;
            };
            list.MoveTextDrag = delegate(int x)
            {
                if (!list.Capture || !m_bDraggingText || m_textSelectionGrid != grid) { return; }
                int caret;
                using (Graphics graphics = list.CreateGraphics())
                { caret = LogCharacterIndex(graphics, m_selectedTextEntry.DisplayText, list.Font, x - textLeft); }
                if (m_nTextCaret == caret) { return; }
                m_nTextCaret = caret;
                m_menuHighlight.Enabled = !String.IsNullOrWhiteSpace(SelectedLogText());
                int localRow = m_nTextRow - list.FirstEntry;
                list.RedrawItems(localRow, localRow, true);
            };
            list.MouseUp += delegate { if (grid == m_textSelectionGrid && m_bDraggingText) { m_bDraggingText = false; list.Capture = false; } };
            list.MouseCaptureChanged += delegate { if (grid == m_textSelectionGrid && !list.Capture) { m_bDraggingText = false; } };
        }

        // 활성 표에서 현재 선택된 전체 행 번호를 반환한다.
        private static int SelectedViewerRow(LogGrid grid)
        {
            VirtualLogList list = grid.LargeView;
            if (list.VirtualListSize > 0) { return list.SelectedIndices.Count == 0 ? -1 : list.FirstEntry + list.SelectedIndices[0]; }
            return grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex;
        }
        // 한 행을 줄바꿈 없이 전체 확인할 수 있는 상세 창을 연다.
        private void ShowLogDetail(DataGridView grid, int index)
        {
            List<LogEntry> entries = grid == m_gridLogs ? m_logPageRows : m_searchPageRows;
            if (index < 0 || index >= entries.Count) { return; }
            LogEntry entry = entries[index];
            ShowText(Path.GetFileName(entry.FilePath) + " · " + entry.LineNumber + "행", entry.FilePath + Environment.NewLine + Environment.NewLine + entry.Text);
        }

        // 통합 로그의 선택 문자열 색상과 각 뷰어의 원문 복사·상세 보기를 연결한다.
        private void InstallGridMenu(DataGridView grid)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem highlight = new ToolStripMenuItem("선택 문자열 색상…") { Name = "SelectedTextColor", Enabled = false };
            highlight.Click += delegate { ChooseHighlightColor(); };
            menu.Items.Add(highlight); menu.Items.Add(new ToolStripSeparator());
            menu.Opening += delegate { highlight.Enabled = grid == m_textSelectionGrid && !String.IsNullOrWhiteSpace(SelectedLogText()); };
            menu.Items.Add("원문 복사", null, delegate
            {
                List<LogEntry> entries = grid == m_gridLogs ? m_logPageRows : m_searchPageRows;
                int index = SelectedViewerRow((LogGrid)grid);
                if (index < 0 || index >= entries.Count) { return; }
                string text = entries[index].Text;
                if (text.Length == 0) { return; }
                try { Clipboard.SetText(text); } catch (ExternalException ex) { ShowError(ex.Message); }
            });
            menu.Items.Add("상세 보기", null, delegate { ShowLogDetail(grid, SelectedViewerRow((LogGrid)grid)); });
            grid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) { return; }
                // 문자열이 선택되어 있으면 우클릭으로 현재 셀을 옮겨 선택 영역을 지우지 않는다.
                if (grid == m_textSelectionGrid && !String.IsNullOrWhiteSpace(SelectedLogText())) { return; }
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            };
            grid.ContextMenuStrip = menu; ((LogGrid)grid).LargeView.ContextMenuStrip = menu;
        }

        // 선택 프로세스의 원본 색상을 팔레트로 변경한다.
        private void ChooseProcessColor()
        {
            LogSource source = m_treeSources.SelectedNode == null ? null : m_treeSources.SelectedNode.Tag as LogSource;
            if (source == null) { ShowError("색상을 바꿀 프로세스를 선택하세요."); return; }
            using (ColorDialog dialog = new ColorDialog { FullOpen = true, Color = m_settings.ProcessColors[source.Process] })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                m_settings.ProcessColors[source.Process] = dialog.Color;
                ColorSourceNodes(); RefreshLogColors(); SaveSettings();
            }
        }

        // 일반 모드에서는 선택 색상을, 다크 모드에서는 같은 계열의 어두운 색상을 사용한다.
        private Color ProcessColor(string process)
        {
            Color color;
            if (!m_settings.ProcessColors.TryGetValue(process, out color)) { color = Color.FromArgb(220, 232, 250); }
            if (m_settings.DarkMode) { color = Theme.Mix(Color.FromArgb(19, 29, 43), color, 0.30); }
            return color;
        }

        // 트리의 프로세스 범례에도 로그 행과 같은 색상을 표시한다.
        private void ColorSourceNodes()
        {
            foreach (TreeView tree in m_sourceTrees)
            {
                if (tree.Nodes.Count == 0) { continue; }
                foreach (TreeNode node in tree.Nodes[0].Nodes)
                {
                    node.BackColor = tree.BackColor;
                    if (m_chkTaskColors.Checked) { node.BackColor = ProcessColor(((LogSource)node.Tag).Process); }
                    node.ForeColor = Theme.TextOn(node.BackColor);
                    foreach (TreeNode child in node.Nodes) { child.BackColor = tree.BackColor; child.ForeColor = tree.ForeColor; }
                }
            }
        }

        // 모든 뷰어와 설정 창에서 동일한 테마 팔레트를 사용한다.
        private void ApplyTheme()
        {
            Theme.Apply(this, m_settings.DarkMode);
            if (m_logWindow != null) { Theme.Apply(m_logWindow, m_settings.DarkMode); }
            if (m_searchWindow != null) { Theme.Apply(m_searchWindow, m_settings.DarkMode); }
            if (m_timingWindow != null) { Theme.Apply(m_timingWindow, m_settings.DarkMode); }
            m_chart.ApplyTheme(m_settings.DarkMode);
            ColorSourceNodes();
            RefreshLogColors();
            ColorKeywordChips();
        }

        // 차트 위에 키워드별 활성 토글을 표시한다. 변경은 새 목록에 적용한다.
        private void RebuildKeywordChips()
        {
            m_keywordChips.SuspendLayout();
            while (m_keywordChips.Controls.Count > 0) { m_keywordChips.Controls[0].Dispose(); }
            for (int index = 0; index < m_rules.Count; index++)
            {
                int ruleIndex = index;
                KeywordRule rule = m_rules[index];
                CheckBox chip = new ViewerToggle { Text = (index + 1) + ". " + rule.Text, Font = Font, Checked = rule.Enabled, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = false, Width = TextRenderer.MeasureText((index + 1) + ". " + rule.Text, Font).Width + 38, Height = 28, Margin = new Padding(0, 3, 6, 3), Padding = new Padding(8, 2, 8, 2), Tag = rule.Color, UseVisualStyleBackColor = false };
                chip.CheckedChanged += delegate
                {
                    List<KeywordRule> changed = m_rules.Select(delegate(KeywordRule item) { return new KeywordRule { Text = item.Text, Enabled = item.Enabled, CaseSensitive = item.CaseSensitive, Color = item.Color, CheckIntervals = item.CheckIntervals, ExpectedIntervalMs = item.ExpectedIntervalMs, TolerancePercent = item.TolerancePercent, LinkedKeywordNumber = item.LinkedKeywordNumber, LinkedExpectedIntervalMs = item.LinkedExpectedIntervalMs, LinkedTolerancePercent = item.LinkedTolerancePercent }; }).ToList();
                    changed[ruleIndex].Enabled = chip.Checked;
                    m_rules = changed; m_bKeywordDirty = true; m_btnSaveKeywords.Text = "저장 *";
                    ColorKeywordChips(); ScheduleRefresh();
                };
                m_keywordChips.Controls.Add(chip);
            }
            ColorKeywordChips(); m_keywordChips.ResumeLayout(true);
        }

        // 키워드 토글의 색상을 테마와 활성 상태에 맞춘다.
        private void ColorKeywordChips()
        {
            foreach (Control control in m_keywordChips.Controls)
            {
                CheckBox chip = (CheckBox)control;
                Color color = (Color)chip.Tag;
                chip.FlatAppearance.BorderColor = color;
                chip.BackColor = m_settings.DarkMode ? Color.FromArgb(26, 35, 48) : Color.White;
                if (chip.Checked) { chip.BackColor = Theme.Mix(chip.BackColor, color, 0.08); }
                chip.ForeColor = Theme.TextOn(chip.BackColor);
                chip.Invalidate();
            }
        }

        // 검증된 새 파서 설정만 적용하고 현재 폴더를 다시 읽는다.
        private async void EditSettings()
        {
            using (SettingsForm dialog = new SettingsForm(m_settings.Options, m_settings.DarkMode))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                m_settings.Options = dialog.Options;
                SaveSettings();
                if (Directory.Exists(m_txtFolder.Text.Trim().Trim('"'))) { await LoadFolder(); }
            }
        }

        // 편집 창에서 확인된 목록만 적용하고 저장 필요 상태를 표시한다.
        private void EditKeywords()
        {
            using (KeywordForm dialog = new KeywordForm(m_rules, m_settings.DarkMode))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                m_rules = dialog.Rules;
                RebuildKeywordChips();
                m_bKeywordDirty = true;
                m_btnSaveKeywords.Text = "저장 *";
                ScheduleRefresh();
            }
        }

        // 최초 실행에서 기존 INI를 기본 프리셋으로 복사하고 마지막 선택을 복원한다.
        private void InitializePresets()
        {
            Directory.CreateDirectory(m_strPresetFolder);
            string[] paths = Directory.GetFiles(m_strPresetFolder, "*.ini");
            if (paths.Length == 0)
            {
                List<KeywordRule> initial = new List<KeywordRule>();
                List<KeyValuePair<string, Color>> initialHighlights = new List<KeyValuePair<string, Color>>();
                if (File.Exists(m_strKeywordPath)) { initial = KeywordSettings.Load(m_strKeywordPath, initialHighlights); }
                string initialPath = KeywordSettings.PresetPath(m_strPresetFolder, "기본");
                KeywordSettings.Save(initialPath, initial, initialHighlights);
                paths = new string[] { initialPath };
            }
            Array.Sort(paths, StringComparer.CurrentCultureIgnoreCase);
            m_strKeywordPath = paths[0];
            foreach (string path in paths)
            {
                if (String.Equals(Path.GetFileNameWithoutExtension(path), m_settings.SelectedPreset, StringComparison.OrdinalIgnoreCase)) { m_strKeywordPath = path; break; }
            }
            RefreshPresetList();
            m_rules = KeywordSettings.Load(m_strKeywordPath, m_highlights);
            m_menuClearHighlights.Enabled = !m_chkTaskColors.Checked && m_highlights.Count > 0;
            m_settings.SelectedPreset = Path.GetFileNameWithoutExtension(m_strKeywordPath);
        }

        // 관리 폴더의 실제 INI 목록과 현재 선택을 콤보에 반영한다.
        private void RefreshPresetList()
        {
            m_bUpdatingPresets = true;
            try
            {
                string[] names = Directory.GetFiles(m_strPresetFolder, "*.ini").Select(Path.GetFileNameWithoutExtension).OrderBy(delegate(string name) { return name; }, StringComparer.CurrentCultureIgnoreCase).ToArray();
                m_cmbPreset.Items.Clear(); m_cmbPreset.Items.AddRange(names);
                m_cmbPreset.SelectedItem = Path.GetFileNameWithoutExtension(m_strKeywordPath);
            }
            finally { m_bUpdatingPresets = false; }
        }

        // 검증된 목록을 프리셋 선택과 차트에 함께 반영한다.
        private void ActivatePreset(string path, List<KeywordRule> rules, List<KeyValuePair<string, Color>> highlights)
        {
            m_strKeywordPath = path; m_rules = rules; m_bKeywordDirty = false;
            m_highlights.Clear(); m_highlights.AddRange(highlights);
            ClearTextSelection(); RefreshLogColors(); m_menuClearHighlights.Enabled = !m_chkTaskColors.Checked && m_highlights.Count > 0;
            m_settings.SelectedPreset = Path.GetFileNameWithoutExtension(path);
            m_btnSaveKeywords.Text = "저장"; m_toolTip.SetToolTip(m_btnSaveKeywords, path);
            RefreshPresetList(); RebuildKeywordChips(); SaveSettings(); ScheduleRefresh();
        }

        // 전환 취소나 잘못된 INI는 기존 프리셋과 편집 중인 내용을 그대로 유지한다.
        private void SwitchPreset()
        {
            if (m_bUpdatingPresets || m_cmbPreset.SelectedItem == null) { return; }
            string name = (string)m_cmbPreset.SelectedItem;
            if (String.Equals(name, Path.GetFileNameWithoutExtension(m_strKeywordPath), StringComparison.OrdinalIgnoreCase)) { return; }
            try
            {
                string path = KeywordSettings.PresetPath(m_strPresetFolder, name);
                List<KeyValuePair<string, Color>> highlights = new List<KeyValuePair<string, Color>>();
                List<KeywordRule> loaded = KeywordSettings.Load(path, highlights);
                if (!ConfirmKeywordChanges()) { RefreshPresetList(); return; }
                ActivatePreset(path, loaded, highlights);
            }
            catch (Exception ex) { RefreshPresetList(); ShowError("프리셋 읽기 실패: " + ex.Message); }
        }

        // 관리 동작은 한 메뉴에 모으고 저장 버튼은 선택한 프리셋에만 적용한다.
        private void InstallPresetMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("새 프리셋", null, delegate { ManagePreset("new"); });
            menu.Items.Add("현재 설정 복제", null, delegate { ManagePreset("copy"); });
            menu.Items.Add("이름 변경", null, delegate { ManagePreset("rename"); });
            menu.Items.Add("삭제", null, delegate { ManagePreset("delete"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("INI 가져오기", null, delegate { ManagePreset("import"); });
            menu.Items.Add("INI 내보내기", null, delegate { SaveKeywords(true); });
            m_btnPresets.ContextMenuStrip = menu;
            m_btnPresets.Click += delegate { menu.Show(m_btnPresets, new Point(0, m_btnPresets.Height)); };
        }

        // 프리셋 이름 입력창은 기존 테마와 표준 대화상자 동작을 사용한다.
        private string AskPresetName(string title, string initial)
        {
            using (Form dialog = new Form { Text = title, ClientSize = new Size(400, 106), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font })
            {
                TextBox name = new TextBox { Text = initial, Left = 16, Top = 16, Width = 368, AccessibleName = "프리셋 이름", MaxLength = 80 };
                Button ok = MakeButton("확인", 80); ok.Location = new Point(208, 58); ok.DialogResult = DialogResult.OK;
                Button cancel = MakeButton("취소", 80); cancel.Location = new Point(304, 58); cancel.DialogResult = DialogResult.Cancel;
                dialog.Controls.AddRange(new Control[] { name, ok, cancel }); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                Theme.Apply(dialog, m_settings.DarkMode);
                if (dialog.ShowDialog(this) != DialogResult.OK) { return null; }
                return name.Text.Trim();
            }
        }

        // 새 이름의 파일이 없을 때만 저장하여 기존 프리셋을 덮어쓰지 않는다.
        private void CreatePreset(string name, List<KeywordRule> rules, List<KeyValuePair<string, Color>> highlights)
        {
            string path = KeywordSettings.PresetPath(m_strPresetFolder, name);
            if (File.Exists(path)) { throw new IOException("같은 이름의 프리셋이 있습니다."); }
            KeywordSettings.Save(path, rules, highlights);
            List<KeyValuePair<string, Color>> savedHighlights = new List<KeyValuePair<string, Color>>();
            List<KeywordRule> savedRules = KeywordSettings.Load(path, savedHighlights);
            ActivatePreset(path, savedRules, savedHighlights);
        }

        // 이름 변경 성공 후에만 현재 경로와 저장된 선택을 갱신한다.
        private void RenamePreset(string name)
        {
            string path = KeywordSettings.PresetPath(m_strPresetFolder, name);
            if (String.Equals(path, m_strKeywordPath, StringComparison.OrdinalIgnoreCase)) { return; }
            if (File.Exists(path)) { throw new IOException("같은 이름의 프리셋이 있습니다."); }
            List<KeyValuePair<string, Color>> highlights = new List<KeyValuePair<string, Color>>();
            List<KeywordRule> saved = KeywordSettings.Load(m_strKeywordPath, highlights);
            File.Move(m_strKeywordPath, path);
            ActivatePreset(path, saved, highlights);
        }

        // 마지막 프리셋은 남기고 대체 프리셋 검증 후 선택 파일만 삭제한다.
        private void DeletePreset()
        {
            string replacement = Directory.GetFiles(m_strPresetFolder, "*.ini").OrderBy(delegate(string path) { return path; }, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(delegate(string path) { return !String.Equals(path, m_strKeywordPath, StringComparison.OrdinalIgnoreCase); });
            if (replacement == null) { throw new InvalidOperationException("프리셋은 최소 한 개가 필요합니다."); }
            List<KeyValuePair<string, Color>> highlights = new List<KeyValuePair<string, Color>>();
            List<KeywordRule> loaded = KeywordSettings.Load(replacement, highlights);
            File.Delete(m_strKeywordPath);
            ActivatePreset(replacement, loaded, highlights);
        }

        // 사용자 선택을 받은 관리 동작만 실행하며 실패한 경우 기존 상태를 보존한다.
        private void ManagePreset(string action)
        {
            try
            {
                string currentName = Path.GetFileNameWithoutExtension(m_strKeywordPath);
                string name;
                switch (action)
                {
                    case "new":
                        if (!ConfirmKeywordChanges()) { return; }
                        name = AskPresetName("새 프리셋", "");
                        if (name != null) { CreatePreset(name, new List<KeywordRule>(), new List<KeyValuePair<string, Color>>()); }
                        break;
                    case "copy":
                        name = AskPresetName("현재 설정 복제", currentName + " 복사");
                        if (name != null) { CreatePreset(name, m_rules, m_highlights); }
                        break;
                    case "rename":
                        if (!ConfirmKeywordChanges()) { return; }
                        name = AskPresetName("프리셋 이름 변경", currentName);
                        if (name != null) { RenamePreset(name); }
                        break;
                    case "delete":
                        if (MessageBox.Show(this, "'" + currentName + "' 프리셋을 삭제할까요?", "프리셋 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { DeletePreset(); }
                        break;
                    case "import":
                        using (OpenFileDialog dialog = new OpenFileDialog { Filter = "키워드 INI (*.ini)|*.ini" })
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                            List<KeyValuePair<string, Color>> highlights = new List<KeyValuePair<string, Color>>();
                            List<KeywordRule> loaded = KeywordSettings.Load(dialog.FileName, highlights);
                            if (!ConfirmKeywordChanges()) { return; }
                            name = AskPresetName("INI 가져오기", Path.GetFileNameWithoutExtension(dialog.FileName));
                            if (name != null) { CreatePreset(name, loaded, highlights); }
                        }
                        break;
                }
            }
            catch (Exception ex) { ShowError("프리셋 변경 실패: " + ex.Message); }
        }

        // 선택 프리셋에 저장하거나 편집 상태를 유지한 채 외부 INI로 내보낸다.
        private bool SaveKeywords(bool export)
        {
            string path = m_strKeywordPath;
            if (export)
            {
                using (SaveFileDialog dialog = new SaveFileDialog { Filter = "키워드 INI (*.ini)|*.ini", DefaultExt = "ini", FileName = Path.GetFileName(path) })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { return false; }
                    path = dialog.FileName;
                }
            }
            try
            {
                KeywordSettings.Save(path, m_rules, m_highlights);
                if (String.Equals(Path.GetFullPath(path), Path.GetFullPath(m_strKeywordPath), StringComparison.OrdinalIgnoreCase))
                {
                    m_bKeywordDirty = false; m_btnSaveKeywords.Text = "저장";
                }
                m_toolTip.SetToolTip(m_btnSaveKeywords, m_strKeywordPath); m_lblStatus.Text = "키워드 저장 · " + path;
                return true;
            }
            catch (Exception ex) { ShowError("키워드 저장 실패: " + ex.Message); return false; }
        }

        // 저장하지 않은 키워드와 문자열 강조를 버리는 동작 전에 사용자 선택을 확인한다.
        private bool ConfirmKeywordChanges()
        {
            if (!m_bKeywordDirty) { return true; }
            DialogResult result = MessageBox.Show(this, "변경한 키워드와 문자열 강조를 프리셋에 저장할까요?", "프리셋 저장", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) { return false; }
            if (result == DialogResult.Yes) { return SaveKeywords(false); }
            return true;
        }

        // 화면/파서/프로세스 색상 변경을 저장하고 실패 사실을 숨기지 않는다.
        private void SaveSettings()
        {
            try { m_settings.Save(m_strSettingsPath); }
            catch (Exception ex) { m_lblStatus.Text = "설정 저장 실패 · " + ex.Message; }
        }

        // 긴 원문이나 경고를 복사 가능한 읽기 전용 창으로 보여준다.
        private void ShowText(string title, string content)
        {
            using (Form dialog = new Form { Text = title, Size = new Size(1000, 500), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false })
            {
                TextBox text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = content, Font = new Font("Consolas", 10F) };
                dialog.Controls.Add(text); Theme.Apply(dialog, m_settings.DarkMode); dialog.ShowDialog(this);
            }
        }

        // 작업 실패를 원인과 함께 표시한다.
        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "LogAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // 닫기 전에 미저장 키워드를 확인하고 백그라운드 작업을 취소한다.
        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            if (!ConfirmKeywordChanges()) { e.Cancel = true; return; }
            m_bClosing = true;
            m_filterTimer.Stop();
            if (m_loadCancellation != null) { m_loadCancellation.Cancel(); }
            if (m_viewCancellation != null) { m_viewCancellation.Cancel(); }
            SaveSettings();
            if (m_exportCancellation != null) { m_exportCancellation.Cancel(); }
        }

        // 폼에 속한 타이머와 툴팁을 해제한다.
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Application.RemoveMessageFilter(this); m_filterTimer.Dispose(); m_toolTip.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class ViewerButton : Button
    {
        public string IconCode { get; set; }

        // 표준 버튼의 키보드/클릭 동작을 유지하고 배경만 사용자 지정으로 그린다.
        public ViewerButton()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        // 선택한 테마의 버튼 표면과 키보드 포커스를 표시한다.
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.DrawAction(this, e.Graphics, false, IconCode, FlatAppearance.BorderColor, Focused && ShowFocusCues);
        }
    }

    // "제목 · 개수 · 상태" 텍스트를 제목과 둥근 배지로 나눠 그린다. Text 값은 그대로 둔다.
    internal sealed class HeadingLabel : Label
    {
        private const string Separator = " · ";

        public HeadingLabel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            string[] parts = Text.Split(new string[] { Separator }, StringSplitOptions.None);
            if (parts.Length < 2) { base.OnPaint(e); return; }
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int x = Padding.Left;
            int titleWidth = TextRenderer.MeasureText(graphics, parts[0], Font, Size.Empty, flags).Width;
            TextRenderer.DrawText(graphics, parts[0], Font, new Rectangle(x, 0, Math.Max(0, Width - x), Height), ForeColor, flags | TextFormatFlags.EndEllipsis);
            x += titleWidth + 8;
            bool dark = BackColor.GetBrightness() < 0.3F;
            float scale = graphics.DpiX / 96F;
            using (Font badgeFont = new Font(Font.FontFamily, 9F, FontStyle.Regular))
            {
                for (int index = 1; index < parts.Length; index++)
                {
                    bool warn = parts[index].Contains("이상") || parts[index].Contains("오류");
                    Color fill = warn ? (dark ? Color.FromArgb(58, 46, 26) : Color.FromArgb(255, 241, 219)) : Theme.Mix(BackColor, ForeColor, 0.08);
                    Color text = warn ? (dark ? Color.FromArgb(240, 179, 90) : Color.FromArgb(154, 91, 0)) : Theme.Mix(BackColor, ForeColor, 0.7);
                    Size size = TextRenderer.MeasureText(graphics, parts[index], badgeFont, Size.Empty, flags);
                    int width = size.Width + (int)(16 * scale);
                    int height = (int)(22 * scale);
                    if (x + width > Width - 4) { break; }
                    Rectangle pill = new Rectangle(x, (Height - height) / 2, width, height);
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddArc(pill.Left, pill.Top, height, height, 90, 180);
                        path.AddArc(pill.Right - height, pill.Top, height, height, 270, 180);
                        path.CloseFigure();
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        using (SolidBrush brush = new SolidBrush(fill)) { graphics.FillPath(brush, path); }
                    }
                    TextRenderer.DrawText(graphics, parts[index], badgeFont, pill, text, flags | TextFormatFlags.HorizontalCenter);
                    x += width + 6;
                }
            }
        }
    }

    internal sealed class ViewerToggle : CheckBox
    {
        public string IconCode { get; set; }

        // 표준 체크 상태와 접근성 동작을 유지하는 버튼형 토글을 구성한다.
        public ViewerToggle()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        // 프로세스 구분, 테마, 키워드의 활성 상태를 같은 버튼 규칙으로 표시한다.
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.DrawAction(this, e.Graphics, Checked, IconCode, FlatAppearance.BorderColor, Focused && ShowFocusCues);
        }
    }

    internal static class Theme
    {
        private const int ActionCornerRadius = 4;
        private const int ActionHorizontalPadding = 8;
        private const int ActionIconWidth = 20;

        // 표준 Windows 아이콘, 테마 색상, 활성/비활성/포커스를 버튼 표면에 그린다.
        public static void DrawAction(Control control, Graphics graphics, bool selected, string iconCode, Color border, bool focus)
        {
            if (control.Width < 2 || control.Height < 2) { return; }
            bool dark = control.BackColor.GetBrightness() < 0.3F;
            Color fill = control.BackColor;
            Color text = control.ForeColor;
            Color accent = Color.FromArgb(24, 97, 218);
            bool swatch = control.Tag is Color;
            if (selected && !swatch) { fill = accent; text = Color.White; border = accent; }
            if (!control.Enabled)
            {
                text = dark ? Color.FromArgb(144, 157, 175) : Color.FromArgb(137, 146, 160);
                fill = dark ? Color.FromArgb(29, 39, 53) : Color.FromArgb(246, 248, 251);
            }
            else if (control.ClientRectangle.Contains(control.PointToClient(Control.MousePosition)))
            {
                double ratio = Control.MouseButtons == MouseButtons.Left ? 0.16 : 0.08;
                fill = Mix(fill, dark ? Color.White : Color.FromArgb(25, 75, 145), ratio);
            }
            graphics.Clear(control.Parent == null ? fill : control.Parent.BackColor);
            float diameter = ActionCornerRadius * 2 * graphics.DpiX / 96F;
            RectangleF bounds = new RectangleF(0.5F, 0.5F, control.Width - 1, control.Height - 1);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush brush = new SolidBrush(fill)) { graphics.FillPath(brush, path); }
                using (Pen pen = new Pen(border)) { graphics.DrawPath(pen, path); }
            }
            int padding = (int)(ActionHorizontalPadding * graphics.DpiX / 96F);
            int iconWidth = (int)(ActionIconWidth * graphics.DpiX / 96F);
            Rectangle label = new Rectangle(padding, 0, Math.Max(0, control.Width - padding * 2), control.Height);
            if (swatch || !String.IsNullOrEmpty(iconCode))
            {
                int contentWidth = Math.Min(label.Width, TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + iconWidth);
                label.X = Math.Max(padding, (control.Width - contentWidth) / 2);
                Rectangle icon = new Rectangle(label.X, 0, iconWidth, control.Height);
                if (swatch)
                {
                    Color color = (Color)control.Tag;
                    if (!selected) { color = Mix(fill, color, 0.25); }
                    using (SolidBrush brush = new SolidBrush(color)) { graphics.FillRectangle(brush, icon.X + 2, (control.Height - 10) / 2, 10, 10); }
                }
                else
                {
                    using (Font font = new Font("Segoe MDL2 Assets", 11F)) { TextRenderer.DrawText(graphics, iconCode, font, icon, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding); }
                }
                label.X += iconWidth; label.Width = Math.Max(0, contentWidth - iconWidth);
            }
            TextRenderer.DrawText(graphics, control.Text, control.Font, label, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (focus) { ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(control.ClientRectangle, -5, -5), text, fill); }
        }

        // 드롭다운 항목에도 일반/다크 팔레트와 선택 대비를 적용한다.
        private static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            ComboBox combo = (ComboBox)sender;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color background = combo.BackColor;
            Color foreground = combo.ForeColor;
            if (selected) { background = Color.FromArgb(24, 97, 218); foreground = Color.White; }
            using (SolidBrush brush = new SolidBrush(background)) { e.Graphics.FillRectangle(brush, e.Bounds); }
            if (e.Index >= 0)
            {
                Rectangle bounds = Rectangle.Inflate(e.Bounds, -5, 0);
                TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds, foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            e.DrawFocusRectangle();
        }
        // 두 색을 섞어 테마별 범례와 선택 색상을 만든다.
        public static Color Mix(Color first, Color second, double ratio)
        {
            return Color.FromArgb((int)(first.R * (1 - ratio) + second.R * ratio), (int)(first.G * (1 - ratio) + second.G * ratio), (int)(first.B * (1 - ratio) + second.B * ratio));
        }

        // 사용자 지정 배경 위에서 글자 대비를 유지한다.
        public static Color TextOn(Color background)
        {
            double luminance = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
            return luminance > 150 ? Color.FromArgb(22, 35, 52) : Color.FromArgb(241, 245, 250);
        }

        // 모든 표준 WinForms 컨트롤에 일반/다크 팔레트를 일관되게 적용한다.
        public static void Apply(Control control, bool dark)
        {
            Color background = dark ? Color.FromArgb(18, 25, 36) : Color.White;
            Color surface = dark ? Color.FromArgb(26, 35, 48) : Color.White;
            Color foreground = dark ? Color.FromArgb(226, 233, 243) : Color.FromArgb(24, 31, 42);
            Color border = dark ? Color.FromArgb(55, 69, 88) : Color.FromArgb(218, 224, 232);
            control.BackColor = background; control.ForeColor = foreground;
            if (control is TextBoxBase || control is TreeView || control is ListBox || control is ComboBox) { control.BackColor = surface; }
            if (control is SplitContainer) { control.BackColor = border; }
            if (control.Tag as string == "heading") { control.BackColor = dark ? Color.FromArgb(31, 42, 58) : Color.FromArgb(245, 247, 250); }
            if (control.Tag as string == "sidebar") { control.BackColor = dark ? Color.FromArgb(21, 30, 43) : Color.FromArgb(243, 246, 250); }
            if (control is Label && control.Tag == null && control.Parent != null) { control.BackColor = control.Parent.BackColor; }
            Button button = control as Button;
            if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 1; button.FlatAppearance.BorderColor = border;
                button.BackColor = surface;
                if (button.Tag as string == "accent") { button.BackColor = Color.FromArgb(24, 97, 218); button.ForeColor = Color.White; button.FlatAppearance.BorderColor = button.BackColor; }
            }
            ViewerToggle toggle = control as ViewerToggle;
            if (toggle != null) { toggle.BackColor = surface; toggle.FlatAppearance.BorderColor = border; }
            ComboBox combo = control as ComboBox;
            if (combo != null) { combo.FlatStyle = FlatStyle.Flat; combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 23; combo.DrawItem -= DrawComboItem; combo.DrawItem += DrawComboItem; }
            DataGridView grid = control as DataGridView;
            if (grid != null)
            {
                grid.EnableHeadersVisualStyles = false; grid.BackgroundColor = surface; grid.GridColor = dark ? border : Color.FromArgb(235, 239, 244);
                grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? Color.FromArgb(35, 47, 63) : Color.FromArgb(240, 243, 247);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
                grid.DefaultCellStyle.BackColor = surface; grid.DefaultCellStyle.ForeColor = foreground;
                grid.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(43, 68, 101) : Color.FromArgb(219, 235, 255);
                grid.DefaultCellStyle.SelectionForeColor = foreground;
                return;
            }
            foreach (Control child in control.Controls) { Apply(child, dark); }
        }
    }
}
