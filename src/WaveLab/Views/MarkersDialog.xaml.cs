using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class MarkersDialog : Window
{
    private sealed record Entry(string Kind, object Item, string Text) { public override string ToString() => Text; }

    private readonly DocumentViewModel _doc;

    public MarkersDialog(DocumentViewModel doc)
    {
        InitializeComponent();
        _doc = doc;
        Refresh();
    }

    private void Refresh()
    {
        list.Items.Clear();
        foreach (var m in _doc.Markers.OrderBy(m => m.Position))
            list.Items.Add(MakeItem(new Entry("marker", m, ""), $"⚑  {m.Name}", TimeFormat.Position(m.Position, _doc.Doc.SampleRate)));
        foreach (var r in _doc.Regions.OrderBy(r => r.Start))
            list.Items.Add(MakeItem(new Entry("region", r, ""),
                $"▬  {r.Name}",
                $"{TimeFormat.Position(r.Start, _doc.Doc.SampleRate)} – {TimeFormat.Position(r.End, _doc.Doc.SampleRate)}"));
        if (list.Items.Count == 0)
            list.Items.Add(new ListBoxItem
            {
                Content = "No markers yet — press Ctrl+M to drop one at the cursor.",
                Foreground = (Brush)FindResource("Faint"),
                IsEnabled = false,
            });
    }

    private ListBoxItem MakeItem(Entry entry, string name, string time)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameText = new TextBlock { Text = name, FontSize = 12.5 };
        var timeText = new TextBlock
        {
            Text = time,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
        };
        Grid.SetColumn(timeText, 1);
        grid.Children.Add(nameText);
        grid.Children.Add(timeText);
        return new ListBoxItem { Content = grid, Tag = entry };
    }

    private Entry? Selected => (list.SelectedItem as ListBoxItem)?.Tag as Entry;

    private void OnJump(object sender, MouseButtonEventArgs e) => JumpToSelected();
    private void OnJumpButton(object sender, RoutedEventArgs e) => JumpToSelected();

    private void JumpToSelected()
    {
        switch (Selected?.Item)
        {
            case Marker m:
                _doc.JumpToMarker(m);
                break;
            case NamedRegion r:
                _doc.SetSelection(r.Start, r.End);
                _doc.CenterViewOn((r.Start + r.End) / 2.0);
                break;
        }
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry == null) return;
        string current = entry.Item is Marker m ? m.Name : ((NamedRegion)entry.Item).Name;
        var name = TextPromptDialog.Show(this, "Rename", current);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (entry.Item is Marker marker) marker.Name = name.Trim();
        else ((NamedRegion)entry.Item).Name = name.Trim();
        _doc.NotifyMarkersChanged();
        Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry == null) return;
        if (entry.Item is Marker marker) _doc.Markers.Remove(marker);
        else if (entry.Item is NamedRegion region) _doc.Regions.Remove(region);
        _doc.NotifyMarkersChanged();
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
