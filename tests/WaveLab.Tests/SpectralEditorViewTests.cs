using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.ViewModels;
using WaveLab.Views.Controls;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpectralEditorViewTests(ITestOutputHelper output)
{
    // ── channel reduction ────────────────────────────────────────

    [Fact]
    public void MidAndSideAreTheSumAndDifference()
    {
        float[][] channels = [[1f, 0.5f, 0f], [0.5f, 0.5f, -1f]];

        float[] mid = SpectralEditorView.Mix(channels, SpectralChannel.Mid);
        float[] side = SpectralEditorView.Mix(channels, SpectralChannel.Side);

        Assert.Equal([0.75f, 0.5f, -0.5f], mid);
        Assert.Equal([0.25f, 0f, 0.5f], side);
    }

    [Fact]
    public void LeftAndRightAreTakenAsTheyAre()
    {
        float[][] channels = [[1f, 2f], [3f, 4f]];

        Assert.Same(channels[0], SpectralEditorView.Mix(channels, SpectralChannel.Left));
        Assert.Same(channels[1], SpectralEditorView.Mix(channels, SpectralChannel.Right));
    }

    [Fact]
    public void AMonoDocumentIsUsedWhicheverChannelIsAsked()
    {
        float[][] channels = [[1f, 2f, 3f]];

        foreach (SpectralChannel channel in Enum.GetValues<SpectralChannel>())
            Assert.Same(channels[0], SpectralEditorView.Mix(channels, channel));
    }

    [Fact]
    public void RaggedChannelsAreReducedToTheShorter()
    {
        float[][] channels = [[1f, 1f, 1f, 1f], [1f, 1f]];

        Assert.Equal(2, SpectralEditorView.Mix(channels, SpectralChannel.Mid).Length);
    }

    [Fact]
    public void NoChannelsIsEmptyRatherThanAnError()
    {
        Assert.Empty(SpectralEditorView.Mix([], SpectralChannel.Mid));
    }

    // ── coordinate mapping ───────────────────────────────────────

    /// <summary>
    /// The mapping the ruler, the selection overlay and every mouse gesture all have to agree on.
    /// Runs on an STA thread because the control is a <c>FrameworkElement</c>.
    /// </summary>
    [Fact]
    public void ScreenPositionsMapToSamplesAndFrequenciesAndBack()
    {
        RunOnUiThread(() =>
        {
            var document = new AudioDocument([new float[480_000], new float[480_000]], 48_000, 32);
            // Zoom before scrolling: ViewStart is clamped so the view cannot run off the end, and at
            // the zoom the constructor leaves behind there is nowhere to scroll to.
            var vm = new DocumentViewModel(document) { SamplesPerPixel = 256 };
            vm.ViewStart = 100_000;
            var view = new SpectralEditorView { Document = vm, Width = 800, Height = 300 };
            view.Measure(new System.Windows.Size(800, 300));
            view.Arrange(new System.Windows.Rect(0, 0, 800, 300));

            // Time runs left to right from the view origin.
            Assert.Equal(100_000, view.SampleAtX(0), 3);
            Assert.Equal(100_000 + 256 * 400, view.SampleAtX(400), 3);

            // Frequency runs bottom to top, logarithmically.
            double top = view.FrequencyAtY(0);
            double middle = view.FrequencyAtY(150);
            double bottom = view.FrequencyAtY(300);
            output.WriteLine($"top {top:0} Hz, middle {middle:0} Hz, bottom {bottom:0} Hz");

            Assert.True(top > middle && middle > bottom, "frequency must rise towards the top");
            Assert.InRange(top, 19_000, 20_100);
            Assert.InRange(bottom, 19, 21);

            // Halfway up a log axis is the geometric mean of the ends, not the arithmetic one.
            Assert.Equal(Math.Sqrt(top * bottom), middle, middle * 0.02);
        });
    }

    [Fact]
    public void WithoutADocumentTheMappingStillAnswersSafely()
    {
        RunOnUiThread(() =>
        {
            var view = new SpectralEditorView { Width = 400, Height = 200 };
            view.Measure(new System.Windows.Size(400, 200));
            view.Arrange(new System.Windows.Rect(0, 0, 400, 200));

            Assert.Equal(0, view.SampleAtX(123));
            Assert.True(double.IsFinite(view.FrequencyAtY(50)));
        });
    }

    [Fact]
    public void AnEmptyRegionIsRecognisedAsEmpty()
    {
        Assert.True(SpectralRegion.None.IsEmpty);
        Assert.True(new SpectralRegion(100, 100, 20, 20_000).IsEmpty);
        Assert.True(new SpectralRegion(100, 200, 5_000, 5_000).IsEmpty);
        Assert.False(new SpectralRegion(100, 200, 500, 5_000).IsEmpty);
    }

    private static void RunOnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the UI thread did not finish");
        if (failure != null) throw failure;
    }
}
