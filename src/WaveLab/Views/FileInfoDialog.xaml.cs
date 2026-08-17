using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>
/// What a file says about itself: the human-facing tags, the broadcast extension, and a read-out of
/// every chunk the file will contain.
/// </summary>
/// <remarks>
/// <para>
/// The third tab is not an editor. It exists because the claim this app now makes — that a file's
/// metadata survives being opened and saved — is one a user has no way to check. Listing what will
/// be written turns it from something to be believed into something to be looked at.
/// </para>
/// <para>
/// Editing here marks the document dirty but writes nothing: the chunks go out with the next Save,
/// through the same codec path everything else uses. A dialog that wrote the file itself would be a
/// second save path to keep correct.
/// </para>
/// </remarks>
public partial class FileInfoDialog : Window
{
    /// <summary>One row of the chunk read-out.</summary>
    private sealed record ChunkRow(string Id, string SizeText, string Meaning);

    /// <summary>
    /// What the chunks this app knows by name are for. Anything absent from here is carried through
    /// untouched and says so, which is the more important of the two cases.
    /// </summary>
    private static readonly Dictionary<string, string> KnownChunks = new(StringComparer.Ordinal)
    {
        ["bext"] = "broadcast extension — description, originator, timeline position",
        ["iXML"] = "field recorder metadata (iXML)",
        ["axml"] = "field recorder metadata (AXML)",
        ["smpl"] = "sampler loop points and root note",
        ["inst"] = "instrument settings",
        ["cue "] = "cue points — this document's markers, rewritten on save",
        ["acid"] = "tempo and key, for loop hosts",
        ["ID3 "] = "an embedded ID3 tag",
        ["MARK"] = "marks — this document's markers, rewritten on save",
        ["COMT"] = "comments",
        ["NAME"] = "title",
        ["AUTH"] = "author",
        ["ANNO"] = "annotation — comment, album, track, genre and year",
        ["(c) "] = "copyright",
    };

    private readonly DocumentViewModel _document;
    private readonly RiffMetadata _riff;
    private readonly ObservableCollection<ChunkRow> _chunks = [];
    private readonly bool _aiff;
    private FileTags _tags;
    private string _codingHistory = "";

    public FileInfoDialog(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        InitializeComponent();
        _document = document;

        // Edited in place on the document's own metadata: a clone would have to be merged back, and
        // Cancel closes without marking anything, which is the whole of what Cancel has to do.
        _riff = document.Doc.Riff;
        _aiff = _riff.IsAiff;
        _tags = FileTags.ReadFrom(_riff);

        chunkList.ItemsSource = _chunks;
        subtitleText.Text = Describe(document);
        LoadDescription();
        LoadBroadcast();
        ShowTab(descriptionTab);
    }

    private static string Describe(DocumentViewModel document)
    {
        AudioDocument doc = document.Doc;
        string container = doc.Riff.IsAiff ? "AIFF" : "RIFF";
        string name = doc.FilePath is { } path ? Path.GetFileName(path) : doc.Title;
        return $"{name} · {container} · {doc.SampleRate / 1000.0:0.#} kHz / {doc.SourceBitDepth}-bit";
    }

    // ── loading ──────────────────────────────────────────────────

    private void LoadDescription()
    {
        titleBox.Text = _tags.Title;
        artistBox.Text = _tags.Artist;
        albumBox.Text = _tags.Album;
        trackBox.Text = _tags.Track;
        genreBox.Text = _tags.Genre;
        yearBox.Text = _tags.Year;
        commentBox.Text = _tags.Comment;
    }

    private void LoadBroadcast()
    {
        if (_aiff)
        {
            // Present but disabled rather than hidden, so the reason it does not apply is visible.
            broadcastTab.IsEnabled = false;
            broadcastNote.Text = "The broadcast extension is a WAV chunk. This document came from an AIFF.";
            return;
        }

        BroadcastInfo? existing = _riff.Find("bext") is { } bext
            ? BroadcastMetadata.ReadBext(bext.Data)
            : null;
        _codingHistory = ReadCodingHistory(_riff.Find("bext")?.Data);

        BroadcastInfo info = existing ?? BroadcastInfo.For(string.Empty, DateTime.Now);
        bextDescription.Text = info.Description;
        bextOriginator.Text = string.IsNullOrWhiteSpace(info.Originator) ? "WaveLab" : info.Originator;
        bextReference.Text = info.OriginatorReference;
        bextDate.Text = info.OriginationDate;
        bextTime.Text = info.OriginationTime;
        bextTimeReference.Text = info.TimeReference.ToString(CultureInfo.InvariantCulture);
        bextHistory.Text = _codingHistory;
        broadcastNote.Text = existing == null
            ? "This file has no broadcast extension yet. Filling in a description adds one."
            : "Time reference is the first sample's position on the recording's own timeline, in samples.";
    }

    /// <summary>Whatever follows the fixed 602 bytes of a bext chunk is its coding history.</summary>
    private static string ReadCodingHistory(byte[]? bext)
    {
        const int fixedBytes = 602;
        if (bext is not { Length: > fixedBytes }) return "";

        string text = Encoding.ASCII.GetString(bext, fixedBytes, bext.Length - fixedBytes);
        return text.TrimEnd('\0');
    }

