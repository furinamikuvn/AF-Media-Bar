// 纯策略：清洗网络播放器（YouTube / 浏览器 SMTC）标题与歌手，自动排除越南 Remix 曲目。
using System.Text.RegularExpressions;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>
/// 负责对 SMTC 提取的媒体标题和歌手进行清洗、去噪，并识别/排除越南 Remix。
/// Sanitizes noisy media titles/artists and excludes Vietnamese remix tracks.
/// </summary>
public static partial class MediaTitleSanitizer
{
    public sealed record SanitizedMediaInfo(string Title, string Artist, bool IsExcludedRemix);

    /// <summary>
    /// 判断是否为越南 Remix、Vinahouse、Vietmix、Nonstop 等跳舞/电音混音曲目。
    /// Determines whether a track is an excluded Vietnamese remix/dance track.
    /// </summary>
    public static bool IsExcludedVietnameseRemix(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        var fullText = $"{title} {artist}".Trim();

        // 1. 显式越南电音/夜店/混音专用流派与标识词（命中即排除）
        if (ExplicitVietnameseDanceGenreRegex().IsMatch(fullText))
        {
            return true;
        }

        // 2. 知名越南 Remix 制作团队 / DJ / 厂牌标识
        if (VietnameseRemixerRegex().IsMatch(fullText))
        {
            return true;
        }

        // 3. 包含 Remix / Mix / Mashup / DJ / PT Remix，且伴随越南语字符或越南歌曲标识
        if (RemixIndicatorRegex().IsMatch(fullText))
        {
            if (VietnameseDiacriticsRegex().IsMatch(fullText) ||
                VietnameseKeywordsRegex().IsMatch(fullText) ||
                TikTokRemixRegex().IsMatch(fullText))
            {
                return true;
            }
        }

        // 4. Bass Boosted / Nonstop 搭配越南语特征
        if (BassBoostedOrNonstopRegex().IsMatch(fullText) &&
            (VietnameseDiacriticsRegex().IsMatch(fullText) || VietnameseKeywordsRegex().IsMatch(fullText)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 对原始标题与歌手进行清洗，提取干净的 Title 与 Artist，并标记是否属于排除的 Remix。
    /// Sanitizes raw title and artist, extracting clean metadata for lyrics search.
    /// </summary>
    public static SanitizedMediaInfo Sanitize(string? rawTitle, string? rawArtist, string? sourceId = null)
    {
        var originalTitle = rawTitle?.Trim() ?? string.Empty;
        var originalArtist = rawArtist?.Trim() ?? string.Empty;

        var isExcluded = IsExcludedVietnameseRemix(originalTitle, originalArtist);

        if (string.IsNullOrWhiteSpace(originalTitle))
        {
            return new(string.Empty, CleanChannelName(originalArtist), isExcluded);
        }

        var cleanedChannel = CleanChannelName(originalArtist);

        // 先剥离标题首尾无关前缀，如 [MV], 【Official MV】
        var workingTitle = LeadingMediaTagRegex().Replace(originalTitle, string.Empty).Trim();

        // 剥离已知的括号去噪内容（如 [Official Music Video], (sped up), (Lofi Version), [4K] 等）
        workingTitle = StripBracketNoise(workingTitle);

        // 按分隔符管道拆分：| , • , 丨 , ｜
        var pipeSegments = SplitByPipes(workingTitle);

        // 过滤掉纯噪音段落（如 "OFFICIAL MV", "4K 60FPS", "speed up"）
        var validSegments = pipeSegments
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !IsPureNoiseSegment(s))
            .ToList();

        string finalTitle;
        string finalArtist;

        if (validSegments.Count >= 2)
        {
            // 多个段落，判断哪段是 Title，哪段是 Artist
            var seg1 = validSegments[0];
            var seg2 = validSegments[1];

            (finalTitle, finalArtist) = DisambiguateTitleAndArtist(seg1, seg2, cleanedChannel, isDashSeparator: false);
        }
        else if (validSegments.Count == 1)
        {
            var single = validSegments[0];

            // 检查是否包含次级分隔符： - , – , — , _
            var dashMatch = DashOrUnderscoreSeparatorRegex().Match(single);
            if (dashMatch.Success)
            {
                var left = single[..dashMatch.Index].Trim();
                var right = single[(dashMatch.Index + dashMatch.Length)..].Trim();

                if (!IsPureNoiseSegment(left) && !IsPureNoiseSegment(right))
                {
                    (finalTitle, finalArtist) = DisambiguateTitleAndArtist(left, right, cleanedChannel, isDashSeparator: true);
                }
                else if (IsPureNoiseSegment(right))
                {
                    finalTitle = left;
                    finalArtist = cleanedChannel;
                }
                else
                {
                    finalTitle = right;
                    finalArtist = cleanedChannel;
                }
            }
            else
            {
                finalTitle = single;
                finalArtist = cleanedChannel;
            }
        }
        else
        {
            finalTitle = originalTitle;
            finalArtist = cleanedChannel;
        }

        // 后置微调：移除标题中残留的 tempo 标记
        finalTitle = CleanRemainingTempoTags(finalTitle);

        return new(finalTitle, string.IsNullOrWhiteSpace(finalArtist) ? cleanedChannel : finalArtist, isExcluded);
    }

    private static (string Title, string Artist) DisambiguateTitleAndArtist(
        string partA, string partB, string channelName, bool isDashSeparator)
    {
        partA = StripBracketNoise(partA).Trim();
        partB = StripBracketNoise(partB).Trim();

        // 1. 如果频道名存在，对比两部分与频道名的匹配度
        if (!string.IsNullOrWhiteSpace(channelName))
        {
            var matchA = ContainsIgnoreCase(partA, channelName) || ContainsIgnoreCase(channelName, partA);
            var matchB = ContainsIgnoreCase(partB, channelName) || ContainsIgnoreCase(channelName, partB);

            if (matchA && !matchB)
            {
                return (partB, partA); // partA 匹配频道名 -> partA 是 Artist，partB 是 Title
            }
            if (matchB && !matchA)
            {
                return (partA, partB); // partB 匹配频道名 -> partB 是 Artist，partA 是 Title
            }
        }

        // 2. 合作歌手标识检查 (ft., feat., x, &)
        var hasCollabA = CollaborationRegex().IsMatch(partA);
        var hasCollabB = CollaborationRegex().IsMatch(partB);

        if (hasCollabA && !hasCollabB)
        {
            return (partB, partA);
        }
        if (hasCollabB && !hasCollabA)
        {
            return (partA, partB);
        }

        // 3. 特殊形态分析：如果 partB 是全大写英文句柄或简短艺人名，而 partA 是多词完整歌曲名
        if (IsArtistHandle(partB) && !IsArtistHandle(partA))
        {
            return (partA, partB);
        }
        if (IsArtistHandle(partA) && !IsArtistHandle(partB))
        {
            return (partB, partA);
        }

        // 4. 惯例回退
        // 对于破折号（-），西方与国际常规大多为 Artist - Title（如 Charlie Puth - Dangerously）
        // 但如果 Part A 包含很多越南语长句而 Part B 为单一词汇，则优先作为 Title - Artist
        if (isDashSeparator)
        {
            return (partB, partA); // 默认 Artist - Title: Title = partB, Artist = partA
        }

        // 对于竖线（|），常规多为 Title | Artist (如 BẠC PHẬN | ICM x JACK)
        return (partA, partB);
    }

    private static bool IsArtistHandle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        // 单个全大写单词，如 "PHUCXP", "JACK", "MONO"
        if (trimmed.Length >= 2 && trimmed.All(c => char.IsAsciiLetterUpper(c) || char.IsDigit(c) || c == '_' || c == '-'))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string source, string target) =>
        source.Contains(target, StringComparison.OrdinalIgnoreCase);

    private static string CleanChannelName(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return string.Empty;
        var text = channel.Trim();
        text = ChannelSuffixRegex().Replace(text, string.Empty).Trim();
        return text;
    }

    private static string StripBracketNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // 匹配各种括号中的噪音内容并移除
        return BracketNoiseRegex().Replace(text, match =>
        {
            var inner = match.Groups[1].Value.Trim();
            return IsNoiseBracketContent(inner) ? string.Empty : match.Value;
        }).Trim();
    }

