using System.Windows.Media;
using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The waveform is painted per device-pixel column into a bitmap, so the pieces worth
/// pinning down are the column→row mapping and the colour compositing.
/// </summary>
public sealed class WaveformViewTests
{
    [Theory]
    [InlineData(-5.0, 0, 10, 0)]     // above the band clamps to the top row
    [InlineData(50.0, 0, 10, 10)]    // below the band clamps to the bottom row
    [InlineData(3.4, 0, 10, 3)]
    [InlineData(3.6, 0, 10, 4)]
    [InlineData(11.2, 8, 20, 11)]    // second channel band keeps its own top offset
    public void ClampRowRoundsAndStaysInsideTheBand(double y, int top, int bottom, int expected) =>
        Assert.Equal(expected, WaveformView.ClampRow(y, top, bottom));

    [Fact]
    public void OpaqueArgbKeepsTheBrushColourAndForcesFullAlpha()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x31, 0xA9, 0x98));
        Assert.Equal(unchecked((int)0xFF31A998), WaveformView.OpaqueArgb(brush));
    }

    [Fact]
    public void BlendOverCompositesTheTranslucentRmsColourOntoThePeakBand()
    {
        var over = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x00, 0x00));
        var under = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF));

        int argb = WaveformView.BlendOver(over, under);

        Assert.Equal(0xFF, (argb >> 24) & 0xFF); // the painted pixel is always opaque
        Assert.Equal(128, (argb >> 16) & 0xFF);
        Assert.Equal(0, (argb >> 8) & 0xFF);
        Assert.Equal(127, argb & 0xFF);
    }

    [Fact]
    public void BlendOverWithAnOpaqueBrushIsThatBrush()
    {
        var over = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56));
        var under = new SolidColorBrush(Colors.White);

        Assert.Equal(WaveformView.OpaqueArgb(over), WaveformView.BlendOver(over, under));
    }
}
