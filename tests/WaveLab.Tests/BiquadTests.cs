using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The second-order section every filter in the app is built from.
/// </summary>
/// <remarks>
/// Two things are worth testing about a biquad and neither is its coefficients. The first is that
/// what it <em>does</em> to a signal is what its own magnitude function <em>says</em> it does — the
/// two are written separately, one drives the audio and the other draws the curve, and nothing else
/// notices when they disagree. The second is that it is stable, because an unstable section does not
/// sound wrong, it destroys the output.
/// </remarks>
public sealed class BiquadTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    /// <summary>
    /// The level a steady tone comes out at, measured by running it through and correlating with
    /// itself. Started well past the transient so the section has settled.
    /// </summary>
    private static double MeasuredDb(Biquad filter, double hz)
    {
        const int settle = 8_000, measure = 48_000;
        double re = 0, im = 0;

        for (int i = 0; i < settle + measure; i++)
        {
            double phase = 2 * Math.PI * hz * i / Rate;
            float y = filter.Process((float)Math.Sin(phase));
            if (i < settle) continue;
            re += y * Math.Cos(phase);
            im -= y * Math.Sin(phase);
        }

        double amplitude = 2 * Math.Sqrt(re * re + im * im) / measure;
        return 20 * Math.Log10(Math.Max(1e-12, amplitude));
    }

    private static IEnumerable<double> SweepHz()
    {
        for (double f = 20; f < Rate / 2.0 - 100; f *= 1.5) yield return f;
    }

    // ── the curve and the audio agree ────────────────────────────

    public static TheoryData<string> EveryKind =>
    [
        "lowpass", "highpass", "bandpass", "bandstop", "notch", "allpass",
        "lowshelf", "highshelf", "peaking", "lowpass12", "highpass12", "firstorderhighpass",
    ];

    private static Biquad Make(string kind) => kind switch
    {
        "lowpass" => Biquad.LowPass(Rate, 1_000, 0.707),
        "highpass" => Biquad.HighPass(Rate, 1_000, 0.707),
        "bandpass" => Biquad.BandPass(Rate, 1_000, 1.0),
        "bandstop" => Biquad.BandStop(Rate, 1_000, 1.0),
        "notch" => Biquad.Notch(Rate, 1_000, 8.0),
        "allpass" => Biquad.AllPass(Rate, 1_000, 0.707),
        "lowshelf" => Biquad.LowShelf(Rate, 200, 6),
        "highshelf" => Biquad.HighShelf(Rate, 5_000, -6),
        "peaking" => Biquad.Peaking(Rate, 1_000, 2.0, 9),
        "lowpass12" => Biquad.LowPass12Db(Rate, 2_000),
        "highpass12" => Biquad.HighPass12Db(Rate, 80),
        _ => Biquad.FirstOrderHighPass(Rate, 30),
    };

    /// <summary>
    /// What the section does to a tone must match what <c>MagnitudeDb</c> claims. The rack draws its
    /// curves from one and processes audio with the other, so a disagreement is a UI that lies.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryKind))]
    public void TheDrawnCurveMatchesTheAudio(string kind)
    {
        Biquad reference = Make(kind);
        double worst = 0, worstAt = 0;

        foreach (double hz in SweepHz())
        {
            Biquad filter = Make(kind);
            double measured = MeasuredDb(filter, hz);
            double claimed = reference.MagnitudeDb(hz, Rate);
            double error = Math.Abs(measured - claimed);
            if (error > worst) { worst = error; worstAt = hz; }
        }

        output.WriteLine($"{kind,-20} worst disagreement {worst:0.000} dB at {worstAt:0} Hz");
        Assert.True(worst < 0.05, $"{kind} measures {worst:0.00} dB away from its own curve at {worstAt:0} Hz");
    }

    // ── stability ────────────────────────────────────────────────

    /// <summary>
    /// Every section, at corners from the very bottom of the band to the very top, has to settle
    /// after an impulse rather than grow. An unstable biquad does not sound wrong; it takes the
    /// output with it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryKind))]
    public void EverySectionIsStableAcrossTheWholeBand(string kind)
    {
        foreach (double corner in new[] { 10.0, 40, 500, 5_000, 15_000, 20_000, 23_000 })
        {
            Biquad filter = kind switch
            {
                "lowpass" => Biquad.LowPass(Rate, corner, 0.707),
                "highpass" => Biquad.HighPass(Rate, corner, 0.707),
                "bandpass" => Biquad.BandPass(Rate, corner, 1.0),
                "bandstop" => Biquad.BandStop(Rate, corner, 1.0),
                "notch" => Biquad.Notch(Rate, corner, 8.0),
                "allpass" => Biquad.AllPass(Rate, corner, 0.707),
                "lowshelf" => Biquad.LowShelf(Rate, corner, 6),
                "highshelf" => Biquad.HighShelf(Rate, corner, -6),
                "peaking" => Biquad.Peaking(Rate, corner, 2.0, 9),
                "lowpass12" => Biquad.LowPass12Db(Rate, corner),
                "highpass12" => Biquad.HighPass12Db(Rate, corner),
                _ => Biquad.FirstOrderHighPass(Rate, corner),
            };

            float peak = 0, tail = 0;
            for (int i = 0; i < 200_000; i++)
            {
                float y = filter.Process(i == 0 ? 1f : 0f);
                Assert.True(float.IsFinite(y), $"{kind} at {corner:0} Hz produced {y}");
                peak = Math.Max(peak, Math.Abs(y));
                if (i > 100_000) tail = Math.Max(tail, Math.Abs(y));
            }

            Assert.True(tail < peak * 1e-3,
                $"{kind} at {corner:0} Hz still rings at {tail:0.000000} after 100k samples (peak {peak:0.000})");
        }
    }

    // ── the specific claims each kind makes ──────────────────────

    /// <summary>An all-pass passes everything, which is the only thing it promises.</summary>
    [Fact]
    public void TheAllPassIsFlat()
    {
        Biquad reference = Biquad.AllPass(Rate, 1_000, 0.707);
        foreach (double hz in SweepHz())
        {
            Biquad filter = Biquad.AllPass(Rate, 1_000, 0.707);
            Assert.Equal(0, MeasuredDb(filter, hz), 0.05);
            Assert.Equal(0, reference.MagnitudeDb(hz, Rate), 0.001);
        }
    }

    /// <summary>Identity is a wire: what goes in comes out, sample for sample.</summary>
    [Fact]
    public void IdentityIsAWire()
    {
        Biquad filter = Biquad.Identity();
        var random = new Random(3);
        for (int i = 0; i < 4_096; i++)
        {
            var x = (float)(random.NextDouble() * 2 - 1);
            Assert.Equal(x, filter.Process(x), 6);
        }
    }

    /// <summary>
    /// A second-order section is 12 dB an octave, and the two named 12 dB helpers have to actually be
    /// that — the Butterworth Q of 0.7071, not something near it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTwelveDecibelHelpersAreButterworth(bool lowPass)
    {
        // Corners chosen so that the octave measured in the stopband sits well clear of Nyquist. The
        // bilinear transform warps the frequency axis, and it warps it most where the analogue
        // prototype is least interesting: measure a lowpass's asymptote at 4 to 8 kHz on a 48 kHz
        // section and it reads 13.3 dB an octave, which is the mapping talking, not the filter.
        double corner = lowPass ? 200 : 4_000;
        Biquad filter = lowPass ? Biquad.LowPass12Db(Rate, corner) : Biquad.HighPass12Db(Rate, corner);

        // −3 dB at the corner is what makes it Butterworth rather than merely second order.
        double atCorner = filter.MagnitudeDb(corner, Rate);

        // An octave two octaves into the stopband, taken in the direction the response is falling.
        double shallow = lowPass ? corner * 4 : corner / 4;
        double deep = lowPass ? corner * 8 : corner / 8;
        double slope = filter.MagnitudeDb(deep, Rate) - filter.MagnitudeDb(shallow, Rate);

        output.WriteLine($"{(lowPass ? "low" : "high")}pass 12 dB: {atCorner:0.00} dB at the corner, "
                         + $"{slope:0.00} dB across the octave from {shallow:0} to {deep:0} Hz");

        Assert.Equal(-3.0103, atCorner, 0.02);
        Assert.Equal(-12, slope, 0.25);
    }

    /// <summary>A peaking section has to hit its stated gain at its stated frequency, and nowhere else.</summary>
    [Theory]
    [InlineData(6.0)]
    [InlineData(-6.0)]
    [InlineData(15.0)]
    public void APeakingSectionHitsItsGainAtItsFrequency(double gainDb)
    {
        const double centre = 1_000;
        Biquad filter = Biquad.Peaking(Rate, centre, 4.0, gainDb);

        Assert.Equal(gainDb, filter.MagnitudeDb(centre, Rate), 0.01);
        Assert.Equal(0, filter.MagnitudeDb(20, Rate), 0.15);
        Assert.Equal(0, filter.MagnitudeDb(20_000, Rate), 0.15);
    }

    /// <summary>Shelves reach their gain on their own side and leave the other alone.</summary>
    [Fact]
    public void ShelvesLiftOneEndAndLeaveTheOther()
    {
        Biquad low = Biquad.LowShelf(Rate, 200, 8);
        Assert.Equal(8, low.MagnitudeDb(20, Rate), 0.3);
        Assert.Equal(0, low.MagnitudeDb(10_000, Rate), 0.05);

        Biquad high = Biquad.HighShelf(Rate, 4_000, -8);
        Assert.Equal(-8, high.MagnitudeDb(20_000, Rate), 0.3);
        Assert.Equal(0, high.MagnitudeDb(50, Rate), 0.05);
    }

    /// <summary>A notch is a hole, and a deep one at its centre.</summary>
    [Fact]
    public void ANotchIsDeepAtItsCentreAndFlatAwayFromIt()
    {
        Biquad notch = Biquad.Notch(Rate, 1_000, 20);
        Assert.True(notch.MagnitudeDb(1_000, Rate) < -60);
        Assert.Equal(0, notch.MagnitudeDb(200, Rate), 0.2);
        Assert.Equal(0, notch.MagnitudeDb(5_000, Rate), 0.2);
    }

    // ── the rack's idiom ─────────────────────────────────────────

    /// <summary>
    /// Copying coefficients must move the filter's shape and not its running state. That is the whole
    /// point of the call: the UI thread builds a new section and the audio thread adopts its
    /// coefficients mid-stream, and if the delay line came with them the signal would jump.
    /// </summary>
    [Fact]
    public void CopyingCoefficientsLeavesTheDelayLineAlone()
    {
        Biquad running = Biquad.LowPass(Rate, 1_000, 0.707);
        for (int i = 0; i < 1_000; i++) running.Process((float)Math.Sin(i * 0.1));

        Biquad fresh = Biquad.HighPass(Rate, 4_000, 0.707);
        running.CopyCoefficientsFrom(fresh);

        // The shape is the new one...
        Assert.Equal(fresh.MagnitudeDb(1_000, Rate), running.MagnitudeDb(1_000, Rate), 9);

        // ...and the output does not jump, because the history is still there.
        float first = running.Process(0f);
        Assert.True(Math.Abs(first) < 1, $"the section jumped to {first} when its coefficients changed");
    }

    [Fact]
    public void ResetClearsTheHistoryAndNothingElse()
    {
        Biquad filter = Biquad.LowPass(Rate, 500, 0.707);
        for (int i = 0; i < 500; i++) filter.Process(1f);

        double before = filter.MagnitudeDb(500, Rate);
        filter.Reset();

        Assert.Equal(before, filter.MagnitudeDb(500, Rate), 9);
        Assert.Equal(0, filter.Process(0f), 9);
    }
}
