using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class TimeStretchTests
{
    private const int SampleRate = 44_100;

    private static float[][] Tone(double frequency, int samples, int channels = 2)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[samples];
            for (int i = 0; i < samples; i++)
                data[c][i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / SampleRate));
        }
        return data;
    }

    /// <summary>Amplitude at one frequency over the steady middle of the signal.</summary>
    private static double ToneAmplitude(float[] signal, double frequency)
    {
        int from = signal.Length / 4, to = signal.Length * 3 / 4;
        double real = 0, imaginary = 0;
        for (int i = from; i < to; i++)
        {
            (double sin, double cos) = Math.SinCos(2 * Math.PI * frequency * i / SampleRate);
            real += signal[i] * cos;
            imaginary += signal[i] * sin;
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / (to - from);
    }

    // ── the transform itself ─────────────────────────────────────

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.25)]
    public void StretchingChangesDurationByTheRequestedFactor(double factor)
    {
        float[][] input = Tone(440, 44_100);

        float[][] output = TimeStretch.Stretch(input, SampleRate, factor);

        Assert.Equal(2, output.Length);
        Assert.Equal((int)Math.Round(44_100 * factor), output[0].Length);
        Assert.Equal(output[0].Length, output[1].Length);
    }

    [Fact]
    public void StretchingLeavesPitchWhereItWas()
    {
        float[][] input = Tone(440, 44_100);

        float[][] output = TimeStretch.Stretch(input, SampleRate, 1.5);

        Assert.True(ToneAmplitude(output[0], 440) > 0.3, "the original tone should survive");
        Assert.True(ToneAmplitude(output[0], 660) < 0.05, "nothing should appear a fifth up");
    }

    [Fact]
    public void PitchShiftingLeavesDurationWhereItWas()
    {
        float[][] input = Tone(440, 44_100);

        float[][] output = TimeStretch.PitchShift(input, SampleRate, 12);

        Assert.InRange(output[0].Length, 44_100 - 200, 44_100 + 200);
        Assert.True(ToneAmplitude(output[0], 880) > 0.2, "an octave up should be an octave up");
    }

    [Fact]
    public void AnUnchangedPitchIsACopy()
    {
        float[][] input = Tone(440, 4_410);

        float[][] output = TimeStretch.PitchShift(input, SampleRate, 0);

        Assert.Equal(input[0], output[0]);
    }

    // ── progress and cancellation ────────────────────────────────

    /// <summary>
    /// The reporting added for the progress overlay must be inert: identical audio, whether or not
    /// anyone is listening to it.
    /// </summary>
    [Fact]
    public void ReportingProgressDoesNotChangeTheOutput()
    {
        float[][] input = Tone(440, 30_000);

        float[][] plain = TimeStretch.Stretch(input, SampleRate, 1.7);
        float[][] instrumented = TimeStretch.Stretch(input, SampleRate, 1.7,
            CancellationToken.None, new Progress<double>(_ => { }));

        Assert.Equal(plain[0], instrumented[0]);
        Assert.Equal(plain[1], instrumented[1]);
    }

    [Fact]
    public void StretchReportsProgressThatRisesToCompletion()
    {
        float[][] input = Tone(440, 200_000);
        var reported = new List<double>();

        TimeStretch.Stretch(input, SampleRate, 2.0, CancellationToken.None,
            new SynchronousProgress(reported.Add));

        Assert.NotEmpty(reported);
        Assert.Equal(0, reported[0], 6);
        Assert.Equal(1, reported[^1], 6);
        for (int i = 1; i < reported.Count; i++)
            Assert.True(reported[i] >= reported[i - 1], "progress must not go backwards");
        Assert.All(reported, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void PitchShiftSpreadsProgressAcrossBothOfItsStages()
    {
        float[][] input = Tone(440, 120_000);
        var reported = new List<double>();

        TimeStretch.PitchShift(input, SampleRate, 5, CancellationToken.None,
            new SynchronousProgress(reported.Add));

        Assert.Equal(1, reported[^1], 6);
        // The stretch owns the first three quarters, so both sides of that boundary must be visited.
        Assert.Contains(reported, value => value is > 0 and < 0.75);
        Assert.Contains(reported, value => value > 0.75);
    }

    [Fact]
    public void StretchStopsWhenCancelled()
    {
        float[][] input = Tone(440, 500_000);
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            TimeStretch.Stretch(input, SampleRate, 3.0, cancellation.Token,
                new SynchronousProgress(value => { if (value > 0.1) cancellation.Cancel(); })));
    }

    [Fact]
    public void PitchShiftStopsWhenCancelled()
    {
        float[][] input = Tone(440, 400_000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            TimeStretch.PitchShift(input, SampleRate, 7, cancellation.Token));
    }

    [Fact]
    public void ShortInputStillCompletes()
    {
        float[][] output = TimeStretch.Stretch(Tone(440, 64), SampleRate, 2.0,
            CancellationToken.None, new SynchronousProgress(_ => { }));

        Assert.Equal(128, output[0].Length);
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to a synchronization context, so a test using it would see
    /// its callbacks arrive late or not at all. This one runs them where they are raised.
    /// </summary>
    private sealed class SynchronousProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
