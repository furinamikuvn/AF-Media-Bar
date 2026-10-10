// 曲目元数据评分的纯策略；不调用歌词库的评分或持有网络资源。
using System.Text.RegularExpressions;
using AFMediaBar.Classes.Models;
using F23.StringSimilarity;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>字符串相似度与线性时长衰减组成的统一匹配分。/ Unified metadata similarity score.</summary>
internal static partial class LyricsMetadataScore
{
    // 试听时长不能否决正确曲目：时长仅占 10%，释放的权重均分给标题和歌手。
    // 阈值和权重属于代码策略，不能重新暴露成匹配严格度设置。
    internal const int MinimumScore = 60;
    internal const double TitleWeight = 0.40;
    internal const double ArtistWeight = 0.40;
    internal const double AlbumWeight = 0.10;
    internal const double DurationWeight = 0.10;
    private static readonly JaroWinkler Similarity = new();

    /// <summary>评分组成；标题缺失时改用标题和歌手合并后的指纹相似度。</summary>
    internal readonly record struct Breakdown(double Title, double Artist, double Album, double Duration,
        double? Fingerprint)
    {
        public double UnroundedScore
        {
            get
            {
                if (Fingerprint is { } fp) return 100 * fp;
                if (Title <= 0) return 0;
                return 100 * (TitleWeight * Title + ArtistWeight * Artist + AlbumWeight * Album + DurationWeight * Duration);
            }
        }
        public int TotalScore => (int)Math.Round(UnroundedScore);
    }

    public static int Calculate(LyricsRequest request, string? title, string? artist, string? album, double? duration) =>
        Calculate(request, [title ?? string.Empty], [artist ?? string.Empty], [album ?? string.Empty], duration);

    public static int Calculate(LyricsRequest request, string[] titles, string[] artists, string[] albums, double? duration) =>
        Explain(request, titles, artists, albums, duration).TotalScore;

    /// <summary>返回实际评分路径的各项相似度，供独立测试器解释分数，不执行搜索或取词。</summary>
    internal static Breakdown Explain(LyricsRequest request, string[] titles, string[] artists, string[] albums, double? duration)
    {
        var requestArtists = LyricsArtistPolicy.Split(request);
        var candidateArtists = artists.SelectMany(artist =>
            LyricsArtistPolicy.Split(artist, request.ArtistSeparators)).ToArray();
        if (string.IsNullOrWhiteSpace(request.Title) || !titles.Any(title => !string.IsNullOrWhiteSpace(title)))
        {
            var scores = from requestArtist in requestArtists
                         let query = Fingerprint($"{request.Title} {requestArtist}")
                         from title in titles
                         from artist in candidateArtists
                         let candidate = Fingerprint($"{title} {artist}")
                         select query.Length == 0 || candidate.Length == 0 ? 0 : Similarity.Similarity(query, candidate);
            return new(0, 0, 0, 0, scores.DefaultIfEmpty(0).Max());
        }

        var isVietnamese = MediaTitleSanitizer.IsVietnameseTrack(request.Title, request.Artist);
        var titleScore = titles.Select(title => isVietnamese ? CompareTitle(request.Title, title) : Compare(request.Title, title)).DefaultIfEmpty(0).Max();
        // Preserve the existing best-artist-match policy, now comparing individual artists on both sides.
        var artistScore = (from requestArtist in requestArtists
                           from candidateArtist in candidateArtists
                           select Compare(requestArtist, candidateArtist)).DefaultIfEmpty(0).Max();
        var albumScore = albums.Select(album => Compare(request.Album, album)).DefaultIfEmpty(0).Max();
        var durationScore = CompareDuration(request.DurationSeconds, duration);
        var directBreakdown = new Breakdown(titleScore, artistScore, albumScore, durationScore, null);

        if (!isVietnamese || directBreakdown.TotalScore >= MinimumScore)
        {
            return directBreakdown;
        }

        // 兜底反向容错：针对某些播放器或元数据源误将标题与歌手颠倒的情况（仅针对越南语曲目）
        // （例如 request.Title = "Sơn Tùng M-TP", request.Artist = "Đừng làm trái tim anh đau"）
        // 必须要求互换后的 Title 与 Artist 均达到高相似度门槛（>= 0.65），以防非同名曲目虚假匹配
        if (!string.IsNullOrWhiteSpace(request.Artist))
        {
            var swappedTitleScore = titles.Select(title => CompareTitle(request.Artist, title)).DefaultIfEmpty(0).Max();
            var swappedArtistScore = (from candidateArtist in candidateArtists
                                      select Compare(request.Title, candidateArtist)).DefaultIfEmpty(0).Max();

            if (swappedTitleScore >= 0.65 && swappedArtistScore >= 0.65)
            {
                var swappedBreakdown = new Breakdown(swappedTitleScore, swappedArtistScore, albumScore, durationScore, null);
                if (swappedBreakdown.TotalScore > directBreakdown.TotalScore)
                {
                    return swappedBreakdown;
                }
            }
        }

        return directBreakdown;
    }

    private static double CompareTitle(string? left, string? right)
    {
        left = left?.Trim().ToLowerInvariant() ?? string.Empty;
        right = right?.Trim().ToLowerInvariant() ?? string.Empty;
        if (left.Length == 0 && right.Length == 0) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;

        // Strict Remix Match:
        // A remix track must only match a candidate that is also a remix.
        // An original non-remix track must not match a remix candidate.
        var leftIsRemix = RemixRegex().IsMatch(left);
        var rightIsRemix = RemixRegex().IsMatch(right);
        if (leftIsRemix != rightIsRemix)
        {
            return 0;
        }

        return Similarity.Similarity(left, right);
    }

    private static double Compare(string? left, string? right)
    {
        left = left?.Trim().ToLowerInvariant() ?? string.Empty;
        right = right?.Trim().ToLowerInvariant() ?? string.Empty;
        if (left.Length == 0 && right.Length == 0) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        return Similarity.Similarity(left, right);
    }

    private static double CompareDuration(double? local, double? remote)
    {
        if (local is not > 0 || remote is not > 0 || !double.IsFinite(local.Value) || !double.IsFinite(remote.Value)) return 0;
        var difference = Math.Abs(local.Value - remote.Value);
        if (difference <= 1) return 1;
        if (difference >= 10) return 0;
        return 1 - (difference - 1) / 9;
    }

    private static string Fingerprint(string text) => string.Join(" ",
        NonWordCharacters().Replace(text.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(token => token, StringComparer.Ordinal));

    [GeneratedRegex(@"[\p{P}\p{S}]")]
    private static partial Regex NonWordCharacters();

    [GeneratedRegex(@"\b(remix|remixes|mashup)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemixRegex();
}
