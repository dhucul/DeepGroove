using WaveLab.Audio;
using WaveLab.Audio.Dsp;

namespace WaveLab.Tests;

/// <summary>One click lifted off a real transfer, with the music underneath it removed.</summary>
/// <param name="Source">Which recording it came from.</param>
/// <param name="Position">Where in that recording, for tracing it back.</param>
/// <param name="Shape">The defect alone, normalised so its largest excursion is 1.</param>
/// <param name="Sharpness">
/// Mean absolute second difference over the shape, divided by its peak. High is a spike, low is a
/// slower thump; kept so a planted set can be checked for variety rather than assumed to have it.
/// </param>
public sealed record RealClick(string Source, int Position, float[] Shape, double Sharpness)
{
    public int Length => Shape.Length;
}

/// <summary>
/// Clicks taken off real shellac transfers, for planting into clean audio at known positions.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for, and what it is not.</b> <see cref="ClickCorpus"/> plants damped impulses of
/// my own invention, and a detector measured only against them is measured against a guess about
/// what a stylus does. These are real defects, so the shape is not a guess — but they are planted
/// into clean material, so their positions are still known exactly and the reference is still
/// clean. What it cannot do is settle whether the detector's rate on a real transfer is right:
/// that needs a recording whose real clicks are marked, and marking them needs ears.
/// </para>
/// <para>
/// <b>The music is removed rather than assumed away.</b> A click sits on top of whatever the record
/// was playing, so lifting the raw samples would carry a scrap of music with it and plant that
/// scrap into unrelated audio. <see cref="Janssen"/> reconstructs what the waveform was doing
/// across the span from the clean audio either side, and the defect is the difference between what
/// is there and what should have been. That is the same model the repair uses, which is worth being
/// explicit about: the shapes are what the repair would have removed, not an independent account of
/// what a stylus does.
/// </para>
/// <para>
/// <b>Only unambiguous ones are taken.</b> A candidate must be a large outlier in a high-order
/// prediction residual, isolated from any other outlier, short, and sitting in a passage quiet
/// enough that it dominates. That deliberately selects the easy, obvious clicks — which is a bias,
/// and it is the safe direction: a library of borderline events would be a library of maybes.
/// </para>
/// </remarks>
public static class RealClickLibrary
{
    private const int Order = 32;
    private const int Block = 4096;
    private const double OutlierSigma = 15.0;
    private const int IsolationSamples = 256;
    private const int MaximumSpan = 40;

