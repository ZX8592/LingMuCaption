using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SubtitleMaster.Helpers;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public sealed record AudioSegmentChunk(
    int Index,
    string FilePath,
    TimeSpan NominalStart,
    TimeSpan NominalEnd,
    TimeSpan ExtractStart,
    TimeSpan ExtractEnd);

public class CliTranscriptionService
{
    private static CliTranscriptionService? _instance;
    public static CliTranscriptionService Instance => _instance ??= new CliTranscriptionService();

    private const string DefaultFallbackModel = "gemini-4-neon-high";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions StructuredJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly ConcurrentDictionary<string, HashSet<string>> _taskConversationIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cliLogLock = new();
    private static readonly object _conversationCleanupLock = new();

    private sealed class CliGlossaryEntry
    {
        [JsonPropertyName("k")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("v")]
        public string Target { get; set; } = string.Empty;
    }

    private sealed class CliSubtitleEntry
    {
        [JsonPropertyName("i")]
        public int Index { get; set; }

        [JsonPropertyName("a")]
        public string? AudioFile { get; set; }

        [JsonPropertyName("s")]
        public string Start { get; set; } = string.Empty;

        [JsonPropertyName("e")]
        public string End { get; set; } = string.Empty;

        [JsonPropertyName("src")]
        public string SourceText { get; set; } = string.Empty;

        [JsonPropertyName("tgt")]
        public string TargetText { get; set; } = string.Empty;

        [JsonPropertyName("f")]
        public string? Flag { get; set; }

        [JsonPropertyName("p")]
        public bool? IsPrimary { get; set; }

        public SubtitleItem ToSubtitleItem() => new()
        {
            Index = Index,
            AudioFile = AudioFile,
            Start = Start,
            End = End,
            SourceText = SourceText,
            TargetText = TargetText,
            Flag = Flag,
            IsPrimary = IsPrimary
        };
    }

    private sealed class CliTurnPayload
    {
        [JsonPropertyName("g")]
        public List<CliGlossaryEntry> Glossary { get; set; } = new();

        [JsonPropertyName("sub")]
        public List<CliSubtitleEntry> Subtitles { get; set; } = new();
    }

    private sealed record CliTurnResult(string? ConversationId, CliTurnPayload Payload);

