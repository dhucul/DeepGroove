using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class LyricsDialog : Window
{
    private readonly DocumentViewModel _document;
    private readonly float[][] _snapshot;
    private readonly int _rate, _version, _selectionStart, _selectionCount;
    private readonly LyricsEngine _engine;
    private readonly Func<AudioDocument, bool, bool>? _play;
    private readonly Action? _stop;
    private CancellationTokenSource? _cancellation;
    private bool _closeWhenFinished;
    private bool Busy => _cancellation != null;
    private LyricsTranscript? Transcript => _document.LyricsTranscript;
    private sealed record LanguageChoice(string Name, string? Code);

    public LyricsDialog(DocumentViewModel document, Func<AudioDocument, bool, bool>? play = null,
        Action? stop = null, LyricsEngine? engine = null)
    {
        _document = document;
        _snapshot = document.Doc.Channels.ToArray();
        _rate = document.Doc.SampleRate;
        _version = document.Doc.EditVersion;
        _selectionStart = document.HasSelection ? document.SelStart : 0;
        _selectionCount = document.HasSelection ? document.SelEnd - document.SelStart : 0;
        _play = play;
        _stop = stop;
        _engine = engine ?? new LyricsEngine();
        InitializeComponent();
        sourceLabel.Text = document.Title;
        languageCombo.ItemsSource = new LanguageChoice[]
        {
            new("Auto-detect", null), new("English", "en"), new("French", "fr"), new("Spanish", "es"),
            new("German", "de"), new("Italian", "it"), new("Portuguese", "pt"), new("Dutch", "nl"),
            new("Japanese", "ja"), new("Korean", "ko"), new("Chinese", "zh"), new("Hindi", "hi"),
            new("Arabic", "ar"), new("Russian", "ru"), new("Ukrainian", "uk"), new("Polish", "pl"),
        };
        languageCombo.SelectedIndex = 0;
        selectionCheck.IsEnabled = _selectionCount > 0;
        selectionCheck.IsChecked = _selectionCount > 0;
        RefreshTranscript();
        RefreshControls();
        Closed += (_, _) => _stop?.Invoke();
    }

    private void RefreshControls()
    {
        bool ready = _engine.IsReady;
        setupButton.IsEnabled = !Busy;
        setupButton.Content = ready ? "Repair / change engine" : "Set up local engine";
        engineLabel.Text = ready ? "Local engine installed. Ready to transcribe." : "One-time setup · no account or API key required.";
        optionsPanel.IsEnabled = hintsPanel.IsEnabled = !Busy;
        transcribeButton.IsEnabled = ready && !Busy;
        cancelButton.IsEnabled = Busy;
        linesGrid.IsReadOnly = Busy;
        copyButton.IsEnabled = exportButton.IsEnabled = !Busy && Transcript?.Lines.Count > 0;
        RefreshLine();
    }

    private void RefreshTranscript()
    {
        linesGrid.ItemsSource = Transcript?.Lines;
        emptyLabel.Visibility = Transcript?.Lines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Transcript is { } result)
        {
            summaryLabel.Text = $"{result.Lines.Count} lines · {result.Language} · {result.Model} · {result.Device} · " +
                $"{result.Lines.Count(l => l.NeedsReview)} to review";
            if (result.SourceEditVersion != _version)
                statusLabel.Text = "The audio has changed since this transcript. Export your edits, then transcribe again to restore matching timings.";
        }
    }

    private void RefreshLine()
    {
        if (playButton == null) return; // SelectionChanged can fire during InitializeComponent.
        var line = linesGrid.SelectedItem as LyricsLine;
        playButton.IsEnabled = !Busy && line != null && _play != null && Transcript?.SourceEditVersion == _version;
        alternativeButton.IsEnabled = !Busy && !string.IsNullOrWhiteSpace(line?.AlternativeText);
        alternativeLabel.Text = string.IsNullOrWhiteSpace(line?.AlternativeText)
            ? "Select a line to replay it. ‘Review’ marks uncertain recognition, not a measured accuracy score."
            : $"Another reading from the original mix: {line.AlternativeText}";
    }

    private async Task RunAsync(Func<IProgress<LyricsProgress>, CancellationToken, Task> action)
    {
        if (Busy) return;
        _stop?.Invoke();
        _cancellation = new CancellationTokenSource();
        progressBar.Value = 0;
        progressBar.IsIndeterminate = true;
        RefreshControls();
        var progress = new Progress<LyricsProgress>(p =>
        {
            if (!Busy) return;
            statusLabel.Text = p.Message;
            progressBar.IsIndeterminate = !p.Fraction.HasValue;
            if (p.Fraction.HasValue) progressBar.Value = Math.Max(progressBar.Value, p.Fraction.Value);
        });
        try { await action(progress, _cancellation.Token); }
        catch (OperationCanceledException) { statusLabel.Text = "Cancelled. The previous transcript has been kept."; }
        catch (Exception ex)
        {
            statusLabel.Text = "Could not complete the operation. Your audio and previous transcript are unchanged.";
            MessageBox.Show(this, ex.Message, "Lyrics & Speech", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            progressBar.IsIndeterminate = false;
            RefreshControls();
            if (_closeWhenFinished) Close();
        }
    }

    private async void OnSetup(object sender, RoutedEventArgs e)
    {
        bool cpu = deviceCombo.SelectedIndex == 1;
        await RunAsync((progress, token) => _engine.SetupAsync(cpu, progress, token));
    }

    private async void OnTranscribe(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (Transcript?.Lines.Count > 0 && MessageBox.Show(this,
            "Replace this tab's transcript? Export first if you want to keep the current text and corrections.",
            "Transcribe again", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int start = selectionCheck.IsChecked == true ? _selectionStart : 0;
        int count = selectionCheck.IsChecked == true ? _selectionCount : _snapshot[0].Length;
        var options = new LyricsOptions(modeCombo.SelectedIndex == 0, modeCombo.SelectedIndex == 2,
            compareCheck.IsChecked == true, qualityCombo.SelectedIndex == 0 ? "large-v3" : "turbo",
            ((LanguageChoice)languageCombo.SelectedItem).Code, hintsText.Text.Trim(), deviceCombo.SelectedIndex == 1);
        await RunAsync(async (progress, token) =>
        {
            var result = await _engine.TranscribeAsync(_snapshot, _rate, start, count, _document.Title, _version, options, progress, token);
            _document.LyricsTranscript = result;
            RefreshTranscript();
            statusLabel.Text = result.Lines.Count == 0 ? "No words detected. Try the original mix, a shorter selection, or the song's language."
                : "Ready. Replay and correct uncertain lines. Export to keep a copy after closing the audio tab.";
        });
    }

    private void OnReplay(object sender, RoutedEventArgs e)
    {
        if (linesGrid.SelectedItem is not LyricsLine line || Transcript?.SourceEditVersion != _version || Busy) return;
        try
        {
            int start = Math.Clamp((int)Math.Floor((line.Start - 0.15) * _rate), 0, _snapshot[0].Length);
            int end = Math.Clamp((int)Math.Ceiling((line.End + 0.2) * _rate), start, _snapshot[0].Length);
            var channels = _snapshot.Select(c => c.AsSpan(start, end - start).ToArray()).ToArray();
            if (_play?.Invoke(new AudioDocument(channels, _rate, 32), loopCheck.IsChecked == true) != true)
                statusLabel.Text = "Playback is unavailable. Stop recording or check your output device.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replay line", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnUseAlternative(object sender, RoutedEventArgs e)
    {
        if (linesGrid.SelectedItem is not LyricsLine line || string.IsNullOrWhiteSpace(line.AlternativeText)) return;
        (line.Text, line.AlternativeText) = (line.AlternativeText, line.Text);
        RefreshLine();
    }

    private void CommitEdits()
    {
        linesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        linesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        try { if (Transcript is { } result) Clipboard.SetText(result.Export(".txt")); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy text", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (Transcript is not { } result) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export transcript", FileName = Path.GetFileNameWithoutExtension(_document.Title) + "-lyrics",
            Filter = "Plain text (*.txt)|*.txt|Timed lyrics (*.lrc)|*.lrc|Subtitles (*.srt)|*.srt|Detailed transcript (*.json)|*.json",
            DefaultExt = ".txt", AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, result.Export(Path.GetExtension(dialog.FileName)), new System.Text.UTF8Encoding(false));
            statusLabel.Text = $"Exported {Path.GetFileName(dialog.FileName)}. Timings are measured from the beginning of the source audio.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export transcript", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnLineChanged(object sender, SelectionChangedEventArgs e) => RefreshLine();
    private void OnStop(object sender, RoutedEventArgs e) => _stop?.Invoke();
    private void OnCancel(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); cancelButton.IsEnabled = false; }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnDragMove(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CommitEdits();
        if (!Busy) return;
        _closeWhenFinished = true;
        _cancellation?.Cancel();
        e.Cancel = true; // Wait until the process tree has exited and temporary audio has been removed.
    }
}
