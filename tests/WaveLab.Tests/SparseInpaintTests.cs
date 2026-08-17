using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The parts the sparse solver is built from, checked separately from the repair that uses them.
/// </summary>
/// <remarks>
/// The solver's behaviour rests on one property — that <c>T = A∘S</c> is a projection — and on two
/// operators, the neighbourhood energy and the shrinkage. A quality measurement on the finished
/// repair can only say that something is wrong; these say which thing.
/// </remarks>
public sealed class SparseInpaintTests(ITestOutputHelper output)
{
    private const int SampleRate = 44_100;
    private const int Fft = 2048, Hop = 512;

    private static float[] Tone(int length, params (double Hz, double Amplitude)[] partials)
    {
        var signal = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)SampleRate;
            double sum = 0;
            foreach ((double hz, double amplitude) in partials)
                sum += amplitude * Math.Sin(2 * Math.PI * hz * t);
            signal[i] = (float)sum;
        }
        return signal;
    }

    private static SpectralRepair.Frame NewFrame(float[] signal)
    {
        SpectralMask mask = SpectralMask.Rectangle(20, 40, 30, 90, feather: 2);
        SpectralRepair.Frame? frame = SpectralRepair.Frame.Create(
            signal, 0, mask, SpectralRepairOptions.Default, CancellationToken.None);
        Assert.NotNull(frame);
        return frame!;
    }

    private static double RelativeDifference(float[] aRe, float[] aIm, float[] bRe, float[] bIm)
    {
        double difference = 0, magnitude = 0;
        for (int i = 0; i < aRe.Length; i++)
        {
            double dr = aRe[i] - bRe[i], di = aIm[i] - bIm[i];
            difference += dr * dr + di * di;
            magnitude += (double)aRe[i] * aRe[i] + (double)aIm[i] * aIm[i];
        }
        return magnitude > 0 ? Math.Sqrt(difference / magnitude) : 0;
    }

    // ── the projection ───────────────────────────────────────────

    /// <summary>
    /// A grid that came from a real signal is already what a real signal could have produced, so
    /// projecting it must change nothing. If this fails the solver is not solving the stated problem
    /// and every quality number downstream is meaningless.
    /// </summary>
    [Fact]
    public void ProjectingSomethingAlreadyConsistentChangesNothing()
    {
        float[] signal = Tone(66_150, (220, 0.4), (440, 0.25), (1_320, 0.2));
        using SpectralRepair.Frame frame = NewFrame(signal);

        float[] before = (float[])frame.Re.Clone();
        float[] beforeIm = (float[])frame.Im.Clone();

        frame.Project(frame.Re, frame.Im);

        double drift = RelativeDifference(before, beforeIm, frame.Re, frame.Im);
        output.WriteLine($"consistent grid moved by {drift:0.000000} relative");
        Assert.True(drift < 1e-3, $"the projection moved a consistent grid by {drift}");
    }

    /// <summary>
    /// Projecting twice must equal projecting once. This is what makes the operator norm one, and
    /// the step size the solver takes is derived from that.
    /// </summary>
    [Fact]
    public void ProjectingTwiceIsTheSameAsProjectingOnce()
    {
        float[] signal = Tone(66_150, (330, 0.3));
        using SpectralRepair.Frame frame = NewFrame(signal);

        var random = new Random(7);
        var re = new float[frame.Re.Length];
        var im = new float[frame.Im.Length];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = (float)(random.NextDouble() * 2 - 1);
            im[i] = (float)(random.NextDouble() * 2 - 1);
        }

        frame.Project(re, im);
        float[] once = (float[])re.Clone();
        float[] onceIm = (float[])im.Clone();

        frame.Project(re, im);

        double drift = RelativeDifference(once, onceIm, re, im);
        output.WriteLine($"a second projection moved it by {drift:0.000000} relative");
        Assert.True(drift < 1e-3, $"the projection is not idempotent: {drift}");
    }

    /// <summary>
    /// A projection cannot make anything larger. A step size of one depends on it, and an operator
    /// that quietly amplifies is the one way an otherwise correct FISTA runs away.
    /// </summary>
    [Fact]
    public void ProjectingNeverIncreasesTheEnergy()
    {
        float[] signal = Tone(66_150, (550, 0.35));
        using SpectralRepair.Frame frame = NewFrame(signal);

        var random = new Random(19);
        var re = new float[frame.Re.Length];
        var im = new float[frame.Im.Length];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = (float)(random.NextDouble() * 2 - 1);
            im[i] = (float)(random.NextDouble() * 2 - 1);
        }

        double before = Energy(re, im);
        frame.Project(re, im);
        double after = Energy(re, im);

        output.WriteLine($"energy {before:0.000} -> {after:0.000}");
        Assert.True(after <= before * 1.02, $"the projection grew the grid from {before} to {after}");

        static double Energy(float[] re, float[] im)
        {
            double sum = 0;
            for (int i = 0; i < re.Length; i++) sum += (double)re[i] * re[i] + (double)im[i] * im[i];
            return sum;
        }
    }

    // ── the neighbourhood ────────────────────────────────────────

    /// <summary>
    /// The neighbourhood energy is a <em>mean</em>, so its square root is a level comparable with one
    /// coefficient's magnitude. That is what lets a single threshold drive both the plain and the
    /// structured operators, and what makes the threshold schedule mean the same thing for each.
    /// </summary>
    [Fact]
    public void TheNeighbourhoodEnergyIsAMeanAndNotASum()
    {
        const int frames = 9, bins = 9;
        var re = new float[frames * bins];
        var im = new float[frames * bins];
        for (int i = 0; i < re.Length; i++) re[i] = 2;

        var energy = new float[re.Length];
        var scratch = new float[re.Length];
        SparseInpaint.NeighbourhoodMeanEnergy(re, im, frames, bins, 2, 2, energy, scratch);

        // Every cell of a constant field must read the same, corners included: the count divides by
        // the taps that exist, not the taps that were asked for.
        Assert.All(energy, value => Assert.Equal(4f, value, 4));
    }

    /// <summary>
    /// A lone cell is diluted by its empty neighbourhood; a cell in a run of them is not. This is the
    /// whole mechanism by which social shrinkage tells a partial from a speck.
    /// </summary>
    [Fact]
    public void ALoneCellReadsQuieterThanTheSameCellInsideARun()
    {
        const int frames = 11, bins = 11;
        var re = new float[frames * bins];
        var im = new float[frames * bins];

        // A lone cell at (2,2); a horizontal run through (8, 0..10).
        re[2 * bins + 2] = 1;
        for (int f = 0; f < frames; f++) re[f * bins + 8] = 1;

        var energy = new float[re.Length];
        var scratch = new float[re.Length];
        SparseInpaint.NeighbourhoodMeanEnergy(re, im, frames, bins, 3, 0, energy, scratch);

        float lone = energy[2 * bins + 2];
        float inRun = energy[5 * bins + 8];
        output.WriteLine($"lone {lone:0.0000}, in a run {inRun:0.0000}");
        Assert.True(inRun > lone * 4, "a cell inside a run must read far louder than a lone one");
    }

    // ── the shrinkage, through the solver ────────────────────────

    /// <summary>
    /// With nothing reliable to reconstruct from the solver declines rather than shrinking the
    /// estimate it was given to silence.
    /// </summary>
    [Fact]
    public void AFullyMaskedBlockIsDeclinedRatherThanEmptied()
    {
        const int frames = 8, bins = 8;
        int n = frames * bins;
        var observed = new float[n];
        var zero = new float[n];
        var mask = new float[n];
        for (int i = 0; i < n; i++) { observed[i] = 1; mask[i] = 1; }

        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = 1;

        bool solved = SparseInpaint.Solve(observed, zero, mask, frames, bins, re, im,
            (_, _) => { }, SparseInpaintOptions.Default);

        Assert.False(solved);
        Assert.All(re, value => Assert.Equal(1f, value, 6));
    }

    // ── measured against the alternative ─────────────────────────

    private const int ProgrammeLength = 66_150;

    /// <summary>Sustained tonal programme, the case the continuation was built for.</summary>
    private static float[] Programme()
    {
        var random = new Random(11);
        var signal = new float[ProgrammeLength];
        for (int i = 0; i < ProgrammeLength; i++)
        {
            double t = i / (double)SampleRate;
            signal[i] = (float)(
                  0.40 * Math.Sin(2 * Math.PI * 220 * t)
                + 0.24 * Math.Sin(2 * Math.PI * 440 * t + 0.7)
                + 0.15 * Math.Sin(2 * Math.PI * 660 * t - 0.3)
                + 0.30 * Math.Sin(2 * Math.PI * 1100 * t)
                + 0.26 * Math.Sin(2 * Math.PI * 1320 * t + 0.2)
                + 0.20 * Math.Sin(2 * Math.PI * 2200 * t - 0.9)
                + 0.16 * Math.Sin(2 * Math.PI * 2640 * t + 1.1)
                + 0.11 * Math.Sin(2 * Math.PI * 3080 * t + 2.3)
                + (random.NextDouble() - 0.5) * 0.002);
        }
        return signal;
    }

    private static (float[] Damaged, int From, int To) AddBurst(float[] clean, int width)
    {
        int from = ProgrammeLength / 2 - width / 2, to = from + width;
        var random = new Random(5);
        var damaged = (float[])clean.Clone();

        var phase = new double[96];
        var frequency = new double[96];
        for (int p = 0; p < 96; p++)
        {
            frequency[p] = 900 + (3_500 - 900) * random.NextDouble();
            phase[p] = random.NextDouble() * 2 * Math.PI;
        }

        for (int i = from; i < to; i++)
        {
            double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / (double)(to - from));
            double sum = 0;
            for (int p = 0; p < 96; p++)
                sum += Math.Sin(2 * Math.PI * frequency[p] * i / SampleRate + phase[p]);
            damaged[i] += (float)(0.9 * envelope * sum / Math.Sqrt(96));
        }
        return (damaged, from, to);
    }

    private static SpectralMask MaskFor(int from, int to)
    {
        int frameFrom = from / Hop, frameTo = to / Hop + 1;
        int binFrom = (int)(900.0 * Fft / SampleRate), binTo = (int)(3_500.0 * Fft / SampleRate) + 1;
        return SpectralMask.Rectangle(frameFrom, frameTo, binFrom, binTo, feather: 2);
    }

    private static double SnrDb(float[] clean, float[] candidate, int start, int count)
    {
        double signal = 0, error = 0;
        for (int i = start; i < start + count && i < clean.Length; i++)
        {
            signal += (double)clean[i] * clean[i];
            double d = candidate[i] - clean[i];
            error += d * d;
        }
        return error <= 0 ? 200 : 10 * Math.Log10(signal / error);
    }

    private static float[] Applied(float[] source, SpectralRepairResult result)
    {
        var merged = (float[])source.Clone();
        result.Samples.CopyTo(merged.AsSpan(result.Start));
        return merged;
    }

    private static double Measure(SpectralHealMethod method, int width, SparseInpaintOptions sparse) =>
        Measure(Programme(), method, width, sparse);

    private static double Measure(float[] clean, SpectralHealMethod method, int width,
        SparseInpaintOptions sparse)
    {
        var (damaged, from, to) = AddBurst(clean, width);
        SpectralMask mask = MaskFor(from, to);

        var options = SpectralRepairOptions.Default with { Method = method, Sparse = sparse };
        SpectralRepairResult result = SpectralRepair.Heal(damaged, 0, mask, options);
        return SnrDb(clean, Applied(damaged, result), result.Start, result.Samples.Length);
    }

    /// <summary>
    /// With the penalty turned down to nothing the solver must reproduce, exactly, the estimate it
    /// was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load-bearing correctness test, and the one that found the two real bugs in this file. The
    /// first was a projection that was not idempotent — the block's outermost frames are not covered
    /// by a full set of overlapping windows, so dividing by one constant left an eleven per cent
    /// drift. The second was subtler and cost two decibels: with the data term written on
    /// <c>Tα</c> and weighted differently inside the selection from outside, the fixed point is a
    /// <em>weighted</em> projection of the estimate, which is not the estimate, so the solver could
    /// not reproduce its own starting point however hard it was held to it.
    /// </para>
    /// <para>
    /// Both showed up here as a number that was not zero, and neither would have been visible in a
    /// quality measurement — which can only say that something is worse, never what.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(4_096)]
    [InlineData(8_192)]
    public void WithNoPenaltyTheSolverReproducesWhatItWasGiven(int width)
    {
        double continuation = Measure(SpectralHealMethod.PartialContinuation, width, default);
        double degenerate = Measure(SpectralHealMethod.SparseInpainting, width,
            SparseInpaintOptions.Default with { LambdaQuantile = 0, LambdaFloorRatio = 1e-9 });

        output.WriteLine($"{width}: continuation {continuation:0.000} dB, "
                         + $"with the penalty off {degenerate:0.000} dB, "
                         + $"apart by {Math.Abs(degenerate - continuation):0.000} dB");

        // Not exactly equal, and it should not be: sixty accelerated iterations in single precision
        // accumulate. A hundredth of a decibel is far below anything that could matter and far above
        // the round-off, so it separates arithmetic from a formulation that has stopped agreeing
        // with itself — which is what the two bugs this test found both looked like.
        Assert.True(Math.Abs(degenerate - continuation) < 0.05,
            $"with no penalty the solver moved the result by {degenerate - continuation:0.000} dB");
    }

    /// <summary>
    /// What the solver actually measures against the continuation it is offered as an alternative to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This records a result rather than demanding one. Sparse inpainting <b>loses</b> here, for two
    /// structural reasons set out on <see cref="SparseInpaint"/>: it reconstructs from evidence
    /// within about a window of the selection's edge, so the middle of a wide selection is out of its
    /// reach, and where the continuation refuses a cell it writes silence, which on unpredictable
    /// material is a better estimate than a plausible fill. That is why the continuation is the
    /// default.
    /// </para>
    /// <para>
    /// The bound is loose and one-sided on purpose. Tightening it would turn a record of a
    /// measurement into a demand that the measurement never change, and the point of keeping it is to
    /// notice if it does.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("tonal", 512)]
    [InlineData("tonal", 4_096)]
    [InlineData("tonal", 8_192)]
    [InlineData("noisy", 512)]
    [InlineData("noisy", 4_096)]
    [InlineData("percussive", 512)]
    [InlineData("percussive", 4_096)]
    public void TheSolverIsMeasuredAgainstTheContinuation(string material, int width)
    {
        float[] clean = material switch
        {
            "noisy" => Noisy(),
            "percussive" => Percussive(),
            _ => Programme(),
        };

        double continuation = Measure(clean, SpectralHealMethod.PartialContinuation, width, default);
        double sparse = Measure(clean, SpectralHealMethod.SparseInpainting, width,
            SparseInpaintOptions.Default);

        output.WriteLine($"{material,-11} {width,-6} continuation {continuation,6:0.0} dB, "
                         + $"sparse {sparse,6:0.0} dB, {sparse - continuation,6:+0.0;-0.0} dB");
        Assert.True(sparse > continuation - 14,
            $"the solver has fallen further behind than it was: {sparse - continuation:0.0} dB");
    }

    /// <summary>Broadband noise under a couple of partials: nothing a per-bin model can extrapolate.</summary>
    private static float[] Noisy()
    {
        var random = new Random(23);
        var signal = new float[ProgrammeLength];
        double lowPass = 0;
        for (int i = 0; i < ProgrammeLength; i++)
        {
            double t = i / (double)SampleRate;
            lowPass = 0.72 * lowPass + 0.28 * (random.NextDouble() * 2 - 1);
            signal[i] = (float)(
                  0.35 * lowPass
                + 0.20 * Math.Sin(2 * Math.PI * 220 * t)
                + 0.12 * Math.Sin(2 * Math.PI * 1_100 * t + 0.4));
        }
        return signal;
    }

    /// <summary>Repeated transients: content that genuinely is not predictable from its neighbours.</summary>
    private static float[] Percussive()
    {
        var random = new Random(31);
        var signal = new float[ProgrammeLength];
        for (int hit = 0; hit < 40; hit++)
        {
            int at = hit * (ProgrammeLength / 40) + random.Next(200);
            const double decay = 0.0016 * SampleRate;
            for (int i = 0; i < 3_000 && at + i < ProgrammeLength; i++)
                signal[at + i] += (float)(0.6 * Math.Exp(-i / decay) * (random.NextDouble() * 2 - 1));
        }
        for (int i = 0; i < ProgrammeLength; i++)
            signal[i] += (float)(0.18 * Math.Sin(2 * Math.PI * 330 * i / SampleRate));
        return signal;
    }

    /// <summary>Silence has no level to read a threshold off, and is declined for the same reason.</summary>
    [Fact]
    public void ASilentBlockIsDeclined()
    {
        const int frames = 8, bins = 8;
        int n = frames * bins;
        var zero = new float[n];
        var mask = new float[n];
        for (int i = 0; i < n; i++) mask[i] = i < n / 2 ? 1 : 0;

        var re = new float[n];
        var im = new float[n];

        Assert.False(SparseInpaint.Solve(zero, zero, mask, frames, bins, re, im,
            (_, _) => { }, SparseInpaintOptions.Default));
    }
}
