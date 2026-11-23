using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LogAnalyzer;

internal static class UiTests
{
    // 실제 WinForms 컨트롤과 메시지 루프에서 사용자 흐름을 검증한다.
    public static int Run(string sampleFolder, bool large = false, int expectedRows = 1000000, bool stress = false)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        int result = 1;
        EventHandler started = null;
        started = delegate
        {
            Application.Idle -= started;
            if (large) { result = ExecuteLargeTests(sampleFolder, expectedRows, stress); }
            else { result = ExecuteTests(sampleFolder); }
            Application.ExitThread();
        };
        Application.Idle += started;
        Application.Run(new ApplicationContext());
        return result;
    }

    // 지정 행 수의 합성 로그를 실제 폼에 로드하고 메모리·응답 지연과 기능별 결과를 기록한다.
    private static int ExecuteLargeTests(string folder, int expectedRows, bool stress)
    {
        string baseFolder = AppDomain.CurrentDomain.BaseDirectory;
        string presetFolder = Path.Combine(baseFolder, "timing-presets");
        Directory.CreateDirectory(presetFolder);
        new AppSettings { SelectedPreset = "대용량 검증" }.Save(Path.Combine(baseFolder, "settings.ini"));
        List<KeywordRule> rules = new List<KeywordRule>();
        for (int index = 0; index < 3; index++) { rules.Add(new KeywordRule { Text = "KW0" + index + "|", Color = Color.SteelBlue }); }
        rules.Add(new KeywordRule { Text = "KW00|", LinkedKeywordNumber = 2, LinkedExpectedIntervalMs = 1000, LinkedTolerancePercent = 20, CheckIntervals = false, Color = Color.Purple });
        KeywordSettings.Save(Path.Combine(presetFolder, "대용량 검증.ini"), rules);
        string artifacts = Path.GetFullPath(Path.Combine(baseFolder, "..", "..", "..", "artifacts"));
        string outputFolder = Path.Combine(artifacts, "performance-gib", Path.GetFileName(folder));
        Directory.CreateDirectory(outputFolder);
        string measurements = Path.Combine(outputFolder, "measurements.csv");
        File.WriteAllText(measurements, "phase,elapsed_ms,working_set,private_bytes,peak_working_set,managed_bytes,max_heartbeat_gap_ms\r\n");
        long inputBytes = 0;
        string[] inputFiles = Directory.GetFiles(folder, "*.log", SearchOption.AllDirectories);
        foreach (string file in inputFiles) { inputBytes += new FileInfo(file).Length; }
        Console.WriteLine("BEGIN rows=" + expectedRows + " files=" + inputFiles.Length + " bytes=" + inputBytes + " x64=" + Environment.Is64BitProcess);
        try
        {
            using (MainForm form = new MainForm(null))
            using (System.Windows.Forms.Timer heartbeat = new System.Windows.Forms.Timer { Interval = 20 })
            {
                Stopwatch clock = Stopwatch.StartNew(); long last = 0; long maxGap = 0;
                heartbeat.Tick += delegate { long now = clock.ElapsedMilliseconds; maxGap = Math.Max(maxGap, now - last); last = now; };
                Action<string, long> record = delegate(string name, long elapsed)
                {
                    Process process = Process.GetCurrentProcess(); process.Refresh();
                    string line = name + "," + elapsed + "," + process.WorkingSet64 + "," + process.PrivateMemorySize64 + "," + process.PeakWorkingSet64 + "," + GC.GetTotalMemory(false) + "," + maxGap;
                    File.AppendAllText(measurements, line + "\r\n"); Console.WriteLine("PASS " + line);
                    maxGap = 0; last = clock.ElapsedMilliseconds;
                };
                heartbeat.Start(); form.Show();
                TestWindowPlacement(Get<TextBox>(form, "m_txtSearch"));
                Task initial = (Task)Call(form, "RefreshView"); Wait(delegate { return initial.IsCompleted; }, "large startup");
                Get<TextBox>(form, "m_txtFolder").Text = folder;
                ProgressBar loadProgress = Get<ProgressBar>(form, "m_loadProgress");
                bool sawFilePercent = false;
                bool sawPreparing = false;
                Get<Label>(form, "m_lblLoadProgress").TextChanged += delegate
                {
                    if (!loadProgress.Visible) { return; }
                    if (loadProgress.Style == ProgressBarStyle.Continuous && loadProgress.Value > 0 && loadProgress.Value < 100) { sawFilePercent = true; }
                    if (loadProgress.Style == ProgressBarStyle.Marquee && Get<Label>(form, "m_lblLoadProgress").Text.Contains("차트")) { sawPreparing = true; }
                };
                Stopwatch phase = Stopwatch.StartNew(); Get<Button>(form, "m_btnReload").PerformClick();
                Assert(loadProgress.Visible, "sidebar shows progress as soon as loading starts");
                Wait(delegate { return !Get<bool>(form, "m_bLoading") && Get<CancellationTokenSource>(form, "m_viewCancellation") == null; }, "large UI load", timeoutSeconds: 300);
                Assert(sawFilePercent && sawPreparing && !loadProgress.Visible, "sidebar shows file progress and chart preparation, then collapses");
                long loadMs = phase.ElapsedMilliseconds;
                Assert(Get<List<LogEntry>>(form, "m_visibleEntries").Count == expectedRows && Get<MainForm.LogGrid>(form, "m_gridLogs").LargeView.VirtualListSize == MainForm.GridPageSize + MainForm.PageOverlapRows, "all rows displayed in actual form");
                Assert(Get<List<string>>(form, "m_warnings").Count == 0 && Get<int>(form, "m_nFileCount") == inputFiles.Length, "all input files read without warnings");
                List<LogEntry> loadedRows = Get<List<LogEntry>>(form, "m_entries");
                for (int index = 1; index < loadedRows.Count; index++)
                { Assert(loadedRows[index - 1].Timestamp <= loadedRows[index].Timestamp, "time ordering"); }
                Assert((loadedRows[loadedRows.Count - 1].Timestamp.Value - loadedRows[0].Timestamp.Value).TotalMilliseconds == expectedRows - 1, "timestamp endpoints");
                record("load_and_chart", loadMs);
                MainForm.VirtualLogList mainList = Get<MainForm.LogGrid>(form, "m_gridLogs").LargeView;
                TimingChart clickChart = Get<TimingChart>(form, "m_chart");
                mainList.Focus();
                Get<CheckBox>(form, "m_chkDark").Checked = true;
                TestHoverPaint(mainList, 1);
                Get<CheckBox>(form, "m_chkDark").Checked = false;
                Get<CheckBox>(form, "m_chkTaskColors").Checked = true;
                TestHoverPaint(mainList, 2);
                Get<CheckBox>(form, "m_chkTaskColors").Checked = false;
                NumericUpDown logPage = Get<NumericUpDown>(form, "m_logPage");
                logPage.Value = Math.Max(1, (int)logPage.Maximum / 2);
                mainList.TopItem = mainList.Items[MainForm.GridPageSize - 10];
                Application.DoEvents();
                TestWheelFrames(mainList);
                int beforeScroll = mainList.TopItem.Index;
                SendMessage(mainList.Handle, 0x0115, new IntPtr(3), IntPtr.Zero); // SB_PAGEDOWN
                Application.DoEvents();
                Assert(mainList.TopItem.Index > beforeScroll, "native scrollbar page scroll");
                TestThumbDrag(mainList);
                mainList.TopItem = mainList.Items[10];
                TestWheelFrames(mainList, true);
                Call(form, "OpenViewerWindow", Get<DataGridView>(form, "m_gridLogs").Parent.Parent);
                Application.DoEvents();
                TestWindowPlacement(mainList);
                TestWheelFrames(mainList);
                Get<Form>(form, "m_logWindow").Close();
                Application.DoEvents();
                logPage.Value = logPage.Maximum;
                mainList.TopItem = mainList.Items[Math.Max(0, mainList.VirtualListSize - 12)];
                Application.DoEvents();
                ListViewItem item = mainList.Items[mainList.TopItem.Index + 1];
                Rectangle rect = item.SubItems[4].Bounds;
                IntPtr anchor = new IntPtr(((rect.Top + rect.Height / 2) << 16) | (rect.Left + 8));
                IntPtr caret = new IntPtr(anchor.ToInt64() + 70);
                SendMessage(mainList.Handle, MouseLeftDown, new IntPtr(1), anchor);
                mainList.Update();
                bool customWhilePressed = mainList.OwnerDraw;
                using (Bitmap pressed = new Bitmap(mainList.Width, mainList.Height))
                using (Bitmap released = new Bitmap(mainList.Width, mainList.Height))
                {
                    mainList.DrawToBitmap(pressed, mainList.ClientRectangle);
                    SendMessage(mainList.Handle, MouseLeftUp, IntPtr.Zero, anchor);
                    mainList.Update();
                    mainList.DrawToBitmap(released, mainList.ClientRectangle);
                    Assert(customWhilePressed == mainList.OwnerDraw, "plain log click keeps the text renderer while pressed and released");
                    Rectangle textArea = Rectangle.Intersect(rect, mainList.ClientRectangle);
                    Assert(textArea.Width > 0 && textArea.Height > 0, "clicked log text is visible");
                    for (int y = textArea.Top; y < textArea.Bottom; y++)
                    {
                        for (int x = textArea.Left; x < textArea.Right; x++)
                        { Assert(pressed.GetPixel(x, y) == released.GetPixel(x, y), "clicked text pixels stay unchanged on release"); }
                    }
                }
                SendMessage(mainList.Handle, MouseLeftDown, new IntPtr(1), anchor);
                SendMessage(mainList.Handle, MouseMove, new IntPtr(1), caret);
                SendMessage(mainList.Handle, MouseLeftUp, IntPtr.Zero, caret);
                Assert(((string)Call(form, "SelectedLogText")).Length > 0, "native drag selects text");
                Assert(Object.ReferenceEquals(Get<LogEntry>(clickChart, "m_selectedEntry"), loadedRows[mainList.FirstEntry + item.Index]), "native selection and chart agree");
                string selectedText = (string)Call(form, "SelectedLogText");
                SendMessage(mainList.Handle, MouseRightDown, new IntPtr(2), caret);
                SendMessage(mainList.Handle, MouseRightUp, IntPtr.Zero, caret);
                Application.DoEvents();
                ContextMenuStrip logMenu = mainList.ContextMenuStrip;
                Assert(logMenu.Visible && logMenu.SourceControl == mainList, "right click opens integrated log menu");
                ToolStripItem colorAction = logMenu.Items["SelectedTextColor"];
                Assert(colorAction.Enabled && (string)Call(form, "SelectedLogText") == selectedText, "right click keeps selected substring for palette");
                logMenu.Close();
                AcceptHighlightColor(form, colorAction);
                Assert(Get<List<KeyValuePair<string, Color>>>(form, "m_highlights").Exists(
                    delegate(KeyValuePair<string, Color> rule) { return rule.Key == selectedText && rule.Value.ToArgb() == Color.Yellow.ToArgb(); }),
                    "palette applies selected substring color");
                Call(form, "SaveKeywords", false);
                TreeView sources = Get<TreeView>(form, "m_treeSources");
                if (sources.Nodes[0].Nodes.Count > 1)
                {
                    Font integratedFont = mainList.Font;
                    int rowHeight = mainList.GetItemRect(0).Height;
                    sources.SelectedNode = sources.Nodes[0].Nodes[0];
                    Wait(delegate { return Get<List<LogEntry>>(form, "m_visibleEntries").Count < expectedRows; }, "switch to single process");
                    Assert(mainList.Visible && mainList.Font.Equals(integratedFont) && mainList.GetItemRect(0).Height == rowHeight, "process selection keeps integrated text font and row height");
                    sources.SelectedNode = sources.Nodes[0];
                    Wait(delegate { return Get<List<LogEntry>>(form, "m_visibleEntries").Count == expectedRows; }, "return to all processes");
                }
                Get<TextBox>(form, "m_txtSearch").Text = "ID=0000001";
                Task one = (Task)Call(form, "RefreshView"); Wait(delegate { return one.IsCompleted; }, "single search match");
                MainForm.LogGrid searchGrid = Get<MainForm.LogGrid>(form, "m_gridSearch");
                Assert(searchGrid.LargeView.Visible && searchGrid.LargeView.VirtualListSize == 1 &&
                    searchGrid.LargeView.Font.Equals(mainList.Font) && searchGrid.LargeView.GetItemRect(0).Height == mainList.GetItemRect(0).Height, "sparse results retain integrated typography");
                Call(form, "OpenViewerWindow", searchGrid.Parent.Parent);
                Application.DoEvents();
                TestWindowPlacement(searchGrid.LargeView);
                searchGrid.LargeView.Columns[4].DisplayIndex = 0;
                TestHoverPaint(searchGrid.LargeView, 0);
                TestSearchDrag(form, searchGrid.LargeView, searchGrid.LargeView.Items[0].SubItems[4].Bounds);
                Get<Form>(form, "m_searchWindow").Close();
                Call(form, "SaveKeywords", false);
                Get<TextBox>(form, "m_txtSearch").Text = "검사 완료";
                phase.Restart(); Task search = (Task)Call(form, "RefreshView"); Wait(delegate { return search.IsCompleted; }, "large UI search", timeoutSeconds: 300);
                long searchMs = phase.ElapsedMilliseconds;
                Assert(Get<List<LogEntry>>(form, "m_searchEntries").Count == expectedRows && Get<MainForm.LogGrid>(form, "m_gridSearch").LargeView.VirtualListSize == MainForm.GridPageSize + MainForm.PageOverlapRows, "all search matches displayed");
                record("search_all_and_chart", searchMs);
                MainForm.VirtualLogList searchList = Get<MainForm.LogGrid>(form, "m_gridSearch").LargeView;
                NumericUpDown searchPage = Get<NumericUpDown>(form, "m_searchPage");
                searchPage.Value = searchPage.Maximum;
                searchList.Items[searchList.VirtualListSize - 1].Selected = true;
                searchList.Items[searchList.VirtualListSize - 1].EnsureVisible();
                Call(searchList, "OnDoubleClick", EventArgs.Empty);
                Assert(Object.ReferenceEquals(Get<LogEntry>(clickChart, "m_selectedEntry"), loadedRows[expectedRows - 1]), "search jumps to final entry");
                phase.Restart();
                Assert(mainList.Items[expectedRows - 1 - mainList.FirstEntry].SubItems[4].Text == loadedRows[expectedRows - 1].DisplayText, "last row can be displayed");
                Capture(form, Path.Combine(outputFolder, "last-row.png")); record("scroll_and_paint_last_row", phase.ElapsedMilliseconds);
                Call(form, "OpenViewerWindow", Get<TableLayoutPanel>(form, "m_panelTiming"));
                Form chartWindow = Get<Form>(form, "m_timingWindow");
                TestWindowPlacement(Get<TimingChart>(form, "m_chart"));
                DateTime first = Get<List<LogEntry>>(form, "m_entries")[0].Timestamp.Value;
                Get<DateTimePicker>(form, "m_dtStart").Value = first; Get<DateTimePicker>(form, "m_dtEnd").Value = first;
                Get<CheckBox>(form, "m_chkTimeRange").Checked = true;
                phase.Restart(); Task range = (Task)Call(form, "RefreshView"); Wait(delegate { return range.IsCompleted; }, "large time filter", timeoutSeconds: 300);
                List<LogEntry> selected = Get<List<LogEntry>>(form, "m_visibleEntries");
                Assert(selected.Count == 1000, "time range narrows million rows to exact first second");
                string export = Path.Combine(outputFolder, "range-export.log");
                Assert(LogReader.ExportRange(export, selected, first, first.AddSeconds(1).AddTicks(-1), CancellationToken.None) == 1000, "large-data selected range exports correctly");
                Assert(File.ReadAllLines(export).Length == 1000, "exported file line count");
                record("time_filter_detached_chart_export", phase.ElapsedMilliseconds);
                chartWindow.Close();
                if (stress)
                {
                    Get<CheckBox>(form, "m_chkTimeRange").Checked = false;
                    Get<TextBox>(form, "m_txtSearch").Text = "";
                    List<KeywordRule> manyRules = Get<List<KeywordRule>>(form, "m_rules"); manyRules.Clear();
                    for (int index = 0; index < 30; index++) { manyRules.Add(new KeywordRule { Text = "KW" + index.ToString("D2") + "|", Color = Color.SteelBlue }); }
                    Call(form, "RebuildKeywordChips");
                    phase.Restart(); Task many = (Task)Call(form, "RefreshView"); Wait(delegate { return many.IsCompleted; }, "30 keywords", timeoutSeconds: 300);
                    TimingData data = Get<TimingData>(Get<TimingChart>(form, "m_chart"), "m_data");
                    Assert(data.Lanes.Count == 30 && data.EventCount == expectedRows, "30 keyword lanes account for every event");
                    Capture(form, Path.Combine(outputFolder, "30-keywords.png")); record("30_keywords_and_paint", phase.ElapsedMilliseconds);
                    // 실제 폼의 재불러오기 취소가 기존 로그를 유지하는지 확인한다.
                    phase.Restart(); Get<Button>(form, "m_btnReload").PerformClick();
                    Wait(delegate { return phase.ElapsedMilliseconds >= 100; }, "before cancel");
                    phase.Restart(); Get<Button>(form, "m_btnCancel").PerformClick();
                    Wait(delegate { return !Get<bool>(form, "m_bLoading"); }, "cancel reload", timeoutSeconds: 300);
                    Assert(Object.ReferenceEquals(Get<List<LogEntry>>(form, "m_entries"), loadedRows), "cancel keeps previous entries");
                    record("cancel_reload", phase.ElapsedMilliseconds);
                    WeakReference previousEntries = new WeakReference(loadedRows);
                    loadedRows = null; selected = null; data = null;
                    phase.Restart(); Get<Button>(form, "m_btnReload").PerformClick();
                    Wait(delegate { return !Get<bool>(form, "m_bLoading") && Get<CancellationTokenSource>(form, "m_viewCancellation") == null; }, "full reload", timeoutSeconds: 300);
                    Assert(Get<List<LogEntry>>(form, "m_visibleEntries").Count == expectedRows && mainList.Entries.Count == expectedRows && Get<List<string>>(form, "m_warnings").Count == 0, "reload keeps complete dataset");
                    Assert(!Object.ReferenceEquals(Get<List<LogEntry>>(form, "m_entries"), previousEntries.Target), "reload replaced the previous dataset");
                    record("full_reload_30_keywords", phase.ElapsedMilliseconds);
                }
                Console.WriteLine("PASS COMPLETE rows=" + expectedRows + " bytes=" + inputBytes + " stress=" + stress);
                form.Close(); heartbeat.Stop();
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL large WinForms: " + ex); return 1; }
        finally
        {
            new AppSettings { SelectedPreset = "기본" }.Save(Path.Combine(baseFolder, "settings.ini"));
            File.Delete(Path.Combine(presetFolder, "대용량 검증.ini"));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr owner);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr value, string text);

    private delegate bool ChildVisitor(IntPtr window, IntPtr state);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr window, ChildVisitor visit, IntPtr state);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    // 시험 대화상자 안에서만 Windows 셸 주소 표시줄의 자식 컨트롤을 찾는다.
    private static IntPtr FindShellControl(IntPtr parent, string className)
    {
        if (parent == IntPtr.Zero) { return IntPtr.Zero; }
        IntPtr result = IntPtr.Zero;
        System.Text.StringBuilder name = new System.Text.StringBuilder(80);
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr state)
        {
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != className) { return true; }
            result = child;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    // 시험 폼의 셸 창의 주소 표시줄을 F4로 열어 주소 입력, 이동, 확정 또는 취소를 검증한다.
    private static void UseExplorerPicker(MainForm form, string path)
    {
        const int Command = 0x0111, SetText = 0x000C, Confirm = 1, Cancel = 2;
        const int KeyDown = 0x0100, KeyUp = 0x0101;
        int stage = 0;
        long addressKeyTime = 0;
        string error = null;
        Stopwatch elapsed = Stopwatch.StartNew();
        using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 })
        {
            timer.Tick += delegate
            {
                IntPtr dialog = GetLastActivePopup(form.Handle);
                if (dialog == form.Handle) { return; }
                if (elapsed.Elapsed.TotalSeconds > 12)
                {
                    error = "Explorer dialog timed out at stage " + stage;
                    timer.Stop(); PostMessage(dialog, Command, new IntPtr(Cancel), IntPtr.Zero); return;
                }
                if (path == null)
                { timer.Stop(); PostMessage(dialog, Command, new IntPtr(Cancel), IntPtr.Zero); return; }
                IntPtr address = FindShellControl(dialog, "Address Band Root");
                if (address == IntPtr.Zero) { return; }
                if (stage == 0)
                {
                    addressKeyTime = elapsed.ElapsedMilliseconds;
                    PostMessage(dialog, KeyDown, new IntPtr((int)Keys.F4), IntPtr.Zero);
                    PostMessage(dialog, KeyUp, new IntPtr((int)Keys.F4), IntPtr.Zero);
                    stage++;
                }
                else if (stage == 1)
                {
                    IntPtr edit = FindShellControl(address, "Edit");
                    if (!IsWindowVisible(edit))
                    {
                        // 셸이 초기화 중 첫 F4를 받지 못하면 주소 표시줄을 다시 연다.
                        if (elapsed.ElapsedMilliseconds - addressKeyTime > 1000) { stage = 0; }
                        return;
                    }
                    SendMessage(edit, SetText, IntPtr.Zero, path);
                    PostMessage(edit, KeyDown, new IntPtr((int)Keys.Enter), IntPtr.Zero);
                    PostMessage(edit, KeyUp, new IntPtr((int)Keys.Enter), IntPtr.Zero);
                    stage++;
                }
                else if (stage == 2 && !IsWindowVisible(FindShellControl(address, "Edit")))
                {
                    stage++;
                    PostMessage(dialog, Command, new IntPtr(Confirm), IntPtr.Zero);
                }
            };
            timer.Start();
            Get<Button>(form, "m_btnFolder").PerformClick();
        }
        Assert(error == null, error);
    }

    // 메시지 루프가 시작된 뒤 UI 스레드의 동기화 컨텍스트에서 테스트한다.
    private static int ExecuteTests(string sampleFolder)
    {
        string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        string keywordPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timing-keywords.ini");
        KeywordSettings.Save(keywordPath, new List<KeywordRule> { new KeywordRule { Text = "Task Start", Color = Color.MediumPurple }, new KeywordRule { Text = "Sequence Start", Color = Color.DarkCyan }, new KeywordRule { Text = "Sequence END", Color = Color.ForestGreen } });
        new AppSettings { SelectedPreset = "기본" }.Save(settingsPath);
        string testPresets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timing-presets");
        if (Directory.Exists(testPresets))
        {
            foreach (string testFile in Directory.GetFiles(testPresets, "검증 프로그램 *.ini")) { File.Delete(testFile); }
            KeywordSettings.Save(Path.Combine(testPresets, "기본.ini"), KeywordSettings.Load(keywordPath));
        }
        string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "artifacts"));
        Directory.CreateDirectory(artifacts);
        try
        {
            TestLocalPairRendering(artifacts);
            using (MainForm form = new MainForm(null))
            {
                form.Show();
                Wait(delegate { return Get<CancellationTokenSource>(form, "m_viewCancellation") == null; }, "startup");
                TextBox mainPath = Get<TextBox>(form, "m_txtFolder");
                mainPath.Text = Path.GetDirectoryName(sampleFolder);
                UseExplorerPicker(form, null);
                Assert(mainPath.Text == Path.GetDirectoryName(sampleFolder), "cancel preserves the initial path");
                UseExplorerPicker(form, sampleFolder);
                Assert(mainPath.Text == sampleFolder, "Explorer address bar navigation selects the requested folder");
                Wait(delegate { return !Get<bool>(form, "m_bLoading") && Get<CancellationTokenSource>(form, "m_viewCancellation") == null; }, "load", delegate { return Get<Label>(form, "m_lblStatus").Text + " loading=" + Get<bool>(form, "m_bLoading") + " count=" + Get<List<LogEntry>>(form, "m_entries").Count; });
                Assert(Get<DataGridView>(form, "m_gridLogs").RowCount == 43312, "sample visible");
                Get<Button>(form, "m_btnReload").PerformClick();
                Get<Button>(form, "m_btnCancel").PerformClick();
                Wait(delegate { return Get<CancellationTokenSource>(form, "m_loadCancellation") == null; }, "sidebar after cancel");
                Assert(!Get<ProgressBar>(form, "m_loadProgress").Visible && Get<DataGridView>(form, "m_gridLogs").RowCount == 43312, "cancel hides load progress and keeps existing rows");
                Assert(Get<DataGridView>(form, "m_gridLogs").Columns[0].HeaderText == "날짜" && Get<DataGridView>(form, "m_gridLogs").Columns[1].HeaderText == "시간", "date and time use separate columns");
                Assert((string)Get<DataGridView>(form, "m_gridLogs").Rows[0].Cells[0].Value == "2026-11-16" && (string)Get<DataGridView>(form, "m_gridLogs").Rows[0].Cells[1].Value == "10:50:03.916", "date and millisecond time values separated");
                Assert(!((string)Get<DataGridView>(form, "m_gridLogs").Rows[0].Cells[4].Value).StartsWith("2026-11-16", StringComparison.Ordinal), "log body excludes the extracted timestamp");

                AssertToolbarLayout(form);
                Assert(Get<DataGridView>(form, "m_gridLogs").Rows[0].Cells[0].FormattedValue != null, "columns available");
                Assert(Get<DataGridView>(form, "m_gridLogs").Rows[0].Cells[0].InheritedStyle.BackColor.ToArgb() == Color.White.ToArgb(), "neutral grid style");
                DataGridViewCellFormattingEventArgs neutral = new DataGridViewCellFormattingEventArgs(0, 0, null, typeof(string), new DataGridViewCellStyle());
                Call(form, "GridCellFormatting", Get<DataGridView>(form, "m_gridLogs"), neutral);
                Assert(neutral.CellStyle.BackColor.ToArgb() == Color.White.ToArgb(), "default rows have no Task colors");
                TextBox search = Get<TextBox>(form, "m_txtSearch");
                search.Text = "Sequence Start";
                Task task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "search");
                int matchCount = Get<DataGridView>(form, "m_gridSearch").RowCount;
                Assert(matchCount > 0 && matchCount < 43312, "search subset");
                List<LogEntry> matches = Get<List<LogEntry>>(form, "m_searchEntries");
                Call(form, "SelectMainEntry", matches[0]);
                Assert(Get<DataGridView>(form, "m_gridLogs").CurrentCell.RowIndex == Get<List<LogEntry>>(form, "m_visibleEntries").IndexOf(matches[0]), "search to original row");
                Console.WriteLine("LAYOUT complete log rows=" + Get<DataGridView>(form, "m_gridLogs").DisplayedRowCount(false) +
                    " search rows=" + Get<DataGridView>(form, "m_gridSearch").DisplayedRowCount(false));
                Capture(form, Path.Combine(artifacts, "light-mode.png"));
                Get<CheckBox>(form, "m_chkDark").Checked = true;
                Assert(form.BackColor.R < 50, "dark background");
                Capture(form, Path.Combine(artifacts, "dark-mode.png"));
                TimingChart chart = Get<TimingChart>(form, "m_chart");
                chart.Zoom(0.5);
                Assert(Get<double>(chart, "m_dViewSpan") < (Get<TimingData>(chart, "m_data").Maximum - Get<TimingData>(chart, "m_data").Minimum).TotalSeconds, "chart zoom");
                chart.ResetView();
                TimingData data = Get<TimingData>(chart, "m_data");
                Assert(data.EventCount == 847, "sample keyword events");
                Assert(data.AnomalyCount > 0, "sample irregular intervals detected");
                LogEntry firstEvent = data.Lanes[0].Entries[0];
                int plotWidth = (int)chart.GetType().GetProperty("PlotWidth", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chart, null);
                double span = Get<double>(chart, "m_dViewSpan");
                int eventX = 194 + (int)((firstEvent.Timestamp.Value - data.Minimum).TotalSeconds / span * plotWidth);
                Call(chart, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, eventX, 56, 0));
                Call(chart, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, eventX, 56, 0));
                Assert(Get<DataGridView>(form, "m_gridLogs").CurrentCell.RowIndex == Get<List<LogEntry>>(form, "m_visibleEntries").IndexOf(firstEvent), "chart to original row");
                DataGridView resultGrid = Get<DataGridView>(form, "m_gridSearch");
                resultGrid.CurrentCell = resultGrid.Rows[resultGrid.RowCount - 1].Cells[0];
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), matches[matches.Count - 1]), "single search row selection moves cursor");
                chart.ResetView(); chart.Zoom(0.01);
                DataGridView mainGrid = Get<DataGridView>(form, "m_gridLogs");
                mainGrid.CurrentCell = mainGrid.Rows[mainGrid.RowCount - 1].Cells[0];
                LogEntry last = Get<List<LogEntry>>(form, "m_visibleEntries")[mainGrid.RowCount - 1];
                double selectedSeconds = (last.Timestamp.Value - data.Minimum).TotalSeconds;
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), last), "single main row selection moves cursor");
                Assert(selectedSeconds >= Get<double>(chart, "m_dViewStart") && selectedSeconds <= Get<double>(chart, "m_dViewStart") + Get<double>(chart, "m_dViewSpan") + 0.000001, "selected cursor is inside zoom window");
                chart.ResetView();
                CheckBox taskColors = Get<CheckBox>(form, "m_chkTaskColors");
                Assert(!taskColors.Checked && mainGrid.Visible && mainGrid.Columns.Count == 6, "default neutral column view");
                mainGrid.CurrentCell = mainGrid.Rows[0].Cells[4]; mainGrid.FirstDisplayedScrollingRowIndex = 0;
                Rectangle logCell = mainGrid.GetCellDisplayRectangle(4, 0, false);
                string firstText = Get<List<LogEntry>>(form, "m_visibleEntries")[0].DisplayText;
                int anchorX; int caretX;
                using (Graphics graphics = mainGrid.CreateGraphics())
                {
                    TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                    anchorX = logCell.Left + mainGrid.DefaultCellStyle.Padding.Left + TextRenderer.MeasureText(graphics, firstText.Substring(0, 1), mainGrid.DefaultCellStyle.Font, new Size(Int32.MaxValue, Int32.MaxValue), flags).Width;
                    caretX = logCell.Left + mainGrid.DefaultCellStyle.Padding.Left + TextRenderer.MeasureText(graphics, firstText.Substring(0, 5), mainGrid.DefaultCellStyle.Font, new Size(Int32.MaxValue, Int32.MaxValue), flags).Width;
                }
                Call(mainGrid, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, anchorX, logCell.Top + 10, 0));
                Call(mainGrid, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, caretX, logCell.Top + 85, 0));
                Assert((string)Call(form, "SelectedLogText") == firstText.Substring(1, 4), "vertical drag selects substring within starting row only");
                Assert(mainGrid.CurrentCell.RowIndex == 0, "drag preserves starting row");
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), Get<List<LogEntry>>(form, "m_visibleEntries")[0]), "substring drag keeps time cursor on starting row");
                Call(mainGrid, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, logCell.Left, logCell.Top - 45, 0));
                Assert((string)Call(form, "SelectedLogText") == firstText.Substring(0, 1), "backward drag remains in row");
                Call(mainGrid, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, logCell.Left, logCell.Top - 45, 0));
                Assert((string)Call(form, "SelectedLogText") == firstText.Substring(0, 1), "selection survives mouse release");
                mainGrid.Capture = false;
                ToolStripItem colorMenu = mainGrid.ContextMenuStrip.Items["SelectedTextColor"];
                Assert(colorMenu != null, "selected text color is available from the context menu");
                Rectangle rightClickCell = mainGrid.GetCellDisplayRectangle(0, 1, false);
                IntPtr rightClick = new IntPtr(((rightClickCell.Top + 5) << 16) | (rightClickCell.Left + 5));
                SendMessage(mainGrid.Handle, MouseRightDown, new IntPtr(2), rightClick);
                SendMessage(mainGrid.Handle, MouseRightUp, IntPtr.Zero, rightClick);
                Application.DoEvents();
                Assert(mainGrid.ContextMenuStrip.Visible && mainGrid.ContextMenuStrip.SourceControl == mainGrid, "right click opens grid context menu");
                Assert((string)Call(form, "SelectedLogText") == firstText.Substring(0, 1) && mainGrid.CurrentCell.RowIndex == 0, "right click preserves substring and its source row");
                Assert(colorMenu.Enabled, "context color action enabled with a selection");
                using (Bitmap menuBitmap = new Bitmap(mainGrid.ContextMenuStrip.Width, mainGrid.ContextMenuStrip.Height))
                {
                    mainGrid.ContextMenuStrip.DrawToBitmap(menuBitmap, mainGrid.ContextMenuStrip.ClientRectangle);
                    menuBitmap.Save(Path.Combine(artifacts, "text-highlight-menu.png"), ImageFormat.Png);
                }
                mainGrid.ContextMenuStrip.Close();
                List<KeyValuePair<string, Color>> highlightRules = Get<List<KeyValuePair<string, Color>>>(form, "m_highlights");
                resultGrid.CurrentCell = resultGrid.Rows[0].Cells[4]; resultGrid.FirstDisplayedScrollingRowIndex = 0;
                Assert(resultGrid.Rows[0].Cells[4].InheritedStyle.Font.Equals(mainGrid.Rows[0].Cells[4].InheritedStyle.Font), "search and log grid fonts match");
                TestHoverPaint(mainGrid, mainGrid.FirstDisplayedScrollingRowIndex);
                TestHoverPaint(resultGrid, 0);
                TestSearchDrag(form, resultGrid, resultGrid.GetCellDisplayRectangle(4, 0, false));
                Button highlights = Get<Button>(form, "m_btnHighlights");
                highlights.PerformClick(); Application.DoEvents();
                Assert(highlights.ContextMenuStrip.Visible && Get<ToolStripMenuItem>(form, "m_menuClearHighlights").Enabled, "highlight menu opens with clear available");
                Get<ToolStripMenuItem>(form, "m_menuClearHighlights").PerformClick();
                highlights.ContextMenuStrip.Close();
                Call(form, "SetHighlight", "Sequence Start", Color.Yellow);
                Call(form, "SetHighlight", "Sequence END", Color.Navy);
                mainGrid.ContextMenuStrip.Show(mainGrid, new Point(logCell.Left + 20, logCell.Top + 20));
                Application.DoEvents(); Assert(!colorMenu.Enabled, "context color action disabled after selection cleared");
                mainGrid.ContextMenuStrip.Close();
                Assert(Get<bool>(form, "m_bKeywordDirty"), "highlight change marks preset dirty");
                Color[] matchingColors = (Color[])Call(form, "LogHighlightColors", "Sequence Start x Sequence Start Sequence END", Int32.MaxValue);
                Assert(matchingColors[0] == Color.Yellow && matchingColors[17] == Color.Yellow && matchingColors[32] == Color.Navy && matchingColors[14].IsEmpty, "all exact matches colored and nonmatches neutral");
                Color[] clippedColors = (Color[])Call(form, "LogHighlightColors", "Sequence Start x Sequence Start Sequence END", 5);
                Assert(clippedColors.Length == 5 && clippedColors[4] == Color.Yellow, "highlight crossing visible boundary remains colored");
                taskColors.Checked = true;
                DataGridViewCellFormattingEventArgs colored = new DataGridViewCellFormattingEventArgs(0, 0, null, typeof(string), new DataGridViewCellStyle());
                Call(form, "GridCellFormatting", mainGrid, colored);
                Assert(colored.CellStyle.BackColor.ToArgb() != Color.White.ToArgb(), "Task button enables process colors");
                Capture(form, Path.Combine(artifacts, "task-colors.png"));
                taskColors.Checked = false;
                Get<CheckBox>(form, "m_chkDark").Checked = false;
                Call(form, "SelectMainEntry", matches[0]);
                mainGrid.Invalidate();
                Capture(form, Path.Combine(artifacts, "white-highlight-mode.png"));
                Button timingPopout = form.GetType().GetField("m_btnPopoutTiming", BindingFlags.NonPublic | BindingFlags.Instance) == null ? null : Get<Button>(form, "m_btnPopoutTiming");
                Assert(timingPopout != null, "timing chart has a new-window button");
                SplitContainer timingSplit = Get<SplitContainer>(form, "m_splitTiming");
                timingSplit.SplitterDistance += 20;
                int embeddedTimingHeight = timingSplit.SplitterDistance;
                int embeddedLogHeight = mainGrid.Height;
                int embeddedSearchHeight = resultGrid.Height;
                int embeddedChartHeight = chart.Height;
                Call(chart, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 194, 56, 0));
                Get<ToolStripMenuItem>(chart, "m_menuStart").PerformClick();
                Assert(Get<DateTime?>(chart, "m_rangeStart").HasValue && !Get<ToolStripMenuItem>(chart, "m_menuExport").Enabled, "right-click start alone cannot export");

                timingPopout.PerformClick();
                Form timingWindow = Get<Form>(form, "m_timingWindow");
                Assert(timingSplit.Panel1Collapsed && mainGrid.Height > embeddedLogHeight && resultGrid.Height > embeddedSearchHeight, "detached chart collapses main area and enlarges both log viewers");
                Capture(form, Path.Combine(artifacts, "main-chart-collapsed.png"));
                Assert(timingWindow != null && chart.FindForm() == timingWindow && chart.Height > embeddedChartHeight, "timing chart opens larger in a separate window");
                Assert(Get<ComboBox>(form, "m_cmbPreset").FindForm() == timingWindow && Get<Button>(form, "m_btnKeywords").FindForm() == timingWindow, "timing tools move with the chart");
                FormClosingEventHandler cancelTimingClose = delegate(object sender, FormClosingEventArgs e) { e.Cancel = true; };
                timingWindow.FormClosing += cancelTimingClose; timingWindow.Close();
                Assert(chart.FindForm() == timingWindow && !chart.IsDisposed, "canceling timing close retains chart");
                Assert(timingSplit.Panel1Collapsed, "canceling timing close keeps main area collapsed");
                timingWindow.FormClosing -= cancelTimingClose;
                Get<CheckBox>(form, "m_chkDark").Checked = true;
                Assert(timingWindow.BackColor.R < 50 && chart.BackColor.R < 50, "detached timing receives theme changes");
                Capture(timingWindow, Path.Combine(artifacts, "detached-timing-dark.png"));
                Get<CheckBox>(form, "m_chkDark").Checked = false;
                Capture(timingWindow, Path.Combine(artifacts, "detached-timing.png"));
                int rangePlotWidth = (int)chart.GetType().GetProperty("PlotWidth", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chart, null);
                Call(chart, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 194 + rangePlotWidth, 56, 0));
                Get<ToolStripMenuItem>(chart, "m_menuEnd").PerformClick();
                DateTime exportStart = Get<DateTime?>(chart, "m_rangeStart").Value;
                DateTime exportEnd = Get<DateTime?>(chart, "m_rangeEnd").Value;
                Assert(Get<ToolStripMenuItem>(chart, "m_menuExport").Enabled && exportEnd >= exportStart, "detached chart preserves start and sets end for export");
                string exported = Path.Combine(artifacts, "ui-range-export.log");
                int exportedCount = LogReader.ExportRange(exported, Get<List<LogEntry>>(form, "m_visibleEntries"), exportStart, exportEnd, CancellationToken.None);
                Assert(exportedCount > 0 && File.ReadAllLines(exported).Length == exportedCount, "selected UI range exports original log rows");
                Capture(timingWindow, Path.Combine(artifacts, "timing-export-range.png"));
                Call(chart, "SetRangeBoundary", exportEnd.AddSeconds(1), true);
                Assert(!Get<ToolStripMenuItem>(chart, "m_menuExport").Enabled, "reversed chart selection disables export");
                Call(chart, "SetRangeBoundary", exportStart, true);

                mainGrid.CurrentCell = mainGrid.Rows[mainGrid.RowCount - 1].Cells[0];
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), Get<List<LogEntry>>(form, "m_visibleEntries")[mainGrid.RowCount - 1]), "main selection updates detached timing cursor");
                resultGrid.CurrentCell = resultGrid.Rows[0].Cells[0];
                Call(resultGrid, "OnCellClick", new DataGridViewCellEventArgs(0, 0));
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), matches[0]), "search selection updates detached timing cursor");
                int detachedPlotWidth = (int)chart.GetType().GetProperty("PlotWidth", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chart, null);
                int detachedEventX = 194 + (int)((firstEvent.Timestamp.Value - data.Minimum).TotalSeconds / span * detachedPlotWidth);
                Call(chart, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, detachedEventX, 56, 0));
                Call(chart, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, detachedEventX, 56, 0));
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), firstEvent), "detached chart marker selects original log");
                Call(form, "SaveKeywords", false);
                List<KeywordRule> manyRules = new List<KeywordRule>(Get<List<KeywordRule>>(form, "m_rules"));
                for (int keywordIndex = manyRules.Count; keywordIndex < 24; keywordIndex++)
                { manyRules.Add(new KeywordRule { Text = "추가 키워드 " + keywordIndex, Color = Color.SteelBlue }); }
                Call(form, "CreatePreset", "검증 프로그램 차트 " + Guid.NewGuid().ToString("N").Substring(0, 8), manyRules, Get<List<KeyValuePair<string, Color>>>(form, "m_highlights"));
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "many detached timing keywords");
                VScrollBar laneScroll = Get<VScrollBar>(chart, "m_vScroll");
                int detachedLanes = (int)chart.GetType().GetProperty("VisibleLanes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chart, null);
                Assert(Get<TimingData>(chart, "m_data").Lanes.Count == 24 && laneScroll.Enabled && detachedLanes > 4, "detached chart displays more keyword lanes and supports overflow");
                FlowLayoutPanel timingChips = Get<FlowLayoutPanel>(form, "m_keywordChips");
                Assert(timingChips.Controls[0].Bottom <= timingChips.ClientSize.Height, "keyword buttons fit above horizontal scrollbar");
                Capture(timingWindow, Path.Combine(artifacts, "detached-timing-many.png"));
                laneScroll.Value = laneScroll.Maximum - laneScroll.LargeChange + 1;
                Assert(laneScroll.Value + detachedLanes >= 24, "last keyword is reachable with vertical scroll");
                timingWindow.Size = timingWindow.MinimumSize;
                Application.DoEvents();
                Assert(timingPopout.Right <= timingPopout.Parent.ClientSize.Width && Get<ComboBox>(form, "m_cmbPreset").Visible, "timing tools fit minimum detached window");
                Capture(timingWindow, Path.Combine(artifacts, "detached-timing-minimum.png"));
                Call(form, "DeletePreset");
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "restore timing preset");
                Assert(Get<TimingData>(chart, "m_data").EventCount == 847, "detached preset switch restores events");
                timingPopout.PerformClick();
                Assert(chart.FindForm() == form && Get<Form>(form, "m_timingWindow") == null, "original-position button restores timing chart");
                Assert(!timingSplit.Panel1Collapsed && timingSplit.SplitterDistance == embeddedTimingHeight, "original-position restores custom chart height");
                timingPopout.PerformClick();
                timingWindow = Get<Form>(form, "m_timingWindow");
                timingWindow.Close();
                Assert(chart.FindForm() == form && !chart.IsDisposed && Get<Form>(form, "m_timingWindow") == null, "timing chart restores when closed");
                Assert(!timingSplit.Panel1Collapsed && timingSplit.SplitterDistance == embeddedTimingHeight, "closing timing window restores custom height repeatedly");
                Call(form, "OpenViewerWindow", Get<TableLayoutPanel>(form, "m_panelLogs"));
                Form logWindow = Get<Form>(form, "m_logWindow");
                Assert(Get<List<TreeView>>(form, "m_sourceTrees").Count == 2, "detached viewer includes a Task sidebar");
                Assert(Get<TableLayoutPanel>(form, "m_panelLogs").FindForm() == logWindow && mainGrid.Visible, "column viewer detached");
                Assert(Get<List<KeyValuePair<string, Color>>>(form, "m_highlights").Count == 2, "detaching preserves highlight rules");
                FormClosingEventHandler cancelClose = delegate(object sender, FormClosingEventArgs e) { e.Cancel = true; };
                logWindow.FormClosing += cancelClose; logWindow.Close();
                Assert(Get<Form>(form, "m_logWindow") == logWindow && Get<TableLayoutPanel>(form, "m_panelLogs").FindForm() == logWindow, "canceling detached close preserves viewer");
                logWindow.FormClosing -= cancelClose;
                Capture(logWindow, Path.Combine(artifacts, "detached-log-viewer.png"));
                Call(form, "OpenViewerWindow", Get<TableLayoutPanel>(form, "m_panelSearch"));
                Form searchWindow = Get<Form>(form, "m_searchWindow");
                Assert(Get<TableLayoutPanel>(form, "m_panelSearch").FindForm() == searchWindow, "search viewer detached");
                List<TreeView> sourceTrees = Get<List<TreeView>>(form, "m_sourceTrees");
                Assert(sourceTrees.Count == 3, "both detached windows have Task sidebars");
                timingPopout.PerformClick(); timingWindow = Get<Form>(form, "m_timingWindow");
                Assert(sourceTrees.Count == 4 && sourceTrees[3].FindForm() == timingWindow, "all three detached viewers have Task sidebars");
                sourceTrees[3].SelectedNode = sourceTrees[3].Nodes[0].Nodes[2];
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "timing Task selection");
                Assert(mainGrid.RowCount == 15 && sourceTrees[0].SelectedNode.Index == 2 && sourceTrees[1].SelectedNode.Index == 2, "timing sidebar filters and synchronizes other viewers");

                TreeView mainSources = sourceTrees[0];
                TreeView logSources = sourceTrees[1];
                TreeView searchSources = sourceTrees[2];
                Assert(logSources.FindForm() == logWindow && searchSources.FindForm() == searchWindow, "sidebars belong to their detached windows");
                logSources.SelectedNode = logSources.Nodes[0].Nodes[2];
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "detached Task selection");
                Assert(mainGrid.RowCount == 15 && mainSources.SelectedNode.Index == 2 && searchSources.SelectedNode.Index == 2, "detached Task selection filters logs and synchronizes all windows");
                searchSources.SelectedNode = searchSources.Nodes[0].Nodes[0].Nodes[0];
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "detached file selection");
                Assert(mainGrid.RowCount > 0 && mainGrid.RowCount < 43312 && mainSources.SelectedNode.Level == 2 && logSources.SelectedNode.Level == 2, "search sidebar selects an individual file");
                Call(form, "ShowAll");
                logSources.Nodes[0].Nodes[2].Checked = false;
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "detached Task checkbox");
                Assert(mainGrid.RowCount == 43312 - 15 && !mainSources.Nodes[0].Nodes[2].Checked && !searchSources.Nodes[0].Nodes[2].Checked, "detached checkbox filters and synchronizes Task state");
                Call(form, "ShowAll");
                Call(form, "BuildSourceTree", Get<List<LogEntry>>(form, "m_entries"));
                Assert(logSources.Nodes[0].Nodes.Count == mainSources.Nodes[0].Nodes.Count && searchSources.Nodes[0].Checked, "rebuilding sources updates open sidebars");
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "restore detached source filters");
                Assert(mainGrid.RowCount == 43312 && logSources.SelectedNode.Level == 0 && searchSources.SelectedNode.Level == 0, "all sources restored across windows");
                Capture(logWindow, Path.Combine(artifacts, "detached-log-viewer.png"));
                Capture(searchWindow, Path.Combine(artifacts, "detached-search-viewer.png"));
                logWindow.Size = logWindow.MinimumSize;
                Application.DoEvents();
                Assert(logSources.Width >= 180 && mainGrid.Width >= 600, "detached minimum size preserves sidebar and viewer: " + logSources.Width + "/" + mainGrid.Width);
                Capture(logWindow, Path.Combine(artifacts, "detached-minimum-window.png"));
                resultGrid.CurrentCell = resultGrid.Rows[0].Cells[0];
                Call(resultGrid, "OnCellClick", new DataGridViewCellEventArgs(0, 0));
                Assert(Object.ReferenceEquals(Get<LogEntry>(chart, "m_selectedEntry"), matches[0]), "detached search moves cursor");
                searchWindow.Close(); logWindow.Close(); timingWindow.Close();
                Assert(sourceTrees.Count == 1, "closed sidebars removed from synchronization");
                Assert(Get<TableLayoutPanel>(form, "m_panelLogs").Parent == Get<SplitContainer>(form, "m_splitResults").Panel1, "main viewer restored");
                Assert(Get<TableLayoutPanel>(form, "m_panelSearch").Parent == Get<SplitContainer>(form, "m_splitResults").Panel2, "search viewer restored");
                Get<ToolStripMenuItem>(form, "m_menuClearHighlights").PerformClick();
                Assert(Get<List<KeyValuePair<string, Color>>>(form, "m_highlights").Count == 0, "highlights cleared");
                search.Text = "["; Get<CheckBox>(form, "m_chkRegex").Checked = true;
                TreeView sources = Get<TreeView>(form, "m_treeSources");
                sources.SelectedNode = sources.Nodes[0].Nodes[2];
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "invalid query filtered main");
                Assert(Get<DataGridView>(form, "m_gridLogs").RowCount == 15, "filter still changes after bad regex");
                Assert(Get<Label>(form, "m_lblSearchCount").Text.Contains("오류"), "search failure visible");
                Get<CheckBox>(form, "m_chkRegex").Checked = false; search.Clear();
                Get<Button>(form, "m_btnAll").PerformClick();
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "reset");
                FlowLayoutPanel chips = Get<FlowLayoutPanel>(form, "m_keywordChips");
                ((CheckBox)chips.Controls[0]).Checked = false;
                Assert(Get<bool>(form, "m_bKeywordDirty"), "keyword dirty");
                Assert((bool)Call(form, "SaveKeywords", false), "save keyword");
                Assert(!KeywordSettings.Load(Get<string>(form, "m_strKeywordPath"))[0].Enabled, "saved toggle");
                ((CheckBox)chips.Controls[0]).Checked = true;
                Call(form, "SaveKeywords", false);
                Assert(KeywordSettings.Load(Get<string>(form, "m_strKeywordPath"))[0].CheckIntervals, "chip toggle preserves interval options");
                Call(form, "SetHighlight", "Sequence Start", Color.Yellow);
                Call(form, "SaveKeywords", false);
                ComboBox presets = Get<ComboBox>(form, "m_cmbPreset");
                string originalPreset = (string)presets.SelectedItem;
                string originalPath = Get<string>(form, "m_strKeywordPath");
                string testName = "검증 프로그램 " + Guid.NewGuid().ToString("N").Substring(0, 8);
                List<KeywordRule> programRules = new List<KeywordRule> { new KeywordRule { Text = "PROGRAM RUN", ExpectedIntervalMs = 2000, TolerancePercent = 15, Color = Color.Navy } };
                Call(form, "CreatePreset", testName, programRules, new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>("Sequence END", Color.Navy) });
                Assert(highlightRules.Count == 1 && highlightRules[0].Key == "Sequence END", "new preset replaces previous highlights");
                Assert((string)presets.SelectedItem == testName && Get<List<KeywordRule>>(form, "m_rules")[0].ExpectedIntervalMs == 2000, "new program preset selected with interval settings");
                Assert(KeywordSettings.Load(originalPath).Count == 3, "original preset remains separate");
                bool duplicateRejected = false;
                try { Call(form, "CreatePreset", originalPreset, programRules, new List<KeyValuePair<string, Color>>()); }
                catch (TargetInvocationException ex) { duplicateRejected = ex.InnerException is IOException; }
                Assert(duplicateRejected && KeywordSettings.Load(originalPath).Count == 3, "duplicate name cannot overwrite existing preset");
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "program preset chart");
                Assert(Get<TimingData>(chart, "m_data").EventCount == 0, "program preset recalculates chart");
                Capture(form, Path.Combine(artifacts, "program-preset.png"));
                presets.SelectedItem = originalPreset;
                Assert(Get<List<KeywordRule>>(form, "m_rules").Count == 3, "switch restores original keywords");
                Assert(highlightRules.Count == 1 && highlightRules[0].Key == "Sequence Start" && highlightRules[0].Value.ToArgb() == Color.Yellow.ToArgb(), "switch restores original highlights");
                task = (Task)Call(form, "RefreshView");
                Wait(delegate { return task.IsCompleted; }, "original preset chart");
                Assert(Get<TimingData>(chart, "m_data").EventCount == 847, "original preset restores chart events");
                presets.SelectedItem = testName;
                Assert(Get<List<KeywordRule>>(form, "m_rules")[0].Text == "PROGRAM RUN", "switch restores program keywords");
                Call(form, "SetHighlight", "Sequence END", Color.Red);
                Call(form, "SaveKeywords", false);
                Assert(AppSettings.Load(settingsPath).SelectedPreset == testName, "selected preset persisted");
                using (MainForm restarted = new MainForm(null))
                {
                    Assert(Get<List<KeywordRule>>(restarted, "m_rules")[0].Text == "PROGRAM RUN", "restart restores selected preset");
                    List<KeyValuePair<string, Color>> restartedHighlights = Get<List<KeyValuePair<string, Color>>>(restarted, "m_highlights");
                    Assert(restartedHighlights.Count == 1 && restartedHighlights[0].Key == "Sequence END" && restartedHighlights[0].Value.ToArgb() == Color.Red.ToArgb(), "restart restores highlight text and changed color");
                }
                // 이름 변경 전 저장하지 않음을 선택하면 디스크에 확정된 내용을 사용한다.
                Get<List<KeywordRule>>(form, "m_rules")[0].Text = "UNSAVED";
                Call(form, "RenamePreset", testName + " 변경");
                Assert(Get<List<KeywordRule>>(form, "m_rules")[0].Text == "PROGRAM RUN", "rename after discard keeps disk and memory consistent");
                Assert(highlightRules.Count == 1 && highlightRules[0].Value.ToArgb() == Color.Red.ToArgb(), "rename preserves highlights");
                Assert(!File.Exists(Path.Combine(Get<string>(form, "m_strPresetFolder"), testName + ".ini")), "rename removes old name");
                Call(form, "DeletePreset");
                Assert((string)presets.SelectedItem == originalPreset && Get<List<KeywordRule>>(form, "m_rules").Count == 3, "delete restores remaining preset");
                if (presets.Items.Count == 1)
                {
                    bool lastRejected = false;
                    try { Call(form, "DeletePreset"); }
                    catch (TargetInvocationException ex) { lastRejected = ex.InnerException is InvalidOperationException; }
                    Assert(lastRejected && File.Exists(originalPath), "last preset is preserved");
                }
                Assert(File.Exists(keywordPath), "legacy keyword INI preserved");
                Assert(highlightRules.Count == 1 && highlightRules[0].Key == "Sequence Start", "delete restores remaining preset highlights");
                Call(form, "CreatePreset", testName + " 복사", Get<List<KeywordRule>>(form, "m_rules"), highlightRules);
                Assert(highlightRules.Count == 1 && highlightRules[0].Key == "Sequence Start", "copy preserves highlight values without list alias loss");
                Call(form, "DeletePreset");
                Get<ToolStripMenuItem>(form, "m_menuClearHighlights").PerformClick();
                Assert(Get<bool>(form, "m_bKeywordDirty"), "clear marks preset dirty");
                Call(form, "SaveKeywords", false);
                using (MainForm cleared = new MainForm(null))
                { Assert(Get<List<KeyValuePair<string, Color>>>(cleared, "m_highlights").Count == 0, "cleared highlights stay cleared after restart"); }
                Get<CheckBox>(form, "m_chkDark").Checked = false;
                using (SettingsForm settings = new SettingsForm(new LogOptions(), false))
                { settings.Show(form); Application.DoEvents(); Capture(settings, Path.Combine(artifacts, "parser-settings.png")); settings.Close(); }
                KeywordRule pairRule = new KeywordRule { Text = "Sequence Start", CheckIntervals = false, LinkedKeywordNumber = 2, ExpectedIntervalMs = 9000, TolerancePercent = 5, LinkedExpectedIntervalMs = 1000, LinkedTolerancePercent = 20 };
                KeywordRule pairTarget = new KeywordRule { Text = "Sequence END", CheckIntervals = false };
                List<KeywordRule> pairRules = new List<KeywordRule> { pairRule, pairTarget, new KeywordRule { Text = "unused", CheckIntervals = false } };
                using (KeywordForm pairSettings = new KeywordForm(pairRules, false))
                {
                    pairSettings.Show(form); Application.DoEvents();
                    DataGridView pairGrid = Get<DataGridView>(pairSettings, "m_grid");
                    Assert(pairGrid.Columns["Number"].DisplayIndex == 0 && Convert.ToInt32(pairGrid.Rows[0].Cells["Number"].Value) == 1, "keyword numbers displayed first");
                    Assert(pairGrid.Rows[0].Cells["LinkedKeywordNumber"] is DataGridViewComboBoxCell && Convert.ToInt32(pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value) == 2, "link dropdown selects registered number");
                    Assert(pairGrid.Columns["ExpectedInterval"].HeaderText == "로그 간격 (ms)" && pairGrid.Columns["LinkedExpectedInterval"].HeaderText == "연동 로그 간격 (ms)", "timing interval headers");
                    DataGridViewColumn toleranceColumn = pairGrid.Columns["Tolerance"];
                    DataGridViewColumn linkedToleranceColumn = pairGrid.Columns["LinkedTolerance"];
                    Assert(toleranceColumn.HeaderText == "로그 간격 허용 편차 (%)" && linkedToleranceColumn.HeaderText == "연동 로그 간격 허용 편차 (%)", "tolerance headers");
                    foreach (DataGridViewColumn column in new DataGridViewColumn[] { toleranceColumn, linkedToleranceColumn })
                    {
                        int textWidth = TextRenderer.MeasureText(column.HeaderText, column.HeaderCell.InheritedStyle.Font).Width;
                        Assert(column.Width >= textWidth + column.HeaderCell.InheritedStyle.Padding.Horizontal + 4, "tolerance header fits on one line");
                    }
                    Assert(pairGrid.Columns["Case"].HeaderText == "대소문자 구분", "case sensitivity header");
                    DataGridViewCell intervalCell = pairGrid.Rows[0].Cells["LinkedExpectedInterval"];
                    DataGridViewCell toleranceCell = pairGrid.Rows[0].Cells["LinkedTolerance"];
                    Assert(!intervalCell.ReadOnly && !toleranceCell.ReadOnly && pairGrid.Rows[1].Cells["LinkedExpectedInterval"].ReadOnly && pairGrid.Rows[1].Cells["LinkedTolerance"].ReadOnly, "preset link controls are row-specific");
                    pairGrid.CurrentCell = pairGrid.Rows[0].Cells["LinkedKeywordNumber"];
                    pairGrid.BeginEdit(true);
                    ((ComboBox)pairGrid.EditingControl).SelectedValue = 0;
                    pairGrid.NotifyCurrentCellDirty(true);
                    Assert(intervalCell.ReadOnly && toleranceCell.ReadOnly && intervalCell.InheritedStyle.BackColor != pairGrid.DefaultCellStyle.BackColor, "selecting None immediately locks and dims both cells");
                    pairGrid.EndEdit();
                    pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value = 2;
                    Assert(!intervalCell.ReadOnly && !toleranceCell.ReadOnly && Convert.ToDouble(intervalCell.Value) == 1000 && Convert.ToDouble(toleranceCell.Value) == 20, "relink preserves both parameters");
                    pairGrid.CurrentCell = pairGrid.Rows[1].Cells["Text"]; Call(pairSettings, "MoveRule", 1);
                    Assert(Convert.ToInt32(pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value) == 3 && (string)pairGrid.Rows[2].Cells["Text"].Value == "Sequence END", "moving target preserves link to same keyword");
                    pairGrid.Rows[2].Cells["Text"].Value = "Renamed END";
                    Assert(Convert.ToInt32(pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value) == 3, "rename keeps numbered link");
                    pairGrid.CurrentCell = pairGrid.Rows[1].Cells["Text"]; Call(pairSettings, "RemoveRule");
                    Assert(Convert.ToInt32(pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value) == 2, "unrelated deletion updates linked number");
                    pairGrid.CurrentCell = pairGrid.Rows[1].Cells["Text"]; Call(pairSettings, "RemoveRule");
                    Assert(Convert.ToInt32(pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value) == 0, "deleting target clears link");
                    Assert(intervalCell.ReadOnly && toleranceCell.ReadOnly, "deleting target disables both parameters");
                    Call(pairSettings, "AddRule", pairTarget); Call(pairSettings, "UpdateKeywordNumbers", (object)null);
                    pairGrid.Rows[0].Cells["LinkedKeywordNumber"].Value = 2;
                    Capture(pairSettings, Path.Combine(artifacts, "keyword-pair-settings.png"));
                    Call(pairSettings, "ApplyRules");
                    Assert(pairSettings.Rules[0].LinkedKeywordNumber == 2 && pairSettings.Rules[0].LinkedExpectedIntervalMs == 1000 && pairSettings.Rules[0].ExpectedIntervalMs == 9000, "editor applies independent link and periodic values");
                }
                string pairPresetName = "검증 프로그램 간격 " + Guid.NewGuid().ToString("N").Substring(0, 8);
                Call(form, "CreatePreset", pairPresetName, pairRules, new List<KeyValuePair<string, Color>>());
                FlowLayoutPanel pairChips = Get<FlowLayoutPanel>(form, "m_keywordChips");
                Assert(pairChips.Controls[0].Text.StartsWith("1. ") && pairChips.Controls[1].Text.StartsWith("2. "), "chart chips use registered numbers");
                ((CheckBox)pairChips.Controls[0]).Checked = false; ((CheckBox)pairChips.Controls[0]).Checked = true;
                Call(form, "SaveKeywords", false);
                KeywordRule savedPair = KeywordSettings.Load(Get<string>(form, "m_strKeywordPath"))[0];
                Assert(savedPair.LinkedKeywordNumber == 2 && savedPair.LinkedExpectedIntervalMs == 1000 && savedPair.ExpectedIntervalMs == 9000, "toggle and preset save retain independent thresholds");
                task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "paired chart");
                Assert(Get<TimingData>(chart, "m_data").AnomalyCount > 0, "sample numbered link anomalies rendered");
                Capture(form, Path.Combine(artifacts, "keyword-pair-chart.png"));
                using (MainForm restartPairs = new MainForm(null))
                { Assert(Get<List<KeywordRule>>(restartPairs, "m_rules")[0].LinkedKeywordNumber == 2, "restart restores numbered link preset"); }
                Call(form, "DeletePreset");
                task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "restore after pair test");
                using (KeywordForm keywords = new KeywordForm(Get<List<KeywordRule>>(form, "m_rules"), false))
                { keywords.Show(form); Application.DoEvents(); Capture(keywords, Path.Combine(artifacts, "keyword-settings.png")); keywords.Close(); }
                TestTimeRange(form, artifacts);
                form.Size = form.MinimumSize;
                Application.DoEvents(); AssertToolbarLayout(form); Capture(form, Path.Combine(artifacts, "minimum-window.png"));
                form.Close();
                Console.WriteLine("PASS WinForms sample load, search=" + matchCount + ", 847 chart events, interval warnings, cursor synchronization, column view, single-row drag, Task colors, detached logs/search/timing, 24 timing lanes, collapse/restore, time ranges, highlights, presets, themes, INI save");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL WinForms: " + ex); return 1; }
    }

    // 누락 지점과 실제 지연 구간만 빨갛게 그려지고 이후 정상 마크는 원래 색인지 확인한다.
    private static void TestLocalPairRendering(string artifacts)
    {
        DateTime first = new DateTime(2026, 11, 17, 12, 0, 0);
        int[] offsets = new int[] { 0, 100, 1000, 2000, 2100, 3000, 3300, 4000, 4100 };
        string[] texts = new string[] { "A", "B", "A", "A", "B", "A", "B", "A", "B" };
        List<LogEntry> rows = new List<LogEntry>();
        for (int index = 0; index < offsets.Length; index++)
        { rows.Add(new LogEntry { Timestamp = first.AddMilliseconds(offsets[index]), Text = texts[index], Process = texts[index] == "A" ? "Task1" : "Task2", Kind = "MAIN" }); }
        List<KeywordRule> rules = new List<KeywordRule>
        {
            new KeywordRule { Text = "A", Color = Color.DodgerBlue, CheckIntervals = false, LinkedKeywordNumber = 2, LinkedExpectedIntervalMs = 100, LinkedTolerancePercent = 20 },
            new KeywordRule { Text = "B", Color = Color.ForestGreen, CheckIntervals = false }
        };
        TimingData data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 2, "one missing marker and one delayed interval");
        using (Form host = new Form { ClientSize = new Size(1100, 240), Text = "연동 이상 구간 검증" })
        using (TimingChart chart = new TimingChart { Dock = DockStyle.Fill })
        {
            host.Controls.Add(chart); host.Show(); chart.SetData(data);
            int gutter = (int)typeof(TimingChart).GetField("LeftGutter", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            int top = (int)typeof(TimingChart).GetField("TopGutter", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            int height = (int)typeof(TimingChart).GetField("LaneHeight", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            int width = (int)typeof(TimingChart).GetProperty("PlotWidth", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chart, null);
            for (int theme = 0; theme < 2; theme++)
            {
                chart.ApplyTheme(theme == 1); Application.DoEvents();
                Color red = theme == 1 ? Color.FromArgb(255, 104, 111) : Color.FromArgb(204, 32, 48);
                using (Bitmap bitmap = new Bitmap(chart.Width, chart.Height))
                {
                    chart.DrawToBitmap(bitmap, chart.ClientRectangle);
                    int missingX = gutter + (int)(1000.0 / 4100 * width);
                    Assert(bitmap.GetPixel(missingX, top + height / 2).ToArgb() == red.ToArgb(), "missing endpoint colors its own marker");
                    foreach (int index in new int[] { 3, 4, 7, 8 })
                    {
                        int lane = texts[index] == "A" ? 0 : 1;
                        int x = gutter + (int)(offsets[index] / 4100.0 * width);
                        Assert(bitmap.GetPixel(x, top + lane * height + height / 2).ToArgb() == rules[lane].Color.ToArgb(), "recovered normal marker color " + index);
                    }
                    int bandX = gutter + (int)(3150.0 / 4100 * width);
                    Assert(bitmap.GetPixel(bandX, top + height / 2 + 8).ToArgb() == red.ToArgb(), "actual delay has a red interval");
                    int normalX = gutter + (int)(4050.0 / 4100 * width);
                    Assert(bitmap.GetPixel(normalX, top + height / 2 + 8).ToArgb() != red.ToArgb(), "later normal interval has no red band");
                    TimingInterval hit = (TimingInterval)Call(chart, "HitInterval", new Point(missingX, top + height / 2));
                    string tooltip = (string)typeof(TimingChart).GetMethod("IntervalText", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { hit });
                    Assert(tooltip.Contains("연동 대상 없음") && !tooltip.Contains("실제"), "missing log tooltip does not invent an elapsed time");
                }
                Capture(host, Path.Combine(artifacts, theme == 1 ? "linked-local-anomaly-dark.png" : "linked-local-anomaly.png"));
            }
            host.Close();
        }
    }

    // 날짜/초 경계와 미인식 행을 포함한 고정 데이터로 공통 시간 필터를 검증한다.
    private static void TestTimeRange(MainForm form, string artifacts)
    {
        Assert(form.GetType().GetField("m_chkTimeRange", BindingFlags.NonPublic | BindingFlags.Instance) != null, "time range controls exist");
        CheckBox enabled = Get<CheckBox>(form, "m_chkTimeRange");
        DateTimePicker start = Get<DateTimePicker>(form, "m_dtStart");
        DateTimePicker end = Get<DateTimePicker>(form, "m_dtEnd");
        Assert(!enabled.Checked, "time filtering is disabled by default");
        List<LogEntry> originalEntries = Get<List<LogEntry>>(form, "m_entries");
        List<KeywordRule> originalRules = Get<List<KeywordRule>>(form, "m_rules");
        DateTime sampleStart = new DateTime(2026, 11, 16, 10, 50, 10);
        DateTime sampleEnd = sampleStart.AddSeconds(3);
        start.Value = sampleStart; end.Value = sampleEnd; enabled.Checked = true;
        Get<TextBox>(form, "m_txtSearch").Text = "Sequence Start";
        Call(form, "ShowAll");
        Task sampleTask = (Task)Call(form, "RefreshView"); Wait(delegate { return sampleTask.IsCompleted; }, "sample time range");
        List<LogEntry> expectedSample = originalEntries.FindAll(delegate(LogEntry row) { return row.Timestamp.HasValue && row.Timestamp.Value >= sampleStart && row.Timestamp.Value < sampleEnd.AddSeconds(1); });
        Assert(expectedSample.Count > 0 && expectedSample.Count < originalEntries.Count && Get<List<LogEntry>>(form, "m_visibleEntries").Count == expectedSample.Count, "actual sample filters to chosen seconds");
        Capture(form, Path.Combine(artifacts, "time-range-sample.png"));
        TreeView sampleSources = Get<TreeView>(form, "m_treeSources");
        sampleSources.SelectedNode = sampleSources.Nodes[0].Nodes[0];
        ComboBox kinds = Get<ComboBox>(form, "m_cmbKind");
        kinds.SelectedIndex = kinds.Items.IndexOf("COMM");
        sampleTask = (Task)Call(form, "RefreshView"); Wait(delegate { return sampleTask.IsCompleted; }, "time range with Task and kind");
        string selectedProcess = Get<string>(form, "m_strSelectedProcess");
        List<LogEntry> scopedSample = expectedSample.FindAll(delegate(LogEntry row) { return row.Process == selectedProcess && row.Kind == "COMM"; });
        int expectedSearch = scopedSample.FindAll(delegate(LogEntry row) { return row.Text.IndexOf("Sequence Start", StringComparison.OrdinalIgnoreCase) >= 0; }).Count;
        Assert(scopedSample.Count > 0 && Get<List<LogEntry>>(form, "m_visibleEntries").Count == scopedSample.Count && Get<List<LogEntry>>(form, "m_searchEntries").Count == expectedSearch, "time range combines with Task, kind and search");
        Call(form, "ShowAll"); kinds.SelectedIndex = 0; enabled.Checked = false;
        DateTime from = new DateTime(2026, 11, 16, 23, 59, 59);
        DateTime until = from.AddSeconds(1);
        List<LogEntry> entries = new List<LogEntry>();
        DateTime?[] times = new DateTime?[] { from.AddTicks(-1), from, from.AddMilliseconds(500), until, until.AddSeconds(1).AddTicks(-1), until.AddSeconds(1), null };
        foreach (DateTime? timestamp in times)
        {
            entries.Add(new LogEntry { Timestamp = timestamp, Process = originalEntries[0].Process, FilePath = originalEntries[0].FilePath, Kind = originalEntries[0].Kind, Text = "range event", LineNumber = entries.Count + 1 });
        }
        form.GetType().GetField("m_entries", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, entries);
        form.GetType().GetField("m_rules", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, new List<KeywordRule> { new KeywordRule { Text = "range event", Color = Color.SteelBlue } });
        Call(form, "BuildSourceTree", Get<List<LogEntry>>(form, "m_entries")); Call(form, "RebuildKeywordChips");
        Get<TextBox>(form, "m_txtSearch").Text = "range event";
        start.Value = from; end.Value = until; enabled.Checked = true;
        Task task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "time range midnight");
        List<LogEntry> visible = Get<List<LogEntry>>(form, "m_visibleEntries");
        Assert(visible.Count == 4 && Object.ReferenceEquals(visible[0], entries[1]) && Object.ReferenceEquals(visible[3], entries[4]), "time range includes both boundary seconds across midnight and excludes unknown times");
        Assert(Get<List<LogEntry>>(form, "m_searchEntries").Count == 4, "search uses the same time range");
        TimingChart chart = Get<TimingChart>(form, "m_chart");
        TimingData data = Get<TimingData>(chart, "m_data");
        Assert(data.EventCount == 4 && data.Minimum == from && data.Maximum == until.AddSeconds(1).AddTicks(-1), "chart events and axis use the selected time range");
        Assert((DateTime)Call(chart, "TimeAtOffset", (data.Maximum - data.Minimum).TotalSeconds) == data.Maximum, "chart labels preserve end-second precision");
        Call(form, "OpenViewerWindow", Get<TableLayoutPanel>(form, "m_panelTiming"));
        Form window = Get<Form>(form, "m_timingWindow");
        start.Value = until;
        task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "single second range");
        Assert(Get<List<LogEntry>>(form, "m_visibleEntries").Count == 2 && Get<TimingData>(chart, "m_data").EventCount == 2, "one-second range filters detached timing and main logs together");
        Capture(form, Path.Combine(artifacts, "time-range-main.png")); Capture(window, Path.Combine(artifacts, "time-range-chart.png"));
        start.Value = until.AddSeconds(1);
        task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "invalid time range");
        Assert(Get<Label>(form, "m_lblStatus").Text.Contains("시간 구간 오류") && Get<List<LogEntry>>(form, "m_visibleEntries").Count == 2, "reversed range reports error and preserves last valid results");
        end.Value = until.AddDays(1); start.Value = end.Value;
        task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "empty time range");
        Assert(Get<List<LogEntry>>(form, "m_visibleEntries").Count == 0 && Get<List<LogEntry>>(form, "m_searchEntries").Count == 0 && Get<TimingData>(chart, "m_data").EventCount == 0, "empty range clears all viewers");
        Get<Button>(form, "m_btnAllTimes").PerformClick();
        task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "all times");
        Assert(!enabled.Checked && Get<List<LogEntry>>(form, "m_visibleEntries").Count == 7, "all times restores entries including unknown timestamps");
        Size originalSize = form.Size;
        form.Size = form.MinimumSize; window.Close();
        SplitContainer split = Get<SplitContainer>(form, "m_splitTiming");
        Assert(!split.Panel1Collapsed && split.SplitterDistance >= split.Panel1MinSize && split.Panel2.Height >= split.Panel2MinSize, "chart restore after main resize respects both panel minimums");
        form.Size = originalSize; start.Value = sampleStart; end.Value = sampleEnd;
        form.GetType().GetField("m_entries", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, originalEntries);
        form.GetType().GetField("m_rules", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, originalRules);
        Get<TextBox>(form, "m_txtSearch").Clear();
        Call(form, "BuildSourceTree", Get<List<LogEntry>>(form, "m_entries")); Call(form, "RebuildKeywordChips");
        task = (Task)Call(form, "RefreshView"); Wait(delegate { return task.IsCompleted; }, "restore sample after time test");
    }

    // 자식 컨트롤에 배달된 Alt+방향키 메시지로 창별 배치, 반복 입력 차단, 복원과 최소화를 검증한다.
    private static void TestWindowPlacement(Control target)
    {
        const int SystemKeyDown = 0x0104, AltContext = 1 << 29, Repeated = 1 << 30;
        Form window = target.FindForm();
        window.Activate(); target.Focus(); Application.DoEvents();
        Rectangle original = window.Bounds;
        Rectangle area = Screen.FromControl(window).WorkingArea;
        int middle = area.Left + area.Width / 2;
        Action<Keys, int> press = delegate(Keys key, int flags)
        {
            PostMessage(target.Handle, SystemKeyDown, new IntPtr((int)key), new IntPtr(AltContext | flags | 1));
            Application.DoEvents();
        };
        press(Keys.Left, 0);
        Assert(window.Bounds == Rectangle.FromLTRB(area.Left, area.Top, middle, area.Bottom), window.Text + " snaps left: " + window.Bounds);
        press(Keys.Right, 0);
        Rectangle right = Rectangle.FromLTRB(middle, area.Top, area.Right, area.Bottom);
        Assert(window.Bounds == right, window.Text + " snaps right");
        press(Keys.Up, 0); Assert(window.WindowState == FormWindowState.Maximized, "maximize viewer");
        press(Keys.Down, 0); Assert(window.WindowState == FormWindowState.Normal && window.Bounds == right, "restore half from maximize");
        press(Keys.Down, Repeated); Assert(window.Bounds == right && window.WindowState == FormWindowState.Normal, "held arrow does not minimize");
        press(Keys.Down, 0); Assert(window.Bounds == original, window.Text + " restores original bounds");
        press(Keys.Down, 0); Assert(window.WindowState == FormWindowState.Minimized, "minimize normal viewer");
        window.WindowState = FormWindowState.Normal;
        window.Activate(); Application.DoEvents();
        Assert(window.Bounds == original, "restore minimized viewer");
        window.Size = window.MinimumSize;
        Application.DoEvents();
        Assert(window.HorizontalScroll.Visible && window.VerticalScroll.Visible &&
            window.Controls[0].Width >= window.AutoScrollMinSize.Width - window.Padding.Horizontal, "compact window preserves full workspace");
        window.AutoScrollPosition = new Point(window.AutoScrollMinSize.Width, window.AutoScrollMinSize.Height);
        Assert(window.AutoScrollPosition.X < 0 && window.AutoScrollPosition.Y < 0, "compact window scrolls to tools");
        window.AutoScrollPosition = Point.Empty;
        window.Bounds = original;
        Application.DoEvents();
    }

    // 기본 및 최소 창에서 버튼의 높이, 수직 중심과 부모 영역 안의 배치를 확인한다.
    private static void AssertToolbarLayout(MainForm form)
    {
        CheckBox task = Get<CheckBox>(form, "m_chkTaskColors");
        Button highlight = Get<Button>(form, "m_btnHighlights");
        Label status = Get<Label>(form, "m_lblStatus");
        Assert(status.Visible && status.Height >= status.Font.Height && status.Width >= 300, "status line is readable");
        Assert(task.Top == highlight.Top && task.Height == highlight.Height, "log action buttons align");
        foreach (string field in new string[] { "m_btnFolder", "m_btnReload", "m_btnCancel", "m_btnSettings", "m_btnKeywords", "m_btnPresets", "m_btnSaveKeywords", "m_btnPopoutLogs", "m_btnPopoutSearch", "m_btnPopoutTiming", "m_btnSearch" })
        {
            Button button = Get<Button>(form, field);
            if (field == "m_btnCancel" && !button.Visible) { continue; }
            Assert(button.Parent.ClientRectangle.Contains(button.Bounds), "button not clipped: " + field);
        }
        Assert(Get<Label>(form, "m_lblScope").Parent == highlight.Parent, "log tools and title share one row");
        Assert(Get<Label>(form, "m_lblSearchCount").Parent == Get<TextBox>(form, "m_txtSearch").Parent, "search field and result count share one row");
        Assert(Get<TextBox>(form, "m_txtSearch").Width >= 120, "search field fits at minimum width");
        TextBox folder = Get<TextBox>(form, "m_txtFolder");
        Button open = Get<Button>(form, "m_btnFolder");
        Assert(Math.Abs(folder.Top + folder.Height / 2.0 - open.Top - open.Height / 2.0) <= 1, "folder controls vertically centered");
        ComboBox preset = Get<ComboBox>(form, "m_cmbPreset");
        Button manage = Get<Button>(form, "m_btnPresets");
        Assert(Math.Abs(preset.Top + preset.Height / 2.0 - manage.Top - manage.Height / 2.0) <= 1, "preset controls vertically centered: " + preset.Top + "/" + preset.Height + " versus " + manage.Top + "/" + manage.Height);
    }

    // 읽기와 재계산이 완료될 때까지 실제 메시지 루프를 실행한다.
    private static void Wait(Func<bool> predicate, string phase, Func<string> diagnostic = null, int timeoutSeconds = 30)
    {
        Stopwatch watch = Stopwatch.StartNew();
        long previous = -1;
        do
        {
            Application.DoEvents(); Thread.Sleep(10);
            if (diagnostic != null && watch.ElapsedMilliseconds / 1000 != previous)
            { previous = watch.ElapsedMilliseconds / 1000; Console.WriteLine(phase + " " + previous + "s " + diagnostic()); }
            if (watch.Elapsed.TotalSeconds > timeoutSeconds) { throw new Exception("UI timeout: " + phase); }
        }
        while (!predicate());
    }

    // 프로덕션 코드에 테스트 전용 API를 추가하지 않고 컨트롤 상태를 읽는다.
    private static T Get<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    }

    // 손잡이를 추적하고 놓을 때 Windows가 보내는 위치를 각각 첫 표시 행과 비교한다.
    private static void TestThumbDrag(MainForm.VirtualLogList list)
    {
        const int VerticalScroll = 0x0115, ThumbTrack = 5, ThumbPosition = 4, EndScroll = 8;
        int target = list.VirtualListSize * 3 / 4;
        SendMessage(list.Handle, VerticalScroll, new IntPtr((target << 16) | ThumbTrack), IntPtr.Zero);
        Assert(list.TopItem.Index == target, "native thumb updates during tracking");
        target -= 10;
        SendMessage(list.Handle, VerticalScroll, new IntPtr((target << 16) | ThumbPosition), IntPtr.Zero);
        SendMessage(list.Handle, VerticalScroll, new IntPtr(EndScroll), IntPtr.Zero);
        Assert(list.TopItem.Index == target, "native thumb keeps released position");
    }

    // 실제 목록 창이 WM_PAINT 처리를 끝낸 뒤 화면에 표시된 위치를 기록한다.
    private sealed class PaintObserver : NativeWindow, IDisposable
    {
        private readonly Action m_record;

        // 테스트 중에만 해당 목록의 창 핸들을 관찰한다.
        public PaintObserver(Control view, Action record)
        {
            m_record = record;
            AssignHandle(view.Handle);
        }

        // 원래 그리기가 끝난 후에만 화면 상태를 읽는다.
        protected override void WndProc(ref Message message)
        {
            const int Paint = 0x000F;
            base.WndProc(ref message);
            if (message.Msg == Paint) { m_record(); }
        }

        // 테스트가 끝나면 원래 창 프로시저로 복원한다.
        public void Dispose() { ReleaseHandle(); }
    }

    // 테스트 창의 실제 클라이언트 영역을 다시 그리지 않고 그대로 복사한다.
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int left, int top, int operation);

    // 다른 창의 이동이나 창 열기 애니메이션을 제외하고 로그 셀의 픽셀만 읽는다.
    private static void CopyCellPixels(Control view, Rectangle cell, Graphics target)
    {
        const int SourceCopy = 0x00CC0020;
        using (Graphics source = view.CreateGraphics())
        {
            IntPtr from = source.GetHdc();
            IntPtr to = target.GetHdc();
            try { Assert(BitBlt(to, 0, 0, cell.Width, cell.Height, from, cell.Left, cell.Top, SourceCopy), "copy live log cell"); }
            finally { target.ReleaseHdc(to); source.ReleaseHdc(from); }
        }
    }

    // 마우스 진입과 이탈 직후 실제 창에 남은 로그 본문 픽셀을 다시 그리기 전과 비교한다.
    private static void TestHoverPaint(Control view, int index)
    {
        const int MouseLeave = 0x02A3;
        MainForm.VirtualLogList list = view as MainForm.VirtualLogList;
        Rectangle bounds = list == null ? ((DataGridView)view).GetCellDisplayRectangle(4, index, false) : list.Items[index].SubItems[4].Bounds;
        Rectangle cell = Rectangle.Intersect(view.ClientRectangle, bounds);
        cell.Width = Math.Min(240, cell.Width);
        Assert(cell.Width > 30 && cell.Height > 1, "hover test cell is visible");
        SendMessage(view.Handle, MouseLeave, IntPtr.Zero, IntPtr.Zero);
        view.Refresh(); Application.DoEvents();
        view.FindForm().Activate(); view.FindForm().Update();
        using (Bitmap before = new Bitmap(cell.Width, cell.Height))
        using (Bitmap after = new Bitmap(cell.Width, cell.Height))
        using (Graphics baseline = Graphics.FromImage(before))
        using (Graphics current = Graphics.FromImage(after))
        {
            CopyCellPixels(view, cell, baseline);
            bool hasText = false;
            for (int y = 1; y < cell.Height - 1 && !hasText; y++)
            {
                for (int x = 4; x < cell.Width; x++)
                { if (before.GetPixel(x, y) != before.GetPixel(2, y)) { hasText = true; break; } }
            }
            Assert(hasText, "hover baseline contains log text cell=" + cell + " bg=" + before.GetPixel(2, 1) + " expected=" + view.BackColor);
            IntPtr position = new IntPtr(((cell.Top + cell.Height / 2) << 16) | (cell.Left + 12));
            // 셀별 콜백이 생략된 Windows의 행 그리기를 재현하고 다음 WM_PAINT 전에 확인한다.
            if (list != null)
            {
                using (Graphics surface = list.CreateGraphics())
                {
                    surface.FillRectangle(Brushes.White, cell.Left + 4, cell.Top + 1, 30, cell.Height - 2);
                    ListViewItem item = list.Items[index];
                    DrawListViewItemEventArgs paint = new DrawListViewItemEventArgs(surface, item, list.GetItemRect(index), index,
                        item.Selected ? ListViewItemStates.Selected : 0);
                    Call(list, "OnDrawItem", paint);
                }
            }
            foreach (int message in new int[] { 0, MouseMove, MouseLeave, MouseMove, MouseLeave })
            {
                if (message != 0) { SendMessage(view.Handle, message, IntPtr.Zero, position); }
                CopyCellPixels(view, cell, current);
                for (int y = 0; y < cell.Height; y++)
                {
                    for (int x = 0; x < cell.Width; x++)
                    { Assert(before.GetPixel(x, y) == after.GetPixel(x, y), "hover preserves painted row at " + x + "," + y + " message=" + message + " from=" + before.GetPixel(x, y) + " to=" + after.GetPixel(x, y)); }
                }
            }
        }
    }

    // 검색 결과에서 실제로 드래그하고, 선택 밖 글자의 위치와 우클릭 팔레트 적용을 확인한다.
    private static void TestSearchDrag(MainForm form, Control view, Rectangle cell)
    {
        int y = cell.Top + cell.Height / 2;
        int left = cell.Left + 6;
        int right = left + 30;
        IntPtr anchor = new IntPtr((y << 16) | left);
        IntPtr caret = new IntPtr((y << 16) | right);
        view.Focus();
        SendMessage(view.Handle, MouseLeftDown, new IntPtr(1), anchor);
        using (Bitmap before = new Bitmap(view.Width, view.Height))
        using (Bitmap during = new Bitmap(view.Width, view.Height))
        {
            view.DrawToBitmap(before, view.ClientRectangle);
            SendMessage(view.Handle, MouseMove, new IntPtr(1), caret);
            view.DrawToBitmap(during, view.ClientRectangle);
            Rectangle area = Rectangle.Intersect(cell, view.ClientRectangle);
            bool hasText = false, selectionPainted = false;
            for (int x = left; x < area.Right - 5; x++)
            {
                for (int row = area.Top + 2; row < area.Bottom - 2; row++)
                {
                    Color original = before.GetPixel(x, row);
                    Color dragged = during.GetPixel(x, row);
                    if (x > right + 12)
                    {
                        Assert(original == dragged, "drag keeps remaining text pixels unchanged");
                        if (original != before.GetPixel(x, area.Top + 1)) { hasText = true; }
                    }
                    else if (original != dragged) { selectionPainted = true; }
                }
            }
            Assert(hasText && selectionPainted, "compared visible text and painted selection");
        }
        SendMessage(view.Handle, MouseLeftUp, IntPtr.Zero, caret);
        string selection = (string)Call(form, "SelectedLogText");
        Assert(!String.IsNullOrWhiteSpace(selection), "search selects text on release");
        SendMessage(view.Handle, MouseRightDown, new IntPtr(2), caret);
        SendMessage(view.Handle, MouseRightUp, IntPtr.Zero, caret);
        Application.DoEvents();
        ToolStripItem colorAction = view.ContextMenuStrip.Items["SelectedTextColor"];
        Assert(view.ContextMenuStrip.Visible && colorAction.Enabled && selection == (string)Call(form, "SelectedLogText"), "search right click preserves selected text");
        view.ContextMenuStrip.Close();
        AcceptHighlightColor(view.FindForm(), colorAction);
        Assert(Get<List<KeyValuePair<string, Color>>>(form, "m_highlights").Exists(
            delegate(KeyValuePair<string, Color> rule) { return rule.Key == selection && rule.Value.ToArgb() == Color.Yellow.ToArgb(); }),
            "search palette sets shared highlight");
    }

    // 선택 영역이 속한 시험 창의 색상 팔레트에서 기본 노란색을 확정한다.
    private static void AcceptHighlightColor(Form owner, ToolStripItem colorAction)
    {
        using (System.Windows.Forms.Timer acceptColor = new System.Windows.Forms.Timer { Interval = 100 })
        {
            acceptColor.Tick += delegate
            {
                Form root = owner.Owner ?? owner;
                IntPtr dialog = GetLastActivePopup(root.Handle);
                if (dialog == IntPtr.Zero || dialog == owner.Handle || dialog == root.Handle) { return; }
                acceptColor.Stop();
                PostMessage(dialog, 0x0111, new IntPtr(1), IntPtr.Zero);
            };
            acceptColor.Start(); colorAction.PerformClick();
        }
    }

    // 연속 휠 입력 도중 그려진 서로 다른 첫 행 수와 입력당 처리 시간을 측정한다.
    private static void TestWheelFrames(MainForm.VirtualLogList list, bool upward = false)
    {
        const int InputCount = 60;
        Point screen = list.PointToScreen(new Point(50, 60));
        IntPtr point = new IntPtr((screen.Y << 16) | (screen.X & 0xffff));
        int first = list.FirstEntry + list.TopItem.Index;
        HashSet<int> painted = new HashSet<int>();

        Stopwatch total = Stopwatch.StartNew();
        int sent = 0; int during = 0; long firstFrameMs = -1;
        Action onPaint = delegate
        {
            int top = list.FirstEntry + SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (top != first && painted.Add(top) && Volatile.Read(ref sent) < InputCount)
            {
                during++; if (firstFrameMs < 0) { firstFrameMs = total.ElapsedMilliseconds; }
            }
        };
        using (PaintObserver observer = new PaintObserver(list, onPaint))
        {
            IntPtr handle = list.Handle;
            long lastPostedMs = 0;
            Task sender = Task.Run(delegate
            {
                for (int index = 0; index < InputCount; index++)
                {
                    Interlocked.Increment(ref sent);
                    PostMessage(handle, 0x020a, new IntPtr((upward ? 120 : -120) << 16), point);
                    if (index + 1 < InputCount) { Thread.Sleep(8); }
                }
                lastPostedMs = total.ElapsedMilliseconds;
            });
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines < 0) { lines = SendMessage(handle, 0x1028, IntPtr.Zero, IntPtr.Zero).ToInt32(); }
            int expected = first + (upward ? -lines : lines) * InputCount;
            Wait(delegate { return sender.IsCompleted && list.FirstEntry + list.TopItem.Index == expected; }, "queued wheel reaches expected row", timeoutSeconds: 10);
            Assert(during >= 3, "intermediate display frames during wheel input: " + during);
            Console.WriteLine("PASS wheel burst inputs=" + InputCount + " framesDuringInput=" + during + " from=" + first + " to=" + expected + " firstFrameMs=" + firstFrameMs + " afterLastInputMs=" + (total.ElapsedMilliseconds - lastPostedMs));
        }
    }
    private const int MouseMove = 0x0200;
    private const int MouseLeftDown = 0x0201;
    private const int MouseLeftUp = 0x0202;
    private const int MouseRightDown = 0x0204;
    private const int MouseRightUp = 0x0205;

    // 테스트 창의 실제 Windows 메시지 경로로 마우스 입력을 전달한다.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr buttons, IntPtr position);

    // 스크롤바 및 마우스 버튼의 동기 입력 경로를 검증한다.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr buttons, IntPtr position);

    // 동일 컨트롤 핸들러를 통합 테스트에서 실행한다.
    private static object Call(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
    }

    // 실제 렌더링 결과를 시각 검증용 아티팩트로 저장한다.
    private static void Capture(Form form, string path)
    {
        Application.DoEvents();
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path, ImageFormat.Png); }
    }

    // 행 수와 조작 결과를 독립 예상값으로 검증한다.
    private static void Assert(bool condition, string name)
    {
        if (!condition) { throw new Exception(name); }
    }
}
