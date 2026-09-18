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

    private static FFmpegService? _instance;
    public static FFmpegService Instance => _instance ??= new FFmpegService();

    private string? _ffmpegPath;
    private bool? _hasNvenc;

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

        // 3. Gyan WinGet default package directory
        string wingetGyan = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages\Gyan.FFmpeg.Essentials_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.0.1-essentials_build\bin\ffmpeg.exe");
        if (File.Exists(wingetGyan)) return _ffmpegPath = wingetGyan;

        // 4. Search system PATH
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

        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg == null) return (_hasNvenc = false).Value;

        try
        {
            var sw = Stopwatch.StartNew();
            var res = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.1 -c:v h264_nvenc -f null -"
            });

            sw.Stop();
            bool isOk = res.ExitCode == 0;
            _hasNvenc = isOk;
            AppLogService.Instance.LogInfo($"[FFmpeg] NVENC GPU 硬件加速探测: {(isOk ? "可用 (已启用 NVIDIA NVENC 硬件加速)" : "不可用 (将使用 CPU libx264 编码)")} (探测耗时: {sw.ElapsedMilliseconds}ms)");
            return isOk;
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning($"[FFmpeg] NVENC 探测异常: {ex.Message}，降级为 CPU");
            return (_hasNvenc = false).Value;
        }
    }

    /// <summary>
    /// Extracts audio as AAC/M4A with a stable 44.1 kHz time base.
    /// MP4 edit lists are disabled because some multimodal decoders misinterpret
    /// FFmpeg AAC priming offsets and stretch the decoded timeline.
    /// </summary>
    public async Task ExtractAudioAsync(
        string videoPath,
        string outputAudioPath,
        TimeSpan expectedOutputDuration,
        TimeSpan? startTime = null,
        TimeSpan? stopTime = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string? ffmpeg = GetFFmpegPath();
        if (ffmpeg == null) throw new InvalidOperationException("FFmpeg is not available.");

        string outDir = Path.GetDirectoryName(outputAudioPath)!;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        var sw = Stopwatch.StartNew();
        AppLogService.Instance.LogInfo($"[FFmpeg] 开始提取音频: {Path.GetFileName(videoPath)} -> {Path.GetFileName(outputAudioPath)} (AAC 192 kbps, 44.1 kHz, 无 Edit List)");

        TimeSpan trimStart = startTime.GetValueOrDefault();
        string trimArgs = trimStart > TimeSpan.Zero
            ? $"-ss {FormatFfmpegTimestamp(trimStart)} "
            : string.Empty;

        if (stopTime.HasValue && stopTime.Value > TimeSpan.Zero)
        {
            if (stopTime.Value <= trimStart)
            {
                throw new ArgumentOutOfRangeException(nameof(stopTime), "Audio stop time must be greater than start time.");
            }

            trimArgs += $"-t {FormatFfmpegTimestamp(stopTime.Value - trimStart)} ";
        }

        // Keep resampling synchronous (no time stretching), anchor the first audio PTS at zero,
        // force a 1/44100 audio time base, and suppress the MP4 edts/elst atoms.
        string args = $"-y -hide_banner -i \"{videoPath}\" {trimArgs}" +
            "-map 0:a:0 -map_metadata -1 -vn -sn -dn " +
            "-af \"aresample=44100:async=0:first_pts=0\" " +
            $"-c:a aac -b:a 192k -ar 44100 -avoid_negative_ts make_zero -use_editlist 0 \"{outputAudioPath}\"";

        await RunFFmpegCommandAsync(ffmpeg, args, expectedOutputDuration, progress, ct);
        progress?.Report(100.0);
        sw.Stop();
        AppLogService.Instance.LogInfo($"[FFmpeg] 音频提取完成，耗时: {sw.Elapsed.TotalSeconds:F2}秒");
    }

    private static string FormatFfmpegTimestamp(TimeSpan value)
    {
        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
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

        int sourceBitrateKbps = await ProbeSourceVideoBitrateKbpsAsync(videoPath, videoDuration);
        
        // Target: 105% of source bitrate to prevent quality loss while preserving file size
        int targetKbps = Math.Max(350, (int)(sourceBitrateKbps * 1.05));
        // Maxrate: 125% of target to allow peak complex frames without bloating file size
        int maxrateKbps = (int)(targetKbps * 1.25);
        // Bufsize: 2x target for smooth rate control
        int bufsizeKbps = targetKbps * 2;

        bool hasNvenc = await CheckNvencAvailableAsync();
        string encoderDesc = hasNvenc ? "NVIDIA NVENC (GPU 硬件加速)" : "CPU libx264 (纯软编)";
        string encoderArgs = hasNvenc
            ? $"-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v {targetKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k -pix_fmt yuv420p"
            : $"-c:v libx264 -preset fast -crf 22 -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k -pix_fmt yuv420p";

        AppLogService.Instance.LogInfo($"[FFmpeg] 开始压制硬字幕，编码器: {encoderDesc}，源视频码率: {sourceBitrateKbps} kbps -> 压制目标码率: {targetKbps} kbps (上限: {maxrateKbps} kbps)，视频时长: {(int)videoDuration.TotalMinutes:D2}:{videoDuration.Seconds:D2}");
        var sw = Stopwatch.StartNew();

        string args = $"-y -i \"{videoPath}\" -vf \"{videoFilter}\" {encoderArgs} -c:a copy \"{outputVideoPath}\"";

        await RunFFmpegCommandAsync(ffmpeg, args, videoDuration, progress, ct);
        sw.Stop();

        double elapsed = sw.Elapsed.TotalSeconds;
        double speed = (videoDuration > TimeSpan.Zero && elapsed > 0) ? videoDuration.TotalSeconds / elapsed : 0;
        AppLogService.Instance.LogInfo($"[FFmpeg] 硬字幕压制完成! 耗时: {elapsed:F2}秒 (平均转码速度: {speed:F1}x) | 输出: {outputVideoPath}");
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

        string args = $"-y -i \"{videoPath}\" -i \"{subtitlePath}\" -c copy -c:s {subtitleCodec} \"{outputVideoPath}\"";

        await RunFFmpegCommandAsync(ffmpeg, args, TimeSpan.Zero, null, ct);
        sw.Stop();
        AppLogService.Instance.LogInfo($"[FFmpeg] 软字幕封装完成，耗时: {sw.Elapsed.TotalSeconds:F2}秒");
    }

    /// <summary>
    /// Accurately estimates or probes the source video stream bitrate in kbps.
    /// Used to preserve original video file size and visual quality without inflating the output by 4-5x.
    /// </summary>
    public async Task<int> ProbeSourceVideoBitrateKbpsAsync(string videoPath, TimeSpan duration)
    {
        // Method 1: ffprobe if available alongside ffmpeg
        try
        {
            string? ffmpeg = GetFFmpegPath();
            if (ffmpeg != null)
            {
                string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
                if (File.Exists(ffprobe))
                {
                    var res = await SilentProcessRunner.RunAsync(new ProcessExecutionOptions
                    {
                        FileName = ffprobe,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\""
                    });

                    string outStr = res.StandardOutput.Trim();
                    if (long.TryParse(outStr, out long bps) && bps > 50000)
                    {
                        return (int)(bps / 1000);
                    }
                }
            }
        }
        catch { }

        // Method 2: Mathematical computation from file size and duration
        try
        {
            if (File.Exists(videoPath) && duration.TotalSeconds > 1.0)
            {
                long fileSizeBytes = new FileInfo(videoPath).Length;
                double totalBitrateBps = (fileSizeBytes * 8.0) / duration.TotalSeconds;
                int videoBitrateKbps = Math.Max(500, (int)((totalBitrateBps * 0.85) / 1000));
                return videoBitrateKbps;
            }
        }
        catch { }

        return 4000;
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
