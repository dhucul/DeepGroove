using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.Help;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _progressTimer;
    private bool _allowClose;
    private bool _closing;
    private bool _longOperationRunning;
    private bool _skipNextAutomaticSpectrogramRender;
    private Task _startupTask = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        spectrumView.Tap = _vm.Engine.Master;
        loudnessView.Source = _vm.Master;
        phaseView.Tap = _vm.Engine.Master;
        _vm.RequestRecordDialog += ShowRecordDialog;
        _vm.RequestSpectrogram += RefreshSpectrogram;
        _vm.RequestSettingsDialog += ShowSettingsDialog;
        _vm.RequestExportDialog += ShowExportDialog;
        _vm.RequestStatisticsDialog += ShowStatisticsDialog;
        _vm.RequestCommandPalette += ShowCommandPalette;
        _vm.Master.RequestSavePreset += PromptSavePreset;

        RestoreWindowPlacement();

        // The progress host stores what workers report and recomputes the visible text here, at a
        // fixed 10 Hz. Marshalling every individual progress report through the dispatcher instead
        // would post tens of thousands of callbacks per render and starve the meters and playhead.
        // Render priority keeps it behind input, which is what lets Cancel stay responsive.
        _progressTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _progressTimer.Tick += (_, _) => _vm.Progress.Tick();
        _progressTimer.Start();

        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs().Skip(1).Where(System.IO.File.Exists).ToArray();
            _startupTask = RunStartupAsync(args);
            await _startupTask;
        };
        Closing += OnWindowClosing;
    }

    private async Task RunStartupAsync(string[] args)
    {
            try { await _vm.StartupLoadAsync(args); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Startup failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        // RunBlocking no longer disables the window — that would disable the overlay's own Cancel
        // button — so the progress host is what says whether something is still running.
        if (_vm.Progress.Blocking is { } blocking)
        {
            blocking.Cancel();
            MessageBox.Show(
                $"{blocking.Title} is stopping. It will finish at its next safe point; close WaveLab again once it has.",
                "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_longOperationRunning || !IsEnabled)
        {
            MessageBox.Show(
                "An audio operation is still running. Wait for it to finish, then close WaveLab again.",
                "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_vm.IsTransportRecording)
        {
            if (MessageBox.Show(
                    "Recording is still in progress. Stop and keep the capture now? WaveLab will stay open so you can review and save it.",
                    "Recording in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await _vm.FinishTransportRecordingAsync();
            return;
        }
        if (_vm.IsFinalizingRecording)
        {
            await _vm.FinishTransportRecordingAsync();
            return;
        }
        if (_vm.HasPendingTransportRecording)
        {
            var choice = MessageBox.Show(
                "A buffered recording still needs to be preserved. Retry finalizing it before exit? Choose No only to discard that capture.",
                "Recording recovery", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                await _vm.FinishTransportRecordingAsync();
                return;
            }
            // No is an explicit request to discard; normal exit cleanup owns it.
        }
        if (_vm.Documents.Any(d => d.IsDirty) &&
            MessageBox.Show("There are unsaved changes. Exit anyway?", "WaveLab",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _closing = true;
        IsEnabled = false;
        SaveWindowPlacement();
        try
        {
            await _startupTask;
            await _vm.OnCleanExitAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nAutosave recovery data, if available, was retained.", "Shutdown warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _allowClose = true;
        // Even when every awaited shutdown task is already complete, this
        // continuation can still be running inside WPF's original Closing
        // event. Calling Close re-entrantly from that event throws and brings
        // down the process. Queue the approved close so the cancelled event
        // can unwind before WPF starts the final close pass.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(Close));
    }

    // ── window placement ─────────────────────────────────────────

    private void RestoreWindowPlacement()
    {
        var s = AppSettings.Instance;
        if (s.WindowWidth > 600 && s.WindowHeight > 400)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
            if (s.WindowLeft is { } left && s.WindowTop is { } top)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var s = AppSettings.Instance;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowLeft = Left;
            s.WindowTop = Top;
        }
    }

    // ── dialogs ──────────────────────────────────────────────────

    private async void ShowRecordDialog()
    {
        bool punchAvailable = _vm.ActiveDocument?.HasSelection == true;
        var dialog = new RecordDialog(punchAvailable) { Owner = this };
        bool accepted = dialog.ShowDialog() == true;
        _vm.RefreshEngineStatus();
        if (!accepted || dialog.ViewModel.Result == null) return;
        if (punchAvailable && dialog.PunchRequested)
            await _vm.PunchInsertAsync(dialog.ViewModel.Result);
        else
            _vm.AddGeneratedDocument(dialog.ViewModel.Result);
    }

    private void OnExtractAudioCd(object sender, RoutedEventArgs e)
    {
        var dialog = new CdImportDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var import in dialog.Imports)
            _vm.AddGeneratedDocument(import.Document);
    }

    private void ShowSettingsDialog()
    {
        var dialog = new SettingsDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.RefreshEngineStatus();
            _vm.ReportAction("Settings saved.");
        }
    }

    private void ShowExportDialog()
    {
        if (_vm.ActiveDocument == null) return;
        if (new ExportDialog(_vm.ActiveDocument) { Owner = this }.ShowDialog() == true)
            _vm.ReportAction("Audio export completed.");
    }

    private void OnOpenAsBitDepth(object sender, RoutedEventArgs e)
    {
        var files = new OpenFileDialog
        {
            Filter = AudioImporter.OpenFilter,
            Multiselect = true,
            InitialDirectory = AppSettings.Instance.LastOpenFolder ?? "",
        };
        if (files.ShowDialog() != true) return;

        string[] choices =
        [
            "16-bit PCM · TPDF dither",
            "16-bit PCM · no dither",
            "24-bit PCM",
            "32-bit float",
        ];
        var choice = new ParamDialog(
            "Open As Bit Depth",
            "Open Copy",
            "Target encoding",
            choices,
            3) { Owner = this };
        if (choice.ShowDialog() != true) return;

        _vm.OpenFiles(files.FileNames, (OpenBitDepth)choice.ComboIndex);
    }

    private void ShowStatisticsDialog()
    {
        if (_vm.ActiveDocument == null) return;
        new StatisticsDialog(_vm.ActiveDocument.Doc) { Owner = this }.ShowDialog();
    }

    private void OnHelpCommand(object sender, ExecutedRoutedEventArgs e)
    {
        ShowHelp(HelpCatalog.StartTopicId);
        e.Handled = true;
    }

    private void OnHelpTopic(object sender, RoutedEventArgs e)
    {
        string? topicId = (sender as FrameworkElement)?.Tag as string;
        ShowHelp(topicId);
    }

    private void ShowHelp(string? topicId) =>
        new HelpDialog(topicId) { Owner = this }.ShowDialog();

    private void PromptSavePreset()
    {
        var name = TextPromptDialog.Show(this, "Save chain preset as…", "My Preset");
        if (!string.IsNullOrWhiteSpace(name)) _vm.Master.SavePresetAs(name);
    }

    private void OnAnalyzeVinylCleanup(object sender, RoutedEventArgs e) =>
        ShowCleanupAnalysis(CleanupProfile.VinylCleanup);

    private void OnAnalyzeCleanTransfer(object sender, RoutedEventArgs e) =>
        ShowCleanupAnalysis(CleanupProfile.CleanTransfer);

    private void OnAnalyzeCleanupMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var vinyl = new MenuItem { Header = "Analyze & Tune Vinyl Cleanup…" };
        vinyl.Click += (_, _) => ShowCleanupAnalysis(CleanupProfile.VinylCleanup);
        menu.Items.Add(vinyl);
        var clean = new MenuItem { Header = "Analyze & Tune Clean Transfer…" };
        clean.Click += (_, _) => ShowCleanupAnalysis(CleanupProfile.CleanTransfer);
        menu.Items.Add(clean);
        menu.PlacementTarget = analyzeCleanupBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ShowCleanupAnalysis(CleanupProfile profile)
    {
        var document = Doc;
        if (document == null || document.Doc.Length == 0 || !_vm.CanAnalyzeCleanup) return;

        var dialog = new CleanupAnalysisDialog(document, _vm, profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultPreset == null) return;
        try
        {
            _vm.Master.ApplyAnalyzedPreset(dialog.ResultPreset);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Analyze & Tune", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAddEffect(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (typeId, name) in EffectFactory.Available)
        {
            var item = new MenuItem { Header = name };
            string id = typeId;
            item.Click += (_, _) => _vm.Master.AddEffectCommand.Execute(id);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = addFxBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    // ── analysis pane ────────────────────────────────────────────

    private void RefreshSpectrogram() => _ = RefreshSpectrogramAsync();

    private Task<bool> RefreshSpectrogramAsync()
    {
        var d = _vm.ActiveDocument;
        if (d == null) return Task.FromResult(false);
        int start = (int)d.ViewStart;
        int end = (int)Math.Min(d.Doc.Length, d.ViewStart + d.SamplesPerPixel * d.ViewWidthPixels);
        return spectrogramView.RenderAsync(d.Doc, start, end);
    }

    private async void OnAnalysisTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, analysisTabs)) return;
        if (analysisTabs.SelectedIndex != 1) return;
        if (_skipNextAutomaticSpectrogramRender)
        {
            _skipNextAutomaticSpectrogramRender = false;
            return;
        }
        await RefreshSpectrogramAsync();
    }

    private async void OnRefreshSpectrogram(object sender, RoutedEventArgs e)
    {
        if (analysisTabs.SelectedIndex != 1)
        {
            _skipNextAutomaticSpectrogramRender = true;
            analysisTabs.SelectedIndex = 1;
        }
        if (await RefreshSpectrogramAsync())
            _vm.ReportAction("Spectrogram refreshed.");
    }

    // ── tools ────────────────────────────────────────────────────

    private DocumentViewModel? Doc => _vm.ActiveDocument;

    private void OnPrepareAudioCd(object sender, RoutedEventArgs e)
    {
        if (Doc == null) return;
        new CdTransferDialog(Doc, _vm) { Owner = this }.ShowDialog();
    }

    private void OnVinylWorkflow(object sender, RoutedEventArgs e)
    {
        var document = Doc;
        if (document == null || document.Doc.Length == 0) return;
        var restoration = new RestorationWorkbenchDialog(document, _vm) { Owner = this };
        bool applied = restoration.ShowDialog() == true;
        if (applied && restoration.PrepareCdRequested && _vm.Documents.Contains(document))
            new CdTransferDialog(document, _vm) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Run a data-transforming op off the UI thread, then commit it as an undoable edit.
    /// <paramref name="target"/> is the document the caller validated *before* it showed its
    /// parameter dialog: a modal dialog pumps a nested dispatcher frame, so an async void
    /// continuation (MainViewModel.OpenFiles → AddDocument) can change ActiveDocument while the
    /// dialog is open. Re-reading Doc here applied the tool — and the captured noise profile —
    /// to whichever file happened to become active.
    /// </summary>
    /// <summary>Tools that cannot report progress; they still get a cancellable indeterminate overlay.</summary>
    private Task<bool> RunRangeTool(string undoName, Func<float[][], int, float[][]?> transform,
        DocumentViewModel? target = null) =>
        RunRangeTool(undoName, null, (input, sampleRate, _, _) => transform(input, sampleRate), target);

    /// <summary>
    /// Dialog → background transform → one undoable splice, behind the progress overlay.
    /// </summary>
    /// <remarks>
    /// This no longer sets <c>IsEnabled = false</c> to keep the splice range valid: that would also
    /// disable the overlay's Cancel button. The overlay covers the window instead, and the range is
    /// re-validated against the document length before the splice regardless — which was always the
    /// real safety net, since a background transform could never have been trusted to a UI flag.
    /// </remarks>
    private async Task<bool> RunRangeTool(string undoName, string? detail,
        Func<float[][], int, IProgress<double>, CancellationToken, float[][]?> transform,
        DocumentViewModel? target = null)
    {
        if (_longOperationRunning) return false;
        var d = target ?? Doc;
        if (d == null || d.Doc.Length == 0 || !_vm.Documents.Contains(d)) return false;
        var (start, count) = d.EditRange();
        if (count <= 0) return false;
        var channels = d.Doc.Channels.ToArray();
        int sr = d.Doc.SampleRate;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        bool applied = false;
        try
        {
            await _vm.Progress.RunBlockingAsync(undoName, detail, async (progress, token) =>
            {
                var output = await Task.Run(() =>
                {
                    var input = channels.Select(ch => ch.AsSpan(start, count).ToArray()).ToArray();
                    return transform(input, sr, progress, token);
                }, token);
                if (output == null || start + count > d.Doc.Length) return;
                _vm.PrepareForDocumentEdit(d);
                d.Doc.ReplaceRange(start, count, output, undoName);
                applied = true;
            });
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction($"{undoName} cancelled · document unchanged.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, undoName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
        return applied;
    }

    private void OnResetAmpZoom(object sender, RoutedEventArgs e)
    {
        if (Doc != null)
        {
            Doc.AmpZoom = 1;
            _vm.ReportAction("Amplitude zoom reset.");
        }
    }

    private void OnManageMarkers(object sender, RoutedEventArgs e)
    {
        var doc = Doc;
        if (doc == null) return;
        var dialog = new MarkersDialog(doc) { Owner = this };
        // close the panel if its document tab goes away
        System.Collections.Specialized.NotifyCollectionChangedEventHandler onDocsChanged = (_, _) =>
        {
            if (!_vm.Documents.Contains(doc)) dialog.Close();
        };
        _vm.Documents.CollectionChanged += onDocsChanged;
        dialog.Closed += (_, _) => _vm.Documents.CollectionChanged -= onDocsChanged;
        dialog.Show();
    }

    // restoration

    private async void OnLearnNoise(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d == null) return;
        if (!d.HasSelection)
        {
            InfoDialog.Show(this, "Learn Noise Profile",
                "Select a stretch of noise-only audio (room tone, hiss between phrases) first, then run this again.");
            return;
        }
        var channels = d.Doc.Channels.ToArray();
        int start = d.SelStart, count = d.SelEnd - d.SelStart;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _vm.Progress.RunBlockingAsync("Learning noise profile",
                "Averaging the spectrum of the selection",
                async (_, token) => d.NoiseProfile =
                    await Task.Run(() => Restoration.LearnNoiseProfile(channels, start, count, token), token));
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction("Learning the noise profile was cancelled.");
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Learn Noise Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
        InfoDialog.Show(this, "Noise Profile Learned",
            "Profile captured from the selection. Now choose Restore → Reduce Noise to apply it to the whole file or another selection.");
        _vm.ReportAction("Noise profile learned from the selection.");
    }

    private void OnReduceNoise(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null) return;
        if (d.NoiseProfile == null)
        {
            InfoDialog.Show(this, "Reduce Noise", "Learn a noise profile from a noise-only selection first (Restore menu).");
            return;
        }
        var dlg = new ParamDialog("Reduce Noise", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("Reduction", 6, 40, 12, v => $"{v:0} dB"),
            new ParamDialog.SliderSpec("Sensitivity", 0, 12, 6, v => $"{v:0} dB")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var profile = d.NoiseProfile;
        double reduction = dlg.Values[0], sensitivity = dlg.Values[1];
        _ = RunRangeTool("Reduce Noise", (data, _) =>
        {
            Restoration.ReduceNoise(data, profile, reduction, sensitivity);
            return data;
        }, d);
    }

    private async void OnRemoveClicks(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = new ParamDialog("Remove Clicks & Pops", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("Sensitivity", 1, 10, 5, v => $"{v:0}", 1)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        double sensitivity = dlg.Values[0];
        int repaired = -1;
        bool completed = await RunRangeTool("Remove Clicks", (data, sampleRate) =>
        {
            repaired = Restoration.RemoveClicks(data, sampleRate, sensitivity);
            // A no-op should not allocate an album-sized undo entry or mark the
            // document dirty merely so the UI can report that nothing was found.
            return repaired > 0 ? data : null;
        }, d);
        if (repaired == 0)
        {
            InfoDialog.Show(this, "Remove Clicks & Pops",
                "No clicks found at this sensitivity — try a higher setting.");
            return;
        }
        if (!completed) return;
        InfoDialog.Show(this, "Remove Clicks & Pops",
            $"{repaired} click(s) repaired. Undo with Ctrl+Z if it went too far.");
    }

    private void OnRemoveHum(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = new ParamDialog("Remove Hum", "Apply", "Mains frequency", ["50 Hz (Europe)", "60 Hz (Americas)"], 1,
            new ParamDialog.SliderSpec("Harmonics", 1, 8, 4, v => $"{v:0}", 1),
            new ParamDialog.SliderSpec("Notch width (Q)", 10, 60, 30, v => $"Q {v:0}")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        double baseFreq = dlg.ComboIndex == 0 ? 50 : 60;
        int harmonics = (int)dlg.Values[0];
        double q = dlg.Values[1];
        _ = RunRangeTool("Remove Hum", (data, sr) =>
        {
            Restoration.RemoveHum(data, sr, baseFreq, harmonics, q);
            return data;
        }, d);
    }

    // silence

    private ParamDialog? SilenceDialog(string title, string ok) =>
        new(title, ok, null, null, 0,
            new ParamDialog.SliderSpec("Threshold", -80, -20, -50, v => $"{v:0} dBFS"),
            new ParamDialog.SliderSpec("Minimum length", 100, 3000, 500, v => $"{v:0} ms"))
        { Owner = this };

    private async void OnDetectSilence(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = SilenceDialog("Detect Silences", "Mark");
        if (dlg!.ShowDialog() != true) return;
        var silences = await DetectSilencesAsync(d, dlg.Values[0], dlg.Values[1]);
        if (silences == null) return;
        d.AddMarkers(silences.Select(s =>
            (s.Start, (string?)$"Silence {TimeFormat.Compact((double)s.Start / d.Doc.SampleRate)}")));
        _vm.ReportAction($"Silence detection completed · {silences.Count} marker(s) added.");
        InfoDialog.Show(this, "Detect Silences", $"{silences.Count} silent stretch(es) marked.");
    }

    private async void OnTrimSilence(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = SilenceDialog("Trim Silences", "Trim");
        if (dlg!.ShowDialog() != true) return;
        var silences = await DetectSilencesAsync(d, dlg.Values[0], dlg.Values[1]);
        if (silences == null) return;
        if (silences.Count == 0)
        {
            InfoDialog.Show(this, "Trim Silences", "Nothing below the threshold was found.");
            return;
        }
        int pad = d.Doc.SampleRate / 20; // keep 50 ms breaths
        int removed = 0;
        _vm.PrepareForDocumentEdit(d);
        foreach (var (start, end) in silences.OrderByDescending(s => s.Start))
        {
            int from = start + pad, to = end - pad;
            if (to - from < d.Doc.SampleRate / 10) continue;
            var empty = new float[d.Doc.ChannelCount][];
            for (int c = 0; c < empty.Length; c++) empty[c] = [];
            d.Doc.ReplaceRange(from, to - from, empty, "Trim Silence");
            removed += to - from;
        }
        InfoDialog.Show(this, "Trim Silences",
            $"Removed {TimeFormat.Compact((double)removed / d.Doc.SampleRate)} of silence across {silences.Count} stretch(es). Each removal is individually undoable.");
    }

    private async void OnSplitSilence(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = SilenceDialog("Split by Silence", "Split");
        if (dlg!.ShowDialog() != true) return;
        var silences = await DetectSilencesAsync(d, dlg.Values[0], dlg.Values[1]);
        if (silences == null) return;
        int prevEnd = 0, n = 0;
        foreach (var (start, end) in silences)
        {
            if (start - prevEnd > d.Doc.SampleRate / 10)
                d.Regions.Add(new NamedRegion { Name = $"Part {++n}", Start = prevEnd, End = start });
            prevEnd = end;
        }
        if (d.Doc.Length - prevEnd > d.Doc.SampleRate / 10)
            d.Regions.Add(new NamedRegion { Name = $"Part {++n}", Start = prevEnd, End = d.Doc.Length });
        d.NotifyMarkersChanged();
        _vm.ReportAction($"Split by Silence completed · {n} region(s) created.");
        InfoDialog.Show(this, "Split by Silence", $"{n} region(s) created — click a region band in the ruler to select it.");
    }

    private async Task<List<(int Start, int End)>?> DetectSilencesAsync(
        DocumentViewModel document, double threshold, double minimumLength)
    {
        if (_longOperationRunning) return null;
        var channels = document.Doc.Channels.ToArray();
        int sampleRate = document.Doc.SampleRate;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            List<(int Start, int End)>? found = null;
            await _vm.Progress.RunBlockingAsync("Detecting silences", "Scanning the file for quiet gaps",
                async (_, token) => found = await Task.Run(() => Restoration.DetectSilences(
                    channels, sampleRate, threshold, minimumLength), token));
            return found;
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction("Silence detection cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Silence Detection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
    }

    // channels

    private void OnSwapChannels(object sender, RoutedEventArgs e) => EditDocument(ChannelTools.SwapChannels);
    private void OnInvertPhase(object sender, RoutedEventArgs e) => EditDocument(doc => ChannelTools.InvertPhase(doc, -1));
    private void OnInvertLeft(object sender, RoutedEventArgs e) => EditDocument(doc => ChannelTools.InvertPhase(doc, 0));
    private void OnInvertRight(object sender, RoutedEventArgs e)
    {
        if (Doc is { Doc.ChannelCount: > 1 } d) { _vm.PrepareForDocumentEdit(d); ChannelTools.InvertPhase(d.Doc, 1); }
    }

    private void EditDocument(Action<AudioDocument> edit)
    {
        var document = Doc;
        if (document == null) return;
        _vm.PrepareForDocumentEdit(document);
        try { edit(document.Doc); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Edit failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OnMonoMixdown(object sender, RoutedEventArgs e) =>
        await RunGeneratedDocumentTool("Mono Mixdown", ChannelTools.MonoMixdown);
    private async void OnExtractLeft(object sender, RoutedEventArgs e) =>
        await RunGeneratedDocumentTool("Extract Left", doc => ChannelTools.ExtractChannel(doc, 0));
    private async void OnExtractRight(object sender, RoutedEventArgs e)
    {
        if (Doc is { Doc.ChannelCount: > 1 })
            await RunGeneratedDocumentTool("Extract Right", doc => ChannelTools.ExtractChannel(doc, 1));
    }

    private async void OnMonoToStereo(object sender, RoutedEventArgs e)
    {
        if (Doc is { Doc.ChannelCount: 1 })
            await RunGeneratedDocumentTool("Mono to Stereo", ChannelTools.MonoToStereo);
        else InfoDialog.Show(this, "Mono → Stereo", "The active file is already multi-channel.");
    }

    private async Task RunGeneratedDocumentTool(string title, Func<AudioDocument, AudioDocument> transform)
    {
        if (_longOperationRunning || Doc is not { } document) return;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _vm.Progress.RunBlockingAsync(title, "Writing to a new tab · source unchanged",
                async (_, token) =>
                {
                    var generated = await Task.Run(() => transform(document.Doc), token);
                    _vm.AddGeneratedDocument(generated,
                        $"{title} completed in a new tab · source audio unchanged.");
                });
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction($"{title} cancelled.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
    }

    private void OnChannelBalance(object sender, RoutedEventArgs e)
    {
        if (Doc is not { Doc.ChannelCount: > 1 }) return;
        var dlg = new ParamDialog("Channel Balance", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("Left gain", -24, 6, 0, v => $"{v:+0.0;-0.0;0.0} dB"),
            new ParamDialog.SliderSpec("Right gain", -24, 6, 0, v => $"{v:+0.0;-0.0;0.0} dB")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _vm.PrepareForDocumentEdit(Doc!);
        ChannelTools.Balance(Doc!.Doc, dlg.Values[0], dlg.Values[1]);
    }

    // time / pitch / rate

    private void OnTimeStretch(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = new ParamDialog("Time Stretch (keeps pitch)", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("New length", 50, 200, 100, v => $"{v:0} %", 1)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        double factor = dlg.Values[0] / 100.0;
        if (Math.Abs(factor - 1) < 0.005)
        {
            _vm.ReportAction("Time Stretch unchanged · no processing applied.");
            return;
        }
        _ = RunRangeTool("Time Stretch", $"WSOLA · {factor:0.###}× duration",
            (data, sr, progress, token) => TimeStretch.Stretch(data, sr, factor, token, progress), d);
    }

    private void OnPitchShift(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = new ParamDialog("Pitch Shift (keeps duration)", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("Semitones", -12, 12, 0, v => $"{v:+0;-0;0} st", 1),
            new ParamDialog.SliderSpec("Cents", -100, 100, 0, v => $"{v:+0;-0;0} ¢", 1)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        double semitones = dlg.Values[0] + dlg.Values[1] / 100.0;
        if (Math.Abs(semitones) < 0.01)
        {
            _vm.ReportAction("Pitch Shift unchanged · no processing applied.");
            return;
        }
        _ = RunRangeTool("Pitch Shift", $"{semitones:+0.##;-0.##;0} semitones · stretch then resample",
            (data, sr, progress, token) => TimeStretch.PitchShift(data, sr, semitones, token, progress), d);
    }

    private async void OnConvertRate(object sender, RoutedEventArgs e)
    {
        if (_longOperationRunning) return;
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        int[] rates = [44100, 48000, 88200, 96000, 192000];
        var items = rates.Select(r => $"{r / 1000.0:0.###} kHz").ToArray();
        var dlg = new ParamDialog($"Convert Sample Rate (from {d.Doc.SampleRate / 1000.0:0.###} kHz)", "Convert → New Tab",
            "Target rate", items, 1) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        int target = rates[Math.Max(0, dlg.ComboIndex)];
        if (target == d.Doc.SampleRate)
        {
            _vm.ReportAction("Sample rate unchanged · no conversion applied.");
            return;
        }
        var doc = d.Doc;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _vm.Progress.RunBlockingAsync("Converting sample rate",
                $"{doc.SampleRate / 1000.0:0.###} → {target / 1000.0:0.###} kHz · windowed-sinc",
                async (progress, token) =>
                {
                    var converted = await Task.Run(
                        () => ChannelTools.ConvertSampleRate(doc, target, token, progress), token);
                    _vm.AddGeneratedDocument(converted,
                        $"Sample rate converted to {target / 1000.0:0.###} kHz in a new tab · source audio unchanged.");
                });
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction("Sample rate conversion cancelled.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Convert Sample Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
    }

    // analysis tools

    private async void OnTuner(object sender, RoutedEventArgs e)
    {
        if (_longOperationRunning) return;
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var (start, count) = d.HasSelection
            ? (d.SelStart, Math.Min(d.SelEnd - d.SelStart, d.Doc.SampleRate * 10))
            : (Math.Max(0, d.Cursor - d.Doc.SampleRate), Math.Min(d.Doc.Length, d.Doc.SampleRate * 3));
        count = Math.Min(count, d.Doc.Length - start);
        if (count < 4096 * 2)
        {
            InfoDialog.Show(this, "Tuner", "Select at least a fifth of a second of audio.");
            return;
        }
        var doc = d.Doc;
        var chans = doc.Channels.ToArray(); // stable refs — splices never mutate old arrays
        int chCount = chans.Length;
        int sampleRate = doc.SampleRate;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            (double Frequency, double Confidence) result = default;
            await _vm.Progress.RunBlockingAsync("Detecting pitch", "YIN over the selection",
                async (_, token) => result = await Task.Run(() =>
                {
                    var mono = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        float v = 0;
                        for (int c = 0; c < chCount; c++) v += chans[c][start + i];
                        mono[i] = v / chCount;
                    }
                    return PitchDetect.Detect(mono, sampleRate);
                }, token));
            InfoDialog.Show(this, "Tuner",
                result.Frequency > 0
                    ? $"Confidence {result.Confidence:P0}. Detected over {TimeFormat.Compact((double)count / doc.SampleRate)} of audio."
                    : "No stable pitch detected — try selecting a sustained note.",
                result.Frequency > 0 ? PitchDetect.Describe(result.Frequency) : null);
        }
        catch (OperationCanceledException) { _vm.ReportAction("Pitch detection cancelled."); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Tuner", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
    }

    private async void OnBpm(object sender, RoutedEventArgs e)
    {
        if (_longOperationRunning) return;
        var d = Doc;
        if (d == null || d.Doc.Length < d.Doc.SampleRate * 5) return;
        var chans = d.Doc.Channels.ToArray(); // stable refs captured on the UI thread
        int sampleRate = d.Doc.SampleRate;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            double bpm = 0, confidence = 0;
            await _vm.Progress.RunBlockingAsync("Detecting tempo", "Onset autocorrelation",
                async (_, token) => (bpm, confidence) =
                    await Task.Run(() => TempoDetect.Detect(chans, sampleRate), token));
            InfoDialog.Show(this, "Tempo Detection",
                bpm > 0 ? $"Confidence {confidence:P0}. Half/double-time ({bpm / 2:0.#} / {bpm * 2:0.#} BPM) may also fit."
                        : "No clear tempo found — the material may be too sparse or rubato.",
                bpm > 0 ? $"{bpm:0.#} BPM" : null);
        }
        catch (OperationCanceledException) { _vm.ReportAction("Tempo detection cancelled."); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Tempo Detection", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
    }

    private void OnBatchConvert(object sender, RoutedEventArgs e) =>
        new BatchConvertDialog { Owner = this }.ShowDialog();

    private void ShowCommandPalette()
    {
        static CommandPalette.Command VmCommand(string name, string? gesture, ICommand command, object? parameter = null) =>
            new(name, gesture, () => command.Execute(parameter), () => command.CanExecute(parameter));

        var commands = new List<CommandPalette.Command>
        {
            VmCommand("Open File…", "Ctrl+O", _vm.OpenCommand),
            new("Extract Audio CD…", null, () => OnExtractAudioCd(this, new RoutedEventArgs())),
            VmCommand("Save", "Ctrl+S", _vm.SaveCommand),
            VmCommand("Save As…", "Ctrl+Shift+S", _vm.SaveAsCommand),
            new("Open As Bit Depth…", null, () => OnOpenAsBitDepth(this, new RoutedEventArgs())),
            VmCommand("Export…", "Ctrl+E", _vm.ExportCommand),
            VmCommand("Recording Setup…", null, _vm.RecordSetupCommand),
            VmCommand("Record / Stop", "Ctrl+R", _vm.RecordCommand),
            VmCommand("Play / Pause", "Space", _vm.PlayCommand),
            VmCommand("Stop", null, _vm.StopCommand),
            VmCommand("Go to Start", "Home", _vm.GoToStartCommand),
            VmCommand("Undo", "Ctrl+Z", _vm.UndoCommand),
            VmCommand("Redo", "Ctrl+Y", _vm.RedoCommand),
            VmCommand("Cut", "Ctrl+X", _vm.CutCommand),
            VmCommand("Copy", "Ctrl+C", _vm.CopyCommand),
            VmCommand("Paste", "Ctrl+V", _vm.PasteCommand),
            VmCommand("Trim to Selection", null, _vm.TrimCommand),
            VmCommand("Select All", "Ctrl+A", _vm.SelectAllCommand),
            VmCommand("Zoom to Fit", "Ctrl+0", _vm.ZoomFitCommand),
            VmCommand("Zoom to Selection", null, _vm.ZoomSelectionCommand),
            VmCommand("Add Marker", "Ctrl+M", _vm.AddMarkerCommand),
            VmCommand("Add Region from Selection", "Ctrl+Shift+M", _vm.AddRegionCommand),
            new("Manage Markers & Regions…", null, () => OnManageMarkers(this, new RoutedEventArgs()), () => _vm.HasDocument),
            VmCommand("Gain +3 dB", null, _vm.GainUpCommand),
            VmCommand("Gain −3 dB", null, _vm.GainDownCommand),
            VmCommand("Normalize to −0.3 dBFS", null, _vm.NormalizeCommand),
            VmCommand("Fade In", null, _vm.FadeInCommand),
            VmCommand("Fade Out", null, _vm.FadeOutCommand),
            VmCommand("Reverse", null, _vm.ReverseCommand),
            VmCommand("Remove DC Offset", null, _vm.RemoveDcCommand),
            VmCommand("Smooth Edit Points", null, _vm.SmoothEditCommand),
            new("Detect Silences → Markers…", null, () => OnDetectSilence(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Trim Silences…", null, () => OnTrimSilence(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Split by Silence → Regions…", null, () => OnSplitSilence(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Swap Channels", null, () => OnSwapChannels(this, new RoutedEventArgs()), () => _vm.HasMultichannelDocument),
            new("Invert Phase", null, () => OnInvertPhase(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Mix Down to Mono", null, () => OnMonoMixdown(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Learn Noise Profile from Selection", null, () => OnLearnNoise(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Vinyl Restoration & CD Transfer…", null, () => OnVinylWorkflow(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Prepare Tracks for Audio CD…", null, () => OnPrepareAudioCd(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Reduce Noise…", null, () => OnReduceNoise(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Remove Clicks & Pops…", null, () => OnRemoveClicks(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Remove Hum…", null, () => OnRemoveHum(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Time Stretch…", null, () => OnTimeStretch(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Pitch Shift…", null, () => OnPitchShift(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Convert Sample Rate…", null, () => OnConvertRate(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Detect Pitch (Tuner)", null, () => OnTuner(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            new("Detect Tempo (BPM)", null, () => OnBpm(this, new RoutedEventArgs()), () => _vm.HasAudioDocument),
            VmCommand("Audio Statistics…", null, _vm.StatisticsCommand),
            new("Analyze & Tune Vinyl Cleanup…", null, () => ShowCleanupAnalysis(CleanupProfile.VinylCleanup), () => _vm.CanAnalyzeCleanup),
            new("Analyze & Tune Clean Transfer…", null, () => ShowCleanupAnalysis(CleanupProfile.CleanTransfer), () => _vm.CanAnalyzeCleanup),
            VmCommand("Render in Place (Undoable)", null, _vm.ApplyChainCommand),
            VmCommand("Render Copy to New Tab", null, _vm.RenderCommand),
            new("Batch Converter…", null, () => OnBatchConvert(this, new RoutedEventArgs())),
            VmCommand("Settings…", null, _vm.SettingsCommand),
        };
        new CommandPalette(commands) { Owner = this }.ShowDialog();
    }

    // ── window chrome / misc ─────────────────────────────────────

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _vm.OpenFiles(files);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();
}
