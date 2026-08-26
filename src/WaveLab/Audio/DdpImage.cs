using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WaveLab.Audio;

/// <summary>What a replication plant is told about one track.</summary>
/// <param name="Title">CD-TEXT title.</param>
/// <param name="Performer">CD-TEXT performer.</param>
/// <param name="Songwriter">CD-TEXT songwriter.</param>
/// <param name="Isrc">The recording's ISRC, twelve characters, or empty.</param>
/// <param name="PreEmphasis">Whether the track was cut with pre-emphasis.</param>
/// <param name="PregapFrames">
/// How much of this track's audio is the silence ahead of the music, in CD frames. The PQ sheet
/// states it as an INDEX 00 of its own, which is what tells a player — and a plant — that the gap
/// belongs to this track and is to be skipped when the track is chosen directly.
/// </param>
public readonly record struct DdpTrackInfo(
    string Title, string Performer = "", string Songwriter = "", string Isrc = "",
    bool PreEmphasis = false, int PregapFrames = 0)
{
    /// <summary>An ISRC with its punctuation removed and its case normalised, or empty if unusable.</summary>
    public string NormalisedIsrc => Audio.Isrc.Normalise(Isrc);
}

/// <summary>Reading and generating the catalogue numbers a PQ sheet carries.</summary>
/// <remarks>
/// An ISRC is twelve characters: two for the country, three for the registrant, two for the year of
/// reference, five for the designation code. Only the last five change from track to track, which is
/// what makes filling a disc from one number a sensible thing to offer — and what makes the wrap at
/// 99999 worth refusing rather than rolling over into somebody else's year.
/// </remarks>
public static class Isrc
{
    /// <summary>Length of an ISRC once its punctuation is removed.</summary>
    public const int Length = 12;

    /// <summary>The designation-code part: the last five digits, which are the ones that count up.</summary>
    public const int DesignationDigits = 5;

    /// <summary>Punctuation removed, case normalised. Empty when the result is not twelve characters.</summary>
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = new StringBuilder(Length);
        foreach (char c in value)
            if (char.IsLetterOrDigit(c)) cleaned.Append(char.ToUpperInvariant(c));
        return cleaned.Length == Length ? cleaned.ToString() : string.Empty;
    }

    /// <summary>Whether this is either blank or a usable ISRC — the two states that are not an error.</summary>
    public static bool IsAcceptable(string? value) =>
        string.IsNullOrWhiteSpace(value) || Normalise(value).Length == Length;

    /// <summary>
    /// The same ISRC with its designation code advanced by <paramref name="steps"/>, or empty if
    /// that would run past 99999.
    /// </summary>
    public static string Advance(string? seed, int steps)
    {
        string normalised = Normalise(seed);
        if (normalised.Length != Length || steps < 0) return string.Empty;

        string prefix = normalised[..(Length - DesignationDigits)];
        string tail = normalised[(Length - DesignationDigits)..];
        if (!int.TryParse(tail, out int designation)) return string.Empty;

        long next = designation + (long)steps;
        return next > 99_999 ? string.Empty : prefix + next.ToString("D5");
    }

    /// <summary>
    /// The ISRCs in a plain text file, one per line, blanks and <c>#</c> comments skipped. A line
    /// that is not an ISRC is kept as an empty entry rather than dropped, so the numbers after it
    /// still land on the tracks they were meant for.
    /// </summary>
    public static List<string> Parse(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            result.Add(Normalise(line));
        }
        return result;
    }
}

/// <summary>What the plant is told about the disc.</summary>
public readonly record struct DdpDiscInfo(
    string Title, string Performer = "", string Upc = "", string Comment = "")
{
    /// <summary>The UPC/EAN reduced to its digits, or empty if it is not thirteen of them.</summary>
    public string NormalisedUpc
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Upc)) return string.Empty;
            var digits = new StringBuilder(13);
            foreach (char c in Upc) if (char.IsDigit(c)) digits.Append(c);
            return digits.Length is 12 or 13 ? digits.ToString().PadLeft(13, '0') : string.Empty;
        }
    }
}

