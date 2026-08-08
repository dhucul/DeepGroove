using System.Windows;

namespace WaveLab.Views;

public partial class InfoDialog : Window
{
    public InfoDialog(string title, string message, string? bigValue = null)
    {
        InitializeComponent();
        titleText.Text = title;
        messageText.Text = message;
        if (bigValue != null)
        {
            bigText.Text = bigValue;
            bigText.Visibility = Visibility.Visible;
        }
    }

    public static void Show(Window owner, string title, string message, string? bigValue = null) =>
        new InfoDialog(title, message, bigValue) { Owner = owner }.ShowDialog();

    private void OnOk(object sender, RoutedEventArgs e) => Close();
}
