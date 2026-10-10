using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using AFMediaBar.Classes.Services;

namespace AFMediaBar.Classes.Services.Audio;

/// <summary>
/// 基于 Silero VAD v5 神经网络的超轻量瞬态歌声活动检测器。
/// 仅需极低算力（~0.1ms/chunk），将实时音频重采样为 16kHz 并在首 2-3 秒内判定是否已开唱。
/// </summary>
public sealed class VocalActivityDetector : IDisposable
{
    private const int VadSampleRate = 16000;
    private const int ChunkSize = 512;
    private const float VocalProbabilityThreshold = 0.28f;
    private const float HighConfidenceVocalThreshold = 0.55f;

    private static byte[]? _cachedModelBytes;
    private static readonly object _modelLock = new();

    private readonly InferenceSession _session;
    private readonly float[] _chunkBuffer = new float[ChunkSize];
    private readonly float[] _normalizedChunk = new float[ChunkSize];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly long[] _sr = new long[] { VadSampleRate };
    private int _chunkCount;
    private bool _hasHighConfidenceVocal;

    // 重采样状态
    private double _resamplePos;
    private float _lastSample;
    private bool _hasLastSample;
    private bool _disposed;

    public int TotalChunksProcessed { get; private set; }
    public int TotalVocalChunks { get; private set; }
    public int ConsecutiveVocalChunks { get; private set; }
    public int MaxConsecutiveVocalChunks { get; private set; }

    /// <summary>
    /// 是否已确凿检测到人声开唱（连续 >= 2 个 chunk 或累计 >= 3 个 chunk 或单次高置信度 >= 0.55）。
    /// </summary>
    public bool HasDetectedVocal => MaxConsecutiveVocalChunks >= 2 || TotalVocalChunks >= 3 || _hasHighConfidenceVocal;

    /// <summary>
    /// 已处理的等效音频时长（秒）。
    /// </summary>
    public double ProcessedAudioSeconds => (double)(TotalChunksProcessed * ChunkSize) / VadSampleRate;

    public VocalActivityDetector()
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        _session = new InferenceSession(GetModelBytes(), options);
    }

    public static byte[] GetModelBytes()
    {
        if (_cachedModelBytes != null)
        {
            return _cachedModelBytes;
        }

        lock (_modelLock)
        {
            if (_cachedModelBytes != null)
            {
                return _cachedModelBytes;
            }

            var diskPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "silero_vad.onnx");
            if (File.Exists(diskPath))
            {
                _cachedModelBytes = File.ReadAllBytes(diskPath);
                return _cachedModelBytes;
            }

            var asm = typeof(VocalActivityDetector).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("silero_vad.onnx", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    _cachedModelBytes = ms.ToArray();
                    return _cachedModelBytes;
                }
            }

            throw new FileNotFoundException("silero_vad.onnx model could not be found on disk or in embedded resources.");
        }
    }

    /// <summary>
    /// 输入单声道 PCM 样本块并执行流式 VAD 检测。
    /// </summary>
    public void ProcessAudio(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (_disposed || samples.IsEmpty || sampleRate <= 0)
        {
            return;
        }

        if (sampleRate == VadSampleRate)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                _chunkBuffer[_chunkCount++] = samples[i];
                if (_chunkCount == ChunkSize)
                {
                    ProcessChunk();
                    _chunkCount = 0;
                }
            }
            return;
        }

        // 线性重采样至 16000Hz
        var ratio = (double)sampleRate / VadSampleRate;
        var count = samples.Length;

        while (true)
        {
            var idx = (int)Math.Floor(_resamplePos);
            if (idx >= count - 1)
            {
                _resamplePos -= (count - 1);
                _lastSample = samples[count - 1];
                _hasLastSample = true;
                break;
            }

            var frac = (float)(_resamplePos - idx);
            var s0 = idx < 0 ? (_hasLastSample ? _lastSample : samples[0]) : samples[idx];
            var s1 = samples[idx + 1];
            var resampled = s0 * (1f - frac) + s1 * frac;

            _chunkBuffer[_chunkCount++] = resampled;
            if (_chunkCount == ChunkSize)
            {
                ProcessChunk();
                _chunkCount = 0;
            }

            _resamplePos += ratio;
        }
    }

    private void ProcessChunk()
    {
        if (_disposed)
        {
            return;
        }

        // 自动软增益归一化（AGC）：防止系统或播放器音量过低导致神经网络被抑制
        var maxAbs = 0f;
        for (var i = 0; i < ChunkSize; i++)
        {
            var abs = Math.Abs(_chunkBuffer[i]);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        var gain = 1.0f;
        if (maxAbs > 0.005f && maxAbs < 0.35f)
        {
            gain = Math.Min(0.35f / maxAbs, 8.0f);
        }

        for (var i = 0; i < ChunkSize; i++)
        {
            _normalizedChunk[i] = Math.Clamp(_chunkBuffer[i] * gain, -1.0f, 1.0f);
        }

        var inputTensor = new DenseTensor<float>(_normalizedChunk.AsMemory(), new[] { 1, ChunkSize });
        var stateTensor = new DenseTensor<float>(_state.AsMemory(), new[] { 2, 1, 128 });
        var srTensor = new DenseTensor<long>(_sr.AsMemory(), Array.Empty<int>());

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor)
        };

        using var results = _session.Run(inputs);
        var outputTensor = results.First(r => r.Name == "output").AsTensor<float>();
        var prob = outputTensor.GetValue(0);

        if (results.First(r => r.Name == "stateN").AsTensor<float>() is DenseTensor<float> stateNTensor)
        {
            stateNTensor.Buffer.Span.CopyTo(_state.AsSpan());
        }

        TotalChunksProcessed++;
        if (prob >= HighConfidenceVocalThreshold)
        {
            _hasHighConfidenceVocal = true;
        }

        if (prob >= VocalProbabilityThreshold)
        {
            ConsecutiveVocalChunks++;
            TotalVocalChunks++;
            if (ConsecutiveVocalChunks > MaxConsecutiveVocalChunks)
            {
                MaxConsecutiveVocalChunks = ConsecutiveVocalChunks;
            }
        }
        else
        {
            ConsecutiveVocalChunks = 0;
        }

        if (TotalChunksProcessed % 10 == 0 || prob >= 0.20f)
        {
            AppLogService.Current?.Info("Lyrics", $"[OneShotAI] Chunk #{TotalChunksProcessed} ({(TotalChunksProcessed * ChunkSize) / 16000.0:F2}s, gain={gain:F1}x): prob={prob:F3}, consecutive={ConsecutiveVocalChunks}, max={MaxConsecutiveVocalChunks}, totalVocal={TotalVocalChunks}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}
