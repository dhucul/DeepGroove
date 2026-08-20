using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// What the stereo widener does to audio, rather than how its coefficients reach the audio thread.
/// </summary>
/// <remarks>
/// <para>
/// Two of these controls changed meaning in the audit and nothing has measured them since.
/// <c>SPLIT FREQ</c> was a dead control — the filters were built and never used — and
/// <c>MONO BASS</c> doubled as the crossover, so a preset saved before that fix means something
/// different now. The tests that matter most here are therefore the two that say which control
/// owns which axis: the split decides where <c>WIDTH</c> stops and <c>LOW WIDTH</c> starts, and
/// mono bass is a separate collapse that the split does not move.
/// </para>
/// <para>
/// Every measurement is taken over the second half of a one-second tone, because the side signal
/// runs through a filter and the safety limiter has a 4 ms attack and a 120 ms recovery — reading
/// the opening samples measures the settling.
/// </para>
/// </remarks>
public sealed class StereoWidthQualityTests
{
    private const int Rate = 48_000;
    private const int Frames = Rate;                 // 1 s
    private const int SteadyFrom = Frames / 2;

    /// <summary>
    /// A tone in the left channel only, which makes mid and side equal and each half of it — so a
    /// width change is visible without a contrived signal, and the side/mid ratio starts at 1.
    /// </summary>
    private static float[] LeftOnly(double hz)
    {
        float[] buffer = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
            buffer[f * 2] = (float)(0.4 * Math.Sin(2 * Math.PI * hz * f / Rate));
        return buffer;
    }

    /// <summary>The same tone in both channels: all mid, no side.</summary>
    private static float[] Centred(double hz)
    {
        float[] buffer = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
        {
            float sample = (float)(0.4 * Math.Sin(2 * Math.PI * hz * f / Rate));
            buffer[f * 2] = sample;
            buffer[f * 2 + 1] = sample;
        }
        return buffer;
    }

    /// <summary>Nearly out of phase: a small mid and a large side, which is what the safety limiter is for.</summary>
    private static float[] OutOfPhase(double hz)
    {
        float[] buffer = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
        {
            double sample = 0.4 * Math.Sin(2 * Math.PI * hz * f / Rate);
            buffer[f * 2] = (float)sample;
            buffer[f * 2 + 1] = (float)(-0.9 * sample);
        }
        return buffer;
    }

    private static double SideRms(float[] buffer) => Rms(buffer, side: true);
    private static double MidRms(float[] buffer) => Rms(buffer, side: false);

    private static double Rms(float[] buffer, bool side)
    {
        double sum = 0;
        for (int f = SteadyFrom; f < Frames; f++)
        {
            double left = buffer[f * 2], right = buffer[f * 2 + 1];
            double value = side ? (left - right) * 0.5 : (left + right) * 0.5;
            sum += value * value;
        }
        return Math.Sqrt(sum / (Frames - SteadyFrom));
    }

    private static double Db(double measured, double reference) =>
        20 * Math.Log10(Math.Max(measured, 1e-20) / Math.Max(reference, 1e-20));

    private static float[] Run(float[] input, int channels, params (string Key, double Value)[] parameters)
    {
        var effect = new StereoWidthEffect();
        effect.Configure(Rate, channels);
        foreach ((string key, double value) in parameters) effect.SetParam(key, value);
        float[] buffer = (float[])input.Clone();
        effect.Process(buffer, 0, buffer.Length);
        return buffer;
    }

    private static float[] Run(float[] input, params (string Key, double Value)[] parameters) =>
        Run(input, 2, parameters);

    /// <summary>
    /// At its defaults the widener is not "almost" transparent — it does not write to the buffer at
    /// all. An effect that costs a rounding error per sample when it is doing nothing is one nobody
    /// can leave in a chain.
    /// </summary>
    [Fact]
    public void AtUnityWidthTheEffectIsExactlyTransparent()
    {
        float[] input = LeftOnly(1_000);

        float[] output = Run(input);

        Assert.Equal(input, output);
    }

