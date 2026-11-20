using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public sealed record VadAlignmentResult(
    int ModifiedCount,
    int ForwardCount,
    int BackwardCount,
    double MedianShiftMs,
    double InferenceSeconds);

/// <summary>
/// Local Silero-VAD ONNX service that refines early subtitle onset timestamps after Stage 3 transcription.
/// </summary>
public sealed class VadAlignmentService
{
    private const int SampleRate = 16000;
    private const int ChunkSamples = 512;      // 32ms per frame at 16kHz
    private const int ContextSamples = 64;     // Silero-VAD v5 context window
    private const double FrameSeconds = ChunkSamples / (double)SampleRate; // 0.032s

    private const float SilenceThreshold = 0.25f;
    private const float SpeechThreshold = 0.50f;
    private const int SilencePrefixFrames = 4; // 128ms pre-onset quiet window
    private const int SpeechWindowFrames = 6;  // 192ms onset confirmation window
    private const int SpeechMinActiveFrames = 5;

    private const double SearchBackwardSeconds = 0.20;
    private const double SearchForwardSeconds = 1.20;
    private const double MinRemainingDurationSeconds = 0.50;
    private const double PreRollSeconds = 0.15;
    private const double DeadbandSeconds = 0.12;
    private const double OverlapToleranceSeconds = 0.05;

    private static VadAlignmentService? _instance;
    public static VadAlignmentService Instance => _instance ??= new VadAlignmentService();

    private readonly object _sessionLock = new();
    private InferenceSession? _session;
    private string? _loadedModelPath;
    private bool _srIsScalar;

    private VadAlignmentService() { }

    public string? GetModelPath()
    {
        string assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        if (File.Exists(assetsPath)) return assetsPath;

        string rootPath = Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx");
        if (File.Exists(rootPath)) return rootPath;

        return null;
    }

    public async Task<VadAlignmentResult?> RefineOnsetsAsync(
        string videoPath,
        IList<SubtitleItem> subtitles,
        CancellationToken cancellationToken)
    {
        if (subtitles == null || subtitles.Count == 0 || string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return null;
        }

        string? ffmpegPath = FFmpegService.Instance.GetFFmpegPath();
        string? modelPath = GetModelPath();
        if (string.IsNullOrEmpty(ffmpegPath) || string.IsNullOrEmpty(modelPath))
        {
            AppLogService.Instance.LogInfo("[VAD对齐] 未找到 ffmpeg 或 silero_vad.onnx，已跳过本地起点精修。");
            return null;
        }

        var sw = Stopwatch.StartNew();

        byte[] pcmBytes = await DecodeMonoFloat32PcmAsync(ffmpegPath, videoPath, cancellationToken);
        if (pcmBytes.Length < ChunkSamples * sizeof(float))
        {
            return null;
        }

        float[] probs = await Task.Run(() => RunSileroVadInference(modelPath, pcmBytes, cancellationToken), cancellationToken);
        if (probs.Length < SilencePrefixFrames + SpeechWindowFrames)
        {
            return null;
        }

        var result = ApplyGuardrailedOnsetAlignment(subtitles, probs);
        sw.Stop();

        return result with { InferenceSeconds = sw.Elapsed.TotalSeconds };
    }

    private InferenceSession EnsureSession(string modelPath)
    {
        lock (_sessionLock)
        {
            if (_session != null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return _session;
            }

            _session?.Dispose();
            var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            _session = new InferenceSession(modelPath, options);
            _loadedModelPath = modelPath;
            _srIsScalar = _session.InputMetadata.TryGetValue("sr", out var meta) && meta.Dimensions.Length == 0;
            return _session;
        }
    }

