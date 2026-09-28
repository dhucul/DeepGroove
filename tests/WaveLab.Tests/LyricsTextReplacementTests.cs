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

public sealed class LyricsTextReplacementTests
{
    [Theory]
    [InlineData("Oh, go on, come home.", "go on", "Roanne", "Oh, Roanne, come home.")]
    [InlineData("JOE ANN and Joe Ann", "joe ann", "Roanne", "Roanne and Roanne")]
    [InlineData("I walked out in the rain.", "I walked out in the rain.", "You waited by the river.", "You waited by the river.")]
    [InlineData("Oh, go\t on!", "go on", "Roanne", "Oh, Roanne!")]
    [InlineData("Sing (go on)?", "(go on)?", "$& Roanne", "Sing $& Roanne")]
    [InlineData("Oh, go on.", "go on", "the name I know", "Oh, the name I know.")]
    [InlineData("remove these words", "remove these words", "", "")]
    public void ReplacesLiteralWordsAndPhrasesWithoutSpellingRestrictions(string before, string find, string replacement, string after)
    {
        var line = new LyricsLine { Text = before };
        var change = Assert.Single(LyricsTextReplacement.Preview([line], find, replacement));
        Assert.Equal(before, line.Text); // Preview is non-mutating.
        Assert.Equal(after, change.After);
        Assert.True(LyricsTextReplacement.Apply([change]));
        Assert.Equal(after, line.Text);
        Assert.True(LyricsTextReplacement.Undo([change]));
        Assert.Equal(before, line.Text);
    }

    [Fact]
    public void MatchOptionsProtectLongerWordsAndSupportCaseSensitiveOrSubstringReplacement()
    {
        var line = new LyricsLine { Text = "Ann ann anniversary" };
        var normal = Assert.Single(LyricsTextReplacement.Preview([line], "ann", "Roanne"));
        Assert.Equal("Roanne Roanne anniversary", normal.After);
        Assert.Equal(2, normal.Matches);
        Assert.Equal("Ann Roanne anniversary", Assert.Single(LyricsTextReplacement.Preview([line], "ann", "Roanne", matchCase: true)).After);
        Assert.Equal("Roanne Roanne Roanneiversary", Assert.Single(LyricsTextReplacement.Preview([line], "ann", "Roanne", wholeWords: false)).After);
        Assert.Empty(LyricsTextReplacement.Preview([line], "", "Roanne"));
        Assert.Empty(LyricsTextReplacement.Preview([line], " \t", "Roanne"));
        Assert.Empty(LyricsTextReplacement.Preview([line], "missing", "Roanne"));
        Assert.Empty(LyricsTextReplacement.Preview([line], line.Text, line.Text));
    }

    [Theory]
    [InlineData("I can sing, but I can't dance and I can’t swim.", "can", "could", "I could sing, but I can't dance and I can’t swim.")]
    [InlineData("can't, can’t, 'can', ‘can’", "can", "could", "can't, can’t, 'could', ‘could’")]
    [InlineData("can't, 't', can’t, ‘t’", "t", "now", "can't, 'now', can’t, ‘now’")]
    [InlineData("O'Connor, 'Connor', O’Connor", "Connor", "Roanne", "O'Connor, 'Roanne', O’Connor")]
    [InlineData("can't and won't", "can't", "cannot", "cannot and won't")]
    [InlineData("can't", "can'", "could", "can't")]
    [InlineData("can't", "'t", "not", "can't")]
    [InlineData("ba a a", "a a", "Roanne", "ba Roanne")]
    public void WholeWordsProtectInternalApostrophesButAllowQuotedWordsAndCompleteContractions(
        string before, string find, string replacement, string expected)
    {
        var changes = LyricsTextReplacement.Preview([new LyricsLine { Text = before }], find, replacement);
        if (before == expected) Assert.Empty(changes);
        else Assert.Equal(expected, Assert.Single(changes).After);
    }

