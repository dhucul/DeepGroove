using WaveLab.Audio.Effects;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The convolution reverb, measured against what convolution actually is.
/// </summary>
/// <remarks>
/// Nothing here checks that the effect runs without throwing. The claim worth testing is that its
/// output equals the direct convolution of the input with the response — everything else about a
/// reverb is taste, and that one thing is arithmetic.
/// </remarks>
public sealed class ConvolutionReverbTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    private static ConvolutionReverbEffect Effect(float[][] response, int channels = 2)
    {
        var effect = new ConvolutionReverbEffect();
        effect.Configure(Rate, channels);
        effect.SetResponse(response, Rate);
        return effect;
    }

    /// <summary>Interleaves per-channel signals into the layout the rack processes.</summary>
    private static float[] Interleave(params float[][] channels)
    {
        int frames = channels[0].Length;
        var buffer = new float[frames * channels.Length];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels.Length; c++)
                buffer[f * channels.Length + c] = channels[c][f];
        return buffer;
    }

    private static float[] Channel(float[] interleaved, int channel, int channels)
    {
        var result = new float[interleaved.Length / channels];
        for (int f = 0; f < result.Length; f++) result[f] = interleaved[f * channels + channel];
        return result;
    }

    /// <summary>Straight time-domain convolution: the definition the fast path has to agree with.</summary>
    private static float[] DirectConvolve(float[] signal, float[] kernel, int length)
    {
        var result = new float[length];
        for (int i = 0; i < length; i++)
        {
            double sum = 0;
            for (int k = 0; k < kernel.Length && k <= i; k++) sum += signal[i - k] * kernel[k];
            result[i] = (float)sum;
        }
        return result;
    }

    private static float[] Noise(int length, int seed)
    {
        var random = new Random(seed);
        var signal = new float[length];
        for (int i = 0; i < length; i++) signal[i] = (float)(random.NextDouble() * 2 - 1);
        return signal;
    }

    // ── the arithmetic ───────────────────────────────────────────

    /// <summary>
    /// Fully wet, no pre-delay, no damping, no trim: the output must be the input convolved with the
    /// response — delayed by the one block the effect declares as its latency, and scaled by the
    /// normalisation it applies. Both are read off the result rather than assumed.
    /// </summary>
    [Fact]
    public void TheOutputIsTheInputConvolvedWithTheResponse()
    {
        const int length = 8_192;
        float[] kernel = Noise(600, 9);
        ConvolutionReverbEffect effect = Effect([kernel], channels: 1);
        effect.SetParam("mix", 1);
        effect.SetParam("predelay", 0);
        effect.SetParam("damp", 20_000);

        float[] input = Noise(length, 21);
        var buffer = (float[])input.Clone();
        effect.Process(buffer, 0, buffer.Length);

        int latency = effect.LatencySamples;
        Assert.True(latency > 0);

        // The response is normalised to unit power on load, so the comparison is up to one scalar.
        float[] reference = DirectConvolve(input, kernel, length);
        double dot = 0, referenceEnergy = 0;
        for (int i = latency + kernel.Length; i < length; i++)
        {
            dot += (double)buffer[i] * reference[i - latency];
            referenceEnergy += (double)reference[i - latency] * reference[i - latency];
        }
        double scale = dot / referenceEnergy;

        double signal = 0, error = 0;
        for (int i = latency + kernel.Length; i < length; i++)
        {
            double want = reference[i - latency] * scale;
            signal += want * want;
            double d = buffer[i] - want;
            error += d * d;
        }

        double snr = 10 * Math.Log10(signal / error);
        output.WriteLine($"against direct convolution: {snr:0.0} dB, latency {latency}, "
                         + $"normalisation {scale:0.0000}");
        Assert.True(snr > 100, $"the fast convolution disagrees with the direct one by {snr:0.0} dB");
    }

    /// <summary>
    /// A stereo response is two responses. The left channel must carry the left of the file and the
    /// right the right — anything else quietly collapses the width the room was recorded with.
    /// </summary>
    [Fact]
    public void AStereoResponseKeepsItsTwoSidesApart()
    {
        // Two responses that share nothing: an impulse at different delays on each side.
        var left = new float[400];
        var right = new float[400];
        left[50] = 1;
        right[300] = 1;

        ConvolutionReverbEffect effect = Effect([left, right]);
        effect.SetParam("mix", 1);
        effect.SetParam("predelay", 0);
        effect.SetParam("damp", 20_000);

        const int frames = 4_096;
        var pulse = new float[frames];
        pulse[0] = 1;
        float[] buffer = Interleave(pulse, pulse);
        effect.Process(buffer, 0, buffer.Length);

        float[] outLeft = Channel(buffer, 0, 2);
        float[] outRight = Channel(buffer, 1, 2);

        int leftPeak = Array.IndexOf(outLeft, outLeft.Max());
        int rightPeak = Array.IndexOf(outRight, outRight.Max());

        output.WriteLine($"left peaks at {leftPeak}, right at {rightPeak} "
                         + $"(latency {effect.LatencySamples})");

        Assert.Equal(50 + effect.LatencySamples, leftPeak);
        Assert.Equal(300 + effect.LatencySamples, rightPeak);
    }

    /// <summary>A mono response is used on both sides, which is a mono room.</summary>
    [Fact]
    public void AMonoResponseIsUsedOnBothChannels()
    {
        var kernel = new float[200];
        kernel[120] = 1;

        ConvolutionReverbEffect effect = Effect([kernel]);
        effect.SetParam("mix", 1);
        effect.SetParam("predelay", 0);
        effect.SetParam("damp", 20_000);

        const int frames = 2_048;
        var pulse = new float[frames];
        pulse[0] = 1;
        float[] buffer = Interleave(pulse, pulse);
        effect.Process(buffer, 0, buffer.Length);

        float[] outLeft = Channel(buffer, 0, 2);
        float[] outRight = Channel(buffer, 1, 2);
        for (int i = 0; i < frames; i++) Assert.Equal(outLeft[i], outRight[i], 6);
    }

    // ── the controls ─────────────────────────────────────────────

    [Fact]
    public void PreDelayMovesTheWetSignalAndNothingElse()
    {
        var kernel = new float[64];
        kernel[0] = 1;

        const double milliseconds = 20;
        var expected = (int)(milliseconds * 0.001 * Rate);

        ConvolutionReverbEffect effect = Effect([kernel], channels: 1);
        effect.SetParam("mix", 1);
        effect.SetParam("damp", 20_000);
        effect.SetParam("predelay", milliseconds);

        var pulse = new float[8_192];
        pulse[0] = 1;
        effect.Process(pulse, 0, pulse.Length);

        int peak = Array.IndexOf(pulse, pulse.Max());
        output.WriteLine($"pre-delay {milliseconds} ms: peak at {peak}, "
                         + $"expected {expected + effect.LatencySamples}");
        Assert.Equal(expected + effect.LatencySamples, peak);
    }

    /// <summary>Fully dry must be the input, exactly — only delayed by the declared latency.</summary>
    [Fact]
    public void FullyDryReturnsTheInput()
    {
        var kernel = new float[500];
        kernel[10] = 1;

        ConvolutionReverbEffect effect = Effect([kernel], channels: 1);
        effect.SetParam("mix", 0);

        float[] input = Noise(4_096, 5);
        var buffer = (float[])input.Clone();
        effect.Process(buffer, 0, buffer.Length);

        int latency = effect.LatencySamples;
        for (int i = latency; i < input.Length; i++)
            Assert.Equal(input[i - latency], buffer[i], 6);
    }

    /// <summary>
    /// Two responses of wildly different peak level but the same power must come out at the same
    /// level. That is what normalising to power rather than to peak buys, and it is the thing that
    /// makes the mix control mean the same across two files.
    /// </summary>
    [Fact]
    public void TwoResponsesOfTheSamePowerComeOutAtTheSameLevel()
    {
        // A sparse response with a tall peak, and a dense one with none — the same total energy.
        var sparse = new float[4_000];
        sparse[0] = 1;

        var dense = new float[4_000];
        var random = new Random(2);
        for (int i = 0; i < dense.Length; i++)
            dense[i] = (float)((random.NextDouble() * 2 - 1) / Math.Sqrt(dense.Length));

        double Level(float[] kernel)
        {
            ConvolutionReverbEffect effect = Effect([kernel], channels: 1);
            effect.SetParam("mix", 1);
            effect.SetParam("predelay", 0);
            effect.SetParam("damp", 20_000);

            var buffer = Noise(16_384, 77);
            effect.Process(buffer, 0, buffer.Length);

            double sum = 0;
            for (int i = 8_000; i < buffer.Length; i++) sum += (double)buffer[i] * buffer[i];
            return Math.Sqrt(sum / (buffer.Length - 8_000));
        }

        double sparseLevel = Level(sparse), denseLevel = Level(dense);
        double differenceDb = 20 * Math.Log10(denseLevel / sparseLevel);

        output.WriteLine($"sparse {sparseLevel:0.0000}, dense {denseLevel:0.0000}, "
                         + $"apart by {differenceDb:+0.0;-0.0} dB");
        Assert.True(Math.Abs(differenceDb) < 1.5,
            $"two responses of equal power came out {differenceDb:0.0} dB apart");
    }

    // ── with no response ─────────────────────────────────────────

    [Fact]
    public void WithNoResponseTheEffectPassesAudioThroughAndSaysSo()
    {
        var effect = new ConvolutionReverbEffect();
        effect.Configure(Rate, 2);

        Assert.False(effect.HasResponse);
        Assert.Equal(0, effect.LatencySamples);
        Assert.NotNull(effect.Readout);

        float[] input = Noise(1_024, 3);
        var buffer = (float[])input.Clone();
        effect.Process(buffer, 0, buffer.Length);

        Assert.Equal(input, buffer);
    }

    /// <summary>A response at another rate is the same room at the wrong speed, so it is resampled.</summary>
    [Fact]
    public void AResponseAtAnotherRateIsResampledToTheSession()
    {
        var effect = new ConvolutionReverbEffect();
        effect.Configure(Rate, 1);

        var kernel = new float[22_050];        // half a second at 44.1 kHz
        kernel[0] = 1;
        effect.SetResponse([kernel], 44_100);

        output.WriteLine($"0.5 s at 44.1 kHz became {effect.ResponseSeconds:0.000} s at {Rate}");
        Assert.Equal(0.5, effect.ResponseSeconds, 0.01);
    }

    // ── it survives a preset ─────────────────────────────────────

    /// <summary>
    /// The response is a file, not a number, so it travels in the preset's state field beside a
    /// plugin's. A preset that forgot which room it was is not a preset.
    /// </summary>
    [Fact]
    public void TheChosenResponseSurvivesAPreset()
    {
        var effect = new ConvolutionReverbEffect();
        effect.Configure(Rate, 2);
        effect.SetParam("mix", 0.4);

        // A path that does not exist: the point is that the choice round-trips, and a missing file
        // must not lose it — the card can then say which room is not there.
        const string path = @"Z:\rooms\Concertgebouw.wav";
        ((IEffectState)effect).RestoreStateText(path);
        Assert.Equal(path, effect.ResponsePath);

        EffectFactory.ChainPreset captured = EffectFactory.Capture("room", [effect]);
        Assert.Equal(path, captured.Effects[0].State);

        List<IAudioEffect> restored = EffectFactory.Instantiate(captured);
        var reverb = Assert.IsType<ConvolutionReverbEffect>(restored[0]);
        Assert.Equal(path, reverb.ResponsePath);
        Assert.Equal(0.4, reverb.GetParam("mix"), 6);
    }

    [Fact]
    public void ABuiltInWithNoStateSavesNone()
    {
        EffectFactory.ChainPreset captured =
            EffectFactory.Capture("plain", [EffectFactory.Create("eq")]);
        Assert.Null(captured.Effects[0].State);
    }
}
