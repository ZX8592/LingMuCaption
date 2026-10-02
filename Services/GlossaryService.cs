using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleMaster.Services;

public record GlossaryEntry(string Source, string Target, string Note);

public static class GlossaryService
{
    public const string SampleFileName = "用户术语表(模板).csv";

    private const string SampleCsvContent =
@"原文,标准中文译名,备注说明
prompt engineering,提示词工程,科技/人工智能领域术语
due diligence,尽职调查,商业与法务术语
confirmation bias,确认偏误,学术/认知心理学术语（避免直译为确认偏差）
Christopher Nolan,克里斯托弗·诺兰,外语人名规范中文汉字（使用间隔号·）
宮崎駿,宫崎骏,日语人名规范汉字
Winterfell,临冬城,影视与文学作品知名IP实体译名
";

    static GlossaryService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string GetBaseDirectory() => Helpers.FilePathHelper.GetAppRootDirectory();

    public static string ResolveCsvFilePath(string csvFileName)
    {
        string rootDir = GetBaseDirectory();
        string rootCandidate = Path.Combine(rootDir, csvFileName);
        if (File.Exists(rootCandidate))
            return rootCandidate;

        string appCandidate = Path.Combine(AppContext.BaseDirectory, csvFileName);
        if (File.Exists(appCandidate))
            return appCandidate;

        return rootCandidate;
    }

    public static void EnsureSampleGlossaryExists()
    {
        try
        {
            string samplePath = Path.Combine(GetBaseDirectory(), SampleFileName);
            if (!File.Exists(samplePath))
            {
                File.WriteAllText(samplePath, SampleCsvContent, new UTF8Encoding(true));
            }
        }
        catch
        {
            // Ignore write errors in restricted environments
        }
    }

