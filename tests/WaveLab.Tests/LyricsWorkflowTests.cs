using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class LyricsWorkflowTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("stop")]
    [InlineData("clear")]
    [InlineData("close")]
    public void RestartWaitsWithoutBlockingAndCanBeCancelledBeforeAudioStarts(string action)
    {
        Wpf.Run(() =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new AudioDocument([new float[3 * 44100]], 44100, 32);
            var doc = new DocumentViewModel(source) { LyricsTranscript = new LyricsTranscript
                { Lines = [new LyricsLine { Start = 1, End = 2, Text = "keep these words" }] } };
            int requests = 0, starts = 0;
            bool finished = false;
            var window = new LyricsDialog(doc, (_, _) => true, () => { }, restart: async (audio, loop, token) =>
            {
                requests++;
                try
                {
                    await gate.Task.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    Assert.Equal(source.Length, audio.Length);
                    Assert.Same(source.Channels[0], audio.Channels[0]);
                    starts++;
                    return true;
                }
                finally { finished = true; }
            });
            Wpf.Show(window, dialog =>
            {
                var restart = (Button)dialog.FindName("restartAudioButton");
                ((Button)dialog.FindName("playAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, requests);
                Assert.False(finished);
                Assert.False(restart.IsEnabled);
                Assert.False(((Button)dialog.FindName("playAllButton")).IsEnabled);
                Assert.True(((Button)dialog.FindName("stopButton")).IsEnabled);
                Assert.False(((DataGrid)dialog.FindName("linesGrid")).IsReadOnly);
                restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, requests);
                bool responsive = false;
                dialog.Dispatcher.BeginInvoke(new Action(() => responsive = true));
                Wpf.Pump();
                Assert.True(responsive);
                if (action == "close") return; // Wpf.Show closes the dialog while the wait is pending.
                if (action == "complete") gate.TrySetResult();
                else ((Button)dialog.FindName(action == "stop" ? "stopButton" : "clearButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForRestart(() => finished && restart.IsEnabled);
                Assert.Equal(action == "complete" ? 1 : 0, starts);
                if (action == "complete") Assert.Contains("from the beginning", ((TextBlock)dialog.FindName("statusLabel")).Text);
                if (action == "stop") Assert.Contains("cancelled", ((TextBlock)dialog.FindName("statusLabel")).Text);
                if (action == "clear")
                {
                    Assert.Null(doc.LyricsTranscript);
                    Assert.Contains("cleared", ((TextBlock)dialog.FindName("statusLabel")).Text);
                }
            });
            WaitForRestart(() => finished);
            gate.TrySetResult();
            Wpf.Pump();
            Assert.Equal(action == "complete" ? 1 : 0, starts);
            Assert.False(source.Dirty);
            doc.Unhook();
        });
    }

    private static void WaitForRestart(Func<bool> complete)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (!complete() && Environment.TickCount64 < deadline) { Wpf.Pump(); Thread.Sleep(5); }
        Assert.True(complete());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeviceReleaseDelaysUseTheStatusLineAndLeavePlaybackRetryable(bool replayLine)
    {
        Wpf.Run(() =>
        {
            var line = new LyricsLine { Start = 0, End = 1, Text = "keep my correction" };
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32))
                { LyricsTranscript = new LyricsTranscript { Lines = [line] } };
            var transcript = doc.LyricsTranscript;
            bool deviceReady = false;
            int attempts = 0;
            Wpf.Show(new LyricsDialog(doc, (_, _) =>
            {
                attempts++;
                if (!deviceReady) throw new PlaybackDeviceBusyException();
                return true;
            }), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedItem = line;
                var play = (Button)window.FindName(replayLine ? "playButton" : "playAllButton");
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Try again shortly", ((TextBlock)window.FindName("statusLabel")).Text);
                Assert.True(play.IsEnabled);
                Assert.Same(transcript, doc.LyricsTranscript);
                Assert.Equal("keep my correction", line.Text);
                deviceReady = true;
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, attempts);
                Assert.DoesNotContain("Try again shortly", ((TextBlock)window.FindName("statusLabel")).Text);
            });
            doc.Unhook();
        });
    }

    [Fact]
    public void StopKeepsTheWholeAudioPositionAndRestartExplicitlyResetsIt()
    {
        Wpf.Run(() =>
        {
            var source = new AudioDocument([new float[5 * 44100]], 44100, 32);
            var doc = new DocumentViewModel(source);
            AudioDocument? active = null;
            bool paused = false, resumedLoop = false;
            int position = 0, starts = 0, resumes = 0, releases = 0;
            var dialog = new LyricsDialog(doc,
                (audio, loop) => { active = audio; paused = false; position = 0; starts++; return true; },
                () => { active = null; paused = false; releases++; },
                pause: audio => { if (!ReferenceEquals(audio, active)) return false; paused = true; return true; },
                resume: (audio, loop) =>
                {
                    if (!ReferenceEquals(audio, active) || !paused) return false;
                    paused = false;
                    resumedLoop = loop;
                    resumes++;
                    return true;
                });
            Wpf.Show(dialog, window =>
            {
                var play = (Button)window.FindName("playAllButton");
                var stop = (Button)window.FindName("stopButton");
                var restart = (Button)window.FindName("restartAudioButton");
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                position = 12345;
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(12345, position);
                Assert.True(paused);
                Assert.Equal("Continue audio", play.Content);
                Assert.Equal(0, releases);
                window.Width = 920;
                window.Height = 740;
                window.UpdateLayout();
                Assert.True(restart.ActualWidth > 50);
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_RESUME_RENDER");
                if (!string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 740, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(renderPath);
                    png.Save(stream);
                }
                ((CheckBox)window.FindName("loopCheck")).IsChecked = true;
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(12345, position);
                Assert.Equal(1, starts);
                Assert.Equal(1, resumes);
                Assert.True(resumedLoop);
                Assert.False(paused);
                position = 27182;
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var previousPreview = active;
                restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotSame(previousPreview, active);
                Assert.Equal(0, position);
                Assert.Equal(2, starts);
                Assert.Equal(1, resumes);
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("clearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(active);
                Assert.False(paused);
                Assert.Equal("Play whole audio", play.Content);
            });
            Assert.Equal(2, releases); // Clear and Close release even a paused stream.
            Assert.False(source.Dirty);
            doc.Unhook();
        });
    }

    [Fact]
    public void UnavailableResumeDoesNotSilentlyRestartTheRecording()
    {
        Wpf.Run(() =>
        {
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32));
            int starts = 0;
            Wpf.Show(new LyricsDialog(doc, (_, _) => { starts++; return true; }, pause: _ => true,
                resume: (_, _) => false), window =>
            {
                var play = (Button)window.FindName("playAllButton");
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("stopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, starts);
                Assert.Contains("no longer available", ((TextBlock)window.FindName("statusLabel")).Text);
                ((Button)window.FindName("restartAudioButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, starts);
            });
            doc.Unhook();
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ContextMenuEditsTheClickedLinesWordsInsteadOfThePreviouslySelectedLine(int clickedColumn)
    {
        Wpf.Run(() =>
        {
            var first = new LyricsLine { Start = 0, End = 1, Text = "first line" };
            var second = new LyricsLine { Start = 1, End = 2, Text = "second line", ModelText = "original evidence" };
            var source = new AudioDocument([new float[3 * 44100]], 44100, 32);
            var doc = new DocumentViewModel(source)
                { LyricsTranscript = new LyricsTranscript { Lines = [first, second] } };
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                var grid = (DataGrid)window.FindName("linesGrid");
                grid.SelectedItem = first;
                grid.CurrentCell = new DataGridCellInfo(first, grid.Columns[clickedColumn]);
                grid.ScrollIntoView(second);
                grid.UpdateLayout();
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(second));
                var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
                menu.PlacementTarget = row;
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                var edit = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
                Assert.Equal("Edit line", edit.Header);
                Assert.True(edit.IsEnabled);
                Assert.Same(second, grid.SelectedItem);
                edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Wpf.Pump();
                var words = (DataGridTextColumn)window.FindName("wordsColumn");
                Assert.Same(second, grid.CurrentCell.Item);
                Assert.Same(words, grid.CurrentCell.Column);
                var editor = Assert.IsType<TextBox>(words.GetCellContent(second));
                Assert.True(editor.IsKeyboardFocused);
                editor.SelectAll();
                editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, editor, "corrected second line"))
                    { RoutedEvent = TextCompositionManager.TextInputEvent });
                Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell, true));
                Assert.True(grid.CommitEdit(DataGridEditingUnit.Row, true));
                Assert.Equal("first line", first.Text);
                Assert.Equal("corrected second line", second.Text);
                Assert.Equal("original evidence", second.ModelText);
                Assert.Equal(1, second.Start);
                Assert.Equal(2, second.End);
            });
            Assert.False(source.Dirty);
            Assert.Equal(0, source.EditVersion);
            doc.Unhook();
        });
    }

    [Fact]
    public void ClearingTheTranscriptBeforeAQueuedMenuEditCannotBringBackAnOldLine()
    {
        Wpf.Run(() =>
        {
            var line = new LyricsLine { Start = 0, End = 1, Text = "old line" };
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32))
                { LyricsTranscript = new LyricsTranscript { Lines = [line] } };
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                var grid = (DataGrid)window.FindName("linesGrid");
                grid.ScrollIntoView(line);
                grid.UpdateLayout();
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(line));
                var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
                menu.PlacementTarget = row;
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ((Button)window.FindName("clearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wpf.Pump();
                Assert.Null(doc.LyricsTranscript);
                Assert.Empty(grid.Items);
                Assert.Null(grid.SelectedItem);
            });
            doc.Unhook();
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WholeAudioPlaybackIgnoresSelectionsAndNeedsNoCurrentTranscript(bool staleTranscript, bool loop)
    {
        Wpf.Run(() =>
        {
            float[][] samples = [Enumerable.Repeat(.25f, 5 * 44100).ToArray(), Enumerable.Repeat(-.125f, 5 * 44100).ToArray()];
            var source = new AudioDocument(samples, 44100, 32) { Title = "Whole recording.wav" };
            var doc = new DocumentViewModel(source);
            doc.SetSelection(44100, 2 * 44100);
            if (staleTranscript)
                doc.LyricsTranscript = new LyricsTranscript { SourceEditVersion = -1,
                    Lines = [new LyricsLine { Start = 1, End = 2, Text = "keep this correction" }] };
            var transcript = doc.LyricsTranscript;
            AudioDocument? played = null;
            bool? repeated = null;
            int stops = 0;
            Wpf.Show(new LyricsDialog(doc, (audio, repeat) => { played = audio; repeated = repeat; return true; }, () => stops++), window =>
            {
                var playAll = (Button)window.FindName("playAllButton");
                Assert.True(playAll.IsEnabled);
                Assert.True(((CheckBox)window.FindName("selectionCheck")).IsChecked);
                ((CheckBox)window.FindName("loopCheck")).IsChecked = loop;
                playAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(played);
                Assert.Equal(source.Length, played.Length);
                Assert.Equal(source.SampleRate, played.SampleRate);
                Assert.Equal(2, played.ChannelCount);
                Assert.Same(samples[0], played.Channels[0]);
                Assert.Same(samples[1], played.Channels[1]);
                Assert.Equal(loop, repeated);
                Assert.Same(transcript, doc.LyricsTranscript);
                Assert.False(((DataGrid)window.FindName("linesGrid")).IsReadOnly);
                ((Button)window.FindName("stopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, stops);
                window.Width = 920;
                window.Height = 740;
                window.UpdateLayout();
                foreach (string name in new[] { "playAllButton", "restartAudioButton", "playButton", "stopButton", "alternativeButton", "copyButton", "exportButton" })
                {
                    var control = (Button)window.FindName(name);
                    Assert.True(control.ActualWidth > 40);
                    Assert.True(control.TransformToAncestor(window).Transform(new Point(control.ActualWidth, 0)).X < window.ActualWidth);
                }
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_PLAY_ALL_RENDER");
                if (!staleTranscript && !loop && !string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 740, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(renderPath);
                    png.Save(stream);
                }
            });
            Assert.Equal(2, stops); // Closing the lyrics window also releases playback.
            Assert.False(source.Dirty);
            Assert.Equal(0, source.EditVersion);
            Assert.Equal(44100, doc.SelStart);
            Assert.Equal(2 * 44100, doc.SelEnd);
            doc.Unhook();
        });
    }

    [Fact]
    public void WholeAudioPlaybackReportsAnUnavailablePlayerWithoutLosingLyrics()
    {
        Wpf.Run(() =>
        {
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32))
                { LyricsTranscript = new LyricsTranscript() };
            var transcript = doc.LyricsTranscript;
            Wpf.Show(new LyricsDialog(doc, (_, _) => false), window =>
            {
                ((Button)window.FindName("playAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Playback is unavailable", ((TextBlock)window.FindName("statusLabel")).Text);
                Assert.Same(transcript, doc.LyricsTranscript);
            });
            Wpf.Show(new LyricsDialog(doc), window =>
                Assert.False(((Button)window.FindName("playAllButton")).IsEnabled));
            doc.Unhook();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearEverythingResetsOnlyThisTabsLyricsAndKeepsItsAudio(bool hasSelection)
    {
        Wpf.Run(() =>
        {
            float[][] source = [Enumerable.Repeat(.25f, 3 * 44100).ToArray()];
            var audio = new AudioDocument(source, 44100, 32) { Title = "My recording.wav" };
            audio.MarkUnsaved();
            var doc = new DocumentViewModel(audio)
            {
                LyricsTranscript = new LyricsTranscript { Language = "en", RangeEnd = 3,
                    Lines = [new LyricsLine { Start = 1, End = 2, Text = "my correction",
                        AlternativeText = "old alternative", RetryText = "pending suggestion", PreviousRetryText = "saved wording" }] },
                LyricsSettings = new LyricsOptions(false, true, false, "turbo", "fr", "Élodie", true, false),
                LyricsSelectionOnly = false,
            };
            if (hasSelection) doc.SetSelection(44100, 88200);
            int selectionStart = doc.SelStart, selectionEnd = doc.SelEnd;
            var vocals = new AudioDocument([new float[3 * 44100], new float[3 * 44100]], 44100, 32);
            doc.LyricsVocals.Store(source, 44100, 0, 0, source[0].Length, false, "clear-test", vocals);
            var other = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32))
                { LyricsTranscript = new LyricsTranscript { Lines = [new LyricsLine { Start = 0, End = 1, Text = "keep this" }] } };
            var otherTranscript = other.LyricsTranscript;
            bool stopped = false;
            Wpf.Show(new LyricsDialog(doc, (_, _) => true, () => stopped = true), window =>
            {
                var grid = (DataGrid)window.FindName("linesGrid");
                grid.SelectedIndex = 0;
                ((CheckBox)window.FindName("loopCheck")).IsChecked = true;
                ((ProgressBar)window.FindName("progressBar")).Value = .7;
                var clear = (Button)window.FindName("clearButton");
                Assert.True(clear.IsEnabled); // Clearing also works when no engine is installed.
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(stopped);
                Assert.Null(doc.LyricsTranscript);
                Assert.Null(doc.LyricsSettings);
                Assert.Null(doc.LyricsSelectionOnly);
                Assert.Null(doc.LyricsVocals.Find(source, 44100, 0, 0, source[0].Length, false, "clear-test"));
                Assert.Empty(grid.Items);
                Assert.Null(grid.SelectedItem);
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("emptyLabel")).Visibility);
                Assert.Empty(((TextBox)window.FindName("hintsText")).Text);
                foreach (string name in new[] { "modeCombo", "qualityCombo", "languageCombo", "deviceCombo" })
                    Assert.Equal(0, ((ComboBox)window.FindName(name)).SelectedIndex);
                Assert.Equal(hasSelection, ((CheckBox)window.FindName("selectionCheck")).IsChecked);
                Assert.True(((CheckBox)window.FindName("retryCheck")).IsChecked);
                Assert.True(((CheckBox)window.FindName("compareCheck")).IsChecked);
                Assert.False(((CheckBox)window.FindName("loopCheck")).IsChecked);
                Assert.Equal(0, ((ProgressBar)window.FindName("progressBar")).Value);
                foreach (string name in new[] { "copyButton", "exportButton", "playButton", "alternativeButton",
                    "retryButton", "acceptRetryButton", "restoreRetryButton", "cancelButton" })
                    Assert.False(((Button)window.FindName(name)).IsEnabled);
                Assert.Empty(((TextBlock)window.FindName("retryLabel")).Text);
                Assert.Contains("Singing can be ambiguous", ((TextBlock)window.FindName("summaryLabel")).Text);
                Assert.Contains("cleared", ((TextBlock)window.FindName("statusLabel")).Text);
                window.Width = 920;
                window.Height = 740;
                window.UpdateLayout();
                Assert.True(grid.ActualHeight > 80);
                Assert.True(clear.TransformToAncestor(window).Transform(new Point(clear.ActualWidth, 0)).X < window.ActualWidth);
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_CLEAR_RENDER");
                if (hasSelection && !string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 740, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(renderPath);
                    png.Save(stream);
                }
            });
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                Assert.Null(doc.LyricsTranscript);
                Assert.Empty(((DataGrid)window.FindName("linesGrid")).Items);
                Assert.Empty(((TextBox)window.FindName("hintsText")).Text);
                Assert.Equal(0, ((ComboBox)window.FindName("modeCombo")).SelectedIndex);
                Assert.Equal(hasSelection, ((CheckBox)window.FindName("selectionCheck")).IsChecked);
            });
            Assert.Same(source[0], audio.Channels[0]);
            Assert.Equal(.25f, audio.Channels[0][0]);
            Assert.Equal(3 * 44100, audio.Length);
            Assert.Equal(0, audio.EditVersion);
            Assert.True(audio.Dirty);
            Assert.Equal(selectionStart, doc.SelStart);
            Assert.Equal(selectionEnd, doc.SelEnd);
            Assert.Same(otherTranscript, other.LyricsTranscript);
            Assert.Equal("keep this", other.LyricsTranscript!.Lines[0].Text);
            doc.Unhook();
            other.Unhook();
        });
    }

    [Theory]
    [InlineData(920, 740)]
    [InlineData(1080, 800)]
    public void SpellingHintsAcceptKeyboardInputAndDisplayTheWholeTextLine(int width, int height)
    {
        Wpf.Run(() =>
        {
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32));
            Wpf.Show(new LyricsDialog(doc), window =>
            {
                window.Width = width;
                window.Height = height;
                window.UpdateLayout();
                var hints = (TextBox)window.FindName("hintsText");
                Assert.True(hints.IsEnabled);
                Assert.False(hints.IsReadOnly);
                Assert.Same(hints, Keyboard.Focus(hints));
                hints.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, hints, "Élodie, Rhiannon"))
                    { RoutedEvent = TextCompositionManager.TextInputEvent });
                window.UpdateLayout();
                Assert.Equal("Élodie, Rhiannon", hints.Text);

                var host = (ScrollViewer)hints.Template.FindName("PART_ContentHost", hints);
                var caret = hints.GetRectFromCharacterIndex(0);
                Assert.True(host.ViewportHeight >= caret.Height - .1,
                    "The hints field must leave room to display its text, not merely store it.");
                Assert.True(caret.Top >= 0 && caret.Bottom <= hints.ActualHeight);
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_HINTS_RENDER");
                if (!string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(renderPath + $"-{width}.png");
                    png.Save(stream);
                }
            });
            Assert.Equal("Élodie, Rhiannon", doc.LyricsSettings!.Hints);
            doc.Unhook();
        });
    }

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
