using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// How much room there is above the spectral gate that ships — the ceiling any better mask
/// estimator could reach, machine-learned or otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers is whether an ML denoiser is worth building at all</b>, and it
/// answers it without a model, a runtime, a download or a training set. Every single-channel
/// denoiser in the class being proposed — RNNoise, DTLN, the DNS-Challenge models, and the
/// Ephraim-Malah MMSE-STSA this repo deleted two commits ago — does the same thing: estimate a
/// per-bin gain to apply to the noisy magnitude spectrum. They differ only in how well they
/// estimate it.
/// </para>
/// <para>
/// So compute the gain <b>exactly</b>, from the clean signal and the noise the harness planted, and
/// apply it in the same STFT the shipped gate runs in. That is the Wiener mask, and no estimator of
/// any kind can beat it in this framework: it is what a perfect estimator would produce. The gap
/// between it and the shipped gate is the <b>entire headroom available</b>. If the gap is small,
/// no model justifies a native dependency and days of training, however good the model is.
/// </para>
/// <para>
/// The ceiling is a real ceiling and not a perfect reconstruction, which is the honest framing: a
/// magnitude mask leaves the noisy <i>phase</i> alone, so even the oracle falls short of the clean
/// signal. That is a property of the whole method class rather than of any one model, which is
/// exactly why it belongs in the ceiling.
/// </para>
/// </remarks>
public sealed class NoiseReductionCeilingTests(ITestOutputHelper output)
{
    /// <summary>
    /// Applies the oracle Wiener mask: the gain a perfect estimator would produce for every bin.
    /// </summary>
    /// <remarks>
    /// Three passes through the same STFT the gate uses, so the comparison is like for like. The
    /// clean and noise spectra are captured per frame first, then the noisy signal is processed with
    /// the mask those two imply. <c>damaged = clean + noise</c> exactly, by construction in
    /// <c>PlantHiss</c>, and every pass runs the same length at the same configuration, so frame
    /// <c>n</c> means the same instant in all three.
    /// </remarks>
    private static float[] OracleWiener(float[] clean, float[] noise, float[] damaged)
    {
        int bins = Restoration.NrFftSize / 2 + 1;
        float[] window = Fft.HannWindow(Restoration.NrFftSize);

        static Dictionary<int, double[]> Capture(float[] signal, int bins, float[] window)
        {
            var power = new Dictionary<int, double[]>();
            var stft = new Stft(Restoration.NrFftSize, Restoration.NrFftSize / 4, window, window,
                StftLeadIn.None, StftNormalization.RunningSum);
            var scratch = new float[signal.Length];
            stft.Process(signal, scratch, (frame, _, re, im) =>
            {
                var row = new double[bins];
                for (int b = 0; b < bins; b++) row[b] = (double)re[b] * re[b] + (double)im[b] * im[b];
                power[frame] = row;
            });
            return power;
        }

        Dictionary<int, double[]> cleanPower = Capture(clean, bins, window);
        Dictionary<int, double[]> noisePower = Capture(noise, bins, window);

        var result = (float[])damaged.Clone();
        var apply = new Stft(Restoration.NrFftSize, Restoration.NrFftSize / 4, window, window,
            StftLeadIn.None, StftNormalization.RunningSum);
        apply.Process(result, result, (frame, _, re, im) =>
        {
            if (!cleanPower.TryGetValue(frame, out double[]? s) ||
                !noisePower.TryGetValue(frame, out double[]? n)) return;
            for (int b = 0; b < bins; b++)
            {
                double total = s[b] + n[b];
                float gain = total <= 0 ? 1f : (float)(s[b] / total);
                re[b] *= gain;
                im[b] *= gain;
            }
        });
        return result;
    }

    /// <summary>
    /// The oracle mask against the shipped gate, over the noise corpus, by hiss severity.
    /// </summary>
    [Fact]
    public void HowMuchHeadroomIsThereAboveTheSpectralGate()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        const double ReductionDb = 10.0, SensitivityDb = 5.0;

