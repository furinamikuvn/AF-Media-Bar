// 验证歌词时间轴自适应变换策略：自动倍速、Lofi 延展与副歌切片定位。
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

[TestClass]
public sealed class LyricTimelineTransformPolicyTests
{
    private static MediaSnapshot CreateSnapshot(
        string title,
        string artist,
        double duration,
        double position,
        LyricDocument? doc = null)
    {
        return new MediaSnapshot(
            IsConnected: true,
            IsPlaying: true,
            CanPlayPause: true,
            CanSkipPrevious: true,
            CanSkipNext: true,
            Title: title,
            Artist: artist,
            SourceId: "chrome.exe",
            SourceName: "Google Chrome",
            Artwork: null,
            Lyrics: doc is not null ? new LyricsResult("Test", doc) : null,
            Position: position,
            Duration: duration,
            CanSeek: true,
            CanChangeRepeat: false,
            RepeatMode: MediaRepeatMode.Off,
            PlaybackRate: 1.0,
            TimelineUpdatedAt: DateTimeOffset.UtcNow);
    }

    private static LyricDocument CreateLyricDoc(params (double start, double end, string text)[] lines)
    {
        var lyricLines = lines.Select(l => new LyricLine(l.start, l.end, l.text)).ToList();
        return new LyricDocument(lyricLines, LyricsSyncType.LineSynced, "LRC");
    }

    [TestMethod]
    public void NormalTrack_LeavesPositionUntouched()
    {
        // 200s video, 205s lyrics => normal ratio 0.975 (in 0.93 - 1.07 window)
        var doc = CreateLyricDoc(
            (0, 10, "Intro"),
            (10, 50, "Verse 1"),
            (50, 100, "Chorus"),
            (100, 150, "Verse 2"),
            (150, 205, "Outro"));

        var snapshot = CreateSnapshot("Đừng Làm Trái Tim Anh Đau", "Sơn Tùng M-TP", 200, 30, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 30);

        Assert.AreEqual(30.0, effective, 0.001);
    }

    [TestMethod]
    public void VietnameseSpeedUp_WithTitleTag_ScalesTimeline()
    {
        // 180s video vs 225s lyrics (speed up 1.25x)
        var doc = CreateLyricDoc(
            (0, 20, "Dạo đầu"),
            (20, 80, "Lời 1"),
            (80, 150, "Điệp khúc"),
            (150, 225, "Lời kết"));

        var snapshot = CreateSnapshot("Nơi Này Có Anh (Speed Up) - Sơn Tùng M-TP", "Sơn Tùng M-TP", 180, 40, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 40);

        // 40s in 180s video maps to 40 * (225 / 180) = 40 * 1.25 = 50.0s in lyrics
        Assert.AreEqual(50.0, effective, 0.01);
    }

    [TestMethod]
    public void VietnameseSpeedUp_WithoutTag_DetectedByDurationRatio()
    {
        // 160s video vs 200s lyrics (R = 0.8, in range 0.65 - 0.92)
        var doc = CreateLyricDoc(
            (0, 20, "Dạo đầu"),
            (20, 100, "Phiên khúc"),
            (100, 200, "Điệp khúc"));

        var snapshot = CreateSnapshot("Đừng Làm Trái Tim Anh Đau", "Sơn Tùng M-TP", 160, 32, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 32);

        // 32s in 160s video maps to 32 * (200 / 160) = 32 * 1.25 = 40.0s
        Assert.AreEqual(40.0, effective, 0.01);
    }

    [TestMethod]
    public void VietnameseSlowedLofi_WithTag_StretchesTimeline()
    {
        // 250s video vs 200s lyrics (slowed ~0.8x speed)
        var doc = CreateLyricDoc(
            (0, 30, "Intro"),
            (30, 120, "Verse"),
            (120, 200, "Chorus"));

        var snapshot = CreateSnapshot("Chờ Đợi Có Đáng Sợ (Slowed + Reverb)", "Andiez", 250, 50, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 50);

        // 50s in 250s video maps to 50 * (200 / 250) = 50 * 0.8 = 40.0s
        Assert.AreEqual(40.0, effective, 0.01);
    }

