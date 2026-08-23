using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>
/// The edit history of one document: what has been applied, in order, with the state the document
/// is in now marked, and the ability to go back to any of it or throw the tail away.
/// </summary>
/// <remarks>
/// <para>
/// Modeless, like the markers manager, and for the same reason — it is meant to sit beside the
/// editor while work carries on. That makes everything it shows perishable: an edit adds a step, an
/// undo moves the mark, and the memory budget can release steps from either end without anyone
/// asking. So the panel holds no state of its own beyond the selected <i>position</i>, and re-reads
/// the whole snapshot on every <see cref="DocumentViewModel.HistoryVersion"/> bump.
/// </para>
/// <para>
/// Row 0 is the document as it was opened, not a step. Every other row is step <c>Position - 1</c>
/// and jumps to <c>Position</c> — that is, "this many steps applied".
/// </para>
/// <para>
/// Being a window of its own puts it outside the shell's progress overlay, which is the one thing
/// about it that is genuinely dangerous: the tools commit against a length check that a same-length
/// jump would slip past. <paramref name="canEdit"/> is how that is held shut, and it is asked again
/// on every refresh rather than sampled once.
/// </para>
/// </remarks>
public partial class HistoryDialog : Window
{
    /// <summary>One row. A view model rather than a built control, so the list can virtualize.</summary>
    internal sealed class HistoryRow
    {
        public required int Position { get; init; }
        public required string Name { get; init; }
        public string Note { get; init; } = "";
        public required Brush NameBrush { get; init; }
        public required FontWeight NameWeight { get; init; }
        public required Brush DotFill { get; init; }
        public required Brush DotStroke { get; init; }
        public string SizeText { get; init; } = "";
        public bool IsSavepoint { get; init; }
        public bool ChangesLength { get; init; }

        public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SavepointVisibility => IsSavepoint ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LengthVisibility => ChangesLength ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly DocumentViewModel _doc;
    private readonly Action<DocumentViewModel, int> _jump;
    private readonly Action<DocumentViewModel, int> _truncate;
    private readonly Func<bool>? _canEdit;
    private readonly ObservableCollection<HistoryRow> _rows = [];

    // Resolved once. Walking the resource tree per row per brush is what a list of a few thousand
    // steps cannot afford.
    private readonly Brush _accent;
    private readonly Brush _faint;
    private readonly Brush _text;

    private HistorySnapshot _history;
    private int _generation = -1;
    private bool _refreshing;

    /// <param name="document">The tab whose history is shown.</param>
    /// <param name="jump">
    /// How to move the document. Required rather than defaulted: the shell's wrapper releases
    /// playback, refuses while a tool is running, and writes the status line, and a panel that
    /// quietly did none of those because a caller left an argument out would be worse than one that
    /// does not compile.
    /// </param>
    /// <param name="truncate">How to discard a step and its tail, same arrangement.</param>
    /// <param name="canEdit">
    /// Asked before every action is offered. Null means always — for a caller with no shell behind
    /// it, which is to say a test.
    /// </param>
    public HistoryDialog(
        DocumentViewModel document,
        Action<DocumentViewModel, int> jump,
        Action<DocumentViewModel, int> truncate,
        Func<bool>? canEdit = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(jump);
        ArgumentNullException.ThrowIfNull(truncate);
        InitializeComponent();

        _doc = document;
        _jump = jump;
        _truncate = truncate;
        _canEdit = canEdit;
        _accent = (Brush)FindResource("Accent");
        _faint = (Brush)FindResource("Faint");
        _text = (Brush)FindResource("Text");

        list.ItemsSource = _rows;
        _doc.PropertyChanged += OnDocumentPropertyChanged;
        Closed += (_, _) => _doc.PropertyChanged -= OnDocumentPropertyChanged;
        Refresh();
    }

    /// <summary>The document this panel is showing, so the shell can avoid opening a second one.</summary>
    public DocumentViewModel Document => _doc;

