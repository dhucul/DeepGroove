using WaveLab.Audio.Dsp;
using WaveLab.Util;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The claim the feature exists to let a user check, checked here: that what a restoration pass
/// removes is the damage and not the music.
/// </summary>
/// <remarks>
/// Every other test in this group is about plumbing — that the difference is the difference, that
/// the tab opens, that the lift never touches the samples. This one runs a real repair over planted
/// damage and asks what actually ended up in the residual, which is the only question the person
/// listening to it has.
/// </remarks>
public sealed class ResidualIsTheDamageTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    /// <summary>
    /// Something with the properties a click detector has to survive: harmonics, vibrato, note
    /// changes and a noise bed. A pure tone would flatter it — nothing but the clicks would be
    /// unpredictable, which is exactly what the detector keys on.
    /// </summary>
    private static float[] Programme(int seconds = 5, int seed = 21)
    {
        int length = Rate * seconds;
        var random = new Random(seed);
        var signal = new float[length];
        double[] notes = [220, 277.18, 329.63, 440, 293.66];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / Rate;
            double fundamental = notes[(int)(t * 2) % notes.Length];
            double vibrato = 1 + 0.004 * Math.Sin(2 * Math.PI * 5.2 * t);
            double envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 2 * t);
            double value = 0;
            for (int harmonic = 1; harmonic <= 6; harmonic++)
                value += Math.Sin(2 * Math.PI * fundamental * vibrato * harmonic * t) / harmonic;
            signal[i] = (float)(0.25 * envelope * value + 0.002 * (random.NextDouble() * 2 - 1));
        }
        return signal;
    }

    private static double EnergyDb(double energy) =>
        energy <= 0 ? double.NegativeInfinity : 10 * Math.Log10(energy);

    [Fact]
    public void WhatClickRepairRemovesLandsOnTheClicksAndNotOnTheMusic()
    {
        float[] clean = Programme();
        (_, float[] damaged, bool[] hit, int planted) = ClickCorpus.Damage(clean, Rate, 18.0, seed: 5);
        Assert.True(planted > 20, $"only {planted} clicks were planted; the measurement needs more");

        var repaired = (float[])damaged.Clone();
        int found = Restoration.RemoveClicks([repaired], Rate, sensitivity: 5);
        Assert.True(found > 0, "the detector found nothing to repair, so there is no residual to judge");

        float[][] removed = RestorationPreview.Difference([damaged], [repaired]);
        float[] residual = removed[0];

        // Widened by the length of a repair: a defect is reconstructed from a span a little either
        // side of it, and counting those samples as "music" would score a correct repair as damage
        // to the programme.
        const int Skirt = 24;
        var onDamage = new bool[hit.Length];
        for (int i = 0; i < hit.Length; i++)
        {
            if (!hit[i]) continue;
            for (int j = Math.Max(0, i - Skirt); j < Math.Min(hit.Length, i + Skirt); j++)
                onDamage[j] = true;
        }

        double onEnergy = 0, offEnergy = 0, programmeEnergy = 0;
        for (int i = 0; i < residual.Length; i++)
        {
            double square = (double)residual[i] * residual[i];
            if (onDamage[i]) onEnergy += square; else offEnergy += square;
            programmeEnergy += (double)clean[i] * clean[i];
        }

        double share = onEnergy / Math.Max(double.Epsilon, onEnergy + offEnergy);
        float residualPeak = RestorationPreview.PeakOf(removed);
        float residualRms = RestorationPreview.RmsOf(removed);
        double liftDb = ResidualSummary.GainToDb(ResidualSummary.MonitorGainFor(residualPeak, residualRms));

        output.WriteLine($"{planted} planted, {found} repaired");
        output.WriteLine($"residual on damage {EnergyDb(onEnergy):0.0} dB, elsewhere {EnergyDb(offEnergy):0.0} dB, " +
                         $"programme {EnergyDb(programmeEnergy):0.0} dB");
        output.WriteLine($"share of the residual sitting on planted damage: {share:P2}");
        output.WriteLine($"residual peak {ResidualSummary.PeakText(residualPeak)}, " +
                         $"rms {ResidualSummary.PeakText(residualRms)}, " +
                         $"programme peak {ResidualSummary.PeakText(RestorationPreview.PeakOf([clean]))}, " +
                         $"monitor lift +{liftDb:0} dB");

        // The load-bearing claim. What was removed is overwhelmingly the planted damage; what lands
        // elsewhere is the detector's false alarms, and hearing them is the point of the feature.
        Assert.True(share > 0.98,
            $"only {share:P1} of what was removed sits on a planted click — the rest came out of the music");

        // And a declick residual needs no lift at all, which was worth measuring because it is the
        // opposite of what a residual sounds like it should be. A click planted 18 dB over the
        // local level is the loudest thing in the file, so what comes out is louder than the
        // programme it came out of — here the residual peaks above full scale while the programme
        // sits at -7.8 dBFS. The lift is for the hiss residual below, not for this one.
        Assert.True(liftDb == 0,
            $"a residual peaking at {ResidualSummary.PeakText(residualPeak)} was offered +{liftDb:0} dB it does not need");
        Assert.True(EnergyDb(onEnergy) > EnergyDb(offEnergy) + 20,
            "the removed material is not concentrated on the damage");
    }

    /// <summary>
    /// The spectral gate's residual is nothing like a hiss bed, and hearing that is the single best
    /// argument for the whole feature.
    /// </summary>
    /// <remarks>
    /// The gate reduces by a fixed depth wherever it is asked to, so what it removes tracks the
    /// programme rather than the noise: measured over a 24 dB range of planted hiss, the residual
    /// stays around 6 dB under the programme's own level and barely moves. That is the same finding
    /// this repo records from the corpus — a fixed reduction applied to hiss already far down costs
    /// more music than it saves noise, which is why <c>SuggestReductionDepthDb</c> exists and why
    /// the gate scores below do-nothing at the quiet severities. What is new is that it is now
    /// <i>audible</i>: the user can play the removed material and hear the music in it.
    /// </remarks>
    [Theory]
    [InlineData(18.0)]
    [InlineData(30.0)]
    public void WhatTheSpectralGateRemovesTracksTheProgrammeRatherThanTheNoise(double snrDb)
    {
        float[] clean = Programme(seconds: 4);
        (_, float[] damaged) = RestorationCorpus.PlantHiss(clean, snrDb, seed: 3);
        float[] profile = RestorationCorpus.LearnProfileAsTheWorkbenchWould(damaged, Rate);

        var reduced = (float[])damaged.Clone();
        Restoration.ReduceNoise([reduced], profile, reductionDb: 12, sensitivityDb: 6);

        float[][] removed = RestorationPreview.Difference([damaged], [reduced]);
        float residualRms = RestorationPreview.RmsOf(removed);
        float programmeRms = RestorationPreview.RmsOf([damaged]);
        double under = 20 * Math.Log10(programmeRms / residualRms);

        output.WriteLine($"hiss planted {snrDb:0} dB down · residual rms " +
                         $"{ResidualSummary.PeakText(residualRms)}, programme rms " +
                         $"{ResidualSummary.PeakText(programmeRms)} · residual sits {under:0.0} dB under");

        Assert.True(residualRms > ResidualSummary.SilenceThreshold, "the gate removed nothing at all");
        // Not a hiss bed tens of dB down — a few dB under the programme, whatever was planted.
        Assert.True(under is > 3 and < 12,
            $"the gate's residual sat {under:0.0} dB under the programme, which is not what six runs measured");
    }

    /// <summary>
    /// The case the monitor lift was built for: a hum residual is far enough under the programme
    /// to be inaudible at its own level, and needs a real lift before anyone can judge it.
    /// </summary>
    /// <remarks>
    /// It is also not <i>only</i> hum. A −42 dBFS mains line comes out as a −33 dBFS residual, so
    /// the notch bank took about 9 dB more than there was hum to take — the music in the notches'
    /// skirts, which is precisely why <c>HumTracker</c> subtracts an estimate of each partial
    /// instead of notching. Two tools, two residuals, and now a way to hear the difference.
    /// </remarks>
    [Fact]
    public void WhatHumRemovalTakesOutIsFarEnoughDownToNeedTheLift()
    {
        float[] clean = Programme(seconds: 3);
        var damaged = (float[])clean.Clone();
        const double HumLevel = 0.01;                    // -40 dBFS, a typical induced mains line
        for (int i = 0; i < damaged.Length; i++)
        {
            double t = (double)i / Rate;
            damaged[i] += (float)(HumLevel * Math.Sin(2 * Math.PI * 50 * t)
                                + HumLevel * 0.3 * Math.Sin(2 * Math.PI * 100 * t));
        }

        var dehummed = (float[])damaged.Clone();
        Restoration.RemoveHum([dehummed], Rate, baseFreq: 50, harmonics: 4, q: 30, strength: 1.0);

        float[][] removed = RestorationPreview.Difference([damaged], [dehummed]);
        float residualPeak = RestorationPreview.PeakOf(removed);
        float residualRms = RestorationPreview.RmsOf(removed);
        float programmeRms = RestorationPreview.RmsOf([damaged]);
        float gain = ResidualSummary.MonitorGainFor(residualPeak, residualRms);
        double liftDb = ResidualSummary.GainToDb(gain);

        output.WriteLine($"residual peak {ResidualSummary.PeakText(residualPeak)}, rms " +
                         $"{ResidualSummary.PeakText(residualRms)}, programme rms " +
                         $"{ResidualSummary.PeakText(programmeRms)}, lift +{liftDb:0} dB");
        output.WriteLine(ResidualSummary.Describe("side one (removed).wav", residualPeak, gain));

        Assert.True(residualRms > ResidualSummary.SilenceThreshold, "nothing was removed");
        Assert.True(20 * Math.Log10(programmeRms / residualRms) > 12,
            "a hum residual should sit well under the programme");
        Assert.True(liftDb > 5,
            $"a residual at {ResidualSummary.PeakText(residualRms)} rms was offered only +{liftDb:0} dB");
    }

    /// <summary>
    /// The other half of the same claim, and the one a user can act on: mixed back, the residual
    /// returns the record as it was. A repair the listener disagrees with is undoable in the
    /// document, and the residual is the evidence that nothing else went with it.
    /// </summary>
    [Fact]
    public void TheResidualAndTheRepairAddBackUpToTheRecord()
    {
        float[] clean = Programme(seconds: 2);
        (_, float[] damaged, _, _) = ClickCorpus.Damage(clean, Rate, 12.0, seed: 8);

        var repaired = (float[])damaged.Clone();
        Restoration.RemoveClicks([repaired], Rate, sensitivity: 6);
        float[] residual = RestorationPreview.Difference([damaged], [repaired])[0];

        double worst = 0;
        for (int i = 0; i < damaged.Length; i++)
            worst = Math.Max(worst, Math.Abs(repaired[i] + residual[i] - damaged[i]));

        output.WriteLine($"worst reconstruction error over {damaged.Length:N0} samples: {worst:0.###e+00}");
        Assert.True(worst <= 1e-6, $"the two halves do not add back up: {worst}");
    }
}
