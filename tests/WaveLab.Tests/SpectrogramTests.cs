using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpectrogramTests(ITestOutputHelper output)
{
    private const int SampleRate = 48_000;

    private static float[] Tone(int length, double binPosition, int fftSize, double amplitude = 1.0)
    {
        var signal = new float[length];
        double frequency = binPosition * SampleRate / fftSize;
        for (int i = 0; i < length; i++)
            signal[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / SampleRate));
        return signal;
    }

    /// <summary>How many bins in a frame hold energy within <paramref name="rangeDb"/> of its peak.</summary>
    private static int BinsWithinOfPeak(SpectrogramData data, int frame, double rangeDb)
    {
        double peak = double.NegativeInfinity;
        for (int b = 0; b < data.Bins; b++) peak = Math.Max(peak, data[frame, b]);
        int count = 0;
        for (int b = 0; b < data.Bins; b++) if (data[frame, b] >= peak - rangeDb) count++;
        return count;
    }

    private static int PeakBin(SpectrogramData data, int frame)
    {
        int best = 0;
        for (int b = 1; b < data.Bins; b++) if (data[frame, b] > data[frame, best]) best = b;
        return best;
    }

    // ── what reassignment is for ─────────────────────────────────

    /// <summary>
    /// A partial sitting between two bins is the case an ordinary spectrogram draws as a band across
    /// both. Reassignment should collapse it.
    /// </summary>
    [Fact]
    public void ReassignmentConcentratesAPartialLyingBetweenBins()
    {
        const int fft = 2048;
        float[] signal = Tone(60_000, binPosition: 100.5, fftSize: fft);
        var plain = SpectrogramSettings.Default with { FftSize = fft, Hop = 512, Reassign = false };
        var sharp = plain with { Reassign = true };

        SpectrogramData ordinary = Spectrogram.Analyze(signal, 0, 60_000, SampleRate, plain);
        SpectrogramData reassigned = Spectrogram.Analyze(signal, 0, 60_000, SampleRate, sharp);

        int middle = ordinary.Frames / 2;
        int ordinaryWidth = BinsWithinOfPeak(ordinary, middle, 30);
        int reassignedWidth = BinsWithinOfPeak(reassigned, middle, 30);

        output.WriteLine($"30 dB width: ordinary {ordinaryWidth} bins, reassigned {reassignedWidth} bins");
        Assert.True(reassignedWidth < ordinaryWidth,
            $"reassignment widened the partial ({reassignedWidth} vs {ordinaryWidth} bins)");
    }

    /// <summary>Whichever way it is analysed, the partial must land where it actually is.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartialLandsOnItsOwnFrequency(bool reassign)
    {
        const int fft = 2048;
        float[] signal = Tone(60_000, binPosition: 100.0, fftSize: fft);
        var settings = SpectrogramSettings.Default with { FftSize = fft, Hop = 512, Reassign = reassign };

        SpectrogramData data = Spectrogram.Analyze(signal, 0, 60_000, SampleRate, settings);

        int peak = PeakBin(data, data.Frames / 2);
        output.WriteLine($"reassign={reassign}: peak at bin {peak}, {data.Frequency(peak):0.0} Hz");
        Assert.InRange(peak, 99, 101);
        Assert.InRange(data.Frequency(peak), 2_300, 2_400);   // 100 × 48000/2048 ≈ 2344 Hz
    }

    /// <summary>
    /// The time half of the same idea: a click is one instant, and an ordinary spectrogram spreads it
    /// over every frame whose window touches it.
    /// </summary>
    [Fact]
    public void ReassignmentLocalisesAnImpulseInTime()
    {
        const int fft = 1024, hop = 256, length = 40_000, click = 20_000;
        var signal = new float[length];
        signal[click] = 1f;

        var plain = SpectrogramSettings.Default with { FftSize = fft, Hop = hop, Reassign = false };
        SpectrogramData ordinary = Spectrogram.Analyze(signal, 0, length, SampleRate, plain);
        SpectrogramData reassigned = Spectrogram.Analyze(signal, 0, length, SampleRate, plain with { Reassign = true });

        output.WriteLine($"frames within 30 dB: ordinary {FramesLit(ordinary)}, reassigned {FramesLit(reassigned)}");
        Assert.True(FramesLit(reassigned) < FramesLit(ordinary),
            "reassignment should confine the impulse to fewer frames");

        static int FramesLit(SpectrogramData data)
        {
            double peak = double.NegativeInfinity;
            var perFrame = new double[data.Frames];
            for (int f = 0; f < data.Frames; f++)
            {
                double best = double.NegativeInfinity;
                for (int b = 0; b < data.Bins; b++) best = Math.Max(best, data[f, b]);
                perFrame[f] = best;
                peak = Math.Max(peak, best);
            }
            int count = 0;
            foreach (double value in perFrame) if (value >= peak - 30) count++;
            return count;
        }
    }

    // ── levels and geometry ──────────────────────────────────────

    /// <summary>
    /// A full-scale sine should read about 0 dB whatever window is chosen, or the display's numbers
    /// would silently depend on the analysis settings.
    /// </summary>
    [Theory]
    [InlineData(WindowKind.Hann)]
    [InlineData(WindowKind.BlackmanHarris)]
    [InlineData(WindowKind.Nuttall)]
    [InlineData(WindowKind.Hamming)]
    public void AFullScaleToneReadsNearZeroDbForEveryWindow(WindowKind kind)
    {
        const int fft = 2048;
        float[] signal = Tone(60_000, binPosition: 200.0, fftSize: fft);
        var settings = SpectrogramSettings.Default with
        {
            FftSize = fft, Hop = 512, Window = kind, Reassign = false,
        };

        SpectrogramData data = Spectrogram.Analyze(signal, 0, 60_000, SampleRate, settings);

        int middle = data.Frames / 2;
        double peak = data[middle, PeakBin(data, middle)];
        output.WriteLine($"{kind}: {peak:0.00} dB");
        Assert.InRange(peak, -1.5, 0.5);
    }

    [Fact]
    public void SilenceSitsOnTheFloor()
    {
        var settings = SpectrogramSettings.Default with { FloorDb = -96 };
        SpectrogramData data = Spectrogram.Analyze(new float[20_000], 0, 20_000, SampleRate, settings);

        Assert.All(data.MagnitudeDb, value => Assert.Equal(-96f, value));
    }

    [Fact]
    public void LevelsAreClampedToTheRequestedRange()
    {
        float[] signal = Tone(30_000, binPosition: 100.0, fftSize: 2048, amplitude: 4.0);
        var settings = SpectrogramSettings.Default with { FloorDb = -60, CeilingDb = -6, Reassign = false };

        SpectrogramData data = Spectrogram.Analyze(signal, 0, 30_000, SampleRate, settings);

        Assert.All(data.MagnitudeDb, value => Assert.InRange(value, -60f, -6f));
    }

    [Theory]
    [InlineData(2048, 512)]
    [InlineData(1024, 256)]
    [InlineData(4096, 1024)]
    public void GeometryMatchesTheSettings(int fftSize, int hop)
    {
        var settings = SpectrogramSettings.Default with { FftSize = fftSize, Hop = hop };
        SpectrogramData data = Spectrogram.Analyze(new float[48_000], 0, 48_000, SampleRate, settings);

        Assert.Equal(fftSize / 2 + 1, data.Bins);
        Assert.Equal(48_000 / hop, data.Frames);
        Assert.Equal(data.Frames * data.Bins, data.MagnitudeDb.Length);
        Assert.Equal(0, data.SampleAt(0));
        Assert.Equal(hop, data.SampleAt(1));
        Assert.Equal(0.0, data.Frequency(0));
        Assert.Equal(SampleRate / 2.0, data.Frequency(data.Bins - 1), 6);
    }

    [Fact]
    public void AnalysisStartsWhereItIsAskedTo()
    {
        SpectrogramData data = Spectrogram.Analyze(new float[48_000], 12_000, 24_000, SampleRate);

        Assert.Equal(12_000, data.FirstSample);
        Assert.Equal(12_000, data.SampleAt(0));
    }

    // ── reassignment windows ─────────────────────────────────────

    [Fact]
    public void TheTimeWeightedWindowIsAntisymmetricAboutTheCentre()
    {
        float[] window = WindowFunctions.Hann(64, periodic: true);
        var (timeWeighted, derivative) = Spectrogram.ReassignmentWindows(window, 64);

        // Time is measured from the frame centre, so weighting by it flips sign across the middle.
        double centre = 63 / 2.0;
        for (int i = 0; i < 64; i++)
            Assert.Equal((i - centre) * window[i], timeWeighted[i], 4);

        // A window rising then falling has a derivative that is positive then negative.
        Assert.True(derivative[16] > 0);
        Assert.True(derivative[48] < 0);
    }

    // ── validation ───────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 250)]    // not a power of two
    [InlineData(128, 32)]      // below the minimum
    [InlineData(32768, 8192)]  // above the maximum
    [InlineData(2048, 300)]    // hop does not divide the size
    [InlineData(2048, 0)]      // degenerate hop
    public void InvalidSettingsAreRejected(int fftSize, int hop)
    {
        var settings = SpectrogramSettings.Default with { FftSize = fftSize, Hop = hop };
        Assert.Throws<ArgumentException>(() =>
            Spectrogram.Analyze(new float[48_000], 0, 48_000, SampleRate, settings));
    }

    [Fact]
    public void ZeroLengthAnalysisIsEmptyRatherThanAnError()
    {
        SpectrogramData data = Spectrogram.Analyze(new float[1_000], 0, 0, SampleRate);
        Assert.Equal(0, data.Frames);
        Assert.Empty(data.MagnitudeDb);
    }

    [Fact]
    public void CancellationStopsAnalysis()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Spectrogram.Analyze(new float[400_000], 0, 400_000, SampleRate,
                SpectrogramSettings.Default, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsFromStartToFinish()
    {
        var reported = new List<double>();
        Spectrogram.Analyze(new float[200_000], 0, 200_000, SampleRate, SpectrogramSettings.Default,
            CancellationToken.None, new SynchronousProgress(reported.Add));

        Assert.NotEmpty(reported);
        Assert.Equal(0, reported[0], 6);
        Assert.Equal(1, reported[^1], 6);
    }

    private sealed class SynchronousProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
