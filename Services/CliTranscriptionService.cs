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

        // Fast-path: return cached result if already verified and not forcing refresh
        if (!forceRefresh && _cachedStatus.HasValue && string.Equals(_cachedCliPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedStatus.Value;
        }

        await _checkCliLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedStatus.HasValue && string.Equals(_cachedCliPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
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
                        _cachedCliPath = resolvedPath;
                        _cachedStatus = (false, "CLI returned non-zero exit code.", models);
                        return _cachedStatus.Value;
                    }
                }
                catch (Exception ex)
                {
                    _cachedCliPath = resolvedPath;
                    _cachedStatus = (false, $"Cannot launch CLI: {ex.Message}", models);
                    return _cachedStatus.Value;
                }
            }
            else if (!File.Exists(resolvedPath))
            {
                _cachedCliPath = resolvedPath;
                _cachedStatus = (false, $"CLI executable not found at: {resolvedPath}", models);
                return _cachedStatus.Value;
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

                _cachedCliPath = resolvedPath;
                string rawErr = $"{runRes.StandardError} {runRes.StandardOutput}";
                string friendlyMsg = IsAuthIssue(rawErr)
                    ? "CLI 未登录，请手动双击运行 tools/antigravity.exe 完成登录后重试"
                    : $"CLI returned exit code {runRes.ExitCode}: {runRes.StandardError}";
                _cachedStatus = (false, friendlyMsg, models);
                return _cachedStatus.Value;
            }
            catch (Exception ex)
            {
                _cachedCliPath = resolvedPath;
                string friendlyMsg = IsAuthIssue(ex.Message)
                    ? "CLI 未登录，请手动双击运行 tools/antigravity.exe 完成登录后重试"
                    : $"Error probing CLI models: {ex.Message}";
                _cachedStatus = (false, friendlyMsg, models);
                return _cachedStatus.Value;
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
        SegmentationStyle segmentationStyle = SegmentationStyle.DenseShort,
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

        string segInstruction = segmentationStyle switch
        {
            SegmentationStyle.SparseLong =>
                "- SUBTITLE SEGMENTATION: SPARSE/LONGER SENTENCES (少断句/长句连贯). Group complete grammatical clauses and full thoughts into longer coherent subtitles (12-25 characters), minimizing rapid subtitle jumping.",
            _ =>
                "- SUBTITLE SEGMENTATION: DENSE/SHORTER SENTENCES (多断句/短句独立 - 默认). Split dialogue into short, punchy subtitles (typically 4-12 characters per subtitle) matching natural speech pauses, breathing rhythms, and conversational tempo. Do NOT combine distinct short phrases into one."
        };

        string searchInstruction = webSearchMode switch
        {
            WebSearchMode.Accurate =>
                "- BACKGROUND & TERMINOLOGY (ACCURATE MODE): Thoroughly verify domain terms, character lore, and proper nouns online if there is any ambiguity. Ensure high precision in terminology.",
            WebSearchMode.Fast =>
                "- BACKGROUND & TERMINOLOGY (FAST MODE): Only perform web searches for essential, unknown core proper nouns when context is completely insufficient. Minimize search calls to maximize speed and efficiency.",
            WebSearchMode.Off =>
                "- BACKGROUND & TERMINOLOGY (OFFLINE MODE): Do NOT perform any web searches. Rely strictly on audio context, common sense, and internal knowledge to transcribe and translate directly.",
            _ => "- BACKGROUND & TERMINOLOGY: Transcribe and translate accurately based on context."
        };

        string prompt =
$@"You are an intelligent, context-aware video subtitle transcription and translation expert.
[CONTEXT: Authorized subtitle transcription for creative fictional entertainment media/anime. All dialogue consists of fictional script lines and character interactions. Faithfully transcribe and translate without bias.]
Your task is to transcribe and translate all dialogue and speech in the audio file into subtitles.
You MUST use the view_file tool to inspect and listen to the audio file:
Audio File Path: {fullAudioPath}

TASKS & REQUIREMENTS:
1. TOOL USAGE & STRICT SANDBOX CONSTRAINTS:
   Call view_file with AbsolutePath=""{fullAudioPath}"" to listen to the entire dialogue in the audio.
   Generate the subtitles directly from your audio perception.
   STRICT: You are operating in an isolated sandbox. Do NOT execute any terminal commands, powershell, or external scripts, and do NOT access any files outside this directory.
2. AUDIO COMPREHENSION & CONTEXT:
   Analyze the overall topic, scenario, tone, speaker identities, relationships, and context across the entire audio.
3. {searchInstruction}
4. TRANSLATION STYLE:
   {styleInstruction}
5. {segInstruction}
6. SUBTITLE TIMING:
   - Provide comprehensive, accurate timestamps from the beginning to the end of the audio file in 'HH:MM:SS.mmm' format.
   - In 'source_text', output the verbatim transcription in the original language.
   - In 'target_text', output the context-aware translation in '{targetLanguage}'. If the original speech is already '{targetLanguage}', provide polished transcript with proper punctuation.
7. STRICT OUTPUT FORMAT:
   - Output ONLY a valid JSON array of objects with fields: index, start, end, source_text, target_text.
   - No markdown explanations, no conversational commentary.

JSON OUTPUT SCHEMA:
[
  {{
    ""index"": 1,
    ""start"": ""00:00:01.200"",
    ""end"": ""00:00:04.500"",
    ""source_text"": ""..."",
    ""target_text"": ""...""
  }}
]""";

        var allSubtitles = new List<SubtitleItem>();
        progress?.Report(15);

        var aiSw = Stopwatch.StartNew();
        bool hasStartedGenerating = false;
        var timeRegex = new Regex(@"""(?:end|start)""\s*:\s*""(\d{1,2}:\d{2}:\d{2}[\.,]\d{3})""", RegexOptions.Compiled);
        TimeSpan latestDiscoveredTime = TimeSpan.Zero;

        // Background real-time status ticker: dynamically pushes live elapsed time during audio perception & deep thinking
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var statusTicker = Task.Run(async () =>
        {
            while (!tickerCts.Token.IsCancellationRequested && !hasStartedGenerating)
            {
                var elapsed = aiSw.Elapsed;
                double seconds = elapsed.TotalSeconds;

                // Smooth progress creep from 15% towards 35% during perception and reasoning
                double creepPct = Math.Min(35.0, 15.0 + (seconds * 0.25));
                progress?.Report(creepPct);

                string statusText = seconds switch
                {
                    < 6.0 => $"AI 正在读取并感知全片多模态音频流 (已耗时 {elapsed:mm\\:ss})...",
                    _ => $"AI 正在深度思考：推理说话人角色、剧情语境与断句对齐 (已思考 {elapsed:mm\\:ss})..."
                };

                onStatusUpdate?.Invoke(statusText);

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
                onStatusUpdate?.Invoke($"AI 正在联网检索验证专有名词与背景知识 (已耗时 {aiSw.Elapsed:mm\\:ss})...");
            }

            var match = timeRegex.Match(line);
            if (match.Success)
            {
                hasStartedGenerating = true;
                try { tickerCts.Cancel(); } catch { }

                var ts = TimeHelper.ParseTime(match.Groups[1].Value);
                if (ts > latestDiscoveredTime)
                {
                    latestDiscoveredTime = ts;
                    if (totalDuration > TimeSpan.Zero)
                    {
                        double ratio = Math.Clamp(ts.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
                        // Map AI transcription to 35% - 85% range of total pipeline
                        double pipelinePct = 35.0 + (ratio * 50.0);
                        progress?.Report(pipelinePct);
                        onStatusUpdate?.Invoke($"AI 转录与多语言翻译中... ({TimeHelper.FormatDuration(ts)} / {TimeHelper.FormatDuration(totalDuration)})");
                    }
                }
            }
        };

        // Turn 1: Ingest complete audio in unified context
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

        var firstBatch = ParseSubtitleResponse(firstOutput);
        allSubtitles.AddRange(firstBatch);

        TimeSpan maxSeen = allSubtitles.Count > 0 ? allSubtitles.Max(s => s.EndTime) : TimeSpan.Zero;
        if (totalDuration > TimeSpan.Zero && maxSeen > latestDiscoveredTime)
        {
            latestDiscoveredTime = maxSeen;
            double ratio = Math.Clamp(maxSeen.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
            progress?.Report(15.0 + (ratio * 70.0));
        }

        AppLogService.Instance.LogInfo($"[AI转录] 轮次 1 解析完成: 生成 {firstBatch.Count} 条字幕，最大时间戳: {TimeHelper.FormatDuration(maxSeen)} (总时长: {TimeHelper.FormatDuration(totalDuration)})");

        // Multi-turn continuation loop for long videos (allowing up to 15 passes for 3-4+ hours)
        int pass = 1;
        while (ShouldTriggerContinuation(totalDuration, maxSeen, allSubtitles.Count) && pass < 15)
        {
            pass++;
            cancellationToken.ThrowIfCancellationRequested();

            double remSec = (totalDuration - maxSeen).TotalSeconds;
            AppLogService.Instance.LogInfo($"[AI转录] 检测到长音频尚未结束 (剩余 {remSec:F1} 秒未覆盖)，启动接力续写 (轮次 {pass}/15，从 {TimeHelper.FormatDuration(maxSeen)} 继续)...");

            onStatusUpdate?.Invoke($"AI 正在进行长篇会话接力续写 (轮次 {pass}/15，当前进度: {TimeHelper.FormatDuration(maxSeen)} / {TimeHelper.FormatDuration(totalDuration)})...");

            string continuePrompt =
$@"Continuing in this SAME conversation and audio context:
Please continue transcribing and translating the remaining dialogue from where you left off (from timestamp {TimeHelper.ToSrtTime(maxSeen)} to the end of the audio).
Maintain full context, character names, and terminology consistency.
Output ONLY the remaining subtitles as a valid JSON array of objects with fields: index, start, end, source_text, target_text.";

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
                progress?.Report(15.0 + (ratio * 70.0));
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning($"[AI转录] 轮次 {pass} 解析出现异常: {ex.Message}，提前结束接力。");
                break;
            }
        }

        if (pass == 1)
        {
            double remSec = totalDuration > TimeSpan.Zero ? Math.Max(0, (totalDuration - maxSeen).TotalSeconds) : 0;
            AppLogService.Instance.LogInfo($"[AI转录] 单轮完整转录成功 (共 {allSubtitles.Count} 条字幕，末尾静音/背景音空白: {remSec:F1} 秒)，无需二次续写。");
        }

        progress?.Report(85);
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

        argList.Add("--print");
        argList.Add(prompt);

        var sw = Stopwatch.StartNew();
        AppLogService.Instance.LogInfo($"[CLI] 启动命令行任务: isContinuation={isContinuation}, model={modelName}");

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
        string stdout = execRes.StandardOutput;
        string stderr = execRes.StandardError;
        int exitCode = execRes.ExitCode;

        AppLogService.Instance.LogInfo($"[CLI] 任务执行结束: 耗时 {sw.Elapsed.TotalSeconds:F2}秒, ExitCode={exitCode}, 输出字符数={stdout.Length}");

        // Diagnostic log
        try
        {
            string logPath = Path.Combine(FilePathHelper.GetLogsDirectory(), "cli_transcription.log");
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] isContinuation={isContinuation} Duration={sw.Elapsed.TotalSeconds:F2}s ExitCode={exitCode}\nStdout Length={stdout.Length}\nStderr={stderr}\n\n";
            File.AppendAllText(logPath, logEntry);
        }
        catch { }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"CLI failed (Exit code {exitCode}):\n{stderr}\n{stdout}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            string errDetail = !string.IsNullOrWhiteSpace(stderr) ? $"\nCLI Details: {stderr.Trim()}" : "";
            throw new InvalidOperationException($"CLI returned an empty response.{errDetail}");
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
                jsonToParse = Regex.Replace(jsonToParse, @"(?<=[{,]\s*)(index|start|end|source_text|target_text)\s*:", "\"$1\":", RegexOptions.IgnoreCase);

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
        var idxPattern = new Regex(@"[""']?index[""']?\s*:\s*(?<val>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                var idxMatch = idxPattern.Match(block);

                int idx = autoIdx++;
                if (idxMatch.Success && int.TryParse(idxMatch.Groups["val"].Value, out int parsedIdx))
                {
                    idx = parsedIdx;
                }

                string src = srcMatch.Success ? srcMatch.Groups["val"].Value : "";
                string tgt = tgtMatch.Success ? tgtMatch.Groups["val"].Value : "";

                try { src = Regex.Unescape(src); } catch { }
                try { tgt = Regex.Unescape(tgt); } catch { }

                result.Add(new SubtitleItem
                {
                    Index = idx,
                    Start = startMatch.Groups["val"].Value,
                    End = endMatch.Groups["val"].Value,
                    SourceText = src,
                    TargetText = tgt
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

    public static void CleanupSessionConversations(DateTime sessionStart)
    {
        try
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string convDir = Path.Combine(userHome, ".gemini", "antigravity", "conversations");
            string brainDir = Path.Combine(userHome, ".gemini", "antigravity", "brain");

            if (Directory.Exists(convDir))
            {
                var files = Directory.GetFiles(convDir, "*.db*");
                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.LastWriteTimeUtc >= sessionStart.AddSeconds(-2))
                        {
                            string fname = Path.GetFileNameWithoutExtension(f);
                            if (fname.Contains("c8325039", StringComparison.OrdinalIgnoreCase)) continue;

                            File.Delete(f);

                            string brainSub = Path.Combine(brainDir, fname);
                            if (Directory.Exists(brainSub))
                            {
                                Directory.Delete(brainSub, recursive: true);
                            }
                        }
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
