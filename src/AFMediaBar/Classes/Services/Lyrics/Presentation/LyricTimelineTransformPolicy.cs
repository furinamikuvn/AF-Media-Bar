using System.Text;
using System.Text.RegularExpressions;
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Utils;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>
/// 歌词时间轴自适应变换参数：缩放比例 (Scale) 与偏移秒数 (Offset)。
/// Lyric timeline adaptive transformation parameters: scale factor and offset in seconds.
/// </summary>
public readonly record struct TimelineTransformParams(double Scale, double Offset)
{
    public static TimelineTransformParams Identity => new(1.0, 0.0);
}

/// <summary>
/// 歌词时间轴自适应策略：针对 YouTube 等平台的 Speed Up、Lofi/Slowed 变速及副歌切片视频，
/// 自动将播放时间映射到原始歌词的时间轴上，实现 0% CPU 消耗的全自动无感对齐。
///
/// 对非越南语（中文及其他外语）曲目保持 100% 原始行为不变，仅在标题显式包含倍速标签时介入。
/// </summary>
public static class LyricTimelineTransformPolicy
{
    private static readonly Regex SpeedUpRegex = new(
        @"(?i)\b(speed\s*up|sped\s*up|nightcore|fast\s*ver|1\.[1-9]x|2x)\b",
        RegexOptions.Compiled);

    private static readonly Regex SlowedRegex = new(
        @"(?i)\b(slowed(\s*\+\s*reverb)?|slowed\s*down|lofi|slow\s*ver|0\.[7-9]x)\b",
        RegexOptions.Compiled);

    private static readonly Regex SnippetRegex = new(
        @"(?i)\b(shorts?|tik\s*tok|đoạn\s*hay\s*nhất|điệp\s*khúc|chorus|snippet|short\s*ver)\b",
        RegexOptions.Compiled);

    private static readonly LruCache<string, TimelineTransformParams> _transformCache = new(64);

