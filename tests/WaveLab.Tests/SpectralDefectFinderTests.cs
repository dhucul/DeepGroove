using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class SpectralDefectFinderTests
{
    internal static float[] Programme(int rate = 44_100, double seconds = 1.4)
    {
        var data = new float[(int)Math.Round(rate * seconds)];
        var noise = new Random(782);
        for (int i = 0; i < data.Length; i++)
        {
            double t = i / (double)rate;
            data[i] = (float)(.08 * Math.Sin(2 * Math.PI * 440 * t) +
                .04 * Math.Sin(2 * Math.PI * 880 * t + .6) +
                .02 * Math.Sin(2 * Math.PI * 1760 * t + .2) +
                .006 * Math.Sin(2 * Math.PI * 3200 * t + .4) +
                .003 * (noise.NextDouble() - .5));
        }
        return data;
    }

    internal static float[] Ringing(float[] clean, int rate = 44_100, double at = .70, double frequency = 2900,
        double amplitude = .24)
    {
        var data = (float[])clean.Clone();
        int start = (int)Math.Round(at * rate);
        for (int i = start; i < Math.Min(data.Length, start + rate * .014); i++)
        {
            double t = (i - start) / (double)rate;
            data[i] += (float)(amplitude * Math.Min(1, t / .00025) * Math.Exp(-t / .0025) *
                Math.Sin(2 * Math.PI * frequency * t));
        }
        return data;
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void RoughSelectionFindsTheRingingBandAndTimeAtDifferentSampleRates(int rate)
    {
        float[] data = Ringing(Programme(rate), rate);
        float[] original = (float[])data.Clone();
        var found = SpectralDefectFinder.FindStrongest([data], rate, 0, data.Length);
        Assert.NotNull(found);
        Assert.InRange(found.PeakSample / (double)rate, .699, .708);
        Assert.True(found.StartSample / (double)rate < .701);
        Assert.True(found.EndSample / (double)rate > .705);
        Assert.InRange(2900, found.LowFrequency, found.HighFrequency);
        Assert.True(found.LowFrequency > 1000, "The bass must not become part of the repair.");
        Assert.True(found.HighFrequency < 5000, "A ringing event must not select all the treble.");
        Assert.True((found.EndSample - found.StartSample) / (double)rate < .05);
        Assert.Equal(original, data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChannelLocalOrOppositePolarityDefectDoesNotCancelInTheSearch(bool opposite)
    {
        float[] clean = Programme(), damaged = Ringing(clean);
        float[] right = opposite ? damaged.Select(v => -v).ToArray() : clean;
        var found = SpectralDefectFinder.FindStrongest([damaged, right], 44_100, 0, clean.Length);
        Assert.NotNull(found);
        Assert.InRange(found.PeakSample / 44_100.0, .699, .708);
    }

    [Fact]
    public void TheFoundMaskActuallyImprovesTheDefectWithoutChangingSurroundingAudio()
    {
        float[] clean = Programme(), damaged = Ringing(clean);
        var found = SpectralDefectFinder.FindStrongest([damaged], 44_100, 0, clean.Length);
        Assert.NotNull(found);
        var result = SpectralRepair.Heal(damaged, 0, found.CreateMask(44_100),
            new SpectralRepairOptions(found.FftSize, found.Hop, .10));
        Assert.False(result.IsEmpty);
        double before = 0, after = 0;
        for (int i = 0; i < result.Samples.Length; i++)
        {
            int sample = result.Start + i;
            before += Math.Pow(damaged[sample] - clean[sample], 2);
            after += Math.Pow(result.Samples[i] - clean[sample], 2);
            Assert.True(float.IsFinite(result.Samples[i]));
        }
        Assert.True(10 * Math.Log10(before / after) > 5,
            $"Repair improved the known added defect by only {10 * Math.Log10(before / after):0.00} dB.");
        Assert.True(result.Start > .65 * 44_100 && result.End < .76 * 44_100);
    }

    [Fact]
    public void SteadyMusicAndASustainedNoteOnsetAreNotRingingDefects()
    {
        float[] clean = Programme();
        Assert.Null(SpectralDefectFinder.FindStrongest([clean], 44_100, 0, clean.Length));
        for (int i = 30_870; i < clean.Length; i++)
            clean[i] += (float)(.2 * Math.Sin(2 * Math.PI * 2900 * i / 44_100));
        Assert.Null(SpectralDefectFinder.FindStrongest([clean], 44_100, 0, clean.Length));
    }

    [Fact]
    public void SilenceAndStationaryNoiseDoNotProduceARepairSelection()
    {
        Assert.Null(SpectralDefectFinder.FindStrongest([new float[44_100]], 44_100, 0, 44_100));
        var rng = new Random(381);
        var noise = Enumerable.Range(0, 44_100).Select(_ => (float)((rng.NextDouble() - .5) * .1)).ToArray();
        Assert.Null(SpectralDefectFinder.FindStrongest([noise], 44_100, 0, noise.Length));
    }

    [Fact]
    public void TheSearchDoesNotSelectADefectOutsideTheRoughSelection()
    {
        float[] data = Ringing(Programme());
        Assert.Null(SpectralDefectFinder.FindStrongest([data], 44_100, 4410, 13230));
        var found = SpectralDefectFinder.FindStrongest([data], 44_100, 28665, 6615);
        Assert.NotNull(found);
        Assert.InRange(found.StartSample, 28665, 35280);
        Assert.InRange(found.EndSample, 28665, 35280);
    }

    [Fact]
    public void DetectionIsIndependentOfOrdinaryGainChanges()
    {
        float[] loud = Ringing(Programme()), quiet = loud.Select(v => v * .05f).ToArray();
        var a = SpectralDefectFinder.FindStrongest([loud], 44_100, 0, loud.Length);
        var b = SpectralDefectFinder.FindStrongest([quiet], 44_100, 0, quiet.Length);
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Equal(a.PeakSample, b.PeakSample);
        Assert.Equal(a.StartSample, b.StartSample);
        Assert.Equal(a.EndSample, b.EndSample);
        Assert.Equal(a.LowFrequency, b.LowFrequency);
        Assert.Equal(a.HighFrequency, b.HighFrequency);
    }

    [Fact]
    public void BroadSearchesAndCancelledSearchesStopBeforeAnalysis()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpectralDefectFinder.FindStrongest([new float[441001]], 44_100, 0, 441001));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SpectralDefectFinder.FindStrongest(
            [new float[44_100]], 44_100, 0, 44_100, cancelled.Token));
    }

    [Fact]
    public void MissingSurroundingContextDoesNotInventARepairAtAFileEdge()
    {
        float[] data = Ringing(Programme(), at: .002);
        Assert.Null(SpectralDefectFinder.FindStrongest([data], 44_100, 0, 1000));
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(7000)]
    public void RingingIsLocatedAtOtherFrequencies(double frequency)
    {
        float[] data = Ringing(Programme(), frequency: frequency);
        var found = SpectralDefectFinder.FindStrongest([data], 44_100, 0, data.Length);
        Assert.NotNull(found);
        Assert.InRange(frequency, found.LowFrequency, found.HighFrequency);
        Assert.InRange(found.PeakSample / 44_100.0, .699, .708);
    }

    [Fact]
    public void ABroadbandPercussionBurstIsNotSelectedAsANarrowRingingDefect()
    {
        float[] data = Programme(); var rng = new Random(281);
        for (int i = 30_870; i < 31_752; i++)
            data[i] += (float)((rng.NextDouble() - .5) * .6 * Math.Exp(-(i - 30_870) / (44_100 * .003)));
        Assert.Null(SpectralDefectFinder.FindStrongest([data], 44_100, 0, data.Length));
    }

    [Fact]
    public void ARejectedHigherContrastBandDoesNotHideAValidRingingDefect()
    {
        float[] music = Programme();
        for (int i = 0; i < music.Length; i++)
            music[i] += (float)(.04 * Math.Sin(2 * Math.PI * 2900 * i / 44_100));
        float[] target = Ringing(music, amplitude: .12);
        float[] mixed = Ringing(target, frequency: 6200, amplitude: .05);

        var alone = SpectralDefectFinder.FindStrongest([target], 44_100, 0, target.Length);
        var competing = SpectralDefectFinder.FindStrongest([mixed], 44_100, 0, mixed.Length);
        Assert.NotNull(alone);
        Assert.NotNull(competing);
        Assert.InRange(competing.PeakSample / 44_100.0, .699, .708);
        Assert.InRange(2900, competing.LowFrequency, competing.HighFrequency);
        Assert.True(competing.HighFrequency < 6200, "The weaker rejected band must not replace the actual target.");
    }

    [Theory]
    [InlineData(.24, false)]
    [InlineData(.4, false)]
    [InlineData(.6, true)]
    public void ASteadyToneInTheOtherChannelCannotHideTheDefect(double toneAmplitude, bool swap)
    {
        float[] damaged = Ringing(Programme()), other = Programme();
        for (int i = 0; i < other.Length; i++)
            other[i] += (float)(toneAmplitude * Math.Sin(2 * Math.PI * 2900 * i / 44_100));
        float[][] stereo = swap ? [other, damaged] : [damaged, other];
        var alone = SpectralDefectFinder.FindStrongest([damaged], 44_100, 0, damaged.Length);
        var found = SpectralDefectFinder.FindStrongest(stereo, 44_100, 0, damaged.Length);
        Assert.NotNull(alone); Assert.NotNull(found);
        Assert.Equal(alone.PeakSample, found.PeakSample);
        Assert.Equal(alone.LowFrequency, found.LowFrequency);
        Assert.Equal(alone.HighFrequency, found.HighFrequency);
    }

    [Fact]
    public void UnrelatedDefectsInDifferentChannelsAreNotMergedIntoOneLargePatch()
    {
        float[] left = Ringing(Programme(), at: .45), right = Ringing(Programme(), at: .95, frequency: 6200);
        var found = SpectralDefectFinder.FindStrongest([left, right], 44_100, 0, left.Length);
        Assert.NotNull(found);
        Assert.True((found.EndSample - found.StartSample) / 44_100.0 < .05);
        Assert.True(found.HighFrequency < 5000 || found.LowFrequency > 4000);
    }

    [Fact]
    public void CompatibleChannelDetectionsCoverBothIndependentlyMeasuredSpans()
    {
        float[] left = Ringing(Programme(), at: .70), right = Ringing(Programme(), at: .703);
        var a = SpectralDefectFinder.FindStrongest([left], 44_100, 0, left.Length);
        var b = SpectralDefectFinder.FindStrongest([right], 44_100, 0, right.Length);
        var stereo = SpectralDefectFinder.FindStrongest([left, right], 44_100, 0, left.Length);
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(stereo);
        Assert.Equal(Math.Min(a.StartSample, b.StartSample), stereo.StartSample);
        Assert.Equal(Math.Max(a.EndSample, b.EndSample), stereo.EndSample);
        Assert.Equal(Math.Min(a.LowFrequency, b.LowFrequency), stereo.LowFrequency);
        Assert.Equal(Math.Max(a.HighFrequency, b.HighFrequency), stereo.HighFrequency);
    }

    [Fact]
    public void CancellationBetweenChannelsStopsTheRemainingAnalysis()
    {
        float[] data = Ringing(Programme()); using var cancelled = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => SpectralDefectFinder.FindStrongest(
            [data, data], 44_100, 0, data.Length, cancelled.Token, new CancelAfterChannel(cancelled)));
    }

    private sealed class CancelAfterChannel(CancellationTokenSource source) : IProgress<double>
    {
        public void Report(double value) { if (value >= .5) source.Cancel(); }
    }
}
