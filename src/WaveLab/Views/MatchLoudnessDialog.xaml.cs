using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>
/// Levels several open tabs to a common programme loudness, and says what it did to each.
/// </summary>
/// <remarks>
/// <para>
/// Three phases, deliberately separate. <b>Measure</b> reads every ticked tab once and keeps the
/// numbers. <b>Recompute</b> turns those numbers into a plan and is pure and instant, so changing
/// the mode from "the quietest track" to "−14 LUFS" costs nothing and never re-reads the audio.
/// <b>Apply</b> commits one undoable gain per document.
/// </para>
/// <para>
/// Both long phases run on a worker. A whole-file gain is a few hundred megabytes of copying on a
/// side of vinyl and this does it to every open tab, which is exactly the freeze the channel tools
/// were already moved off the dispatcher to avoid; the commit itself is a pointer swap through
/// <see cref="AudioDocument.ReplaceAllOwned"/>, which retains the outgoing arrays rather than
/// copying them.
/// </para>
/// <para>
/// Measuring is sequential rather than parallel. Each meter carries its own ring buffers, the
/// progress split assumes one item at a time, and a cancelled parallel run leaves a table that is
/// partly from this measurement and partly from the last one.
/// </para>
/// <para>
/// Apply is all or nothing. A part-applied loudness match is a worse state to be left holding than
/// an unapplied one — half the record moved and no record of which half — so anything that goes
/// wrong part way through undoes what has already been committed.
/// </para>
/// </remarks>
public partial class MatchLoudnessDialog : Window
{
    /// <summary>One tab in the table: what it measured, and what the current plan gives it.</summary>
    internal sealed class TrackRow : ObservableObject
    {
        private bool _isSelected = true;
        private string _measuredText = "—";
        private string _truePeakText = "—";
        private string _gainText = "—";
        private string _resultText = "—";
        private string _note = "not measured";
        private Brush _noteBrush = Brushes.Gray;

        public required DocumentViewModel Document { get; init; }
        public required string Title { get; init; }

        /// <summary>What was measured, or null while it has not been.</summary>
        public LoudnessMeasurement? Measurement { get; set; }

        /// <summary>The document's edit version at the moment it was measured.</summary>
        public int MeasuredEditVersion { get; set; } = -1;