    [Fact]
    public void SubstringModeStillAllowsExplicitChangesInsideContractions()
    {
        var line = new LyricsLine { Text = "can't" };
        Assert.Equal("can’t", Assert.Single(LyricsTextReplacement.Preview([line], "'", "’", wholeWords: false)).After);
        Assert.Equal("couldn't", Assert.Single(LyricsTextReplacement.Preview([line], "can", "couldn", wholeWords: false)).After);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindKeepsSurroundingWhitespaceAsRequiredContext(bool wholeWords)
    {
        var line = new LyricsLine { Text = "a cat and a man" };
        var change = Assert.Single(LyricsTextReplacement.Preview([line], " a ", " Roanne ", wholeWords: wholeWords));
        Assert.Equal("a cat and Roanne man", change.After);
        Assert.Equal(1, change.Matches);
        Assert.True(LyricsTextReplacement.Apply([change]));
        Assert.True(LyricsTextReplacement.Undo([change]));
        Assert.Equal("a cat and a man", line.Text);
    }

    [Theory]
    [InlineData("a at a", " a", " Roanne", "a at Roanne")]
    [InlineData("a cat and a", "a ", "Roanne ", "Roanne cat and a")]
    [InlineData("\tgo\t on\r\n", " go on ", "Roanne", "Roanne")]
    public void FindPreservesLeadingOrTrailingContextWhileAllowingDifferentWhitespaceRuns(
        string before, string find, string replacement, string expected)
        => Assert.Equal(expected, Assert.Single(LyricsTextReplacement.Preview(
            [new LyricsLine { Text = before }], find, replacement)).After);

    [Fact]
    public void ReplacementCountExcludesOccurrencesAlreadySpelledAsTheReplacement()
    {
        var change = Assert.Single(LyricsTextReplacement.Preview(
            [new LyricsLine { Text = "Roanne and roanne" }], "roanne", "Roanne"));
        Assert.Equal("Roanne and Roanne", change.After);
        Assert.Equal(1, change.Matches);
    }

    [Theory]
    [InlineData("  go on  ")]
    [InlineData("\tgo\t on\r\n")]
    public void PrefilledPaddedPhraseHasNoChangesUntilEditedAndUndoRestoresItsExactSpacing(string original)
    {
        Wpf.Run(() =>
        {
            var line = new LyricsLine { Text = original };
            Wpf.Show(new LyricsReplaceDialog([line], line, new()), window =>
            {
                var find = (TextBox)window.FindName("findText");
                var replacement = (TextBox)window.FindName("replacementText");
                var apply = (Button)window.FindName("applyButton");
                Assert.Equal(original, find.Text);
                Assert.Equal(original, replacement.Text);
                Assert.Empty(((DataGrid)window.FindName("previewGrid")).Items);
                Assert.False(apply.IsEnabled);
                replacement.Text = "Roanne";
                Assert.True(apply.IsEnabled);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Roanne", line.Text);
                ((Button)window.FindName("undoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(original, line.Text);
                Assert.Null(line.PreviousRetryText);
            });
        });
    }

    [Fact]
    public void AWholeTranscriptChangePreservesEvidenceAndUndoRestoresEarlierRestorePoints()
    {
        var first = new LyricsLine { Start = 1, End = 2, Text = "Go on, go on!", ModelText = "Go on, go on!",
            Words = [new(1, 2, " Go on", .9)], AlternativeText = "alternate", RetryText = "retry", PreviousRetryText = "earlier correction" };
        var second = new LyricsLine { Start = 3, End = 4, Text = "go on", PreviousRetryText = "" };
        var untouched = new LyricsLine { Text = "a different lyric" };
        var evidence = first.Words;
        var changes = LyricsTextReplacement.Preview([first, second, untouched], "go on", "Roanne");
        Assert.Equal(2, changes.Count);
        Assert.Equal(3, changes.Sum(c => c.Matches));
        Assert.True(LyricsTextReplacement.Apply(changes));
        Assert.Equal("Roanne, Roanne!", first.Text);
        Assert.Equal("Roanne", second.Text);
        Assert.Equal("a different lyric", untouched.Text);
        Assert.Equal("Go on, go on!", first.ModelText);
        Assert.Same(evidence, first.Words);
        Assert.Equal((1d, 2d), (first.Start, first.End));
        Assert.Equal("alternate", first.AlternativeText);
        Assert.Equal("retry", first.RetryText);
        Assert.True(LyricsTextReplacement.Undo(changes));
        Assert.Equal("Go on, go on!", first.Text);
        Assert.Equal("go on", second.Text);
        Assert.Equal("earlier correction", first.PreviousRetryText);
        Assert.Equal("", second.PreviousRetryText);
    }

    [Fact]
    public void StalePreviewsAndUndoCannotOverwriteNewerEditsOrPartiallyChangeOtherLines()
    {
        var first = new LyricsLine { Text = "go on" };
        var second = new LyricsLine { Text = "go on" };
        var changes = LyricsTextReplacement.Preview([first, second], "go on", "Roanne");
        second.Text = "my new edit";
        Assert.False(LyricsTextReplacement.Apply(changes));
        Assert.Equal("go on", first.Text);
        second.Text = "go on";
        Assert.True(LyricsTextReplacement.Apply(changes));
        second.Text = "another edit";
        Assert.False(LyricsTextReplacement.Undo(changes));
        Assert.Equal("Roanne", first.Text);
        Assert.Equal("another edit", second.Text);
    }

    [Theory]
    [InlineData(false, 740, 600)]
    [InlineData(true, 900, 660)]
    public void ReplacementDialogSupportsWholePhrasesScopesRepeatedReadingsAndUndoAfterReopening(bool speech, int width, int height)
    {
        Wpf.Run(() =>
        {
            var first = new LyricsLine { Start = 0, End = 1, Text = "Oh, go on, come home." };
            var second = new LyricsLine { Start = 1, End = 2, Text = "Go on, Joe Ann." };
            var doc = new DocumentViewModel(new AudioDocument([new float[2 * 44100]], 44100, 32))
            {
                LyricsTranscript = new LyricsTranscript { RangeEnd = 2, Lines = [first, second] },
                LyricsSettings = new LyricsOptions(false, speech, false, "large-v3", "en", "", false),
            };
            Wpf.Show(new LyricsDialog(doc), parent =>
            {
                parent.Width = 920;
                parent.Height = 740;
                ((DataGrid)parent.FindName("linesGrid")).SelectedItem = first;
                var open = (Button)parent.FindName("replaceTextButton");
                Assert.True(open.IsEnabled);
                parent.UpdateLayout();
                var last = (Button)parent.FindName("alternativeButton");
                Assert.True(last.TransformToAncestor(parent).Transform(new Point(last.ActualWidth, 0)).X < parent.ActualWidth);
                InspectReplacement(parent, open, dialog =>
                {
                    dialog.Width = width;
                    dialog.Height = height;
                    var find = (TextBox)dialog.FindName("findText");
                    var replace = (TextBox)dialog.FindName("replacementText");
                    var scope = (ComboBox)dialog.FindName("scopeCombo");
                    var apply = (Button)dialog.FindName("applyButton");
                    var undo = (Button)dialog.FindName("undoButton");
                    var preview = (DataGrid)dialog.FindName("previewGrid");
                    Assert.Equal(first.Text, find.Text);
                    Assert.Equal(first.Text, replace.Text);
                    Assert.Equal(0, scope.SelectedIndex);
                    Assert.False(apply.IsEnabled);
                    // Rewrite the entire selected phrase, then undo it.
                    replace.Text = "You waited by the river.";
                    Assert.Single(preview.Items);
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("You waited by the river.", first.Text);
                    Assert.Equal("Go on, Joe Ann.", second.Text);
                    undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Oh, go on, come home.", first.Text);
                    // Change a two-word mistaken name throughout the transcript.
                    find.Text = "go on";
                    replace.Text = "Roanne";
                    Assert.Single(preview.Items);
                    scope.SelectedIndex = 1;
                    Assert.Equal(2, preview.Items.Count);
                    dialog.UpdateLayout();
                    Wpf.Pump();
                    dialog.UpdateLayout();
                    Assert.True(preview.ActualHeight > 100);
                    Assert.True(preview.Columns.Sum(c => c.ActualWidth) <= preview.ActualWidth,
                        $"Preview columns {preview.Columns.Sum(c => c.ActualWidth)} must fit width {preview.ActualWidth}.");
                    string? render = Environment.GetEnvironmentVariable("WAVELAB_REPLACEMENT_RENDER");
                    if (render != null)
                    {
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(dialog);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(render + $"-{width}.png");
                        png.Save(output);
                    }
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Oh, Roanne, come home.", first.Text);
                    Assert.Equal("Roanne, Joe Ann.", second.Text);
                    // A second, unrelated reading can become the very same name.
                    find.Text = "Joe Ann";
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Roanne, Roanne.", second.Text);
                });
                InspectReplacement(parent, open, dialog =>
                {
                    var undo = (Button)dialog.FindName("undoButton");
                    Assert.True(undo.IsEnabled);
                    undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Roanne, Joe Ann.", second.Text);
                    undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Oh, go on, come home.", first.Text);
                    Assert.Equal("Go on, Joe Ann.", second.Text);
                    Assert.False(undo.IsEnabled);
                });
                Assert.Contains("Oh, go on, come home.", doc.LyricsTranscript.Export(".txt"));
            });
            doc.Unhook();
        });
    }

    [Fact]
    public void NoSelectionStartsWithWholeTranscriptAndClosingAPreviewChangesNothing()
    {
        Wpf.Run(() =>
        {
            var line = new LyricsLine { Text = "go on" };
            Wpf.Show(new LyricsReplaceDialog([line], null, new()), window =>
            {
                Assert.Equal(1, ((ComboBox)window.FindName("scopeCombo")).SelectedIndex);
                Assert.False(((ComboBoxItem)window.FindName("selectedScope")).IsEnabled);
                ((TextBox)window.FindName("findText")).Text = "go on";
                ((TextBox)window.FindName("replacementText")).Text = "Roanne";
                Assert.True(((Button)window.FindName("applyButton")).IsEnabled);
            });
            Assert.Equal("go on", line.Text);
            Assert.Null(line.PreviousRetryText);
        });
    }

    private static void InspectReplacement(Window parent, Button open, Action<LyricsReplaceDialog> inspect)
    {
        bool inspected = false;
        Exception? failure = null;
        parent.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = parent.OwnedWindows.OfType<LyricsReplaceDialog>().Single();
            dialog.Left = dialog.Top = -10_000;
            try { inspect(dialog); inspected = true; }
            catch (Exception ex) { failure = ex; }
            finally { dialog.Close(); }
        }));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(inspected);
    }
}
