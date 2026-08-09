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

        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs().Skip(1).Where(System.IO.File.Exists).ToArray();
            _vm.StartupLoad(args);
        };

        Closing += async (_, e) =>
        {
            if (_vm.IsTransportRecording)
            {
                e.Cancel = true;
                if (MessageBox.Show(
                        "Recording is still in progress. Stop and keep the capture now? WaveLab will stay open so you can review and save it.",
                        "Recording in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    await _vm.FinishTransportRecordingAsync();
                return;
            }
            if (_vm.IsFinalizingRecording)
            {
                e.Cancel = true;
                await _vm.FinishTransportRecordingAsync();
                return;
            }
            if (_vm.HasPendingTransportRecording)
            {
                var choice = MessageBox.Show(
                    "A buffered recording still needs to be preserved. Retry finalizing it before exit? Choose No only to discard that capture.",
                    "Recording recovery", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (choice == MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    await _vm.FinishTransportRecordingAsync();
                    return;
                }
                // No is an explicit request to discard; normal exit cleanup owns it.
            }
            if (_vm.Documents.Any(d => d.IsDirty) &&
                MessageBox.Show("There are unsaved changes. Exit anyway?", "WaveLab",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            SaveWindowPlacement();
            _vm.OnCleanExit();
        };
    }

    // ── window placement ─────────────────────────────────────────

    private void RestoreWindowPlacement()
    {
        var s = AppSettings.Instance;
        if (s.WindowWidth > 600 && s.WindowHeight > 400)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
            if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
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

    private void ShowRecordDialog()
    {
        bool punchAvailable = _vm.ActiveDocument?.HasSelection == true;
        var dialog = new RecordDialog(punchAvailable) { Owner = this };
        bool accepted = dialog.ShowDialog() == true;
        _vm.RefreshEngineStatus();
        if (!accepted || dialog.ViewModel.Result == null) return;
        if (punchAvailable && dialog.PunchRequested)
            _vm.PunchInsert(dialog.ViewModel.Result);
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
        analysisTabs.SelectedIndex = 1;
        RefreshSpectrogram();
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
    private async Task RunRangeTool(string undoName, Func<float[][], int, float[][]?> transform)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var (start, count) = d.EditRange();
        if (count <= 0) return;
        var input = d.Doc.CopyRange(start, count);
        int sr = d.Doc.SampleRate;
        IsEnabled = false; // block edits while the transform runs so the splice range stays valid
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var output = await Task.Run(() => transform(input, sr));
            if (output != null && start + count <= d.Doc.Length)
                d.Doc.ReplaceRange(start, count, output, undoName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, undoName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
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

    private void OnLearnNoise(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null) return;
        if (!d.HasSelection)
        {
            InfoDialog.Show(this, "Learn Noise Profile",
                "Select a stretch of noise-only audio (room tone, hiss between phrases) first, then run this again.");
            return;
        }
        d.NoiseProfile = Restoration.LearnNoiseProfile(d.Doc.Channels, d.SelStart, d.SelEnd - d.SelStart);
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
        int repaired = 0;
        await RunRangeTool("Remove Clicks", (data, sampleRate) =>
        {
            repaired = Restoration.RemoveClicks(data, sampleRate, sensitivity);
            return data;
        });
        InfoDialog.Show(this, "Remove Clicks & Pops",
            repaired > 0 ? $"{repaired} click(s) repaired. Undo with Ctrl+Z if it went too far."
                         : "No clicks found at this sensitivity — try a higher setting.");
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

    private void OnDetectSilence(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = SilenceDialog("Detect Silences", "Mark");
        if (dlg!.ShowDialog() != true) return;
        var silences = Restoration.DetectSilences(d.Doc.Channels, d.Doc.SampleRate, dlg.Values[0], dlg.Values[1]);
        foreach (var (start, _) in silences) d.AddMarker(start, $"Silence {TimeFormat.Compact((double)start / d.Doc.SampleRate)}");
        InfoDialog.Show(this, "Detect Silences", $"{silences.Count} silent stretch(es) marked.");
    }

    private void OnTrimSilence(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = SilenceDialog("Trim Silences", "Trim");
        if (dlg!.ShowDialog() != true) return;
        var silences = Restoration.DetectSilences(d.Doc.Channels, d.Doc.SampleRate, dlg.Values[0], dlg.Values[1]);
        if (silences.Count == 0)
        {
            InfoDialog.Show(this, "Trim Silences", "Nothing below the threshold was found.");
            return;
        }
        int pad = d.Doc.SampleRate / 20; // keep 50 ms breaths
        int removed = 0;
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

    private void OnSplitSilence(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length == 0) return;
        var dlg = SilenceDialog("Split by Silence", "Split");
        if (dlg!.ShowDialog() != true) return;
        var silences = Restoration.DetectSilences(d.Doc.Channels, d.Doc.SampleRate, dlg.Values[0], dlg.Values[1]);
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

    // channels

    private void OnSwapChannels(object sender, RoutedEventArgs e) { if (Doc != null) ChannelTools.SwapChannels(Doc.Doc); }
    private void OnInvertPhase(object sender, RoutedEventArgs e) { if (Doc != null) ChannelTools.InvertPhase(Doc.Doc, -1); }
    private void OnInvertLeft(object sender, RoutedEventArgs e) { if (Doc != null) ChannelTools.InvertPhase(Doc.Doc, 0); }
    private void OnInvertRight(object sender, RoutedEventArgs e) { if (Doc is { Doc.ChannelCount: > 1 }) ChannelTools.InvertPhase(Doc.Doc, 1); }
    private void OnMonoMixdown(object sender, RoutedEventArgs e) { if (Doc != null) _vm.AddGeneratedDocument(ChannelTools.MonoMixdown(Doc.Doc)); }
    private void OnExtractLeft(object sender, RoutedEventArgs e) { if (Doc != null) _vm.AddGeneratedDocument(ChannelTools.ExtractChannel(Doc.Doc, 0)); }
    private void OnExtractRight(object sender, RoutedEventArgs e) { if (Doc is { Doc.ChannelCount: > 1 }) _vm.AddGeneratedDocument(ChannelTools.ExtractChannel(Doc.Doc, 1)); }

    private void OnMonoToStereo(object sender, RoutedEventArgs e)
    {
        if (Doc is { Doc.ChannelCount: 1 }) _vm.AddGeneratedDocument(ChannelTools.MonoToStereo(Doc.Doc));
        else InfoDialog.Show(this, "Mono → Stereo", "The active file is already multi-channel.");
    }

    private void OnChannelBalance(object sender, RoutedEventArgs e)
    {
        if (Doc is not { Doc.ChannelCount: > 1 }) return;
        var dlg = new ParamDialog("Channel Balance", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("Left gain", -24, 6, 0, v => $"{v:+0.0;-0.0;0.0} dB"),
            new ParamDialog.SliderSpec("Right gain", -24, 6, 0, v => $"{v:+0.0;-0.0;0.0} dB")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
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
        }
    }

    // analysis tools

    private async void OnTuner(object sender, RoutedEventArgs e)
    {
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

    private async void OnBpm(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (d == null || d.Doc.Length < d.Doc.SampleRate * 5) return;
        var chans = d.Doc.Channels.ToArray(); // stable refs captured on the UI thread
        int sampleRate = d.Doc.SampleRate;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (bpm, confidence) = await Task.Run(() => TempoDetect.Detect(chans, sampleRate));
            InfoDialog.Show(this, "Tempo Detection",
                bpm > 0 ? $"Confidence {confidence:P0}. Half/double-time ({bpm / 2:0.#} / {bpm * 2:0.#} BPM) may also fit."
                        : "No clear tempo found — the material may be too sparse or rubato.",
                bpm > 0 ? $"{bpm:0.#} BPM" : null);
        }
        finally { Mouse.OverrideCursor = null; }
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
