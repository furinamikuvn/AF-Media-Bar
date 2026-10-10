// Owns demand-driven NAudio loopback capture on a hosted worker; callbacks only update samples under the gate.
// Host shutdown awaits the worker, which disposes capture before its endpoint. UI callers never touch native audio resources.
using System.Diagnostics;
using AFMediaBar.Classes.Abstractions;
using AFMediaBar.Classes.Settings;
using AFMediaBar.Classes.Services.Audio;
using Microsoft.Extensions.Hosting;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 后台回环采集与频谱分析；由宿主等待采集停止，界面仅读取缓存样本。
/// Background loopback capture and spectrum analysis; the host awaits capture shutdown and UI paths read cached samples only.
/// </summary>
public sealed class AudioMonitorService : BackgroundService, IMemoryPrunable
{
    private const int MinimumRingSize = 4_096;
    private readonly AudioCaptureDeviceResolver _deviceResolver;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _demand = new(0, 1);
    private int _wakePending;
    private long _lastDemandTick = long.MinValue;
    private int _captureVersion;
    private bool _disposed;
    private bool _disposeCalled;
    private bool _captureReady;
    private MemoryPruneLevel _pruneLevel;
    private int _sampleWriteIndex;
    private int _sampleCount;
    private int _sampleRate;
    private long _lastPacketTick;
    private double _spectrumReference;
    private long _lastSpectrumTick;
    // FFT 相关缓冲区随采样率确定点数后再分配（96 kHz 至少要 4096 点才能把 45–206 Hz 的前几段分开），
    // 因此它们不是 readonly 的固定数组；ConfigureFft 在拿到混音格式后统一重建。
    // The FFT buffers are allocated once the sample rate fixes the size (96 kHz needs at least 4096 points to separate the first bands
    // between 45 and 206 Hz), so they are not fixed readonly arrays; ConfigureFft rebuilds them after the mix format is known.
    private float[] _sampleRing = new float[MinimumRingSize];
    private double[] _fftReal = new double[SpectrumAnalysisPolicy.MinimumFftSize];
    private double[] _fftImaginary = new double[SpectrumAnalysisPolicy.MinimumFftSize];
    private double[] _fftWindow = CreateFftWindow(SpectrumAnalysisPolicy.MinimumFftSize);
    private double[] _powerSpectrum = new double[SpectrumAnalysisPolicy.MinimumFftSize / 2 + 1];
    private int _fftSize = SpectrumAnalysisPolicy.MinimumFftSize;
    private int _ringSize = MinimumRingSize;
    // 频段边界只随柱数变化，因此按柱数缓存；采样线程每帧都要读它，不能每帧重新计算等比数列。
    // Band edges change only with the bar count, so they are cached per count: the sampling thread reads them every frame
    // and must not rebuild the geometric progression each time.
    private float[] _bandEdges = SpectrumBandPolicy.CreateBandEdges(SpectrumComponentSettings.DefaultBandCount);
    private int _bandEdgeCount = SpectrumComponentSettings.DefaultBandCount;
    // 频段幅度先整体算完再映射：相对 dB 窗口需要一个"本次采样的最大幅度"作为参考更新的输入。
    // Band magnitudes are computed in full before mapping: the relative dB window needs this sample's largest magnitude to advance the reference.
    private readonly double[] _bandMagnitudes = new double[SpectrumComponentSettings.MaximumBandCount];

    /// <summary>创建由宿主控制生命周期的频谱服务。 / Creates the host-managed spectrum service.</summary>
    public AudioMonitorService(AudioCaptureDeviceResolver deviceResolver) => _deviceResolver = deviceResolver;

    /// <summary>
    /// 在后台计算频谱并把结果交给 UI；UI 线程不等待采样回调持有的锁或 FFT。
    /// Computes the spectrum in the background so the UI never waits on the capture callback's gate or FFT.
    /// </summary>
    public Task<bool> GetSpectrumAsync(float[] bands, int bandCount) =>
        Task.Run(() => GetSpectrum(bands, bandCount));

