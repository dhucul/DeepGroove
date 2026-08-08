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
        Closed += (_, _) => { _timer.Stop(); _click.Dispose(); };
    }

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_starting) return;
        if (!ViewModel.IsRecording)
        {
            double bpm = double.TryParse(txtBpm.Text, out var b) ? Math.Clamp(b, 30, 300) : 120;
            bool countIn = chkCountIn.IsChecked == true;
            bool metronome = chkMetronome.IsChecked == true;

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
                    await Task.Delay(ClickTrack.CountInMs(bpm, 4, bars));
                    if (!IsVisible) { _click.Stop(); return; } // cancelled during count-in
                    countInText.Visibility = Visibility.Collapsed;
                }

                if (!metronome) _click.Stop();

                if (!ViewModel.Start())
                {
                    _click.Stop();
                    return;
                }
                startText.Text = "Stop & Insert";
                deviceCombo.IsEnabled = false;
                chkCountIn.IsEnabled = false;
                chkMetronome.IsEnabled = false;
            }
            finally
            {
                _starting = false;
                startBtn.IsEnabled = true;
            }
        }
        else
        {
            _click.Stop();
            ViewModel.StopAndFinish();
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
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
