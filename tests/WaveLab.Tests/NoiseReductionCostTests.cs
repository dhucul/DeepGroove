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

    /// <summary>
    /// A waveform residual cannot score a notch bank, and this is the evidence for that claim.
    /// </summary>
    /// <remarks>
    /// Cascaded notches rotate phase well outside their own bandwidth. A sample-wise difference
    /// charges that rotation as error even though nobody hears it, so the same filter looks
    /// catastrophic by one metric and near-transparent by another. The first hum measurement made
    /// exactly this mistake and reported the wrong winner by 52 cells to 2 - see
    /// <c>RestorationCorpusTests.TheNotchBankTakesTheHumOffWithoutTakingTheMusic</c>.
    /// </remarks>
    [Fact]
    public void AWaveformResidualCannotScoreANotchBank()
    {
        const int seconds = 20;
        var music = new float[Rate * seconds];
        for (int i = 0; i < music.Length; i++)
            music[i] = (float)(0.30 * Math.Sin(2 * Math.PI * 443 * i / (double)Rate)
                             + 0.15 * Math.Sin(2 * Math.PI * 1973 * i / (double)Rate));

        // No hum in it at all, so a perfect remover would change nothing whatever.
        float[][] notched = [(float[])music.Clone()];
        Restoration.RemoveHum(notched, Rate, 50.0, 6, 30.0);

        double signal = 0, error = 0;
        for (int i = Rate; i < music.Length; i++)          // skip a second of ring-in
        {
            double d = music[i] - notched[0][i];
            signal += (double)music[i] * music[i];
            error += d * d;
        }
        double residualDb = 10 * Math.Log10(signal / Math.Max(error, 1e-30));

        // Probed at the two frequencies the music is actually at, rather than through
        // RestorationCorpus.MusicChangeDb - that takes the worst of seven fixed probes, which is
        // right for the broadband corpus material it is written for and wrong here, where five of
        // the seven sit on a leakage floor a notch bank moves by 17 dB without touching any music.
        double probeDb = 0;
        foreach (double frequency in new[] { 443.0, 1973.0 })
            probeDb = Math.Max(probeDb, Math.Abs(
                RestorationCorpus.BinPowerDb(notched[0], Rate, frequency, Rate)
                - RestorationCorpus.BinPowerDb(music, Rate, frequency, Rate)));

        output.WriteLine($"notching music that has no hum in it: waveform residual says " +
            $"{residualDb:F1} dB SNR, the music's own frequencies say {probeDb:F2} dB moved");

        // The residual reports substantial damage; the frequency-domain probe reports almost none.
        Assert.True(residualDb < 30, $"residual unexpectedly clean at {residualDb:F1} dB");
        Assert.True(probeDb < 1.0, $"the bank really did move the music, by {probeDb:F2} dB");
    }
}