    /// <summary>
    /// Re-reads whether the history may be moved. The shell calls this when a long operation starts
    /// or ends, because the overlay that covers the editor does not reach a separate window.
    /// </summary>
    public void RefreshActions() => UpdateActions();

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.HistoryVersion)) Refresh();
    }

    private void Refresh()
    {
        _history = _doc.Doc.GetHistory();
        int previous = list.SelectedIndex;
        bool renumbered = _generation != _history.Generation;
        _generation = _history.Generation;

        _refreshing = true;
        try
        {
            _rows.Clear();
            _rows.Add(BaselineRow(_history));
            foreach (var entry in _history.Entries) _rows.Add(Row(entry));

            // A step released by the budget renumbers everything after it, so an index held across
            // that names a different row. Clamping is right when the list merely grew; when it
            // renumbered, the only honest answer is to go back to where the document actually is.
            int wanted = renumbered || previous < 0 ? _history.Position : previous;
            list.SelectedIndex = Math.Clamp(wanted, 0, _rows.Count - 1);
        }
        finally
        {
            _refreshing = false;
        }

        headerDetail.Text = Header(_doc, _history);
        UpdateActions();
    }

    private static string Header(DocumentViewModel document, in HistorySnapshot history)
    {
        string steps = history.Entries.Count == 0
            ? "no edits yet"
            : $"{history.Position} of {history.Entries.Count} steps applied";
        return $"{document.Doc.Title} · {steps} · {Size(history.RetainedBytes)} of {Size(history.BudgetBytes)} retained";
    }

    private static string Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} kB"
        : $"{bytes} B";

    private HistoryRow BaselineRow(in HistorySnapshot history)
    {
        bool released = history.DiscardedOlderSteps > 0;
        string note = released
            ? $"{history.DiscardedOlderSteps} older step(s) released to stay inside the {Size(history.BudgetBytes)} undo limit"
            : history.Entries.Count == 0
                ? "No edits yet — everything you do to this file will be listed here."
                : "";
        return Build(
            position: 0,
            name: released ? "Earliest retained state" : "Opened",
            note: note,
            applied: true,
            current: history.Position == 0,
            savepoint: history.BaselineIsSavepoint,
            changesLength: false,
            size: "");
    }

    private HistoryRow Row(in HistoryEntry entry) => Build(
        position: entry.Index + 1,
        name: entry.Name,
        note: "",
        applied: entry.IsApplied,
        current: entry.IsCurrent,
        savepoint: entry.IsSavepoint,
        changesLength: entry.ChangesLength,
        size: Size(entry.RetainedBytes));

    private HistoryRow Build(
        int position, string name, string note,
        bool applied, bool current, bool savepoint, bool changesLength, string size) => new()
        {
            Position = position,
            Name = name,
            Note = note,
            NameBrush = applied ? _text : _faint,
            NameWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            // An undone step is drawn hollow: it is still on the timeline and redo can bring it
            // back, but it is not in the audio.
            DotFill = current ? _accent : applied ? _faint : Brushes.Transparent,
            DotStroke = applied ? Brushes.Transparent : _faint,
            SizeText = size,
            IsSavepoint = savepoint,
            ChangesLength = changesLength,
        };

    private int? SelectedPosition => (list.SelectedItem as HistoryRow)?.Position;

    private void UpdateActions()
    {
        bool allowed = _canEdit?.Invoke() ?? true;
        int? position = SelectedPosition;

        jumpButton.IsEnabled = allowed && position is { } p && p != _history.Position;
        deleteButton.IsEnabled = allowed && position is > 0;
        copyButton.IsEnabled = _history.Entries.Count > 0;

        string? caution = !allowed
            ? "An operation is running · the history is read-only until it finishes."
            : Caution(_history, position ?? _history.Position, _doc.Regions.Count);
        cautionText.Text = caution ?? string.Empty;
        cautionText.Visibility = caution == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The warning shown before a jump that crosses a length-changing step, or null when there is
    /// nothing to warn about.
    /// </summary>
    /// <remarks>
    /// A region that collapses during a length-changing splice is removed and undo does not bring it
    /// back — true of a single Ctrl+Z today, and this panel makes it easy to cross several at once.
    /// Snapshotting markers per step was considered and rejected: they are edited without any audio
    /// edit at all, so restoring a snapshot would silently delete work that no step on this list is
    /// responsible for. Static and parameterised so the wording can be tested without a window.
    /// </remarks>
    internal static string? Caution(in HistorySnapshot history, int position, int regionCount)
    {
        if (regionCount == 0 || position == history.Position) return null;
        int from = Math.Min(position, history.Position);
        int to = Math.Max(position, history.Position);
        int crossing = 0;
        for (int i = from; i < to && i < history.Entries.Count; i++)
            if (history.Entries[i].ChangesLength) crossing++;
        if (crossing == 0) return null;
        return $"Crossing {crossing} step(s) that change the file's length · "
             + "a region that collapses is not brought back.";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        UpdateActions();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => JumpToSelected();

    private void OnJumpButton(object sender, RoutedEventArgs e) => JumpToSelected();

    /// <remarks>
    /// No refresh afterwards: a jump that moved anything raises the document's change event, which
    /// comes back through <see cref="DocumentViewModel.HistoryVersion"/>, and one that moved nothing
    /// has nothing to redraw. That is the contract the callbacks are required for.
    /// </remarks>
    private void JumpToSelected()
    {
        if (!jumpButton.IsEnabled) return;
        if (SelectedPosition is not { } position) return;
        if (position == _history.Position) return;
        _jump(_doc, position);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (!deleteButton.IsEnabled) return;
        if (SelectedPosition is not { } position || position <= 0) return;
        int index = position - 1;
        if (index >= _history.Entries.Count) return;

        int discarded = _history.Entries.Count - index;
        var message = new StringBuilder();
        message.Append(discarded == 1
            ? $"Discard \"{_history.Entries[index].Name}\"?"
            : $"Discard \"{_history.Entries[index].Name}\" and the {discarded - 1} step(s) after it?");
        message.Append("\n\nThis cannot be undone — the steps are released, not moved.");
        for (int i = index; i < _history.Entries.Count; i++)
        {
            if (!_history.Entries[i].IsSavepoint) continue;
            message.Append("\n\nThe last saved state is among them, so the file will be left with "
                         + "unsaved changes you can no longer undo your way back out of.");
            break;
        }

        if (MessageBox.Show(this, message.ToString(), "Delete from here",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        // Re-read: the box was up long enough for an edit to land, and the index would then name a
        // different step. The shell's wrapper refuses an index the history no longer holds.
        _truncate(_doc, index);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var history = _history;
        var text = new StringBuilder();
        text.AppendLine($"Edit history — {_doc.Doc.Title}");
        text.AppendLine(new string('-', 44));
        text.AppendLine($"     {(history.Position == 0 ? "▶" : " ")} {(history.DiscardedOlderSteps > 0 ? "Earliest retained state" : "Opened")}");
        for (int i = 0; i < history.Entries.Count; i++)
        {
            var entry = history.Entries[i];
            text.AppendLine(
                $"{i + 1,4} {(entry.IsCurrent ? "▶" : " ")} {entry.Name}"
                + (entry.IsSavepoint ? "  [saved]" : "")
                + (entry.IsApplied ? "" : "  [undone]"));
        }
        text.AppendLine(new string('-', 44));
        text.AppendLine($"{history.Position} of {history.Entries.Count} steps applied · {Size(history.RetainedBytes)} retained");
        if (history.DiscardedOlderSteps > 0 || history.DiscardedNewerSteps > 0)
        {
            text.AppendLine(
                $"{history.DiscardedOlderSteps} older and {history.DiscardedNewerSteps} newer step(s) "
                + "released to stay inside the undo memory limit.");
        }

        // Clipboard access fails when another process is holding it; a copy button is not worth a
        // dialog about it.
        try { Clipboard.SetText(text.ToString()); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
