using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class CdTransferDialog : Window
{
    /// <summary>
    /// One row of the PQ sheet. Title and performer reach both deliverables — a cue sheet carries a
    /// PERFORMER line per track as well as for the disc. Songwriter, ISRC and pre-emphasis are
    /// catalogue information only a DDP carries, and <see cref="DdpFields"/> is what gates those.
    /// </summary>
    private sealed class TrackRow : ObservableObject
    {
        private static readonly SolidColorBrush IsrcOk =
            Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x2D, 0x36)));
        private static readonly SolidColorBrush IsrcBad =
            Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x3A, 0x36)));

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        private string _title;
        private string _performer;
        private string _songwriter;
        private string _isrc;
        private bool _preEmphasis;
        private int _order;
        private string _cdLengthText = "—";
        private bool _ddpFields;

        public TrackRow(CdTrackPlan plan, int order, int sampleRate, NamedRegion? sourceRegion = null)
        {
            Plan = plan;
            _title = plan.Title;
            _performer = plan.Performer;
            _songwriter = plan.Songwriter;
            _isrc = plan.Isrc;
            _preEmphasis = plan.PreEmphasis;
            _order = order;
            SampleRate = sampleRate;
            SourceRegion = sourceRegion;
        }

        public CdTrackPlan Plan { get; private set; }
        public NamedRegion? SourceRegion { get; private set; }
        public int SampleRate { get; }
        public string Title { get => _title; set => Set(ref _title, value); }
        public string Performer { get => _performer; set => Set(ref _performer, value); }
        public string Songwriter { get => _songwriter; set => Set(ref _songwriter, value); }

        public string Isrc
        {
            get => _isrc;
            set
            {
                if (!Set(ref _isrc, value)) return;
                Raise(nameof(IsrcBrush));
            }
        }

        /// <summary>Blank and valid are the two states that are not an error; anything else is marked.</summary>
        public bool IsrcAcceptable => Audio.Isrc.IsAcceptable(Isrc);
        public Brush IsrcBrush => IsrcAcceptable ? IsrcOk : IsrcBad;

        public bool PreEmphasis
        {
            get => _preEmphasis;
            set { if (Set(ref _preEmphasis, value)) Raise(nameof(EmphasisText)); }
        }

        public string EmphasisText => PreEmphasis ? "ON" : "—";

        public int Order { get => _order; set { if (Set(ref _order, value)) Raise(nameof(OrderText)); } }
        public string OrderText => $"{Order:00}";
        public string StartText => TimeFormat.Position(Plan.SourceStart, SampleRate);
        public string EndText => TimeFormat.Position(Plan.SourceEnd, SampleRate);
        public double SecondsStart => SampleRate > 0 ? Plan.SourceStart / (double)SampleRate : 0;
        public double SecondsEnd => SampleRate > 0 ? Plan.SourceEnd / (double)SampleRate : 0;
        public string DurationText => TimeFormat.Compact(Plan.DurationSeconds(SampleRate));

        /// <summary>
        /// What this track occupies on the disc, in CD frames. It is not the source duration: the
        /// plan is aligned to 588-sample sectors first, and that alignment moves both boundaries.
        /// </summary>
        public string CdLengthText { get => _cdLengthText; set => Set(ref _cdLengthText, value); }

        /// <summary>
        /// Whether the DDP-only catalogue fields — songwriter, ISRC and pre-emphasis — are live. They
        /// are disabled rather than hidden for a WAV+CUE package, so the reason they do not apply is
        /// visible instead of the columns vanishing. Performer is not among them any more.
        /// </summary>
        public bool DdpFields { get => _ddpFields; set => Set(ref _ddpFields, value); }

        public CdTrackPlan ToPlan() => Plan with
        {
            Title = string.IsNullOrWhiteSpace(Title) ? $"Track {Order:00}" : Title.Trim(),
            Performer = Performer?.Trim() ?? string.Empty,
            Songwriter = Songwriter?.Trim() ?? string.Empty,
            Isrc = Audio.Isrc.Normalise(Isrc),
            PreEmphasis = PreEmphasis,
        };

        public void SetRange(int start, int end) =>
            SetPlan(Plan with { SourceStart = start, SourceEnd = end });

        /// <summary>
        /// Replace the range and the pregap together. The editable fields — title, performer,
        /// songwriter, ISRC, pre-emphasis — are held on the row rather than on the plan, so a plan
        /// arriving from a gap pass cannot overwrite what the user has typed.
        /// </summary>
        public void SetPlan(CdTrackPlan plan)
        {
            Plan = Plan with
            {
                SourceStart = plan.SourceStart,
                SourceEnd = plan.SourceEnd,
                PregapSeconds = plan.PregapSeconds,
            };
            Raise(nameof(StartText));
            Raise(nameof(EndText));
            Raise(nameof(DurationText));
        }

        public void BindRegion(NamedRegion region) => SourceRegion = region;
    }

    /// <summary>
    /// The window open on each document, so a second Prepare Audio CD raises the one already
    /// arranging that file instead of standing up a rival list beside it. Modeless windows can be
    /// asked for twice; a modal one never could.
    /// </summary>
    private static readonly Dictionary<DocumentViewModel, CdTransferDialog> OpenDialogs = [];

    private readonly DocumentViewModel _document;
    private readonly MainViewModel _main;
    private readonly ObservableCollection<TrackRow> _tracks = [];
    private readonly HashSet<NamedRegion> _knownPlanRegions = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closeWhenFinished;
    private bool _dialogReady;
    private bool _syncingRack;
    private double _gapSeconds;
    private bool _applyingGap;

    /// <summary>
    /// Whether the gap may only be added. Set for a programme assembled from separate files, whose
    /// heads and tails are finished decisions rather than the record's own quiet — see
    /// <see cref="CdTransfer.WithEvenPregaps"/> for why the two cases cannot share one rule.
    /// </summary>
    private bool _addOnlyGap;
    private float[]? _envelope;

    /// <summary>
    /// Open — or raise — the CD window for <paramref name="document"/>. It is modeless, so the
    /// waveform stays live underneath it: the selection Add Track takes, and the cursor Split cuts
    /// at, are the ones on screen now rather than whichever ones happened to be set before the
    /// window appeared.
    /// </summary>
    /// <param name="evenPregapSeconds">
    /// A gap to arrive with, for a programme assembled from separate files. It also says that those
    /// files are finished masters, so the gap adds silence and never trims a head or a tail back to
    /// make room for it. Null is the transfer case: a side cut into tracks, arriving with no gap
    /// because the record's own quiet is already sitting between the songs.
    /// </param>
    public static CdTransferDialog ShowFor(DocumentViewModel document, MainViewModel main, Window? owner,
        double? evenPregapSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(main);
        if (OpenDialogs.TryGetValue(document, out CdTransferDialog? existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var dialog = new CdTransferDialog(document, main, evenPregapSeconds) { Owner = owner };
        dialog.Closed += (_, _) =>
        {
            if (OpenDialogs.TryGetValue(document, out CdTransferDialog? registered) &&
                ReferenceEquals(registered, dialog))
                OpenDialogs.Remove(document);
        };
        // Registered only once it is actually up. A Show that throws — a closing owner, most
        // plausibly during shutdown — would otherwise leave an entry nothing can clear, because the
        // Closed that removes it never runs; every later request would then raise a window that was
        // never shown, and Activate on one of those fails silently.
        dialog.Show();
        OpenDialogs[document] = dialog;
        return dialog;
    }

    public CdTransferDialog(DocumentViewModel document, MainViewModel main,
        double? evenPregapSeconds = null)
    {
        InitializeComponent();
        _document = document;
        _main = main;
        trackList.ItemsSource = _tracks;
        discTitle.Text = Path.GetFileNameWithoutExtension(document.Doc.Title);
        renderRackCheck.IsChecked = main.Master.RackEnabled;

        var regionTracks = CdTransfer.FromRegionsWithSources(document.Regions, document.Doc.Length);
        if (regionTracks.Count > 0)
            ReplaceTracks(regionTracks.Select(item => item.Plan).ToList(),
                regionTracks.Select(item => item.Source).ToList());
        ApplyDeliverable();

        // A gap the caller already knows the answer to, put on screen and into the plan before the
        // window is ever shown. The box defaults to 0 because a transferred side arrives with the
        // record's quiet still between its songs; a set of separate files arrives with nothing
        // between them at all, and leaving that to be discovered produced a disc whose every track
        // ran hard into the next.
        if (evenPregapSeconds is { } seed)
        {
            _addOnlyGap = true;
            _gapSeconds = CdTransfer.SnapGapSeconds(seed);
            gapBox.Text = $"{_gapSeconds:0.###}";
            _applyingGap = true;
            try { ApplyGapToRows(PlanWithGap(_gapSeconds)); }
            finally { _applyingGap = false; }
            UpdatePlan();
        }

        // Everything this window shows is a view of state the main window can still change while it
        // is open. Each subscription keeps one of those in step; all three come off again on close.
        document.Doc.Changed += OnSourceEdited;
        main.Master.PropertyChanged += OnMasterChanged;
        main.Documents.CollectionChanged += OnDocumentsChanged;

        Loaded += async (_, _) =>
        {
            _dialogReady = true;
            if (_tracks.Count == 0) await SuggestTracksAsync();
            if (_tracks.Count > 0) trackList.SelectedIndex = 0;
        };
        Closing += OnDialogClosing;
        Closed += (_, _) =>
        {
            document.Doc.Changed -= OnSourceEdited;
            main.Master.PropertyChanged -= OnMasterChanged;
            main.Documents.CollectionChanged -= OnDocumentsChanged;
            _operation?.Cancel();
            _main.StopPreview();
        };
    }

    /// <summary>
    /// An edit landed in the file underneath the arranged list. Track ranges are anchored to the
    /// timeline exactly as markers and regions are, so they move with the splice rather than
    /// silently coming to mean a different piece of music.
    /// </summary>
    private void OnSourceEdited(int start, int removed, int inserted)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSourceEdited(start, removed, inserted));
            return;
        }

        // The envelope describes audio that has just been spliced, so it is no longer this file's.
        _envelope = null;

        int length = _document.Doc.Length;
        foreach (TrackRow row in _tracks)
        {
            int mappedStart = Math.Clamp(
                DocumentViewModel.MapEditAnchor(row.Plan.SourceStart, start, removed, inserted), 0, length);
            int mappedEnd = Math.Clamp(
                DocumentViewModel.MapEditAnchor(row.Plan.SourceEnd, start, removed, inserted), 0, length);
            if (mappedStart != row.Plan.SourceStart || mappedEnd != row.Plan.SourceEnd)
                row.SetRange(mappedStart, mappedEnd);
        }

        // A track the edit collapsed is left in the list rather than dropped: validation names it,
        // and the row is the only record of a title and ISRC that would otherwise go with it.
        UpdatePlan();
    }

    /// <summary>
    /// The rack this window renders through is the one master rack, which the main window can bypass
    /// while this is open. The checkbox is that switch, not a private copy of it — so it follows,
    /// and closing the window no longer restores a state the user has since changed deliberately.
    /// </summary>
    private void OnMasterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MasterSectionViewModel.RackEnabled) or null)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMasterChanged(sender, e));
            return;
        }
        if (renderRackCheck.IsChecked == _main.Master.RackEnabled) return;
        _syncingRack = true;
        try { renderRackCheck.IsChecked = _main.Master.RackEnabled; }
        finally { _syncingRack = false; }
    }

    /// <summary>The file this window arranges was closed; there is nothing left to prepare.</summary>
    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // An Add is a new tab and can never be the reason this document went away.
        if (e.Action == NotifyCollectionChangedAction.Add) return;
        if (_main.Documents.Contains(_document)) return;
        // Close through the ordinary path. Clearing _busy here forced the close past
        // OnDialogClosing and took the window down while an export was still unwinding: a write
        // that had already finished then reached InfoDialog.Show with a dead owner, and a package
        // sitting correctly on disk was reported as having failed. OnDialogClosing cancels this,
        // cancels the operation and re-issues the close once it lands.
        Close();
    }

    private void ReplaceTracks(
        IReadOnlyList<CdTrackPlan> plans, IReadOnlyList<NamedRegion>? sourceRegions = null)
    {
        if (sourceRegions != null && sourceRegions.Count != plans.Count)
            throw new ArgumentException("Track plans and source regions must have matching counts.", nameof(sourceRegions));
        foreach (var old in _tracks) old.PropertyChanged -= OnTrackRowChanged;
        _tracks.Clear();
        int order = 1;
        for (int i = 0; i < plans.Count; i++)
        {
            NamedRegion? sourceRegion = sourceRegions?[i];
            if (sourceRegion != null) _knownPlanRegions.Add(sourceRegion);
            _tracks.Add(NewRow(plans[i], order++, sourceRegion));
        }
        UpdatePlan();
        // Rebuilding the collection leaves the list with nothing selected, and Preview, Remove,
        // Split, ▲ and ▼ all read their enabled state off that selection — so Analyze used to hand
        // back a list with five of the buttons below it dead until the user clicked a row. On a
        // side whose gaps have not moved it also proposes exactly what is already on screen, so
        // there was nothing else to see either, and the button read as doing nothing at all.
        if (_tracks.Count > 0) trackList.SelectedIndex = 0;
    }

    /// <summary>
    /// The one place a row is built, so a row added by Split or Add Track cannot come out with
    /// its catalogue fields in a different state from the rest of the list.
    /// </summary>
    private TrackRow NewRow(CdTrackPlan plan, int order, NamedRegion? sourceRegion = null)
    {
        var row = new TrackRow(plan, order, _document.Doc.SampleRate, sourceRegion)
        {
            DdpFields = ddpBtn.IsChecked == true,
        };
        row.PropertyChanged += OnTrackRowChanged;
        return row;
    }

    private void OnTrackRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackRow.Title) or nameof(TrackRow.Isrc)) UpdatePlan();
    }

    // ── deliverable ──────────────────────────────────────────────

    /// <summary>Whether the DDP image set is the chosen deliverable rather than the WAV+CUE package.</summary>
    private bool Ddp => ddpBtn.IsChecked == true;

    /// <summary>
    /// Whether the burner package is written as one continuous WAV rather than one file per track.
    /// The disc is identical either way; what differs is whether the cue sheet's INDEX times are
    /// measured from each file's own start or from the start of the disc.
    /// </summary>
    private bool ImageCue => imageCueBtn.IsChecked == true;

    /// <summary>
    /// The three buttons are one choice, so checking any unchecks the others. <c>ToggleButton</c>s
    /// rather than a combo because the choice changes what half the dialog means, and a combo hides
    /// that behind a click.
    /// </summary>
    private void OnDeliverableChanged(object sender, RoutedEventArgs e)
    {
        // Each segment is re-checked from the sender rather than toggled, which is also what makes
        // clicking the checked one a no-op: WPF has already unchecked it by the time Click arrives,
        // and this puts it back.
        wavCueBtn.IsChecked = ReferenceEquals(sender, wavCueBtn);
        imageCueBtn.IsChecked = ReferenceEquals(sender, imageCueBtn);
        ddpBtn.IsChecked = ReferenceEquals(sender, ddpBtn);
        ApplyDeliverable();
    }

    private void ApplyDeliverable()
    {
        bool ddp = Ddp;
        foreach (TrackRow row in _tracks) row.DdpFields = ddp;

        // The disc performer is not DDP-only: a cue sheet carries a PERFORMER line, and until it
        // was wired through the exporter wrote a fabricated one. UPC and the ISRC tools stay DDP,
        // because a cue sheet has nowhere to put either.
        discPerformer.IsEnabled = !_busy;
        discUpc.IsEnabled = !_busy && ddp;
        importIsrcBtn.IsEnabled = autoNumberBtn.IsEnabled = !_busy && ddp;
        exportBtn.Content = ddp ? "Export DDP Image Set…"
            : ImageCue ? "Export CD Image…"
            : "Export CD Package…";
        deliverableHint.Text = ddp
            ? "PQ sheet, CD-TEXT, catalogue numbers and a checksum, for a pressing plant."
            : ImageCue
                ? "One continuous 16-bit WAV and a cue sheet whose INDEX times run across the disc. Catalogue fields do not apply."
                : "One sector-aligned 16-bit WAV per track, and a cue sheet indexing each from its own start. Catalogue fields do not apply.";
        UpdatePlan();
    }

    // ── catalogue numbers ────────────────────────────────────────

    /// <summary>
    /// Rewrites a committed ISRC in the form the PQ sheet will carry. A user typing
    /// <c>GB-AAA-24-00001</c> is typing the same code, and leaving the punctuation on screen while
    /// writing the bare twelve characters into the file shows something the deliverable does not say.
    /// </summary>
    private void OnIsrcCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not TrackRow row) return;
        string normalised = Isrc.Normalise(row.Isrc);
        if (normalised.Length == Isrc.Length && !string.Equals(row.Isrc, normalised, StringComparison.Ordinal))
            row.Isrc = normalised;
    }

    private void OnImportIsrcs(object sender, RoutedEventArgs e)
    {
        if (_busy || _tracks.Count == 0) return;
        var picker = new OpenFileDialog
        {
            Title = "Choose a text file of ISRCs, one per line, in track order",
            Filter = "Text files|*.txt;*.csv|All files|*.*",
        };
        if (picker.ShowDialog(this) != true) return;

        List<string> codes;
        try { codes = Isrc.Parse(File.ReadAllText(picker.FileName)); }
        catch (Exception ex)
        {
            statusText.Text = $"Could not read that file: {ex.Message}";
            return;
        }

        int applied = 0, rejected = 0;
        for (int i = 0; i < _tracks.Count && i < codes.Count; i++)
        {
            // A line that was not an ISRC arrives empty rather than missing, so the numbers after
            // it still land on the tracks they were meant for. Leave that track alone and say so.
            if (codes[i].Length == 0) { rejected++; continue; }
            _tracks[i].Isrc = codes[i];
            applied++;
        }

        statusText.Text = codes.Count == 0
            ? "That file held no ISRCs."
            : $"Applied {applied} ISRC(s) from {codes.Count} line(s)" +
              (rejected > 0 ? $"; {rejected} line(s) were not valid ISRCs and were skipped." : ".") +
              (codes.Count < _tracks.Count ? $" The last {_tracks.Count - codes.Count} track(s) were left as they were." : "");
        UpdatePlan();
    }

    /// <summary>
    /// Fills every ISRC from the first one by advancing the designation code — the last five digits,
    /// which are the only part that changes between tracks on one release.
    /// </summary>
    private void OnAutoNumberIsrcs(object sender, RoutedEventArgs e)
    {
        if (_busy || _tracks.Count == 0) return;
        string seed = Isrc.Normalise(_tracks[0].Isrc);
        if (seed.Length != Isrc.Length)
        {
            statusText.Text = "Enter a valid ISRC on track 01 first; the rest are counted up from it.";
            return;
        }

        for (int i = 1; i < _tracks.Count; i++)
        {
            string next = Isrc.Advance(seed, i);
            if (next.Length == 0)
            {
                statusText.Text = $"Numbering stopped at track {i:00}: the designation code would pass 99999.";
                UpdatePlan();
                return;
            }
            _tracks[i].Isrc = next;
        }

        statusText.Text = $"Numbered {_tracks.Count} track(s) from {seed}.";
        UpdatePlan();
    }

    private async void OnAutoSplit(object sender, RoutedEventArgs e) => await SuggestTracksAsync();

    private async Task SuggestTracksAsync()
    {
        if (_busy) return;
        if (_document.Doc.Length == 0)
        {
            // Silence here read as a dead button: the press does nothing and says nothing.
            statusText.Text = "There is no audio to analyze.";
            return;
        }

        SetBusy(true, "Analyzing quiet gaps without changing the recording...");
        _operation = new CancellationTokenSource();
        try
        {
            var channels = _document.Doc.Channels.ToArray();
            int rate = _document.Doc.SampleRate;
            int version = _document.Doc.EditVersion;
            double threshold = thresholdSlider.Value;
            // Captured rather than read off the field inside the lambda, which runs on the pool
            // while the finally below is free to null it.
            CancellationToken token = _operation.Token;
            var plans = await Task.Run(() => CdTransfer.SuggestTracks(
                channels, rate, threshold, CdTransfer.DefaultMinimumGapSeconds,
                CdTransfer.AutoSplitMinimumTrackSeconds, token), token);

            RefuseAStaleProposal(version, "Press Analyze again.");
            (int previous, double moved) = ApplyProposal(plans, rate);
            statusText.Text = CdTransfer.DescribeProposal(plans.Count, previous, moved);
        }
        catch (OperationCanceledException) { statusText.Text = "Track analysis cancelled."; }
        catch (Exception ex) { statusText.Text = ex.Message; }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
        }
    }

    /// <summary>
    /// Refuse a proposal measured against audio the document no longer holds.
    /// </summary>
    /// <remarks>
    /// This window is modeless so the waveform stays editable underneath it, which means a splice
    /// can land while an analysis is running. <see cref="OnSourceEdited"/> has already carried
    /// every row onto the new timeline by then; writing ranges derived from the old audio over them
    /// would put each track on music it was never measured against, and nothing would say so.
    /// Preview and Export have always checked this. The two analysis paths did not.
    /// </remarks>
    private void RefuseAStaleProposal(int version, string retry)
    {
        if (version != _document.Doc.EditVersion)
            throw new InvalidOperationException(
                $"The recording changed while it was being read. {retry}");
    }

    /// <summary>
    /// Put a proposal into the list, and report what it did to it: how many rows there were before,
    /// and how far the furthest split moved when the count did not change.
    /// </summary>
    /// <remarks>
    /// Shared by Analyze and Find Tracks, so a swept answer keeps what has been typed on exactly the
    /// terms a hand-set one does. <b>Same number of tracks means the same tracks, moved</b>:
    /// rebuilding the list for that throws away every title, performer and ISRC typed since — and
    /// the commonest press of all is the second one, where the window has already analysed on load
    /// and the splits have not moved at all. The ranges are updated in place instead, which also
    /// keeps the region each row is bound to and the row that was selected.
    /// </remarks>
    private (int Previous, double MovedSeconds) ApplyProposal(IReadOnlyList<CdTrackPlan> plans, int rate)
    {
        int previous = _tracks.Count;
        if (plans.Count == 0 || plans.Count != previous)
        {
            ReplaceTracks(plans);
            RetrimForGap();
            UpdatePlan();
            return (previous, double.NaN);
        }

        // Signed, and the furthest mover wins: which way the splits went is what says whether they
        // are eating the end of a song or the start of the next one.
        int worst = 0;
        for (int i = 0; i < plans.Count; i++)
        {
            worst = FurthestMove(worst, plans[i].SourceStart - _tracks[i].Plan.SourceStart);
            worst = FurthestMove(worst, plans[i].SourceEnd - _tracks[i].Plan.SourceEnd);
            _tracks[i].SetRange(plans[i].SourceStart, plans[i].SourceEnd);
        }
        RetrimForGap();
        UpdatePlan();
        return (previous, worst / (double)Math.Max(1, rate));
    }

    /// <summary>
    /// The track count typed into the box, or null when it is blank. Anything that is not a count a
    /// CD could hold is treated as blank, and <paramref name="rejected"/> says so.
    /// </summary>
    private int? TargetTracks(out bool rejected)
    {
        rejected = false;
        string typed = trackCountBox.Text?.Trim() ?? "";
        if (typed.Length == 0) return null;
        if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.CurrentCulture, out int count) &&
            count >= 1 && count <= CdTransfer.MaximumTracks)
            return count;
        rejected = true;
        return null;
    }

    // ── an even gap between every pair of tracks ─────────────────

    /// <summary>Enter commits the box, so a gap can be set without reaching for another control.</summary>
    private void OnGapKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyGap();
    }

    private void OnGapCommitted(object sender, RoutedEventArgs e) => ApplyGap();

    /// <summary>
    /// Put the gap back over a list that has just changed underneath it.
    /// </summary>
    /// <remarks>
    /// A gap is an instruction about the disc — "two seconds between every pair" — not a one-off
    /// edit, so re-analysing or adding a track must not quietly drop it. Safe to run at any time
    /// because <see cref="CdTransfer.ApplyGaps"/> is idempotent: a range already trimmed to its
    /// music trims to itself. Silent, because the caller has its own line to write.
    /// </remarks>
    private void RetrimForGap()
    {
        if (_gapSeconds <= 0 || _applyingGap || _tracks.Count == 0) return;
        _applyingGap = true;
        try { ApplyGapToRows(PlanWithGap(_gapSeconds)); }
        finally { _applyingGap = false; }
    }

    /// <summary>
    /// The plan this window's gap setting implies, by whichever of the two rules applies to the
    /// audio underneath it. Which one that is was decided when the window opened, by the caller that
    /// knew where the audio came from — a transferred side, or a set of separate files.
    /// </summary>
    private List<CdTrackPlan> PlanWithGap(double seconds) => _addOnlyGap
        ? CdTransfer.WithEvenPregaps(CurrentPlan(), seconds)
        : CdTransfer.ApplyGaps(
            _document.Doc.Channels.ToArray(), _document.Doc.SampleRate, CurrentPlan(),
            seconds, thresholdSlider.Value, Envelope());

    /// <summary>Puts a gap pass onto the rows, and says how many track boundaries it moved.</summary>
    private int ApplyGapToRows(IReadOnlyList<CdTrackPlan> planned)
    {
        int moved = 0;
        for (int i = 0; i < _tracks.Count && i < planned.Count; i++)
        {
            if (planned[i].SourceStart != _tracks[i].Plan.SourceStart ||
                planned[i].SourceEnd != _tracks[i].Plan.SourceEnd)
                moved++;
            _tracks[i].SetPlan(planned[i]);
        }
        return moved;
    }

    /// <summary>
    /// Set the silence between every pair of tracks to what the box says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This <b>trims the rows, visibly</b>, rather than doing something at export the user cannot
    /// see: an even gap means taking the record's own quiet off both ends of each split and putting
    /// back exactly what was asked for, so SOURCE IN and SOURCE OUT move and can be read and
    /// corrected. A gap arranged in secret at export would be a plan that does not describe the
    /// disc, which is the fault this window has been reported for four times.
    /// </para>
    /// <para>
    /// The quiet is judged at the AUTO SPLIT threshold — the level the user has already called
    /// quiet — so the two halves of the window cannot disagree about where a song ends.
    /// </para>
    /// </remarks>
    private void ApplyGap()
    {
        if (_busy || !_dialogReady || _applyingGap) return;

        string typed = gapBox.Text?.Trim() ?? "";
        if (typed.Length == 0) typed = "0";
        if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) &&
            !double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            gapBox.Text = $"{_gapSeconds:0.###}";
            statusText.Text = $"Enter a gap from 0 to {CdTransfer.MaximumGapSeconds:0} seconds.";
            return;
        }

        // Snapped to the only lengths a pregap can be, and shown snapped: rounding the entry to
        // tenths let 0.1 s through, which is seven and a half CD frames and reaches the disc as
        // eight, so the box said 0.1 and the disc got 0.107.
        seconds = CdTransfer.SnapGapSeconds(seconds);
        gapBox.Text = $"{seconds:0.###}";
        if (_tracks.Count == 0) { _gapSeconds = seconds; return; }

        _applyingGap = true;
        int moved;
        try { moved = ApplyGapToRows(PlanWithGap(seconds)); }
        finally { _applyingGap = false; }

        _gapSeconds = seconds;
        UpdatePlan();
        statusText.Text = CdTransfer.DescribeGap(seconds, _tracks.Count, moved, !_addOnlyGap);
    }

    /// <summary>
    /// Try every setting and use the one the tracks hold steadiest at, instead of leaving the user
    /// to hunt for it. See <see cref="CdTransfer.SweepTracks"/> for why that is findable at all.
    /// </summary>
    private async void OnFindTracks(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_document.Doc.Length == 0)
        {
            statusText.Text = "There is no audio to look through.";
            return;
        }

        int? target = TargetTracks(out bool rejected);
        if (rejected)
        {
            statusText.Text = $"Enter a track count from 1 to {CdTransfer.MaximumTracks}, or leave the box " +
                              "empty to take the steadiest answer.";
            return;
        }

        SetBusy(true, "Trying every setting to see which the tracks hold steadiest at...");
        _operation = new CancellationTokenSource();
        try
        {
            var channels = _document.Doc.Channels.ToArray();
            int rate = _document.Doc.SampleRate;
            int version = _document.Doc.EditVersion;
            CancellationToken token = _operation.Token;
            CdSplitSweep sweep = await Task.Run(
                () => CdTransfer.SweepTracks(channels, rate, target, token), token);

            RefuseAStaleProposal(version, "Press Find Tracks again.");
            if (sweep.Best is { } best)
            {
                // The slider moves first, so the label beside it and the line below the list are
                // describing the same setting by the time either is read.
                thresholdSlider.Value = Math.Clamp(
                    best.ChosenDb, thresholdSlider.Minimum, thresholdSlider.Maximum);
                ApplyProposal(CdTransfer.PlansFor(best), rate);
            }
            statusText.Text = CdTransfer.DescribeSweep(sweep, target);
        }
        catch (OperationCanceledException) { statusText.Text = "The search was cancelled."; }
        catch (Exception ex) { statusText.Text = ex.Message; }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
        }
    }

    /// <summary>
    /// The document's block-peak envelope, kept because the gap pass searches it instead of the
    /// samples.
    /// </summary>
    /// <remarks>
    /// Measuring it is one pass over the file, and it is dropped whenever the file changes, so a
    /// gap applied after an edit pays for it once. What it buys is that <see cref="RetrimForGap"/>
    /// - which every arrow press reaches through <see cref="RefreshOrder"/> - no longer walks the
    /// audio each time.
    /// </remarks>
    private float[] Envelope() =>
        _envelope ??= Restoration.BlockPeaks(_document.Doc.Channels.ToArray(), _document.Doc.SampleRate);

    /// <summary>Whichever of the two moved further from where it was, sign kept.</summary>
    private static int FurthestMove(int worst, int candidate) =>
        Math.Abs(candidate) > Math.Abs(worst) ? candidate : worst;

    /// <summary>
    /// How much one press takes when there is no selection to take instead. Three minutes is a song
    /// rather than a measurement; the In/Out fields are where it becomes the right length.
    /// </summary>
    private const double NewTrackBlockSeconds = 180;

    /// <summary>
    /// Add a track by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selection wins where there is one — that is the direct expression of "this part is a
    /// track". With nothing selected there are two other places a track can come from, and the
    /// order between them is the whole of what this button had wrong twice.
    /// </para>
    /// <para>
    /// The tracks here <b>tile the recording</b>: the analysis this window opens with proposes
    /// boundaries running <c>0 → …gaps… → end</c>, contiguously, so all of the file is claimed
    /// before the user has touched anything. Hunting for unclaimed space therefore found nothing in
    /// the ordinary flow and the button reported that everything was claimed — with an analysis that
    /// had found one gap, that is a list of two tracks and no way to reach a third. Unclaimed space
    /// only exists after a Remove or a shortened row, so it is checked first and is the exception.
    /// The rule is that another track comes out of an existing one: a block off its front, leaving
    /// the remainder as the next row, which is <see cref="DivideRow"/> — the same operation Split
    /// performs, at a fixed offset instead of at the cursor.
    /// </para>
    /// </remarks>
    private void OnAddTrack(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_tracks.Count >= CdTransfer.MaximumTracks)
        {
            statusText.Text = $"A CD holds at most {CdTransfer.MaximumTracks} tracks. Remove one before adding another.";
            return;
        }

        int rate = Math.Max(1, _document.Doc.SampleRate);
        int block = (int)Math.Round(NewTrackBlockSeconds * rate);
        int minimum = Math.Max(1, (int)Math.Ceiling(CdTransfer.MinimumTrackSeconds * rate));

        if (_document.HasSelection)
        {
            // Taken verbatim, short or overlapping. The user pointed at this range; if it cannot be
            // a CD track the validation line says why, which beats quietly adding a different one.
            AppendRow(_document.SelStart, _document.SelEnd, TrackOrigin.Selection);
            return;
        }

        if (_document.Doc.Length == 0)
        {
            statusText.Text = "There is no audio here to make a track from.";
            return;
        }

        (int gapStart, int gapEnd) = LargestUnclaimedSpan();
        if (gapEnd - gapStart >= minimum)
        {
            int end = gapStart + block;
            // Take the whole stretch rather than leave a scrap behind it too short to be a track of
            // its own — a remainder under the CD minimum is unusable, and a press that produced one
            // would need a second press to undo it.
            if (end >= gapEnd || gapEnd - end < minimum) end = gapEnd;
            AppendRow(gapStart, end, end == gapEnd ? TrackOrigin.RestOfGap : TrackOrigin.StartOfGap);
            return;
        }

        TrackRow? target = trackList.SelectedItem as TrackRow ?? LongestRow();
        if (target == null)
        {
            statusText.Text = "There are no tracks to divide yet. " +
                "Press Analyze, or select part of the waveform and press Add Track again.";
            return;
        }

        int targetOrder = target.Order;
        TrackRow? remainder = DivideRow(target, target.Plan.SourceStart + block, $"Track {target.Order + 1:00}");
        if (remainder == null)
        {
            statusText.Text = DescribeTooShort(targetOrder, "divide");
            return;
        }
        statusText.Text = DescribeAddedByDividing(targetOrder, target.SecondsEnd, remainder.Order);
    }

    /// <summary>Where a hand-added track came from, for the line that reports it.</summary>
    internal enum TrackOrigin { Selection, RestOfGap, StartOfGap }

    /// <summary>Put a new row after the selected one, or at the end, and select it.</summary>
    private void AppendRow(int start, int end, TrackOrigin origin)
    {
        int index = trackList.SelectedItem is TrackRow selected ? _tracks.IndexOf(selected) + 1 : _tracks.Count;
        TrackRow row = NewRow(new CdTrackPlan(start, end, $"Track {index + 1:00}"), index + 1);
        _tracks.Insert(index, row);
        RefreshOrder();
        // Selecting the new row is also what makes the next press land after it rather than beside
        // it, so repeated presses come out in source order without the user reaching for ▲▼.
        trackList.SelectedItem = row;
        trackList.ScrollIntoView(row);
        statusText.Text = DescribeAddedTrack(row.Order, row.SecondsStart, row.SecondsEnd, origin);
    }

    // ── what the list operations say they did ────────────────────
    //
    // Same rule as CdTransfer.DescribeProposal, and for the same report: name what is on screen in
    // ordinary words, then name the next thing to do. These three lines were the ones left in the
    // old voice — "off the unclaimed stretch", "fine-tune the boundary", "Synchronized 3 arranged
    // track region(s)" — naming things by what they are called in the source rather than by what
    // the user is looking at. Pure and internal so the wording is tested and measured without a
    // window, which is how DescribeOutputMix and DescribeNoiseDepth are arranged.

    /// <summary>
    /// A position in the status line, which is prose. The In and Out cells carry milliseconds
    /// because a boundary is exact; a sentence about one does not need them.
    /// </summary>
    private static string At(double seconds) => TimeFormat.Compact(seconds);

    internal static string DescribeAddedTrack(int order, double start, double end, TrackOrigin origin)
    {
        string from = origin switch
        {
            TrackOrigin.Selection => "taken from what you had selected",
            TrackOrigin.RestOfGap => "filling the stretch no track was using",
            _ => "off the front of the stretch no track was using",
        };
        return $"Track {order:00} added, {At(start)} to {At(end)}, {from}. " +
               "Use its SOURCE IN and SOURCE OUT boxes to move it.";
    }

    /// <summary>
    /// Add Track and Split share <see cref="DivideRow"/>, and the button pressed is not the same
    /// question as the operation performed: pressing Add Track and being told "Split at 0:31" is
    /// the readout describing the code rather than the press.
    /// </summary>
    internal static string DescribeAddedByDividing(int fromOrder, double splitAt, int newOrder) =>
        $"Track {newOrder:00} added by dividing track {fromOrder:00} at {At(splitAt)}. " +
        "Use their SOURCE IN and SOURCE OUT boxes to move the split.";

    internal static string DescribeSplitTrack(int order, double splitAt, int newOrder) =>
        $"Split at {At(splitAt)} - track {order:00} ends there and track {newOrder:00} starts. " +
        "Use their SOURCE IN and SOURCE OUT boxes to move it.";

    /// <summary>
    /// Why a row will not divide, which is a rule about CDs rather than about this program: both
    /// halves have to clear the minimum a disc can hold. Naming the number is the difference
    /// between a refusal and an explanation.
    /// </summary>
    internal static string DescribeTooShort(int order, string verb) =>
        $"Track {order:00} is too short to {verb} - each half would be under the " +
        $"{CdTransfer.MinimumTrackSeconds:0} seconds a CD track has to run for.";

    /// <summary>
    /// Save Track List writes the track list onto the waveform as named regions, which is where
    /// they are visible and what the sidecar saves. "Synchronized 3 arranged track region(s)"
    /// described the operation, in the word the code uses for the thing it wrote; this describes
    /// what the user now has, in the word on the label.
    /// </summary>
    internal static string DescribeRegionSync(int tracks, int untouched, bool changed)
    {
        if (!changed) return "The regions on the waveform already match this track list.";
        string rest = untouched switch
        {
            0 => "",
            1 => " One other region was left alone.",
            _ => $" {untouched} other regions were left alone.",
        };
        return $"Marked {(tracks == 1 ? "1 track" : $"{tracks} tracks")} on the waveform.{rest}";
    }

    /// <summary>The row covering the most of the recording, or null when the list is empty.</summary>
    private TrackRow? LongestRow()
    {
        TrackRow? longest = null;
        foreach (TrackRow row in _tracks)
            if (longest == null || row.Plan.Length > longest.Plan.Length) longest = row;
        return longest;
    }

    /// <summary>
    /// Divide one row in two at <paramref name="split"/> and select the right-hand half — which is
    /// what makes a repeated Add Track march forward through the recording rather than subdivide the
    /// same head over and over. <paramref name="split"/> is clamped to the row's midpoint where
    /// either half would otherwise fall under the CD minimum. Null when the row cannot be divided.
    /// </summary>
    private TrackRow? DivideRow(TrackRow row, int split, string rightTitle)
    {
        CdTrackPlan plan = row.ToPlan();
        int minimum = Math.Max(1, (int)Math.Ceiling(CdTransfer.MinimumTrackSeconds * row.SampleRate));
        if (plan.Length < minimum * 2) return null;
        if (split - plan.SourceStart < minimum || plan.SourceEnd - split < minimum)
            split = plan.SourceStart + plan.Length / 2;

        int index = _tracks.IndexOf(row);
        row.SetRange(plan.SourceStart, split);

        // The right-hand half inherits performer, songwriter and pre-emphasis — the same record made
        // them — but not the ISRC, which identifies one recording. Two tracks cannot both carry it.
        TrackRow right = NewRow(plan with
        {
            SourceStart = split,
            Title = rightTitle,
            Isrc = string.Empty,
        }, index + 2);
        _tracks.Insert(index + 1, right);
        RefreshOrder();
        trackList.SelectedItem = right;
        trackList.ScrollIntoView(right);
        return right;
    }

    /// <summary>
    /// The longest run of the recording that no track's source range covers. Ranges may overlap and
    /// need not be in order, so they are swept rather than assumed to be a tidy sequence.
    /// </summary>
    private (int Start, int End) LargestUnclaimedSpan()
    {
        int length = _document.Doc.Length;
        if (length <= 0) return (0, 0);

        var claimed = _tracks
            .Select(t => (Start: Math.Clamp(t.Plan.SourceStart, 0, length), End: Math.Clamp(t.Plan.SourceEnd, 0, length)))
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ToList();

        int bestStart = 0, bestEnd = 0, covered = 0;
        foreach ((int start, int end) in claimed)
        {
            if (start > covered && start - covered > bestEnd - bestStart) (bestStart, bestEnd) = (covered, start);
            covered = Math.Max(covered, end);
        }
        if (length - covered > bestEnd - bestStart) (bestStart, bestEnd) = (covered, length);
        return (bestStart, bestEnd);
    }

    private void OnRangeEditCommitted(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not TextBox editor || editor.DataContext is not TrackRow row) return;
        bool editsStart = string.Equals(editor.Name, "startEditor", StringComparison.Ordinal);
        string displayedPosition = editsStart ? row.StartText : row.EndText;
        if (string.Equals(editor.Text?.Trim(), displayedPosition, StringComparison.Ordinal))
        {
            // TimeFormat intentionally displays milliseconds, while region boundaries
            // can carry finer sample precision. Leaving an untouched editor must not
            // quantize that exact boundary merely because the control lost focus.
            editor.Text = displayedPosition;
            return;
        }
        if (!TryParsePosition(editor.Text, row.SampleRate, out int sample))
        {
            editor.Text = displayedPosition;
            statusText.Text = "Enter a position as seconds, mm:ss, or hh:mm:ss.mmm.";
            return;
        }

        int start = editsStart ? sample : row.Plan.SourceStart;
        int end = editsStart ? row.Plan.SourceEnd : sample;
        if (start < 0 || end > _document.Doc.Length || start >= end)
        {
            editor.Text = displayedPosition;
            statusText.Text = "Track In must be before Out, and both must stay inside the recording.";
            return;
        }

        row.SetRange(start, end);
        editor.Text = editsStart ? row.StartText : row.EndText;
        statusText.Text = $"Adjusted track {row.Order:00} to {row.DurationText}.";
        UpdatePlan();
    }

    private static bool TryParsePosition(string? text, int sampleRate, out int sample)
    {
        sample = 0;
        if (sampleRate <= 0 || string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;

        static bool TryNumber(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        static bool TryWholeNumber(string value, out double result) =>
            TryNumber(value, out result) && result == Math.Truncate(result);

        double seconds;
        if (parts.Length == 1)
        {
            if (!TryNumber(parts[0], out seconds)) return false;
        }
        else
        {
            if (!TryNumber(parts[^1], out double secondPart) || secondPart < 0 || secondPart >= 60)
                return false;
            if (!TryWholeNumber(parts[^2], out double minutePart) || minutePart < 0 ||
                (parts.Length == 3 && minutePart >= 60))
                return false;
            double hourPart = 0;
            if (parts.Length == 3 && (!TryWholeNumber(parts[0], out hourPart) || hourPart < 0))
                return false;
            seconds = hourPart * 3600 + minutePart * 60 + secondPart;
        }

        if (!double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue / (double)sampleRate)
            return false;
        sample = (int)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);
        return true;
    }

    private void OnSplitSelected(object sender, RoutedEventArgs e)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        CdTrackPlan plan = row.ToPlan();
        int order = row.Order;
        // The cursor is live now that the window is modeless, so this is the boundary on screen
        // rather than wherever the playhead happened to be before it opened.
        int split = _document.Cursor > plan.SourceStart && _document.Cursor < plan.SourceEnd
            ? _document.Cursor
            : plan.SourceStart + plan.Length / 2;

        TrackRow? remainder = DivideRow(row, split, $"{plan.Title} B");
        if (remainder == null)
        {
            statusText.Text = DescribeTooShort(order, "split");
            return;
        }
        statusText.Text = DescribeSplitTrack(order, row.SecondsEnd, remainder.Order);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        int from = _tracks.IndexOf(row), to = from + delta;
        if (to < 0 || to >= _tracks.Count) return;
        _tracks.Move(from, to);
        RefreshOrder();
        trackList.SelectedItem = row;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        int index = _tracks.IndexOf(row);
        row.PropertyChanged -= OnTrackRowChanged;
        _tracks.Remove(row);
        RefreshOrder();
        if (_tracks.Count > 0) trackList.SelectedIndex = Math.Min(index, _tracks.Count - 1);
    }

    private void RefreshOrder()
    {
        for (int i = 0; i < _tracks.Count; i++) _tracks[i].Order = i + 1;
        RetrimForGap();
        UpdatePlan();
    }

    private void OnSaveRegions(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var existing = _document.Regions.ToList();
        var consumed = new HashSet<NamedRegion>(ReferenceEqualityComparer.Instance);
        var synchronized = new List<NamedRegion>(_tracks.Count + existing.Count);
        int added = 0;
        int updated = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            TrackRow row = _tracks[i];
            CdTrackPlan plan = row.ToPlan();
            NamedRegion? region = row.SourceRegion;
            if (region == null || !existing.Contains(region) || consumed.Contains(region))
            {
                region = new NamedRegion
                {
                    Name = plan.Title,
                    Start = plan.SourceStart,
                    End = plan.SourceEnd,
                    CdTrackOrder = i + 1,
                };
                row.BindRegion(region);
                _knownPlanRegions.Add(region);
                added++;
            }
            else
            {
                consumed.Add(region);
                if (!string.Equals(region.Name, plan.Title, StringComparison.Ordinal) ||
                    region.Start != plan.SourceStart || region.End != plan.SourceEnd ||
                    region.CdTrackOrder != i + 1)
                {
                    region.Name = plan.Title;
                    region.Start = plan.SourceStart;
                    region.End = plan.SourceEnd;
                    region.CdTrackOrder = i + 1;
                    updated++;
                }
            }
            synchronized.Add(region);
        }

        // Former plan regions omitted by Remove or superseded by Analyze are
        // intentionally dropped. Untagged regions that never participated in this
        // dialog remain independent annotations and keep their relative order.
        var preservedRegions = existing.Where(region => !consumed.Contains(region) &&
            !_knownPlanRegions.Contains(region) && region.CdTrackOrder is not > 0).ToList();
        int removed = existing.Count - consumed.Count - preservedRegions.Count;
        synchronized.AddRange(preservedRegions);
        bool reordered = synchronized.Count != _document.Regions.Count ||
            !_document.Regions.SequenceEqual(synchronized, ReferenceEqualityComparer.Instance);
        if (reordered)
        {
            _document.Regions.Clear();
            foreach (var region in synchronized) _document.Regions.Add(region);
        }

        if (added > 0 || updated > 0 || removed > 0 || reordered) _document.NotifyMarkersChanged();
        statusText.Text = DescribeRegionSync(
            _tracks.Count, preservedRegions.Count,
            changed: added > 0 || updated > 0 || removed > 0 || reordered);
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        _main.StopPreview();
        SetBusy(true, "Preparing a bounded track preview...");
        _operation = new CancellationTokenSource();
        try
        {
            var plan = row.ToPlan();
            int maximum = Math.Max(1, _document.Doc.SampleRate * 15);
            int count = Math.Min(plan.Length, maximum);
            int start = plan.SourceStart;
            if (_document.Cursor >= plan.SourceStart && _document.Cursor < plan.SourceEnd)
                start = Math.Clamp(_document.Cursor - count / 3, plan.SourceStart, plan.SourceEnd - count);

            bool renderRack = renderRackCheck.IsChecked == true;
            int version = _document.Doc.EditVersion;
            int sampleRate = _document.Doc.SampleRate;
            float[][] source = _document.Doc.Channels.ToArray();
            CancellationToken token = _operation.Token;
            IProgress<double>? rackProgress = renderRack
                ? new Progress<double>(fraction =>
                {
                    progressBar.Value = fraction;
                    statusText.Text = $"Warming the rack from the program start — {fraction:P0}";
                })
                : null;
            float[][] data = await Task.Run(() => renderRack
                    ? _main.Engine.Master.ProcessOfflineRange(
                        source, sampleRate, start, count, token, rackProgress)
                    : CopyRange(source, start, count, token), token);
            if (version != _document.Doc.EditVersion)
                throw new InvalidOperationException("The source changed while the preview was rendering. Try Preview again.");

            var preview = new AudioDocument(data, sampleRate,
                renderRack ? 32 : _document.Doc.SourceBitDepth)
            {
                Title = $"Preview - {plan.Title}",
            };
            // The rack render is already baked into this transient document. Bypass
            // the live rack during playback so the audition is not processed twice.
            // PlayPreview returns false when a transport recording is active/pending or the
            // engine is awaiting recovery — reporting "Previewing…" then would be a lie.
            if (!_main.PlayPreview(preview, loop: false, bypassRack: true))
                statusText.Text = "Preview is unavailable while recording audio is active or awaiting recovery.";
            else
                statusText.Text = renderRack
                    ? $"Previewing {plan.Title} from the same continuous rack render used for export."
                    : $"Previewing the dry source for {plan.Title}; export will also remain dry.";
        }
        catch (OperationCanceledException) { statusText.Text = "Preview preparation cancelled."; }
        catch (Exception ex) { statusText.Text = ex.Message; }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var plan = CurrentPlan();
        var errors = CdTransfer.Validate(plan, _document.Doc.SampleRate, _document.Doc.Length)
            .Where(i => i.Severity == CdPlanIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(i => "- " + i.Message)),
                "CD plan needs attention", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool ddp = Ddp;
        bool imageCue = ImageCue;
        var picker = new OpenFolderDialog
        {
            Title = ddp
                ? "Choose a new or empty folder for the DDP image set"
                : imageCue
                    ? "Choose a new or empty folder for the CD image and cue sheet"
                    : "Choose a new or empty folder for the CD package",
        };
        if (picker.ShowDialog(this) != true) return;

        _main.StopPreview();
        SetBusy(true, "Preparing CD-compatible tracks...");
        _operation = new CancellationTokenSource();
        closeBtn.Content = "Cancel";
        try
        {
            CancellationToken token = _operation.Token;
            bool renderRack = renderRackCheck.IsChecked == true;
            AudioDocument exportSource = _document.Doc;
            double packageStart = renderRack ? 0.15 : 0;

            if (renderRack)
            {
                int version = _document.Doc.EditVersion;
                int sampleRate = _document.Doc.SampleRate;
                float[][] stableSource = _document.Doc.Channels.ToArray();
                var rackProgress = new Progress<double>(fraction =>
                {
                    progressBar.Value = fraction * packageStart;
                    statusText.Text = $"Rendering the current rack - {fraction:P0}";
                });
                float[][] rendered = await Task.Run(() =>
                    _main.Engine.Master.ProcessOffline(stableSource, sampleRate, token, rackProgress), token);
                if (version != _document.Doc.EditVersion)
                    throw new InvalidOperationException("The source changed while the rack was rendering. Start the package again.");
                exportSource = new AudioDocument(rendered, sampleRate, sourceBitDepth: 32)
                {
                    Title = _document.Doc.Title + " - CD master",
                };
            }

            var progress = new Progress<CdPackageProgress>(p =>
            {
                progressBar.Value = packageStart + p.Fraction * (1 - packageStart);
                statusText.Text =
                    p.CompletedTracks >= p.TotalTracks
                        && p.CurrentTrack != CdPackageProgress.WritingImageStage
                    ? "CD package complete."
                    : p.CurrentTrack is CdPackageProgress.ConvertingStage
                                     or CdPackageProgress.WritingImageStage
                        ? $"{p.CurrentTrack} - {p.Fraction:P0}"
                        : $"Preparing {p.CurrentTrack} - {Math.Min(p.CompletedTracks + 1, p.TotalTracks)} of {p.TotalTracks}";
            });
            if (ddp)
            {
                var disc = new DdpDiscInfo(discTitle.Text, discPerformer.Text, discUpc.Text);
                DdpResult image = await CdTransfer.ExportDdpAsync(exportSource, plan, picker.FolderName,
                    disc, progress, token);
                progressBar.Value = 1;
                InfoDialog.Show(this, "DDP Image Set Ready",
                    $"Wrote a {image.Tracks}-track DDP 2.00 image set: {image.ImageBytes / (1024.0 * 1024):0.0} MB of " +
                    "audio plus its PQ descriptor, CD-TEXT and checksum. Send the whole folder to the plant.\n\n" +
                    $"IMAGE.DAT MD5  {image.ImageMd5}",
                    image.Folder);
            }
            else if (imageCue)
            {
                var result = await CdTransfer.ExportImageAsync(exportSource, plan, picker.FolderName,
                    discTitle.Text, discPerformer.Text, progress, token);
                progressBar.Value = 1;
                InfoDialog.Show(this, "CD Image Ready",
                    $"Wrote the whole programme as one 16-bit WAV of {new FileInfo(result.WaveFiles[0]).Length / (1024.0 * 1024):0.0} MB " +
                    $"and a CUE sheet indexing {plan.Count} track(s) by their position on the disc. " +
                    "Open the CUE file in your preferred disc-burning application.",
                    result.CueFile);
            }
            else
            {
                var result = await CdTransfer.ExportPackageAsync(exportSource, plan, picker.FolderName,
                    discTitle.Text, discPerformer.Text, progress, token);
                progressBar.Value = 1;
                InfoDialog.Show(this, "CD Package Ready",
                    $"Created {result.WaveFiles.Count} sector-aligned CD WAV file(s) and a CUE sheet. Open the CUE file in your preferred disc-burning application.",
                    result.CueFile);
            }
        }
        catch (OperationCanceledException) { statusText.Text = "CD package export cancelled; staged files were removed."; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CD package failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            statusText.Text = "No completed package was published.";
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            closeBtn.Content = "Close";
            SetBusy(false, statusText.Text);
        }
    }

    private List<CdTrackPlan> CurrentPlan() => _tracks.Select(t => t.ToPlan()).ToList();

    private void UpdatePlan()
    {
        List<CdTrackPlan> plan = CurrentPlan();
        var issues = CdTransfer.Validate(plan, _document.Doc.SampleRate, _document.Doc.Length);

        // Where each track actually lands once the plan is sector-aligned. This is what a plant
        // reads, and it is not the source duration — the alignment moves both boundaries.
        CdPqLayout layout = CdTransfer.PqSheet(plan, _document.Doc.SampleRate, _document.Doc.Length);
        for (int i = 0; i < _tracks.Count; i++)
            _tracks[i].CdLengthText = i < layout.Tracks.Count ? layout.Tracks[i].LengthTimecode : "—";

        var issue = issues.FirstOrDefault(i => i.Severity == CdPlanIssueSeverity.Error)
                    ?? issues.FirstOrDefault(i => i.Severity == CdPlanIssueSeverity.Warning)
                    ?? issues.FirstOrDefault();
        string message = issue?.Message ?? "";
        var severity = issue?.Severity ?? CdPlanIssueSeverity.Information;

        if (Ddp)
        {
            int bad = _tracks.Count(t => !t.IsrcAcceptable);
            int set = _tracks.Count(t => Isrc.Normalise(t.Isrc).Length == Isrc.Length);
            string upc = new DdpDiscInfo(discTitle.Text, Upc: discUpc.Text).NormalisedUpc;
            bool upcTyped = !string.IsNullOrWhiteSpace(discUpc.Text);

            // A catalogue number that is nearly right is worse than one that is absent: it will be
            // omitted from the sheet rather than written short, and the user should hear that now.
            if (bad > 0 && severity != CdPlanIssueSeverity.Error)
            {
                message = bad == 1
                    ? "One ISRC is not twelve characters, so it will be left out rather than written short."
                    : $"{bad} ISRCs are not twelve characters, so they will be left out rather than written short.";
                severity = CdPlanIssueSeverity.Warning;
            }
            else if (upcTyped && upc.Length == 0 && severity != CdPlanIssueSeverity.Error)
            {
                message = "The UPC/EAN is not twelve or thirteen digits, so it will be left out.";
                severity = CdPlanIssueSeverity.Warning;
            }
            else if (severity == CdPlanIssueSeverity.Information)
            {
                message = $"{message} Lead-out at {layout.LeadOutTimecode}. {set} of {_tracks.Count} ISRCs set.";
            }
        }

        validationText.Text = message;
        validationText.Foreground = severity switch
        {
            CdPlanIssueSeverity.Error => (Brush)FindResource("Red"),
            CdPlanIssueSeverity.Warning => (Brush)FindResource("Amber"),
            _ => (Brush)FindResource("Muted"),
        };
        exportBtn.IsEnabled = !_busy && !issues.Any(i => i.Severity == CdPlanIssueSeverity.Error);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        trackList.IsEnabled = !busy;
        discTitle.IsEnabled = !busy;
        discPerformer.IsEnabled = !busy;
        discUpc.IsEnabled = !busy && Ddp;
        importIsrcBtn.IsEnabled = autoNumberBtn.IsEnabled = !busy && Ddp;
        wavCueBtn.IsEnabled = imageCueBtn.IsEnabled = ddpBtn.IsEnabled = !busy;
        thresholdSlider.IsEnabled = !busy;
        gapBox.IsEnabled = !busy;
        analyzeBtn.IsEnabled = !busy;
        findTracksBtn.IsEnabled = !busy;
        trackCountBox.IsEnabled = !busy;
        addBtn.IsEnabled = !busy;
        saveRegionsBtn.IsEnabled = !busy;
        renderRackCheck.IsEnabled = !busy;
        previewBtn.IsEnabled = !busy && trackList.SelectedItem != null;
        upBtn.IsEnabled = downBtn.IsEnabled = removeBtn.IsEnabled = splitBtn.IsEnabled =
            !busy && trackList.SelectedItem != null;
        statusText.Text = status;
        UpdatePlan();
        if (!busy) progressBar.Value = 0;
        if (!busy && _closeWhenFinished)
        {
            _closeWhenFinished = false;
            Close();
        }
    }

    private void OnTrackSelected(object sender, SelectionChangedEventArgs e)
    {
        bool selected = trackList.SelectedItem != null && !_busy;
        previewBtn.IsEnabled = upBtn.IsEnabled = downBtn.IsEnabled = removeBtn.IsEnabled =
            splitBtn.IsEnabled = selected;
    }

    /// <summary>
    /// The label prints whole decibels, so the control holds them. Rounding here rather than
    /// relying on <c>IsSnapToTickEnabled</c>, which WPF applies to a thumb drag and not to a value
    /// set any other way — so a slider moved from code sat at −45.4 dB under a label reading
    /// "−45 dB", and analysed at the figure nobody was shown. Setting Value re-enters this handler
    /// once with an already-round number, which then falls through.
    /// </summary>
    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double whole = Math.Round(e.NewValue);
        if (whole != e.NewValue)
        {
            thresholdSlider.Value = whole;
            return;
        }
        if (thresholdText != null) thresholdText.Text = $"{whole:0} dB";
    }

    private void OnPlanChanged(object sender, TextChangedEventArgs e)
    {
        if (validationText != null) UpdatePlan();
    }

    private void OnRenderRackChanged(object sender, RoutedEventArgs e)
    {
        if (!_dialogReady || _syncingRack) return;
        _main.StopPreview();
        _main.Master.RackEnabled = renderRackCheck.IsChecked == true;
        statusText.Text = renderRackCheck.IsChecked == true
            ? "The current rack will be heard in Preview and rendered into the CD files."
            : "Preview and export are both using the dry, unprocessed source.";
    }

    private void OnDialogClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        // Remember the request: SetBusy re-issues the close once the work unwinds,
        // otherwise the user has to click X a second time.
        _closeWhenFinished = true;
        _operation?.Cancel();
        statusText.Text = "Cancelling the current operation...";
    }

    private static float[][] CopyRange(
        IReadOnlyList<float[]> source, int start, int count, CancellationToken cancellationToken)
    {
        const int block = 1 << 20;
        var output = new float[source.Count][];
        for (int c = 0; c < source.Count; c++)
        {
            output[c] = new float[count];
            for (int offset = 0; offset < count; offset += block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(block, count - offset);
                Array.Copy(source[c], start + offset, output[c], offset, n);
            }
        }
        return output;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy) _operation?.Cancel();
        else Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
