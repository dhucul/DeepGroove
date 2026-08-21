using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// What each stage of a noise-reduction measurement actually costs, on a few seconds of audio.
/// </summary>
/// <remarks>
/// This exists because a corpus run of the two reducers over about ten minutes of audio consumed
/// <b>51 CPU-hours and never finished</b>. The cost model behind it said twenty minutes. Timing one
/// short pass first would have said so in seconds, which is the whole lesson.
/// </remarks>
public sealed class NoiseReductionCostTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    private static float[] Programme(int seconds, double noiseFloor)
    {
        var random = new Random(5);
        var data = new float[Rate * seconds];
        for (int i = 0; i < data.Length; i++)
        {
            double tone = 0.30 * Math.Sin(2 * Math.PI * 440 * i / (double)Rate)
                        + 0.12 * Math.Sin(2 * Math.PI * 1970 * i / (double)Rate);
            data[i] = (float)(tone + (random.NextDouble() - 0.5) * noiseFloor);
        }
        return data;
    }

    [Fact]
    public void MeasureWhatEachStageCosts()
    {
        const int seconds = 5;
        var source = Programme(seconds, 0.01);
        float[] profile = Restoration.LearnNoiseProfile([source], 0, Math.Min(source.Length, Rate * 2));

        double Time(string name, Action work)
        {
            work();                                   // warm
            var sw = Stopwatch.StartNew();
            work();
            double ms = sw.Elapsed.TotalMilliseconds;
            output.WriteLine($"{name,-34} {ms,9:F1} ms   {ms / (seconds * 1000) :F2}x realtime");
            return ms;
        }

        Time("LearnNoiseProfile (2 s window)",
            () => Restoration.LearnNoiseProfile([source], 0, Rate * 2));

        double gate = Time("ReduceNoise (spectral gate)", () =>
        {
            float[][] data = [(float[])source.Clone()];
            Restoration.ReduceNoise(data, profile, 10.0, 5.0);
        });

        // A bare pass through the same overlap-add machinery, changing nothing. Whatever this
        // costs is the framework rather than either estimator.
        var stft = new Stft(Restoration.NrFftSize, Restoration.NrFftSize / 4,
            Fft.HannWindow(Restoration.NrFftSize), Fft.HannWindow(Restoration.NrFftSize),
            StftLeadIn.None, StftNormalization.RunningSum);
        Time("Stft round trip, no processing", () =>
        {
            var copy = (float[])source.Clone();
            stft.Process(copy, copy, null);
        });
    }
}
