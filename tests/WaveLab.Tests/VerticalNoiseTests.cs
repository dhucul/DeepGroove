using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The two stages a record transfer needs before anything else in the restoration chain can work:
/// the subsonic high-pass, and the side-signal scale that collapses vertical groove noise.
/// </summary>
/// <remarks>
/// Both are trivial DSP and neither is tested for its arithmetic. What is worth pinning is the pair
/// of claims they are in the chain for: that the high-pass reaches the rumble without reaching the
/// music, and that the side scale is a <em>bit-exact</em> no-op at one — because the whole point of
/// it being a slider is that a user who leaves it alone gets their file back untouched, and
/// <c>mid + side</c> reconstructs a float sample to a rounding error rather than to the sample.
/// </remarks>
public sealed class VerticalNoiseTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = Rate * 2;

    private static float[][] Stereo(Func<double, int, double> sample)
    {
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[Length];
            for (int i = 0; i < Length; i++) data[c][i] = (float)sample(i / (double)Rate, c);
        }
        return data;
    }

    private static double Rms(float[] x)
    {
        double sum = 0;
        foreach (float v in x) sum += (double)v * v;
        return 20 * Math.Log10(Math.Sqrt(sum / x.Length) + 1e-12);
    }

    // ── subsonic high-pass ───────────────────────────────────────

    /// <summary>
    /// The band it exists for goes; the band the music is in stays. Both measured on the same pass,
    /// because a filter that takes 30 dB off 12 Hz by taking 3 dB off everything is not a fix.
    /// </summary>
    /// <remarks>
    /// The figures are the fourth-order Butterworth response at a 30 Hz corner and nothing more —
    /// analytically 31.8 / 21.9 / 7.2 dB at these three tones, measured here as 30.9 / 21.7 / 7.3
    /// with the startup transient included in the RMS. They are written down so that a change to the
    /// section Qs or to the cascade shows up as a failure rather than as a slightly different sound.
    /// The two tones that matter are the ones a real transfer peaked at, 10.8 and 16.1 Hz.
    /// </remarks>
    [Theory]
    [InlineData(12, 30.0)]
    [InlineData(16, 21.0)]
    [InlineData(25, 7.0)]
    public void TheHighPassReachesTheRumble(double toneHz, double expectedLossDb)
    {
        float[][] rumble = Stereo((t, _) => 0.3 * Math.Sin(2 * Math.PI * toneHz * t));
        double before = Rms(rumble[0]);
        Restoration.RemoveSubsonic(rumble, Rate, 30);
        double after = Rms(rumble[0]);

        output.WriteLine($"{toneHz} Hz: {before:0.0} -> {after:0.0} dBFS ({before - after:0.0} dB down)");
        Assert.True(before - after > expectedLossDb,
            $"{toneHz} Hz lost only {before - after:0.0} dB, wanted more than {expectedLossDb:0.0}");
    }

    [Theory]
    [InlineData(120)]
    [InlineData(440)]
    [InlineData(4_000)]
    public void TheHighPassLeavesTheMusicAlone(double toneHz)
    {
        float[][] music = Stereo((t, _) => 0.3 * Math.Sin(2 * Math.PI * toneHz * t));
        double before = Rms(music[0]);
        Restoration.RemoveSubsonic(music, Rate, 30);
        double after = Rms(music[0]);

        output.WriteLine($"{toneHz} Hz: {before:0.00} -> {after:0.00} dBFS ({before - after:+0.00;-0.00;0.00} dB)");
        Assert.True(Math.Abs(before - after) < 0.5,
            $"{toneHz} Hz moved {before - after:0.00} dB, which is not leaving it alone");
    }

    /// <summary>Zero strength must not run the audio through the filter at all.</summary>
    [Fact]
    public void AtZeroStrengthTheHighPassIsBitExact()
    {
        float[][] signal = Stereo((t, c) => 0.3 * Math.Sin(2 * Math.PI * (8 + c) * t));
        float[] original = (float[])signal[0].Clone();
        Restoration.RemoveSubsonic(signal, Rate, 30, strength: 0);
        Assert.Equal(original, signal[0]);
    }

    /// <summary>
    /// Each channel gets its own filter pair. A shared one carries the first channel's delay line
    /// into the second, which is silent on a correlated pair and audible on an anti-phase one —
    /// exactly the material this stage is for.
    /// </summary>
    [Fact]
    public void EachChannelIsFilteredIndependently()
    {
        float[][] antiPhase = Stereo((t, c) =>
            (c == 0 ? 1 : -1) * 0.3 * Math.Sin(2 * Math.PI * 12 * t));
        Restoration.RemoveSubsonic(antiPhase, Rate, 30);

        for (int i = 0; i < Length; i++)
            Assert.Equal(-antiPhase[1][i], antiPhase[0][i], 6);
    }

    // ── side scale ───────────────────────────────────────────────

    /// <summary>
    /// The claim the whole vertical-noise stage rests on: anti-phase content is in the side signal,
    /// so scaling the side reaches it and scaling the side leaves centred content alone.
    /// </summary>
    [Fact]
    public void ScalingTheSideRemovesAntiPhaseContentAndKeepsCentredContent()
    {
        float[][] mixed = Stereo((t, c) =>
              0.30 * Math.Sin(2 * Math.PI * 440 * t)                          // centred: music
            + (c == 0 ? 1 : -1) * 0.30 * Math.Sin(2 * Math.PI * 1_500 * t));  // anti-phase: noise

        var mid = new float[Length];
        var side = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            mid[i] = (mixed[0][i] + mixed[1][i]) * 0.5f;
            side[i] = (mixed[0][i] - mixed[1][i]) * 0.5f;
        }
        double midBefore = Rms(mid), sideBefore = Rms(side);

        Restoration.ScaleSide(mixed, 0);

        for (int i = 0; i < Length; i++)
        {
            mid[i] = (mixed[0][i] + mixed[1][i]) * 0.5f;
            side[i] = (mixed[0][i] - mixed[1][i]) * 0.5f;
        }
        output.WriteLine($"mid  {midBefore:0.0} -> {Rms(mid):0.0} dBFS");
        output.WriteLine($"side {sideBefore:0.0} -> {Rms(side):0.0} dBFS");

        Assert.True(Math.Abs(Rms(mid) - midBefore) < 0.01, "the centred tone should not have moved");
        Assert.True(Rms(side) < sideBefore - 100, "the anti-phase tone should be gone");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.5, 0.5)]
    public void TheSideScalesByTheAmountAsked(double level, double expected)
    {
        float[][] sideOnly = Stereo((t, c) =>
            (c == 0 ? 1 : -1) * 0.3 * Math.Sin(2 * Math.PI * 1_000 * t));
        double before = Rms(sideOnly[0]);
        Restoration.ScaleSide(sideOnly, level);
        double after = Rms(sideOnly[0]);

        double ratio = Math.Pow(10, (after - before) / 20);
        output.WriteLine($"level {level:0.00}: {before:0.0} -> {after:0.0} dBFS, ratio {ratio:0.000}");
        Assert.Equal(expected, ratio, 3);
    }

    /// <summary>
    /// A level of one returns the same bits, not merely the same audio. This is the reason for the
    /// early return: the arithmetic alone comes back a rounding error away and a user who never
    /// touched the control would still see a modified file.
    /// </summary>
    [Fact]
    public void AtFullLevelTheSideScaleIsBitExact()
    {
        float[][] signal = Stereo((t, c) =>
            0.31 * Math.Sin(2 * Math.PI * 437 * t + c) + 0.17 * Math.Sin(2 * Math.PI * 1_237 * t));
        float[] left = (float[])signal[0].Clone();
        float[] right = (float[])signal[1].Clone();

        Restoration.ScaleSide(signal, 1.0);

        Assert.Equal(left, signal[0]);
        Assert.Equal(right, signal[1]);
    }

    /// <summary>Anything that is not a stereo pair has no side signal, so it is left alone.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AnythingThatIsNotAPairIsUntouched(int channels)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[512];
            for (int i = 0; i < 512; i++) data[c][i] = (float)Math.Sin(i * 0.01 + c);
        }
        float[][] original = data.Select(c => (float[])c.Clone()).ToArray();

        Restoration.ScaleSide(data, 0);

        for (int c = 0; c < channels; c++) Assert.Equal(original[c], data[c]);
    }
}
