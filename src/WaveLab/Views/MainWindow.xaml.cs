using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _allowClose;
    private bool _closing;
    private bool _longOperationRunning;
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
        Close();
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
            _vm.RefreshEngineStatus();
    }

    private void ShowExportDialog()
    {
        if (_vm.ActiveDocument == null) return;
        new ExportDialog(_vm.ActiveDocument) { Owner = this }.ShowDialog();
    }

    private void ShowStatisticsDialog()
    {
        if (_vm.ActiveDocument == null) return;
        new StatisticsDialog(_vm.ActiveDocument.Doc) { Owner = this }.ShowDialog();
    }

    private void PromptSavePreset()
    {
        var name = TextPromptDialog.Show(this, "Save chain preset as…", "My Preset");
        if (!string.IsNullOrWhiteSpace(name)) _vm.Master.SavePresetAs(name);
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

    private void RefreshSpectrogram()
    {
        var d = _vm.ActiveDocument;
        if (d == null) return;
        int start = (int)d.ViewStart;
        int end = (int)Math.Min(d.Doc.Length, d.ViewStart + d.SamplesPerPixel * d.ViewWidthPixels);
        spectrogramView.Render(d.Doc, start, end);
    }

    private void OnAnalysisTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, analysisTabs)) return;
        if (analysisTabs.SelectedIndex == 1) RefreshSpectrogram();
    }

    private void OnRefreshSpectrogram(object sender, RoutedEventArgs e)
    {
        if (analysisTabs.SelectedIndex == 1) RefreshSpectrogram();
        else analysisTabs.SelectedIndex = 1;
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

    /// <summary>Run a data-transforming op off the UI thread, then commit it as an undoable edit.</summary>
    private async Task<bool> RunRangeTool(string undoName, Func<float[][], int, float[][]?> transform)
    {
        if (_longOperationRunning) return false;
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return false;
        var (start, count) = d.EditRange();
        if (count <= 0) return false;
        var channels = d.Doc.Channels.ToArray();
        int sr = d.Doc.SampleRate;
        _longOperationRunning = true;
        IsEnabled = false; // block edits while the transform runs so the splice range stays valid
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var output = await Task.Run(() =>
            {
                var input = channels.Select(ch => ch.AsSpan(start, count).ToArray()).ToArray();
                return transform(input, sr);
            });
            if (output == null || start + count > d.Doc.Length) return false;
            _vm.PrepareForDocumentEdit(d);
            d.Doc.ReplaceRange(start, count, output, undoName);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, undoName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
            _longOperationRunning = false;
        }
    }

    private void OnResetAmpZoom(object sender, RoutedEventArgs e)
    {
        if (Doc != null) Doc.AmpZoom = 1;
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
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try { d.NoiseProfile = await Task.Run(() => Restoration.LearnNoiseProfile(channels, start, count)); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Learn Noise Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
            _longOperationRunning = false;
        }
        InfoDialog.Show(this, "Noise Profile Learned",
            "Profile captured from the selection. Now choose Restore → Reduce Noise to apply it to the whole file or another selection.");
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
        });
    }

    private async void OnRemoveClicks(object sender, RoutedEventArgs e)
    {
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
        });
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
        });
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
        InfoDialog.Show(this, "Split by Silence", $"{n} region(s) created — click a region band in the ruler to select it.");
    }

    private async Task<List<(int Start, int End)>?> DetectSilencesAsync(
        DocumentViewModel document, double threshold, double minimumLength)
    {
        if (_longOperationRunning) return null;
        var channels = document.Doc.Channels.ToArray();
        int sampleRate = document.Doc.SampleRate;
        _longOperationRunning = true;
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            return await Task.Run(() => Restoration.DetectSilences(
                channels, sampleRate, threshold, minimumLength));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Silence Detection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
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
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var generated = await Task.Run(() => transform(document.Doc));
            _vm.AddGeneratedDocument(generated);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
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
        if (Math.Abs(factor - 1) < 0.005) return;
        _ = RunRangeTool("Time Stretch", (data, sr) => TimeStretch.Stretch(data, sr, factor));
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
        if (Math.Abs(semitones) < 0.01) return;
        _ = RunRangeTool("Pitch Shift", (data, sr) => TimeStretch.PitchShift(data, sr, semitones));
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
        if (target == d.Doc.SampleRate) return;
        var doc = d.Doc;
        _longOperationRunning = true;
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var converted = await Task.Run(() => ChannelTools.ConvertSampleRate(doc, target));
            _vm.AddGeneratedDocument(converted);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Convert Sample Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
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
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
        var result = await Task.Run(() =>
        {
            var mono = new float[count];
            for (int i = 0; i < count; i++)
            {
                float v = 0;
                for (int c = 0; c < chCount; c++) v += chans[c][start + i];
                mono[i] = v / chCount;
            }
            return PitchDetect.Detect(mono, sampleRate);
        });
        InfoDialog.Show(this, "Tuner",
            result.Frequency > 0
                ? $"Confidence {result.Confidence:P0}. Detected over {TimeFormat.Compact((double)count / doc.SampleRate)} of audio."
                : "No stable pitch detected — try selecting a sustained note.",
            result.Frequency > 0 ? PitchDetect.Describe(result.Frequency) : null);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Tuner", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
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
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (bpm, confidence) = await Task.Run(() => TempoDetect.Detect(chans, sampleRate));
            InfoDialog.Show(this, "Tempo Detection",
                bpm > 0 ? $"Confidence {confidence:P0}. Half/double-time ({bpm / 2:0.#} / {bpm * 2:0.#} BPM) may also fit."
                        : "No clear tempo found — the material may be too sparse or rubato.",
                bpm > 0 ? $"{bpm:0.#} BPM" : null);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Tempo Detection", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
            _longOperationRunning = false;
        }
    }

    private void OnBatchConvert(object sender, RoutedEventArgs e) =>
        new BatchConvertDialog { Owner = this }.ShowDialog();

    private void ShowCommandPalette()
    {
        var commands = new List<CommandPalette.Command>
        {
            new("Open File…", "Ctrl+O", () => _vm.OpenCommand.Execute(null)),
            new("Extract Audio CD…", null, () => OnExtractAudioCd(this, new RoutedEventArgs())),
            new("Save", "Ctrl+S", () => _vm.SaveCommand.Execute(null)),
            new("Save As…", "Ctrl+Shift+S", () => _vm.SaveAsCommand.Execute(null)),
            new("Export…", "Ctrl+E", () => _vm.ExportCommand.Execute(null)),
            new("Recording Setup…", null, () =>
            {
                if (_vm.RecordSetupCommand.CanExecute(null))
                    _vm.RecordSetupCommand.Execute(null);
            }),
            new("Record / Stop", "Ctrl+R", () => _vm.RecordCommand.Execute(null)),
            new("Play / Stop", "Space", () => _vm.PlayCommand.Execute(null)),
            new("Go to Start", "Home", () => _vm.GoToStartCommand.Execute(null)),
            new("Undo", "Ctrl+Z", () => _vm.UndoCommand.Execute(null)),
            new("Redo", "Ctrl+Y", () => _vm.RedoCommand.Execute(null)),
            new("Cut", "Ctrl+X", () => _vm.CutCommand.Execute(null)),
            new("Copy", "Ctrl+C", () => _vm.CopyCommand.Execute(null)),
            new("Paste", "Ctrl+V", () => _vm.PasteCommand.Execute(null)),
            new("Trim to Selection", null, () => _vm.TrimCommand.Execute(null)),
            new("Select All", "Ctrl+A", () => _vm.SelectAllCommand.Execute(null)),
            new("Zoom to Fit", "Ctrl+0", () => _vm.ZoomFitCommand.Execute(null)),
            new("Zoom to Selection", null, () => _vm.ZoomSelectionCommand.Execute(null)),
            new("Add Marker", "Ctrl+M", () => _vm.AddMarkerCommand.Execute(null)),
            new("Add Region from Selection", "Ctrl+Shift+M", () => _vm.AddRegionCommand.Execute(null)),
            new("Manage Markers & Regions…", null, () => OnManageMarkers(this, new RoutedEventArgs())),
            new("Gain +3 dB", null, () => _vm.GainUpCommand.Execute(null)),
            new("Gain −3 dB", null, () => _vm.GainDownCommand.Execute(null)),
            new("Normalize to −0.3 dBFS", null, () => _vm.NormalizeCommand.Execute(null)),
            new("Fade In", null, () => _vm.FadeInCommand.Execute(null)),
            new("Fade Out", null, () => _vm.FadeOutCommand.Execute(null)),
            new("Reverse", null, () => _vm.ReverseCommand.Execute(null)),
            new("Remove DC Offset", null, () => _vm.RemoveDcCommand.Execute(null)),
            new("Smooth Edit Points", null, () => _vm.SmoothEditCommand.Execute(null)),
            new("Detect Silences → Markers…", null, () => OnDetectSilence(this, new RoutedEventArgs())),
            new("Trim Silences…", null, () => OnTrimSilence(this, new RoutedEventArgs())),
            new("Split by Silence → Regions…", null, () => OnSplitSilence(this, new RoutedEventArgs())),
            new("Swap Channels", null, () => OnSwapChannels(this, new RoutedEventArgs())),
            new("Invert Phase", null, () => OnInvertPhase(this, new RoutedEventArgs())),
            new("Mix Down to Mono", null, () => OnMonoMixdown(this, new RoutedEventArgs())),
            new("Learn Noise Profile from Selection", null, () => OnLearnNoise(this, new RoutedEventArgs())),
            new("Vinyl Restoration & CD Transfer…", null, () => OnVinylWorkflow(this, new RoutedEventArgs())),
            new("Prepare Tracks for Audio CD…", null, () => OnPrepareAudioCd(this, new RoutedEventArgs())),
            new("Reduce Noise…", null, () => OnReduceNoise(this, new RoutedEventArgs())),
            new("Remove Clicks & Pops…", null, () => OnRemoveClicks(this, new RoutedEventArgs())),
            new("Remove Hum…", null, () => OnRemoveHum(this, new RoutedEventArgs())),
            new("Time Stretch…", null, () => OnTimeStretch(this, new RoutedEventArgs())),
            new("Pitch Shift…", null, () => OnPitchShift(this, new RoutedEventArgs())),
            new("Convert Sample Rate…", null, () => OnConvertRate(this, new RoutedEventArgs())),
            new("Detect Pitch (Tuner)", null, () => OnTuner(this, new RoutedEventArgs())),
            new("Detect Tempo (BPM)", null, () => OnBpm(this, new RoutedEventArgs())),
            new("Audio Statistics…", null, () => _vm.StatisticsCommand.Execute(null)),
            new("Apply Chain to Selection / File", null, () => _vm.ApplyChainCommand.Execute(null)),
            new("Render to New Tab", null, () => _vm.RenderCommand.Execute(null)),
            new("Batch Converter…", null, () => OnBatchConvert(this, new RoutedEventArgs())),
            new("Settings…", null, () => _vm.SettingsCommand.Execute(null)),
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
