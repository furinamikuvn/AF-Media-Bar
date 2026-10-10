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

        // 3. TikTok Remix 专有混音标识
        if (TikTokRemixRegex().IsMatch(fullText))
        {
            return true;
        }

        // 4. Bass Boosted / Nonstop / Nightcore / DJ 搭配越南语特征（纯非官方混音）
        if (BassBoostedOrNonstopOrNightcoreRegex().IsMatch(fullText) &&
            (VietnameseDiacriticsRegex().IsMatch(fullText) || VietnameseKeywordsRegex().IsMatch(fullText)))
        {
            return true;
        }

        if (DjIndicatorRegex().IsMatch(fullText) &&
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
            var seg1 = validSegments[0];
            var seg2 = validSegments[1];

            // 关键特例：如果 seg1 本身已经包含了破折号且能有效拆分为两段（如 "Lời Yêu Ngây Dại - Kha" 或 "Người Em Cố Đô - Rum x Daa"），
            // 则说明 seg1 自身已经是一套完整的 Title - Artist，而后续的 seg2 （如 "Ibuki - Blue Archive Live2D" 或 "Speed Up Songs"）
            // 只是背景视频或次级修饰信息，不应将 seg1 与 seg2 强行当成 Title 与 Artist。
            var seg1DashMatch = DashOrUnderscoreSeparatorRegex().Match(seg1);
            if (seg1DashMatch.Success)
            {
                var left = seg1[..seg1DashMatch.Index].Trim();
                var right = seg1[(seg1DashMatch.Index + seg1DashMatch.Length)..].Trim();
                if (!IsPureNoiseSegment(left) && !IsPureNoiseSegment(right))
                {
                    (finalTitle, finalArtist) = DisambiguateTitleAndArtist(left, right, cleanedChannel, isDashSeparator: true);
                }
                else
                {
                    (finalTitle, finalArtist) = DisambiguateTitleAndArtist(seg1, seg2, cleanedChannel, isDashSeparator: false);
                }
            }
            else
            {
                (finalTitle, finalArtist) = DisambiguateTitleAndArtist(seg1, seg2, cleanedChannel, isDashSeparator: false);
            }
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

        // 后置微调：移除标题中残留的 tempo 标记并清洗歌手名
        finalTitle = CleanRemainingTempoTags(finalTitle);
        finalArtist = CleanArtistName(finalArtist, cleanedChannel);

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

        // 2. 检查两部分是否是明显歌曲名或明显艺人名
        var isTitleA = IsLikelySongTitle(partA);
        var isTitleB = IsLikelySongTitle(partB);
        var isArtistA = IsLikelyArtist(partA);
        var isArtistB = IsLikelyArtist(partB);

        // 如果 partA 是歌名且 partB 是歌手 -> (partA, partB)
        if (isTitleA && isArtistB)
        {
            return (partA, partB);
        }
        // 如果 partB 是歌名且 partA 是歌手 -> (partB, partA)
        if (isTitleB && isArtistA)
        {
            return (partB, partA);
        }

        // 3. 合作歌手标识检查 (ft., feat., x, &)
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

        // 4. 单侧艺人特征
        if (isArtistB && !isArtistA)
        {
            return (partA, partB);
        }
        if (isArtistA && !isArtistB)
        {
            return (partB, partA);
        }

        // 5. 单侧歌名特征
        if (isTitleA && !isTitleB)
        {
            return (partA, partB);
        }
        if (isTitleB && !isTitleA)
        {
            return (partB, partA);
        }

        // 6. 惯例回退：在网络视频及现代 YouTube 视频标题中，Title - Artist 占据绝大多数
        return (partA, partB);
    }

    private static bool IsLikelyArtist(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        // 1. 命中知名歌手名称或艺人后缀/标记
        if (KnownArtistsRegex().IsMatch(trimmed)) return true;
        if (ArtistMarkerRegex().IsMatch(trimmed)) return true;

        // 2. 单个全大写/缩写词（如 "PHUCXP", "JACK", "MONO", "IU", "MCK"）
        if (trimmed.Length >= 2 && trimmed.All(c => char.IsAsciiLetterUpper(c) || char.IsDigit(c) || c == '_' || c == '-'))
        {
            return true;
        }

        // 3. 简短单一艺人艺名（如 "Ronboogz", "Chillies", "Soobin", "Erik"）：单个词且首字母大写
        if (!trimmed.Contains(' ') && trimmed.Length >= 3 && trimmed.Length <= 15 &&
            char.IsUpper(trimmed[0]) && trimmed.Skip(1).All(char.IsLetter))
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelySongTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        // 1. 包含典型越南歌曲词汇/句式词（如 "đừng làm trái tim anh đau", "bước qua nhau", "nhắn nhủ"）
        if (VietnameseSongTitleKeywordsRegex().IsMatch(trimmed))
        {
            return true;
        }

        // 2. 词数达到 3 个或以上的多词短语（歌曲名通常比单一歌手名更长）
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount >= 3)
        {
            return true;
        }

        return false;
    }

    private static string CleanArtistName(string? artist, string? channelName)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        var text = artist.Trim();

        // 剥离频道宣传/二创合作噪音（如 "x yuu speed up", "ft. Lofi chill"）
        text = TrailingPromoOrTempoCollabRegex().Replace(text, string.Empty).Trim();

        // 移除单独的 tempo 标记
        text = TempoTagRegex().Replace(text, string.Empty).Trim();

        // 移除 Emoji 及特殊修饰符号
        text = EmojiOrSymbolRegex().Replace(text, string.Empty).Trim();

        return text.Trim(' ', '-', '|', '•', '_', '/', '&', ',');
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
        if (NoiseBracketContentRegex().IsMatch(inner)) return true;

        // 综合去噪：处理组合型标签，如 [ Lyrics // Speed Up ], [ MV | 4K 60FPS ], [ Vietsub + Kara ]
        var withoutNoise = NoiseTokenRegex().Replace(inner, string.Empty);
        var remainingClean = withoutNoise.Trim(' ', '/', '\\', '|', '+', '&', '-', '•', ',', ';', '_');
        return remainingClean.Length == 0;
    }

    private static bool IsPureNoiseSegment(string segment)
    {
        var text = segment.Trim();
        if (text.Length == 0) return true;
        if (PureNoiseSegmentRegex().IsMatch(text)) return true;
        if (PureNoiseThemeRegex().IsMatch(text)) return true;

        // 综合去噪：如果整个段落全是噪音词（如 "Speed Up Songs", "Lyrics // Speed Up"）
        var withoutNoise = NoiseTokenRegex().Replace(text, string.Empty);
        var remainingClean = withoutNoise.Trim(' ', '/', '\\', '|', '+', '&', '-', '•', ',', ';', '_');
        return remainingClean.Length == 0;
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

    [GeneratedRegex(@"bass\s*boosted|bassboost|nonstop|nightcore", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BassBoostedOrNonstopOrNightcoreRegex();

    [GeneratedRegex(@"\bdj\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DjIndicatorRegex();

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

    [GeneratedRegex(@"(?:\s*[\|•丨｜]\s*|\s*//+\s*|\s+/\s*|\s*/\s+|(?<=\S\s+\S+)/(?=\S)|(?<=\S)/(?=\S+\s+\S+))")]
    private static partial Regex PipeSeparatorRegex();

    [GeneratedRegex(@"(?:\s*[–—_]\s*|\s+-\s+)")]
    private static partial Regex DashOrUnderscoreSeparatorRegex();

    [GeneratedRegex(@"\b(ft\.|feat\.|featuring|x|&|vs\.)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollaborationRegex();

    [GeneratedRegex(@"\b(speed\s*up|sped\s*up|speedup|lo-?fi|slowed(\s*(\+|\&|and)\s*reverb)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TempoTagRegex();

    [GeneratedRegex(@"\b(sơn\s*tùng|son\s*tung|m-tp|mtp|ronboogz|phucxp|jack|j97|k-icm|icm|tăng\s*duy\s*tân|tang\s*duy\s*tan|vũ\.|vũ\b|đen\s*vâu|đen\b|mono|grey\s*d|chillies|da\s*lab|erik|min\b|soobin|hieuthuhai|mck|tlinh|amee|phan\s*mạnh\s*quỳnh|phan\s*manh\s*quynh|hoàng\s*dũng|hoang\s*dung|wren\s*evans|b\s*ray|thành\s*đạt|thanh\s*dat|đức\s*phúc|duc\s*phuc|hòa\s*minzy|hoa\s*minzy|phương\s*ly|phuong\s*ly|noo\s*phước\s*thịnh|noo\s*phuoc\s*thinh|bích\s*phương|bich\s*phuong|bảo\s*anh|bao\s*anh|trung\s*quân|trung\s*quan|văn\s*mai\s*hương|van\s*mai\s*huong|quân\s*a\.?p|quan\s*a\.?p|khắc\s*việt|khac\s*viet|hoài\s*lâm|hoai\s*lam|charlie\s*puth|ed\s*sheeran|taylor\s*swift|alan\s*walker|iu|bts|blackpink|aespa|newjeans|twice|billie\s*eilish|justin\s*bieber|adele|the\s*weeknd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownArtistsRegex();

    [GeneratedRegex(@"\b(m-tp|official|records|entertainment|productions|studio|band|orchestra|choir|prod\.|dj\b|mc\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistMarkerRegex();

    [GeneratedRegex(@"\b(đừng|làm|cho|nơi|chúng\s*ta|của|ngày|mai|khi|sau|nếu|lời|trái\s*tim|đau|nỗi\s*sầu|nhắn\s*nhủ|bạc\s*phận|sóng\s*gió|hồng\s*nhan|cắt\s*đôi|bước\s*qua|tình|như|thấy|anh|em|tôi|người|yêu|thương|chuyện|đôi\s*ta|dấu\s*mưa|ánh\s*nắng|hẹn\s*ước|mưa|gió|nắng|sầu|nhớ|quên|đi|về)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseSongTitleKeywordsRegex();

    [GeneratedRegex(@"\s*(?:x|ft\.|feat\.|featuring|&)\s*.*?(?:speed\s*up|sped\s*up|lo-?fi|slowed|remix|nightcore|chill).*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPromoOrTempoCollabRegex();

    [GeneratedRegex(@"[\p{Cs}\p{So}\p{Sk}\p{Cn}]")]
    private static partial Regex EmojiOrSymbolRegex();

    [GeneratedRegex(@"(?:\b(official(\s*(?:music|audio|video|visualizer|mv))?|music\s*video|mv|m/v|audio|visualizer|lyric\s*video|lyrics?\s*video|lyrics?|video\s*ca\s*nhạc|4k|8k|hd|fhd|uhd|1080p|720p|60fps|hi-res|lossless|speed\s*up|sped\s*up|speedup|slowed|reverb|lo-?fi(\s*ver(sion)?)?|chill|acoustic|piano|guitar|vietsub|karaoke|kara|sub\s*eng|engsub|ost|soundtrack|teaser|trailer|remix)\b|[//\\|+\-&•,;_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseTokenRegex();

    [GeneratedRegex(@"^(?:speed\s*up\s*songs?|sped\s*up\s*songs?|nhạc\s*chill(\s*(?:tiktok|buồn|gây\s*nghiện))?|chill\s*songs?|nhạc\s*(?:hot\s*)?tiktok|trend\s*tiktok|.*(?:blue\s*archive|genshin\s*impact|honkai|live2d|wallpaper|amv|mmd|anime\s*mv).*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PureNoiseThemeRegex();
}