    /// <summary>
    /// 解析媒体快照对应的有效歌词定位时间。
    /// Resolves the effective position for lyric indexing given the media snapshot.
    /// </summary>
    /// <param name="snapshot">媒体快照 / Media snapshot.</param>
    /// <param name="rawPosition">播放器原始秒数 / Raw playback position in seconds.</param>
    /// <returns>对齐映射后的歌词秒数 / Transformed lyric position in seconds.</returns>
    public static double ResolvePosition(MediaSnapshot snapshot, double rawPosition)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!double.IsFinite(rawPosition) || rawPosition < 0)
        {
            return rawPosition;
        }

        var lines = snapshot.Lyrics?.Document.Lines;
        if (lines is not { Count: > 0 } || snapshot.Duration <= 0 || !double.IsFinite(snapshot.Duration))
        {
            return rawPosition;
        }

        var lyricDuration = lines[^1].End;
        if (lyricDuration <= 0 || !double.IsFinite(lyricDuration))
        {
            return rawPosition;
        }

        var cacheKey = $"{snapshot.Title}\t{snapshot.Artist}\t{snapshot.Duration:F1}\t{lyricDuration:F1}";
        if (!_transformCache.TryGetValue(cacheKey, out var transform))
        {
            transform = ComputeTransform(snapshot, lines, lyricDuration);
            _transformCache.Set(cacheKey, transform);
        }

        var transformed = (rawPosition * transform.Scale) + transform.Offset;
        return Math.Clamp(transformed, 0, lyricDuration);
    }

    private static TimelineTransformParams ComputeTransform(
        MediaSnapshot snapshot,
        IReadOnlyList<LyricLine> lines,
        double lyricDuration)
    {
        var title = snapshot.Title ?? string.Empty;
        var artist = snapshot.Artist ?? string.Empty;

        var isVietnamese = MediaTitleSanitizer.IsVietnameseTrack(title, artist);
        var hasSpeedUpTag = SpeedUpRegex.IsMatch(title);
        var hasSlowedTag = SlowedRegex.IsMatch(title);
        var hasSnippetTag = SnippetRegex.IsMatch(title);

        var durationRatio = snapshot.Duration / lyricDuration;

        // 1. 副歌切片检测（Chorus Snippet / Short Ver）：
        // 视频时长极短 (<= 90s) 且远短于原曲 (< 45% 原曲时长)，或显式带有切片标签
        var isSnippetCandidate = hasSnippetTag || (snapshot.Duration <= 90 && durationRatio <= 0.45 && lines.Count >= 6);
        if (isSnippetCandidate && (isVietnamese || hasSnippetTag))
        {
            var chorusStart = DetectChorusStart(lines, lyricDuration);
            if (chorusStart > 0)
            {
                var scale = hasSpeedUpTag ? 1.25 : (hasSlowedTag ? 0.8 : 1.0);
                return new TimelineTransformParams(scale, chorusStart);
            }
        }

        // 2. 变速检测（Speed Up）：
        // 显式包含倍速标签，或者越南语曲目且时长比率落在标准加速区间 (0.65 - 0.92)
        if (hasSpeedUpTag || (isVietnamese && durationRatio >= 0.65 && durationRatio <= 0.92))
        {
            var firstLineStart = lines[0].Start;
            // 真实场景：YouTube Speed Up 视频通常裁切掉了开头的前奏（>= 6 秒），让歌词在第 0 秒就立即开唱。
            if (firstLineStart >= 6.0)
            {
                var remainingLyricDuration = lyricDuration - firstLineStart;
                var trimmedScale = remainingLyricDuration / snapshot.Duration;
                if (trimmedScale >= 1.05 && trimmedScale <= 1.60)
                {
                    return new TimelineTransformParams(trimmedScale, firstLineStart);
                }
            }

            var scale = Math.Clamp(lyricDuration / snapshot.Duration, 1.05, 1.60);
            return new TimelineTransformParams(scale, 0.0);
        }

        // 3. 延展检测（Slowed / Lofi）：
        // 显式包含减速标签，或者越南语曲目且时长比率落在标准减速区间 (1.08 - 1.45)
        if (hasSlowedTag || (isVietnamese && durationRatio >= 1.08 && durationRatio <= 1.45))
        {
            var firstLineStart = lines[0].Start;
            if (hasSlowedTag && firstLineStart >= 6.0)
            {
                var remainingLyricDuration = lyricDuration - firstLineStart;
                var trimmedScale = remainingLyricDuration / snapshot.Duration;
                if (trimmedScale >= 0.65 && trimmedScale <= 0.95)
                {
                    return new TimelineTransformParams(trimmedScale, firstLineStart);
                }
            }

            var scale = Math.Clamp(lyricDuration / snapshot.Duration, 0.65, 0.95);
            return new TimelineTransformParams(scale, 0.0);
        }

        // 4. 默认原曲原速（保持 100% 原始行为）
        return TimelineTransformParams.Identity;
    }

    /// <summary>
    /// 启发式副歌起点检测：利用副歌在歌词中高频重复的文本特征，定位副歌第一次唱起的秒数。
    /// </summary>
    public static double DetectChorusStart(IReadOnlyList<LyricLine> lines, double lyricDuration)
    {
        if (lines.Count < 4)
        {
            return 0;
        }

        var normalized = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            normalized[i] = NormalizeForMatch(lines[i].Text);
        }

        // 优先寻找连续 2 句以上重复的段落（标准副歌特征）
        for (var i = 0; i < lines.Count - 2; i++)
        {
            if (normalized[i].Length < 3)
            {
                continue;
            }

            for (var j = i + 3; j < lines.Count - 1; j++)
            {
                if (normalized[i] == normalized[j] && normalized[i + 1] == normalized[j + 1])
                {
                    // 找到了重复段落，检查起点是否在合理的副歌区间（原曲 10% - 70% 范围内）
                    if (lines[i].Start >= lyricDuration * 0.10 && lines[i].Start <= lyricDuration * 0.70)
                    {
                        return lines[i].Start;
                    }

                    // 若未严格落在区间内，仍返回该段起点作为最强候选
                    return lines[i].Start;
                }
            }
        }

        // 次选：寻找单句重复（至少 5 个字）且落在黄金副歌区间（20% - 60%）
        for (var i = 0; i < lines.Count; i++)
        {
            if (normalized[i].Length < 5)
            {
                continue;
            }

            for (var j = i + 3; j < lines.Count; j++)
            {
                if (normalized[i] == normalized[j] &&
                    lines[i].Start >= lyricDuration * 0.15 &&
                    lines[i].Start <= lyricDuration * 0.65)
                {
                    return lines[i].Start;
                }
            }
        }

        return 0;
    }

    private static string NormalizeForMatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
