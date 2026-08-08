using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WaveLab.Views;

/// <summary>Searchable command palette (Ctrl+Shift+P).</summary>
public partial class CommandPalette : Window
{
    public sealed record Command(string Name, string? Gesture, Action Execute);

    private readonly List<Command> _all;

    public CommandPalette(List<Command> commands)
    {
        InitializeComponent();
        _all = commands;
        Loaded += (_, _) => { search.Focus(); Filter(""); };
    }

    private void Filter(string query)
    {
        results.Items.Clear();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var cmd in _all)
        {
            if (terms.Length > 0 && !terms.All(t => cmd.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = cmd.Name, FontSize = 12.5 });
            if (cmd.Gesture != null)
            {
                var g = new TextBlock
                {
                    Text = cmd.Gesture,
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("Faint"),
                };
                Grid.SetColumn(g, 1);
                grid.Children.Add(g);
            }
            results.Items.Add(new ListBoxItem { Content = grid, Tag = cmd });
            if (results.Items.Count >= 40) break;
        }
        if (results.Items.Count > 0) results.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Filter(search.Text);

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
            case Key.Down:
                if (results.SelectedIndex < results.Items.Count - 1) results.SelectedIndex++;
                results.ScrollIntoView(results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (results.SelectedIndex > 0) results.SelectedIndex--;
                results.ScrollIntoView(results.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e) => RunSelected();

    private void RunSelected()
    {
        if ((results.SelectedItem as ListBoxItem)?.Tag is not Command cmd) return;
        Close();
        cmd.Execute();
    }

    private void OnDeactivated(object sender, EventArgs e) => Close();
}