    /// <summary>
    /// WIDTH owns the band above the split and LOW WIDTH the band below it. Closing one leaves the
    /// other's band alone, which is the whole claim of a two-band widener.
    /// </summary>
    [Fact]
    public void EachWidthControlOwnsItsOwnSideOfTheSplit()
    {
        // LOW WIDTH shut: the bass collapses to mono and the top is untouched.
        double bassUnderLowWidth = Db(SideRms(Run(LeftOnly(30), ("lowWidth", 0))), SideRms(LeftOnly(30)));
        double topUnderLowWidth = Db(SideRms(Run(LeftOnly(5_000), ("lowWidth", 0))), SideRms(LeftOnly(5_000)));

        Assert.True(bassUnderLowWidth < -12, $"30 Hz side moved {bassUnderLowWidth:0.0} dB.");
        Assert.InRange(topUnderLowWidth, -0.5, 0.5);

        // WIDTH shut: the mirror image.
        double bassUnderWidth = Db(SideRms(Run(LeftOnly(30), ("width", 0))), SideRms(LeftOnly(30)));
        double topUnderWidth = Db(SideRms(Run(LeftOnly(5_000), ("width", 0))), SideRms(LeftOnly(5_000)));

        Assert.InRange(bassUnderWidth, -0.5, 0.5);
        Assert.True(topUnderWidth < -30, $"5 kHz side moved {topUnderWidth:0.0} dB.");
    }

    /// <summary>
    /// SPLIT FREQ is live, and it is what decides which band a frequency is in. This is the control
    /// the audit found built and never used: with it dead, moving it would change nothing at all.
    /// </summary>
    [Fact]
    public void MovingTheSplitMovesWhichBandAToneBelongsTo()
    {
        float[] input = LeftOnly(300);
        double reference = SideRms(input);

        // The tone sits below a 2 kHz split, so LOW WIDTH governs it...
        double belowTheSplit = Db(SideRms(Run(input, ("lowWidth", 0), ("splitFreq", 2_000))), reference);

        // ...and above a 100 Hz one, where WIDTH does instead and LOW WIDTH cannot reach it.
        double aboveTheSplit = Db(SideRms(Run(input, ("lowWidth", 0), ("splitFreq", 100))), reference);

        Assert.True(belowTheSplit < -10, $"below the split the side moved {belowTheSplit:0.0} dB.");
        Assert.InRange(aboveTheSplit, -1, 1);
    }

    /// <summary>
    /// The high band is the residual of the low one rather than a second filter, so when both
    /// widths agree the pair reduces to a plain scaling with nothing happening at the crossover. A
    /// filtered low plus a filtered high would notch there, and a notch in the side signal is a
    /// hole in the stereo image at one frequency.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(300)]    // the split itself, where a notch would be deepest
    [InlineData(1_000)]
    public void WithBothWidthsEqualTheSideIsScaledFlatThroughTheCrossover(double hz)
    {
        float[] input = LeftOnly(hz);

        float[] output = Run(input, ("width", 1.5), ("lowWidth", 1.5), ("safety", 0));

        Assert.Equal(1.5, SideRms(output) / SideRms(input), 2);
    }

    /// <summary>
    /// MONO BASS does what its label says — it collapses the side below its own cutoff — and it is
    /// not the crossover. Before the audit it was both, so this is the other half of the semantic
    /// change that saved presets are exposed to.
    /// </summary>
    [Fact]
    public void MonoBassCollapsesTheBassOnItsOwnFrequencyNotTheSplits()
    {
        double bass = Db(SideRms(Run(LeftOnly(40), ("monoBass", 200), ("safety", 0))), SideRms(LeftOnly(40)));
        double top = Db(SideRms(Run(LeftOnly(4_000), ("monoBass", 200), ("safety", 0))), SideRms(LeftOnly(4_000)));

        Assert.True(bass < -10, $"40 Hz side moved {bass:0.0} dB.");
        Assert.InRange(top, -0.5, 0.5);

        // And the split cannot move it: the same collapse at either end of SPLIT FREQ's range.
        double atNarrowSplit = SideRms(Run(LeftOnly(40), ("monoBass", 200), ("safety", 0), ("splitFreq", 100)));
        double atWideSplit = SideRms(Run(LeftOnly(40), ("monoBass", 200), ("safety", 0), ("splitFreq", 2_000)));

        Assert.InRange(Db(atWideSplit, atNarrowSplit), -0.05, 0.05);
    }

