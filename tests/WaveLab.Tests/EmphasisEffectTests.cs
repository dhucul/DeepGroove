using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

public sealed class EmphasisEffectTests
{
    [Theory]
    [InlineData(44_100, 0, 50e-6, 1_000)]
    [InlineData(44_100, 0, 50e-6, 15_000)]
    [InlineData(48_000, 0, 50e-6, 10_000)]
    [InlineData(48_000, 0, 50e-6, 15_000)]
    [InlineData(44_100, 1, 75e-6, 1_000)]
    [InlineData(44_100, 1, 75e-6, 15_000)]
    [InlineData(48_000, 1, 75e-6, 10_000)]
    [InlineData(48_000, 1, 75e-6, 15_000)]
    public void FmDeEmphasisTracksTheSpecifiedRcCurve(
        int sampleRate, int standard, double tau, double frequency)
    {
        EmphasisEffect effect = Configured(sampleRate, standard, emphasis: false);

        double measured = MeasuredDb(effect, sampleRate, frequency);
        double expected = -10.0 * Math.Log10(1.0 + Math.Pow(2.0 * Math.PI * frequency * tau, 2));

        // The ideal RC curve has infinite Nyquist gain in the reciprocal direction. The stable
        // minimum-phase shelf is fitted across the 15 kHz FM programme band.
        Assert.InRange(measured, expected - 0.45, expected + 0.45);
    }

