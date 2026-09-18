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
                if (t.Status != VideoTaskStatus.Waiting && t.Status != VideoTaskStatus.Completed && t.Status != VideoTaskStatus.Failed)
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

        // Start processing immediately!
        _ = StartQueueProcessingAsync();
    }

    private void AddVideoTask(string videoPath, string? importedRootDir)
    {
        // Avoid duplicate in queue if already waiting or running
        if (Tasks.Any(t => string.Equals(t.FilePath, videoPath, StringComparison.OrdinalIgnoreCase) && !t.IsCompleted && !t.IsFailed))
        {
            return;
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
        var completedList = Tasks.Where(t => t.IsCompleted).ToList();
        foreach (var item in completedList)
        {
            Tasks.Remove(item);
        }
    }

    [RelayCommand]
    public void RetryTask(VideoTaskItem item)
    {
        if (item == null) return;
        RunOnUi(() =>
        {
            item.Status = VideoTaskStatus.Waiting;
            item.StatusText = Loc.StatusWaiting;
            item.Progress = 0;
            item.ErrorMessage = null;
        });

        _ = StartQueueProcessingAsync();
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
            // Already running
            return;
        }

        int completedCount = 0;

        try
        {
            IsProcessing = true;
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            while (true)
            {
                var nextTask = Tasks.FirstOrDefault(t => t.Status == VideoTaskStatus.Waiting);
                if (nextTask == null) break;

                await ProcessSingleTaskAsync(nextTask, token);
                if (nextTask.IsCompleted)
                {
                    completedCount++;
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
            IsProcessing = false;
            _queueSemaphore.Release();
        }
    }

    private async Task ProcessSingleTaskAsync(VideoTaskItem task, CancellationToken token)
    {
        DateTime taskStartTimeUtc = DateTime.UtcNow;
        string? tempAudio = null;
        var settings = SettingsService.Instance.CurrentSettings;
        string taskSandboxDir = Path.Combine(Path.GetTempPath(), "LingMuCaption", $"task_{Guid.NewGuid():N}");
        try { Directory.CreateDirectory(taskSandboxDir); } catch { }

        var totalSw = Stopwatch.StartNew();
        var stageSw = new Stopwatch();
        var stageTimings = new Dictionary<string, double>();

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

            // Step 1: Extract full unbroken audio
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.ExtractingAudio;
                task.StatusText = Loc.StatusExtractingAudio;
                task.Progress = 10;
            });

            stageSw.Restart();
            var extractProgress = new Progress<double>(p =>
            {
                RunOnUi(() => task.Progress = 10 + (p * 0.25)); // 10% to 35%
            });

            tempAudio = await AudioExtractorService.Instance.ExtractAudioAsync(
                task.FilePath, 
                null, 
                null, 
                extractProgress, 
                token,
                outputDir: taskSandboxDir);

            stageSw.Stop();
            double extractSec = stageSw.Elapsed.TotalSeconds;
            stageTimings["1. 音频分离提取"] = extractSec;
            AppLogService.Instance.LogStage("1. 音频分离提取", extractSec, $"Windows MediaTranscoder AAC/M4A, 临时音频: {Path.GetFileName(tempAudio)}");

            // Step 2: Transcribe entire unbroken audio in single unified context window
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Transcribing;
                task.StatusText = Loc.StatusTranscribing;
                task.Progress = 40;
            });

            stageSw.Restart();
            var transcriptionProgress = new Progress<double>(p =>
            {
                RunOnUi(() => task.Progress = 40 + (p * 0.28)); // 40% to 68%
            });

            var rawSubtitles = await CliTranscriptionService.Instance.TranscribeAudioAsync(
                tempAudio,
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
                    RunOnUi(() => task.StatusText = status);
                },
                token);

            stageSw.Stop();
            double aiSec = stageSw.Elapsed.TotalSeconds;
            stageTimings["2. AI转录与翻译"] = aiSec;
            TimeSpan maxSeen = rawSubtitles.Count > 0 ? rawSubtitles.Max(s => s.EndTime) : TimeSpan.Zero;
            AppLogService.Instance.LogStage("2. AI转录与翻译", aiSec, $"模型: {settings.ModelName}, 思考强度: {settings.ThinkingIntensity}, 生成字幕: {rawSubtitles.Count} 条, 最新时间戳: {TimeHelper.FormatDuration(maxSeen)}");

            // Step 3: Format & Validate
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Formatting;
                task.StatusText = Loc.StatusFormatting;
                task.Progress = 70;
            });

            stageSw.Restart();
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
            await File.WriteAllTextAsync(srtPath, srtContent, token);

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
                await File.WriteAllTextAsync(assPath, assContent, token);
                RunOnUi(() => task.OutputPath = srtPath);
            }
            else if (settings.OutputMode == OutputMode.HardsubVideo)
            {
                RunOnUi(() =>
                {
                    task.Status = VideoTaskStatus.RenderingVideo;
                    task.StatusText = Loc.StatusRenderingVideo;
                    task.Progress = 75;
                });

                string outVideoPath = Path.Combine(outDir, $"{baseFileName}_subtitled{outVideoExt}");
                string tempSubFile = Path.Combine(Path.GetTempPath(), $"{baseFileName}_{Guid.NewGuid():N}.ass");

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
                    await File.WriteAllTextAsync(tempSubFile, assContent, token);

                    var renderProgress = new Progress<double>(p =>
                    {
                        RunOnUi(() => task.Progress = 75 + (p * 0.24)); // 75% to 99%
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
                        token);

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
                    task.Progress = 75;
                });

                string outVideoPath = Path.Combine(outDir, $"{baseFileName}_subtitled{outVideoExt}");
                string tempSubFile = Path.Combine(Path.GetTempPath(), $"{baseFileName}_{Guid.NewGuid():N}.ass");

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
                    await File.WriteAllTextAsync(tempSubFile, assContent, token);

                    await VideoSynthesizerService.Instance.SynthesizeSoftsubAsync(
                        task.FilePath,
                        outVideoPath,
                        tempSubFile,
                        token);

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
            AppLogService.Instance.LogTaskComplete(task.FileName, totalSec, stageTimings, isSuccess: true, outputPath: task.OutputPath);

            RunOnUi(() =>
            {
                task.Stopwatch.Stop();
                task.UpdateElapsed();
                task.Status = VideoTaskStatus.Completed;
                task.StatusText = $"{Loc.StatusCompleted} ({task.ElapsedTimeText})";
                task.Progress = 100;
            });
        }
        catch (OperationCanceledException)
        {
            totalSw.Stop();
            AppLogService.Instance.LogTaskComplete(task.FileName, totalSw.Elapsed.TotalSeconds, stageTimings, isSuccess: false, error: "用户取消了任务");
            RunOnUi(() =>
            {
                task.Status = VideoTaskStatus.Failed;
                task.StatusText = Loc.StatusFailed;
                task.ErrorMessage = "Operation was canceled.";
            });
        }
        catch (Exception ex)
        {
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
            RunOnUi(() =>
            {
                task.Stopwatch.Stop();
                task.UpdateElapsed();
            });

            if (settings.IsDebugMode)
            {
                AppLogService.Instance.LogInfo($"[调试模式] 任务完成，已保留临时沙盒与音频文件: {taskSandboxDir}");
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
                CliTranscriptionService.CleanupSessionConversations(taskStartTimeUtc);
                AppLogService.Instance.LogInfo("[清理] 已彻底清理本次任务所有临时音频、沙盒与会话缓存。");
            }
        }
    }
}
