using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.ViewModels;

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
    public bool PunchRequested => chkPunch.IsChecked == true;

    public RecordDialog(bool punchAvailable = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ConstrainToOwnerMonitor();
        DataContext = ViewModel;

        cmbBars.Items.Add("1 bar (4/4)");
        cmbBars.Items.Add("2 bars (4/4)");
        cmbBars.SelectedIndex = 0;

        if (punchAvailable)
        {
            chkPunch.Visibility = Visibility.Visible;
            chkPunch.IsChecked = true;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => ViewModel.Tick();
        _timer.Start();
        ViewModel.UnexpectedStopCompleted += OnUnexpectedStopCompleted;
        ViewModel.MonitoringStopped += OnMonitoringStopped;
        ViewModel.NeedleDropMonitoringStopped += OnNeedleDropMonitoringStopped;
        ViewModel.NeedleDropStarted += OnNeedleDropStarted;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += (_, e) =>
        {
            if (ViewModel.IsFinalizing)
            {
                e.Cancel = true;
                return;
            }

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
            ViewModel.UnexpectedStopCompleted -= OnUnexpectedStopCompleted;
            ViewModel.MonitoringStopped -= OnMonitoringStopped;
            ViewModel.NeedleDropMonitoringStopped -= OnNeedleDropMonitoringStopped;
            ViewModel.NeedleDropStarted -= OnNeedleDropStarted;
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
        bool started = ViewModel.IsLevelChecking
            ? ViewModel.StopLevelCheck()
            : ViewModel.StartLevelCheck();
        if (started) SetSetupControlsEnabled(true);
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
        if (failure != null)
        {
            MessageBox.Show($"Could not preserve the interrupted recording:\n{failure.Message}", "Record",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PrepareAfterFinalizationFailure();
            return;
        }

        string reason = info.CapacityReached
            ? "The recording reached WaveLab's in-memory safety limit. The captured audio was kept."
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
        levelCheckBtn.IsEnabled = enabled && !ViewModel.IsRecording;
        resetLevelCheckBtn.IsEnabled = enabled
            && (ViewModel.IsLevelChecking || ViewModel.HasStoppedLevelCheck);
        inputGainControls.IsEnabled = enabled;
        chkNeedleDrop.IsEnabled = enabled;
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
