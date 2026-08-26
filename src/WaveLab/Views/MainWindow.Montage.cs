using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Montage;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>
/// The montage tab: showing it, editing the selected clip, and rendering it out.
/// </summary>
/// <remarks>
/// Split from the main partial because none of it touches the waveform editor. The montage panel is
/// a sibling of the editor grid that covers the same area, so switching to a montage tab is a
/// visibility change rather than a re-layout.
/// </remarks>
public partial class MainWindow
{
    private bool _updatingMontageInspector;

    /// <summary>Shows the montage panel or the editor, whichever the active tab calls for.</summary>
    private void ApplyMontageVisibility()
    {
        bool montage = _vm.ActiveMontage != null;
        montagePanel.Visibility = montage ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.ActiveMontage is { } vm)
        {
            montageSnapBtn.IsChecked = vm.SnapToZeroCrossing;
            montageMoveBtn.IsChecked = vm.Tool == MontageTool.Move;
            montageTrimBtn.IsChecked = vm.Tool == MontageTool.Trim;
            montageSplitBtn.IsChecked = vm.Tool == MontageTool.Split;
            if (vm.Montage.Length > 0 && vm.SamplesPerPixel <= 1) vm.ZoomFull();
        }
        RefreshMontageChrome();
    }

    /// <summary>Creates an empty montage in a new tab.</summary>
    private void OnNewMontage(object sender, RoutedEventArgs e)
    {
        var montage = new MontageDocument(44_100, 2) { Title = "Untitled montage" };
        _vm.AddMontage(montage);
        ApplyMontageVisibility();
        _vm.ReportAction("New montage created. Use Add Clip to place audio on the lane.");
    }

    private void OnOpenMontage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Open montage",
            Filter = "Deep Groove montage|*" + MontageStore.Extension + "|All files|*.*",
        };
        if (picker.ShowDialog(this) != true) return;

        try
        {
            MontageLoadResult loaded = MontageStore.Load(picker.FileName);
            _vm.AddMontage(loaded.Montage);
            ApplyMontageVisibility();

            _vm.ReportAction(loaded.MissingSources.Count == 0
                ? $"Opened {loaded.Montage.Title}."
                : $"Opened {loaded.Montage.Title}; {loaded.MissingSources.Count} source file(s) " +
                  "could not be found and their clips are silent.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open montage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveMontage(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveMontage is not { } vm) return;

        string? path = vm.Montage.FilePath;
        if (path == null)
        {
            var picker = new SaveFileDialog
            {
                Title = "Save montage",
                Filter = "Deep Groove montage|*" + MontageStore.Extension,
                FileName = MontageViewModel.SuggestedFileName(vm.Montage),
            };
            if (picker.ShowDialog(this) != true) return;
            path = picker.FileName;
        }

        try
        {
            MontageStore.Save(vm.Montage, path);
            vm.MarkSaved();
            _vm.ReportAction($"Saved {vm.Montage.Title}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save montage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── the toolbar ──────────────────────────────────────────────

    private void OnMontageTool(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveMontage is not { } vm) return;

        vm.Tool = ReferenceEquals(sender, montageTrimBtn) ? MontageTool.Trim
            : ReferenceEquals(sender, montageSplitBtn) ? MontageTool.Split
            : MontageTool.Move;

        montageMoveBtn.IsChecked = vm.Tool == MontageTool.Move;
        montageTrimBtn.IsChecked = vm.Tool == MontageTool.Trim;
        montageSplitBtn.IsChecked = vm.Tool == MontageTool.Split;
    }

    private void OnMontageSnap(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveMontage is { } vm) vm.SnapToZeroCrossing = montageSnapBtn.IsChecked == true;
    }

    private async void OnMontageAddClip(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveMontage is not { } vm) return;

        var picker = new OpenFileDialog
        {
            Title = "Add audio to the montage",
            Filter = AudioImporter.OpenFilter,
            Multiselect = true,
        };
        if (picker.ShowDialog(this) != true) return;

        string[] paths = picker.FileNames;
        MontageDocument montage = vm.Montage;
        try
        {
            // Loading resamples, so it runs off the UI thread. The clips are placed afterwards, on
            // the UI thread, because the lane is bound to the collection they go into.
            var loaded = await Task.Run(() =>
            {
                var sources = new List<MontageSource>();
                foreach (string path in paths)
                    sources.Add(MontageSource.Load(path, montage.SampleRate, montage.ChannelCount));
                return sources;
            });

            foreach (MontageSource source in loaded)
            {
                int index = montage.AddSource(source);
                vm.Selected = montage.Append(index);
            }
            vm.Touch();
            if (montage.Clips.Count == loaded.Count) vm.ZoomFull();
            RefreshMontageChrome();

            int resampled = loaded.Count(s => s.WasResampled);
            _vm.ReportAction(resampled == 0
                ? $"Added {loaded.Count} clip(s)."
                : $"Added {loaded.Count} clip(s); {resampled} were brought onto the montage's rate.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Add clip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnMontageRender(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveMontage is not { } vm) return;

        var dialog = new MontageRenderDialog(vm, _vm.Engine.Master) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Rendered is not { } rendered) return;

        MontageRenderResult? result = dialog.Result;
        string measured = result == null ? "" :
            $" Peak {result.PeakDb:0.0} dBFS" +
            (result.Crossfades > 0
                ? $", {result.Crossfades} crossfade(s) at a mean correlation of {result.MeanCorrelation:0.00}."
                : ".");

        switch (dialog.Destination)
        {
            case MontageDestination.NewTab:
                _vm.AddDocument(rendered);
                _vm.ReportAction($"Montage rendered into a new tab, \"{rendered.Title}\".{measured}");
                break;

            case MontageDestination.File:
                _vm.ReportAction($"Montage rendered to a file.{measured}");
                break;

            case MontageDestination.CdPackage:
            case MontageDestination.DdpImage:
            {
                // The rendered programme opens as a tab first, because the CD dialog works on a
                // document and the user should be able to hear and check what is about to be cut.
                _vm.AddDocument(rendered);
                if (_vm.ActiveDocument is { } document)
                {
                    foreach (CdTrackPlan plan in dialog.TrackPlan())
                    {
                        document.Regions.Add(new NamedRegion
                        {
                            Name = plan.Title,
                            Start = plan.SourceStart,
                            End = plan.SourceEnd,
                            CdTrackOrder = document.Regions.Count + 1,
                        });
                    }
                    document.NotifyMarkersChanged();
                    // Names the tab and says where the writing happens: neither destination writes
                    // anything itself, and the folder is asked for by the CD window's Export.
                    _vm.ReportAction(
                        $"Rendered into a new tab, \"{document.Title}\", with one region per clip. " +
                        "Prepare Audio CD is open; its Export button writes the package." + measured);
                    CdTransferDialog.ShowFor(document, _vm, this);
                }
                break;
            }
        }
    }

    // ── the inspector ────────────────────────────────────────────

    private void OnMontageSelectionChanged(object? sender, EventArgs e) => RefreshMontageChrome();

    private void RefreshMontageChrome()
    {
        if (_vm.ActiveMontage is not { } vm)
        {
            montageClipPanel.Visibility = Visibility.Collapsed;
            return;
        }

        montageReadout.Text = Describe(vm);
        montageStatus.Text = vm.Summary;
        montageOverview.InvalidateVisual();

        MontageClip? clip = vm.Selected;
        montageClipPanel.Visibility = clip == null ? Visibility.Collapsed : Visibility.Visible;
        montageClipTitle.Text = clip == null ? "NO CLIP SELECTED" : "CLIP";
        if (clip == null) return;

        _updatingMontageInspector = true;
        try
        {
            int rate = vm.Montage.SampleRate;
            montageClipName.Text = clip.Name;
            montageClipSource.Text = vm.Montage.Sources[clip.SourceIndex].Name;
            montageClipIn.Text = TimeFormat.Position(clip.SourceStart, rate);
            montageClipLength.Text = TimeFormat.Position(clip.Length, rate);
            montageClipAt.Text = TimeFormat.Position(clip.TimelineStart, rate);

            montageClipGain.Value = Math.Clamp(clip.GainDb, -24, 12);
            montageClipGainText.Text = $"{clip.GainDb:+0.0;-0.0;0.0} dB";

            SetShape(clip.FadeInShape, fadeInLin, fadeInPow, fadeInS, fadeInDb);
            SetShape(clip.FadeOutShape, fadeOutLin, fadeOutPow, fadeOutS, fadeOutDb);
            montageFadeInLabel.Text = $"FADE IN · {Milliseconds(clip.FadeInSamples, rate)}";
            montageFadeOutLabel.Text = $"FADE OUT · {Milliseconds(clip.FadeOutSamples, rate)}";
        }
        finally { _updatingMontageInspector = false; }

        RefreshCrossfadePanel(vm);
    }

    private void RefreshCrossfadePanel(MontageViewModel vm)
    {
        MontageCrossfadeInfo? crossfade = vm.SelectedCrossfade;
        montageCrossfadePanel.Visibility = crossfade == null ? Visibility.Collapsed : Visibility.Visible;
        if (crossfade == null) return;

        montageCrossfadeLabel.Text = $"CROSSFADE OUT → {crossfade.NextClip.ToUpperInvariant()}";
        montageCrossfadeCurve.Correlation = crossfade.Correlation;
        montageCrossfadeCurve.Shape = crossfade.Shape;
        montageCrossfadeCurve.Correlation = crossfade.EffectiveCorrelation;
        // Adding zero, so a measurement a hair below it does not read as "-0.000".
        montageCorrelation.Text = (crossfade.Correlation + 0).ToString("0.000");
        montageCrossfadeLength.Text = $"{crossfade.OverlapSeconds:0.000} s";

        // The pill states what was measured, and the note states what the other law would have cost.
        // A number on its own does not tell anyone whether it mattered.
        double error = crossfade.FixedLawErrorDb;
        montageLawText.Text = crossfade.Cancels
            ? "polarity — check this join"
            : $"measured · {crossfade.LawName}";
        montageLawText.Foreground = crossfade.Cancels
            ? (System.Windows.Media.Brush)FindResource("Amber")
            : (System.Windows.Media.Brush)FindResource("Accent");
        montageLawPill.BorderBrush = crossfade.Cancels
            ? (System.Windows.Media.Brush)FindResource("Amber")
            : (System.Windows.Media.Brush)FindResource("AccentDim");

        montageCrossfadeNote.Text = crossfade.Cancels
            ? "The two sides partly cancel. No pair of fades holds the level through that, so the law "
              + "falls back to equal power — the fix is the polarity, not the crossfade."
            : Math.Abs(error) < 0.2
                ? "Either law would be close here."
                : $"A fixed {(crossfade.EffectiveCorrelation < 0.5 ? "equal-gain" : "equal-power")} law "
                  + $"would be {Math.Abs(error):0.0} dB out at this join.";
    }

    private static string Milliseconds(int samples, int rate) =>
        rate <= 0 || samples <= 0 ? "none" : $"{samples * 1000.0 / rate:0} ms";

    private static string Describe(MontageViewModel vm)
    {
        int crossfades = 0, gaps = 0;
        var clips = vm.Montage.Clips;
        for (int i = 0; i + 1 < clips.Count; i++)
        {
            if (MontageDocument.Overlap(clips[i], clips[i + 1]) > 0) crossfades++;
            else if (clips[i + 1].TimelineStart > clips[i].TimelineEnd) gaps++;
        }
        return $"{clips.Count} clip(s) · {crossfades} crossfade(s) · {gaps} gap(s) · {vm.DescribeLength()}";
    }

    private static void SetShape(FadeShape shape, ToggleButton linear, ToggleButton power,
        ToggleButton curve, ToggleButton decibel)
    {
        linear.IsChecked = shape == FadeShape.Linear;
        power.IsChecked = shape == FadeShape.EqualPower;
        curve.IsChecked = shape == FadeShape.SCurve;
        decibel.IsChecked = shape == FadeShape.DecibelLinear;
    }

    private static FadeShape ShapeOf(object sender, ToggleButton linear, ToggleButton power,
        ToggleButton curve) =>
        ReferenceEquals(sender, linear) ? FadeShape.Linear
        : ReferenceEquals(sender, power) ? FadeShape.EqualPower
        : ReferenceEquals(sender, curve) ? FadeShape.SCurve
        : FadeShape.DecibelLinear;

    private void OnMontageClipNameChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingMontageInspector || _vm.ActiveMontage is not { Selected: { } clip } vm) return;
        string name = montageClipName.Text?.Trim() ?? "";
        if (name.Length == 0 || string.Equals(name, clip.Name, StringComparison.Ordinal)) return;

        clip.Name = name;
        vm.Touch(structural: false);
        RefreshMontageChrome();
    }

    private void OnMontageGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMontageInspector || _vm?.ActiveMontage is not { Selected: { } clip } vm) return;

        clip.GainDb = Math.Round(e.NewValue, 1);
        montageClipGainText.Text = $"{clip.GainDb:+0.0;-0.0;0.0} dB";
        vm.Touch(structural: false);
    }

    private void OnFadeInShape(object sender, RoutedEventArgs e)
    {
        if (_updatingMontageInspector || _vm.ActiveMontage is not { Selected: { } clip } vm) return;

        clip.FadeInShape = ShapeOf(sender, fadeInLin, fadeInPow, fadeInS);
        SetShape(clip.FadeInShape, fadeInLin, fadeInPow, fadeInS, fadeInDb);
        vm.Touch(structural: false);
        RefreshCrossfadePanel(vm);
    }

    private void OnFadeOutShape(object sender, RoutedEventArgs e)
    {
        if (_updatingMontageInspector || _vm.ActiveMontage is not { Selected: { } clip } vm) return;

        clip.FadeOutShape = ShapeOf(sender, fadeOutLin, fadeOutPow, fadeOutS);
        SetShape(clip.FadeOutShape, fadeOutLin, fadeOutPow, fadeOutS, fadeOutDb);
        vm.Touch(structural: false);
    }
}
