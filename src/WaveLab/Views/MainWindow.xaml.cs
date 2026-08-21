using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Vst3;
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

        // Both fire while the effect is still whole. A plugin's editor is a native window holding
        // the plugin's controller, so it has to be shut before the rack lets go of the plugin —
        // afterwards there is nothing safe left to close it with.
        _vm.Master.EffectRemoving += ClosePluginEditorFor;
        _vm.Master.ChainReplacing += CloseAllPluginEditors;
        _vm.EditorViewChanged += ApplyEditorViewMode;
        ApplyEditorViewMode();

        // The montage panel covers the editor rather than replacing it, so switching tabs is a
        // visibility change here rather than a re-layout there.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ActiveTab) or nameof(MainViewModel.ActiveMontage))
                ApplyMontageVisibility();
        };
        ApplyMontageVisibility();

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

    /// <summary>
    /// Lays the editor rows out for the current mode. Heights are set here rather than bound because
    /// the split is resizable: once the user has dragged the splitter, the star weights are theirs,
    /// and a binding would overwrite them on the next mode change.
    /// </summary>
    private void ApplyEditorViewMode()
    {
        switch (_vm.EditorView)
        {
            case EditorViewMode.Waveform:
                waveformRow.Height = new GridLength(1, GridUnitType.Star);
                splitterRow.Height = new GridLength(0);
                spectralRow.Height = new GridLength(0);
                editorSplitter.Visibility = Visibility.Collapsed;
                break;

            case EditorViewMode.Split:
                waveformRow.Height = new GridLength(1, GridUnitType.Star);
                splitterRow.Height = GridLength.Auto;
                spectralRow.Height = new GridLength(2, GridUnitType.Star);
                editorSplitter.Visibility = Visibility.Visible;
                break;

            default:
                waveformRow.Height = new GridLength(0);
                splitterRow.Height = new GridLength(0);
                spectralRow.Height = new GridLength(1, GridUnitType.Star);
                editorSplitter.Visibility = Visibility.Collapsed;
                break;
        }

        // Nothing is analysed while the spectrogram is hidden: the control only works when it is
        // asked to render, so the waveform-only mode costs exactly what it did before.
        bool spectral = _vm.EditorView != EditorViewMode.Waveform;
        spectralEditor.Visibility = spectral ? Visibility.Visible : Visibility.Collapsed;
        frequencyRuler.Visibility = spectral ? Visibility.Visible : Visibility.Collapsed;
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
                $"{blocking.Title} is stopping. It will finish at its next safe point; close Deep Groove again once it has.",
                "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_longOperationRunning || !IsEnabled)
        {
            MessageBox.Show(
                "An audio operation is still running. Wait for it to finish, then close Deep Groove again.",
                "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_vm.IsTransportRecording)
        {
            if (MessageBox.Show(
                    "Recording is still in progress. Stop and keep the capture now? Deep Groove will stay open so you can review and save it.",
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
            MessageBox.Show("There are unsaved changes. Exit anyway?", "Deep Groove",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _closing = true;
        IsEnabled = false;
        SaveWindowPlacement();

        // Plugin editors are native windows owned by this one. They go before the shutdown work
        // starts, so a plugin is not drawing into a window whose owner is winding down.
        CloseAllPluginEditors();
        try
        {
            // Bounded. Startup work is best-effort by the time the user is closing, and
            // _closing has already latched — so a session restore stuck on a disconnected
            // network path made the window impossible to close by any means.
            try { await _startupTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
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
            // A remembered position is honoured only if it still lands on a screen. Restoring one
            // that does not opens the window where nobody can see it, which reads as the app
            // failing to start; falling through leaves the XAML's CenterScreen.
            if (s.WindowLeft is { } left && s.WindowTop is { } top
                && WindowPlacement.IsReachable(
                    new Rect(left, top, Width, Height), WindowPlacement.VirtualScreen))
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
            // Checked on the way out as well as on the way in, because this is where an
            // unreachable position gets in. An offscreen render probe parks the window far outside
            // the desktop and then closes it, and that value then outlives the probe, the app and
            // a reinstall. Refusing to store one keeps the last good position instead.
            if (WindowPlacement.IsReachable(
                    new Rect(Left, Top, Width, Height), WindowPlacement.VirtualScreen))
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
            }
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

    /// <summary>
    /// The tags, the broadcast extension, and what the file will actually contain. Editing marks the
    /// document dirty; the chunks are written by the next Save through the ordinary codec path.
    /// </summary>
    private void OnFileInformation(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d == null) return;

        if (new FileInfoDialog(d) { Owner = this }.ShowDialog() == true)
            _vm.ReportAction($"File information updated for {d.Doc.Title}. Save to write it.");
    }

    private void OnFileInformationCommand(object sender, ExecutedRoutedEventArgs e)
    {
        OnFileInformation(sender, e);
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

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildPluginMenu());

        var manage = new MenuItem { Header = "Manage VST3 Plugins…" };
        manage.Click += (_, _) => ShowVst3Manager();
        menu.Items.Add(manage);

        menu.PlacementTarget = addFxBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The installed plugins, as a submenu. Built each time the menu opens rather than cached,
    /// because a scan can happen between two openings of it.
    /// </summary>
    private MenuItem BuildPluginMenu()
    {
        var pluginMenu = new MenuItem { Header = "VST3 Plugin" };
        var usable = Vst3PluginHost.Instance.UsablePlugins;

        if (usable.Count == 0)
        {
            // Two different empties, and the difference matters: nothing scanned yet is a thing to
            // go and do, while nothing usable found is a thing to go and look at.
            bool anythingScanned = Vst3PluginHost.Instance.Catalogue.Results.Count > 0;
            pluginMenu.Items.Add(new MenuItem
            {
                Header = anythingScanned
                    ? "No usable plugins — see Manage VST3 Plugins…"
                    : "No plugins scanned yet — see Manage VST3 Plugins…",
                IsEnabled = false,
            });
            return pluginMenu;
        }

        foreach (Vst3ScanResult plugin in usable)
        {
            string path = plugin.Path;
            var item = new MenuItem
            {
                Header = plugin.Name,
                InputGestureText = plugin.Vendor,
                ToolTip = plugin.Parameters == 0
                    ? "Publishes no parameters — driven from its own editor"
                    : $"{plugin.Parameters} parameters",
            };
            item.Click += (_, _) => _vm.Master.AddEffectCommand.Execute(Vst3Effect.TypeIdPrefix + path);
            pluginMenu.Items.Add(item);
        }
        return pluginMenu;
    }

    /// <summary>
    /// Chooses the impulse response a convolution reverb runs. Any file the app can open will do —
    /// a response is only an audio file that happens to be a room.
    /// </summary>
    private void OnLoadImpulseResponse(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EffectViewModel vm }) return;
        if (vm.Effect is not ConvolutionReverbEffect reverb) return;

        var settings = AppSettings.Instance;
        var dialog = new OpenFileDialog
        {
            Title = "Choose an impulse response",
            Filter = "Audio files|*.wav;*.wave;*.bwf;*.rf64;*.w64;*.aiff;*.aif;*.aifc;*.flac;*.mp3"
                     + "|All files|*.*",
            CheckFileExists = true,
            // Started where the last one came from rather than where the last audio file did:
            // impulse responses live in a library, and the library is not the music folder.
            InitialDirectory = Directory.Exists(settings.LastImpulseFolder ?? "")
                ? settings.LastImpulseFolder
                : Path.GetDirectoryName(reverb.ResponsePath ?? "") is { Length: > 0 } beside
                  && Directory.Exists(beside)
                    ? beside
                    : "",
        };
        if (dialog.ShowDialog(this) != true) return;

        if (reverb.LoadResponse(dialog.FileName, out string error))
        {
            settings.LastImpulseFolder = Path.GetDirectoryName(dialog.FileName);
            settings.Save();

            vm.RefreshResponse();
            _vm.Master.ReportStatus(
                $"{vm.ResponseTitle} loaded · {vm.ResponseDetail} · source unchanged until render.");
        }
        else
        {
            // The effect keeps whatever it had. Saying so matters: a file picker that closes and
            // changes nothing otherwise looks like it worked.
            vm.RefreshResponse();
            MessageBox.Show(this,
                $"That file could not be used as an impulse response.\n\n{error}",
                "Impulse response", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnManageVst3(object sender, RoutedEventArgs e) => ShowVst3Manager();

    private void ShowVst3Manager()
    {
        var dialog = new Vst3ManagerDialog { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Opens a plugin's own editor. One window per plugin, non-modal, and it does not stop playback —
    /// turning a knob while listening is the whole point of having one.
    /// </summary>
    private void OnOpenPluginEditor(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EffectViewModel vm }) return;
        if (vm.Plugin is not { } effect) return;

        if (_pluginEditors.TryGetValue(effect, out Vst3EditorWindow? open))
        {
            open.Activate();
            return;
        }

        try
        {
            var window = new Vst3EditorWindow(effect.Plugin) { Owner = this };
            _pluginEditors[effect] = window;
            window.Closed += (_, _) => _pluginEditors.Remove(effect);
            window.Show();
        }
        catch (Exception ex)
        {
            _pluginEditors.Remove(effect);
            MessageBox.Show(this, $"The plugin's editor would not open.\n\n{ex.Message}",
                effect.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Open plugin editors, so a second click raises the one that is up rather than making another,
    /// and so they can all be closed with the window that owns them.
    /// </summary>
    private readonly Dictionary<Vst3Effect, Vst3EditorWindow> _pluginEditors = [];

    private void ClosePluginEditorFor(EffectViewModel vm)
    {
        if (vm.Plugin is not { } effect) return;
        if (!_pluginEditors.TryGetValue(effect, out Vst3EditorWindow? window)) return;

        try { window.Close(); } catch { }
        _pluginEditors.Remove(effect);
    }

    private void CloseAllPluginEditors()
    {
        foreach (Vst3EditorWindow window in _pluginEditors.Values.ToArray())
        {
            try { window.Close(); } catch { }
        }
        _pluginEditors.Clear();
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

    // ── loudness compliance ──────────────────────────────────────

    /// <summary>
    /// Measures the document against a delivery target and shows the report, with the option to
    /// save it beside the master as a delivery note.
    /// </summary>
    private async void OnLoudnessCompliance(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d is not { Doc.Length: > 0 }) return;

        var dialog = new ParamDialog("Loudness compliance", "Measure",
            "Target", [.. LoudnessTarget.All.Select(t => $"{t.Name} — {t.IntegratedLufs:0.0} LUFS, " +
                                                        $"≤ {t.TruePeakDbtp:0.0} dBTP")], 0)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        LoudnessTarget target = LoudnessTarget.All[
            Math.Clamp(dialog.ComboIndex, 0, LoudnessTarget.All.Count - 1)];
        float[][] channels = d.Doc.Channels.ToArray();
        int rate = d.Doc.SampleRate;
        var report = default(LoudnessReport);

        _longOperationRunning = true;
        try
        {
            await _vm.Progress.RunBlockingAsync("Measuring loudness", target.Name,
                async (progress, token) =>
                {
                    report = await Task.Run(
                        () => LoudnessCompliance.Measure(channels, rate, target, token, progress), token);
                });
        }
        catch (OperationCanceledException) { _vm.ReportAction("Measurement cancelled."); return; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Loudness compliance", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { _longOperationRunning = false; }

        string text = LoudnessCompliance.Format(report);
        _vm.ReportAction($"{target.Name}: {(report.Passed ? "compliant" : "not compliant")} · " +
                         $"{report.IntegratedLufs:0.0} LUFS, {report.TruePeakDbtp:0.0} dBTP.");

        var info = new InfoDialog($"Loudness compliance — {(report.Passed ? "PASS" : "FAIL")}", text)
        {
            Owner = this,
        };
        info.ShowDialog();
    }

    // ── wow and flutter ──────────────────────────────────────────

    /// <summary>
    /// Measures the transfer's speed variation, reports it, then straightens the time base.
    /// </summary>
    private async void OnCorrectWowFlutter(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d is not { Doc.Length: > 0 }) return;

        float[][] channels = d.Doc.Channels.ToArray();
        int rate = d.Doc.SampleRate;
        var report = WowFlutterReport.None;

        _longOperationRunning = true;
        try
        {
            await _vm.Progress.RunBlockingAsync("Measuring wow and flutter",
                "Following the spectrum along a log-frequency axis", async (progress, token) =>
                {
                    report = await Task.Run(
                        () => WowFlutter.Analyze(channels[0], rate, WowFlutterOptions.Default,
                            token, progress), token);
                });
        }
        catch (OperationCanceledException) { _vm.ReportAction("Measurement cancelled."); return; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Measuring wow and flutter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { _longOperationRunning = false; }

        if (!report.Found)
        {
            MessageBox.Show("There was not enough sustained material above 1 kHz to follow the " +
                            "speed of the transfer.", "Correct wow and flutter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string verdict = report.RmsPercent switch
        {
            < 0.05 => "That is at the floor of what this can measure; there is probably nothing to correct.",
            < 0.15 => "That is low — audible only on sustained piano or strings, if at all.",
            < 0.4 => "That is enough to hear on sustained notes.",
            _ => "That is severe.",
        };
        string reach = $"Only variation faster than {WowFlutterOptions.Default.BaselineSeconds:0} seconds " +
                       "is corrected: a record running consistently fast is at the wrong pitch, which " +
                       "is a different repair.";

        if (MessageBox.Show(
                $"Speed variation measures {report.RmsPercent:0.000}% rms, peaking at " +
                $"{report.PeakPercent:0.000}%.\n\n{verdict}\n\n{reach}\n\nStraighten the time base?",
                "Correct wow and flutter", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            _vm.ReportAction($"Wow and flutter measured at {report.RmsPercent:0.000}% rms · left uncorrected.");
            return;
        }

        _ = RunWholeFileTool("Correct Wow & Flutter",
            $"{report.RmsPercent:0.000}% rms · one time base for every channel",
            (working, sampleRate, progress, token) =>
            {
                WowFlutter.Correct(working, sampleRate, WowFlutterOptions.Default, token, progress);
                return working;
            }, d);
    }

    // ── drifting hum ─────────────────────────────────────────────

    /// <summary>
    /// Measures the hum's fundamental, reports it, then follows and subtracts it.
    /// </summary>
    /// <remarks>
    /// Separate from <c>Remove Hum…</c>, which is the real-time notch bank. This one follows a
    /// fundamental that moves and subtracts an estimate of each partial rather than notching, so it
    /// leaves the music at those frequencies alone — but it is an offline pass over the whole file,
    /// not something that can run on the audio thread.
    /// </remarks>
    private async void OnTrackHum(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d is not { Doc.Length: > 0 }) return;

        float[][] channels = d.Doc.Channels.ToArray();
        int rate = d.Doc.SampleRate;
        var report = HumReport.None;

        _longOperationRunning = true;
        try
        {
            await _vm.Progress.RunBlockingAsync("Measuring hum", "Looking for a mains fundamental",
                async (progress, token) =>
                {
                    report = await Task.Run(
                        () => HumTracker.Measure(channels[0], rate, HumTrackOptions.Default, token), token);
                });
        }
        catch (OperationCanceledException) { _vm.ReportAction("Hum measurement cancelled."); return; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Measuring hum", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { _longOperationRunning = false; }

        if (!report.Found)
        {
            MessageBox.Show("No mains hum was found: there is no steady comb of partials between " +
                            "45 and 65 Hz to follow.", "Track and remove drifting hum",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string drift = report.DriftHz < 0.02
            ? "It is steady, so a fixed notch would have suited it too."
            : $"It wanders by {report.DriftHz:0.00} Hz, which a fixed notch cannot follow.";
        if (MessageBox.Show(
                $"A hum was found at {report.MeanHz:0.00} Hz, {Math.Abs(report.LevelDb):0} dB below the " +
                $"programme.\n\n{drift}\n\nFollow it and subtract it?",
                "Track and remove drifting hum", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            _vm.ReportAction($"Hum measured at {report.MeanHz:0.00} Hz · left in place.");
            return;
        }

        _ = RunWholeFileTool("Remove Drifting Hum",
            $"{report.MeanHz:0.00} Hz · following {report.DriftHz:0.00} Hz of drift",
            (working, sampleRate, progress, token) =>
            {
                for (int c = 0; c < working.Length; c++)
                {
                    // Measured per channel: a hum is picked up differently by each, and one
                    // channel's trajectory is not the other's.
                    HumTracker.Remove(working[c], sampleRate, HumTrackOptions.Default, token,
                        SubProgress.Slice(progress, c, working.Length));
                }
                return working;
            }, d);
    }

    // ── surface crackle ──────────────────────────────────────────

    private void OnRemoveCrackle(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d is not { Doc.Length: > 0 }) return;

        var dialog = new ParamDialog("Remove surface crackle", "Remove", null, null, 0,
            // Expressed in robust deviations of the prediction residual, which is what the detector
            // actually thresholds. Lower finds more and risks the music; higher only takes the
            // obvious.
            new ParamDialog.SliderSpec("Sensitivity", 2.0, 8.0, 3.5,
                value => $"{value:0.0}σ", 0.1),
            new ParamDialog.SliderSpec("Longest defect", 4, 32, 12,
                value => $"{Math.Round(value):0} samples", 1))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        var options = DecrackleOptions.Default with
        {
            Threshold = dialog.Values[0],
            MaximumRunLength = (int)Math.Round(dialog.Values[1]),
        };

        var report = DecrackleReport.None;
        _ = RunRangeTool("Remove Crackle", $"{options.Threshold:0.0}σ residual threshold",
            (data, sampleRate, progress, token) =>
            {
                int events = 0, fallbacks = 0;
                long replaced = 0;
                for (int c = 0; c < data.Length; c++)
                {
                    DecrackleReport channel = Decrackle.Process(data[c], options, token,
                        SubProgress.Slice(progress, c, data.Length));
                    events += channel.Events;
                    fallbacks += channel.Fallbacks;
                    replaced += channel.SamplesReplaced;
                }
                report = new DecrackleReport(events, replaced, fallbacks);
                return data;
            }, d).ContinueWith(task =>
            {
                if (task.Result)
                {
                    _vm.ReportAction($"Crackle removed · {report.Events} defects, " +
                                     $"{report.SamplesReplaced} samples replaced.");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── disc equalisation ────────────────────────────────────────

    /// <summary>
    /// Applies a disc equalisation curve, either way round. The whole file, not the selection: a
    /// curve is a property of how the disc was cut, so applying it to part of one makes no sense.
    /// </summary>
    private void OnRecordingCurve(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d == null || d.Doc.Length == 0) return;

        var names = RecordingCurves.All
            .Select(spec => spec.TrebleHz > 0
                ? $"{spec.Name} — {spec.TurnoverHz:0} Hz, {RecordingCurves.ResponseDb(spec, 10_000):0.0} dB at 10 kHz"
                : $"{spec.Name} — {spec.TurnoverHz:0} Hz turnover, no rolloff")
            .ToArray();

        var dialog = new ParamDialog("Disc equalisation curve", "Apply",
        [
            new ParamDialog.ComboSpec("Curve", names),
            new ParamDialog.ComboSpec("Direction",
                ["Playback — undo the curve (de-emphasis)", "Record — impose the curve (pre-emphasis)"]),
            new ParamDialog.ComboSpec("Phase",
                ["Minimum — as an analog preamp", "Linear — no added dispersion"]),
        ])
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        int[] choices = dialog.ComboIndices;
        RecordingCurveSpec spec = RecordingCurves.All[Math.Clamp(choices[0], 0, RecordingCurves.All.Count - 1)];
        var direction = choices[1] == 1 ? CurveDirection.Record : CurveDirection.Playback;
        var phase = choices[2] == 1 ? CurvePhase.Linear : CurvePhase.Minimum;

        string verb = direction == CurveDirection.Playback ? "De-emphasis" : "Pre-emphasis";
        _ = RunWholeFileTool($"{verb} · {spec.Name}",
            $"{phase} phase · {RecordingCurves.DefaultTaps} taps",
            (channels, sampleRate, progress, token) =>
            {
                RecordingCurves.Apply(channels, spec, sampleRate, direction, phase,
                    RecordingCurves.DefaultTaps, token, progress);
                return channels;
            }, d);
    }

    /// <summary>
    /// Measures the timing difference between the channels and offers to correct it.
    /// </summary>
    /// <remarks>
    /// Measure first, then ask. The correction is meaningless without the number, and the number is
    /// often the answer on its own — a transfer that measures a fifth of a sample does not need
    /// correcting, and saying so is more useful than applying something inaudible.
    /// </remarks>
    private async void OnCorrectAzimuth(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        if (_longOperationRunning || d is not { Doc.Length: > 0 } || d.Doc.Channels.Count < 2) return;

        float[][] channels = d.Doc.Channels.ToArray();
        int rate = d.Doc.SampleRate;
        AzimuthEstimate estimate = AzimuthEstimate.None;

        _longOperationRunning = true;
        try
        {
            await _vm.Progress.RunBlockingAsync("Measuring azimuth",
                "GCC-PHAT across the side", async (progress, token) =>
            {
                estimate = await Task.Run(
                    () => Azimuth.Estimate(channels[0], channels[1], rate,
                        AzimuthOptions.Default, token, progress), token);
            });
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction("Azimuth measurement cancelled.");
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Measuring azimuth", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            _longOperationRunning = false;
        }

        if (estimate.Windows == 0)
        {
            MessageBox.Show("There was not enough correlated material to measure the channels against each other.",
                "Correct stylus azimuth", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string lead =
            $"The right channel is {Math.Abs(estimate.Microseconds(rate)):0.0} µs " +
            $"{(estimate.DelaySamples >= 0 ? "behind" : "ahead of")} the left " +
            $"({Math.Abs(estimate.DelaySamples):0.000} samples), measured over {estimate.Windows} windows.";
        string trust = estimate.Confidence switch
        {
            > 0.8 => "The windows agreed closely, so this is a solid reading.",
            > 0.4 => "The windows agreed only roughly — treat this as approximate.",
            _ => "The windows disagreed badly. The channels may not be correlated enough to measure; " +
                 "correcting on this reading is not advisable.",
        };
        string worth = Math.Abs(estimate.Microseconds(rate)) < 5
            ? "\n\nThat is small enough to be inaudible; there is probably nothing to correct."
            : "";

        if (MessageBox.Show($"{lead}\n\n{trust}{worth}\n\nCorrect it?", "Correct stylus azimuth",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _vm.ReportAction($"Azimuth measured at {estimate.Microseconds(rate):0.0} µs · left uncorrected.");
            return;
        }

        double delay = estimate.DelaySamples;
        _ = RunWholeFileTool("Correct Azimuth",
            $"{delay:0.000} samples · split between the channels",
            (working, _, _, _) =>
            {
                Azimuth.Align(working, delay);
                return working;
            }, d);
    }

    /// <summary>
    /// A transform over the whole file rather than the selection, committed as one undoable edit.
    /// </summary>
    private async Task<bool> RunWholeFileTool(string undoName, string? detail,
        Func<float[][], int, IProgress<double>, CancellationToken, float[][]?> transform,
        DocumentViewModel target)
    {
        if (_longOperationRunning || target.Doc.Length == 0) return false;

        float[][] channels = target.Doc.Channels.ToArray();
        int rate = target.Doc.SampleRate;
        int length = target.Doc.Length;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        bool applied = false;
        try
        {
            await _vm.Progress.RunBlockingAsync(undoName, detail, async (progress, token) =>
            {
                float[][]? output = await Task.Run(() =>
                {
                    // Copied first: the transform works in place and the document must not change
                    // under the UI until the splice commits.
                    var working = new float[channels.Length][];
                    for (int c = 0; c < channels.Length; c++) working[c] = (float[])channels[c].Clone();
                    return transform(working, rate, progress, token);
                }, token);

                if (output == null || length != target.Doc.Length) return;
                _vm.PrepareForDocumentEdit(target);
                target.Doc.ReplaceRange(0, length, output, undoName);
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

    // ── spectral repair ──────────────────────────────────────────

    private void OnSpectralHeal(object sender, RoutedEventArgs e) =>
        _ = RunSpectralRepair("Heal", "Rebuilding the selection from the partials running through it",
            (channel, mask, options, progress, token) =>
                SpectralRepair.Heal(channel, 0, mask, options, token, progress));

    private void OnSpectralAttenuate(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSpectralSelection) return;

        var dialog = new ParamDialog("Attenuate selection", "Attenuate",
            "How far down", ["Down to the surrounding level", "By a fixed amount"], 0,
            // "Reduction" rather than "Limit": the label is fixed when the dialog is built, and it
            // has to read correctly under either mode the combo above it selects.
            new ParamDialog.SliderSpec("Reduction", 3, 90, 24, value => $"−{value:0} dB", 1))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        double reduction = dialog.Values[0];
        bool toSurroundings = dialog.ComboIndex == 0;

        // The limit means different things in the two modes, which is the point of having both: a
        // fixed reduction is the amount, while matching the surroundings takes each bin down to what
        // it carried either side and uses the limit only as a stop.
        _ = RunSpectralRepair("Attenuate",
            toSurroundings
                ? $"Down to the surrounding level · no more than {reduction:0} dB"
                : $"{reduction:0} dB across the selected region",
            (channel, mask, options, _, token) => toSurroundings
                ? SpectralRepair.AttenuateToSurroundings(channel, 0, mask, reduction, options, token)
                : SpectralRepair.Attenuate(channel, 0, mask, -reduction, options, token));
    }

    private void OnSpectralGain(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSpectralSelection) return;

        var dialog = new ParamDialog("Gain selection", "Apply", null, null, 0,
            new ParamDialog.SliderSpec("Gain", -24, 24, -6,
                value => $"{value:+0.#;−0.#;0} dB", 0.5))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        double gain = dialog.Values[0];
        if (Math.Abs(gain) < 0.01) return;

        _ = RunSpectralRepair("Gain", $"{gain:+0.#;−0.#;0} dB across the selected region",
            (channel, mask, options, _, token) =>
                SpectralRepair.Attenuate(channel, 0, mask, gain, options, token));
    }

    /// <summary>
    /// Learns the selection's spectral signature and removes it from elsewhere in the file.
    /// </summary>
    /// <remarks>
    /// Two steps behind one button, because the second is useless without the first and the first
    /// has no effect on its own. The selection should hold the offending sound alone — the signature
    /// is whatever is in it, so anything else in there is learned too and removed along with it.
    /// </remarks>
    private void OnSpectralLearnPattern(object sender, RoutedEventArgs e)
    {
        var d = Doc;
        SpectralSelection selection = _vm.SpectralSelection;
        if (_longOperationRunning || d == null || d.Doc.Length == 0 || selection.IsEmpty) return;

        var dialog = new ParamDialog("Learn pattern from selection", "Remove",
            "Remove it from", ["The whole file", "The selection only"], 0,
            new ParamDialog.SliderSpec("Reduction", 3, 40, 18, value => $"−{value:0} dB", 1),
            new ParamDialog.SliderSpec("Sensitivity", 0.5, 3.0, 1.0, value => $"{value:0.0}×", 0.1))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;

        var options = new SpectralPatternOptions(selection.FftSize, selection.Hop,
            dialog.Values[0], dialog.Values[1],
            SpectralPatternOptions.Default.Smoothing,
            SpectralPatternOptions.Default.AbsenceProbability);

        bool wholeFile = dialog.ComboIndex == 0;
        SpectralRegion bounds = selection.Bounds;
        int start = wholeFile ? 0 : Math.Clamp(bounds.StartSample, 0, d.Doc.Length);
        int count = wholeFile
            ? d.Doc.Length
            : Math.Clamp(bounds.EndSample - start, 0, d.Doc.Length - start);
        if (count <= 0) return;

        _ = RunPatternRemoval(d, selection, options, start, count,
            wholeFile ? "the whole file" : "the selected span");
    }

    private async Task RunPatternRemoval(DocumentViewModel d, SpectralSelection selection,
        SpectralPatternOptions options, int start, int count, string where)
    {
        float[][] channels = d.Doc.Channels.ToArray();
        int rate = d.Doc.SampleRate;
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _vm.Progress.RunBlockingAsync("Remove Pattern",
                $"Learned from the selection · removing from {where}", async (progress, token) =>
            {
                var learned = 0;
                var band = (Low: 0.0, High: 0.0);

                var output = await Task.Run(() =>
                {
                    var result = new float[channels.Length][];
                    for (int c = 0; c < channels.Length; c++)
                    {
                        // Learned per channel: a buzz is rarely identical on both, and a signature
                        // averaged across them would fit neither.
                        SpectralPattern pattern = SpectralPattern.Learn(
                            channels[c], 0, selection.Mask, rate, options, token);
                        if (c == 0) { learned = pattern.LearnedBins; band = pattern.Band; }

                        float[] cleaned = pattern.Remove(channels[c], start, count, options, token,
                            SubProgress.Slice(progress, c, channels.Length));
                        result[c] = cleaned.Length == count
                            ? cleaned
                            : channels[c].AsSpan(start, count).ToArray();
                    }
                    return result;
                }, token);

                if (learned == 0)
                {
                    _vm.ReportAction("Nothing was learned from that selection · document unchanged.");
                    return;
                }
                if (start + count > d.Doc.Length) return;

                _vm.PrepareForDocumentEdit(d);
                d.Doc.ReplaceRange(start, count, output, "Remove Pattern");
                _vm.ReportAction(
                    $"Pattern removed · learned {learned} bins over {band.Low:0}–{band.High:0} Hz.");
            });
        }
        catch (OperationCanceledException)
        {
            _vm.ReportAction("Remove Pattern cancelled · document unchanged.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Remove Pattern", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _longOperationRunning = false;
        }
    }

    /// <summary>
    /// Spectral edits do not go through <see cref="RunRangeTool"/>: that splices the *time* selection,
    /// and a spectral repair decides its own span. A mask covering a few frames still needs a window
    /// of context either side to be resynthesised cleanly, so the repair reports back where its
    /// result belongs and that is what gets spliced.
    /// </summary>
    private async Task RunSpectralRepair(string undoName, string detail,
        Func<float[], SpectralMask, SpectralRepairOptions, IProgress<double>, CancellationToken,
            SpectralRepairResult> repair)
    {
        if (_longOperationRunning) return;
        var d = Doc;
        SpectralSelection selection = _vm.SpectralSelection;
        if (d == null || d.Doc.Length == 0 || selection.IsEmpty) return;

        // The mask carries the grid it was built in, so a lasso or a wand is repaired through exactly
        // what the user drew rather than through a rectangle reconstructed from its bounds.
        var options = new SpectralRepairOptions(selection.FftSize, selection.Hop,
            SpectralRepairOptions.Default.PartialDriftRadians);
        SpectralMask mask = selection.Mask;
        if (mask.IsEmpty || selection.SampleRate != d.Doc.SampleRate) return;

        float[][] channels = d.Doc.Channels.ToArray();
        _longOperationRunning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _vm.Progress.RunBlockingAsync(undoName, detail, async (progress, token) =>
            {
                var results = await Task.Run(() =>
                {
                    var repaired = new SpectralRepairResult[channels.Length];
                    for (int c = 0; c < channels.Length; c++)
                    {
                        repaired[c] = repair(channels[c], mask, options,
                            SubProgress.Slice(progress, c, channels.Length), token);
                    }
                    return repaired;
                }, token);

                if (results.Length == 0 || results[0].IsEmpty) return;
                int start = results[0].Start, count = results[0].Samples.Length;
                if (start + count > d.Doc.Length) return;

                _vm.PrepareForDocumentEdit(d);
                d.Doc.ReplaceRange(start, count, Array.ConvertAll(results, r => r.Samples), undoName);
                _vm.ReportAction($"{undoName} applied over {count} samples.");
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
        int sampleRate = d.Doc.SampleRate;          // captured up front, like the profile above
        _ = RunRangeTool("Reduce Noise", (data, _) =>
        {
            // The depth follows how much noise there is to remove rather than the slider alone.
            // Measured, a fixed depth comes out worse than leaving the audio alone on 46 of 108
            // corpus cells; this takes that to 15. See Restoration.SuggestReductionDepthDb.
            double depth = Restoration.SuggestReductionDepthDb(data, sampleRate, reduction);
            if (depth > 0) Restoration.ReduceNoise(data, profile, depth, sensitivity);
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
