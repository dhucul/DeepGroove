using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class CdTransferDialog : Window
{
    private sealed class TrackRow : ObservableObject
    {
        private string _title;
        private int _order;

        public TrackRow(CdTrackPlan plan, int order, int sampleRate, NamedRegion? sourceRegion = null)
        {
            Plan = plan;
            _title = plan.Title;
            _order = order;
            SampleRate = sampleRate;
            SourceRegion = sourceRegion;
        }

        public CdTrackPlan Plan { get; private set; }
        public NamedRegion? SourceRegion { get; private set; }
        public int SampleRate { get; }
        public string Title { get => _title; set => Set(ref _title, value); }
        public int Order { get => _order; set { if (Set(ref _order, value)) Raise(nameof(OrderText)); } }
        public string OrderText => $"{Order:00}";
        public string StartText => TimeFormat.Position(Plan.SourceStart, SampleRate);
        public string EndText => TimeFormat.Position(Plan.SourceEnd, SampleRate);
        public string DurationText => TimeFormat.Compact(Plan.DurationSeconds(SampleRate));
        public CdTrackPlan ToPlan() => Plan with
        {
            Title = string.IsNullOrWhiteSpace(Title) ? $"Track {Order:00}" : Title.Trim(),
        };

        public void SetRange(int start, int end)
        {
            Plan = Plan with { SourceStart = start, SourceEnd = end };
            Raise(nameof(StartText));
            Raise(nameof(EndText));
            Raise(nameof(DurationText));
        }

        public void BindRegion(NamedRegion region) => SourceRegion = region;
    }

    private readonly DocumentViewModel _document;
    private readonly MainViewModel _main;
    private readonly ObservableCollection<TrackRow> _tracks = [];
    private readonly HashSet<NamedRegion> _knownPlanRegions = new(ReferenceEqualityComparer.Instance);
    private readonly bool _rackWasEnabled;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closeWhenFinished;
    private bool _dialogReady;
    private bool _restoringRack;

    public CdTransferDialog(DocumentViewModel document, MainViewModel main)
    {
        InitializeComponent();
        _document = document;
        _main = main;
        _rackWasEnabled = main.Master.RackEnabled;
        trackList.ItemsSource = _tracks;
        discTitle.Text = Path.GetFileNameWithoutExtension(document.Doc.Title);
        renderRackCheck.IsChecked = _rackWasEnabled;

        var regionTracks = CdTransfer.FromRegionsWithSources(document.Regions, document.Doc.Length);
        if (regionTracks.Count > 0)
            ReplaceTracks(regionTracks.Select(item => item.Plan).ToList(),
                regionTracks.Select(item => item.Source).ToList());
        Loaded += async (_, _) =>
        {
            _dialogReady = true;
            if (_tracks.Count == 0) await SuggestTracksAsync();
            if (_tracks.Count > 0) trackList.SelectedIndex = 0;
        };
        Closing += OnDialogClosing;
        Closed += (_, _) =>
        {
            _operation?.Cancel();
            _main.StopPreview();
            RestoreRackState();
        };
    }

    private void ReplaceTracks(
        IReadOnlyList<CdTrackPlan> plans, IReadOnlyList<NamedRegion>? sourceRegions = null)
    {
        if (sourceRegions != null && sourceRegions.Count != plans.Count)
            throw new ArgumentException("Track plans and source regions must have matching counts.", nameof(sourceRegions));
        foreach (var old in _tracks) old.PropertyChanged -= OnTrackRowChanged;
        _tracks.Clear();
        int order = 1;
        for (int i = 0; i < plans.Count; i++)
        {
            NamedRegion? sourceRegion = sourceRegions?[i];
            if (sourceRegion != null) _knownPlanRegions.Add(sourceRegion);
            var row = new TrackRow(plans[i], order++, _document.Doc.SampleRate, sourceRegion);
            row.PropertyChanged += OnTrackRowChanged;
            _tracks.Add(row);
        }
        UpdatePlan();
    }

    private void OnTrackRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackRow.Title)) UpdatePlan();
    }

    private async void OnAutoSplit(object sender, RoutedEventArgs e) => await SuggestTracksAsync();

    private async Task SuggestTracksAsync()
    {
        if (_busy || _document.Doc.Length == 0) return;
        SetBusy(true, "Analyzing quiet gaps without changing the recording...");
        _operation = new CancellationTokenSource();
        try
        {
            var channels = _document.Doc.Channels.ToArray();
            int rate = _document.Doc.SampleRate;
            double threshold = thresholdSlider.Value;
            var plans = await Task.Run(() => CdTransfer.SuggestTracks(
                channels, rate, threshold, minimumSilenceSeconds: 1.25,
                minimumTrackSeconds: 20, _operation.Token), _operation.Token);
            ReplaceTracks(plans);
            statusText.Text = plans.Count > 1
                ? $"Found {plans.Count} probable tracks. Rename or reorder them before export."
                : "No sustained track gaps were found; one full-length track was proposed.";
        }
        catch (OperationCanceledException) { statusText.Text = "Track analysis cancelled."; }
        catch (Exception ex) { statusText.Text = ex.Message; }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
        }
    }

    private void OnAddSelection(object sender, RoutedEventArgs e)
    {
        if (_busy || !_document.HasSelection) return;
        var row = new TrackRow(new CdTrackPlan(_document.SelStart, _document.SelEnd,
            $"Track {_tracks.Count + 1:00}"), _tracks.Count + 1, _document.Doc.SampleRate);
        row.PropertyChanged += OnTrackRowChanged;
        _tracks.Add(row);
        trackList.SelectedItem = row;
        UpdatePlan();
    }

    private void OnRangeEditCommitted(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not TextBox editor || editor.DataContext is not TrackRow row) return;
        bool editsStart = string.Equals(editor.Name, "startEditor", StringComparison.Ordinal);
        string displayedPosition = editsStart ? row.StartText : row.EndText;
        if (string.Equals(editor.Text?.Trim(), displayedPosition, StringComparison.Ordinal))
        {
            // TimeFormat intentionally displays milliseconds, while region boundaries
            // can carry finer sample precision. Leaving an untouched editor must not
            // quantize that exact boundary merely because the control lost focus.
            editor.Text = displayedPosition;
            return;
        }
        if (!TryParsePosition(editor.Text, row.SampleRate, out int sample))
        {
            editor.Text = displayedPosition;
            statusText.Text = "Enter a position as seconds, mm:ss, or hh:mm:ss.mmm.";
            return;
        }

        int start = editsStart ? sample : row.Plan.SourceStart;
        int end = editsStart ? row.Plan.SourceEnd : sample;
        if (start < 0 || end > _document.Doc.Length || start >= end)
        {
            editor.Text = displayedPosition;
            statusText.Text = "Track In must be before Out, and both must stay inside the recording.";
            return;
        }

        row.SetRange(start, end);
        editor.Text = editsStart ? row.StartText : row.EndText;
        statusText.Text = $"Adjusted track {row.Order:00} to {row.DurationText}.";
        UpdatePlan();
    }

    private static bool TryParsePosition(string? text, int sampleRate, out int sample)
    {
        sample = 0;
        if (sampleRate <= 0 || string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;

        static bool TryNumber(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        static bool TryWholeNumber(string value, out double result) =>
            TryNumber(value, out result) && result == Math.Truncate(result);

        double seconds;
        if (parts.Length == 1)
        {
            if (!TryNumber(parts[0], out seconds)) return false;
        }
        else
        {
            if (!TryNumber(parts[^1], out double secondPart) || secondPart < 0 || secondPart >= 60)
                return false;
            if (!TryWholeNumber(parts[^2], out double minutePart) || minutePart < 0 ||
                (parts.Length == 3 && minutePart >= 60))
                return false;
            double hourPart = 0;
            if (parts.Length == 3 && (!TryWholeNumber(parts[0], out hourPart) || hourPart < 0))
                return false;
            seconds = hourPart * 3600 + minutePart * 60 + secondPart;
        }

        if (!double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue / (double)sampleRate)
            return false;
        sample = (int)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);
        return true;
    }

    private void OnSplitSelected(object sender, RoutedEventArgs e)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        CdTrackPlan plan = row.ToPlan();
        int minimum = Math.Max(1, (int)Math.Ceiling(CdTransfer.MinimumTrackSeconds * row.SampleRate));
        if (plan.Length < minimum * 2)
        {
            statusText.Text = $"Track {row.Order:00} is too short to split into two valid CD tracks.";
            return;
        }

        int split = _document.Cursor > plan.SourceStart && _document.Cursor < plan.SourceEnd
            ? _document.Cursor
            : plan.SourceStart + plan.Length / 2;
        if (split - plan.SourceStart < minimum || plan.SourceEnd - split < minimum)
            split = plan.SourceStart + plan.Length / 2;

        int index = _tracks.IndexOf(row);
        row.SetRange(plan.SourceStart, split);
        var right = new TrackRow(
            new CdTrackPlan(split, plan.SourceEnd, $"{plan.Title} B"), index + 2, row.SampleRate);
        right.PropertyChanged += OnTrackRowChanged;
        _tracks.Insert(index + 1, right);
        RefreshOrder();
        trackList.SelectedItem = right;
        statusText.Text = $"Split at {TimeFormat.Position(split, row.SampleRate)}; edit In/Out fields to fine-tune the boundary.";
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        int from = _tracks.IndexOf(row), to = from + delta;
        if (to < 0 || to >= _tracks.Count) return;
        _tracks.Move(from, to);
        RefreshOrder();
        trackList.SelectedItem = row;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        int index = _tracks.IndexOf(row);
        row.PropertyChanged -= OnTrackRowChanged;
        _tracks.Remove(row);
        RefreshOrder();
        if (_tracks.Count > 0) trackList.SelectedIndex = Math.Min(index, _tracks.Count - 1);
    }

    private void RefreshOrder()
    {
        for (int i = 0; i < _tracks.Count; i++) _tracks[i].Order = i + 1;
        UpdatePlan();
    }

    private void OnSaveRegions(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var existing = _document.Regions.ToList();
        var consumed = new HashSet<NamedRegion>(ReferenceEqualityComparer.Instance);
        var synchronized = new List<NamedRegion>(_tracks.Count + existing.Count);
        int added = 0;
        int updated = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            TrackRow row = _tracks[i];
            CdTrackPlan plan = row.ToPlan();
            NamedRegion? region = row.SourceRegion;
            if (region == null || !existing.Contains(region) || consumed.Contains(region))
            {
                region = new NamedRegion
                {
                    Name = plan.Title,
                    Start = plan.SourceStart,
                    End = plan.SourceEnd,
                    CdTrackOrder = i + 1,
                };
                row.BindRegion(region);
                _knownPlanRegions.Add(region);
                added++;
            }
            else
            {
                consumed.Add(region);
                if (!string.Equals(region.Name, plan.Title, StringComparison.Ordinal) ||
                    region.Start != plan.SourceStart || region.End != plan.SourceEnd ||
                    region.CdTrackOrder != i + 1)
                {
                    region.Name = plan.Title;
                    region.Start = plan.SourceStart;
                    region.End = plan.SourceEnd;
                    region.CdTrackOrder = i + 1;
                    updated++;
                }
            }
            synchronized.Add(region);
        }

        // Former plan regions omitted by Remove or superseded by Analyze are
        // intentionally dropped. Untagged regions that never participated in this
        // dialog remain independent annotations and keep their relative order.
        var preservedRegions = existing.Where(region => !consumed.Contains(region) &&
            !_knownPlanRegions.Contains(region) && region.CdTrackOrder is not > 0).ToList();
        int removed = existing.Count - consumed.Count - preservedRegions.Count;
        synchronized.AddRange(preservedRegions);
        bool reordered = synchronized.Count != _document.Regions.Count ||
            !_document.Regions.SequenceEqual(synchronized, ReferenceEqualityComparer.Instance);
        if (reordered)
        {
            _document.Regions.Clear();
            foreach (var region in synchronized) _document.Regions.Add(region);
        }

        if (added > 0 || updated > 0 || removed > 0 || reordered) _document.NotifyMarkersChanged();
        statusText.Text = added > 0 || updated > 0 || removed > 0 || reordered
            ? $"Synchronized {_tracks.Count} arranged track region(s); {preservedRegions.Count} other region(s) preserved."
            : "Track regions already match the arranged CD sequence.";
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_busy || trackList.SelectedItem is not TrackRow row) return;
        _main.StopPreview();
        SetBusy(true, "Preparing a bounded track preview...");
        _operation = new CancellationTokenSource();
        try
        {
            var plan = row.ToPlan();
            int maximum = Math.Max(1, _document.Doc.SampleRate * 15);
            int count = Math.Min(plan.Length, maximum);
            int start = plan.SourceStart;
            if (_document.Cursor >= plan.SourceStart && _document.Cursor < plan.SourceEnd)
                start = Math.Clamp(_document.Cursor - count / 3, plan.SourceStart, plan.SourceEnd - count);

            bool renderRack = renderRackCheck.IsChecked == true;
            int version = _document.Doc.EditVersion;
            int sampleRate = _document.Doc.SampleRate;
            float[][] source = _document.Doc.Channels.ToArray();
            CancellationToken token = _operation.Token;
            IProgress<double>? rackProgress = renderRack
                ? new Progress<double>(fraction =>
                {
                    progressBar.Value = fraction;
                    statusText.Text = $"Warming the rack from the program start — {fraction:P0}";
                })
                : null;
            float[][] data = await Task.Run(() => renderRack
                    ? _main.Engine.Master.ProcessOfflineRange(
                        source, sampleRate, start, count, token, rackProgress)
                    : CopyRange(source, start, count, token), token);
            if (version != _document.Doc.EditVersion)
                throw new InvalidOperationException("The source changed while the preview was rendering. Try Preview again.");

            var preview = new AudioDocument(data, sampleRate,
                renderRack ? 32 : _document.Doc.SourceBitDepth)
            {
                Title = $"Preview - {plan.Title}",
            };
            // The rack render is already baked into this transient document. Bypass
            // the live rack during playback so the audition is not processed twice.
            // PlayPreview returns false when a transport recording is active/pending or the
            // engine is awaiting recovery — reporting "Previewing…" then would be a lie.
            if (!_main.PlayPreview(preview, loop: false, bypassRack: true))
                statusText.Text = "Preview is unavailable while recording audio is active or awaiting recovery.";
            else
                statusText.Text = renderRack
                    ? $"Previewing {plan.Title} from the same continuous rack render used for export."
                    : $"Previewing the dry source for {plan.Title}; export will also remain dry.";
        }
        catch (OperationCanceledException) { statusText.Text = "Preview preparation cancelled."; }
        catch (Exception ex) { statusText.Text = ex.Message; }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var plan = CurrentPlan();
        var errors = CdTransfer.Validate(plan, _document.Doc.SampleRate, _document.Doc.Length)
            .Where(i => i.Severity == CdPlanIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors.Select(i => "- " + i.Message)),
                "CD plan needs attention", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new OpenFolderDialog { Title = "Choose a new or empty folder for the CD package" };
        if (picker.ShowDialog(this) != true) return;

        _main.StopPreview();
        SetBusy(true, "Preparing CD-compatible tracks...");
        _operation = new CancellationTokenSource();
        closeBtn.Content = "Cancel";
        try
        {
            CancellationToken token = _operation.Token;
            bool renderRack = renderRackCheck.IsChecked == true;
            AudioDocument exportSource = _document.Doc;
            double packageStart = renderRack ? 0.15 : 0;

            if (renderRack)
            {
                int version = _document.Doc.EditVersion;
                int sampleRate = _document.Doc.SampleRate;
                float[][] stableSource = _document.Doc.Channels.ToArray();
                var rackProgress = new Progress<double>(fraction =>
                {
                    progressBar.Value = fraction * packageStart;
                    statusText.Text = $"Rendering the current rack - {fraction:P0}";
                });
                float[][] rendered = await Task.Run(() =>
                    _main.Engine.Master.ProcessOffline(stableSource, sampleRate, token, rackProgress), token);
                if (version != _document.Doc.EditVersion)
                    throw new InvalidOperationException("The source changed while the rack was rendering. Start the package again.");
                exportSource = new AudioDocument(rendered, sampleRate, sourceBitDepth: 32)
                {
                    Title = _document.Doc.Title + " - CD master",
                };
            }

            var progress = new Progress<CdPackageProgress>(p =>
            {
                progressBar.Value = packageStart + p.Fraction * (1 - packageStart);
                statusText.Text = p.CompletedTracks >= p.TotalTracks
                    ? "CD package complete."
                    : p.CurrentTrack == "Converting continuous master"
                        ? $"{p.CurrentTrack} - {p.Fraction:P0}"
                        : $"Preparing {p.CurrentTrack} - {Math.Min(p.CompletedTracks + 1, p.TotalTracks)} of {p.TotalTracks}";
            });
            var result = await CdTransfer.ExportPackageAsync(exportSource, plan, picker.FolderName,
                discTitle.Text, progress, token);
            progressBar.Value = 1;
            InfoDialog.Show(this, "CD Package Ready",
                $"Created {result.WaveFiles.Count} sector-aligned CD WAV file(s) and a CUE sheet. Open the CUE file in your preferred disc-burning application.",
                result.CueFile);
        }
        catch (OperationCanceledException) { statusText.Text = "CD package export cancelled; staged files were removed."; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CD package failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            statusText.Text = "No completed package was published.";
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            closeBtn.Content = "Close";
            SetBusy(false, statusText.Text);
        }
    }

    private List<CdTrackPlan> CurrentPlan() => _tracks.Select(t => t.ToPlan()).ToList();

    private void UpdatePlan()
    {
        var issues = CdTransfer.Validate(CurrentPlan(), _document.Doc.SampleRate, _document.Doc.Length);
        var issue = issues.FirstOrDefault(i => i.Severity == CdPlanIssueSeverity.Error)
                    ?? issues.FirstOrDefault(i => i.Severity == CdPlanIssueSeverity.Warning)
                    ?? issues.FirstOrDefault();
        validationText.Text = issue?.Message ?? "";
        validationText.Foreground = issue?.Severity switch
        {
            CdPlanIssueSeverity.Error => (System.Windows.Media.Brush)FindResource("Red"),
            CdPlanIssueSeverity.Warning => (System.Windows.Media.Brush)FindResource("Amber"),
            _ => (System.Windows.Media.Brush)FindResource("Muted"),
        };
        exportBtn.IsEnabled = !_busy && !issues.Any(i => i.Severity == CdPlanIssueSeverity.Error);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        trackList.IsEnabled = !busy;
        discTitle.IsEnabled = !busy;
        thresholdSlider.IsEnabled = !busy;
        analyzeBtn.IsEnabled = !busy;
        addSelectionBtn.IsEnabled = !busy;
        saveRegionsBtn.IsEnabled = !busy;
        renderRackCheck.IsEnabled = !busy;
        previewBtn.IsEnabled = !busy && trackList.SelectedItem != null;
        upBtn.IsEnabled = downBtn.IsEnabled = removeBtn.IsEnabled = splitBtn.IsEnabled =
            !busy && trackList.SelectedItem != null;
        statusText.Text = status;
        UpdatePlan();
        if (!busy) progressBar.Value = 0;
        if (!busy && _closeWhenFinished)
        {
            _closeWhenFinished = false;
            Close();
        }
    }

    private void OnTrackSelected(object sender, SelectionChangedEventArgs e)
    {
        bool selected = trackList.SelectedItem != null && !_busy;
        previewBtn.IsEnabled = upBtn.IsEnabled = downBtn.IsEnabled = removeBtn.IsEnabled =
            splitBtn.IsEnabled = selected;
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (thresholdText != null) thresholdText.Text = $"{e.NewValue:0} dB";
    }

    private void OnPlanChanged(object sender, TextChangedEventArgs e)
    {
        if (validationText != null) UpdatePlan();
    }

    private void OnRenderRackChanged(object sender, RoutedEventArgs e)
    {
        if (!_dialogReady || _restoringRack) return;
        _main.StopPreview();
        _main.Master.RackEnabled = renderRackCheck.IsChecked == true;
        statusText.Text = renderRackCheck.IsChecked == true
            ? "The current rack will be heard in Preview and rendered into the CD files."
            : "Preview and export are both using the dry, unprocessed source.";
    }

    private void RestoreRackState()
    {
        _restoringRack = true;
        try { _main.Master.RackEnabled = _rackWasEnabled; }
        finally { _restoringRack = false; }
    }

    private void OnDialogClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        // Remember the request: SetBusy re-issues the close once the work unwinds,
        // otherwise the user has to click X a second time.
        _closeWhenFinished = true;
        _operation?.Cancel();
        statusText.Text = "Cancelling the current operation...";
    }

    private static float[][] CopyRange(
        IReadOnlyList<float[]> source, int start, int count, CancellationToken cancellationToken)
    {
        const int block = 1 << 20;
        var output = new float[source.Count][];
        for (int c = 0; c < source.Count; c++)
        {
            output[c] = new float[count];
            for (int offset = 0; offset < count; offset += block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(block, count - offset);
                Array.Copy(source[c], start + offset, output[c], offset, n);
            }
        }
        return output;
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
