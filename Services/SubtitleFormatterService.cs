using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SubtitleMaster.Helpers;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public class SubtitleFormatterService
{
    private static SubtitleFormatterService? _instance;
    public static SubtitleFormatterService Instance => _instance ??= new SubtitleFormatterService();

    private static readonly TimeSpan MinReadableDuration = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Validates and sanitizes raw subtitle entries.
    /// Checks for monotonic ordering, non-overlapping or clean spans, and non-empty text.
    /// </summary>
    public List<SubtitleItem> ValidateAndClean(List<SubtitleItem> rawItems)
    {
        if (rawItems == null || rawItems.Count == 0)
        {
            throw new InvalidOperationException("Subtitle list is empty.");
        }

        var valid = new List<SubtitleItem>();

        foreach (var item in rawItems)
        {
            string target = (item.TargetText ?? string.Empty).Trim();
            string source = (item.SourceText ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(source))
            {
                continue;
            }

            if (string.IsNullOrEmpty(target)) target = source;
            target = NormalizeChineseSubtitleText(target);
            source = string.IsNullOrEmpty(source) ? target : NormalizeSourceSubtitleText(source);

            item.TargetText = target;
            item.SourceText = source;

            if (item.StartTime < TimeSpan.Zero) item.StartTime = TimeSpan.Zero;
            if (item.EndTime <= item.StartTime)
            {
                item.EndTime = item.StartTime.Add(TimeSpan.FromSeconds(2.0));
            }

            valid.Add(item);
        }

        if (valid.Count == 0)
        {
            throw new InvalidOperationException("All subtitle items had empty text.");
        }

        // Sort chronologically (if start times are identical, put longer utterance first so it stays on the bottom track)
        valid = valid
            .OrderBy(x => x.StartTime)
            .ThenByDescending(x => x.EndTime - x.StartTime)
            .ToList();

        // Dual-Track Interval Scheduler:
        // Assign genuine simultaneous multi-speaker overlaps to the Top Track (IsTopTrack = true)
        // while keeping normal sequential dialogue and minor turn-taking collisions on the Bottom Track (IsTopTrack = false).
        SubtitleItem? activeBottom = null;
        foreach (var item in valid)
        {
            item.IsTopTrack = false;

            if (activeBottom != null && item.StartTime < activeBottom.EndTime)
            {
                TimeSpan overlapEnd = item.EndTime < activeBottom.EndTime ? item.EndTime : activeBottom.EndTime;
                double overlapSec = (overlapEnd - item.StartTime).TotalSeconds;
                double tailCutSec = (activeBottom.EndTime - item.StartTime).TotalSeconds;
                double durA = Math.Max(0.1, (activeBottom.EndTime - activeBottom.StartTime).TotalSeconds);
                double durB = Math.Max(0.1, (item.EndTime - item.StartTime).TotalSeconds);
                double minDur = Math.Min(durA, durB);
                double overlapRatio = overlapSec / minDur;

                bool isSameText =
                    string.Equals(activeBottom.TargetText.Trim(), item.TargetText.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(activeBottom.SourceText.Trim(), item.SourceText.Trim(), StringComparison.OrdinalIgnoreCase);

                bool isGenuineDualSpeaker = !isSameText &&
                    ((overlapSec >= 0.35 && overlapRatio >= 0.30) ||
                     (tailCutSec >= 0.45 && overlapRatio >= 0.60));

                if (isGenuineDualSpeaker)
                {
                    item.IsTopTrack = true;
                    continue;
                }
            }

            activeBottom = item;
        }

        // Apply software-side timing padding (-100ms start, +100ms end), minimum duration extension, and non-overlap clamping independently per track
        var bottomTrack = valid.Where(x => !x.IsTopTrack).ToList();
        var topTrack = valid.Where(x => x.IsTopTrack).ToList();

        ApplyTrackPaddingAndClamping(bottomTrack);
        ApplyTrackPaddingAndClamping(topTrack);

        // Re-sort combined list and assign final indices and formatted timestamps
        valid = valid
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.IsTopTrack ? 1 : 0)
            .ToList();

        for (int i = 0; i < valid.Count; i++)
        {
            valid[i].Index = i + 1;
            valid[i].Start = TimeHelper.ToSrtTime(valid[i].StartTime);
            valid[i].End = TimeHelper.ToSrtTime(valid[i].EndTime);
        }

        return valid;
    }

    /// <summary>
    /// Normalizes Chinese subtitle text according to Netflix Timed Text Style Guide (Section 2, 11, 12):
    /// - Converts full-width ASCII numbers and letters (０-９, Ａ-Ｚ, ａ-ｚ) to half-width
    /// - Normalizes katakana/variant middle dots (・, ∙) to standard Chinese interpunct (·, U+00B7)
    /// - Normalizes ellipses (..., ⋯) to standard Unicode ellipsis (……, U+2026)
    /// - Collapses compound exclamation/question marks (!?, ?!, ??, !!) into a single full-width ？ or ！
    /// - Strips trailing periods (。), commas (，), enumeration commas (、), and semicolons (；) at line ends (including before closing quotes)
    /// - Replaces mid-sentence commas (，) and semicolons (；) with a single space while preserving numeric separators (e.g. 1,000)
    /// </summary>
    public static string NormalizeChineseSubtitleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // 1. Convert full-width numbers/letters to half-width & normalize interpuncts
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if ((c >= '\uFF10' && c <= '\uFF19') || // ０-９
                (c >= '\uFF21' && c <= '\uFF3A') || // Ａ-Ｚ
                (c >= '\uFF41' && c <= '\uFF5A'))   // ａ-ｚ
            {
                sb.Append((char)(c - 0xFEE0));
            }
            else if (c == '\u30FB' || c == '\u2219') // ・ or ∙ -> ·
            {
                sb.Append('\u00B7');
            }
            else
            {
                sb.Append(c);
            }
        }

        string s = sb.ToString();

        // Strip leaked foreign phonetic particles (e.g. Japanese Hiragana/Katakana like 'なのよ' or Korean Hangul) mixed into Chinese target_text
        if (Regex.IsMatch(s, @"[\u4e00-\u9fa5]"))
        {
            s = Regex.Replace(s, @"[\u3040-\u309F\u30A0-\u30FF\uAC00-\uD7AF\u3130-\u318F]+", string.Empty);
        }

        // 2. Normalize ellipses: midline ⋯ (U+22EF) or 3+ ASCII dots -> …… (U+2026)
        s = Regex.Replace(s, @"\.{3,}|[\u22EF\u2026]+", "……");

        // 3. Normalize compound ? and ! (Prohibit !?, ?!, ??, !! per Section 12)
        s = Regex.Replace(s, @"[?？!！]{2,}", match =>
        {
            string val = match.Value;
            return (val.Contains('?') || val.Contains('？')) ? "？" : "！";
        });

        // Convert standalone half-width ? or ! adjacent to Chinese characters or end of string to full-width
        s = Regex.Replace(s, @"(?<=[\u4e00-\u9fa5""”'’》）])\?", "？");
        s = Regex.Replace(s, @"(?<=[\u4e00-\u9fa5""”'’》）])!", "！");

        // 4. Process line by line in case of multi-line subtitle text
        var lines = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Strip trailing periods/commas/enumeration commas/semicolons before closing quotes/brackets at the end of a line
            line = Regex.Replace(line, @"[。，、；,;]+(?=[”’）》""'\)\]]+$)", string.Empty);

            // Strip trailing periods/commas/enumeration commas/semicolons at the very end of the line
            line = Regex.Replace(line, @"[。，、；,;]+$", string.Empty);
            // Also strip trailing ASCII period if preceded by a Chinese character
            line = Regex.Replace(line, @"(?<=[\u4e00-\u9fa5])\.$", string.Empty);

            // If a comma/period is inside a mid-line closing quote (e.g. “你好，”然后), move the pause space outside the closing quote
            line = Regex.Replace(line, @"[，；。]+([”’）》])", "$1 ");

            // Replace mid-line Chinese commas, semicolons, and periods with a single space
            line = Regex.Replace(line, @"[，；。]+", " ");
            // Replace ASCII comma/semicolon when adjacent to Chinese characters (preserving numeric 1,000)
            line = Regex.Replace(line, @"(?<=[\u4e00-\u9fa5])\s*[,;]\s*|\s*[,;]\s*(?=[\u4e00-\u9fa5])", " ");

            // Remove spaces on both sides of colons, enumeration commas, and interpuncts (：, 、, ·)
            line = Regex.Replace(line, @"\s*([：、·])\s*", "$1");
            // Remove spaces immediately inside opening quotes/brackets (“ ‘ 《 （) and before closing quotes/brackets (” ’ 》 ）)
            line = Regex.Replace(line, @"([“‘《（])\s+", "$1");
            line = Regex.Replace(line, @"\s+([”’》）])", "$1");

            // Collapse multiple spaces into a single half-width space
            line = Regex.Replace(line, @"\s{2,}", " ").Trim();

            lines[i] = line;
        }

        return string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));
    }

    /// <summary>
    /// Universal 3-Layer Script-Adaptive Subtitle Normalizer for source_text across all languages:
    /// - Layer 1 (Universal): Full-width ASCII alphanumeric to half-width, compound punctuation (!?, ?!, ??, !!) collapsing, whitespace cleanup.
    /// - Layer 2A (CJK Scriptio Continua - Chinese/Japanese): If Chinese without kana, applies Chinese subtitle rules; if Japanese (contains Hiragana/Katakana), strips trailing periods/commas and converts mid-line commas (、/，) to single spaces while preserving katakana middle dot (・).
    /// - Layer 2B (Space-Delimited Scripts - English, European, Cyrillic, Korean, etc.): Preserves mid-sentence commas (,) for grammatical clarity while stripping trailing single periods (.) and trailing commas/semicolons at line ends.
    /// </summary>
    public static string NormalizeSourceSubtitleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        bool hasKana = Regex.IsMatch(text, @"[\u3040-\u309F\u30A0-\u30FF]");
        bool hasHanzi = Regex.IsMatch(text, @"[\u4E00-\u9FA5]");

        // Pure Chinese source text (e.g. Mandarin/Cantonese audio) -> use the full Chinese subtitle normalizer
        if (hasHanzi && !hasKana)
        {
            return NormalizeChineseSubtitleText(text);
        }

        // Layer 1: Universal full-width alphanumeric to half-width conversion
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if ((c >= '\uFF10' && c <= '\uFF19') || // ０-９
                (c >= '\uFF21' && c <= '\uFF3A') || // Ａ-Ｚ
                (c >= '\uFF41' && c <= '\uFF5A'))   // ａ-ｚ
            {
                sb.Append((char)(c - 0xFEE0));
            }
            else
            {
                sb.Append(c);
            }
        }

        string s = sb.ToString();

        if (hasKana)
        {
            // Layer 2A: Japanese (CJK non-spaced script)
            s = Regex.Replace(s, @"\.{3,}|[\u22EF\u2026]+", "……");
            s = Regex.Replace(s, @"[?？!！]{2,}", match =>
            {
                string val = match.Value;
                return (val.Contains('?') || val.Contains('？')) ? "？" : "！";
            });
            s = Regex.Replace(s, @"\?", "？");
            s = Regex.Replace(s, @"!", "！");

            var jpLines = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < jpLines.Length; i++)
            {
                string line = jpLines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Strip trailing periods/commas before closing quotes/brackets or at line end
                line = Regex.Replace(line, @"[。．，、；,;]+(?=[」』”’）》""'\)\]]+$)", string.Empty);
                line = Regex.Replace(line, @"[。．，、；,;]+$", string.Empty);

                // Replace mid-line Japanese commas/periods with a single space
                line = Regex.Replace(line, @"[、，。．]+", " ");

                // Clean spaces inside Japanese brackets/quotes and around katakana middle dot ・
                line = Regex.Replace(line, @"\s*([・：])\s*", "$1");
                line = Regex.Replace(line, @"([「『（“])\s+", "$1");
                line = Regex.Replace(line, @"\s+([」』）”])", "$1");
                line = Regex.Replace(line, @"\s{2,}", " ").Trim();

                jpLines[i] = line;
            }

            return string.Join("\n", jpLines.Where(l => !string.IsNullOrEmpty(l)));
        }
        else
        {
            // Layer 2B: Space-Delimited Scripts (English, European languages, Russian, Korean, etc.)
            // Normalize ellipsis to standard 3 ASCII dots (...)
            s = Regex.Replace(s, @"\.{3,}|[\u22EF\u2026]+", "...");

            // Collapse compound ? and ! into single half-width ? or !
            s = Regex.Replace(s, @"[?？!！]{2,}", match =>
            {
                string val = match.Value;
                return (val.Contains('?') || val.Contains('？')) ? "?" : "!";
            });

            var lines = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Strip trailing commas, semicolons, or CJK periods at line end (including before closing quotes)
                line = Regex.Replace(line, @"[。，、；,;]+(?=[""”'’\)\]]*$)", string.Empty);

                // Strip single trailing period '.' at the end of a subtitle line (preserving '...' ellipsis)
                line = Regex.Replace(line, @"(?<!\.)\.(?=[""”'’\)\]]*$)", string.Empty);

                // Collapse multiple spaces into a single space while preserving mid-sentence commas
                line = Regex.Replace(line, @"\s{2,}", " ").Trim();

                lines[i] = line;
            }

            return string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));
        }
    }

    private static void ApplyTrackPaddingAndClamping(List<SubtitleItem> trackItems)
    {
        if (trackItems.Count == 0) return;

        for (int i = 0; i < trackItems.Count; i++)
        {
            var paddedStart = trackItems[i].StartTime; // 0ms start advance (previously -100ms)
            if (paddedStart < TimeSpan.Zero) paddedStart = TimeSpan.Zero;
            if (i > 0 && paddedStart < trackItems[i - 1].EndTime)
            {
                paddedStart = trackItems[i - 1].EndTime;
            }
            trackItems[i].StartTime = paddedStart;

            var paddedEnd = trackItems[i].EndTime + TimeSpan.FromMilliseconds(100);
            if (i < trackItems.Count - 1 && paddedEnd > trackItems[i + 1].StartTime)
            {
                paddedEnd = trackItems[i].EndTime;
            }
            trackItems[i].EndTime = paddedEnd;

            // Ensure minimum readable duration (800ms) by extending EndTime forward when space before the next subtitle permits
            var currentDuration = trackItems[i].EndTime - trackItems[i].StartTime;
            if (currentDuration < MinReadableDuration)
            {
                var targetEnd = trackItems[i].StartTime + MinReadableDuration;
                if (i < trackItems.Count - 1 && targetEnd > trackItems[i + 1].StartTime)
                {
                    targetEnd = trackItems[i + 1].StartTime;
                }
                if (targetEnd > trackItems[i].EndTime)
                {
                    trackItems[i].EndTime = targetEnd;
                }
            }
        }

        for (int i = 1; i < trackItems.Count; i++)
        {
            if (trackItems[i].StartTime < trackItems[i - 1].EndTime)
            {
                trackItems[i - 1].EndTime = trackItems[i].StartTime;
                if (trackItems[i - 1].EndTime <= trackItems[i - 1].StartTime)
                {
                    trackItems[i - 1].EndTime = trackItems[i - 1].StartTime + TimeSpan.FromMilliseconds(150);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a language descriptor represents Chinese (Simplified/Traditional).
    /// </summary>
    public static bool IsChineseLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return true;
        string l = lang.Trim().ToLowerInvariant();
        return l.Contains("zh") || l.Contains("中文") || l.Contains("汉语") || l.Contains("华语") || l.Contains("chinese") || l.Contains("简体") || l.Contains("繁体");
    }

    /// <summary>
    /// Constructs the ending watermark attribution text in Chinese or English.
    /// </summary>
    public static string GetWatermarkText(string? modelName, string? targetLanguage)
    {
        string model = string.IsNullOrWhiteSpace(modelName) ? "AI" : modelName.Trim();
        bool isChinese = IsChineseLanguage(targetLanguage);
        if (isChinese)
        {
            return $"本字幕由灵幕助手调用{model}生成 仅供交流使用 请勿用于商业用途";
        }
        else
        {
            return $"Subtitles generated by LingMu Caption via '{model}'. For communication purposes only. Not for commercial use.";
        }
    }

    /// <summary>
    /// Generates standard SRT string content.
    /// </summary>
    public string GenerateSrtContent(
        List<SubtitleItem> items, 
        bool includeSecondary,
        bool showWatermark = false,
        string? modelName = null,
        string? targetLanguage = null,
        TimeSpan? totalDuration = null)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine($"{TimeHelper.ToSrtTime(item.StartTime)} --> {TimeHelper.ToSrtTime(item.EndTime)}");
            string prefix = item.IsTopTrack ? "{\\an8}" : string.Empty;
            sb.AppendLine($"{prefix}{item.TargetText}");

            if (includeSecondary &&
                !string.IsNullOrWhiteSpace(item.SourceText))
            {
                sb.AppendLine(item.SourceText);
            }

            sb.AppendLine();
        }

        // Ending watermark attribution in last few seconds
        if (showWatermark && items.Count > 0)
        {
            TimeSpan videoEnd = (totalDuration.HasValue && totalDuration.Value > TimeSpan.Zero)
                ? totalDuration.Value
                : items.Max(i => i.EndTime);

            double endSec = videoEnd.TotalSeconds;
            double wmDuration = Math.Min(4.5, Math.Max(2.0, endSec * 0.25));
            TimeSpan wmStart = TimeSpan.FromSeconds(Math.Max(0.0, endSec - wmDuration));
            TimeSpan wmEnd = videoEnd;

            if (wmEnd > wmStart)
            {
                string wmText = GetWatermarkText(modelName, targetLanguage);
                sb.AppendLine((items.Count + 1).ToString());
                sb.AppendLine($"{TimeHelper.ToSrtTime(wmStart)} --> {TimeHelper.ToSrtTime(wmEnd)}");
                sb.AppendLine(wmText);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates ASS (Advanced SubStation Alpha) styled effect subtitle content.
    /// </summary>
    public string GenerateAssContent(
        List<SubtitleItem> items, 
        EffectSubtitleConfig config, 
        bool includeSecondary,
        bool showWatermark = false,
        string? modelName = null,
        string? targetLanguage = null,
        TimeSpan? totalDuration = null)
    {
        var sb = new StringBuilder();

        // 1. Script Info
        sb.AppendLine("[Script Info]");
        sb.AppendLine("; Script generated by LingMu Caption");
        sb.AppendLine("Title: LingMu Caption Effect Subtitles");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine("YCbCr Matrix: TV.709");
        sb.AppendLine("PlayResX: 1920");
        sb.AppendLine("PlayResY: 1080");
        sb.AppendLine();

        // Convert Colors to ASS format: &HAABBGGRR
        string primaryAss = HexColorToAss(config.PrimaryColor);
        string secondaryAss = HexColorToAss(config.SecondaryColor);
        string outlineAss = HexColorToAss(config.OutlineColor);
        string shadowAss = HexColorToAss(config.ShadowColor);

        // 2. V4+ Styles
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        
        int boldVal = config.Bold ? -1 : 0;
        int italicVal = config.Italic ? -1 : 0;
        int spacingVal = (int)Math.Round(config.LetterSpacing);
        int marginL = config.MarginL > 0 ? config.MarginL : 30;
        int marginR = config.MarginR > 0 ? config.MarginR : 30;
        int marginV = config.MarginV > 0 ? config.MarginV : 45;
        int alignment = config.Alignment > 0 ? config.Alignment : 2;
        int topAlignment = 8; // Top-Center (\an8) for simultaneous second speaker
        int mainFontSize = Math.Max(1, config.FontSize);

        // Primary Main Style (Bottom Track)
        sb.AppendLine($"Style: Default,{config.FontName},{mainFontSize},{primaryAss},&H000000FF,{outlineAss},{shadowAss},{boldVal},{italicVal},0,0,100,100,{spacingVal},0,1,{config.OutlineWidth:0.#},{config.ShadowDepth:0.#},{alignment},{marginL},{marginR},{marginV},1");

        // Simultaneous Speaker Top Style (Top Track, Alignment = 8 Top-Center)
        sb.AppendLine($"Style: TopDefault,{config.FontName},{mainFontSize},{primaryAss},&H000000FF,{outlineAss},{shadowAss},{boldVal},{italicVal},0,0,100,100,{spacingVal},0,1,{config.OutlineWidth:0.#},{config.ShadowDepth:0.#},{topAlignment},{marginL},{marginR},{marginV},1");
        
        // Secondary Subtitle Style (scaled font, secondary color, 65% proportional outline & shadow, unbolded)
        double secScale = config.SecondaryScale > 0 ? (config.SecondaryScale <= 1.0 ? config.SecondaryScale : config.SecondaryScale / 100.0) : 0.70;
        int subFontSize = Math.Max(12, (int)Math.Round(
            mainFontSize * secScale,
            MidpointRounding.AwayFromZero));
        int subMarginV = Math.Max(10, marginV - 20);
        string secFontName = !string.IsNullOrWhiteSpace(config.SecondaryFontName) ? config.SecondaryFontName : config.FontName;
        double subOutlineWidth = config.OutlineWidth > 0 ? Math.Max(0.8, Math.Round(config.OutlineWidth * 0.65, 1)) : 0.0;
        double subShadowDepth = config.ShadowDepth > 0 ? Math.Round(config.ShadowDepth * 0.65, 1) : 0.0;
        string subOutlineStr = subOutlineWidth.ToString("0.#", CultureInfo.InvariantCulture);
        string subShadowStr = subShadowDepth.ToString("0.#", CultureInfo.InvariantCulture);
        sb.AppendLine($"Style: Secondary,{secFontName},{subFontSize},{secondaryAss},&H000000FF,{outlineAss},{shadowAss},0,{italicVal},0,0,100,100,{spacingVal},0,1,{subOutlineStr},{subShadowStr},{alignment},{marginL},{marginR},{subMarginV},1");

        // Top Watermark Style (Alignment 8 = Top Center, FontSize 26 = Small subtle disclaimer, MarginV 24 = from top)
        sb.AppendLine($"Style: Watermark,{config.FontName},26,&H20FFFFFF,&H000000FF,&H30000000,&H60000000,0,0,0,0,100,100,0,0,1,1.2,0.8,8,30,30,24,1");
        sb.AppendLine();

        // 3. Events
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        double animSpeed = config.AnimationSpeed > 0.1 ? config.AnimationSpeed : 1.0;
        int ScaleMs(int baseMs) => Math.Max(20, (int)Math.Round(baseMs / animSpeed));

        // Adjustable edge softening (default EdgeBlur = 1.0 -> \be1; > 0 -> \blurX; <= 0 -> disabled)
        bool useEdgeSoftening = config.EdgeBlur > 0.01 && (config.OutlineWidth > 0 || config.ShadowDepth > 0) && config.Animation != AnimationPreset.BlurGlow;
        string edgeBlurTag = !useEdgeSoftening
            ? ""
            : (Math.Abs(config.EdgeBlur - 1.0) < 0.05
                ? "\\be1"
                : $"\\blur{config.EdgeBlur.ToString("0.#", CultureInfo.InvariantCulture)}");

        string animTag = config.Animation switch
        {
            AnimationPreset.FadeInOut => "{" + edgeBlurTag + "\\fad(" + ScaleMs(220) + "," + ScaleMs(220) + ")}",
            AnimationPreset.PopIn => "{" + edgeBlurTag + "\\fad(" + ScaleMs(80) + "," + ScaleMs(80) + ")\\fscx82\\fscy82\\t(0," + ScaleMs(160) + ",\\fscx100\\fscy100)}",
            AnimationPreset.SlideUp => "{" + edgeBlurTag + "\\fad(" + ScaleMs(120) + "," + ScaleMs(120) + ")\\t(0," + ScaleMs(180) + ",\\fscy108)\\t(" + ScaleMs(180) + "," + ScaleMs(300) + ",\\fscy100)}",
            AnimationPreset.BlurGlow => "{\\fad(" + ScaleMs(160) + "," + ScaleMs(160) + ")\\blur3\\t(0," + ScaleMs(200) + ",\\blur0.8)}",
            AnimationPreset.KaraokeSweep => "{" + edgeBlurTag + "\\fad(" + ScaleMs(120) + "," + ScaleMs(120) + ")\\t(0," + ScaleMs(250) + ",\\c&H0000D7FF&)\\t(" + ScaleMs(250) + "," + ScaleMs(500) + ",\\c&H00FFFFFF&)}",
            _ => useEdgeSoftening ? "{" + edgeBlurTag + "}" : ""
        };

        string secFontTag = !string.IsNullOrWhiteSpace(config.SecondaryFontName) ? $"\\fn{config.SecondaryFontName}" : "";

        foreach (var item in items)
        {
            string startStr = TimeHelper.ToAssTime(item.StartTime);
            string endStr = TimeHelper.ToAssTime(item.EndTime);
            string styleName = item.IsTopTrack ? "TopDefault" : "Default";

            // Escape ASS text
            string targetText = EscapeAssText(item.TargetText);

            if (includeSecondary &&
                !string.IsNullOrWhiteSpace(item.SourceText))
            {
                string sourceText = EscapeAssText(item.SourceText);
                // Bilingual stacked text with proportional 65% outline/shadow, unbolded weight (\b0), and edge softening (\be1)
                string subStyleOverrides = $"\\fs{subFontSize}\\b0\\bord{subOutlineStr}\\shad{subShadowStr}{edgeBlurTag}\\c{secondaryAss}{secFontTag}";
                string spacingTag = config.SubSpacing > 0
                    ? $"\\N{{\\fs{config.SubSpacing}\\alpha&HFF&\\bord0\\shad0}}\\h\\N{{\\r{styleName}{subStyleOverrides}}}"
                    : $"\\N{{{subStyleOverrides}}}";
                string dialogue = $"{animTag}{targetText}{spacingTag}{sourceText}";
                sb.AppendLine($"Dialogue: 0,{startStr},{endStr},{styleName},,0,0,0,,{dialogue}");
            }
            else
            {
                string dialogue = $"{animTag}{targetText}";
                sb.AppendLine($"Dialogue: 0,{startStr},{endStr},{styleName},,0,0,0,,{dialogue}");
            }
        }

        // Ending watermark attribution in last few seconds (Top Center, small font, smooth fade-in/out)
        if (showWatermark && items.Count > 0)
        {
            TimeSpan videoEnd = (totalDuration.HasValue && totalDuration.Value > TimeSpan.Zero)
                ? totalDuration.Value
                : items.Max(i => i.EndTime);

            double endSec = videoEnd.TotalSeconds;
            double wmDuration = Math.Min(4.5, Math.Max(2.0, endSec * 0.25));
            TimeSpan wmStart = TimeSpan.FromSeconds(Math.Max(0.0, endSec - wmDuration));
            TimeSpan wmEnd = videoEnd;

            if (wmEnd > wmStart)
            {
                string wmStartStr = TimeHelper.ToAssTime(wmStart);
                string wmEndStr = TimeHelper.ToAssTime(wmEnd);
                string wmText = GetWatermarkText(modelName, targetLanguage);
                string escapedWm = EscapeAssText(wmText);
                sb.AppendLine($"Dialogue: 1,{wmStartStr},{wmEndStr},Watermark,,0,0,0,,{{\\fad(350,350)}}{escapedWm}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detects if the source audio language is the same as the target language.
    /// If so, translation is not needed and secondary subtitles should automatically be disabled.
    /// </summary>
    public bool DetectSameLanguage(List<SubtitleItem> items, string targetLanguage)
    {
        if (items == null || items.Count == 0) return false;

        int identicalCount = 0;
        int evaluatedCount = 0;
        int chineseSourceCount = 0;
        int englishSourceCount = 0;

        foreach (var item in items)
        {
            string src = (item.SourceText ?? string.Empty).Trim();
            string tgt = (item.TargetText ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(src) && string.IsNullOrEmpty(tgt)) continue;
            evaluatedCount++;

            string cleanSrc = NormalizeText(src);
            string cleanTgt = NormalizeText(tgt);

            if (string.Equals(cleanSrc, cleanTgt, StringComparison.OrdinalIgnoreCase))
            {
                identicalCount++;
            }

            if (ContainsChinese(src)) chineseSourceCount++;
            if (IsPredominantlyLatin(src)) englishSourceCount++;
        }

        if (evaluatedCount == 0) return false;

        // Condition 1: >= 60% of items have essentially identical source and target text
        if ((double)identicalCount / evaluatedCount >= 0.60)
        {
            return true;
        }

        // Condition 2: Target is Chinese (zh-CN / zh) and source speech is predominantly Chinese (>= 60%)
        bool isTargetChinese = targetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (isTargetChinese && (double)chineseSourceCount / evaluatedCount >= 0.60)
        {
            return true;
        }

        // Condition 3: Target is English and source speech is predominantly Latin (>= 70%)
        bool isTargetEnglish = targetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (isTargetEnglish && (double)englishSourceCount / evaluatedCount >= 0.70)
        {
            return true;
        }

        return false;
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    public static bool ContainsChinese(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int count = 0;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) count++;
        }
        return count >= Math.Max(1, text.Length * 0.35);
    }

    public static bool IsPredominantlyLatin(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int latin = 0, total = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                total++;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
            }
        }
        return total > 0 && ((double)latin / total) > 0.8;
    }

    public static string HexColorToAss(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "&H00FFFFFF";
        hex = hex.TrimStart('#');

        byte a = 0; // ASS alpha 0 = fully opaque
        byte r = 255, g = 255, b = 255;

        try
        {
            if (hex.Length == 6)
            {
                r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            }
            else if (hex.Length == 8)
            {
                byte hexA = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                a = (byte)(255 - hexA); // Invert for ASS alpha (0 = opaque, 255 = transparent)
                r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
            }
        }
        catch { }

        // ASS order: &HAABBGGRR
        return $"&H{a:X2}{b:X2}{g:X2}{r:X2}";
    }

    private static string EscapeAssText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("{", "(").Replace("}", ")").Replace("\r\n", "\\N").Replace("\n", "\\N");
    }
}
