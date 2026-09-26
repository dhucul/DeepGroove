using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using WaveLab.Audio.Dsp;

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
            if (char.IsAsciiLetterOrDigit(c)) cleaned.Append(char.ToUpperInvariant(c));
        if (cleaned.Length != Length) return string.Empty;

        string result = cleaned.ToString();
        bool valid = char.IsAsciiLetter(result[0]) && char.IsAsciiLetter(result[1]);
        for (int index = 2; valid && index < 5; index++)
            valid = char.IsAsciiLetterOrDigit(result[index]);
        for (int index = 5; valid && index < Length; index++)
            valid = char.IsAsciiDigit(result[index]);
        return valid ? result : string.Empty;
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
            foreach (char c in Upc) if (char.IsAsciiDigit(c)) digits.Append(c);
            if (digits.Length is not (12 or 13)) return string.Empty;
            string normalised = digits.ToString().PadLeft(13, '0');
            return HasValidCheckDigit(normalised) ? normalised : string.Empty;
        }
    }

    private static bool HasValidCheckDigit(string value)
    {
        int sum = 0;
        for (int index = 0; index < 12; index++)
        {
            int digit = value[index] - '0';
            sum += (index & 1) == 0 ? digit : digit * 3;
        }
        return (10 - sum % 10) % 10 == value[12] - '0';
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
/// <c>DDPID</c> identifies the master; <c>DDPMS</c> maps the streams; <c>PQDESCR</c> carries track
/// and index records; <c>IMAGE.DAT</c> is interleaved little-endian audio. Optional <c>CDTEXT.BIN</c>
/// carries titles. <c>CHECKSUM.MD5</c> covers the complete set; the image also keeps its own checksum.
/// </para>
/// <para>
/// The image includes the initial 150-sector pause. DDPID and DDPMS use fixed 128-byte records;
/// PQDESCR uses 64-byte records. These are machine records, not a printable PQ report.
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
        CancellationToken cancellationToken = default, IProgress<double>? progress = null,
        bool? dither = null)
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
        for (int t = 0; t < tracks.Count; t++)
        {
            float[][] track = tracks[t];
            if (track is not { Length: 2 } || track[0] is null || track[1] is null)
                throw new ArgumentException($"Track {t + 1} is not stereo.", nameof(tracks));
            if (track[0].Length != track[1].Length)
                throw new ArgumentException(
                    $"Track {t + 1} has channels of different lengths.", nameof(tracks));
            if (track[0].Length == 0 || info[t].PregapFrames < 0 ||
                (long)info[t].PregapFrames * SamplesPerFrame >= track[0].Length)
                throw new ArgumentException($"Track {t + 1} has no audio after its pregap.", nameof(info));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var starts = new int[tracks.Count];
        long sectors = LeadInFrames;
        for (int t = 0; t < tracks.Count; t++)
        {
            starts[t] = checked((int)sectors);
            sectors += ((long)tracks[t][0].Length + SamplesPerFrame - 1) / SamplesPerFrame;
        }
        if (sectors >= 100 * 60 * FramesPerSecond)
            throw new ArgumentException("The DDP programme exceeds its two-digit CD minute field.", nameof(tracks));
        int totalFrames = (int)sectors;
        byte[] pq = BuildPqDescriptors(starts, totalFrames, info, disc);
        byte[] cdText = BuildCdText(info, disc);

        Directory.CreateDirectory(folder);
        var written = new List<string>();

        string imagePath = Path.Combine(folder, "IMAGE.DAT");
        long total = 0;
        bool applyDither = dither ?? NeedsDither(tracks, cancellationToken);
        var quantizer = applyDither
            ? new Dither(DitherKind.FlatTpdf, 16, 2, sampleRate, autoBlank: true)
            : null;

        string md5;
        using (var stream = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None,
                   1 << 20, FileOptions.SequentialScan))
        using (var hash = MD5.Create())
        using (var crypto = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
        {
            // The DDP stream explicitly includes index 00 of track 1. The map states that
            // these 150 pause sectors are already present, so a reader must not add them again.
            byte[] silence = new byte[SamplesPerFrame * 4];
            for (int i = 0; i < LeadInFrames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crypto.Write(silence);
                total += silence.Length;
            }
            for (int t = 0; t < tracks.Count; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)t / tracks.Count);

                float[][] track = tracks[t];
                total += WriteTrack(crypto, track, quantizer, cancellationToken);
            }

            crypto.FlushFinalBlock();
            md5 = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
        }
        written.Add(imagePath);

        written.Add(WriteBinary(folder, "DDPID", BuildDdpId(disc)));
        written.Add(WriteBinary(folder, "DDPMS", BuildDdpMs(totalFrames, pq.Length, cdText.Length)));
        written.Add(WriteBinary(folder, "PQDESCR", pq));
        if (cdText.Length > 0) written.Add(WriteBinary(folder, "CDTEXT.BIN", cdText));
        var checksums = new StringBuilder();
        foreach (string file in written)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(file);
            string digest = file == imagePath ? md5 : Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
            checksums.Append(digest).Append(" *").Append(Path.GetFileName(file)).Append("\r\n");
        }
        written.Add(WriteText(folder, "CHECKSUM.MD5", checksums.ToString()));
        written.Add(WriteText(folder, "IMAGE.DAT.md5", $"{md5} *IMAGE.DAT{Environment.NewLine}"));

        progress?.Report(1);
        return new DdpResult(folder, written, total, md5, tracks.Count);
    }

    /// <summary>
    /// Writes one track as interleaved little-endian 16-bit, padded to a whole CD frame.
    /// </summary>
    private static long WriteTrack(Stream stream, float[][] track, Dither? quantizer,
        CancellationToken cancellationToken)
    {
        int frames = Math.Min(track[0].Length, track[1].Length);

        // Padded up to a frame boundary: a track that does not fill its last frame would otherwise
        // push every track after it off the frame grid, and a CD has no way to represent that.
        long padded = ((long)frames + SamplesPerFrame - 1) / SamplesPerFrame * SamplesPerFrame;
        var buffer = new byte[SamplesPerFrame * 4];

        for (long start = 0; start < padded; start += SamplesPerFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int at = 0;
            for (int i = 0; i < SamplesPerFrame; i++)
            {
                long index = start + i;
                for (int c = 0; c < 2; c++)
                {
                    float sample = index < frames ? track[c][index] : 0f;
                    if (!float.IsFinite(sample)) sample = 0f;
                    double quantized = quantizer?.Process(c, Math.Clamp(sample, -1f, 1f))
                        ?? Math.Clamp(sample, -1f, 1f);
                    int value = Math.Clamp((int)Math.Round(quantized * 32768.0),
                        short.MinValue, short.MaxValue);

                    buffer[at++] = (byte)value;
                    buffer[at++] = (byte)(value >> 8);
                }
            }
            stream.Write(buffer, 0, buffer.Length);
        }

        return (long)padded * 4;
    }

    private static bool NeedsDither(IReadOnlyList<float[][]> tracks,
        CancellationToken cancellationToken)
    {
        foreach (float[][] track in tracks)
        {
            foreach (float[] channel in track)
            {
                for (int i = 0; i < channel.Length; i++)
                {
                    if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    float sample = channel[i];
                    if (!float.IsFinite(sample)) return true;
                    double scaled = sample * 32768.0;
                    if (scaled < short.MinValue || scaled > short.MaxValue ||
                        scaled != Math.Truncate(scaled))
                        return true;
                }
            }
        }
        return false;
    }

    // ── the descriptor files ─────────────────────────────────────

    // Field offsets are the DDP 2.00 on-disk layout. Interoperability is checked with
    // Andreas Ruge's independent cue2ddp/ddpinfo, including decoded audio and CD-TEXT.
    private static byte[] BuildDdpId(DdpDiscInfo disc)
    {
        byte[] record = BlankRecord(128);
        Field(record, 0, 8, "DDP 2.00");
        Field(record, 8, 13, disc.NormalisedUpc);
        Field(record, 38, 48, Fixed(disc.Title, 48));
        Field(record, 87, 2, "CD");
        return record;
    }

    private static byte[] BuildDdpMs(int sectors, int pqBytes, int cdTextBytes)
    {
        using var output = new MemoryStream();
        if (cdTextBytes > 0) WriteMap("S0", "CDTEXT", "CDTEXT.BIN", cdTextBytes);
        WriteMap("S0", "PQ DESCR", "PQDESCR", pqBytes);
        WriteMap("D0", "", "IMAGE.DAT", sectors);
        return output.ToArray();

        void WriteMap(string type, string subcode, string file, int length)
        {
            byte[] record = BlankRecord(128);
            Field(record, 0, 4, "VVVM");
            Field(record, 4, 2, type);
            Field(record, 14, 8, length.ToString(CultureInfo.InvariantCulture).PadLeft(8));
            Field(record, 30, 8, subcode);
            if (type == "D0")
            {
                Field(record, 38, 4, "DA71"); // CD-DA, complete 2352-byte sectors
                Field(record, 46, 4, LeadInFrames.ToString(CultureInfo.InvariantCulture).PadLeft(4));
            }
            else if (subcode == "CDTEXT") Field(record, 55, 2, "00");
            Field(record, 71, 3, " 17");
            Field(record, 74, 17, file);
            output.Write(record);
        }
    }

    private static byte[] BuildPqDescriptors(int[] starts, int totalFrames,
        IReadOnlyList<DdpTrackInfo> info, DdpDiscInfo disc)
    {
        using var output = new MemoryStream();
        if (disc.NormalisedUpc.Length > 0) WritePq("00", 0, 0, false, "", disc.NormalisedUpc);
        for (int t = 0; t < info.Count; t++)
        {
            string track = (t + 1).ToString("D2", CultureInfo.InvariantCulture);
            int pregap = info[t].PregapFrames;
            bool hasIndexZero = t == 0 || pregap > 0;
            if (hasIndexZero)
                WritePq(track, 0, t == 0 ? 0 : starts[t], info[t].PreEmphasis, info[t].NormalisedIsrc, "");
            WritePq(track, 1, starts[t] + pregap, info[t].PreEmphasis,
                hasIndexZero ? "" : info[t].NormalisedIsrc, "");
        }
        WritePq("AA", 1, totalFrames, false, "", "");
        return output.ToArray();

        void WritePq(string track, int index, int at, bool emphasis, string isrc, string upc)
        {
            byte[] record = BlankRecord(64);
            Field(record, 0, 4, "VVVS");
            Field(record, 4, 2, track);
            Field(record, 6, 2, index.ToString("D2", CultureInfo.InvariantCulture));
            Field(record, 10, 6, string.Create(CultureInfo.InvariantCulture,
                $"{at / (60 * FramesPerSecond):D2}{at / FramesPerSecond % 60:D2}{at % FramesPerSecond:D2}"));
            Field(record, 16, 2, emphasis ? "11" : "01");
            Field(record, 20, 12, isrc);
            Field(record, 32, 13, upc);
            output.Write(record);
        }
    }

    private static byte[] BlankRecord(int length)
    {
        var record = new byte[length];
        Array.Fill(record, (byte)' ');
        return record;
    }

    private static void Field(byte[] record, int offset, int width, string value)
    {
        if (value.Length > width) throw new InvalidDataException("A DDP field exceeds its fixed width.");
        // Control characters cannot be allowed to break a machine record. CD-TEXT below keeps
        // its supported non-ASCII text; this field is the ASCII master identifier only.
        for (int i = 0; i < value.Length; i++)
            record[offset + i] = value[i] is >= ' ' and <= '~' ? (byte)value[i] : (byte)' ';
    }

    private static byte[] BuildCdText(IReadOnlyList<DdpTrackInfo> info, DdpDiscInfo disc)
    {
        var packs = new List<byte[]>();
        var counts = new byte[16];
        var latin1 = Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        AddText(0x80, disc.Title, info.Select(i => i.Title));
        AddText(0x81, disc.Performer, info.Select(i => i.Performer));
        AddText(0x82, "", info.Select(i => i.Songwriter));
        if (packs.Count == 0) return [];

        // Three size-information packs describe the character set, track limits, per-type pack
        // counts, final sequence numbers, and languages for all eight possible blocks.
        byte[] sizes = new byte[36];
        sizes[0] = 0; // ISO 8859-1
        sizes[1] = 1;
        sizes[2] = (byte)info.Count;
        counts[15] = 3;
        counts.CopyTo(sizes, 4);
        sizes[20] = (byte)(packs.Count + 2);
        sizes[28] = 9; // English block
        for (byte i = 0; i < 3; i++)
        {
            var pack = NewPack(0x8f, i, 0);
            Array.Copy(sizes, i * 12, pack, 4, 12);
            FinishPack(pack);
        }
        return packs.SelectMany(p => p).ToArray();

        byte[] NewPack(byte type, byte track, byte position)
        {
            if (packs.Count >= 256) throw new ArgumentException("The CD-TEXT exceeds one language block's 256-pack limit.");
            var pack = new byte[18];
            pack[0] = type; pack[1] = track; pack[2] = (byte)packs.Count; pack[3] = position;
            return pack;
        }
        void FinishPack(byte[] pack)
        {
            ushort crc = Crc16(pack, 0, 16);
            pack[16] = (byte)(crc >> 8); pack[17] = (byte)crc;
            packs.Add(pack);
        }
        void AddText(byte type, string? discText, IEnumerable<string> trackText)
        {
            string[] text = [discText ?? "", .. trackText.Select(t => t ?? "")];
            if (text.All(string.IsNullOrEmpty)) return;
            byte[][] strings;
            try
            {
                if (text.Any(s => s.Any(char.IsControl)))
                    throw new ArgumentException("CD-TEXT cannot contain line breaks or control characters.");
                strings = text.Select(s => latin1.GetBytes(s + '\0')).ToArray();
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException("DDP CD-TEXT supports Latin-1 characters. Change unsupported titles or export WAV + CUE instead.", ex);
            }
            int track = 0, offset = 0;
            while (track < strings.Length)
            {
                if (packs.Count >= 253) throw new ArgumentException("The CD-TEXT exceeds one language block's 256-pack limit.");
                var pack = NewPack(type, (byte)track, (byte)Math.Min(offset, 15));
                for (int i = 0; i < 12 && track < strings.Length; i++)
                {
                    pack[4 + i] = strings[track][offset++];
                    if (offset == strings[track].Length) { track++; offset = 0; }
                }
                counts[type - 0x80]++;
                FinishPack(pack);
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
        return (ushort)~crc; // CD-TEXT stores the complemented CRC.
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
