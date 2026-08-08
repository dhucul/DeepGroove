using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WaveLab.Views;

/// <summary>
/// Generic parameter dialog: an optional combo plus any number of value sliders.
/// Used by the restoration, silence, stretch/pitch and channel tools.
/// </summary>
public partial class ParamDialog : Window
{
    public sealed record SliderSpec(string Label, double Min, double Max, double Default, Func<double, string> Format,
        double Tick = 0);

    private readonly List<(SliderSpec Spec, Slider Slider, TextBlock Value)> _sliders = [];
    private ComboBox? _combo;

    public int ComboIndex => _combo?.SelectedIndex ?? -1;
    public double[] Values => _sliders.Select(s => s.Slider.Value).ToArray();

    public ParamDialog(string title, string okLabel, string? comboLabel, string[]? comboItems, int comboDefault,
        params SliderSpec[] sliders)
    {
        InitializeComponent();
        titleText.Text = title;
        okBtn.Content = okLabel;

        if (comboLabel != null && comboItems is { Length: > 0 })
        {
            body.Children.Add(Label(comboLabel));
            _combo = new ComboBox { Margin = new Thickness(0, 6, 0, 14) };
            foreach (var item in comboItems) _combo.Items.Add(item);
            _combo.SelectedIndex = Math.Clamp(comboDefault, 0, comboItems.Length - 1);
            body.Children.Add(_combo);
        }

        foreach (var spec in sliders)
        {
            body.Children.Add(Label(spec.Label));
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            var slider = new Slider
            {
                Minimum = spec.Min,
                Maximum = spec.Max,
                Value = spec.Default,
                Margin = new Thickness(0, 0, 10, 0),
            };
            if (spec.Tick > 0)
            {
                slider.TickFrequency = spec.Tick;
                slider.IsSnapToTickEnabled = true;
            }
            var value = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Text = spec.Format(spec.Default),
            };
            Grid.SetColumn(value, 1);
            slider.ValueChanged += (_, _) => value.Text = spec.Format(slider.Value);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            body.Children.Add(grid);
            _sliders.Add((spec, slider, value));
        }
    }

    private TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Foreground = (Brush)FindResource("Faint"),
    };

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
