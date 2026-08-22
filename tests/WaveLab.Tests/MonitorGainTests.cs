using WaveLab.Audio;
using WaveLab.Util;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The lift that makes removed material audible. Its whole justification is that it reaches the
/// speakers and nothing else — the moment it touches the samples, the residual stops being the
/// exact difference and stops being worth keeping.
/// </summary>
public sealed class MonitorGainTests(ITestOutputHelper output)
{
    private static AudioDocument Doc(params float[] samples) =>
        new([samples], 44_100, 32);

    [Fact]
    public void AnOrdinaryDocumentIsAtUnityAndIsNotAResidual()
    {
        var doc = Doc(0.1f, 0.2f);
        Assert.Equal(1f, doc.MonitorGain);
        Assert.False(doc.IsResidual);
    }

    [Fact]
    public void ANegativeOrInfiniteGainIsRefused()
    {
        var doc = Doc(0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.MonitorGain = -1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.MonitorGain = float.PositiveInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.MonitorGain = float.NaN);
    }

    /// <summary>
    /// A residual whose body and peak are close — a steady hiss bed with nothing sticking out of
    /// it — is placed by its body, at −24 dBFS.
    /// </summary>
    [Fact]
    public void AnEvenResidualIsPlacedByItsBody()
    {
        foreach (float rms in new[] { 1e-4f, 1e-3f, 1e-2f })
        {
            float peak = rms * 4;                       // crest factor of a noise bed
            float gain = ResidualSummary.MonitorGainFor(peak, rms);
            double landed = 20 * Math.Log10(rms * gain);
            output.WriteLine($"rms {20 * Math.Log10(rms):0.0} dBFS, peak {20 * Math.Log10(peak):0.0} " +
                             $"→ +{ResidualSummary.GainToDb(gain):0.0} dB → body at {landed:0.00} dBFS");
            Assert.Equal(ResidualSummary.MonitorRmsTargetDbfs, landed, 3);
        }
    }

    /// <summary>
    /// A residual with isolated loud moments — which is what click repair produces — is held back
    /// by its peak instead, so the clicks in it do not arrive past full scale.
    /// </summary>
    [Fact]
    public void ASpikyResidualIsHeldBackByItsPeak()
    {
        const float rms = 1e-4f;
        const float peak = 0.5f;                        // one click, 74 dB over the body
        float gain = ResidualSummary.MonitorGainFor(peak, rms);
        double landedPeak = 20 * Math.Log10(peak * gain);
        output.WriteLine($"+{ResidualSummary.GainToDb(gain):0.0} dB → peak at {landedPeak:0.00} dBFS");
        Assert.Equal(ResidualSummary.MonitorPeakCeilingDbfs, landedPeak, 3);
    }

    /// <summary>
    /// The lift can never take the loudest thing in the file past full scale. That matters more
    /// here than anywhere else in the app: the loudest thing in a declick residual is a click, and
    /// it is going to the speakers.
    /// </summary>
    [Fact]
    public void TheLiftNeverPushesThePeakPastFullScale()
    {
        var random = new Random(3);
        for (int n = 0; n < 2000; n++)
        {
            float peak = (float)Math.Pow(10, -(random.NextDouble() * 9));
            float rms = (float)(peak * Math.Pow(10, -(random.NextDouble() * 5)));
            Assert.True(peak * ResidualSummary.MonitorGainFor(peak, rms) <= 1f);
        }
    }

    /// <summary>
    /// A declick residual can be louder than the record it came out of — measured, one peaks above
    /// full scale while the programme sits at −7.8 dBFS. It is never cut.
    /// </summary>
    [Fact]
    public void NothingIsLiftedThatDoesNotNeedIt()
    {
        Assert.Equal(1f, ResidualSummary.MonitorGainFor(0f, 0f));
        Assert.Equal(1f, ResidualSummary.MonitorGainFor(ResidualSummary.SilenceThreshold, 1e-9f));
        Assert.Equal(1f, ResidualSummary.MonitorGainFor(0.9f, 0.3f));
        Assert.Equal(1f, ResidualSummary.MonitorGainFor(2.16f, 0.02f));   // the measured declick case
        Assert.Equal(1f, ResidualSummary.MonitorGainFor(float.NaN, 0.01f));
    }

