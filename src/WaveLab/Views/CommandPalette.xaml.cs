using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WaveLab.Views;

/// <summary>Searchable command palette (Ctrl+Shift+P).</summary>
public partial class CommandPalette : Window
{
    public sealed record Command(string Name, string? Gesture, Action Execute, Func<bool>? CanExecute = null);

    /// <summary>
    /// Upper bound on rendered rows. It exists so a pathological command list cannot cost a
    /// screenful of layout, not to hide commands: the palette is built with 55 entries and the
    /// results list scrolls, so at this size nothing is ever dropped. When it does bite, the
    /// trailing row below says so — a silently truncated list reads as "that is all there is".
    /// </summary>
    private const int MaxResults = 200;

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
        int matched = 0;
        foreach (var cmd in _all)
        {
            if (terms.Length > 0 && !terms.All(t => cmd.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;
            matched++;
            if (results.Items.Count >= MaxResults) continue;
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
            results.Items.Add(new ListBoxItem
            {
                Content = grid,
                Tag = cmd,
                IsEnabled = cmd.CanExecute?.Invoke() ?? true,
            });
        }

        // Say what was dropped. The row is disabled and carries no Command, so neither
        // RunSelected nor MoveSelection can land on it.
        if (matched > results.Items.Count)
        {
            results.Items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Content = new TextBlock
                {
                    Text = $"…and {matched - results.Items.Count} more — keep typing to narrow the list",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Faint"),
                },
            });
        }

        results.SelectedItem = results.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.IsEnabled);
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
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e) => RunSelected();

    private void RunSelected()
    {
        if (results.SelectedItem is not ListBoxItem { IsEnabled: true, Tag: Command cmd }) return;
        if (cmd.CanExecute?.Invoke() == false) return;
        Close();
        cmd.Execute();
    }

    private void MoveSelection(int delta)
    {
        int index = results.SelectedIndex;
        for (int next = index + delta; next >= 0 && next < results.Items.Count; next += delta)
        {
            if (results.Items[next] is not ListBoxItem { IsEnabled: true } item) continue;
            results.SelectedItem = item;
            results.ScrollIntoView(item);
            return;
        }
    }

    private void OnDeactivated(object sender, EventArgs e) => Close();
}