    /// <summary>Extracts clicks from one recording.</summary>
    public static List<RealClick> Extract(string name, float[] samples, int limit = 40)
    {
        var found = new List<RealClick>();
        if (samples.Length < Block * 2) return found;

        var residual = new double[Block];
        var magnitude = new double[Block];
        var coefficients = new double[Order + 1];
        var autocorrelation = new double[Order + 1];

        for (int start = 0; start + Block <= samples.Length && found.Count < limit; start += Block)
        {
            if (!Decrackle.FitPredictor(samples, start, Block, Order, autocorrelation, coefficients))
                continue;

            double squareSum = 0;
            for (int i = 0; i < Block; i++)
            {
                double predicted = 0;
                for (int k = 1; k <= Order; k++)
                {
                    int index = start + i - k;
                    if (index >= 0) predicted += coefficients[k] * samples[index];
                }
                residual[i] = samples[start + i] - predicted;
                magnitude[i] = Math.Abs(residual[i]);
                squareSum += samples[start + i] * (double)samples[start + i];
            }

            double scale = Decrackle.RobustScale(magnitude);
            if (scale <= 1e-9) continue;
            double limitValue = scale * OutlierSigma;
            double blockRms = Math.Sqrt(squareSum / Block);

            for (int i = IsolationSamples; i < Block - IsolationSamples && found.Count < limit; i++)
            {
                if (magnitude[i] <= limitValue) continue;

                int spanStart = i, spanEnd = i + 1;
                while (spanEnd < Block && magnitude[spanEnd] > scale * 3 && spanEnd - spanStart < MaximumSpan)
                    spanEnd++;
                int span = spanEnd - spanStart;

                // Isolated: nothing else of consequence within a quarter of a millisecond either
                // side, so the extracted shape is one defect rather than a burst of them.
                bool isolated = true;
                for (int j = spanStart - IsolationSamples; j < spanEnd + IsolationSamples && isolated; j++)
                {
                    if (j >= spanStart && j < spanEnd) continue;
                    if ((uint)j < (uint)Block && magnitude[j] > scale * 4) isolated = false;
                }
                if (!isolated) { i = spanEnd; continue; }

                int absoluteStart = start + spanStart, absoluteEnd = start + spanEnd;
                var options = JanssenOptions.For(span, outputLimit: 4.0);
                if (!Janssen.TryInterpolate(samples, absoluteStart, absoluteEnd, options,
                        out double[] reconstruction))
                {
                    i = spanEnd;
                    continue;
                }

                var shape = new float[span];
                double peak = 0;
                for (int j = 0; j < span; j++)
                {
                    shape[j] = (float)(samples[absoluteStart + j] - reconstruction[j]);
                    peak = Math.Max(peak, Math.Abs(shape[j]));
                }
                // It has to stand well clear of the passage it came from, or the "defect" is
                // largely the interpolator disagreeing with the music.
                if (peak < blockRms * 2 || peak <= 1e-6) { i = spanEnd; continue; }

                double roughness = 0;
                for (int j = 1; j < span - 1; j++)
                    roughness += Math.Abs(shape[j] - 0.5 * (shape[j - 1] + shape[j + 1]));
                for (int j = 0; j < span; j++) shape[j] = (float)(shape[j] / peak);

                found.Add(new RealClick(name, absoluteStart, shape,
                    span > 2 ? roughness / ((span - 2) * peak) : 1.0));
                i = spanEnd + IsolationSamples;
            }
        }
        return found;
    }

    /// <summary>Builds the library from whichever shellac transfers are present.</summary>
    public static List<RealClick> Build(int perRecording = 40)
    {
        var library = new List<RealClick>();
        foreach (var recording in DeclipCorpus.Recordings().Where(r => r.Corpus == "3"))
        {
            AudioDocument document;
            try { document = AudioImporter.Load(recording.Path); }
            catch { continue; }
            if (document.Channels.Count == 0) continue;
            library.AddRange(Extract(recording.ShortName, document.Channels[0], perRecording));
        }
        return library;
    }

    /// <summary>
    /// Plants library clicks into clean audio at a chosen level above the local signal, and reports
    /// exactly where they went.
    /// </summary>
    public static (float[] Clean, float[] Damaged, bool[] Hit, int Count) Plant(
        float[] source, int sampleRate, IReadOnlyList<RealClick> library,
        double aboveLocalDb, int seed)
    {
        var clean = (float[])source.Clone();
        var damaged = (float[])source.Clone();
        var hit = new bool[source.Length];
        if (library.Count == 0) return (clean, damaged, hit, 0);

        int half = Math.Max(1, sampleRate * ClickCorpus.LocalWindowMs / 2000);
        double overall = 0;
        for (int i = 0; i < source.Length; i++) overall += (double)source[i] * source[i];
        overall = Math.Sqrt(overall / Math.Max(1, source.Length));
        double floor = overall * 0.05;
        double gain = Math.Pow(10.0, aboveLocalDb / 20.0);

        var random = new Random(seed);
        int spacing = Math.Max(1, (int)(sampleRate / ClickCorpus.ClicksPerSecond));
        int count = 0;

        for (int centre = spacing; centre < source.Length - spacing; centre += spacing)
        {
            int at = centre + random.Next(-spacing / 3, spacing / 3);
            var click = library[random.Next(library.Count)];
            if (at < 32 || at + click.Length >= source.Length - 32) continue;

            double local = ClickCorpus.LocalRmsAt(source, at, half);
            if (local < floor) continue;

            double amplitude = local * gain * (0.6 + random.NextDouble() * 0.8);
            for (int j = 0; j < click.Length; j++)
            {
                damaged[at + j] = (float)Math.Clamp(damaged[at + j] + click.Shape[j] * amplitude, -4.0, 4.0);
                hit[at + j] = true;
            }
            count++;
        }
        return (clean, damaged, hit, count);
    }
}
