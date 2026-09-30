using LanLink.Core.Remote;
using NAudio.Wave;

namespace LanLink.App.Platform;

/// <summary>Capture ce que joue ce PC (WASAPI loopback) et l'envoie en PCM 16 bits.</summary>
public sealed class LoopbackAudioCapturer : IAudioCapturer
{
    private readonly WasapiLoopbackCapture _capture = new();
    private readonly WaveFormat _source;

    public AudioFormatInfo Format { get; }

    public event Action<byte[]>? DataAvailable;

    public LoopbackAudioCapturer()
    {
        _source = _capture.WaveFormat;
        if (_source.Encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Pcm)
            && _source is not WaveFormatExtensible)
            throw new NotSupportedException("Format audio non pris en charge.");
        Format = new AudioFormatInfo(_source.SampleRate, _source.Channels, 16);
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) DataAvailable?.Invoke(To16Bit(e.Buffer, e.BytesRecorded));
        };
    }

    public void Start() => _capture.StartRecording();

    private byte[] To16Bit(byte[] buffer, int count)
    {
        if (_source.BitsPerSample == 16) return buffer.AsSpan(0, count).ToArray();
        if (_source.BitsPerSample != 32) return Array.Empty<byte>();

        var samples = count / 4;
        var output = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = BitConverter.ToSingle(buffer, i * 4);
            var s = (short)Math.Clamp((int)Math.Round(value * 32767f), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(s & 0xFF);
            output[i * 2 + 1] = (byte)(s >> 8);
        }
        return output;
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch (InvalidOperationException) { }
        _capture.Dispose();
    }
}

/// <summary>Joue l'audio reçu avec une petite mémoire tampon (les retards sont abandonnés pour rester synchronisé).</summary>
public sealed class WaveOutAudioPlayer : IAudioPlayer
{
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;

    public void Start(AudioFormatInfo format)
    {
        Dispose();
        _buffer = new BufferedWaveProvider(new WaveFormat(format.SampleRate, 16, format.Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true,
        };
        _output = new WaveOutEvent { DesiredLatency = 120 };
        _output.Init(_buffer);
        _output.Play();
    }

    public void Play(ReadOnlyMemory<byte> pcm)
    {
        if (_buffer is null || pcm.IsEmpty) return;
        var data = pcm.ToArray();
        _buffer.AddSamples(data, 0, data.Length);
    }

    public void Dispose()
    {
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }
}
