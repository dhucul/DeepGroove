using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// End-to-end quality of click removal, measured against known-clean audio.
/// </summary>
/// <remarks>
/// These are the golden tests for the switch from bidirectional extrapolation to Janssen
/// interpolation. Unlike the overlap-add migration, the change here is <em>meant</em> to alter the
/// output, so pinning a fingerprint would pin the wrong thing. What must hold instead is that
/// detection is untouched, and that what replaces each defect is measurably closer to the audio
/// that was there before the defect — which is the only definition of "better" that does not come
/// down to opinion.
/// </remarks>
public sealed class ClickRepairQualityTests(ITestOutputHelper output)
{
    private const int SampleRate = 44_100;
    private const int Length = 60_000;

    /// <summary>Programme material: several partials, gentle vibrato, and a light noise floor.</summary>
    private static float[][] CleanProgramme(int seed = 5)
    {
        var random = new Random(seed);
        var left = new float[Length];
        var right = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)SampleRate;
            double vibrato = 1 + 0.002 * Math.Sin(2 * Math.PI * 5 * t);
            // Scaled to peak around a quarter of full scale, matching the level the existing
            // detection tests use — the detector's thresholds are relative, and a defect has to
            // stand well clear of the programme to be one.
            double value = 0.35 * (
                  0.32 * Math.Sin(2 * Math.PI * 196 * vibrato * t)
                + 0.20 * Math.Sin(2 * Math.PI * 392 * vibrato * t)
                + 0.11 * Math.Sin(2 * Math.PI * 588 * vibrato * t)
                + 0.06 * Math.Sin(2 * Math.PI * 784 * vibrato * t)
                + 0.03 * Math.Sin(2 * Math.PI * 1176 * vibrato * t));
            double hiss = (random.NextDouble() - 0.5) * 0.004;
            left[i] = (float)(value + hiss);
            right[i] = (float)(value * 0.94 + hiss * 1.1);
        }
        return [left, right];
    }

    /// <summary>
    /// Stylus-style impulses: a sharp onset decaying over about a millisecond, at the amplitude and
    /// shape the existing detection tests establish as reliably detectable.
    /// </summary>
    private static (float[][] Damaged, int[] Positions) WithClicks(float[][] clean, int count, int seed = 17)
    {
        var random = new Random(seed);
        var damaged = new[] { (float[])clean[0].Clone(), (float[])clean[1].Clone() };
        var positions = new int[count];
        int spacing = Length / (count + 2);

        for (int c = 0; c < count; c++)
        {
            int at = spacing * (c + 1) + random.Next(-40, 40);
            positions[c] = at;
            int width = 32 + random.Next(0, 24);
            double amplitude = 0.6 + random.NextDouble() * 0.2;
            double sign = random.Next(2) == 0 ? 1 : -1;
            for (int i = 0; i < width; i++)
            {
                double decay = Math.Exp(-i / 24.0);
                float impulse = (float)(sign * amplitude * decay);
                damaged[0][at + i] += impulse;
                damaged[1][at + i] += impulse * 0.9f;
            }
        }
        return (damaged, positions);
    }

    /// <summary>Error against the clean original, in dB, over a window around each defect.</summary>
    private static double ResidualErrorDb(float[][] clean, float[][] candidate, int[] positions, int radius = 64)
    {
        double signal = 0, error = 0;
        foreach (int at in positions)
        {
            int from = Math.Max(0, at - radius), to = Math.Min(Length, at + radius);
            for (int channel = 0; channel < clean.Length; channel++)
            {
                for (int i = from; i < to; i++)
                {
                    double reference = clean[channel][i];
                    double difference = reference - candidate[channel][i];
                    signal += reference * reference;
                    error += difference * difference;
                }
            }
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    // ── the measurements that justify the change ─────────────────

    /// <summary>Error against the clean original over exactly the spans the repairer replaced.</summary>
    /// <remarks>
    /// Deliberately measured on the repaired spans rather than a fixed window around each defect.
    /// A window also contains whatever part of the click the detector did not claim, and that
    /// undetected tail dominates the figure — which would make this a measurement of detection
    /// coverage rather than of how well the gaps were filled. Only the latter changes here.
    /// </remarks>
    private static double SpanResidualDb(float[][] clean, float[][] candidate,
        IReadOnlyList<ClickEvent> events)
    {
        double signal = 0, error = 0;
        foreach (var defect in events)
        {
            int channel = Math.Clamp(defect.Channel, 0, clean.Length - 1);
            for (int i = Math.Max(0, defect.StartSample); i < Math.Min(Length, defect.EndSample); i++)
            {
                double reference = clean[channel][i];
                double difference = reference - candidate[channel][i];
                signal += reference * reference;
                error += difference * difference;
            }
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    /// <summary>
    /// The headline: over the spans it replaces, the repairer must put back something much closer to
    /// the audio that was there than the damage was. This is the number the switch to Janssen has to
    /// improve, and the reason the interpolation method is worth changing at all.
    /// </summary>
    [Fact]
    public void RepairedSpansAreMuchCloserToTheOriginalThanTheDamageWas()
    {
        float[][] clean = CleanProgramme();
        var (damaged, _) = WithClicks(clean, count: 24);

        var analysis = Restoration.AnalyzeClicks(damaged, SampleRate,
            new ClickAnalysisOptions { Sensitivity = 7.0 });
        double before = SpanResidualDb(clean, damaged, analysis.Events);

        int repaired = Restoration.RepairClicksInPlace(damaged, analysis.Events);
        double after = SpanResidualDb(clean, damaged, analysis.Events);

        output.WriteLine($"repaired {repaired} spans · {before:0.0} dB → {after:0.0} dB " +
                         $"(improvement {after - before:0.0} dB)");

        Assert.True(repaired > 0, "nothing was detected, so the measurement is meaningless");
        // Measured on this material: the bidirectional predictor this replaced managed -12.2 → -0.8
        // dB (11.5 dB of improvement); Janssen manages -12.2 → 2.0 dB (14.2 dB). The bounds sit just
        // below the latter, so a regression to the old method — or to the cubic fallback — fails.
        Assert.True(after > before + 13,
            $"repair improved the residual by only {after - before:0.0} dB");
        Assert.True(after > 0, $"post-repair residual was only {after:0.0} dB");
    }

    /// <summary>
    /// Detection must be unaffected by how the gaps are subsequently filled. If this moves at the
    /// same time as the reconstruction changes, the two effects cannot be told apart.
    /// </summary>
    [Theory]
    [InlineData(6.0)]
    [InlineData(7.0)]
    [InlineData(10.0)]
    public void DetectionFindsTheSameDefectsRegardlessOfHowTheyAreFilled(double sensitivity)
    {
        float[][] clean = CleanProgramme();
        var (damaged, positions) = WithClicks(clean, count: 24);

        var analysis = Restoration.AnalyzeClicks(damaged, SampleRate,
            new ClickAnalysisOptions { Sensitivity = sensitivity });

        output.WriteLine($"sensitivity {sensitivity}: {analysis.Events.Count} events");

        // Every injected defect should be covered by some detected event on at least one channel.
        int covered = 0;
        foreach (int at in positions)
        {
            foreach (var defect in analysis.Events)
            {
                if (defect.StartSample <= at + 12 && defect.EndSample >= at) { covered++; break; }
            }
        }
        Assert.True(covered >= positions.Length - 2,
            $"only {covered} of {positions.Length} injected defects were detected");
    }

    [Fact]
    public void CleanAudioIsLeftAlone()
    {
        float[][] clean = CleanProgramme();
        var candidate = new[] { (float[])clean[0].Clone(), (float[])clean[1].Clone() };

        Restoration.RemoveClicks(candidate, SampleRate, sensitivity: 7.0);

        // Whatever the detector finds on undamaged programme, repairing it must not move the audio
        // meaningfully — a de-clicker that dulls clean material is worse than none.
        double residual = ResidualErrorDb(clean, candidate, [15_000, 30_000, 45_000], radius: 4_000);
        output.WriteLine($"clean-audio residual after repair: {residual:0.0} dB");
        Assert.True(residual > 45, $"clean audio was altered: residual only {residual:0.0} dB");
    }

    [Fact]
    public void RepairIsDeterministic()
    {
        float[][] clean = CleanProgramme();
        var (first, _) = WithClicks(clean, count: 12);
        var (second, _) = WithClicks(clean, count: 12);

        Restoration.RemoveClicks(first, SampleRate, sensitivity: 7.0);
        Restoration.RemoveClicks(second, SampleRate, sensitivity: 7.0);

        Assert.Equal(first[0], second[0]);
        Assert.Equal(first[1], second[1]);
    }

    [Fact]
    public void PartialStrengthBlendsTowardTheRepair()
    {
        float[][] clean = CleanProgramme();
        var (full, positions) = WithClicks(clean, count: 16);
        var (half, _) = WithClicks(clean, count: 16);
        var (none, _) = WithClicks(clean, count: 16);

        Restoration.RemoveClicks(full, SampleRate, 7.0, strength: 1.0);
        Restoration.RemoveClicks(half, SampleRate, 7.0, strength: 0.5);

        double fullDb = ResidualErrorDb(clean, full, positions);
        double halfDb = ResidualErrorDb(clean, half, positions);
        double noneDb = ResidualErrorDb(clean, none, positions);

        output.WriteLine($"strength 0 → {noneDb:0.0} dB, 0.5 → {halfDb:0.0} dB, 1.0 → {fullDb:0.0} dB");
        Assert.True(halfDb > noneDb, "half strength should still improve on the damage");
        Assert.True(fullDb > halfDb, "full strength should improve on half");
    }

    /// <summary>
    /// The channels repair concurrently — the parallelism the de-crackle stage already claims,
    /// safe because each channel's samples are independent. Safe has to mean bit-identical:
    /// this pins the parallel default against the same repair run one channel at a time, so a
    /// scheduling dependency in the plan or the interpolator cannot creep in silently.
    /// </summary>
    [Fact]
    public void RepairingChannelsInParallelMatchesDoingThemInTurn()
    {
        float[][] clean = CleanProgramme();
        var (sequential, _) = WithClicks(clean, count: 24);
        var parallel = new[] { (float[])sequential[0].Clone(), (float[])sequential[1].Clone() };

        var analysis = Restoration.AnalyzeClicks(sequential, SampleRate,
            new ClickAnalysisOptions { Sensitivity = 7.0 });
        var options = new ClickRepairOptions();

        int repairedInTurn = Restoration.RepairClicksInPlace(sequential, analysis.Events,
            options, CancellationToken.None, progress: null, maxDegreeOfParallelism: 1);
        int repairedAtOnce = Restoration.RepairClicksInPlace(parallel, analysis.Events, options);

        Assert.True(repairedInTurn > 0, "nothing was repaired, so the comparison is meaningless");
        Assert.Equal(repairedInTurn, repairedAtOnce);
        for (int channel = 0; channel < 2; channel++)
        {
            Assert.Equal(sequential[channel], parallel[channel]);
            output.WriteLine($"channel {channel}: {sequential[channel].Length} samples identical");
        }
    }
}
