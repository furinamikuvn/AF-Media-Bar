// 验证 YouTube 及网络媒体标题清洗与越南 Remix 自动排除策略。
using AFMediaBar.Classes.Services.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

[TestClass]
public sealed class MediaTitleSanitizerTests
{
    [TestMethod]
    [DataRow("Bạc Phận Remix", "K-ICM x Jack")]
    [DataRow("Áng Mây Vô Tình Remix (Nightcore) - DBlue x Chu Duyên", "DBlue")]
    [DataRow("[Nightcore] Mashup Nỗi Đau Xót Xa x Em Có Ổn Không (PT Remix)", "")]
    [DataRow("Cắt Đôi Nỗi Sầu (Vinahouse 2024)", "Tăng Duy Tân")]
    [DataRow("Vietmix 2024 Bay Phòng Nhạc Quẩy Cực Căng", "DJ Minh Trí")]
    [DataRow("Nonstop Vinahey Bass Boosted 2024", "DJ Đại Mèo")]
    [DataRow("Lắc Cư Quẩy Tung Nóc", "DJ")]
    [DataRow("Nỗi Đau Xót Xa (Đại Mèo Remix)", "")]
    [DataRow("Bạc Phận (Cukak Remix)", "Jack")]
    [DataRow("Bac Phan Remix", "Jack")]
    [DataRow("Cat Doi Noi Sau (Remix)", "Tang Duy Tan")]
    [DataRow("Ngay Mai Nguoi Ta Lay Chong (Viet Mix)", "")]
    [DataRow("Hot TikTok Remix - Em Có Ổn Không", "")]
    [DataRow("Tình Đệ Tử - Vinahey Nhạc Sàn Cực Bốc", "")]
    [DataRow("Mashup Nhạc Trẻ Remix Bass Boosted", "")]
    public void ExcludesVietnameseRemixTracks(string title, string artist)
    {
        Assert.IsTrue(MediaTitleSanitizer.IsExcludedVietnameseRemix(title, artist),
            $"Track '{title}' by '{artist}' should be identified as an excluded Vietnamese remix.");
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
    public void AllowsOriginalSpeedUpLofiAndWesternTracks(string title, string artist)
    {
        Assert.IsFalse(MediaTitleSanitizer.IsExcludedVietnameseRemix(title, artist),
            $"Track '{title}' by '{artist}' should not be excluded.");
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
            "Bạc Phận Remix (Vinahouse 2024)",
            "DJ Đại Mèo",
            "chrome.exe");

        Assert.IsTrue(result.IsExcludedRemix);
    }

    [TestMethod]
    public void EligibilityPolicyRejectsVietnameseRemixEvenWhenBrowserLyricsAllowed()
    {
        Assert.IsFalse(LyricsMediaEligibilityPolicy.ShouldFetch(
            "chrome.exe",
            isVideo: false,
            allowBrowserAndVideoLyrics: true,
            title: "Bạc Phận Remix",
            artist: "Jack"));

        Assert.IsTrue(LyricsMediaEligibilityPolicy.ShouldFetch(
            "chrome.exe",
            isVideo: false,
            allowBrowserAndVideoLyrics: true,
            title: "Bạc Phận",
            artist: "Jack"));
    }
}
