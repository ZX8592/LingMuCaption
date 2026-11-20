using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SubtitleMaster.Helpers;

namespace SubtitleMaster.Services;

public class FFmpegService
{
    public sealed record AssPreviewFrame(
        string ImagePath,
        int X,
        int Y,
        int Width,
        int Height);

    public sealed record VideoStreamProbeInfo(
        int BitrateKbps,
        bool IsBitrateDetected,
        string? VideoCodec,
        string? PixelFormat,
        bool Is10Bit);

    private static FFmpegService? _instance;
    public static FFmpegService Instance => _instance ??= new FFmpegService();

    private string? _ffmpegPath;
    private bool? _hasNvenc;
    private bool? _hasHevc8BitNvenc;
    private bool? _hasHevc10BitNvenc;
    private bool? _hasAv18BitNvenc;
    private bool? _hasAv110BitNvenc;

    public string? GetFFmpegPath()
    {
        if (_ffmpegPath != null && File.Exists(_ffmpegPath))
        {
            return _ffmpegPath;
        }

        // 1. Check tools directory (tools/ffmpeg.exe or ../tools/ffmpeg.exe)
        string toolsPath = Path.Combine(FilePathHelper.GetToolsDirectory(), "ffmpeg.exe");
        if (File.Exists(toolsPath)) return _ffmpegPath = toolsPath;

        // 2. Local base directory
        string localPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(localPath)) return _ffmpegPath = localPath;