    [Theory]
    [InlineData(44_100, 1_000)]
    [InlineData(44_100, 10_000)]
    [InlineData(44_100, 20_000)]
    [InlineData(48_000, 1_000)]
    [InlineData(48_000, 10_000)]
    [InlineData(48_000, 20_000)]
    public void CdDeEmphasisTracksTheFiftyFifteenMicrosecondCurve(
        int sampleRate, double frequency)
    {
        EmphasisEffect effect = Configured(sampleRate, 2, emphasis: false);

        double measured = MeasuredDb(effect, sampleRate, frequency);
        double omega = 2.0 * Math.PI * frequency;
        double expected = 10.0 * Math.Log10(
            (1.0 + Math.Pow(omega * 15e-6, 2)) /
            (1.0 + Math.Pow(omega * 50e-6, 2)));

        Assert.InRange(measured, expected - 0.55, expected + 0.55);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EmphasisAndDeEmphasisAreReciprocal(int standard)
    {
        EmphasisEffect emphasis = Configured(48_000, standard, emphasis: true);
        EmphasisEffect deemphasis = Configured(48_000, standard, emphasis: false);
        var random = new Random(1729 + standard);
        float[] original = Enumerable.Range(0, 32_768)
            .Select(_ => (float)((random.NextDouble() * 2.0 - 1.0) * 0.02))
            .ToArray();
        float[] processed = (float[])original.Clone();

        emphasis.Process(processed, 0, processed.Length);
        deemphasis.Process(processed, 0, processed.Length);

        double maxError = original.Zip(processed, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxError < 2e-5, $"Maximum round-trip error was {maxError:E3}.");
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    public void EveryCurveAndDirectionStaysFiniteAtSupportedRates(int sampleRate)
    {
        for (int standard = 0; standard < 3; standard++)
        for (int direction = 0; direction < 2; direction++)
        {
            var effect = new EmphasisEffect();
            effect.SetParam("standard", standard);
            effect.SetParam("direction", direction);
            effect.Configure(sampleRate, 2);
            float[] signal = Enumerable.Range(0, 4096)
                .Select(index => (float)(0.1 * Math.Sin(index * 2.91)))
                .ToArray();

            effect.Process(signal, 0, signal.Length);

            Assert.All(signal, sample => Assert.True(float.IsFinite(sample)));
        }
    }

    [Fact]
    public void FactoryAndPresetRoundTripKeepTheSelectedStandardAndDirection()
    {
        IAudioEffect effect = EffectFactory.Create("deemphasis");
        effect.SetParam("standard", 1);
        effect.SetParam("direction", 1);

        EffectFactory.ChainPreset captured = EffectFactory.Capture("FM transmit", [effect]);
        IAudioEffect restored = Assert.Single(EffectFactory.Instantiate(captured));

        Assert.IsType<EmphasisEffect>(restored);
        Assert.Equal(1, restored.GetParam("standard"));
        Assert.Equal(1, restored.GetParam("direction"));
        Assert.Contains(EffectFactory.Available, item => item.TypeId == "deemphasis");
    }

    [Fact]
    public void DiscreteParametersSnapBeforeTheyReachProcessingOrPresets()
    {
        var effect = new EmphasisEffect();

        effect.SetParam("direction", 0.5);
        effect.SetParam("standard", 1.49);

        Assert.Equal(1, effect.GetParam("direction"));
        Assert.Equal(1, effect.GetParam("standard"));
        EffectFactory.EffectState saved = Assert.Single(
            EffectFactory.Capture("snapped", [effect]).Effects);
        Assert.Equal(1, saved.Params["direction"]);
        Assert.Equal(1, saved.Params["standard"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ParameterRevisionStartsFromTheSameStateAsAFreshEffect(int standard)
    {
        EmphasisEffect changed = Configured(48_000, (standard + 1) % 3, emphasis: false);
        EmphasisEffect fresh = Configured(48_000, standard, emphasis: true);
        float[] history = Enumerable.Repeat(0.25f, 4096).ToArray();
        changed.Process(history, 0, history.Length);

        changed.SetParam("standard", standard);
        changed.SetParam("direction", 1);
        float[] changedImpulse = new float[4096];
        float[] freshImpulse = new float[4096];
        changedImpulse[0] = freshImpulse[0] = 0.05f;

        changed.Process(changedImpulse, 0, changedImpulse.Length);
        fresh.Process(freshImpulse, 0, freshImpulse.Length);

        Assert.Equal(freshImpulse, changedImpulse);
    }

    [Fact]
    public void RequestedResetIsAppliedByTheNextProcessingBlock()
    {
        EmphasisEffect changed = Configured(48_000, 1, emphasis: false);
        EmphasisEffect fresh = Configured(48_000, 1, emphasis: false);
        float[] history = Enumerable.Repeat(0.25f, 4096).ToArray();
        changed.Process(history, 0, history.Length);
        changed.ResetState();
        float[] changedImpulse = new float[4096];
        float[] freshImpulse = new float[4096];
        changedImpulse[0] = freshImpulse[0] = 0.05f;

        changed.Process(changedImpulse, 0, changedImpulse.Length);
        fresh.Process(freshImpulse, 0, freshImpulse.Length);

        Assert.Equal(freshImpulse, changedImpulse);
    }

    private static EmphasisEffect Configured(int sampleRate, int standard, bool emphasis)
    {
        var effect = new EmphasisEffect();
        effect.SetParam("standard", standard);
        effect.SetParam("direction", emphasis ? 1 : 0);
        effect.Configure(sampleRate, 1);
        return effect;
    }

    private static double MeasuredDb(EmphasisEffect effect, int sampleRate, double frequency)
    {
        int frames = sampleRate * 2;
        float[] signal = new float[frames];
        double phaseStep = 2.0 * Math.PI * frequency / sampleRate;
        for (int index = 0; index < signal.Length; index++)
            signal[index] = (float)(0.1 * Math.Sin(index * phaseStep));

        effect.ResetState();
        effect.Process(signal, 0, signal.Length);

        int start = sampleRate / 2;
        double sum = 0;
        for (int index = start; index < signal.Length; index++)
            sum += signal[index] * signal[index];
        double rms = Math.Sqrt(sum / (signal.Length - start));
        return 20.0 * Math.Log10(rms / (0.1 / Math.Sqrt(2.0)));
    }
}
