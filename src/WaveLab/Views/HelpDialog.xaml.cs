using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Help;

namespace WaveLab.Views;

public partial class HelpDialog : Window
{
    private readonly string _initialTopicId;
    private bool _initialized;

    public HelpDialog(string? initialTopicId = null)
    {
        _initialTopicId = HelpCatalog.GetTopic(initialTopicId).Id;
        InitializeComponent();
        ApplySearch("");
        _initialized = true;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) ApplySearch(searchBox.Text);
    }

    private void ApplySearch(string? query)
    {
        HelpTopic? previous = topicList.SelectedItem as HelpTopic;
        IReadOnlyList<HelpTopic> matches = HelpCatalog.Search(query);
        topicList.ItemsSource = matches;
        noResults.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        topicList.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        resultCount.Text = matches.Count == HelpCatalog.Topics.Count
            ? $"{matches.Count} help topics"
            : $"{matches.Count} of {HelpCatalog.Topics.Count} topics";

        if (matches.Count == 0)
        {
            topicContent.DataContext = null;
            return;
        }

        HelpTopic selection = previous != null && matches.Contains(previous)
            ? previous
            : matches.FirstOrDefault(topic => topic.Id == _initialTopicId) ?? matches[0];
        topicList.SelectedItem = selection;
        topicList.ScrollIntoView(selection);
    }

    private void OnTopicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        topicContent.DataContext = topicList.SelectedItem;
        contentScroll.ScrollToTop();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            searchBox.Focus();
            searchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
