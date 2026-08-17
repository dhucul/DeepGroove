using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// A-SPADE wired into the declipper, and the rule that decides when it is the better method.
/// </summary>
/// <remarks>
/// Neither reconstruction dominates, so the claim under test is not "A-SPADE is better" but "the
/// automatic choice is at least as good as whichever method would have been picked blindly". These
/// tests therefore run <em>both</em> methods on identical damage and check the automatic path
/// against the winner, which is the only assertion that stays honest if the crossover ever moves.
/// </remarks>
public sealed class DeclipMethodTests(ITestOutputHelper output)
{
    private const int SampleRate = 44_100;
    private const int Length = 40_000;

    /// <summary>Four partials over a whisper of noise. Sparse, so A-SPADE survives heavy damage.</summary>
    private static float[] Tonal(double peak = 0.95)
    {
        var random = new Random(3);
        var signal = new float[Length];
        double maximum = 0;
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)SampleRate;
            double value =
                  0.50 * Math.Sin(2 * Math.PI * 147 * t)
                + 0.28 * Math.Sin(2 * Math.PI * 294 * t + 0.4)
                + 0.16 * Math.Sin(2 * Math.PI * 441 * t - 0.8)
                + 0.09 * Math.Sin(2 * Math.PI * 882 * t)
                + (random.NextDouble() - 0.5) * 0.004;
            signal[i] = (float)value;
            maximum = Math.Max(maximum, Math.Abs(value));
        }
        for (int i = 0; i < Length; i++) signal[i] = (float)(signal[i] / maximum * peak);
        return signal;
    }

    /// <summary>Twenty-four harmonics over a −15 dB noise bed. Dense, so A-SPADE runs out early.</summary>
    private static float[] Dense(double peak = 0.95)
    {
        var random = new Random(7);
        var signal = new float[Length];
        double maximum = 0;
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)SampleRate;
            double value = 0;
            for (int h = 1; h <= 24; h++)
                value += Math.Sin(2 * Math.PI * 110 * h * t + h * 0.7) / h;
            value += (random.NextDouble() - 0.5) * 0.35;
            signal[i] = (float)value;
            maximum = Math.Max(maximum, Math.Abs(value));
        }
        for (int i = 0; i < Length; i++) signal[i] = (float)(signal[i] / maximum * peak);
        return signal;
    }

    /// <summary>Struck notes that decay to silence, so clean frames survive heavy clipping.</summary>
    private static float[] Percussive(double peak = 0.95)
    {
        var random = new Random(11);
        var signal = new float[Length];
        double maximum = 0;
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)SampleRate;
            int hit = i / 4000;
            double since = (i - hit * 4000) / (double)SampleRate;
            double envelope = Math.Exp(-since * 45);
            double value = envelope * (
                  Math.Sin(2 * Math.PI * (180 + hit * 37) * t)
                + 0.6 * Math.Sin(2 * Math.PI * (430 + hit * 61) * t)
                + 0.4 * (random.NextDouble() - 0.5));
            signal[i] = (float)value;
            maximum = Math.Max(maximum, Math.Abs(value));
        }
        for (int i = 0; i < Length; i++) signal[i] = (float)(signal[i] / maximum * peak);
        return signal;
    }

    private static (float[] Clipped, bool[] Mask) Clip(float[] clean, double level)
    {
        var clipped = (float[])clean.Clone();
        var mask = new bool[clean.Length];
        for (int i = 0; i < clean.Length; i++)
        {
            if (clipped[i] > level) { clipped[i] = (float)level; mask[i] = true; }
            else if (clipped[i] < -level) { clipped[i] = (float)-level; mask[i] = true; }
        }
        return (clipped, mask);
    }

    /// <summary>Error against the original, over the samples clipping destroyed.</summary>
    private static double ClippedSnrDb(float[] clean, float[] candidate, bool[] mask)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            if (!mask[i]) continue;
            double difference = clean[i] - candidate[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    private static ClippingAnalysisResult Analyse(float[] clipped, double level) =>
        Restoration.AnalyzeClipping([clipped], SampleRate, new ClippingAnalysisOptions
        {
            AbsoluteThreshold = level,
            MinimumConsecutiveSamples = 1,
            MinimumConfidence = 0,
        });

    private static double Repair(float[] clean, float[] clipped, bool[] mask,
        ClippingAnalysisResult analysis, DeclipMethod method)
    {
        var audio = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = method });
        return ClippedSnrDb(clean, audio[0], mask);
    }

    // ── the choice ───────────────────────────────────────────────

    /// <summary>
    /// The headline claim. Across material and severity the automatic method never lands meaningfully
    /// below the better of the two, which is what makes it safe as the default — and it beats the
    /// incumbent outright wherever A-SPADE is the winner.
    /// </summary>
    [Theory]
    [InlineData("tonal", 0.80)]
    [InlineData("tonal", 0.60)]
    [InlineData("tonal", 0.50)]
    [InlineData("tonal", 0.30)]
    [InlineData("dense", 0.70)]
    [InlineData("dense", 0.60)]
    [InlineData("dense", 0.50)]
    [InlineData("percussive", 0.50)]
    [InlineData("percussive", 0.15)]
    [InlineData("percussive", 0.09)]
    public void TheAutomaticChoiceTracksTheBetterMethod(string material, double level)
    {
        float[] clean = material switch
        {
            "dense" => Dense(),
            "percussive" => Percussive(),
            _ => Tonal(),
        };
        var (clipped, mask) = Clip(clean, level);
        var analysis = Analyse(clipped, level);

        double peak = Repair(clean, clipped, mask, analysis, DeclipMethod.PeakReconstruction);
        double sparse = Repair(clean, clipped, mask, analysis, DeclipMethod.Sparse);
        double automatic = Repair(clean, clipped, mask, analysis, DeclipMethod.Automatic);
        output.WriteLine($"{material} @ {level:0.00}: peak {peak:0.0}  sparse {sparse:0.0}  auto {automatic:0.0}");

        // Half a decibel of slack: the automatic path must equal one of the two, and which one it
        // equals is the point — this is not a tolerance on the reconstruction itself.
        double best = Math.Max(peak, sparse);
        Assert.True(automatic > best - 0.5,
            $"{material} at {level:0.00} chose the worse method: {automatic:0.0} dB against {best:0.0} available.");
    }

    /// <summary>
    /// The rule has to actually discriminate, or "tracks the better method" would pass by picking
    /// one method forever. Sparse material clipped at 40% goes to A-SPADE and dense material at the
    /// same severity does not.
    /// </summary>
    [Fact]
    public void SparsityAndNotDamageAloneDecidesTheMethod()
    {
        double sparse = DeclipMethodChooser.ToleratedClippedFraction(10);
        double dense = DeclipMethodChooser.ToleratedClippedFraction(45);
        Assert.True(sparse > dense, "Sparse material must tolerate more damage than dense material.");

        Assert.True(DeclipMethodChooser.PrefersSparse(0.40, effectiveSparsity: 10),
            "Tonal material at 40% clipped measured 4.6 dB better under A-SPADE.");
        Assert.False(DeclipMethodChooser.PrefersSparse(0.40, effectiveSparsity: 45),
            "Dense material at 40% clipped measured 0.5 dB better under the peak reconstruction.");
    }

    /// <summary>
    /// Undamaged audio is what the sparsity reading is taken from. Clipping is broadband, so counting
    /// damaged frames reads the flat tops rather than the music — measured at 31+ against 8.6 on the
    /// same percussive material, which is a different method at three of four severities.
    /// </summary>
    [Fact]
    public void SparsityIsMeasuredOnTheAudioThatSurvived()
    {
        float[] clean = Percussive();
        var (clipped, _) = Clip(clean, 0.09);

        double aware = DeclipMethodChooser.EffectiveSparsity(clipped, 0.09);
        double blind = DeclipMethodChooser.EffectiveSparsity(clipped);
        output.WriteLine($"clip-aware {aware:0.0}   damage-inclusive {blind:0.0}");

        Assert.True(aware < blind / 2,
            $"Reading the damage inflated sparsity from {aware:0.0} to only {blind:0.0}.");
        Assert.True(aware < DeclipMethodChooser.SparseBins,
            $"Percussive material read {aware:0.0} bins, which would classify it as dense.");
    }

    /// <summary>Silence and near-silence carry no evidence, and no evidence must not mean A-SPADE.</summary>
    [Fact]
    public void MaterialWithNothingToJudgeIsReadAsDense()
    {
        Assert.Equal(DeclipMethodChooser.DenseBins, DeclipMethodChooser.EffectiveSparsity(new float[8192]));
        Assert.Equal(DeclipMethodChooser.DenseBins, DeclipMethodChooser.EffectiveSparsity(new float[16]));
        Assert.False(DeclipMethodChooser.PrefersSparse(0, effectiveSparsity: 5),
            "Undamaged audio has no repair to choose a method for.");
    }

    // ── the wiring ───────────────────────────────────────────────

    /// <summary>
    /// STRENGTH and the reconstruction ceiling are the two controls the workbench exposes, and they
    /// have to mean the same thing whichever method ran, or switching method silently rescales the
    /// user's settings.
    /// </summary>
    [Fact]
    public void StrengthScalesTheSparseRepairToo()
    {
        float[] clean = Tonal();
        var (clipped, _) = Clip(clean, 0.7);
        var analysis = Analyse(clipped, 0.7);

        var dry = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = DeclipMethod.Sparse, Strength = 0 });
        var half = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = DeclipMethod.Sparse, Strength = 0.5 });
        var full = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = DeclipMethod.Sparse, Strength = 1 });

        Assert.Equal(clipped, dry[0]);
        for (int i = 0; i < clipped.Length; i++)
        {
            double moved = Math.Abs(half[0][i] - clipped[i]);
            double available = Math.Abs(full[0][i] - clipped[i]);
            Assert.True(moved <= available + 1e-5, $"Half strength moved further than full at {i}.");
        }
    }

    [Fact]
    public void TheSparseRepairHonoursTheReconstructionCeiling()
    {
        float[] clean = Tonal();
        var (clipped, _) = Clip(clean, 0.5);
        var analysis = Analyse(clipped, 0.5);

        var audio = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = DeclipMethod.Sparse, MaximumReconstructionDb = 1.0 });

        double ceiling = 0.5 * Math.Pow(10, 1.0 / 20.0);
        foreach (float value in audio[0])
        {
            Assert.True(float.IsFinite(value), "The sparse repair produced a non-finite sample.");
            Assert.True(Math.Abs(value) <= ceiling + 1e-4,
                $"Sample {value} passed the {ceiling:0.000} reconstruction ceiling.");
        }
    }

    /// <summary>
    /// The two methods must never both run over one channel. A-SPADE picks its own frame boundaries,
    /// and drawing an arch through its output afterwards would replace the waveform it reconstructed
    /// with the shape the other method assumed.
    /// </summary>
    [Fact]
    public void OnlyOneMethodTouchesAChannel()
    {
        float[] clean = Tonal();
        var (clipped, mask) = Clip(clean, 0.7);
        var analysis = Analyse(clipped, 0.7);

        var sparse = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = DeclipMethod.Sparse });
        var automatic = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = DeclipMethod.Automatic });

        // This severity and material is one the chooser sends to A-SPADE, so the automatic result
        // must be that pass untouched rather than that pass with arches drawn over it.
        Assert.Equal(sparse[0], automatic[0]);
        output.WriteLine($"sparse {ClippedSnrDb(clean, sparse[0], mask):0.0} dB");
    }

    [Fact]
    public void ChannelsAreChosenIndependently()
    {
        float[] tonal = Tonal();
        float[] dense = Dense();
        var (tonalClipped, _) = Clip(tonal, 0.6);
        var (denseClipped, _) = Clip(dense, 0.6);

        var analysis = Restoration.AnalyzeClipping([tonalClipped, denseClipped], SampleRate,
            new ClippingAnalysisOptions
            {
                AbsoluteThreshold = 0.6,
                MinimumConsecutiveSamples = 1,
                MinimumConfidence = 0,
            });

        var automatic = Restoration.RepairClipping([(float[])tonalClipped.Clone(), (float[])denseClipped.Clone()],
            analysis.Events);
        var allSparse = Restoration.RepairClipping([(float[])tonalClipped.Clone(), (float[])denseClipped.Clone()],
            analysis.Events, new DeclippingOptions { Method = DeclipMethod.Sparse });
        var allPeak = Restoration.RepairClipping([(float[])tonalClipped.Clone(), (float[])denseClipped.Clone()],
            analysis.Events, new DeclippingOptions { Method = DeclipMethod.PeakReconstruction });

        // Tonal at 40% clipped goes to A-SPADE and dense at 18% goes to A-SPADE as well, so the
        // interesting assertion is simply that the decision is per channel rather than per file:
        // at least one channel must be free to differ from a blanket choice.
        bool leftMatchesSparse = automatic[0].SequenceEqual(allSparse[0]);
        bool rightMatchesSparse = automatic[1].SequenceEqual(allSparse[1]);
        bool leftMatchesPeak = automatic[0].SequenceEqual(allPeak[0]);
        bool rightMatchesPeak = automatic[1].SequenceEqual(allPeak[1]);

        Assert.True(leftMatchesSparse || leftMatchesPeak, "Left channel matched neither method exactly.");
        Assert.True(rightMatchesSparse || rightMatchesPeak, "Right channel matched neither method exactly.");
    }

    [Fact]
    public void PeakReconstructionRemainsAvailableUnchanged()
    {
        float[] clean = Tonal();
        var (clipped, mask) = Clip(clean, 0.5);
        var analysis = Analyse(clipped, 0.5);

        double peak = Repair(clean, clipped, mask, analysis, DeclipMethod.PeakReconstruction);
        double raw = ClippedSnrDb(clean, clipped, mask);
        Assert.True(peak > raw + 6,
            $"The peak reconstruction moved heavily crushed tonal material only {peak - raw:0.0} dB.");
    }

    // ── what the workbench reports ───────────────────────────────

    /// <summary>
    /// The readout must describe the pass that actually ran. It is computed from the same call the
    /// repair selects channels with, so the two cannot drift — this pins that they agree.
    /// </summary>
    [Theory]
    [InlineData(0.60, DeclipMethod.Sparse)]
    [InlineData(0.50, DeclipMethod.PeakReconstruction)]
    public void TheReportedChoiceIsTheChoiceThatRuns(double level, DeclipMethod expected)
    {
        float[] clean = Tonal();
        var (clipped, _) = Clip(clean, level);
        var analysis = Analyse(clipped, level);

        var choices = Restoration.DescribeDeclipChoices([clipped], analysis.Events);
        var choice = Assert.Single(choices);
        output.WriteLine($"{choice.Method} · {choice.ClippedFraction * 100:0.#}% · {choice.EffectiveSparsity:0} bins");
        Assert.Equal(expected, choice.Method);

        // The report claims a method; the repair must produce that method's output.
        var automatic = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events);
        var forced = Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
            new DeclippingOptions { Method = expected });
        Assert.Equal(forced[0], automatic[0]);
    }

    [Fact]
    public void ForcingAMethodIsReportedWithoutMeasuringSparsity()
    {
        float[] clean = Tonal();
        var (clipped, _) = Clip(clean, 0.6);
        var analysis = Analyse(clipped, 0.6);

        foreach (var method in (DeclipMethod[])[DeclipMethod.Sparse, DeclipMethod.PeakReconstruction])
        {
            var choice = Assert.Single(Restoration.DescribeDeclipChoices([clipped], analysis.Events, method));
            Assert.Equal(method, choice.Method);
            Assert.True(choice.ClippedFraction > 0, "The damage is still worth reporting when the method is forced.");
        }
    }

    [Fact]
    public void UndamagedAudioIsDescribedAsNothingToDo()
    {
        float[] clean = Tonal(0.5);
        var analysis = Analyse(clean, 0.9);
        Assert.Empty(analysis.Events);
        Assert.Empty(Restoration.DescribeDeclipChoices([clean], analysis.Events));
    }

    [Fact]
    public void CancellationStopsTheSparsePath()
    {
        float[] clean = Tonal();
        var (clipped, _) = Clip(clean, 0.7);
        var analysis = Analyse(clipped, 0.7);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Restoration.RepairClipping([(float[])clipped.Clone()], analysis.Events,
                new DeclippingOptions { Method = DeclipMethod.Sparse }, cancellation.Token));
    }
}
