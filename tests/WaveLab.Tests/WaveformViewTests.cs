using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

public sealed class WaveformViewTests
{
    [Fact]
    public void GeometryWindowOverscansAndCoversSmallPlaybackMoves()
    {
        WaveformView.GeometryWindow window = WaveformView.CalculateGeometryWindow(
            documentLength: 100_000,
            viewStart: 20_000,
            samplesPerPixel: 10,
            viewWidth: 100);

        Assert.Equal(19_500, window.StartSample);
        Assert.Equal(21_500, window.EndSample);
        Assert.Equal(200, window.PixelWidth);
        Assert.True(WaveformView.GeometryWindowCovers(window, 20_500, 21_500));
        Assert.False(WaveformView.GeometryWindowCovers(window, 20_501, 21_501));
    }

    [Fact]
    public void GeometryWindowShiftsBackAtDocumentEnd()
    {
        WaveformView.GeometryWindow window = WaveformView.CalculateGeometryWindow(
            documentLength: 100_000,
            viewStart: 99_000,
            samplesPerPixel: 10,
            viewWidth: 100);

        Assert.Equal(98_000, window.StartSample);
        Assert.Equal(100_000, window.EndSample);
        Assert.True(WaveformView.GeometryWindowCovers(window, 99_000, 100_000));
    }
}
