using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using LogAnalyzer;

internal static class Program
{
    private static int m_nFailed;
    private static string m_strRoot;

    // 고립된 임시 폴더에서 실제 파일 입출력과 파서를 검증한다.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--generate-gib") { GenerateGigabyteLogs(args[1]); return 0; }
        if (args.Length > 1 && args[0] == "--ui-large") { return UiTests.Run(args[1], true, args.Length > 2 ? Int32.Parse(args[2]) : 1000000, args.Length > 3 && args[3] == "--stress"); }
        if (args.Length > 0 && args[0] == "--ui") { return UiTests.Run(args.Length > 1 ? args[1] : null); }
        m_strRoot = Path.Combine(Path.GetTempPath(), "LogAnalyzerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_strRoot);
        try
        {
            Run("mixed timestamps / raw preservation / process identity", TestFormats);
            Run("custom timestamp / process mapping", TestCustom);
            Run("display body excludes parsed timestamp and preserves raw text", TestDisplayBody);
            Run("disabled process rule and offset-only base date", TestParserBoundaries);
            Run("UTF-8 and CP949 Korean", TestEncoding);
            Run("corrupted BOM is reported", TestBadBom);
            Run("timeline keyword lanes", TestTimeline);
            Run("interval median / short / long / zero gaps", TestIntervalAnomalies);
            Run("interval source isolation and sparse data", TestIntervalSources);
            Run("fixed interval / tolerance boundaries / disabled", TestFixedIntervals);
            Run("interval settings INI compatibility", TestIntervalIni);
            Run("numbered links / global FIFO / independent thresholds", TestKeywordPairs);
            Run("linked anomaly recovery stays local", TestPairRecovery);
            Run("numbered link INI / migration / validation", TestPairIni);
            Run("search case / regex / invalid regex", TestSearch);
            Run("keyword INI unicode and special characters", TestKeywords);
            Run("invalid keyword file rejected", TestInvalidIni);
            Run("preset names and selection persistence", TestPresetNames);
            Run("invalid highlight INI rejected", TestInvalidHighlightIni);
            Run("highlight INI roundtrip and legacy compatibility", TestHighlightIni);
            Run("cancellation", TestCancellation);
            Run("range export original text / boundaries / atomic cancel", TestRangeExport);
            string sample = Environment.GetEnvironmentVariable("LOGANALYZER_SAMPLE_ZIP");
            if (!String.IsNullOrEmpty(sample)) { Run("sample 43312 lines", delegate { TestSample(sample); }); }
            Console.WriteLine(m_nFailed == 0 ? "ALL TESTS PASSED" : "FAILED: " + m_nFailed);
            return m_nFailed == 0 ? 0 : 1;
        }
        finally { Directory.Delete(m_strRoot, true); }
    }

    // 새 전용 폴더에 동일한 600만 행을 단일 파일과 8개 파일로 생성한다. 기존 폴더는 덮어쓰지 않는다.
    private static void GenerateGigabyteLogs(string folder)
    {
        const int RowCount = 6000000;
        const int FileCount = 8;
        const int BytesPerLine = 192;
        const int TimePermutation = 1000003;
        if (Directory.Exists(folder)) { throw new IOException("생성 대상 폴더가 이미 존재합니다."); }
        string singleFolder = Path.Combine(folder, "single");
        string multiFolder = Path.Combine(folder, "multi");
        Directory.CreateDirectory(singleFolder); Directory.CreateDirectory(multiFolder);
        UTF8Encoding encoding = new UTF8Encoding(true);
        List<StreamWriter> writers = new List<StreamWriter>();
        DateTime first = new DateTime(2026, 11, 18, 0, 0, 0);
        try
        {
            writers.Add(new StreamWriter(Path.Combine(singleFolder, "Process0_MAIN.log"), false, encoding, 65536));
            for (int index = 0; index < FileCount; index++)
            {
                string processFolder = Path.Combine(multiFolder, "Process" + index);
                Directory.CreateDirectory(processFolder);
                writers.Add(new StreamWriter(Path.Combine(processFolder, "Process" + index + "_MAIN.log"), false, encoding, 65536));
            }
            for (int index = 0; index < RowCount; index++)
            {
                long sequence = (long)index * TimePermutation % RowCount;
                string line = first.AddMilliseconds(sequence).ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                    + " [INFO] KW" + (sequence % 30).ToString("D2") + "| 검사 완료 ID=" + sequence.ToString("D7") + " ";
                line += new string('X', BytesPerLine - 2 - encoding.GetByteCount(line));
                writers[0].WriteLine(line); writers[1 + index % FileCount].WriteLine(line);
                if ((index + 1) % 1000000 == 0) { Console.WriteLine("GENERATED " + (index + 1) + " rows in both datasets"); }
            }
        }
        finally { foreach (StreamWriter writer in writers) { writer.Dispose(); } }
        Console.WriteLine("GENERATED single_bytes=" + new FileInfo(Path.Combine(singleFolder, "Process0_MAIN.log")).Length
            + " multi_bytes=" + Directory.GetFiles(multiFolder, "*.log", SearchOption.AllDirectories).Sum(delegate(string file) { return new FileInfo(file).Length; }));
    }

