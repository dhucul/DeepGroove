using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Cuts the listening set for the vertical-noise chain into <c>listening/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <c>WAVELAB_LISTENING=1</c>, and it needs corpus 1 present. It writes files rather
/// than asserting anything, because the claim it supports is the one no measurement here can make:
/// the numbers say 318 ticks became 54 and that it cost 0.42 dB of high frequencies, and whether
/// that is a restoration or a smearing is a question for ears.
/// </para>
/// <para>
/// The excerpt runs from the end of the music into the run-out on purpose. A run-out alone flatters
/// any de-noiser — there is nothing there to damage — and a music-only excerpt hides the thing being
/// removed. The boundary is where both questions are asked at once.
/// </para>
/// </remarks>
public sealed class VerticalNoiseListeningSet(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("WAVELAB_LISTENING") is { Length: > 0 } &&
        Environment.GetEnvironmentVariable("WAVELAB_CORPUS") is { Length: > 0 };

    private static string SourcePath =>
        Environment.GetEnvironmentVariable("WAVELAB_LISTENING_SOURCE") is { Length: > 0 } set
            ? set
            : @"C:\Users\dhucu\Music\mymusic\One More Chance.wav";

    private static string OutputDirectory =>
        Environment.GetEnvironmentVariable("WAVELAB_LISTENING_OUT") is { Length: > 0 } set
            ? set
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "listening");

    [Fact]
    public void CutTheListeningSet()
    {
        if (!Enabled)
        {
            output.WriteLine("set WAVELAB_LISTENING=1 and WAVELAB_CORPUS=1 to cut the files");
            return;
        }
        if (!File.Exists(SourcePath))
        {
            output.WriteLine($"no source at {SourcePath}");
            return;
        }

        AudioDocument source = AudioImporter.Load(SourcePath);
        int length = source.Length;

        // The last 25 seconds: the tail of the music, the fade, and the run-out behind it.
        int count = Math.Min(Rate * 25, length);
        int start = length - count;

        float[][] dry = Slice(source, start, count);
        float[][] crackleOnly = Slice(source, start, count);
        float[][] chain = Slice(source, start, count);

        // What the tool does today, reached from the Restore menu.
        foreach (float[] channel in crackleOnly) Decrackle.Process(channel, DecrackleOptions.Default);

        // What the workbench chain does now, in its own order.
        Restoration.RemoveSubsonic(chain, Rate, 30);
        Restoration.ScaleSide(chain, 0);
        foreach (float[] channel in chain) Decrackle.Process(channel, DecrackleOptions.Default);

        // The residual is the claim itself: everything the chain decided was not music.
        var residual = new float[dry.Length][];
        for (int c = 0; c < dry.Length; c++)
        {
            residual[c] = new float[count];
            for (int i = 0; i < count; i++) residual[c][i] = dry[c][i] - chain[c][i];
        }

        // The three programme files share a level so none of them wins by being louder, and the
        // level matched is <b>integrated loudness</b> rather than programme RMS. Matching RMS left
        // them 1.0 LU apart: collapsing the side changes how the channels sum under BS.1770, which
        // a per-channel RMS cannot see. The residual keeps its own level, because what matters
        // about it is how loud it actually is.
        double reference = Loudness(dry, source.SampleRate).Lufs;
        Normalize(crackleOnly, reference - Loudness(crackleOnly, source.SampleRate).Lufs);
        Normalize(chain, reference - Loudness(chain, source.SampleRate).Lufs);

        string directory = Path.GetFullPath(OutputDirectory);
        Directory.CreateDirectory(directory);

        Write(directory, "05-vertical-dry.wav", dry, source.SampleRate);
        Write(directory, "05-vertical-decrackle-only.wav", crackleOnly, source.SampleRate);
        Write(directory, "05-vertical-chain.wav", chain, source.SampleRate);
        Write(directory, "05-vertical-residual.wav", residual, source.SampleRate);

        output.WriteLine($"cut from {Path.GetFileName(SourcePath)} at {start / (double)Rate:0.0} s, " +
                         $"{count / (double)Rate:0.0} s, into {directory}");
        foreach (var (name, data) in new[]
                 {
                     ("dry", dry), ("de-crackle only", crackleOnly),
                     ("full chain", chain), ("residual", residual),
                 })
        {
            var (lufs, truePeak) = Loudness(data, source.SampleRate);
            output.WriteLine($"| `05-vertical-{Slug(name)}.wav` | {name} | {PeakDb(data[0]):0.0} | " +
                             $"{truePeak:0.0} | {lufs:0.0} | ticks in the run-out: **{RunOutTicks(data[0])}** |");
        }
    }

    private static float[][] Slice(AudioDocument document, int start, int count)
    {
        var copy = new float[document.Channels.Count][];
        for (int c = 0; c < copy.Length; c++)
        {
            copy[c] = new float[count];
            Array.Copy(document.Channels[c], start, copy[c], 0, count);
        }
        return copy;
    }

    /// <summary>The louder half of the excerpt, which is the music rather than the run-out.</summary>
    private static double ProgrammeDb(float[] channel)
    {
        const int block = Rate / 10;
        var levels = new List<double>();
        for (int start = 0; start + block <= channel.Length; start += block)
        {
            double power = 0;
            for (int i = start; i < start + block; i++) power += (double)channel[i] * channel[i];
            levels.Add(power / block);
        }
        if (levels.Count == 0) return RmsDb(channel);
        levels.Sort();
        double sum = 0;
        int taken = 0;
        for (int i = levels.Count / 2; i < levels.Count; i++) { sum += levels[i]; taken++; }
        return 10 * Math.Log10(sum / Math.Max(1, taken) + 1e-20);
    }

    private static void Normalize(float[][] channels, double gainDb)
    {
        float gain = (float)Math.Pow(10, gainDb / 20);
        foreach (float[] channel in channels)
            for (int i = 0; i < channel.Length; i++) channel[i] *= gain;
    }

    private static double RmsDb(float[] channel)
    {
        double sum = 0;
        foreach (float value in channel) sum += (double)value * value;
        return 20 * Math.Log10(Math.Sqrt(sum / Math.Max(1, channel.Length)) + 1e-12);
    }

    private static double PeakDb(float[] channel)
    {
        double peak = 0;
        foreach (float value in channel) peak = Math.Max(peak, Math.Abs(value));
        return 20 * Math.Log10(peak + 1e-12);
    }

    /// <summary>
    /// Ticks above −45 dBFS in the last five seconds, which is the run-out. The number that matters
    /// for this group, and the same metric <c>VerticalNoiseCorpusTests</c> asserts on.
    /// </summary>
    private static int RunOutTicks(float[] channel)
    {
        int count = Math.Min(Rate * 5, channel.Length);
        var window = new float[count];
        Array.Copy(channel, channel.Length - count, window, 0, count);
        var work = new[] { window };
        Restoration.RemoveSubsonic(work, Rate, 120);

        const double level = 0.005_623_413;          // −45 dBFS
        int ticks = 0;
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(work[0][i]) <= level) continue;
            ticks++;
            while (i < count && Math.Abs(work[0][i]) > level * 0.4) i++;
            i += 8;
        }
        return ticks;
    }

    private static string Slug(string name) => name switch
    {
        "de-crackle only" => "decrackle-only",
        "full chain" => "chain",
        _ => name,
    };

    private static (double Lufs, double TruePeak) Loudness(float[][] channels, int sampleRate)
    {
        var meter = new LoudnessMeter();
        meter.Configure(sampleRate, channels.Length);
        int frames = channels[0].Length;
        const int block = 16384;
        var interleaved = new float[block * channels.Length];
        for (int start = 0; start < frames; start += block)
        {
            int count = Math.Min(block, frames - start);
            for (int frame = 0; frame < count; frame++)
                for (int c = 0; c < channels.Length; c++)
                    interleaved[frame * channels.Length + c] = channels[c][start + frame];
            meter.Process(interleaved, 0, count * channels.Length);
        }
        meter.FlushTruePeak();
        return (meter.IntegratedLufs, meter.TruePeakDb);
    }

    private static void Write(string directory, string name, float[][] channels, int sampleRate)
    {
        var document = new AudioDocument(channels, sampleRate, 24);
        WavCodec.Save(document, Path.Combine(directory, name), 24, dither: false);
    }
}