/// <summary>What a DDP export produced.</summary>
public sealed record DdpResult(
    string Folder, IReadOnlyList<string> Files, long ImageBytes, string ImageMd5, int Tracks);

/// <summary>
/// Writes a DDP 2.00 image set: the deliverable a replication plant actually accepts.
/// </summary>
/// <remarks>
/// <para>
/// This app already produces the hard part — CD-frame-aligned, gapless 44.1 kHz 16-bit audio with a
/// cue sheet. A cue sheet plus WAVs is what a duplicator takes; a DDP image set is what a pressing
/// plant takes, and the difference is not quality but formality: the PQ information, the catalogue
/// numbers and the CD-TEXT are stated in files the plant's systems read directly, and a checksum
/// travels with the audio so a corrupted transfer is caught before glass is cut.
/// </para>
/// <para>
/// The set is five files. <c>DDPID</c> identifies the whole thing; <c>DDPMS</c> is the map, one
/// stream descriptor per part; <c>PQDESCR</c> is the PQ sheet, one line per track; <c>IMAGE.DAT</c>
/// is the audio as one interleaved big-endian stream; <c>CDTEXT.BIN</c> carries the titles. The MD5
/// of the image is written alongside, which is what lets the plant prove it received what was sent.
/// </para>
/// <para>
/// <b>Big-endian.</b> A DDP image is CD-DA byte order, which is the opposite of WAV's. Getting it
/// backwards produces a file of exactly the right length, full of noise, that no check short of
/// listening will catch.
/// </para>
/// </remarks>
public static class DdpImage
{
    /// <summary>Frames per second on a CD: the unit every PQ offset is expressed in.</summary>
    public const int FramesPerSecond = 75;

    /// <summary>Samples in one CD frame at 44.1 kHz — 588, and every track must start on one.</summary>
    public const int SamplesPerFrame = 44_100 / FramesPerSecond;

    /// <summary>The standard two-second pause before the first track.</summary>
    public const int LeadInFrames = 2 * FramesPerSecond;

    /// <summary>
    /// Writes the image set for a sequence of tracks, all of which must be 44.1 kHz stereo.
    /// </summary>
    public static DdpResult Write(string folder, IReadOnlyList<float[][]> tracks,
        IReadOnlyList<DdpTrackInfo> info, DdpDiscInfo disc, int sampleRate,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(info);
        if (sampleRate != 44_100)
            throw new ArgumentException("A DDP image is 44.1 kHz by definition.", nameof(sampleRate));
        if (tracks.Count == 0) throw new ArgumentException("There is nothing to write.", nameof(tracks));
        if (tracks.Count > 99) throw new ArgumentException("A CD holds at most 99 tracks.", nameof(tracks));
        if (info.Count != tracks.Count)
            throw new ArgumentException("Every track needs its own information.", nameof(info));

        Directory.CreateDirectory(folder);
        var written = new List<string>();

        string imagePath = Path.Combine(folder, "IMAGE.DAT");
        var starts = new int[tracks.Count];
        long total = 0;

        string md5;
        using (var stream = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None,
                   1 << 20, FileOptions.SequentialScan))
        using (var hash = MD5.Create())
        using (var crypto = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
        {
            for (int t = 0; t < tracks.Count; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)t / tracks.Count);

                float[][] track = tracks[t];
                if (track.Length != 2)
                    throw new ArgumentException($"Track {t + 1} is not stereo.", nameof(tracks));

                starts[t] = (int)(total / (SamplesPerFrame * 4L));
                total += WriteTrack(crypto, track, cancellationToken);
            }

