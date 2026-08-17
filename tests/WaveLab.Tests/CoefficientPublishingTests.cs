using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Coefficients cross from the parameter path to the audio thread as a published snapshot, and
/// nothing else about a filter crosses at all.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Biquad"/> holds five coefficients and two delay-line samples in one struct, and
/// the halves belong to different threads. Writing coefficients into a filter the audio callback
/// is running is five unordered writes: read half-applied, the pole pair can leave the unit circle
/// and the delay line latches NaN. So the parameter path publishes a
/// <see cref="BiquadCoefficients"/> and <c>Process</c> copies it in.
/// </para>
/// <para>
/// That leaves two things to hold on to and they pull in opposite directions. The copy must
/// actually happen — a forgotten one is silent, because the filter keeps running with whatever it
/// was tuned to last and only sounds slightly stale. And the copy must stay a *coefficient* copy —
/// replacing the whole struct would be just as correct across threads and would reset the delay
/// line on every knob tick, which is audible as a click. The first is what
/// <see cref="AParameterChangeReachesTheAudioPath"/> pins; the second is what the mid-stream
/// click tests pin. The concurrency test can only ever fail, never prove: it is a backstop against
/// someone writing coefficients straight into a live filter again.
/// </para>
/// </remarks>
public sealed class CoefficientPublishingTests
{
    private const int SampleRate = 48_000;

    /// <summary>
    /// One effect whose sound is decided by a filter, and the parameter that retunes it.
    /// </summary>
    /// <param name="Hz">A tone the chosen parameter demonstrably changes the treatment of.</param>
    private sealed record Case(string Name, string Key, double Low, double High, double Hz, int Channels);

