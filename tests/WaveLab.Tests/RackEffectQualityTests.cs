using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Behavioral regression tests for rack-effect DSP quality: parameters must do
/// what they claim, renders must stay aligned, and live sweeps must not click.
/// </summary>
public sealed class RackEffectQualityTests
{
    private const int SampleRate = 48_000;

    [Fact]
    public void ReverbPreDelayActuallyDelaysTheWetSignal()
    {
        var fx = new ReverbEffect();
        fx.SetParam("mix", 1);        // fully wet
        fx.SetParam("preDelay", 50);  // 50 ms = 2400 samples
        fx.SetParam("modDepth", 0);
        fx.Configure(SampleRate, 1);

        float[] buffer = new float[SampleRate / 5]; // 200 ms
        buffer[0] = 1; // impulse
        fx.Process(buffer, 0, buffer.Length);

        double early = MaxAbs(buffer, 0, 1800);               // first 37.5 ms
        double later = MaxAbs(buffer, 1800, buffer.Length);   // after the pre-delay
        Assert.True(early < 1e-4, $"Wet signal arrived {early} before the 50 ms pre-delay.");
        Assert.True(later > 1e-3, "Wet signal never arrived after the pre-delay.");
    }

    [Fact]
    public void DelayPingPongBouncesEchoesBetweenChannels()
    {
        var fx = new DelayEffect();
        fx.SetParam("time", 10);      // 10 ms = 480 frames
        fx.SetParam("feedback", 0.5);
        fx.SetParam("mix", 1);        // wet only
        fx.SetParam("pingPong", 1);
        fx.Configure(SampleRate, 2);

        float[] buffer = new float[4800 * 2]; // 100 ms, stereo interleaved
        buffer[0] = 1; // impulse in the LEFT channel only
        fx.Process(buffer, 0, buffer.Length);

        double firstEchoRight = Math.Abs(buffer[2 * 480 + 1]);
        double firstEchoLeft = Math.Abs(buffer[2 * 480]);
        double secondEchoLeft = Math.Abs(buffer[2 * 960]);

        Assert.True(firstEchoRight > 0.9,
            $"First echo should land in RIGHT at 10 ms, got {firstEchoRight:0.000}.");
        Assert.True(firstEchoLeft < 0.01,
            $"LEFT should be silent at 10 ms in ping-pong, got {firstEchoLeft:0.000}.");
        Assert.True(secondEchoLeft is > 0.4 and < 0.6,
            $"Second echo should land in LEFT at 20 ms near 0.5, got {secondEchoLeft:0.000}.");
    }

    [Fact]
    public void CompressorReportsItsActualLookaheadLatency()
    {
        var fx = new CompressorEffect();
        fx.SetParam("lookahead", 2);
        fx.Configure(SampleRate, 2);
        Assert.Equal(96, fx.LatencySamples); // 2 ms at 48 kHz

        fx.SetParam("lookahead", 0);
        Assert.Equal(0, fx.LatencySamples);
    }

    [Fact]
    public void EqGainChangesMidStreamDoNotClick()
    {
        var fx = new EqEffect();
        fx.Configure(SampleRate, 1);

        int n = 4096;
        float[] buffer = new float[n];
        for (int i = 0; i < n; i++)
            buffer[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));

        fx.Process(buffer, 0, 1000);
        fx.SetParam("midGain", 12); // mid-stream boost: must not reset filter state
        fx.Process(buffer, 1000, n - 1000);

        double maxStep = 0;
        for (int i = 1001; i < 1300; i++)
            maxStep = Math.Max(maxStep, Math.Abs(buffer[i] - buffer[i - 1]));

        // A 0.5-amplitude 440 Hz sine steps at most ~0.03 per sample; a state-reset
        // click would jump far beyond 0.2.
        Assert.True(maxStep < 0.2, $"Mid-stream gain change clicked (step {maxStep:0.000}).");
    }

    [Fact]
    public void SpectralNoiseReductionAttenuatesLearnedNoise()
    {
        var fx = new NoiseReductionEffect();
        fx.SetParam("learn", 1);
        fx.Configure(SampleRate, 1);

        var rng = new Random(42);
        float[] learn = new float[SampleRate / 2]; // 0.5 s of noise
        for (int i = 0; i < learn.Length; i++)
            learn[i] = (float)(rng.NextDouble() * 2 - 1) * 0.01f;
        fx.Process(learn, 0, learn.Length);

        fx.SetParam("learn", 0);
        fx.SetParam("spectral", 1);

        float[] test = new float[SampleRate]; // 1 s of fresh noise, same level
        for (int i = 0; i < test.Length; i++)
            test[i] = (float)(rng.NextDouble() * 2 - 1) * 0.01f;
        double inputRms = Rms(test, test.Length - 12000);

        fx.Process(test, 0, test.Length);
        double outputRms = Rms(test, test.Length - 12000); // steady-state region

        Assert.True(outputRms < inputRms * 0.35,
            $"Learned noise was barely reduced: {inputRms:0.00000} in, {outputRms:0.00000} out.");
        Assert.Equal(2048, fx.LatencySamples); // spectral pipeline reports its delay
    }

    [Fact]
    public void LimiterAtDefaultSettingsIsTransparentToProgramMaterial()
    {
        var fx = new LimiterEffect();
        fx.Configure(SampleRate, 1);

        int n = SampleRate;
        float[] buffer = new float[n];
        for (int i = 0; i < n; i++)
            buffer[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 997 * i / SampleRate));
        fx.Process(buffer, 0, n);

        double outputRms = Rms(buffer, n / 2);
        double expectedRms = 0.5 / Math.Sqrt(2);
        double errorDb = Math.Abs(20 * Math.Log10(outputRms / expectedRms));

        Assert.True(errorDb < 0.1,
            $"Default limiter altered program level by {errorDb:0.00} dB.");
    }

    private static double MaxAbs(float[] samples, int start, int end)
    {
        double max = 0;
        for (int i = start; i < end; i++)
            max = Math.Max(max, Math.Abs(samples[i]));
        return max;
    }

    private static double Rms(float[] samples, int start)
    {
        double sum = 0;
        for (int i = start; i < samples.Length; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (samples.Length - start));
    }
}
