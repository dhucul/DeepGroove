using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class DdpInteropTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-ddp-interop-").FullName;
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ReferenceProgrammeHasInteroperableRecordsAndCdText()
    {
        // CD-TEXT generated independently by cue2ddp 1.1 from Disc/Artist and the three titles
        // below. This catches sequence numbering, string terminators, size packs, and CRC rules.
        const string goldenText = "gAAAAERpc2MAVHJhY2sgT0gEgAEBB25lAFRyYWNrIFR3b1XJgAICCQBUcmFjayBUaHJlZeZvgAMDCwAAAAAAAAAAAAAAAF33gQAEAEFydGlzdABBcnRpc/0igQEFBXQAQXJ0aXN0AEFydIwlgQMGA2lzdAAAAAAAAAAAAGKsjwAHAAABAwAEAwAAAAAAAED7jwEIAAAAAAAAAAADCQAAANamjwIJAAAAAAAJAAAAAAAAAApO";
        float[][][] audio = [Track(5, 0), Track(7, 2), Track(7, 2)];
        DdpTrackInfo[] tracks =
        [
            new("Track One", "Artist", Isrc: "GBAAA2400001"),
            new("Track Two", "Artist", Isrc: "GBAAA2400002", PreEmphasis: true, PregapFrames: 150),
            new("Track Three", "Artist", PregapFrames: 150),
        ];
        var disc = new DdpDiscInfo("Disc", "Artist", "5012345678900");
        var result = DdpImage.Write(_directory, audio, tracks, disc, 44100, dither: false);
        byte[] id = File.ReadAllBytes(Path.Combine(_directory, "DDPID"));
        Assert.Equal(128, id.Length);
        Assert.Equal("DDP 2.00", Encoding.ASCII.GetString(id, 0, 8));
        Assert.Equal("5012345678900", Encoding.ASCII.GetString(id, 8, 13));
        Assert.Equal("CD", Encoding.ASCII.GetString(id, 87, 2));

        byte[] map = File.ReadAllBytes(Path.Combine(_directory, "DDPMS"));
        Assert.Equal(384, map.Length);
        var files = new List<string>();
        for (int i = 0; i < map.Length; i += 128)
        {
            string record = Encoding.ASCII.GetString(map, i, 128);
            Assert.StartsWith("VVVM", record);
            Assert.Equal(17, int.Parse(record.AsSpan(71, 3), CultureInfo.InvariantCulture));
            string name = record.Substring(74, 17).Trim();
            files.Add(name);
            int length = int.Parse(record.AsSpan(14, 8), CultureInfo.InvariantCulture);
            long bytes = new FileInfo(Path.Combine(_directory, name)).Length;
            if (name == "IMAGE.DAT")
            {
                Assert.Equal("D0", record.Substring(4, 2));
                Assert.Equal("DA7", record.Substring(38, 3));
                Assert.Equal(150, int.Parse(record.AsSpan(46, 4), CultureInfo.InvariantCulture));
                Assert.Equal(1575, length);
                Assert.Equal(length * 2352L, bytes);
            }
            else Assert.Equal(length, bytes);
        }
        Assert.Equal(new[] { "CDTEXT.BIN", "PQDESCR", "IMAGE.DAT" }, files);
        Assert.Equal(Convert.FromBase64String(goldenText), File.ReadAllBytes(Path.Combine(_directory, "CDTEXT.BIN")));
        Assert.Equal(21L * 44100 * 4, result.ImageBytes);
        var pq = DdpTestReader.ReadPq(Path.Combine(_directory, "PQDESCR"));
        Assert.Equal(new[] { 150, 675, 1200 }, pq.Where(p => p.Index == 1 && p.Track != "AA").Select(p => p.Sector));
        Assert.Equal(1575, Assert.Single(pq, p => p.Track == "AA").Sector);
        foreach (string line in File.ReadAllLines(Path.Combine(_directory, "CHECKSUM.MD5")))
        {
            string name = line[34..];
            using var stream = File.OpenRead(Path.Combine(_directory, name));
            Assert.Equal(line[..32], Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant());
        }

        // Optional retained fixture for ddpinfo --verify / --wave interoperability checks.
        if (Environment.GetEnvironmentVariable("WAVELAB_DDP_INTEROP_OUTPUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            string package = Path.Combine(folder, "ddp");
            DdpImage.Write(package, audio, tracks, disc, 44100, dither: false);
            float[][] programme = [audio.SelectMany(t => t[0]).ToArray(), audio.SelectMany(t => t[1]).ToArray()];
            WavCodec.Save(new AudioDocument(programme, 44100, 16), Path.Combine(folder, "expected.wav"), 16, false);
        }
    }

    [Fact]
    public void UnsupportedCdTextFailsBeforeWritingAnyFiles()
    {
        Assert.Throws<ArgumentException>(() => DdpImage.Write(_directory,
            [Track(4, 0)], [new DdpTrackInfo("音楽")], new DdpDiscInfo("Disc"), 44100));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void TextExactlyTwelveCharactersHasATerminatorAndLatin1IsPreserved()
    {
        DdpImage.Write(_directory, [Track(4, 0)], [new DdpTrackInfo("é12345678901")],
            new DdpDiscInfo("123456789012"), 44100);
        byte[] packs = File.ReadAllBytes(Path.Combine(_directory, "CDTEXT.BIN"));
        var text = new List<byte>();
        for (int i = 0; i < packs.Length; i += 18)
            if (packs[i] == 0x80) text.AddRange(packs.AsSpan(i + 4, 12).ToArray());
        Assert.StartsWith("123456789012\0é12345678901\0", Encoding.Latin1.GetString(text.ToArray()));
    }

    private static float[][] Track(int seconds, int gapSeconds)
    {
        float[][] result = [new float[44100 * seconds], new float[44100 * seconds]];
        for (int i = gapSeconds * 44100; i < result[0].Length; i++)
        {
            float value = (int)(10000 * Math.Sin(2 * Math.PI * 440 * (i % 44100) / 44100)) / 32768f;
            result[0][i] = value;
            result[1][i] = -value;
        }
        return result;
    }
}
