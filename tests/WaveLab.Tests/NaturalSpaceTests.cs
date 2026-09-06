using WaveLab.Audio;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

public sealed class NaturalSpaceTests
{
    private const int Rate = 48_000;

    private static MonoToStereoEffect Create(int rate = Rate, double amount = 0.28)
    {
        var effect = new MonoToStereoEffect();
        effect.SetParam("algorithm", 3);
        effect.SetParam("amount", amount);
        effect.SetParam("bass", 180);
        effect.SetParam("safety", 0.9);
        effect.Configure(rate, 2);
        return effect;
    }

    private static float[] Noise(int frames, bool stereo = false)
    {
        var random = new Random(7321);
        var result = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            result[2 * i] = (float)(random.NextDouble() - 0.5) * 0.5f;
            result[2 * i + 1] = stereo
                ? (float)(random.NextDouble() - 0.5) * 0.5f : result[2 * i];
        }
        return result;
    }

    [Fact]
    public void WidthIsDelayedAudioRatherThanChannelGain()
    {
        var effect = Create();
        var impulse = new float[Rate / 2];
        impulse[0] = impulse[1] = 0.5f;
        effect.Process(impulse, 0, impulse.Length);
        Assert.Equal(0.5f, impulse[0]);
        Assert.Equal(0.5f, impulse[1]);
        // A pan/volume change cannot produce output after the input impulse ends.
        Assert.Contains(impulse.Skip(2), sample => Math.Abs(sample) > 0.001);
        for (int i = 2; i < impulse.Length; i += 2)
            Assert.Equal(-impulse[i], impulse[i + 1]);
        Assert.All(impulse.TakeLast(200), sample => Assert.InRange(Math.Abs(sample), 0, 1e-7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MonoFoldIsUnchangedForMonoAndStereoSources(bool stereo)
    {
        float[] source = Noise(Rate, stereo);
        float[] output = (float[])source.Clone();
        Create().Process(output, 0, output.Length);
        for (int i = 0; i < source.Length; i += 2)
            Assert.InRange(Math.Abs((double)output[i] + output[i + 1] - source[i] - source[i + 1]), 0, 1e-7);
    }

    [Fact]
    public void ExistingStereoDetailIsRetainedAlongsideTheAddedAmbience()
    {
        float[] stereo = Noise(Rate, true);
        var mono = new float[stereo.Length];
        for (int i = 0; i < stereo.Length; i += 2)
            mono[i] = mono[i + 1] = (stereo[i] + stereo[i + 1]) * 0.5f;
        float[] output = (float[])stereo.Clone();
        Create().Process(output, 0, output.Length);
        Create().Process(mono, 0, mono.Length);
        for (int i = 0; i < output.Length; i += 2)
        {
            double originalSide = (stereo[i] - stereo[i + 1]) * 0.5;
            double addedSide = (mono[i] - mono[i + 1]) * 0.5;
            Assert.InRange(Math.Abs((output[i] - output[i + 1]) * 0.5 - originalSide - addedSide), 0, 1e-7);
        }
    }

    [Fact]
    public void BroadbandMonoStaysBalancedWithSubtleMeasurableWidth()
    {
        float[] output = Noise(Rate * 3);
        Create().Process(output, 0, output.Length);
        double left = 0, right = 0, side = 0, mid = 0;
        for (int i = Rate * 2; i < output.Length; i += 2)
        {
            left += output[i] * (double)output[i];
            right += output[i + 1] * (double)output[i + 1];
            side += Math.Pow((output[i] - output[i + 1]) * 0.5, 2);
            mid += Math.Pow((output[i] + output[i + 1]) * 0.5, 2);
        }
        Assert.InRange(Math.Abs(10 * Math.Log10(left / right)), 0, 0.1);
        Assert.InRange(Math.Sqrt(side / mid), 0.05, 0.3);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void LowBassIsProtectedAcrossSampleRates(int rate)
    {
        double SideRatio(double frequency)
        {
            float[] tone = new float[rate * 4];
            for (int i = 0; i < tone.Length; i += 2)
                tone[i] = tone[i + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * frequency * i / (2 * rate)));
            var effect = Create(rate, 1);
            effect.SetParam("safety", 0);
            effect.Process(tone, 0, tone.Length);
            double side = 0, mid = 0;
            for (int i = rate * 2; i < tone.Length; i += 2)
            {
                Assert.True(float.IsFinite(tone[i]) && float.IsFinite(tone[i + 1]));
                side += Math.Pow((tone[i] - tone[i + 1]) * 0.5, 2);
                mid += Math.Pow((tone[i] + tone[i + 1]) * 0.5, 2);
            }
            return Math.Sqrt(side / mid);
        }
        Assert.InRange(SideRatio(40), 0, 0.05);
        Assert.True(SideRatio(997) > 0.15);
    }

    [Fact]
    public void ZeroAmountIsTransparentAndResetClearsReflections()
    {
        var effect = Create(amount: 0);
        float[] source = Noise(Rate, true);
        float[] output = (float[])source.Clone();
        effect.Process(output, 0, output.Length);
        Assert.Equal(source, output);
        effect.SetParam("amount", 1);
        effect.ResetState();
        var silence = new float[Rate];
        effect.Process(silence, 0, silence.Length);
        Assert.All(silence, sample => Assert.Equal(0, sample));
        effect.ResetState();
        float[] fresh = (float[])source.Clone();
        Create(amount: 1).Process(fresh, 0, fresh.Length);
        effect.Process(output, 0, output.Length);
        Assert.Equal(fresh, output);
    }

    [Fact]
    public void CallbackSizesAndNonzeroOffsetsDoNotChangeTheAudio()
    {
        float[] whole = Noise(Rate);
        float[] chunked = (float[])whole.Clone();
        Create().Process(whole, 0, whole.Length);
        var effect = Create();
        for (int offset = 0; offset < chunked.Length; offset += 254)
            effect.Process(chunked, offset, Math.Min(254, chunked.Length - offset));
        Assert.Equal(whole, chunked);
    }

    [Fact]
    public void SettingsAppliedAfterConfigureMatchSettingsAppliedBeforeConfigure()
    {
        var live = new MonoToStereoEffect();
        live.Configure(Rate, 2);
        live.SetParam("algorithm", 3);
        live.SetParam("delay", 25);
        var offline = EffectFactory.Clone(live);
        offline.Configure(Rate, 2);
        float[] streamed = Noise(Rate);
        float[] rendered = (float[])streamed.Clone();
        live.Process(streamed, 0, streamed.Length);
        offline.Process(rendered, 0, rendered.Length);
        Assert.Equal(rendered, streamed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReturningToNaturalSpaceDoesNotReplayAnOldReflection(int otherAlgorithm)
    {
        var effect = Create();
        float[] programme = Noise(1200);
        effect.Process(programme, 0, programme.Length);
        effect.SetParam("algorithm", otherAlgorithm);
        var elapsedSilence = new float[Rate];
        effect.Process(elapsedSilence, 0, elapsedSilence.Length);
        effect.SetParam("algorithm", 3);
        var returned = new float[Rate];
        effect.Process(returned, 0, returned.Length);
        Assert.Equal(0f, returned.Max(sample => Math.Abs(sample)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1e-10)]
    [InlineData(1e-9)]
    public void InactiveAmountDoesNotAppendSilenceToCopyRenders(double amount)
    {
        using var master = new MasterSection();
        master.ReplaceChain([Create(amount: amount)]);
        float[][] output = master.ProcessOffline([new float[100]], Rate, includeTail: true);
        Assert.Equal(100, output[0].Length);
    }

    [Fact]
    public void CopyRenderKeepsTheReflectionsAfterTheLastNote()
    {
        using var master = new MasterSection();
        master.ReplaceChain([Create()]);
        var source = new float[100];
        source[^1] = 0.5f;
        float[][] copy = master.ProcessOffline([source], Rate, includeTail: true);
        Assert.Equal(source.Length + Rate / 4, copy[0].Length);
        Assert.Contains(copy[0].Skip(source.Length), sample => Math.Abs(sample) > 0.001);
        Assert.All(copy[0].TakeLast(200), sample => Assert.InRange(Math.Abs(sample), 0, 1e-7));
        float[][] inPlace = master.ProcessOffline([source], Rate);
        Assert.Equal(source.Length, inPlace[0].Length);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(25)]
    public void FullAmountRemainsFiniteAndSafetyBoundsAddedWidth(double space)
    {
        var effect = Create(amount: 1);
        effect.SetParam("delay", space);
        effect.SetParam("bass", 40);
        effect.SetParam("safety", 1);
        float[] samples = Noise(Rate * 3);
        effect.Process(samples, 0, samples.Length);
        double side = 0, mid = 0;
        for (int i = Rate * 2; i < samples.Length; i += 2)
        {
            Assert.True(float.IsFinite(samples[i]) && float.IsFinite(samples[i + 1]));
            side += Math.Pow((samples[i] - samples[i + 1]) * 0.5, 2);
            mid += Math.Pow((samples[i] + samples[i + 1]) * 0.5, 2);
        }
        Assert.InRange(Math.Sqrt(side / mid), 0.1, 0.36);
    }

    [Fact]
    public void SavedPresetRetainsNaturalSpaceAndMonoRendersMatchStreaming()
    {
        var preset = EffectFactory.CreateFactoryPreset("Record to CD - Natural Space");
        var roundTrip = EffectFactory.Instantiate(EffectFactory.Capture(preset.Name, EffectFactory.Instantiate(preset)));
        var effect = Assert.IsType<MonoToStereoEffect>(Assert.Single(roundTrip, fx => fx.TypeId == "mono-stereo"));
        Assert.Equal(3, effect.GetParam("algorithm"));
        Assert.Equal(-1, roundTrip.Last().GetParam("ceiling"));
        var stereo = Noise(Rate);
        var mono = Enumerable.Range(0, Rate).Select(i => stereo[i * 2]).ToArray();
        using var master = new MasterSection();
        master.ReplaceChain([effect]);
        Assert.True(master.ExpandsMonoToStereo);
        float[][] rendered = master.ProcessOffline([mono], Rate);
        var direct = EffectFactory.Clone(effect);
        direct.Configure(Rate, 2);
        direct.Process(stereo, 0, stereo.Length);
        Assert.Equal(2, rendered.Length);
        for (int i = 0; i < Rate; i++)
        {
            Assert.Equal(stereo[i * 2], rendered[0][i]);
            Assert.Equal(stereo[i * 2 + 1], rendered[1][i]);
        }
    }
}
