using System.Collections.Concurrent;

namespace WaveLab.Audio.Dsp;

/// <summary>Settings for <see cref="ConstantQ"/>.</summary>
/// <param name="BinsPerOctave">Resolution. 12 is a semitone, 36 a third of one.</param>
/// <param name="MinimumFrequency">Bottom of the analysis.</param>
/// <param name="MaximumFrequency">Top of it, clamped below Nyquist.</param>
/// <param name="MaximumWindow">
/// The longest window any bin may use, which is what makes the transform affordable — and what
/// bounds the resolution at the bottom. See the class remarks.
/// </param>
/// <param name="Hop">Frame advance, in samples.</param>
public readonly record struct ConstantQSettings(
    int BinsPerOctave = 36,
    double MinimumFrequency = 30,
    double MaximumFrequency = 20_000,
    int MaximumWindow = 16384,
    int Hop = 512)
{
    /// <remarks>Spelled out rather than <c>new()</c>, which zero-initialises a record struct.</remarks>
    public static ConstantQSettings Default { get; } = new(
        BinsPerOctave: 36,
        MinimumFrequency: 30,
        MaximumFrequency: 20_000,
        MaximumWindow: 16384,
        Hop: 512);

    internal ConstantQSettings OrDefault() => BinsPerOctave <= 0 ? Default : this;
}

/// <summary>
/// A constant-Q spectrogram: every octave gets the same number of bins, which is where hearing
/// already is.
/// </summary>
/// <remarks>
/// <para>
/// An ordinary transform has one bandwidth everywhere. At 2048 points and 44.1 kHz that is 21.5 Hz,
/// which at the bottom of the picture cannot tell 50 Hz mains from 60 — the first thing anyone wants
/// to know about a hum — and at the top spends a thousand bins on the octave above 10 kHz that
/// nobody needs, while giving all of them a 46 ms window so a click is three frames wide however
/// short it was. Constant-Q makes the bandwidth proportional to the centre frequency instead: fine
/// in frequency at the bottom, fine in <em>time</em> at the top, which is the multi-resolution the
/// two ends actually want.
/// </para>
/// <para>
/// <b>It is computed the Brown–Puckette way, not as a filter bank.</b> Correlating the signal
/// against a windowed complex exponential per bin per frame is the definition and is far too slow —
/// the bottom bins want windows tens of thousands of samples long. Instead each bin's kernel is
/// transformed once, up front, and stored: a windowed exponential is narrow in frequency, so its
/// transform is a handful of significant values and everything else is discarded. One FFT of the
/// frame then multiplies against every bin's sparse kernel at once, and the whole transform costs
/// one FFT plus a few thousand multiplies per frame however many bins there are.
/// </para>
/// <para>
/// <b>The window is capped, and that cap is the honest limit of the thing.</b> True constant-Q at 36
/// bins per octave wants a window of Q·fs/f, which at 30 Hz is seventy-five thousand samples — 1.7
/// seconds, for one row of one frame. Capping it means the bins below about 140 Hz stop being
/// constant-Q and become a fixed-window analysis instead. That is not a compromise of the useful
/// part: a 16384-point window at 44.1 kHz resolves 2.7 Hz, so 50 Hz and 60 Hz mains and every
/// harmonic of either are still separated, which is the whole reason to be down there.
/// </para>
/// </remarks>
public static class ConstantQ
{
    /// <summary>Kernel values below this fraction of the peak are dropped.</summary>
    /// <remarks>
    /// What makes the kernel sparse, and so what makes the transform fast. A windowed exponential's
    /// transform is concentrated around its own centre; the tail is the window's sidelobes, and
    /// keeping it would cost most of the multiplications to add a hundredth of a decibel.
    /// </remarks>
    private const double KernelThreshold = 0.0054;

    private static readonly ConcurrentDictionary<(int Rate, ConstantQSettings Settings), Kernel> Cache = new();

    /// <summary>The transformed, thresholded kernels — built once per shape of analysis.</summary>
    private sealed class Kernel
    {
        public required int Size { get; init; }
        public required double[] Frequencies { get; init; }
        public required int[][] Indices { get; init; }
        public required float[][] Re { get; init; }
        public required float[][] Im { get; init; }
        public int Bins => Frequencies.Length;
    }

    /// <summary>How many bins an analysis of this shape produces, without running one.</summary>
    public static int BinCount(int sampleRate, ConstantQSettings settings) =>
        Build(sampleRate, settings.OrDefault()).Bins;

    /// <summary>Centre frequencies of the bins, bottom first.</summary>
    public static double[] Frequencies(int sampleRate, ConstantQSettings settings) =>
        (double[])Build(sampleRate, settings.OrDefault()).Frequencies.Clone();

