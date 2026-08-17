using System.Windows;
using System.Windows.Automation;
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

    /// <summary>One labelled choice. A dialog may carry several.</summary>
    public sealed record ComboSpec(string Label, string[] Items, int Default = 0);

    private readonly List<(SliderSpec Spec, Slider Slider, TextBlock Value)> _sliders = [];
    private readonly List<ComboBox> _combos = [];

    /// <summary>The first choice's index, for the many callers that only have one.</summary>
    public int ComboIndex => _combos.Count > 0 ? _combos[0].SelectedIndex : -1;

    /// <summary>Every choice's index, in the order the specs were given.</summary>
    public int[] ComboIndices => _combos.Select(c => c.SelectedIndex).ToArray();

    public double[] Values => _sliders.Select(s => s.Slider.Value).ToArray();

    public ParamDialog(string title, string okLabel, string? comboLabel, string[]? comboItems, int comboDefault,
        params SliderSpec[] sliders)
        : this(title, okLabel,
            comboLabel != null && comboItems is { Length: > 0 }
                ? [new ComboSpec(comboLabel, comboItems, comboDefault)]
                : [],
            sliders)
    {
    }

    /// <summary>Any number of choices followed by any number of sliders.</summary>
    public ParamDialog(string title, string okLabel, ComboSpec[] combos, params SliderSpec[] sliders)
    {
        ArgumentNullException.ThrowIfNull(combos);
        InitializeComponent();
        titleText.Text = title;
        okBtn.Content = okLabel;

        foreach (ComboSpec spec in combos)
        {
            if (spec.Items.Length == 0) continue;
            body.Children.Add(Label(spec.Label));
            var combo = new ComboBox { Margin = new Thickness(0, 6, 0, 14) };
            AutomationProperties.SetName(combo, spec.Label);
            foreach (string item in spec.Items) combo.Items.Add(item);
            combo.SelectedIndex = Math.Clamp(spec.Default, 0, spec.Items.Length - 1);
            body.Children.Add(combo);
            _combos.Add(combo);
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
            AutomationProperties.SetName(slider, spec.Label);
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
