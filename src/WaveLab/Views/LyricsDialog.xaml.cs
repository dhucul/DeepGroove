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
    private readonly Func<AudioDocument, bool>? _pause;
    private readonly Func<AudioDocument, bool, bool>? _resume;
    private readonly Func<AudioDocument, bool, CancellationToken, Task<bool>>? _restart;
    private CancellationTokenSource? _playbackCancellation;
    private bool PlaybackPending => _playbackCancellation != null;
    private AudioDocument? _wholeAudio;
    private bool _wholePaused;
    private CancellationTokenSource? _cancellation;
    private bool _closeWhenFinished;
    private LyricsLine? _observedLine;
    private readonly Stack<IReadOnlyList<LyricsTextReplacement.Change>> _replacementHistory = new();
    private bool Busy => _cancellation != null;
    public AudioDocument? IsolatedVocals { get; private set; }
    private LyricsTranscript? Transcript => _document.LyricsTranscript;
    private sealed record LanguageChoice(string Name, string? Code);

    public LyricsDialog(DocumentViewModel document, Func<AudioDocument, bool, bool>? play = null,
        Action? stop = null, LyricsEngine? engine = null,
        Func<AudioDocument, bool>? pause = null, Func<AudioDocument, bool, bool>? resume = null,
        Func<AudioDocument, bool, CancellationToken, Task<bool>>? restart = null)
    {
        _document = document;
        _snapshot = document.Doc.Channels.ToArray();
        _rate = document.Doc.SampleRate;
        _version = document.Doc.EditVersion;
        _selectionStart = document.HasSelection ? document.SelStart : 0;
        _selectionCount = document.HasSelection ? document.SelEnd - document.SelStart : 0;
        _play = play;
        _stop = stop;
        _pause = pause;
        _resume = resume;
        _restart = restart;
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
        selectionCheck.IsChecked = _selectionCount > 0 && (_document.LyricsSelectionOnly ?? true);
        if (_document.LyricsSettings is { } saved)
        {
            modeCombo.SelectedIndex = saved.Speech ? 2 : saved.IsolateVocals ? 0 : 1;
            qualityCombo.SelectedIndex = saved.Model == "turbo" ? 1 : 0;
            languageCombo.SelectedValue = saved.Language;
            if (languageCombo.SelectedIndex < 0) languageCombo.SelectedIndex = 0;
            hintsText.Text = saved.Hints;
            deviceCombo.SelectedIndex = saved.CpuOnly ? 1 : 0;
            retryCheck.IsChecked = saved.RetryUnclear;
            compareCheck.IsChecked = saved.CompareOriginal;
        }
        RefreshTranscript();
        RefreshControls();
        Closed += (_, _) =>
        {
            if (_observedLine != null) _observedLine.PropertyChanged -= OnLineTextChanged;
            _playbackCancellation?.Cancel();
            _stop?.Invoke();
        };
    }

    private void RefreshControls()
    {
        bool ready = _engine.IsReady;
        engineLabel.Text = ready ? "Ready to transcribe · built into Deep Groove."
            : "Transcription files are missing from this installation. Reinstall Deep Groove or rebuild the complete Release app.";
        optionsPanel.IsEnabled = hintsPanel.IsEnabled = !Busy;
        transcribeButton.IsEnabled = vocalsButton.IsEnabled = ready && !Busy;
        cancelButton.IsEnabled = Busy;
        clearButton.IsEnabled = !Busy;
        linesGrid.IsReadOnly = Busy;
        copyButton.IsEnabled = exportButton.IsEnabled = !Busy && Transcript?.Lines.Count > 0;
        replaceTextButton.IsEnabled = !Busy && Transcript?.Lines.Count > 0;
        playAllButton.IsEnabled = !PlaybackPending && _play != null && _snapshot.Length > 0 && _snapshot[0].Length > 0;
        restartAudioButton.IsEnabled = playAllButton.IsEnabled;
        RefreshPlayback();
        RefreshOptions();
        RefreshLine();
    }

    private void RefreshTranscript()
    {
        linesGrid.ItemsSource = Transcript?.Lines;
        emptyLabel.Visibility = Transcript?.Lines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Transcript is { } result)
        {
            summaryLabel.Text = $"{result.Lines.Count} lines · {result.Language} · {result.Model} · {result.Device} · " +
                $"{result.Lines.Count(l => l.NeedsReview)} to review · {result.Lines.Count(l => l.Recovered)} recovered";
            if (result.SourceEditVersion != _version)
                statusLabel.Text = "The audio has changed since this transcript. Export your edits, then transcribe again to restore matching timings.";
        }
        else summaryLabel.Text = "Singing can be ambiguous. Review the result while listening.";
    }

    private void RefreshLine()
    {
        if (playButton == null) return; // SelectionChanged can fire during InitializeComponent.
        var line = linesGrid.SelectedItem as LyricsLine;
        RefreshSpellingSuggestion();
        playButton.IsEnabled = !Busy && !PlaybackPending && line != null && _play != null && Transcript?.SourceEditVersion == _version;
        alternativeButton.IsEnabled = !Busy && !string.IsNullOrWhiteSpace(line?.AlternativeText);
        retryButton.IsEnabled = !Busy && _engine.IsReady && line != null
            && Transcript?.SourceEditVersion == _version && _document.Doc.EditVersion == _version;
        acceptRetryButton.IsEnabled = !Busy && !string.IsNullOrWhiteSpace(line?.RetryText);
        restoreRetryButton.IsEnabled = !Busy && line?.PreviousRetryText != null;
        restoreRetryButton.ToolTip = line?.PreviousRetryText is { } previous
            ? string.IsNullOrEmpty(previous) ? "Restore the previous empty line." : "Previous text: " + previous
            : "Restore the wording from before the last correction, retry or spelling suggestion.";
        retryLabel.Text = string.IsNullOrWhiteSpace(line?.RetryText) ? ""
            : "Retry suggestion: " + line.RetryText;
        retryLabel.Visibility = string.IsNullOrWhiteSpace(line?.RetryText) ? Visibility.Collapsed : Visibility.Visible;
        string note = line?.Recovered == true ? line.RecoveryNote : "";
        alternativeLabel.Text = string.IsNullOrWhiteSpace(line?.AlternativeText)
            ? string.IsNullOrWhiteSpace(note) ? "Select a line to replay it. Review and Recovered lines need a listening check." : note
            : $"{note} Another reading: {line.AlternativeText}".Trim();
    }

    private async Task RunAsync(Func<IProgress<LyricsProgress>, CancellationToken, Task> action)
    {
        if (Busy) return;
        // Recognition reads the stable snapshot; listening does not change its input.
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

    private async void OnTranscribe(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (Transcript?.Lines.Count > 0 && MessageBox.Show(this,
            "Replace this tab's transcript? Export first if you want to keep the current text and corrections.",
            "Transcribe again", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int start = selectionCheck.IsChecked == true ? _selectionStart : 0;
        int count = selectionCheck.IsChecked == true ? _selectionCount : _snapshot[0].Length;
        var options = SaveOptions();
        await RunAsync(async (progress, token) =>
        {
            var result = await _engine.TranscribeAsync(_snapshot, _rate, start, count, _document.Title, _version, options, progress, token, _document.LyricsVocals);
            _document.LyricsTranscript = result;
            _replacementHistory.Clear();
            RefreshTranscript();
            statusLabel.Text = result.Lines.Count == 0 ? "No words detected. Try the original mix, a shorter selection, or the song's language."
                : "Ready. Replay Review and Recovered lines, then export to keep your corrections.";
        });
    }

    private async void OnIsolateVocals(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        int start = selectionCheck.IsChecked == true ? _selectionStart : 0;
        int count = selectionCheck.IsChecked == true ? _selectionCount : _snapshot[0].Length;
        bool cpuOnly = SaveOptions().CpuOnly;
        await RunAsync(async (progress, token) =>
        {
            var vocals = await _engine.IsolateVocalsAsync(_snapshot, _rate, start, count,
                _document.Title, cpuOnly, progress, token, _document.LyricsVocals, _version);
            token.ThrowIfCancellationRequested();
            IsolatedVocals = vocals;
            _closeWhenFinished = true;
        });
    }

    private LyricsOptions SaveOptions()
    {
        var options = new LyricsOptions(modeCombo.SelectedIndex == 0, modeCombo.SelectedIndex == 2,
            compareCheck.IsChecked == true, qualityCombo.SelectedIndex == 0 ? "large-v3" : "turbo",
            (languageCombo.SelectedItem as LanguageChoice)?.Code, hintsText.Text.Trim(),
            deviceCombo.SelectedIndex == 1, retryCheck.IsChecked == true);
        _document.LyricsSettings = options;
        // Opening with no selection must not erase a previous selection preference.
        if (_selectionCount > 0) _document.LyricsSelectionOnly = selectionCheck.IsChecked == true;
        return options;
    }

    private void RefreshOptions()
    {
        if (retryCheck == null || compareCheck == null) return;
        retryCheck.IsEnabled = !Busy && modeCombo.SelectedIndex != 2;
        compareCheck.IsEnabled = !Busy && modeCombo.SelectedIndex == 0;
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => RefreshOptions();

    private async void OnRetryLine(object sender, RoutedEventArgs e)
    {
        if (Busy || linesGrid.SelectedItem is not LyricsLine line || Transcript is not { } transcript
            || transcript.SourceEditVersion != _version || _document.Doc.EditVersion != _version) return;
        CommitEdits();
        var (start, count) = LyricsLineRetry.Range(line, transcript, _rate, _snapshot[0].Length);
        if (count <= 0) return;
        var options = SaveOptions();
        options = options with { CompareOriginal = false, RetryUnclear = false,
            Language = options.Language ?? (string.IsNullOrWhiteSpace(transcript.Language) ? null : transcript.Language) };
        await RunAsync(async (progress, token) =>
        {
            var result = await _engine.TranscribeAsync(_snapshot, _rate, start, count, _document.Title,
                _version, options, progress, token, _document.LyricsVocals);
            token.ThrowIfCancellationRequested();
            string suggestion = LyricsLineRetry.Suggestion(line, result, transcript.Lines);
            if (string.IsNullOrWhiteSpace(suggestion) || suggestion == line.Text.Trim())
            {
                line.RetryText = "";
                RefreshLine();
                statusLabel.Text = "The retry found no different reading for this line. Your text has been kept.";
                return;
            }
            line.RetryText = suggestion;
            linesGrid.SelectedItem = line;
            RefreshLine();
            statusLabel.Text = "Retry ready. Listen to the line, then choose Use retry reading if it is better. Your text has been kept.";
        });
    }

    private void OnAcceptRetry(object sender, RoutedEventArgs e)
    {
        if (Busy || linesGrid.SelectedItem is not LyricsLine line || string.IsNullOrWhiteSpace(line.RetryText)) return;
        CommitEdits();
        line.PreviousRetryText = line.Text;
        line.Text = line.RetryText;
        line.RetryText = "";
        RefreshLine();
        statusLabel.Text = "Only this line's text was changed. Its timings and the other lines were kept.";
    }

    private void OnReplaceText(object sender, RoutedEventArgs e)
    {
        if (Busy || Transcript is not { Lines.Count: > 0 } transcript) return;
        CommitEdits();
        new LyricsReplaceDialog(transcript.Lines, linesGrid.SelectedItem as LyricsLine, _replacementHistory)
            { Owner = this }.ShowDialog();
        RefreshLine();
    }

    private void RefreshSpellingSuggestion()
    {
        if (spellingSuggestionPanel == null) return; // TextChanged fires during initialization.
        string? suggestion = linesGrid.SelectedItem is LyricsLine line
            ? LyricsSpellingHints.Suggest(line.Text, hintsText.Text) : null;
        spellingSuggestionPanel.Visibility = suggestion == null ? Visibility.Collapsed : Visibility.Visible;
        spellingSuggestionButton.IsEnabled = !Busy && suggestion != null;
        spellingSuggestionLabel.Text = suggestion == null ? "" : "Spelling suggestion: " + suggestion;
        spellingSuggestionLabel.ToolTip = suggestion;
    }

    private void OnHintsChanged(object sender, TextChangedEventArgs e) => RefreshSpellingSuggestion();

    private void OnLineTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsLine.Text)) RefreshSpellingSuggestion();
    }

    private void OnUseHintedSpelling(object sender, RoutedEventArgs e)
    {
        if (Busy || linesGrid.SelectedItem is not LyricsLine line) return;
        CommitEdits();
        // Recompute after committing an in-progress edit; never apply a stale preview.
        if (LyricsSpellingHints.Suggest(line.Text, hintsText.Text) is not { } suggestion) return;
        line.PreviousRetryText = line.Text;
        line.Text = suggestion;
        SaveOptions();
        RefreshLine();
        statusLabel.Text = "Hinted spelling applied to this line. Restore previous text can undo it. Timings and recognition evidence are kept.";
    }

    private void OnRestoreRetry(object sender, RoutedEventArgs e)
    {
        if (Busy || linesGrid.SelectedItem is not LyricsLine line || line.PreviousRetryText == null) return;
        CommitEdits();
        line.Text = line.PreviousRetryText;
        line.PreviousRetryText = null;
        RefreshLine();
        statusLabel.Text = "This line's previous wording was restored. Any new retry suggestion is still available.";
    }

    private void OnPlayAll(object sender, RoutedEventArgs e)
    {
        if (PlaybackPending || _play == null || _snapshot.Length == 0 || _snapshot[0].Length == 0) return;
        try
        {
            // Document edits replace channel arrays. Reuse this window's immutable snapshot
            // rather than allocating a second full recording for playback.
            _wholeAudio ??= new AudioDocument(_snapshot, _rate, 32) { Title = _document.Title };
            bool continuing = _wholePaused;
            bool played = continuing ? _resume?.Invoke(_wholeAudio, loopCheck.IsChecked == true) == true
                : _play(_wholeAudio, loopCheck.IsChecked == true);
            if (!played)
            {
                statusLabel.Text = continuing
                    ? "The previous playback is no longer available. Choose Restart audio to start from the beginning."
                    : "Playback is unavailable. Stop recording or check your output device.";
                return;
            }
            _wholePaused = false;
            RefreshPlayback();
            if (!Busy)
                statusLabel.Text = continuing ? "Continuing the recording from where you stopped."
                    : "Playing the whole recording. You can read and edit the lyrics while listening.";
        }
        catch (PlaybackDeviceBusyException) { ShowPlaybackBusy(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Play whole audio", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void OnRestartAudio(object sender, RoutedEventArgs e)
    {
        if (PlaybackPending || _play == null || _snapshot.Length == 0 || _snapshot[0].Length == 0) return;
        var audio = new AudioDocument(_snapshot, _rate, 32) { Title = _document.Title };
        _wholeAudio = audio;
        _wholePaused = false;
        using var cancellation = new CancellationTokenSource();
        _playbackCancellation = cancellation;
        RefreshControls();
        if (!Busy) statusLabel.Text = "Restarting audio… waiting for the audio device. Stop cancels the restart.";
        try
        {
            bool played = _restart != null
                ? await _restart(audio, loopCheck.IsChecked == true, cancellation.Token)
                : _play(audio, loopCheck.IsChecked == true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Busy) statusLabel.Text = played ? "Playing the whole recording from the beginning."
                : "Playback is unavailable. Stop recording or check your output device.";
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { /* Stop, Clear or Close owns the final status. */ }
        catch (PlaybackDeviceBusyException) { ShowPlaybackBusy(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restart audio", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _playbackCancellation = null;
            RefreshControls();
        }
    }

    private void RefreshPlayback()
        => playAllButton.Content = _wholePaused ? "Continue audio" : "Play whole audio";

    private void ShowPlaybackBusy()
        => statusLabel.Text = "The audio device is finishing the previous playback. Try again shortly.";

    private void OnStop(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PlaybackPending)
            {
                _playbackCancellation!.Cancel();
                _stop?.Invoke();
                _wholePaused = false;
                RefreshPlayback();
                if (!Busy) statusLabel.Text = "Playback stopped. The pending restart was cancelled.";
                return;
            }
            if (_wholeAudio != null && _resume != null && _pause?.Invoke(_wholeAudio) == true)
            {
                _wholePaused = true;
                if (!Busy) statusLabel.Text = "Stopped at the current position. Continue audio resumes here; Restart audio starts over.";
            }
            else
            {
                _stop?.Invoke();
                _wholePaused = false;
            }
            RefreshPlayback();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Stop audio", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnReplay(object sender, RoutedEventArgs e)
    {
        if (PlaybackPending || linesGrid.SelectedItem is not LyricsLine line || Transcript?.SourceEditVersion != _version || Busy) return;
        try
        {
            int start = Math.Clamp((int)Math.Floor((line.Start - 0.15) * _rate), 0, _snapshot[0].Length);
            int end = Math.Clamp((int)Math.Ceiling((line.End + 0.2) * _rate), start, _snapshot[0].Length);
            var channels = _snapshot.Select(c => c.AsSpan(start, end - start).ToArray()).ToArray();
            if (_play?.Invoke(new AudioDocument(channels, _rate, 32), loopCheck.IsChecked == true) != true)
                statusLabel.Text = "Playback is unavailable. Stop recording or check your output device.";
            else
            {
                _wholePaused = false;
                RefreshPlayback();
                statusLabel.Text = "Playing the selected line.";
            }
        }
        catch (PlaybackDeviceBusyException) { ShowPlaybackBusy(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replay line", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnUseAlternative(object sender, RoutedEventArgs e)
    {
        if (Busy || linesGrid.SelectedItem is not LyricsLine line || string.IsNullOrWhiteSpace(line.AlternativeText)) return;
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

    private void OnClearEverything(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        CommitEdits();
        _playbackCancellation?.Cancel();
        _stop?.Invoke();
        _wholePaused = false;
        _wholeAudio = null;
        _document.LyricsTranscript = null;
        _replacementHistory.Clear();
        _document.LyricsSettings = null;
        _document.LyricsSelectionOnly = null;
        _document.LyricsVocals.Clear();
        IsolatedVocals = null;
        linesGrid.SelectedItem = null;
        modeCombo.SelectedIndex = 0;
        qualityCombo.SelectedIndex = 0;
        languageCombo.SelectedIndex = 0;
        deviceCombo.SelectedIndex = 0;
        hintsText.Clear();
        selectionCheck.IsChecked = _selectionCount > 0;
        retryCheck.IsChecked = compareCheck.IsChecked = true;
        loopCheck.IsChecked = false;
        progressBar.IsIndeterminate = false;
        progressBar.Value = 0;
        RefreshTranscript();
        RefreshControls();
        statusLabel.Text = "Lyrics, hints and suggestions cleared. Options reset. Your audio is unchanged.";
    }

    private void OnLyricsRowLoading(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.ContextMenu != null) return;
        var edit = new MenuItem { Header = "Edit line" };
        edit.Click += OnEditLine;
        var menu = new ContextMenu();
        menu.Items.Add(edit);
        menu.Opened += OnLineMenuOpened;
        e.Row.ContextMenu = menu;
    }

    private void OnLineMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var line = (menu.PlacementTarget as DataGridRow)?.Item as LyricsLine;
        bool canEdit = !Busy && line != null && Transcript?.Lines.Contains(line) == true;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = canEdit;
        if (canEdit)
        {
            CommitEdits();
            linesGrid.SelectedItem = line;
        }
    }

    private void OnEditLine(object sender, RoutedEventArgs e)
    {
        if (Busy || sender is not MenuItem item
            || ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu
            || menu.PlacementTarget is not DataGridRow { Item: LyricsLine line }
            || Transcript?.Lines.Contains(line) != true) return;
        menu.IsOpen = false;
        // Let the popup release keyboard focus before focusing the words cell.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (Busy || !IsVisible || Transcript?.Lines.Contains(line) != true) return;
            CommitEdits();
            linesGrid.SelectedItem = line;
            linesGrid.ScrollIntoView(line, wordsColumn);
            linesGrid.UpdateLayout();
            linesGrid.CurrentCell = new DataGridCellInfo(line, wordsColumn);
            linesGrid.Focus();
            linesGrid.BeginEdit();
        }));
    }

    private void OnLineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_observedLine != null) _observedLine.PropertyChanged -= OnLineTextChanged;
        _observedLine = linesGrid.SelectedItem as LyricsLine;
        if (_observedLine != null) _observedLine.PropertyChanged += OnLineTextChanged;
        RefreshLine();
    }
    private void OnCancel(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); cancelButton.IsEnabled = false; }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnDragMove(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CommitEdits();
        SaveOptions();
        _playbackCancellation?.Cancel();
        if (!Busy) return;
        _closeWhenFinished = true;
        _cancellation?.Cancel();
        e.Cancel = true; // Wait until the process tree has exited and temporary audio has been removed.
    }
}
