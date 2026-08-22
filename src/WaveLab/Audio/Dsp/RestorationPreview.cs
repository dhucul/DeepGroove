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

    /// <summary>
    /// Return what a restoration pass took out: the sample-aligned difference between the
    /// audio that went in and the audio that was committed. This is the complement of
    /// <see cref="MixRange"/> and is deliberately the only way the app derives removed
    /// material — a tool reporting its own residual can disagree with what actually ran,
    /// and a dry/wet blend is handled for free (dry − blend = wet · (dry − processed)).
    /// </summary>
    /// <param name="dry">The audio before processing. May be longer than <paramref name="processed"/>.</param>
    /// <param name="processed">The committed result, whose length sets the result length.</param>
    /// <param name="dryOffset">Where <paramref name="processed"/> starts inside <paramref name="dry"/>.</param>
    public static float[][] Difference(IReadOnlyList<float[]> dry, IReadOnlyList<float[]> processed,
        int dryOffset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dry);
        ArgumentNullException.ThrowIfNull(processed);
        if (dry.Count != processed.Count)
            throw new ArgumentException("Dry and processed audio must have the same channel count.");
        ArgumentOutOfRangeException.ThrowIfNegative(dryOffset);

        int count = processed.Count == 0 ? 0 : processed[0]?.Length
            ?? throw new ArgumentException("Channel buffers cannot be null.", nameof(processed));
        var result = new float[processed.Count][];
        for (int c = 0; c < processed.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dry[c] is null || processed[c] is null)
                throw new ArgumentException("Channel buffers cannot be null.");
            if (processed[c].Length != count)
                throw new ArgumentException("Processed channel lengths must match.", nameof(processed));
            if (dry[c].Length < dryOffset + count)
                throw new ArgumentException("The dry audio is shorter than the processed range.", nameof(dry));

            float[] source = dry[c];
            float[] wet = processed[c];
            var removed = new float[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                removed[i] = source[dryOffset + i] - wet[i];
            }
            result[c] = removed;
        }
        return result;
    }

    /// <summary>How loud a residual is, in the two ways the monitor lift needs to know.</summary>
    public readonly record struct ResidualLevels(float Peak, float Rms);

    /// <summary>
    /// Peak and RMS in one pass. Both are wanted together and only ever together, and the caller
    /// is holding a buffer the size of the range — two passes over that is memory traffic the UI
    /// thread must not be asked for.
    /// </summary>
    public static ResidualLevels MeasureLevels(IReadOnlyList<float[]> channels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        float peak = 0f;
        double sum = 0;
        long count = 0;
        for (int c = 0; c < channels.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] samples = channels[c]
                ?? throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = samples[i];
                float magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
                sum += (double)sample * sample;
            }
            count += samples.Length;
        }
        return new ResidualLevels(peak, count == 0 ? 0f : (float)Math.Sqrt(sum / count));
    }

    /// <summary>Largest absolute sample across every channel; 0 for empty or silent audio.</summary>
    public static float PeakOf(IReadOnlyList<float[]> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        float peak = 0f;
        for (int c = 0; c < channels.Count; c++)
        {
            float[] samples = channels[c]
                ?? throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));
            for (int i = 0; i < samples.Length; i++)
            {
                float magnitude = Math.Abs(samples[i]);
                if (magnitude > peak) peak = magnitude;
            }
        }
        return peak;
    }

    /// <summary>Root-mean-square across every channel; 0 for empty or silent audio.</summary>
    public static float RmsOf(IReadOnlyList<float[]> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        double sum = 0;
        long count = 0;
        for (int c = 0; c < channels.Count; c++)
        {
            float[] samples = channels[c]
                ?? throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));
            for (int i = 0; i < samples.Length; i++) sum += (double)samples[i] * samples[i];
            count += samples.Length;
        }
        return count == 0 ? 0f : (float)Math.Sqrt(sum / count);
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
