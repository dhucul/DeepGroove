using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.Views.Controls;

namespace WaveLab.Views;

public partial class SettingsDialog : Window
{
    private sealed record DeviceItem(string? Id, string Name) { public override string ToString() => Name; }
    private sealed record OptionItem(string Key, string Name) { public override string ToString() => Name; }

    private sealed record HardwareDiagnostics(string OutputDetails, string InputDetails, string Status);

    private sealed record SettingsSnapshot(
        bool ReopenLastSession,
        int UndoLimitMb,
        string? OutputDeviceId,
        string? InputDeviceId,
        int BufferMs,
        int CaptureBufferMs,
        string OutputShareMode,
        string InputShareMode,
        bool OutputEventSync,
        bool InputEventSync,
        string OutputDefaultRole,
        string InputDefaultRole,
        double RecordingTargetCeilingDb,
        bool AutosaveEnabled,
        int AutosaveMinutes,
        string ExportFormat,
        int ExportBitrateKbps);

    private bool _initialized;
    private bool _suspendAudioRefresh;
    private bool _closePending;
    private bool _allowClose;
    private bool _isClosed;
    private CancellationTokenSource? _testCancellation;
    private CancellationTokenSource? _diagnosticsCancellation;
    private Task? _activeTest;
    private int _diagnosticsGeneration;

    private static readonly TimeSpan DiagnosticsTimeout = TimeSpan.FromSeconds(8);

    public bool Saved { get; private set; }

    public SettingsDialog()
    {
        InitializeComponent();
        AppSettings settings = AppSettings.Instance;

        chkReopen.IsChecked = settings.ReopenLastSession;
        sldUndo.Value = Math.Clamp(settings.UndoLimitMb, 64, 4096);
        sldNoiseCeiling.Value = AppSettings.NormalizeNoiseDepthCeilingDb(settings.NoiseDepthCeilingDb);
        chkAutosave.IsChecked = settings.AutosaveEnabled;

        PopulateOption(cmbOutputRole, DefaultRoles, settings.OutputDefaultRole);
        PopulateOption(cmbInputRole, DefaultRoles, settings.InputDefaultRole);
        PopulateOption(cmbOutputShare, ShareModes, settings.OutputShareMode);
        PopulateOption(cmbInputShare, ShareModes, settings.InputShareMode);
        PopulateOption(cmbOutputScheduling, SchedulingModes, settings.OutputEventSync ? "event" : "poll");
        PopulateOption(cmbInputScheduling, SchedulingModes, settings.InputEventSync ? "event" : "poll");
        PopulateDevices(settings.OutputDeviceId, settings.InputDeviceId);

        sldBuffer.Value = Math.Clamp(settings.BufferMs, 3, 500);
        sldCaptureBuffer.Value = Math.Clamp(settings.CaptureBufferMs, 3, 500);

        foreach (int minutes in Intervals) cmbAutosaveInterval.Items.Add($"Every {minutes} min");
        int intervalIndex = Array.IndexOf(Intervals, settings.AutosaveMinutes);
        cmbAutosaveInterval.SelectedIndex = intervalIndex >= 0 ? intervalIndex : 2;

        RangeTargetCeiling(settings.RecordingTargetCeilingDb);

        foreach (OptionItem format in Formats()) cmbExportFormat.Items.Add(format);
        SelectOption(cmbExportFormat, settings.ExportFormat);

        foreach (int bitrate in Bitrates) cmbExportBitrate.Items.Add($"{bitrate} kbps");
        int bitrateIndex = Array.IndexOf(Bitrates, settings.ExportBitrateKbps);
        cmbExportBitrate.SelectedIndex = bitrateIndex >= 0 ? bitrateIndex : 2;

        _initialized = true;
        UpdateLabels();
        UpdateExportFormatUi();
        UpdateAudioUi(refreshDiagnostics: true);
    }

    /// <summary>
    /// Ranges the ceiling slider and redraws its landmarks. The landmarks depend on
    /// the range, which a stored ceiling below the slider's usual floor widens, so the
    /// two always move together.
    /// </summary>
    private void RangeTargetCeiling(double ceilingDb)
    {
        TargetCeilingScale.Range(sldTargetCeiling, ceilingDb);
        TargetCeilingScale.DrawLandmarks(
            ceilingLandmarks, sldTargetCeiling, (Brush)FindResource("Faint"), withLabels: true);
        // Range() leaves Value alone when it already matches, so paint the readout here
        // rather than relying on ValueChanged having fired.
        lblTargetCeiling.Text = TargetCeilingScale.Format(sldTargetCeiling.Value);
    }