    /// <summary>从缓存样本填充频谱；不会等待音频设备或后台采集。 / Fills bands from cached samples without waiting on audio devices.</summary>
    public bool GetSpectrum(float[] bands, int bandCount)
    {
        var count = SpectrumBandPolicy.ClampBandCount(bandCount);
        if (bands.Length < count)
            throw new ArgumentException($"At least {count} bands are required.", nameof(bands));
        lock (_gate)
        {
            Array.Clear(bands, 0, count);
            if (_disposed || _pruneLevel >= MemoryPruneLevel.DisplayOff)
                return false;
            _lastDemandTick = Environment.TickCount64;
            _deviceResolver.RequestScan();
            WakeWorker();
            if (!_captureReady)
                return false;
            if (_sampleCount < _fftSize || Environment.TickCount64 - _lastPacketTick > 180)
            {
                var now = Environment.TickCount64;
                var elapsed = _lastSpectrumTick == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(now - _lastSpectrumTick);
                _lastSpectrumTick = now;
                _spectrumReference = SpectrumLevelPolicy.UpdateReference(_spectrumReference, 0, elapsed);
                return true;
            }
            CalculateSpectrum(bands, count);
            return true;
        }
    }

    /// <summary>使旧采集失效，由后台释放并在需要时重建。 / Invalidates capture for background disposal and demand-driven recreation.</summary>
    public void ResetAfterEnvironmentChange()
    {
        lock (_gate)
        {
            if (_disposed) return;
            InvalidateCapture();
            WakeWorker();
        }
    }

    /// <summary>参与者名称。 / Diagnostic participant name.</summary>
    public string PruneParticipantName => "audio-capture";

