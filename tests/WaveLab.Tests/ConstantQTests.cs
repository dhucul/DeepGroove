using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The constant-Q analysis, against the two claims that justify it: it resolves the bottom of the
/// band where a fixed transform cannot, and it reads level the same as the analysis it sits beside.
/// </summary>
public sealed class ConstantQTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    private static float[] Tone(int length, double hz, double amplitude = 1.0)
    {
        var signal = new float[length];
        for (int i = 0; i < length; i++)
            signal[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return signal;
    }

    private static float[] Sum(params float[][] parts)
    {
        var signal = new float[parts[0].Length];
        foreach (float[] part in parts)
            for (int i = 0; i < signal.Length; i++) signal[i] += part[i];
        return signal;
    }

    /// <summary>The loudest bin of the middle frame, and what it read.</summary>
    private static (int Bin, double Db) Peak(SpectrogramData data)
    {
        int frame = data.Frames / 2;
        int best = 0;
        double peak = double.NegativeInfinity;
        for (int b = 0; b < data.Bins; b++)
        {
            double value = data[frame, b];
            if (value <= peak) continue;
            peak = value;
            best = b;
        }
        return (best, peak);
    }

    // ── the bins are where they claim to be ──────────────────────

    /// <summary>
    /// A tone must peak at the bin whose centre frequency it is. This is the test that catches a
    /// kernel built at the wrong frequency, or an axis off by an octave.
    /// </summary>
    [Theory]
    [InlineData(55)]
    [InlineData(220)]
    [InlineData(440)]
    [InlineData(1_000)]
    [InlineData(4_186)]
    [InlineData(12_000)]
    public void AToneLandsOnTheBinItBelongsTo(double hz)
    {
        SpectrogramData data = ConstantQ.Analyze(Tone(Rate, hz), 0, Rate, Rate);
        (int bin, double db) = Peak(data);

        double found = data.Frequency(bin);
        double cents = 1200 * Math.Log2(found / hz);

        output.WriteLine($"{hz,7:0} Hz -> bin {bin,3} at {found,8:0.0} Hz ({cents:+0.0;-0.0} cents), {db:0.0} dB");

        // A third of a semitone is 33 cents, so landing within half a bin is landing within 17.
        Assert.True(Math.Abs(cents) < 17, $"{hz} Hz landed {cents:0} cents away, at {found:0.0} Hz");
    }

    /// <summary>
    /// A full-scale tone reads 0 dB, which is the same calibration the linear analysis uses — the
    /// two displays have to agree about level or switching between them looks like a gain change.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(200)]
    public void AFullScaleToneOnABinCentreReadsZeroDecibels(int bin)
    {
        // Measured at a bin's own centre frequency, where there is no scalloping to confuse the
        // calibration with. Off-centre losses are the next test's business.
        double hz = ConstantQ.Frequencies(Rate, ConstantQSettings.Default)[bin];
        SpectrogramData data = ConstantQ.Analyze(Tone(Rate, hz), 0, Rate, Rate);
        (_, double db) = Peak(data);

        output.WriteLine($"bin {bin} is {hz:0.00} Hz; full scale there reads {db:+0.00;-0.00} dB");
        Assert.True(Math.Abs(db) < 0.15, $"a full-scale tone on a bin centre read {db:0.00} dB");
    }

    /// <summary>
    /// A tone between two bins reads low, and by no more than the window says it should.
    /// </summary>
    /// <remarks>
    /// Scalloping, and it is a property of the window rather than a defect: a Hann window's response
    /// is 1.42 dB down halfway between bins, so a tone landing there reads 1.42 dB light however
    /// correct everything else is. Worth a test of its own because the first version of the
    /// calibration test above measured tones at round numbers of hertz, which land wherever they
    /// land — 220 Hz sits 16 cents off a bin at 36 per octave — and read the window's own shape as a
    /// calibration error.
    /// </remarks>
    [Fact]
    public void AToneBetweenTwoBinsReadsLowByNoMoreThanTheWindowAllows()
    {
        double[] frequencies = ConstantQ.Frequencies(Rate, ConstantQSettings.Default);

        double worst = 0;
        double worstAt = 0;
        for (int bin = 60; bin < 260; bin += 20)
        {
            // Exactly halfway between two bins, in the geometric sense the axis uses.
            double hz = Math.Sqrt(frequencies[bin] * frequencies[bin + 1]);
            (_, double db) = Peak(ConstantQ.Analyze(Tone(Rate, hz), 0, Rate, Rate));
            if (-db > worst) { worst = -db; worstAt = hz; }
        }

        output.WriteLine($"worst scalloping {worst:0.00} dB, at {worstAt:0.0} Hz "
                         + "(a Hann window allows 1.42)");
        Assert.True(worst < 1.6, $"a tone between bins read {worst:0.00} dB light");
    }

    [Fact]
    public void HalvingTheAmplitudeCostsSixDecibels()
    {
        (_, double loud) = Peak(ConstantQ.Analyze(Tone(Rate, 1_000), 0, Rate, Rate));
        (_, double quiet) = Peak(ConstantQ.Analyze(Tone(Rate, 1_000, 0.5), 0, Rate, Rate));

        output.WriteLine($"{loud:0.00} dB and {quiet:0.00} dB, {loud - quiet:0.00} apart");
        Assert.Equal(6.02, loud - quiet, 0.15);
    }

    // ── the claim the whole thing is for ─────────────────────────

    /// <summary>
    /// The reason to have this at all: 50 Hz mains and 60 Hz mains must be separable, and a
    /// 2048-point transform cannot separate them because its bins are 21.5 Hz apart.
    /// </summary>
    [Fact]
    public void FiftyAndSixtyHertzAreTwoThingsAndNotOne()
    {
        float[] hum = Sum(Tone(Rate * 2, 50), Tone(Rate * 2, 60, 0.8));

        SpectrogramData data = ConstantQ.Analyze(hum, 0, Rate * 2, Rate);
        int frame = data.Frames / 2;

        int at50 = Nearest(data, 50), at60 = Nearest(data, 60);
        Assert.NotEqual(at50, at60);

        // A dip between the two is what "separated" means. Without one they are a single blob.
        double valley = double.PositiveInfinity;
        for (int b = Math.Min(at50, at60) + 1; b < Math.Max(at50, at60); b++)
            valley = Math.Min(valley, data[frame, b]);

        double lower = Math.Min(data[frame, at50], data[frame, at60]);
        output.WriteLine($"50 Hz at bin {at50} reads {data[frame, at50]:0.0} dB, "
                         + $"60 Hz at bin {at60} reads {data[frame, at60]:0.0} dB, "
                         + $"the valley between them {valley:0.0} dB");

        Assert.True(lower - valley > 6, $"only {lower - valley:0.0} dB of dip between them");

        static int Nearest(SpectrogramData data, double hz)
        {
            int best = 0;
            double distance = double.MaxValue;
            for (int b = 0; b < data.Bins; b++)
            {
                double d = Math.Abs(data.Frequency(b) - hz);
                if (d >= distance) continue;
                distance = d;
                best = b;
            }
            return best;
        }
    }

    /// <summary>
    /// Every octave gets the same number of bins. That is the definition, and it is what the linear
    /// analysis cannot do however it is drawn.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(36)]
    public void EveryOctaveGetsTheSameNumberOfBins(int perOctave)
    {
        var settings = ConstantQSettings.Default with { BinsPerOctave = perOctave };
        double[] frequencies = ConstantQ.Frequencies(Rate, settings);

        foreach (double bottom in new[] { 50.0, 400.0, 3_000.0 })
        {
            int inOctave = frequencies.Count(f => f >= bottom && f < bottom * 2);
            output.WriteLine($"{perOctave}/octave: {inOctave} bins between {bottom:0} and {bottom * 2:0} Hz");
            Assert.Equal(perOctave, inOctave);
        }
    }

    /// <summary>Bins ascend, and by a constant ratio, which is what makes the inverse a logarithm.</summary>
    [Fact]
    public void TheBinsRiseByAConstantRatio()
    {
        double[] frequencies = ConstantQ.Frequencies(Rate, ConstantQSettings.Default);
        double expected = Math.Pow(2, 1.0 / 36);

        for (int b = 1; b < frequencies.Length; b++)
            Assert.Equal(expected, frequencies[b] / frequencies[b - 1], 1e-9);
    }

    // ── it travels through the display like the linear one ───────

    /// <summary>
    /// The display asks the data where a frequency falls rather than assuming an arithmetic. The
    /// answer has to be the inverse of the frequency table, or the picture is drawn at the wrong
    /// height and selections land in the wrong place.
    /// </summary>
    [Fact]
    public void TheBinLookupIsTheInverseOfTheFrequencyTable()
    {
        SpectrogramData data = ConstantQ.Analyze(Tone(Rate, 440), 0, Rate, Rate);

        for (int b = 0; b < data.Bins; b += 7)
        {
            double round = data.BinForFrequency(data.Frequency(b));
            Assert.Equal(b, round, 1e-6);
        }
    }

    /// <summary>The linear analysis has no table and must keep its own arithmetic.</summary>
    [Fact]
    public void ALinearAnalysisStillMapsLinearly()
    {
        SpectrogramData data = Spectrogram.Analyze(Tone(Rate, 440), 0, Rate, Rate);

        Assert.Null(data.BinFrequencies);
        Assert.Equal(0, data.BinForFrequency(0), 1e-9);
        Assert.Equal(data.FftSize / 2.0, data.BinForFrequency(Rate / 2.0), 1e-6);
        for (int b = 0; b < data.Bins; b += 13)
            Assert.Equal(b, data.BinForFrequency(data.Frequency(b)), 1e-6);
    }

    // ── shape and edges ──────────────────────────────────────────

    [Fact]
    public void TheAnalysisHasTheShapeItSaysItHas()
    {
        var settings = ConstantQSettings.Default with { Hop = 512 };
        SpectrogramData data = ConstantQ.Analyze(Tone(Rate, 1_000), 0, Rate, Rate, settings);

        Assert.Equal(ConstantQ.BinCount(Rate, settings), data.Bins);
        Assert.Equal(data.Frames * data.Bins, data.MagnitudeDb.Length);
        Assert.Equal(512, data.Hop);
        Assert.All(data.MagnitudeDb, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void SilenceReadsAsSilenceRatherThanNothing()
    {
        SpectrogramData data = ConstantQ.Analyze(new float[Rate], 0, Rate, Rate);
        Assert.All(data.MagnitudeDb, v => Assert.True(v < -100, $"silence read {v} dB"));
    }

    [Fact]
    public void AnEmptySpanIsHandled()
    {
        SpectrogramData data = ConstantQ.Analyze([], 0, 0, Rate);
        Assert.Equal(0, data.Frames);
        Assert.Empty(data.MagnitudeDb);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ConstantQ.Analyze(Tone(Rate * 4, 440), 0, Rate * 4, Rate, ConstantQSettings.Default,
                cancelled.Token));
    }
}