    public static List<string> GetAvailableCsvFiles()
    {
        EnsureSampleGlossaryExists();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ScanDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    {
                        result.Add(name);
                    }
                }
            }
            catch { }
        }

        string rootDir = GetBaseDirectory();
        ScanDir(rootDir);
        if (!string.Equals(rootDir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ScanDir(AppContext.BaseDirectory);
        }

        return result
            .OrderBy(f => !string.Equals(f, SampleFileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<GlossaryEntry> LoadGlossary(string? csvFileName)
    {
        var entries = new List<GlossaryEntry>();
        if (string.IsNullOrWhiteSpace(csvFileName))
        {
            return entries;
        }

        try
        {
            string fullPath = ResolveCsvFilePath(csvFileName);
            if (!File.Exists(fullPath))
            {
                return entries;
            }

            string content = ReadCsvTextWithAutoEncoding(fullPath);
            using var reader = new StringReader(content);
            string? line;
            bool isFirstRow = true;
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = ParseCsvLine(line);
                if (cols.Count < 2) continue;

                string src = NormalizeTermPunctuation(cols[0]);
                string tgt = NormalizeTermPunctuation(cols[1]);
                string note = cols.Count > 2 ? cols[2].Trim() : string.Empty;

                if (isFirstRow)
                {
                    isFirstRow = false;
                    if (string.Equals(src, "原文", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(src, "source", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(src, "原名", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
                    continue;

                if (seenSources.Add(src))
                {
                    entries.Add(new GlossaryEntry(src, tgt, note));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning($"[术语表] 读取 {csvFileName} 异常: {ex.Message}");
        }

        return entries;
    }

    public static string FormatUserGlossaryLine(List<GlossaryEntry> entries)
    {
        if (entries == null || entries.Count == 0)
            return string.Empty;

        var parts = new List<string>(entries.Count);
        foreach (var e in entries)
        {
            bool hasCustomNote = !string.IsNullOrWhiteSpace(e.Note) &&
                                 !string.Equals(e.Note, "AI自动收录", StringComparison.OrdinalIgnoreCase);
            parts.Add(hasCustomNote ? $"{e.Source}={e.Target} ({e.Note})" : $"{e.Source}={e.Target}");
        }
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Normalizes punctuation in glossary entries:
    /// - Normalizes katakana/variant middle dots (・ \u30FB, ∙ \u2219, ･ \uFF65) to standard Chinese interpunct (· \u00B7)
    /// - Converts full-width ASCII numbers and letters (０-９, Ａ-Ｚ, ａ-ｚ) to half-width
    /// - Normalizes ellipses (..., ⋯) to standard Unicode ellipsis (…… \u2026)
    /// - Strips enclosing quotes, backticks, and brackets
    /// </summary>
    public static string NormalizeTermPunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if ((c >= '\uFF10' && c <= '\uFF19') || // ０-９
                (c >= '\uFF21' && c <= '\uFF3A') || // Ａ-Ｚ
                (c >= '\uFF41' && c <= '\uFF5A'))   // ａ-ｚ
            {
                sb.Append((char)(c - 0xFEE0));
            }
            else if (c == '\u30FB' || c == '\u2219' || c == '\uFF65') // ・, ∙, ･ -> ·
            {
                sb.Append('\u00B7');
            }
            else
            {
                sb.Append(c);
            }
        }

        string s = sb.ToString().Trim().Trim('[', ']', '`', '"', '\'');
        s = Regex.Replace(s, @"\.{3,}|[\u22EF\u2026]+", "……");
        return s;
    }

    public static void ExtractGlossaryFromOutput(string? rawOutput, Dictionary<string, string> targetMap)
    {
        if (string.IsNullOrWhiteSpace(rawOutput) || targetMap == null)
            return;

        using var reader = new StringReader(rawOutput);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            int idx = trimmed.IndexOf("GLOSSARY:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            string body = trimmed[(idx + "GLOSSARY:".Length)..].Trim();
            if (string.IsNullOrWhiteSpace(body) || string.Equals(body, "NONE", StringComparison.OrdinalIgnoreCase))
                continue;

            var pairs = body.Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                int eqIdx = pair.IndexOf('=');
                if (eqIdx <= 0 || eqIdx >= pair.Length - 1) continue;

                string src = NormalizeTermPunctuation(pair[..eqIdx]);
                string tgt = pair[(eqIdx + 1)..].Trim();

                // Strip trailing parenthetical notes if model echoed them in GLOSSARY
                int parenIdx = tgt.IndexOf('(');
                if (parenIdx > 0) tgt = tgt[..parenIdx].Trim();
                int fullParenIdx = tgt.IndexOf('（');
                if (fullParenIdx > 0) tgt = tgt[..fullParenIdx].Trim();

                tgt = NormalizeTermPunctuation(tgt);

                if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
                    continue;
                if (string.Equals(src, "source1", StringComparison.OrdinalIgnoreCase))
                    continue;

                targetMap[src] = tgt;
            }
        }
    }

    public static int AppendNewTermsToCsv(string? csvFileName, Dictionary<string, string> discoveredMap)
    {
        if (string.IsNullOrWhiteSpace(csvFileName) || discoveredMap == null || discoveredMap.Count == 0)
            return 0;

        try
        {
            string fullPath = ResolveCsvFilePath(csvFileName);
            if (!File.Exists(fullPath))
                return 0;

            var existingEntries = LoadGlossary(csvFileName);
            var existingSources = new HashSet<string>(
                existingEntries.Select(e => e.Source),
                StringComparer.OrdinalIgnoreCase);

            var newRows = new List<string>();
            foreach (var kvp in discoveredMap)
            {
                string src = NormalizeTermPunctuation(kvp.Key);
                string tgt = NormalizeTermPunctuation(kvp.Value);
                if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
                    continue;

                if (!existingSources.Contains(src))
                {
                    existingSources.Add(src);
                    newRows.Add($"{EscapeCsvField(src)},{EscapeCsvField(tgt)},AI自动收录");
                }
            }

            if (newRows.Count == 0)
                return 0;

            string existingText = ReadCsvTextWithAutoEncoding(fullPath);
            var sb = new StringBuilder(existingText);
            if (sb.Length > 0 && sb[^1] != '\n' && sb[^1] != '\r')
            {
                sb.AppendLine();
            }

            foreach (var row in newRows)
            {
                sb.AppendLine(row);
            }

            File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(true));
            return newRows.Count;
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning($"[术语表] 回写 {csvFileName} 异常: {ex.Message}");
            return 0;
        }
    }

    private static string ReadCsvTextWithAutoEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            var utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(bytes);
        }
        catch
        {
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}
