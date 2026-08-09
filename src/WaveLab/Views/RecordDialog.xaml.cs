using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class RecordDialog : Window
{
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
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (ViewModel.IsRecording || ViewModel.HasPendingCapture) ViewModel.Cancel();
            ViewModel.Dispose();
            _lifetimeCts.Dispose();
        };
    }

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsFinalizing) return;
        if (ViewModel.HasPendingCapture)
        {
            await FinalizeAndCloseAsync();
            return;
        }

        if (!ViewModel.IsRecording)
        {
            double bpm = double.TryParse(txtBpm.Text, out var b) ? Math.Clamp(b, 30, 300) : 120;
            bool countIn = chkCountIn.IsChecked == true;
            bool metronome = chkMetronome.IsChecked == true;
            CancellationToken lifetimeToken = _lifetimeCts.Token;

            _starting = true;
            startBtn.IsEnabled = false;
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

                if (!ViewModel.Start())
                {
                    _click.Stop();
                    return;
                }
                startText.Text = "Stop & Insert";
                SetSetupControlsEnabled(false);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                try { _click.Stop(); } catch { }
                if (!IsVisible) return;
                countInText.Visibility = Visibility.Collapsed;
                startText.Text = "Start Recording";
                SetSetupControlsEnabled(true);
                MessageBox.Show($"Could not start the metronome or recording:\n{ex.Message}", "Record",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _starting = false;
                startBtn.IsEnabled = !ViewModel.IsFinalizing;
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
        startText.Text = retryable ? "Retry Finalization" : "Start Recording";
        startBtn.IsEnabled = true;
        cancelBtn.IsEnabled = true;
        if (!retryable) SetSetupControlsEnabled(true);
    }

    private void SetSetupControlsEnabled(bool enabled)
    {
        deviceCombo.IsEnabled = enabled;
        chkCountIn.IsEnabled = enabled;
        chkMetronome.IsEnabled = enabled;
        txtBpm.IsEnabled = enabled;
        cmbBars.IsEnabled = enabled;
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
