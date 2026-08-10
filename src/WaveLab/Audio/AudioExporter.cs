using System.IO;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

public enum ExportFormat
{
    Wav32Float,
    Wav24,
    Wav16,
    Wav16Undithered,
    Aiff32,
    Aiff24,
    Aiff16,
    Aiff16Undithered,
    Mp3,
    Aac,
    Wma,
    Flac,
}

/// <summary>
/// Exports a document (or a range of it) to WAV/AIFF via internal codecs, or to
/// MP3 / AAC / WMA / FLAC through Media Foundation encoders where Windows provides them.
/// </summary>
public static class AudioExporter
{
    public static string FilterFor(ExportFormat format) => format switch
    {
        ExportFormat.Mp3 => "MP3|*.mp3",
        ExportFormat.Aac => "AAC (M4A)|*.m4a",
        ExportFormat.Wma => "WMA|*.wma",
        ExportFormat.Flac => "FLAC|*.flac",
        ExportFormat.Aiff32 or ExportFormat.Aiff24 or ExportFormat.Aiff16 or
            ExportFormat.Aiff16Undithered => "AIFF|*.aiff",
        _ => "WAV|*.wav",
    };

    public static bool IsLossy(ExportFormat format) =>
        format is ExportFormat.Mp3 or ExportFormat.Aac or ExportFormat.Wma;

    /// <summary>Export. start/count select a range (0, doc.Length for all). targetRate 0 = keep.</summary>
    public static void Export(
        AudioDocument doc,
        string path,
        ExportFormat format,
        int bitrateKbps,
        int start,
        int count,
        int targetRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (targetRate < 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        cancellationToken.ThrowIfCancellationRequested();

        var source = doc.Channels.ToArray();
        if (source.Length == 0)
            throw new InvalidOperationException("Cannot export a document with no audio channels.");
        int sourceLength = source[0].Length;
        if (source.Any(channel => channel == null || channel.Length != sourceLength))
            throw new InvalidOperationException("All document channels must have the same sample count.");
        if (start < 0 || start > sourceLength)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0 || count > sourceLength - start)
            throw new ArgumentOutOfRangeException(nameof(count));

        float[][] data = start == 0 && count == sourceLength
            ? source
            : CopyRange(source, start, count, cancellationToken);

        int rate = doc.SampleRate;
        if (rate <= 0) throw new InvalidOperationException("The document sample rate must be positive.");
        if (targetRate > 0 && targetRate != rate)
        {
            data = Resampler.Resample(data, rate, targetRate, cancellationToken);
            rate = targetRate;
        }

        string finalPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The export path has no directory.");
        string extension = Path.GetExtension(finalPath);
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}{extension}");

        try
        {
            switch (format)
            {
                case ExportFormat.Wav32Float:
                case ExportFormat.Wav24:
                case ExportFormat.Wav16:
                case ExportFormat.Wav16Undithered:
                {
                    int depth = format switch
                    {
                        ExportFormat.Wav24 => 24,
                        ExportFormat.Wav16 or ExportFormat.Wav16Undithered => 16,
                        _ => 32,
                    };
                    var snapshot = new AudioDocument(data, rate, depth);
                    WavCodec.Save(snapshot, stagePath, depth, dither: format == ExportFormat.Wav16,
                        cancellationToken: cancellationToken);
                    break;
                }
                case ExportFormat.Aiff32:
                case ExportFormat.Aiff24:
                case ExportFormat.Aiff16:
                case ExportFormat.Aiff16Undithered:
                {
                    int depth = format switch
                    {
                        ExportFormat.Aiff24 => 24,
                        ExportFormat.Aiff16 or ExportFormat.Aiff16Undithered => 16,
                        _ => 32,
                    };
                    var snapshot = new AudioDocument(data, rate, depth);
                    AiffCodec.Save(snapshot, stagePath, depth,
                        dither: format == ExportFormat.Aiff16,
                        cancellationToken: cancellationToken);
                    break;
                }
                case ExportFormat.Mp3:
                case ExportFormat.Aac:
                case ExportFormat.Wma:
                case ExportFormat.Flac:
                    EncodeViaMediaFoundation(data, rate, stagePath, format, bitrateKbps, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagePath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(stagePath); } catch { }
            throw;
        }
    }

