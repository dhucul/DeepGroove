using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Render invariants shared by the complete built-in rack. A processor may have latency or memory,
/// but changing the host's callback size must never change its samples.
/// </summary>
public sealed class EffectRenderAuditTests
{
    private const int Rate = 48_000;

    public static TheoryData<string> BuiltIns()
    {
        var data = new TheoryData<string>();
        foreach ((string typeId, _) in EffectFactory.Available) data.Add(typeId);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryBuiltInEffectIsIndependentOfRenderBlockSize(string typeId)
    {
        IAudioEffect whole = EffectFactory.Create(typeId);
        IAudioEffect chunked = EffectFactory.Create(typeId);
        Stress(whole);
        Stress(chunked);
        whole.Configure(Rate, 2);
        chunked.Configure(Rate, 2);

        const int frames = 12_000;
        float[] source = StereoProgram(frames);
        float[] oneBlock = (float[])source.Clone();
        float[] manyBlocks = (float[])source.Clone();

        whole.Process(oneBlock, 0, oneBlock.Length);
        int[] sizes = [1, 7, 64, 511, 3, 1_027, 251];
        int position = 0, sizeIndex = 0;
        while (position < frames)
        {
            int blockFrames = Math.Min(sizes[sizeIndex++ % sizes.Length], frames - position);
            chunked.Process(manyBlocks, position * 2, blockFrames * 2);
            position += blockFrames;
        }

        Assert.Equal(oneBlock, manyBlocks);
        Assert.All(oneBlock, sample => Assert.True(float.IsFinite(sample),
            $"{typeId} emitted a non-finite sample."));
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryBuiltInOfflineRenderMatchesItsStreamingProcessor(string typeId)
    {
        IAudioEffect direct = EffectFactory.Create(typeId);
        IAudioEffect inRack = EffectFactory.Create(typeId);
        Stress(direct);
        Stress(inRack);
        direct.Configure(Rate, 2);

        const int frames = 12_000;
        float[] source = StereoProgram(frames);
        int latency = direct.LatencySamples;
        var streamed = new float[(frames + latency) * 2];
        source.CopyTo(streamed, 0);
        direct.Process(streamed, 0, streamed.Length);

        var deinterleaved = new[] { new float[frames], new float[frames] };
        for (int frame = 0; frame < frames; frame++)
        {
            deinterleaved[0][frame] = source[frame * 2];
            deinterleaved[1][frame] = source[frame * 2 + 1];
        }
        using var master = new MasterSection();
        master.ReplaceChain([inRack]);
        float[][] rendered = master.ProcessOffline(deinterleaved, Rate);

        for (int frame = 0; frame < frames; frame++)
        {
            Assert.Equal(streamed[(frame + latency) * 2], rendered[0][frame]);
            Assert.Equal(streamed[(frame + latency) * 2 + 1], rendered[1][frame]);
        }
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryBuiltInPowerSwitchBypassesTheLivePlaybackPath(string typeId)
    {
        const int frames = 12_000;
        float[] source = StereoProgram(frames);
        // The second half drops below the restoration and gate thresholds. A continuously loud
        // test programme correctly leaves those processors open and would mistake transparency
        // for a disconnected preview path.
        int quietStart = typeId is "denoise" or "gate" ? 0 : frames / 2;
        for (int frame = quietStart; frame < frames; frame++)
        {
            source[frame * 2] *= 0.001f;
            source[frame * 2 + 1] *= 0.001f;
        }
        IAudioEffect effect = EffectFactory.Create(typeId);
        Stress(effect);

        using var live = new MasterSection();
        live.ReplaceChain([effect]);
        Assert.True(live.SetEffectEnabled(effect, false));
        float[] bypassed = ReadLive(live, source, channels: 2);

        using var empty = new MasterSection();
        empty.ReplaceChain([]);
        float[] reference = ReadLive(empty, source, channels: 2);
        Assert.Equal(reference, bypassed);

        Assert.True(live.SetEffectEnabled(effect, true));
        float[] enabled = ReadLive(live, source, channels: 2);
        if (typeId != "convolution")
        {
            Assert.True(enabled.Where((sample, index) =>
                    Math.Abs(sample - reference[index]) > 1e-7f).Any(),
                $"{typeId} was enabled but did not affect live playback.");
        }
    }

    [Fact]
    public void DelayFeedbackFilterLeavesTheFirstEchoUncoloured()
    {
        var effect = new DelayEffect();
        effect.SetParam("time", 10);
        effect.SetParam("feedback", 0.7);
        effect.SetParam("mix", 1);
        effect.SetParam("fbFilter", 1);
        effect.SetParam("fbFreq", 200);
        effect.Configure(Rate, 1);

        var impulse = new float[Rate / 10];
        impulse[0] = 1;
        effect.Process(impulse, 0, impulse.Length);

        Assert.InRange(impulse[Rate / 100], 0.999f, 1.001f);
    }

    [Fact]
    public void DynamicEqDetectorFollowsAFrequencyChangeAfterConfigure()
    {
        var swept = new DynamicEqEffect();
        swept.SetParam("freq", 300);
        ConfigureDynamicCut(swept);
        swept.Configure(Rate, 1);
        swept.SetParam("freq", 3_000);
        swept.ResetState();

        var configuredThere = new DynamicEqEffect();
        configuredThere.SetParam("freq", 3_000);
        ConfigureDynamicCut(configuredThere);
        configuredThere.Configure(Rate, 1);

        float[] source = Enumerable.Range(0, Rate)
            .Select(frame => (float)(0.8 * Math.Sin(2 * Math.PI * 3_000 * frame / Rate)))
            .ToArray();
        float[] afterSweep = (float[])source.Clone();
        float[] configuredOutput = (float[])source.Clone();
        swept.Process(afterSweep, 0, afterSweep.Length);
        configuredThere.Process(configuredOutput, 0, configuredOutput.Length);

        Assert.Equal(configuredOutput, afterSweep);
    }

    [Fact]
    public void OversampledSaturationAlignsItsDryPathForOfflineRender()
    {
        using var master = new MasterSection();
        var saturation = new SaturationEffect();
        saturation.SetParam("mix", 0);
        saturation.SetParam("oversample", 3);
        master.ReplaceChain([saturation]);
        float[] source = Enumerable.Range(0, 4_096)
            .Select(frame => (float)(0.4 * Math.Sin(2 * Math.PI * 997 * frame / Rate)))
            .ToArray();

        float[][] rendered = master.ProcessOffline([(float[])source.Clone()], Rate);

        Assert.Equal(source, rendered[0]);
    }

    [Fact]
    public void CopyRenderPreservesTheStereoDelayTail()
    {
        using var master = new MasterSection();
        var delay = new DelayEffect();
        delay.SetParam("time", 10);
        delay.SetParam("feedback", 0);
        delay.SetParam("mix", 1);
        master.ReplaceChain([delay]);
        var source = new float[100];
        source[^1] = 1;

        float[][] inPlaceLength = master.ProcessOffline([(float[])source.Clone()], Rate);
        float[][] copyLength = master.ProcessOffline(
            [(float[])source.Clone()], Rate, includeTail: true);

        Assert.Equal(source.Length, inPlaceLength[0].Length);
        Assert.Equal(source.Length + Rate / 100, copyLength[0].Length);
        Assert.Equal(1f, copyLength[0][source.Length - 1 + Rate / 100], 6);
    }

    [Fact]
    public void ReenabledHumPartialDoesNotResumeAStaleFilterTail()
    {
        var effect = new HumRemovalEffect();
        var reference = new HumRemovalEffect();
        foreach (HumRemovalEffect target in new[] { effect, reference })
        {
            target.SetParam("frequency", 60);
            target.SetParam("harmonics", 2);
            target.SetParam("harmonicMask", 3);
            target.SetParam("amount", 1);
            target.Configure(Rate, 1);
        }

        var excite = Enumerable.Range(0, Rate)
            .Select(frame => (float)(0.8 * Math.Sin(2 * Math.PI * 120 * frame / Rate)))
            .ToArray();
        effect.Process(excite, 0, excite.Length);
        var referenceExcite = Enumerable.Range(0, Rate)
            .Select(frame => (float)(0.8 * Math.Sin(2 * Math.PI * 120 * frame / Rate)))
            .ToArray();
        reference.Process(referenceExcite, 0, referenceExcite.Length);
        effect.SetParam("harmonicMask", 1);
        reference.SetParam("harmonicMask", 1);
        var settling = new float[Rate];
        effect.Process(settling, 0, settling.Length);
        reference.Process(new float[Rate], 0, Rate);
        effect.SetParam("harmonicMask", 3);
        var resumed = new float[256];
        var firstStageOnly = new float[256];
        effect.Process(resumed, 0, resumed.Length);
        reference.Process(firstStageOnly, 0, firstStageOnly.Length);

        Biquad cleanSecondPartial = Biquad.Notch(Rate, 120, 35);
        float[] expected = firstStageOnly
            .Select(sample => cleanSecondPartial.Process(sample))
            .ToArray();

        Assert.Equal(expected, resumed);
    }

    private static void ConfigureDynamicCut(IAudioEffect effect)
    {
        effect.SetParam("q", 4);
        effect.SetParam("range", -18);
        effect.SetParam("threshold", -12);
        effect.SetParam("ratio", 1);
        effect.SetParam("attack", 1);
        effect.SetParam("release", 200);
    }

    private static float[] StereoProgram(int frames)
    {
        var samples = new float[frames * 2];
        var random = new Random(0x51A7);
        for (int frame = 0; frame < frames; frame++)
        {
            double left = 0.22 * Math.Sin(2 * Math.PI * 997 * frame / Rate)
                        + 0.08 * Math.Sin(2 * Math.PI * 4_113 * frame / Rate)
                        + (random.NextDouble() - 0.5) * 0.01;
            double right = 0.19 * Math.Sin(2 * Math.PI * 997 * frame / Rate + 0.3)
                         + 0.07 * Math.Sin(2 * Math.PI * 6_101 * frame / Rate)
                         + (random.NextDouble() - 0.5) * 0.01;
            samples[frame * 2] = (float)left;
            samples[frame * 2 + 1] = (float)right;
        }
        samples[0] += 0.7f;
        samples[1] -= 0.5f;
        return samples;
    }

    private static float[] ReadLive(MasterSection master, float[] source, int channels)
    {
        master.SetSource(new BufferSampleProvider(source, Rate, channels));
        var output = new float[source.Length];
        Assert.Equal(output.Length, master.Read(output));
        return output;
    }

    private sealed class BufferSampleProvider(float[] samples, int sampleRate, int channels)
        : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private static void Stress(IAudioEffect effect)
    {
        static void Set(IAudioEffect target, params (string Key, double Value)[] values)
        {
            foreach ((string key, double value) in values) target.SetParam(key, value);
        }

        switch (effect.TypeId)
        {
            case "eq": Set(effect, ("lowGain", 3), ("midGain", -4), ("highGain", 2)); break;
            case "compressor": Set(effect, ("thresh", -30), ("ratio", 6), ("mix", 0.7)); break;
            case "multiband": Set(effect, ("threshold", -30), ("ratio", 5), ("lowgain", 2)); break;
            case "dyneq": Set(effect, ("freq", 1_000), ("range", -10), ("threshold", -30)); break;
            case "linphase-eq": Set(effect, ("lowgain", 4), ("highmidgain", -3)); break;
            case "deemphasis": Set(effect, ("direction", 1), ("standard", 1)); break;
            case "deesser": Set(effect, ("threshold", -50), ("range", 16)); break;
            case "transient": Set(effect, ("attack", 8), ("sustain", -5)); break;
            case "normalizer": Set(effect, ("target", -14), ("response", 250)); break;
            case "trim": Set(effect, ("gain", 3), ("phaseRotate", 80)); break;
            case "mono-stereo": Set(effect, ("amount", 0.7), ("algorithm", 1)); break;
            case "stereo-width": Set(effect, ("width", 1.5), ("lowWidth", 0.7)); break;
            case "channel-balance": Set(effect, ("balance", 4), ("align", 0.7)); break;
            case "denoise": Set(effect, ("threshold", -45), ("reduction", 16), ("hiss", 12)); break;
            case "dehum": Set(effect, ("frequency", 60), ("harmonics", 8), ("dynamic", 1)); break;
            case "gate": Set(effect, ("thresh", -45), ("range", -30), ("scFilter", 2)); break;
            case "reverb": Set(effect, ("mix", 0.6), ("preDelay", 37), ("modDepth", 3)); break;
            case "delay": Set(effect, ("time", 10), ("feedback", 0.6), ("mix", 0.7), ("pingPong", 1)); break;
            case "chorus": Set(effect, ("mix", 0.6), ("voices", 4), ("feedback", 0.4)); break;
            case "saturation": Set(effect, ("drive", 18), ("mix", 0.5), ("oversample", 3)); break;
            case "filter": Set(effect, ("mode", 1), ("cutoff", 180), ("slope", 1)); break;
            case "limiter": Set(effect, ("thresh", -6), ("ceiling", -2)); break;
            // Convolution is a deliberate bypass until an impulse response is loaded.
        }
    }
}
