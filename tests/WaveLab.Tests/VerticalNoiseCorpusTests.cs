using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The vertical-surface-noise chain measured on real record transfers.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>WAVELAB_CORPUS=1</c>, like every other corpus harness here. The recordings are
/// the float WAV transfers made by this app's own recorder, which sit beside the AIFFs corpus 1 is
/// built from — <c>DeclipCorpus</c> deliberately takes only the AIFFs, and nothing here changes
/// that, because every corpus figure in <c>CLAUDE.md</c> is AIFF-only and must stay comparable.
/// </para>
/// <para>
/// <b>The ordering claim is the thing to protect.</b> On the un-collapsed stereo file the shipped
/// de-crackler barely moves this material; after the side is collapsed the same detector, unchanged,
/// removes most of what is left. That is a fact about the chain's order, so the second test here
/// asserts the <em>failure</em> as well as the first asserts the success — otherwise someone could
/// reorder the stages, lose the effect entirely, and still pass.
/// </para>
/// </remarks>
public sealed class VerticalNoiseCorpusTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    /// <summary>Ticks louder than this, above 120 Hz, are what "crackle" means to a listener.</summary>
    private const double TickLevel = 0.005_623_413;   // −45 dBFS

    private static bool Enabled => Environment.GetEnvironmentVariable("WAVELAB_CORPUS") is { Length: > 0 };

    private static string CorpusRoot =>
        Environment.GetEnvironmentVariable("WAVELAB_CORPUS1") is { Length: > 0 } set
            ? set
            : @"C:\Users\dhucu\Music\mymusic";

    private sealed record Transfer(string Name, float[][] Channels);

    private static IReadOnlyList<Transfer> Transfers()
    {
        if (!Directory.Exists(CorpusRoot)) return [];
        var found = new List<Transfer>();
        foreach (string file in Directory.GetFiles(CorpusRoot, "*.wav")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith("._", StringComparison.Ordinal)) continue;
            AudioDocument document;
            try { document = AudioImporter.Load(file); }
            catch (Exception) { continue; }
            if (document.Channels.Count != 2 || document.SampleRate != Rate) continue;
            if (document.Length < Rate * 20) continue;
            found.Add(new Transfer(Path.GetFileNameWithoutExtension(file),
                [document.Channels[0], document.Channels[1]]));
        }
        return found;
    }

    private static float[][] Slice(float[][] channels, int start, int count)
    {
        var copy = new float[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            copy[c] = new float[count];
            Array.Copy(channels[c], start, copy[c], 0, count);
        }
        return copy;
    }

    /// <summary>Excursions above <see cref="TickLevel"/> once the rumble is out of the way.</summary>
    private static int Ticks(float[] channel)
    {
        var work = new[] { (float[])channel.Clone() };
        Restoration.RemoveSubsonic(work, Rate, 120);
        float[] high = work[0];

        int count = 0;
        for (int i = 0; i < high.Length; i++)
        {
            if (Math.Abs(high[i]) <= TickLevel) continue;
            count++;
            while (i < high.Length && Math.Abs(high[i]) > TickLevel * 0.4) i++;
            i += 8;                                   // one tick, not its ringing
        }
        return count;
    }

    private static double Rms(float[] channel)
    {
        double sum = 0;
        foreach (float value in channel) sum += (double)value * value;
        return 20 * Math.Log10(Math.Sqrt(sum / Math.Max(1, channel.Length)) + 1e-12);
    }

    /// <summary>First-difference energy share — a proxy for how much top end a repair cost.</summary>
    private static double HighFrequencyDb(float[] channel)
    {
        double difference = 0, total = 0;
        for (int i = 1; i < channel.Length; i++)
        {
            double d = channel[i] - channel[i - 1];
            difference += d * d;
            total += (double)channel[i] * channel[i];
        }
        return 10 * Math.Log10(difference / Math.Max(1e-30, total) + 1e-30);
    }

    private static void Decrackle(float[][] channels)
    {
        foreach (float[] channel in channels)
            WaveLab.Audio.Dsp.Decrackle.Process(channel, DecrackleOptions.Default);
    }

    // ── the claims ───────────────────────────────────────────────

    /// <summary>
    /// Surface noise is vertical on every transfer measured, which is what makes the side control
    /// worth having at all — and it is <em>not</em> what decides how far the side may go.
    /// </summary>
    [Fact]
    public void SurfaceNoiseIsVerticalOnEveryTransfer()
    {
        if (!Enabled) return;
        var transfers = Transfers();
        if (transfers.Count == 0) return;

        foreach (Transfer transfer in transfers)
        {
            int length = transfer.Channels[0].Length;
            float[][] programme = Slice(transfer.Channels, length / 3, Rate * 4);
            float[][] quiet = Slice(transfer.Channels, length - Rate * 4, Rate * 4);

            double programmeRatio = SideToMidDb(programme);
            double quietRatio = SideToMidDb(quiet);
            output.WriteLine($"{transfer.Name,-34} programme {programmeRatio,6:0.0} dB, " +
                             $"quiet {quietRatio,6:0.0} dB, rise {quietRatio - programmeRatio,5:0.0} dB");

            Assert.True(quietRatio > programmeRatio,
                $"{transfer.Name}: the quiet end is no more vertical than the programme");
        }
    }

    private static double SideToMidDb(float[][] channels)
    {
        double mid = 0, side = 0;
        for (int i = 0; i < channels[0].Length; i++)
        {
            double m = (channels[0][i] + channels[1][i]) * 0.5;
            double s = (channels[0][i] - channels[1][i]) * 0.5;
            mid += m * m;
            side += s * s;
        }
        return 10 * Math.Log10(Math.Max(1e-20, side) / Math.Max(1e-20, mid));
    }

    /// <summary>
    /// The headline: on the run-out of a mono pressing the full chain removes the great majority of
    /// the crackle, where the de-crackler alone removes almost none of it.
    /// </summary>
    [Fact]
    public void CollapsingTheSideFirstIsWhatMakesTheDeCracklerWork()
    {
        if (!Enabled) return;
        var transfers = Transfers();
        if (transfers.Count == 0) return;

        int measured = 0;
        foreach (Transfer transfer in transfers)
        {
            int length = transfer.Channels[0].Length;
            float[][] runOut = Slice(transfer.Channels, length - Rate * 4, Rate * 4);
            if (SideToMidDb(Slice(transfer.Channels, length / 3, Rate * 4))
                > RestorationRecommendations.MonoPressingSideToMidDb) continue;  // not a mono pressing

            int before = Ticks(runOut[0]);
            if (before < 100) continue;                 // too clean to say anything about
            measured++;

            float[][] crackleOnly = Slice(runOut, 0, runOut[0].Length);
            Restoration.RemoveSubsonic(crackleOnly, Rate, 30);
            Decrackle(crackleOnly);
            int afterCrackleOnly = Ticks(crackleOnly[0]);

            float[][] chain = Slice(runOut, 0, runOut[0].Length);
            Restoration.RemoveSubsonic(chain, Rate, 30);
            Restoration.ScaleSide(chain, 0);
            Decrackle(chain);
            int afterChain = Ticks(chain[0]);

            output.WriteLine($"{transfer.Name,-34} {before,4} ticks -> " +
                             $"{afterCrackleOnly,4} de-crackle alone -> {afterChain,4} full chain " +
                             $"({Rms(runOut[0]):0.0} -> {Rms(chain[0]):0.0} dBFS)");

            // Bounds set from what the shipped code measures, with room, rather than from the
            // exploration that led here: 318 -> 263 -> 54 on `One More Chance`. The de-crackler
            // alone removes 17% of the ticks and the chain removes 83%, and the second assertion
            // is the ordering claim - it fails if someone moves the side collapse after the
            // repairers, which loses the effect entirely while still looking like a chain.
            Assert.True(afterChain <= before * 0.25,
                $"{transfer.Name}: the chain left {afterChain} of {before} ticks");
            Assert.True(afterCrackleOnly >= before * 0.6,
                $"{transfer.Name}: de-crackle alone removed more than expected " +
                $"({afterCrackleOnly} of {before}) - the ordering claim may no longer hold");
            Assert.True(afterChain < afterCrackleOnly * 0.5,
                $"{transfer.Name}: collapsing the side first bought little " +
                $"({afterChain} against {afterCrackleOnly})");
        }

        // Stated rather than asserted: only pressings cut mono with a dirty enough run-out are
        // measurable this way, and in the collection this was written against that is one file.
        // A claim resting on one recording is worth reading as such.
        output.WriteLine($"{measured} mono pressing(s) with a measurable run-out");
    }

    /// <summary>
    /// What the chain costs where the music is. This is the guard against someone later
    /// "improving" the sensitivity: below three deviations the de-crackler repairs twice as many
    /// samples and takes twice as much top end for a worse result at the quiet end.
    /// </summary>
    [Fact]
    public void TheChainsCostOnTheMusicIsBounded()
    {
        if (!Enabled) return;
        var transfers = Transfers();
        if (transfers.Count == 0) return;

        foreach (Transfer transfer in transfers)
        {
            int length = transfer.Channels[0].Length;
            float[][] music = Slice(transfer.Channels, length / 3, Rate * 4);
            bool monoPressing = SideToMidDb(music) <= RestorationRecommendations.MonoPressingSideToMidDb;

            float[][] chain = Slice(music, 0, music[0].Length);
            Restoration.RemoveSubsonic(chain, Rate, 30);
            if (monoPressing) Restoration.ScaleSide(chain, 0);
            double reference = HighFrequencyDb(chain[0]);
            Decrackle(chain);
            double cost = reference - HighFrequencyDb(chain[0]);

            output.WriteLine($"{transfer.Name,-34} {(monoPressing ? "mono " : "stereo")} " +
                             $"high-frequency cost {cost:0.00} dB");

            Assert.True(cost < 1.5,
                $"{transfer.Name}: de-crackling cost {cost:0.00} dB of high frequencies");
        }
    }

    /// <summary>
    /// The whole path a user actually takes: analyse the file, and read back what the three new
    /// cards would say. Nothing is asserted about the numbers themselves beyond their being
    /// coherent - this is the end-to-end check that the measurements reach the controls at all,
    /// which is the step that would otherwise only ever be done by eye.
    /// </summary>
    [Fact]
    public void TheAnalysisReachesTheThreeNewControls()
    {
        if (!Enabled) return;
        var transfers = Transfers();
        if (transfers.Count == 0) return;

        foreach (Transfer transfer in transfers)
        {
            CleanupAnalysisResult cleanup = CleanupAnalyzer.Analyze(
                transfer.Channels, Rate, CleanupProfile.VinylCleanup);
            ClickAnalysisResult clicks = Restoration.AnalyzeClicks(transfer.Channels, Rate,
                new ClickAnalysisOptions
                {
                    Sensitivity = RestorationRecommendations.ExploratoryClickSensitivity,
                    PreserveTransients = true,
                });
            ClippingAnalysisResult clipping = Restoration.AnalyzeClipping(transfer.Channels, Rate,
                new ClippingAnalysisOptions());

            RestorationRecommendations.Settings recommended =
                RestorationRecommendations.Create(clicks, clipping, cleanup);

            output.WriteLine($"{transfer.Name}");
            output.WriteLine($"   side-to-mid {cleanup.SideToMidDb,6:0.0} dB -> side level " +
                             $"{recommended.SideLevel,5:P0}");
            output.WriteLine($"   high-pass {(recommended.HighPass ? $"on at {recommended.HighPassCutoffHz:0} Hz" : "bypassed"),-16}" +
                             $" de-crackle {(recommended.Decrackle ? $"on at {recommended.DecrackleThreshold:0.0}" : "off")}" +
                             $" ({clicks.Events.Count:N0} impulses)");
            output.WriteLine($"   {RestorationWorkbenchDialogSideLine(cleanup.SideToMidDb, recommended.SideLevel)}");

            Assert.InRange(recommended.SideLevel, 0.0, 1.0);
            Assert.InRange(recommended.HighPassCutoffHz, 20.0, 60.0);
            Assert.True(recommended.DecrackleThreshold >= 3.0);
        }
    }

    private static string RestorationWorkbenchDialogSideLine(double sideToMidDb, double level) =>
        WaveLab.Views.RestorationWorkbenchDialog.DescribeSideLevel(
            level < 1.0, analysed: true, stereo: true, sideToMidDb, level);

    /// <summary>The harness must not run by accident; the corpora are not redistributable.</summary>
    [Fact]
    public void TheHarnessIsOffUnlessItIsAskedFor()
    {
        if (Environment.GetEnvironmentVariable("WAVELAB_CORPUS") is null) Assert.False(Enabled);
        output.WriteLine($"vertical-noise corpus enabled: {Enabled}");
    }
}
