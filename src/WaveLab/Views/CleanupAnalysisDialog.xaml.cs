using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>
/// Analysis-first tuning for the Vinyl Cleanup and Clean Transfer factory racks.
/// The dialog works from a stable source snapshot and returns a preset; it never
/// edits the document or replaces the live rack itself.
/// </summary>
public partial class CleanupAnalysisDialog : Window
{
    private sealed record MetricRow(string Label, string Value, string Evidence);

    private sealed class RecommendationRow(CleanupRecommendation recommendation)
    {
        public string TypeId { get; } = recommendation.TypeId;
        public string Name { get; } = recommendation.DisplayName;
        public string Evidence { get; } = recommendation.Evidence;
        public string Transition { get; } =
            $"{recommendation.CurrentText}  →  {recommendation.RecommendedText}";
        public string ConfidenceText { get; } =
            $"{Math.Clamp(recommendation.Confidence, 0, 1):P0}";
        public bool IsSelected { get; set; } = recommendation.ApplyByDefault;
    }

    private readonly DocumentViewModel _document;
    private readonly MainViewModel _main;
    private readonly CleanupProfile _profile;
    private readonly float[][] _sourceReferences;
    private readonly int _rangeStart;
    private readonly int _rangeCount;
    private readonly int _sourceEditVersion;
    private readonly int _sampleRate;
    private readonly int _sourceBitDepth;
    private readonly int _previewStart;
    private readonly int _previewLength;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<RecommendationRow> _recommendationRows = [];

    private CancellationTokenSource? _operation;
    private CleanupAnalysisResult? _analysis;
    private float[][]? _source;
    private float[][]? _recommendedPreview;
    private string? _recommendedPreviewKey;
    private bool _nextPreviewIsRecommended = true;
    private bool _loadingRows;
    private bool _busy;
    private bool _closed;

    public CleanupAnalysisDialog(
        DocumentViewModel document,
        MainViewModel main,
        CleanupProfile profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(main);
        if (profile is not (CleanupProfile.VinylCleanup or CleanupProfile.CleanTransfer))
            throw new ArgumentOutOfRangeException(nameof(profile));

        InitializeComponent();
        _document = document;
        _main = main;
        _profile = profile;

        // Capture all live-document state on the UI thread. AudioDocument edits
        // splice in new channel arrays, so these references remain a coherent,
        // immutable point-in-time source while worker threads inspect them.
        (_rangeStart, _rangeCount) = document.EditRange();
        _sourceEditVersion = document.Doc.EditVersion;
        _sampleRate = document.Doc.SampleRate;
        _sourceBitDepth = document.Doc.SourceBitDepth;
        _sourceReferences = document.Doc.Channels.ToArray();

        int maximumPreview = Math.Max(0, Math.Min(_rangeCount, checked(_sampleRate * 12)));
        int relativeCursor = document.Cursor >= _rangeStart &&
                             document.Cursor < _rangeStart + _rangeCount
            ? document.Cursor - _rangeStart
            : 0;
        _previewStart = maximumPreview == 0
            ? 0
            : Math.Clamp(relativeCursor - _sampleRate * 2, 0,
                Math.Max(0, _rangeCount - maximumPreview));
        _previewLength = maximumPreview;

        string profileName = ProfileName(profile);
        Title = $"{profileName} · Analyze & Tune";
        titleText.Text = Title;
        recommendationTitle.Text = $"RECOMMENDED {profileName.ToUpperInvariant()} SETTINGS";

        string rangeName = document.HasSelection
            ? $"Selection · {TimeFormat.Position(_rangeStart, _sampleRate)} — " +
              TimeFormat.Position(_rangeStart + _rangeCount, _sampleRate)
            : "Entire recording";
        string channels = _sourceReferences.Length switch
        {
            1 => "mono",
            2 => "stereo",
            var count => $"{count} channels",
        };
        sourceText.Text = $"{rangeName} · {TimeFormat.Compact(_rangeCount / (double)_sampleRate)}" +
                          $" · {_sampleRate / 1000.0:0.0} kHz · {channels}";

        Loaded += OnLoaded;
        Closed += OnClosed;
        UpdateUiState();
    }

