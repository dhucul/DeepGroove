using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class RackValueDialog : Window
{
    private readonly EffectParamViewModel _parameter;

    public RackValueDialog(EffectParamViewModel parameter)
    {
        _parameter = parameter;
        InitializeComponent();
        titleText.Text = parameter.EntryIsPluginPosition
            ? $"{parameter.Label} — slider position (%)"
            : $"{parameter.Label}{(parameter.EntryUnit.Length > 0 ? $" ({parameter.EntryUnit})" : "")}";
        Title = $"Enter {parameter.Label}";
        hintText.Text = parameter.EntryHint;
        AutomationProperties.SetName(valueInput, titleText.Text);
        valueInput.Text = parameter.EntryText;
        UpdateInput();
        Loaded += (_, _) => { valueInput.Focus(); valueInput.SelectAll(); };
    }

    private void OnValueChanged(object sender, TextChangedEventArgs e)
    {
        if (applyBtn != null) UpdateInput();
    }

    private void UpdateInput()
    {
        bool valid = _parameter.TryParseEntry(valueInput.Text, out double value, out _);
        applyBtn.IsEnabled = valid;
        previewText.Text = valid ? $"Will set: {_parameter.FormatEntryValue(value)}" : "";
        errorText.Text = "Enter a finite number within the range and step shown above.";
        errorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!_parameter.TryParseEntry(valueInput.Text, out double value, out _)) return;
        _parameter.Value = value;
        DialogResult = true;
    }
}