    public static bool IsSupportedGeminiModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && modelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
        && !modelId.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase)
        && !modelId.Contains("orchestrator", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> CreateCliEnvironment() => new()
    {
        ["CI"] = "1",
        ["TERM"] = "dumb",
        ["NO_COLOR"] = "1",
        ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"
    };

    private (bool IsReady, string Message, List<string> Models)? _cachedStatus;
    private string? _cachedCliPath;
    private readonly SemaphoreSlim _checkCliLock = new(1, 1);

    /// <summary>
    /// Checks if Antigravity CLI is ready and returns list of available Gemini models.
    /// Caches result in-memory to prevent repeated process spawns when idle or switching pages.
    /// Uses native SilentProcessRunner with DETACHED_PROCESS to eliminate any console window flicker.
    /// </summary>
    public async Task<(bool IsReady, string Message, List<string> Models)> CheckCliStatusAsync(string cliPath, bool forceRefresh = false)
    {
        string resolvedPath = ResolveCliPath(cliPath);

        // Fast-path: only return cached result if it was ALREADY verified ready (IsReady == true) and not forcing refresh
        if (!forceRefresh && _cachedStatus.HasValue && _cachedStatus.Value.IsReady && string.Equals(_cachedCliPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedStatus.Value;
        }

        await _checkCliLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedStatus.HasValue && _cachedStatus.Value.IsReady && string.Equals(_cachedCliPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedStatus.Value;
            }

            var models = new List<string>();

            if (!File.Exists(resolvedPath) && resolvedPath == "antigravity.exe")
            {
                // Try to see if it's on PATH using detached runner
                try
                {
                    var testRes = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
                    {
                        FileName = resolvedPath,
                        Arguments = "--version",
                        EnvironmentVariables = CreateCliEnvironment()
                    });

                    if (testRes.ExitCode != 0)
                    {
                        _cachedStatus = null;
                        return (false, "CLI returned non-zero exit code.", models);
                    }
                }
                catch (Exception ex)
                {
                    _cachedStatus = null;
                    return (false, $"Cannot launch CLI: {ex.Message}", models);
                }
            }
            else if (!File.Exists(resolvedPath))
            {
                _cachedStatus = null;
                return (false, $"CLI executable not found at: {resolvedPath}", models);
            }

            try
            {
                var runRes = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
                {
                    FileName = resolvedPath,
                    Arguments = "models",
                    EnvironmentVariables = CreateCliEnvironment()
                });

                if (runRes.ExitCode == 0)
                {
                    var lines = runRes.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("Fetching available models") || string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            string modelId = parts[0].Trim();
                            if (IsSupportedGeminiModel(modelId) && !models.Contains(modelId, StringComparer.OrdinalIgnoreCase))
                            {
                                models.Add(modelId);
                            }
                        }
                    }

                    if (models.Count == 0)
                    {
                        models.Add(DefaultFallbackModel);
                        models.Add("gemini-4-hydrogen-high");
                        models.Add("gemini-4-argon-high");
                    }

                    _cachedCliPath = resolvedPath;
                    _cachedStatus = (true, "CLI is active and ready.", models);
                    return _cachedStatus.Value;
                }

                _cachedStatus = null;
                string rawErr = $"{runRes.StandardError} {runRes.StandardOutput}";
                string friendlyMsg = IsAuthIssue(rawErr)
                    ? "CLI 未登录，请手动双击运行 tools/antigravity.exe 完成登录后重试"
                    : $"CLI returned exit code {runRes.ExitCode}: {runRes.StandardError}";
                return (false, friendlyMsg, models);
            }
            catch (Exception ex)
            {
                _cachedStatus = null;
                string friendlyMsg = IsAuthIssue(ex.Message)
                    ? "CLI 未登录，请手动双击运行 tools/antigravity.exe 完成登录后重试"
                    : $"Error probing CLI models: {ex.Message}";
                return (false, friendlyMsg, models);
            }
        }
        finally
        {
            _checkCliLock.Release();
        }
    }

    public static List<AudioSegmentChunk> BuildAudioSegments(TimeSpan totalDuration)
    {
        var segments = new List<AudioSegmentChunk>();
        var chunk = TimeSpan.FromMinutes(12);
        var overlap = TimeSpan.FromSeconds(10);

        if (totalDuration <= TimeSpan.Zero || totalDuration <= chunk)
        {
            TimeSpan nominalEnd = totalDuration > TimeSpan.Zero ? totalDuration : TimeSpan.FromHours(4);
            TimeSpan extractEnd = totalDuration > TimeSpan.Zero ? totalDuration : TimeSpan.Zero;
            segments.Add(new AudioSegmentChunk(1, string.Empty, TimeSpan.Zero, nominalEnd, TimeSpan.Zero, extractEnd));
            return segments;
        }

        TimeSpan cur = TimeSpan.Zero;
        int index = 1;
        while (cur < totalDuration)
        {
            TimeSpan next = cur + chunk;
            if (next > totalDuration || (totalDuration - next < TimeSpan.FromMinutes(2)))
            {
                next = totalDuration;
            }

            TimeSpan extractEnd = next < totalDuration
                ? (next + overlap <= totalDuration ? next + overlap : totalDuration)
                : totalDuration;

            segments.Add(new AudioSegmentChunk(index++, string.Empty, cur, next, cur, extractEnd));
            cur = next;
        }

        return segments;
    }

    private const long MaxSingleBatchMediaBytes = 20L * 1000 * 1000;
    private const int MaxSegmentsPerSession = 3;
    private const int MaxPriorTranslatedContextLines = 1000;
    private const int BoundaryBilingualOverlapLines = 4;
    private const int MaxPriorStage1SearchChars = 10000;

    private static string FormatLocalTimestamp(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    /// <summary>
    /// Splits pre-cut 12-minute (+10s tail) audio segments into independent CLI sessions of up to 3 segments (36 min)
    /// and <=20MB per session. Each session runs Stage 1 -> Stage 2 -> Stage 3, passing only locked glossary terms
    /// and minimal target-text-only prior lines (up to 1000 lines + last few bilingual boundary lines) to subsequent sessions.
    /// </summary>
    public async Task<List<SubtitleItem>> TranscribeAudioAsync(
        IReadOnlyList<AudioSegmentChunk> audioSegments,
        TimeSpan totalDuration,
        string targetLanguage,
        string modelName,
        string cliPath,
        TranslationStyle translationStyle = TranslationStyle.Balanced,
        SegmentationStyle segmentationStyle = SegmentationStyle.Standard,
        WebSearchMode webSearchMode = WebSearchMode.Accurate,
        IProgress<double>? progress = null,
        Action<string>? onStatusUpdate = null,
        CancellationToken cancellationToken = default)
    {
        if (audioSegments == null || audioSegments.Count == 0)
        {
            throw new ArgumentException("At least one audio segment is required.", nameof(audioSegments));
        }

        string resolvedCli = ResolveCliPath(cliPath);
        if (!File.Exists(resolvedCli) && resolvedCli != "antigravity.exe")
        {
            throw new FileNotFoundException($"Antigravity CLI not found at: {resolvedCli}");
        }

        string effectiveModel = NormalizeSupportedModelName(modelName);

        var normalizedSegments = audioSegments
            .Select(s => s with { FilePath = Path.GetFullPath(s.FilePath).Replace('\\', '/') })
            .ToList();
        string audioDir = Path.GetDirectoryName(normalizedSegments[0].FilePath) ?? Path.GetTempPath();
        var (stage2SchemaPath, stage3SchemaPath) = EnsureOutputSchemaFiles(audioDir);

        string styleInstruction = translationStyle switch
        {
            TranslationStyle.Literal => 
                "- Style Preference: LITERAL TRANSLATION (偏直译). Faithfully preserve the original sentence structure, nuances, and technical terminology without unnecessary paraphrasing.",
            TranslationStyle.Free => 
                "- Style Preference: FREE / LOCALIZED TRANSLATION (偏意译). Prioritize natural, fluent, and localized target language expressions. Adapt idioms, slang, and cultural metaphors into smooth video subtitles.",
            _ => 
                "- Style Preference: BALANCED TRANSLATION (标准/平衡). Achieve an optimal balance between precision/terminology fidelity and conversational fluency suitable for video subtitles."
        };

        bool isShorter = segmentationStyle == SegmentationStyle.Shorter;
        int maxCjkChars = isShorter ? 15 : 23;
        int maxNonCjkChars = isShorter ? 30 : 45;
        string modeHeader = isShorter
            ? $"- SUBTITLE SEGMENTATION: SHORTER (单行上限 CJK {maxCjkChars} 字 / 非 CJK {maxNonCjkChars} 字符):"
            : $"- SUBTITLE SEGMENTATION: STANDARD (单行上限 CJK {maxCjkChars} 字 / 非 CJK {maxNonCjkChars} 字符 - 默认推荐):";

        string segInstruction =
            $"{modeHeader}\n" +
            "  * Semantic Integrity First: Prioritize complete, coherent semantic units and natural clause structures. Do NOT fragment intact grammatical sentences into isolated words or tiny phrases.\n" +
            "  * Holistic Natural Pacing: Balance semantic coherence with acoustic naturalness. Synthesize acoustic pauses, conversational breath, speaker turns, and speech tempo to decide the most natural breaking points.\n" +
            "  * Clean Boundaries (STRICT - NO ARTIFICIAL ELLIPSES): When a sentence is divided across subtitle entries, do NOT add artificial leading or trailing ellipses (\"...\", \"……\"), hyphens, or continuation dots at the break boundaries. Output clean, natural spoken words without unnecessary punctuation clutter.\n" +
            "  * Dual-Track Hard Length Limit (MANDATORY SPLIT IF EITHER LINE EXCEEDS LIMIT):\n" +
            $"    - For CJK scripts (Chinese/Japanese/Korean) in EITHER 'tgt' OR 'src', characters MUST NOT exceed {maxCjkChars} characters (CJK 字数 <= {maxCjkChars}).\n" +
            $"    - For ALL non-CJK scripts (Latin, Cyrillic, Arabic, Thai, Indic, etc.) in EITHER 'tgt' OR 'src', characters MUST NOT exceed {maxNonCjkChars} characters (非 CJK 字符数 <= {maxNonCjkChars}).\n" +
            "    - Whenever EITHER 'tgt' OR 'src' exceeds its respective limit, the sentence MUST be split at the most logical grammatical pause into separate subtitle entries with independent timestamps.";

        string searchInstruction = webSearchMode switch
        {
            WebSearchMode.Off =>
                "- WEB SEARCH RULES (OFFLINE): Do NOT perform any web searches. Rely strictly on audio context and internal knowledge.",
            _ =>
                "- WEB SEARCH RULES: Web search is permitted ONLY for overall background, domain knowledge, entity names, scenario comprehension, and extended song lyrics appearing in the audio. You MUST NOT search for line-by-line dialogue transcripts or original work texts."
        };

        string entityRetrievalInstruction = webSearchMode == WebSearchMode.Off
            ? "     * If the audio belongs to a known intellectual property (e.g. series, movie, TV series, video game, novel) or a specialized academic/technical domain: Rely strictly on your internal pre-trained knowledge base to retrieve and anchor the canonical official Chinese names (官方规范中文译名) of all key entities, participants, organizations, and specialized terminology upfront without calling web search tools.\n" +
              "     * Canonical Chinese Glyphs: Always anchor exact standard Chinese characters upfront to prevent homophone errors (同音异形字) and maintain 100% consistency across all segments."
            : "     * Post-Search Chinese Proper Noun Verification: Perform background and song lyric searches in the original spoken language first: theme/insert songs must be searched by song title and artist for their complete official lyrics (e.g., \"[Song]\" \"[Artist]\" full lyrics OR 歌詞), and the full text must be checked against the official lyrics during transcription, with acoustic hallucinations and fabrications strictly prohibited (插曲必须以歌曲名检索完整官方歌词，并在转录时全文对照官方歌词，严禁脑补臆造空耳). After all regular searches are completed, you MUST execute at least one additional `search_web` query IN CHINESE specifically for the relevant entity names, person names, and key terms (covering BOTH familiar key figures and unfamiliar terms—never skip key entities relying on memory) to retrieve their exact official Chinese characters. Do NOT include your own unverified or remembered Chinese translations of those names in the search query. Chinese proper-noun searches MUST be your final search step—once Stage 2 begins, do NOT call `search_web` at any time (use `\"f\"` instead).\n" +
              "     * Canonical Chinese Glyphs: Foreign search results lack standard Chinese characters. Always anchor the exact standard Chinese glyphs for all names and entities from that Chinese search result, preventing homophone errors (同音异形字) and maintaining 100% consistency across all segments.";

        // Partition segments into independent CLI sessions (max 3 segments / 36 min, and <= 20 MB per session)
        var sessionGroups = BuildSessionGroups(normalizedSegments);
        var (fullStage2Rules, simplifiedStage2Rules) = BuildStage2RuleBlocks(
            styleInstruction,
            segInstruction,
            maxCjkChars,
            maxNonCjkChars);

        string? selectedGlossaryCsv = SettingsService.Instance.CurrentSettings.SelectedGlossaryCsv;
        bool isGlossaryEnabled = !string.IsNullOrWhiteSpace(selectedGlossaryCsv);
        var userEntries = isGlossaryEnabled ? GlossaryService.LoadGlossary(selectedGlossaryCsv) : new List<GlossaryEntry>();
        var userLockedSources = new HashSet<string>(userEntries.Select(e => e.Source), StringComparer.OrdinalIgnoreCase);
        var accumulatedGlossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (isGlossaryEnabled)
        {
            if (userEntries.Count > 0)
            {
                AppLogService.Instance.LogInfo($"[术语表] 已从 {selectedGlossaryCsv} 加载 {userEntries.Count} 条跨视频锁定术语。");
            }
            else
            {
                AppLogService.Instance.LogInfo($"[术语表] 已启用 {selectedGlossaryCsv}（当前含 0 条有效术语，任务完成后将自动写入新专名）。");
            }
        }

        if (sessionGroups.Count > 1)
        {
            AppLogService.Instance.LogInfo(
                $"[AI转录] 长音频共 {normalizedSegments.Count} 段，将拆分为 {sessionGroups.Count} 组独立对话（每组最多 {MaxSegmentsPerSession} 段且单次音频 <= 20 MB），跨组继承已锁定译名、前序阶段一有效检索（最多 {MaxPriorStage1SearchChars} 字符）与最近 {MaxPriorTranslatedContextLines} 句极简纯译文。");
        }

        var allSubtitles = new List<SubtitleItem>();
        var accumulatedStage1Searches = new List<(string Query, string Summary)>();
        var seenStage1SearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double currentReportedPct = 10.0;
        void ReportMonotonicProgress(double pct)
        {
            if (pct > currentReportedPct)
            {
                currentReportedPct = pct;
            }
            progress?.Report(currentReportedPct);
        }

        ReportMonotonicProgress(10.0);
        TimeSpan globalMaxSeen = TimeSpan.Zero;

        // Allocate global [10%, 80%] progress proportional to each session group's nominal duration
        var groupWeights = sessionGroups
            .Select(g => Math.Max(1.0, (g[^1].NominalEnd - g[0].NominalStart).TotalSeconds))
            .ToList();
        double totalWeight = groupWeights.Sum();
        double cursorPct = 10.0;

        for (int groupIdx = 0; groupIdx < sessionGroups.Count; groupIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupSegments = sessionGroups[groupIdx];
            double groupSpanPct = 70.0 * (groupWeights[groupIdx] / totalWeight);
            double groupStartPct = cursorPct;
            double groupEndPct = (groupIdx == sessionGroups.Count - 1) ? 80.0 : (cursorPct + groupSpanPct);
            cursorPct = groupEndPct;

            long groupBytes = groupSegments.Sum(s => File.Exists(s.FilePath) ? new FileInfo(s.FilePath).Length : 0);

            if (sessionGroups.Count > 1)
            {
                AppLogService.Instance.LogInfo(
                    $"[AI转录] 启动独立对话组 {groupIdx + 1}/{sessionGroups.Count} (分段 {groupSegments[0].Index}-{groupSegments[^1].Index}/{normalizedSegments.Count}, " +
                    $"{TimeHelper.FormatDuration(groupSegments[0].NominalStart)} - {TimeHelper.FormatDuration(groupSegments[^1].NominalEnd)}, 音频大小: {groupBytes / (1024.0 * 1024.0):F2} MiB)...");
            }

            var (groupSubtitles, groupConversationId) = await TranscribeSessionGroupAsync(
                resolvedCli,
                audioDir,
                stage2SchemaPath,
                stage3SchemaPath,
                effectiveModel,
                totalDuration,
                groupSegments,
                groupIdx,
                sessionGroups.Count,
                normalizedSegments.Count,
                groupStartPct,
                groupEndPct,
                allSubtitles,
                globalMaxSeen,
                accumulatedStage1Searches,
                userEntries,
                userLockedSources,
                accumulatedGlossary,
                fullStage2Rules,
                simplifiedStage2Rules,
                searchInstruction,
                entityRetrievalInstruction,
                webSearchMode,
                ReportMonotonicProgress,
                onStatusUpdate,
                cancellationToken);

            if (sessionGroups.Count > 1 && groupIdx < sessionGroups.Count - 1 && webSearchMode != WebSearchMode.Off)
            {
                int addedSearches = AppendStage1SearchFindingsFromCliLogs(
                    groupConversationId,
                    accumulatedStage1Searches,
                    seenStage1SearchQueries);
                if (addedSearches > 0)
                {
                    int totalSearchChars = accumulatedStage1Searches.Sum(x => x.Summary.Length + x.Query.Length);
                    AppLogService.Instance.LogInfo(
                        $"[AI转录] 对话组 {groupIdx + 1}/{sessionGroups.Count} 提取了 {addedSearches} 条阶段一有效检索结果（累计 {accumulatedStage1Searches.Count} 条，正文字数约 {totalSearchChars}，跨组上限 {MaxPriorStage1SearchChars} 字符）。");
                }
            }

            foreach (var item in groupSubtitles)
            {
                item.Index = allSubtitles.Count + 1;
                allSubtitles.Add(item);
            }

            if (allSubtitles.Count > 0)
            {
                TimeSpan newGlobalMax = allSubtitles.Max(s => s.EndTime);
                if (newGlobalMax > globalMaxSeen)
                {
                    globalMaxSeen = newGlobalMax;
                }
            }

            ReportMonotonicProgress(groupEndPct);

            if (sessionGroups.Count > 1)
            {
                int totalLockedTerms = userLockedSources.Count + accumulatedGlossary.Count(kv => !userLockedSources.Contains(kv.Key));
                AppLogService.Instance.LogInfo(
                    $"[AI转录] 对话组 {groupIdx + 1}/{sessionGroups.Count} 完成: 本组产出 {groupSubtitles.Count} 条，累计 {allSubtitles.Count} 条字幕，已锁定译名 {totalLockedTerms} 条。");
            }
        }

        if (isGlossaryEnabled && accumulatedGlossary.Count > 0)
        {
            int addedTerms = GlossaryService.AppendNewTermsToCsv(selectedGlossaryCsv, accumulatedGlossary);
            if (addedTerms > 0)
            {
                AppLogService.Instance.LogInfo($"[术语表] 任务完成，已向 {selectedGlossaryCsv} 自动追加写入 {addedTerms} 条新专有名词。");
            }
        }

        ReportMonotonicProgress(80.0);
        return allSubtitles;
    }

    private async Task<(List<SubtitleItem> Subtitles, string? ConversationId)> TranscribeSessionGroupAsync(
        string resolvedCli,
        string? groupWorkingDir,
        string stage2SchemaPath,
        string stage3SchemaPath,
        string modelName,
        TimeSpan totalDuration,
        List<AudioSegmentChunk> segments,
        int groupIndex,
        int totalGroups,
        int totalVideoSegments,
        double groupStartPct,
        double groupEndPct,
        IReadOnlyList<SubtitleItem> priorSubtitles,
        TimeSpan priorMaxSeen,
        IReadOnlyList<(string Query, string Summary)> priorStage1Searches,
        IReadOnlyList<GlossaryEntry> userEntries,
        HashSet<string> userLockedSources,
        Dictionary<string, string> accumulatedGlossary,
        string fullStage2Rules,
        string simplifiedStage2Rules,
        string searchInstruction,
        string entityRetrievalInstruction,
        WebSearchMode webSearchMode,
        Action<double> reportMonotonicProgress,
        Action<string>? onStatusUpdate,
        CancellationToken cancellationToken)
    {
        bool isContinuationSession = groupIndex > 0;
        bool isFinalGroup = groupIndex == totalGroups - 1;
        TimeSpan groupNominalStart = segments[0].NominalStart;
        TimeSpan groupTargetEnd = isFinalGroup ? totalDuration : segments[^1].NominalEnd;
        double groupSpanPct = groupEndPct - groupStartPct;

        // Preserve exact 10 -> 35 -> 65 -> 80 ratios for single-session runs; shrink Stage 1 slice for continuation sessions
        double stage1Share = isContinuationSession ? 0.14 : (25.0 / 70.0);
        double stage2Share = isContinuationSession ? 0.64 : (30.0 / 70.0);
        double stage1EndPct = groupStartPct + groupSpanPct * stage1Share;
        double stage2EndPct = stage1EndPct + groupSpanPct * stage2Share;

        string lockedGlossaryBlock = BuildLockedGlossaryInstruction(userEntries, accumulatedGlossary, isContinuationSession);
        string priorSearchBlock = isContinuationSession
            ? BuildPriorSearchFindingsSection(priorStage1Searches)
            : string.Empty;
        string priorContextBlock = isContinuationSession
            ? BuildPriorContextSection(priorSubtitles, groupNominalStart)
            : string.Empty;

        string prompt = BuildGroupTurn1Prompt(
            segments,
            totalVideoSegments,
            groupIndex,
            totalGroups,
            priorSubtitles.LastOrDefault(),
            priorMaxSeen,
            fullStage2Rules,
            searchInstruction,
            entityRetrievalInstruction,
            lockedGlossaryBlock,
            priorSearchBlock,
            priorContextBlock,
            webSearchMode);

        var groupSubtitles = new List<SubtitleItem>();
        var aiSw = Stopwatch.StartNew();
        bool hasStartedGenerating = false;
        var timeRegex = new Regex(@"""(?:e|s|end|start)""\s*:\s*""(\d{1,2}:\d{2}:\d{2}[\.,]\d{3})""", RegexOptions.Compiled);
        TimeSpan maxSeen = priorMaxSeen;
        TimeSpan latestDiscoveredTime = priorMaxSeen;

        double seg1MaxPct = stage1EndPct + ((stage2EndPct - stage1EndPct) / Math.Max(1, segments.Count)) - 0.4;
        double stage1DurationEstimate = isContinuationSession ? 60.0 : 180.0;
        const double segHalfTimeSeconds = 220.0;

        var seg0 = segments[0];
        string seg0FileName = Path.GetFileName(seg0.FilePath);
        string groupTag = totalGroups > 1 ? $"对话 {groupIndex + 1}/{totalGroups} · " : string.Empty;

        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? activeTurnTickerCts = tickerCts;
        int currentGlobalSegIndex = seg0.Index;
        TimeSpan currentSegmentOffset = seg0.NominalStart;

        var statusTicker = Task.Run(async () =>
        {
            while (!tickerCts.Token.IsCancellationRequested && !hasStartedGenerating)
            {
                var elapsed = aiSw.Elapsed;
                double seconds = elapsed.TotalSeconds;

                if (seconds <= stage1DurationEstimate)
                {
                    double tRatio = seconds / stage1DurationEstimate;
                    double eased = tRatio * tRatio * (3.0 - 2.0 * tRatio);
                    double creepPct = groupStartPct + (stage1EndPct - groupStartPct) * eased;
                    reportMonotonicProgress(creepPct);

                    string statusText = seconds switch
                    {
                        < 8.0 => $"{groupTag}AI 正在读取并感知本组音频流 (已耗时 {elapsed:mm\\:ss})...",
                        _ => isContinuationSession
                            ? $"{groupTag}AI 正在继承前文语境并感知本组音频 (已思考 {elapsed:mm\\:ss})..."
                            : $"{groupTag}AI 正在全局感知背景语境与说话人逻辑 (已思考 {elapsed:mm\\:ss})..."
                    };
                    onStatusUpdate?.Invoke(statusText);
                }
                else
                {
                    double seg1Sec = seconds - stage1DurationEstimate;
                    double creepPct = ComputeSmoothStageProgress(stage1EndPct, seg1MaxPct, seg1Sec, halfTimeSeconds: segHalfTimeSeconds);
                    reportMonotonicProgress(creepPct);
                    string segLabel = totalVideoSegments > 1 ? $"第 {seg0.Index}/{totalVideoSegments} 分段" : "全片";
                    onStatusUpdate?.Invoke($"{groupTag}AI 正在转录{segLabel} ({TimeHelper.FormatDuration(seg0.NominalStart)} - {TimeHelper.FormatDuration(seg0.NominalEnd)}，已耗时 {elapsed:mm\\:ss})...");
                }

                try
                {
                    await Task.Delay(1000, tickerCts.Token);
                }
                catch
                {
                    break;
                }
            }
        }, tickerCts.Token);

        double groupDurSec = Math.Max(1.0, (groupTargetEnd - groupNominalStart).TotalSeconds);
        Action<string> handleToolActivity = toolName =>
        {
            if (string.Equals(toolName, "search_web", StringComparison.OrdinalIgnoreCase))
            {
                onStatusUpdate?.Invoke($"{groupTag}AI 正在检索验证背景知识与领域术语 (已耗时 {aiSw.Elapsed:mm\\:ss})...");
            }
        };

        Action<string> handleTextDelta = delta =>
        {
            var match = timeRegex.Match(delta);
            if (match.Success)
            {
                if (!hasStartedGenerating)
                {
                    hasStartedGenerating = true;
                    reportMonotonicProgress(stage1EndPct);
                }

                try { activeTurnTickerCts?.Cancel(); } catch { }

                var localTs = TimeHelper.ParseTimestamp(match.Groups[1].Value);
                var ts = currentSegmentOffset + localTs;
                if (ts > latestDiscoveredTime)
                {
                    latestDiscoveredTime = ts;
                    double ratio = Math.Clamp((ts - groupNominalStart).TotalSeconds / groupDurSec, 0.0, 1.0);
                    double pipelinePct = stage1EndPct + ratio * (stage2EndPct - stage1EndPct);
                    reportMonotonicProgress(pipelinePct);
                    string prefix = totalVideoSegments > 1
                        ? $"{groupTag}AI 正在转录第 {currentGlobalSegIndex}/{totalVideoSegments} 分段"
                        : "AI 转录与多语言翻译中...";
                    onStatusUpdate?.Invoke($"{prefix} ({TimeHelper.FormatDuration(ts)} / {TimeHelper.FormatDuration(totalDuration)})");
                }
            }
        };

        // Turn 1 of this session group: load group segments (<20MB) -> Stage 1 -> Stage 2 Segment 1 of group
        string? sessionConversationId = null;
        CliTurnResult firstResult = null!;
        try
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    firstResult = await RunCliTurnAsync(
                        resolvedCli,
                        groupWorkingDir,
                        stage2SchemaPath,
                        modelName,
                        prompt,
                        conversationId: null,
                        handleToolActivity,
                        handleTextDelta,
                        cancellationToken);
                    sessionConversationId = firstResult.ConversationId;
                    break;
                }
                catch (InvalidOperationException ex) when (attempt < 2 && ex.Message.Contains("安全过滤器", StringComparison.Ordinal))
                {
                    await Task.Delay(1200, cancellationToken);
                }
            }
        }
        finally
        {
            try { tickerCts.Cancel(); } catch { }
        }

        MergeGlossaryExcludingUserLocked(firstResult.Payload.Glossary, userLockedSources, accumulatedGlossary);

        var firstBatch = FinalizeBatchTimestamps(firstResult.Payload.Subtitles, timestampOffset: seg0.NominalStart, flagTimingAnomalies: true);
        bool hasPriorContext = priorSubtitles.Count > 0;
        int firstAdded = AppendSegmentEntries(
            groupSubtitles,
            firstBatch,
            seg0FileName,
            seg0.NominalStart,
            maxSeen,
            deduplicateBeforeMaxSeen: hasPriorContext,
            baseGlobalIndex: priorSubtitles.Count);

        if (groupSubtitles.Count > 0)
        {
            TimeSpan groupMax = groupSubtitles.Max(s => s.EndTime);
            if (groupMax > maxSeen) maxSeen = groupMax;
        }

        double seg1DonePct = stage1EndPct + ((stage2EndPct - stage1EndPct) / Math.Max(1, segments.Count));
        if (maxSeen > latestDiscoveredTime)
        {
            latestDiscoveredTime = maxSeen;
            double ratio = Math.Clamp((maxSeen - groupNominalStart).TotalSeconds / groupDurSec, 0.0, 1.0);
            reportMonotonicProgress(Math.Max(seg1DonePct, stage1EndPct + ratio * (stage2EndPct - stage1EndPct)));
        }
        else
        {
            reportMonotonicProgress(seg1DonePct);
        }

        AppLogService.Instance.LogInfo(
            $"[AI转录] {groupTag}分段 {seg0.Index}/{totalVideoSegments} ({seg0FileName}) 解析完成: 新增 {firstAdded} 条字幕，最大时间戳: {TimeHelper.FormatDuration(maxSeen)} (全片时长: {TimeHelper.FormatDuration(totalDuration)})");

        // Process remaining 12-minute segments in this session group (stage1EndPct -> stage2EndPct)
        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            cancellationToken.ThrowIfCancellationRequested();

            int localTurnNumber = i + 1;
            currentGlobalSegIndex = seg.Index;
            currentSegmentOffset = seg.NominalStart;
            string segFileName = Path.GetFileName(seg.FilePath);

            double segStartPct = stage1EndPct + (((double)i / segments.Count) * (stage2EndPct - stage1EndPct));
            double segEndPct = stage1EndPct + (((double)localTurnNumber / segments.Count) * (stage2EndPct - stage1EndPct));
            reportMonotonicProgress(segStartPct);

            AppLogService.Instance.LogInfo(
                $"[AI转录] {groupTag}启动第 {seg.Index}/{totalVideoSegments} 分段转录 ({segFileName}: {TimeHelper.FormatDuration(seg.NominalStart)} - {TimeHelper.FormatDuration(seg.NominalEnd)})...");

            var segSw = Stopwatch.StartNew();
            using var segTickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeTurnTickerCts = segTickerCts;
            bool segDone = false;
            var segTicker = Task.Run(async () =>
            {
                while (!segTickerCts.Token.IsCancellationRequested && !segDone)
                {
                    var elapsed = segSw.Elapsed;
                    double creepPct = ComputeSmoothStageProgress(segStartPct, segEndPct - 0.4, elapsed.TotalSeconds, halfTimeSeconds: segHalfTimeSeconds);
                    reportMonotonicProgress(creepPct);
                    if (!segTickerCts.Token.IsCancellationRequested)
                    {
                        onStatusUpdate?.Invoke($"{groupTag}AI 正在转录第 {seg.Index}/{totalVideoSegments} 分段 ({TimeHelper.FormatDuration(seg.NominalStart)} - {TimeHelper.FormatDuration(seg.NominalEnd)}，已耗时 {elapsed:mm\\:ss})...");
                    }
                    try
                    {
                        await Task.Delay(1000, segTickerCts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, segTickerCts.Token);

            TimeSpan localNominalEnd = seg.NominalEnd - seg.NominalStart;
            TimeSpan localExtractEnd = seg.ExtractEnd > seg.ExtractStart ? (seg.ExtractEnd - seg.ExtractStart) : localNominalEnd;
            string localExtractEndStr = FormatLocalTimestamp(localExtractEnd);
            var lastItem = groupSubtitles.LastOrDefault() ?? priorSubtitles.LastOrDefault();
            string boundaryAnchorLine = BuildBoundaryAnchorInstruction(seg, maxSeen, lastItem);

            string continuePrompt =
$@"Continuing in this SAME conversation and audio context:
STAGE 2 - NEXT SEGMENT (Segment {seg.Index}/{totalVideoSegments}): Audio file `{segFileName}` (global video window {FormatLocalTimestamp(seg.NominalStart)} to {FormatLocalTimestamp(seg.ExtractEnd)}; local file duration {localExtractEndStr}).
CRITICAL SEGMENT-LOCAL TIMELINE RULE:
- Listen strictly to `{segFileName}` (already loaded in this conversation; only call `read_file` on ""{seg.FilePath}"" if prior compaction removed direct access to its audio).
- Output ALL `s` and `e` timestamps strictly RELATIVE to the start of `{segFileName}` (`00:00:00.000` = beginning of `{segFileName}`). Do NOT manually add the {FormatLocalTimestamp(seg.NominalStart)} offset—the host program will add it automatically.
{boundaryAnchorLine}
{simplifiedStage2Rules}
Do NOT call `search_web` at any time in Stage 2 (use `""f""` instead). Maintain full context and entity consistency.
Return a JSON object matching the schema: populate `""g""` (`[{{""k"":""..."",""v"":""...""}}]`) with ONLY newly introduced or updated proper nouns in this segment (empty array `[]` if unchanged from earlier turns), and `""sub""` with ONLY the subtitle entries for `{segFileName}` (`s`, `e`, `src`, `tgt`, optional `f`/`p`).";

            CliTurnResult segResult;
            try
            {
                segResult = await RunCliTurnAsync(
                    resolvedCli,
                    groupWorkingDir,
                    stage2SchemaPath,
                    modelName,
                    continuePrompt,
                    sessionConversationId,
                    handleToolActivity,
                    handleTextDelta,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(segResult.ConversationId))
                {
                    sessionConversationId = segResult.ConversationId;
                }
            }
            finally
            {
                segDone = true;
                try { segTickerCts.Cancel(); } catch { }
            }

            try
            {
                MergeGlossaryExcludingUserLocked(segResult.Payload.Glossary, userLockedSources, accumulatedGlossary);
                var batch = FinalizeBatchTimestamps(segResult.Payload.Subtitles, timestampOffset: seg.NominalStart, flagTimingAnomalies: true);
                int added = AppendSegmentEntries(
                    groupSubtitles,
                    batch,
                    segFileName,
                    seg.NominalStart,
                    maxSeen,
                    deduplicateBeforeMaxSeen: groupSubtitles.Count > 0 || hasPriorContext,
                    baseGlobalIndex: priorSubtitles.Count);

                TimeSpan newMax = groupSubtitles.Count > 0 ? groupSubtitles.Max(s => s.EndTime) : maxSeen;
                if (newMax > maxSeen) maxSeen = newMax;

                AppLogService.Instance.LogInfo($"[AI转录] {groupTag}分段 {seg.Index}/{totalVideoSegments} ({segFileName}) 完成: 新增 {added} 条字幕，最新时间戳: {TimeHelper.FormatDuration(maxSeen)}");
                reportMonotonicProgress(segEndPct);
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning($"[AI转录] {groupTag}分段 {seg.Index}/{totalVideoSegments} 解析异常: {ex.Message}");
            }
        }

        // Fallback continuation loop if the final segment in this group stopped short of groupTargetEnd
        int pass = segments.Count;
        var lastSeg = segments[^1];
        string lastSegFileName = Path.GetFileName(lastSeg.FilePath);
        while (ShouldTriggerContinuation(groupTargetEnd, maxSeen, groupSubtitles.Count, isFinalGroup) && pass < 15)
        {
            pass++;
            cancellationToken.ThrowIfCancellationRequested();

            currentGlobalSegIndex = lastSeg.Index;
            currentSegmentOffset = lastSeg.NominalStart;

            double remSec = (groupTargetEnd - maxSeen).TotalSeconds;
            AppLogService.Instance.LogInfo(
                $"[AI转录] {groupTag}检测到本组末段尚未结束 (剩余 {remSec:F1} 秒未覆盖)，启动接力续写 (轮次 {pass}/15，{lastSegFileName} 从 {TimeHelper.FormatDuration(maxSeen)} 继续)...");
            onStatusUpdate?.Invoke($"{groupTag}AI 正在进行会话内接力续写 (轮次 {pass}/15，当前进度: {TimeHelper.FormatDuration(maxSeen)} / {TimeHelper.FormatDuration(groupTargetEnd)})...");

            var lastFallbackItem = groupSubtitles.LastOrDefault() ?? priorSubtitles.LastOrDefault();
            string lastFallbackPreview = lastFallbackItem != null
                ? (!string.IsNullOrWhiteSpace(lastFallbackItem.TargetText) ? lastFallbackItem.TargetText : lastFallbackItem.SourceText)
                : string.Empty;
            TimeSpan localMaxSeen = maxSeen > lastSeg.NominalStart ? (maxSeen - lastSeg.NominalStart) : TimeSpan.Zero;
            string localMaxSeenStr = FormatLocalTimestamp(localMaxSeen);
            TimeSpan localTargetEnd = lastSeg.NominalEnd - lastSeg.NominalStart;
            string localTargetEndStr = FormatLocalTimestamp(localTargetEnd);

            string continuePrompt =
$@"Continuing in this SAME conversation and audio context:
Target audio segment file: `{lastSegFileName}` (already loaded in context).
Output `s` and `e` timestamps strictly RELATIVE to the start of `{lastSegFileName}` (`00:00:00.000`-based; the host program adds the segment offset automatically).
The previous pass inside `{lastSegFileName}` finished around local time {localMaxSeenStr} with the final subtitle line: ""{lastFallbackPreview}"".
Please continue transcribing and translating the remaining dialogue and sung lyrics in `{lastSegFileName}` starting from the very first new spoken utterance AFTER local time {localMaxSeenStr} (""{lastFallbackPreview}"") up to approximately local time {localTargetEndStr}.
{simplifiedStage2Rules}
Do NOT call `search_web` at any time in Stage 2 (use `""f""` instead). Maintain full context, entity names, and terminology consistency.
Return a JSON object matching the schema: populate `""g""` with ONLY newly introduced or updated proper nouns (empty array `[]` if unchanged) and `""sub""` with ONLY the remaining subtitle entries (`s`, `e`, `src`, `tgt`, optional `f`/`p`).";

            var continueResult = await RunCliTurnAsync(
                resolvedCli,
                groupWorkingDir,
                stage2SchemaPath,
                modelName,
                continuePrompt,
                sessionConversationId,
                handleToolActivity,
                handleTextDelta,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(continueResult.ConversationId))
            {
                sessionConversationId = continueResult.ConversationId;
            }

            if (continueResult.Payload.Subtitles.Count == 0)
            {
                AppLogService.Instance.LogInfo($"[AI转录] {groupTag}轮次 {pass} 返回 0 条字幕，已到达本组台词终点，结束续写。");
                break;
            }

            try
            {
                MergeGlossaryExcludingUserLocked(continueResult.Payload.Glossary, userLockedSources, accumulatedGlossary);
                var nextBatch = FinalizeBatchTimestamps(continueResult.Payload.Subtitles, timestampOffset: lastSeg.NominalStart, flagTimingAnomalies: true);

                int addedCount = 0;
                foreach (var item in nextBatch)
                {
                    if (item.StartTime >= maxSeen.Subtract(TimeSpan.FromSeconds(1)))
                    {
                        item.Index = priorSubtitles.Count + groupSubtitles.Count + 1;
                        item.AudioFile = lastSegFileName;
                        item.SegmentOffset = lastSeg.NominalStart;
                        groupSubtitles.Add(item);
                        addedCount++;
                    }
                }

                TimeSpan newMax = groupSubtitles.Count > 0 ? groupSubtitles.Max(s => s.EndTime) : maxSeen;
                AppLogService.Instance.LogInfo($"[AI转录] {groupTag}轮次 {pass} 完成: 新增 {addedCount} 条字幕，最新时间戳推进至 {TimeHelper.FormatDuration(newMax)}");

                if (newMax <= maxSeen)
                {
                    AppLogService.Instance.LogInfo($"[AI转录] {groupTag}轮次 {pass} 未能推进新的时间戳，结束续写。");
                    break;
                }
                maxSeen = newMax;

                double ratio = Math.Clamp((maxSeen - groupNominalStart).TotalSeconds / groupDurSec, 0.0, 1.0);
                reportMonotonicProgress(stage1EndPct + ratio * (stage2EndPct - stage1EndPct));
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning($"[AI转录] {groupTag}轮次 {pass} 解析出现异常: {ex.Message}，提前结束接力。");
                break;
            }
        }

        FlagSequentialTimingInversions(groupSubtitles, groupTag);

        // STAGE 3: Targeted verification on flagged items within this session group
        await VerifyFlaggedEntriesStage3Async(
            resolvedCli,
            groupWorkingDir,
            stage3SchemaPath,
            modelName,
            sessionConversationId,
            segments,
            groupSubtitles,
            groupTag,
            stage2EndPct,
            groupEndPct,
            webSearchMode,
            userLockedSources,
            accumulatedGlossary,
            reportMonotonicProgress,
            onStatusUpdate,
            cancellationToken);

        groupSubtitles = groupSubtitles
            .OrderBy(s => s.StartTime)
            .ThenBy(s => s.Index)
            .ToList();

        reportMonotonicProgress(groupEndPct);
        return (groupSubtitles, sessionConversationId);
    }

    private async Task VerifyFlaggedEntriesStage3Async(
        string resolvedCli,
        string? groupWorkingDir,
        string schemaFilePath,
        string modelName,
        string? conversationId,
        IReadOnlyList<AudioSegmentChunk> segments,
        List<SubtitleItem> groupSubtitles,
        string groupTag,
        double stage3StartPct,
        double stage3EndPct,
        WebSearchMode webSearchMode,
        HashSet<string> userLockedSources,
        Dictionary<string, string> accumulatedGlossary,
        Action<double> reportMonotonicProgress,
        Action<string>? onStatusUpdate,
        CancellationToken cancellationToken)
    {
        reportMonotonicProgress(stage3StartPct);
        var directFlagged = groupSubtitles.Where(s => !string.IsNullOrWhiteSpace(s.Flag)).ToList();
        var overlapAnchors = directFlagged
            .Where(s => s.IsPrimary == false ||
                        (s.Flag != null && (s.Flag.Contains("overlap", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("boundary", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("timing", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("pause", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("drift", StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var anchorIndices = new HashSet<int>();
        for (int idx = 0; idx < groupSubtitles.Count; idx++)
        {
            if (overlapAnchors.Contains(groupSubtitles[idx]))
            {
                anchorIndices.Add(idx);
            }
        }

        var flaggedItems = groupSubtitles
            .Where((s, idx) => !string.IsNullOrWhiteSpace(s.Flag) ||
                               anchorIndices.Contains(idx - 1) ||
                               anchorIndices.Contains(idx + 1) ||
                               overlapAnchors.Any(a => Math.Abs((s.StartTime - a.StartTime).TotalSeconds) <= 5.0))
            .ToList();

        if (flaggedItems.Count == 0)
        {
            AppLogService.Instance.LogInfo($"[AI转录] {groupTag}无疑难标记，阶段三自动跳过。");
            return;
        }

        onStatusUpdate?.Invoke($"{groupTag}AI 正在进行靶向核对与微创修正 ({flaggedItems.Count} 处疑难标记)...");
        AppLogService.Instance.LogInfo($"[AI转录] {groupTag}发现 {flaggedItems.Count} 条疑难标记，启动阶段三靶向核对 ({stage3StartPct:F0}% -> {stage3EndPct:F0}%)...");

        var stage3Sw = Stopwatch.StartNew();
        using var stage3TickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool stage3Done = false;
        double stage3HalfTime = Math.Max(135.0, flaggedItems.Count * 3.0);
        var stage3Ticker = Task.Run(async () =>
        {
            while (!stage3TickerCts.Token.IsCancellationRequested && !stage3Done)
            {
                var elapsed = stage3Sw.Elapsed;
                double creepPct = ComputeSmoothStageProgress(stage3StartPct, stage3EndPct - 0.4, elapsed.TotalSeconds, halfTimeSeconds: stage3HalfTime);
                reportMonotonicProgress(creepPct);
                onStatusUpdate?.Invoke($"{groupTag}AI 正在进行靶向核对与微创修正 ({flaggedItems.Count} 处疑难标记，已耗时 {elapsed:mm\\:ss})...");
                try
                {
                    await Task.Delay(1000, stage3TickerCts.Token);
                }
                catch
                {
                    break;
                }
            }
        }, stage3TickerCts.Token);

        try
        {
            string searchGuidance = webSearchMode switch
            {
                WebSearchMode.Off =>
                    "Rely strictly on established audio context, acoustic cross-reference, and dialogue coherence without calling web search.",
                _ =>
                    "Perform targeted web search ONLY when strictly necessary to verify unresolved proper nouns, specialized domain terminology, or missing song lyrics/titles (never re-search already locked glossary terms or prior Stage 1 search findings, and never put unverified/fuzzy acoustic fragments or guessed lyrics into search queries)."
            };

            var stage3JsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            var chunkByFile = segments.ToDictionary(s => Path.GetFileName(s.FilePath), StringComparer.OrdinalIgnoreCase);
            const int maxItemsPerStage3Batch = 65;
            int correctedCount = 0;
            var updatedMatches = new HashSet<SubtitleItem>();

            for (int batchOffset = 0; batchOffset < flaggedItems.Count; batchOffset += maxItemsPerStage3Batch)
            {
                var batchItems = flaggedItems.Skip(batchOffset).Take(maxItemsPerStage3Batch).ToList();
                string itemsJson = JsonSerializer.Serialize(batchItems.Select(f =>
                {
                    string segFile = !string.IsNullOrWhiteSpace(f.AudioFile)
                        ? f.AudioFile
                        : Path.GetFileName(segments[0].FilePath);
                    TimeSpan segOffset = f.SegmentOffset;
                    TimeSpan localStart = f.StartTime >= segOffset ? (f.StartTime - segOffset) : f.StartTime;
                    TimeSpan localEnd = f.EndTime >= segOffset ? (f.EndTime - segOffset) : f.EndTime;
                    return new
                    {
                        i = f.Index,
                        a = segFile,
                        s = FormatLocalTimestamp(localStart),
                        e = FormatLocalTimestamp(localEnd),
                        src = f.SourceText,
                        tgt = f.TargetText,
                        p = f.IsPrimary,
                        f = f.Flag
                    };
                }), stage3JsonOptions);

                string verifyPrompt =
$@"STAGE 3 - TARGETED VERIFICATION & REFINEMENT:
The following subtitle entries were flagged in Stage 2 due to proper noun translation, semantic accuracy, multi-hearing ambiguity, acoustic masking/song lyrics, contextual incoherence, simultaneous dual-voice overlap, or difficulty pinpointing first/last syllable start/end boundaries (unflagged neighboring reference entries may also be included in the same batch for boundary comparison).
Each entry uses compact keys: `i` (index), `a` (segment audio file inside this session), `s`/`e` (start/end RELATIVE to `a`, `00:00:00.000`-based), `src` (source text), `tgt` (target text), optional `p` (`is_primary`), and `f` (`flag`):
{itemsJson}

With full audio context now established across all segments in this session, verify and refine the entries above according to these principles ({searchGuidance}):
- STRICT NO-LOG-READING RULE: NEVER call `read_file` on `.system_generated` or `transcript*.jsonl` log files. Rely directly on the provided entries above and the corresponding audio file (`""a""`).
- Independent Re-Listening Anti-Anchoring Principle: NEVER blindly trust the provided `src`, `tgt`, or `s`/`e` timestamps. For every entry with `f`, re-listen independently to its audio file `""a""` first to verify spoken phonemes and first/last syllable positions, focusing on resolving the issue raised in `f`; for unflagged neighboring reference entries, ONLY modify them if clear line shifting, omitted lines, or timestamp collisions are found—never make unnecessary paraphrasing or stylistic edits to them.
1. Official Nomenclature, Song Lyrics & Translation Standardization:
   - Strictly reuse all locked glossary translations; rectify unverified proper nouns or terminology doubts into standard Chinese terms via official name cross-reference or targeted web search.
   - For song lyric entries: if Stage 1 did not retrieve the complete official lyrics, search using the confirmed song title and artist to complete them; if official lyrics were already retrieved yet remain uncertain against the audio, carefully check the actual singing in the audio for omissions or version differences, rectify `src` strictly to match the corresponding lyrics actually sung in the audio (NEVER force-overwrite with unsung official lyrics), and refine `tgt` into natural, apt Chinese translations, strictly eliminating literal machine translation, inverted meanings, and phonetic hallucinations.
   - If the audio is natively Chinese, rectify any homophone errors, dialect misunderstandings, or non-standard terms into standard Chinese.
2. Acoustic Reality & Dual-Voice Overlap Audit:
   - Rely strictly on true acoustic hearing inside each audio file `""a""`. Overturn any words Stage 2 fabricated to fit context, guessed wrong across multiple hearings, or misheard, rectifying `src` to authentic heard syllables and accurate terminology.
   - For entries flagged with dual-voice or dialogue-lyric overlap, carefully re-listen to that exact time window inside `""a""`, restore any omitted or truncated clauses of the simultaneous second voice/lyric, output both entries with true overlapping timestamps, and accurately label `""p""` (`true` for main dialogue, `false` for secondary/lyric; consistently set `""p"": false` across all entries of the same secondary track so subtitles stay stably at the top without jumping up and down).
3. First/Last Syllable Boundaries & 100ms Precision Re-Verification:
   - Re-listen to the speech boundaries inside `""a""` and accurately pinpoint the natural spoken onset of the FIRST syllable of the first word (`s`) and the natural acoustic decay offset of the LAST syllable of the last word (`e`) (NEVER anchor to breaths, music, sound effects, or volume level alone, and NEVER inertially attach `s` to the previous sentence's `e`).
   - Timestamp precision MUST be at least to the 100ms level (e.g., `.100`, `.300`, `.700`); NEVER lazily round most entries' `s`/`e` to `.000` or `.500`.
   - For entries flagged with boundary/alignment uncertainty, >20s duration, or inverted/out-of-order timestamps, inspect the neighboring audio window and context inside `""a""` to fix wrong minute digits, excessive spans, inverted boundaries, shifted adjacent lines, or omitted untranscribed utterances.
Return a JSON object matching the schema:
- `""g""`: array of ONLY newly verified or updated proper nouns (`[{{""k"":""..."",""v"":""...""}}]`; empty array `[]` if unchanged).
- `""sub""`: ONLY entries that actually require modification (in `s`, `e`, `src`, `tgt`, or `p`) or newly recovered missing/overlapping lines, with fields `i`, `a`, `s` (relative to `a`), `e` (relative to `a`), `src`, `tgt` (and `p` when overlapping or secondary).
DIFF-ONLY OUTPUT RULE (CRITICAL): DO NOT echo or copy back entries that are already accurate and unchanged. If NO entries in this batch need any change or addition, set `""sub"": []`.";

                var verifyResult = await RunCliTurnAsync(
                    resolvedCli,
                    groupWorkingDir,
                    schemaFilePath,
                    modelName,
                    verifyPrompt,
                    conversationId,
                    onToolActivity: null,
                    onTextDelta: null,
                    cancellationToken);

                MergeGlossaryExcludingUserLocked(verifyResult.Payload.Glossary, userLockedSources, accumulatedGlossary);

                var verifiedBatch = FinalizeBatchTimestamps(verifyResult.Payload.Subtitles);
                foreach (var vItem in verifiedBatch)
                {
                    var matchByIndex = groupSubtitles.FirstOrDefault(x =>
                        !updatedMatches.Contains(x) && x.Index == vItem.Index);

                    AudioSegmentChunk? targetChunk = null;
                    if (!string.IsNullOrWhiteSpace(vItem.AudioFile) &&
                        chunkByFile.TryGetValue(Path.GetFileName(vItem.AudioFile), out var foundChunk))
                    {
                        targetChunk = foundChunk;
                    }
                    else if (matchByIndex != null &&
                             !string.IsNullOrWhiteSpace(matchByIndex.AudioFile) &&
                             chunkByFile.TryGetValue(Path.GetFileName(matchByIndex.AudioFile), out var fallbackChunk))
                    {
                        targetChunk = fallbackChunk;
                    }

                    TimeSpan segOffset = targetChunk?.NominalStart ?? matchByIndex?.SegmentOffset ?? TimeSpan.Zero;
                    TimeSpan maxLocalDur = targetChunk != null
                        ? (targetChunk.ExtractEnd - targetChunk.NominalStart) + TimeSpan.FromSeconds(5)
                        : TimeSpan.FromMinutes(16);

                    bool looksGlobalAlready = segOffset > TimeSpan.Zero
                        && vItem.StartTime >= segOffset - TimeSpan.FromSeconds(2)
                        && vItem.StartTime > maxLocalDur;

                    TimeSpan globalStart = looksGlobalAlready ? vItem.StartTime : segOffset + vItem.StartTime;
                    TimeSpan globalEnd = looksGlobalAlready ? vItem.EndTime : segOffset + vItem.EndTime;
                    if (globalEnd <= globalStart)
                    {
                        globalEnd = globalStart.Add(TimeSpan.FromSeconds(1.5));
                    }

                    var match = matchByIndex ?? groupSubtitles.FirstOrDefault(x =>
                        !updatedMatches.Contains(x) &&
                        Math.Abs((x.StartTime - globalStart).TotalSeconds) < 1.0);

                    if (match != null)
                    {
                        updatedMatches.Add(match);
                        bool hasActualChange =
                            !string.Equals(match.SourceText, vItem.SourceText, StringComparison.Ordinal) ||
                            !string.Equals(match.TargetText, vItem.TargetText, StringComparison.Ordinal) ||
                            Math.Abs((match.StartTime - globalStart).TotalMilliseconds) >= 1.0 ||
                            Math.Abs((match.EndTime - globalEnd).TotalMilliseconds) >= 1.0 ||
                            (vItem.IsPrimary.HasValue && match.IsPrimary != vItem.IsPrimary);

                        match.SourceText = vItem.SourceText;
                        match.TargetText = vItem.TargetText;
                        match.StartTime = globalStart;
                        match.EndTime = globalEnd;
                        match.Start = TimeHelper.ToSrtTime(globalStart);
                        match.End = TimeHelper.ToSrtTime(globalEnd);
                        if (targetChunk != null)
                        {
                            match.AudioFile = Path.GetFileName(targetChunk.FilePath);
                            match.SegmentOffset = targetChunk.NominalStart;
                        }
                        if (vItem.IsPrimary.HasValue) match.IsPrimary = vItem.IsPrimary;
                        match.Flag = null;
                        if (hasActualChange)
                        {
                            correctedCount++;
                        }
                    }
                    else
                    {
                        vItem.StartTime = globalStart;
                        vItem.EndTime = globalEnd;
                        vItem.Start = TimeHelper.ToSrtTime(globalStart);
                        vItem.End = TimeHelper.ToSrtTime(globalEnd);
                        if (targetChunk != null)
                        {
                            vItem.AudioFile = Path.GetFileName(targetChunk.FilePath);
                            vItem.SegmentOffset = targetChunk.NominalStart;
                        }
                        vItem.Flag = null;
                        groupSubtitles.Add(vItem);
                        correctedCount++;
                    }
                }

                // Clear flags on verified unchanged items omitted from diff-only output
                foreach (var item in batchItems)
                {
                    item.Flag = null;
                }
            }

            stage3Done = true;
            try { stage3TickerCts.Cancel(); } catch { }
            AppLogService.Instance.LogInfo($"[AI转录] {groupTag}阶段三靶向核对完成，共核验 {flaggedItems.Count} 条，实际修正/补充 {correctedCount} 条字幕。");
        }
        catch (Exception ex)
        {
            stage3Done = true;
            try { stage3TickerCts.Cancel(); } catch { }
            string warnMsg = $"[AI转录] {groupTag}阶段三靶向核对因网络异常未完成: {ex.Message}。已保留阶段二转录成果 ({flaggedItems.Count} 处疑难标记未核验)。";
            AppLogService.Instance.LogWarning(warnMsg);
            onStatusUpdate?.Invoke($"⚠️ {groupTag}阶段三核验受网络影响跳过，已保留原字幕 ({flaggedItems.Count} 处疑难未核验)");
        }
    }

    // Gentle S-damped curve: pow(t/T, 1.35) / (1 + pow(t/T, 1.35))
    private static double ComputeSmoothStageProgress(double startPct, double maxPct, double elapsedSeconds, double halfTimeSeconds)
    {
        if (elapsedSeconds <= 0 || maxPct <= startPct) return startPct;
        double x = Math.Pow(elapsedSeconds / Math.Max(1.0, halfTimeSeconds), 1.35);
        double ratio = x / (1.0 + x);
        return startPct + (maxPct - startPct) * ratio;
    }

    private static List<List<AudioSegmentChunk>> BuildSessionGroups(IReadOnlyList<AudioSegmentChunk> segments)
    {
        var groups = new List<List<AudioSegmentChunk>>();
        var currentGroup = new List<AudioSegmentChunk>();
        long currentBytes = 0;

        foreach (var seg in segments)
        {
            long segBytes = File.Exists(seg.FilePath) ? new FileInfo(seg.FilePath).Length : 0;
            if (segBytes > MaxSingleBatchMediaBytes)
            {
                throw new InvalidOperationException(
                    $"单个音频分段 {Path.GetFileName(seg.FilePath)} 大小为 {segBytes / (1024.0 * 1024.0):F2} MiB（{segBytes:N0} 字节），已超出单次会话 20 MB 上限。");
            }

            if (currentGroup.Count > 0 &&
                (currentGroup.Count >= MaxSegmentsPerSession || currentBytes + segBytes > MaxSingleBatchMediaBytes))
            {
                groups.Add(currentGroup);
                currentGroup = new List<AudioSegmentChunk>();
                currentBytes = 0;
            }

            currentGroup.Add(seg);
            currentBytes += segBytes;
        }

        if (currentGroup.Count > 0)
        {
            groups.Add(currentGroup);
        }

        return groups;
    }

    private static (string FullRules, string SimplifiedRules) BuildStage2RuleBlocks(
        string styleInstruction,
        string segInstruction,
        int maxCjkChars,
        int maxNonCjkChars)
    {
        string fullStage2Rules =
$@"   - CORE ACOUSTIC PRINCIPLES:
     * Verbatim Syllable Fidelity: Speech phonemes and syllables MUST match the acoustic audio completely and verbatim. Never guess words, substitute unheard syllables, or normalize colloquial speech into formal text.
     * No Semantic or Acoustic Fabrication: Transcribe strictly what is physically spoken. Never invent words or phrases merely because they seem plausible for the topic or context. Never alter heard syllables to conform to anticipated idioms, cliches, or assumed colloquial habits.
     * Grammatical & Morphological Precision (in 'src' ONLY): Strictly preserve the speaker's exact spoken grammatical endings, verb conjugations, and tenses in 'src'. Do not append or elongate trailing syllables or modal particles that were not spoken.
     * Masked, Interrupted & Faint Speech: Never discard short remarks, interjections, affirmations, or background vocals/lyrics occurring between or masked by noise, background music, or louder foreground dialogue. Do NOT miss any faint or low-volume speech.
     * Repeated Phrases (Creative Drama & Fiction ONLY): For creative drama, anime, or films ONLY: when identical words, cries, or exclamations are repeated 4 or more times in rapid succession within a short acoustic interval (without extended pauses), merge from the start of the first to the end of the last into a single entry, displaying ONLY 2 repetitions followed by an ellipsis ('……'), while anchoring 's' to the first repetition and 'e' to the final repetition.
     * Completely Unintelligible Speech & Lyrics: If an audio segment (dialogue or song vocals) is completely unintelligible or phonemes cannot be acoustically anchored due to heavy noise, music masking, or distortion, you are strictly forbidden from fabricating imaginary text. You are permitted to refrain from transcribing speculative words, but you MUST output the time span with a flag requesting full-segment re-verification (e.g., `""f"": ""unintelligible / full segment re-verification required""`).
     * Rapid Turn-Taking & Simultaneous Multi-Voice Overlaps:
       - When speakers talk in rapid succession or interrupt, do NOT merge or assimilate the first speaker's trailing words with the second speaker's speech. Keep them as separate entries.
       - When two voices occur simultaneously (two speakers talking at the same time, or foreground dialogue overlapping with background song lyrics), you MUST output BOTH as COMPLETE, un-truncated subtitle entries with their true overlapping acoustic timestamps. NEVER truncate one speaker's sentence or split the time interval half-and-half to avoid timestamp overlap.
       - Primary Subtitle Identification on Overlaps: Whenever two entries overlap in time, explicitly indicate which entry is the main subtitle using `""p"": true` (for the main foreground dialogue that stays on the bottom track) and `""p"": false` (for the secondary interjection or background song lyric that goes to the top track).
   {segInstruction}
   - TIMESTAMPS & ACOUSTIC BOUNDARY SNAPPING:
     * Format & Precision: Set 's' (start) and 'e' (end) timestamps strictly to 'HH:MM:SS.mmm', with timing accuracy at least to the 100ms level (e.g., '.100', '.300', '.700'). Strictly DO NOT lazily round most sentences' 's'/'e' to '.000' or '.500'.
     * Physical Acoustic Onset & Decay Snapping (发声起点与衰减截止点锚定):
       - The 's' timestamp MUST anchor precisely to the natural spoken onset of the FIRST syllable of the first word in this subtitle entry (jump across gaps; NEVER inertially attach to the previous sentence's 'e'). Accurately pinpoint the position of that first word's first syllable (do NOT snap to breaths, music, or sound effects, and strictly NEVER locate boundaries based on audio volume level alone).
       - The 'e' timestamp MUST anchor precisely where the acoustic decay of the LAST syllable of the last word in this subtitle entry naturally finishes before silence/pause begins (do not drag into background music or audio gaps).
     * Universal Split Re-Alignment (断句拆行独立声学重对齐):
       - Whenever a continuous sentence is divided into multiple subtitle entries (for character limits, clauses, or pauses), NEVER divide duration linearly or proportionally.
       - Every individual split entry MUST independently re-snap its 's' and 'e' to the physical speech boundaries of that specific phrase. Audible pauses or breath gaps between split phrases MUST remain as empty silence between timestamps (never hang subtitles across pauses).
     * Anti-Drift Alignment:
       - Be careful not to confuse or mix up the 's' and 'e' timestamps between different clauses or adjacent utterances.
   - MANDATORY FLAGGING RULES (High-sensitivity quality screen; add `""f""` field whenever any of these conditions occurs - DO NOT GUESS OR FABRICATE WITHOUT FLAGGING):
     1) Unsearched / Unverified Chinese Proper Nouns (未检索中文译名的人名/专有名词必打标):
        You MUST add `""f""` to ANY person name, character name, or proper noun in 'tgt' whose exact Chinese characters were NOT explicitly retrieved via a Chinese search query in Stage 1 and listed in `""g""` (even if the name feels familiar from internal memory), or whenever you had to transliterate/improvise (e.g., `""f"": ""unsearched Chinese proper noun: XXX""`).
     2) Non-Standard / Uncertain Translation & Paraphrased Complex Syntax (翻译规范度/长难句意译存疑必打标):
        If the Chinese translation feels awkward, ambiguous, or potentially divergent from video context, OR if complex long syntax or unfamiliar concepts make the translation uncertain, ADD `""f""` (e.g., `""f"": ""uncertain translation / complex syntax: YYY""`).
     3) Phonetic Discrepancy & Multi-Candidate Hesitation (听感犹豫/多备选发音必打标):
        Transcribed words do not fully or unambiguously match the heard acoustic syllables/phonemes. Whenever you hesitate, re-listen, or consider 2 or more possible hearings/interpretations during your thinking process, you MUST add `""f""` (even if you eventually settled on one).
     4) Acoustic Masking / Singing (声学掩蔽/演唱):
        You MUST add `""f""` whenever speech is muffled, extremely rapid, whispered, or heavily masked by music, noise, sound effects, or distortion. For song lyrics, you MUST add `""f""` in BOTH cases: (a) complete official lyrics were NOT retrieved in Stage 1; OR (b) although official lyrics were retrieved, you are uncertain whether what is actually sung in the audio matches them completely (e.g., suspected skipped verses, version differences, altered lyrics, or mismatch between heard singing and official lyrics).
     5) Contextual Incoherence: The heard phrase contradicts surrounding conversation logic or scene grammar.
     6) Confirmed or Suspected Dual-Voice / Dialogue-Lyric Overlap (确定或怀疑存在双语音重叠必打标):
        Whenever you detect OR even suspect that two voices (two simultaneous speakers, or speech overlapping with background vocals/lyrics) are occurring at the same time, you MUST add `""f""` (e.g., `""f"": ""dual-voice overlap: verify both complete utterances and primary track""`) for Stage 3 verification.
     7) Unclear Start/End Syllables or Boundary Misalignment (起止音节难以定位或边界错位必打标):
        Whenever interference makes it impossible to accurately hear the onset of the first word's first syllable or the end of the last word's last syllable, leaving the 's'/'e' boundaries uncertain (or risking shifted adjacent lines or omitted lines), you MUST add `""f""` (e.g., `""f"": ""timestamp/boundary check""`).
     8) Completely Unintelligible Speech or Lyrics (完全无法辨识语音或歌词必打标):
        For speech or sung lyrics completely unintelligible or un-anchor-able due to noise/music masking, refrain from guessing and output with `""f""` requesting full segment re-verification (e.g., `""f"": ""unintelligible / full segment re-verification required""`).
   {styleInstruction}
   - SOURCE & TARGET TEXT SPECIFICATIONS:
     * In 'src' (original spoken text), output verbatim transcription matching heard syllables, native orthography, and standard numeral conventions of that language (never arbitrarily mix numeral systems), aligned strictly to the exact clause boundaries of 'tgt' without artificial continuation ellipses.
     * In 'tgt' (translated subtitle text), provide professional, localized Chinese subtitles:
       - Conciseness & Tone Fidelity: Convey the most accurate meaning and tone using the fewest words possible. If the original audio is already in Chinese, output polished, standardized Chinese subtitles.
       - Natural Chinese Expression & Particle Localization: 'tgt' must read as natural, idiomatic Chinese. Never leave untranslated foreign modal particles, honorific suffixes, or phonetic filler syllables in 'tgt'; convert their nuance into natural Chinese expressions or omit them cleanly. Widely accepted international terms and standard technical acronyms (e.g., AI, CPU, DNA) may be retained in their conventional form.
       - Formatting Conventions: Never mix Arabic numerals and Chinese number characters within the same number or weekday (e.g., write '五千二百' and '星期二', NEVER '五千2百' or '星期2'). Enclose titles of works (books, films, songs, shows, games) in '《》', and use '·' as the separator in transliterated names (e.g., '约翰·史密斯').";

        string simplifiedStage2Rules =
$@"Follow all acoustic and transcription principles:
1. Verbatim syllable fidelity & Complete Overlaps: No semantic or acoustic fabrication; in 'src', preserve exact grammatical endings and spoken phonemes. Never discard remarks or background song lyrics masked by noise, music, or louder foreground speech. Do NOT miss any faint/quiet voices or speech. For creative drama/anime ONLY: identical words repeated 4+ times in rapid succession within a short interval may merge into 2 repetitions with an ellipsis ('……') from the first start to the last end. Never fabricate imaginary text for completely unintelligible speech or lyrics masked by loud music/noise—refrain from guessing and add `""f""` requesting full segment re-verification. Whenever two voices occur simultaneously (dual speakers or dialogue + background song lyrics), you MUST output BOTH as COMPLETE, un-truncated subtitle entries with their true overlapping timestamps (never truncate either voice or slice time half-and-half), and set `""p"": true` on the main dialogue entry and `""p"": false` on the secondary/lyric entry.
2. Acoustic Snapping & Precision: Timing MUST be accurate at least to the 100ms level (strictly DO NOT lazily round most entries to '.000' or '.500'). Pinpoint and anchor 's' strictly to the natural spoken onset of the first word's first syllable (do NOT snap to breaths, music, or sound effects, NEVER locate boundaries by volume alone, and NEVER inertially attach 's' near the previous sentence's 'e'); anchor 'e' strictly to the acoustic decay endpoint of the last word's last syllable. Be careful not to confuse the 's' and 'e' timestamps between different clauses or adjacent lines.
3. Universal Split Re-alignment: For ALL split subtitle entries (whether split due to character limits, clauses, pauses, or pacing), independently anchor each split line to the physical acoustic speech boundaries of that specific phrase (never divide time linearly).
4. Clean boundaries: NO artificial leading/trailing ellipses ('...' or '……').
5. Dual-Track Length Limit: In BOTH 'tgt' and 'src', CJK scripts MUST NOT exceed {maxCjkChars} characters, and ALL non-CJK scripts MUST NOT exceed {maxNonCjkChars} characters. Split at a natural grammatical pause whenever either line exceeds its limit.
{styleInstruction}
6. Source & Target text: In 'src', follow the native orthography and standard numeral conventions of the spoken language, aligned strictly to each split clause. In 'tgt', convey the most accurate tone using the fewest words in idiomatic Chinese (convert foreign modal particles into natural Chinese expressions or omit them cleanly; standard technical acronyms may remain). Enclose work titles in '《》' and use '·' for transliterated name separators.
7. Mandatory Flagging: Add the `""f""` field whenever ANY of these occur: any proper noun/person name not retrieved in Chinese in Stage 1 and listed in `""g""` (or temporarily transliterated/improvised), awkward/ambiguous/divergent translation or uncertain complex syntax, hesitation or 2+ possible hearings during thinking, muffled/extremely rapid/whispered or heavily masked speech, song lyrics lacking complete official lyrics from Stage 1 or uncertain whether the actual singing matches official lyrics completely, contextual incoherence, confirmed or suspected simultaneous dual-voice/lyric overlap, interference preventing accurate pinpointing of first/last syllable boundaries (uncertain boundaries or suspected line shifting/omission), or completely unintelligible speech or lyrics.";

        return (fullStage2Rules, simplifiedStage2Rules);
    }

    private static string FormatSegmentListLines(IEnumerable<AudioSegmentChunk> list, bool includeToolCallSyntax)
    {
        var sb = new StringBuilder();
        foreach (var seg in list)
        {
            string fileName = Path.GetFileName(seg.FilePath);
            string globalRange = $"{FormatLocalTimestamp(seg.NominalStart)} to {FormatLocalTimestamp(seg.ExtractEnd)}";
            string localDur = FormatLocalTimestamp(seg.ExtractEnd - seg.ExtractStart);
            if (includeToolCallSyntax)
            {
                sb.AppendLine($"   - Segment {seg.Index} (`{fileName}`, global window {globalRange}, local duration {localDur}): call `read_file` with Path=\"{seg.FilePath}\" (or `view_file` with AbsolutePath=\"{seg.FilePath}\")");
            }
            else
            {
                sb.AppendLine($"   - Segment {seg.Index}: `{fileName}` (path: \"{seg.FilePath}\", global window {globalRange}, local duration {localDur})");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildLockedGlossaryInstruction(
        IReadOnlyList<GlossaryEntry> userEntries,
        IReadOnlyDictionary<string, string> accumulatedGlossary,
        bool isContinuationSession)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string userLine = GlossaryService.FormatUserGlossaryLine(userEntries.ToList());
        if (!string.IsNullOrWhiteSpace(userLine))
        {
            parts.Add(userLine);
            foreach (var entry in userEntries)
            {
                seen.Add(entry.Source);
            }
        }

        foreach (var kvp in accumulatedGlossary)
        {
            if (seen.Add(kvp.Key))
            {
                parts.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        string combinedLine = string.Join(" | ", parts);
        string title = isContinuationSession
            ? "LOCKED GLOSSARY (INHERITED & USER-VERIFIED — HIGHEST PRIORITY)"
            : "USER GLOSSARY (PRE-VERIFIED & LOCKED — HIGHEST PRIORITY)";

        return
$@"
   - {title}:
     * Strictly reuse the following locked translations throughout all stages. Treat EVERY listed term as already verified in `g` (do NOT waste `search_web` calls re-searching any of them or flag them as unsearched), and include ONLY newly discovered proper nouns (not already listed here) in the JSON `""g""` array:
       {combinedLine}";
    }

    private static string BuildPriorContextSection(IReadOnlyList<SubtitleItem> priorSubtitles, TimeSpan nextSegNominalStart)
    {
        if (priorSubtitles.Count == 0)
        {
            return string.Empty;
        }

        int skipCount = Math.Max(0, priorSubtitles.Count - MaxPriorTranslatedContextLines);
        var recent = priorSubtitles.Skip(skipCount).ToList();
        int boundaryCount = Math.Min(BoundaryBilingualOverlapLines, recent.Count);
        int bodyCount = recent.Count - boundaryCount;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"PRIOR TRANSLATED CONTEXT (LAST {recent.Count} SUBTITLE LINES BEFORE {FormatLocalTimestamp(nextSegNominalStart)} — FOR STORYLINE, TONE & CONTINUITY ONLY):");
        if (skipCount > 0)
        {
            sb.AppendLine($"({skipCount} earlier lines omitted; recent {recent.Count} lines shown below without timestamps/indices.)");
        }

        if (bodyCount > 0)
        {
            for (int i = 0; i < bodyCount; i++)
            {
                var item = recent[i];
                string line = !string.IsNullOrWhiteSpace(item.TargetText) ? item.TargetText.Trim() : item.SourceText.Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    sb.AppendLine(line);
                }
            }
        }

        sb.AppendLine($"IMMEDIATE BOUNDARY LINES (LAST {boundaryCount} LINES FROM PREVIOUS SESSION ENDING AT GLOBAL TIME {TimeHelper.ToSrtTime(recent[^1].EndTime)}):");
        for (int i = bodyCount; i < recent.Count; i++)
        {
            var item = recent[i];
            sb.AppendLine($"- {item.SourceText.Trim()} => {item.TargetText.Trim()}");
        }

        return sb.ToString();
    }

    private static string BuildPriorSearchFindingsSection(IReadOnlyList<(string Query, string Summary)> priorSearches)
    {
        if (priorSearches.Count == 0)
        {
            return string.Empty;
        }

        var bodySb = new StringBuilder();
        int includedCount = 0;
        bool truncated = false;

        foreach (var (query, summary) in priorSearches)
        {
            if (string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            string entry = $"[Search: {query}]\n{summary}";
            int separatorLen = bodySb.Length > 0 ? 2 : 0; // "\n\n" between entries
            if (bodySb.Length + separatorLen + entry.Length > MaxPriorStage1SearchChars)
            {
                if (bodySb.Length == 0)
                {
                    bodySb.Append(entry[..MaxPriorStage1SearchChars].TrimEnd());
                    includedCount++;
                }
                truncated = true;
                break;
            }

            if (bodySb.Length > 0)
            {
                bodySb.Append("\n\n");
            }
            bodySb.Append(entry);
            includedCount++;
        }

        if (bodySb.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"PRIOR STAGE 1 VERIFIED SEARCH FINDINGS (EARLIEST {includedCount}/{priorSearches.Count} SEARCHES, MAX {MaxPriorStage1SearchChars} CHARS — DO NOT RE-SEARCH THESE):");
        if (truncated)
        {
            sb.AppendLine($"(Later searches exceeding {MaxPriorStage1SearchChars} chars were omitted; rely on these earliest foundational findings and LOCKED GLOSSARY.)");
        }
        sb.AppendLine(bodySb.ToString());
        return sb.ToString();
    }

    private static int AppendStage1SearchFindingsFromCliLogs(
        string? convId,
        List<(string Query, string Summary)> targetList,
        HashSet<string> seenQueries)
    {
        if (string.IsNullOrWhiteSpace(convId))
        {
            return 0;
        }

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string logsDir = Path.Combine(userHome, ".gemini", "antigravity-cli", "brain", convId, ".system_generated", "logs");
        string fullLog = Path.Combine(logsDir, "transcript_full.jsonl");
        string logPath = File.Exists(fullLog) ? fullLog : Path.Combine(logsDir, "transcript.jsonl");
        if (!File.Exists(logPath))
        {
            return 0;
        }

        int added = 0;
        int userInputCount = 0;
        var pendingTools = new Queue<(string Name, string? Query)>();

        try
        {
            foreach (string line in File.ReadLines(logPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string stepType = root.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? string.Empty) : string.Empty;

                if (string.Equals(stepType, "USER_INPUT", StringComparison.OrdinalIgnoreCase))
                {
                    userInputCount++;
                    if (userInputCount >= 2)
                    {
                        // Stop once Turn 2 begins; Turn 1 holds all Stage 1 web searches for this session group
                        break;
                    }
                    continue;
                }

                if (string.Equals(stepType, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCallsProp.EnumerateArray())
                        {
                            string toolName = tc.TryGetProperty("name", out var nameProp) ? (nameProp.GetString() ?? string.Empty) : string.Empty;
                            string? query = null;
                            if (string.Equals(toolName, "search_web", StringComparison.OrdinalIgnoreCase) &&
                                tc.TryGetProperty("args", out var argsProp) &&
                                argsProp.TryGetProperty("query", out var qProp))
                            {
                                query = qProp.GetString();
                            }
                            pendingTools.Enqueue((toolName, query));
                        }
                    }
                    continue;
                }

                if (string.Equals(stepType, "GENERIC", StringComparison.OrdinalIgnoreCase) && pendingTools.Count > 0)
                {
                    var (toolName, query) = pendingTools.Dequeue();
                    if (!string.Equals(toolName, "search_web", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(query))
                    {
                        continue;
                    }

                    string? rawContent = root.TryGetProperty("content", out var contentProp) ? contentProp.GetString() : null;
                    string summary = ExtractEffectiveSearchSummary(rawContent);
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        continue;
                    }

                    string normalizedQuery = query.Trim();
                    if (seenQueries.Add(normalizedQuery))
                    {
                        targetList.Add((normalizedQuery, summary));
                        added++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning($"[AI转录] 读取阶段一检索摘要失败: {ex.Message}");
        }

        return added;
    }

    private static string ExtractEffectiveSearchSummary(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return string.Empty;
        }

        string text = rawContent.Replace("\r\n", "\n");
        const string summaryMarker = "returned the following summary:";
        int markerIdx = text.IndexOf(summaryMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx >= 0)
        {
            text = text[(markerIdx + summaryMarker.Length)..];
        }
        else
        {
            // Fallback: strip leading CLI metadata lines if summary header differs
            var filtered = text
                .Split('\n')
                .SkipWhile(l => l.StartsWith("Created At:", StringComparison.OrdinalIgnoreCase) ||
                                l.StartsWith("Completed At:", StringComparison.OrdinalIgnoreCase));
            text = string.Join('\n', filtered);
        }

        int sourcesIdx = text.IndexOf("\nSources:", StringComparison.OrdinalIgnoreCase);
        if (sourcesIdx >= 0)
        {
            text = text[..sourcesIdx];
        }
        else
        {
            int noteIdx = text.IndexOf("\nNote: Source URLs are HTTP redirects", StringComparison.OrdinalIgnoreCase);
            if (noteIdx >= 0)
            {
                text = text[..noteIdx];
            }
        }

        // Remove inline numeric grounding citations like [1], [2] and collapse excessive blank lines
        text = Regex.Replace(text, @"\[\d{1,2}\]", string.Empty);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string BuildBoundaryAnchorInstruction(AudioSegmentChunk seg, TimeSpan maxSeen, SubtitleItem? lastItem)
    {
        string segFileName = Path.GetFileName(seg.FilePath);
        TimeSpan localResumeStart = maxSeen > seg.NominalStart ? (maxSeen - seg.NominalStart) : TimeSpan.Zero;
        TimeSpan localNominalEnd = seg.NominalEnd - seg.NominalStart;
        TimeSpan localExtractEnd = seg.ExtractEnd > seg.ExtractStart ? (seg.ExtractEnd - seg.ExtractStart) : localNominalEnd;
        string localResumeStr = FormatLocalTimestamp(localResumeStart);
        string localNominalEndStr = FormatLocalTimestamp(localNominalEnd);
        string localExtractEndStr = FormatLocalTimestamp(localExtractEnd);
        bool hasTailExtension = seg.ExtractEnd > seg.NominalEnd;

        string lastLinePreview = lastItem != null
            ? (!string.IsNullOrWhiteSpace(lastItem.TargetText) ? lastItem.TargetText : lastItem.SourceText)
            : string.Empty;

        string tailInstruction = hasTailExtension
            ? $" (`{segFileName}` spans local `00:00:00.000` to `{localExtractEndStr}`, including a 10-second overlap tail past `{localNominalEndStr}`; if `{localNominalEndStr}` falls mid-sentence, use that tail to finish the complete sentence before stopping)"
            : string.Empty;

        if (localResumeStart > TimeSpan.Zero && !string.IsNullOrWhiteSpace(lastLinePreview))
        {
            return
$@"The previous segment already finished across the overlap boundary around local time {localResumeStr} inside `{segFileName}` with the final subtitle line: ""{lastLinePreview}"".
Skip any repeated audio up through {localResumeStr} (""{lastLinePreview}"") and start from the very first NEW spoken utterance AFTER {localResumeStr} inside `{segFileName}` up to approximately local time {localNominalEndStr}{tailInstruction}.";
        }

        if (!string.IsNullOrWhiteSpace(lastLinePreview))
        {
            return
$@"The previous segment finished with the final subtitle line: ""{lastLinePreview}"".
Transcribe `{segFileName}` starting from local time `00:00:00.000` (immediately after ""{lastLinePreview}"") up to approximately local time {localNominalEndStr}{tailInstruction}.";
        }

        return $@"Transcribe and translate all dialogue and sung lyrics inside `{segFileName}` from local time `00:00:00.000` up to approximately local time {localNominalEndStr}{tailInstruction}.";
    }

    private static string BuildGroupTurn1Prompt(
        IReadOnlyList<AudioSegmentChunk> segments,
        int totalVideoSegments,
        int groupIndex,
        int totalGroups,
        SubtitleItem? lastPriorItem,
        TimeSpan priorMaxSeen,
        string fullStage2Rules,
        string searchInstruction,
        string entityRetrievalInstruction,
        string lockedGlossaryBlock,
        string priorSearchBlock,
        string priorContextBlock,
        WebSearchMode webSearchMode)
    {
        string allSegmentsManifest = FormatSegmentListLines(segments, includeToolCallSyntax: false);
        string allAudioReadInstructions = FormatSegmentListLines(segments, includeToolCallSyntax: true);
        string toolReadGuidance =
$@"   - Load ALL audio segment file(s) assigned to this session directly in this main session:
{allAudioReadInstructions}";

        var seg0 = segments[0];
        string seg0FileName = Path.GetFileName(seg0.FilePath);
        TimeSpan seg0NominalDur = seg0.NominalEnd - seg0.NominalStart;
        TimeSpan seg0ExtractDur = seg0.ExtractEnd > seg0.ExtractStart ? (seg0.ExtractEnd - seg0.ExtractStart) : seg0NominalDur;
        string seg0NominalEndLocalStr = FormatLocalTimestamp(seg0NominalDur);
        string seg0ExtractEndLocalStr = FormatLocalTimestamp(seg0ExtractDur);

        bool isContinuationSession = groupIndex > 0;
        string seg0Desc = totalVideoSegments > 1
            ? $"Segment {seg0.Index}/{totalVideoSegments}: `{seg0FileName}` (local 00:00:00.000 to ~{seg0NominalEndLocalStr}, file length {seg0ExtractEndLocalStr})"
            : $"Full Audio (`{seg0FileName}`)";

        string seg0BoundaryRule;
        if (isContinuationSession)
        {
            string anchorInstruction = BuildBoundaryAnchorInstruction(seg0, priorMaxSeen, lastPriorItem);
            seg0BoundaryRule =
$@"   - SEGMENT BOUNDARY & SEGMENT-LOCAL TIMELINE RULE:
     * Transcribe ONLY `{seg0FileName}` (Segment {seg0.Index}/{totalVideoSegments}, global window starting at {FormatLocalTimestamp(seg0.NominalStart)}).
     * Output `s` (start) and `e` (end) timestamps strictly RELATIVE to the start of `{seg0FileName}` (`00:00:00.000` = beginning of `{seg0FileName}`). Do NOT add {FormatLocalTimestamp(seg0.NominalStart)} manually—the host program adds it automatically.
     * {anchorInstruction.Replace("\n", "\n     * ")}
";
        }
        else if (segments.Count > 1)
        {
            seg0BoundaryRule =
$@"   - SEGMENT BOUNDARY & SEGMENT-LOCAL TIMELINE RULE:
     * Transcribe ONLY `{seg0FileName}` (Segment {seg0.Index}/{totalVideoSegments}).
     * Output `s` (start) and `e` (end) timestamps strictly RELATIVE to the start of `{seg0FileName}` (`00:00:00.000` at the beginning of `{seg0FileName}`).
     * `{seg0FileName}` spans local `00:00:00.000` to `{seg0ExtractEndLocalStr}`, including a 10-second overlap tail past `{seg0NominalEndLocalStr}` so ongoing boundary sentences are never cut off.
     * Transcribe up to approximately local time {seg0NominalEndLocalStr}. If {seg0NominalEndLocalStr} falls in the middle of an ongoing spoken sentence, use the extra 10-second tail inside `{seg0FileName}` to finish that complete sentence before stopping.
";
        }
        else
        {
            seg0BoundaryRule =
$@"   - SEGMENT-LOCAL TIMELINE RULE:
     * Output `s` (start) and `e` (end) timestamps strictly RELATIVE to the start of `{seg0FileName}` (`00:00:00.000` at the beginning of the file).
";
        }

        string stage1Section;
        if (!isContinuationSession)
        {
            stage1Section =
$@"2. STAGE 1 - OVERALL BACKGROUND & ENTITY GROUNDING (ACROSS ALL LOADED SEGMENTS):
   - Perceive the macro scenario across ALL loaded audio segment files (`{Path.GetFileName(segments[0].FilePath)}` through `{Path.GetFileName(segments[^1].FilePath)}`): subject domain (e.g. creative drama, anime, film/cinema, TV series, technology, academic lecture, documentary, news/talk show, business), primary speakers/participants, speaking styles, and core specialized entities (proper nouns, personal/entity names, organizations, technical terms, domain nomenclature). Establishing domain vocabulary early anchors unfamiliar phonetic patterns.
   - OFFICIAL CHINESE TRANSLATED & CANONICAL NAMES RETRIEVAL (MANDATORY FOR KNOWN IPs & SPECIALIZED DOMAINS):
{entityRetrievalInstruction}
   - NATIVE CHINESE AUDIO RECOGNITION:
     * If the original audio speech is already in Chinese (Mandarin, regional dialects, Cantonese, etc.), identify the thematic topic, industry terminology, idioms, and official proper nouns in Chinese upfront to avoid homophone confusion (同音字/异形字偏差).
   {searchInstruction}{lockedGlossaryBlock}";
        }
        else
        {
            string contSearchRule = webSearchMode == WebSearchMode.Off
                ? "   - WEB SEARCH RULES (OFFLINE): Do NOT perform any web searches. Rely strictly on LOCKED GLOSSARY, prior Stage 1 search findings, prior context, and audio."
                : $"   {searchInstruction}\n" +
                  "   - CONTINUATION SESSION SEARCH RESTRICTION (STRICT): Strictly reuse all locked translations in LOCKED GLOSSARY and background information in PRIOR STAGE 1 VERIFIED SEARCH FINDINGS—do NOT waste `search_web` calls re-searching anything already established there. When THIS session's audio introduces content missing from both LOCKED GLOSSARY and PRIOR STAGE 1 VERIFIED SEARCH FINDINGS, you may still perform targeted `search_web` calls (following the exact same Chinese verification & non-dialogue-search rules—NO searches outside these strict limits).";

            stage1Section =
$@"2. STAGE 1 - CONTINUATION SESSION GROUNDING (SESSION {groupIndex + 1}/{totalGroups}):
   - You are continuing a long-form video starting from Segment {seg0.Index}/{totalVideoSegments} ({FormatLocalTimestamp(seg0.NominalStart)} onward). Maintain full continuity of work background, characters, tone, and speaker relationships from PRIOR STAGE 1 VERIFIED SEARCH FINDINGS and PRIOR TRANSLATED CONTEXT.
{contSearchRule}{lockedGlossaryBlock}";
        }

        return
$@"You are an intelligent, context-aware video subtitle transcription and translation expert dedicated to producing professional Chinese subtitles.
Your task is to transcribe and translate all dialogue and speech across the audio segment file(s) loaded in this session into professional subtitles.

ALL AUDIO SEGMENTS IN THIS SESSION:
{allSegmentsManifest}
{priorSearchBlock}{priorContextBlock}
WORKFLOW & REQUIREMENTS:

1. TOOL USAGE & SANDBOX:
{toolReadGuidance}
   - STRICT NO-SUBAGENT & NO INTERMEDIATE TEXT: Do NOT call `invoke_subagent`, `define_subagent`, `send_message`, `manage_subagents`, or `schedule`. Execute all stages directly in this main session, and never output intermediate progress/status text (such as ""正在读取..."" or ""请稍候..."") before the final output.
   - STRICT: You are in an isolated sandbox. Do NOT execute terminal commands or scripts, and do NOT access files outside this directory.

{stage1Section}

3. STAGE 2 - LINEAR TRANSCRIPTION & SEGMENTATION ({seg0Desc}):
{seg0BoundaryRule}{fullStage2Rules}

4. STRICT JSON SCHEMA OUTPUT:
   - Return ONLY a JSON object matching the enforced output schema (`g` + `sub`). Do NOT include explanations or markdown commentary.
   - `g`: array of verified proper nouns (`[{{""k"":""..."",""v"":""...""}}]`, empty array `[]` if none or unchanged).
   - `sub`: array of all dialogue/lyric entries (`s`, `e` as `HH:MM:SS.mmm`, `src`, `tgt`, optional `f`/`p`).
   - Do NOT artificially restrict total entries; generate naturally based on audio speech density.";
    }

    private static int AppendSegmentEntries(
        List<SubtitleItem> targetList,
        IEnumerable<SubtitleItem> batch,
        string segFileName,
        TimeSpan segNominalStart,
        TimeSpan maxSeen,
        bool deduplicateBeforeMaxSeen,
        int baseGlobalIndex)
    {
        int added = 0;
        foreach (var item in batch)
        {
            if (deduplicateBeforeMaxSeen && item.EndTime <= maxSeen + TimeSpan.FromMilliseconds(250))
            {
                continue;
            }
            item.Index = baseGlobalIndex + targetList.Count + 1;
            item.AudioFile = segFileName;
            item.SegmentOffset = segNominalStart;
            targetList.Add(item);
            added++;
        }
        return added;
    }

    private static void MergeGlossaryExcludingUserLocked(
        IReadOnlyList<CliGlossaryEntry>? entries,
        HashSet<string> userLockedSources,
        Dictionary<string, string> accumulatedGlossary)
    {
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            string src = GlossaryService.NormalizeTermPunctuation(entry.Source);
            string tgt = GlossaryService.NormalizeTermPunctuation(entry.Target);
            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt) ||
                string.Equals(src, "source1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!userLockedSources.Contains(src))
            {
                accumulatedGlossary[src] = tgt;
            }
        }
    }

    private static bool ShouldTriggerContinuation(TimeSpan targetEnd, TimeSpan maxSeen, int subtitleCount, bool isFinalGroup = true)
    {
        if (targetEnd <= TimeSpan.Zero || subtitleCount == 0) return false;

        double remainingSeconds = (targetEnd - maxSeen).TotalSeconds;
        if (remainingSeconds <= 0) return false;

        // Intermediate session groups end in mid-video dialogue, so only allow a tight 15s boundary tolerance
        if (!isFinalGroup)
        {
            return remainingSeconds > 15.0;
        }

        // For short final media (<= 3.5 minutes)
        if (targetEnd <= TimeSpan.FromMinutes(3.5))
        {
            if (remainingSeconds <= 45.0 || maxSeen.TotalSeconds >= targetEnd.TotalSeconds * 0.5)
            {
                return false;
            }
        }

        // For medium/long final media (> 3.5 minutes): tolerate outro music/credits buffer
        double outroBufferThreshold = Math.Max(35.0, targetEnd.TotalSeconds * 0.04);
        if (remainingSeconds <= outroBufferThreshold)
        {
            return false;
        }

        return true;
    }

    private static string NormalizeSupportedModelName(string? requestedModel)
    {
        string candidate = string.IsNullOrWhiteSpace(requestedModel) ? DefaultFallbackModel : requestedModel.Trim();
        if (candidate.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase))
        {
            string downgraded = candidate[..^6] + "-high";
            AppLogService.Instance.LogInfo($"[AI转录] 已移除 xhigh 支持，模型 {candidate} 已自动降级为 {downgraded}");
            candidate = downgraded;
        }

        return IsSupportedGeminiModel(candidate) ? candidate : DefaultFallbackModel;
    }

    private static (string Stage2SchemaPath, string Stage3SchemaPath) EnsureOutputSchemaFiles(string sandboxDir)
    {
        Directory.CreateDirectory(sandboxDir);
        string stage2Path = Path.Combine(sandboxDir, "cli_stage2_schema.json");
        string stage3Path = Path.Combine(sandboxDir, "cli_stage3_schema.json");

        File.WriteAllText(stage2Path, BuildCompactSchemaJson(includeStage3Identity: false), Utf8NoBom);
        File.WriteAllText(stage3Path, BuildCompactSchemaJson(includeStage3Identity: true), Utf8NoBom);
        return (stage2Path, stage3Path);
    }

    private static string BuildCompactSchemaJson(bool includeStage3Identity)
    {
        string identityProps = includeStage3Identity
            ? "\"i\": { \"type\": \"integer\" },\n          \"a\": { \"type\": \"string\" },\n          "
            : string.Empty;
        string flagProp = includeStage3Identity
            ? string.Empty
            : ",\n          \"f\": { \"type\": \"string\" }";
        string requiredSubFields = includeStage3Identity
            ? "[\"i\", \"a\", \"s\", \"e\", \"src\", \"tgt\"]"
            : "[\"s\", \"e\", \"src\", \"tgt\"]";

        return $$"""
{
  "type": "object",
  "properties": {
    "g": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "k": { "type": "string" },
          "v": { "type": "string" }
        },
        "required": ["k", "v"],
        "additionalProperties": false
      }
    },
    "sub": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          {{identityProps}}"s": { "type": "string" },
          "e": { "type": "string" },
          "src": { "type": "string" },
          "tgt": { "type": "string" }{{flagProp}},
          "p": { "type": "boolean" }
        },
        "required": {{requiredSubFields}},
        "additionalProperties": false
      }
    }
  },
  "required": ["g", "sub"],
  "additionalProperties": false
}
""";
    }

    private static void RegisterTaskConversation(string? workingDir, string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        string normalizedDir = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
        var set = _taskConversationIds.GetOrAdd(normalizedDir, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (set)
        {
            set.Add(conversationId.Trim());
        }
    }

    private async Task<CliTurnResult> RunCliTurnAsync(
        string resolvedCli,
        string? workingDir,
        string schemaFilePath,
        string modelName,
        string prompt,
        string? conversationId,
        Action<string>? onToolActivity,
        Action<string>? onTextDelta,
        CancellationToken cancellationToken)
    {
        var argList = new List<string>();

        if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
        {
            argList.Add("--add-dir");
            argList.Add(workingDir);
        }

        argList.Add("--sandbox");
        argList.Add("--print-timeout");
        argList.Add("35m");
        argList.Add("--disable-slash-commands");
        argList.Add("--dangerously-skip-permissions");

        argList.Add("--model");
        argList.Add(NormalizeSupportedModelName(modelName));

        argList.Add("--output-format");
        argList.Add("stream-json");
        argList.Add("--json-schema");
        argList.Add(schemaFilePath);

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            argList.Add("--conversation");
            argList.Add(conversationId);
        }

        string effectivePrompt = prompt;
        if (prompt.Length > 24000 && !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
        {
            string promptFile = Path.Combine(workingDir, $"prompt_{Guid.NewGuid():N}.txt").Replace("\\", "/");
            await File.WriteAllTextAsync(promptFile, prompt, Utf8NoBom, cancellationToken);
            effectivePrompt = $"Read the full task instructions in \"{promptFile}\" using read_file (or view_file), and strictly execute every instruction and output format requirement inside.";
        }

        argList.Add("--print");
        argList.Add(effectivePrompt);

        const int maxAttempts = 1;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                int delaySeconds = attempt switch
                {
                    2 => 6,
                    3 => 15,
                    _ => 30
                };
                AppLogService.Instance.LogWarning($"[CLI] 检测到网络连接异常 (如 EOF/连接中断)，等待 {delaySeconds} 秒后进行第 {attempt}/{maxAttempts} 次自动指数退避重试...");
                await Task.Delay(delaySeconds * 1000, cancellationToken);
            }

            var sw = Stopwatch.StartNew();
            AppLogService.Instance.LogInfo($"[CLI] 启动命令行任务: resumeId={conversationId ?? "<new>"}, model={modelName}{(attempt > 1 ? $" (重试第 {attempt} 次)" : "")}");

            string? capturedConversationId = conversationId;
            string? resultStatus = null;
            string? resultResponse = null;
            CliTurnPayload? structuredPayload = null;

            var execRes = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
            {
                FileName = resolvedCli,
                ArgumentList = argList,
                WorkingDirectory = workingDir,
                EnvironmentVariables = CreateCliEnvironment(),
                OnOutputLine = line => ProcessStreamEventLine(
                    line,
                    workingDir,
                    onToolActivity,
                    onTextDelta,
                    ref capturedConversationId,
                    ref resultStatus,
                    ref resultResponse,
                    ref structuredPayload)
            }, cancellationToken);

            sw.Stop();
            string stdout = execRes.StandardOutput;
            string stderr = execRes.StandardError;
            int exitCode = execRes.ExitCode;

            AppLogService.Instance.LogInfo($"[CLI] 任务执行结束: 耗时 {sw.Elapsed.TotalSeconds:F2}秒, ExitCode={exitCode}, Status={resultStatus ?? "<none>"}, ConvId={capturedConversationId ?? "<none>"}");

            try
            {
                string logPath = Path.Combine(FilePathHelper.GetLogsDirectory(), "cli_transcription.log");
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] resumeId={conversationId ?? "<new>"} actualConvId={capturedConversationId ?? "<none>"} (Attempt {attempt}/{maxAttempts}) Duration={sw.Elapsed.TotalSeconds:F2}s ExitCode={exitCode} Status={resultStatus ?? "<none>"}\nStdout Length={stdout.Length}\nStderr={stderr}\n\n";
                lock (_cliLogLock)
                {
                    File.AppendAllText(logPath, logEntry);
                }
            }
            catch { }

            string combinedDiagnostics = $"{stderr}\n{resultResponse}\n{stdout}";
            if (combinedDiagnostics.Contains("blocked by Gemini's filters", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("内容触发了 Gemini 官方安全过滤器误拦截（通常是由于动漫/影视剧中的争吵或对决台词触发了 AI 平台的敏感词策略）。程序已内置自动重试机制，请点击卡片右侧的【Retry】重试即可。");
            }

            if (structuredPayload == null && !string.IsNullOrWhiteSpace(resultResponse))
            {
                structuredPayload = TryParseCliPayload(resultResponse);
            }

            if (exitCode != 0 || (resultStatus != null && !string.Equals(resultStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
            {
                if (exitCode == 0 && structuredPayload != null)
                {
                    AppLogService.Instance.LogWarning($"[CLI] 进程返回 ExitCode=0 且 Status={resultStatus ?? "<none>"}，但已捕获有效 JSON 结构化结果 ({structuredPayload.Subtitles.Count} 条字幕)，继续使用该结果。");
                    return new CliTurnResult(capturedConversationId, structuredPayload);
                }

                bool isNetworkError = combinedDiagnostics.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                                   || combinedDiagnostics.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                                   || combinedDiagnostics.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
                                   || combinedDiagnostics.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                                   || combinedDiagnostics.Contains("Eligibility check failed", StringComparison.OrdinalIgnoreCase);

                if (attempt < maxAttempts && isNetworkError)
                {
                    AppLogService.Instance.LogWarning($"[CLI] 轮次执行遭遇偶发网络断连 (ExitCode={exitCode}, stderr: {stderr.Trim()})，准备接力重试...");
                    continue;
                }

                throw new InvalidOperationException($"CLI failed (Exit code {exitCode}, Status {resultStatus ?? "unknown"}):\n{stderr}\n{resultResponse ?? stdout}");
            }

            if (structuredPayload == null)
            {
                if (attempt < maxAttempts)
                {
                    AppLogService.Instance.LogWarning($"[CLI] 轮次未返回有效的 structured_output，准备重试 ({attempt}/{maxAttempts})...");
                    continue;
                }
                string errDetail = !string.IsNullOrWhiteSpace(stderr) ? $"\nCLI Details: {stderr.Trim()}" : "";
                throw new InvalidOperationException($"CLI did not return valid structured_output.{errDetail}");
            }

            return new CliTurnResult(capturedConversationId, structuredPayload);
        }

        throw new InvalidOperationException("CLI execution failed after all retry attempts.");
    }

    private static CliTurnPayload? TryParseCliPayload(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        string trimmed = rawText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = trimmed.IndexOf('\n');
            int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("g", out var glossaryProp)
                || glossaryProp.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("sub", out var subtitlesProp)
                || subtitlesProp.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<CliTurnPayload>(trimmed, StructuredJsonOptions);
            if (payload == null)
            {
                return null;
            }

            payload.Glossary ??= new();
            payload.Subtitles ??= new();
            return payload;
        }
        catch
        {
            return null;
        }
    }

    private static void ProcessStreamEventLine(
        string line,
        string? workingDir,
        Action<string>? onToolActivity,
        Action<string>? onTextDelta,
        ref string? capturedConversationId,
        ref string? resultStatus,
        ref string? resultResponse,
        ref CliTurnPayload? structuredPayload)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? evt = root.TryGetProperty("event", out var evtProp) ? evtProp.GetString() : null;

            if (string.Equals(evt, "init", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("conversation_id", out var cidProp))
                {
                    string? cid = cidProp.GetString();
                    if (!string.IsNullOrWhiteSpace(cid))
                    {
                        capturedConversationId = cid;
                        RegisterTaskConversation(workingDir, cid);
                    }
                }
                return;
            }

            if (string.Equals(evt, "step_update", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("step_update", out var su)
                && su.ValueKind == JsonValueKind.Object)
            {
                if (su.TryGetProperty("conversation_id", out var suCid))
                {
                    string? cid = suCid.GetString();
                    if (!string.IsNullOrWhiteSpace(cid))
                    {
                        capturedConversationId ??= cid;
                        RegisterTaskConversation(workingDir, cid);
                    }
                }

                string? stepType = su.TryGetProperty("step_type", out var stProp) ? stProp.GetString() : null;
                if (string.Equals(stepType, "tool", StringComparison.OrdinalIgnoreCase)
                    && su.TryGetProperty("tool_name", out var tnProp))
                {
                    string? toolName = tnProp.GetString();
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        onToolActivity?.Invoke(toolName);
                    }
                }
                else if (string.Equals(stepType, "agent_response", StringComparison.OrdinalIgnoreCase)
                    && su.TryGetProperty("text_delta", out var tdProp))
                {
                    string? delta = tdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(delta))
                    {
                        onTextDelta?.Invoke(delta);
                    }
                }
                return;
            }

            if (string.Equals(evt, "result", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("result", out var resObj)
                && resObj.ValueKind == JsonValueKind.Object)
            {
                if (resObj.TryGetProperty("conversation_id", out var rCid))
                {
                    string? cid = rCid.GetString();
                    if (!string.IsNullOrWhiteSpace(cid))
                    {
                        capturedConversationId = cid;
                        RegisterTaskConversation(workingDir, cid);
                    }
                }

                resultStatus = resObj.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
                resultResponse = resObj.TryGetProperty("response", out var respProp) ? respProp.GetString() : null;

                if (resObj.TryGetProperty("structured_output", out var soProp)
                    && soProp.ValueKind == JsonValueKind.Object)
                {
                    structuredPayload = soProp.Deserialize<CliTurnPayload>(StructuredJsonOptions);
                }
            }
        }
        catch
        {
        }
    }

    private static void AppendAutoFlag(SubtitleItem item, string reason)
    {
        if (string.IsNullOrWhiteSpace(item.Flag))
        {
            item.Flag = reason;
        }
        else if (!item.Flag.Contains(reason, StringComparison.OrdinalIgnoreCase))
        {
            item.Flag = $"{item.Flag}; {reason}";
        }
    }

    private static void FlagSequentialTimingInversions(List<SubtitleItem> groupSubtitles, string groupTag)
    {
        if (groupSubtitles.Count == 0) return;

        var backwardThreshold = TimeSpan.FromSeconds(10);

        for (int i = 1; i < groupSubtitles.Count; i++)
        {
            var prev = groupSubtitles[i - 1];
            var curr = groupSubtitles[i];
            if (curr.StartTime < prev.StartTime - backwardThreshold)
            {
                AppendAutoFlag(prev, "auto-flag: sequence timestamp jump/inversion - timestamp/boundary check");
                AppendAutoFlag(curr, "auto-flag: sequence timestamp inversion (start earlier than previous cue) - timestamp/boundary check");
            }
        }

        int autoFlagCount = groupSubtitles.Count(s =>
            !string.IsNullOrWhiteSpace(s.Flag) &&
            s.Flag.Contains("auto-flag:", StringComparison.OrdinalIgnoreCase));

        if (autoFlagCount > 0)
        {
            AppLogService.Instance.LogWarning(
                $"[AI转录] {groupTag}阶段二后检发现 {autoFlagCount} 条时间异常字幕（单条 >20秒 或 起止/序列时间倒挂），已自动打标送入阶段三复核。");
        }
    }

    private static List<SubtitleItem> FinalizeBatchTimestamps(
        IEnumerable<CliSubtitleEntry>? entries,
        TimeSpan timestampOffset = default,
        bool flagTimingAnomalies = false)
    {
        var result = new List<SubtitleItem>();
        if (entries == null) return result;

        var maxNormalDuration = TimeSpan.FromSeconds(20);
        foreach (var entry in entries)
        {
            var item = entry.ToSubtitleItem();
            var relativeStart = TimeHelper.ParseTimestamp(item.Start);
            var relativeEnd = TimeHelper.ParseTimestamp(item.End);

            if (relativeEnd <= relativeStart)
            {
                if (flagTimingAnomalies)
                {
                    AppendAutoFlag(item, "auto-flag: inverted timestamp (end <= start) - timestamp/boundary check");
                }
                relativeEnd = relativeStart.Add(TimeSpan.FromSeconds(1.5));
            }
            else if (flagTimingAnomalies && (relativeEnd - relativeStart) > maxNormalDuration)
            {
                AppendAutoFlag(item, $"auto-flag: abnormal duration ({(relativeEnd - relativeStart).TotalSeconds:F1}s > 20s) - timestamp/boundary check");
            }

            item.StartTime = timestampOffset + relativeStart;
            item.EndTime = timestampOffset + relativeEnd;
            item.Start = TimeHelper.ToSrtTime(item.StartTime);
            item.End = TimeHelper.ToSrtTime(item.EndTime);
            result.Add(item);
        }
        return result;
    }

    public static bool IsAuthIssue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string lower = text.ToLowerInvariant();
        return lower.Contains("auth") || lower.Contains("login") || lower.Contains("unauthorized") || lower.Contains("not logged in") || lower.Contains("sign in") || lower.Contains("token expired");
    }

    public static void CleanupSessionConversations(string? taskSandboxDir)
    {
        if (string.IsNullOrWhiteSpace(taskSandboxDir)) return;

        lock (_conversationCleanupLock)
        {
            CleanupSessionConversationsCore(taskSandboxDir);
        }
    }

    private static void CleanupSessionConversationsCore(string taskSandboxDir)
    {
        try
        {
            string normalizedTarget = Path.GetFullPath(taskSandboxDir).TrimEnd('\\', '/');
            var idsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_taskConversationIds.TryRemove(normalizedTarget, out var trackedIds))
            {
                lock (trackedIds)
                {
                    foreach (var id in trackedIds)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            idsToDelete.Add(id);
                        }
                    }
                }
            }

            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string cliRoot = Path.Combine(userHome, ".gemini", "antigravity-cli");
            string mapFile = Path.Combine(cliRoot, "cache", "last_conversations.json");
            Dictionary<string, string>? map = null;
            var matchedMapKeys = new List<string>();

            if (File.Exists(mapFile))
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mapFile));
                if (map != null && map.Count > 0)
                {
                    string targetPrefix = normalizedTarget + Path.DirectorySeparatorChar;
                    foreach (var kvp in map)
                    {
                        string candidatePath;
                        try
                        {
                            candidatePath = Path.GetFullPath(kvp.Key).TrimEnd('\\', '/');
                        }
                        catch
                        {
                            candidatePath = kvp.Key.Replace('/', '\\').TrimEnd('\\');
                        }

                        if (string.Equals(candidatePath, normalizedTarget, StringComparison.OrdinalIgnoreCase)
                            || candidatePath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(kvp.Value) && idsToDelete.Contains(kvp.Value)))
                        {
                            matchedMapKeys.Add(kvp.Key);
                            if (!string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                idsToDelete.Add(kvp.Value);
                            }
                        }
                    }
                }
            }

            string convDir = Path.Combine(cliRoot, "conversations");
            foreach (string convId in idsToDelete)
            {
                if (Directory.Exists(convDir))
                {
                    foreach (var dbFile in Directory.GetFiles(convDir, $"{convId}.db*"))
                    {
                        try { File.Delete(dbFile); } catch { }
                    }
                }

                string brainSub = Path.Combine(cliRoot, "brain", convId);
                if (Directory.Exists(brainSub))
                {
                    try { Directory.Delete(brainSub, recursive: true); } catch { }
                }

                string lockFile = Path.Combine(cliRoot, "presence", $"{convId}.lock");
                if (File.Exists(lockFile))
                {
                    try { File.Delete(lockFile); } catch { }
                }
            }

            if (map != null && matchedMapKeys.Count > 0)
            {
                bool removedAny = false;
                foreach (var key in matchedMapKeys)
                {
                    if (map.Remove(key))
                    {
                        removedAny = true;
                    }
                }

                if (removedAny)
                {
                    try
                    {
                        File.WriteAllText(mapFile, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public string ResolveCliPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // Check common locations
        string[] candidates =
        [
            Path.Combine(FilePathHelper.GetToolsDirectory(), "antigravity.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "antigravity.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "tools", "antigravity.exe"),
            Path.Combine(AppContext.BaseDirectory, "antigravity.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "antigravity.exe")),
            "antigravity.exe"
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return "antigravity.exe";
    }
}
