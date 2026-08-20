using System.IO;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// AUTO DETECT against programme material, which is the only condition it is ever used in.
/// </summary>
/// <remarks>
/// <para>
/// Nothing tested this. The detector's own tests fed it hum with nothing over it, and
/// <see cref="CoefficientPublishingTests"/> turns AUTO DETECT off explicitly — so a gate that fired
/// only when the hum was a large share of the signal passed everything while doing nothing on a
/// real transfer. Preparing a listening pass is what found it: hum planted at −40 dBFS under this
/// track came out **1.2 dB quieter during playback and unchanged on render**, with the readout
/// reporting 50.5 Hz and 60.0 Hz for the same settings.
/// </para>
/// <para>
/// So these run on <c>demo_track.wav</c> — the repository's own recording, the same one
/// <see cref="RealAudioDeclipTests"/> uses — with hum planted at a known level, and they run at
/// several block sizes because the block size is the other half of what was wrong: the detector
/// measured over whatever the caller brought, and the two callers bring 60 ms and 1.37 s.
/// </para>
/// </remarks>
public sealed class HumAutoDetectTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    /// <summary>The block the playback engine asks for at the default 60 ms buffer.</summary>
    private const int PlaybackBlock = 2_880;

    /// <summary>The block <c>MasterSection.ProcessOffline</c> uses.</summary>
    private const int RenderBlock = 65_536;

    public static TheoryData<int> BlockSizes() => [240, 512, PlaybackBlock, 24_000, RenderBlock];

    private static AudioDocument Track()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "demo_track.wav"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return AudioImporter.Load(Path.Combine(dir!.FullName, "demo_track.wav"));
    }

    /// <summary>The track with a mains comb — fundamental and three partials — laid over it.</summary>
    private static float[][] WithHum(AudioDocument track, double fundamental, double amplitude = 0.010)
    {
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[track.Length];
            for (int f = 0; f < track.Length; f++)
            {
                double t = f / (double)Rate, hum = 0;
                for (int harmonic = 1; harmonic <= 4; harmonic++)
                    hum += Math.Sin(2 * Math.PI * fundamental * harmonic * t) * amplitude / harmonic;
                data[c][f] = (float)(track.Channels[c][f] + hum);
            }
        }
        return data;
    }

    /// <summary>Level of one steady tone, by correlation over a whole number of its periods.</summary>
    private static double ToneDb(float[] channel, double hz)
    {
        int frames = (int)(Math.Floor(channel.Length * hz / Rate) * Rate / hz);
        double re = 0, im = 0;
        for (int f = 0; f < frames; f++)
        {
            double phase = 2 * Math.PI * hz * f / Rate;
            re += channel[f] * Math.Cos(phase);
            im += channel[f] * Math.Sin(phase);
        }
        return 20 * Math.Log10(Math.Max(2 * Math.Sqrt(re * re + im * im) / frames, 1e-12));
    }

    private static (string Readout, double Removed) Run(float[][] input, double mains, int blockFrames, double humHz)
    {
        var data = new float[input.Length][];
        for (int c = 0; c < input.Length; c++) data[c] = (float[])input[c].Clone();

        float[] interleaved = new float[data[0].Length * data.Length];
        for (int f = 0; f < data[0].Length; f++)
            for (int c = 0; c < data.Length; c++)
                interleaved[f * data.Length + c] = data[c][f];

        var effect = new HumRemovalEffect();
        effect.SetParam("frequency", mains);
        effect.SetParam("autoDetect", 1);
        effect.SetParam("amount", 1);
        effect.Configure(Rate, data.Length);

        int step = blockFrames * data.Length;
        for (int i = 0; i < interleaved.Length; i += step)
            effect.Process(interleaved, i, Math.Min(step, interleaved.Length - i));

        var processed = new float[data.Length][];
        for (int c = 0; c < data.Length; c++) processed[c] = new float[data[0].Length];
        for (int f = 0; f < data[0].Length; f++)
            for (int c = 0; c < data.Length; c++)
                processed[c][f] = interleaved[f * data.Length + c];

        return (effect.Readout ?? "", ToneDb(input[0], humHz) - ToneDb(processed[0], humHz));
    }

    /// <summary>
    /// The failure this was written for: a 50 Hz transfer, the control left at its 60 Hz default,
    /// and music over the hum. It has to reach 50 Hz whatever size the caller's blocks are.
    /// </summary>
    [Theory]
    [MemberData(nameof(BlockSizes))]
    public void ItFindsAFiftyHertzHumUnderProgrammeWhateverTheBlockSize(int blockFrames)
    {
        float[][] hummed = WithHum(Track(), 50);

        (string readout, double removed) = Run(hummed, mains: 60, blockFrames, humHz: 50);
        output.WriteLine($"block {blockFrames}: removed {removed:0.0} dB, readout {readout}");

        Assert.Contains("50.0 Hz", readout);
        Assert.True(removed > 15, $"only {removed:0.0} dB of hum was removed at a block of {blockFrames}.");
    }

    /// <summary>
    /// The two callers must agree. They did not: 60 ms blocks reported 50.5 Hz and the offline
    /// block reported 60.0, so a preview and the render it promised were different processors.
    /// </summary>
    [Fact]
    public void PlaybackAndOfflineRenderReachTheSameAnswer()
    {
        float[][] hummed = WithHum(Track(), 50);

        (string playback, _) = Run(hummed, mains: 60, PlaybackBlock, humHz: 50);
        (string render, _) = Run(hummed, mains: 60, RenderBlock, humHz: 50);

        Assert.Equal(playback, render);
    }

    /// <summary>
    /// A 60 Hz transfer with the control left on 50 is the same fault mirrored, and it is the one
    /// that catches a detector that merely drifts toward whatever it is told.
    /// </summary>
    [Fact]
    public void ItIsNotDraggedTowardsTheManualSetting()
    {
        float[][] hummed = WithHum(Track(), 60);

        (string readout, double removed) = Run(hummed, mains: 50, PlaybackBlock, humHz: 60);

        Assert.Contains("60.0 Hz", readout);
        Assert.True(removed > 15, $"only {removed:0.0} dB was removed.");
    }

    /// <summary>
    /// With no hum there is nothing to find, and the manual setting stands. A detector that locks
    /// onto programme material would notch a record that never had any hum on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BlockSizes))]
    public void ItInventsNoHumOnACleanRecording(int blockFrames)
    {
        AudioDocument track = Track();
        float[][] clean = [.. track.Channels.Select(channel => (float[])channel.Clone())];

        (string readout, _) = Run(clean, mains: 60, blockFrames, humHz: 50);

        Assert.Contains("60.0 Hz", readout);
    }

    /// <summary>
    /// The answer is one of the two mains frequencies and never a blend of them.
    /// </summary>
    /// <remarks>
    /// Smoothing a frequency toward whichever candidate won each block put the notch bank at
    /// 54.4 Hz — a frequency no mains supply runs at, and between the only two it was choosing
    /// from, so the notches sat where neither the hum nor anything else was. Confidence is
    /// smoothed; the answer is voted.
    /// </remarks>
    [Theory]
    [InlineData(50)]
    [InlineData(60)]
    public void TheAnswerIsAMainsFrequencyAndNotAnAverageOfTwo(double fundamental)
    {
        float[][] hummed = WithHum(Track(), fundamental);

        (string readout, _) = Run(hummed, mains: fundamental == 50 ? 60 : 50, PlaybackBlock, fundamental);

        Assert.Contains($"{fundamental:0.0} Hz", readout);
    }

    /// <summary>
    /// How far down it still works, stated rather than left to be discovered.
    /// </summary>
    /// <remarks>
    /// It reaches a hum at −70 dBFS under this programme — about 52 dB below it — and does not
    /// reach one at −80. That is the trade the prominence threshold sets: a mains line has to
    /// stand 8 dB clear of what the music is doing at frequencies no mains harmonic can reach.
    /// Below the floor nothing is invented; the manual setting simply stands.
    /// </remarks>
    [Fact]
    public void ItReachesAHumFiftyDecibelsUnderTheProgramme()
    {
        AudioDocument track = Track();

        (string found, _) = Run(WithHum(track, 50, 0.00032), mains: 60, PlaybackBlock, humHz: 50);
        (string missed, _) = Run(WithHum(track, 50, 0.00010), mains: 60, PlaybackBlock, humHz: 50);

        Assert.Contains("50.0 Hz", found);       // -70 dBFS
        Assert.Contains("60.0 Hz", missed);      // -80 dBFS: below the floor, so the manual value stands
    }
}
