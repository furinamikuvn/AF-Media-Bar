// 验证 YouTube 及网络媒体标题清洗、非官方混音排除与严格 Remix 匹配策略。
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

[TestClass]
public sealed class MediaTitleSanitizerTests
{
    [TestMethod]
    [DataRow("Áng Mây Vô Tình Remix (Nightcore) - DBlue x Chu Duyên", "DBlue")]
    [DataRow("[Nightcore] Mashup Nỗi Đau Xót Xa x Em Có Ổn Không (PT Remix)", "")]
    [DataRow("Cắt Đôi Nỗi Sầu (Vinahouse 2024)", "Tăng Duy Tân")]
    [DataRow("Vietmix 2024 Bay Phòng Nhạc Quẩy Cực Căng", "DJ Minh Trí")]
    [DataRow("Nonstop Vinahey Bass Boosted 2024", "DJ Đại Mèo")]
    [DataRow("Lắc Cư Quẩy Tung Nóc", "DJ")]
    [DataRow("Nỗi Đau Xót Xa (Đại Mèo Remix)", "")]
    [DataRow("Bạc Phận (Cukak Remix)", "Jack")]
    [DataRow("Ngay Mai Nguoi Ta Lay Chong (Viet Mix)", "")]
    [DataRow("Hot TikTok Remix - Em Có Ổn Không", "")]
    [DataRow("Tình Đệ Tử - Vinahey Nhạc Sàn Cực Bốc", "")]
    [DataRow("Mashup Nhạc Trẻ Remix Bass Boosted", "")]
    public void ExcludesUnofficialVietnameseRemixTracks(string title, string artist)
    {
        Assert.IsTrue(MediaTitleSanitizer.IsExcludedVietnameseRemix(title, artist),
            $"Track '{title}' by '{artist}' should be identified as an excluded unofficial Vietnamese remix.");
    }

    [TestMethod]
    [DataRow("BẠC PHẬN | ICM x JACK | OFFICIAL MV", "ICM Entertainment")]
    [DataRow("Như Anh Đã Thấy Em - PHUCXP | speed up", "MinhTann Music")]
    [DataRow("Bước Qua Nhau - Vũ. (Lofi Ver.)", "Vũ. Official")]
    [DataRow("Đom Đóm (sped up)", "Jack - J97")]
    [DataRow("SƠN TÙNG M-TP | ĐỪNG LÀM TRÁI TIM ANH ĐAU | OFFICIAL MUSIC VIDEO", "Sơn Tùng M-TP Official")]
    [DataRow("Shape of You (Galantis Remix)", "Ed Sheeran")]
    [DataRow("Charlie Puth - Dangerously [Official Music Video]", "Charlie Puth")]
    [DataRow("Chuyện Đôi Ta - Emcee L (Da LAB) ft. Muội | Official MV", "Da LAB Official")]
    [DataRow("Phan Mạnh Quỳnh - Sau Lời Từ Khước (from MAI Soundtrack) | Lyric Video", "Phan Mạnh Quỳnh")]
    [DataRow("Lạc Trôi (Triple D Remix)", "Sơn Tùng M-TP")]
    [DataRow("Có Chắc Yêu Là Đây (Onionn Remix)", "Sơn Tùng M-TP")]
    [DataRow("Bạc Phận (Remix)", "Jack")]
    public void AllowsOriginalSpeedUpLofiAndOfficialRemixTracks(string title, string artist)
    {
        Assert.IsFalse(MediaTitleSanitizer.IsExcludedVietnameseRemix(title, artist),
            $"Track '{title}' by '{artist}' should not be excluded upfront.");
    }

    [TestMethod]
    public void SanitizesYouTubeTitleAndChannelWhenTitlePiped()
    {
        var result = MediaTitleSanitizer.Sanitize(
            "BẠC PHẬN | ICM x JACK | OFFICIAL MV",
            "ICM Entertainment",
            "chrome.exe");

        Assert.AreEqual("BẠC PHẬN", result.Title);
        Assert.AreEqual("ICM x JACK", result.Artist);
        Assert.IsFalse(result.IsExcludedRemix);
    }

    [TestMethod]
    public void SanitizesArtistFirstWhenChannelMatchesFirstSegment()
    {
        var result = MediaTitleSanitizer.Sanitize(
            "SƠN TÙNG M-TP | ĐỪNG LÀM TRÁI TIM ANH ĐAU | OFFICIAL MUSIC VIDEO",
            "Sơn Tùng M-TP Official",
            "msedge.exe");

        Assert.AreEqual("ĐỪNG LÀM TRÁI TIM ANH ĐAU", result.Title);
        Assert.AreEqual("SƠN TÙNG M-TP", result.Artist);
        Assert.IsFalse(result.IsExcludedRemix);
    }

