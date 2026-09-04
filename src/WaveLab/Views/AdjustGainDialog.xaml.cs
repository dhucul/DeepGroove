using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WaveLab.Views;

public partial class AdjustGainDialog : Window
{
    public double GainDb { get; private set; }

    public AdjustGainDialog(string rangeDescription)
    {
        InitializeComponent();
        rangeText.Text = rangeDescription;
        UpdateInput();
        Loaded += (_, _) => { gainText.Focus(); gainText.SelectAll(); };
    }

    internal static bool TryParseGain(string text, out double gainDb)
    {
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out gainDb)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out gainDb);
        if (!parsed || !double.IsFinite(gainDb) || gainDb < -60 || gainDb > 60) return false;
        double rounded = Math.Round(gainDb, 1, MidpointRounding.AwayFromZero);
        if (Math.Abs(gainDb - rounded) > 1e-9) return false;
        gainDb = rounded;
        return true;
    }

    private void OnGainChanged(object sender, TextChangedEventArgs e)
    {
        if (applyBtn != null) UpdateInput();
    }

    private void UpdateInput()
    {
        bool valid = TryParseGain(gainText.Text, out double gainDb);
        applyBtn.IsEnabled = valid && gainDb != 0;
        clippingText.Visibility = valid && gainDb > 0 ? Visibility.Visible : Visibility.Collapsed;
        validationText.Text = !valid ? "Enter a number from −60 to +60 in 0.1 dB steps."
            : gainDb == 0 ? "0 dB makes no change."
            : $"Apply {gainDb:+0.0;-0.0} dB equally to every channel. One undoable edit.";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!TryParseGain(gainText.Text, out double gainDb) || gainDb == 0) return;
        GainDb = gainDb;
        DialogResult = true;
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
