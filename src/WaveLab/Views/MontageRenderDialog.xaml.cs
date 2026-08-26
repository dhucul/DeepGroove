using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Montage;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>Where a montage is being rendered to.</summary>
public enum MontageDestination { NewTab, File, CdPackage, DdpImage }

/// <summary>
/// Renders a montage, to a tab, a file, or the CD/DDP path Phase 3 already provides.
/// </summary>
/// <remarks>
/// The dialog renders and hands the result back rather than acting on it. Opening a tab, writing a
/// file and driving the CD transfer dialog are all the main window's business, and a dialog that did
/// them would be a second copy of each.
/// </remarks>
public partial class MontageRenderDialog : Window
{
    private readonly MontageViewModel _montage;
    private readonly MasterSection? _master;
    private CancellationTokenSource? _operation;
    private bool _busy;

    public MontageRenderDialog(MontageViewModel montage, MasterSection? master = null)
    {
        ArgumentNullException.ThrowIfNull(montage);
        InitializeComponent();
        _montage = montage;
        _master = master;

        renderRackCheck.IsChecked = false;
        renderRackCheck.IsEnabled = master != null;
        subtitleText.Text = montage.Montage.Title;
        Describe();
        Closing += OnDialogClosing;
    }

    /// <summary>What the user chose.</summary>
    public MontageDestination Destination { get; private set; } = MontageDestination.NewTab;

    /// <summary>
    /// The rendered audio, once Render has succeeded, and null whenever it has not. This — rather
    /// than <c>DialogResult</c> — is what says the render worked: the caller was already testing it,
    /// and a window that only reports success through <c>DialogResult</c> can only ever be shown
    /// modally, which put the whole render path out of reach of a test.
    /// </summary>
    public AudioDocument? Rendered { get; private set; }

    /// <summary>What the render measured, for the caller to report.</summary>
    public MontageRenderResult? Result { get; private set; }

    /// <summary>
    /// What the rendered document is called in the tab strip.
    /// </summary>
    /// <remarks>
    /// Named apart from the montage it came from, because both live in that strip at once. A render
    /// carrying the montage's own title put a second "Side A" beside the first, which is
    /// indistinguishable from the render having done nothing at all — and Render &amp; Prepare CD
    /// was reported as exactly that: the CD window opened and nothing else appeared to change. The
    /// <c>(suffix).wav</c> shape is the one the channel and sample-rate tools already use.
    /// </remarks>
    public static string RenderedTitle(string? montageTitle) =>
        $"{(string.IsNullOrWhiteSpace(montageTitle) ? "Montage" : montageTitle.Trim())} (render).wav";

    /// <summary>
    /// One CD track per clip, in lane order, for the CD and DDP destinations.
    /// </summary>
    /// <remarks>
    /// A montage <em>is</em> an ordered set of ranges, which is exactly what the CD packager takes —
    /// so the ranges here are over the <em>rendered</em> programme, not over any source. A clip's
    /// track therefore starts where the clip starts on the timeline, and a crossfaded pair hands the
    /// boundary to the incoming clip: the overlap belongs to the track it leads into, which is where
    /// a listener would say the next track begins.
    /// </remarks>
    public List<CdTrackPlan> TrackPlan()
    {
        var plans = new List<CdTrackPlan>();
        var clips = _montage.Montage.Clips;
        int length = _montage.Montage.Length;

        for (int i = 0; i < clips.Count; i++)
        {
            int start = clips[i].TimelineStart;
            int end = i + 1 < clips.Count ? clips[i + 1].TimelineStart : length;
            if (i > 0) start = Math.Max(start, plans[^1].SourceEnd);
            if (end <= start) continue;

            plans.Add(new CdTrackPlan(start, Math.Min(end, length), clips[i].Name));
        }
        return plans;
    }

    private void Describe()
    {
        MontageDocument montage = _montage.Montage;
        int crossfades = 0;
        for (int i = 0; i + 1 < montage.Clips.Count; i++)
            if (MontageDocument.Overlap(montage.Clips[i], montage.Clips[i + 1]) > 0) crossfades++;

        lengthText.Text = TimeFormat.Compact(montage.Duration);
        clipsText.Text = montage.Clips.Count.ToString();
        crossfadeText.Text = crossfades == 0 ? "none" : crossfades.ToString();
        sourcesText.Text = $"{montage.Sources.Count} file(s)";

        var errors = montage.Validate()
            .Where(i => i.Severity == MontageIssueSeverity.Error).ToList();
        renderBtn.IsEnabled = errors.Count == 0;
        statusText.Text = errors.Count > 0
            ? errors[0].Message
            : "Ready.";
    }

