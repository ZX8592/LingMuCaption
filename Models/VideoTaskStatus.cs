namespace SubtitleMaster.Models;

public enum VideoTaskStatus
{
    Waiting,
    ExtractingAudio,
    Transcribing,
    Formatting,
    RenderingVideo,
    Completed,
    Failed,
    Canceled
}
