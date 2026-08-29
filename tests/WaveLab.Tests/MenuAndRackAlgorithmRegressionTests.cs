using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>Algorithm defects found while auditing the remaining menu tools and rack.</summary>
public sealed class MenuAndRackAlgorithmRegressionTests
{
    private const int Rate = 48_000;

    [Fact]
    public void EqualPowerFadeMeetsMinusThreeDbAtItsMidpoint()
    {
        var document = new AudioDocument([Enumerable.Repeat(1f, 101).ToArray()], Rate, 32);

        Processing.FadeIn(document, 0, document.Length);

        Assert.Equal(0f, document.Channels[0][0], 6);
        Assert.Equal(Math.Sqrt(0.5), document.Channels[0][50], 5);
        Assert.Equal(1f, document.Channels[0][100], 6);
    }

    [Fact]
    public void SCurveFadeReachesDigitalSilenceAndUnityExactly()
    {
        var fadeIn = new AudioDocument([Enumerable.Repeat(1f, 101).ToArray()], Rate, 32);
        var fadeOut = new AudioDocument([Enumerable.Repeat(1f, 101).ToArray()], Rate, 32);

        Processing.FadeIn(fadeIn, 0, fadeIn.Length, curveType: 4);
        Processing.FadeOut(fadeOut, 0, fadeOut.Length, curveType: 4);

        Assert.Equal(0f, fadeIn.Channels[0][0]);
        Assert.Equal(1f, fadeIn.Channels[0][^1]);
        Assert.Equal(1f, fadeOut.Channels[0][0]);
        Assert.Equal(0f, fadeOut.Channels[0][^1]);
    }

    [Fact]
    public void TimeStretchKeepsAnAntiPhaseStereoToneAsItsTimingGuide()
    {
        float[] left = Tone(440, Rate);
        float[] right = left.Select(sample => -sample).ToArray();

        float[][] stretched = TimeStretch.Stretch([left, right], Rate, 1.5);

        double amplitude = ToneAmplitude(stretched[0], 440, Rate);
        Assert.True(amplitude > 0.3, $"anti-phase guide lost the tone ({amplitude:0.000})");
        for (int i = Rate / 4; i < stretched[0].Length * 3 / 4; i += 97)
            Assert.Equal(-stretched[0][i], stretched[1][i], 5);
    }

    [Fact]
    public void PitchDetectorUsesAWindowThatContainsAllRequiredLagSamples()
    {
        float[] shortTone = Tone(220, 6_000);

        (double frequency, double confidence) = PitchDetect.Detect(shortTone, Rate);

        Assert.InRange(frequency, 218, 222);
        Assert.True(confidence > 0.8);
    }