    private float[] RunSileroVadInference(string modelPath, byte[] pcmBytes, CancellationToken cancellationToken)
    {
        InferenceSession session = EnsureSession(modelPath);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(pcmBytes.AsSpan());
        int totalChunks = samples.Length / ChunkSamples;
        var probs = new float[totalChunks];

        var inputBuffer = new float[ContextSamples + ChunkSamples];
        var stateBuffer = new float[2 * 1 * 128];
        var srBuffer = new long[] { SampleRate };

        var inputTensor = new DenseTensor<float>(inputBuffer, new[] { 1, ContextSamples + ChunkSamples });
        var stateTensor = new DenseTensor<float>(stateBuffer, new[] { 2, 1, 128 });
        var srTensor = _srIsScalar
            ? new DenseTensor<long>(srBuffer, ReadOnlySpan<int>.Empty)
            : new DenseTensor<long>(srBuffer, new[] { 1 });

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor)
        };

        for (int i = 0; i < totalChunks; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            samples.Slice(i * ChunkSamples, ChunkSamples).CopyTo(inputBuffer.AsSpan(ContextSamples, ChunkSamples));

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(inputs);
            probs[i] = outputs[0].AsEnumerable<float>().First();

            Tensor<float> nextState = outputs[1].AsTensor<float>();
            if (nextState is DenseTensor<float> denseNext)
            {
                denseNext.Buffer.Span.CopyTo(stateBuffer);
            }
            else
            {
                int idx = 0;
                foreach (float v in nextState)
                {
                    stateBuffer[idx++] = v;
                }
            }

            // Preserve last 64 samples as context for the next chunk
            Array.Copy(inputBuffer, ChunkSamples, inputBuffer, 0, ContextSamples);
        }

        return probs;
    }

    private static VadAlignmentResult ApplyGuardrailedOnsetAlignment(IList<SubtitleItem> subtitles, float[] probs)
    {
        var orderedMain = subtitles
            .Where(s => s.IsPrimary != false)
            .OrderBy(s => s.StartTime)
            .ToList();

        var shiftsMs = new List<double>();
        int forwardCount = 0;
        int backwardCount = 0;
        TimeSpan prevMainEnd = TimeSpan.Zero;

        for (int pos = 0; pos < orderedMain.Count; pos++)
        {
            var item = orderedMain[pos];

            // Skip entries overlapping an earlier main-track utterance (secondary simultaneous speech)
            if (item.StartTime < prevMainEnd - TimeSpan.FromSeconds(OverlapToleranceSeconds))
            {
                if (item.EndTime > prevMainEnd) prevMainEnd = item.EndTime;
                continue;
            }

            TimeSpan nextMainStart = TimeSpan.MaxValue;
            for (int nxt = pos + 1; nxt < orderedMain.Count; nxt++)
            {
                if (orderedMain[nxt].StartTime >= item.EndTime - TimeSpan.FromSeconds(OverlapToleranceSeconds))
                {
                    nextMainStart = orderedMain[nxt].StartTime;
                    break;
                }
            }

            TimeSpan winLo = item.StartTime - TimeSpan.FromSeconds(SearchBackwardSeconds);
            if (winLo < prevMainEnd) winLo = prevMainEnd;

            TimeSpan maxByForward = item.StartTime + TimeSpan.FromSeconds(SearchForwardSeconds);
            TimeSpan maxByDuration = item.EndTime - TimeSpan.FromSeconds(MinRemainingDurationSeconds);
            TimeSpan winHi = maxByForward < maxByDuration ? maxByForward : maxByDuration;
            if (nextMainStart < winHi) winHi = nextMainStart;

            if (winHi > winLo)
            {
                int i0 = Math.Max(SilencePrefixFrames, (int)Math.Ceiling(winLo.TotalSeconds / FrameSeconds));
                int i1 = Math.Min(probs.Length - SpeechWindowFrames, (int)Math.Floor(winHi.TotalSeconds / FrameSeconds));

                for (int i = i0; i <= i1; i++)
                {
                    if (!IsSilenceToSpeechTransition(probs, i)) continue;

                    double vadOnsetSec = i * FrameSeconds;
                    TimeSpan candidateStart = TimeSpan.FromSeconds(Math.Max(0.0, vadOnsetSec - PreRollSeconds));
                    if (candidateStart < prevMainEnd)
                    {
                        candidateStart = prevMainEnd;
                    }

                    double deltaSec = (candidateStart - item.StartTime).TotalSeconds;
                    if (Math.Abs(deltaSec) >= DeadbandSeconds)
                    {
                        item.StartTime = candidateStart;
                        shiftsMs.Add(deltaSec * 1000.0);
                        if (deltaSec > 0) forwardCount++;
                        else backwardCount++;
                    }
                    break;
                }
            }

            if (item.EndTime > prevMainEnd) prevMainEnd = item.EndTime;
        }

        double medianMs = 0;
        if (shiftsMs.Count > 0)
        {
            shiftsMs.Sort();
            int mid = shiftsMs.Count / 2;
            medianMs = (shiftsMs.Count % 2 == 1)
                ? shiftsMs[mid]
                : (shiftsMs[mid - 1] + shiftsMs[mid]) / 2.0;
        }

        return new VadAlignmentResult(
            ModifiedCount: shiftsMs.Count,
            ForwardCount: forwardCount,
            BackwardCount: backwardCount,
            MedianShiftMs: medianMs,
            InferenceSeconds: 0);
    }

    private static bool IsSilenceToSpeechTransition(float[] probs, int idx)
    {
        for (int p = idx - SilencePrefixFrames; p < idx; p++)
        {
            if (probs[p] >= SilenceThreshold) return false;
        }

        int active = 0;
        for (int s = idx; s < idx + SpeechWindowFrames; s++)
        {
            if (probs[s] >= SpeechThreshold) active++;
        }

        return active >= SpeechMinActiveFrames;
    }

    private static async Task<byte[]> DecodeMonoFloat32PcmAsync(
        string ffmpegPath,
        string videoPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-v error -i \"{videoPath}\" -vn -ac 1 -ar {SampleRate} -f f32le -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for VAD PCM extraction.");

        using var reg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        using var ms = new MemoryStream();
        Task stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stderrTask;

        return process.ExitCode == 0 ? ms.ToArray() : Array.Empty<byte>();
    }
}
