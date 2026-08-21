using System.IO;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Pins the defects a module-by-module audit found, each of which had no coverage.
/// </summary>
/// <remarks>
/// One test per finding, named for the behaviour rather than the fix, so a regression reads as a
/// statement about the app rather than as "the patch came out". Where the defect was a crash the
/// test would have thrown before the fix; where it was a silent wrong answer the test states the
/// answer.
/// </remarks>
public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-audit").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    // ── Wave64 ───────────────────────────────────────────────────

    /// <summary>
    /// A 64-bit float Wave64 file decoded in full and then threw out of AudioDocument's
    /// setter, which takes 16, 24 or 32. The container's depth has to be mapped into the
    /// document's domain, as the WAV and AIFF loaders both do.
    /// </summary>
    [Fact]
    public void Wave64WithSixtyFourBitFloatSamplesOpens()
    {
        string path = Path("float64.w64");
        WriteWave64Float64(path, sampleRate: 48_000, channels: 2, frames: 512);

        AudioDocument document = Wave64Codec.Load(path);

        Assert.Equal(2, document.ChannelCount);
        Assert.Equal(512, document.Length);
        Assert.Equal(48_000, document.SampleRate);
        Assert.Equal(32, document.SourceBitDepth);   // float, narrowed to the document's float depth
        Assert.Equal(0.5f, document.Channels[0][128], 1e-6f);
    }

    /// <summary>
    /// A .w64 opened with no FilePath and the title "Untitled", so Save silently became
    /// Save As. Both siblings set them.
    /// </summary>
    [Fact]
    public void Wave64CarriesItsOwnIdentity()
    {
        string path = Path("named.w64");
        var source = new AudioDocument([new float[256], new float[256]], 44_100, 24);
        Wave64Codec.Save(source, path, 24);

        AudioDocument loaded = Wave64Codec.Load(path);

        Assert.Equal(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(loaded.FilePath!));
        Assert.Equal("named.w64", loaded.Title);
    }

    /// <summary>
    /// The Wave64 writer staged through a fixed "&lt;name&gt;.part" opened with
    /// FileMode.Create, so a second writer to the same destination truncated the first
    /// one's staging file instead of failing. Nothing may be left behind either.
    /// </summary>
    [Fact]
    public void Wave64LeavesNoStagingFileBehind()
    {
        string path = Path("staged.w64");
        var source = new AudioDocument([new float[128]], 44_100, 24);
        Wave64Codec.Save(source, path, 24);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.part"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    // ── Biquad ───────────────────────────────────────────────────

    /// <summary>
    /// MonoToStereoEffect's fixed 4500 Hz all-pass sits above Nyquist at an 8 kHz project
    /// rate. Unclamped that put the pole pair at radius 1.32 and the filter diverged, so
    /// the rack produced growing output and then non-finite samples.
    /// </summary>
    [Theory]
    [InlineData(8_000, 4_500)]     // above Nyquist
    [InlineData(8_000, 4_000)]     // exactly Nyquist: a double pole on the unit circle
    [InlineData(8_000, 20_000)]    // far past it, where cos and sin have wrapped
    public void FiltersStayStableAboveNyquist(int sampleRate, double frequency)
    {
        foreach (Biquad filter in new[]
                 {
                     Biquad.AllPass(sampleRate, frequency, 0.707),
                     Biquad.LowPass(sampleRate, frequency, 0.707),
                     Biquad.HighPass(sampleRate, frequency, 0.707),
                     Biquad.Peaking(sampleRate, frequency, 1.0, 6.0),
                     Biquad.HighShelf(sampleRate, frequency, 6.0),
                 })
        {
            Biquad running = filter;
            float peak = 0;
            for (int i = 0; i < 20_000; i++)
            {
                float output = running.Process(i == 0 ? 1f : 0f);   // impulse response
                Assert.True(float.IsFinite(output), $"{sampleRate} Hz / {frequency} Hz produced {output}");
                peak = Math.Max(peak, Math.Abs(output));
            }

            // A stable filter's impulse response decays. A diverging one reaches the rails.
            Assert.True(peak < 100f, $"{sampleRate} Hz / {frequency} Hz reached {peak}");
        }
    }

    /// <summary>
    /// FirstOrderHighPass was the second-order form at Q = 0.5, so it rolled off at
    /// 12 dB/octave rather than the 6 its name promises — twice the low-end rejection the
    /// compressor's sidechain was asking for.
    /// </summary>
    [Fact]
    public void FirstOrderHighPassRollsOffAtSixDecibelsPerOctave()
    {
        const int rate = 48_000;
        Biquad filter = Biquad.FirstOrderHighPass(rate, 1_000);

        double octaveBelow = filter.MagnitudeDb(500, rate);
        double twoOctavesBelow = filter.MagnitudeDb(250, rate);

        // Each octave down costs another 6 dB once well inside the stopband.
        Assert.InRange(octaveBelow - twoOctavesBelow, 5.0, 7.0);
    }

    // ── RiffMetadata ─────────────────────────────────────────────

    /// <summary>
    /// Each chunk was capped but the count was not, so a file made of empty chunk headers
    /// turned into millions of records — about a gigabyte of managed heap from a hundred
    /// megabytes of input.
    /// </summary>
    [Fact]
    public void CarriedMetadataIsBoundedInTotalAndInCount()
    {
        var metadata = new RiffMetadata();
        for (int i = 0; i < RiffMetadata.MaximumChunkCount + 500; i++)
            metadata.Add($"c{i % 10:00}", []);

        Assert.True(metadata.Chunks.Count <= RiffMetadata.MaximumChunkCount);

        var byBytes = new RiffMetadata();
        var megabyte = new byte[1024 * 1024];
        for (int i = 0; i < 200; i++) byBytes.Add("junk", megabyte);

        Assert.True(byBytes.ByteLength <= RiffMetadata.MaximumTotalBytes + RiffMetadata.MaximumChunkBytes);
    }

    /// <summary>
    /// Chunk ids come from widening raw bytes, and ASCII mapped anything above 0x7F to
    /// '?' on the way out — which is not the byte-for-byte fidelity this type exists for.
    /// </summary>
    [Fact]
    public void ChunkIdsWithHighBytesSurviveTheRoundTrip()
    {
        string id = RiffMetadata.IdFrom(0xC3B5A4E9);
        var metadata = new RiffMetadata();
        metadata.Add(id, [1, 2, 3, 4]);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            metadata.WriteTo(writer);

        byte[] written = stream.ToArray();
        Assert.Equal(0xE9, written[0]);
        Assert.Equal(0xA4, written[1]);
        Assert.Equal(0xB5, written[2]);
        Assert.Equal(0xC3, written[3]);
    }

    // ── restoration ──────────────────────────────────────────────

    /// <summary>
    /// Both silence detectors indexed channel zero before validating, where every other
    /// public entry point in the file validates first.
    /// </summary>
    [Fact]
    public void SilenceDetectionOnNoChannelsIsEmptyRatherThanAThrow()
    {
        Assert.Empty(Restoration.DetectSilences([], 44_100, -60, 100));
        Assert.Empty(Restoration.DetectSilencesAdvanced([], 44_100, -60, 100));
    }

    /// <summary>
    /// The spline declipper interpolates its knots, and every knot sits at or below the
    /// rail — so away from the centre it wrote values under what was actually recorded.
    /// The main declipper carries a note recording that this clamp was the whole of a
    /// measured regression on percussive material.
    /// </summary>
    [Fact]
    public void SplineDeclipperNeverWritesBelowTheRecordedRail()
    {
        const int rate = 44_100;
        var channel = new float[2_000];
        for (int i = 0; i < channel.Length; i++)
            channel[i] = (float)(0.95 * Math.Sin(2 * Math.PI * 220 * i / rate));

        // Flatten a positive peak into a rail.
        var railed = new List<int>();
        for (int i = 0; i < channel.Length; i++)
        {
            if (channel[i] <= 0.9f) continue;
            channel[i] = 0.9f;
            railed.Add(i);
        }
        Assert.NotEmpty(railed);

        float[][] source = [(float[])channel.Clone()];
        ClippingAnalysisResult analysis = Restoration.AnalyzeClipping(source, rate);
        Assert.NotEmpty(analysis.Events);

        float[][] repaired = Restoration.RepairClippingSpline(source, analysis.Events);

        foreach (int i in railed)
        {
            if (source[0][i] <= 0) continue;
            Assert.True(repaired[0][i] >= source[0][i] - 1e-6f,
                $"sample {i} came back under the rail: {repaired[0][i]} < {source[0][i]}");
        }
    }

    // ── the predictive detectors ─────────────────────────────────

    /// <summary>
    /// A file whose last block is digitally silent must not hang the click analyzer.
    /// </summary>
    /// <remarks>
    /// This is a regression against the audit itself. Making predictive detection scan the tail
    /// replaced a bounded loop with one carrying a clamped index, and the pre-existing
    /// <c>continue</c> for a zero residual scale then re-clamped to the same block forever. A
    /// silent tail gives exactly that scale, <c>PredictiveDetection</c> is on by default, and the
    /// symptom was the whole application wedged with no error - found only because a corpus run
    /// burned 51 CPU-hours on one 8-second Windows chime.
    /// </remarks>
    [Theory]
    [InlineData(22_050)]
    [InlineData(44_100)]
    public async Task AFileWithASilentTailDoesNotHangClickAnalysis(int sampleRate)
    {
        var channel = new float[sampleRate * 8];
        int music = channel.Length - 6000;              // a tail shorter than one 4096 block
        for (int i = 0; i < music; i++)
            channel[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
        // channel[music..] stays exactly zero: a constant block, so the residual scale is zero.

        var analysis = Task.Run(() => Restoration.AnalyzeClicks(
            [channel], sampleRate, new ClickAnalysisOptions { PredictiveDetection = true }));

        ClickAnalysisResult result = await analysis.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result);
    }

    /// <summary>Crackle detection carries the same clamped-index loop, so it gets the same test.</summary>
    [Fact]
    public async Task ASilentTailDoesNotHangCrackleDetection()
    {
        const int rate = 44_100;
        var channel = new float[rate * 8];
        int music = channel.Length - 6000;
        for (int i = 0; i < music; i++)
            channel[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / rate));

        var detection = Task.Run(() => Decrackle.Process(channel));
        await detection.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ── the offline render ───────────────────────────────────────

    /// <summary>
    /// ProcessOffline indexed channel zero and assumed every channel matched it, where
    /// ProcessOfflineRange had validated both since it was written.
    /// </summary>
    [Fact]
    public void OfflineRenderValidatesItsInputLikeItsSibling()
    {
        var master = new MasterSection();

        Assert.Empty(master.ProcessOffline([], 44_100));
        Assert.Throws<ArgumentException>(() =>
            master.ProcessOffline([new float[100], new float[50]], 44_100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            master.ProcessOffline([new float[100]], 0));
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>
    /// A minimal Wave64 file whose samples are 64-bit IEEE float — a shape the reader
    /// decoded correctly and then refused to hand back.
    /// </summary>
    private static void WriteWave64Float64(string path, int sampleRate, int channels, int frames)
    {
        var riff = new Guid("66666972-912E-11CF-A5D6-28DB04C10000");
        var wave = new Guid("65766177-ACF3-11D3-8CD1-00C04F8EDB8A");
        var fmt = new Guid("20746D66-ACF3-11D3-8CD1-00C04F8EDB8A");
        var data = new Guid("61746164-ACF3-11D3-8CD1-00C04F8EDB8A");

        const int headerBytes = 24;                        // a chunk size counts its own header
        int blockAlign = channels * sizeof(double);
        long fmtSize = headerBytes + 18;
        long dataSize = headerBytes + (long)frames * blockAlign;
        static long Pad(long size) => (8 - size % 8) % 8;  // chunks align to eight, not two

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(riff.ToByteArray());
        writer.Write(16 + 8 + 16 + fmtSize + Pad(fmtSize) + dataSize + Pad(dataSize));
        writer.Write(wave.ToByteArray());

        writer.Write(fmt.ToByteArray());
        writer.Write(fmtSize);
        writer.Write((ushort)3);                           // IEEE float
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)64);                          // the depth that used to throw
        writer.Write((ushort)0);                           // cbSize
        for (long i = 0; i < Pad(fmtSize); i++) writer.Write((byte)0);

        writer.Write(data.ToByteArray());
        writer.Write(dataSize);
        for (int frame = 0; frame < frames; frame++)
            for (int c = 0; c < channels; c++)
                writer.Write(frame == 128 ? 0.5 : 0.0);
        for (long i = 0; i < Pad(dataSize); i++) writer.Write((byte)0);
    }
}
