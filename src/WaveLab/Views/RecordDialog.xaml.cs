using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views.Controls;

namespace WaveLab.Views;

public partial class RecordDialog : Window
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private readonly DispatcherTimer _timer;
    private readonly ClickTrack _click = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _starting;

    public RecordViewModel ViewModel { get; } = new();

    /// <summary>True when the user asked to punch the recording into the current selection.</summary>
    public bool PunchRequested => ViewModel.PunchInsertEnabled;

    public RecordDialog(bool punchAvailable = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ConstrainToOwnerMonitor();
        DataContext = ViewModel;

        cmbBars.Items.Add("1 bar (4/4)");
        cmbBars.Items.Add("2 bars (4/4)");
        cmbBars.SelectedIndex = 0;

        resetCeilingBtn.Content =
            $"Reset to {TargetCeilingScale.Format(AppSettings.DefaultRecordingTargetCeilingDb)}";

        LoadAutoStopSettings();

        if (punchAvailable)
        {
            chkPunch.Visibility = Visibility.Visible;
            ViewModel.PunchInsertEnabled = true;
            chkPunch.IsChecked = true;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => ViewModel.Tick();
        _timer.Start();
        ViewModel.RecordingBitDepthSaveFailed += OnRecordingBitDepthSaveFailed;
        ViewModel.UnexpectedStopCompleted += OnUnexpectedStopCompleted;
        ViewModel.MonitoringStopped += OnMonitoringStopped;
        ViewModel.NeedleDropMonitoringStopped += OnNeedleDropMonitoringStopped;
        ViewModel.NeedleDropStarted += OnNeedleDropStarted;
        ViewModel.AutoStopped += OnAutoStopped;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += (_, e) =>
        {
            if (ViewModel.IsFinalizing)
            {
                e.Cancel = true;
                return;
            }

            // Belt and braces: a popup torn down with its window does not reliably
            // raise Closed, and a ceiling nudged but never committed would be lost.
            ViewModel.CommitTargetCeiling();

            if (ViewModel.HasPendingCapture)
            {
                var choice = MessageBox.Show(
                    "The captured audio is still buffered because finalization did not complete. Discard it?",
                    "Discard recording?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                ViewModel.Cancel();
            }
        };
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            _timer.Stop();
            _click.Dispose();
            ViewModel.RecordingBitDepthSaveFailed -= OnRecordingBitDepthSaveFailed;
            ViewModel.UnexpectedStopCompleted -= OnUnexpectedStopCompleted;
            ViewModel.MonitoringStopped -= OnMonitoringStopped;
            ViewModel.NeedleDropMonitoringStopped -= OnNeedleDropMonitoringStopped;
            ViewModel.NeedleDropStarted -= OnNeedleDropStarted;
            ViewModel.AutoStopped -= OnAutoStopped;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (ViewModel.IsRecording || ViewModel.IsLevelChecking
                || ViewModel.IsWaitingForNeedleDrop || ViewModel.HasPendingCapture)
                ViewModel.Cancel();
            ViewModel.Dispose();
            _lifetimeCts.Dispose();
        };
    }

    private void ConstrainToOwnerMonitor()
    {
        nint windowHandle = new WindowInteropHelper(this).Handle;
        nint ownerHandle = Owner == null ? 0 : new WindowInteropHelper(Owner).Handle;
        nint targetHandle = ownerHandle != 0 ? ownerHandle : windowHandle;
        nint monitor = MonitorFromWindow(targetHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        double availableWidth;
        double availableHeight;
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(Owner ?? this);
            availableWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX - 24;
            availableHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY - 24;
        }
        else
        {
            availableWidth = SystemParameters.WorkArea.Width - 24;
            availableHeight = SystemParameters.WorkArea.Height - 24;
        }

        MaxWidth = Math.Max(1, availableWidth);
        MaxHeight = Math.Max(1, availableHeight);
        Width = Math.Min(720, MaxWidth);
    }

    private void OnLevelCheck(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop
            || ViewModel.IsFinalizing) return;
        bool wasChecking = ViewModel.IsLevelChecking;
        bool started = wasChecking ? ViewModel.StopLevelCheck() : ViewModel.StartLevelCheck();
        if (started)
        {
            SetSetupControlsEnabled(true);
        }
        else if (wasChecking && ViewModel.IsLevelChecking)
        {
            // The stop gate was held by a finalizing capture. Say so rather than
            // letting the button appear to do nothing.
            MessageBox.Show(
                "The level check is still finishing. Try again in a moment.", "Record",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnApplyRecommendation(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop
            || ViewModel.IsFinalizing) return;
        if (ViewModel.ApplyRecommendedInputSetting()) SetSetupControlsEnabled(true);
    }

    private void OnUseRememberedSetting(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop
            || ViewModel.IsFinalizing) return;
        if (ViewModel.UseRememberedSetting()) SetSetupControlsEnabled(true);
    }

    private void OnForgetDeviceMemory(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop
            || ViewModel.IsFinalizing) return;
        // Only a genuine save failure is worth a dialog: having nothing to forget is
        // not an error, and the button is disabled in that state anyway.
        if (ViewModel.ForgetDeviceMemory() == RecordViewModel.ForgetMemoryOutcome.SaveFailed)
        {
            MessageBox.Show(
                "The remembered setting could not be cleared:\n"
                + (AppSettings.Instance.LastSaveError ?? "unknown error"), "Record",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Guards the slider against the view model while the popup is being set up:
    /// ranging the slider moves its value, and that must not read back as the user
    /// choosing a ceiling.
    /// </summary>
    private bool _rangingCeiling;

    /// <summary>Frozen: the landmarks are rebuilt every time the popup opens.</summary>
    private static readonly SolidColorBrush CeilingLandmarkBrush = new(Color.FromRgb(0x6E, 0x8B, 0x86));

    static RecordDialog() => CeilingLandmarkBrush.Freeze();

    private void OnOpenTargetCeiling(object sender, RoutedEventArgs e) => ceilingPopup.IsOpen = true;

    // Ranging lives on the popup rather than on the chip's click so that the slider is
    // never shown holding WPF's default 0–10 range, whatever opened it.
    private void OnTargetCeilingOpened(object? sender, EventArgs e)
    {
        _rangingCeiling = true;
        try
        {
            TargetCeilingScale.Range(sldCardCeiling, ViewModel.TargetCeilingDb);
            TargetCeilingScale.DrawLandmarks(
                cardCeilingLandmarks, sldCardCeiling, CeilingLandmarkBrush, withLabels: true);
            lblCardCeiling.Text = TargetCeilingScale.Format(sldCardCeiling.Value);
        }
        finally { _rangingCeiling = false; }

        sldCardCeiling.Focus();
    }

    private void OnCardCeilingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblCardCeiling == null) return;
        lblCardCeiling.Text = TargetCeilingScale.Format(e.NewValue);
        if (_rangingCeiling) return;
        // Cheap during the gesture; the analyzer and settings file are retargeted once
        // it ends. See RecordViewModel.CommitTargetCeiling.
        ViewModel.TargetCeilingDb = e.NewValue;
    }

    /// <summary>
    /// End of a drag. Keyboard and Reset changes commit when the popup closes instead,
    /// which is the same gesture boundary for a control that is not being dragged.
    /// </summary>
    private void OnCeilingDragCompleted(object sender, DragCompletedEventArgs e) =>
        ViewModel.CommitTargetCeiling();

    private void OnResetTargetCeiling(object sender, RoutedEventArgs e) =>
        sldCardCeiling.Value = AppSettings.DefaultRecordingTargetCeilingDb;

    private void OnCloseTargetCeiling(object sender, RoutedEventArgs e) => ceilingPopup.IsOpen = false;

    // The chip is what the popup came from, so that is where the keyboard belongs
    // once it closes — otherwise focus is left inside a popup that no longer exists.
    private void OnTargetCeilingClosed(object? sender, EventArgs e)
    {
        ViewModel.CommitTargetCeiling();
        ceilingChipBtn.Focus();
    }

    private void OnResetLevelCheck(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop
            || ViewModel.IsFinalizing) return;
        if (ViewModel.ResetLevelCheck()) SetSetupControlsEnabled(true);
    }

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsFinalizing) return;
        if (ViewModel.IsWaitingForNeedleDrop)
        {
            ViewModel.Cancel();
            countInText.Visibility = Visibility.Collapsed;
            startText.Text = chkNeedleDrop.IsChecked == true
                ? "Arm for Needle Drop"
                : "Start Recording";
            SetSetupControlsEnabled(true);
            return;
        }
        if (ViewModel.HasPendingCapture)
        {
            await FinalizeAndCloseAsync();
            return;
        }

        if (!ViewModel.IsRecording)
        {
            CommitAutoStopEdits();
            bool transitionFromLevelCheck = ViewModel.IsLevelChecking;
            bool needleDropStart = chkNeedleDrop.IsChecked == true;
            double bpm = double.TryParse(txtBpm.Text, out var b) ? Math.Clamp(b, 30, 300) : 120;
            bool countIn = !needleDropStart && chkCountIn.IsChecked == true;
            bool metronome = !needleDropStart && chkMetronome.IsChecked == true;
            CancellationToken lifetimeToken = _lifetimeCts.Token;

            _starting = true;
            startBtn.IsEnabled = false;
            SetSetupControlsEnabled(false);
            try
            {
                if (countIn || metronome)
                    _click.Start(bpm, 4);

                if (countIn)
                {
                    countInText.Visibility = Visibility.Visible;
                    int bars = cmbBars.SelectedIndex + 1;
                    await Task.Delay(ClickTrack.CountInMs(bpm, 4, bars), lifetimeToken);
                    if (!IsVisible) return; // cancelled during count-in
                    countInText.Visibility = Visibility.Collapsed;
                }

                if (!metronome) _click.Stop();

                // Do not silently open a fresh recording stream if the monitored
                // device disappeared while a count-in was running.
                if (transitionFromLevelCheck && !ViewModel.IsLevelChecking)
                {
                    _click.Stop();
                    return;
                }

                bool started = needleDropStart
                    ? ViewModel.StartOnNeedleDrop()
                    : ViewModel.Start();
                if (!started)
                {
                    _click.Stop();
                    SetSetupControlsEnabled(true);
                    return;
                }
                if (needleDropStart)
                {
                    countInText.Text = "LOWER THE NEEDLE…";
                    countInText.Visibility = Visibility.Visible;
                    startText.Text = "Cancel Auto-Start";
                }
                else
                {
                    startText.Text = "Stop & Insert";
                }
                SetSetupControlsEnabled(false);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                try { _click.Stop(); } catch { }
                if (!IsVisible) return;
                countInText.Visibility = Visibility.Collapsed;
                startText.Text = chkNeedleDrop.IsChecked == true
                    ? "Arm for Needle Drop"
                    : "Start Recording";
                SetSetupControlsEnabled(true);
                MessageBox.Show($"Could not start the metronome or recording:\n{ex.Message}", "Record",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _starting = false;
                startBtn.IsEnabled = !ViewModel.IsFinalizing;
                if (!ViewModel.IsRecording && !ViewModel.IsWaitingForNeedleDrop
                    && !ViewModel.IsFinalizing)
                    SetSetupControlsEnabled(true);
            }
        }
        else
        {
            await FinalizeAndCloseAsync();
        }
    }

    private async Task FinalizeAndCloseAsync()
    {
        _click.Stop();
        startBtn.IsEnabled = false;
        startText.Text = "Finalizing…";
        try
        {
            await ViewModel.StopAndFinishAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not finalize the recording:\n{ex.Message}", "Record",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PrepareAfterFinalizationFailure();
            return;
        }

        if (!IsVisible) return;
        DialogResult = true;
        Close();
    }

    private void OnUnexpectedStopCompleted(RecordingStoppedInfo info, Exception? failure)
    {
        if (!IsVisible) return;
        // The take is over either way — OnAutoStopped and FinalizeAndCloseAsync both stop the
        // click here. Without this the metronome kept playing over the error dialog when
        // finalization failed and the window stayed open.
        try { _click.Stop(); } catch { }

        if (failure != null)
        {
            MessageBox.Show($"Could not preserve the interrupted recording:\n{failure.Message}", "Record",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PrepareAfterFinalizationFailure();
            return;
        }

        string reason = info.CapacityReached
            ? "The recording reached Deep Groove's in-memory safety limit. The captured audio was kept."
            : info.Error != null
                ? $"The input device stopped unexpectedly. Audio captured before the failure was kept.\n\n{info.Error.Message}"
                : "The input device stopped. The captured audio was kept.";
        MessageBox.Show(reason, "Recording stopped", MessageBoxButton.OK,
            info.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (ViewModel.Result != null)
        {
            DialogResult = true;
            Close();
        }
        else
        {
            PrepareAfterFinalizationFailure();
        }
    }

    private void OnMonitoringStopped(RecordingStoppedInfo info)
    {
        if (!IsVisible) return;
        string reason = info.Error != null
            ? $"The input device stopped during the level check.\n\n{info.Error.Message}"
            : "The input device stopped during the level check.";
        MessageBox.Show(reason, "Level check stopped", MessageBoxButton.OK,
            info.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        SetSetupControlsEnabled(true);
    }

    private void OnNeedleDropMonitoringStopped(RecordingStoppedInfo info)
    {
        if (!IsVisible) return;
        countInText.Visibility = Visibility.Collapsed;
        startText.Text = chkNeedleDrop.IsChecked == true
            ? "Arm for Needle Drop"
            : "Start Recording";
        string reason = info.Error != null
            ? $"The input device stopped while waiting for the needle.\n\n{info.Error.Message}"
            : "The input device stopped while waiting for the needle.";
        MessageBox.Show(reason, "Needle-drop recording stopped", MessageBoxButton.OK,
            info.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        SetSetupControlsEnabled(true);
    }

    private void OnNeedleDropStarted()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)OnNeedleDropStarted);
            return;
        }
        if (!IsVisible) return;
        countInText.Visibility = Visibility.Collapsed;
        startText.Text = "Stop & Insert";
    }

    private void LoadAutoStopSettings()
    {
        AppSettings settings = AppSettings.Instance;
        ViewModel.AutoStopOnRunOut = settings.RecordAutoStopOnRunOut;
        ViewModel.RunOutHoldSeconds = settings.RecordRunOutHoldSeconds;
        ViewModel.AutoStopOnDuration = settings.RecordAutoStopOnDuration;
        ViewModel.AutoStopMinutes = settings.RecordAutoStopMinutes;
        ShowAutoStopValues();
        // SetSetupControlsEnabled only runs on later state changes, so give the
        // dependent rows their opening enablement here.
        runOutHoldRow.IsEnabled = ViewModel.AutoStopOnRunOut;
        txtAutoStopMinutes.IsEnabled = ViewModel.AutoStopOnDuration;
    }

    /// <summary>Writes the view model's clamped values back into the two text boxes.</summary>
    private void ShowAutoStopValues()
    {
        txtRunOutHold.Text = ViewModel.RunOutHoldSeconds.ToString("0.#");
        txtAutoStopMinutes.Text = ViewModel.AutoStopMinutes.ToString();
    }

    /// <summary>Best effort — a settings write failure must not block a take.</summary>
    private void SaveAutoStopSettings()
    {
        try
        {
            AppSettings settings = AppSettings.Instance;
            settings.RecordAutoStopOnRunOut = ViewModel.AutoStopOnRunOut;
            settings.RecordRunOutHoldSeconds = ViewModel.RunOutHoldSeconds;
            settings.RecordAutoStopOnDuration = ViewModel.AutoStopOnDuration;
            settings.RecordAutoStopMinutes = ViewModel.AutoStopMinutes;
            settings.Save();
        }
        catch { }
    }

    private void OnAutoStopOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop
            || ViewModel.IsFinalizing) return;
        SetSetupControlsEnabled(true);
        SaveAutoStopSettings();
    }

    private void OnRunOutHoldChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (double.TryParse(txtRunOutHold.Text, out double seconds))
            ViewModel.RunOutHoldSeconds = seconds;
        ShowAutoStopValues();
        SaveAutoStopSettings();
    }

    private void OnAutoStopMinutesChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (int.TryParse(txtAutoStopMinutes.Text, out int minutes))
            ViewModel.AutoStopMinutes = minutes;
        ShowAutoStopValues();
        SaveAutoStopSettings();
    }

    /// <summary>
    /// Pull whatever is typed in the auto-stop boxes into the view model even if
    /// they never lost focus, so Start always uses what the user can see.
    /// </summary>
    private void CommitAutoStopEdits()
    {
        if (double.TryParse(txtRunOutHold.Text, out double seconds))
            ViewModel.RunOutHoldSeconds = seconds;
        if (int.TryParse(txtAutoStopMinutes.Text, out int minutes))
            ViewModel.AutoStopMinutes = minutes;
        ShowAutoStopValues();
        SaveAutoStopSettings();
    }

    /// <summary>
    /// The take ended itself. This is an ordinary, expected finish, so it closes
    /// with the recording exactly as pressing Stop would.
    /// </summary>
    private void OnAutoStopped(AutoStopInfo info, Exception? failure)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnAutoStopped(info, failure));
            return;
        }
        if (!IsVisible) return;
        try { _click.Stop(); } catch { }

        if (failure != null)
        {
            MessageBox.Show($"Could not finalize the automatically stopped recording:\n{failure.Message}",
                "Record", MessageBoxButton.OK, MessageBoxImage.Warning);
            PrepareAfterFinalizationFailure();
            return;
        }
        if (ViewModel.Result == null)
        {
            MessageBox.Show(
                info.Reason == AutoStopReason.RunOut
                    ? "Recording stopped at the run-out groove, but no audio was captured."
                    : "Recording stopped at the maximum take length, but no audio was captured.",
                "Record", MessageBoxButton.OK, MessageBoxImage.Information);
            PrepareAfterFinalizationFailure();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnNeedleDropOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ViewModel.IsRecording || ViewModel.IsWaitingForNeedleDrop) return;
        bool enabled = chkNeedleDrop.IsChecked == true;
        if (enabled)
        {
            chkCountIn.IsChecked = false;
            chkMetronome.IsChecked = false;
        }
        startText.Text = enabled ? "Arm for Needle Drop" : "Start Recording";
        SetSetupControlsEnabled(true);
    }

    private void OnPunchOptionChanged(object sender, RoutedEventArgs e)
    {
        if (bitDepthCombo == null) return;
        bool punching = chkPunch.IsChecked == true;
        ViewModel.PunchInsertEnabled = punching;
        bitDepthCombo.IsEnabled = !punching
            && !_starting
            && !ViewModel.IsRecording
            && !ViewModel.IsWaitingForNeedleDrop
            && !ViewModel.IsFinalizing;
        bitDepthCombo.ToolTip = punching
            ? "Punch recording keeps the open document's existing bit depth"
            : "Bit depth used when the completed take is saved";
    }

    private void OnRecordingBitDepthSaveFailed(string error)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnRecordingBitDepthSaveFailed(error));
            return;
        }

        MessageBox.Show(
            "The recording bit-depth preference could not be saved:\n" + error,
            "Record", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordViewModel.IsLevelChecking)
            || e.PropertyName == nameof(RecordViewModel.HasStoppedLevelCheck))
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnViewModelPropertyChanged(sender, e));
                return;
            }
            SetSetupControlsEnabled(!ViewModel.IsRecording && !ViewModel.IsFinalizing);
            return;
        }
        if (e.PropertyName != nameof(RecordViewModel.IsFinalizing)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

        bool finalizing = ViewModel.IsFinalizing;
        startBtn.IsEnabled = !finalizing && !_starting;
        cancelBtn.IsEnabled = !finalizing;
        if (finalizing) startText.Text = "Finalizing…";
    }

    private void PrepareAfterFinalizationFailure()
    {
        bool retryable = ViewModel.HasPendingCapture;
        startText.Text = retryable
            ? "Retry Finalization"
            : chkNeedleDrop.IsChecked == true
                ? "Arm for Needle Drop"
                : "Start Recording";
        startBtn.IsEnabled = true;
        cancelBtn.IsEnabled = true;
        if (!retryable) SetSetupControlsEnabled(true);
    }

    private void SetSetupControlsEnabled(bool enabled)
    {
        deviceCombo.IsEnabled = enabled && !ViewModel.IsLevelChecking;
        bitDepthCombo.IsEnabled = enabled && !ViewModel.PunchInsertEnabled;
        levelCheckBtn.IsEnabled = enabled && !ViewModel.IsRecording;
        resetLevelCheckBtn.IsEnabled = enabled
            && (ViewModel.IsLevelChecking || ViewModel.HasStoppedLevelCheck);
        // Apply and the two memory buttons sit outside inputGainControls, and their
        // enablement changes mid-scan rather than only on a state transition, so
        // they stay bound to view-model predicates instead of being set here.
        // Assigning IsEnabled locally would clobber those bindings.
        inputGainControls.IsEnabled = enabled;
        wholeRecordCheck.IsEnabled = enabled
            && !ViewModel.IsLevelChecking
            && !ViewModel.HasStoppedLevelCheck;
        chkNeedleDrop.IsEnabled = enabled;
        chkRunOutStop.IsEnabled = enabled;
        chkDurationStop.IsEnabled = enabled;
        runOutHoldRow.IsEnabled = enabled && chkRunOutStop.IsChecked == true;
        txtAutoStopMinutes.IsEnabled = enabled && chkDurationStop.IsChecked == true;
        bool metronomeEnabled = enabled && chkNeedleDrop.IsChecked != true;
        chkCountIn.IsEnabled = metronomeEnabled;
        chkMetronome.IsEnabled = metronomeEnabled;
        txtBpm.IsEnabled = metronomeEnabled;
        cmbBars.IsEnabled = metronomeEnabled;
        chkPunch.IsEnabled = enabled;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsFinalizing) return;
        _click.Stop();
        ViewModel.Cancel();
        DialogResult = false;
        Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