            crypto.FlushFinalBlock();
            md5 = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
        }
        written.Add(imagePath);

        int totalFrames = (int)(total / (SamplesPerFrame * 4L));
        written.Add(WriteText(folder, "DDPID", BuildDdpId(disc)));
        written.Add(WriteText(folder, "DDPMS", BuildDdpMs(totalFrames, md5)));
        written.Add(WriteText(folder, "PQDESCR", BuildPqSheet(starts, totalFrames, info, disc)));
        written.Add(WriteBinary(folder, "CDTEXT.BIN", BuildCdText(info, disc)));
        written.Add(WriteText(folder, "IMAGE.DAT.md5", $"{md5} *IMAGE.DAT{Environment.NewLine}"));

        progress?.Report(1);
        return new DdpResult(folder, written, total, md5, tracks.Count);
    }

    /// <summary>
    /// Writes one track as interleaved big-endian 16-bit, padded to a whole CD frame.
    /// </summary>
    private static long WriteTrack(Stream stream, float[][] track, CancellationToken cancellationToken)
    {
        int frames = Math.Min(track[0].Length, track[1].Length);

        // Padded up to a frame boundary: a track that does not fill its last frame would otherwise
        // push every track after it off the frame grid, and a CD has no way to represent that.
        int padded = (frames + SamplesPerFrame - 1) / SamplesPerFrame * SamplesPerFrame;
        var buffer = new byte[SamplesPerFrame * 4];

        for (int start = 0; start < padded; start += SamplesPerFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int at = 0;
            for (int i = 0; i < SamplesPerFrame; i++)
            {
                int index = start + i;
                for (int c = 0; c < 2; c++)
                {
                    float sample = index < frames ? track[c][index] : 0f;
                    if (!float.IsFinite(sample)) sample = 0f;
                    int value = Math.Clamp((int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32768.0),
                        short.MinValue, short.MaxValue);

                    // Big-endian: CD-DA byte order, the opposite of a WAV's.
                    buffer[at++] = (byte)(value >> 8);
                    buffer[at++] = (byte)value;
                }
            }
            stream.Write(buffer, 0, buffer.Length);
        }

        return (long)padded * 4;
    }

    // ── the descriptor files ─────────────────────────────────────

    private static string BuildDdpId(DdpDiscInfo disc)
    {
        var text = new StringBuilder();
        text.Append("DDP 2.00LEVEL A ");
        text.Append(Fixed(disc.Title, 32));
        text.Append(Fixed(disc.NormalisedUpc, 13));
        text.Append(Fixed("DEEP GROOVE", 16));
        text.AppendLine();
        return text.ToString();
    }

    private static string BuildDdpMs(int totalFrames, string md5)
    {
        var text = new StringBuilder();
        text.AppendLine($"VVVMSUP  IMAGE.DAT   {totalFrames:D8}  CDDA        MD5={md5}");
        return text.ToString();
    }

    /// <summary>
    /// The PQ sheet: where each track starts, in minutes, seconds and CD frames from the start of
    /// the programme, plus its catalogue and flag information.
    /// </summary>
    private static string BuildPqSheet(int[] starts, int totalFrames,
        IReadOnlyList<DdpTrackInfo> info, DdpDiscInfo disc)
    {
        var text = new StringBuilder();
        // Hyphen rather than an em dash on purpose: this file is written as ASCII, which maps
        // anything above 0x7F to a question mark, so the wording has to stay inside it. Same trap
        // the AIFF text chunks were fixed for.
        text.AppendLine($"# PQ descriptor - {info.Count} track{(info.Count == 1 ? "" : "s")}");
        if (disc.NormalisedUpc.Length > 0) text.AppendLine($"UPC/EAN  {disc.NormalisedUpc}");
        text.AppendLine("TRK  INDEX  START         LENGTH        ISRC          EMPH  TITLE");

        for (int t = 0; t < info.Count; t++)
        {
            // Offsets are stated from the start of the programme, with the two-second lead-in added:
            // that is where the plant's timeline begins, not where the audio does.
            int start = starts[t] + LeadInFrames;
            int end = (t + 1 < starts.Length ? starts[t + 1] : totalFrames) + LeadInFrames;

            // A pregap is stated as an index of its own: INDEX 00 where the silence begins and
            // INDEX 01 where the music does. Without the first row the plant reads the gap as the
            // opening of the track, and a player would neither count it down nor skip it.
            int pregap = Math.Clamp(info[t].PregapFrames, 0, Math.Max(0, end - start));
            if (pregap > 0)
                text.AppendLine(
                    $"{t + 1,3:D2}  00     {Timecode(start)}  {Timecode(pregap)}  " +
                    $"{Fixed(string.Empty, 12)}  {(info[t].PreEmphasis ? "ON " : "OFF")}   (pregap)");

            text.AppendLine(
                $"{t + 1,3:D2}  01     {Timecode(start + pregap)}  {Timecode(end - start - pregap)}  " +
                $"{Fixed(info[t].NormalisedIsrc, 12)}  {(info[t].PreEmphasis ? "ON " : "OFF")}   {info[t].Title}");
        }

        text.AppendLine($"LEAD-OUT      {Timecode(totalFrames + LeadInFrames)}");
        return text.ToString();
    }

    /// <summary>
    /// CD-TEXT as pack-sized records: the disc's own title and performer first, then each track's.
    /// </summary>
    private static byte[] BuildCdText(IReadOnlyList<DdpTrackInfo> info, DdpDiscInfo disc)
    {
        using var memory = new MemoryStream();

        AppendPacks(memory, 0x80, disc.Title, info.Select(i => i.Title));
        AppendPacks(memory, 0x81, disc.Performer, info.Select(i => i.Performer));
        AppendPacks(memory, 0x82, string.Empty, info.Select(i => i.Songwriter));

        return memory.ToArray();

        static void AppendPacks(Stream stream, byte type, string disc, IEnumerable<string> tracks)
        {
            var all = new List<string> { disc ?? string.Empty };
            all.AddRange(tracks.Select(t => t ?? string.Empty));
            if (all.TrueForAll(string.IsNullOrEmpty)) return;

            for (int number = 0; number < all.Count; number++)
            {
                byte[] text = Encoding.ASCII.GetBytes(all[number]);
                for (int offset = 0; offset < Math.Max(1, text.Length); offset += 12)
                {
                    var pack = new byte[18];
                    pack[0] = type;
                    pack[1] = (byte)number;
                    pack[2] = (byte)(offset / 12);
                    pack[3] = 0;
                    for (int i = 0; i < 12 && offset + i < text.Length; i++) pack[4 + i] = text[offset + i];

                    // CRC over the pack, as the format requires, so a damaged pack is detectable.
                    ushort crc = Crc16(pack, 0, 16);
                    pack[16] = (byte)(crc >> 8);
                    pack[17] = (byte)crc;
                    stream.Write(pack, 0, pack.Length);
                }
            }
        }
    }

    /// <summary>CCITT CRC-16, which is what CD-TEXT packs are checked with.</summary>
    internal static ushort Crc16(byte[] data, int offset, int length)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>Minutes, seconds and CD frames, which is how a PQ sheet states every position.</summary>
    public static string Timecode(int frames)
    {
        if (frames < 0) frames = 0;
        int minutes = frames / (60 * FramesPerSecond);
        int seconds = frames / FramesPerSecond % 60;
        int remainder = frames % FramesPerSecond;
        return $"{minutes:D2}:{seconds:D2}:{remainder:D2}";
    }

    private static string Fixed(string? value, int length)
    {
        value ??= string.Empty;
        return value.Length >= length ? value[..length] : value.PadRight(length);
    }

    private static string WriteText(string folder, string name, string content)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, content, Encoding.ASCII);
        return path;
    }

    private static string WriteBinary(string folder, string name, byte[] content)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
