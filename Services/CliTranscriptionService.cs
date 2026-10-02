using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SubtitleMaster.Helpers;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public class CliTranscriptionService
{
    private static CliTranscriptionService? _instance;
    public static CliTranscriptionService Instance => _instance ??= new CliTranscriptionService();

    private (bool IsReady, string Message, List<string> Models)? _cachedStatus;
    private string? _cachedCliPath;
    private readonly SemaphoreSlim _checkCliLock = new(1, 1);

    /// <summary>
    /// Checks if Antigravity CLI is ready and returns list of available models.
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
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            ["CI"] = "1",
                            ["TERM"] = "dumb",
                            ["NO_COLOR"] = "1"
                        }
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
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["CI"] = "1",
                        ["TERM"] = "dumb",
                        ["NO_COLOR"] = "1"
                    }
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
                            if (!string.IsNullOrEmpty(modelId) && !models.Contains(modelId))
                            {
                                models.Add(modelId);
                            }
                        }
                    }

                    if (models.Count == 0)
                    {
                        models.Add("gemini-3.8-flash-high");
                        models.Add("gemini-3.7-flash-high");
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

    /// <summary>
    /// Transcribes and translates the full, unbroken audio file in a single unified context window.
    /// If the audio is long and multiple turns are needed, continues in the EXACT same conversation
    /// using --continue without ever switching context or chopping audio boundaries.
    /// </summary>
    public async Task<List<SubtitleItem>> TranscribeAudioAsync(
        string audioFilePath,
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
        string resolvedCli = ResolveCliPath(cliPath);
        if (!File.Exists(resolvedCli) && resolvedCli != "antigravity.exe")
        {
            throw new FileNotFoundException($"Antigravity CLI not found at: {resolvedCli}");
        }

        string fullAudioPath = Path.GetFullPath(audioFilePath).Replace('\\', '/');
        string? audioDir = Path.GetDirectoryName(fullAudioPath);

        string styleInstruction = translationStyle switch
        {
            TranslationStyle.Literal => 
                "- Style Preference: LITERAL TRANSLATION (偏直译). Faithfully preserve the original sentence structure, nuances, and technical terminology without unnecessary paraphrasing.",
            TranslationStyle.Free => 
                "- Style Preference: FREE / LOCALIZED TRANSLATION (偏意译). Prioritize natural, fluent, and localized target language expressions. Adapt idioms, slang, and cultural metaphors into smooth video subtitles.",
            _ => 
                "- Style Preference: BALANCED TRANSLATION (标准/平衡). Achieve an optimal balance between precision/terminology fidelity and conversational fluency suitable for video subtitles."
        };

        int maxChars = segmentationStyle == SegmentationStyle.Shorter ? 13 : 26;
        int maxSourceCjkChars = segmentationStyle == SegmentationStyle.Shorter ? 16 : 30;
        int maxSourceLatinChars = segmentationStyle == SegmentationStyle.Shorter ? 38 : 70;

        string segInstruction = segmentationStyle switch
        {
            SegmentationStyle.Shorter =>
                "- SUBTITLE SEGMENTATION (单行上限 13 个中文字符):\n" +
                "  * Semantic Integrity First: Prioritize complete, coherent semantic units and natural clause structures. Do NOT fragment intact grammatical sentences into isolated words or tiny phrases.\n" +
                "  * Holistic Natural Pacing: Balance semantic coherence with acoustic naturalness. Synthesize acoustic pauses, conversational breath, speaker turns, and speech tempo to decide the most natural breaking points.\n" +
                "  * Clean Boundaries (STRICT - NO ARTIFICIAL ELLIPSES): When a sentence is divided across subtitle entries, do NOT add artificial leading or trailing ellipses (\"...\", \"……\"), hyphens, or continuation dots at the break boundaries. Output clean, natural spoken words without unnecessary punctuation clutter.\n" +
                "  * Dual-Track Hard Length Limit (MANDATORY SPLIT IF EITHER LINE EXCEEDS LIMIT):\n" +
                "    - In 'target_text', Chinese characters MUST NOT exceed 13 characters (汉字字数 <= 13).\n" +
                "    - In 'source_text', CJK characters MUST NOT exceed 16 characters, or Latin/alphabetic text MUST NOT exceed 38 characters.\n" +
                "    - Whenever EITHER 'target_text' OR 'source_text' exceeds its respective limit, the sentence MUST be split at the most logical grammatical pause into separate subtitle entries with independent timestamps.",
            _ =>
                "- SUBTITLE SEGMENTATION: STANDARD (单行上限 26 个中文字符 - 默认推荐):\n" +
                "  * Semantic Integrity First: Prioritize complete, coherent semantic units and natural clause structures. Do NOT fragment intact grammatical sentences into isolated words or tiny phrases.\n" +
                "  * Holistic Natural Pacing: Balance semantic coherence with acoustic naturalness. Synthesize acoustic pauses, conversational breath, speaker turns, and speech tempo to decide the most natural breaking points.\n" +
                "  * Clean Boundaries (STRICT - NO ARTIFICIAL ELLIPSES): When a sentence is divided across subtitle entries, do NOT add artificial leading or trailing ellipses (\"...\", \"……\"), hyphens, or continuation dots at the break boundaries. Output clean, natural spoken words without unnecessary punctuation clutter.\n" +
                "  * Dual-Track Hard Length Limit (MANDATORY SPLIT IF EITHER LINE EXCEEDS LIMIT):\n" +
                "    - In 'target_text', Chinese characters MUST NOT exceed 26 characters (汉字字数 <= 26).\n" +
                "    - In 'source_text', CJK characters MUST NOT exceed 30 characters, or Latin/alphabetic text MUST NOT exceed 70 characters.\n" +
                "    - Whenever EITHER 'target_text' OR 'source_text' exceeds its respective limit, the sentence MUST be split at the most logical grammatical pause into separate subtitle entries with independent timestamps."
        };

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
            : "     * Post-Search Chinese Proper Noun Verification: Perform background and song lyric searches in the original spoken language first: theme/insert songs must be searched by song title and artist for their complete official lyrics (e.g., \"[Song]\" \"[Artist]\" full lyrics OR 歌詞), and the full text must be checked against the official lyrics during transcription, with acoustic hallucinations and fabrications strictly prohibited (插曲必须以歌曲名检索完整官方歌词，并在转录时全文对照官方歌词，严禁脑补臆造空耳). After all regular searches are completed, you MUST execute at least one additional `search_web` query IN CHINESE specifically for the relevant entity names, person names, and key terms (covering BOTH familiar key figures and unfamiliar terms—never skip key entities relying on memory) to retrieve their exact official Chinese characters. Do NOT include your own unverified or remembered Chinese translations of those names in the search query. Chinese proper-noun searches MUST be your final search step—once Stage 2 begins, do NOT call `search_web` at any time (use `\"flag\"` instead).\n" +
              "     * Canonical Chinese Glyphs: Foreign search results lack standard Chinese characters. Always anchor the exact standard Chinese glyphs for all names and entities from that Chinese search result, preventing homophone errors (同音异形字) and maintaining 100% consistency across all segments.";

        // Divide into 12-minute segments to avoid token limit cutoffs
        var segments = new List<(TimeSpan Start, TimeSpan End)>();
        var chunk = TimeSpan.FromMinutes(12);

        if (totalDuration <= TimeSpan.Zero || totalDuration <= chunk)
        {
            segments.Add((TimeSpan.Zero, totalDuration > TimeSpan.Zero ? totalDuration : TimeSpan.FromHours(4)));
        }
        else
        {
            TimeSpan cur = TimeSpan.Zero;
            while (cur < totalDuration)
            {
                TimeSpan next = cur + chunk;
                if (next > totalDuration || (totalDuration - next < TimeSpan.FromMinutes(2)))
                {
                    next = totalDuration;
                }
                segments.Add((cur, next));
                cur = next;
            }
        }

        string seg0EndStr = TimeHelper.ToSrtTime(segments[0].End);
        string seg0Desc = segments.Count > 1
            ? $"Segment 1/{segments.Count}: {TimeHelper.ToSrtTime(segments[0].Start)} to {seg0EndStr}"
            : "Full Audio";

        string seg0BoundaryRule = segments.Count > 1
            ? $@"   - SEGMENT BOUNDARY RULE (SOFT BOUNDARY COMPLETION):
     * Transcribe up to approximately {seg0EndStr}. If {seg0EndStr} falls in the middle of an ongoing spoken sentence, you MUST finish transcribing that entire sentence completely (allowing the end timestamp to extend slightly past {seg0EndStr}, e.g. by 1–3 seconds) before ending this JSON array. NEVER cut a sentence in half at the segment boundary.
"
            : string.Empty;

        string fullStage2Rules =
$@"   - CORE ACOUSTIC PRINCIPLES:
     * Verbatim Syllable Fidelity: Speech phonemes and syllables MUST match the acoustic audio completely and verbatim. Never guess words, substitute unheard syllables, or normalize colloquial speech into formal text.
     * No Semantic or Acoustic Fabrication: Transcribe strictly what is physically spoken. Never invent words or phrases merely because they seem plausible for the topic or context. Never alter heard syllables to conform to anticipated idioms, cliches, or assumed colloquial habits.
     * Grammatical & Morphological Precision (in 'source_text' ONLY): Strictly preserve the speaker's exact spoken grammatical endings, verb conjugations, and tenses in 'source_text'. Do not append or elongate trailing syllables or modal particles that were not spoken.
     * Masked, Interrupted & Faint Speech: Never discard short remarks, interjections, affirmations, or background vocals/lyrics occurring between or masked by noise, background music, or louder foreground dialogue. Do NOT miss any faint or low-volume speech.
     * Repeated Phrases (Creative Drama & Fiction ONLY): For creative drama, anime, or films ONLY: when identical words, cries, or exclamations are repeated 4 or more times in rapid succession within a short acoustic interval (without extended pauses), merge from the start of the first to the end of the last into a single entry, displaying ONLY 2 repetitions followed by an ellipsis ('……'), while anchoring 'start' to the first repetition and 'end' to the final repetition.
     * Completely Unintelligible Speech & Lyrics: If an audio segment (dialogue or song vocals) is completely unintelligible or phonemes cannot be acoustically anchored due to heavy noise, music masking, or distortion, you are strictly forbidden from fabricating imaginary text. You are permitted to refrain from transcribing speculative words, but you MUST output the time span with a flag requesting full-segment re-verification (e.g., flag: ""unintelligible / full segment re-verification required"").
     * Rapid Turn-Taking & Simultaneous Multi-Voice Overlaps:
       - When speakers talk in rapid succession or interrupt, do NOT merge or assimilate the first speaker's trailing words with the second speaker's speech. Keep them as separate entries.
       - When two voices occur simultaneously (two speakers talking at the same time, or foreground dialogue overlapping with background song lyrics), you MUST output BOTH as COMPLETE, un-truncated subtitle entries with their true overlapping acoustic timestamps. NEVER truncate one speaker's sentence or split the time interval half-and-half to avoid timestamp overlap.
       - Primary Subtitle Identification on Overlaps: Whenever two entries overlap in time, explicitly indicate which entry is the main subtitle using `""is_primary"": true` (for the main foreground dialogue that stays on the bottom track) and `""is_primary"": false` (for the secondary interjection or background song lyric that goes to the top track).
   {segInstruction}
   - TIMESTAMPS & ACOUSTIC BOUNDARY SNAPPING:
     * Format: Set 'start' and 'end' timestamps strictly to 'HH:MM:SS.mmm'.
     * Physical Acoustic Onset & Decay Snapping (发声起点与衰减截止点锚定):
       - The 'start' timestamp MUST anchor exactly where the first syllable naturally begins after silence/pause (jump across gaps; NEVER inertially attach to the previous sentence's 'end'). Exclude pre-speech silence, breaths, and intro music.
       - The 'end' timestamp MUST anchor exactly where the final syllable acoustic decay naturally finishes before silence/pause begins (do not drag into background music or audio gaps).
     * Universal Split Re-Alignment (断句拆行独立声学重对齐):
       - Whenever a continuous sentence is divided into multiple subtitle entries (for character limits, clauses, or pauses), NEVER divide duration linearly or proportionally.
       - Every individual split entry MUST independently re-snap its start and end to the physical speech boundaries of that specific phrase. Audible pauses or breath gaps between split phrases MUST remain as empty silence between timestamps (never hang subtitles across pauses).
     * Compact Thinking Timeline & Anti-Drift Alignment:
       - During `<thinking>`, alongside your analysis, briefly draft a one-line timeline (`MM:SS.s-MM:SS.s verbatim_clause [FLAG if any]`) so timestamps and flags are anchored before JSON output.
       - Be careful not to confuse or mix up the start and end timestamps between different clauses or adjacent utterances.
   - MANDATORY FLAGGING RULES (High-sensitivity quality screen: over-flagging is ALWAYS preferred over missing an error, and outputting 0 flags across a multi-minute dialogue segment is an audit failure. Add ""flag"" field whenever any of these conditions occurs - DO NOT GUESS OR FABRICATE WITHOUT FLAGGING):
     1) Unsearched / Unverified Chinese Proper Nouns (未检索中文译名的人名/专有名词必打标):
        You MUST add a flag to ANY person name, character name, or proper noun in 'target_text' whose exact Chinese characters were NOT explicitly retrieved via a Chinese search query in Stage 1 and listed in `GLOSSARY` (even if the name feels familiar from internal memory), or whenever you had to transliterate/improvise (e.g., flag: ""unsearched Chinese proper noun: XXX"").
     2) Non-Standard / Uncertain Translation & Paraphrased Complex Syntax (翻译规范度/长难句意译存疑必打标):
        If the Chinese translation feels awkward, ambiguous, potentially divergent from video context, contains native Chinese homophone/slang ambiguity, OR whenever a line contains dense specialized domain concepts or intricate argumentative syntax where you had to paraphrase or smooth out logic rather than translate literally syllable-by-syllable, ADD A FLAG (e.g., flag: ""dense syntax / paraphrased translation: YYY"").
     3) Phonetic Discrepancy & Multi-Candidate Hesitation (听感犹豫/多备选发音必打标):
        Transcribed words do not fully or unambiguously match the heard acoustic syllables/phonemes. Whenever you hesitate, re-listen, or consider 2 or more possible hearings/interpretations during your thinking process, you MUST add a flag (even if you eventually settled on one).
     4) Acoustic Masking / Singing: Speech or song lyrics are muffled, rapid, whispered, or heavily masked by music, noise, sound effects, or distortion. If you are uncertain about sung lyrics, YOU MUST FLAG THEM.
     5) Contextual Incoherence: The heard phrase contradicts surrounding conversation logic or scene grammar.
     6) Confirmed or Suspected Dual-Voice / Dialogue-Lyric Overlap (确定或怀疑存在双语音重叠必打标):
        Whenever you detect OR even suspect that two voices (two simultaneous speakers, or speech overlapping with background vocals/lyrics) are occurring at the same time, you MUST add a flag (e.g., flag: ""dual-voice overlap: verify both complete utterances and primary track"") for Stage 3 verification.
     7) Suspected Timestamp Drift or Pause-Boundary Anomaly (时间轴错位/停顿切分异常存疑必打标):
        Whenever timestamps risk drifting or misaligning—such as multi-clause sentences spanning an internal pause, rapid consecutive cries/interjections, or uncertain start/end boundaries—you MUST add a flag (e.g., flag: ""timestamp/boundary check"").
     8) Completely Unintelligible Speech or Lyrics (完全无法辨识语音或歌词必打标):
        For speech or sung lyrics completely unintelligible or un-anchor-able due to noise/music masking, refrain from guessing and output with a flag requesting full segment re-verification (e.g., flag: ""unintelligible / full segment re-verification required"").
   {styleInstruction}
   - SOURCE & TARGET TEXT SPECIFICATIONS:
     * In 'source_text', output verbatim transcription in the original spoken language matching heard syllables, native orthography, and standard numeral conventions of that language (never arbitrarily mix numeral systems), aligned strictly to the exact clause boundaries of 'target_text' without artificial continuation ellipses.
     * In 'target_text', provide professional, localized Chinese subtitles:
       - Conciseness & Tone Fidelity: Convey the most accurate meaning and tone using the fewest words possible. If the original audio is already in Chinese, output polished, standardized Chinese subtitles.
       - Natural Chinese Expression & Particle Localization: 'target_text' must read as natural, idiomatic Chinese. Never leave untranslated foreign modal particles, honorific suffixes, or phonetic filler syllables in 'target_text'; convert their nuance into natural Chinese expressions or omit them cleanly. Widely accepted international terms and standard technical acronyms (e.g., AI, CPU, DNA) may be retained in their conventional form.
       - Formatting Conventions: Never mix Arabic numerals and Chinese number characters within the same number or weekday (e.g., write '五千二百' and '星期二', NEVER '五千2百' or '星期2'). Enclose titles of works (books, films, songs, shows, games) in '《》', and use '·' as the separator in transliterated names (e.g., '约翰·史密斯').";

        string simplifiedStage2Rules =
