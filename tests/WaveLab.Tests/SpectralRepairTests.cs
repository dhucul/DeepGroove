using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpectralRepairTests(ITestOutputHelper output)
{
    private const int SampleRate = 44_100;
    private const int Length = 66_150;          // 1.5 s
    private const int Fft = 2048, Hop = 512;

    private static SpectralRepairOptions Options => SpectralRepairOptions.Default;

    /// <summary>
    /// Sustained tonal programme with real content inside the band the mask covers. Without that
    /// content the measurement would only ever reward emptying the selection and would say nothing
    /// about whether anything was reconstructed.
    /// </summary>
    private static float[] Programme(int seed = 11)
    {
        var random = new Random(seed);
        var signal = new float[Length];
        for (int i = 0; i < Length; i++)
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

    /// <summary>
    /// A band-limited burst laid over the programme — a cough, a chair creak, a squeak: loud,
    /// short, and occupying a definite region of the plane rather than a definite span of time.
    /// </summary>
    /// <remarks>
    /// Built as a dense sum of sinusoids inside the band rather than by filtering noise. A one-pole
    /// pair is not a bandpass in any useful sense: most of its energy lands outside the stated edges,
    /// so the mask correctly leaves it behind and the measurement reads as though the repair had
    /// failed. A defect has to be inside the region drawn around it, or the number describes the test
    /// signal rather than the algorithm.
    /// </remarks>
    private static (float[] Damaged, int From, int To, double Low, double High) AddBurst(
        float[] clean, int seed = 5, double amplitude = 0.9, int width = 8_000)
    {
        int from = Length / 2 - width / 2, to = from + width;
        const double low = 900, high = 3_500;
        const int partials = 96;

        var damaged = (float[])clean.Clone();
        var random = new Random(seed);
        var frequency = new double[partials];
        var phase = new double[partials];
        for (int p = 0; p < partials; p++)
        {
            frequency[p] = low + (high - low) * random.NextDouble();
            phase[p] = random.NextDouble() * 2 * Math.PI;
        }

        for (int i = from; i < to; i++)
        {
            double value = 0, t = i / (double)SampleRate;
            for (int p = 0; p < partials; p++) value += Math.Sin(2 * Math.PI * frequency[p] * t + phase[p]);
            double position = (i - from) / (double)(to - from);
            double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * position);
            damaged[i] += (float)(value / Math.Sqrt(partials) * envelope * amplitude);
        }
        return (damaged, from, to, low, high);
    }

    private static SpectralMask MaskFor(int from, int to, double low, double high, int feather = 2)
    {
        int frameFrom = from / Hop, frameTo = to / Hop + 1;
        int binFrom = (int)(low * Fft / SampleRate), binTo = (int)(high * Fft / SampleRate) + 1;
        return SpectralMask.Rectangle(frameFrom, frameTo, binFrom, binTo, feather);
    }

    /// <summary>Error against the clean original over the span the repair actually replaced.</summary>
    private static double SnrDb(float[] clean, float[] candidate, int start, int count)
    {
        double signal = 0, error = 0;
        for (int i = start; i < start + count && i < clean.Length; i++)
        {
            double difference = clean[i] - candidate[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    private static float[] Applied(float[] source, SpectralRepairResult result)
    {
        var merged = (float[])source.Clone();
        result.Samples.CopyTo(merged.AsSpan(result.Start));
        return merged;
    }

    // ── the headline measurement ─────────────────────────────────

    /// <summary>
    /// The whole point: a defect that overlaps the music in time is removed, and what it covered is
    /// rebuilt from the partials running through it. Measured against the clean original over the
    /// span actually replaced, not over a fixed window — a wide window would mostly measure audio
    /// nothing touched and report a flattering number.
    /// </summary>
    [Theory]
    [InlineData(4_096, 12.0)]
    [InlineData(8_192, 16.0)]
    [InlineData(16_384, 16.0)]
    public void HealingRemovesADefectAndRebuildsWhatItCovered(int width, double expectedDb)
    {
        float[] clean = Programme();
        var (damaged, from, to, low, high) = AddBurst(clean, width: width);
        SpectralMask mask = MaskFor(from, to, low, high);

        var stopwatch = Stopwatch.StartNew();
        SpectralRepairResult result = SpectralRepair.Heal(damaged, 0, mask, Options);
        stopwatch.Stop();

        double before = SnrDb(clean, damaged, result.Start, result.Samples.Length);
        double after = SnrDb(clean, Applied(damaged, result), result.Start, result.Samples.Length);

        output.WriteLine($"{width} samples: {before:0.0} dB -> {after:0.0} dB " +
                         $"over {result.Samples.Length} replaced, in {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(after > expectedDb, $"healing reached only {after:0.0} dB");
    }

    /// <summary>
    /// Reconstruction has to beat the alternative it is competing with, which is not the damaged
    /// audio but the selection simply taken out. Anything that cannot clear that bar is not worth
    /// running.
    /// </summary>
    [Fact]
    public void HealingBeatsSimplyRemovingTheSelection()
    {
        float[] clean = Programme();
        var (damaged, from, to, low, high) = AddBurst(clean);
        SpectralMask mask = MaskFor(from, to, low, high);

        SpectralRepairResult emptied = SpectralRepair.Attenuate(damaged, 0, mask, -200, Options);
        SpectralRepairResult healed = SpectralRepair.Heal(damaged, 0, mask, Options);

        double removed = SnrDb(clean, Applied(damaged, emptied), emptied.Start, emptied.Samples.Length);
        double rebuilt = SnrDb(clean, Applied(damaged, healed), healed.Start, healed.Samples.Length);

        output.WriteLine($"emptied {removed:0.0} dB, healed {rebuilt:0.0} dB");
        Assert.True(rebuilt > removed + 8, $"healing gained only {rebuilt - removed:0.0} dB over emptying");
    }

    /// <summary>
    /// The gate is the difference between reconstruction and invention. Opening it fully makes the
    /// fill build coherent tones out of anything, including a noise bed; closing it entirely turns
    /// healing into removal, which must agree exactly with what attenuating to silence does.
    /// </summary>
    [Fact]
    public void ClosingTheGateCompletelyIsExactlyRemovingTheSelection()
    {
        float[] clean = Programme();
        var (damaged, from, to, low, high) = AddBurst(clean);
        SpectralMask mask = MaskFor(from, to, low, high);

        SpectralRepairResult emptied = SpectralRepair.Attenuate(damaged, 0, mask, -400, Options);
        SpectralRepairResult closed = SpectralRepair.Heal(damaged, 0, mask,
            Options with { PartialDriftRadians = 0 });

        Assert.Equal(emptied.Start, closed.Start);
        double worst = 0;
        for (int i = 0; i < emptied.Samples.Length; i++)
            worst = Math.Max(worst, Math.Abs(emptied.Samples[i] - closed.Samples[i]));

        output.WriteLine($"worst difference {worst:0.000e+0}");
        Assert.True(worst < 1e-5, $"the two paths disagree by {worst}");
    }

    /// <summary>A bin carrying only noise must be emptied rather than continued as a tone.</summary>
    [Fact]
    public void NoiseIsNotMistakenForAPartial()
    {
        const int frames = 24, bins = 40;
        var random = new Random(7);
        var re = new float[frames * bins];
        var im = new float[frames * bins];

        // Bin 10 carries a steady partial; bin 20 carries noise of the same level.
        double advance = 2 * Math.PI * 10 * 0.25;
        for (int f = 0; f < frames; f++)
        {
            re[f * bins + 10] = (float)(0.5 * Math.Cos(advance * f));
            im[f * bins + 10] = (float)(0.5 * Math.Sin(advance * f));
            double phase = random.NextDouble() * 2 * Math.PI;
            re[f * bins + 20] = (float)(0.5 * Math.Cos(phase));
            im[f * bins + 20] = (float)(0.5 * Math.Sin(phase));
        }

        var keep = new float[frames * bins];
        for (int f = 8; f < 16; f++)
            for (int b = 0; b < bins; b++) keep[f * bins + b] = 1f;

        var outRe = new float[frames * bins];
        var outIm = new float[frames * bins];
        SpectralRepair.ContinuePartials(re, im, keep, frames, bins, outRe, outIm, 0.25, 0.10);

        double partial = 0, noise = 0;
        for (int f = 8; f < 16; f++)
        {
            partial += Math.Abs(outRe[f * bins + 10]) + Math.Abs(outIm[f * bins + 10]);
            noise += Math.Abs(outRe[f * bins + 20]) + Math.Abs(outIm[f * bins + 20]);
        }

        output.WriteLine($"partial kept {partial:0.00}, noise kept {noise:0.00}");
        Assert.True(partial > 2, "the steady partial should have been continued");
        Assert.Equal(0, noise, 6);
    }

    /// <summary>
    /// A continued partial has to arrive at the far edge in phase. If it does not, it beats against
    /// the audio it is spliced to — the level looks right and the join cancels.
    /// </summary>
    [Fact]
    public void AContinuedPartialArrivesAtTheFarEdgeInPhase()
    {
        const int frames = 20, bins = 8;
        var re = new float[frames * bins];
        var im = new float[frames * bins];
        double advance = 2 * Math.PI * 3 * 0.25 + 0.17;      // deliberately off the bin centre

        for (int f = 0; f < frames; f++)
        {
            re[f * bins + 3] = (float)(0.6 * Math.Cos(advance * f));
            im[f * bins + 3] = (float)(0.6 * Math.Sin(advance * f));
        }

        var keep = new float[frames * bins];
        for (int f = 6; f < 14; f++) keep[f * bins + 3] = 1f;

        var outRe = new float[frames * bins];
        var outIm = new float[frames * bins];
        SpectralRepair.ContinuePartials(re, im, keep, frames, bins, outRe, outIm, 0.25, 0.10);

        double worst = 0;
        for (int f = 6; f < 14; f++)
        {
            int index = f * bins + 3;
            worst = Math.Max(worst, Math.Abs(outRe[index] - re[index]));
            worst = Math.Max(worst, Math.Abs(outIm[index] - im[index]));
        }

        output.WriteLine($"worst deviation from the true trajectory {worst:0.0000}");
        Assert.True(worst < 0.01, $"the continuation drifted by {worst:0.0000}");
    }

    // ── attenuation ──────────────────────────────────────────────

    [Fact]
    public void AttenuatingBySixtyDecibelsEmptiesTheSelectedBand()
    {
        float[] clean = Programme();
        SpectralMask mask = MaskFor(20_000, 30_000, 300, 700);

        SpectralRepairResult result = SpectralRepair.Attenuate(clean, 0, mask, -60, Options);
        float[] applied = Applied(clean, result);

        double before = Energy(clean, 440);
        double after = Energy(applied, 440);
        double untouched = Energy(applied, 220) / Energy(clean, 220);

        output.WriteLine($"440 Hz fell {10 * Math.Log10(before / Math.Max(after, 1e-30)):0.0} dB, " +
                         $"220 Hz changed by {10 * Math.Log10(untouched):0.00} dB");

        Assert.True(after < before * 1e-3, "the selected partial should be gone");
        Assert.InRange(10 * Math.Log10(untouched), -0.5, 0.5);

        // Goertzel over the middle of the affected span.
        static double Energy(float[] signal, double centre)
        {
            double coefficient = 2 * Math.Cos(2 * Math.PI * centre / SampleRate);
            double s1 = 0, s2 = 0;
            for (int i = 24_000; i < 26_000; i++)
            {
                double s = signal[i] + coefficient * s1 - s2;
                s2 = s1; s1 = s;
            }
            return s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        }
    }

    /// <summary>
    /// A unity-gain attenuation is the identity, which is the round-trip test for the whole framing:
    /// window pair, overlap, normalization and span bookkeeping all have to be right for a repair
    /// that changes nothing to change nothing.
    /// </summary>
    [Fact]
    public void AUnityGainEditReturnsTheAudioUnchanged()
    {
        float[] clean = Programme();
        SpectralMask mask = MaskFor(20_000, 30_000, 300, 700);

        SpectralRepairResult result = SpectralRepair.Attenuate(clean, 0, mask, 0, Options);

        double worst = 0;
        for (int i = 0; i < result.Samples.Length; i++)
            worst = Math.Max(worst, Math.Abs(result.Samples[i] - clean[result.Start + i]));

        output.WriteLine($"worst deviation {worst:0.000e+0} over {result.Samples.Length} samples");
        Assert.True(worst < 1e-5, $"a no-op edit moved a sample by {worst}");
    }

    /// <summary>Nothing outside the replaced span can move, because nothing outside it is returned.</summary>
    [Fact]
    public void TheReplacedSpanCoversTheSelectionAndLittleElse()
    {
        float[] clean = Programme();
        SpectralMask mask = MaskFor(20_000, 30_000, 300, 700);

        SpectralRepairResult result = SpectralRepair.Attenuate(clean, 0, mask, -20, Options);

        output.WriteLine($"selection 20000..30000, replaced {result.Start}..{result.End}");
        Assert.True(result.Start <= 20_000, "the span must cover the start of the selection");
        Assert.True(result.End >= 30_000, "the span must cover the end of the selection");
        Assert.True(result.Start >= 20_000 - Fft, "the span reaches further back than a window");
        Assert.True(result.End <= 30_000 + 2 * Fft, "the span reaches further forward than a window");
    }

    /// <summary>
    /// A selection barely wider than the feather still has to do something. Eroding it out of
    /// existence would make a small repair silently no-op, which reads as the tool being broken.
    /// </summary>
    [Fact]
    public void ASelectionNarrowerThanTheFeatherStillRepairs()
    {
        float[] clean = Programme();
        var (damaged, from, to, low, high) = AddBurst(clean, width: 1_024);
        SpectralMask mask = MaskFor(from, to, low, high);

        Assert.False(mask.IsEmpty, "a narrow selection was eroded away entirely");

        SpectralRepairResult result = SpectralRepair.Heal(damaged, 0, mask, Options);
        double before = SnrDb(clean, damaged, result.Start, result.Samples.Length);
        double after = SnrDb(clean, Applied(damaged, result), result.Start, result.Samples.Length);

        output.WriteLine($"1024-sample selection: {before:0.0} -> {after:0.0} dB");
        Assert.False(result.IsEmpty);
        Assert.True(after > before, "a narrow repair must still be an improvement");
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void AnEmptyMaskIsANoOp()
    {
        float[] clean = Programme();
        SpectralMask empty = SpectralMask.Rectangle(10, 10, 20, 40);

        Assert.True(SpectralRepair.Heal(clean, 0, empty, Options).IsEmpty);
        Assert.True(SpectralRepair.Attenuate(clean, 0, empty, -20, Options).IsEmpty);
    }

    [Fact]
    public void AnEmptySignalIsANoOp()
    {
        Assert.True(SpectralRepair.Heal([], 0, MaskFor(0, 4_000, 400, 900), Options).IsEmpty);
    }

    [Fact]
    public void SilenceHealsToSilenceRatherThanThrowing()
    {
        var silence = new float[Length];
        SpectralRepairResult result = SpectralRepair.Heal(silence, 0, MaskFor(20_000, 24_000, 400, 900), Options);

        Assert.False(result.IsEmpty);
        Assert.All(result.Samples, sample => Assert.Equal(0f, sample, 6));
    }

    [Fact]
    public void ASelectionAtTheVeryStartIsHandled()
    {
        float[] clean = Programme();
        SpectralRepairResult result = SpectralRepair.Heal(clean, 0, MaskFor(0, 4_000, 400, 900), Options);

        Assert.Equal(0, result.Start);
        Assert.All(result.Samples, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void ASelectionRunningPastTheEndIsClamped()
    {
        float[] clean = Programme();
        SpectralRepairResult result = SpectralRepair.Heal(
            clean, 0, MaskFor(Length - 3_000, Length + 20_000, 400, 900), Options);

        Assert.True(result.End <= Length, $"the repair ran to {result.End} past {Length}");
        Assert.All(result.Samples, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void AnInvalidHopIsRejected()
    {
        float[] clean = Programme();
        Assert.Throws<ArgumentException>(() =>
            SpectralRepair.Heal(clean, 0, MaskFor(20_000, 24_000, 400, 900), Options with { Hop = 300 }));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        float[] clean = Programme();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SpectralRepair.Heal(clean, 0, MaskFor(20_000, 24_000, 400, 900), Options, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        float[] clean = Programme();
        var reports = new List<double>();

        SpectralRepair.Heal(clean, 0, MaskFor(20_000, 24_000, 400, 900), Options,
            CancellationToken.None, new DirectProgress(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
    }

    /// <summary><see cref="Progress{T}"/> posts asynchronously; collect synchronously instead.</summary>
    private sealed class DirectProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
