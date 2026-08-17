using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class RecordingCurveTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    private static RecordingCurveSpec Riaa => RecordingCurves.Spec(RecordingCurve.Riaa);

    // ── the curve itself ─────────────────────────────────────────

    /// <summary>
    /// The published RIAA playback response, relative to 1 kHz. Everything else in this file is
    /// measured against the analytic curve, so the analytic curve has to be right first.
    /// </summary>
    [Theory]
    [InlineData(20, 19.274)]
    [InlineData(50, 16.941)]
    [InlineData(100, 13.088)]
    [InlineData(500, 2.648)]
    [InlineData(1_000, 0.0)]
    [InlineData(2_000, -2.589)]
    [InlineData(5_000, -8.210)]
    [InlineData(10_000, -13.734)]
    [InlineData(20_000, -19.620)]
    public void TheRiaaCurveMatchesItsPublishedValues(double frequency, double expectedDb)
    {
        double actual = RecordingCurves.ResponseDb(Riaa, frequency);
        output.WriteLine($"{frequency,6} Hz: {actual,8:+0.000;-0.000;0.000} dB, " +
                         $"published {expectedDb,8:+0.000;-0.000;0.000}");
        Assert.Equal(expectedDb, actual, 0.01);
    }

    [Fact]
    public void TheCurveIsUnityAtItsReferenceFrequency()
    {
        foreach (RecordingCurveSpec spec in RecordingCurves.All)
            Assert.Equal(0, RecordingCurves.ResponseDb(spec, RecordingCurves.ReferenceHz), 9);
    }

    /// <summary>
    /// Above any rumble filter the curve only ever falls. A curve carrying one legitimately rises
    /// below its corner — that is what the filter is — so the sweep starts clear of it.
    /// </summary>
    [Fact]
    public void EveryCurveFallsAcrossTheBand()
    {
        foreach (RecordingCurveSpec spec in RecordingCurves.All)
        {
            double previous = double.MaxValue;
            double from = spec.RumbleHz > 0 ? spec.RumbleHz * 2 : 20;
            for (double f = from; f <= 20_000; f *= 1.15)
            {
                double db = RecordingCurves.ResponseDb(spec, f);
                Assert.True(db < previous + 1e-9, $"{spec.Name} rose at {f:0} Hz");
                previous = db;
            }
            output.WriteLine($"{spec.Name,-26} turnover {spec.TurnoverHz,6:0.0} Hz, " +
                             $"treble {(spec.TrebleHz > 0 ? $"{spec.TrebleHz:0} Hz" : "none"),8}, " +
                             $"10 kHz {RecordingCurves.ResponseDb(spec, 10_000),7:0.0} dB");
        }
    }

    /// <summary>The IEC amendment is a rumble filter and nothing else: it may only affect the bottom.</summary>
    [Fact]
    public void TheIecAmendmentOnlyChangesTheBottomEnd()
    {
        RecordingCurveSpec plain = Riaa;
        RecordingCurveSpec iec = RecordingCurves.Spec(RecordingCurve.RiaaIec);

        double at20 = RecordingCurves.ResponseDb(iec, 20) - RecordingCurves.ResponseDb(plain, 20);
        double at100 = RecordingCurves.ResponseDb(iec, 100) - RecordingCurves.ResponseDb(plain, 100);
        double at1k = RecordingCurves.ResponseDb(iec, 1_000) - RecordingCurves.ResponseDb(plain, 1_000);

        output.WriteLine($"IEC against plain RIAA: 20 Hz {at20:0.0} dB, 100 Hz {at100:0.00} dB, " +
                         $"1 kHz {at1k:0.000} dB");
        Assert.InRange(at20, -4.5, -2.0);
        Assert.InRange(at100, -0.5, 0);
        Assert.Equal(0, at1k, 6);
    }

    /// <summary>
    /// A curve with no treble pole is a shelf, so its top is essentially flat — but not exactly 0 dB
    /// at 10 kHz, because 1 kHz is still a little way down the shelf and everything is quoted
    /// relative to 1 kHz. The claim worth testing is how much flatter it is than RIAA, not that it
    /// is perfectly flat.
    /// </summary>
    [Fact]
    public void ACurveWithNoTrebleRolloffKeepsItsTop()
    {
        RecordingCurveSpec coarse = RecordingCurves.Spec(RecordingCurve.Coarse78);
        Assert.Equal(0, coarse.TrebleHz);

        double coarseAt10k = RecordingCurves.ResponseDb(coarse, 10_000);
        double riaaAt10k = RecordingCurves.ResponseDb(Riaa, 10_000);
        output.WriteLine($"at 10 kHz: 78 rpm curve {coarseAt10k:0.00} dB, RIAA {riaaAt10k:0.00} dB");

        Assert.InRange(coarseAt10k, -1.5, 0.1);
        Assert.True(coarseAt10k > riaaAt10k + 10, "the shelf-only curve should keep far more of its top");

        // Flat where the shelf has finished, which is the real property.
        Assert.Equal(RecordingCurves.ResponseDb(coarse, 5_000),
            RecordingCurves.ResponseDb(coarse, 20_000), 0.2);
    }

    // ── the designed filter ──────────────────────────────────────

    /// <summary>
    /// The measurement that decides whether the design is worth having: how far the filter's actual
    /// response strays from the analytic curve across the audio band.
    /// </summary>
    [Theory]
    [InlineData(CurvePhase.Minimum)]
    [InlineData(CurvePhase.Linear)]
    public void TheDesignedFilterMatchesTheCurveAcrossTheBand(CurvePhase phase)
    {
        float[] kernel = RecordingCurves.Design(Riaa, Rate, CurveDirection.Playback, phase);

        double worst = 0, worstAt = 0;
        for (double f = 20; f <= 20_000; f *= 1.05)
        {
            double designed = 20 * Math.Log10(Math.Max(1e-12, ResponseAt(kernel, f, Rate)));
            double reference = 20 * Math.Log10(Math.Max(1e-12, RecordingCurves.Magnitude(Riaa, f)));
            double error = Math.Abs(designed - reference);
            if (error > worst) { worst = error; worstAt = f; }
        }

        output.WriteLine($"{phase} phase: worst error {worst:0.000} dB at {worstAt:0} Hz");
        Assert.True(worst < 0.1, $"the filter is out by {worst:0.00} dB at {worstAt:0} Hz");
    }

    /// <summary>
    /// A minimum-phase kernel must actually be causal — its energy front-loaded — or the cepstral
    /// fold has gone the wrong way round and the result is neither minimum phase nor aligned.
    /// </summary>
    [Fact]
    public void TheMinimumPhaseKernelIsCausal()
    {
        float[] kernel = RecordingCurves.Design(Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum);

        double total = 0, head = 0;
        for (int i = 0; i < kernel.Length; i++)
        {
            double energy = (double)kernel[i] * kernel[i];
            total += energy;
            if (i < kernel.Length / 16) head += energy;
        }

        output.WriteLine($"{head / total:P1} of the energy is in the first sixteenth");
        Assert.True(head / total > 0.9, "a minimum-phase kernel should be front-loaded");
    }

    [Fact]
    public void TheLinearPhaseKernelIsSymmetric()
    {
        float[] kernel = RecordingCurves.Design(Riaa, Rate, CurveDirection.Playback, CurvePhase.Linear);

        double worst = 0;
        int centre = kernel.Length / 2;
        for (int i = 1; i < kernel.Length / 4; i++)
            worst = Math.Max(worst, Math.Abs(kernel[centre - i] - kernel[centre + i]));

        output.WriteLine($"worst asymmetry {worst:0.000e+0}");
        Assert.True(worst < 1e-6, $"the kernel is not symmetric: {worst}");
    }

    // ── round trip ───────────────────────────────────────────────

    /// <summary>
    /// Recording then playing back has to return the audio it started with. This is the end-to-end
    /// check on the design, the convolution and the alignment together — a mistake in any of the
    /// three shows up here and almost nowhere else.
    /// </summary>
    [Theory]
    [InlineData(CurvePhase.Minimum)]
    [InlineData(CurvePhase.Linear)]
    public void PreEmphasisAndDeEmphasisCancel(CurvePhase phase)
    {
        float[] original = Programme();
        var channels = new[] { (float[])original.Clone() };

        RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Record, phase);
        RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Playback, phase);

        // The kernel needs room to settle at each end; the interior is what has to come back.
        const int skip = 20_000;
        double signal = 0, error = 0;
        for (int i = skip; i < original.Length - skip; i++)
        {
            double difference = original[i] - channels[0][i];
            signal += (double)original[i] * original[i];
            error += difference * difference;
        }

        double snr = 10 * Math.Log10(signal / Math.Max(error, 1e-30));
        output.WriteLine($"{phase} phase round trip: {snr:0.0} dB");
        Assert.True(snr > 45, $"the round trip only reached {snr:0.0} dB");
    }

    /// <summary>De-emphasis has to boost the bass and cut the treble, by the published amounts.</summary>
    [Fact]
    public void PlaybackAppliesTheCurveToRealAudio()
    {
        var channels = new[] { Programme() };
        double before100 = Level(channels[0], 100);
        double before10k = Level(channels[0], 10_000);
        double before1k = Level(channels[0], 1_000);

        RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum);

        double at100 = Db(Level(channels[0], 100) / before100) - Db(Level(channels[0], 1_000) / before1k);
        double at10k = Db(Level(channels[0], 10_000) / before10k) - Db(Level(channels[0], 1_000) / before1k);

        output.WriteLine($"100 Hz moved {at100:+0.0;-0.0} dB, 10 kHz moved {at10k:+0.0;-0.0} dB, " +
                         $"both relative to 1 kHz");
        Assert.Equal(RecordingCurves.ResponseDb(Riaa, 100), at100, 0.5);
        Assert.Equal(RecordingCurves.ResponseDb(Riaa, 10_000), at10k, 0.5);
    }

    [Fact]
    public void RecordIsTheExactInverseOfPlayback()
    {
        foreach (double f in new[] { 20.0, 100, 1_000, 10_000, 20_000 })
        {
            float[] playback = RecordingCurves.Design(Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum);
            float[] record = RecordingCurves.Design(Riaa, Rate, CurveDirection.Record, CurvePhase.Minimum);

            double product = ResponseAt(playback, f, Rate) * ResponseAt(record, f, Rate);
            output.WriteLine($"{f,6:0} Hz: playback × record = {Db(product):+0.000;-0.000;0.000} dB");
            Assert.Equal(0, Db(product), 0.15);
        }
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void AnEmptyChannelIsANoOp()
    {
        var channels = new[] { Array.Empty<float>() };
        RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum);
        Assert.Empty(channels[0]);
    }

    [Fact]
    public void ASignalShorterThanTheKernelIsStillProcessed()
    {
        var channels = new[] { new float[500] };
        channels[0][250] = 1f;

        RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum);

        Assert.Contains(channels[0], sample => Math.Abs(sample) > 1e-6);
        Assert.All(channels[0], sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var channels = new[] { Programme() };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum,
                RecordingCurves.DefaultTaps, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        var channels = new[] { Programme(), Programme() };
        var reports = new List<double>();

        RecordingCurves.Apply(channels, Riaa, Rate, CurveDirection.Playback, CurvePhase.Minimum,
            RecordingCurves.DefaultTaps, CancellationToken.None, new DirectProgress(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>Broadband programme, so every part of the curve is exercised at once.</summary>
    private static float[] Programme(int length = 200_000)
    {
        var signal = new float[length];
        var random = new Random(37);
        double state = 0;
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)Rate;
            double value = 0.20 * Math.Sin(2 * Math.PI * 100 * t)
                         + 0.20 * Math.Sin(2 * Math.PI * 1_000 * t + 0.4)
                         + 0.20 * Math.Sin(2 * Math.PI * 10_000 * t - 0.8);
            state = state * 0.7 + (random.NextDouble() - 0.5) * 0.3;
            signal[i] = (float)(value + state * 0.05);
        }
        return signal;
    }

    /// <summary>Magnitude of a kernel's response at a frequency, by direct evaluation.</summary>
    private static double ResponseAt(float[] kernel, double frequency, int sampleRate)
    {
        double omega = 2 * Math.PI * frequency / sampleRate;
        double re = 0, im = 0;
        for (int i = 0; i < kernel.Length; i++)
        {
            re += kernel[i] * Math.Cos(omega * i);
            im -= kernel[i] * Math.Sin(omega * i);
        }
        return Math.Sqrt(re * re + im * im);
    }

    private static double Level(float[] signal, double frequency)
    {
        double omega = 2 * Math.PI * frequency / Rate;
        double re = 0, im = 0;
        const int from = 40_000, to = 160_000;
        for (int i = from; i < to; i++)
        {
            re += signal[i] * Math.Cos(omega * i);
            im -= signal[i] * Math.Sin(omega * i);
        }
        return Math.Sqrt(re * re + im * im) / (to - from);
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-15));

    private sealed class DirectProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
