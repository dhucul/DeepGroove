using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class StftTests
{
    private static float[] Noise(int n, int seed)
    {
        var random = new Random(seed);
        var signal = new float[n];
        for (int i = 0; i < n; i++) signal[i] = (float)(random.NextDouble() * 2 - 1);
        return signal;
    }

    private static double WorstError(float[] a, float[] b)
    {
        double worst = 0;
        for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    // ── perfect reconstruction ───────────────────────────────────

    /// <summary>
    /// The property everything else rests on: analyse and resynthesise without touching the spectrum
    /// and the signal must come back unchanged — including the first and last samples, which a naive
    /// frame layout fades in from silence.
    /// </summary>
    [Theory]
    [InlineData(1024, 256)]   // 75 %, the default
    [InlineData(1024, 512)]   // 50 %
    [InlineData(1024, 128)]   // 87.5 %
    [InlineData(256, 64)]
    [InlineData(4096, 1024)]
    public void UntouchedFramesReconstructTheSignalExactly(int fftSize, int hop)
    {
        var stft = new Stft(fftSize, hop);
        float[] input = Noise(20_000, seed: fftSize + hop);
        var output = new float[input.Length];

        stft.Process(input, output, processor: null);

        Assert.True(stft.ReconstructsExactly());
        Assert.True(WorstError(input, output) < 1e-5, $"worst error {WorstError(input, output):E3}");
    }

    [Fact]
    public void ReconstructionHoldsRightToBothEdges()
    {
        var stft = new Stft(512, 128);
        float[] input = Noise(4096, seed: 7);
        var output = new float[input.Length];

        stft.Process(input, output, processor: null);

        // The edges are where a partial overlap would show up, so check them specifically.
        for (int i = 0; i < 64; i++)
        {
            Assert.True(Math.Abs(input[i] - output[i]) < 1e-5, $"start sample {i} drifted");
            int j = input.Length - 1 - i;
            Assert.True(Math.Abs(input[j] - output[j]) < 1e-5, $"end sample {j} drifted");
        }
    }

    [Fact]
    public void ProcessingInPlaceMatchesProcessingIntoASeparateBuffer()
    {
        var stft = new Stft(512, 128);
        float[] input = Noise(8192, seed: 21);
        var separate = new float[input.Length];
        var inPlace = (float[])input.Clone();

        stft.Process(input, separate, (_, _, re, im) => Halve(re, im));
        stft.Process(inPlace, inPlace, (_, _, re, im) => Halve(re, im));

        Assert.True(WorstError(separate, inPlace) < 1e-6);

        static void Halve(Span<float> re, Span<float> im)
        {
            for (int b = 0; b < re.Length; b++) { re[b] *= 0.5f; im[b] *= 0.5f; }
        }
    }

    [Fact]
    public void AHannAnalysisWithRectangularSynthesisAlsoReconstructs()
    {
        // The other classic pairing: all the tapering on analysis, none on synthesis.
        float[] analysis = WindowFunctions.Hann(1024, periodic: true);
        float[] synthesis = WindowFunctions.Create(WindowKind.Rectangular, 1024);
        var stft = new Stft(1024, 512, analysis, synthesis);

        float[] input = Noise(10_000, seed: 3);
        var output = new float[input.Length];
        stft.Process(input, output, processor: null);

        Assert.True(stft.ReconstructsExactly());
        Assert.True(WorstError(input, output) < 1e-5);
    }

    // ── spectral modification ────────────────────────────────────

    [Fact]
    public void ZeroingEverySpectrumProducesSilence()
    {
        var stft = new Stft(512, 128);
        float[] input = Noise(4096, seed: 11);
        var output = new float[input.Length];

        stft.Process(input, output, (_, _, re, im) => { re.Clear(); im.Clear(); });

        Assert.All(output, sample => Assert.True(Math.Abs(sample) < 1e-6));
    }

    [Fact]
    public void ScalingEverySpectrumScalesTheSignal()
    {
        var stft = new Stft(512, 128);
        float[] input = Noise(4096, seed: 13);
        var output = new float[input.Length];

        stft.Process(input, output, (_, _, re, im) =>
        {
            for (int b = 0; b < re.Length; b++) { re[b] *= 0.25f; im[b] *= 0.25f; }
        });

        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(input[i] * 0.25f - output[i]) < 1e-5);
    }

    /// <summary>A brickwall in the spectral domain should actually remove the band.</summary>
    [Fact]
    public void ZeroingABandRemovesThatBandFromTheOutput()
    {
        const int sampleRate = 48_000, fftSize = 2048, hop = 512, length = 24_000;
        var stft = new Stft(fftSize, hop);

        var input = new float[length];
        for (int i = 0; i < length; i++)
            input[i] = (float)(Math.Sin(2 * Math.PI * 500 * i / sampleRate)
                             + Math.Sin(2 * Math.PI * 8000 * i / sampleRate));

        var output = new float[length];
        int cutoffBin = (int)(4000.0 / sampleRate * fftSize);
        stft.Process(input, output, (_, _, re, im) =>
        {
            for (int b = cutoffBin; b < re.Length; b++) { re[b] = 0; im[b] = 0; }
        });

        // Measure what survived at each tone, away from the edges.
        Assert.True(ToneAmplitude(output, 500, sampleRate) > 0.9);
        Assert.True(ToneAmplitude(output, 8000, sampleRate) < 0.02);
    }

    private static double ToneAmplitude(float[] signal, double frequency, int sampleRate)
    {
        int from = signal.Length / 4, to = signal.Length * 3 / 4;
        double real = 0, imaginary = 0;
        for (int i = from; i < to; i++)
        {
            (double sin, double cos) = Math.SinCos(2 * Math.PI * frequency * i / sampleRate);
            real += signal[i] * cos;
            imaginary += signal[i] * sin;
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / (to - from);
    }

    // ── analysis ─────────────────────────────────────────────────

    [Fact]
    public void AnalyzeFindsAToneInItsOwnBin()
    {
        const int fftSize = 1024, hop = 256, bin = 40;
        var stft = new Stft(fftSize, hop);
        var input = new float[8192];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * bin * i / fftSize);

        int inspected = 0;
        stft.Analyze(input, (index, start, re, im) =>
        {
            if (start < fftSize || start > input.Length - 2 * fftSize) return;   // steady-state frames only
            int loudest = 0;
            double best = -1;
            for (int b = 0; b < re.Length; b++)
            {
                double magnitude = re[b] * re[b] + im[b] * im[b];
                if (magnitude > best) { best = magnitude; loudest = b; }
            }
            Assert.Equal(bin, loudest);
            inspected++;
        });

        Assert.True(inspected > 4);
    }

    [Theory]
    [InlineData(10_000, 1024, 256)]
    [InlineData(1, 512, 128)]
    [InlineData(5000, 256, 256)]
    public void FrameCountMatchesTheFramesActuallyVisited(int length, int fftSize, int hop)
    {
        var stft = new Stft(fftSize, hop);
        var input = new float[length];

        int visited = 0;
        stft.Analyze(input, (_, _, _, _) => visited++);

        Assert.Equal(visited, stft.FrameCount(length));
    }

    // ── configuration ────────────────────────────────────────────

    [Fact]
    public void DefaultConfigurationIsSqrtHannBothSides()
    {
        var stft = new Stft(1024, 256);

        Assert.Same(stft.AnalysisWindow, stft.SynthesisWindow);
        Assert.Equal(513, stft.Bins);
        Assert.Equal(768, stft.Latency);
        Assert.Equal(2.0, stft.OverlapConstant, 6);
        Assert.True(stft.ReconstructsExactly());
    }

    [Fact]
    public void APairThatCannotTileIsReportedRatherThanSilentlyRippling()
    {
        // Symmetric Hann at 50 % does not sum flat; the constructor still accepts it, but the caller
        // can find out before trusting the output.
        var stft = new Stft(512, 256, Fft.HannWindow(512),
            WindowFunctions.Create(WindowKind.Rectangular, 512));

        Assert.False(stft.ReconstructsExactly());
    }

    [Theory]
    [InlineData(1000, 250)]   // not a power of two
    [InlineData(3, 1)]        // too small
    [InlineData(1024, 300)]   // hop does not divide the size
    [InlineData(1024, 0)]     // degenerate hop
    [InlineData(1024, 2048)]  // hop larger than the frame
    public void InvalidConfigurationsAreRejected(int fftSize, int hop)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Stft(fftSize, hop));
    }

    [Fact]
    public void MismatchedWindowLengthsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new Stft(1024, 256, new float[512]));
        Assert.Throws<ArgumentException>(() => new Stft(1024, 256, null, new float[512]));
    }

    [Fact]
    public void OutputShorterThanInputIsRejected()
    {
        var stft = new Stft(256, 64);
        Assert.Throws<ArgumentException>(() => stft.Process(new float[100], new float[50], null));
    }

    [Fact]
    public void EmptyInputIsANoOp()
    {
        var stft = new Stft(256, 64);
        stft.Process([], [], (_, _, _, _) => throw new InvalidOperationException("should not run"));
        Assert.Equal(0, stft.FrameCount(0));
    }

    [Fact]
    public void CancellationStopsProcessing()
    {
        var stft = new Stft(256, 64);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            stft.Process(new float[10_000], new float[10_000], null, cancellation.Token));
    }
}
