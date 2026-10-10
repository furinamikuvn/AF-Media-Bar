using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

[TestClass]
public sealed class OneShotAiListenerTests
{
    [TestMethod]
    public void SileroVad_InferenceRunsSuccessfully()
    {
        var modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\AFMediaBar\Assets\Models\silero_vad.onnx"));
        Assert.IsTrue(File.Exists(modelPath), $"Model file not found at: {modelPath}");

        var bytes = File.ReadAllBytes(modelPath);
        using var session = new InferenceSession(bytes);

        // 512 samples of silence (16kHz, 32ms)
        var inputSamples = new float[512];
        var inputTensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(inputSamples, new[] { 1, 512 });

        // State tensor [2, 1, 128] initialized to zeros
        var stateSamples = new float[2 * 1 * 128];
        var stateTensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(stateSamples, new[] { 2, 1, 128 });

        // Sample rate: scalar int64 16000
        var srTensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>());

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor)
        };

        // Test chunk 1
        using (var results = session.Run(inputs))
        {
            var outputTensor = results.First(r => r.Name == "output").AsTensor<float>();
            var prob = outputTensor.GetValue(0);
            TestContext?.WriteLine($"Chunk 1 silence prob: {prob:F4}");
            Assert.IsTrue(prob < 0.1f);

            var stateNTensor = (Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>)results.First(r => r.Name == "stateN").AsTensor<float>();
            stateNTensor.Buffer.Span.CopyTo(stateSamples.AsSpan());
        }

        // Test chunk 2 with carried-over state
        using (var results2 = session.Run(inputs))
        {
            var outputTensor2 = results2.First(r => r.Name == "output").AsTensor<float>();
            var prob2 = outputTensor2.GetValue(0);
            TestContext?.WriteLine($"Chunk 2 silence prob: {prob2:F4}");
            Assert.IsTrue(prob2 < 0.1f);
        }
    }

    [TestMethod]
    public void SileroVad_EmbeddedResourceIsPresentInAssembly()
    {
        var asm = typeof(AFMediaBar.Classes.Models.MediaSnapshot).Assembly;
        var names = asm.GetManifestResourceNames();
        var found = names.FirstOrDefault(n => n.EndsWith("silero_vad.onnx", StringComparison.OrdinalIgnoreCase));
        TestContext?.WriteLine($"Found embedded resource: {found}");
        Assert.IsNotNull(found, $"silero_vad.onnx embedded resource not found in {asm.FullName}. Available: {string.Join(", ", names)}");

        using var stream = asm.GetManifestResourceStream(found);
        Assert.IsNotNull(stream);
        Assert.IsTrue(stream.Length > 1_000_000, $"Expected model size > 1MB, got {stream.Length}");
    }

    [TestMethod]
    public void VocalActivityDetector_ProcessSilence_DetectsNoVocal()
    {
        using var detector = new AFMediaBar.Classes.Services.Audio.VocalActivityDetector();

        // Feed 3 seconds of silence at 48000Hz (simulating WASAPI Loopback capture)
        var silenceChunk = new float[4800]; // 100ms at 48kHz
        for (var i = 0; i < 30; i++)
        {
            detector.ProcessAudio(silenceChunk, 48000);
        }

        Assert.IsFalse(detector.HasDetectedVocal, "Silence should never trigger vocal detection!");
        Assert.AreEqual(0, detector.MaxConsecutiveVocalChunks);
        Assert.IsTrue(detector.ProcessedAudioSeconds >= 2.5, $"Processed audio seconds was {detector.ProcessedAudioSeconds}");
    }

    [TestMethod]
    public void OneShotAiListener_CacheAndOverride_WorksDeterministically()
    {
        AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.ClearCache();

        var snapshot = new AFMediaBar.Classes.Models.MediaSnapshot(
            IsConnected: true,
            IsPlaying: true,
            CanPlayPause: true,
            CanSkipPrevious: true,
            CanSkipNext: true,
            Title: "Gió - Jank [ Lyrics // Speed Up ]",
            Artist: "SArisu Chill",
            SourceId: "YouTube",
            SourceName: "YouTube",
            Artwork: null,
            Lyrics: null,
            Position: 0.0,
            Duration: 201.2,
            CanSeek: true,
            CanChangeRepeat: false,
            RepeatMode: AFMediaBar.Classes.Models.MediaRepeatMode.Off,
            PlaybackRate: 1.0,
            TimelineUpdatedAt: DateTimeOffset.UtcNow);

        // First check: without override, intro >= 5s should return false initially
        var initial = AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.IsIntroTrimmed(snapshot, 19.8);
        Assert.IsFalse(initial);

        // When intro < 5s: always false
        var shortIntro = AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.IsIntroTrimmed(snapshot, 3.0);
        Assert.IsFalse(shortIntro);

        // Set override to true (simulating AI vocal detection completion)
        AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.SetIntroTrimmedOverride("Gió - Jank [ Lyrics // Speed Up ]", "SArisu Chill", 201.2, true);
        var afterOverride = AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.IsIntroTrimmed(snapshot, 19.8);
        Assert.IsTrue(afterOverride);

        // Clear cache resets state
        AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.ClearCache();
        var afterClear = AFMediaBar.Classes.Services.Lyrics.OneShotAiListener.IsIntroTrimmed(snapshot, 19.8);
        Assert.IsFalse(afterClear);
    }

    public TestContext? TestContext { get; set; }
}