    private void OnDestinationChanged(object sender, RoutedEventArgs e)
    {
        Destination =
            ReferenceEquals(sender, fileBtn) ? MontageDestination.File
            : ReferenceEquals(sender, cdBtn) ? MontageDestination.CdPackage
            : ReferenceEquals(sender, ddpBtn) ? MontageDestination.DdpImage
            : MontageDestination.NewTab;

        newTabBtn.IsChecked = Destination == MontageDestination.NewTab;
        fileBtn.IsChecked = Destination == MontageDestination.File;
        cdBtn.IsChecked = Destination == MontageDestination.CdPackage;
        ddpBtn.IsChecked = Destination == MontageDestination.DdpImage;

        renderBtn.Content = Destination switch
        {
            MontageDestination.File => "Render…",
            MontageDestination.CdPackage => "Render & Prepare CD…",
            MontageDestination.DdpImage => "Render & Prepare DDP…",
            _ => "Render",
        };

        noteText.Text = Destination is MontageDestination.CdPackage or MontageDestination.DdpImage
            ? "One CD track per clip, in lane order. A crossfaded boundary belongs to the track it leads into, "
              + "which is where a listener would say the next track begins. The CD dialog opens next so the "
              + "running order and catalogue numbers can be checked before anything is written."
            : "Clips are placed at their timeline positions and each source is read once, so a clip used twice "
              + "is not resampled twice. The peak is reported rather than limited: overlapping clips can sum "
              + "past full scale, and that is a decision for you, not for the renderer.";
    }

    private async void OnRenderClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string? filePath = null;
        if (Destination == MontageDestination.File)
        {
            var picker = new SaveFileDialog
            {
                Filter = "WAV — 32-bit float|*.wav|WAV — 24-bit PCM|*.wav|" +
                         "WAV — 16-bit PCM (dithered)|*.wav|AIFF — 24-bit PCM|*.aiff",
                FileName = SafeName(_montage.Montage.Title),
                DefaultExt = ".wav",
            };
            if (picker.ShowDialog(this) != true) return;
            filePath = picker.FileName;
        }

        SetBusy(true, "Rendering the montage…");
        _operation = new CancellationTokenSource();
        try
        {
            CancellationToken token = _operation.Token;
            MontageDocument montage = _montage.Montage;
            bool renderRack = renderRackCheck.IsChecked == true && _master != null;
            double renderShare = renderRack ? 0.7 : 1.0;

            var progress = new Progress<double>(f =>
            {
                progressBar.Value = f * renderShare;
                statusText.Text = $"Rendering the montage — {f:P0}";
            });

            MontageRenderResult result = await Task.Run(
                () => MontageRenderer.Render(montage, token, progress), token);

            float[][] audio = result.Channels;
            if (renderRack && _master != null)
            {
                var rackProgress = new Progress<double>(f =>
                {
                    progressBar.Value = renderShare + f * (1 - renderShare);
                    statusText.Text = $"Rendering the master rack — {f:P0}";
                });
                audio = await Task.Run(
                    () => _master.ProcessOffline(audio, montage.SampleRate, token, rackProgress),
                    token);
            }

            Rendered = new AudioDocument(audio, montage.SampleRate, sourceBitDepth: 32)
            {
                Title = RenderedTitle(montage.Title),
            };
            Result = result;
            progressBar.Value = 1;

            if (filePath != null)
            {
                int depth = Path.GetExtension(filePath)
                    .Equals(".aiff", StringComparison.OrdinalIgnoreCase) ? 24 : 32;
                await Task.Run(() => WavCodec.Save(Rendered, filePath, depth, dither: depth == 16,
                    cancellationToken: token), token);
            }

        }
        catch (OperationCanceledException) { statusText.Text = "Render cancelled."; Rendered = null; }
        catch (Exception ex)
        {
            Rendered = null;
            MessageBox.Show(ex.Message, "Render failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            statusText.Text = "Nothing was rendered.";
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
        }

        // Outside the try, and after SetBusy has cleared _busy. Closing from inside it ran while the
        // window still considered itself busy, and OnDialogClosing cancels a close in that state to
        // protect a render in flight — so the window stayed open, ShowDialog never returned, and
        // everything the caller does with the result never happened. Rendering to a file looked like
        // it worked because the file is written before this point; every other destination does its
        // visible work after ShowDialog returns, so all of them looked dead.
        if (Rendered != null) Close();
    }

    private static string SafeName(string value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "Montage" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        foreach (ToggleButton button in new[] { newTabBtn, fileBtn, cdBtn, ddpBtn })
            button.IsEnabled = !busy;
        renderRackCheck.IsEnabled = !busy && _master != null;
        renderBtn.IsEnabled = !busy;
        closeBtn.Content = busy ? "Cancel" : "Close";
        statusText.Text = status;
        if (!busy) progressBar.Value = 0;
    }

    private void OnDialogClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        _operation?.Cancel();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy) _operation?.Cancel();
        else Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
