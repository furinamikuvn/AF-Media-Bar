using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Services.Audio;
using AFMediaBar.Classes.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>
/// One-Shot AI 歌声监听协调器：
/// 当检测到变速候选曲目且原曲含有前奏时，仅在播放起始的 2-3 秒内通过 WASAPI Loopback
/// 瞬态侦听音频流；Silero VAD 判定出结果后立即彻底休眠断开，确保 0% 持续 CPU 占用。
/// </summary>
public static class OneShotAiListener
{
    private static readonly LruCache<string, bool> _decisionCache = new(128);
    private static readonly object _inspectionLock = new();
    private static string? _activeInspectionKey;
    private static CancellationTokenSource? _activeCts;
    private static AudioMonitorService? _customAudioMonitor;

    /// <summary>
    /// 设置测试专用的 AudioMonitorService 实例（用于单元测试）。
    /// </summary>
    public static void SetAudioMonitorForTesting(AudioMonitorService? audioMonitor)
    {
        _customAudioMonitor = audioMonitor;
    }

    /// <summary>
    /// 设置测试覆盖结果（用于确定性单元测试）。
    /// </summary>
    public static void SetIntroTrimmedOverride(string title, string artist, double duration, bool isTrimmed)
    {
        var key = BuildKey(title, artist, duration);
        _decisionCache.Set(key, isTrimmed);
    }

    /// <summary>
    /// 清空决策缓存。
    /// </summary>
    public static void ClearCache()
    {
        lock (_inspectionLock)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
            _activeInspectionKey = null;
        }
        _decisionCache.Clear();
    }

    /// <summary>
    /// 查询当前媒体快照对应的视频是否在开头裁切掉了前奏。
    /// </summary>
    public static bool IsIntroTrimmed(MediaSnapshot snapshot, double firstLineStart)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 原曲前奏不足 5 秒，无需判定前奏裁切
        if (firstLineStart < 5.0)
        {
            return false;
        }

        var key = BuildKey(snapshot.Title ?? string.Empty, snapshot.Artist ?? string.Empty, snapshot.Duration);
        if (_decisionCache.TryGetValue(key, out var isTrimmed))
        {
            return isTrimmed;
        }

        // 仅在曲目起始阶段（<= 5 秒）且处于播放中时触发瞬态 AI 侦听
        var position = snapshot.Position;
        if (position <= 5.0 && snapshot.IsPlaying)
        {
            TryStartInspection(key);
        }

        return false;
    }

    private static string BuildKey(string title, string artist, double duration) =>
        $"{title}\t{artist}\t{duration:F1}";

    private static void TryStartInspection(string key)
    {
        lock (_inspectionLock)
        {
            if (_activeInspectionKey == key || _decisionCache.TryGetValue(key, out _))
            {
                return;
            }

            _activeCts?.Cancel();
            _activeCts?.Dispose();

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6.0));
            _activeCts = cts;
            _activeInspectionKey = key;

            AppLogService.Current?.Info("Lyrics", $"[OneShotAI] Launching inspection task for: {key}");
            _ = Task.Run(() => RunInspectionAsync(key, cts), cts.Token);
        }
    }

    private static async Task RunInspectionAsync(string key, CancellationTokenSource cts)
    {
        AudioMonitorService? audioMonitor = _customAudioMonitor;
        if (audioMonitor == null)
        {
            try
            {
                audioMonitor = App.Services?.GetService<AudioMonitorService>();
            }
            catch (Exception ex)
            {
                AppLogService.Current?.Warn("Lyrics", $"[OneShotAI] Failed to get AudioMonitorService: {ex.Message}");
            }
        }

        if (audioMonitor == null)
        {
            AppLogService.Current?.Warn("Lyrics", $"[OneShotAI] AudioMonitorService is null! Fallback decision=false for {key}");
            _decisionCache.Set(key, false);
            return;
        }

        AppLogService.Current?.Info("Lyrics", $"[OneShotAI] Starting inspection for: {key}");
        VocalActivityDetector? detector = null;
        var finished = 0;

        void Finish(bool result, string reason)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0)
            {
                return;
            }

            AppLogService.Current?.Info("Lyrics", $"[OneShotAI] Inspection finished: result={result}, reason='{reason}' for {key}");
            
            // 只有当确凿检测到人声，或者已经真正处理了足量有效音频(>= 3.5秒)时，才持久化缓存结果；
            // 避免因播放暂停、未出声等原因导致早熟的 false 误判封死后续判定。
            if (result || (detector != null && detector.ProcessedAudioSeconds >= 3.5))
            {
                _decisionCache.Set(key, result);
            }

            try
            {
                detector?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OneShotAiListener] Detector dispose failed: {ex.Message}");
            }

            lock (_inspectionLock)
            {
                if (_activeInspectionKey == key)
                {
                    _activeInspectionKey = null;
                }
            }

            try
            {
                cts.Cancel();
            }
            catch { }
        }

        Action<float[], int, int> consumer = (buffer, count, sampleRate) =>
        {
            if (cts.IsCancellationRequested || finished != 0 || detector == null)
            {
                return;
            }

            try
            {
                detector.ProcessAudio(buffer.AsSpan(0, count), sampleRate);
                if (detector.HasDetectedVocal)
                {
                    Finish(true, $"Vocal detected (chunks={detector.TotalVocalChunks}, maxConsecutive={detector.MaxConsecutiveVocalChunks}, processed={detector.ProcessedAudioSeconds:F2}s)");
                }
                else if (detector.ProcessedAudioSeconds >= 4.0)
                {
                    Finish(false, $"4.0s processed without vocal (maxConsecutive={detector.MaxConsecutiveVocalChunks})");
                }
            }
            catch (Exception ex)
            {
                AppLogService.Current?.Warn("Lyrics", $"[OneShotAI] Inference failed: {ex.Message}");
                Finish(false, $"Inference exception: {ex.Message}");
            }
        };

        try
        {
            AppLogService.Current?.Info("Lyrics", $"[OneShotAI] Initializing detector and attaching audio consumer...");
            detector = new VocalActivityDetector();
            audioMonitor.AddAudioConsumer(consumer);

            // 保持侦听直至判定完成或 5.5 秒超时
            var checkInterval = TimeSpan.FromMilliseconds(100);
            var elapsed = TimeSpan.Zero;
            var maxWait = TimeSpan.FromSeconds(5.5);

            while (!cts.IsCancellationRequested && finished == 0 && elapsed < maxWait)
            {
                await Task.Delay(checkInterval, cts.Token).ConfigureAwait(false);
                elapsed += checkInterval;
            }

            if (finished == 0)
            {
                Finish(false, $"Timeout waiting for audio (elapsed={elapsed.TotalSeconds:F1}s, processedAudio={detector?.ProcessedAudioSeconds:F1}s)");
            }
        }
        catch (OperationCanceledException)
        {
            if (finished == 0)
            {
                Finish(false, "Operation canceled");
            }
        }
        catch (Exception ex)
        {
            AppLogService.Current?.Error("Lyrics", $"[OneShotAI] Session error: {ex.Message}", ex);
            Finish(false, $"Session exception: {ex.Message}");
        }
        finally
        {
            audioMonitor.RemoveAudioConsumer(consumer);
        }
    }
}
