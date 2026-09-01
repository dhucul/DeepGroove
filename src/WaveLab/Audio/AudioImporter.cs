using System.IO;
using NAudio.Wave;

namespace WaveLab.Audio;

public enum OpenBitDepth
{
    Pcm16Dithered,
    Pcm16Undithered,
    Pcm24,
    Float32,
}

/// <summary>
/// Loads any supported audio file into an AudioDocument.
/// WAV, AIFF and DSD go through native codecs; compressed PCM formats decode via Media Foundation.
/// </summary>
public static class AudioImporter
{
    public const string OpenFilter =
        "Audio files (*.wav;*.w64;*.aif;*.aiff;*.aifc;*.dsf;*.dff;*.diff;*.dsdiff;*.mp3;*.flac;*.m4a;*.wma)|*.wav;*.w64;*.aif;*.aiff;*.aifc;*.dsf;*.dff;*.diff;*.dsdiff;*.mp3;*.flac;*.m4a;*.wma|" +
        "Wave files (*.wav;*.w64)|*.wav;*.w64|AIFF files (*.aif;*.aiff;*.aifc)|*.aif;*.aiff;*.aifc|" +
        "DSD files (*.dsf;*.dff;*.diff;*.dsdiff)|*.dsf;*.dff;*.diff;*.dsdiff|" +
        "All files (*.*)|*.*";

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path);
        if (extension.Equals(".w64", StringComparison.OrdinalIgnoreCase))
            return Wave64Codec.Load(path, cancellationToken);
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            // Asked rather than assumed: Wave64 is written to .w64 by convention and to .wav by
            // more than one application that ought to know better, and the two containers share
            // nothing but their samples.
            return Wave64Codec.IsWave64(path)
                ? Wave64Codec.Load(path, cancellationToken)
                : WavCodec.Load(path, cancellationToken);
        }
        if (extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".aifc", StringComparison.OrdinalIgnoreCase))
            return AiffCodec.Load(path, cancellationToken);
        if (extension.Equals(".dsf", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dff", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".diff", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dsdiff", StringComparison.OrdinalIgnoreCase))
            return DsdCodec.Load(path, cancellationToken);

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
            read = sp.Read(buf);
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
        for (int b = 0; b < blocks.Count; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] block = blocks[b];
            // Release each block as it is consumed: the decoded list and the
            // deinterleaved output would otherwise both be fully rooted, doubling
            // peak memory on a long import.
            blocks[b] = null!;
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

    /// <summary>
    /// Decode a file into a new, unsaved document at the requested depth. Editing
    /// buffers remain 32-bit float. Undithered 16-bit and 24-bit choices constrain
    /// samples immediately; dithered 16-bit defers its single stochastic
    /// quantization pass until save.
    /// </summary>
    public static AudioDocument LoadAs(
        string path,
        OpenBitDepth bitDepth,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(bitDepth)) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        AudioDocument source = Load(path, cancellationToken);
        float[][] channels = PrepareSamples(
            source.Channels,
            bitDepth,
            cancellationToken);
        int bits = bitDepth switch
        {
            OpenBitDepth.Pcm16Dithered or OpenBitDepth.Pcm16Undithered => 16,
            OpenBitDepth.Pcm24 => 24,
            _ => 32,
        };
        string suffix = bitDepth switch
        {
            OpenBitDepth.Pcm16Dithered => "16-bit dithered",
            OpenBitDepth.Pcm16Undithered => "16-bit no dither",
            OpenBitDepth.Pcm24 => "24-bit",
            _ => "32-bit float",
        };
        var converted = new AudioDocument(channels, source.SampleRate, bits)
        {
            FilePath = null,
            Title = $"{Path.GetFileNameWithoutExtension(path)} ({suffix}).wav",
            Dither16BitOnSave = bitDepth != OpenBitDepth.Pcm16Undithered,
        };
        converted.MarkUnsaved();
        return converted;
    }

    private static float[][] PrepareSamples(
        IReadOnlyList<float[]> source,
        OpenBitDepth bitDepth,
        CancellationToken cancellationToken)
    {
        if (bitDepth == OpenBitDepth.Float32)
            return source.ToArray();

        var result = new float[source.Count][];
        for (int channel = 0; channel < source.Count; channel++)
        {
            float[] input = source[channel];
            float[] output = new float[input.Length];
            result[channel] = output;
            for (int index = 0; index < input.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                float sample = float.IsFinite(input[index]) ? Math.Clamp(input[index], -1f, 1f) : 0f;
                if (bitDepth == OpenBitDepth.Pcm16Dithered)
                {
                    output[index] = sample;
                }
                else if (bitDepth == OpenBitDepth.Pcm16Undithered)
                {
                    double value = sample * 32768.0;
                    int quantized = Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
                    output[index] = quantized / 32768f;
                }
                else
                {
                    int quantized = Math.Clamp(
                        (int)Math.Round(sample * 8388608.0), -8388608, 8388607);
                    output[index] = quantized / 8388608f;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
