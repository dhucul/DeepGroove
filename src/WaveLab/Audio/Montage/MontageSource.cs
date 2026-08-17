using System.IO;

namespace WaveLab.Audio.Montage;

/// <summary>
/// A file a montage draws on, brought onto the montage's own clock and channel layout once.
/// </summary>
/// <remarks>
/// <para>
/// A montage references files rather than copying them, so one source can feed many clips at no
/// cost. What it does <em>not</em> do is let a clip play at a different rate from its neighbours:
/// the sample rate and channel count are reconciled here, at load, so everything downstream —
/// crossfade correlation, the renderer, the length arithmetic — works in one clock with no
/// per-clip conversion. Import does not normalise rate anywhere else in this app, so if this did
/// not do it, nothing would.
/// </para>
/// <para>
/// Converting once on the way in rather than per clip at render also matters for quality: a clip
/// used twice would otherwise be resampled twice, and a resampler is not free of error.
/// </para>
/// </remarks>
public sealed class MontageSource
{
    private MontageSource(string? path, string name, float[][] channels, int sampleRate, int originalRate)
    {
        Path = path;
        Name = name;
        Channels = channels;
        SampleRate = sampleRate;
        OriginalSampleRate = originalRate;
    }

    /// <summary>Where it came from, or null for audio handed in directly.</summary>
    public string? Path { get; }

    public string Name { get; }

    /// <summary>The audio, at the montage's rate and channel count.</summary>
    public float[][] Channels { get; }

    /// <summary>The montage's rate — this audio has already been brought onto it.</summary>
    public int SampleRate { get; }

    /// <summary>What the file itself was, which is worth showing when it was not the montage's.</summary>
    public int OriginalSampleRate { get; }

    public bool WasResampled => OriginalSampleRate != SampleRate;
    public int ChannelCount => Channels.Length;
    public int Length => Channels.Length > 0 ? Channels[0].Length : 0;
    public double Duration => SampleRate > 0 ? (double)Length / SampleRate : 0;

    /// <summary>Loads a file onto a montage's clock and channel layout.</summary>
    public static MontageSource Load(string path, int sampleRate, int channelCount,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AudioDocument document = AudioImporter.Load(path, cancellationToken);

        return From(document.Channels.ToArray(), document.SampleRate, sampleRate, channelCount,
            System.IO.Path.GetFileNameWithoutExtension(path), path, cancellationToken, progress);
    }

    /// <summary>Brings audio already in hand onto a montage's clock and channel layout.</summary>
    public static MontageSource From(float[][] channels, int sourceRate, int sampleRate,
        int channelCount, string name, string? path = null,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));

        float[][] audio = channels;
        if (sourceRate != sampleRate)
            audio = Resampler.Resample(audio, sourceRate, sampleRate, cancellationToken, progress);

        audio = MatchChannels(audio, channelCount, cancellationToken);
        return new MontageSource(path, string.IsNullOrWhiteSpace(name) ? "Source" : name.Trim(),
            audio, sampleRate, sourceRate);
    }

    /// <summary>
    /// Fits audio to the montage's channel count: a mono source is copied to both sides, a wider one
    /// is summed down.
    /// </summary>
    /// <remarks>
    /// The downmix divides by the channel count rather than summing, because summing two correlated
    /// channels is +6 dB and a mono montage would clip on the first stereo file dropped into it.
    /// </remarks>
    private static float[][] MatchChannels(float[][] audio, int channelCount,
        CancellationToken cancellationToken)
    {
        if (audio.Length == channelCount) return audio;
        if (audio.Length == 0) return [.. Enumerable.Range(0, channelCount).Select(_ => Array.Empty<float>())];

        int frames = audio[0].Length;
        var result = new float[channelCount][];

        if (audio.Length == 1)
        {
            // The same array on every channel would alias: an edit to one would show on all.
            for (int c = 0; c < channelCount; c++) result[c] = (float[])audio[0].Clone();
            return result;
        }

        if (channelCount == 1)
        {
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                double sum = 0;
                for (int c = 0; c < audio.Length; c++) sum += audio[c][i];
                mono[i] = (float)(sum / audio.Length);
            }
            return [mono];
        }

        // Anything else: take the channels that exist, and fill the rest with silence rather than
        // guessing at a matrix nobody asked for.
        for (int c = 0; c < channelCount; c++)
            result[c] = c < audio.Length ? (float[])audio[c].Clone() : new float[frames];
        return result;
    }
}
