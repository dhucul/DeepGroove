using System.Windows;
using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class LyricsWorkflowTests
{
    [Fact]
    public void SettingsSurviveDialogReopeningAndStayWithTheirOwnTab()
    {
        Wpf.Run(() =>
        {
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32));
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                ((ComboBox)window.FindName("modeCombo")).SelectedIndex = 1;
                ((ComboBox)window.FindName("qualityCombo")).SelectedIndex = 1;
                ((ComboBox)window.FindName("languageCombo")).SelectedValue = "fr";
                ((TextBox)window.FindName("hintsText")).Text = "Élodie";
                ((ComboBox)window.FindName("deviceCombo")).SelectedIndex = 1;
                ((CheckBox)window.FindName("retryCheck")).IsChecked = false;
                Assert.False(((CheckBox)window.FindName("compareCheck")).IsEnabled);
            });
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                Assert.Equal(1, ((ComboBox)window.FindName("modeCombo")).SelectedIndex);
                Assert.Equal(1, ((ComboBox)window.FindName("qualityCombo")).SelectedIndex);
                Assert.Equal("fr", ((ComboBox)window.FindName("languageCombo")).SelectedValue);
                Assert.Equal("Élodie", ((TextBox)window.FindName("hintsText")).Text);
                Assert.Equal(1, ((ComboBox)window.FindName("deviceCombo")).SelectedIndex);
                Assert.False(((CheckBox)window.FindName("retryCheck")).IsChecked);
                ((ComboBox)window.FindName("modeCombo")).SelectedIndex = 0;
                Assert.True(((CheckBox)window.FindName("compareCheck")).IsEnabled);
                Assert.True(((CheckBox)window.FindName("compareCheck")).IsChecked);
            });
            var other = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32));
            Assert.Null(other.LyricsSettings);
            doc.Unhook();
            other.Unhook();
        });
    }

    [Theory]
    [InlineData("my correction")]
    [InlineData("")]
    public void AcceptingARetryChangesOnlyThatLineAndCanRestoreTheUsersCorrection(string previousText)
    {
        Wpf.Run(() =>
        {
            var line = new LyricsLine { Start = 1, End = 3, Text = previousText, ModelText = "model text",
                AlternativeText = "existing alternative", RetryText = "retry suggestion" };
            var other = new LyricsLine { Start = 4, End = 5, Text = "another correction" };
            var doc = new DocumentViewModel(new AudioDocument([new float[6 * 44100]], 44100, 32))
            { LyricsTranscript = new LyricsTranscript { RangeEnd = 6, Lines = [line, other] } };
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedItem = line;
                Assert.Equal(previousText, line.Text);
                var accept = (Button)window.FindName("acceptRetryButton");
                var restore = (Button)window.FindName("restoreRetryButton");
                Assert.True(accept.IsEnabled);
                Assert.False(restore.IsEnabled);
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("retry suggestion", line.Text);
                Assert.Equal(previousText, line.PreviousRetryText);
                Assert.Empty(line.RetryText);
                Assert.False(accept.IsEnabled);
                Assert.True(restore.IsEnabled);
                Assert.Equal("existing alternative", line.AlternativeText);
                Assert.Equal("another correction", other.Text);
                Assert.Equal("model text", line.ModelText);
                Assert.Equal(1, line.Start);
                Assert.Equal(3, line.End);
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(previousText, line.Text);
                Assert.Null(line.PreviousRetryText);
                Assert.False(restore.IsEnabled);
                Assert.DoesNotContain("retry suggestion", doc.LyricsTranscript.Export(".txt"));
            });
            doc.Unhook();
        });
    }

    [Fact]
    public void RetryKeepsContextInsideTheTranscriptAndExcludesNeighborWords()
    {
        var target = new LyricsLine { Start = 61, End = 64 };
        var next = new LyricsLine { Start = 64, End = 67 };
        var transcript = new LyricsTranscript { RangeStart = 60, RangeEnd = 68, Lines = [target, next] };
        Assert.Equal((60000, 7000), LyricsLineRetry.Range(target, transcript, 1000, 100000));
        var retry = new LyricsTranscript { Lines = [new LyricsLine { Words = [
            new(60, 60.5, " previous", .9), new(61, 62, " correct", .9),
            new(62, 63, " words", .9), new(64, 65, " neighbor", .9)] }] };
        Assert.Equal("correct words", LyricsLineRetry.Suggestion(target, retry, transcript.Lines));
        Assert.Empty(target.Text);
    }

    [Fact]
    public void CacheUsesSourceIdentityVersionRangeAndProcessorAndCopiesExports()
    {
        var cache = new LyricsVocalCache();
        float[][] source = [new float[4 * 48000]];
        var stem = new AudioDocument([Enumerable.Repeat(.25f, 2 * 44100).ToArray(), new float[2 * 44100]], 44100, 32);
        cache.Store(source, 48000, 3, 48000, 96000, false, "engine", stem);
        var slice = Assert.IsType<LyricsVocalCache.Slice>(cache.Find(source, 48000, 3, 72000, 24000, false, "engine"));
        Assert.Equal(22050, slice.Start);
        Assert.Equal(22050, slice.Count);
        var exported = slice.ToDocument("Song.wav", true);
        exported.Channels[0][0] = .75f;
        Assert.Equal(.25f, slice.Channels[0][slice.Start]);
        Assert.Null(exported.FilePath);
        Assert.True(exported.RequiresSaveAs);
        Assert.Null(cache.Find(source, 48000, 4, 72000, 24000, false, "engine"));
        Assert.Null(cache.Find(source, 48000, 3, 72000, 24000, true, "engine"));
        Assert.Null(cache.Find(source, 48000, 3, 72000, 24000, false, "new engine"));
        Assert.Null(cache.Find([new float[4 * 48000]], 48000, 3, 72000, 24000, false, "engine"));
        Assert.Null(cache.Find(source, 48000, 3, 0, 48000, false, "engine"));
        Assert.Null(cache.Find(source, 48000, 3, 120000, 48000, false, "engine"));
        cache.Clear();
        Assert.Null(cache.Find(source, 48000, 3, 72000, 24000, false, "engine"));
    }

    [Fact]
    public void EditingAndClosingTheTabReleaseTheCachedStem()
    {
        Wpf.Run(() =>
        {
            float[][] source = [new float[44100]];
            var audio = new AudioDocument(source, 44100, 32);
            var doc = new DocumentViewModel(audio);
            var stem = new AudioDocument([new float[44100], new float[44100]], 44100, 32);
            doc.LyricsVocals.Store(source, 44100, 0, 0, 44100, false, "engine", stem);
            audio.ReplaceRange(0, 1, [new float[1]], "Edit");
            Assert.Null(doc.LyricsVocals.Find(source, 44100, 0, 0, 44100, false, "engine"));
            source = audio.Channels.ToArray();
            doc.LyricsVocals.Store(source, 44100, audio.EditVersion, 0, 44100, false, "engine", stem);
            doc.Unhook();
            Assert.Null(doc.LyricsVocals.Find(source, 44100, audio.EditVersion, 0, 44100, false, "engine"));
        });
    }
}