    private static float[][] CopyRange(
        IReadOnlyList<float[]> source,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        var result = new float[source.Count][];
        const int blockSize = 1 << 20;
        for (int channel = 0; channel < source.Count; channel++)
        {
            result[channel] = new float[count];
            for (int offset = 0; offset < count; offset += blockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int block = Math.Min(blockSize, count - offset);
                Array.Copy(source[channel], start + offset, result[channel], offset, block);
            }
        }
        return result;
    }

    private static void EncodeViaMediaFoundation(
        float[][] data,
        int rate,
        string path,
        ExportFormat format,
        int bitrateKbps,
        CancellationToken cancellationToken)
    {
        int channels = data.Length;
        if (channels == 0) throw new InvalidOperationException("Cannot encode audio with no channels.");
        int frames = data[0].Length;
        if (data.Any(channel => channel.Length != frames))
            throw new InvalidOperationException("All encoder input channels must have the same sample count.");

        Guid subtype;
        int desiredBitrate;
        switch (format)
        {
            case ExportFormat.Mp3:
                subtype = AudioSubtypes.MFAudioFormat_MP3;
                desiredBitrate = CheckedBitrate(bitrateKbps);
                break;
            case ExportFormat.Aac:
                subtype = AudioSubtypes.MFAudioFormat_AAC;
                desiredBitrate = CheckedBitrate(bitrateKbps);
                break;
            case ExportFormat.Wma:
                subtype = AudioSubtypes.MFAudioFormat_WMAudioV8;
                desiredBitrate = CheckedBitrate(bitrateKbps);
                break;
            case ExportFormat.Flac:
                subtype = AudioSubtypes.MFAudioFormat_FLAC;
                desiredBitrate = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        // Lossy encoders already discard precision, so retain the established
        // dithered 16-bit input path for broad MFT compatibility. FLAC must not
        // inherit that quantization: Windows' FLAC MFT accepts packed 24-bit PCM.
        IWaveProvider provider = format == ExportFormat.Flac
            ? new Pcm24Provider(data, rate, cancellationToken)
            : new Pcm16Provider(data, rate, cancellationToken);
        int minimumOutputBits = format == ExportFormat.Flac ? 24 : 0;
        MediaType? mediaType = SelectMediaTypeOwned(
                subtype, provider.WaveFormat, desiredBitrate, minimumOutputBits)
            ?? throw new NotSupportedException(format == ExportFormat.Flac
                ? "This Windows build has no 24-bit FLAC encoder. Export as WAV instead."
                : $"This Windows build has no suitable {format} encoder.");
        try
        {
            // The encoder owns the selected COM media type only after its
            // constructor succeeds. Release it here if construction fails.
            using var encoder = new MediaFoundationEncoder(mediaType);
            mediaType = null;
            encoder.Encode(path, provider);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (mediaType != null) ReleaseMediaType(mediaType);
        }
    }

    private static int CheckedBitrate(int bitrateKbps)
    {
        if (bitrateKbps <= 0) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        return checked(bitrateKbps * 1000);
    }

    private static MediaType? SelectMediaTypeOwned(
        Guid subtype,
        WaveFormat format,
        int desiredBitrate,
        int minimumBitsPerSample = 0)
    {
        MediaType[] mediaTypes = MediaFoundationEncoder.GetOutputMediaTypes(subtype);
        MediaType? selected = null;
        long selectedDelta = long.MaxValue;
        try
        {
            foreach (MediaType mediaType in mediaTypes)
            {
                if (mediaType.SampleRate != format.SampleRate || mediaType.ChannelCount != format.Channels)
                    continue;
                if (minimumBitsPerSample > 0 &&
                    mediaType.TryGetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE) < minimumBitsPerSample)
                    continue;
                long bitrate = (long)mediaType.AverageBytesPerSecond * 8;
                long delta = Math.Abs((long)desiredBitrate - bitrate);
                if (delta >= selectedDelta) continue;
                selected = mediaType;
                selectedDelta = delta;
            }

            foreach (MediaType mediaType in mediaTypes)
                if (!ReferenceEquals(mediaType, selected)) ReleaseMediaType(mediaType);
            return selected;
        }
        catch
        {
            foreach (MediaType mediaType in mediaTypes) ReleaseMediaType(mediaType);
            throw;
        }
    }

