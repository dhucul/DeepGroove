using System.Windows;
using System.Windows.Media;

namespace WaveLab.Views.Controls;

/// <summary>Frozen brushes, pens and typefaces shared by the custom-drawn controls.</summary>
public static class WaveTheme
{
    public static readonly Color Accent = Color.FromRgb(0x3F, 0xD6, 0xC2);
    public static readonly Color AccentBright = Color.FromRgb(0x57, 0xE6, 0xD2);
    public static readonly Color Amber = Color.FromRgb(0xFF, 0xB4, 0x54);

    public static readonly Brush WaveBg = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x14)));
    public static readonly Brush PanelBg = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1B)));
    public static readonly Brush OverviewBg = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x17)));
    public static readonly Brush SpectrumBg = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x19)));

    public static readonly Brush WavePeak = Freeze(new SolidColorBrush(Color.FromRgb(0x31, 0xA9, 0x98)));
    public static readonly Brush WavePeakSel = Freeze(new SolidColorBrush(Color.FromRgb(0x57, 0xE6, 0xD2)));
    public static readonly Brush WaveRms = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x57, 0xCF, 0xC0)));
    public static readonly Brush WaveRmsSel = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0xAE, 0xF5, 0xEA)));

    public static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0x3F, 0xD6, 0xC2)));
    public static readonly Brush SelectionOverlay = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0x3F, 0xD6, 0xC2)));
    public static readonly Pen SelectionEdge = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x3F, 0xD6, 0xC2)), 1));
    public static readonly Pen CenterLine = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xE7, 0xEA, 0xEE)), 1));
    public static readonly Pen GridLine = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), 1));
    public static readonly Pen ChannelDivider = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x1D, 0x22, 0x28)), 1));
    public static readonly Pen Playhead = FreezePen(new Pen(new SolidColorBrush(Amber), 1.25));
    public static readonly Pen CursorPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0xB8, 0xE7, 0xEA, 0xEE)), 1));
    public static readonly Pen TickPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x4A)), 1));
    public static readonly Pen AccentPen = FreezePen(new Pen(new SolidColorBrush(Accent), 1.6));
    public static readonly Pen PeakHoldPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xB4, 0x54)), 1));
    public static readonly Pen ViewportPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x73, 0x3F, 0xD6, 0xC2)), 1));
    public static readonly Pen MarkerLine = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xB4, 0x54)), 1));
    public static readonly Brush ViewportFill = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0x3F, 0xD6, 0xC2)));

    public static readonly Brush TextMuted = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x71, 0x7A)));
    public static readonly Brush TextFaint = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x58)));

    public static readonly Typeface MonoFace = new(
        new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#JetBrains Mono"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface UiFace = new(
        new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Inter"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static FormattedText Text(string s, Typeface face, double size, Brush brush, double dpi) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