        // 3. Search system PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return _ffmpegPath = candidate;
                }
                catch { }
            }
        }

        return _ffmpegPath = null;
    }

    public bool IsAvailable => GetFFmpegPath() != null;

    /// <summary>
    /// Renders an ASS subtitle frame through the same FFmpeg/libass path used by
    /// hardsub export, then crops the transparent result to its visible pixels.
    /// The returned coordinates remain relative to the 1920x1080 ASS canvas.
    /// </summary>
    public async Task<AssPreviewFrame> RenderAssPreviewFrameAsync(
        string subtitlePath,
        string outputImagePath,
        CancellationToken ct = default)
    {
        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg == null) throw new InvalidOperationException("FFmpeg is not available.");

        string outputDirectory = Path.GetDirectoryName(outputImagePath)!;
        if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        string fullFramePath = Path.Combine(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(outputImagePath)}_full.png");

        try
        {
            string escapedSub = subtitlePath
                .Replace('\\', '/')
                .Replace(":", "\\:")
                .Replace("'", "\\'");

            string renderArgs =
                "-y -hide_banner -loglevel error " +
                "-f lavfi -i \"color=c=black@0.0:s=1920x1080:r=25:d=2,format=rgba\" " +
                $"-vf \"subtitles='{escapedSub}':alpha=1\" " +
                $"-ss 1 -frames:v 1 \"{fullFramePath}\"";

            await RunFFmpegCommandAsync(
                ffmpeg,
                renderArgs,
                TimeSpan.Zero,
                null,
                ct);

            var bboxResult = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel info -i \"{fullFramePath}\" -vf \"alphaextract,bbox=min_val=1\" -frames:v 1 -f null -"
            }, ct);

            if (bboxResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg preview bounds detection failed: {bboxResult.StandardError}");
            }

            var matches = Regex.Matches(
                bboxResult.StandardError,
                @"x1:(\d+)\s+x2:\d+\s+y1:(\d+)\s+y2:\d+\s+w:(\d+)\s+h:(\d+)");

            if (matches.Count == 0)
            {
                throw new InvalidOperationException("FFmpeg preview did not contain any visible subtitle pixels.");
            }

            Match bounds = matches[^1];
            int x = int.Parse(bounds.Groups[1].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(bounds.Groups[2].Value, CultureInfo.InvariantCulture);
            int width = int.Parse(bounds.Groups[3].Value, CultureInfo.InvariantCulture);
            int height = int.Parse(bounds.Groups[4].Value, CultureInfo.InvariantCulture);

            string cropArgs =
                $"-y -hide_banner -loglevel error -i \"{fullFramePath}\" " +
                $"-vf \"crop={width}:{height}:{x}:{y}\" -frames:v 1 \"{outputImagePath}\"";

            await RunFFmpegCommandAsync(
                ffmpeg,
                cropArgs,
                TimeSpan.Zero,
                null,
                ct);

            return new AssPreviewFrame(outputImagePath, x, y, width, height);
        }
        finally
        {
            try
            {
                if (File.Exists(fullFramePath)) File.Delete(fullFramePath);
            }
            catch { }
        }
    }

    /// <summary>
    /// Checks if NVIDIA NVENC hardware acceleration is actually functional on this PC.
    /// Uses native SilentProcessRunner with DETACHED_PROCESS to eliminate any window flash.
    /// </summary>
    public async Task<bool> CheckNvencAvailableAsync()
    {
        if (_hasNvenc.HasValue) return _hasNvenc.Value;

        bool isOk = await ProbeNvencEncoderAsync(
            "-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.1 -c:v h264_nvenc -f null -",
            "NVENC GPU 硬件加速");
        _hasNvenc = isOk;
        return isOk;
    }

    /// <summary>
    /// Checks if NVIDIA NVENC 8-bit HEVC hardware encoding (yuv420p Main) is supported on this PC.
    /// </summary>
    public async Task<bool> CheckHevc8BitNvencAvailableAsync()
    {
        if (_hasHevc8BitNvenc.HasValue) return _hasHevc8BitNvenc.Value;

        bool isOk = await ProbeNvencEncoderAsync(
            "-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.1 -pix_fmt yuv420p -c:v hevc_nvenc -profile:v main -f null -",
            "NVENC HEVC 8-bit 硬件加速");
        _hasHevc8BitNvenc = isOk;
        return isOk;
    }

    /// <summary>
    /// Checks if NVIDIA NVENC 10-bit HEVC hardware encoding (p010le Main 10) is supported on this PC.
    /// </summary>
    public async Task<bool> CheckHevc10BitNvencAvailableAsync()
    {
        if (_hasHevc10BitNvenc.HasValue) return _hasHevc10BitNvenc.Value;

        bool isOk = await ProbeNvencEncoderAsync(
            "-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.1 -pix_fmt p010le -c:v hevc_nvenc -profile:v main10 -f null -",
            "NVENC HEVC 10-bit 硬件加速");
        _hasHevc10BitNvenc = isOk;
        return isOk;
    }

    /// <summary>
    /// Checks if NVIDIA NVENC AV1 hardware encoding is supported for the requested bit depth.
    /// </summary>
    public async Task<bool> CheckAv1NvencAvailableAsync(bool is10Bit)
    {
        if (is10Bit)
        {
            if (_hasAv110BitNvenc.HasValue) return _hasAv110BitNvenc.Value;
            bool isOk = await ProbeNvencEncoderAsync(
                "-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.1 -pix_fmt p010le -c:v av1_nvenc -f null -",
                "NVENC AV1 10-bit 硬件加速");
            _hasAv110BitNvenc = isOk;
            return isOk;
        }

        if (_hasAv18BitNvenc.HasValue) return _hasAv18BitNvenc.Value;
        bool isOk8 = await ProbeNvencEncoderAsync(
            "-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.1 -pix_fmt yuv420p -c:v av1_nvenc -f null -",
            "NVENC AV1 8-bit 硬件加速");
        _hasAv18BitNvenc = isOk8;
        return isOk8;
    }

    private async Task<bool> ProbeNvencEncoderAsync(string probeArgs, string label)
    {
        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg == null) return false;

        try
        {
            var sw = Stopwatch.StartNew();
            var res = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
            {
                FileName = ffmpeg,
                Arguments = probeArgs
            });

            sw.Stop();
            bool isOk = res.ExitCode == 0;
            AppLogService.Instance.LogInfo($"[FFmpeg] {label}探测: {(isOk ? "可用" : "不可用")} (探测耗时: {sw.ElapsedMilliseconds}ms)");
            return isOk;
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning($"[FFmpeg] {label}探测异常: {ex.Message}，将降级处理");
            return false;
        }
    }

    /// <summary>
    /// Burns subtitles into video frames using libass with hardware acceleration.
    /// </summary>
    public async Task BurnInHardsubAsync(
        string videoPath,
        string subtitlePath,
        string outputVideoPath,
        TimeSpan videoDuration,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg == null) throw new InvalidOperationException("FFmpeg is not available.");

        string outDir = Path.GetDirectoryName(outputVideoPath)!;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        // Escape path for FFmpeg subtitles filter on Windows: C\:/path/to/sub.ass
        string escapedSub = subtitlePath.Replace('\\', '/').Replace(":", "\\:");
        string videoFilter = $"subtitles='{escapedSub}'";

        VideoStreamProbeInfo sourceInfo = await ProbeSourceVideoInfoAsync(videoPath, videoDuration);

        bool useTargetBitrate = sourceInfo.IsBitrateDetected && sourceInfo.BitrateKbps > 0;
        int targetBitrateKbps = useTargetBitrate
            ? Math.Max(300, (int)Math.Round(sourceInfo.BitrateKbps * 1.1))
            : sourceInfo.BitrateKbps;
        int maxrateKbps = useTargetBitrate
            ? (int)Math.Round(targetBitrateKbps * 1.5)
            : Math.Max(8000, sourceInfo.BitrateKbps * 2);
        int bufsizeKbps = maxrateKbps * 2;

        bool isMp4Family = Path.GetExtension(outputVideoPath) is var outExt &&
            (outExt.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
             outExt.Equals(".mov", StringComparison.OrdinalIgnoreCase));

        var (encoderDesc, encoderArgs) = await SelectHardsubEncoderAsync(
            sourceInfo,
            isMp4Family,
            useTargetBitrate,
            targetBitrateKbps,
            maxrateKbps,
            bufsizeKbps);

        string codecDesc = string.IsNullOrEmpty(sourceInfo.VideoCodec) ? "unknown" : sourceInfo.VideoCodec;
        string pixDesc = string.IsNullOrEmpty(sourceInfo.PixelFormat) ? "unknown" : sourceInfo.PixelFormat;
        string rateDesc = useTargetBitrate
            ? $"目标码率 {targetBitrateKbps} kbps (原视频 1.1x，峰值上限: {maxrateKbps} kbps)"
            : $"兜底 CQ/CRF 19 (峰值上限: {maxrateKbps} kbps)";
        AppLogService.Instance.LogInfo($"[FFmpeg] 开始压制硬字幕，编码器: {encoderDesc}，源格式: {codecDesc}/{pixDesc} ({(sourceInfo.Is10Bit ? "10-bit" : "8-bit")})，源视频码率: {sourceInfo.BitrateKbps} kbps -> {rateDesc}，视频时长: {(int)videoDuration.TotalMinutes:D2}:{videoDuration.Seconds:D2}");
        var sw = Stopwatch.StartNew();

        string args = $"-y -i \"{videoPath}\" -vf \"{videoFilter}\" {encoderArgs} -c:a copy -sn \"{outputVideoPath}\"";

        await RunFFmpegCommandAsync(ffmpeg, args, videoDuration, progress, ct);
        sw.Stop();

        double elapsed = sw.Elapsed.TotalSeconds;
        double speed = (videoDuration > TimeSpan.Zero && elapsed > 0) ? videoDuration.TotalSeconds / elapsed : 0;
        AppLogService.Instance.LogInfo($"[FFmpeg] 硬字幕压制完成! 耗时: {elapsed:F2}秒 (平均转码速度: {speed:F1}x) | 输出: {outputVideoPath}");
    }

    private async Task<(string Description, string Arguments)> SelectHardsubEncoderAsync(
        VideoStreamProbeInfo sourceInfo,
        bool isMp4Family,
        bool useTargetBitrate,
        int targetBitrateKbps,
        int maxrateKbps,
        int bufsizeKbps)
    {
        string hevcTagArg = isMp4Family ? " -tag:v hvc1" : string.Empty;
        string nvencRateArgs = useTargetBitrate
            ? $"-rc vbr -b:v {targetBitrateKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k"
            : $"-rc vbr -cq 19 -b:v 0 -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k";
        string x264RateArgs = useTargetBitrate
            ? $"-b:v {targetBitrateKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k"
            : $"-crf 19 -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k";
        string x265RateArgs = useTargetBitrate
            ? $"-b:v {targetBitrateKbps}k -x265-params vbv-maxrate={maxrateKbps}:vbv-bufsize={bufsizeKbps}"
            : $"-crf 19 -x265-params vbv-maxrate={maxrateKbps}:vbv-bufsize={bufsizeKbps}";

        string sourceCodec = sourceInfo.VideoCodec ?? string.Empty;
        string bitDepthLabel = sourceInfo.Is10Bit ? "10-bit" : "8-bit";

        if (sourceCodec == "av1" && await CheckAv1NvencAvailableAsync(sourceInfo.Is10Bit))
        {
            string av1PixFmt = sourceInfo.Is10Bit ? "p010le" : "yuv420p";
            return (
                $"NVIDIA NVENC AV1 {bitDepthLabel} (GPU 硬件加速)",
                $"-c:v av1_nvenc -preset p4 {nvencRateArgs} -pix_fmt {av1PixFmt}"
            );
        }

        if (sourceCodec == "hevc" || sourceInfo.Is10Bit)
        {
            if (sourceInfo.Is10Bit)
            {
                if (await CheckHevc10BitNvencAvailableAsync())
                {
                    return (
                        "NVIDIA NVENC HEVC 10-bit (GPU 硬件加速)",
                        $"-c:v hevc_nvenc -preset p4 -profile:v main10 {nvencRateArgs} -pix_fmt p010le{hevcTagArg}"
                    );
                }

                return (
                    "CPU libx265 10-bit (纯软编)",
                    $"-c:v libx265 -preset medium {x265RateArgs} -pix_fmt yuv420p10le{hevcTagArg}"
                );
            }

            if (await CheckHevc8BitNvencAvailableAsync())
            {
                return (
                    "NVIDIA NVENC HEVC 8-bit (GPU 硬件加速)",
                    $"-c:v hevc_nvenc -preset p4 -profile:v main {nvencRateArgs} -pix_fmt yuv420p{hevcTagArg}"
                );
            }

            return (
                "CPU libx265 8-bit (纯软编)",
                $"-c:v libx265 -preset medium {x265RateArgs} -pix_fmt yuv420p{hevcTagArg}"
            );
        }

        if (await CheckNvencAvailableAsync())
        {
            return (
                "NVIDIA NVENC H.264 (GPU 硬件加速)",
                $"-c:v h264_nvenc -preset p4 -profile:v high {nvencRateArgs} -pix_fmt yuv420p"
            );
        }

        return (
            "CPU libx264 (纯软编)",
            $"-c:v libx264 -preset fast {x264RateArgs} -pix_fmt yuv420p"
        );
    }

    /// <summary>
    /// Muxes subtitles into video container as an internal stream (instant lossless packaging).
    /// </summary>
    public async Task MuxSoftsubAsync(
        string videoPath,
        string subtitlePath,
        string outputVideoPath,
        CancellationToken ct = default)
    {
        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg == null) throw new InvalidOperationException("FFmpeg is not available.");

        string outDir = Path.GetDirectoryName(outputVideoPath)!;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        string subExt = Path.GetExtension(subtitlePath).ToLowerInvariant();
        string outExt = Path.GetExtension(outputVideoPath).ToLowerInvariant();

        string subtitleCodec = "copy";
        if (outExt == ".mp4")
        {
            // MP4 container only supports mov_text
            subtitleCodec = "mov_text";
        }
        else if (subExt == ".ass")
        {
            subtitleCodec = "ass";
        }

        var sw = Stopwatch.StartNew();
        AppLogService.Instance.LogInfo($"[FFmpeg] 开始封装软字幕轨 ({subtitleCodec}) 到: {Path.GetFileName(outputVideoPath)}");

        string args = $"-y -i \"{videoPath}\" -i \"{subtitlePath}\" -map 0:v -map 0:a? -map 1:s -c copy -c:s {subtitleCodec} -disposition:s:0 default \"{outputVideoPath}\"";

        await RunFFmpegCommandAsync(ffmpeg, args, TimeSpan.Zero, null, ct);
        sw.Stop();
        AppLogService.Instance.LogInfo($"[FFmpeg] 软字幕封装完成，耗时: {sw.Elapsed.TotalSeconds:F2}秒");
    }

    /// <summary>
    /// Probes the source video stream bitrate (kbps), video codec, pixel format, and 10-bit depth
    /// via ffmpeg -i stream metadata, falling back to file size math when bitrate is missing.
    /// </summary>
    public async Task<VideoStreamProbeInfo> ProbeSourceVideoInfoAsync(string videoPath, TimeSpan duration)
    {
        int bitrateKbps = 0;
        string? videoCodec = null;
        string? pixFmt = null;

        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg != null)
        {
            // Parse ffmpeg -hide_banner -i stderr
            try
            {
                var res = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
                {
                    FileName = ffmpeg,
                    Arguments = $"-hide_banner -i \"{videoPath}\""
                });

                var parsed = ParseFfmpegProbeOutput(res.StandardError);
                if (parsed.BitrateKbps > 0)
                {
                    bitrateKbps = parsed.BitrateKbps;
                }
                if (!string.IsNullOrEmpty(parsed.VideoCodec))
                {
                    videoCodec = parsed.VideoCodec;
                }
                if (!string.IsNullOrEmpty(parsed.PixelFormat))
                {
                    pixFmt = parsed.PixelFormat;
                }
            }
            catch { }
        }

        bool isBitrateDetected = bitrateKbps > 0;

        // Mathematical fallback from file size and duration when container omits bitrate
        if (bitrateKbps <= 0)
        {
            try
            {
                if (File.Exists(videoPath) && duration.TotalSeconds > 1.0)
                {
                    long fileSizeBytes = new FileInfo(videoPath).Length;
                    double totalBitrateBps = (fileSizeBytes * 8.0) / duration.TotalSeconds;
                    bitrateKbps = Math.Max(500, (int)((totalBitrateBps * 0.90) / 1000));
                    isBitrateDetected = true;
                }
            }
            catch { }
        }

        if (bitrateKbps <= 0) bitrateKbps = 4000;

        bool is10Bit = Is10BitPixelFormat(pixFmt);
        return new VideoStreamProbeInfo(bitrateKbps, isBitrateDetected, videoCodec, pixFmt, is10Bit);
    }

    private static (int BitrateKbps, string? VideoCodec, string? PixelFormat) ParseFfmpegProbeOutput(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return (0, null, null);

        int bitrateKbps = 0;
        string? videoCodec = null;
        string? pixFmt = null;
        int containerBitrateKbps = 0;
        int audioBitrateSumKbps = 0;

        foreach (string line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (containerBitrateKbps == 0)
            {
                var durMatch = Regex.Match(line, @"Duration:.*?\bbitrate:\s*(\d+)\s*kb/s", RegexOptions.IgnoreCase);
                if (durMatch.Success && int.TryParse(durMatch.Groups[1].Value, out int totalKbps))
                {
                    containerBitrateKbps = totalKbps;
                }
            }

            if (videoCodec == null && pixFmt == null && line.Contains("Video:", StringComparison.Ordinal) && !line.Contains("attached pic", StringComparison.OrdinalIgnoreCase))
            {
                // Format: Video: <codec> [(profile)] [(tag)], <pix_fmt>[(details)], <WxH>, [<kbps> kb/s]
                var codecMatch = Regex.Match(line, @"Video:\s*([a-z0-9_]+)", RegexOptions.IgnoreCase);
                if (codecMatch.Success)
                {
                    videoCodec = NormalizeVideoCodecName(codecMatch.Groups[1].Value);
                }

                var pixMatch = Regex.Match(line, @"Video:\s*[^,\r\n]+,\s*([a-z][a-z0-9_]+)", RegexOptions.IgnoreCase);
                if (pixMatch.Success)
                {
                    pixFmt = pixMatch.Groups[1].Value.Trim();
                }

                var vRateMatch = Regex.Match(line, @"\b(\d+)\s*kb/s\b", RegexOptions.IgnoreCase);
                if (vRateMatch.Success && int.TryParse(vRateMatch.Groups[1].Value, out int vKbps) && vKbps > 50)
                {
                    bitrateKbps = vKbps;
                }
            }
            else if (line.Contains("Audio:", StringComparison.Ordinal))
            {
                var aRateMatch = Regex.Match(line, @"\b(\d+)\s*kb/s\b", RegexOptions.IgnoreCase);
                if (aRateMatch.Success && int.TryParse(aRateMatch.Groups[1].Value, out int aKbps))
                {
                    audioBitrateSumKbps += aKbps;
                }
            }
        }

        if (bitrateKbps <= 0 && containerBitrateKbps > 0)
        {
            bitrateKbps = Math.Max(500, containerBitrateKbps - audioBitrateSumKbps);
        }

        return (bitrateKbps, videoCodec, pixFmt);
    }

    private static string NormalizeVideoCodecName(string rawCodec)
    {
        string normalized = rawCodec.Trim().ToLowerInvariant();
        return normalized switch
        {
            "h265" or "hev1" or "hvc1" => "hevc",
            "avc" or "avc1" => "h264",
            "av01" => "av1",
            _ => normalized
        };
    }

    private static bool Is10BitPixelFormat(string? pixFmt)
    {
        if (string.IsNullOrWhiteSpace(pixFmt)) return false;

        return Regex.IsMatch(
            pixFmt,
            @"(?:p010|p210|p410|10le|10be|10msb)",
            RegexOptions.IgnoreCase);
    }

    private async Task RunFFmpegCommandAsync(
        string ffmpegPath,
        string arguments,
        TimeSpan totalDuration,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var errorOutput = new System.Text.StringBuilder();
        var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})", RegexOptions.Compiled);

        void OnLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            errorOutput.AppendLine(line);

            if (progress != null && totalDuration > TimeSpan.Zero)
            {
                var match = timeRegex.Match(line);
                if (match.Success)
                {
                    if (int.TryParse(match.Groups[1].Value, out int hours) &&
                        int.TryParse(match.Groups[2].Value, out int minutes) &&
                        double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
                    {
                        var current = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
                        double pct = Math.Clamp((current.TotalSeconds / totalDuration.TotalSeconds) * 100.0, 0, 100);
                        progress.Report(pct);
                    }
                }
            }
        }

        var res = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            OnErrorLine = OnLine,
            OnOutputLine = OnLine
        }, ct);

        if (res.ExitCode != 0)
        {
            string err = errorOutput.ToString();
            var lines = err.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int takeCount = Math.Min(10, lines.Length);
            string tail = lines.Length > 0 ? string.Join("\n", lines[^takeCount..]) : "No output";
            throw new InvalidOperationException($"FFmpeg failed with exit code {res.ExitCode}:\n{tail}");
        }
    }
}