        var rows = RestorationCorpus.MeasureNoise(cell =>
        {
            var noise = new float[cell.Damaged.Length];
            for (int i = 0; i < noise.Length; i++) noise[i] = cell.Damaged[i] - cell.Clean[i];

            float[][] gate = [(float[])cell.Damaged.Clone()];
            Restoration.ReduceNoise(gate, cell.Profile, ReductionDb, SensitivityDb);

            float[] oracle = OracleWiener(cell.Clean, noise, cell.Damaged);

            double raw = RestorationCorpus.SegmentalSnrDb(cell.Clean, cell.Damaged, cell.SampleRate);
            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.SnrDb,
                Gate: RestorationCorpus.SegmentalSnrDb(cell.Clean, gate[0], cell.SampleRate) - raw,
                Oracle: RestorationCorpus.SegmentalSnrDb(cell.Clean, oracle, cell.SampleRate) - raw);
        });

        if (rows.Count == 0) { output.WriteLine("no corpus recordings found"); return; }

        output.WriteLine($"headroom above the spectral gate: {rows.Count} cells");
        output.WriteLine($"{"severity",-10}{"gate",10}{"oracle",10}{"headroom",10}");
        foreach (var group in rows.GroupBy(r => r.SnrDb).OrderByDescending(g => g.Key))
            output.WriteLine($"{group.Key + " dB down",-10}" +
                $"{group.Average(r => r.Gate),10:+0.00;-0.00}" +
                $"{group.Average(r => r.Oracle),10:+0.00;-0.00}" +
                $"{group.Average(r => r.Oracle - r.Gate),10:+0.00;-0.00}");

        double gateMean = rows.Average(r => r.Gate), oracleMean = rows.Average(r => r.Oracle);
        output.WriteLine($"{"ALL",-10}{gateMean,10:+0.00;-0.00}{oracleMean,10:+0.00;-0.00}" +
            $"{oracleMean - gateMean,10:+0.00;-0.00}");
        output.WriteLine($"the oracle beats the gate in {rows.Count(r => r.Oracle > r.Gate)} of {rows.Count} cells");

        // Headroom over the gate flatters any replacement, because the gate scores *below
        // do-nothing* on quiet hiss - a fixed 10 dB reduction applied to noise already 30 dB under
        // the programme costs more music than it saves noise, which is documented and not a defect.
        // A rule that simply declined to fire there would capture that part for free, so the honest
        // target is the better of the two, and what is left is headroom only a better estimator can
        // reach.
        double honest = rows.Average(r => r.Oracle - Math.Max(r.Gate, 0));
        output.WriteLine($"against the better of the gate and doing nothing, the headroom is " +
            $"{honest:+0.00;-0.00} dB");

        // The oracle is what a perfect estimator produces. If it does not beat the shipped gate,
        // something is wrong with the measurement rather than with the gate.
        Assert.True(oracleMean > gateMean,
            $"the oracle mask ({oracleMean:F2} dB) did not beat the gate ({gateMean:F2} dB), " +
            "which means the harness is measuring the wrong thing");
    }

    /// <summary>
    /// The oracle mask must be a genuine ceiling, so it is calibrated with no corpus needed.
    /// </summary>
    /// <remarks>
    /// Two claims, both cheap to get wrong. Given noise it can see perfectly the mask must remove
    /// most of it — otherwise it is not an oracle and the headroom figure is meaningless. And on a
    /// clean signal it must do essentially nothing, because a Wiener gain with no noise in the
    /// denominator is 1: an oracle that damages clean audio would understate the ceiling.
    /// </remarks>
    [Fact]
    public void TheOracleMaskIsActuallyAnOracle()
    {
        const int rate = 44_100, seconds = 4;
        var clean = new float[rate * seconds];
        for (int i = 0; i < clean.Length; i++)
            clean[i] = (float)(0.35 * Math.Sin(2 * Math.PI * 440 * i / (double)rate)
                             + 0.15 * Math.Sin(2 * Math.PI * 1970 * i / (double)rate));

        var (_, damaged) = RestorationCorpus.PlantHiss(clean, 12.0, seed: 4);
        var noise = new float[clean.Length];
        for (int i = 0; i < noise.Length; i++) noise[i] = damaged[i] - clean[i];

        double raw = RestorationCorpus.SegmentalSnrDb(clean, damaged, rate);
        double oracle = RestorationCorpus.SegmentalSnrDb(clean, OracleWiener(clean, noise, damaged), rate);
        output.WriteLine($"planted at 12 dB down: raw {raw:F2} dB, oracle {oracle:F2} dB, " +
            $"gain {oracle - raw:+0.00;-0.00} dB");

        Assert.True(oracle - raw > 10, $"the oracle only gained {oracle - raw:F2} dB on noise it can see perfectly");

        // Nothing to remove, so nothing may be removed - but "nothing" is bounded by what a bare
        // pass through the same overlap-add costs, which is measured here rather than assumed. The
        // round trip is the floor under both the oracle and the gate, so it cancels out of the
        // headroom figure; it is reported because a ceiling quoted without it invites the reading
        // that the oracle is near-perfect reconstruction, which it is not.
        var silence = new float[clean.Length];
        double untouched = RestorationCorpus.SegmentalSnrDb(clean, OracleWiener(clean, silence, clean), rate);

        var bare = (float[])clean.Clone();
        new Stft(Restoration.NrFftSize, Restoration.NrFftSize / 4,
            Fft.HannWindow(Restoration.NrFftSize), Fft.HannWindow(Restoration.NrFftSize),
            StftLeadIn.None, StftNormalization.RunningSum).Process(bare, bare, null);
        double roundTrip = RestorationCorpus.SegmentalSnrDb(clean, bare, rate);

        output.WriteLine($"on clean audio the oracle scores {untouched:F1} dB, a bare STFT round trip " +
            $"scores {roundTrip:F1} dB - the oracle costs {roundTrip - untouched:F2} dB beyond it");
        Assert.True(untouched > roundTrip - 1.0,
            $"the oracle damaged clean audio: {untouched:F1} dB against a {roundTrip:F1} dB round trip");
    }
}