    // 프리셋 경로 탈출과 예약 이름을 거부하고 한글 이름 및 마지막 선택을 보존한다.
    private static void TestPresetNames()
    {
        string folder = Folder("presets");
        string path = KeywordSettings.PresetPath(folder, "프로그램 A");
        Assert(Path.GetDirectoryName(path) == folder && Path.GetFileName(path) == "프로그램 A.ini", "named preset remains inside folder");
        foreach (string name in new string[] { "", "..", "../escape", "..\\escape", "C:\\outside", "name.", "name ", "CON", "aux.log", "LPT1", "a:b", new string('x', 81) })
        {
            bool rejected = false;
            try { KeywordSettings.PresetPath(folder, name); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "unsafe name rejected: " + name);
        }
        string settingsPath = Path.Combine(folder, "settings.ini");
        new AppSettings { SelectedPreset = "프로그램 A" }.Save(settingsPath);
        Assert(AppSettings.Load(settingsPath).SelectedPreset == "프로그램 A", "selected program roundtrip");
    }

    // 강조 목록이 손상된 프리셋을 정상 키워드 파일로 오인하지 않는다.
    private static void TestInvalidHighlightIni()
    {
        string path = Path.Combine(m_strRoot, "bad-highlights.ini");
        KeywordSettings.Save(path, new List<KeywordRule>());
        IniFile ini = IniFile.Load(path);
        ini.Set("TextHighlights", "Count", "-1"); ini.Save(path);
        bool rejected = false;
        try { KeywordSettings.Load(path); } catch (FormatException) { rejected = true; }
        Assert(rejected, "negative highlight count rejected");
    }

    // 강조 문자열·색상·우선순위와 이전 INI 호환, 실패 시 기존 목록 보존을 검증한다.
    private static void TestHighlightIni()
    {
        string path = Path.Combine(m_strRoot, "highlights.ini");
        string text = " [검사]=Start;#\\\" ";
        List<KeyValuePair<string, Color>> highlights = new List<KeyValuePair<string, Color>>
        {
            new KeyValuePair<string, Color>(text, Color.FromArgb(12, 34, 56)),
            new KeyValuePair<string, Color>("Start", Color.Yellow)
        };
        KeywordSettings.Save(path, new List<KeywordRule> { new KeywordRule { Text = "cycle" } }, highlights);
        List<KeyValuePair<string, Color>> loaded = new List<KeyValuePair<string, Color>>();
        Assert(KeywordSettings.Load(path, loaded).Count == 1 && loaded.Count == 2, "keywords and highlights roundtrip together");
        Assert(loaded[0].Key == text && loaded[0].Value.ToArgb() == highlights[0].Value.ToArgb() && loaded[1].Key == "Start", "text color and order preserved");
        IniFile ini = IniFile.Load(path);
        ini.Set("Highlight1", "Color", "invalid-color"); ini.Save(path);
        bool rejected = false;
        try { KeywordSettings.Load(path, loaded); } catch (FormatException) { rejected = true; }
        Assert(rejected && loaded.Count == 2 && loaded[1].Value.ToArgb() == Color.Yellow.ToArgb(), "bad highlight cannot partially replace existing values");
        File.WriteAllText(path, "[TimingKeywords]\r\nVersion=1\r\nCount=0\r\n", new UTF8Encoding(true));
        KeywordSettings.Load(path, loaded);
        Assert(loaded.Count == 0, "legacy preset clears previous highlights");
        KeywordSettings.Save(path, new List<KeywordRule>(), highlights);
        byte[] before = File.ReadAllBytes(path);
        highlights.Add(new KeyValuePair<string, Color>("", Color.Transparent));
        rejected = false;
        try { KeywordSettings.Save(path, new List<KeywordRule>(), highlights); } catch (ArgumentException) { rejected = true; }
        Assert(rejected && before.SequenceEqual(File.ReadAllBytes(path)), "invalid save preserves file");
    }

