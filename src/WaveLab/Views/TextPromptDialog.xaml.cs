using System.Windows;
using System.Windows.Input;

namespace WaveLab.Views;

public partial class TextPromptDialog : Window
{
    public string? Result { get; private set; }

    public TextPromptDialog(string prompt, string initial = "")
    {
        InitializeComponent();
        promptText.Text = prompt;
        input.Text = initial;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }

    public static string? Show(Window owner, string prompt, string initial = "")
    {
        var dlg = new TextPromptDialog(prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
        else if (e.Key == Key.Escape) OnCancel(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = input.Text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
