using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The two normalize commands end to end, on real documents rather than on arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// They answer different questions and this file keeps them apart on purpose. <b>Peak</b> scales a
/// range so its loudest sample reaches a ceiling — a statement about samples, and meaningful on a
/// selection. <b>Loudness</b> brings the whole file to a programme level in LUFS — a statement
/// about perception, meaningless on a fragment, and bounded by a true-peak ceiling it will stop at
/// rather than breach.
/// </para>
/// <para>
/// The loudness half deliberately drives the same seam the window does — <see cref="LoudnessMatch"/>
/// to decide, <see cref="Processing.MatchLoudnessData"/> to apply — and then re-measures the result
/// with <see cref="LoudnessCompliance"/>. Asserting against a second, independent read of the audio
/// is what makes this a test of the command rather than a restatement of its own arithmetic.
/// </para>
/// </remarks>
public sealed class NormalizationTests
{
    private const int Rate = 44_100;

    /// <summary>
    /// A tone long enough for BS.1770 to have something to gate. Anything under 400 ms produces no
    /// block at all, which is the reason the loudness command does not take a selection.
    /// </summary>
    private static AudioDocument Tone(double amplitude, int seconds = 3, int channels = 2)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[Rate * seconds];
            for (int i = 0; i < data[c].Length; i++)
                data[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * 1_000 * i / Rate));
        }
        return new AudioDocument(data, Rate, 32) { Title = "Tone" };
    }

    private static double Peak(AudioDocument document, int start = 0, int count = -1)
    {
        if (count < 0) count = document.Length - start;
        float peak = 0;
        foreach (var channel in document.Channels)
            for (int i = start; i < start + count; i++) peak = Math.Max(peak, Math.Abs(channel[i]));
        return peak;
    }

    /// <summary>Measure, plan, commit — the window's path with the window taken out of it.</summary>
    private static LoudnessMatchStep Normalize(AudioDocument document, LoudnessTarget target)
    {
        LoudnessMeasurement measurement = LoudnessMatch.Measure(
            document.Title, document.Channels, document.SampleRate, target);
        LoudnessMatchStep step =
            LoudnessMatch.Plan([measurement], LoudnessMatchMode.Target, target).Steps[0];
        if (step.CanApply)
            document.ReplaceAllOwned(
                Processing.MatchLoudnessData(document.Channels, step.GainDb),
                Processing.MatchLoudnessName(step.GainDb, target.IntegratedLufs));
        return step;
    }

    // ── peak ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.3)]
    [InlineData(-1)]
    [InlineData(-6)]
    public void PeakNormalizeScalesTheLoudestSampleToTheCeilingItWasGiven(double ceilingDbfs)
    {
        var document = Tone(0.2);

        Processing.Normalize(document, 0, document.Length, ceilingDbfs);

        Assert.Equal(Math.Pow(10, ceilingDbfs / 20.0), Peak(document), 5);
    }

    /// <summary>
    /// The ceiling is a choice now, so the history entry has to carry it: two normalizations to
    /// different ceilings were previously both called "Normalize" and read back identically.
    /// </summary>
    [Fact]
    public void PeakNormalizeNamesTheCeilingItUsed()
    {
        var document = Tone(0.2);

        Processing.Normalize(document, 0, document.Length, -0.3);
        Assert.Equal("Normalize -0.3 dBFS", document.NextUndoName);

        Processing.Normalize(document, 0, document.Length, -6);
        Assert.Equal("Normalize -6.0 dBFS", document.NextUndoName);
    }

    /// <summary>
    /// Peak normalize keeps taking a range, which is the difference from the loudness command: the
    /// peak of a selection is the same kind of measurement as the peak of a file.
    /// </summary>
    [Fact]
    public void PeakNormalizeOnASelectionLeavesTheRestOfTheFileAlone()
    {
        var document = Tone(0.2);
        int half = document.Length / 2;
        float untouched = document.Channels[0][half + 100];

        Processing.Normalize(document, 0, half, -6);

        Assert.Equal(untouched, document.Channels[0][half + 100], 6);
        Assert.Equal(Math.Pow(10, -6 / 20.0), Peak(document, 0, half), 5);
        Assert.Equal(0.2, Peak(document, half), 3);
    }

    /// <summary>
    /// Nothing to scale is not a divide by zero — and it is not an edit either.
    /// </summary>
    /// <remarks>
    /// <see cref="Processing.Apply"/> commits whatever its delegate leaves behind, so declining
    /// inside the delegate still spliced the range over itself: an undo entry and a dirty document
    /// for an edit that changed nothing, which is the defect the Reduce Noise path was already
    /// fixed for. The return value is what lets the status line say which of the two happened.
    /// </remarks>
    [Fact]
    public void PeakNormalizeDeclinesASilentRangeRatherThanSplicingItOverItself()
    {
        var document = Tone(0);

        Assert.False(Processing.Normalize(document, 0, document.Length, -0.3));

        Assert.Equal(0, Peak(document), 6);
        Assert.Equal(0, document.HistoryCount);
        Assert.False(document.Dirty);
    }

    /// <summary>A range with something in it is still normalized, and says so.</summary>
    [Fact]
    public void PeakNormalizeReportsThatItAppliedWhenItDid()
    {
        var document = Tone(0.2);

        Assert.True(Processing.Normalize(document, 0, document.Length, -0.3));

        Assert.Equal(1, document.HistoryCount);
    }

    // ── loudness ─────────────────────────────────────────────────

    /// <summary>
    /// Measured again afterwards, by the compliance meter rather than by the code that chose the
    /// gain: the file has to actually be where the plan said it would be.
    /// </summary>
    [Theory]
    [InlineData(0.03)]   // needs a large boost
    [InlineData(0.5)]    // needs a cut
    public void LoudnessNormalizeLandsWithinToleranceOfItsTarget(double amplitude)
    {
        var document = Tone(amplitude);
        LoudnessTarget target = LoudnessTarget.Streaming;

        LoudnessMatchStep step = Normalize(document, target);

        Assert.True(step.CanApply);
        Assert.Equal(0, step.ShortfallDb, 6);

        LoudnessReport after =
            LoudnessCompliance.Measure(document.Channels, document.SampleRate, target);
        Assert.True(
            Math.Abs(after.IntegratedLufs - target.IntegratedLufs) <= target.ToleranceLu,
            $"landed at {after.IntegratedLufs:0.00} LUFS, wanted "
            + $"{target.IntegratedLufs:0.0} ± {target.ToleranceLu:0.0}");
        Assert.True(after.TruePeakDbtp <= target.TruePeakDbtp + 1e-6,
            $"true peak {after.TruePeakDbtp:0.00} dBTP is over the {target.TruePeakDbtp:0.0} ceiling");
    }

    /// <summary>
    /// The rule the batch converter used to break. Where the peaks will not allow the target, the
    /// file stops at the ceiling and stays short of the target rather than crossing it — and the
    /// step says how far short, because that shortfall is how much limiting the master would need.
    /// </summary>
    [Fact]
    public void LoudnessNormalizeStopsAtTheCeilingRatherThanReachingTheTarget()
    {
        // Quiet programme, loud transient: loudness reads low while the peak is already high, so
        // the ceiling binds long before the target does.
        var document = Tone(0.02);
        foreach (var channel in document.Channels) channel[Rate] = 0.5f;
        LoudnessTarget target = LoudnessTarget.Streaming;

        LoudnessMatchStep step = Normalize(document, target);

        Assert.True(step.CanApply);
        Assert.True(step.ShortfallDb > 1,
            $"expected the ceiling to bind, but the shortfall was {step.ShortfallDb:0.00} dB");

        LoudnessReport after =
            LoudnessCompliance.Measure(document.Channels, document.SampleRate, target);
        Assert.Equal(target.TruePeakDbtp, after.TruePeakDbtp, 1);
        Assert.True(after.IntegratedLufs < target.IntegratedLufs,
            "the target was reached after all, which means the ceiling was not what limited it");
    }

    /// <summary>Too quiet to measure is left alone, not given the gain that would reach a target.</summary>
    [Fact]
    public void LoudnessNormalizeLeavesADocumentBelowTheGateUntouched()
    {
        var document = Tone(0);

        LoudnessMatchStep step = Normalize(document, LoudnessTarget.Streaming);

        Assert.False(step.CanApply);
        Assert.Equal(0, document.HistoryCount);
        Assert.Contains("silent", step.Note);
    }

    /// <summary>
    /// Why the command refuses a selection rather than quietly measuring one: a range under the
    /// 400 ms block produces no gated block at all, so there is no loudness to normalize to and no
    /// honest gain to apply.
    /// </summary>
    [Fact]
    public void ARangeShorterThanTheGateHasNoLoudnessToNormalizeTo()
    {
        var document = Tone(0.2, seconds: 1);
        float[][] fragment = [.. document.Channels.Select(channel => channel[..(Rate / 10)])];

        LoudnessReport report = LoudnessCompliance.Measure(fragment, Rate, LoudnessTarget.Streaming);

        Assert.False(double.IsFinite(report.IntegratedLufs));
    }
}