    private static void ReleaseMediaType(MediaType mediaType)
    {
        try
        {
            object mediaFoundationObject = mediaType.MediaFoundationObject;
            if (Marshal.IsComObject(mediaFoundationObject))
                Marshal.FinalReleaseComObject(mediaFoundationObject);
        }
        catch
        {
            // Capability probing and cleanup must preserve the primary result/error.
        }
    }

    /// <summary>True if a FLAC encoder MFT is present on this machine.</summary>
    public static bool FlacAvailable()
    {
        MediaType[] mediaTypes = [];
        try
        {
            mediaTypes = MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_FLAC);
            return mediaTypes.Any(mediaType =>
                mediaType.SampleRate == 48_000 &&
                mediaType.ChannelCount == 2 &&
                mediaType.TryGetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE) >= 24);
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (MediaType mediaType in mediaTypes) ReleaseMediaType(mediaType);
        }
    }

    private sealed class Pcm16Provider : IWaveProvider
    {
        private readonly float[][] _data;
        private readonly CancellationToken _cancellationToken;
        private readonly TpdfDither _dither = new();
        private int _position;

        public Pcm16Provider(float[][] data, int sampleRate, CancellationToken cancellationToken)
        {
            _data = data;
            _cancellationToken = cancellationToken;
            WaveFormat = new WaveFormat(sampleRate, 16, data.Length);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
            // NAudio 2.2.1 allocates an IMFMediaBuffer and IMFSample before it
            // calls the provider, but does not release them when Read throws.
            // Report end-of-stream on cancellation so NAudio reaches its COM
            // release statements; EncodeViaMediaFoundation propagates the token
            // immediately after Encode returns and its staged output is deleted.
            if (_cancellationToken.IsCancellationRequested) return 0;

            int channels = _data.Length;
            int frameBytes = checked(channels * sizeof(short));
            int framesRequested = count / frameBytes;
            int frames = Math.Min(framesRequested, _data[0].Length - _position);
            int output = offset;
            int framesWritten = 0;
            for (; framesWritten < frames; framesWritten++)
            {
                if ((framesWritten & 4095) == 0 && _cancellationToken.IsCancellationRequested)
                    break;
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = _data[channel][_position + framesWritten];
                    if (!float.IsFinite(sample)) sample = 0;
                    sample = Math.Clamp(sample, -1f, 1f);
                    int quantized = (int)Math.Round(sample * 32768.0 + _dither.Next());
                    quantized = Math.Clamp(quantized, short.MinValue, short.MaxValue);
                    buffer[output++] = (byte)quantized;
                    buffer[output++] = (byte)(quantized >> 8);
                }
            }

            _position += framesWritten;
            return framesWritten * frameBytes;
        }
    }

    private sealed class Pcm24Provider : IWaveProvider
    {
        private readonly float[][] _data;
        private readonly CancellationToken _cancellationToken;
        private int _position;

        public Pcm24Provider(float[][] data, int sampleRate, CancellationToken cancellationToken)
        {
            _data = data;
            _cancellationToken = cancellationToken;
            WaveFormat = new WaveFormat(sampleRate, 24, data.Length);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
            // See Pcm16Provider: throwing from Read leaks NAudio 2.2.1's current
            // IMFMediaBuffer/IMFSample allocation, so cancellation ends the stream.
            if (_cancellationToken.IsCancellationRequested) return 0;

            int channels = _data.Length;
            int frameBytes = checked(channels * 3);
            int framesRequested = count / frameBytes;
            int frames = Math.Min(framesRequested, _data[0].Length - _position);
            int output = offset;
            int framesWritten = 0;
            for (; framesWritten < frames; framesWritten++)
            {
                if ((framesWritten & 4095) == 0 && _cancellationToken.IsCancellationRequested)
                    break;
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = _data[channel][_position + framesWritten];
                    if (!float.IsFinite(sample)) sample = 0;
                    int quantized = (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 8388608.0);
                    quantized = Math.Clamp(quantized, -8388608, 8388607);
                    buffer[output++] = (byte)quantized;
                    buffer[output++] = (byte)(quantized >> 8);
                    buffer[output++] = (byte)(quantized >> 16);
                }
            }

            _position += framesWritten;
            return framesWritten * frameBytes;
        }
    }
}