    [Fact]
    public void PitchAndTempoDetectionObserveCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PitchDetect.Detect(Tone(220, Rate), Rate, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() =>
            TempoDetect.Detect([Tone(220, Rate * 10)], Rate, cancelled.Token));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(140)]
    public void TempoCorrelationFindsThePulseRateWithoutShortLagBias(double expected)
    {
        float[] clicks = new float[Rate * 30];
        int period = (int)Math.Round(Rate * 60 / expected);
        for (int at = 0; at < clicks.Length; at += period)
            for (int i = 0; i < 64 && at + i < clicks.Length; i++)
                clicks[at + i] = 1 - i / 64f;

        (double bpm, double confidence) = TempoDetect.Detect([clicks], Rate);

        Assert.InRange(bpm, expected - 1, expected + 1);
        Assert.True(confidence > 0.5, $"tempo confidence was {confidence:0.000}");
    }

    [Fact]
    public void OfflineRackRenderHonoursGlobalMidSideMode()
    {
        using var master = new MasterSection();
        var trim = new TrimEffect();
        trim.SetParam("polarityL", 1);
        master.ReplaceChain([trim]);
        master.MidSideMode = true;
        float[][] input = [Enumerable.Repeat(1f, 64).ToArray(), new float[64]];

        float[][] output = master.ProcessOffline(input, Rate);

        Assert.All(output[0], sample => Assert.Equal(0f, sample, 6));
        Assert.All(output[1], sample => Assert.Equal(-1f, sample, 6));
    }

    [Fact]
    public void OfflineMonoExpansionStillHonoursGlobalMidSideMode()
    {
        using var master = new MasterSection();
        var enhancer = new MonoToStereoEffect();
        enhancer.SetParam("amount", 0);
        var trim = new TrimEffect();
        trim.SetParam("polarityL", 1);
        master.ReplaceChain([enhancer, trim]);
        master.MidSideMode = true;

        float[][] output = master.ProcessOffline(
            [Enumerable.Repeat(1f, 64).ToArray()], Rate);

        Assert.Equal(2, output.Length);
        Assert.All(output[0], sample => Assert.Equal(-1f, sample, 6));
        Assert.All(output[1], sample => Assert.Equal(-1f, sample, 6));
    }

    [Fact]
    public void DeEsserDoesNotShiftAnOfflineRenderItDoesNotModify()
    {
        using var master = new MasterSection();
        master.ReplaceChain([new DeEsserEffect()]);
        float[] tone = Tone(200, Rate);

        float[][] output = master.ProcessOffline([tone], Rate);

        Assert.Equal(1_024, master.ChainSnapshot[0].LatencySamples);
        for (int i = 0; i < tone.Length; i += 101)
            Assert.Equal(tone[i], output[0][i], 4);
    }

    [Fact]
    public void DeEsserOutputDoesNotDependOnHostBlockSize()
    {
        float[] source = Noise(Rate * 2, 71)
            .Select(sample => sample * 10).ToArray();
        var wholeEffect = new DeEsserEffect();
        var chunkedEffect = new DeEsserEffect();
        wholeEffect.SetParam("threshold", -60);
        chunkedEffect.SetParam("threshold", -60);
        wholeEffect.Configure(Rate, 1);
        chunkedEffect.Configure(Rate, 1);

        float[] whole = ProcessWithBlocks(wholeEffect, source, [int.MaxValue]);
        float[] chunked = ProcessWithBlocks(chunkedEffect, source, [73, 511, 128, 997, 31]);

        Assert.Equal(whole, chunked);
        double maximumChange = 0;
        for (int index = 0; index < source.Length; index++)
            maximumChange = Math.Max(maximumChange,
                Math.Abs(whole[index + wholeEffect.LatencySamples] - source[index]));
        Assert.True(maximumChange > 1e-4, "test signal did not engage the de-esser");
    }

    [Fact]
    public void ChangingSaturationOversamplingMatchesConfiguringAtThatFactor()
    {
        var setBefore = new SaturationEffect();
        setBefore.SetParam("oversample", 3);
        setBefore.Configure(Rate, 1);

        var setAfter = new SaturationEffect();
        setAfter.Configure(Rate, 1);
        setAfter.SetParam("oversample", 3);

        float[] input = Tone(7_000, Rate / 2);
        float[] before = (float[])input.Clone();
        float[] after = (float[])input.Clone();
        setBefore.Process(before, 0, before.Length);
        setAfter.Process(after, 0, after.Length);

        Assert.Equal(before, after);
    }

    [Fact]
    public void ReturningToAnOversamplingFactorDoesNotResumeOldFilterHistory()
    {
        var effect = new SaturationEffect();
        effect.Configure(Rate, 1);
        effect.SetParam("oversample", 1); // 2x
        var impulse = new float[256];
        impulse[^1] = 1;
        effect.Process(impulse, 0, impulse.Length);

        effect.SetParam("oversample", 3); // leave the 2x bank idle
        var settling = new float[Rate / 2];
        effect.Process(settling, 0, settling.Length);
        effect.SetParam("oversample", 1);

        var resumed = new float[512];
        effect.Process(resumed, 0, resumed.Length);
        Assert.All(resumed, sample => Assert.InRange(Math.Abs(sample), 0, 1e-7));
    }

    [Fact]
    public void LevelNormalizerGainCanActuallyMoveTowardItsTarget()
    {
        var effect = new LevelNormalizerEffect();
        effect.SetParam("target", -18);
        effect.SetParam("maxBoost", 6);
        effect.SetParam("maxCut", 0);
        effect.SetParam("response", 250);
        effect.SetParam("maxGainChange", 12);
        effect.SetParam("truePeakLimit", 0);
        effect.Configure(Rate, 1);
        float[] quiet = Tone(1_000, Rate * 4, amplitude: 0.03);
        double input = Rms(quiet, 0, Rate);

        effect.Process(quiet, 0, quiet.Length);

        double settled = Rms(quiet, quiet.Length - Rate, quiet.Length);
        Assert.True(settled > input * 1.5,
            $"normalizer remained nearly stationary: {input:0.00000} -> {settled:0.00000}");
    }

    [Fact]
    public void LevelNormalizerEnforcesItsTruePeakCeilingOnAnInitialTransient()
    {
        var effect = new LevelNormalizerEffect();
        effect.SetParam("truePeakLimit", -6);
        effect.Configure(Rate, 1);
        var signal = new float[Rate / 10 + effect.LatencySamples];
        signal[10] = 0.99f;

        effect.Process(signal, 0, signal.Length);

        double ceiling = Math.Pow(10, -6 / 20.0);
        Assert.True(signal.Max(sample => Math.Abs(sample)) <= ceiling + 1e-4,
            $"output exceeded the selected ceiling: {signal.Max(sample => Math.Abs(sample)):0.000000}");
    }

    [Fact]
    public void LearnedRackNoiseProfileSurvivesTheOfflineClone()
    {
        using var master = new MasterSection();
        var effect = Assert.IsType<NoiseReductionEffect>(master.AddEffect("denoise"));
        effect.SetParam("reduction", 0);
        effect.SetParam("hiss", 0);
        effect.SetParam("learn", 1);
        float[] learned = Noise(Rate, 17);
        effect.Process(learned, 0, learned.Length);
        effect.SetParam("learn", 0);
        effect.SetParam("spectral", 1);
        effect.Process(new float[2], 0, 2); // publish the completed profile
        float[] source = Noise(Rate * 2, 19);
        double dry = Rms(source, Rate, source.Length);

        float[][] rendered = master.ProcessOffline([(float[])source.Clone()], Rate);

        double wet = Rms(rendered[0], Rate, rendered[0].Length);
        Assert.True(wet < dry * 0.6, $"offline clone lost its profile: {dry:0.00000} -> {wet:0.00000}");
    }

    [Fact]
    public void RackNoiseReductionUsesOneMaskForTheStereoPair()
    {
        var effect = new NoiseReductionEffect();
        effect.SetParam("reduction", 0);
        effect.SetParam("hiss", 0);
        effect.SetParam("learn", 1);
        effect.Configure(Rate, 2);
        float[] learned = Noise(Rate, 23);
        effect.Process(Interleave(learned, learned), 0, learned.Length * 2);
        effect.SetParam("learn", 0);
        effect.SetParam("spectral", 1);

        float[] left = Noise(Rate * 2, 29);
        float[] right = left.Select(sample => sample * 0.5f).ToArray();
        float[] stereo = Interleave(left, right);
        effect.Process(stereo, 0, stereo.Length);

        for (int frame = Rate; frame < left.Length; frame += 113)
            Assert.Equal(stereo[frame * 2] * 0.5f, stereo[frame * 2 + 1], 5);
    }

    [Fact]
    public void DynamicsProcessorsKeepStereoGainLinked()
    {
        const int frames = Rate * 2;
        float[] left = Tone(300, frames, amplitude: 0.6);
        float[] right = left.Select(sample => sample * 0.2f).ToArray();

        IAudioEffect[] effects =
        [
            Configured(new DynamicEqEffect(), ("freq", 300), ("q", 1.5),
                ("range", -12), ("threshold", -30), ("ratio", 1)),
            Configured(new MultibandCompressorEffect(), ("threshold", -30), ("ratio", 8)),
        ];

        foreach (IAudioEffect effect in effects)
        {
            float[] stereo = Interleave(left, right);
            effect.Process(stereo, 0, stereo.Length);
            for (int frame = Rate; frame < frames; frame += 127)
                Assert.Equal(stereo[frame * 2] * 0.2f, stereo[frame * 2 + 1], 3);
        }
    }

    [Fact]
    public void RackHumAutoDetectionListensToTheRightChannelToo()
    {
        const int seconds = 3;
        var interleaved = new float[Rate * seconds * 2];
        for (int frame = 0; frame < Rate * seconds; frame++)
            interleaved[frame * 2 + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 50 * frame / Rate));

        var effect = new HumRemovalEffect();
        effect.SetParam("frequency", 60);
        effect.SetParam("harmonics", 1);
        effect.SetParam("autoDetect", 1);
        effect.SetParam("dynamic", 0);
        effect.Configure(Rate, 2);

        const int blockSamples = 2_048 * 2;
        for (int offset = 0; offset < interleaved.Length; offset += blockSamples)
            effect.Process(interleaved, offset, Math.Min(blockSamples, interleaved.Length - offset));

        Assert.Contains("50.0 Hz", effect.Readout);
    }

    [Fact]
    public void RackAlignmentFindsAnOddSampleOffset()
    {
        const int length = 4_096;
        const int delay = 5;
        var random = new Random(9_147);
        double[] left = Enumerable.Range(0, length)
            .Select(_ => random.NextDouble() * 2 - 1).ToArray();
        var right = new double[length];
        Array.Copy(left, 0, right, delay, length - delay);

        (double alignment, double confidence) =
            ChannelBalanceEffect.EstimateAlignment(left, right, maximumLag: 20);

        Assert.InRange(alignment, -5.05, -4.95);
        Assert.True(confidence > 0.99);
    }

    [Fact]
    public void RackReverbDoesNotCancelAnAntiPhaseStereoInput()
    {
        const int frames = Rate;
        var interleaved = new float[frames * 2];
        interleaved[0] = 1;
        interleaved[1] = -1;

        var effect = new ReverbEffect();
        effect.SetParam("mix", 1);
        effect.SetParam("preDelay", 0);
        effect.Configure(Rate, 2);
        effect.Process(interleaved, 0, interleaved.Length);

        Assert.True(Rms(interleaved, 0, interleaved.Length) > 1e-5,
            "anti-phase input disappeared from the wet path");
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(10, 9)]
    public void EveryMultichannelInputCanExciteRackReverb(int channels, int impulseChannel)
    {
        var interleaved = new float[Rate * channels];
        interleaved[impulseChannel] = 1;

        var effect = new ReverbEffect();
        effect.SetParam("mix", 1);
        effect.SetParam("preDelay", 0);
        effect.Configure(Rate, channels);
        effect.Process(interleaved, 0, interleaved.Length);

        Assert.True(Rms(interleaved, 0, interleaved.Length) > 1e-5,
            $"input channel {impulseChannel} never entered the wet network");
    }

    [Fact]
    public void HumRemovalClampsHarmonicCountsBeforeApplyingTheBitMask()
    {
        float[] source = Tone(1_485, Rate, amplitude: 0.3); // 45 Hz × 33
        float[][] clamped = [(float[])source.Clone()];
        float[][] oversized = [(float[])source.Clone()];

        Restoration.RemoveHum(clamped, Rate, 45, 30, 30, 1);
        Restoration.RemoveHum(oversized, Rate, 45, 100, 30, 1);

        Assert.Equal(clamped[0], oversized[0]);
    }

    private static T Configured<T>(T effect, params (string Key, double Value)[] settings)
        where T : IAudioEffect
    {
        foreach ((string key, double value) in settings) effect.SetParam(key, value);
        effect.Configure(Rate, 2);
        return effect;
    }

    private static float[] Tone(double frequency, int length, double amplitude = 0.5)
    {
        var result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
        return result;
    }

    private static float[] Noise(int length, int seed)
    {
        var random = new Random(seed);
        var result = new float[length];
        for (int i = 0; i < length; i++) result[i] = (float)((random.NextDouble() * 2 - 1) * 0.02);
        return result;
    }

    private static float[] Interleave(float[] left, float[] right)
    {
        var result = new float[Math.Min(left.Length, right.Length) * 2];
        for (int i = 0; i < result.Length / 2; i++)
        {
            result[i * 2] = left[i];
            result[i * 2 + 1] = right[i];
        }
        return result;
    }

    private static float[] ProcessWithBlocks(
        IAudioEffect effect, float[] source, IReadOnlyList<int> blockSizes)
    {
        var data = new float[source.Length + effect.LatencySamples];
        Array.Copy(source, data, source.Length);
        int offset = 0, block = 0;
        while (offset < data.Length)
        {
            int take = Math.Min(blockSizes[block++ % blockSizes.Count], data.Length - offset);
            effect.Process(data, offset, take);
            offset += take;
        }
        return data;
    }

    private static double ToneAmplitude(float[] samples, double frequency, int sampleRate)
    {
        int from = samples.Length / 4, to = samples.Length * 3 / 4;
        double re = 0, im = 0;
        for (int i = from; i < to; i++)
        {
            double phase = 2 * Math.PI * frequency * i / sampleRate;
            re += samples[i] * Math.Cos(phase);
            im -= samples[i] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(re * re + im * im) / (to - from);
    }

    private static double Rms(float[] samples, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }
}