    /// <summary>息屏时停止采集；恢复后等界面再次请求。 / Stops capture with the display off and waits for fresh demand after resuming.</summary>
    public void Prune(MemoryPruneLevel level)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pruneLevel = level;
            if (level >= MemoryPruneLevel.DisplayOff)
            {
                _lastDemandTick = long.MinValue;
                InvalidateCapture();
            }
            WakeWorker();
        }
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _disposed = true;
            InvalidateCapture();
        }
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposeCalled) return;
            _disposeCalled = true;
            _disposed = true;
            InvalidateCapture();
        }
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int version;
                lock (_gate) version = _captureVersion;
                if (!HasDemand(version))
                {
                    await _demand.WaitAsync(stoppingToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _wakePending, 0);
                    continue;
                }
                try
                {
                    await CaptureAsync(version, stoppingToken).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[AudioMonitorService] Capture failed: {exception.Message}");
                    failures = Math.Min(failures + 1, 4);
                    var delay = failures switch { 1 => 100, 2 => 500, 3 => 1000, _ => 3000 };
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                _captureReady = false;
                ClearSamples();
            }
            // Dispose/StopAsync invalidate all publishers before this worker releases its signal.
            _demand.Dispose();
        }
    }

    private async Task CaptureAsync(int version, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        var target = _deviceResolver.TargetDeviceId;
        using var device = string.IsNullOrEmpty(target)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(target);
        using var capture = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().Build();
        var format = capture.WaveFormat;
        var reader = new LoopbackSampleReader(format, PushSample);
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceId = device.ID;
        lock (_gate)
        {
            if (!HasDemandCore(version)) return;
            _sampleRate = format.SampleRate;
            ConfigureFft(_sampleRate);
            ClearSamples();
            _captureReady = true;
        }
        CaptureDataAvailableHandler onData = (buffer, _, _, _) =>
        {
            lock (_gate)
            {
                if (!HasDemandCore(version) || !_captureReady) return;
                reader.Append(buffer);
                _lastPacketTick = Environment.TickCount64;
            }
        };
        EventHandler<StoppedEventArgs> onStopped = (_, args) => stopped.TrySetResult(args.Exception);
        capture.DataAvailable += onData;
        capture.RecordingStopped += onStopped;
        try
        {
            capture.StartRecording();
            while (HasDemand(version) && !token.IsCancellationRequested)
            {
                var nextTarget = _deviceResolver.TargetDeviceId;
                if (!string.IsNullOrEmpty(nextTarget) && !string.Equals(nextTarget, deviceId, StringComparison.OrdinalIgnoreCase))
                    break;
                if (stopped.Task.IsCompleted)
                    throw await stopped.Task.ConfigureAwait(false) ?? new InvalidOperationException("Loopback capture stopped unexpectedly.");
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _captureReady = false;
                ClearSamples();
            }
            capture.DataAvailable -= onData;
            capture.RecordingStopped -= onStopped;
            // Disposal joins NAudio's capture thread, outside the sample gate and off the UI thread.
        }
    }

    private bool HasDemand(int version)
    {
        lock (_gate) return HasDemandCore(version);
    }

    private bool HasDemandCore(int version) => !_disposed && version == _captureVersion &&
        _pruneLevel < MemoryPruneLevel.DisplayOff && _lastDemandTick != long.MinValue &&
        Environment.TickCount64 - _lastDemandTick <= 1000;

    private void WakeWorker()
    {
        // Capture requests can arrive every frame. One queued wake-up is sufficient;
        // coalescing before Release avoids a caught first-chance exception on every frame.
        if (Interlocked.Exchange(ref _wakePending, 1) != 0)
            return;
        try { _demand.Release(); }
        catch (ObjectDisposedException) { }
    }

    private void InvalidateCapture()
    {
        _captureVersion++;
        _captureReady = false;
        ClearSamples();
    }

    private void ClearSamples()
    {
        _sampleWriteIndex = 0;
        _sampleCount = 0;
        _lastPacketTick = 0;
        _spectrumReference = 0;
        _lastSpectrumTick = 0;
    }

    /// <summary>
    /// 按采样率重建 FFT 缓冲区（点数为 2 的幂、bin 宽目标见 <see cref="SpectrumAnalysisPolicy.ResolveFftSize"/>）。
    /// 点数不变时不做任何事；重建会同时清空采样环，因为旧样本的点数语义已经不同。
    /// Rebuilds the FFT buffers for a sample rate (the size is a power of two; see <see cref="SpectrumAnalysisPolicy.ResolveFftSize"/>
    /// for the bin-width target). An unchanged size does nothing; a rebuild also clears the sample ring, because old samples belong
    /// to a different size.
    /// </summary>
    private void ConfigureFft(int sampleRate)
    {
        var fftSize = SpectrumAnalysisPolicy.ResolveFftSize(sampleRate);
        if (fftSize == _fftSize)
        {
            return;
        }

        _fftSize = fftSize;
        _ringSize = Math.Max(MinimumRingSize, fftSize * 2);
        _sampleRing = new float[_ringSize];
        _fftReal = new double[_fftSize];
        _fftImaginary = new double[_fftSize];
        _fftWindow = CreateFftWindow(_fftSize);
        _powerSpectrum = new double[_fftSize / 2 + 1];
        _sampleWriteIndex = 0;
        _sampleCount = 0;
    }

    private void PushSample(float sample)
    {
        _sampleRing[_sampleWriteIndex] = sample;
        _sampleWriteIndex = (_sampleWriteIndex + 1) % _ringSize;
        _sampleCount = Math.Min(_sampleCount + 1, _ringSize);
    }

    private void CalculateSpectrum(float[] bands, int bandCount)
    {
        var start = (_sampleWriteIndex - _fftSize + _ringSize) % _ringSize;
        for (var index = 0; index < _fftSize; index++)
        {
            _fftReal[index] = _sampleRing[(start + index) % _ringSize] * _fftWindow[index];
            _fftImaginary[index] = 0;
        }

        TransformFft();
        var binWidth = _sampleRate / (double)_fftSize;
        var nyquistBin = _fftSize / 2 - 1;

        // 先展成功率谱：频段积分在功率域做，再开方取回幅度，比"取频段内最大 bin"平滑得多，
        // 也不会因为频段比一个 bin 还窄而照抄同一个值。
        // First build the power spectrum: bands integrate in the power domain and take the square root afterwards, which is far
        // smoother than "largest bin in the band" and never copies one bin's value into a band narrower than a bin.
        for (var bin = 0; bin <= nyquistBin; bin++)
        {
            _powerSpectrum[bin] = _fftReal[bin] * _fftReal[bin] + _fftImaginary[bin] * _fftImaginary[bin];
        }

        // 与旧的 2/N 幅度归一化保持一致：幅度 = sqrt(功率积分 × 4/N²)。
        // Keeps the old 2/N amplitude normalization: magnitude = sqrt(integrated power × 4/N²).
        var normalization = 4.0 / ((double)_fftSize * _fftSize);
        var edges = ResolveBandEdges(bandCount);
        var peakMagnitude = 0d;
        for (var band = 0; band < bandCount; band++)
        {
            var low = edges[band];
            var high = edges[band + 1];
            var power = SpectrumAnalysisPolicy.IntegrateBandPower(_powerSpectrum, binWidth, low, high);
            // 频段中心取几何中心：对数频段上它才是听觉意义上的"中间"。
            // The band center is the geometric one: on a logarithmic axis that is the perceptual middle.
            var center = Math.Sqrt((double)low * high);
            var magnitude = Math.Sqrt(power * normalization * SpectrumAnalysisPolicy.ResolveTiltPowerGain(center));
            _bandMagnitudes[band] = magnitude;
            peakMagnitude = Math.Max(peakMagnitude, magnitude);
        }

        // 先按真实时间推进参考峰值，再把各频段映射成相对它的 dB 电平；这样显示强弱与系统音量的绝对大小无关。
        // The reference peak is advanced by real time first, then every band is mapped to a level relative to it, so display strength
        // no longer follows the absolute system volume.
        var now = Environment.TickCount64;
        var elapsed = _lastSpectrumTick == 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(now - _lastSpectrumTick);
        _lastSpectrumTick = now;
        _spectrumReference = SpectrumLevelPolicy.UpdateReference(_spectrumReference, peakMagnitude, elapsed);

        for (var band = 0; band < bandCount; band++)
        {
            bands[band] = SpectrumLevelPolicy.ToNormalizedLevel(_bandMagnitudes[band], _spectrumReference);
        }
    }

    /// <summary>返回请求柱数对应的频段边界，柱数变化时重建缓存。 / Returns the band edges for the requested count, rebuilding the cache when the count changes.</summary>
    private float[] ResolveBandEdges(int bandCount)
    {
        if (bandCount == _bandEdgeCount)
            return _bandEdges;

        _bandEdges = SpectrumBandPolicy.CreateBandEdges(bandCount);
        _bandEdgeCount = bandCount;
        return _bandEdges;
    }

    private void TransformFft()
    {
        var target = 0;
        for (var source = 1; source < _fftSize; source++)
        {
            var bit = _fftSize >> 1;
            while ((target & bit) != 0)
            {
                target ^= bit;
                bit >>= 1;
            }

            target ^= bit;
            if (source < target)
            {
                (_fftReal[source], _fftReal[target]) = (_fftReal[target], _fftReal[source]);
                (_fftImaginary[source], _fftImaginary[target]) =
                    (_fftImaginary[target], _fftImaginary[source]);
            }
        }

        for (var length = 2; length <= _fftSize; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);
            for (var offset = 0; offset < _fftSize; offset += length)
            {
                var rotationReal = 1d;
                var rotationImaginary = 0d;
                var halfLength = length >> 1;
                for (var index = 0; index < halfLength; index++)
                {
                    var evenIndex = offset + index;
                    var oddIndex = evenIndex + halfLength;
                    var oddReal = _fftReal[oddIndex] * rotationReal -
                        _fftImaginary[oddIndex] * rotationImaginary;
                    var oddImaginary = _fftReal[oddIndex] * rotationImaginary +
                        _fftImaginary[oddIndex] * rotationReal;
                    var evenReal = _fftReal[evenIndex];
                    var evenImaginary = _fftImaginary[evenIndex];
                    _fftReal[evenIndex] = evenReal + oddReal;
                    _fftImaginary[evenIndex] = evenImaginary + oddImaginary;
                    _fftReal[oddIndex] = evenReal - oddReal;
                    _fftImaginary[oddIndex] = evenImaginary - oddImaginary;

                    var nextRotationReal = rotationReal * stepReal -
                        rotationImaginary * stepImaginary;
                    rotationImaginary = rotationReal * stepImaginary +
                        rotationImaginary * stepReal;
                    rotationReal = nextRotationReal;
                }
            }
        }
    }

    private static double[] CreateFftWindow(int fftSize)
    {
        var window = new double[fftSize];
        for (var index = 0; index < fftSize; index++)
        {
            window[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (fftSize - 1));
        }

        return window;
    }

}
