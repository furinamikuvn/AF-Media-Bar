// Converts borrowed NAudio capture packets to mono spectrum samples. Owns only reusable managed buffers, never native resources.
using NAudio.Wave;

namespace AFMediaBar.Classes.Services.Audio;

internal sealed class LoopbackSampleReader
{
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _samples;
    private readonly float[] _scratch;
    private readonly float[] _monoBuffer = new float[1024];
    private readonly Action<float[], int> _publishChunk;
    private readonly int _channels;
    private readonly int _blockAlign;

    internal LoopbackSampleReader(WaveFormat format, Action<float[], int> publishChunk)
    {
        if (format.Channels is < 1 or > 32 || format.SampleRate <= 0)
            throw new ArgumentException("Unsupported loopback channel count or sample rate.", nameof(format));
        _channels = format.Channels;
        _blockAlign = format.BlockAlign;
        _publishChunk = publishChunk;
        _buffer = new BufferedWaveProvider(format.AsStandardWaveFormat(), TimeSpan.FromSeconds(2048d / format.SampleRate))
        {
            ReadFully = false
        };
        _samples = _buffer.ToSampleProvider();
        _scratch = new float[1024 * _channels];
    }

    internal void Append(ReadOnlySpan<byte> packet)
    {
        if (packet.Length % _blockAlign != 0)
            throw new ArgumentException("Capture packets must contain complete audio frames.", nameof(packet));

        // Chunk large packets so high-rate, multichannel devices cannot overflow or grow the buffer.
        while (!packet.IsEmpty)
        {
            var length = Math.Min(packet.Length, 1024 * _blockAlign);
            _buffer.AddSamples(packet[..length]);
            packet = packet[length..];
            int read;
            while ((read = _samples.Read(_scratch.AsSpan())) > 0)
            {
                var monoCount = 0;
                for (var offset = 0; offset < read; offset += _channels)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < _channels; channel++)
                        sum += _scratch[offset + channel];
                    var mono = sum / _channels;
                    _monoBuffer[monoCount++] = float.IsFinite(mono) ? Math.Clamp(mono, -1f, 1f) : 0;
                }

                if (monoCount > 0)
                {
                    _publishChunk(_monoBuffer, monoCount);
                }
            }
        }
    }
}
