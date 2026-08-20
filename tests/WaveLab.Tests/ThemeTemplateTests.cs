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
}