    // 표시용 본문은 파서가 인식한 시각만 제외하고 다른 시각과 원문을 보존한다.
    private static void TestDisplayBody()
    {
        string folder = Folder("display-body");
        string[] lines = new string[]
        {
            "2026-11-16 10:20:30.123 [INFO] next=11:22:33 payload=2026-11-17",
            "[2026-11-16T10:20:31.456] [INFO] ready",
            "prefix (2026/11/16 10:20:32,789) done",
            "2026-13-99 10:20:30 invalid",
            "plain continuation",
            "10:20:33 time-only"
        };
        File.WriteAllLines(Path.Combine(folder, "display.log"), lines, new UTF8Encoding(true));
        LogReadResult result = LogReader.ReadFolder(folder, new LogOptions { BaseDate = new DateTime(2026, 11, 16) }, CancellationToken.None, null);
        string[] expected = new string[] { "[INFO] next=11:22:33 payload=2026-11-17", "[INFO] ready", "prefix done", lines[3], lines[4], "time-only" };
        foreach (LogEntry entry in result.Entries)
        {
            Assert(entry.Text == lines[entry.LineNumber - 1], "raw log preserved");
            Assert(entry.DisplayText == expected[entry.LineNumber - 1], "only extracted timestamp removed: " + entry.LineNumber);
        }
        string custom = Folder("display-custom");
        File.WriteAllText(Path.Combine(custom, "custom.log"), "at=16.11.2026|12:34:56,789;msg=완료", new UTF8Encoding(true));
        LogOptions options = new LogOptions { TimestampPattern = "at=(?<time>[^;]+)", TimestampFormats = "dd.MM.yyyy'|'HH:mm:ss,fff" };
        LogEntry customEntry = LogReader.ReadFolder(custom, options, CancellationToken.None, null).Entries[0];
        Assert(customEntry.DisplayText == "at=;msg=완료", "custom group removed without guessing other fields");
        Assert(LogReader.Search(result.Entries, "2026-11-16", false, false, CancellationToken.None).Count == 2, "raw timestamp search remains available");
    }

    // 각 시나리오의 실패를 독립적으로 수집한다.
    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { m_nFailed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    // 실제 관측값을 독립된 기대값과 비교한다.
    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }

