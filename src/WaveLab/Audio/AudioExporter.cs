using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

public enum ExportFormat { Wav32Float, Wav24, Wav16, Mp3, Aac, Wma, Flac }

/// <summary>
/// Exports a document (or a range of it) to WAV via the internal codec, or to
/// MP3 / AAC / WMA / FLAC through Media Foundation encoders where Windows provides them.
/// </summary>
public static class AudioExporter
{
    public static string FilterFor(ExportFormat f) => f switch
    {
        ExportFormat.Mp3 => "MP3|*.mp3",
        ExportFormat.Aac => "AAC (M4A)|*.m4a",
        ExportFormat.Wma => "WMA|*.wma",
        ExportFormat.Flac => "FLAC|*.flac",
        _ => "WAV|*.wav",
    };

    public static bool IsLossy(ExportFormat f) => f is ExportFormat.Mp3 or ExportFormat.Aac or ExportFormat.Wma;

    /// <summary>Export. start/count select a range (0, doc.Length for all). targetRate 0 = keep.</summary>
    public static void Export(AudioDocument doc, string path, ExportFormat format, int bitrateKbps,
        int start, int count, int targetRate)
    {
        float[][] data;
        if (start == 0 && count == doc.Length)
        {
            data = new float[doc.ChannelCount][];
            for (int c = 0; c < doc.ChannelCount; c++) data[c] = doc.Channels[c];
        }
        else
        {
            data = doc.CopyRange(start, count);
        }

        int rate = doc.SampleRate;
        if (targetRate > 0 && targetRate != rate)
        {
            data = Resampler.Resample(data, rate, targetRate);
            rate = targetRate;
        }

        switch (format)
        {
            case ExportFormat.Wav32Float:
            case ExportFormat.Wav24:
            case ExportFormat.Wav16:
            {
                int depth = format switch { ExportFormat.Wav24 => 24, ExportFormat.Wav16 => 16, _ => 32 };
                var temp = new AudioDocument(data, rate, depth);
                WavCodec.Save(temp, path, depth, dither: depth == 16);
                break;
            }
            default:
                EncodeViaMediaFoundation(data, rate, path, format, bitrateKbps);
                break;
        }
    }

    private static void EncodeViaMediaFoundation(float[][] data, int rate, string path, ExportFormat format, int bitrateKbps)
    {
        int channels = data.Length;
        int frames = data[0].Length;

        // Feed encoders 16-bit PCM (dithered) — universally accepted by the MF encoder MFTs.
        long bytesNeeded = (long)frames * channels * 2;
        if (bytesNeeded > int.MaxValue) throw new InvalidOperationException("File is too long to encode in one pass.");
        var pcm = new byte[bytesNeeded];
        var dither = new TpdfDither();
        int o = 0;
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
            {
                int q = (int)Math.Round(data[c][f] * 32767.0 + dither.Next());
                q = Math.Clamp(q, short.MinValue, short.MaxValue);
                pcm[o++] = (byte)q;
                pcm[o++] = (byte)(q >> 8);
            }

        var waveFormat = new WaveFormat(rate, 16, channels);
        using var ms = new MemoryStream(pcm);
        using var provider = new RawSourceWaveStream(ms, waveFormat);

        switch (format)
        {
            case ExportFormat.Mp3:
                MediaFoundationEncoder.EncodeToMp3(provider, path, bitrateKbps * 1000);
                break;
            case ExportFormat.Aac:
                MediaFoundationEncoder.EncodeToAac(provider, path, bitrateKbps * 1000);
                break;
            case ExportFormat.Wma:
                MediaFoundationEncoder.EncodeToWma(provider, path, bitrateKbps * 1000);
                break;
            case ExportFormat.Flac:
            {
                var mediaType = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_FLAC, waveFormat, 0);
                if (mediaType == null)
                    throw new NotSupportedException("This Windows build has no FLAC encoder. Export as WAV instead.");
                using var encoder = new MediaFoundationEncoder(mediaType);
                encoder.Encode(path, provider);
                break;
            }
        }
    }

    /// <summary>True if a FLAC encoder MFT is present on this machine.</summary>
    public static bool FlacAvailable()
    {
        try
        {
            return MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_FLAC, new WaveFormat(48000, 16, 2), 0) != null;
        }
        catch { return false; }
    }
}