    // ── tabs ─────────────────────────────────────────────────────

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton tab) ShowTab(tab);
    }

    private void ShowTab(System.Windows.Controls.Primitives.ToggleButton tab)
    {
        // Reading the chunk list on entry rather than once at construction: the Description tab may
        // have been applied since, and a read-out that is out of date is worse than none.
        if (ReferenceEquals(tab, chunksTab)) RefreshChunks();

        descriptionTab.IsChecked = ReferenceEquals(tab, descriptionTab);
        broadcastTab.IsChecked = ReferenceEquals(tab, broadcastTab);
        chunksTab.IsChecked = ReferenceEquals(tab, chunksTab);

        descriptionPage.Visibility = Visible(descriptionTab);
        broadcastPage.Visibility = Visible(broadcastTab);
        chunksPage.Visibility = Visible(chunksTab);

        applyBtn.IsEnabled = !ReferenceEquals(tab, chunksTab);
        cancelBtn.Content = ReferenceEquals(tab, chunksTab) ? "Close" : "Cancel";
        statusText.Text = ReferenceEquals(tab, chunksTab)
            ? "Read-only — this is what the file will contain."
            : ReferenceEquals(tab, broadcastTab)
                ? "Written to the bext chunk on save."
                : _aiff
                    ? "Written to NAME, AUTH and ANNO on save."
                    : "Written to LIST/INFO on save · ID3v2.4 on MP3 export.";

        Visibility Visible(System.Windows.Controls.Primitives.ToggleButton which) =>
            ReferenceEquals(tab, which) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshChunks()
    {
        _chunks.Clear();

        // The audio chunks are not in the carried set — the codec writes them from the document —
        // so they are listed first by hand or the read-out would look like the file has no audio.
        _chunks.Add(new ChunkRow(_aiff ? "COMM" : "fmt ", "—", "the sample format"));
        _chunks.Add(new ChunkRow(_aiff ? "SSND" : "data", SizeOf(AudioBytes()), "the audio"));

        foreach (RiffChunk chunk in _riff.Chunks)
        {
            string id = chunk.Id;
            string meaning = KnownChunks.TryGetValue(id, out string? known)
                ? known
                : id == "LIST"
                    ? ListMeaning(chunk)
                    : "carried through from the source file, untouched";
            _chunks.Add(new ChunkRow(id, SizeOf(chunk.Data.Length), meaning));
        }

        int carried = _riff.Chunks.Count(c => !KnownChunks.ContainsKey(c.Id) && c.Id != "LIST");
        chunkNote.Text = carried > 0
            ? $"{carried} chunk(s) this app does not interpret are carried through byte for byte."
            : "Everything listed here survives an edit and a save.";
    }

    private static string ListMeaning(RiffChunk chunk)
    {
        if (chunk.Data.Length < 4) return "an empty list";
        return Encoding.ASCII.GetString(chunk.Data, 0, 4) switch
        {
            "INFO" => "information tags — title, artist, album and the rest",
            "adtl" => "marker labels, rewritten on save",
            var type => $"a {type} list, carried through untouched",
        };
    }

    private long AudioBytes()
    {
        AudioDocument doc = _document.Doc;
        return (long)doc.Length * doc.ChannelCount * (doc.SourceBitDepth / 8);
    }

    private static string SizeOf(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} kB"
        : $"{bytes} B";

    // ── applying ─────────────────────────────────────────────────

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // Everything is validated before anything is written, so a rejected time reference cannot
        // leave the document's metadata half-changed and its dirty flag unset.
        bool removeBext = false;
        byte[]? bext = null;
        if (!_aiff && !TryBuildBroadcast(out bext, out removeBext)) return;

        _tags = new FileTags
        {
            Title = titleBox.Text,
            Artist = artistBox.Text,
            Album = albumBox.Text,
            Track = trackBox.Text,
            Genre = genreBox.Text,
            Year = yearBox.Text,
            Comment = commentBox.Text,
        };
        _tags.WriteTo(_riff);

        if (removeBext) _riff.Remove("bext");
        else if (bext != null) _riff.Set("bext", bext);

        // Nothing is written to disk here. The chunks travel out with the next Save, through the
        // codec path everything else uses; a dialog that wrote the file would be a second save path.
        _document.Doc.MarkMetadataChanged();
        DialogResult = true;
        Close();
    }

    private bool TryBuildBroadcast(out byte[]? bext, out bool remove)
    {
        bext = null;
        remove = false;
        string description = bextDescription.Text?.Trim() ?? "";
        string originator = bextOriginator.Text?.Trim() ?? "";
        string reference = bextReference.Text?.Trim() ?? "";
        string date = bextDate.Text?.Trim() ?? "";
        string time = bextTime.Text?.Trim() ?? "";
        string history = bextHistory.Text ?? "";

        // Everything blank means "no broadcast extension", which has to be expressible: a file that
        // never had one should not acquire an empty one merely because the tab was opened.
        if (description.Length == 0 && originator.Length == 0 && reference.Length == 0 &&
            history.Trim().Length == 0 && ZeroOrBlank(bextTimeReference.Text))
        {
            remove = true;
            return true;
        }

        if (!ulong.TryParse(bextTimeReference.Text?.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong timeReference))
        {
            ShowTab(broadcastTab);
            statusText.Text = "The time reference is a whole number of samples since midnight.";
            bextTimeReference.Focus();
            return false;
        }

        if (date.Length > 0 && !DateTime.TryParseExact(date, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            ShowTab(broadcastTab);
            statusText.Text = "The origination date is written yyyy-MM-dd.";
            bextDate.Focus();
            return false;
        }

        if (time.Length > 0 && !TimeSpan.TryParseExact(time, @"hh\:mm\:ss",
                CultureInfo.InvariantCulture, out _))
        {
            ShowTab(broadcastTab);
            statusText.Text = "The origination time is written HH:mm:ss.";
            bextTime.Focus();
            return false;
        }

        var info = new BroadcastInfo(description, originator, reference, date, time, timeReference);
        bext = BroadcastMetadata.WriteBext(info, history);
        return true;

        static bool ZeroOrBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Trim() == "0";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