    // 테스트별 파일 경로를 생성한다.
    private static string Folder(string name)
    {
        string path = Path.Combine(m_strRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // 정렬, 원문 보존, 여러 프로세스의 구분을 검증한다.
    private static void TestFormats()
    {
        string folder = Folder("formats");
        File.WriteAllLines(Path.Combine(folder, "Worker_1_MAIN.log"), new string[] {
            "2026-11-16 10:00:02.123 [INFO] second", "stack trace without timestamp", "2026/11/16 10:00:01.500 first" });
        File.WriteAllText(Path.Combine(folder, "Worker_2_COMM.log"), "10:00:03.050 time only");
        File.WriteAllText(Path.Combine(folder, "empty.log"), "");
        LogReadResult result = LogReader.ReadFolder(folder, new LogOptions { BaseDate = new DateTime(2026, 11, 16) }, CancellationToken.None, null);
        Assert(result.Entries.Count == 4, "all physical lines retained");
        Assert(result.FileCount == 3, "empty files counted");
        Assert(result.Entries[0].Text.EndsWith("first"), "chronological sort");
        Assert(result.Entries[1].Timestamp.Value.Millisecond == 123, "millisecond precision");
        Assert(result.Entries[2].Timestamp == new DateTime(2026, 11, 16, 10, 0, 3, 50), "time-only base date");
        Assert(!result.Entries[3].Timestamp.HasValue && result.Entries[3].LineNumber == 2, "unknown line retained last");
        Assert(result.Entries[0].Process != result.Entries[2].Process, "different process instances");
        Assert(result.Entries[0].Kind == "MAIN", "file kind extracted");
    }

    // 기본 샘플과 다른 날짜/파일 형식을 설정으로 처리한다.
    private static void TestCustom()
    {
        string folder = Folder("custom");
        File.WriteAllText(Path.Combine(folder, "alpha-events.txt"), "at=16.11.2026|12:34:56,789;msg=완료");
        LogOptions options = new LogOptions { TimestampPattern = @"at=(?<time>[^;]+)", TimestampFormats = "dd.MM.yyyy'|'HH:mm:ss,fff", ProcessPattern = @"^(?<process>[^-]+)-(?<kind>.+)$" };
        LogReadResult result = LogReader.ReadFolder(folder, options, CancellationToken.None, null);
        Assert(result.Entries.Count == 1 && result.Entries[0].Timestamp == new DateTime(2026, 11, 16, 12, 34, 56, 789), "custom exact format");
        Assert(result.Entries[0].Process.EndsWith("alpha") && result.Entries[0].Kind == "events", "custom file pattern");
    }

    // 두 가지 흔한 인코딩의 한글 원문을 확인한다.
    private static void TestEncoding()
    {
        string folder = Folder("encoding");
        File.WriteAllText(Path.Combine(folder, "utf.log"), "2026-11-16 12:00:00.000 한글 UTF", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(folder, "ansi.log"), "2026-11-16 12:00:00.000 한글 ANSI", Encoding.GetEncoding(949));
        LogReadResult result = LogReader.ReadFolder(folder, new LogOptions(), CancellationToken.None, null);
        Assert(result.Entries.Count == 2 && result.Entries.All(delegate(LogEntry row) { return row.Text.Contains("한글"); }), "Korean not corrupted");
    }

    // 사용하지 않는 규칙과 시간대만 있는 로그에서도 사용자 설정을 지킨다.
    private static void TestParserBoundaries()
    {
        string folder = Folder("boundaries");
        File.WriteAllText(Path.Combine(folder, "source.log"), "12:34:56Z offset");
        LogOptions options = new LogOptions { GroupByFolder = true, ProcessPattern = "[", BaseDate = new DateTime(2000, 1, 1) };
        LogReadResult result = LogReader.ReadFolder(folder, options, CancellationToken.None, null);
        DateTime expected = new DateTime(2000, 1, 1, 12, 34, 56, DateTimeKind.Utc).ToLocalTime();
        Assert(result.Entries.Count == 1 && result.Entries[0].Timestamp == expected, "disabled rule ignored and offset uses base date");
    }

    // BOM이 있는 손상 파일을 정상 원문으로 오인하지 않는다.
    private static void TestBadBom()
    {
        string folder = Folder("bad-bom");
        File.WriteAllBytes(Path.Combine(folder, "broken.log"), new byte[] { 0xEF, 0xBB, 0xBF, 0x31, 0x32, 0x3A, 0x30, 0x30, 0x3A, 0x30, 0x30, 0x20, 0xFF });
        LogReadResult result = LogReader.ReadFolder(folder, new LogOptions(), CancellationToken.None, null);
        Assert(result.Entries.Count == 0 && result.Warnings.Count == 1, "invalid BOM content must not silently replace bytes");
    }

    // 시간 없는 행과 비활성 키워드를 제외하고 동일 시각 이벤트도 보존한다.
    private static void TestTimeline()
    {
        DateTime time = new DateTime(2026, 11, 16, 12, 0, 0, 123);
        List<LogEntry> entries = new List<LogEntry> { new LogEntry { Timestamp = time, Text = "start" }, new LogEntry { Timestamp = time, Text = "Start" }, new LogEntry { Text = "start" } };
        List<KeywordRule> rules = new List<KeywordRule> { new KeywordRule { Text = "start" }, new KeywordRule { Text = "Start", CaseSensitive = true }, new KeywordRule { Text = "start", Enabled = false } };
        TimingData data = TimingData.Build(entries, rules, CancellationToken.None);
        Assert(data.Lanes.Count == 2 && data.EventCount == 3, "active lanes and matching events");
        Assert(data.Lanes[0].Entries.Count == 2 && data.Lanes[1].Entries.Count == 1, "duplicates and case sensitivity");
        Assert(data.Minimum == time && data.Maximum > data.Minimum, "single instant can be plotted");
    }

    // 정상 1초 간격 사이의 짧음, 길음, 동시 기록을 놓치지 않는지 검증한다.
    private static void TestIntervalAnomalies()
    {
        List<LogEntry> rows = IntervalRows("A", "MAIN", new int[] { 0, 1000, 2000, 3000, 4000, 5000, 5100, 8100, 8100 });
        TimingData data = TimingData.Build(rows, new List<KeywordRule> { new KeywordRule { Text = "cycle" } }, CancellationToken.None);
        Assert(data.AnomalyCount == 3, "100ms, 3000ms and 0ms must be flagged");
        Assert(data.Lanes[0].Anomalies.All(delegate(TimingInterval gap) { return gap.ExpectedMs == 1000; }), "median is unaffected by extremes");
        Assert(data.Lanes[0].Anomalies[0].ActualMs == 100 && data.Lanes[0].Anomalies[1].ActualMs == 3000 && data.Lanes[0].Anomalies[2].ActualMs == 0, "actual interval values");
        Assert(data.Lanes[0].AbnormalEntries.Contains(rows[5]) && data.Lanes[0].AbnormalEntries.Contains(rows[8]), "both boundaries are red");
    }

    // 다른 프로세스/종류를 섞지 않고 자동 추정 표본이 부족하면 판정하지 않는다.
    private static void TestIntervalSources()
    {
        List<LogEntry> rows = IntervalRows("A", "MAIN", new int[] { 0, 1000, 2000, 3000, 4000, 5000 });
        rows.AddRange(IntervalRows("B", "MAIN", new int[] { 100, 2100, 4100, 6100, 8100, 10100 }));
        rows.AddRange(IntervalRows("A", "TACT", new int[] { 1, 11, 21, 31, 41, 51 }));
        rows.Sort(delegate(LogEntry left, LogEntry right) { return left.Timestamp.Value.CompareTo(right.Timestamp.Value); });
        List<KeywordRule> rules = new List<KeywordRule> { new KeywordRule { Text = "cycle" } };
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 0, "independent periodic streams stay normal");
        Assert(TimingData.Build(IntervalRows("A", "MAIN", new int[] { 0, 1000, 1100, 9000 }), rules, CancellationToken.None).AnomalyCount == 0, "insufficient positive gaps");
        Assert(TimingData.Build(IntervalRows("A", "MAIN", new int[] { 0, 0, 0, 0, 0, 0 }), rules, CancellationToken.None).AnomalyCount == 0, "all simultaneous cannot define baseline");
    }

    // 허용 경계값은 정상이며 지정 주기는 소수 표본에도 사용한다.
    private static void TestFixedIntervals()
    {
        List<LogEntry> rows = IntervalRows("A", "MAIN", new int[] { 0, 500, 2000, 2499, 4000 });
        KeywordRule rule = new KeywordRule { Text = "cycle", ExpectedIntervalMs = 1000, TolerancePercent = 50 };
        TimingData data = TimingData.Build(rows, new List<KeywordRule> { rule }, CancellationToken.None);
        Assert(data.AnomalyCount == 2 && data.Lanes[0].Anomalies[0].ActualMs == 499 && data.Lanes[0].Anomalies[1].ActualMs == 1501, "strict tolerance bounds");
        rule.CheckIntervals = false;
        Assert(TimingData.Build(rows, new List<KeywordRule> { rule }, CancellationToken.None).AnomalyCount == 0, "disabled check");
    }

    // 양 끝 로그와 원문을 보존하고 취소/원본 덮어쓰기 시 기존 파일을 지킨다.
    private static void TestRangeExport()
    {
        string path = Path.Combine(m_strRoot, "export.log");
        DateTime time = new DateTime(2026, 11, 17, 12, 0, 0);
        List<LogEntry> rows = new List<LogEntry>();
        for (int index = 0; index < 4; index++)
        { rows.Add(new LogEntry { Timestamp = time.AddMilliseconds(index), Text = "원문 " + index, DisplayText = "표시 " + index }); }
        rows.Add(new LogEntry { Text = "시간 미인식" });
        Assert(LogReader.ExportRange(path, rows, time.AddMilliseconds(1), time.AddMilliseconds(2), CancellationToken.None) == 2, "both exact boundaries included");
        Assert(File.ReadAllText(path, Encoding.UTF8) == "원문 1\r\n원문 2\r\n", "raw text exported without display substitution");
        using (CancellationTokenSource source = new CancellationTokenSource())
        {
            source.Cancel(); bool canceled = false;
            try { LogReader.ExportRange(path, rows, time, time.AddSeconds(1), source.Token); } catch (OperationCanceledException) { canceled = true; }
            Assert(canceled && File.ReadAllText(path, Encoding.UTF8).StartsWith("원문 1"), "cancel preserves destination");
        }
        List<LogEntry> large = new List<LogEntry>(100000);
        LogEntry repeated = new LogEntry { Timestamp = time, Text = new string('x', 1024) };
        for (int index = 0; index < 100000; index++) { large.Add(repeated); }
        using (CancellationTokenSource source = new CancellationTokenSource())
        {
            source.CancelAfter(5); bool canceled = false;
            try { LogReader.ExportRange(path, large, time, time, source.Token); } catch (OperationCanceledException) { canceled = true; }
            Assert(canceled && File.ReadAllText(path, Encoding.UTF8).StartsWith("원문 1"), "large export cancellation preserves destination");
            Assert(Directory.GetFiles(m_strRoot, "export.log.*.tmp").Length == 0, "temporary export cleaned after cancellation");
        }
        rows[0].FilePath = path; bool rejected = false;
        try { LogReader.ExportRange(path, rows, time, time, CancellationToken.None); } catch (IOException) { rejected = true; }
        Assert(rejected && File.ReadAllText(path, Encoding.UTF8).StartsWith("원문 1"), "loaded source cannot be overwritten");
        string emptySource = Path.Combine(Folder("export-source"), "empty.log");
        File.WriteAllText(emptySource, "");
        LogReadResult loaded = LogReader.ReadFolder(Path.GetDirectoryName(emptySource), new LogOptions(), CancellationToken.None, null);
        Assert(loaded.FileCount == 1 && loaded.Entries.Count == 0 && loaded.SourceFiles.Count == 1, "empty source path retained");
        rejected = false;
        try { LogReader.ExportRange(emptySource, rows, time, time.AddSeconds(1), CancellationToken.None, loaded.SourceFiles); } catch (IOException) { rejected = true; }
        Assert(rejected && new FileInfo(emptySource).Length == 0, "empty input file also protected from export overwrite");
    }

    // 등록 번호로 다른 Task의 키워드를 FIFO 연결하고 반복/연동 기준을 독립 검사한다.
    private static void TestKeywordPairs()
    {
        DateTime time = new DateTime(2026, 11, 17, 12, 0, 0);
        List<LogEntry> rows = IntervalRows("Task1", "MAIN", new int[] { 0, 100, 200, 500, 800, 900 });
        string[] texts = new string[] { "B", "A", "A", "B", "B", "A" };
        for (int index = 0; index < rows.Count; index++)
        { rows[index].Text = texts[index]; if (texts[index] == "B") { rows[index].Process = "Task2"; rows[index].Kind = "COMM"; } }
        KeywordRule rule = new KeywordRule { Text = "A", CheckIntervals = false, LinkedKeywordNumber = 2, LinkedExpectedIntervalMs = 400, LinkedTolerancePercent = 0 };
        KeywordRule target = new KeywordRule { Text = "B", CheckIntervals = false };
        List<KeywordRule> rules = new List<KeywordRule> { rule, target };
        TimingData data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 1 && data.Lanes[0].Anomalies[0].Start == rows[2] && data.Lanes[0].Anomalies[0].End == rows[4] && data.Lanes[0].Anomalies[0].ActualMs == 600, "FIFO ignores Task/kind and consumes each A/B once");
        Assert(data.Lanes[1].AbnormalEntries.Contains(rows[4]) && data.Lanes[0].Number == 1 && data.Lanes[1].Number == 2, "both linked log markers use matching registered numbers");
        rows = IntervalRows("P", "MAIN", new int[] { 0, 1000, 1100, 2000 });
        for (int index = 0; index < rows.Count; index++) { rows[index].Text = index < 2 ? "A" : "B"; }
        rule.CheckIntervals = true; rule.ExpectedIntervalMs = 1000; rule.TolerancePercent = 0;
        rule.LinkedExpectedIntervalMs = 1000; rule.LinkedTolerancePercent = 0;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 1, "linked threshold flags 1100 while periodic 1000 remains normal");
        rule.ExpectedIntervalMs = 2000;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 2, "changing periodic threshold does not change linked threshold");
        rule.LinkedTolerancePercent = 10;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 1, "linked tolerance does not change periodic tolerance");
        rule.CheckIntervals = false; rule.LinkedExpectedIntervalMs = 0; rule.LinkedTolerancePercent = 50; rows.Clear();
        for (int index = 0; index < 6; index++)
        {
            rows.Add(new LogEntry { Timestamp = time.AddSeconds(index), Text = "A", Process = "P1", Kind = "MAIN" });
            rows.Add(new LogEntry { Timestamp = time.AddSeconds(index).AddMilliseconds(index == 5 ? 400 : 100), Text = "B", Process = "P2", Kind = "COMM" });
        }
        data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 1 && data.Lanes[0].Anomalies[0].ExpectedMs == 100, "linked automatic baseline uses integrated completed pairs");
        rule.LinkedExpectedIntervalMs = 100; rows = new List<LogEntry> { new LogEntry { Timestamp = time, Text = "AB" } };
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 0, "same row never links to itself");
        rows.Add(new LogEntry { Timestamp = time, Text = "b" }); target.CaseSensitive = true;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 0, "target keyword case setting is respected");
        target.CaseSensitive = false;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 1, "separate simultaneous rows can form zero-duration link");
        target.Enabled = false;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 1, "hidden target remains available as registered link");
        rule.LinkedKeywordNumber = 0;
        Assert(TimingData.Build(rows, rules, CancellationToken.None).AnomalyCount == 0, "none disables linked comparison");
    }

    // 종료 로그 누락 뒤의 정상 연결을 복구하고 실제 연속 이상은 숨기지 않는다.
    private static void TestPairRecovery()
    {
        KeywordRule start = new KeywordRule { Text = "A", CheckIntervals = false, LinkedKeywordNumber = 2, LinkedExpectedIntervalMs = 100, LinkedTolerancePercent = 20 };
        KeywordRule end = new KeywordRule { Text = "B", CheckIntervals = false };
        List<KeywordRule> rules = new List<KeywordRule> { start, end };
        List<LogEntry> rows = IntervalRows("Task1", "MAIN", new int[] { 0, 100, 1000, 2000, 2100, 3000, 3100 });
        string[] texts = new string[] { "A", "B", "A", "A", "B", "A", "B" };
        for (int index = 0; index < rows.Count; index++)
        { rows[index].Text = texts[index]; if (texts[index] == "B") { rows[index].Process = "Task2"; } }
        TimingData data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 1, "one missing endpoint must not shift every subsequent pair");
        Assert(data.Lanes[0].Anomalies[0].Start == rows[2] && data.Lanes[0].Anomalies[0].End == rows[2], "unmatched log is a local marker, not a band through later normal cycles");
        Assert(data.Lanes[0].AbnormalEntries.Count == 1 && data.Lanes[1].AbnormalEntries.Count == 0, "recovered normal starts and ends retain their keyword colors");
        start.LinkedExpectedIntervalMs = 0;
        for (int index = 4; index < 12; index++)
        {
            rows.Add(new LogEntry { Timestamp = rows[0].Timestamp.Value.AddSeconds(index), Text = "A" });
            rows.Add(new LogEntry { Timestamp = rows[0].Timestamp.Value.AddSeconds(index).AddMilliseconds(100), Text = "B" });
        }
        data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 1 && data.Lanes[0].Anomalies[0].Start == rows[2] && data.Lanes[0].Anomalies[0].MissingEnd && data.Lanes[0].Anomalies[0].ExpectedMs == 100, "automatic baseline also recovers after one missing endpoint");
        rows.Clear();
        DateTime first = new DateTime(2026, 11, 17, 12, 0, 0);
        for (int index = 0; index < 12; index++)
        {
            rows.Add(new LogEntry { Timestamp = first.AddSeconds(index), Text = "A" });
            if (index != 1 && index != 10) { rows.Add(new LogEntry { Timestamp = first.AddSeconds(index).AddMilliseconds(100), Text = "B" }); }
        }
        data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 2 && data.Lanes[0].Anomalies.TrueForAll(delegate(TimingInterval pair) { return pair.MissingEnd && pair.ExpectedMs == 100; }), "automatic baseline and recovery tolerate multiple missing endpoints");
        start.LinkedExpectedIntervalMs = 100;
        rows = IntervalRows("P", "MAIN", new int[] { 0, 300, 1000, 1100, 2000, 2300, 3000, 3100 });
        for (int index = 0; index < rows.Count; index++) { rows[index].Text = index % 2 == 0 ? "A" : "B"; }
        data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 2 && data.Lanes[0].Anomalies[0].End == rows[1] && data.Lanes[0].Anomalies[1].End == rows[5], "real delayed intervals remain red with exact endpoints");
        Assert(!data.Lanes[0].AbnormalEntries.Contains(rows[2]) && !data.Lanes[1].AbnormalEntries.Contains(rows[3]), "normal interval between anomalies remains normal");
        rows = IntervalRows("P", "MAIN", new int[] { 0, 100, 200, 300 });
        for (int index = 0; index < rows.Count; index++) { rows[index].Text = index < 2 ? "A" : "B"; }
        data = TimingData.Build(rows, rules, CancellationToken.None);
        Assert(data.AnomalyCount == 2 && data.Lanes[0].Anomalies.TrueForAll(delegate(TimingInterval pair) { return pair.ActualMs == 200; }), "balanced overlapping FIFO delays must not be discarded as missing endpoints");

    }

    // 순번/별도 기준을 왕복하고 이전 종료 문자열을 등록된 순번으로 변환한다.
    private static void TestPairIni()
    {
        string path = Path.Combine(m_strRoot, "pair.ini");
        KeywordRule rule = new KeywordRule { Text = "START", LinkedKeywordNumber = 2, ExpectedIntervalMs = 9000, TolerancePercent = 5, LinkedExpectedIntervalMs = 500, LinkedTolerancePercent = 20 };
        List<KeywordRule> rules = new List<KeywordRule> { rule, new KeywordRule { Text = "END" } };
        KeywordSettings.Save(path, rules);
        KeywordRule loaded = KeywordSettings.Load(path)[0];
        Assert(loaded.LinkedKeywordNumber == 2 && loaded.LinkedExpectedIntervalMs == 500 && loaded.ExpectedIntervalMs == 9000 && loaded.LinkedTolerancePercent == 20 && loaded.TolerancePercent == 5, "link and periodic settings roundtrip independently");
        rule.LinkedKeywordNumber = 9; bool rejected = false;
        try { KeywordSettings.Save(path, rules); } catch (FormatException) { rejected = true; }
        Assert(rejected && KeywordSettings.Load(path)[0].LinkedKeywordNumber == 2, "invalid registered number cannot damage preset");
        rule.LinkedKeywordNumber = 1; rejected = false;
        try { KeywordSettings.ValidateLinks(rules); } catch (FormatException) { rejected = true; }
        Assert(rejected, "self number rejected");
        rule.LinkedKeywordNumber = 2; rule.LinkedExpectedIntervalMs = Double.NaN; rejected = false;
        try { KeywordSettings.Save(path, rules); } catch (FormatException) { rejected = true; }
        Assert(rejected, "invalid linked threshold rejected");
        File.WriteAllText(path, "[TimingKeywords]\nVersion=1\nCount=1\n[Keyword0]\nText=START\nEndKeyword=END\nExpectedIntervalMs=250\nTolerancePercent=15\n");
        List<KeywordRule> migrated = KeywordSettings.Load(path);
        Assert(migrated.Count == 2 && migrated[0].LinkedKeywordNumber == 2 && migrated[1].Text == "END" && migrated[0].LinkedExpectedIntervalMs == 250 && migrated[0].LinkedTolerancePercent == 15 && !migrated[0].CheckIntervals, "legacy endpoint migrates without losing settings or enabling periodic check");
        File.WriteAllText(path, "[TimingKeywords]\nVersion=1\nCount=2\n[Keyword0]\nText=START\nEndKeyword=END\n[Keyword1]\nText=END\n");
        migrated = KeywordSettings.Load(path);
        Assert(migrated.Count == 2 && migrated[0].LinkedKeywordNumber == 2, "legacy existing target reused");
    }

    // 독립 기대값을 가진 순차 이벤트를 테스트 안에서 생성한다.
    private static List<LogEntry> IntervalRows(string process, string kind, int[] milliseconds)
    {
        List<LogEntry> rows = new List<LogEntry>();
        DateTime start = new DateTime(2026, 11, 16, 12, 0, 0);
        foreach (int offset in milliseconds)
        { rows.Add(new LogEntry { Timestamp = start.AddMilliseconds(offset), Text = "cycle", Process = process, Kind = kind, LineNumber = rows.Count + 1 }); }
        return rows;
    }

    // 새 설정을 왕복하고 기존 키워드 INI에도 기본 검사 설정을 적용한다.
    private static void TestIntervalIni()
    {
        string path = Path.Combine(m_strRoot, "interval.ini");
        KeywordSettings.Save(path, new List<KeywordRule> { new KeywordRule { Text = "cycle", CheckIntervals = false, ExpectedIntervalMs = 1234.5, TolerancePercent = 12.5 } });
        KeywordRule loaded = KeywordSettings.Load(path)[0];
        Assert(!loaded.CheckIntervals && loaded.ExpectedIntervalMs == 1234.5 && loaded.TolerancePercent == 12.5, "custom interval settings preserved");
        File.WriteAllText(path, "[TimingKeywords]\nVersion=1\nCount=1\n[Keyword0]\nText=cycle\n");
        loaded = KeywordSettings.Load(path)[0];
        Assert(loaded.CheckIntervals && loaded.ExpectedIntervalMs == 0 && loaded.TolerancePercent == 50, "legacy defaults");
        File.AppendAllText(path, "TolerancePercent=NaN\n");
        bool rejected = false;
        try { KeywordSettings.Load(path); } catch (FormatException) { rejected = true; }
        Assert(rejected, "invalid interval thresholds rejected");
    }

    // 검색 옵션과 정규식 오류를 검증한다.
    private static void TestSearch()
    {
        List<LogEntry> rows = new List<LogEntry> { new LogEntry { Text = "Start 12" }, new LogEntry { Text = "start 34" }, new LogEntry { Text = "끝" } };
        Assert(LogReader.Search(rows, "start", false, false, CancellationToken.None).Count == 2, "ignore case");
        Assert(LogReader.Search(rows, "Start", true, false, CancellationToken.None).Count == 1, "case sensitive");
        Assert(LogReader.Search(rows, @"\d{2}", false, true, CancellationToken.None).Count == 2, "regex");
        bool rejected = false;
        try { LogReader.Search(rows, "[", false, true, CancellationToken.None); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "invalid regex rejected");
    }

    // INI의 구분자와 한글이 키워드 내용에서 손실되지 않는지 확인한다.
    private static void TestKeywords()
    {
        string file = Path.Combine(m_strRoot, "keywords.ini");
        List<KeywordRule> rules = new List<KeywordRule> { new KeywordRule { Text = " [검사]=Start;#\\n ", Enabled = false, CaseSensitive = true, Color = Color.FromArgb(12, 34, 56) } };
        KeywordSettings.Save(file, rules);
        List<KeywordRule> loaded = KeywordSettings.Load(file);
        Assert(loaded.Count == 1, "rule count");
        Assert(loaded[0].Text == rules[0].Text && !loaded[0].Enabled && loaded[0].CaseSensitive && loaded[0].Color.ToArgb() == rules[0].Color.ToArgb(), "lossless round trip");
    }

    // 손상되거나 다른 종류인 INI를 빈 목록으로 오인하지 않는다.
    private static void TestInvalidIni()
    {
        string file = Path.Combine(m_strRoot, "bad.ini");
        File.WriteAllText(file, "[Unrelated]\nCount=0");
        bool rejected = false;
        try { KeywordSettings.Load(file); } catch (FormatException) { rejected = true; }
        Assert(rejected, "wrong schema rejected");
    }

    // 취소 요청을 무시하고 읽기를 계속하지 않는다.
    private static void TestCancellation()
    {
        CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();
        bool cancelled = false;
        try { LogReader.ReadFolder(Folder("cancel"), new LogOptions(), source.Token, null); } catch (OperationCanceledException) { cancelled = true; }
        source.Dispose();
        Assert(cancelled, "cancellation observed");
    }

    // 실제 샘플의 줄 수와 시각 범위를 독립적으로 확인한다.
    private static void TestSample(string zipPath)
    {
        string folder = Folder("sample");
        ZipFile.ExtractToDirectory(zipPath, folder);
        LogReadResult result = LogReader.ReadFolder(folder, new LogOptions(), CancellationToken.None, null);
        Assert(result.Entries.Count == 43312 && result.FileCount == 30, "sample counts");
        Assert(result.Entries[0].Timestamp == new DateTime(2026, 11, 16, 10, 50, 3, 916), "sample first time");
        Assert(result.Entries.Last().Timestamp == new DateTime(2026, 11, 16, 10, 59, 58, 899), "sample last time");
        Assert(result.Warnings.Count == 0, "no read warnings");
    }
}
