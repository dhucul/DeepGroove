using WaveLab.Audio.Dsp;
using System.Reflection;
using Xunit;

namespace WaveLab.Tests;

public sealed class LoudnessMeterTests
{
    private const int SampleRate = 48_000;

    /// <summary>
    /// Feeds a stereo 1 kHz tone whose amplitude steps through <paramref name="segmentAmplitudes"/>,
    /// each held for <paramref name="secondsPerSegment"/>.
    /// </summary>
    private static LoudnessMeter Measure(double[] segmentAmplitudes, int secondsPerSegment)
    {
        var meter = new LoudnessMeter();
        meter.Configure(SampleRate, 2);

        const int chunkFrames = 4800;
        var chunk = new float[chunkFrames * 2];
        long frame = 0;

        foreach (double amplitude in segmentAmplitudes)
        {
            long framesRemaining = (long)secondsPerSegment * SampleRate;
            while (framesRemaining > 0)
            {
                int frames = (int)Math.Min(chunkFrames, framesRemaining);
                for (int f = 0; f < frames; f++)
                {
                    float sample = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * (frame + f) / SampleRate));
                    chunk[f * 2] = sample;
                    chunk[f * 2 + 1] = sample;
                }
                meter.Process(chunk, 0, frames * 2);
                frame += frames;
                framesRemaining -= frames;
            }
        }
        return meter;
    }

    [Fact]
    public void SteadyProgrammeHasEssentiallyNoLoudnessRange()
    {
        LoudnessMeter meter = Measure([0.5], secondsPerSegment: 30);

        Assert.InRange(meter.LoudnessRangeLu, 0, 0.5);
    }

    /// <summary>
    /// Two passages ten decibels apart should report ten LU. The absolute levels do not matter:
    /// K-weighting shifts both alike, so the difference is what the measurement is really about.
    /// </summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(5.0)]
    [InlineData(20.0)]
    public void LoudnessRangeMatchesTheSpreadBetweenTwoPassages(double spreadDb)
    {
        double quiet = 0.5 * Math.Pow(10, -spreadDb / 20);
        LoudnessMeter meter = Measure([0.5, quiet], secondsPerSegment: 30);

        Assert.InRange(meter.LoudnessRangeLu, spreadDb - 1.5, spreadDb + 1.5);
    }

    /// <summary>
    /// The relative gate is what stops a quiet passage from being counted as programme. A run-out or
    /// a fade sitting more than 20 LU below the mean must drop out of the distribution entirely —
    /// measured from ungated momentary blocks, as this used to be, the same signal reports a range of
    /// roughly forty.
    /// </summary>
    [Fact]
    public void PassagesMoreThanTwentyLuDownAreGatedOut()
    {
        double tenDown = 0.5 * Math.Pow(10, -10.0 / 20);
        double fortyDown = 0.5 * Math.Pow(10, -40.0 / 20);

        LoudnessMeter meter = Measure([0.5, tenDown, fortyDown], secondsPerSegment: 30);

        Assert.InRange(meter.LoudnessRangeLu, 8.5, 12.5);
    }

    [Fact]
    public void SilenceBelowTheAbsoluteGateIsIgnored()
    {
        LoudnessMeter meter = Measure([0.5, 0.0], secondsPerSegment: 30);

        // Digital silence is far below -70 LUFS, so it never enters the distribution.
        Assert.InRange(meter.LoudnessRangeLu, 0, 1.0);
    }

    [Fact]
    public void TooLittleProgrammeReportsNoRangeRatherThanGuessing()
    {
        // Under three seconds there is not even one complete short-term window.
        LoudnessMeter meter = Measure([0.5], secondsPerSegment: 2);

        Assert.Equal(0, meter.LoudnessRangeLu);
    }

    [Fact]
    public void ResetClearsTheShortTermDistribution()
    {
        LoudnessMeter meter = Measure([0.5, 0.05], secondsPerSegment: 30);
        Assert.True(meter.LoudnessRangeLu > 5);

        meter.Reset();

        Assert.Equal(0, meter.LoudnessRangeLu);
    }

    // ── the measurements that were already there, now covered ────

    [Fact]
    public void IntegratedLoudnessSettlesNearTheShortTermValueOnSteadyProgramme()
    {
        LoudnessMeter meter = Measure([0.5], secondsPerSegment: 20);

        Assert.True(double.IsFinite(meter.IntegratedLufs));
        Assert.InRange(meter.IntegratedLufs, meter.ShortTermLufs - 0.5, meter.ShortTermLufs + 0.5);
    }

    [Fact]
    public void LouderProgrammeMeasuresLouder()
    {
        double quiet = Measure([0.1], 20).IntegratedLufs;
        double loud = Measure([0.5], 20).IntegratedLufs;

        Assert.InRange(loud - quiet, 13.0, 14.9);   // 20*log10(5) ≈ 14 dB
    }

    [Fact]
    public void TruePeakSeesBetweenTheSamples()
    {
        // A tone at a quarter of the sample rate, offset so no sample lands on the crest: the peak
        // sample reads well below full scale but the true peak is close to it.
        var meter = new LoudnessMeter();
        meter.Configure(SampleRate, 2);

        var chunk = new float[2 * 4800];
        double samplePeak = 0;
        for (int f = 0; f < 4800; f++)
        {
            float sample = (float)(0.9 * Math.Sin(2 * Math.PI * (SampleRate / 4.0) * f / SampleRate + Math.PI / 4));
            chunk[f * 2] = chunk[f * 2 + 1] = sample;
            samplePeak = Math.Max(samplePeak, Math.Abs(sample));
        }
        meter.Process(chunk, 0, chunk.Length);

        double samplePeakDb = 20 * Math.Log10(samplePeak);
        Assert.True(meter.TruePeakDb > samplePeakDb,
            $"true peak {meter.TruePeakDb:0.00} should exceed sample peak {samplePeakDb:0.00}");
        Assert.InRange(meter.TruePeakDb, -1.5, 0.5);
    }

    [Fact]
    public void NonFiniteSamplesDoNotPoisonTheMeter()
    {
        var meter = new LoudnessMeter();
        meter.Configure(SampleRate, 2);

        var chunk = new float[2 * 4800];
        for (int f = 0; f < 4800; f++)
        {
            chunk[f * 2] = f == 100 ? float.NaN : 0.5f;
            chunk[f * 2 + 1] = f == 200 ? float.PositiveInfinity : 0.5f;
        }
        meter.Process(chunk, 0, chunk.Length);

        Assert.True(double.IsFinite(meter.TruePeakDb));
        Assert.True(double.IsFinite(meter.MomentaryLufs) || double.IsNegativeInfinity(meter.MomentaryLufs));
    }

    [Fact]
    public async Task ChannelReconfigurationCannotSplitFrameCalculationFromIndexing()
    {
        var meter = new LoudnessMeter();
        meter.Configure(SampleRate, 1);
        object sync = typeof(LoudnessMeter)
            .GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(meter)!;
        var started = new ManualResetEventSlim();
        Task processing;

        lock (sync)
        {
            processing = Task.Run(() =>
            {
                started.Set();
                meter.Process(new float[2], 0, 2);
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            Thread.Sleep(25); // let Process reach the held lock

            // Monitor is re-entrant on this thread. The waiting Process must read
            // the new width only after this configuration is complete.
            meter.Configure(SampleRate, 2);
        }

        await processing.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