        /// <summary>The gain the current plan gives this track, or null when it gets none.</summary>
        public double? PlannedGainDb { get; set; }

        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        public string MeasuredText { get => _measuredText; set => Set(ref _measuredText, value); }
        public string TruePeakText { get => _truePeakText; set => Set(ref _truePeakText, value); }
        public string GainText { get => _gainText; set => Set(ref _gainText, value); }
        public string ResultText { get => _resultText; set => Set(ref _resultText, value); }
        public string Note { get => _note; set => Set(ref _note, value); }
        public Brush NoteBrush { get => _noteBrush; set => Set(ref _noteBrush, value); }
    }

    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0x9E));
    private static readonly Brush FaintBrush = new SolidColorBrush(Color.FromRgb(0x5D, 0x64, 0x6D));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x3C));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD0, 0x7A));

    private readonly ObservableCollection<TrackRow> _rows = [];
    private readonly ObservableCollection<TrackRow> _referenceChoices = [];
    private readonly IReadOnlyList<DocumentViewModel> _documents;
    private readonly Action<DocumentViewModel>? _prepareForEdit;
    private CancellationTokenSource? _cts;
    private LoudnessMatchPlan? _plan;
    private TrackRow? _reference;

    /// <summary>
    /// True from construction, because the XAML sets the mode combo's initial selection part way
    /// through <c>InitializeComponent</c> — the handler would then run against controls further down
    /// the file that have not been created yet.
    /// </summary>
    private bool _suspendRecompute = true;
    private bool _busy;
    private string _phase = "";
    private bool _closeWhenFinished;

    static MatchLoudnessDialog()
    {
        MutedBrush.Freeze();
        FaintBrush.Freeze();
        AmberBrush.Freeze();
        GreenBrush.Freeze();
    }

    /// <param name="documents">The open audio tabs, in tab order.</param>
    /// <param name="prepareForEdit">
    /// Called for each document immediately before its gain is committed, so the shell can release
    /// playback. Optional, which is what lets the dialog be constructed in a test with no shell.
    /// </param>
    public MatchLoudnessDialog(
        IReadOnlyList<DocumentViewModel> documents,
        Action<DocumentViewModel>? prepareForEdit = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        InitializeComponent();
        _documents = documents;
        _prepareForEdit = prepareForEdit;

        foreach (var target in LoudnessTarget.All)
        {
            targetCombo.Items.Add(
                $"{target.Name} — {target.IntegratedLufs:0.0} LUFS, ≤ {target.TruePeakDbtp:0.0} dBTP");
        }
        targetCombo.SelectedIndex = LoudnessTarget.All.Count > 3 ? 3 : 0;

        foreach (var document in documents)
            _rows.Add(new TrackRow { Document = document, Title = document.Doc.Title });
        trackItems.ItemsSource = _rows;
        referenceCombo.ItemsSource = _referenceChoices;
        _suspendRecompute = false;

        RebuildReferenceList();
        Recompute();
    }

    /// <summary>One line describing what was applied, for the shell's status line. Null until applied.</summary>
    public string? ResultSummary { get; private set; }

    /// <summary>The rows, for tests.</summary>
    internal IReadOnlyList<TrackRow> Rows => _rows;

    /// <summary>Whether a measurement or an apply is running, for tests.</summary>
    internal bool Busy => _busy;

    /// <summary>Starts the measurement pass, for tests.</summary>
    internal void Measure() => OnMeasure(this, new RoutedEventArgs());

    private LoudnessMatchMode Mode => modeCombo.SelectedIndex switch
    {
        1 => LoudnessMatchMode.Quietest,
        2 => LoudnessMatchMode.Average,
        3 => LoudnessMatchMode.Reference,
        _ => LoudnessMatchMode.Target,
    };

    private LoudnessTarget Target =>
        LoudnessTarget.All[Math.Clamp(targetCombo.SelectedIndex, 0, LoudnessTarget.All.Count - 1)];

    // ── plan ────────────────────────────────────────────────────

    private void OnPlanInputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendRecompute) return;
        if (ReferenceEquals(sender, referenceCombo)) _reference = referenceCombo.SelectedItem as TrackRow;
        Recompute();
    }

    private void OnTrackTicked(object sender, RoutedEventArgs e)
    {
        if (_suspendRecompute) return;
        RebuildReferenceList();
        Recompute();
    }

    /// <summary>
    /// Refills the reference list with the tracks that could serve as one.
    /// </summary>
    /// <remarks>
    /// Keyed on the row rather than its title: two tabs can carry the same name — the same file
    /// opened twice, or two untitled recordings — and matching by text would silently point the
    /// reference at a different track. When the chosen one is no longer eligible the caller is told,
    /// because the level everything else is being moved to has just changed.
    /// </remarks>
    private void RebuildReferenceList()
    {
        bool suspended = _suspendRecompute;
        _suspendRecompute = true;
        try
        {
            _referenceChoices.Clear();
            foreach (var row in _rows)
                if (row.IsSelected && row.Measurement != null) _referenceChoices.Add(row);

            if (_reference != null && _referenceChoices.Contains(_reference))
            {
                referenceCombo.SelectedItem = _reference;
                return;
            }

            _reference = _referenceChoices.Count > 0 ? _referenceChoices[0] : null;
            referenceCombo.SelectedItem = _reference;
        }
        finally
        {
            _suspendRecompute = suspended;
        }
    }

    /// <summary>
    /// Rebuilds the plan from what has already been measured. Pure — it never touches audio, which
    /// is why every control on the strip can call it directly.
    /// </summary>
    private void Recompute()
    {
        bool relative = Mode != LoudnessMatchMode.Target;
        targetCombo.IsEnabled = !relative;
        targetHint.Text = relative
            ? $"ceiling {LoudnessMatch.RelativeCeilingDbtp:0.0} dBTP in the relative modes"
            : "loudness and true peak both come from this specification";
        referenceCombo.IsEnabled = Mode == LoudnessMatchMode.Reference;
        referenceHint.Text = Mode == LoudnessMatchMode.Reference
            ? "left exactly as it is; everything else moves to meet it"
            : "used only when matching to a reference track";

        var measured = new List<LoudnessMeasurement>();
        var measuredRows = new List<TrackRow>();
        foreach (var row in _rows)
        {
            row.PlannedGainDb = null;
            if (!row.IsSelected)
            {
                Describe(row, "not included", FaintBrush);
                continue;
            }
            if (row.Measurement is not { } m)
            {
                Describe(row, "not measured", FaintBrush);
                continue;
            }
            measured.Add(m);
            measuredRows.Add(row);
        }

        if (measured.Count == 0)
        {
            _plan = null;
            statusText.Text = _rows.Any(r => r.IsSelected)
                ? "Nothing measured yet — press Measure."
                : "Tick the tracks to level, then measure.";
            UpdateActions();
            return;
        }

        int referenceIndex = Mode == LoudnessMatchMode.Reference && _reference != null
            ? measuredRows.IndexOf(_reference)
            : -1;
        var plan = LoudnessMatch.Plan(measured, Mode, Target, referenceIndex);
        _plan = plan;

        for (int i = 0; i < measuredRows.Count; i++)
        {
            var row = measuredRows[i];
            var step = plan.Steps[i];
            row.MeasuredText = Level(step.Measurement.IntegratedLufs, "LUFS");
            row.TruePeakText = Level(step.Measurement.TruePeakDbtp, "dBTP");
            row.GainText = step.CanApply ? $"{step.GainDb:+0.0;-0.0}" : "—";
            row.ResultText = step.CanApply ? Level(step.ResultingLufs, "LUFS") : "—";
            row.Note = step.Note;
            row.NoteBrush = step.Note == "reference" ? GreenBrush
                : step.ShortfallDb > LoudnessMatch.NegligibleGainDb ? AmberBrush
                : step.CanApply ? MutedBrush
                : FaintBrush;
            row.PlannedGainDb = step.CanApply ? step.GainDb : null;
        }

        statusText.Text = plan.Summary;
        UpdateActions();
    }

    /// <summary>Blanks a row that is not part of the current plan, and says which of the two it is.</summary>
    private static void Describe(TrackRow row, string note, Brush brush)
    {
        row.MeasuredText = "—";
        row.TruePeakText = "—";
        row.GainText = "—";
        row.ResultText = "—";
        row.Note = note;
        row.NoteBrush = brush;
    }

    private static string Level(double value, string unit) =>
        double.IsFinite(value) ? $"{value:0.0} {unit}" : "—";

    private void UpdateActions()
    {
        bool anyTicked = _rows.Any(r => r.IsSelected);
        measureBtn.IsEnabled = !_busy && anyTicked;
        copyBtn.IsEnabled = !_busy && _plan != null;
        applyBtn.IsEnabled = !_busy && _rows.Any(r => r.PlannedGainDb != null);
        controlStrip.IsEnabled = !_busy;
        closeBtn.Content = _busy ? "Cancel" : "Close";
    }

    // ── measure ─────────────────────────────────────────────────

    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var queue = _rows.Where(r => r.IsSelected).ToList();
        if (queue.Count == 0) return;

        BeginPhase("Measuring");
        var token = _cts!.Token;
        var reporter = new ProgressSink(value => progressBar.Value = value);

        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                var row = queue[i];
                token.ThrowIfCancellationRequested();
                statusText.Text = $"Measuring {row.Title} ({i + 1} of {queue.Count})…";

                // Snapshotted on the UI thread: an edit publishes new channel arrays rather than
                // mutating these, so the measurement reads one coherent version of the audio, and
                // the version stamp is what Apply checks it against later.
                var channels = row.Document.Doc.Channels.ToArray();
                int rate = row.Document.Doc.SampleRate;
                int version = row.Document.Doc.EditVersion;
                var slice = SubProgress.Slice(reporter, i, queue.Count);
                var target = Target;

                var measurement = await Task.Run(
                    () => LoudnessMatch.Measure(row.Title, channels, rate, target, token, slice), token);

                row.Measurement = measurement;
                row.MeasuredEditVersion = version;
            }

            progressBar.Value = 1;
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Measurement cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Match Loudness", MessageBoxButton.OK, MessageBoxImage.Warning);
            statusText.Text = "Measurement failed.";
        }
        finally
        {
            EndPhase();
            RebuildReferenceList();
            Recompute();
        }

        if (_closeWhenFinished) Close();
    }

    /// <summary>
    /// Marshals a worker thread's progress onto the bar. <see cref="SubProgress"/> deliberately does
    /// not post through a synchronization context, so the hop has to happen here — and it is
    /// throttled, because the meter reports once per 4 096 frames and a side of vinyl is thousands
    /// of those. Posting every one would queue more dispatcher work than the bar can show.
    /// </summary>
    private sealed class ProgressSink(Action<double> set) : IProgress<double>
    {
        private const double Step = 0.005;
        private double _posted = -1;

        public void Report(double value)
        {
            double clamped = Math.Clamp(value, 0, 1);
            // Written on whichever pool thread the current item is running on, read on the next
            // one; a stale read costs at most one extra post.
            if (Math.Abs(clamped - Volatile.Read(ref _posted)) < Step && clamped < 1) return;
            Volatile.Write(ref _posted, clamped);
            Application.Current?.Dispatcher.BeginInvoke(() => set(clamped));
        }
    }

    // ── apply ───────────────────────────────────────────────────

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is not { } plan) return;

        var pending = _rows.Where(r => r.PlannedGainDb != null).ToList();
        if (pending.Count == 0) return;
        if (!StillMeasured(pending)) return;

        BeginPhase("Applying");
        var token = _cts!.Token;
        var applied = new List<TrackRow>();

        try
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var row = pending[i];
                if (row.PlannedGainDb is not { } gainDb) continue;
                token.ThrowIfCancellationRequested();
                statusText.Text = $"Applying to {row.Title} ({i + 1} of {pending.Count})…";

                var channels = row.Document.Doc.Channels.ToArray();
                var scaled = await Task.Run(
                    () => Processing.MatchLoudnessData(channels, gainDb, token), token);

                // Re-checked here rather than only up front: the dispatcher pumps across the await,
                // so a background render can land between one document and the next.
                if (row.Document.Doc.EditVersion != row.MeasuredEditVersion)
                    throw new InvalidOperationException($"{row.Title} changed while the gain was being applied.");

                _prepareForEdit?.Invoke(row.Document);
                row.Document.Doc.ReplaceAllOwned(scaled, Processing.MatchLoudnessName(gainDb, plan.TargetLufs));
                row.MeasuredEditVersion = row.Document.Doc.EditVersion;
                applied.Add(row);
                progressBar.Value = (i + 1) / (double)pending.Count;
            }
        }
        catch (OperationCanceledException)
        {
            int reverted = applied.Count;
            int stuck = RollBack(applied);
            statusText.Text = reverted == 0
                ? "Cancelled · nothing was applied."
                : $"Cancelled · {reverted - stuck} of {reverted} file(s) already levelled were put back"
                  + (stuck > 0 ? $", {stuck} changed since and were left as they are." : ".");
            EndPhase();
            if (_closeWhenFinished) Close();
            return;
        }
        catch (Exception ex)
        {
            int reverted = applied.Count;
            int stuck = RollBack(applied);
            MessageBox.Show(
                this,
                ex.Message + "\n\n" + (reverted == 0
                    ? "Nothing was applied."
                    : stuck == 0
                        ? "The files already levelled were put back, so nothing was applied."
                        : $"{reverted - stuck} of the {reverted} files already levelled were put back; "
                          + $"{stuck} changed underneath and were left as they are.")
                + " Measure again to work from the current audio.",
                "Match Loudness", MessageBoxButton.OK, MessageBoxImage.Information);
            statusText.Text = stuck == 0
                ? "Source changed · nothing was applied."
                : $"Source changed · {stuck} file(s) could not be put back.";
            EndPhase();
            if (_closeWhenFinished) Close();
            return;
        }

        EndPhase();
        ResultSummary = plan.Summary;
        DialogResult = true;
    }

    /// <summary>
    /// The up-front check. Everything measured must still be the audio it was measured from, or the
    /// plan describes files that no longer exist in that state.
    /// </summary>
    private bool StillMeasured(IEnumerable<TrackRow> rows)
    {
        foreach (var row in rows)
        {
            if (_documents.Contains(row.Document) && row.Document.Doc.EditVersion == row.MeasuredEditVersion)
                continue;

            MessageBox.Show(
                this,
                "One or more of these documents changed after they were measured. Nothing was "
                + "applied. Measure again to work from the current audio.",
                "Source changed", MessageBoxButton.OK, MessageBoxImage.Information);
            statusText.Text = "Source changed · nothing was applied.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Puts back what was already committed, so a run that cannot finish leaves nothing half done.
    /// Returns how many could not be put back.
    /// </summary>
    /// <remarks>
    /// Each commit is exactly one undo entry, so undoing one is exact rather than approximate — but
    /// only while it is still the top of that document's stack. The dispatcher pumps across every
    /// await in the apply loop, so a background render can land on a document after this dialog has
    /// already levelled it, and undoing then would put back <i>that</i> edit instead. The version
    /// stamped at commit time is what tells the two apart; a document that has moved since is left
    /// alone and counted, because a wrong rollback is worse than an admitted one.
    /// </remarks>
    private static int RollBack(List<TrackRow> applied)
    {
        int stuck = 0;
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            var row = applied[i];
            if (row.Document.Doc.CanUndo && row.Document.Doc.EditVersion == row.MeasuredEditVersion)
                row.Document.Doc.Undo();
            else
                stuck++;
        }
        applied.Clear();
        return stuck;
    }

    private void BeginPhase(string phase)
    {
        _busy = true;
        _phase = phase;
        _cts = new CancellationTokenSource();
        progressBar.Value = 0;
        UpdateActions();
    }

    private void EndPhase()
    {
        _cts?.Dispose();
        _cts = null;
        _busy = false;
        _phase = "";
        UpdateActions();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan) return;
        // A copy button is not worth a dialog when another process is holding the clipboard.
        try { Clipboard.SetText(LoudnessMatch.Format(plan)); } catch { }
    }

    // ── lifetime ────────────────────────────────────────────────

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            statusText.Text = $"Cancelling {_phase.ToLowerInvariant()}…";
            return;
        }
        Close();
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (!_busy) return;
        // Let the run unwind rather than tearing the window out from under it — an apply in flight
        // has committed edits to put back. The loop closes the window itself once it has stopped.
        e.Cancel = true;
        _closeWhenFinished = true;
        _cts?.Cancel();
        statusText.Text = $"Cancelling {_phase.ToLowerInvariant()}…";
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
