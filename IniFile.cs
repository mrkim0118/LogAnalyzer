using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace LogAnalyzer
{
    internal sealed class KeywordRule
    {
        public const double DefaultTolerancePercent = 50;
        public const double MaximumTolerancePercent = 1000;
        public const double MaximumExpectedIntervalMs = 86400000;
        public string Text = "";
        public bool Enabled = true;
        public bool CaseSensitive = false;
        public Color Color = Color.DodgerBlue;
        public bool CheckIntervals = true;
        public double ExpectedIntervalMs = 0;
        public double TolerancePercent = DefaultTolerancePercent;
        public int LinkedKeywordNumber;
        public double LinkedExpectedIntervalMs;
        public double LinkedTolerancePercent = DefaultTolerancePercent;
    }

    internal static class KeywordSettings
    {
        public const int MaximumRules = 1000;
        public const int MaximumHighlights = 1000;

        // 프리셋 이름을 검증하여 관리 폴더 밖의 경로나 Windows 예약 파일을 만들지 않는다.
        public static string PresetPath(string folder, string name)
        {
            const int MaximumPresetNameLength = 80;
            if (String.IsNullOrWhiteSpace(name) || name.Length > MaximumPresetNameLength || name != name.Trim()
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".", StringComparison.Ordinal))
            { throw new ArgumentException("프리셋 이름은 1~80자로 입력하고 경로 문자나 끝 공백·마침표는 제외하세요."); }
            string stem = name.Split('.')[0].ToUpperInvariant();
            string[] reserved = new string[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (Array.IndexOf(reserved, stem) >= 0) { throw new ArgumentException("Windows에서 예약한 파일 이름은 사용할 수 없습니다."); }
            return Path.Combine(folder, name + ".ini");
        }

        // 키워드와 문자열 강조를 같은 UTF-8 INI에 원자적으로 저장한다.
        public static void Save(string path, List<KeywordRule> rules, List<KeyValuePair<string, Color>> highlights = null)
        {
            if (rules.Count > MaximumRules) { throw new ArgumentException("키워드는 최대 " + MaximumRules + "개입니다."); }
            ValidateLinks(rules);
            IniFile ini = new IniFile();
            ini.Set("TimingKeywords", "Version", "1");
            ini.Set("TimingKeywords", "Count", rules.Count.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < rules.Count; index++)
            {
                KeywordRule rule = rules[index];
                if (String.IsNullOrWhiteSpace(rule.Text)) { throw new ArgumentException("빈 키워드는 저장할 수 없습니다."); }
                ValidateIntervals(rule);
                string section = "Keyword" + index.ToString(CultureInfo.InvariantCulture);
                ini.Set(section, "Text", rule.Text);
                ini.Set(section, "Enabled", rule.Enabled ? "true" : "false");
                ini.Set(section, "CaseSensitive", rule.CaseSensitive ? "true" : "false");
                ini.Set(section, "Color", ColorTranslator.ToHtml(rule.Color));
                ini.Set(section, "CheckIntervals", rule.CheckIntervals ? "true" : "false");
                ini.Set(section, "ExpectedIntervalMs", rule.ExpectedIntervalMs.ToString("R", CultureInfo.InvariantCulture));
                ini.Set(section, "TolerancePercent", rule.TolerancePercent.ToString("R", CultureInfo.InvariantCulture));
                ini.Set(section, "LinkedKeywordNumber", rule.LinkedKeywordNumber.ToString(CultureInfo.InvariantCulture));
                ini.Set(section, "LinkedExpectedIntervalMs", rule.LinkedExpectedIntervalMs.ToString("R", CultureInfo.InvariantCulture));
                ini.Set(section, "LinkedTolerancePercent", rule.LinkedTolerancePercent.ToString("R", CultureInfo.InvariantCulture));
            }
            int highlightCount = highlights == null ? 0 : highlights.Count;
            if (highlightCount > MaximumHighlights) { throw new ArgumentException("문자열 강조는 최대 " + MaximumHighlights + "개입니다."); }
            ini.Set("TextHighlights", "Count", highlightCount.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < highlightCount; index++)
            {
                KeyValuePair<string, Color> highlight = highlights[index];
                if (String.IsNullOrWhiteSpace(highlight.Key) || highlight.Value.IsEmpty || highlight.Value.A != 255)
                { throw new ArgumentException("강조 문자열과 불투명한 색상이 필요합니다."); }
                string section = "Highlight" + index.ToString(CultureInfo.InvariantCulture);
                ini.Set(section, "Text", highlight.Key);
                ini.Set(section, "Color", ColorTranslator.ToHtml(highlight.Value));
            }
            ini.Save(path);
        }

        // 모든 키워드와 강조를 검증한 뒤에만 전달받은 강조 목록을 교체한다. 이전 INI는 강조 없음으로 읽는다.
        public static List<KeywordRule> Load(string path, List<KeyValuePair<string, Color>> highlights = null)
        {
            IniFile ini = IniFile.Load(path);
            int count;
            if (ini.Get("TimingKeywords", "Version", "") != "1" || !Int32.TryParse(ini.Get("TimingKeywords", "Count", ""), out count) || count < 0 || count > MaximumRules)
            { throw new FormatException("올바른 타이밍 키워드 INI 파일이 아닙니다."); }
            List<KeywordRule> rules = new List<KeywordRule>();
            Dictionary<int, string> legacyLinks = new Dictionary<int, string>();
            for (int index = 0; index < count; index++)
            {
                string section = "Keyword" + index.ToString(CultureInfo.InvariantCulture);
                string text = ini.Get(section, "Text", "");
                bool enabled;
                bool sensitive;
                Color color;
                if (String.IsNullOrWhiteSpace(text) || !Boolean.TryParse(ini.Get(section, "Enabled", "true"), out enabled) || !Boolean.TryParse(ini.Get(section, "CaseSensitive", "false"), out sensitive))
                { throw new FormatException(section + "의 키워드 설정이 올바르지 않습니다."); }
                try { color = ColorTranslator.FromHtml(ini.Get(section, "Color", "#3B82F6")); }
                catch (Exception ex)
                { throw new FormatException(section + "의 색상이 올바르지 않습니다.", ex); }
                if (color.IsEmpty || color.A != 255) { throw new FormatException("불투명한 키워드 색상이 필요합니다."); }
                KeywordRule rule = new KeywordRule { Text = text, Enabled = enabled, CaseSensitive = sensitive, Color = color };
                if (!Boolean.TryParse(ini.Get(section, "CheckIntervals", "true"), out rule.CheckIntervals)
                    || !Double.TryParse(ini.Get(section, "ExpectedIntervalMs", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out rule.ExpectedIntervalMs)
                    || !Double.TryParse(ini.Get(section, "TolerancePercent", KeywordRule.DefaultTolerancePercent.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out rule.TolerancePercent))
                { throw new FormatException(section + "의 간격 검사 설정이 올바르지 않습니다."); }
                string linkedNumber = ini.Get(section, "LinkedKeywordNumber", "");
                string legacyEnd = ini.Get(section, "EndKeyword", "");
                if (!Int32.TryParse(linkedNumber.Length == 0 ? "0" : linkedNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out rule.LinkedKeywordNumber)
                    || !Double.TryParse(ini.Get(section, "LinkedExpectedIntervalMs", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out rule.LinkedExpectedIntervalMs)
                    || !Double.TryParse(ini.Get(section, "LinkedTolerancePercent", KeywordRule.DefaultTolerancePercent.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out rule.LinkedTolerancePercent))
                { throw new FormatException(section + "의 키워드 연동 설정이 올바르지 않습니다."); }
                if (linkedNumber.Length == 0 && !String.IsNullOrWhiteSpace(legacyEnd) && rule.CheckIntervals) { legacyLinks.Add(index, legacyEnd); }
                ValidateIntervals(rule);
                rules.Add(rule);
            }
            // 이전 자유 종료 문자열은 등록된 키워드의 순번으로 옮겨 기존 값을 잃지 않게 한다.
            foreach (KeyValuePair<int, string> legacy in legacyLinks)
            {
                KeywordRule rule = rules[legacy.Key];
                int target = rules.FindIndex(delegate(KeywordRule item) { return item != rule && item.Text == legacy.Value && item.CaseSensitive == rule.CaseSensitive; });
                if (target < 0)
                {
                    if (rules.Count >= MaximumRules) { throw new FormatException("이전 종료 키워드를 등록할 공간이 없습니다."); }
                    target = rules.Count;
                    rules.Add(new KeywordRule { Text = legacy.Value, CaseSensitive = rule.CaseSensitive, Color = rule.Color, CheckIntervals = false });
                }
                rule.LinkedKeywordNumber = target + 1;
                rule.LinkedExpectedIntervalMs = rule.ExpectedIntervalMs; rule.LinkedTolerancePercent = rule.TolerancePercent;
                rule.CheckIntervals = false;
            }
            ValidateLinks(rules);
            int highlightCount;
            if (!Int32.TryParse(ini.Get("TextHighlights", "Count", "0"), out highlightCount) || highlightCount < 0 || highlightCount > MaximumHighlights)
            { throw new FormatException("문자열 강조 개수가 올바르지 않습니다."); }
            List<KeyValuePair<string, Color>> loadedHighlights = new List<KeyValuePair<string, Color>>();
            for (int index = 0; index < highlightCount; index++)
            {
                string section = "Highlight" + index.ToString(CultureInfo.InvariantCulture);
                string text = ini.Get(section, "Text", "");
                Color color;
                if (String.IsNullOrWhiteSpace(text)) { throw new FormatException(section + "의 강조 문자열이 비어 있습니다."); }
                try { color = ColorTranslator.FromHtml(ini.Get(section, "Color", "")); }
                catch (Exception ex) { throw new FormatException(section + "의 강조 색상이 올바르지 않습니다.", ex); }
                if (color.IsEmpty || color.A != 255) { throw new FormatException(section + "에 불투명한 강조 색상이 필요합니다."); }
                loadedHighlights.Add(new KeyValuePair<string, Color>(text, color));
            }
            if (highlights != null) { highlights.Clear(); highlights.AddRange(loadedHighlights); }
            return rules;
        }

        // 등록 순번의 범위와 자기 참조를 UI/저장/로드에서 같은 기준으로 검사한다.
        public static void ValidateLinks(List<KeywordRule> rules)
        {
            for (int index = 0; index < rules.Count; index++)
            {
                KeywordRule rule = rules[index];
                if (rule.LinkedKeywordNumber < 0 || rule.LinkedKeywordNumber > rules.Count || rule.LinkedKeywordNumber == index + 1)
                { throw new FormatException((index + 1) + "번의 연동 대상은 다른 등록 키워드의 순번으로 선택하세요."); }
                ValidateIntervals(rule);
            }
        }

        // UI와 INI 양쪽에서 유효한 간격 검사 범위만 허용한다. 주기 0은 자동 추정이다.
        public static void ValidateIntervals(KeywordRule rule)
        {
            if (Double.IsNaN(rule.ExpectedIntervalMs) || Double.IsInfinity(rule.ExpectedIntervalMs) || rule.ExpectedIntervalMs < 0 || rule.ExpectedIntervalMs > KeywordRule.MaximumExpectedIntervalMs)
            { throw new FormatException("기준 주기는 0~" + KeywordRule.MaximumExpectedIntervalMs + " ms 범위로 입력하세요. 0은 자동입니다."); }
            if (Double.IsNaN(rule.TolerancePercent) || Double.IsInfinity(rule.TolerancePercent) || rule.TolerancePercent < 0 || rule.TolerancePercent > KeywordRule.MaximumTolerancePercent)
            { throw new FormatException("허용 편차는 0~" + KeywordRule.MaximumTolerancePercent + "% 범위로 입력하세요."); }
            if (Double.IsNaN(rule.LinkedExpectedIntervalMs) || Double.IsInfinity(rule.LinkedExpectedIntervalMs) || rule.LinkedExpectedIntervalMs < 0 || rule.LinkedExpectedIntervalMs > KeywordRule.MaximumExpectedIntervalMs)
            { throw new FormatException("연동 기준은 0~" + KeywordRule.MaximumExpectedIntervalMs + " ms 범위로 입력하세요. 0은 자동입니다."); }
            if (Double.IsNaN(rule.LinkedTolerancePercent) || Double.IsInfinity(rule.LinkedTolerancePercent) || rule.LinkedTolerancePercent < 0 || rule.LinkedTolerancePercent > KeywordRule.MaximumTolerancePercent)
            { throw new FormatException("연동 허용 편차는 0~" + KeywordRule.MaximumTolerancePercent + "% 범위로 입력하세요."); }
        }
    }

    internal sealed class AppSettings
    {
        public LogOptions Options = new LogOptions();
        public bool DarkMode = false;
        public string LastFolder = "";
        public string SelectedPreset = "";
        public Dictionary<string, Color> ProcessColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        // 화면 설정과 파서 설정을 읽는다. 존재하지 않는 파일에는 기본값을 사용한다.
        public static AppSettings Load(string path)
        {
            AppSettings settings = new AppSettings();
            if (!File.Exists(path)) { return settings; }
            IniFile ini = IniFile.Load(path);
            settings.DarkMode = Boolean.Parse(ini.Get("Viewer", "DarkMode", "false"));
            settings.LastFolder = ini.Get("Viewer", "LastFolder", "");
            settings.SelectedPreset = ini.Get("Viewer", "SelectedPreset", "");
            settings.Options.FilePatterns = ini.Get("Parser", "FilePatterns", settings.Options.FilePatterns);
            settings.Options.EncodingName = ini.Get("Parser", "Encoding", "auto");
            settings.Options.TimestampPattern = ini.Get("Parser", "TimestampPattern", "");
            settings.Options.TimestampFormats = ini.Get("Parser", "TimestampFormats", "");
            settings.Options.ProcessPattern = ini.Get("Parser", "ProcessPattern", settings.Options.ProcessPattern);
            settings.Options.GroupByFolder = Boolean.Parse(ini.Get("Parser", "GroupByFolder", "false"));
            settings.Options.BaseDate = DateTime.ParseExact(ini.Get("Parser", "BaseDate", DateTime.Today.ToString("yyyy-MM-dd")), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            int count = Int32.Parse(ini.Get("Colors", "Count", "0"), CultureInfo.InvariantCulture);
            if (count < 0 || count > 10000) { throw new FormatException("프로세스 색상 개수가 올바르지 않습니다."); }
            for (int index = 0; index < count; index++)
            {
                string section = "Process" + index;
                string name = ini.Get(section, "Name", "");
                if (name.Length > 0) { settings.ProcessColors[name] = ColorTranslator.FromHtml(ini.Get(section, "Color", "#DCEBFF")); }
            }
            LogReader.ValidateOptions(settings.Options);
            return settings;
        }

        // 사용자 지정 프로세스 이름도 INI 구분자와 충돌하지 않도록 값으로 저장한다.
        public void Save(string path)
        {
            IniFile ini = new IniFile();
            ini.Set("Viewer", "DarkMode", DarkMode ? "true" : "false");
            ini.Set("Viewer", "LastFolder", LastFolder);
            ini.Set("Viewer", "SelectedPreset", SelectedPreset);
            ini.Set("Parser", "FilePatterns", Options.FilePatterns);
            ini.Set("Parser", "Encoding", Options.EncodingName);
            ini.Set("Parser", "TimestampPattern", Options.TimestampPattern);
            ini.Set("Parser", "TimestampFormats", Options.TimestampFormats);
            ini.Set("Parser", "ProcessPattern", Options.ProcessPattern);
            ini.Set("Parser", "GroupByFolder", Options.GroupByFolder ? "true" : "false");
            ini.Set("Parser", "BaseDate", Options.BaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            ini.Set("Colors", "Count", ProcessColors.Count.ToString(CultureInfo.InvariantCulture));
            int index = 0;
            foreach (KeyValuePair<string, Color> pair in ProcessColors)
            {
                ini.Set("Process" + index, "Name", pair.Key);
                ini.Set("Process" + index, "Color", ColorTranslator.ToHtml(pair.Value));
                index++;
            }
            ini.Save(path);
        }
    }

    internal sealed class IniFile
    {
        private readonly Dictionary<string, Dictionary<string, string>> m_sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // 섹션과 키로 값을 등록한다.
        public void Set(string section, string key, string value)
        {
            if (!m_sections.ContainsKey(section)) { m_sections.Add(section, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }
            m_sections[section][key] = value;
        }

        // 없는 키에만 기본값을 적용한다.
        public string Get(string section, string key, string defaultValue)
        {
            Dictionary<string, string> values;
            string value;
            if (m_sections.TryGetValue(section, out values) && values.TryGetValue(key, out value)) { return value; }
            return defaultValue;
        }

        // 따옴표로 둘러싼 값의 역슬래시 이스케이프를 복원한다.
        private static string Decode(string value)
        {
            if (!value.StartsWith("\"", StringComparison.Ordinal)) { return value; }
            if (value.Length < 2 || !value.EndsWith("\"", StringComparison.Ordinal)) { throw new FormatException("INI 값의 따옴표가 닫히지 않았습니다."); }
            StringBuilder text = new StringBuilder();
            for (int index = 1; index < value.Length - 1; index++)
            {
                char character = value[index];
                if (character == '\\')
                {
                    index++;
                    if (index >= value.Length - 1) { throw new FormatException("잘못된 INI 이스케이프입니다."); }
                    switch (value[index])
                    {
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case '\\': text.Append('\\'); break;
                        case '"': text.Append('"'); break;
                        default: throw new FormatException("지원하지 않는 INI 이스케이프입니다.");
                    }
                }
                else { text.Append(character); }
            }
            return text.ToString();
        }

        // 값의 공백, 한글, 구분 문자를 보존하는 UTF-8 INI를 읽는다.
        public static IniFile Load(string path)
        {
            IniFile ini = new IniFile();
            string section = "";
            foreach (string source in File.ReadAllLines(path, new UTF8Encoding(false, true)))
            {
                string line = source.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) { continue; }
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                int separator = line.IndexOf('=');
                if (section.Length == 0 || separator <= 0) { throw new FormatException("INI 행 형식이 올바르지 않습니다: " + line); }
                string key = line.Substring(0, separator).Trim();
                if (ini.m_sections.ContainsKey(section) && ini.m_sections[section].ContainsKey(key)) { throw new FormatException("중복된 INI 키: " + key); }
                ini.Set(section, key, Decode(line.Substring(separator + 1).Trim()));
            }
            return ini;
        }

        // 임시 파일을 완전히 기록한 뒤 교체해 저장 도중 기존 설정이 손상되지 않게 한다.
        public void Save(string path)
        {
            string destination = Path.GetFullPath(path);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(true), 1024, true))
                    {
                        writer.NewLine = "\r\n";
                        foreach (KeyValuePair<string, Dictionary<string, string>> section in m_sections)
                        {
                            writer.WriteLine("[" + section.Key + "]");
                            foreach (KeyValuePair<string, string> pair in section.Value)
                            {
                                string value = pair.Value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                                writer.WriteLine(pair.Key + "=\"" + value + "\"");
                            }
                            writer.WriteLine();
                        }
                    }
                    stream.Flush(true);
                }
                if (File.Exists(destination)) { File.Replace(temporary, destination, null); }
                else { File.Move(temporary, destination); }
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
    }
}
