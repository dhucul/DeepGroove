using System.IO;
using System.Text;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class DeliveryTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-delivery").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private static AudioDocument Document(int frames = 4_096, int channels = 2)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(0.3 * Math.Sin(2 * Math.PI * (440 + c * 110) * i / Rate));
        }
        return new AudioDocument(data, Rate, 32);
    }

    /// <summary>Writes a WAV with extra chunks in it, as another program would leave them.</summary>
    private string WriteWithExtraChunks(params (string Id, byte[] Data)[] extras)
    {
        string path = Path("source.wav");
        AudioDocument doc = Document();
        foreach ((string id, byte[] data) in extras) doc.Riff.Set(id, data);
        WavCodec.Save(doc, path, 24, dither: false);
        return path;
    }

    // ── chunks survive ───────────────────────────────────────────

    /// <summary>
    /// The property the whole chunk model exists for: what another program put in the file is still
    /// there after this one has opened it and saved it back.
    /// </summary>
    [Fact]
    public void UnknownChunksSurviveALoadAndSave()
    {
        byte[] payload = Encoding.ASCII.GetBytes("field recorder scene 3 take 7, boom left");
        byte[] loops = [1, 2, 3, 4, 5, 6, 7, 8];
        string source = WriteWithExtraChunks(("iXML", payload), ("smpl", loops));

        AudioDocument opened = WavCodec.Load(source);
        output.WriteLine($"carried through: {string.Join(", ", opened.Riff.Chunks)}");

        string saved = Path("saved.wav");
        WavCodec.Save(opened, saved, 24, dither: false);
        AudioDocument reopened = WavCodec.Load(saved);

        Assert.Equal(payload, reopened.Riff.Find("iXML")!.Value.Data);
        Assert.Equal(loops, reopened.Riff.Find("smpl")!.Value.Data);
    }

    [Fact]
    public void AnOddLengthChunkIsPaddedAndComesBackTheSameLength()
    {
        byte[] odd = Encoding.ASCII.GetBytes("seven!!");     // 7 bytes
        string source = WriteWithExtraChunks(("note", odd));

        AudioDocument reopened = WavCodec.Load(source);
        RiffChunk? chunk = reopened.Riff.Find("note");

        Assert.NotNull(chunk);
        Assert.Equal(7, chunk!.Value.Data.Length);
        Assert.Equal(odd, chunk.Value.Data);
    }

    /// <summary>The chunks this app writes itself must not also be carried as opaque copies.</summary>
    [Fact]
    public void ChunksThisAppOwnsAreNotDuplicated()
    {
        string source = WriteWithExtraChunks(("iXML", [1, 2, 3]));
        AudioDocument opened = WavCodec.Load(source);

        foreach (RiffChunk chunk in opened.Riff.Chunks)
        {
            Assert.NotEqual("fmt ", chunk.Id);
            Assert.NotEqual("data", chunk.Id);
            Assert.NotEqual("fact", chunk.Id);
        }
    }

    [Fact]
    public void AudioIsUnchangedByCarryingMetadata()
    {
        AudioDocument original = Document();
        string plain = Path("plain.wav");
        string tagged = Path("tagged.wav");

        WavCodec.Save(original, plain, 24, dither: false);
        original.Riff.Set("iXML", Encoding.ASCII.GetBytes(new string('x', 5_000)));
        WavCodec.Save(original, tagged, 24, dither: false);

        AudioDocument a = WavCodec.Load(plain);
        AudioDocument b = WavCodec.Load(tagged);

        Assert.Equal(a.Length, b.Length);
        for (int c = 0; c < a.Channels.Count; c++) Assert.Equal(a.Channels[c], b.Channels[c]);
    }

    [Fact]
    public void AnImplausiblyLargeChunkIsNotCarried()
    {
        var metadata = new RiffMetadata();
        metadata.Add("junk", new byte[RiffMetadata.MaximumChunkBytes + 1]);
        Assert.True(metadata.IsEmpty);
    }

    // ── broadcast metadata ───────────────────────────────────────

    /// <summary>
    /// The broadcast extension has to survive a round trip through a real file, because that is the
    /// only place its fixed-width fields and their padding are exercised.
    /// </summary>
    [Fact]
    public void TheBroadcastExtensionSurvivesAFileRoundTrip()
    {
        var info = new BroadcastInfo(
            "Side A, needle drop", "WaveLab", "REF-0042", "2024-03-11", "14:22:05",
            TimeReference: 44_100UL * 3_600);

        AudioDocument doc = Document();
        doc.Riff.Set("bext", BroadcastMetadata.WriteBext(info, "A=PCM,F=44100,W=24,M=stereo"));

        string path = Path("bext.wav");
        WavCodec.Save(doc, path, 24, dither: false);

        BroadcastInfo? read = BroadcastMetadata.ReadBext(WavCodec.Load(path).Riff.Find("bext")!.Value.Data);
        output.WriteLine($"read back: {read}");

        Assert.NotNull(read);
        Assert.Equal(info.Description, read!.Value.Description);
        Assert.Equal(info.Originator, read.Value.Originator);
        Assert.Equal(info.OriginatorReference, read.Value.OriginatorReference);
        Assert.Equal(info.OriginationDate, read.Value.OriginationDate);
        Assert.Equal(info.OriginationTime, read.Value.OriginationTime);
        Assert.Equal(info.TimeReference, read.Value.TimeReference);
    }

    /// <summary>
    /// The time reference is 64-bit, and a day at 96 kHz overflows 32. Getting this wrong puts a
    /// take at the wrong place on a timeline in a way nothing else would reveal.
    /// </summary>
    [Fact]
    public void TheTimeReferenceSurvivesBeyondThirtyTwoBits()
    {
        const ulong late = 96_000UL * 60 * 60 * 20;      // twenty hours at 96 kHz
        Assert.True(late > uint.MaxValue);

        var info = BroadcastInfo.For("late take", new DateTime(2024, 1, 1), late);
        BroadcastInfo? read = BroadcastMetadata.ReadBext(BroadcastMetadata.WriteBext(info));

        Assert.Equal(late, read!.Value.TimeReference);
    }

    [Fact]
    public void InfoTagsSurviveARoundTrip()
    {
        var tags = new Dictionary<string, string>
        {
            ["INAM"] = "Loving You Baby",
            ["IART"] = "The Transfer",
            ["ICMT"] = "Needle drop, second pass — a comment with an em dash",
        };

        Dictionary<string, string> read = BroadcastMetadata.ReadInfoList(BroadcastMetadata.WriteInfoList(tags));
        foreach ((string key, string value) in tags) Assert.Equal(value, read[key]);
    }

    // ── markers in the file ──────────────────────────────────────

    /// <summary>
    /// Markers written as cue points travel inside the WAV. Until now they lived only in a sidecar,
    /// which every other program ignores and which is lost the moment the file is copied alone.
    /// </summary>
    [Fact]
    public void MarkersTravelInsideTheFile()
    {
        List<BroadcastMetadata.CuePoint> points =
        [
            new(1, 0, "Track 1"),
            new(2, 132_300, "Track 2"),
            new(3, 264_600, "Track 3 — with a dash"),
        ];

        AudioDocument doc = Document();
        doc.Riff.Set("cue ", BroadcastMetadata.WriteCueChunk(points));
        doc.Riff.Set("LIST", BroadcastMetadata.WriteLabelList(points));

        string path = Path("marked.wav");
        WavCodec.Save(doc, path, 24, dither: false);
        AudioDocument reopened = WavCodec.Load(path);

        List<BroadcastMetadata.CuePoint> read = BroadcastMetadata.ReadCuePoints(
            reopened.Riff.Find("cue ")!.Value.Data,
            reopened.Riff.Find("LIST")?.Data);

        output.WriteLine($"read back {read.Count}: {string.Join(", ", read.Select(p => $"{p.Position}={p.Label}"))}");
        Assert.Equal(points.Count, read.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].Position, read[i].Position);
            Assert.Equal(points[i].Label, read[i].Label);
        }
    }

    [Fact]
    public void CuePointsWithoutLabelsStillRead()
    {
        List<BroadcastMetadata.CuePoint> points = [new(1, 1_000, ""), new(2, 2_000, "")];
        List<BroadcastMetadata.CuePoint> read =
            BroadcastMetadata.ReadCuePoints(BroadcastMetadata.WriteCueChunk(points), null);

        Assert.Equal(2, read.Count);
        Assert.Equal(1_000, read[0].Position);
    }

    [Fact]
    public void ADamagedCueChunkIsNotTrusted()
    {
        Assert.Empty(BroadcastMetadata.ReadCuePoints([0xFF, 0xFF, 0xFF, 0x7F], null));
        Assert.Empty(BroadcastMetadata.ReadCuePoints([1, 0, 0, 0], null));   // count of one, no body
    }

    // ── loudness compliance ──────────────────────────────────────

    private static float[][] Programme(double amplitude, int seconds = 12)
    {
        int frames = Rate * seconds;
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * 1_000 * i / Rate));
        }
        return data;
    }

    [Fact]
    public void AProgrammeIsMeasuredAgainstItsTarget()
    {
        LoudnessReport report = LoudnessCompliance.Measure(Programme(0.1), Rate, LoudnessTarget.Ebu);

        output.WriteLine(LoudnessCompliance.Format(report));
        Assert.Equal(LoudnessTarget.Ebu.Name, report.Target.Name);
        Assert.True(double.IsFinite(report.IntegratedLufs));
        Assert.True(double.IsFinite(report.TruePeakDbtp));
        Assert.Equal(3, report.Checks.Count);
    }

    /// <summary>Something at the target must pass; something far from it must not.</summary>
    [Fact]
    public void ComplianceIsDecidedByTheNumbersNotTheIntent()
    {
        LoudnessReport quiet = LoudnessCompliance.Measure(Programme(0.005), Rate, LoudnessTarget.Streaming);
        output.WriteLine($"very quiet: {quiet.IntegratedLufs:0.0} LUFS, passed {quiet.Passed}");
        Assert.False(quiet.Passed);

        // Corrected by the gain the report itself suggests, it should then comply.
        float[][] corrected = Programme(0.005);
        double gain = Math.Pow(10, quiet.SuggestedGainDb / 20.0);
        foreach (float[] channel in corrected)
            for (int i = 0; i < channel.Length; i++) channel[i] = (float)(channel[i] * gain);

        LoudnessReport after = LoudnessCompliance.Measure(corrected, Rate, LoudnessTarget.Streaming);
        output.WriteLine($"after {quiet.SuggestedGainDb:+0.0;-0.0} dB: {after.IntegratedLufs:0.0} LUFS, " +
                         $"passed {after.Passed}");
        Assert.True(after.Passed, LoudnessCompliance.Format(after));
    }

    /// <summary>
    /// The suggested gain is limited by the true-peak ceiling, not by loudness alone. Offering a
    /// gain that would breach the ceiling is worse than offering none.
    /// </summary>
    [Fact]
    public void TheSuggestedGainNeverBreachesTheCeiling()
    {
        // Already close to full scale: loudness asks for more, the ceiling does not allow it.
        LoudnessReport report = LoudnessCompliance.Measure(Programme(0.9), Rate, LoudnessTarget.Streaming);

        double wanted = LoudnessTarget.Streaming.IntegratedLufs - report.IntegratedLufs;
        output.WriteLine($"{report.IntegratedLufs:0.0} LUFS at {report.TruePeakDbtp:0.0} dBTP: " +
                         $"loudness asks {wanted:+0.0;-0.0} dB, suggested {report.SuggestedGainDb:+0.0;-0.0} dB");

        Assert.True(report.TruePeakDbtp + report.SuggestedGainDb
                    <= LoudnessTarget.Streaming.TruePeakDbtp + 1e-6,
            "the suggested gain would push the true peak past the ceiling");
    }

    [Fact]
    public void SilenceIsReportedRatherThanCrashing()
    {
        var silence = new float[2][];
        for (int c = 0; c < 2; c++) silence[c] = new float[Rate * 3];

        LoudnessReport report = LoudnessCompliance.Measure(silence, Rate, LoudnessTarget.Ebu);
        string text = LoudnessCompliance.Format(report);

        output.WriteLine(text);
        Assert.Contains("—", text);
        Assert.False(report.Passed);
    }

    [Fact]
    public void EveryTargetIsDistinctAndSane()
    {
        foreach (LoudnessTarget target in LoudnessTarget.All)
        {
            output.WriteLine($"{target.Name,-18} {target.IntegratedLufs,6:0.0} LUFS, " +
                             $"≤ {target.TruePeakDbtp:0.0} dBTP, ± {target.ToleranceLu:0.0} LU");
            Assert.InRange(target.IntegratedLufs, -30, -8);
            Assert.InRange(target.TruePeakDbtp, -3, 0);
            Assert.True(target.ToleranceLu > 0);
        }
        Assert.Equal(LoudnessTarget.All.Count,
            LoudnessTarget.All.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LoudnessCompliance.Measure(Programme(0.2), Rate, LoudnessTarget.Ebu, cancellation.Token));
    }
}
