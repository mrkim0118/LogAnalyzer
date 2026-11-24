using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LogAnalyzer
{
    internal sealed class LogOptions
    {
        public string FilePatterns = "*.log;*.txt";
        public string EncodingName = "auto";
        public string TimestampPattern = "";
        public string TimestampFormats = "";
        public string ProcessPattern = @"^(?<process>.+?)(?:_(?<kind>[^_]+))?$";
        public bool GroupByFolder = false;
        public DateTime BaseDate = DateTime.Today;
    }

    internal sealed class LogEntry
    {
        public DateTime? Timestamp;
        public string Process;
        public string Kind;
        public string FilePath;
        public int LineNumber;
        public string Text;
        private string m_strDisplayText;
        private int m_nTimestampStart;
        private int m_nTimestampLength;

        // 화면에는 시각 필드를 제외한 본문을 사용하고 원문은 Text에 그대로 보관한다.
        public string DisplayText
        {
            get
            {
                if (m_strDisplayText != null) { return m_strDisplayText; }
                if (m_nTimestampLength > 0) { return Text.Remove(m_nTimestampStart, m_nTimestampLength); }
                return Text;
            }
            set { m_strDisplayText = value; m_nTimestampLength = 0; }
        }

        // 표시용 문자열을 행마다 복제하지 않고 제거 범위만 보관해 실제 조회할 때 생성한다.
        public void ExcludeTimestamp(int start, int length)
        {
            int end = start + length;
            if (start > 0 && end < Text.Length)
            {
                char opening = Text[start - 1];
                char closing = Text[end];
                if ((opening == '[' && closing == ']') || (opening == '(' && closing == ')')) { start--; end++; }
            }
            while (end < Text.Length && Char.IsWhiteSpace(Text[end])) { end++; }
            m_strDisplayText = null; m_nTimestampStart = start; m_nTimestampLength = end - start;
        }
    }

    internal sealed class LogReadResult
    {
        public List<LogEntry> Entries = new List<LogEntry>();
        public List<string> Warnings = new List<string>();
        public List<string> SourceFiles = new List<string>();
        public int FileCount;
        public List<LogEntry> FileRepresentatives = new List<LogEntry>();
        public int MissingTimestampCount;
    }

    internal static class LogReader
    {
        public const int RegexTimeoutMilliseconds = 250;
        private const int ProgressLineInterval = 256;
        private const int ExportBufferSize = 65536;
        private static readonly Regex m_rxTime = new Regex(@"(?<!\d)(?:\d{4}[-/]\d{2}[-/]\d{2}[T ]+)?\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?(?!\d)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));
        private static readonly string[] m_arrDateFormats = new string[] {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss.FFFFFFF" };
        private static readonly string[] m_arrTimeFormats = new string[] { "HH:mm:ss", "HH:mm:ss.FFFFFFF" };

        // 설정을 읽기 전에 검증하여 잘못된 규칙으로 일부 데이터만 반영하지 않는다.
        public static void ValidateOptions(LogOptions options)
        {
            if (String.IsNullOrWhiteSpace(options.FilePatterns)) { throw new ArgumentException("파일 패턴을 입력하세요."); }
            foreach (string pattern in options.FilePatterns.Split(';'))
            {
                if (String.IsNullOrWhiteSpace(pattern) || pattern.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0)
                { throw new ArgumentException("파일 패턴은 *.log;*.txt 형식으로 입력하세요."); }
            }
            if (!String.IsNullOrWhiteSpace(options.TimestampPattern))
            {
                Regex regex = CreateRegex(options.TimestampPattern, true);
                if (!regex.GetGroupNames().Contains("time")) { throw new ArgumentException("시간 정규식에 (?<time>...) 그룹이 필요합니다."); }
            }
            if (!options.GroupByFolder && !String.IsNullOrWhiteSpace(options.ProcessPattern))
            {
                Regex regex = CreateRegex(options.ProcessPattern, true);
                if (!regex.GetGroupNames().Contains("process")) { throw new ArgumentException("프로세스 정규식에 (?<process>...) 그룹이 필요합니다."); }
            }
            if (!String.Equals(options.EncodingName, "auto", StringComparison.OrdinalIgnoreCase)) { GetEncoding(options.EncodingName); }
        }

        // 하위 폴더의 로그를 읽고 같은 시각은 파일명과 줄 번호로 안정 정렬한다.
        public static LogReadResult ReadFolder(string path, LogOptions options, CancellationToken token, IProgress<ProgressChangedEventArgs> progress)
        {
            token.ThrowIfCancellationRequested();
            ValidateOptions(options);
            string root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Directory.Exists(root)) { throw new DirectoryNotFoundException("로그 폴더를 찾을 수 없습니다: " + path); }
            LogReadResult result = new LogReadResult();
            List<string> files = result.SourceFiles;
            CollectFiles(root, options.FilePatterns.Split(';'), files, result.Warnings, token);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            Regex timeRegex = null;
            Regex processRegex = null;
            if (!String.IsNullOrWhiteSpace(options.TimestampPattern)) { timeRegex = CreateRegex(options.TimestampPattern, true); }
            if (!options.GroupByFolder && !String.IsNullOrWhiteSpace(options.ProcessPattern)) { processRegex = CreateRegex(options.ProcessPattern, true); }
            string[] formats = options.TimestampFormats.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            System.Diagnostics.Stopwatch progressClock = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < files.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                string file = files[index];
                Action<int> reportPercent = null;
                if (progress != null)
                {
                    string status = String.Format("로딩 중 · {0}/{1} 파일 · {2}", index + 1, files.Count, Path.GetFileName(file));
                    int lastPercent = -1;
                    reportPercent = delegate(int percent)
                    {
                        // 디코딩 재시도에서도 진행률은 역행하지 않으며 많은 파일의 알림을 제한한다.
                        const int ReportIntervalMs = 80;
                        if (percent <= lastPercent || (index != 0 || lastPercent > 0) && progressClock.ElapsedMilliseconds < ReportIntervalMs) { return; }
                        lastPercent = percent;
                        progressClock.Restart();
                        progress.Report(new ProgressChangedEventArgs(percent, status));
                    };
                    reportPercent(0);
                }
                try
                {
                    int missingTimestampCount;
                    List<LogEntry> rows;
                    if (String.Equals(options.EncodingName, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        try { rows = ReadFile(root, file, options, new UTF8Encoding(false, true), timeRegex, processRegex, formats, token, reportPercent, out missingTimestampCount); }
                        catch (DecoderFallbackException) { rows = ReadFile(root, file, options, GetEncoding("949"), timeRegex, processRegex, formats, token, reportPercent, out missingTimestampCount); }
                    }
                    else { rows = ReadFile(root, file, options, GetEncoding(options.EncodingName), timeRegex, processRegex, formats, token, reportPercent, out missingTimestampCount); }
                    if (rows.Count > 0) { result.FileRepresentatives.Add(rows[0]); }
                    result.MissingTimestampCount += missingTimestampCount;
                    if (result.Entries.Count == 0) { result.Entries = rows; }
                    else { result.Entries.AddRange(rows); }
                    result.FileCount++;
                }
                catch (IOException ex) { result.Warnings.Add(file + ": " + ex.Message); }
                catch (UnauthorizedAccessException ex) { result.Warnings.Add(file + ": " + ex.Message); }
                catch (DecoderFallbackException ex) { result.Warnings.Add(file + ": 인코딩 해석 실패. " + ex.Message); }
            }
            token.ThrowIfCancellationRequested();
            if (progress != null) { progress.Report(new ProgressChangedEventArgs(-1, "시간순 정렬 중 · " + result.Entries.Count.ToString("N0") + "줄")); }
            result.Entries.Sort(delegate(LogEntry left, LogEntry right)
            {
                int order = Nullable.Compare(left.Timestamp, right.Timestamp);
                if (!left.Timestamp.HasValue && right.Timestamp.HasValue) { return 1; }
                if (left.Timestamp.HasValue && !right.Timestamp.HasValue) { return -1; }
                if (order != 0) { return order; }
                order = StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);
                if (order != 0) { return order; }
                return left.LineNumber.CompareTo(right.LineNumber);
            });
            token.ThrowIfCancellationRequested();
            return result;
        }

        // 재분석 지점은 건너뛰고 접근 불가 경로는 경고로 기록한다.
        private static void CollectFiles(string folder, string[] patterns, List<string> files, List<string> warnings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string pattern in patterns)
                {
                    foreach (string file in Directory.GetFiles(folder, pattern.Trim())) { unique.Add(file); }
                }
                files.AddRange(unique);
                foreach (string child in Directory.GetDirectories(folder))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) { CollectFiles(child, patterns, files, warnings, token); }
                }
            }
            catch (UnauthorizedAccessException ex) { warnings.Add(folder + ": " + ex.Message); }
            catch (IOException ex) { warnings.Add(folder + ": " + ex.Message); }
        }

        // 파일 전체의 디코딩이 성공한 뒤에만 호출자에게 원문 행들을 전달한다.
        private static List<LogEntry> ReadFile(string root, string file, LogOptions options, Encoding encoding, Regex timeRegex, Regex processRegex, string[] formats, CancellationToken token, Action<int> reportPercent, out int missingTimestampCount)
        {
            missingTimestampCount = 0;
            string relative = file.Substring(root.Length);
            string folder = Path.GetDirectoryName(relative);
            string process = Path.GetFileNameWithoutExtension(file);
            string kind = Path.GetExtension(file).TrimStart('.');
            if (options.GroupByFolder)
            {
                process = String.IsNullOrEmpty(folder) ? "(루트)" : folder;
                kind = Path.GetFileNameWithoutExtension(file);
            }
            else
            {
                if (processRegex != null)
                {
                    Match match = processRegex.Match(process);
                    if (match.Success && match.Groups["process"].Length > 0)
                    {
                        process = match.Groups["process"].Value;
                        if (match.Groups["kind"].Success) { kind = match.Groups["kind"].Value; }
                    }
                }
                if (!String.IsNullOrEmpty(folder)) { process = folder + " / " + process; }
            }
            List<LogEntry> rows = new List<LogEntry>();
            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] prefix = new byte[4];
                int bytesRead = stream.Read(prefix, 0, prefix.Length);
                int skipBytes = 0;
                if (bytesRead >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0 && prefix[3] == 0)
                { encoding = new UTF32Encoding(false, true, true); skipBytes = 4; }
                else if (bytesRead >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF)
                { encoding = new UTF32Encoding(true, true, true); skipBytes = 4; }
                else if (bytesRead >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
                { encoding = new UTF8Encoding(false, true); skipBytes = 3; }
                else if (bytesRead >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
                { encoding = new UnicodeEncoding(false, true, true); skipBytes = 2; }
                else if (bytesRead >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
                { encoding = new UnicodeEncoding(true, true, true); skipBytes = 2; }
                long fileBytes = stream.Length;
                stream.Position = skipBytes;
                // BOM 자동 감지가 엄격한 디코더를 치환하지 않도록 직접 선택한 인코딩을 사용한다.
                using (StreamReader reader = new StreamReader(stream, encoding, false))
                {
                    int lineNumber = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        lineNumber++;
                        Match match = (timeRegex ?? m_rxTime).Match(line);
                        Group timeGroup = timeRegex == null ? match : match.Groups["time"];
                        DateTime? timestamp = null;
                        if (timeGroup.Success)
                        {
                            timestamp = ParseTime(timeGroup.Value, options.BaseDate, formats);
                        }
                        LogEntry entry = new LogEntry { FilePath = file, LineNumber = lineNumber, Text = line, Process = process, Kind = kind, Timestamp = timestamp };
                        if (timestamp.HasValue) { entry.ExcludeTimestamp(timeGroup.Index, timeGroup.Length); }
                        else { missingTimestampCount++; }
                        rows.Add(entry);
                        if (reportPercent != null && fileBytes > 0 && lineNumber % ProgressLineInterval == 0)
                        {
                            reportPercent((int)Math.Min(99, 100.0 * stream.Position / fileBytes));
                        }
                    }
                }
            }
            return rows;
        }

        // 추출한 시각 문자열에 사용자 형식을 우선 적용하며 기본 날짜/시간 형식도 지원한다.
        private static DateTime? ParseTime(string value, DateTime baseDate, string[] formats)
        {
            DateTime parsed;
            if (formats.Length > 0)
            {
                foreach (string format in formats)
                {
                    double epoch;
                    if (format == "unix-ms" || format == "unix-s")
                    {
                        if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out epoch)) { continue; }
                        try
                        {
                            DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                            if (format == "unix-ms") { return origin.AddMilliseconds(epoch).ToLocalTime(); }
                            return origin.AddSeconds(epoch).ToLocalTime();
                        }
                        catch (ArgumentOutOfRangeException) { continue; }
                    }
                    if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AllowWhiteSpaces, out parsed))
                    { return ApplyBaseDate(parsed, baseDate); }
                }
                return null;
            }
            value = value.Replace(',', '.');
            if (DateTime.TryParseExact(value, m_arrDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)) { return parsed; }
            if (DateTime.TryParseExact(value, m_arrTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out parsed)) { return baseDate.Date.Add(parsed.TimeOfDay); }
            if (Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$"))
            {
                DateTimeOffset offset;
                if (Regex.IsMatch(value, @"^\d{2}:")) { value = baseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + value; }
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out offset)) { return offset.LocalDateTime; }
            }
            return null;
        }

        // 날짜가 없는 사용자 시간 형식에는 선택한 기준 날짜를 붙인다.
        private static DateTime ApplyBaseDate(DateTime parsed, DateTime baseDate)
        {
            if (parsed.Date == DateTime.MinValue.Date) { return baseDate.Date.Add(parsed.TimeOfDay); }
            return parsed;
        }

        // 잘못된 바이트를 물음표로 치환하지 않는 인코딩을 생성한다.
        private static Encoding GetEncoding(string name)
        {
            int codePage;
            if (Int32.TryParse(name, out codePage)) { return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        // 사용자 정규식에 시간 제한을 적용해 잘못된 패턴의 무한 처리를 방지한다.
        internal static Regex CreateRegex(string pattern, bool caseSensitive)
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!caseSensitive) { options |= RegexOptions.IgnoreCase; }
            return new Regex(pattern, options, TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));
        }

        // 현재 표시 범위의 원문에서 문자열 또는 시간 제한이 있는 정규식으로 검색한다.
        public static List<LogEntry> Search(List<LogEntry> entries, string query, bool caseSensitive, bool regex, CancellationToken token)
        {
            List<LogEntry> matches = new List<LogEntry>();
            if (String.IsNullOrEmpty(query)) { return matches; }
            Regex pattern = null;
            if (regex) { pattern = CreateRegex(query, caseSensitive); }
            StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (LogEntry entry in entries)
            {
                token.ThrowIfCancellationRequested();
                bool matched;
                if (pattern == null) { matched = entry.Text.IndexOf(query, comparison) >= 0; }
                else { matched = pattern.IsMatch(entry.Text); }
                if (matched) { matches.Add(entry); }
            }
            return matches;
        }

        // 선택 구간의 원문만 스트리밍하고 성공한 임시 파일만 교체하여 취소 시 기존 파일을 보존한다.
        public static int ExportRange(string path, List<LogEntry> entries, DateTime start, DateTime end, CancellationToken token, List<string> sourceFiles = null)
        {
            if (start > end) { throw new ArgumentException("시작 지점이 끝 지점보다 늦습니다."); }
            string destination = Path.GetFullPath(path);
            if (sourceFiles != null)
            {
                foreach (string file in sourceFiles)
                {
                    token.ThrowIfCancellationRequested();
                    if (String.Equals(destination, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                    { throw new IOException("불러온 원본 로그 파일에는 덮어쓸 수 없습니다."); }
                }
            }
            else
            {
                foreach (LogEntry entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!String.IsNullOrEmpty(entry.FilePath) && String.Equals(destination, Path.GetFullPath(entry.FilePath), StringComparison.OrdinalIgnoreCase))
                    { throw new IOException("불러온 원본 로그 파일에는 덮어쓸 수 없습니다."); }
                }
            }
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            int count = 0;
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(true), ExportBufferSize, true))
                    {
                        writer.NewLine = "\r\n";
                        foreach (LogEntry entry in entries)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!entry.Timestamp.HasValue || entry.Timestamp.Value < start || entry.Timestamp.Value > end) { continue; }
                            writer.WriteLine(entry.Text); count++;
                        }
                    }
                    token.ThrowIfCancellationRequested(); stream.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(destination)) { File.Replace(temporary, destination, null); }
                else { File.Move(temporary, destination); }
                return count;
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
    }
}