    [TestMethod]
    public void SanitizesSpeedUpTrackAndStripsNoise()
    {
        var result = MediaTitleSanitizer.Sanitize(
            "Như Anh Đã Thấy Em - PHUCXP | speed up",
            "MinhTann Music",
            "chrome.exe");

        Assert.AreEqual("Như Anh Đã Thấy Em", result.Title);
        Assert.AreEqual("PHUCXP", result.Artist);
        Assert.IsFalse(result.IsExcludedRemix);
    }

    [TestMethod]
    public void SanitizesLofiAndQualityBrackets()
    {
        var result = MediaTitleSanitizer.Sanitize(
            "Bước Qua Nhau (Lofi Version) [Audio 4K]",
            "Vũ. - Topic",
            "chrome.exe");

        Assert.AreEqual("Bước Qua Nhau", result.Title);
        Assert.AreEqual("Vũ.", result.Artist);
        Assert.IsFalse(result.IsExcludedRemix);
    }

    [TestMethod]
    public void SanitizesKoreanMvWithUnderscoreSeparator()
    {
        var result = MediaTitleSanitizer.Sanitize(
            "[MV] IU(아이유) _ Blueming(블루밍)",
            "1theK (원더케이)",
            "chrome.exe");

        Assert.AreEqual("Blueming(블루밍)", result.Title);
        Assert.AreEqual("IU(아이유)", result.Artist);
        Assert.IsFalse(result.IsExcludedRemix);
    }

    [TestMethod]
    public void MarksExcludedRemixInSanitizedResult()
    {
        var result = MediaTitleSanitizer.Sanitize(
            "Cắt Đôi Nỗi Sầu (Vinahouse 2024)",
            "DJ Đại Mèo",
            "chrome.exe");

        Assert.IsTrue(result.IsExcludedRemix);
    }

    [TestMethod]
    public void EligibilityPolicyRejectsUnofficialVietnameseRemix()
    {
        Assert.IsFalse(LyricsMediaEligibilityPolicy.ShouldFetch(
            "chrome.exe",
            isVideo: false,
            allowBrowserAndVideoLyrics: true,
            title: "Cắt Đôi Nỗi Sầu (Vinahouse 2024)",
            artist: "DJ Đại Mèo"));

        Assert.IsTrue(LyricsMediaEligibilityPolicy.ShouldFetch(
            "chrome.exe",
            isVideo: false,
            allowBrowserAndVideoLyrics: true,
            title: "Bạc Phận",
            artist: "Jack"));
    }

    [TestMethod]
    public void StrictRemixPolicyRejectsOriginalTrackWhenRequestIsRemix()
    {
        var request = new LyricsRequest("Lạc Trôi (Triple D Remix)", "Sơn Tùng M-TP", "Album", 255.0, null);

        // 候选曲目为原版（非 Remix），评分应直接归零，低于门槛
        var scoreOriginal = LyricsMetadataScore.Calculate(request, "Lạc Trôi", "Sơn Tùng M-TP", "Album", 255.0);
        Assert.IsTrue(scoreOriginal < LyricsMetadataScore.MinimumScore,
            $"Original track should not match remix request. Score was: {scoreOriginal}");

        // 候选曲目同样为 Remix，允许匹配且达到高分
        var scoreRemix = LyricsMetadataScore.Calculate(request, "Lạc Trôi (Triple D Remix)", "Sơn Tùng M-TP", "Album", 255.0);
        Assert.IsTrue(scoreRemix >= LyricsMetadataScore.MinimumScore,
            $"Remix candidate matching remix request should have high score. Score was: {scoreRemix}");
    }

    [TestMethod]
    public void StrictRemixPolicyRejectsRemixTrackWhenRequestIsOriginal()
    {
        var request = new LyricsRequest("Lạc Trôi", "Sơn Tùng M-TP", "Album", 255.0, null);

        // 候选曲目为 Remix，原版请求不应采纳
        var scoreRemix = LyricsMetadataScore.Calculate(request, "Lạc Trôi (Triple D Remix)", "Sơn Tùng M-TP", "Album", 255.0);
        Assert.IsTrue(scoreRemix < LyricsMetadataScore.MinimumScore,
            $"Remix candidate should not match original request. Score was: {scoreRemix}");

        // 候选曲目为原版，正常匹配
        var scoreOriginal = LyricsMetadataScore.Calculate(request, "Lạc Trôi", "Sơn Tùng M-TP", "Album", 255.0);
        Assert.IsTrue(scoreOriginal >= LyricsMetadataScore.MinimumScore,
            $"Original candidate matching original request should have high score. Score was: {scoreOriginal}");
    }
}
