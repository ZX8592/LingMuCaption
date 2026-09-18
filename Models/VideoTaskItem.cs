using System;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SubtitleMaster.Helpers;
using SubtitleMaster.Services;

namespace SubtitleMaster.Models;

public partial class VideoTaskItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _fileSize;

    [ObservableProperty]
    private string _fileSizeFormatted = string.Empty;

    [ObservableProperty]
    private string? _importedRootDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ShowElapsedTime))]
    [NotifyPropertyChangedFor(nameof(FormattedElapsedTime))]
    private VideoTaskStatus _status = VideoTaskStatus.Waiting;

    [ObservableProperty]
    private string _statusText = "Waiting in queue...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private double _progress = 0.0;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _outputPath;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private string _durationText = "--:--";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedElapsedTime))]
    private string _elapsedTimeText = "00:00";

    public Stopwatch Stopwatch { get; } = new();

    public string ProgressPercentText => $"{Math.Clamp((int)Math.Round(Progress), 0, 100)}%";

    public bool IsRunning => Status is VideoTaskStatus.ExtractingAudio
                                  or VideoTaskStatus.Transcribing
                                  or VideoTaskStatus.Formatting
                                  or VideoTaskStatus.RenderingVideo;

    public bool IsCompleted => Status == VideoTaskStatus.Completed;

    public bool IsFailed => Status == VideoTaskStatus.Failed;

    public string OpenText => LocalizationService.Instance.OpenFolderBtn;
    public string RetryText => LocalizationService.Instance.RetryBtn;

    public bool ShowElapsedTime => Status != VideoTaskStatus.Waiting || (ElapsedTimeText != "00:00" && !string.IsNullOrEmpty(ElapsedTimeText));

    public string FormattedElapsedTime => IsCompleted
        ? $"总耗时: {ElapsedTimeText}"
        : $"耗时: {ElapsedTimeText}";

    public void UpdateElapsed()
    {
        var ts = Stopwatch.Elapsed;
        ElapsedTimeText = $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    public VideoTaskItem(string filePath, string? importedRootDir = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        ImportedRootDirectory = importedRootDir;

        try
        {
            var info = new FileInfo(filePath);
            FileSize = info.Length;
            FileSizeFormatted = FilePathHelper.FormatFileSize(info.Length);
        }
        catch
        {
            FileSizeFormatted = "Unknown size";
        }
    }
}