    private static bool IsNoiseBracketContent(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return true;
        return NoiseBracketContentRegex().IsMatch(inner);
    }

    private static bool IsPureNoiseSegment(string segment)
    {
        var text = segment.Trim();
        if (text.Length == 0) return true;
        return PureNoiseSegmentRegex().IsMatch(text);
    }

    private static string CleanRemainingTempoTags(string title)
    {
        var text = TempoTagRegex().Replace(title, string.Empty);
        return text.Trim(' ', '-', '|', '•', '_', '/');
    }

    private static List<string> SplitByPipes(string text)
    {
        var parts = PipeSeparatorRegex().Split(text);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    [GeneratedRegex(@"vinahouse|vina\s*house|vina-house|vinahey|vina\s*hey|vina-hey|vietmix|viet\s*mix|viet-mix|viet\s*remix|remix\s*viet|việt\s*remix|remix\s*việt|lắc\s*cư|lac\s*cu|laccu|bay\s*phòng|bay\s*phong|nhạc\s*quẩy|nhac\s*quay|nhạc\s*sàn|nhac\s*san|quẩy\s*bar|quay\s*bar|vũ\s*trường|vu\s*truong|pt\s*remix", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitVietnameseDanceGenreRegex();

    [GeneratedRegex(@"đại\s*mèo|dai\s*meo|cukak|h2o\s*remix|acv\s*remix|k-icm\s*remix|icm\s*remix|ngọc\s*hải\s*remix|ngoc\s*hai\s*remix|phong\s*max\s*remix|minh\s*trí\s*remix|minh\s*tri\s*remix|oran\s*remix|orinn\s*remix|tilo\s*remix", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseRemixerRegex();

    [GeneratedRegex(@"\b(remix|remixes|mix|mashup|club\s*mix|dance\s*mix|dj)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemixIndicatorRegex();

    [GeneratedRegex(@"[àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬĐÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ]")]
    private static partial Regex VietnameseDiacriticsRegex();

    [GeneratedRegex(@"\b(bac\s*phan|song\s*gio|hong\s*nhan|cat\s*doi\s*noi\s*sau|thien\s*ly\s*oi|dung\s*lam\s*trai\s*tim\s*anh\s*dau|nang\s*chen\s*tieu\s*sau|ngay\s*mai\s*nguoi\s*ta\s*lay\s*chong|ai\s*chung\s*tinh\s*duoc\s*mai|nhu\s*anh\s*da\s*thay\s*em|sao\s*cung\s*duoc|va\s*the\s*la\s*het|em\s*cua\s*ngay\s*hom\s*qua|lac\s*troi|chay\s*ngay\s*di|chung\s*ta\s*cua\s*hien\s*tai|chung\s*ta\s*cua\s*tuong\s*lai|noi\s*dau\s*xot\s*xa|em\s*co\s*on\s*khong|tinh\s*de\s*tu|ang\s*may\s*vo\s*tinh|tang\s*duy\s*tan|son\s*tung|phan\s*manh\s*quynh|hoai\s*lam|nhac\s*tre)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseKeywordsRegex();

    [GeneratedRegex(@"hot\s*tik\s*tok\s*remix|hot\s*tiktok\s*remix|remix\s*tik\s*tok|remix\s*tiktok|trend\s*tiktok\s*remix", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TikTokRemixRegex();

    [GeneratedRegex(@"bass\s*boosted|bassboost|nonstop", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BassBoostedOrNonstopRegex();

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|【[^】]*】)\s*")]
    private static partial Regex LeadingMediaTagRegex();

    [GeneratedRegex(@"[\[(【《「]([^\])】》」]*)[\])】》」]")]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"^(official\s*(music\s*)?video|official\s*mv|mv|m/v|music\s*video|official\s*audio|audio\s*official|audio|audio\s*4k|official\s*track|official\s*visualizer|visualizer|lyric\s*video|lyrics?\s*video|official\s*lyrics?|lyrics?|video\s*lyric|video\s*ca\s*nhạc|4k(\s*60fps)?|8k|hd|fhd|uhd|1080p|720p|60fps|hi-res|lossless|dolby\s*atmos|speed\s*up|sped\s*up|speedup|slowed(\s*(\+|\&|and)\s*reverb)?|slow(\s*(\+|\&|and)\s*reverb)?|lo-?fi(\s*ver(sion)?)?|chill(\s*ver(sion)?)?|acoustic(\s*ver(sion)?)?|piano\s*ver(sion)?|guitar\s*ver(sion)?|teaser|trailer|behind\s*the\s*scenes|making\s*of|dance\s*practice|performance\s*video|from\s+[^)]+\s+soundtrack|ost)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseBracketContentRegex();

    [GeneratedRegex(@"^(official\s*(music\s*)?video|official\s*mv|mv|m/v|music\s*video|official\s*audio|audio\s*official|audio|official\s*track|official\s*visualizer|visualizer|lyric\s*video|lyrics?\s*video|official\s*lyrics?|video\s*ca\s*nhạc|4k|8k|hd|fhd|uhd|1080p|720p|60fps|speed\s*up|sped\s*up|lo-?fi(\s*ver(sion)?)?|slowed(\s*(\+|\&|and)\s*reverb)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PureNoiseSegmentRegex();

    [GeneratedRegex(@"\s*[-–—]?\s*(Topic|Official(\s*Channel|\s*Music|\s*Audio|\s*MV|\s*Video)?|Kênh\s*Chính\s*Thức|Entertainment|Ent\.|Records|Production|Studio|Media|Music|VEVO)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelSuffixRegex();

    [GeneratedRegex(@"\s*[\|•丨｜]\s*")]
    private static partial Regex PipeSeparatorRegex();

    [GeneratedRegex(@"(?:\s*[–—_]\s*|\s+-\s+)")]
    private static partial Regex DashOrUnderscoreSeparatorRegex();

    [GeneratedRegex(@"\b(ft\.|feat\.|featuring|x|&|vs\.)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollaborationRegex();

    [GeneratedRegex(@"\b(speed\s*up|sped\s*up|speedup|lo-?fi|slowed(\s*(\+|\&|and)\s*reverb)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TempoTagRegex();
}
