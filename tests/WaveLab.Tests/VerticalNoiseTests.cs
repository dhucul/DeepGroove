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
/// A third claim came later and is about the residual rather than about the audio: what a
/// <b>Keep what was removed</b> pass captures from the high-pass is the phase it rotates the
/// midrange through, not level it took away.
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


    /// <summary>One second past the 30 Hz filter's warm-up, which needs about 12,700 samples.</summary>
    private const int AnalysisStart = Rate;

    /// <summary>
    /// One frequency's complex amplitude over the settled second. The window is a whole number of
    /// cycles for every tone used here, so there is no leakage and no window function is needed.
    /// </summary>
    private static (double Re, double Im) Bin(float[] x, double freq)
    {
        double re = 0, im = 0, w = 2 * Math.PI * freq / Rate;
        for (int i = AnalysisStart; i < x.Length; i++)
        {
            re += x[i] * Math.Cos(w * i);
            im -= x[i] * Math.Sin(w * i);
        }
        return (re, im);
    }

    /// <summary>The level of one buffer's tone against another's, in dB.</summary>
    private static double RelativeDb(float[] x, float[] reference, double freq)
    {
        var (xr, xi) = Bin(x, freq);
        var (rr, ri) = Bin(reference, freq);
        return 10 * Math.Log10((xr * xr + xi * xi) / (rr * rr + ri * ri));
    }

    /// <summary>Gain and phase of the filter that turned <paramref name="input"/> into <paramref name="result"/>.</summary>
    private static (double GainDb, double Phase) Response(float[] input, float[] result, double freq)
    {
        var (xr, xi) = Bin(input, freq);
        var (yr, yi) = Bin(result, freq);
        double d = xr * xr + xi * xi;
        double hr = (yr * xr + yi * xi) / d, hi = (yi * xr - yr * xi) / d;
        return (20 * Math.Log10(Math.Sqrt(hr * hr + hi * hi)), Math.Atan2(hi, hr));
    }

    /// <summary>What a <b>Keep what was removed</b> pass would capture from the high-pass alone.</summary>
    private static float[] Residual(double toneHz)
    {
        float[][] music = Stereo((t, _) => 0.3 * Math.Sin(2 * Math.PI * toneHz * t));
        float[] original = (float[])music[0].Clone();
        Restoration.RemoveSubsonic(music, Rate, 30);
        var residual = new float[Length];
        for (int i = 0; i < Length; i++) residual[i] = original[i] - music[0][i];
        return residual;
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

    // ── what the residual is made of ─────────────────────────────

    /// <summary>
    /// <b>Keep what was removed</b> on a subsonic-only pass produces a file with the vocals plainly
    /// audible in it, and the obvious reading — that a 30 Hz high-pass is reaching into the midrange
    /// — is wrong. Above the corner this filter takes <em>no level at all</em>; everything the
    /// residual holds up there is the phase it rotates the music through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Subtracting two signals of equal magnitude that differ by an angle θ leaves
    /// <c>2·sin(θ/2)</c>, so the residual's level follows from the filter's phase and from nothing
    /// else. Measured on a real transfer the two agree over four decades to within 0.4 dB; here,
    /// against a pure tone, they agree to three decimals. The expectations are the fourth-order
    /// Butterworth response at a 30 Hz corner and nothing more — analytically −2.230 / −8.164 /
    /// −16.102 / −22.130 / −28.194 / −34.395 dB at these six tones.
    /// </para>
    /// <para>
    /// This is worth pinning rather than commenting because the residual is the one artefact of this
    /// stage anybody listens to, it will always sound like the filter ate their music, and the
    /// tempting fixes — narrowing the filter, or smoothing what comes out — would be changes to a
    /// filter that is already correct. A failure here means the high-pass has stopped being minimum
    /// phase, or has started taking level where it should take none.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(100, -2.23)]
    [InlineData(200, -8.16)]
    [InlineData(500, -16.10)]
    [InlineData(1_000, -22.13)]
    [InlineData(2_000, -28.19)]
    [InlineData(4_000, -34.40)]
    public void TheHighPassResidualIsPhaseRatherThanLevel(double toneHz, double expectedResidualDb)
    {
        float[][] music = Stereo((t, _) => 0.3 * Math.Sin(2 * Math.PI * toneHz * t));
        float[] original = (float[])music[0].Clone();
        Restoration.RemoveSubsonic(music, Rate, 30);

        var (gainDb, phase) = Response(original, music[0], toneHz);
        double residualDb = RelativeDb(Residual(toneHz), original, toneHz);
        double fromPhaseDb = 20 * Math.Log10(Math.Abs(2 * Math.Sin(phase / 2)));

        output.WriteLine($"{toneHz:0} Hz: level lost {gainDb:0.00000} dB, phase {phase * 180 / Math.PI:0.000}°, " +
                         $"residual {residualDb:0.00} dB, phase alone predicts {fromPhaseDb:0.00} dB");

        Assert.True(Math.Abs(gainDb) < 0.001,
            $"{toneHz:0} Hz lost {gainDb:0.00000} dB of level, and the claim is that it loses none");
        Assert.True(Math.Abs(residualDb - fromPhaseDb) < 0.05,
            $"{toneHz:0} Hz residual is {residualDb:0.00} dB where its own phase accounts for {fromPhaseDb:0.00} dB");
        Assert.True(Math.Abs(residualDb - expectedResidualDb) < 0.2,
            $"{toneHz:0} Hz residual is {residualDb:0.00} dB, expected {expectedResidualDb:0.00} dB");
    }

    /// <summary>
    /// The residual cannot be quiet in the midrange however steep the filter is, and that is
    /// arithmetic rather than a calibration: the complement of an Nth-order high-pass has a
    /// numerator of order N−1, so <c>1 − H</c> falls at 6 dB an octave whatever N is. Expect the
    /// same of the residual of any other minimum-phase filter in this app.
    /// </summary>
    /// <remarks>
    /// Only to 2 kHz. Past there the digital filter carries phase the analog prototype does not and
    /// the step opens up — 6.20 dB into 4 kHz and 6.80 into 8 — so a wider range would be pinning
    /// the bilinear transform rather than the claim.
    /// </remarks>
    [Fact]
    public void TheResidualFallsAtSixDecibelsAnOctaveHoweverSteepTheFilterIs()
    {
        double previous = double.NaN;
        foreach (double toneHz in new double[] { 125, 250, 500, 1_000, 2_000 })
        {
            float[][] music = Stereo((t, _) => 0.3 * Math.Sin(2 * Math.PI * toneHz * t));
            double level = RelativeDb(Residual(toneHz), music[0], toneHz);
            if (!double.IsNaN(previous))
            {
                double step = previous - level;
                output.WriteLine($"{toneHz / 2:0} -> {toneHz:0} Hz: residual fell {step:0.000} dB");
                Assert.True(Math.Abs(step - 6.02) < 0.15,
                    $"the octave to {toneHz:0} Hz fell {step:0.000} dB, which is not 6 dB an octave");
            }
            previous = level;
        }
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

    /// <summary>
    /// Running the channels at once must give the same audio as running them one after another.
    /// </summary>
    /// <remarks>
    /// The workbench de-crackles its channels in parallel, because that stage runs at 0.19 to 0.36x
    /// realtime and dominates the chain. The claim underneath is that each channel is independent —
    /// one task owns one array and reads no other — so this asserts the only thing that could
    /// falsify it: identical output, bit for bit, however the work is scheduled.
    /// </remarks>
    [Fact]
    public void DeCracklingChannelsInParallelMatchesDoingThemInTurn()
    {
        var random = new Random(23);
        var sequential = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            sequential[c] = new float[Rate * 3];
            for (int i = 0; i < sequential[c].Length; i++)
            {
                double t = i / (double)Rate;
                double music = 0.28 * Math.Sin(2 * Math.PI * (196 + c * 7) * t)
                             + 0.15 * Math.Sin(2 * Math.PI * 587 * t);
                double grain = random.NextDouble() < 0.004 ? (random.NextDouble() - 0.5) * 0.4 : 0;
                sequential[c][i] = (float)(music + grain + (random.NextDouble() - 0.5) * 0.002);
            }
        }
        var parallel = sequential.Select(c => (float[])c.Clone()).ToArray();

        foreach (float[] channel in sequential) Decrackle.Process(channel, DecrackleOptions.Default);
        Parallel.For(0, parallel.Length,
            channel => Decrackle.Process(parallel[channel], DecrackleOptions.Default));

        for (int c = 0; c < 2; c++)
        {
            Assert.Equal(sequential[c], parallel[c]);
            output.WriteLine($"channel {c}: {sequential[c].Length} samples identical");
        }
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