$@"Follow all acoustic and transcription principles:
1. Verbatim syllable fidelity & Complete Overlaps: No semantic or acoustic fabrication; in 'source_text', preserve exact grammatical endings and spoken phonemes. Never discard remarks or background song lyrics masked by noise, music, or louder foreground speech. Do NOT miss any faint/quiet voices or speech. For creative drama/anime ONLY: identical words repeated 4+ times in rapid succession within a short interval may merge into 2 repetitions with an ellipsis ('……') from the first start to the last end. Never fabricate imaginary text for completely unintelligible speech or lyrics masked by loud music/noise—refrain from guessing and add a flag requesting full segment re-verification. Whenever two voices occur simultaneously (dual speakers or dialogue + background song lyrics), you MUST output BOTH as COMPLETE, un-truncated subtitle entries with their true overlapping timestamps (never truncate either voice or slice time half-and-half), and set `""is_primary"": true` on the main dialogue entry and `""is_primary"": false` on the secondary/lyric entry.
2. Acoustic Snapping: Anchor 'start' strictly where the first syllable begins (NEVER inertially attach 'start' near the previous sentence's 'end'); anchor 'end' strictly where the final syllable finishes. During `<thinking>`, alongside your analysis, briefly note a compact one-line timeline (`MM:SS.s-MM:SS.s clause [FLAG]`), and be careful not to confuse the start and end timestamps between different clauses or adjacent lines.
3. Universal Split Re-alignment: For ALL split subtitle entries (whether split due to character limits, clauses, pauses, or pacing), independently anchor each split line to the physical acoustic speech boundaries of that specific phrase (never divide time linearly).
4. Clean boundaries: NO artificial leading/trailing ellipses ('...' or '……').
5. Dual-Track Length Limit: 'target_text' MUST NOT exceed {maxChars} Chinese characters, and 'source_text' MUST NOT exceed {maxSourceCjkChars} CJK characters (or {maxSourceLatinChars} alphabetic characters). Split at a natural grammatical pause whenever either line exceeds its limit.
{styleInstruction}
6. Source & Target text: In 'source_text', follow the native orthography and standard numeral conventions of the spoken language, aligned strictly to each split clause. In 'target_text', convey the most accurate tone using the fewest words in idiomatic Chinese (convert foreign modal particles into natural Chinese expressions or omit them cleanly; standard technical acronyms may remain). Enclose work titles in '《》' and use '·' for transliterated name separators.
7. Mandatory Flagging (High-Sensitivity — 0 flags is an audit failure): Use the ""flag"" field to flag ANY proper noun not in `GLOSSARY`, ANY phrase where you hesitated or considered 2+ possible hearings in your thinking, ANY paraphrased dense/formal sentence, ANY confirmed or suspected simultaneous dual-voice/lyric overlap, ANY suspected timestamp drift or pause-boundary anomaly (e.g., multi-clause pauses or rapid consecutive cries), ANY completely unintelligible speech or lyrics, as well as uncertain translations, homophone doubts, acoustic masking, song lyrics, or contextual incoherence.";

        string? selectedGlossaryCsv = SettingsService.Instance.CurrentSettings.SelectedGlossaryCsv;
        bool isGlossaryEnabled = !string.IsNullOrWhiteSpace(selectedGlossaryCsv);
        string userGlossaryInstruction = string.Empty;
        var discoveredGlossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (isGlossaryEnabled)
        {
            var userEntries = GlossaryService.LoadGlossary(selectedGlossaryCsv);
            string userGlossaryLine = GlossaryService.FormatUserGlossaryLine(userEntries);
            if (!string.IsNullOrWhiteSpace(userGlossaryLine))
            {
                userGlossaryInstruction =
$@"
   - USER GLOSSARY (PRE-VERIFIED & LOCKED — HIGHEST PRIORITY):
     * Strictly apply the following locked translations throughout all stages. Treat them as pre-verified in `GLOSSARY` (do NOT waste `search_web` calls re-searching them or flag them as unsearched), and append any newly discovered proper nouns from this audio to `GLOSSARY`:
       {userGlossaryLine}";
                AppLogService.Instance.LogInfo($"[术语表] 已从 {selectedGlossaryCsv} 加载 {userEntries.Count} 条跨视频锁定术语。");
            }
            else
            {
                AppLogService.Instance.LogInfo($"[术语表] 已启用 {selectedGlossaryCsv}（当前含 0 条有效术语，任务完成后将自动写入新专名）。");
            }
        }

        string prompt =
$@"You are an intelligent, context-aware video subtitle transcription and translation expert dedicated to producing professional Chinese subtitles.
Your task is to transcribe and translate all dialogue and speech in the audio file into professional subtitles.
You MUST use the view_file tool to inspect and listen to the audio file:
Audio File Path: {fullAudioPath}

WORKFLOW & REQUIREMENTS:

1. TOOL USAGE & SANDBOX:
   - Call view_file with AbsolutePath=""{fullAudioPath}"" to listen to the audio.
   - STRICT: You are in an isolated sandbox. Do NOT execute terminal commands or scripts, and do NOT access files outside this directory.

2. STAGE 1 - OVERALL BACKGROUND & ENTITY GROUNDING:
   - Perceive the macro scenario across the audio: subject domain (e.g. creative drama, anime, film/cinema, TV series, technology, academic lecture, documentary, news/talk show, business), primary speakers/participants, speaking styles, and core specialized entities (proper nouns, personal/entity names, organizations, technical terms, domain nomenclature). Establishing domain vocabulary early anchors unfamiliar phonetic patterns.
   - OFFICIAL CHINESE TRANSLATED & CANONICAL NAMES RETRIEVAL (MANDATORY FOR KNOWN IPs & SPECIALIZED DOMAINS):
{entityRetrievalInstruction}
   - NATIVE CHINESE AUDIO RECOGNITION:
     * If the original audio speech is already in Chinese (Mandarin, regional dialects, Cantonese, etc.), identify the thematic topic, industry terminology, idioms, and official proper nouns in Chinese upfront to avoid homophone confusion (同音字/异形字偏差).
   {searchInstruction}{userGlossaryInstruction}

3. STAGE 2 - LINEAR TRANSCRIPTION & SEGMENTATION ({seg0Desc}):
{seg0BoundaryRule}{fullStage2Rules}

4. STRICT OUTPUT FORMAT:
   - On the first line before the JSON array, output a single glossary line of verified proper nouns (format: `GLOSSARY: source1=中文1 | source2=中文2`, no square brackets), followed immediately by ONLY the valid JSON array of objects. No markdown explanations, no conversational commentary.
   - Do NOT artificially restrict total entries; generate naturally based on audio speech density.

JSON OUTPUT SCHEMA:
[
  {{
    ""index"": 1,
    ""source_text"": ""..."",
    ""target_text"": ""..."",
    ""start"": ""00:00:01.200"",
    ""end"": ""00:00:04.200""
  }},
  {{
    ""index"": 2,
    ""source_text"": ""..."",
    ""target_text"": ""..."",
    ""start"": ""00:00:06.850"",
    ""end"": ""00:00:09.400"",
    ""flag"": ""short note on why flagged""
  }}
]";

        var allSubtitles = new List<SubtitleItem>();
        double currentReportedPct = 10.0;
        void ReportMonotonicProgress(double pct)
        {
            if (pct > currentReportedPct)
            {
                currentReportedPct = pct;
            }
            progress?.Report(currentReportedPct);
        }

        // Gentle S-damped curve: pow(t/T, 1.35) / (1 + pow(t/T, 1.35))
        // Avoids front-loaded speed in the first half (only ~16% at 0.3*T, exactly 50% at T) and smoothly approaches maxPct.
        static double ComputeSmoothStageProgress(double startPct, double maxPct, double elapsedSeconds, double halfTimeSeconds)
        {
            if (elapsedSeconds <= 0 || maxPct <= startPct) return startPct;
            double x = Math.Pow(elapsedSeconds / Math.Max(1.0, halfTimeSeconds), 1.35);
            double ratio = x / (1.0 + x);
            return startPct + (maxPct - startPct) * ratio;
        }

        ReportMonotonicProgress(10.0);

        var aiSw = Stopwatch.StartNew();
        bool hasStartedGenerating = false;
        var timeRegex = new Regex(@"""(?:end|start)""\s*:\s*""(\d{1,2}:\d{2}:\d{2}[\.,]\d{3})""", RegexOptions.Compiled);
        TimeSpan latestDiscoveredTime = TimeSpan.Zero;

        double seg1MaxPct = 35.0 + (30.0 / Math.Max(1, segments.Count)) - 0.4;
        const double stage1DurationEstimate = 110.0; // ~110s for Stage 1 (10% -> 35%), then seamlessly transitions into Segment 1 (35% -> seg1MaxPct)

        // Track active segment ticker CTS and current segment number so handleStreamLine cancels the active ticker immediately upon streaming
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? activeTurnTickerCts = tickerCts;
        int currentTurnNumber = 1;

        var statusTicker = Task.Run(async () =>
        {
            while (!tickerCts.Token.IsCancellationRequested && !hasStartedGenerating)
            {
                var elapsed = aiSw.Elapsed;
                double seconds = elapsed.TotalSeconds;

                if (seconds <= stage1DurationEstimate)
                {
                    // Stage 1 (10% -> 35%): gentle S-curve reaching 35.0% across stage1DurationEstimate
                    double tRatio = seconds / stage1DurationEstimate;
                    // Smooth ease-in-out cubic blend from 0 to 1 over [0, stage1DurationEstimate]
                    double eased = tRatio * tRatio * (3.0 - 2.0 * tRatio);
                    double creepPct = 10.0 + 25.0 * eased;
                    ReportMonotonicProgress(creepPct);

                    string statusText = seconds switch
                    {
                        < 8.0 => $"AI 正在读取并感知全片多模态音频流 (已耗时 {elapsed:mm\\:ss})...",
                        _ => $"AI 正在全局感知背景语境与说话人逻辑 (已思考 {elapsed:mm\\:ss})..."
                    };
                    onStatusUpdate?.Invoke(statusText);
                }
                else
                {
                    // Seamless transition into Stage 2 Segment 1 transcription (35% -> seg1MaxPct)
                    double seg1Sec = seconds - stage1DurationEstimate;
                    double creepPct = ComputeSmoothStageProgress(35.0, seg1MaxPct, seg1Sec, halfTimeSeconds: 135.0);
                    ReportMonotonicProgress(creepPct);
                    onStatusUpdate?.Invoke($"AI 正在转录第 1/{segments.Count} 分段 ({TimeHelper.FormatDuration(segments[0].Start)} - {TimeHelper.FormatDuration(segments[0].End)}，已耗时 {elapsed:mm\\:ss})...");
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

        Action<string> handleStreamLine = line =>
        {
            if (line.Contains("search_web", StringComparison.OrdinalIgnoreCase) || line.Contains("Searching web", StringComparison.OrdinalIgnoreCase))
            {
                onStatusUpdate?.Invoke($"AI 正在检索验证背景知识与领域术语 (已耗时 {aiSw.Elapsed:mm\\:ss})...");
            }

            var match = timeRegex.Match(line);
            if (match.Success)
            {
                if (!hasStartedGenerating)
                {
                    hasStartedGenerating = true;
                    ReportMonotonicProgress(35.0);
                }

                // Always cancel the currently active segment ticker so it never fights with streamed timestamp updates
                try { activeTurnTickerCts?.Cancel(); } catch { }

                var ts = TimeHelper.ParseTime(match.Groups[1].Value);
                if (ts > latestDiscoveredTime)
                {
                    latestDiscoveredTime = ts;
                    if (totalDuration > TimeSpan.Zero)
                    {
                        double ratio = Math.Clamp(ts.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
                        double pipelinePct = 35.0 + (ratio * 30.0); // 35.0% to 65.0%
                        ReportMonotonicProgress(pipelinePct);
                        string prefix = segments.Count > 1
                            ? $"AI 正在转录第 {currentTurnNumber}/{segments.Count} 分段"
                            : "AI 转录与多语言翻译中...";
                        onStatusUpdate?.Invoke($"{prefix} ({TimeHelper.FormatDuration(ts)} / {TimeHelper.FormatDuration(totalDuration)})");
                    }
                }
            }
        };

        // Turn 1: Ingest complete audio in unified context and transcribe Segment 1
        string firstOutput = "";
        try
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                firstOutput = await RunCliTurnAsync(
                    resolvedCli, 
                    audioDir, 
                    modelName, 
                    prompt, 
                    isContinuation: false, 
                    handleStreamLine,
                    cancellationToken);

                if (firstOutput.Contains("blocked by Gemini's filters", StringComparison.OrdinalIgnoreCase))
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(1200, cancellationToken);
                        continue;
                    }
                }
                break;
            }
        }
        finally
        {
            try { tickerCts.Cancel(); } catch { }
        }

        if (isGlossaryEnabled)
        {
            GlossaryService.ExtractGlossaryFromOutput(firstOutput, discoveredGlossary);
        }

        var firstBatch = ParseSubtitleResponse(firstOutput);
        allSubtitles.AddRange(firstBatch);

        TimeSpan maxSeen = allSubtitles.Count > 0 ? allSubtitles.Max(s => s.EndTime) : TimeSpan.Zero;
        double seg1DonePct = 35.0 + (30.0 / Math.Max(1, segments.Count));
        if (totalDuration > TimeSpan.Zero && maxSeen > latestDiscoveredTime)
        {
            latestDiscoveredTime = maxSeen;
            double ratio = Math.Clamp(maxSeen.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
            ReportMonotonicProgress(Math.Max(seg1DonePct, 35.0 + (ratio * 30.0))); // 35% to 65%
        }
        else
        {
            ReportMonotonicProgress(seg1DonePct);
        }

        AppLogService.Instance.LogInfo($"[AI转录] 分段 1/{segments.Count} 解析完成: 生成 {firstBatch.Count} 条字幕，最大时间戳: {TimeHelper.FormatDuration(maxSeen)} (总时长: {TimeHelper.FormatDuration(totalDuration)})");

        // Process remaining 12-minute segments in the SAME session (35% -> 65%)
        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            cancellationToken.ThrowIfCancellationRequested();

            int turnNumber = i + 1;
            currentTurnNumber = turnNumber;
            bool useFullRules = (turnNumber - 1) % 4 == 0; // Turn 1, 5, 9, 13... use Full Rules; others use Simplified Rules
            string activeStage2Rules = useFullRules ? fullStage2Rules : simplifiedStage2Rules;
            string ruleModeDesc = useFullRules ? "完整规则周期刷新" : "精简规则";

            double segStartPct = 35.0 + (((double)i / segments.Count) * 30.0);
            double segEndPct = 35.0 + (((double)turnNumber / segments.Count) * 30.0);
            ReportMonotonicProgress(segStartPct);

            AppLogService.Instance.LogInfo($"[AI转录] 启动第 {turnNumber}/{segments.Count} 分段转录 ({TimeHelper.FormatDuration(seg.Start)} - {TimeHelper.FormatDuration(seg.End)}) [{ruleModeDesc}]...");

            var segSw = Stopwatch.StartNew();
            using var segTickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeTurnTickerCts = segTickerCts;
            bool segDone = false;
            var segTicker = Task.Run(async () =>
            {
                while (!segTickerCts.Token.IsCancellationRequested && !segDone)
                {
                    var elapsed = segSw.Elapsed;
                    double creepPct = ComputeSmoothStageProgress(segStartPct, segEndPct - 0.4, elapsed.TotalSeconds, halfTimeSeconds: 135.0);
                    ReportMonotonicProgress(creepPct);
                    if (!segTickerCts.Token.IsCancellationRequested)
                    {
                        onStatusUpdate?.Invoke($"AI 正在转录第 {turnNumber}/{segments.Count} 分段 ({TimeHelper.FormatDuration(seg.Start)} - {TimeHelper.FormatDuration(seg.End)}，已耗时 {elapsed:mm\\:ss})...");
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

            TimeSpan actualStart = maxSeen > TimeSpan.Zero ? maxSeen : seg.Start;
            string actualStartStr = TimeHelper.ToSrtTime(actualStart);
            string segEndStr = TimeHelper.ToSrtTime(seg.End);
            var lastItem = allSubtitles.LastOrDefault();
            string lastLinePreview = lastItem != null
                ? (!string.IsNullOrWhiteSpace(lastItem.TargetText) ? lastItem.TargetText : lastItem.SourceText)
                : string.Empty;

            string boundaryAnchorLine = !string.IsNullOrWhiteSpace(lastLinePreview)
                ? $@"The previous segment finished cleanly at {actualStartStr} with the final subtitle line: ""{lastLinePreview}"".
Please start from the very first new spoken utterance AFTER {actualStartStr} (""{lastLinePreview}"") and transcribe/translate all dialogue and sung lyrics up to approximately {segEndStr} (if {segEndStr} falls mid-sentence, finish that complete sentence before stopping)."
                : $@"Please transcribe and translate all dialogue and sung lyrics within this time window ({actualStartStr} to {segEndStr}; if {segEndStr} falls mid-sentence, finish that complete sentence before stopping).";

            string continuePrompt =
$@"Continuing in this SAME conversation and audio context:
STAGE 2 - NEXT SEGMENT ({turnNumber}/{segments.Count}): {actualStartStr} to {segEndStr}.
{boundaryAnchorLine}
{activeStage2Rules}
Do NOT call `search_web` at any time in Stage 2 (use `""flag""` instead). Maintain full context, entity consistency, and the same JSON format.
On the first line before the JSON array, output the single glossary line of verified proper nouns (`GLOSSARY: source1=中文1 | source2=中文2`, no square brackets), followed immediately by ONLY the subtitle entries for this segment as a valid JSON array.";

            string segOutput;
            try
            {
                segOutput = await RunCliTurnAsync(
                    resolvedCli,
                    audioDir,
                    modelName,
                    continuePrompt,
                    isContinuation: true,
                    handleStreamLine,
                    cancellationToken);
            }
            finally
            {
                segDone = true;
                try { segTickerCts.Cancel(); } catch { }
            }

            try
            {
                if (isGlossaryEnabled)
                {
                    GlossaryService.ExtractGlossaryFromOutput(segOutput, discoveredGlossary);
                }
                var batch = ParseSubtitleResponse(segOutput);
                int added = 0;
                foreach (var item in batch)
                {
                    if (allSubtitles.Count > 0 && item.EndTime <= maxSeen + TimeSpan.FromMilliseconds(250))
                    {
                        continue;
                    }
                    item.Index = allSubtitles.Count + 1;
                    allSubtitles.Add(item);
                    added++;
                }

                TimeSpan newMax = allSubtitles.Count > 0 ? allSubtitles.Max(s => s.EndTime) : maxSeen;
                AppLogService.Instance.LogInfo($"[AI转录] 分段 {turnNumber}/{segments.Count} 完成: 新增 {added} 条字幕，最新时间戳: {TimeHelper.FormatDuration(newMax)}");
                maxSeen = newMax;

                ReportMonotonicProgress(segEndPct); // 35% to 65%
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning($"[AI转录] 分段 {turnNumber}/{segments.Count} 解析异常: {ex.Message}");
            }
        }

        // Fallback continuation loop if video still has un-transcribed dialogue (e.g. unknown duration)
        int pass = segments.Count;
        while (ShouldTriggerContinuation(totalDuration, maxSeen, allSubtitles.Count) && pass < 15)
        {
            pass++;
            cancellationToken.ThrowIfCancellationRequested();

            bool useFullRules = (pass - 1) % 4 == 0; // Turn 1, 5, 9, 13... use Full Rules; others use Simplified Rules
            string activeStage2Rules = useFullRules ? fullStage2Rules : simplifiedStage2Rules;
            string ruleModeDesc = useFullRules ? "完整规则周期刷新" : "精简规则";

            double remSec = (totalDuration - maxSeen).TotalSeconds;
            AppLogService.Instance.LogInfo($"[AI转录] 检测到长音频尚未结束 (剩余 {remSec:F1} 秒未覆盖)，启动接力续写 (轮次 {pass}/15，从 {TimeHelper.FormatDuration(maxSeen)} 继续) [{ruleModeDesc}]...");
            onStatusUpdate?.Invoke($"AI 正在进行长篇会话接力续写 (轮次 {pass}/15，当前进度: {TimeHelper.FormatDuration(maxSeen)} / {TimeHelper.FormatDuration(totalDuration)})...");

            var lastFallbackItem = allSubtitles.LastOrDefault();
            string lastFallbackPreview = lastFallbackItem != null
                ? (!string.IsNullOrWhiteSpace(lastFallbackItem.TargetText) ? lastFallbackItem.TargetText : lastFallbackItem.SourceText)
                : string.Empty;
            string maxSeenStr = TimeHelper.ToSrtTime(maxSeen);

            string continuePrompt =
$@"Continuing in this SAME conversation and audio context:
The previous pass finished at {maxSeenStr} with the final subtitle line: ""{lastFallbackPreview}"".
Please continue transcribing and translating the remaining dialogue and sung lyrics starting from the very first new spoken utterance AFTER {maxSeenStr} (""{lastFallbackPreview}"") to the end of the audio.
{activeStage2Rules}
Do NOT call `search_web` at any time in Stage 2 (use `""flag""` instead). Maintain full context, entity names, and terminology consistency.
On the first line before the JSON array, output the single glossary line of verified proper nouns (`GLOSSARY: source1=中文1 | source2=中文2`, no square brackets), followed immediately by ONLY the remaining subtitles as a valid JSON array of objects with fields: index, source_text, target_text, start, end (and optional flag).";

            string continueOutput = await RunCliTurnAsync(
                resolvedCli, 
                audioDir, 
                modelName, 
                continuePrompt, 
                isContinuation: true, 
                handleStreamLine, 
                cancellationToken);

            if (string.IsNullOrWhiteSpace(continueOutput))
            {
                AppLogService.Instance.LogInfo($"[AI转录] 轮次 {pass} 返回空内容，已到达台词终点，结束续写。");
                break;
            }

            try
            {
                if (isGlossaryEnabled)
                {
                    GlossaryService.ExtractGlossaryFromOutput(continueOutput, discoveredGlossary);
                }
                var nextBatch = ParseSubtitleResponse(continueOutput);
                if (nextBatch.Count == 0)
                {
                    AppLogService.Instance.LogInfo($"[AI转录] 轮次 {pass} 解析出 0 条字幕，已到达台词终点，结束续写。");
                    break;
                }

                int addedCount = 0;
                foreach (var item in nextBatch)
                {
                    if (item.StartTime >= maxSeen.Subtract(TimeSpan.FromSeconds(1)))
                    {
                        item.Index = allSubtitles.Count + 1;
                        allSubtitles.Add(item);
                        addedCount++;
                    }
                }

                TimeSpan newMax = allSubtitles.Max(s => s.EndTime);
                AppLogService.Instance.LogInfo($"[AI转录] 轮次 {pass} 完成: 新增 {addedCount} 条字幕，最新时间戳推进至 {TimeHelper.FormatDuration(newMax)}");

                if (newMax <= maxSeen)
                {
                    AppLogService.Instance.LogInfo($"[AI转录] 轮次 {pass} 未能推进新的时间戳，结束续写。");
                    break;
                }
                maxSeen = newMax;

                double ratio = Math.Clamp(maxSeen.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
                ReportMonotonicProgress(35.0 + (ratio * 30.0)); // 35% to 65%
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning($"[AI转录] 轮次 {pass} 解析出现异常: {ex.Message}，提前结束接力。");
                break;
            }
        }

        // STAGE 3: Targeted verification on flagged items (65% to 80% smooth transition)
        ReportMonotonicProgress(65.0);
        var directFlagged = allSubtitles.Where(s => !string.IsNullOrWhiteSpace(s.Flag)).ToList();
        var overlapAnchors = directFlagged
            .Where(s => s.IsPrimary == false ||
                        (s.Flag != null && (s.Flag.Contains("overlap", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("boundary", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("timing", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("pause", StringComparison.OrdinalIgnoreCase) ||
                                            s.Flag.Contains("drift", StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var flaggedItems = allSubtitles
            .Where(s => !string.IsNullOrWhiteSpace(s.Flag) ||
                        overlapAnchors.Any(a => Math.Abs((s.StartTime - a.StartTime).TotalSeconds) <= 15.0))
            .ToList();
        if (flaggedItems.Count > 0)
        {
            onStatusUpdate?.Invoke($"AI 正在进行靶向核对与微创修正 ({flaggedItems.Count} 处疑难标记)...");
            AppLogService.Instance.LogInfo($"[AI转录] 发现 {flaggedItems.Count} 条疑难标记，启动阶段三靶向核对与平滑推进 (65% -> 80%)...");

            // Smooth progress ticker from 65.0% towards 79.6% during Stage 3 using gentle S-curve
            var stage3Sw = Stopwatch.StartNew();
            using var stage3TickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool stage3Done = false;
            double stage3HalfTime = Math.Max(85.0, flaggedItems.Count * 2.0);
            var stage3Ticker = Task.Run(async () =>
            {
                while (!stage3TickerCts.Token.IsCancellationRequested && !stage3Done)
                {
                    var elapsed = stage3Sw.Elapsed;
                    double seconds = elapsed.TotalSeconds;
                    double creepPct = ComputeSmoothStageProgress(65.0, 79.6, seconds, halfTimeSeconds: stage3HalfTime);
                    ReportMonotonicProgress(creepPct);
                    onStatusUpdate?.Invoke($"AI 正在进行靶向核对与微创修正 ({flaggedItems.Count} 处疑难标记，已耗时 {elapsed:mm\\:ss})...");
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
                        "Perform targeted web search as needed to verify exact proper nouns, specialized domain terminology, or standard song lyrics/titles. You are strictly forbidden from putting unverified/fuzzy acoustic transcription fragments or guessed lyrics into search queries; you must instead search using known, verified keywords."
                };

                var stage3JsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };

                const int maxItemsPerStage3Batch = 65;
                int correctedCount = 0;
                var updatedMatches = new HashSet<SubtitleItem>();

                for (int batchOffset = 0; batchOffset < flaggedItems.Count; batchOffset += maxItemsPerStage3Batch)
                {
                    var batchItems = flaggedItems.Skip(batchOffset).Take(maxItemsPerStage3Batch).ToList();
                    string itemsJson = JsonSerializer.Serialize(batchItems.Select(f => new
                    {
                        index = f.Index,
                        source_text = f.SourceText,
                        target_text = f.TargetText,
                        start = f.Start,
                        end = f.End,
                        is_primary = f.IsPrimary,
                        flag = f.Flag
                    }), stage3JsonOptions);

                    string verifyPrompt =
$@"STAGE 3 - TARGETED VERIFICATION & REFINEMENT:
The following subtitle entries were flagged during transcription due to non-standard entity translations, translation/phrasing quality uncertainty, phonetic/syllable ambiguity, domain terminology, context conflict, simultaneous dual-voice overlap, timestamp/boundary uncertainty, or acoustic masking/song lyrics:
{itemsJson}

With full audio context now established across the entire recording, verify and refine each specific entry ({searchGuidance}):
1. Official Nomenclature, Song Lyrics & Translation Standardization:
   - For entries flagged with unsearched entity names, improvised transliterations, or uncertain phrasing, cross-reference with the official Chinese translation table or perform targeted web search (e.g., official databases, authoritative domain wikis, or official glossaries) to rectify them into authoritative, standard Chinese terms based on official sources.
   - For entries flagged with song lyrics or singing: audit each line against the complete official lyrics sheet (if gaps, fast rap passages, or acoustic ambiguities remain from Stage 1, search the web using the confirmed song title and artist to retrieve the missing lyrics). Rectify 'source_text' to authoritative official lyrics, and rectify 'target_text' into natural, expressive localized Chinese translations, strictly eliminating inverted meanings, literal machine translation, and phonetic hallucinations.
   - If the audio is natively Chinese, rectify any homophone errors, dialect misunderstandings, or non-standard terms into standard Chinese.
2. Acoustic Reality & Fabrication Audit:
   - Audit each entry against the true acoustic audio. Reject and overturn any lines where Stage 2 may have hallucinated or fabricated words to fit a conversational context. Rectify 'source_text' to authentic heard syllables and official terminology.
   - For entries flagged with dual-voice or dialogue-lyric overlap, carefully re-listen to that exact time window to ensure BOTH simultaneous voices are transcribed in full without missing clauses or artificial truncation, outputting both overlapping entries with accurate timestamps and `""is_primary""` (true for main dialogue, false for secondary/lyric). Also set `""is_primary"": false` on all content related to the secondary-marked parts so they stay at the top of the screen and avoid jumping up and down.
3. Acoustic Boundary & Split Re-Verification:
   - Verify that 'start' and 'end' timestamps strictly anchor to the first syllable onset (never inertially attached near the previous 'end' across pauses) and final syllable decay offset, especially for split phrases.
   - For entries flagged with timestamp/boundary uncertainty (or adjacent to them), inspect the surrounding ±15-second audio window to fix any confused start/end timestamps between clauses or shifted/omitted neighboring lines.
On the first line before the JSON array, output the updated single glossary line of all verified proper nouns (`GLOSSARY: source1=中文1 | source2=中文2`, no square brackets), followed immediately by ONLY a valid JSON array containing the verified/corrected entries with fields: index, source_text, target_text, start, end (and is_primary when overlapping or secondary).";

                    string verifyOutput = await RunCliTurnAsync(
                        resolvedCli,
                        audioDir,
                        modelName,
                        verifyPrompt,
                        isContinuation: true,
                        onLineReceived: null,
                        cancellationToken);

                    if (isGlossaryEnabled)
                    {
                        GlossaryService.ExtractGlossaryFromOutput(verifyOutput, discoveredGlossary);
                    }

                    var verifiedBatch = ParseSubtitleResponse(verifyOutput);
                    foreach (var vItem in verifiedBatch)
                    {
                        var match = allSubtitles.FirstOrDefault(x =>
                            !updatedMatches.Contains(x) &&
                            (x.Index == vItem.Index || Math.Abs((x.StartTime - vItem.StartTime).TotalSeconds) < 1.0));

                        if (match != null)
                        {
                            updatedMatches.Add(match);
                            match.SourceText = vItem.SourceText;
                            match.TargetText = vItem.TargetText;
                            match.StartTime = vItem.StartTime;
                            match.EndTime = vItem.EndTime;
                            match.Start = vItem.Start;
                            match.End = vItem.End;
                            if (vItem.IsPrimary.HasValue) match.IsPrimary = vItem.IsPrimary;
                            match.Flag = null;
                            correctedCount++;
                        }
                        else
                        {
                            vItem.Flag = null;
                            allSubtitles.Add(vItem);
                            correctedCount++;
                        }
                    }
                }

                stage3Done = true;
                try { stage3TickerCts.Cancel(); } catch { }
                AppLogService.Instance.LogInfo($"[AI转录] 阶段三靶向核对完成，成功修正/补充 {correctedCount} 条字幕。");
            }
            catch (Exception ex)
            {
                stage3Done = true;
                try { stage3TickerCts.Cancel(); } catch { }
                string warnMsg = $"[AI转录] 阶段三靶向核对因网络异常未完成: {ex.Message}。已保留阶段二转录成果 ({flaggedItems.Count} 处疑难标记未核验)。";
                AppLogService.Instance.LogWarning(warnMsg);
                onStatusUpdate?.Invoke($"⚠️ 阶段三核验受网络影响跳过，已保留原字幕 ({flaggedItems.Count} 处疑难未核验)");
            }
        }
        else
        {
            AppLogService.Instance.LogInfo("[AI转录] 无疑难标记，阶段三自动跳过。");
        }

        if (isGlossaryEnabled && discoveredGlossary.Count > 0)
        {
            int addedTerms = GlossaryService.AppendNewTermsToCsv(selectedGlossaryCsv, discoveredGlossary);
            if (addedTerms > 0)
            {
                AppLogService.Instance.LogInfo($"[术语表] 任务完成，已向 {selectedGlossaryCsv} 自动追加写入 {addedTerms} 条新专有名词。");
            }
        }

        ReportMonotonicProgress(80.0);
        return allSubtitles;
    }

    private static bool ShouldTriggerContinuation(TimeSpan totalDuration, TimeSpan maxSeen, int subtitleCount)
    {
        if (totalDuration <= TimeSpan.Zero || subtitleCount == 0) return false;

        double remainingSeconds = (totalDuration - maxSeen).TotalSeconds;
        if (remainingSeconds <= 0) return false;

        // For short media (<= 3.5 minutes):
        // In a single prompt, Gemini models process the entire audio stream.
        // If subtitles exist and reached either within 45s of the end or at least 50% through,
        // the remaining duration is outro music, silence, or credits.
        if (totalDuration <= TimeSpan.FromMinutes(3.5))
        {
            if (remainingSeconds <= 45.0 || maxSeen.TotalSeconds >= totalDuration.TotalSeconds * 0.5)
            {
                return false;
            }
        }

        // For medium/long media (> 3.5 minutes):
        // Natural speech endings often have 25-35 seconds of background music/outro.
        // Don't trigger if remaining time is <= 35s or <= 4% of total video duration.
        double outroBufferThreshold = Math.Max(35.0, totalDuration.TotalSeconds * 0.04);
        if (remainingSeconds <= outroBufferThreshold)
        {
            return false;
        }

        return true;
    }

    private async Task<string> RunCliTurnAsync(
        string resolvedCli,
        string? workingDir,
        string modelName,
        string prompt,
        bool isContinuation,
        Action<string>? onLineReceived,
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

        if (isContinuation)
        {
            argList.Add("--continue");
        }
        else
        {
            argList.Add("--model");
            argList.Add(string.IsNullOrWhiteSpace(modelName) ? "gemini-3.8-flash-high" : modelName);
        }

        string effectivePrompt = prompt;
        if (prompt.Length > 24000 && !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
        {
            string promptFile = Path.Combine(workingDir, $"prompt_{Guid.NewGuid():N}.txt").Replace("\\", "/");
            await File.WriteAllTextAsync(promptFile, prompt, Encoding.UTF8, cancellationToken);
            effectivePrompt = $"Read the full task instructions in {promptFile} using view_file, and strictly execute every instruction and output format requirement inside.";
        }

        argList.Add("--print");
        argList.Add(effectivePrompt);

        int maxAttempts = 4;
        string stdout = string.Empty;
        string stderr = string.Empty;
        int exitCode = -1;

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
            AppLogService.Instance.LogInfo($"[CLI] 启动命令行任务: isContinuation={isContinuation}, model={modelName}{(attempt > 1 ? $" (重试第 {attempt} 次)" : "")}");

            var env = new Dictionary<string, string>
            {
                ["CI"] = "1",
                ["TERM"] = "dumb",
                ["NO_COLOR"] = "1"
            };

            var execRes = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
            {
                FileName = resolvedCli,
                ArgumentList = argList,
                WorkingDirectory = workingDir,
                EnvironmentVariables = env,
                OnOutputLine = onLineReceived
            }, cancellationToken);

            sw.Stop();
            stdout = execRes.StandardOutput;
            stderr = execRes.StandardError;
            exitCode = execRes.ExitCode;

            AppLogService.Instance.LogInfo($"[CLI] 任务执行结束: 耗时 {sw.Elapsed.TotalSeconds:F2}秒, ExitCode={exitCode}, 输出字符数={stdout.Length}");

            // Diagnostic log
            try
            {
                string logPath = Path.Combine(FilePathHelper.GetLogsDirectory(), "cli_transcription.log");
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] isContinuation={isContinuation} (Attempt {attempt}/{maxAttempts}) Duration={sw.Elapsed.TotalSeconds:F2}s ExitCode={exitCode}\nStdout Length={stdout.Length}\nStderr={stderr}\n\n";
                File.AppendAllText(logPath, logEntry);
            }
            catch { }

            if (exitCode != 0)
            {
                bool isNetworkError = stderr.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                                   || stderr.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                                   || stderr.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
                                   || stderr.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                                   || stderr.Contains("Eligibility check failed", StringComparison.OrdinalIgnoreCase);

                if (attempt < maxAttempts && isNetworkError)
                {
                    AppLogService.Instance.LogWarning($"[CLI] 轮次执行遭遇偶发网络断连 (ExitCode={exitCode}, stderr: {stderr.Trim()})，准备接力重试...");
                    continue;
                }

                throw new InvalidOperationException($"CLI failed (Exit code {exitCode}):\n{stderr}\n{stdout}");
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                if (attempt < maxAttempts)
                {
                    AppLogService.Instance.LogWarning($"[CLI] 轮次返回空白输出，准备重试 ({attempt}/{maxAttempts})...");
                    continue;
                }
                string errDetail = !string.IsNullOrWhiteSpace(stderr) ? $"\nCLI Details: {stderr.Trim()}" : "";
                throw new InvalidOperationException($"CLI returned an empty response.{errDetail}");
            }

            return stdout;
        }

        return stdout;
    }

    private List<SubtitleItem> ParseSubtitleResponse(string rawResponse, TimeSpan timestampOffset = default)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            throw new InvalidOperationException("CLI returned an empty response.");
        }

        if (rawResponse.Contains("blocked by Gemini's filters", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("内容触发了 Gemini 官方安全过滤器误拦截（通常是由于动漫/影视剧中的争吵或对决台词触发了 AI 平台的敏感词策略）。程序已内置自动重试机制，请点击卡片右侧的【Retry】重试即可。");
        }

        List<SubtitleItem>? list = null;

        // Method 1: High-speed strict/sanitized JSON parsing
        try
        {
            int startIndex = rawResponse.IndexOf('[');
            if (startIndex >= 0)
            {
                string candidate = rawResponse.Substring(startIndex).Trim();
                int lastBracket = candidate.LastIndexOf(']');
                string jsonToParse;

                if (lastBracket > 0)
                {
                    jsonToParse = candidate.Substring(0, lastBracket + 1);
                }
                else
                {
                    // Truncated stream! Salvage all complete subtitle objects by finding the last closing brace '}'
                    int lastBrace = candidate.LastIndexOf('}');
                    if (lastBrace > 0)
                    {
                        jsonToParse = candidate.Substring(0, lastBrace + 1) + "\n]";
                    }
                    else
                    {
                        jsonToParse = candidate;
                    }
                }

                // Sanitize common LLM quirks (unquoted keys, etc.)
                jsonToParse = Regex.Replace(jsonToParse, @"(?<=[{,]\s*)(index|start|end|source_text|target_text|flag)\s*:", "\"$1\":", RegexOptions.IgnoreCase);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                list = JsonSerializer.Deserialize<List<SubtitleItem>>(jsonToParse, options);
            }
        }
        catch
        {
            // Fall back to Method 2 if strict JSON parsing encounters any LLM typos (e.g. missing commas, broken quotes)
        }

        // Method 2: Fault-tolerant block extractor (handles missing quotes, missing commas between lines, etc.)
        if (list == null || list.Count == 0)
        {
            list = ExtractSubtitlesViaRegex(rawResponse);
        }

        if (list == null)
        {
            list = new List<SubtitleItem>();
        }

        // If rawResponse explicitly contains empty JSON array `[]`, this segment was silence or background music
        if (list.Count == 0 && Regex.IsMatch(rawResponse, @"\[\s*\]"))
        {
            return list;
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException($"No subtitle entries found in CLI response.\nRaw:\n{rawResponse}");
        }

        // Fill StartTime and EndTime with timestampOffset
        foreach (var item in list)
        {
            var relativeStart = TimeHelper.ParseTimestamp(item.Start);
            var relativeEnd = TimeHelper.ParseTimestamp(item.End);

            if (relativeEnd <= relativeStart)
            {
                relativeEnd = relativeStart.Add(TimeSpan.FromSeconds(1.5));
            }

            item.StartTime = timestampOffset + relativeStart;
            item.EndTime = timestampOffset + relativeEnd;

            item.Start = TimeHelper.ToSrtTime(item.StartTime);
            item.End = TimeHelper.ToSrtTime(item.EndTime);
        }

        return list;
    }

    /// <summary>
    /// Bulletproof regex block parser: independently extracts each subtitle item even if an LLM omitted commas
    /// between properties or left keys unquoted in massive 2,000+ line JSON responses.
    /// </summary>
    private List<SubtitleItem> ExtractSubtitlesViaRegex(string text)
    {
        var result = new List<SubtitleItem>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var objPattern = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);
        var matches = objPattern.Matches(text);

        var startPattern = new Regex(@"[""']?start[""']?\s*:\s*[""']?(?<val>\d{1,2}:\d{2}(?::\d{2})?(?:[.,]\d+)?)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var endPattern = new Regex(@"[""']?end[""']?\s*:\s*[""']?(?<val>\d{1,2}:\d{2}(?::\d{2})?(?:[.,]\d+)?)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var srcPattern = new Regex(@"[""']?source_text[""']?\s*:\s*""(?<val>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var tgtPattern = new Regex(@"[""']?target_text[""']?\s*:\s*""(?<val>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var flagPattern = new Regex(@"[""']?flag[""']?\s*:\s*""(?<val>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var idxPattern = new Regex(@"[""']?index[""']?\s*:\s*(?<val>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var primaryPattern = new Regex(@"[""']?is_primary[""']?\s*:\s*(?<val>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        int autoIdx = 1;
        foreach (Match m in matches)
        {
            string block = m.Value;
            var startMatch = startPattern.Match(block);
            var endMatch = endPattern.Match(block);

            if (startMatch.Success && endMatch.Success)
            {
                var srcMatch = srcPattern.Match(block);
                var tgtMatch = tgtPattern.Match(block);
                var flagMatch = flagPattern.Match(block);
                var idxMatch = idxPattern.Match(block);
                var primaryMatch = primaryPattern.Match(block);

                int idx = autoIdx++;
                if (idxMatch.Success && int.TryParse(idxMatch.Groups["val"].Value, out int parsedIdx))
                {
                    idx = parsedIdx;
                }

                bool? isPrimary = null;
                if (primaryMatch.Success && bool.TryParse(primaryMatch.Groups["val"].Value, out bool parsedPrimary))
                {
                    isPrimary = parsedPrimary;
                }

                string src = srcMatch.Success ? srcMatch.Groups["val"].Value : "";
                string tgt = tgtMatch.Success ? tgtMatch.Groups["val"].Value : "";
                string? flagVal = flagMatch.Success ? flagMatch.Groups["val"].Value : null;

                try { src = Regex.Unescape(src); } catch { }
                try { tgt = Regex.Unescape(tgt); } catch { }
                if (!string.IsNullOrEmpty(flagVal)) { try { flagVal = Regex.Unescape(flagVal); } catch { } }

                result.Add(new SubtitleItem
                {
                    Index = idx,
                    Start = startMatch.Groups["val"].Value,
                    End = endMatch.Groups["val"].Value,
                    SourceText = src,
                    TargetText = tgt,
                    Flag = flagVal,
                    IsPrimary = isPrimary
                });
            }
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

        try
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string cliRoot = Path.Combine(userHome, ".gemini", "antigravity-cli");
            string mapFile = Path.Combine(cliRoot, "cache", "last_conversations.json");

            if (!File.Exists(mapFile)) return;

            string json = File.ReadAllText(mapFile);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (map == null || map.Count == 0) return;

            string normalizedTarget = Path.GetFullPath(taskSandboxDir).TrimEnd('\\', '/');
            string? matchedKey = null;
            string? convId = null;

            foreach (var kvp in map)
            {
                string candidatePath = kvp.Key.TrimEnd('\\', '/');
                if (string.Equals(candidatePath, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = kvp.Key;
                    convId = kvp.Value;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(convId)) return;

            // Delete ONLY the specific CLI conversation belonging to this taskSandboxDir
            string convDir = Path.Combine(cliRoot, "conversations");
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

            if (matchedKey != null && map.Remove(matchedKey))
            {
                try
                {
                    File.WriteAllText(mapFile, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
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
