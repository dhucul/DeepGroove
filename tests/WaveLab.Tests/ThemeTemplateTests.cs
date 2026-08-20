using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The handful of facts about <c>Themes/Theme.xaml</c> that a shipped visual bug turned on.
/// </summary>
/// <remarks>
/// Every one of these was found by rendering a real window and looking at it, because a template is
/// not code any test was reading: a control whose style drops a property still lays out, still
/// draws, and still passes everything except the eye. These are cheap to keep and each one names
/// the fault it stands in for.
/// </remarks>
public sealed class ThemeTemplateTests
{
    private const double IconSquare = 38;   // ToolButton's fixed width and height

    private static T Templated<T>(T control, double width = 200, double height = 60)
        where T : FrameworkElement
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        return control;
    }

    private static Style Resource(string key) =>
        Assert.IsType<Style>(Application.Current.TryFindResource(key));

    /// <summary>
    /// A disabled field that looks live is a UI that lies. The CD transfer dialog's catalogue
    /// fields are switched off for a WAV package, and before this trigger existed the only way to
    /// find that out was to click into one and try to type.
    /// </summary>
    [Fact]
    public void ADisabledTextBoxLooksDisabled()
    {
        // Inside a real window, because the themed TextBox style carries no key: it is found by
        // type through the tree the box is in, and a box that is in no tree never picks it up.
        (double live, double off) = Wpf.Run(() =>
        {
            var enabled = new TextBox { Text = "GBAAA2400001" };
            var disabled = new TextBox { Text = "GBAAA2400002", IsEnabled = false };
            var panel = new StackPanel();
            panel.Children.Add(enabled);
            panel.Children.Add(disabled);

            (double Live, double Off) result = default;
            Wpf.Show(new Window { Content = panel, Width = 300, Height = 200 },
                _ => result = (Opacity(enabled), Opacity(disabled)));
            return result;
        });

        Assert.Equal(1, live);
        Assert.True(off < 1, $"a disabled field was drawn at {off:0.00} opacity, the same as a live one.");

        static double Opacity(TextBox box)
        {
            Assert.NotNull(box.Template);
            return Assert.IsType<Border>(box.Template.FindName("bd", box)).Opacity;
        }
    }

    /// <summary>
    /// <c>SegmentButton</c> is built on <c>ToolButton</c>, which is a 38×38 icon square with its
    /// width nailed down. A segment carries a word, so it has to let that width go — inheriting it
    /// shipped a view switch reading "Wa | Sp | Sp".
    /// </summary>
    [Fact]
    public void ASegmentButtonIsWideEnoughForItsWord()
    {
        double width = Wpf.Run(() =>
        {
            var segment = new ToggleButton { Content = "Waveform", Style = Resource("SegmentButton") };
            segment.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return segment.DesiredSize.Width;
        });

        Assert.True(width > IconSquare, $"a segment reading \"Waveform\" measured {width:0} px.");
    }

    /// <summary>
    /// The accent button honours <c>Padding</c>, so a button can be sized to its text rather than
    /// to a column — it did not always, and a button sized that way came out cramped.
    /// </summary>
    [Fact]
    public void TheAccentButtonHonoursItsPadding()
    {
        (double bare, double padded) = Wpf.Run(() =>
        {
            double Width(Thickness padding)
            {
                var button = new Button
                {
                    Content = "Export DDP Image Set…",
                    Style = Resource("AccentButton"),
                    Padding = padding,
                };
                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return button.DesiredSize.Width;
            }

            return (Width(new Thickness(0)), Width(new Thickness(24, 0, 24, 0)));
        });

        Assert.Equal(48, padded - bare, 0);
    }

    /// <summary>
    /// <c>ToolButton</c>, on the other hand, does not — a known limitation rather than an oversight
    /// waiting to be found again.
    /// </summary>
    /// <remarks>
    /// Its template centres the content and binds no margin to it, and the style pins the width at
    /// 38 anyway, so a tool button is sized with <c>MinWidth</c> and never with <c>Padding</c>. If
    /// this test ever fails because the template started honouring padding, that is an improvement
    /// — delete it and correct the note in <c>CLAUDE.md</c> that sends people to <c>MinWidth</c>.
    /// </remarks>
    [Fact]
    public void TheToolButtonIgnoresPaddingAndIsSizedWithMinWidthInstead()
    {
        (double padded, double minimum) = Wpf.Run(() =>
        {
            var withPadding = new Button
            {
                Content = "×",
                Style = Resource("ToolButton"),
                Padding = new Thickness(40, 0, 40, 0),
            };
            withPadding.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var withMinWidth = new Button
            {
                Content = "×",
                Style = Resource("ToolButton"),
                MinWidth = 120,
            };
            withMinWidth.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            return (withPadding.DesiredSize.Width, withMinWidth.DesiredSize.Width);
        });

        Assert.Equal(IconSquare, padded, 0);
        Assert.Equal(120, minimum, 0);
    }

    /// <summary>
    /// A combo bound through <c>DisplayMemberPath</c> shows the path's value, not the item's type.
    /// </summary>
    /// <remarks>
    /// The themed template bound <c>SelectionBoxItem</c> and <c>SelectionBoxItemTemplate</c> but not
    /// <c>ContentTemplateSelector</c>, and <c>DisplayMemberPath</c> reaches the closed box through
    /// the selector — so the box fell back to <c>ToString()</c>. On the CD import dialog that put
    /// "WaveLab.Views.CdImportDialog+DriveRow" where the drive's name belonged, which is what a real
    /// disc rip surfaced. The stock template binds all three.
    /// </remarks>
    [Fact]
    public void AComboBoxShowsItsDisplayMemberPathRatherThanTheItemsTypeName()
    {
        string shown = Wpf.Run(() =>
        {
            var combo = new ComboBox
            {
                DisplayMemberPath = "DisplayText",
                ItemsSource = new[] { new Row() },
            };
            string text = "";
            Wpf.Show(new Window { Content = combo, Width = 400, Height = 120 }, _ =>
            {
                combo.SelectedIndex = 0;
                Wpf.Pump();
                text = FirstText(combo);
            });
            return text;
        });

        Assert.Equal(Row.Expected, shown);
    }

    public sealed class Row
    {
        public const string Expected = "Pioneer BD-RW · 12 audio track(s)";
        public string DisplayText => Expected;
    }

    /// <summary>
    /// A tab strip long enough to scroll keeps its tabs at full height.
    /// </summary>
    /// <remarks>
    /// The strip scrolls horizontally once the open files outrun the window, and the scroll bar has
    /// to come from somewhere. In a fixed-height row it comes out of the tabs: fourteen files — one
    /// CD — took the usable height from 35 px to 18 and cut every tab and every name in half.
    /// <c>MainWindow</c>'s tab row is <c>Auto</c> so the strip grows instead, and this is the
    /// measurement that says why.
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(14)]
    public void AScrollingTabStripKeepsItsTabsAtFullHeight(int tabs)
    {
        double height = Wpf.Run(() =>
        {
            var list = new ListBox
            {
                Style = Resource("FileTabs"),
                VerticalAlignment = VerticalAlignment.Bottom,
                ItemTemplate = TabTemplate(),
                ItemsSource = Enumerable.Range(1, tabs)
                    .Select(i => $"Audio CD - Track {i:00}").ToList(),
            };

            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.Children.Add(list);

            double measured = 0;
            Wpf.Show(new Window { Content = host, Width = 1280, Height = 800 }, _ =>
            {
                Wpf.Pump();
                var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(tabs - 1);
                measured = container?.ActualHeight ?? 0;
            });
            return measured;
        });

        Assert.Equal(30, height, 0);
    }

    private static DataTemplate TabTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <TextBlock Text="{Binding}" FontSize="12.5" MaxWidth="220" TextTrimming="CharacterEllipsis"/>
        </DataTemplate>
        """);

    private static string FirstText(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && block.Text.Length > 0) return block.Text;
            string found = FirstText(child);
            if (found.Length > 0) return found;
        }
        return "";
    }

    /// <summary>
    /// The slim scroll bar is slim in both directions.
    /// </summary>
    /// <remarks>
    /// Setting <c>Height</c> is not enough. What a style does not declare falls through to the
    /// built-in <c>ScrollBar</c> style, which sets a minimum from <c>SystemParameters</c> — so the
    /// horizontal bar reported <c>Height</c> 8 and measured 17, and the nine pixels came out of
    /// whatever it was scrolling. In the file tab strip that was visible as a strip 49 px tall
    /// instead of 40.
    /// </remarks>
    [Theory]
    [InlineData(Orientation.Horizontal)]
    [InlineData(Orientation.Vertical)]
    public void TheScrollBarIsAsSlimAsItIsAskedToBe(Orientation orientation)
    {
        double thickness = Wpf.Run(() =>
        {
            var bar = new System.Windows.Controls.Primitives.ScrollBar
            {
                Orientation = orientation,
                Maximum = 100,
                ViewportSize = 10,
            };
            double measured = 0;
            Wpf.Show(new Window { Content = bar, Width = 400, Height = 200 }, _ =>
            {
                Wpf.Pump();
                measured = orientation == Orientation.Horizontal ? bar.ActualHeight : bar.ActualWidth;
            });
            return measured;
        });

        Assert.Equal(8, thickness, 0);
    }
}
