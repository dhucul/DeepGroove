namespace WaveLab.Audio;

/// <summary>
/// High-quality, linear-phase sample-rate conversion using a cached polyphase
/// Blackman-Harris-windowed sinc filter.
/// </summary>
public static class Resampler
{
    private const int HalfTaps = 32;
    private const int TapCount = HalfTaps * 2;
    private const int FirstTap = -HalfTaps + 1;
    private const int MaximumKernelPhases = 4096;
    private const int KernelCacheCapacity = 8;
    private const int CancellationCheckMask = 0x0FFF;

    // Kernels for common audio-rate ratios are small (48 kHz <-> 44.1 kHz uses
    // only 147/160 phases). Keep a bounded cache so track-at-a-time CD transfers
    // do not repeatedly rebuild them, without retaining arbitrary rate ratios forever.
    private static readonly object KernelCacheLock = new();
    private static readonly Dictionary<KernelKey, Kernel> KernelCache = [];
    private static readonly Queue<KernelKey> KernelCacheOrder = [];

    /// <summary>Resamples equally-sized, deinterleaved channels.</summary>
    public static float[][] Resample(IReadOnlyList<float[]> channels, int srcRate, int dstRate)
        => Resample(channels, srcRate, dstRate, CancellationToken.None);

    /// <summary>
    /// Resamples equally-sized, deinterleaved channels. Progress is reported from
    /// zero through one, and cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static float[][] Resample(
        IReadOnlyList<float[]> channels,
        int srcRate,
        int dstRate,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (srcRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(srcRate), "The source sample rate must be positive.");
        if (dstRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(dstRate), "The destination sample rate must be positive.");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.0);

        int channelCount = channels.Count;
        if (channelCount == 0)
        {
            progress?.Report(1.0);
            return [];
        }

        var sourceChannels = new float[channelCount][];
        int sourceLength = -1;
        for (int channel = 0; channel < channelCount; channel++)
        {
            float[] source = channels[channel]
                ?? throw new ArgumentException("Channels cannot contain null arrays.", nameof(channels));

            if (sourceLength < 0)
                sourceLength = source.Length;
            else if (source.Length != sourceLength)
                throw new ArgumentException("All channels must have the same sample count.", nameof(channels));

            sourceChannels[channel] = source;
        }

        if (srcRate == dstRate)
            return CopyChannels(sourceChannels, cancellationToken, progress);

        int destinationLength = GetDestinationLength(sourceLength, srcRate, dstRate);
        var result = AllocateChannels(channelCount, destinationLength);
        if (destinationLength == 0)
        {
            progress?.Report(1.0);
            return result;
        }

        int greatestCommonDivisor = GreatestCommonDivisor(srcRate, dstRate);
        int sourceStep = srcRate / greatestCommonDivisor;
        int exactPhaseCount = dstRate / greatestCommonDivisor;
        int kernelPhaseCount = Math.Min(exactPhaseCount, MaximumKernelPhases);
        double ratio = (double)dstRate / srcRate;
        double cutoff = Math.Min(1.0, ratio) * 0.945;
        Kernel kernel = GetKernel(cutoff, kernelPhaseCount, cancellationToken);

        // Advance the source location as an exact rational number. This avoids
        // accumulated floating-point drift and keeps every channel sample-aligned.
        long sourceCenter = 0;
        long sourcePhase = 0;
        int wholeSourceStep = sourceStep / exactPhaseCount;
        int fractionalSourceStep = sourceStep % exactPhaseCount;
        long progressStride = Math.Max(1, destinationLength / 1000);
        long nextProgressSample = progressStride;
        var sums = new double[channelCount];

        for (int destinationIndex = 0; destinationIndex < destinationLength; destinationIndex++)
        {
            if ((destinationIndex & CancellationCheckMask) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int phaseIndex;
            double phaseBlend;
            if (kernelPhaseCount == exactPhaseCount)
            {
                phaseIndex = (int)sourcePhase;
                phaseBlend = 0.0;
            }
            else
            {
                long scaledPhase = sourcePhase * kernelPhaseCount;
                phaseIndex = (int)(scaledPhase / exactPhaseCount);
                phaseBlend = (scaledPhase % exactPhaseCount) / (double)exactPhaseCount;
            }

            int kernelOffset = phaseIndex * TapCount;
            int nextKernelOffset = kernelOffset + TapCount;
            long firstSource = sourceCenter + FirstTap;
            int firstValidTap = firstSource < 0 ? (int)Math.Min(TapCount, -firstSource) : 0;
            long availableSamples = sourceLength - firstSource;
            int lastValidTap = availableSamples <= 0
                ? 0
                : (int)Math.Min(TapCount, availableSamples);

            Array.Clear(sums);
            double weightSum = 0.0;
            bool isInterior = firstValidTap == 0 && lastValidTap == TapCount;

            for (int tap = firstValidTap; tap < lastValidTap; tap++)
            {
                double weight = kernel.Weights[kernelOffset + tap];
                if (phaseBlend != 0.0)
                {
                    double nextWeight = kernel.Weights[nextKernelOffset + tap];
                    weight += (nextWeight - weight) * phaseBlend;
                }

                if (!isInterior)
                    weightSum += weight;

                int sourceIndex = (int)(firstSource + tap);
                for (int channel = 0; channel < channelCount; channel++)
                {
                    float sample = sourceChannels[channel][sourceIndex];
                    if (float.IsFinite(sample))
                        sums[channel] += sample * weight;
                }
            }

            if (isInterior)
                weightSum = 1.0; // Every precomputed phase is DC-normalized.

            if (double.IsFinite(weightSum) && Math.Abs(weightSum) > 1e-15)
            {
                double inverseWeight = 1.0 / weightSum;
                for (int channel = 0; channel < channelCount; channel++)
                    result[channel][destinationIndex] = ToFiniteFloat(sums[channel] * inverseWeight);
            }
            else
            {
                int nearestSource = (int)Math.Clamp(sourceCenter, 0L, sourceLength - 1L);
                for (int channel = 0; channel < channelCount; channel++)
                    result[channel][destinationIndex] = Sanitize(sourceChannels[channel][nearestSource]);
            }

            sourceCenter += wholeSourceStep;
            sourcePhase += fractionalSourceStep;
            if (sourcePhase >= exactPhaseCount)
            {
                sourcePhase -= exactPhaseCount;
                sourceCenter++;
            }

            if (destinationIndex + 1 >= nextProgressSample)
            {
                progress?.Report((destinationIndex + 1) / (double)destinationLength);
                nextProgressSample += progressStride;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1.0);
        return result;
    }

    private static float[][] CopyChannels(
        IReadOnlyList<float[]> sourceChannels,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        var copy = new float[sourceChannels.Count][];
        int sampleCount = sourceChannels[0].Length;
        long totalSamples = (long)sourceChannels.Count * sampleCount;
        long progressStride = Math.Max(1L, totalSamples / 1000L);
        long nextProgressSample = progressStride;
        long completedSamples = 0;

        for (int channel = 0; channel < sourceChannels.Count; channel++)
        {
            float[] source = sourceChannels[channel];
            var destination = new float[sampleCount];
            for (int sample = 0; sample < sampleCount; sample++)
            {
                if ((sample & CancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                destination[sample] = Sanitize(source[sample]);
                completedSamples++;
                if (completedSamples >= nextProgressSample)
                {
                    progress?.Report(completedSamples / (double)totalSamples);
                    nextProgressSample += progressStride;
                }
            }
            copy[channel] = destination;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1.0);
        return copy;
    }

    private static float[][] AllocateChannels(int channelCount, int sampleCount)
    {
        var channels = new float[channelCount][];
        for (int channel = 0; channel < channelCount; channel++)
            channels[channel] = new float[sampleCount];
        return channels;
    }

    private static int GetDestinationLength(int sourceLength, int srcRate, int dstRate)
    {
        double exactLength = (long)sourceLength * dstRate / (double)srcRate;
        double roundedLength = Math.Round(exactLength, MidpointRounding.ToEven);
        if (roundedLength > int.MaxValue)
            throw new ArgumentException("The resampled audio would exceed the maximum supported sample count.");
        return (int)roundedLength;
    }

    private static Kernel GetKernel(double cutoff, int phaseCount, CancellationToken cancellationToken)
    {
        var key = new KernelKey(BitConverter.DoubleToInt64Bits(cutoff), phaseCount);
        lock (KernelCacheLock)
        {
            if (KernelCache.TryGetValue(key, out Kernel? cached))
                return cached;
        }

        Kernel created = BuildKernel(cutoff, phaseCount, cancellationToken);

        lock (KernelCacheLock)
        {
            if (KernelCache.TryGetValue(key, out Kernel? cached))
                return cached;

            if (KernelCache.Count >= KernelCacheCapacity)
            {
                KernelKey oldest = KernelCacheOrder.Dequeue();
                KernelCache.Remove(oldest);
            }

            KernelCache.Add(key, created);
            KernelCacheOrder.Enqueue(key);
            return created;
        }
    }

    private static Kernel BuildKernel(double cutoff, int phaseCount, CancellationToken cancellationToken)
    {
        // Include an endpoint row so uncommon ratios with more than 4096 exact
        // phases can interpolate smoothly across the final phase interval.
        var weights = new double[(phaseCount + 1) * TapCount];
        for (int phase = 0; phase <= phaseCount; phase++)
        {
            if ((phase & 0x3F) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double fraction = phase / (double)phaseCount;
            int rowOffset = phase * TapCount;
            double sum = 0.0;

            for (int tap = 0; tap < TapCount; tap++)
            {
                double distance = FirstTap + tap - fraction;
                double sincArgument = distance * cutoff;
                double sinc = sincArgument == 0.0
                    ? 1.0
                    : Math.Sin(Math.PI * sincArgument) / (Math.PI * sincArgument);

                double windowPosition = distance / HalfTaps;
                double window = Math.Abs(windowPosition) <= 1.0
                    ? 0.35875
                      + 0.48829 * Math.Cos(Math.PI * windowPosition)
                      + 0.14128 * Math.Cos(2.0 * Math.PI * windowPosition)
                      + 0.01168 * Math.Cos(3.0 * Math.PI * windowPosition)
                    : 0.0;

                double weight = sinc * window;
                weights[rowOffset + tap] = weight;
                sum += weight;
            }

            if (!double.IsFinite(sum) || Math.Abs(sum) <= 1e-15)
                throw new InvalidOperationException("Unable to construct a stable resampling kernel.");

            double inverseSum = 1.0 / sum;
            for (int tap = 0; tap < TapCount; tap++)
                weights[rowOffset + tap] *= inverseSum;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new Kernel(weights);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }

    private static float Sanitize(float sample) => float.IsFinite(sample) ? sample : 0f;

    private static float ToFiniteFloat(double sample)
    {
        if (double.IsNaN(sample))
            return 0f;
        if (sample >= float.MaxValue)
            return float.MaxValue;
        if (sample <= -float.MaxValue)
            return -float.MaxValue;
        return (float)sample;
    }

    private readonly record struct KernelKey(long CutoffBits, int PhaseCount);

    private sealed class Kernel(double[] weights)
    {
        public double[] Weights { get; } = weights;
    }
}