    [TestMethod]
    public void NonVietnameseTrack_WithoutSpeedTag_RemainsUntouchedEvenIfDurationDiffers()
    {
        // Chinese track with 220s video vs 260s lyrics.
        // Should NOT be auto-scaled because non-Vietnamese behavior must stay untouched!
        var doc = CreateLyricDoc(
            (0, 20, "前奏"),
            (20, 120, "主歌"),
            (120, 260, "副歌"));

        var snapshot = CreateSnapshot("晴天 - 周杰伦", "周杰伦", 220, 45, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 45);

        Assert.AreEqual(45.0, effective, 0.001);
    }

    [TestMethod]
    public void NonVietnameseTrack_WithExplicitSpeedUpTag_IsScaled()
    {
        // International track with explicit tag
        var doc = CreateLyricDoc(
            (0, 15, "Intro"),
            (15, 80, "Verse"),
            (80, 150, "Chorus"));

        var snapshot = CreateSnapshot("Stay (Speed Up) - The Kid LAROI", "The Kid LAROI", 120, 24, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 24);

        // 24 * (150 / 120) = 24 * 1.25 = 30.0s
        Assert.AreEqual(30.0, effective, 0.01);
    }

    [TestMethod]
    public void SnippetOrShortVideo_DetectsChorusStart()
    {
        // 45s short video vs 220s original song.
        // Verse 1 lines: "Từng ngày dài trôi qua", "Ánh mắt ai nhìn theo"
        // Chorus lines (repeats!): "Giờ em ở nơi khuê phòng", "Thấu chăng lòng anh đau"
        var doc = CreateLyricDoc(
            (0, 15, "Dạo đầu nhẹ nhàng"),
            (15, 30, "Từng ngày dài trôi qua"),
            (30, 45, "Ánh mắt ai nhìn theo"),
            (45, 60, "Gió lạnh từng cơn buốt giá"),
            (60, 75, "Giờ em ở nơi khuê phòng"),        // Chorus starts here!
            (75, 90, "Thấu chăng lòng anh đau"),
            (90, 110, "Mưa rơi ướt bờ vai gầy"),
            (110, 130, "Phiên khúc hai tiếp nối"),
            (130, 150, "Con đường xưa giờ vắng bóng"),
            (150, 165, "Giờ em ở nơi khuê phòng"),       // Chorus repeat 2!
            (165, 180, "Thấu chăng lòng anh đau"),
            (180, 200, "Mưa rơi ướt bờ vai gầy"),
            (200, 220, "Đoạn outro kết bài"));

        var snapshot = CreateSnapshot("Giờ Em Ở Nơi Khuê Phòng (Short Ver)", "Ca Sĩ", 45, 10, doc);
        var effective = LyricTimelineTransformPolicy.ResolvePosition(snapshot, 10);

        // Chorus detected at 60s, so raw 10s should map to 60s + 10s = 70.0s
        Assert.AreEqual(70.0, effective, 0.5);
    }

    [TestMethod]
    public void InvalidInputs_HandledSafely()
    {
        var snapshotNoLyrics = CreateSnapshot("Bài hát", "Ca Sĩ", 180, 25, null);
        Assert.AreEqual(25.0, LyricTimelineTransformPolicy.ResolvePosition(snapshotNoLyrics, 25));

        var docZeroDuration = CreateLyricDoc((0, 0, ""));
        var snapshotZero = CreateSnapshot("Bài hát", "Ca Sĩ", 0, 10, docZeroDuration);
        Assert.AreEqual(10.0, LyricTimelineTransformPolicy.ResolvePosition(snapshotZero, 10));
    }
}
