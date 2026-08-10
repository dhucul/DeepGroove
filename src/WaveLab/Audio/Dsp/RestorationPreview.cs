namespace WaveLab.Audio.Dsp;

/// <summary>Channel routing used only while auditioning restoration output.</summary>
public enum RestorationAuditionMode
{
    Stereo,
    Left,
    Right,
}

/// <summary>Sample-aligned helpers for non-destructive restoration previews.</summary>
public static class RestorationPreview
{
    /// <summary>
    /// Return a new buffer containing a linear, sample-aligned dry/wet mix. Linear mixing
    /// is intentional here: restoration output is time-aligned with the source, so it
    /// avoids the level lift and combing that an equal-power effect mix could introduce.
    /// </summary>
    public static float[][] Mix(IReadOnlyList<float[]> dry, IReadOnlyList<float[]> processed,
        double wetAmount, bool bypass = false)
    {
        ValidatePair(dry, processed);
        return MixRange(dry, processed, 0, dry.Count == 0 ? 0 : dry[0].Length,
            wetAmount, bypass);
    }

    /// <summary>
    /// Render only a requested preview range into new buffers. The input buffers are never
    /// changed, making repeated strength and bypass previews safe for undoable documents.
    /// </summary>
    public static float[][] MixRange(IReadOnlyList<float[]> dry, IReadOnlyList<float[]> processed,
        int startSample, int sampleCount, double wetAmount, bool bypass = false,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        ValidatePair(dry, processed);
        int length = dry.Count == 0 ? 0 : dry[0].Length;
        if (startSample < 0 || startSample > length)
            throw new ArgumentOutOfRangeException(nameof(startSample));
        if (sampleCount < 0 || sampleCount > length - startSample)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));

        float wet = bypass ? 0f : (float)Math.Clamp(wetAmount, 0.0, 1.0);
        float dryGain = 1f - wet;
        var result = new float[dry.Count][];
        for (int c = 0; c < dry.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = new float[sampleCount];
            if (wet == 0f)
            {
                Array.Copy(dry[c], startSample, output, 0, sampleCount);
            }
            else if (wet == 1f)
            {
                Array.Copy(processed[c], startSample, output, 0, sampleCount);
            }
            else
            {
                for (int i = 0; i < sampleCount; i++)
                    output[i] = dry[c][startSample + i] * dryGain + processed[c][startSample + i] * wet;
            }
            result[c] = output;
            progress?.Report(new RestorationProgress(RestorationStage.RenderingPreview,
                (double)(c + 1) / Math.Max(1, dry.Count), c, dry.Count));
        }
        return result;
    }

    /// <summary>Clone deinterleaved channel buffers without changing their sample values.</summary>
    public static float[][] Clone(IReadOnlyList<float[]> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var copy = new float[channels.Count][];
        for (int c = 0; c < channels.Count; c++)
        {
            ArgumentNullException.ThrowIfNull(channels[c]);
            copy[c] = (float[])channels[c].Clone();
        }
        return copy;
    }

    /// <summary>
    /// Create monitoring buffers for all channels or for one side of a stereo source.
    /// A soloed side is duplicated to both output channels so it remains centered and
    /// easy to inspect. Source buffers are never returned or modified.
    /// </summary>
    public static float[][] CreateAudition(IReadOnlyList<float[]> channels,
        RestorationAuditionMode mode)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (channels.Count == 0) return [];

        int length = channels[0]?.Length
            ?? throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));
        for (int channel = 1; channel < channels.Count; channel++)
        {
            if (channels[channel] is null)
                throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));
            if (channels[channel].Length != length)
                throw new ArgumentException("All channel buffers must have the same length.",
                    nameof(channels));
        }

        if (mode == RestorationAuditionMode.Stereo || channels.Count == 1)
            return Clone(channels);

        int selectedChannel = mode == RestorationAuditionMode.Left ? 0 : 1;
        float[] selected = channels[selectedChannel];
        return [(float[])selected.Clone(), (float[])selected.Clone()];
    }

    private static void ValidatePair(IReadOnlyList<float[]> dry, IReadOnlyList<float[]> processed)
    {
        ArgumentNullException.ThrowIfNull(dry);
        ArgumentNullException.ThrowIfNull(processed);
        if (dry.Count != processed.Count)
            throw new ArgumentException("Dry and processed audio must have the same channel count.");

        int length = dry.Count == 0 ? 0 : dry[0]?.Length
            ?? throw new ArgumentException("Channel buffers cannot be null.", nameof(dry));
        for (int c = 0; c < dry.Count; c++)
        {
            if (dry[c] is null || processed[c] is null)
                throw new ArgumentException("Channel buffers cannot be null.");
            if (dry[c].Length != length || processed[c].Length != length)
                throw new ArgumentException("Dry and processed channel lengths must match.");
        }
    }
}
