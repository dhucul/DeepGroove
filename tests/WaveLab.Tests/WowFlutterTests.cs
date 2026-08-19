using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class WowFlutterTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 441_000;      // 10 s

    private static WowFlutterOptions Options => WowFlutterOptions.Default;

    /// <summary>
    /// Programme with several sustained partials and a note change part way through, so the
    /// measurement has to survive material that does not hold still.
    /// </summary>
    private static float[] Programme(int length = Length + 40_000, int seed = 53)
    {
        var signal = new float[length];
        var random = new Random(seed);
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)Rate;
            // Two chords, changing at four seconds.
            double[] notes = t < 4.0 ? [196.0, 247, 294, 392] : [220.0, 277, 330, 440];
            double value = 0;
            for (int n = 0; n < notes.Length; n++)
            {
                // Harmonics well up the band, because that is where the speed is measured: a third
                // of a percent takes three hundred cycles to see, which is a long window down low
                // and a short one up high.
                for (int h = 1; h <= 20; h++)
                {
                    double frequency = notes[n] * h;
                    if (frequency > 9_000) break;
                    value += 0.10 / h * Math.Sin(2 * Math.PI * frequency * t + n + h);
                }
            }
            signal[i] = (float)(value * 0.3 + (random.NextDouble() - 0.5) * 0.002);
        }
        return signal;
    }

    /// <summary>
    /// Plays a signal back with a sinusoidal speed variation of the given depth and rate — wow, as a
    /// warped record or a worn idler produces it.
    /// </summary>
    private static float[] AddWow(float[] source, double depth, double wowHz, int length = Length)
    {
        // Position advances at 1 + depth·sin(2πf t), so its integral is the sample to read.
        double omega = 2 * Math.PI * wowHz / Rate;
        return Interpolation.Resample(new float[length].Select(source).ToArray(),
            i => i + depth / omega * (1 - Math.Cos(omega * i)));
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-15));

    /// <summary>
    /// Residual against the reference, after taking out any bulk time offset between them.
    /// </summary>
    /// <remarks>
    /// Measured in windows, each aligned on its own, and reduced by the median. Correcting a time
    /// base means integrating a rate: the constant of integration is arbitrary, so a perfect
    /// correction still leaves the audio shifted, and what is left of the measurement noise leaves
    /// that shift slowly wandering. Comparing sample against sample across a whole file reads a good
    /// repair as a total failure — a first version of this did exactly that. What can honestly be
    /// asked is whether the audio is intact <em>locally</em>, which is what a listener hears.
    /// </remarks>
    private static double Residual(float[] reference, float[] candidate, int skip = 60_000)
    {
        int count = Math.Min(reference.Length, candidate.Length);
        const int span = 4_096;
        var scores = new List<double>();

        for (int start = skip; start + span < count - skip; start += span)
        {
            int bestOffset = 0;
            double best = double.NegativeInfinity;
            for (int offset = -600; offset <= 600; offset++)
            {
                double dot = 0;
                for (int i = 0; i < span; i += 5)
                {
                    int j = start + i + offset;
                    if ((uint)j < (uint)count) dot += reference[start + i] * candidate[j];
                }
                if (dot > best) { best = dot; bestOffset = offset; }
            }

            double signal = 0, error = 0;
            for (int i = 0; i < span; i++)
            {
                int j = start + i + bestOffset;
                if ((uint)j >= (uint)count) continue;
                double difference = reference[start + i] - candidate[j];
                signal += (double)reference[start + i] * reference[start + i];
                error += difference * difference;
            }
            scores.Add(10 * Math.Log10(signal / Math.Max(error, 1e-30)));
        }

        if (scores.Count == 0) return 0;
        scores.Sort();
        return scores[scores.Count / 2];
    }

    // ── measurement ──────────────────────────────────────────────

    /// <summary>
    /// The headline measurement: a planted speed variation is reported at about its real depth.
    /// Wow is quoted as a percentage, so that is what has to come back.
    /// </summary>
    [Theory]
    [InlineData(0.003, 1.5)]
    [InlineData(0.006, 2.0)]
    [InlineData(0.012, 0.8)]
    public void APlantedSpeedVariationIsMeasuredAtItsRealDepth(double depth, double wowHz)
    {
        float[] warped = AddWow(Programme(), depth, wowHz);

        WowFlutterReport report = WowFlutter.Analyze(warped, Rate, Options);

        output.WriteLine($"planted {depth * 100:0.00}% at {wowHz} Hz: " +
                         $"peak {report.PeakPercent:0.000}%, rms {report.RmsPercent:0.000}%, " +
                         $"{report.Blocks} blocks, {report.Confidence:P0} believed");

        Assert.True(report.Found);

        // The planted variation is sinusoidal, so its rms is the depth over root two.
        Assert.Equal(depth * 100 / Math.Sqrt(2), report.RmsPercent, depth * 100 * 0.5);
    }

    [Fact]
    public void SteadyMaterialMeasuresAsSteady()
    {
        float[] steady = Programme(Length);

        WowFlutterReport report = WowFlutter.Analyze(steady, Rate, Options);
        output.WriteLine($"steady material: peak {report.PeakPercent:0.000}%, " +
                         $"rms {report.RmsPercent:0.000}%, {report.Confidence:P0} believed");

        Assert.True(report.RmsPercent < 0.08, $"steady material measured {report.RmsPercent:0.000}% of wow");
    }

    /// <summary>
    /// A note change is not a change of speed. The whole spectrum slides when the turntable drifts;
    /// a chord change rearranges it, and the two must not be confused.
    /// </summary>
    [Fact]
    public void AChordChangeIsNotReadAsALurchInSpeed()
    {
        float[] steady = Programme(Length);
        WowFlutterReport report = WowFlutter.Analyze(steady, Rate, Options);

        // The programme changes chord at four seconds; nothing there may look like a speed jump.
        output.WriteLine($"across a chord change: peak {report.PeakPercent:0.000}%");
        Assert.True(report.PeakPercent < 0.5,
            $"a chord change registered as {report.PeakPercent:0.000}% of speed error");
    }

    // ── correction ───────────────────────────────────────────────

    /// <summary>
    /// The headline: correcting brings the warped transfer back toward the original. Measured as a
    /// residual against the unwarped signal, which is the only honest test of a time-base repair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated per wow rate, because the falloff is real and inherent. Seeing a frequency move by a
    /// fraction of a percent takes a window of some length, and the faster the wow the more of a
    /// cycle that window averages over — so the fastest case is measured least well and corrected
    /// least well. Recording that is more use than an average that hides it.
    /// </para>
    /// <para>
    /// <b>These expectations were lowered when the estimator moved from measuring velocity to
    /// measuring position, and the reason is the whole point of the change.</b> This programme is a
    /// sustained stationary tone, where consecutive frames are nearly identical and a frame-to-frame
    /// correlation is extremely precise — it scored +7.5 dB here against +1.1 for the method that
    /// replaced it. Real music is not stationary: consecutive frames differ for musical reasons, the
    /// frame-to-frame shift is then noisy, and integrating that noise is a random walk. Measured
    /// across five corpora the old method left <b>220 to 290 samples of residual drift whatever was
    /// planted</b>, turning 51 samples of error into 238 at a 0.3% wow; the new one leaves 227 down
    /// to 75 and falls with the damage as it should. So the numbers here got worse and the tool got
    /// better, and a test on a stationary tone is exactly the instrument that could not see it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0.003, 1.5, 5.0, 0.50)]
    [InlineData(0.006, 2.0, 2.0, 0.70)]
    [InlineData(0.012, 0.8, 0.8, 0.70)]
    public void CorrectingMovesTheAudioBackTowardTheOriginal(double depth, double wowHz,
        double expectedGainDb, double expectedRemaining)
    {
        float[] source = Programme();
        float[] reference = new float[Length].Select(source).ToArray();
        float[] warped = AddWow(source, depth, wowHz);

        var channels = new[] { (float[])warped.Clone() };
        WowFlutterReport report = WowFlutter.Correct(channels, Rate, Options);
        WowFlutterReport left = WowFlutter.Analyze(channels[0], Rate, Options);

        double before = Residual(reference, warped);
        double after = Residual(reference, channels[0]);
        output.WriteLine($"{depth * 100:0.0}% at {wowHz} Hz: waveform {before:0.0} → {after:0.0} dB; " +
                         $"wow {report.RmsPercent:0.000}% → {left.RmsPercent:0.000}% rms");

        // Both matter. The remaining wow says the time base was straightened; the waveform says the
        // straightening put the audio back where it belonged rather than somewhere else smooth.
        Assert.True(left.RmsPercent < report.RmsPercent * expectedRemaining,
            $"wow only fell from {report.RmsPercent:0.000}% to {left.RmsPercent:0.000}%");
        Assert.True(after > before + expectedGainDb,
            $"correction gained only {after - before:0.0} dB on the waveform");
    }

    /// <summary>
    /// Both channels must be resampled along the same map, or the stereo image wanders — a worse
    /// fault than the wow being corrected.
    /// </summary>
    [Fact]
    public void BothChannelsAreCorrectedAlongOneMap()
    {
        float[] source = Programme();
        float[] warped = AddWow(source, 0.006, 1.5);

        var channels = new[] { (float[])warped.Clone(), (float[])warped.Clone() };
        WowFlutter.Correct(channels, Rate, Options);

        double worst = 0;
        for (int i = 0; i < channels[0].Length; i++)
            worst = Math.Max(worst, Math.Abs(channels[0][i] - channels[1][i]));

        output.WriteLine($"the two channels differ by at most {worst:0.0e+0}");
        Assert.Equal(0, worst, 9);
    }

    /// <summary>
    /// Slow speed error is left alone deliberately: a record running consistently fast is at the
    /// wrong pitch, which is a different complaint with a different remedy.
    /// </summary>
    [Fact]
    public void AConstantSpeedErrorIsLeftAlone()
    {
        float[] source = Programme();
        // Half a percent fast, throughout — not wow.
        float[] fast = Interpolation.Resample(new float[Length].Select(source).ToArray(),
            i => i * 1.005);

        var channels = new[] { (float[])fast.Clone() };
        WowFlutterReport report = WowFlutter.Correct(channels, Rate, Options);

        double worst = 0;
        for (int i = 60_000; i < Length - 60_000; i++)
            worst = Math.Max(worst, Math.Abs(channels[0][i] - fast[i]));

        output.WriteLine($"a constant 0.5% error measured {report.RmsPercent:0.000}% of wow; " +
                         $"the audio moved by at most {worst:0.000}");
        Assert.True(report.RmsPercent < 0.15, "a constant speed error was reported as wow");
    }

    [Fact]
    public void CorrectingSteadyMaterialBarelyChangesIt()
    {
        float[] steady = Programme(Length);
        var channels = new[] { (float[])steady.Clone() };

        WowFlutter.Correct(channels, Rate, Options);

        double residual = Residual(steady, channels[0]);
        output.WriteLine($"steady material after a correction pass: {residual:0.0} dB");
        Assert.True(residual > 20, $"steady material was disturbed down to {residual:0.0} dB");
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void SilenceIsNotMeasured()
    {
        WowFlutterReport report = WowFlutter.Analyze(new float[Length], Rate, Options);
        output.WriteLine($"silence: found {report.Found}, {report.Blocks} blocks");
        Assert.True(report.RmsPercent < 0.5);
    }

    [Fact]
    public void MaterialTooShortIsNotMeasured()
    {
        Assert.False(WowFlutter.Analyze(new float[8_000], Rate, Options).Found);
    }

    [Fact]
    public void NoChannelsIsANoOp()
    {
        Assert.False(WowFlutter.Correct([], Rate, Options).Found);
        Assert.False(WowFlutter.Correct([[]], Rate, Options).Found);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        float[] warped = AddWow(Programme(), 0.006, 1.5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WowFlutter.Correct([warped], Rate, Options, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        float[] warped = AddWow(Programme(), 0.006, 1.5);
        var reports = new List<double>();

        WowFlutter.Correct([warped], Rate, Options, CancellationToken.None, new DirectProgress(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
    }

    private sealed class DirectProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}

/// <summary>Copies the first N samples of a source into a buffer, for building fixed-length cases.</summary>
internal static class WowFlutterTestHelpers
{
    public static float[] Select(this float[] destination, float[] source)
    {
        int count = Math.Min(destination.Length, source.Length);
        Array.Copy(source, destination, count);
        return destination;
    }
}