    /// <summary>
    /// PHASE SAFE bounds how far the side may run ahead of the mid, which is what stops a widened
    /// mix disappearing when it is folded down to mono. At zero it is not a bypass — it relaxes the
    /// bound rather than removing it — and the two settings have to be visibly different.
    /// </summary>
    [Fact]
    public void PhaseSafetyBoundsHowFarTheSideRunsAheadOfTheMid()
    {
        float[] input = OutOfPhase(1_000);

        float[] guarded = Run(input, ("width", 2), ("safety", 1));
        float[] relaxed = Run(input, ("width", 2), ("safety", 0));

        double guardedRatio = Math.Pow(SideRms(guarded) / MidRms(guarded), 2);
        double relaxedRatio = Math.Pow(SideRms(relaxed) / MidRms(relaxed), 2);

        Assert.True(guardedRatio < 1.2, $"guarded side/mid energy was {guardedRatio:0.00}.");
        Assert.True(relaxedRatio > 6, $"relaxed side/mid energy was {relaxedRatio:0.00}.");
    }

    /// <summary>
    /// Haas widening is built from the mid alone and adds an antisymmetric pair to it, so the mono
    /// sum comes back exactly as it went in. That is the property that makes it usable on material
    /// destined for a mono playout — and the same construction is why the source's own side is
    /// discarded rather than widened, which is worth knowing before reaching for the mode.
    /// </summary>
    [Fact]
    public void HaasWideningPreservesTheMonoSumAndIsBuiltFromItAlone()
    {
        // 700 Hz against the default 5 ms delay is three and a half periods, which is where the
        // comb this mode is built on is at a peak. The test below is the other half of that.
        float[] centred = Centred(700);

        float[] widened = Run(centred, ("mode", 1));

        // The sum is untouched, and something was nevertheless added to the sides.
        for (int f = SteadyFrom; f < Frames; f++)
            Assert.Equal(centred[f * 2], (widened[f * 2] + widened[f * 2 + 1]) * 0.5f, 5);
        Assert.True(SideRms(widened) > 0.02, $"Haas produced a side of {SideRms(widened):0.000}.");

        // A stereo source gives the same answer as its own mono fold: the input side is dropped.
        float[] fromStereo = Run(LeftOnly(700), ("mode", 1));
        float[] fromItsMonoSum = Run(Centred(700), ("mode", 1));
        for (int f = SteadyFrom; f < Frames; f++)
            Assert.Equal(fromItsMonoSum[f * 2] * 0.5f, fromStereo[f * 2], 5);
    }

    /// <summary>
    /// Haas widening is a comb, so it has nulls, and at one of them the mode does nothing at all.
    /// </summary>
    /// <remarks>
    /// The side is the difference between the mid and a delayed copy of it, so a frequency whose
    /// period divides the delay is subtracted from itself. At the default 5 ms that is every
    /// multiple of 200 Hz — 1 kHz among them — and the mode is not broken there, it is at a null.
    /// Nothing in the UI says so, which is the reason to write it down here.
    /// </remarks>
    [Fact]
    public void HaasWideningVanishesWhereTheDelayIsAWholeNumberOfPeriods()
    {
        double atTheNull = SideRms(Run(Centred(1_000), ("mode", 1)));   // exactly five periods
        double besideIt = SideRms(Run(Centred(1_100), ("mode", 1)));    // five and a half

        Assert.True(atTheNull < 0.001, $"the null produced a side of {atTheNull:0.0000}.");
        Assert.True(besideIt > 0.02, $"beside it the side was only {besideIt:0.0000}.");
    }

    /// <summary>
    /// There is no side signal in a mono document, so every control is inapplicable rather than
    /// merely ineffective — and the readout says so instead of reporting a correlation of a channel
    /// against itself.
    /// </summary>
    [Fact]
    public void AMonoDocumentIsLeftAloneAndSaysWhy()
    {
        var effect = new StereoWidthEffect();
        effect.Configure(Rate, 1);
        effect.SetParam("width", 2);
        effect.SetParam("monoBass", 400);

        float[] input = new float[Frames];
        for (int f = 0; f < Frames; f++) input[f] = (float)(0.4 * Math.Sin(2 * Math.PI * 1_000 * f / Rate));
        float[] output = (float[])input.Clone();
        effect.Process(output, 0, output.Length);

        Assert.Equal(input, output);
        Assert.Equal("STEREO ONLY", effect.Readout);
    }
}