    private void OnTargetCeilingSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblTargetCeiling == null) return;
        lblTargetCeiling.Text = TargetCeilingScale.Format(e.NewValue);
    }

    private static readonly int[] Intervals = [1, 2, 3, 5, 10, 15];
    private static readonly int[] Bitrates = [128, 160, 192, 256, 320];

    private static readonly OptionItem[] DefaultRoles =
    [
        new("multimedia", "Multimedia · music and media"),
        new("console", "Console · general interactive audio"),
        new("communications", "Communications · calls and headsets"),
    ];

    private static readonly OptionItem[] ShareModes =
    [
        new("shared", "Shared · Windows mixer (recommended)"),
        new("exclusive", "Exclusive · direct endpoint access"),
    ];

    private static readonly OptionItem[] SchedulingModes =
    [
        new("event", "Event-driven · efficient and precise"),
        new("poll", "Polling · compatibility fallback"),
    ];

    private static OptionItem[] Formats() =>
    [
        new("wav32", "Uncompressed WAV · 32-bit float"),
        new("wav24", "Uncompressed WAV · 24-bit PCM"),
        new("wav16", "Uncompressed WAV · 16-bit PCM (dithered)"),
        new("wav16nodither", "Uncompressed WAV · 16-bit PCM (no dither)"),
        new("aiff32", "Uncompressed AIFF · 32-bit PCM"),
        new("aiff24", "Uncompressed AIFF · 24-bit PCM"),
        new("aiff16", "Uncompressed AIFF · 16-bit PCM (dithered)"),
        new("aiff16nodither", "Uncompressed AIFF · 16-bit PCM (no dither)"),
        new("flac", "Lossless FLAC · 24-bit"),
        new("mp3", "Lossy MP3"),
        new("aac", "Lossy AAC (M4A)"),
        new("wma", "Lossy WMA"),
    ];

    private void PopulateDevices(string? outputId, string? inputId)
    {
        cmbOutput.Items.Clear();
        cmbOutput.Items.Add(new DeviceItem(null, "Follow Windows default"));
        cmbOutput.ToolTip = null;
        try
        {
            foreach (var (id, name) in PlaybackEngine.GetOutputDevices())
                cmbOutput.Items.Add(new DeviceItem(id, name));
        }
        catch (Exception ex)
        {
            cmbOutput.ToolTip = "Output devices could not be enumerated: " + ex.Message;
        }
        SelectDevice(cmbOutput, outputId);

        cmbInput.Items.Clear();
        cmbInput.Items.Add(new DeviceItem(null, "Follow Windows default"));
        cmbInput.ToolTip = null;
        try
        {
            foreach (var (id, name) in RecordingEngine.GetCaptureDevices())
                cmbInput.Items.Add(new DeviceItem(id, name));
        }
        catch (Exception ex)
        {
            cmbInput.ToolTip = "Input devices could not be enumerated: " + ex.Message;
        }
        SelectDevice(cmbInput, inputId);
    }

    private static void PopulateOption(ComboBox combo, IEnumerable<OptionItem> items, string selectedKey)
    {
        foreach (OptionItem item in items) combo.Items.Add(item);
        SelectOption(combo, selectedKey);
    }

    private static void SelectDevice(ComboBox combo, string? id)
    {
        combo.SelectedIndex = 0;
        foreach (DeviceItem item in combo.Items)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                break;
            }
        }
    }

    private static void SelectOption(ComboBox combo, string key)
    {
        combo.SelectedIndex = 0;
        foreach (OptionItem item in combo.Items)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                break;
            }
        }
    }

    private static string SelectedKey(ComboBox combo, string fallback) =>
        (combo.SelectedItem as OptionItem)?.Key ?? fallback;

    private void UpdateLabels()
    {
        if (lblUndo != null) lblUndo.Text = $"{(int)sldUndo.Value} MB";
        UpdateNoiseCeilingReadout();
        if (lblBuffer != null) lblBuffer.Text = $"{(int)sldBuffer.Value} ms";
        if (lblCaptureBuffer != null) lblCaptureBuffer.Text = $"{(int)sldCaptureBuffer.Value} ms";
    }

    private void OnUndoSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnNoiseCeilingSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateNoiseCeilingReadout();

    /// <summary>
    /// The value, and what moving it off the default gives up.
    /// </summary>
    /// <remarks>
    /// <b>Amber on the departure, not on the setting.</b> Ten is the measured optimum and anything
    /// above it is a deliberate trade, so the line states the trade rather than warning about the
    /// control - the same rule the noise depth readout follows, and the same reason: colouring a
    /// choice like a fault teaches users to distrust the colour.
    /// </remarks>
    private void UpdateNoiseCeilingReadout()
    {
        if (lblNoiseCeiling == null) return;
        double ceiling = AppSettings.NormalizeNoiseDepthCeilingDb(sldNoiseCeiling.Value);
        lblNoiseCeiling.Text = $"{ceiling:0} dB";
        if (lblNoiseCeilingWarning == null) return;

        if (ceiling <= Restoration.NoiseDepthCeilingDb)
        {
            lblNoiseCeilingWarning.Text = "";
            lblNoiseCeilingWarning.Visibility = Visibility.Collapsed;
            return;
        }

        // What a file that the default protects now gets. Worked from the rule itself so the two
        // cannot drift: a 12 dB request on programme sitting 15.9 dB above its floor, which is the
        // corpus severity where a fixed depth measured -8.13 dB segmental.
        double applied = Restoration.SuggestReductionDepthDb(15.9, 12.0, ceiling);
        lblNoiseCeilingWarning.Visibility = Visibility.Visible;
        lblNoiseCeilingWarning.Text =
            $"Above the measured optimum of {Restoration.NoiseDepthCeilingDb:0} dB. Measured over 108 cells, "
            + "scaling the depth takes files that come out worse than doing nothing from 46 to 15; raising "
            + "this gives that protection back. At this setting a file whose hiss is already 15.9 dB down "
            + $"would be reduced by {applied:0.0} dB where the default reduces it by none.";
    }
    private void OnBufferSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnExportFormatChanged(object sender, SelectionChangedEventArgs e) => UpdateExportFormatUi();

    private void UpdateExportFormatUi()
    {
        if (exportBitratePanel == null) return;
        string? key = (cmbExportFormat.SelectedItem as OptionItem)?.Key;
        exportBitratePanel.Visibility = key is "mp3" or "aac" or "wma"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (pageGeneral == null) return;
        pageGeneral.Visibility = navGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageAudio.Visibility = navAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageRecording.Visibility = navRecording.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageAutosave.Visibility = navAutosave.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageExport.Visibility = navExport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAudioConfigurationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) UpdateAudioUi(refreshDiagnostics: !_suspendAudioRefresh);
    }

    private void UpdateAudioUi(bool refreshDiagnostics)
    {
        if (!_initialized) return;
        cmbOutputRole.IsEnabled = (cmbOutput.SelectedItem as DeviceItem)?.Id == null;
        cmbInputRole.IsEnabled = (cmbInput.SelectedItem as DeviceItem)?.Id == null;

        bool outputExclusive = SelectedKey(cmbOutputShare, "shared") == "exclusive";
        bool inputExclusive = SelectedKey(cmbInputShare, "shared") == "exclusive";
        txtModeWarning.Text = outputExclusive || inputExclusive
            ? "Exclusive mode bypasses the Windows mixer and can block other applications. Playback also requires the document's sample rate and channel format to be accepted by the endpoint; use Test Output and the exclusive-format probe below."
            : "Shared mode follows the endpoint mix format, allows other applications to use the device, and is the safest choice. Event-driven scheduling normally gives the most stable low-latency behavior.";

        if (refreshDiagnostics) RefreshDiagnostics();
    }

    private async void RefreshDiagnostics()
    {
        string? outputId = (cmbOutput.SelectedItem as DeviceItem)?.Id;
        string? inputId = (cmbInput.SelectedItem as DeviceItem)?.Id;
        Role outputRole = AudioHardwareOptions.ParseRole(SelectedKey(cmbOutputRole, "multimedia"), Role.Multimedia);
        Role inputRole = AudioHardwareOptions.ParseRole(SelectedKey(cmbInputRole, "console"), Role.Console);

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous = _diagnosticsCancellation;
        _diagnosticsCancellation = cancellation;
        previous?.Cancel();
        int generation = ++_diagnosticsGeneration;
        txtHardwareStatus.Text = "Refreshing audio hardware diagnostics…";

        Task<HardwareDiagnostics> probe = Task.Run(
            () => InspectHardware(outputId, inputId, outputRole, inputRole, cancellation.Token),
            cancellation.Token);
        _ = probe.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            HardwareDiagnostics diagnostics = await probe.WaitAsync(DiagnosticsTimeout, cancellation.Token);
            if (_isClosed || generation != _diagnosticsGeneration) return;
            txtOutputDetails.Text = diagnostics.OutputDetails;
            txtInputDetails.Text = diagnostics.InputDetails;
            txtHardwareStatus.Text = diagnostics.Status;
        }
        catch (OperationCanceledException)
        {
            // A newer selection or dialog shutdown superseded this scan.
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            if (!_isClosed && generation == _diagnosticsGeneration)
                txtHardwareStatus.Text = "Audio hardware diagnostics timed out after 8 seconds. Try Refresh Hardware.";
        }
        catch (Exception ex)
        {
            if (!_isClosed && generation == _diagnosticsGeneration)
                txtHardwareStatus.Text = "Audio hardware diagnostics failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_diagnosticsCancellation, cancellation))
                _diagnosticsCancellation = null;
            cancellation.Dispose();
        }
    }

    private static HardwareDiagnostics InspectHardware(
        string? outputId,
        string? inputId,
        Role outputRole,
        Role inputRole,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AudioEndpointInfo output = AudioHardware.Inspect(outputId, DataFlow.Render, outputRole);
        cancellationToken.ThrowIfCancellationRequested();
        AudioEndpointInfo input = AudioHardware.Inspect(inputId, DataFlow.Capture, inputRole);
        cancellationToken.ThrowIfCancellationRequested();
        var outputs = AudioHardware.Inventory(DataFlow.Render);
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = AudioHardware.Inventory(DataFlow.Capture);
        cancellationToken.ThrowIfCancellationRequested();

        string status = $"{outputs.Active} active output{(outputs.Active == 1 ? "" : "s")} · "
            + $"{inputs.Active} active input{(inputs.Active == 1 ? "" : "s")} · "
            + $"{outputs.Disabled + inputs.Disabled} disabled · {outputs.Unplugged + inputs.Unplugged} unavailable";
        return new HardwareDiagnostics(output.Details, input.Details, status);
    }

    private void OnRefreshHardware(object sender, RoutedEventArgs e)
    {
        string? outputId = (cmbOutput.SelectedItem as DeviceItem)?.Id;
        string? inputId = (cmbInput.SelectedItem as DeviceItem)?.Id;
        _suspendAudioRefresh = true;
        try
        {
            PopulateDevices(outputId, inputId);
        }
        finally
        {
            _suspendAudioRefresh = false;
        }
        UpdateAudioUi(refreshDiagnostics: true);
    }

    private async void OnTestOutput(object sender, RoutedEventArgs e)
    {
        if (_testCancellation != null)
        {
            _testCancellation.Cancel();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        Task test = RunOutputTestAsync(cancellation);
        _activeTest = test;
        try
        {
            await test;
        }
        finally
        {
            if (ReferenceEquals(_activeTest, test)) _activeTest = null;
        }
    }

    private async Task RunOutputTestAsync(CancellationTokenSource cancellation)
    {
        btnTestOutput.Content = "■ Stop Test";
        btnTestInput.IsEnabled = false;
        btnRefreshHardware.IsEnabled = false;
        txtHardwareStatus.Text = "Testing the selected output with a quiet 440 Hz tone…";

        try
        {
            await AudioHardware.TestOutputAsync(
                (cmbOutput.SelectedItem as DeviceItem)?.Id,
                AudioHardwareOptions.ParseRole(SelectedKey(cmbOutputRole, "multimedia"), Role.Multimedia),
                AudioHardwareOptions.ParseShareMode(SelectedKey(cmbOutputShare, "shared")),
                SelectedKey(cmbOutputScheduling, "event") == "event",
                (int)sldBuffer.Value,
                cancellation.Token);
            txtHardwareStatus.Text = "Output test completed successfully.";
        }
        catch (OperationCanceledException)
        {
            txtHardwareStatus.Text = "Output test stopped.";
        }
        catch (Exception ex)
        {
            txtHardwareStatus.Text = "Output test failed. Review the selected mode, format probe, and buffer.";
            if (!_closePending)
            {
                MessageBox.Show("The selected output path could not complete its test:\n\n" + ex.Message,
                    "Audio Hardware Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_testCancellation, cancellation)) _testCancellation = null;
            cancellation.Dispose();
            btnTestOutput.Content = "▶ Test Output";
            btnTestInput.IsEnabled = true;
            btnRefreshHardware.IsEnabled = true;
        }
    }

    private async void OnTestInput(object sender, RoutedEventArgs e)
    {
        if (_testCancellation != null)
        {
            _testCancellation.Cancel();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        Task test = RunInputTestAsync(cancellation);
        _activeTest = test;
        try
        {
            await test;
        }
        finally
        {
            if (ReferenceEquals(_activeTest, test)) _activeTest = null;
        }
    }

    private async Task RunInputTestAsync(CancellationTokenSource cancellation)
    {
        btnTestInput.Content = "■ Stop Test";
        btnTestOutput.IsEnabled = false;
        btnRefreshHardware.IsEnabled = false;
        txtHardwareStatus.Text = "Listening to the selected input for 1.5 seconds…";

        try
        {
            AudioInputTestResult result = await AudioHardware.TestInputAsync(
                (cmbInput.SelectedItem as DeviceItem)?.Id,
                AudioHardwareOptions.ParseRole(SelectedKey(cmbInputRole, "console"), Role.Console),
                AudioHardwareOptions.ParseShareMode(SelectedKey(cmbInputShare, "shared")),
                SelectedKey(cmbInputScheduling, "event") == "event",
                (int)sldCaptureBuffer.Value,
                cancellation.Token);
            string signal = result.PeakDbFs <= -90 ? " · no meaningful signal detected" : "";
            txtHardwareStatus.Text = $"Input test complete · peak {result.PeakDbFs:0.0} dBFS · "
                + $"RMS {result.RmsDbFs:0.0} dBFS{signal}";
        }
        catch (OperationCanceledException)
        {
            txtHardwareStatus.Text = "Input test stopped.";
        }
        catch (Exception ex)
        {
            txtHardwareStatus.Text = "Input test failed. Review the selected mode, format, privacy access, and buffer.";
            if (!_closePending)
            {
                MessageBox.Show("The selected input path could not complete its test:\n\n" + ex.Message,
                    "Audio Hardware Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_testCancellation, cancellation)) _testCancellation = null;
            cancellation.Dispose();
            btnTestInput.Content = "● Test Input";
            btnTestOutput.IsEnabled = true;
            btnRefreshHardware.IsEnabled = true;
        }
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        _suspendAudioRefresh = true;
        try
        {
            chkReopen.IsChecked = true;
            sldUndo.Value = 512;
            sldNoiseCeiling.Value = Restoration.NoiseDepthCeilingDb;
            cmbOutput.SelectedIndex = 0;
            cmbInput.SelectedIndex = 0;
            SelectOption(cmbOutputRole, "multimedia");
            SelectOption(cmbInputRole, "console");
            SelectOption(cmbOutputShare, "shared");
            SelectOption(cmbInputShare, "shared");
            SelectOption(cmbOutputScheduling, "event");
            SelectOption(cmbInputScheduling, "event");
            sldBuffer.Value = 60;
            sldCaptureBuffer.Value = 100;
            RangeTargetCeiling(AppSettings.DefaultRecordingTargetCeilingDb);
            chkAutosave.IsChecked = true;
            cmbAutosaveInterval.SelectedIndex = 2;
            cmbExportFormat.SelectedIndex = 0;
            cmbExportBitrate.SelectedIndex = 2;
        }
        finally
        {
            _suspendAudioRefresh = false;
        }
        UpdateAudioUi(refreshDiagnostics: true);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        AppSettings settings = AppSettings.Instance;
        SettingsSnapshot previous = Capture(settings);

        settings.ReopenLastSession = chkReopen.IsChecked == true;
        settings.UndoLimitMb = (int)sldUndo.Value;
        settings.NoiseDepthCeilingDb = AppSettings.NormalizeNoiseDepthCeilingDb(sldNoiseCeiling.Value);
        settings.OutputDeviceId = (cmbOutput.SelectedItem as DeviceItem)?.Id;
        settings.InputDeviceId = (cmbInput.SelectedItem as DeviceItem)?.Id;
        settings.BufferMs = (int)sldBuffer.Value;
        settings.CaptureBufferMs = (int)sldCaptureBuffer.Value;
        settings.OutputShareMode = SelectedKey(cmbOutputShare, "shared");
        settings.InputShareMode = SelectedKey(cmbInputShare, "shared");
        settings.OutputEventSync = SelectedKey(cmbOutputScheduling, "event") == "event";
        settings.InputEventSync = SelectedKey(cmbInputScheduling, "event") == "event";
        settings.OutputDefaultRole = SelectedKey(cmbOutputRole, "multimedia");
        settings.InputDefaultRole = SelectedKey(cmbInputRole, "console");
        settings.RecordingTargetCeilingDb =
            AppSettings.NormalizeTargetCeilingDb(sldTargetCeiling.Value);
        settings.AutosaveEnabled = chkAutosave.IsChecked == true;
        settings.AutosaveMinutes = Intervals[Math.Max(0, cmbAutosaveInterval.SelectedIndex)];
        settings.ExportFormat = (cmbExportFormat.SelectedItem as OptionItem)?.Key ?? "wav32";
        settings.ExportBitrateKbps = Bitrates[Math.Max(0, cmbExportBitrate.SelectedIndex)];

        if (!settings.Save())
        {
            Restore(settings, previous);
            MessageBox.Show("Settings could not be saved:\n" + settings.LastSaveError, "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AudioDocument.UndoBudgetBytes = settings.UndoLimitBytes;
        Saved = true;
        await CloseSafelyAsync(dialogResult: true);
    }

    private static SettingsSnapshot Capture(AppSettings settings) => new(
        settings.ReopenLastSession,
        settings.UndoLimitMb,
        settings.OutputDeviceId,
        settings.InputDeviceId,
        settings.BufferMs,
        settings.CaptureBufferMs,
        settings.OutputShareMode,
        settings.InputShareMode,
        settings.OutputEventSync,
        settings.InputEventSync,
        settings.OutputDefaultRole,
        settings.InputDefaultRole,
        settings.RecordingTargetCeilingDb,
        settings.AutosaveEnabled,
        settings.AutosaveMinutes,
        settings.ExportFormat,
        settings.ExportBitrateKbps);

    private static void Restore(AppSettings settings, SettingsSnapshot snapshot)
    {
        settings.ReopenLastSession = snapshot.ReopenLastSession;
        settings.UndoLimitMb = snapshot.UndoLimitMb;
        settings.OutputDeviceId = snapshot.OutputDeviceId;
        settings.InputDeviceId = snapshot.InputDeviceId;
        settings.BufferMs = snapshot.BufferMs;
        settings.CaptureBufferMs = snapshot.CaptureBufferMs;
        settings.OutputShareMode = snapshot.OutputShareMode;
        settings.InputShareMode = snapshot.InputShareMode;
        settings.OutputEventSync = snapshot.OutputEventSync;
        settings.InputEventSync = snapshot.InputEventSync;
        settings.OutputDefaultRole = snapshot.OutputDefaultRole;
        settings.InputDefaultRole = snapshot.InputDefaultRole;
        settings.RecordingTargetCeilingDb = snapshot.RecordingTargetCeilingDb;
        settings.AutosaveEnabled = snapshot.AutosaveEnabled;
        settings.AutosaveMinutes = snapshot.AutosaveMinutes;
        settings.ExportFormat = snapshot.ExportFormat;
        settings.ExportBitrateKbps = snapshot.ExportBitrateKbps;
    }

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        await CloseSafelyAsync(dialogResult: false);
    }

    private async Task CloseSafelyAsync(bool dialogResult)
    {
        if (_closePending) return;
        _closePending = true;
        IsEnabled = false;
        _diagnosticsCancellation?.Cancel();
        _testCancellation?.Cancel();

        Task? activeTest = _activeTest;
        if (activeTest != null)
        {
            txtHardwareStatus.Text = "Stopping the audio hardware test…";
            try { await activeTest; }
            catch { /* The test reports its own failures and always tears down its endpoint. */ }
        }

        if (_isClosed) return;
        _allowClose = true;
        DialogResult = dialogResult;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _diagnosticsCancellation?.Cancel();
        if (_allowClose || _activeTest == null) return;

        e.Cancel = true;
        await CloseSafelyAsync(dialogResult: false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _diagnosticsCancellation?.Cancel();
        _testCancellation?.Cancel();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
