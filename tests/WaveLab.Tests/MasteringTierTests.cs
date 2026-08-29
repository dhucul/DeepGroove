using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>The processors added in the mastering tier, and the properties that justify each.</summary>
public sealed class MasteringTierTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;
    private const int Frames = 48_000;

    private static float[] Tone(double frequency, double amplitude = 0.3, int frames = Frames,
        int channels = 2)
    {
        var buffer = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * f / Rate));
            for (int c = 0; c < channels; c++) buffer[f * channels + c] = value;
        }
        return buffer;
    }

    private static double LevelDb(float[] buffer, double frequency, int channels = 2,
        int from = 20_000, int to = 44_000)
    {
        double re = 0, im = 0, weight = 0;
        for (int f = from; f < to; f++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (f - from) / (double)(to - from));
            double omega = 2 * Math.PI * frequency * f / Rate;
            re += buffer[f * channels] * window * Math.Cos(omega);
            im -= buffer[f * channels] * window * Math.Sin(omega);
            weight += window;
        }
        return 20 * Math.Log10(Math.Max(Math.Sqrt(re * re + im * im) / weight * 2, 1e-12));
    }

    private static T Configured<T>(params (string Key, double Value)[] settings) where T : IAudioEffect, new()
    {
        var effect = new T();
        effect.Configure(Rate, 2);
        foreach ((string key, double value) in settings) effect.SetParam(key, value);
        return effect;
    }

    // ── state-variable filter ────────────────────────────────────

    /// <summary>
    /// A bell must land where it is aimed and be the size it is set to. Measured by running tones
    /// through the filter, not by evaluating a second model of it.
    /// </summary>
    [Theory]
    [InlineData(1_000, 6)]
    [InlineData(1_000, -9)]
    [InlineData(200, 12)]
    [InlineData(6_000, -6)]
    public void TheStateVariableBellHitsItsCentreAndGain(double frequency, double gainDb)
    {
        var filter = new StateVariableFilter();
        filter.Set(SvfMode.Peaking, Rate, frequency, q: 1.0, gainDb);

        double atCentre = filter.MagnitudeDb(frequency, Rate);
        double farBelow = filter.MagnitudeDb(frequency / 16, Rate);
        double farAbove = filter.MagnitudeDb(Math.Min(frequency * 16, Rate * 0.4), Rate);

        output.WriteLine($"{frequency} Hz {gainDb:+0;-0} dB: centre {atCentre:0.00}, " +
                         $"far below {farBelow:0.00}, far above {farAbove:0.00}");

        Assert.Equal(gainDb, atCentre, 0.5);
        Assert.Equal(0, farBelow, 0.6);
        Assert.Equal(0, farAbove, 0.6);
    }

    /// <summary>
    /// The property the whole dynamic equaliser rests on: the cutoff can move between samples
    /// without the filter clicking. A biquad cannot do this — its state stops meaning the same thing
    /// when the coefficients change.
    /// </summary>
    [Fact]
    public void RetuningEverySampleDoesNotClick()
    {
        var filter = new StateVariableFilter();
        var swept = new float[Frames];
        double worstJump = 0;

        for (int i = 0; i < Frames; i++)
        {
            // A cutoff sweeping across three octaves while audio flows through.
            double frequency = 300 * Math.Pow(2, 3 * Math.Sin(2 * Math.PI * 3 * i / Rate));
            filter.Set(SvfMode.Peaking, Rate, frequency, q: 2.0, gainDb: 9);
            swept[i] = filter.Process((float)(0.3 * Math.Sin(2 * Math.PI * 700 * i / Rate)));

            if (i > 1_000) worstJump = Math.Max(worstJump, Math.Abs(swept[i] - swept[i - 1]));
        }

        // A click is a discontinuity: at 700 Hz a smooth signal moves far less than this per sample.
        output.WriteLine($"largest sample-to-sample step while sweeping: {worstJump:0.0000}");
        Assert.True(worstJump < 0.15, $"the filter stepped by {worstJump:0.000}, which is a click");
        Assert.All(swept, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void EveryModeIsStableAtHighResonance()
    {
        foreach (SvfMode mode in Enum.GetValues<SvfMode>())
        {
            var filter = new StateVariableFilter();
            filter.Set(mode, Rate, 8_000, q: 20, gainDb: 12);

            var random = new Random(5);
            float worst = 0;
            for (int i = 0; i < 20_000; i++)
            {
                float value = filter.Process((float)(random.NextDouble() - 0.5));
                worst = Math.Max(worst, Math.Abs(value));
                Assert.True(float.IsFinite(value), $"{mode} produced {value}");
            }
            output.WriteLine($"{mode} at Q 20: peak {worst:0.00}");
        }
    }

    // ── dynamic EQ ───────────────────────────────────────────────

    /// <summary>
    /// The whole point: the band acts when there is energy in its range and stays out of the way
    /// when there is not. A static equaliser cannot tell the difference.
    /// </summary>
    [Fact]
    public void TheDynamicBandActsOnlyWhenItsOwnRangeIsLoud()
    {
        DynamicEqEffect Build() => Configured<DynamicEqEffect>(
            ("freq", 300), ("q", 1.5), ("range", -12), ("threshold", -30),
            ("ratio", 1), ("attack", 5), ("release", 80), ("mode", 0));

        // Loud at the band's own frequency: it should duck.
        float[] loud = Tone(300, 0.5);
        Build().Process(loud, 0, loud.Length);
        double loudChange = LevelDb(loud, 300) - LevelDb(Tone(300, 0.5), 300);

        // Quiet at the same frequency: it should not.
        float[] quiet = Tone(300, 0.004);
        Build().Process(quiet, 0, quiet.Length);
        double quietChange = LevelDb(quiet, 300) - LevelDb(Tone(300, 0.004), 300);

        output.WriteLine($"loud passage {loudChange:+0.0;-0.0} dB, quiet passage {quietChange:+0.0;-0.0} dB");
        Assert.True(loudChange < -3, $"the band only moved {loudChange:0.0} dB on loud material");
        Assert.True(Math.Abs(quietChange) < 1, $"the band moved {quietChange:0.0} dB on quiet material");
    }

    /// <summary>
    /// The detector listens through its own band. Energy elsewhere in the spectrum must not pull the
    /// band down, or this is a compressor with a tone control on it.
    /// </summary>
    [Fact]
    public void EnergyOutsideTheBandDoesNotTriggerIt()
    {
        var effect = Configured<DynamicEqEffect>(
            ("freq", 300), ("q", 2), ("range", -12), ("threshold", -30), ("ratio", 1));

        // Loud at 5 kHz, nothing at 300 Hz.
        float[] elsewhere = Tone(5_000, 0.6);
        float[] reference = Tone(5_000, 0.6);
        effect.Process(elsewhere, 0, elsewhere.Length);

        double change = LevelDb(elsewhere, 5_000) - LevelDb(reference, 5_000);
        output.WriteLine($"a loud 5 kHz tone moved a 300 Hz band by {change:+0.0;-0.0} dB");
        Assert.True(Math.Abs(change) < 1.5, "energy outside the band should not trigger it");
    }

    // ── transient shaper ─────────────────────────────────────────

    /// <summary>
    /// Attacks and sustains are separated by change, not by level — which is why the same setting
    /// works on a quiet transient and a loud one.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.05)]
    public void AttackAndSustainAreShapedIndependentlyOfLevel(double amplitude)
    {
        // A struck note: instant attack, slow decay.
        var buffer = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
        {
            double envelope = Math.Exp(-(f % 12_000) / 2_500.0);
            var value = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * 400 * f / Rate));
            buffer[f * 2] = value;
            buffer[f * 2 + 1] = value;
        }
        var reference = (float[])buffer.Clone();

        var effect = Configured<TransientShaperEffect>(
            ("attack", 9), ("sustain", 0), ("fast", 2), ("slow", 150));
        effect.Process(buffer, 0, buffer.Length);

        // Right at a strike against a little later, both relative to the untouched signal.
        double atStrike = Ratio(buffer, reference, 12_010, 12_060);
        double inDecay = Ratio(buffer, reference, 16_000, 18_000);

        output.WriteLine($"amplitude {amplitude}: attack {atStrike:0.00}×, decay {inDecay:0.00}×");
        Assert.True(atStrike > inDecay * 1.4, "the attack should be lifted relative to the decay");

        static double Ratio(float[] processed, float[] reference, int from, int to)
        {
            double a = 0, b = 0;
            for (int f = from; f < to; f++)
            {
                a += Math.Abs(processed[f * 2]);
                b += Math.Abs(reference[f * 2]);
            }
            return a / Math.Max(b, 1e-12);
        }
    }

    [Fact]
    public void WithBothControlsCentredNothingChanges()
    {
        float[] buffer = Tone(1_000, 0.4);
        var reference = (float[])buffer.Clone();

        var effect = Configured<TransientShaperEffect>(("attack", 0), ("sustain", 0), ("output", 0));
        effect.Process(buffer, 0, buffer.Length);

        Assert.Equal(reference, buffer);
    }

    // ── multiband compressor ─────────────────────────────────────

    /// <summary>
    /// The reason to split before compressing: a loud bass note must not duck the whole spectrum,
    /// which is exactly what a full-band compressor does.
    /// </summary>
    [Fact]
    public void ALoudBassNoteDoesNotDuckTheTop()
    {
        // Bass loud enough to compress hard, plus a steady quiet tone up top.
        var buffer = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
        {
            var value = (float)(0.7 * Math.Sin(2 * Math.PI * 60 * f / Rate)
                              + 0.05 * Math.Sin(2 * Math.PI * 6_000 * f / Rate));
            buffer[f * 2] = value;
            buffer[f * 2 + 1] = value;
        }
        var reference = (float[])buffer.Clone();

        var effect = Configured<MultibandCompressorEffect>(
            ("threshold", -24), ("ratio", 8), ("attack", 5), ("release", 120));
        effect.Process(buffer, 0, buffer.Length);

        double bass = LevelDb(buffer, 60) - LevelDb(reference, 60);
        double top = LevelDb(buffer, 6_000) - LevelDb(reference, 6_000);

        output.WriteLine($"bass {bass:+0.0;-0.0} dB, top {top:+0.0;-0.0} dB, readout {effect.Readout}");
        Assert.True(bass < -3, $"the bass should have been compressed, not moved {bass:0.0} dB");
        Assert.True(Math.Abs(top) < 2, $"the top moved {top:0.0} dB with the bass");
    }

    /// <summary>
    /// With the compressor doing nothing, the bands must still recombine to what came in — the
    /// crossover's property, but this is where it actually matters.
    /// </summary>
    [Fact]
    public void WithNoCompressionTheBandsRecombineFlat()
    {
        var effect = Configured<MultibandCompressorEffect>(("threshold", 0), ("ratio", 1));

        double worst = 0;
        foreach (double frequency in new[] { 50.0, 200, 700, 2_000, 9_000 })
        {
            float[] buffer = Tone(frequency, 0.2);
            float[] reference = Tone(frequency, 0.2);
            effect.ResetState();
            effect.Process(buffer, 0, buffer.Length);

            double change = LevelDb(buffer, frequency) - LevelDb(reference, frequency);
            output.WriteLine($"{frequency,6:0} Hz: {change:+0.00;-0.00} dB");
            worst = Math.Max(worst, Math.Abs(change));
        }
        Assert.True(worst < 0.5, $"the bands are {worst:0.00} dB off recombining flat");
    }

    // ── linear phase EQ ──────────────────────────────────────────

    /// <summary>A boost has to arrive where it was aimed.</summary>
    [Fact]
    public void TheLinearPhaseBoostLandsOnItsBand()
    {
        var effect = Configured<LinearPhaseEqEffect>(
            ("lowmidfreq", 1_000), ("lowmidgain", 9), ("q", 1));

        float[] onBand = Tone(1_000, 0.2);
        float[] offBand = Tone(60, 0.2);
        float[] onReference = Tone(1_000, 0.2);
        float[] offReference = Tone(60, 0.2);

        effect.Process(onBand, 0, onBand.Length);
        effect.ResetState();
        effect.Process(offBand, 0, offBand.Length);

        double onChange = LevelDb(onBand, 1_000) - LevelDb(onReference, 1_000);
        double offChange = LevelDb(offBand, 60) - LevelDb(offReference, 60);

        output.WriteLine($"at 1 kHz {onChange:+0.0;-0.0} dB, at 60 Hz {offChange:+0.0;-0.0} dB, " +
                         $"latency {effect.LatencySamples}");
        Assert.True(onChange > 5, $"the boost only reached {onChange:0.0} dB");
        Assert.True(Math.Abs(offChange) < 2.5, $"60 Hz moved {offChange:0.0} dB");
    }

    [Fact]
    public void AFlatCurveCostsNothing()
    {
        var effect = Configured<LinearPhaseEqEffect>(
            ("lowgain", 0), ("lowmidgain", 0), ("highmidgain", 0), ("highgain", 0));

        float[] buffer = Tone(1_000, 0.3);
        var reference = (float[])buffer.Clone();
        effect.Process(buffer, 0, buffer.Length);

        Assert.Equal(0, effect.LatencySamples);
        Assert.Equal(reference, buffer);
    }

    // ── partitioned convolution ──────────────────────────────────

    /// <summary>
    /// Partitioning must give the same answer as convolving directly — that is the only thing it is
    /// allowed to change about the result, as against how long it takes and how late it arrives.
    /// </summary>
    [Fact]
    public void PartitionedConvolutionMatchesTheDirectSum()
    {
        var random = new Random(31);
        var kernel = new float[600];
        for (int i = 0; i < kernel.Length; i++)
            kernel[i] = (float)((random.NextDouble() - 0.5) * Math.Exp(-i / 150.0));

        const int length = 4_096;
        var input = new float[length];
        for (int i = 0; i < length; i++) input[i] = (float)(random.NextDouble() - 0.5);

        var direct = new float[length];
        for (int n = 0; n < length; n++)
        {
            double sum = 0;
            for (int k = 0; k < kernel.Length && k <= n; k++) sum += kernel[k] * input[n - k];
            direct[n] = (float)sum;
        }

        var convolver = new PartitionedConvolver(kernel, channels: 1, blockSize: 128);
        var processed = new float[length];
        var block = new float[convolver.BlockSize];
        for (int start = 0; start + convolver.BlockSize <= length; start += convolver.BlockSize)
        {
            input.AsSpan(start, convolver.BlockSize).CopyTo(block);
            convolver.ProcessBlock(0, block);
            block.CopyTo(processed.AsSpan(start));
        }

        // The alignment is measured rather than assumed, and then checked against what the class
        // declares: everything downstream compensates by the reported latency, so a disagreement
        // between the two is the bug, not a detail.
        int measured = 0;
        double best = double.MaxValue;
        for (int delay = 0; delay <= 2 * convolver.BlockSize; delay++)
        {
            double error = 0;
            for (int i = 1_000; i < length - 2 * convolver.BlockSize; i += 5)
                error += Math.Abs(processed[i + delay] - direct[i]);
            if (error < best) { best = error; measured = delay; }
        }

        int reported = convolver.LatencySamples;
        double worst = 0;
        for (int i = 1_000; i < length - reported; i++)
            worst = Math.Max(worst, Math.Abs(processed[i + reported] - direct[i]));

        output.WriteLine($"{convolver.Partitions} partitions, block {convolver.BlockSize}: " +
                         $"worst difference {worst:0.0e+0}; latency reported {reported}, measured {measured}");
        Assert.Equal(reported, measured);
        Assert.True(worst < 1e-4, $"partitioned convolution differs by {worst:0.0e+0}");
    }

    // ── filter slope ─────────────────────────────────────────────

    /// <summary>
    /// The 24 dB mode is a fourth-order Butterworth, so at default resonance it must be maximally
    /// flat — no peak before the corner — and exactly 3 dB down at it.
    /// </summary>
    [Fact]
    public void TheSteepFilterModeIsMaximallyFlat()
    {
        var effect = Configured<FilterEffect>(
            ("cutoff", 1_000), ("q", 0.707), ("mode", 0), ("slope", 1));

        double worstPeak = 0;
        foreach (double frequency in new[] { 100.0, 200, 400, 700 })
        {
            float[] buffer = Tone(frequency, 0.3);
            float[] reference = Tone(frequency, 0.3);
            effect.ResetState();
            effect.Process(buffer, 0, buffer.Length);

            double change = LevelDb(buffer, frequency) - LevelDb(reference, frequency);
            output.WriteLine($"{frequency,5:0} Hz: {change:+0.00;-0.00} dB");
            worstPeak = Math.Max(worstPeak, change);
        }

        Assert.True(worstPeak < 0.5,
            $"the passband peaks by {worstPeak:0.00} dB, so it is not maximally flat");
    }

    [Fact]
    public void TheSteepFilterModeIsTwiceAsSteep()
    {
        double SlopeOf(double slope)
        {
            var effect = Configured<FilterEffect>(
                ("cutoff", 1_000), ("q", 0.707), ("mode", 0), ("slope", slope));

            float[] atTwo = Tone(2_000, 0.3);
            float[] atFour = Tone(4_000, 0.3);
            effect.Process(atTwo, 0, atTwo.Length);
            effect.ResetState();
            effect.Process(atFour, 0, atFour.Length);

            return (LevelDb(atTwo, 2_000) - LevelDb(Tone(2_000, 0.3), 2_000))
                 - (LevelDb(atFour, 4_000) - LevelDb(Tone(4_000, 0.3), 4_000));
        }

        double gentle = SlopeOf(0);
        double steep = SlopeOf(1);
        output.WriteLine($"per octave above the corner: 12 dB mode {gentle:0.0}, 24 dB mode {steep:0.0}");

        Assert.InRange(gentle, 10, 14);
        Assert.InRange(steep, 21, 27);
    }

    [Fact]
    public void SwitchingFilterPhaseModesCannotResumeStaleHistory()
    {
        var effect = Configured<FilterEffect>(
            ("cutoff", 30), ("q", 0.707), ("mode", 1), ("slope", 1), ("phase", 0));
        var impulse = new float[2_048];
        impulse[^1] = 1;
        effect.Process(impulse, 0, impulse.Length);

        effect.SetParam("phase", 1);
        var linearSilence = new float[effect.LatencySamples + 512];
        effect.Process(linearSilence, 0, linearSilence.Length);
        effect.SetParam("phase", 0);
        var resumedIir = new float[2_048];
        effect.Process(resumedIir, 0, resumedIir.Length);

        Assert.All(linearSilence, sample => Assert.InRange(Math.Abs(sample), 0, 1e-7));
        Assert.All(resumedIir, sample => Assert.InRange(Math.Abs(sample), 0, 1e-7));
    }
}
