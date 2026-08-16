using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveLab.Util;

namespace WaveLab.Views.Controls;

/// <summary>
/// Shared presentation for the Recording Level Assistant's target-ceiling slider,
/// which appears both in Settings and on the level-check card.
///
/// The landmark marks are drawn rather than left to <see cref="Slider"/> ticks on
/// purpose: setting <see cref="Slider.Ticks"/> makes <c>IsSnapToTickEnabled</c>
/// snap to that collection instead of <c>TickFrequency</c>, which would reduce the
/// continuous ceiling back to the three values it replaced. The theme's slider
/// template also carries no <c>TickBar</c>, so ticks would not paint anyway.
/// </summary>
internal static class TargetCeilingScale
{
    /// <summary>Thumb width from the slider template in Theme.xaml.</summary>
    private const double ThumbWidth = 10;

    /// <summary>One decimal always, so the readout does not jitter in width.</summary>
    internal static string Format(double ceilingDb) => $"−{Math.Abs(ceilingDb):0.0} dBTP";

    /// <summary>
    /// Ranges a slider over the adjustable ceiling span, extending its floor to meet
    /// a stored value deeper than the slider normally reaches so that opening a
    /// dialog can never quietly raise a ceiling someone set by hand.
    /// </summary>
    internal static void Range(Slider slider, double ceilingDb)
    {
        double ceiling = AppSettings.NormalizeTargetCeilingDb(ceilingDb);
        slider.Minimum = Math.Min(AppSettings.AdjustableRecordingTargetCeilingFloorDb, ceiling);
        slider.Maximum = Audio.Dsp.RecordingLevelAnalyzer.MaximumTargetCeilingDb;
        slider.TickFrequency = AppSettings.RecordingTargetCeilingStepDb;
        slider.SmallChange = AppSettings.RecordingTargetCeilingStepDb;
        slider.LargeChange = AppSettings.RecordingTargetCeilingStepDb * 2;
        slider.IsSnapToTickEnabled = true;
        slider.Value = ceiling;
    }

    /// <summary>
    /// Fills <paramref name="canvas"/> with a tick per landmark ceiling, positioned
    /// against the slider's own range so the marks stay true when the floor has been
    /// extended. Call after <see cref="Range"/>.
    /// </summary>
    internal static void DrawLandmarks(Canvas canvas, Slider slider, Brush brush, bool withLabels)
    {
        canvas.Children.Clear();
        double span = slider.Maximum - slider.Minimum;
        double track = slider.Width - ThumbWidth;
        if (!double.IsFinite(span) || span <= 0 || !double.IsFinite(track) || track <= 0) return;

        foreach (double landmark in AppSettings.RecordingTargetCeilingLandmarksDb)
        {
            if (landmark < slider.Minimum || landmark > slider.Maximum) continue;
            double x = ThumbWidth / 2 + track * (landmark - slider.Minimum) / span;

            var tick = new Border { Width = 1, Height = 4, Background = brush, Opacity = 0.85 };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 0);
            canvas.Children.Add(tick);

            if (!withLabels) continue;
            var label = new TextBlock
            {
                Text = $"−{Math.Abs(landmark):0.#}",
                FontSize = 8.5,
                Foreground = brush,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, 5);
            canvas.Children.Add(label);
        }
    }
}