    /// <summary>
    /// The analyzed custom chain selected by the user. The caller owns applying
    /// this preset to the live rack after ShowDialog returns true.
    /// </summary>
    public EffectFactory.ChainPreset? ResultPreset { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_rangeCount <= 0 || _sourceReferences.Length == 0)
        {
            statusText.Text = "There is no audio in the selected range.";
            analysisSummaryText.Text = "No audio to analyze";
            reanalyzeBtn.IsEnabled = false;
            return;
        }

        await AnalyzeAsync();
    }

    private async void OnReanalyze(object sender, RoutedEventArgs e)
    {
        if (_document.Doc.EditVersion != _sourceEditVersion)
        {
            MessageBox.Show(this,
                "The source document changed after this window was opened. Reopen Analyze & Tune to inspect the current audio.",
                "Source changed", MessageBoxButton.OK, MessageBoxImage.Information);
            statusText.Text = "Source changed · re-analysis was not started.";
            return;
        }

        await AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (_busy || _closed) return;
        StopPreview();
        InvalidatePreview();
        analysisSummaryText.Text = "Analyzing representative passages…";
        statusText.Text = _source == null
            ? "Creating a stable source snapshot…"
            : "Re-analyzing the stable source snapshot…";
        progressBar.Value = 0;

        var operation = BeginOperation();
        var progress = new Progress<CleanupAnalysisProgress>(update =>
        {
            if (!IsCurrent(operation)) return;
            statusText.Text = string.IsNullOrWhiteSpace(update.Stage)
                ? "Analyzing the source…"
                : update.Stage;
            progressBar.Value = Math.Clamp(update.Fraction, 0, 1);
        });

        try
        {
            var completed = await Task.Run(() =>
            {
                operation.Token.ThrowIfCancellationRequested();
                float[][] source = _source ?? CaptureSourceRange(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
                    source, _sampleRate, _profile, operation.Token, progress);
                operation.Token.ThrowIfCancellationRequested();
                return (Source: source, Result: result);
            }, operation.Token);

            if (!IsCurrent(operation)) return;
            _source = completed.Source;
            _analysis = completed.Result;
            PopulateResults(completed.Result);
            progressBar.Value = 1;
            analysisSummaryText.Text = completed.Result.WindowsAnalyzed == 1
                ? "Analysis complete · 1 representative passage"
                : $"Analysis complete · {completed.Result.WindowsAnalyzed:N0} representative passages";
            statusText.Text =
                $"Recommendations are ready · preview is limited to {_previewLength / (double)_sampleRate:0.#} seconds.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(operation))
            {
                analysisSummaryText.Text = "Analysis cancelled";
                statusText.Text = _analysis == null
                    ? "Analysis cancelled · the source was not changed."
                    : "Analysis cancelled · the previous recommendations remain available.";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation))
            {
                analysisSummaryText.Text = "Analysis failed";
                statusText.Text = _analysis == null
                    ? "The cleanup analysis could not be completed."
                    : "Re-analysis failed · the previous recommendations remain available.";
                MessageBox.Show(this, ex.Message, "Cleanup analysis failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private void PopulateResults(CleanupAnalysisResult result)
    {
        metricsItems.ItemsSource = result.Metrics
            .Select(metric => new MetricRow(metric.Label, metric.Value, metric.Detail))
            .ToList();

        _loadingRows = true;
        try
        {
            _recommendationRows.Clear();
            _recommendationRows.AddRange(result.Recommendations.Select(item => new RecommendationRow(item)));
            recommendationItems.ItemsSource = null;
            recommendationItems.ItemsSource = _recommendationRows;
        }
        finally
        {
            _loadingRows = false;
        }
    }

    private void OnRecommendationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingRows || _analysis == null) return;
        InvalidatePreview();
        statusText.Text =
            $"{SelectedTypeIds().Count:N0} recommendations selected · preview again to hear this combination.";
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_analysis == null || _source == null || _busy || _closed || _previewLength <= 0) return;
        StopPreview();

        bool recommended = _nextPreviewIsRecommended;
        _nextPreviewIsRecommended = !_nextPreviewIsRecommended;
        IReadOnlyList<string> selectedTypeIds = SelectedTypeIds();
        string selectionKey = string.Join("\u001f", selectedTypeIds.Order(StringComparer.Ordinal));
        EffectFactory.ChainPreset? preset = recommended
            ? _analysis.BuildSelectedPreset(selectedTypeIds)
            : null;
        var operation = BeginOperation();
        statusText.Text = recommended
            ? "Rendering a bounded preview through the recommended rack…"
            : "Preparing the dry comparison…";
        progressBar.Value = 0;
        var renderProgress = new Progress<double>(fraction =>
        {
            if (!IsCurrent(operation)) return;
            progressBar.Value = Math.Clamp(fraction, 0, 1);
        });

        try
        {
            float[][] previewAudio;
            if (recommended && _recommendedPreview != null && _recommendedPreviewKey == selectionKey)
            {
                previewAudio = _recommendedPreview;
                progressBar.Value = 1;
            }
            else
            {
                previewAudio = await Task.Run(() => recommended
                    ? RenderRecommendedPreview(preset!, operation.Token, renderProgress)
                    : CopyChannels(_source, _previewStart, _previewLength, operation.Token),
                    operation.Token);
            }

            if (!IsCurrent(operation)) return;
            if (recommended)
            {
                _recommendedPreview = previewAudio;
                _recommendedPreviewKey = selectionKey;
            }

            var preview = new AudioDocument(previewAudio, _sampleRate, _sourceBitDepth)
            {
                Title = recommended
                    ? $"{ProfileName(_profile)} · recommended preview"
                    : $"{ProfileName(_profile)} · dry preview",
            };
            _main.PlayPreview(preview, loop: true, bypassRack: true);
            progressBar.Value = 1;
            previewBtn.Content = recommended ? "▶ Play A · Dry" : "▶ Play B · Tuned";
            statusText.Text = recommended
                ? $"Audition B · recommended settings · {_previewLength / (double)_sampleRate:0.#}-second loop. Click again for A · dry."
                : $"Audition A · original source · {_previewLength / (double)_sampleRate:0.#}-second loop. Click again for B · tuned.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(operation)) statusText.Text = "Preview cancelled.";
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation))
            {
                statusText.Text = "The A/B preview could not be prepared.";
                MessageBox.Show(this, ex.Message, "Preview failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private float[][] RenderRecommendedPreview(
        EffectFactory.ChainPreset preset,
        CancellationToken cancellationToken,
        IProgress<double> progress)
    {
        var source = _source ?? throw new InvalidOperationException("The analysis source is unavailable.");

        // Give stateful effects two seconds of context while keeping worker cost
        // bounded to at most fourteen seconds of program audio.
        int warmStart = Math.Max(0, _previewStart - checked(_sampleRate * 2));
        int relativePreviewStart = _previewStart - warmStart;
        int boundedLength = checked(relativePreviewStart + _previewLength);
        float[][] boundedSource = CopyChannels(source, warmStart, boundedLength, cancellationToken);

        var isolatedRack = new MasterSection();
        isolatedRack.ReplaceChain(EffectFactory.Instantiate(preset));
        isolatedRack.RackEnabled = true;
        return isolatedRack.ProcessOfflineRange(
            boundedSource,
            _sampleRate,
            relativePreviewStart,
            _previewLength,
            cancellationToken,
            progress);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_analysis == null || _busy || _closed) return;
        StopPreview();

        if (_document.Doc.EditVersion != _sourceEditVersion ||
            _rangeStart < 0 || _rangeStart + _rangeCount > _document.Doc.Length)
        {
            MessageBox.Show(this,
                "The source document changed while Analyze & Tune was open. Nothing was applied. Reopen this window to analyze the current audio.",
                "Source changed", MessageBoxButton.OK, MessageBoxImage.Information);
            statusText.Text = "Source changed · no rack preset was returned.";
            return;
        }

        try
        {
            ResultPreset = _analysis.BuildSelectedPreset(SelectedTypeIds());
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ResultPreset = null;
            statusText.Text = "The analyzed rack preset could not be created.";
            MessageBox.Show(this, ex.Message, "Apply to Rack failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private IReadOnlyList<string> SelectedTypeIds() => _recommendationRows
        .Where(row => row.IsSelected)
        .Select(row => row.TypeId)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private float[][] CaptureSourceRange(CancellationToken cancellationToken)
    {
        bool entireSnapshot = _rangeStart == 0 && _sourceReferences.Length > 0 &&
                              _rangeCount == _sourceReferences[0].Length;
        return entireSnapshot
            ? _sourceReferences
            : CopyChannels(_sourceReferences, _rangeStart, _rangeCount, cancellationToken);
    }

    private static float[][] CopyChannels(
        IReadOnlyList<float[]> source,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0) return [];
        if (start < 0 || count < 0 || start > source[0].Length - count)
            throw new ArgumentOutOfRangeException(nameof(start));

        var copy = new float[source.Count][];
        const int block = 65536;
        for (int channel = 0; channel < source.Count; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source[channel].Length < start + count)
                throw new ArgumentException("Source channels do not have a consistent length.", nameof(source));
            copy[channel] = new float[count];
            for (int offset = 0; offset < count; offset += block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(block, count - offset);
                Array.Copy(source[channel], start + offset, copy[channel], offset, length);
            }
        }
        return copy;
    }

    private CancellationTokenSource BeginOperation()
    {
        if (_busy) throw new InvalidOperationException("Another cleanup operation is already running.");
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        _busy = true;
        UpdateUiState();
        return operation;
    }

    private bool IsCurrent(CancellationTokenSource operation) =>
        !_closed && ReferenceEquals(_operation, operation) && !operation.IsCancellationRequested;

    private void CompleteOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operation, operation))
        {
            _operation = null;
            _busy = false;
        }
        operation.Dispose();
        if (!_closed) UpdateUiState();
    }

    private void UpdateUiState()
    {
        bool hasResult = _analysis != null && _source != null;
        resultsHost.IsEnabled = hasResult && !_busy;
        reanalyzeBtn.IsEnabled = !_busy && !_closed && _rangeCount > 0;
        previewBtn.IsEnabled = hasResult && !_busy && !_closed && _previewLength > 0;
        applyBtn.IsEnabled = hasResult && !_busy && !_closed;
        cancelBtn.Content = _busy ? "Cancel" : "Close";
    }

    private void InvalidatePreview()
    {
        StopPreview();
        _recommendedPreview = null;
        _recommendedPreviewKey = null;
        _nextPreviewIsRecommended = true;
        previewBtn.Content = "▶ A/B Preview";
    }

    private void StopPreview()
    {
        try { _main.StopPreview(); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        try { _operation?.Cancel(); } catch { }
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        try { _operation?.Cancel(); } catch { }
        try { _lifetime.Cancel(); } catch { }
        StopPreview();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        try { _operation?.Cancel(); } catch { }
        try { _lifetime.Cancel(); } catch { }
        StopPreview();
        _lifetime.Dispose();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static string ProfileName(CleanupProfile profile) => profile switch
    {
        CleanupProfile.VinylCleanup => "Vinyl Cleanup",
        CleanupProfile.CleanTransfer => "Clean Transfer",
        _ => "Cleanup",
    };
}
