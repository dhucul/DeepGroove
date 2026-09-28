using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Audio.Transcription;

namespace WaveLab.Views;

public partial class LyricsReplaceDialog : Window
{
    private readonly IReadOnlyList<LyricsLine> _lines;
    private readonly LyricsLine? _selected;
    private readonly Stack<IReadOnlyList<LyricsTextReplacement.Change>> _history;
    private IReadOnlyList<LyricsTextReplacement.Change> _preview = [];
    private bool _ready;

    internal LyricsReplaceDialog(IReadOnlyList<LyricsLine> lines, LyricsLine? selected,
        Stack<IReadOnlyList<LyricsTextReplacement.Change>> history)
    {
        _lines = lines;
        _selected = selected != null && lines.Contains(selected) ? selected : null;
        _history = history;
        InitializeComponent();
        selectedScope.IsEnabled = _selected != null;
        scopeCombo.SelectedIndex = _selected != null ? 0 : 1;
        findText.Text = replacementText.Text = _selected?.Text ?? "";
        _ready = true;
        RefreshPreview();
        Loaded += (_, _) =>
        {
            var editor = _selected == null ? findText : replacementText;
            editor.Focus();
            editor.SelectAll();
        };
    }

    private void RefreshPreview()
    {
        if (!_ready) return;
        IEnumerable<LyricsLine> scope = scopeCombo.SelectedIndex == 0
            ? _selected == null ? [] : [_selected] : _lines;
        _preview = LyricsTextReplacement.Preview(scope, findText.Text, replacementText.Text,
            matchCaseCheck.IsChecked == true, wholeWordsCheck.IsChecked == true);
        previewGrid.ItemsSource = _preview;
        previewLabel.Text = string.IsNullOrWhiteSpace(findText.Text) ? "Enter the word or phrase you want to replace."
            : _preview.Count == 0 ? "No changes to apply. Check the Find text and the selected scope."
            : $"Preview: {_preview.Sum(c => c.Matches)} replacement(s) in {_preview.Count} line(s).";
        applyButton.IsEnabled = _preview.Count > 0;
        undoButton.IsEnabled = _history.TryPeek(out var previous) && LyricsTextReplacement.CanUndo(previous);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!LyricsTextReplacement.Apply(_preview))
        {
            RefreshPreview();
            statusLabel.Text = "Review the current preview before applying a replacement.";
            return;
        }
        int matches = _preview.Sum(c => c.Matches), lines = _preview.Count;
        _history.Push(_preview);
        RefreshPreview();
        statusLabel.Text = $"Replaced {matches} occurrence(s) in {lines} line(s). You can replace another phrase or undo this change.";
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (!_history.TryPeek(out var previous) || !LyricsTextReplacement.Undo(previous))
        {
            RefreshPreview();
            statusLabel.Text = "The text has changed since that replacement. Your newer edits have been kept.";
            return;
        }
        _history.Pop();
        RefreshPreview();
        statusLabel.Text = "The last replacement was undone.";
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();
    private void OnScopeChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void OnOptionChanged(object sender, RoutedEventArgs e) => RefreshPreview();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnDragMove(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