    private static readonly Case[] Cases =
    [
        new("compressor", "scHpf", 20, 500, 100, 1),
        new("gate", "scFilter", 0, 2, 100, 1),
        new("delay", "fbFilter", 0, 1, 2000, 1),
        new("trim", "phaseRotate", 0, 180, 440, 1),
        new("saturation", "tone", 0, 1, 6000, 1),
        new("stereo-width", "splitFreq", 100, 2000, 700, 2),
        new("filter", "cutoff", 500, 8000, 2000, 1),
        new("eq", "midGain", -12, 12, 650, 1),
        new("dehum", "frequency", 50, 60, 60, 1),
    ];

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases) data.Add(c.Name);
        return data;
    }

    private static Case Find(string name) => Cases.Single(c => c.Name == name);

    /// <summary>
    /// The effect, with everything except the parameter under test set so that parameter is
    /// audible: full wet, a detector that will actually engage, no safety net muting the result.
    /// </summary>
    private static IAudioEffect Make(string name)
    {
        switch (name)
        {
            case "compressor":
                var compressor = new CompressorEffect();
                compressor.SetParam("thresh", -40);
                compressor.SetParam("ratio", 12);
                compressor.SetParam("attack", 1);
                compressor.SetParam("release", 50);
                compressor.SetParam("knee", 0);
                compressor.SetParam("lookahead", 0);
                return compressor;

            case "gate":
                var gate = new GateEffect();
                gate.SetParam("thresh", -20);
                gate.SetParam("hyst", 0);
                gate.SetParam("range", -60);
                gate.SetParam("attack", 0.1);
                gate.SetParam("release", 20);
                gate.SetParam("hold", 0);
                gate.SetParam("scFreq", 6000);
                return gate;

            case "delay":
                var delay = new DelayEffect();
                delay.SetParam("time", 10);
                delay.SetParam("feedback", 0.5);
                delay.SetParam("mix", 1);
                delay.SetParam("fbFreq", 400);
                return delay;

            case "trim":
                return new TrimEffect();

            case "saturation":
                var saturation = new SaturationEffect();
                saturation.SetParam("mix", 1);
                saturation.SetParam("drive", 12);
                return saturation;

            case "stereo-width":
                var width = new StereoWidthEffect();
                width.SetParam("width", 2);
                width.SetParam("lowWidth", 0);
                width.SetParam("safety", 0);
                width.SetParam("monoBass", 0);
                return width;

            case "filter":
                return new FilterEffect();

            case "eq":
                return new EqEffect();

            case "dehum":
                var hum = new HumRemovalEffect();
                hum.SetParam("amount", 1);
                hum.SetParam("harmonics", 1);
                hum.SetParam("q", 35);
                hum.SetParam("autoDetect", 0);
                hum.SetParam("dynamic", 0);
                return hum;

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "No such case.");
        }
    }

    /// <summary>A tone, interleaved, with the channels detuned so a stereo effect has a side signal.</summary>
    private static float[] Tone(double hz, int channels, int frames)
    {
        float[] buffer = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                buffer[f * channels + c] =
                    (float)(0.4 * Math.Sin(2 * Math.PI * hz * (1 + 0.25 * c) * f / SampleRate));
        return buffer;
    }

    /// <summary>Process the whole signal with the parameter held at one value throughout.</summary>
    private static float[] Steady(Case test, double value, int frames)
    {
        var fx = Make(test.Name);
        fx.SetParam(test.Key, value);
        fx.Configure(SampleRate, test.Channels);
        float[] buffer = Tone(test.Hz, test.Channels, frames);
        fx.Process(buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>The largest sample-to-sample jump over a window, which is what a click looks like.</summary>
    private static double MaxStep(float[] buffer, int from, int count)
    {
        double step = 0;
        for (int i = Math.Max(1, from); i < Math.Min(buffer.Length, from + count); i++)
            step = Math.Max(step, Math.Abs(buffer[i] - buffer[i - 1]));
        return step;
    }

    private static double Distance(float[] a, float[] b, int from)
    {
        double sum = 0;
        for (int i = from; i < a.Length; i++) sum += (a[i] - b[i]) * (a[i] - b[i]);
        return Math.Sqrt(sum / (a.Length - from));
    }

    /// <summary>
    /// A parameter moved between blocks must be honoured by the next one. This is the failure the
    /// snapshot introduces if the copy in <c>Process</c> is ever dropped: the coefficients are
    /// published and nothing ever reads them, so the filter keeps running at its old tuning and
    /// the effect merely sounds wrong rather than breaking.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void AParameterChangeReachesTheAudioPath(string name)
    {
        Case test = Find(name);
        const int frames = SampleRate;              // 1 s
        int half = frames / 2 * test.Channels;
        int measureFrom = frames * 3 / 4 * test.Channels; // a quarter-second to settle after the change

        float[] low = Steady(test, test.Low, frames);
        float[] high = Steady(test, test.High, frames);

        var fx = Make(test.Name);
        fx.SetParam(test.Key, test.Low);
        fx.Configure(SampleRate, test.Channels);
        float[] switched = Tone(test.Hz, test.Channels, frames);
        fx.Process(switched, 0, half);
        fx.SetParam(test.Key, test.High);
        fx.Process(switched, half, switched.Length - half);

        double toLow = Distance(switched, low, measureFrom);
        double toHigh = Distance(switched, high, measureFrom);
        double spread = Distance(low, high, measureFrom);

        // The parameter has to matter at all, or the rest of the test proves nothing.
        Assert.True(spread > 1e-3, $"'{test.Key}' changed nothing between {test.Low} and {test.High}.");
        Assert.True(toHigh < 0.25 * toLow,
            $"After moving '{test.Key}' the output still matched the old setting " +
            $"(distance {toHigh:0.00000} to the new, {toLow:0.00000} to the old).");
    }

    /// <summary>
    /// The published snapshot carries coefficients only. Swapping a whole <see cref="Biquad"/> in
    /// would be equally thread-safe and would zero the delay line, which is a click — the reason
    /// the copy is <see cref="Biquad.CopyCoefficientsFrom"/> and not an assignment.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void AParameterChangeDoesNotResetTheDelayLine(string name)
    {
        Case test = Find(name);
        // Two effects carry so much state outside the biquad that the comparison stops being about
        // the biquad. The gate's whole job is to change the gain by 60 dB once its detector stops
        // hearing the tone, and the delay holds two seconds of already-recorded echoes that drain
        // through the newly inserted loop filter — in both the step at the change is the effect
        // working. Their coefficients are covered by the other two tests.
        if (name is "gate" or "delay") return;

        const int frames = SampleRate / 4;
        int half = frames / 2 * test.Channels;
        int window = 300 * test.Channels;

        // The reference is the same signal with the parameter held at the *new* value throughout —
        // not the switched run's own past. Several of these parameters change the level as well as
        // the tuning, and comparing against the quieter side of that reads as a click.
        float[] steady = Steady(test, test.High, frames);

        var fx = Make(test.Name);
        fx.SetParam(test.Key, test.Low);
        fx.Configure(SampleRate, test.Channels);
        float[] switched = Tone(test.Hz, test.Channels, frames);
        fx.Process(switched, 0, half);
        fx.SetParam(test.Key, test.High);
        fx.Process(switched, half, switched.Length - half);

        double moved = MaxStep(switched, half, window);
        double settled = MaxStep(steady, half, window);

        // A filter that carried its delay line across the change tracks the settled reference. One
        // that restarted from zero lurches away from it.
        Assert.True(moved < Math.Max(0.02, settled * 3),
            $"Moving '{test.Key}' mid-stream stepped {moved:0.0000} where a settled filter steps {settled:0.0000}.");
    }

    /// <summary>
    /// A parameter storm from another thread while the audio thread processes. This can only ever
    /// catch a regression, never prove its absence — but writing coefficients straight into a live
    /// filter is exactly what it is here to catch, and a latched NaN is not subtle.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ParametersMovedWhileProcessingCannotDestroyTheOutput(string name)
    {
        Case test = Find(name);
        var fx = Make(test.Name);
        fx.Configure(SampleRate, test.Channels);

        const int blocks = 400;
        float[] block = Tone(test.Hz, test.Channels, 512);
        float[] source = (float[])block.Clone();

        using var done = new ManualResetEventSlim();
        var knob = new Thread(() =>
        {
            var random = new Random(20260817);
            while (!done.IsSet)
                fx.SetParam(test.Key, test.Low + random.NextDouble() * (test.High - test.Low));
        }) { IsBackground = true };
        knob.Start();

        double worst = 0;
        for (int b = 0; b < blocks; b++)
        {
            Array.Copy(source, block, block.Length);
            fx.Process(block, 0, block.Length);
            foreach (float v in block)
            {
                Assert.True(float.IsFinite(v), $"'{test.Name}' produced {v} under a parameter storm.");
                worst = Math.Max(worst, Math.Abs(v));
            }
        }

        done.Set();
        knob.Join(TimeSpan.FromSeconds(5));

        // Every effect here is bounded by construction; anything past this is a filter that went
        // unstable rather than a setting that got loud.
        Assert.True(worst < 100, $"'{test.Name}' reached {worst:0.0} under a parameter storm.");
    }

    /// <summary>
    /// <see cref="StudioEq"/> was never torn — it held a lock across both the gain setters and
    /// <c>Process</c>. But a monitor is not priority-inheriting, so that lock was on the render
    /// callback, and it is gone. These two pin what the lock was buying.
    /// </summary>
    [Fact]
    public void StudioEqGainChangesReachTheAudioPath()
    {
        var eq = new StudioEq();
        eq.Configure(SampleRate, 1);

        int frames = SampleRate / 4;
        float[] flat = ToneMono(StudioEq.MidFreq, frames);
        eq.Process(flat, 0, flat.Length);
        double before = Rms(flat, frames / 2);

        eq.MidGainDb = 12;
        float[] boosted = ToneMono(StudioEq.MidFreq, frames);
        eq.Process(boosted, 0, boosted.Length);
        double after = Rms(boosted, frames / 2);

        double db = 20 * Math.Log10(after / before);
        Assert.True(db > 6, $"A +12 dB mid boost moved the band by {db:0.0} dB.");
    }

    [Fact]
    public void StudioEqGainChangesMidStreamDoNotClick()
    {
        var eq = new StudioEq();
        eq.Configure(SampleRate, 1);

        int frames = 4096;
        float[] buffer = ToneMono(440, frames);
        eq.Process(buffer, 0, 1000);
        eq.MidGainDb = 12;
        eq.Process(buffer, 1000, frames - 1000);

        double step = 0;
        for (int i = 1001; i < 1300; i++) step = Math.Max(step, Math.Abs(buffer[i] - buffer[i - 1]));

        // Same bound as EqEffect's: a 0.4-amplitude 440 Hz sine steps ~0.023 a sample, and a
        // reset delay line jumps far past 0.2.
        Assert.True(step < 0.2, $"A mid-stream gain change clicked (step {step:0.000}).");
    }

    /// <summary>
    /// The hum remover publishes the *request* rather than finished coefficients, because its
    /// auto-detector retunes the same bank from inside <c>Process</c> and only one thread may
    /// write it. Both writers still have to reach the notches.
    /// </summary>
    [Fact]
    public void HumRemovalFollowsAManualFrequencyChange()
    {
        var fx = new HumRemovalEffect();
        fx.SetParam("amount", 1);
        fx.SetParam("harmonics", 1);
        fx.SetParam("autoDetect", 0);
        fx.SetParam("dynamic", 0);
        fx.SetParam("frequency", 50);
        fx.Configure(SampleRate, 1);

        float[] hum = ToneMono(60, SampleRate);
        fx.Process(hum, 0, hum.Length);
        double passed = Rms(hum, hum.Length / 2);

        // A notch this narrow — Q 35 at 60 Hz is under two hertz wide — takes about a second to
        // ring down, so the measurement waits it out rather than reading the settling as the depth.
        fx.SetParam("frequency", 60);
        double removed = 0;
        for (int block = 0; block < 4; block++)
        {
            float[] notched = ToneMono(60, SampleRate, block * SampleRate);
            fx.Process(notched, 0, notched.Length);
            removed = Rms(notched, 0);
        }

        double db = 20 * Math.Log10(removed / passed);
        Assert.True(db < -40, $"Retuning MAINS to the hum only took it down {db:0.0} dB.");
    }

    [Fact]
    public void HumRemovalRetunesWhenTheDetectorLocks()
    {
        var fx = new HumRemovalEffect();
        fx.SetParam("amount", 1);
        fx.SetParam("harmonics", 1);
        fx.SetParam("autoDetect", 1);
        fx.SetParam("dynamic", 0);
        fx.SetParam("frequency", 50);   // deliberately the wrong mains: the detector must correct it
        fx.Configure(SampleRate, 1);

        const int blockFrames = 4096;
        double first = 0, last = 0;
        for (int b = 0; b < 60; b++)
        {
            float[] block = ToneMono(60, blockFrames, b * blockFrames);
            fx.Process(block, 0, block.Length);
            double rms = Rms(block, 0);
            if (b == 0) first = rms;
            last = rms;
        }

        double db = 20 * Math.Log10(last / Math.Max(1e-12, first));
        Assert.True(db < -20, $"The detector locked to 60 Hz but the notch only followed by {db:0.0} dB.");
    }

    private static float[] ToneMono(double hz, int frames, int startFrame = 0)
    {
        float[] buffer = new float[frames];
        for (int i = 0; i < frames; i++)
            buffer[i] = (float)(0.4 * Math.Sin(2 * Math.PI * hz * (startFrame + i) / SampleRate));
        return buffer;
    }

    private static double Rms(float[] buffer, int from)
    {
        double sum = 0;
        for (int i = from; i < buffer.Length; i++) sum += buffer[i] * (double)buffer[i];
        return Math.Sqrt(sum / Math.Max(1, buffer.Length - from));
    }
}
