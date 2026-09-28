using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public class VideoSynthesizerService
{
    private static VideoSynthesizerService? _instance;
    public static VideoSynthesizerService Instance => _instance ??= new VideoSynthesizerService();

    /// <summary>
    /// Synthesizes hard subtitles into video frames using Windows Media Foundation MediaComposition.
    /// Completely self-contained, no external ffmpeg.exe needed.
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

        config = SubtitleFormatterService.Instance.GetEffectivePreviewConfig(config);

        // Prefer FFmpeg with libass and NVENC hardware acceleration
        if (FFmpegService.Instance.IsAvailable)
        {
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

                if (File.Exists(destinationVideoPath) && new FileInfo(destinationVideoPath).Length > 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Fallback to MediaComposition below
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempAss) && File.Exists(tempAss))
                {
                    try { File.Delete(tempAss); } catch { }
                }
            }
        }

        string tempOverlayDir = Path.Combine(Path.GetTempPath(), $"submaster_overlays_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempOverlayDir);

        try
        {
            var sourceFile = await StorageFile.GetFileFromPathAsync(sourceVideoPath);
            var destFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destinationVideoPath)!);
            var destFile = await destFolder.CreateFileAsync(Path.GetFileName(destinationVideoPath), CreationCollisionOption.ReplaceExisting);

            var videoClip = await MediaClip.CreateFromFileAsync(sourceFile);
            var composition = new MediaComposition();
            composition.Clips.Add(videoClip);

            // Get video resolution (default to 1920x1080 if not detectable)
            uint width = videoClip.GetVideoEncodingProperties().Width;
            uint height = videoClip.GetVideoEncodingProperties().Height;
            if (width == 0) width = 1920;
            if (height == 0) height = 1080;

            var overlayLayer = new MediaOverlayLayer();

            for (int i = 0; i < subtitles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = subtitles[i];
                var duration = item.EndTime - item.StartTime;
                if (duration <= TimeSpan.Zero) continue;

                // Render subtitle frame as transparent PNG
                string overlayImgPath = Path.Combine(tempOverlayDir, $"sub_{i:0000}.png");
                RenderSubtitleToPng(overlayImgPath, (int)width, (int)height, item, config, includeSecondary);

                var imgFile = await StorageFile.GetFileFromPathAsync(overlayImgPath);
                var imgClip = await MediaClip.CreateFromImageFileAsync(imgFile, duration);

                var overlay = new MediaOverlay(imgClip)
                {
                    Position = new Windows.Foundation.Rect(0, 0, width, height),
                    Delay = item.StartTime,
                    Opacity = 1.0
                };

                overlayLayer.Overlays.Add(overlay);
            }

            composition.OverlayLayers.Add(overlayLayer);

            var renderOp = composition.RenderToFileAsync(destFile, MediaTrimmingPreference.Fast);
            renderOp.Progress = (info, percent) =>
            {
                progress?.Report(percent);
            };

            using (cancellationToken.Register(() => renderOp.Cancel()))
            {
                var failureReason = await renderOp.AsTask(cancellationToken);
                if (failureReason != TranscodeFailureReason.None)
                {
                    throw new InvalidOperationException($"Video rendering failed: {failureReason}");
                }
            }
        }
        finally
        {
            // Clean up temporary image files
            try
            {
                if (Directory.Exists(tempOverlayDir))
                {
                    Directory.Delete(tempOverlayDir, true);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Synthesizes softsub (embedded subtitle track or lossless companion pairing).
    /// </summary>
    public async Task SynthesizeSoftsubAsync(
        string sourceVideoPath,
        string destinationVideoPath,
        string subtitleFilePath,
        CancellationToken cancellationToken = default)
    {
        if (FFmpegService.Instance.IsAvailable)
        {
            try
            {
                await FFmpegService.Instance.MuxSoftsubAsync(sourceVideoPath, subtitleFilePath, destinationVideoPath, cancellationToken);
                if (File.Exists(destinationVideoPath) && new FileInfo(destinationVideoPath).Length > 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Fallback to companion copy
            }
        }

        // Fallback for environments without FFmpeg:
        // Copy the source video to destination, and output matching subtitle companion file
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(Path.GetFullPath(sourceVideoPath), Path.GetFullPath(destinationVideoPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceVideoPath, destinationVideoPath, true);
            }

            string subExt = Path.GetExtension(subtitleFilePath);
            string targetSubPath = Path.ChangeExtension(destinationVideoPath, subExt);
            if (!string.Equals(Path.GetFullPath(subtitleFilePath), Path.GetFullPath(targetSubPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(subtitleFilePath, targetSubPath, true);
            }
        }, cancellationToken);
    }

    private void RenderSubtitleToPng(
        string outputPath,
        int videoWidth,
        int videoHeight,
        SubtitleItem item,
        EffectSubtitleConfig config,
        bool includeSecondary)
    {
        using var bitmap = new Bitmap(videoWidth, videoHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Scale font size proportionally to video height (base 1080p)
            float scaleFactor = videoHeight / 1080f;
            float mainFontSize = Math.Max(18f, config.FontSize * scaleFactor);
            float subFontSize = Math.Max(14f, (config.FontSize * 0.72f) * scaleFactor);
            float outlineThickness = Math.Max(1f, (float)config.OutlineWidth * scaleFactor);
            float shadowOffset = (float)config.ShadowDepth * scaleFactor;

            Color primaryColor = ParseColor(config.PrimaryColor, Color.White);
            Color secondaryColor = ParseColor(config.SecondaryColor, Color.Gold);
            Color outlineColor = ParseColor(config.OutlineColor, Color.Black);
            Color shadowColor = ParseColor(config.ShadowColor, Color.FromArgb(160, 0, 0, 0));

            string fontFamily = string.IsNullOrWhiteSpace(config.FontName) ? "Microsoft YaHei" : config.FontName;
            Font? mainFont = null;
            Font? subFont = null;

            try
            {
                mainFont = new Font(fontFamily, mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch
            {
                mainFont = new Font(FontFamily.GenericSansSerif, mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            try
            {
                subFont = new Font(fontFamily, subFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                subFont = new Font(FontFamily.GenericSansSerif, subFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }

            using (mainFont)
            using (subFont)
            {
                bool hasSub = includeSecondary &&
                              !string.IsNullOrWhiteSpace(item.SourceText);

                // Compute layout
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Far
                };

                float marginV = config.MarginV * scaleFactor;
                float bottomY = videoHeight - marginV;

                var mainSize = g.MeasureString(item.TargetText, mainFont, videoWidth - 100);
                var subSize = hasSub ? g.MeasureString(item.SourceText, subFont, videoWidth - 100) : SizeF.Empty;

                float totalHeight = mainSize.Height + (hasSub ? subSize.Height + (4 * scaleFactor) : 0);
                float startY = item.IsTopTrack ? marginV : (bottomY - totalHeight);

                // Draw Main Text
                var mainRect = new RectangleF(50, startY, videoWidth - 100, mainSize.Height);
                DrawTextWithOutline(g, item.TargetText, mainFont, mainRect, primaryColor, outlineColor, shadowColor, outlineThickness, shadowOffset, sf);

                // Draw Sub Text if present
                if (hasSub)
                {
                    float subY = startY + mainSize.Height + (4 * scaleFactor);
                    var subRect = new RectangleF(50, subY, videoWidth - 100, subSize.Height);
                    DrawTextWithOutline(g, item.SourceText, subFont, subRect, secondaryColor, outlineColor, shadowColor, outlineThickness * 0.8f, shadowOffset * 0.8f, sf);
                }
            }
        }

        bitmap.Save(outputPath, ImageFormat.Png);
    }

    private void DrawTextWithOutline(
        Graphics g,
        string text,
        Font font,
        RectangleF rect,
        Color textColor,
        Color outlineColor,
        Color shadowColor,
        float outlineWidth,
        float shadowOffset,
        StringFormat sf)
    {
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, rect, sf);

        // Draw shadow
        if (shadowOffset > 0)
        {
            using var shadowMatrix = new Matrix();
            shadowMatrix.Translate(shadowOffset, shadowOffset);
            using var shadowPath = (GraphicsPath)path.Clone();
            shadowPath.Transform(shadowMatrix);

            using var shadowBrush = new SolidBrush(shadowColor);
            g.FillPath(shadowBrush, shadowPath);
        }

        // Draw outline
        if (outlineWidth > 0)
        {
            using var pen = new Pen(outlineColor, outlineWidth * 2)
            {
                LineJoin = LineJoin.Round
            };
            g.DrawPath(pen, path);
        }

        // Draw fill
        using var textBrush = new SolidBrush(textColor);
        g.FillPath(textBrush, path);
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            if (hex.Length == 8)
            {
                int a = Convert.ToInt32(hex.Substring(0, 2), 16);
                int r = Convert.ToInt32(hex.Substring(2, 2), 16);
                int g = Convert.ToInt32(hex.Substring(4, 2), 16);
                int b = Convert.ToInt32(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return fallback;
    }
}
