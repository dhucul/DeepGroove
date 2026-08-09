using System.IO;
using NAudio.Wave;

namespace WaveLab.Audio;

/// <summary>
/// Loads any supported audio file into an AudioDocument.
/// WAV goes through the sample-accurate WavCodec; MP3/FLAC/M4A decode via Media Foundation.
/// </summary>
public static class AudioImporter
{
    public const string OpenFilter =
        "Audio files (*.wav;*.mp3;*.flac;*.m4a;*.wma)|*.wav;*.mp3;*.flac;*.m4a;*.wma|" +
        "Wave files (*.wav)|*.wav|All files (*.*)|*.*";

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return WavCodec.Load(path, cancellationToken);

        using var reader = new MediaFoundationReader(path);
        var sp = reader.ToSampleProvider();
        int channels = sp.WaveFormat.Channels;
        int sampleRate = sp.WaveFormat.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
            throw new InvalidDataException("The decoder returned an invalid audio format.");

        var blocks = new List<float[]>();
        var buf = new float[checked(sampleRate * channels)]; // ~1s chunks
        int read;
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = sp.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            if (total + read > (long)Array.MaxLength * channels)
                throw new InvalidDataException("The decoded audio exceeds the maximum supported sample count.");
            var block = new float[read];
            Array.Copy(buf, block, read);
            blocks.Add(block);
            total += read;
        }

        if (total % channels != 0)
            throw new InvalidDataException("The decoder returned a partial audio sample frame.");
        int frames = checked((int)(total / channels));
        var ch = new float[channels][];
        for (int c = 0; c < channels; c++) ch[c] = new float[frames];

        int frame = 0, carryChannel = 0;
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < block.Length; i++)
            {
                if (frame >= frames) break;
                ch[carryChannel][frame] = block[i];
                if (++carryChannel == channels) { carryChannel = 0; frame++; }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        int srcBits = reader.WaveFormat.BitsPerSample is 16 or 24 or 32 ? reader.WaveFormat.BitsPerSample : 16;
        return new AudioDocument(ch, sampleRate, srcBits)
        {
            FilePath = null, // compressed sources must be saved back out as WAV
            Title = Path.GetFileName(path),
        };
    }
}