    /// <summary>
    /// The cap binds well above the silence threshold, so it is a real band rather than a
    /// theoretical one — below the threshold nothing is lifted at all, because there is nothing
    /// there.
    /// </summary>
    [Fact]
    public void TheLiftStopsAtSixtyDecibels()
    {
        float gain = ResidualSummary.MonitorGainFor(1e-5f, 1e-6f);
        Assert.Equal(ResidualSummary.MaximumMonitorGainDb, ResidualSummary.GainToDb(gain), 3);
        Assert.Equal(1f, ResidualSummary.MonitorGainFor(1e-9f, 1e-9f));
    }

    /// <summary>
    /// A residual whose RMS could not be measured falls back to the peak rule rather than to no
    /// lift: something is better than a file that plays silently.
    /// </summary>
    [Fact]
    public void AMissingBodyMeasurementFallsBackToThePeak()
    {
        float gain = ResidualSummary.MonitorGainFor(0.01f, 0f);
        Assert.Equal(ResidualSummary.MonitorPeakCeilingDbfs, 20 * Math.Log10(0.01f * gain), 3);
    }

    [Fact]
    public void TheMonitorPathClampsRatherThanWrappingOrExploding()
    {
        var buffer = new float[] { 0.5f, -0.5f, 0.05f, -0.05f };
        PlaybackEngine.ApplyMonitorGain(buffer, 0, buffer.Length, 4f, limit: true);
        Assert.Equal([1f, -1f, 0.2f, -0.2f], buffer);
    }

    /// <summary>
    /// The case the limit exists for, and the one a gain-only test would miss: a declick residual
    /// is louder than the record it came out of, so it is left at unity — and unity is exactly
    /// where a "clamp only when the gain is not one" rule would let it straight through.
    /// </summary>
    [Fact]
    public void AResidualPastFullScaleIsHeldBackEvenAtUnityGain()
    {
        var buffer = new float[] { 2.16f, -2.16f, 0.5f };
        PlaybackEngine.ApplyMonitorGain(buffer, 0, buffer.Length, 1f, limit: true);
        Assert.Equal([1f, -1f, 0.5f], buffer);
    }

    [Fact]
    public void AnOrdinaryDocumentPassesThroughUntouched()
    {
        var buffer = new float[] { 0.5f, -0.25f, 1.9f };
        PlaybackEngine.ApplyMonitorGain(buffer, 0, buffer.Length, 1f, limit: false);
        Assert.Equal([0.5f, -0.25f, 1.9f], buffer);
    }

    [Fact]
    public void OnlyTheRequestedSpanIsTouched()
    {
        var buffer = new float[] { 0.1f, 0.1f, 0.1f, 0.1f };
        PlaybackEngine.ApplyMonitorGain(buffer, 1, 2, 2f, limit: true);
        Assert.Equal([0.1f, 0.2f, 0.2f, 0.1f], buffer);
    }

    /// <summary>
    /// The claim in the monitor bar's own caption: saves and exports write the true residual
    /// whatever the monitor is set to.
    /// </summary>
    [Fact]
    public void SavingADocumentUnderALiftWritesTheUnliftedSamples()
    {
        var samples = new float[512];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.001 * Math.Sin(2 * Math.PI * 440 * i / 44_100.0));

        var doc = new AudioDocument([(float[])samples.Clone()], 44_100, 32)
        {
            IsResidual = true,
            MonitorGain = ResidualSummary.MonitorGainFor(RestorationPeak(samples), RestorationRms(samples)),
        };
        Assert.True(doc.MonitorGain > 50f, "the test signal should need a large lift to be audible");

        string path = Path.Combine(Path.GetTempPath(), $"wavelab-residual-{Guid.NewGuid():N}.wav");
        try
        {
            WavCodec.Save(doc, path, 32, dither: false);
            AudioDocument reloaded = WavCodec.Load(path, CancellationToken.None);
            Assert.Equal(samples.Length, reloaded.Length);
            for (int i = 0; i < samples.Length; i++)
                Assert.Equal(samples[i], reloaded.Channels[0][i]);
            Assert.Equal(1f, reloaded.MonitorGain);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static float RestorationPeak(float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    private static float RestorationRms(float[] samples)
    {
        double sum = 0;
        foreach (float sample in samples) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
