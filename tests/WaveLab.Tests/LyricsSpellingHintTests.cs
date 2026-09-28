using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class LyricsSpellingHintTests
{
    [Theory]
    [InlineData("Oh, rowan, come home.", "Roanne", "Oh, Roanne, come home.")]
    [InlineData("ROWAN and rowan’s song.", "Roanne", "Roanne and Roanne’s song.")]
    [InlineData("rowan's song", "Roanne", "Roanne's song")]
    [InlineData("Elodie met rowan.", " Élodie; Roanne\nRoanne ", "Élodie met Roanne.")]
    [InlineData("roanne", "Roanne", "Roanne")]
    [InlineData("rowan", "Rowan, Roanne", "Rowan")]
    [InlineData("o’connor", "O'Connor", "O'Connor")]
    [InlineData("Joanne", "Roanne", "Roanne")]
    [InlineData("Xlodie", "Élodie", "Élodie")]
    public void OffersHintedSpellingWithoutChangingSurroundingText(string text, string hints, string expected)
        => Assert.Equal(expected, LyricsSpellingHints.Suggest(text, hints));

    [Theory]
    [InlineData("rowan", "")]
    [InlineData("Roanne", "Roanne")]
    [InlineData("rowan", "Roanne, Rowena")]
    [InlineData("rowan", "Roanne Smith")]
    [InlineData("The road winds around the trees.", "Roanne")]
    [InlineData("Yorkshire and Newark", "New York")]
    [InlineData("an in at on", "Ann")]
    [InlineData("rowanberry", "Roanne")]
    [InlineData("rowan", "Please use the name Roanne.")]
    public void LeavesUnrelatedOrAmbiguousTextForManualEditing(string text, string hints)
        => Assert.Null(LyricsSpellingHints.Suggest(text, hints));

    [Theory]
    [InlineData(false, 920, 740)]
    [InlineData(true, 1080, 800)]
    public void HintCanCorrectAnExistingLineWithoutRecognitionAndRestoreIt(bool speech, int width, int height)
    {
        Wpf.Run(() =>
        {
            var target = new LyricsLine { Start = 0, End = 1, Text = "Oh, rowan, come home.",
                ModelText = "Oh, rowan, come home.", AlternativeText = "existing alternate",
                RetryText = "existing retry", Words = [new(0, 1, " rowan", .95)] };
            var neighbor = new LyricsLine { Start = 1, End = 2, Text = "rowan stays here" };
            var doc = new DocumentViewModel(new AudioDocument([new float[2 * 44100]], 44100, 32))
            {
                LyricsTranscript = new LyricsTranscript { RangeEnd = 2, Lines = [target, neighbor] },
                LyricsSettings = new LyricsOptions(false, speech, false, "large-v3", "en", "", false),
            };
            var originalWords = target.Words;
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                window.Width = width;
                window.Height = height;
                var grid = (DataGrid)window.FindName("linesGrid");
                var hints = (TextBox)window.FindName("hintsText");
                var panel = (DockPanel)window.FindName("spellingSuggestionPanel");
                var apply = (Button)window.FindName("spellingSuggestionButton");
                var preview = (TextBlock)window.FindName("spellingSuggestionLabel");
                var restore = (Button)window.FindName("restoreRetryButton");
                grid.SelectedItem = target;
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                hints.Text = "Roanne";
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.True(apply.IsEnabled);
                Assert.Contains("Oh, Roanne, come home.", preview.Text);
                Assert.Equal("Oh, rowan, come home.", target.Text);
                Assert.False(restore.IsEnabled);
                window.UpdateLayout();
                Assert.True(grid.ActualHeight > 80, "Spelling and retry previews must leave room for the transcript.");
                Assert.True(apply.TransformToAncestor(window).Transform(new Point(apply.ActualWidth, apply.ActualHeight)).X < window.ActualWidth);
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_SPELLING_RENDER");
                if (renderPath != null)
                {
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(renderPath + $"-{width}.png");
                    png.Save(output);
                }
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Oh, Roanne, come home.", target.Text);
                Assert.Equal("Oh, rowan, come home.", target.PreviousRetryText);
                Assert.Equal("Oh, rowan, come home.", target.ModelText);
                Assert.Same(originalWords, target.Words);
                Assert.Equal(" rowan", target.Words[0].Word);
                Assert.Equal(0, target.Start);
                Assert.Equal(1, target.End);
                Assert.Equal("existing alternate", target.AlternativeText);
                Assert.Equal("existing retry", target.RetryText);
                Assert.Equal("rowan stays here", neighbor.Text);
                Assert.Contains("Oh, Roanne, come home.", doc.LyricsTranscript.Export(".txt"));
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.True(restore.IsEnabled);
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Oh, rowan, come home.", target.Text);
                Assert.Null(target.PreviousRetryText);
                Assert.Equal(Visibility.Visible, panel.Visibility);

                // Both manual edits and competing hints must invalidate the old preview.
                target.Text = "The road winds around the trees.";
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                target.Text = "rowan";
                Assert.Equal(Visibility.Visible, panel.Visibility);
                hints.Text = "Roanne, Rowena";
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                hints.Text = "Roanne";
                grid.SelectedItem = neighbor;
                Assert.Contains("Roanne stays here", preview.Text);
                grid.SelectedItem = null;
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
            });
            Assert.Equal("Roanne", doc.LyricsSettings!.Hints);
            doc.Unhook();
        });
    }
}
