using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleMaster.Helpers;
using SubtitleMaster.Models;
using SubtitleMaster.Services;

namespace SubtitleMaster.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0);
    private CancellationTokenSource? _queueCts;

    [ObservableProperty]
    private ObservableCollection<VideoTaskItem> _tasks = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTasks))]
    private int _taskCount;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _isCliReady;

    [ObservableProperty]
    private string _cliStatusText = "Checking CLI...";

    public bool HasTasks => Tasks.Count > 0;

    public LocalizationService Loc => LocalizationService.Instance;

    public static MainViewModel Instance { get; } = new();

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private Microsoft.UI.Xaml.DispatcherTimer? _elapsedTimer;

    public void RunOnUi(Action action)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    private Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }
            return tcs.Task;
        }

        return Task.FromResult(func());
    }

    public void NotifyQueueUpdated()
    {
        try { _wakeSignal.Release(); } catch { }
        _ = StartQueueProcessingAsync();
    }

    public MainViewModel()
    {
        Tasks.CollectionChanged += (s, e) =>
        {
            TaskCount = Tasks.Count;
        };

        _elapsedTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += (s, e) =>
        {
            foreach (var t in Tasks)
            {
                if (t.IsRunning)
                {
                    t.UpdateElapsed();
                }
            }
        };
        _elapsedTimer.Start();

        // Check CLI availability in background
        _ = CheckCliStatusAsync();
    }

    public async Task CheckCliStatusAsync(bool forceRefresh = false)
    {
        var settings = SettingsService.Instance.CurrentSettings;
        var (ready, msg, models) = await CliTranscriptionService.Instance.CheckCliStatusAsync(settings.CliPath, forceRefresh);
        IsCliReady = ready;
        CliStatusText = ready ? Loc.CliReadyText : $"{Loc.CliNotReadyText}: {msg}";
    }

    /// <summary>
    /// Core entry point: adds files or folder and starts execution IMMEDIATELY without any confirmation button.
    /// </summary>
    public void AddFilesAndStart(IEnumerable<string> paths, string? importedRootDir = null)
    {
        var videoPaths = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Scan folder recursively for video files
                try
                {
                    var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                                         .Where(FilePathHelper.IsVideoFile);
                    foreach (var f in files)
                    {
                        AddVideoTask(f, path);
                    }
                }
                catch { }
            }
            else if (File.Exists(path) && FilePathHelper.IsVideoFile(path))
            {
                AddVideoTask(path, importedRootDir);
            }
        }

        // Start processing immediately (or wake running scheduler for parallel slots)
        NotifyQueueUpdated();
    }

    private void AddVideoTask(string videoPath, string? importedRootDir)
    {
        // Avoid duplicate only if the same file is already waiting or actively running
        if (Tasks.Any(t => string.Equals(t.FilePath, videoPath, StringComparison.OrdinalIgnoreCase) &&
                           (t.Status == VideoTaskStatus.Waiting || t.IsRunning)))
        {
            return;
        }

        // Remove stale failed/canceled entries for the same file so re-adding starts cleanly
        var staleItems = Tasks
            .Where(t => string.Equals(t.FilePath, videoPath, StringComparison.OrdinalIgnoreCase) &&
                        (t.IsFailed || t.IsCanceled))
            .ToList();
        foreach (var stale in staleItems)
        {
            Tasks.Remove(stale);
        }

        var item = new VideoTaskItem(videoPath, importedRootDir)
        {
            Status = VideoTaskStatus.Waiting,
            StatusText = Loc.StatusWaiting,
            Progress = 0
        };

        Tasks.Add(item);
    }

    [RelayCommand]
    public void ClearCompleted()
    {
        RunOnUi(() =>
        {
            var finishedList = Tasks.Where(t => t.IsFinished).ToList();
            foreach (var item in finishedList)
            {
                Tasks.Remove(item);
            }
        });
    }

    [RelayCommand]
    public void RetryTask(VideoTaskItem item)
    {
        if (item == null || item.Status == VideoTaskStatus.Waiting || item.IsRunning) return;
        RunOnUi(() =>
        {
            item.Status = VideoTaskStatus.Waiting;
            item.StatusText = Loc.StatusWaiting;
            item.Progress = 0;
            item.ErrorMessage = null;
            item.OutputPath = null;
            item.Stopwatch.Reset();
            item.ElapsedTimeText = "00:00";
        });

        NotifyQueueUpdated();
    }

    [RelayCommand]
    public void CancelTask(VideoTaskItem item)
    {
        if (item == null) return;

        if (item.Status == VideoTaskStatus.Waiting)
        {
            try { item.Cts?.Cancel(); } catch { }
            RunOnUi(() =>
            {
                item.Status = VideoTaskStatus.Canceled;
                item.StatusText = Loc.StatusCanceled;
                item.ErrorMessage = null;
            });
            AppLogService.Instance.LogInfo($"[任务取消] 队列中等待任务已取消: {item.FileName}");
            return;
        }

        if (item.IsRunning)
        {
            try
            {
                item.Cts?.Cancel();
                RunOnUi(() => item.StatusText = Loc.StatusCanceling);
                AppLogService.Instance.LogInfo($"[任务取消] 正在中止运行中的任务: {item.FileName}");
            }
            catch { }
        }
    }

    [RelayCommand]
    public void OpenFolder(VideoTaskItem item)
    {
        if (item == null) return;

        string target = !string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath)
            ? item.OutputPath
            : item.FilePath;

        try
        {
            if (File.Exists(target))
            {
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            }
            else
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        }
        catch { }
    }

    private async Task StartQueueProcessingAsync()
    {
        if (!await _queueSemaphore.WaitAsync(0))
        {
            // Coordinator already running; signal it in case parallel slots are available
            try { _wakeSignal.Release(); } catch { }
            return;
        }

        int completedCount = 0;
        var activeWorkers = new Dictionary<Task<bool>, VideoTaskItem>();
        var claimedTaskIds = new HashSet<Guid>();

        try
        {
            RunOnUi(() => IsProcessing = true);
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            while (true)
            {
                // Drain queued wake signals before scanning available work
                while (_wakeSignal.Wait(0)) { }

                int maxConcurrency = Math.Clamp(SettingsService.Instance.CurrentSettings.MaxConcurrentTasks, 1, 4);

                while (activeWorkers.Count < maxConcurrency)
                {
                    var nextTask = await RunOnUiAsync(() =>
                        Tasks.FirstOrDefault(t => t.Status == VideoTaskStatus.Waiting && !claimedTaskIds.Contains(t.Id)));
                    if (nextTask == null) break;

                    claimedTaskIds.Add(nextTask.Id);
                    var capturedTask = nextTask;
                    Task<bool> worker = Task.Run(() => ProcessSingleTaskAsync(capturedTask, token));
                    activeWorkers[worker] = capturedTask;
                }

                if (activeWorkers.Count == 0)
                {
                    break;
                }

                // Wait until either any active worker finishes or a wake signal arrives (new file / retry / concurrency change)
                using var wakeCts = new CancellationTokenSource();
                var wakeTask = _wakeSignal.WaitAsync(wakeCts.Token);
                var waitList = new List<Task>(activeWorkers.Count + 1);
                waitList.AddRange(activeWorkers.Keys);
                waitList.Add(wakeTask);

                Task firstFinished = await Task.WhenAny(waitList);
                if (firstFinished != wakeTask)
                {
                    wakeCts.Cancel();
                    try { await wakeTask; } catch (OperationCanceledException) { }
                }

                foreach (var doneWorker in activeWorkers.Keys.Where(w => w.IsCompleted).ToList())
                {
                    var doneItem = activeWorkers[doneWorker];
                    activeWorkers.Remove(doneWorker);
                    claimedTaskIds.Remove(doneItem.Id);
                    try
                    {
                        if (await doneWorker)
                        {
                            completedCount++;
                        }
                    }
                    catch { }
                }
            }

            if (completedCount > 0)
            {
                NotificationService.Instance.ShowCompletionToast(
                    Loc.NotificationCompletedTitle,
                    $"{completedCount} {Loc.NotificationCompletedBody}");
            }
        }
        finally
        {
            RunOnUi(() => IsProcessing = false);
            _queueSemaphore.Release();
        }

        // Handle edge case where a new waiting task arrived right as the coordinator was exiting
        bool hasMoreWaiting = await RunOnUiAsync(() => Tasks.Any(t => t.Status == VideoTaskStatus.Waiting));
        if (hasMoreWaiting)
        {
            _ = StartQueueProcessingAsync();
        }
    }

    private async Task<bool> ProcessSingleTaskAsync(VideoTaskItem task, CancellationToken token)
    {
        DateTime taskStartTimeUtc = DateTime.UtcNow;
        string? tempAudio = null;
        string? createdSrtPath = null;
        string? createdAssPath = null;
        string? createdVideoPath = null;
        bool isCanceledOrFailed = false;
        bool isSucceeded = false;

        var settings = SettingsService.Instance.CurrentSettings;
        string taskSandboxDir = Path.Combine(Path.GetTempPath(), "LingMuCaption", $"task_{Guid.NewGuid():N}");
        try { Directory.CreateDirectory(taskSandboxDir); } catch { }

        var totalSw = Stopwatch.StartNew();
        var stageSw = new Stopwatch();
        var stageTimings = new Dictionary<string, double>();

        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        task.Cts = taskCts;
        var taskToken = taskCts.Token;

        // Abort immediately if the user canceled the task while it was being claimed
        bool alreadyCanceled = await RunOnUiAsync(() => task.Status == VideoTaskStatus.Canceled);
        if (alreadyCanceled || taskToken.IsCancellationRequested)
        {
            task.Cts = null;
            if (!settings.IsDebugMode && Directory.Exists(taskSandboxDir))
            {
                AudioExtractorService.Instance.CleanupDirectory(taskSandboxDir);
            }
            return false;
        }

        RunOnUi(() =>
        {
            task.Stopwatch.Restart();
            task.UpdateElapsed();
        });

        try
        {
            // Probe duration
            var duration = await AudioExtractorService.Instance.GetVideoDurationAsync(task.FilePath);
            if (duration > TimeSpan.Zero)
            {
                RunOnUi(() =>
                {
                    task.Duration = duration;
                    task.DurationText = TimeHelper.FormatDuration(duration);
                });
            }

            bool hasNvenc = await FFmpegService.Instance.CheckNvencAvailableAsync();
            AppLogService.Instance.LogTaskStart(
                task.FileName,
                duration,
                task.FileSize,
                settings.ModelName,
                settings.ThinkingIntensity,
                settings.OutputMode,
                hasNvenc,
                settings.EnableWebSearch,
                settings.TargetLanguage);

            EnsureSufficientDiskSpace(task.FilePath, task.ImportedRootDirectory, taskSandboxDir, duration, settings.OutputMode);

            // Step 1: Extract segmented M4A audio files (15-min chunks with +10s tail overlap)
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.ExtractingAudio;
                task.StatusText = Loc.StatusExtractingAudio;
                task.Progress = 0;
            });

            stageSw.Restart();
            var plannedSegments = CliTranscriptionService.BuildAudioSegments(duration);
            var extractedSegments = new List<AudioSegmentChunk>(plannedSegments.Count);

            for (int segIdx = 0; segIdx < plannedSegments.Count; segIdx++)
            {
                var planned = plannedSegments[segIdx];
                int capturedIdx = segIdx;
                int totalSegCount = plannedSegments.Count;

                if (totalSegCount > 1)
                {
                    RunOnUi(() => task.StatusText = $"{Loc.StatusExtractingAudio} ({capturedIdx + 1}/{totalSegCount})");
                }

                var extractProgress = new Progress<double>(p =>
                {
                    double overallPct = ((capturedIdx + Math.Clamp(p, 0.0, 100.0) / 100.0) / totalSegCount) * 10.0;
                    RunOnUi(() => task.Progress = overallPct); // 0% to 10%
                });

                TimeSpan? trimStart = planned.ExtractStart > TimeSpan.Zero ? planned.ExtractStart : null;
                TimeSpan? trimStop = planned.ExtractEnd > TimeSpan.Zero ? planned.ExtractEnd : null;
                string segFileName = $"seg_{planned.Index:00}.m4a";

                string segAudioPath = await AudioExtractorService.Instance.ExtractAudioAsync(
                    task.FilePath,
                    trimStart,
                    trimStop,
                    extractProgress,
                    taskToken,
                    outputDir: taskSandboxDir,
                    outputFileName: segFileName);

                extractedSegments.Add(planned with { FilePath = segAudioPath });
            }

            tempAudio = extractedSegments[0].FilePath;
            long totalAudioBytes = extractedSegments.Sum(s => File.Exists(s.FilePath) ? new FileInfo(s.FilePath).Length : 0);

            RunOnUi(() => task.Progress = 10);

            stageSw.Stop();
            double extractSec = stageSw.Elapsed.TotalSeconds;
            stageTimings["1. 音频分离提取"] = extractSec;
            AppLogService.Instance.LogStage(
                "1. 音频分离提取",
                extractSec,
                $"Windows MediaTranscoder AAC/M4A (48kbps/单声道), 共 {extractedSegments.Count} 段 (12分钟/+10秒尾缀), 总大小: {totalAudioBytes / (1024.0 * 1024.0):F2} MiB, 首段: {Path.GetFileName(tempAudio)}");

            // Step 2: Load all segments in single batch (<20MB), run Stage 1 across all audio, then transcribe per segment
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Transcribing;
                task.StatusText = Loc.StatusTranscribing;
                task.Progress = 10;
            });

            stageSw.Restart();
            var transcriptionProgress = new Progress<double>(p =>
            {
                RunOnUi(() => task.Progress = p); // 10% to 80%
            });

            string? transcriptionWarning = null;
            var rawSubtitles = await CliTranscriptionService.Instance.TranscribeAudioAsync(
                extractedSegments,
                duration,
                settings.TargetLanguage,
                settings.ModelName,
                settings.CliPath,
                settings.TranslationStyle,
                settings.SegmentationStyle,
                settings.WebSearchMode,
                transcriptionProgress,
                status =>
                {
                    if (status.Contains("⚠️") || status.Contains("未核验") || status.Contains("跳过"))
                    {
                        transcriptionWarning = status;
                    }
                    RunOnUi(() => task.StatusText = status);
                },
                taskToken);

            stageSw.Stop();
            double aiSec = stageSw.Elapsed.TotalSeconds;
            stageTimings["2. AI转录与翻译"] = aiSec;
            TimeSpan maxSeen = rawSubtitles.Count > 0 ? rawSubtitles.Max(s => s.EndTime) : TimeSpan.Zero;
            AppLogService.Instance.LogStage("2. AI转录与翻译", aiSec, $"模型: {settings.ModelName}, 思考强度: {settings.ThinkingIntensity}, 生成字幕: {rawSubtitles.Count} 条, 最新时间戳: {TimeHelper.FormatDuration(maxSeen)}");

            // Step 3: Optional Local Silero-VAD Start Alignment + Format & Validate
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Formatting;
                task.StatusText = Loc.StatusFormatting;
                task.Progress = 80;
            });

            stageSw.Restart();

            if (settings.EnableVadAlignment)
            {
                try
                {
                    var vadRes = await VadAlignmentService.Instance.RefineOnsetsAsync(task.FilePath, rawSubtitles, taskToken);
                    if (vadRes != null)
                    {
                        AppLogService.Instance.LogInfo(
                            $"[VAD对齐] Silero-VAD 本地起点精修完成: 耗时 {vadRes.InferenceSeconds:F2}s, 共调整 {vadRes.ModifiedCount}/{rawSubtitles.Count} 条 " +
                            $"(延后修正早挂 {vadRes.ForwardCount} 条, 提前回调 {vadRes.BackwardCount} 条, 中位偏移 {vadRes.MedianShiftMs:+0;-0;0}ms)。");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogService.Instance.LogInfo($"[VAD对齐] 本地精修异常，已回退保留原 AI 时间轴: {ex.Message}");
                }
            }

            var cleanSubtitles = SubtitleFormatterService.Instance.ValidateAndClean(rawSubtitles);

            bool isSameLanguage = SubtitleFormatterService.Instance.DetectSameLanguage(cleanSubtitles, settings.TargetLanguage);
            bool effectiveSecondary = settings.EnableSecondarySubtitle && !isSameLanguage;

            if (isSameLanguage && settings.EnableSecondarySubtitle)
            {
                AppLogService.Instance.LogInfo("[字幕格式化] 检测到原音频语言与目标语言一致（同语言视频），已自动停用副字幕，仅生成单语精修字幕。");
            }

            string outDir = FilePathHelper.GetOutputDirectory(task.FilePath, task.ImportedRootDirectory);
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            string baseFileName = Path.GetFileNameWithoutExtension(task.FilePath);
            string outVideoExt = Path.GetExtension(task.FilePath);

            // Step 4: Always generate standard SRT subtitle file
            string srtPath = Path.Combine(outDir, $"{baseFileName}.srt");
            string srtContent = SubtitleFormatterService.Instance.GenerateSrtContent(
                cleanSubtitles, 
                effectiveSecondary,
                showWatermark: true,
                modelName: settings.ModelName,
                targetLanguage: settings.TargetLanguage,
                totalDuration: task.Duration);
            await File.WriteAllTextAsync(srtPath, srtContent, taskToken);
            createdSrtPath = srtPath;

            // Step 5: Output according to chosen OutputMode
            if (settings.OutputMode == OutputMode.SubtitleOnly)
            {
                string assPath = Path.Combine(outDir, $"{baseFileName}.ass");
                string assContent = SubtitleFormatterService.Instance.GenerateAssContent(
                    cleanSubtitles, 
                    settings.EffectConfig, 
                    effectiveSecondary,
                    showWatermark: true,
                    modelName: settings.ModelName,
                    targetLanguage: settings.TargetLanguage,
                    totalDuration: task.Duration);
                await File.WriteAllTextAsync(assPath, assContent, taskToken);
                createdAssPath = assPath;
                RunOnUi(() => task.OutputPath = srtPath);
            }
            else if (settings.OutputMode == OutputMode.HardsubVideo)
            {
                RunOnUi(() =>
                {
                    task.Status = VideoTaskStatus.RenderingVideo;
                    task.StatusText = Loc.StatusRenderingVideo;
                    task.Progress = 80;
                });

                string outVideoPath = Path.Combine(outDir, $"{baseFileName}_subtitled{outVideoExt}");
                string tempSubFile = Path.Combine(Path.GetTempPath(), $"{baseFileName}_{Guid.NewGuid():N}.ass");
                createdVideoPath = outVideoPath;

                try
                {
                    string assContent = SubtitleFormatterService.Instance.GenerateAssContent(
                        cleanSubtitles, 
                        settings.EffectConfig, 
                        effectiveSecondary,
                        showWatermark: true,
                        modelName: settings.ModelName,
                        targetLanguage: settings.TargetLanguage,
                        totalDuration: task.Duration);
                    await File.WriteAllTextAsync(tempSubFile, assContent, taskToken);

                    var renderProgress = new Progress<double>(p =>
                    {
                        RunOnUi(() => task.Progress = 80.0 + (p * 0.19)); // 80% to 99%
                    });

                    await VideoSynthesizerService.Instance.SynthesizeHardsubVideoAsync(
                        task.FilePath,
                        outVideoPath,
                        cleanSubtitles,
                        settings.EffectConfig,
                        effectiveSecondary,
                        tempSubFile,
                        task.Duration,
                        renderProgress,
                        taskToken);

                    RunOnUi(() => task.OutputPath = outVideoPath);
                }
                finally
                {
                    if (File.Exists(tempSubFile))
                    {
                        try { File.Delete(tempSubFile); } catch { }
                    }
                }
            }
            else if (settings.OutputMode == OutputMode.SoftsubVideo)
            {
                RunOnUi(() =>
                {
                    task.Status = VideoTaskStatus.RenderingVideo;
                    task.StatusText = Loc.StatusRenderingVideo;
                    task.Progress = 80;
                });

                string outVideoPath = Path.Combine(outDir, $"{baseFileName}_subtitled{outVideoExt}");
                string tempSubFile = Path.Combine(Path.GetTempPath(), $"{baseFileName}_{Guid.NewGuid():N}.ass");
                createdVideoPath = outVideoPath;

                try
                {
                    string assContent = SubtitleFormatterService.Instance.GenerateAssContent(
                        cleanSubtitles, 
                        settings.EffectConfig, 
                        effectiveSecondary,
                        showWatermark: true,
                        modelName: settings.ModelName,
                        targetLanguage: settings.TargetLanguage,
                        totalDuration: task.Duration);
                    await File.WriteAllTextAsync(tempSubFile, assContent, taskToken);

                    await VideoSynthesizerService.Instance.SynthesizeSoftsubAsync(
                        task.FilePath,
                        outVideoPath,
                        tempSubFile,
                        taskToken);

                    RunOnUi(() => task.OutputPath = outVideoPath);
                }
                finally
                {
                    if (File.Exists(tempSubFile))
                    {
                        try { File.Delete(tempSubFile); } catch { }
                    }
                }
            }

            stageSw.Stop();
            double synthSec = stageSw.Elapsed.TotalSeconds;
            string synthStageName = settings.OutputMode switch
            {
                OutputMode.HardsubVideo => "3. 视频硬字幕压制",
                OutputMode.SoftsubVideo => "3. 视频软字幕封装",
                _ => "3. 字幕生成与校验"
            };
            stageTimings[synthStageName] = synthSec;
            AppLogService.Instance.LogStage(synthStageName, synthSec, $"输出: {task.OutputPath}");

            totalSw.Stop();
            double totalSec = totalSw.Elapsed.TotalSeconds;
            if (!string.IsNullOrEmpty(transcriptionWarning))
            {
                AppLogService.Instance.LogWarning($"[任务警告] 视频: {task.FileName} - {transcriptionWarning}");
            }
            AppLogService.Instance.LogTaskComplete(task.FileName, totalSec, stageTimings, isSuccess: true, outputPath: task.OutputPath);

            isSucceeded = true;
            RunOnUi(() =>
            {
                task.Stopwatch.Stop();
                task.UpdateElapsed();
                task.Status = VideoTaskStatus.Completed;
                if (!string.IsNullOrEmpty(transcriptionWarning))
                {
                    task.StatusText = $"{Loc.StatusCompleted} ({task.ElapsedTimeText}, 阶段三部分跳过)";
                }
                else
                {
                    task.StatusText = $"{Loc.StatusCompleted} ({task.ElapsedTimeText})";
                }
                task.Progress = 100;
            });
        }
        catch (OperationCanceledException)
        {
            isCanceledOrFailed = true;
            totalSw.Stop();
            AppLogService.Instance.LogTaskComplete(task.FileName, totalSw.Elapsed.TotalSeconds, stageTimings, isSuccess: false, error: "用户取消了任务");
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Canceled;
                task.StatusText = Loc.StatusCanceled;
                task.ErrorMessage = null;
            });
        }
        catch (Exception ex)
        {
            isCanceledOrFailed = true;
            totalSw.Stop();
            AppLogService.Instance.LogTaskComplete(task.FileName, totalSw.Elapsed.TotalSeconds, stageTimings, isSuccess: false, error: ex.Message);
            AppLogService.Instance.LogError($"[任务执行错误] {task.FileName}", ex);
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Failed;
                task.StatusText = Loc.StatusFailed;
                task.ErrorMessage = ex.Message;
            });
        }
        finally
        {
            task.Cts = null;
            RunOnUi(() =>
            {
                task.Stopwatch.Stop();
                task.UpdateElapsed();
            });

            // Always clean up any partial output files if the task was canceled or failed (even in debug mode)
            if (isCanceledOrFailed)
            {
                if (!string.IsNullOrEmpty(createdSrtPath) && File.Exists(createdSrtPath))
                {
                    try { File.Delete(createdSrtPath); } catch { }
                }
                if (!string.IsNullOrEmpty(createdAssPath) && File.Exists(createdAssPath))
                {
                    try { File.Delete(createdAssPath); } catch { }
                }
                if (!string.IsNullOrEmpty(createdVideoPath) && File.Exists(createdVideoPath))
                {
                    try { File.Delete(createdVideoPath); } catch { }
                }
            }

            if (settings.IsDebugMode)
            {
                AppLogService.Instance.LogInfo($"[调试模式] 任务结束，已保留临时沙盒与音频文件: {taskSandboxDir}");
            }
            else
            {
                if (!string.IsNullOrEmpty(tempAudio))
                {
                    AudioExtractorService.Instance.CleanupTempFile(tempAudio);
                }
                if (Directory.Exists(taskSandboxDir))
                {
                    AudioExtractorService.Instance.CleanupDirectory(taskSandboxDir);
                }
                CliTranscriptionService.CleanupSessionConversations(taskSandboxDir);

                AppLogService.Instance.LogInfo("[清理] 已彻底清理本次任务所有临时音频、沙盒与会话缓存。");
            }
        }

        return isSucceeded;
    }

    private static void EnsureSufficientDiskSpace(
        string videoFilePath,
        string? importedRootDir,
        string tempSandboxDir,
        TimeSpan duration,
        OutputMode outputMode)
    {
        long sourceBytes = 0;
        try
        {
            if (File.Exists(videoFilePath))
            {
                sourceBytes = new FileInfo(videoFilePath).Length;
            }
        }
        catch { }

        // Estimate required temporary space for AAC 192kbps extracted audio + sandbox logs (~2.5 MB/min, minimum 100 MB)
        long requiredTempBytes = Math.Max(
            100L * 1024 * 1024,
            duration.TotalMinutes > 0
                ? (long)(duration.TotalMinutes * 2.5 * 1024 * 1024)
                : Math.Max(100L * 1024 * 1024, sourceBytes / 10));

        // Estimate required output space based on OutputMode
        long requiredOutputBytes = outputMode switch
        {
            OutputMode.HardsubVideo => (long)(sourceBytes * 1.25) + (100L * 1024 * 1024),
            OutputMode.SoftsubVideo => (long)(sourceBytes * 1.05) + (50L * 1024 * 1024),
            _ => 20L * 1024 * 1024
        };

        string outDir = FilePathHelper.GetOutputDirectory(videoFilePath, importedRootDir);
        string? tempRoot = null;
        string? outRoot = null;
        try { tempRoot = Path.GetPathRoot(Path.GetFullPath(tempSandboxDir)); } catch { }
        try { outRoot = Path.GetPathRoot(Path.GetFullPath(outDir)); } catch { }

        static string FormatMbOrGb(long bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb >= 1.0 ? $"{gb:F2} GB" : $"{bytes / (1024.0 * 1024.0):F0} MB";
        }

        if (!string.IsNullOrWhiteSpace(tempRoot) &&
            !string.IsNullOrWhiteSpace(outRoot) &&
            string.Equals(tempRoot, outRoot, StringComparison.OrdinalIgnoreCase))
        {
            long totalRequired = requiredTempBytes + requiredOutputBytes;
            if (TryGetAvailableFreeSpace(outRoot, out long freeBytes) && freeBytes < totalRequired)
            {
                throw new IOException($"磁盘 ({outRoot.TrimEnd('\\')}) 剩余空间不足：当前可用 {FormatMbOrGb(freeBytes)}，本次任务预计需要至少 {FormatMbOrGb(totalRequired)}。");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) &&
                TryGetAvailableFreeSpace(tempRoot, out long freeTempBytes) &&
                freeTempBytes < requiredTempBytes)
            {
                throw new IOException($"系统临时盘 ({tempRoot.TrimEnd('\\')}) 剩余空间不足：当前可用 {FormatMbOrGb(freeTempBytes)}，提取音频预计需要至少 {FormatMbOrGb(requiredTempBytes)}。");
            }

            if (!string.IsNullOrWhiteSpace(outRoot) &&
                TryGetAvailableFreeSpace(outRoot, out long freeOutBytes) &&
                freeOutBytes < requiredOutputBytes)
            {
                throw new IOException($"输出目标盘 ({outRoot.TrimEnd('\\')}) 剩余空间不足：当前可用 {FormatMbOrGb(freeOutBytes)}，导出结果预计需要至少 {FormatMbOrGb(requiredOutputBytes)}。");
            }
        }
    }

    private static bool TryGetAvailableFreeSpace(string rootPath, out long availableFreeSpace)
    {
        availableFreeSpace = 0;
        try
        {
            var drive = new DriveInfo(rootPath);
            if (drive.IsReady)
            {
                availableFreeSpace = drive.AvailableFreeSpace;
                return true;
            }
        }
        catch { }
        return false;
    }
}
