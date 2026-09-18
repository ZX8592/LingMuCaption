using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace SubtitleMaster.Services;

public class AudioExtractorService
{
    private static AudioExtractorService? _instance;
    public static AudioExtractorService Instance => _instance ??= new AudioExtractorService();

    /// <summary>
    /// Gets the duration of the video file using Windows native Shell/Storage properties.
    /// </summary>
    public async Task<TimeSpan> GetVideoDurationAsync(string videoFilePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(videoFilePath);
            var props = await file.Properties.GetVideoPropertiesAsync();
            return props.Duration;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Extracts audio from a video file into a temporary M4A/MP3 file using Windows Native MediaTranscoder.
    /// Supports optional startTime/stopTime trimming for long audio chunking.
    /// Completely zero dependency on external ffmpeg.exe.
    /// </summary>
    public async Task<string> ExtractAudioAsync(
        string videoFilePath,
        TimeSpan? startTime = null,
        TimeSpan? stopTime = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        string? outputDir = null)
    {
        if (!File.Exists(videoFilePath))
        {
            throw new FileNotFoundException($"Video file not found: {videoFilePath}");
        }

        string tempFolderDir = !string.IsNullOrEmpty(outputDir) && Directory.Exists(outputDir)
            ? outputDir
            : Path.Combine(Path.GetTempPath(), "LingMuCaption", "audio");
        if (!Directory.Exists(tempFolderDir))
        {
            Directory.CreateDirectory(tempFolderDir);
        }

        string tempAudioPath = Path.Combine(tempFolderDir, $"audio_{Guid.NewGuid():N}.m4a");


        try
        {
            var sourceFile = await StorageFile.GetFileFromPathAsync(videoFilePath);
            var tempFolder = await StorageFolder.GetFolderFromPathAsync(tempFolderDir);
            var destFile = await tempFolder.CreateFileAsync(Path.GetFileName(tempAudioPath), CreationCollisionOption.ReplaceExisting);

            var transcoder = new MediaTranscoder
            {
                // We only need the audio stream
                VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default
            };

            if (startTime.HasValue && startTime.Value > TimeSpan.Zero)
            {
                transcoder.TrimStartTime = startTime.Value;
            }
            if (stopTime.HasValue && stopTime.Value > TimeSpan.Zero)
            {
                transcoder.TrimStopTime = stopTime.Value;
            }

            // Standard AAC M4A profile (lightweight, universally compatible with Antigravity multimodal)
            var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);

            var prepareOp = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);
            if (!prepareOp.CanTranscode)
            {
                // Try MP3 fallback if M4A profile failed
                profile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Medium);
                prepareOp = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);

                if (!prepareOp.CanTranscode)
                {
                    throw new InvalidOperationException($"Windows Media Transcoder cannot process this video: {prepareOp.FailureReason}");
                }
            }

            var transcodeOp = prepareOp.TranscodeAsync();
            transcodeOp.Progress = (info, percent) =>
            {
                progress?.Report(percent);
            };

            using (cancellationToken.Register(() => transcodeOp.Cancel()))
            {
                await transcodeOp.AsTask(cancellationToken);
            }

            if (!File.Exists(destFile.Path) || new FileInfo(destFile.Path).Length == 0)
            {
                throw new InvalidOperationException("Extracted audio file is empty or missing.");
            }

            return destFile.Path;
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tempAudioPath))
            {
                try { File.Delete(tempAudioPath); } catch { }
            }
            throw;
        }
        catch (Exception ex)
        {
            if (File.Exists(tempAudioPath))
            {
                try { File.Delete(tempAudioPath); } catch { }
            }
            throw new InvalidOperationException($"Audio extraction failed: {ex.Message}", ex);
        }
    }

    public void CleanupTempFile(string? tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    public void CleanupDirectory(string? dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return;
        try
        {
            Directory.Delete(dirPath, recursive: true);
        }
        catch { }
    }
}
