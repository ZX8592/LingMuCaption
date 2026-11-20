using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public class VideoSynthesizerService
{
    private static VideoSynthesizerService? _instance;
    public static VideoSynthesizerService Instance => _instance ??= new VideoSynthesizerService();

    /// <summary>
    /// Synthesizes hard subtitles into video frames using FFmpeg/libass.
    /// </summary>
    public async Task SynthesizeHardsubVideoAsync(
        string sourceVideoPath,
        string destinationVideoPath,
        List<SubtitleItem> subtitles,
        EffectSubtitleConfig config,
        bool includeSecondary,
        string? subtitleFilePath = null,
        TimeSpan? videoDuration = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceVideoPath))
            throw new FileNotFoundException($"Source video not found: {sourceVideoPath}");

        if (!FFmpegService.Instance.IsAvailable)
            throw new InvalidOperationException("未检测到 FFmpeg (tools/ffmpeg.exe)，无法压制硬字幕视频。");

        string? tempAss = null;
        try
        {
            string subPathToUse = subtitleFilePath ?? string.Empty;
            if (string.IsNullOrEmpty(subPathToUse) || !File.Exists(subPathToUse))
            {
                tempAss = Path.Combine(Path.GetTempPath(), $"sub_{Guid.NewGuid():N}.ass");
                string assContent = SubtitleFormatterService.Instance.GenerateAssContent(subtitles, config, includeSecondary);
                await File.WriteAllTextAsync(tempAss, assContent, cancellationToken);
                subPathToUse = tempAss;
            }

            await FFmpegService.Instance.BurnInHardsubAsync(
                sourceVideoPath,
                subPathToUse,
                destinationVideoPath,
                videoDuration ?? TimeSpan.Zero,
                progress,
                cancellationToken);

            if (!File.Exists(destinationVideoPath) || new FileInfo(destinationVideoPath).Length == 0)
            {
                throw new InvalidOperationException("FFmpeg 硬字幕压制未生成有效输出视频文件。");
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempAss) && File.Exists(tempAss))
            {
                try { File.Delete(tempAss); } catch { }
            }
        }
    }

    /// <summary>
    /// Synthesizes softsub (embedded subtitle track via FFmpeg stream muxing).
    /// </summary>
    public async Task SynthesizeSoftsubAsync(
        string sourceVideoPath,
        string destinationVideoPath,
        string subtitleFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!FFmpegService.Instance.IsAvailable)
            throw new InvalidOperationException("未检测到 FFmpeg (tools/ffmpeg.exe)，无法封装软字幕视频。");

        await FFmpegService.Instance.MuxSoftsubAsync(sourceVideoPath, subtitleFilePath, destinationVideoPath, cancellationToken);
        if (!File.Exists(destinationVideoPath) || new FileInfo(destinationVideoPath).Length == 0)
        {
            throw new InvalidOperationException("FFmpeg 软字幕封装未生成有效输出视频文件。");
        }
    }
}