    /// <summary>
    /// Analyses a span into a constant-Q spectrogram, in the same shape the linear analysis returns
    /// so the display need not care which it was given.
    /// </summary>
    public static SpectrogramData Analyze(float[] samples, int from, int count, int sampleRate,
        ConstantQSettings settings = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        settings = settings.OrDefault();
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        Kernel kernel = Build(sampleRate, settings);
        int hop = Math.Max(1, settings.Hop);
        int size = kernel.Size;

        from = Math.Clamp(from, 0, Math.Max(0, samples.Length));
        count = Math.Clamp(count, 0, Math.Max(0, samples.Length - from));

        int frames = count <= 0 ? 0 : Math.Max(1, count / hop);
        int bins = kernel.Bins;
        var magnitude = new float[(long)frames * bins <= int.MaxValue ? frames * bins : 0];
        if (magnitude.Length == 0 || bins == 0)
            return new SpectrogramData(0, Math.Max(bins, 1), [], sampleRate, size, hop, from,
                kernel.Frequencies);

        var done = 0;
        Parallel.For(0, frames,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Re: new float[size], Im: new float[size]),
            (f, _, scratch) =>
            {
                // The frame is centred on its own sample, like the linear analysis, so the two put
                // the same event at the same place on the time axis.
                int centre = from + f * hop;
                int start = centre - size / 2;

                for (int i = 0; i < size; i++)
                {
                    int index = start + i;
                    scratch.Re[i] = (uint)index < (uint)samples.Length ? samples[index] : 0f;
                    scratch.Im[i] = 0f;
                }
                Fft.Forward(scratch.Re, scratch.Im);

                int row = f * bins;
                for (int b = 0; b < bins; b++)
                {
                    int[] indices = kernel.Indices[b];
                    float[] kernelRe = kernel.Re[b], kernelIm = kernel.Im[b];

                    double sumRe = 0, sumIm = 0;
                    for (int t = 0; t < indices.Length; t++)
                    {
                        int at = indices[t];
                        float xr = scratch.Re[at], xi = scratch.Im[at];
                        float kr = kernelRe[t], ki = kernelIm[t];
                        sumRe += xr * kr - xi * ki;
                        sumIm += xr * ki + xi * kr;
                    }

                    double amplitude = Math.Sqrt(sumRe * sumRe + sumIm * sumIm);
                    magnitude[row + b] = (float)(20 * Math.Log10(Math.Max(1e-12, amplitude)));
                }

                if (progress != null && (Interlocked.Increment(ref done) & 15) == 0)
                    progress.Report(Math.Min(1, done / (double)frames));
                return scratch;
            },
            _ => { });

        progress?.Report(1);
        return new SpectrogramData(frames, bins, magnitude, sampleRate, size, hop, from,
            kernel.Frequencies);
    }

    // ── the kernels ──────────────────────────────────────────────

    private static Kernel Build(int sampleRate, ConstantQSettings settings) =>
        Cache.GetOrAdd((sampleRate, settings), key => Construct(key.Rate, key.Settings));

    private static Kernel Construct(int sampleRate, ConstantQSettings settings)
    {
        int perOctave = Math.Clamp(settings.BinsPerOctave, 1, 192);
        double lowest = Math.Clamp(settings.MinimumFrequency, 1, sampleRate * 0.4);
        double highest = Math.Clamp(settings.MaximumFrequency, lowest * 2, sampleRate * 0.475);

        // Q is fixed by the resolution: the bandwidth that puts adjacent bins exactly one step apart.
        double q = 1.0 / (Math.Pow(2, 1.0 / perOctave) - 1);

        int size = Fft.NextPowerOfTwo(Math.Clamp(settings.MaximumWindow, 256, 1 << 18));

        var frequencies = new List<double>();
        for (int k = 0; ; k++)
        {
            double f = lowest * Math.Pow(2, k / (double)perOctave);
            if (f > highest) break;
            frequencies.Add(f);
        }

        int bins = frequencies.Count;
        var indices = new int[bins][];
        var kernelRe = new float[bins][];
        var kernelIm = new float[bins][];

        var re = new float[size];
        var im = new float[size];

        for (int b = 0; b < bins; b++)
        {
            double f = frequencies[b];
            var length = (int)Math.Min(size, Math.Round(q * sampleRate / f));
            length = Math.Max(4, length - (length & 1));       // even, so it centres exactly

            Array.Clear(re);
            Array.Clear(im);

            // Hann, and normalised so a full-scale sinusoid at the bin's own centre reads 0 dB —
            // the same calibration the linear analysis uses, so the two displays agree about level.
            double windowSum = 0;
            for (int n = 0; n < length; n++) windowSum += 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / length);
            double scale = windowSum > 0 ? 2.0 / windowSum : 0;

            int offset = (size - length) / 2;
            for (int n = 0; n < length; n++)
            {
                double window = (0.5 - 0.5 * Math.Cos(2 * Math.PI * n / length)) * scale;

                // Phase measured from the centre of the window, so a kernel's phase does not depend
                // on where in the buffer it happens to sit.
                double phase = 2 * Math.PI * f * (n - length / 2.0) / sampleRate;
                re[offset + n] = (float)(window * Math.Cos(phase));
                im[offset + n] = (float)(window * Math.Sin(phase));
            }

            Fft.Forward(re, im);

            double peak = 0;
            for (int i = 0; i < size; i++)
                peak = Math.Max(peak, Math.Sqrt((double)re[i] * re[i] + (double)im[i] * im[i]));

            double cut = peak * KernelThreshold;
            var keptIndices = new List<int>();
            var keptRe = new List<float>();
            var keptIm = new List<float>();

            for (int i = 0; i < size; i++)
            {
                double magnitude = Math.Sqrt((double)re[i] * re[i] + (double)im[i] * im[i]);
                if (magnitude < cut) continue;

                keptIndices.Add(i);

                // Conjugated and divided by the transform length: the frame's spectrum is correlated
                // against the kernel, and Parseval puts the 1/N here rather than in the inner loop.
                keptRe.Add((float)(re[i] / size));
                keptIm.Add((float)(-im[i] / size));
            }

            indices[b] = [.. keptIndices];
            kernelRe[b] = [.. keptRe];
            kernelIm[b] = [.. keptIm];
        }

        return new Kernel
        {
            Size = size,
            Frequencies = [.. frequencies],
            Indices = indices,
            Re = kernelRe,
            Im = kernelIm,
        };
    }
}
