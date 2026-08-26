using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

/// <summary>
/// Analysis-first vinyl restoration. All processing is performed on a stable copy of
/// the selected source range; the document is changed only by the final ReplaceRange.
/// </summary>
public partial class RestorationWorkbenchDialog : Window
{
    private sealed record RestorationSettings(
        bool RepairClicks,
        double ClickSensitivity,
        double ClickStrength,
        bool Declip,
        double DeclipStrength,
        double DeclipHeadroomDb,
        DeclipMethod DeclipMethod,
        bool ReduceNoise,
        double NoiseReductionDb,
        double NoiseSensitivityDb,
        bool RemoveHum,
        double HumAmount,
        double HumFrequency,
        int HumHarmonics,
        double HumQ,
        bool RemoveSubsonic,
        double SubsonicCutoffHz,
        bool ReduceSide,
        double SideLevel,
        bool Decrackle,
        double DecrackleThreshold,
        double WetAmount,
        bool Bypass);

    private sealed record AnalysisBundle(
        ClickAnalysisResult Clicks,
        ClippingAnalysisResult Clipping);

    private sealed record NoiseProfileResult(float[]? Profile, int RelativeStart, bool Automatic);

    private readonly record struct OperationProgress(string Text, double Fraction);

    private sealed class DspProgressAdapter(
        IProgress<OperationProgress> target,
        double start,
        double span) : IProgress<RestorationProgress>
    {
        public void Report(RestorationProgress value)
        {
            string text = value.Stage switch
            {
                RestorationStage.AnalyzingClicks => $"Analyzing clicks and pops · {value.EventsProcessed:N0} candidates",
                RestorationStage.AnalyzingClipping => $"Analyzing clipped peaks · {value.EventsProcessed:N0} candidates",
                RestorationStage.RepairingClicks => "Repairing clicks and pops…",
                RestorationStage.RepairingClipping => "Reconstructing clipped peaks…",
                RestorationStage.RenderingPreview => "Blending dry and restored audio…",
                _ => "Processing audio…",
            };
            target.Report(new OperationProgress(text,
                Math.Clamp(start + value.Fraction * span, 0, 1)));
        }
    }

    private sealed class CleanupProgressAdapter(
        IProgress<OperationProgress> target,
        double start,
        double span) : IProgress<CleanupAnalysisProgress>
    {
        public void Report(CleanupAnalysisProgress value) =>
            target.Report(new OperationProgress(value.Stage,
                Math.Clamp(start + value.Fraction * span, 0, 1)));
    }

    /// <summary>
    /// The workbench open on each document. Modeless, Restore &gt; Vinyl Restoration can be chosen
    /// twice; two of these on one file would each hold their own analysis of it and each be willing
    /// to commit that analysis over the other's edit.
    /// </summary>
    private static readonly Dictionary<DocumentViewModel, RestorationWorkbenchDialog> OpenDialogs = [];

    private readonly DocumentViewModel _document;
    private readonly MainViewModel _main;

    // Everything the analysis was taken from. Not readonly: the window outlives an edit now, and
    // Re-analyze re-takes all of it against whatever the document has become. See CaptureSource.
    private float[][] _sourceReferences = [];
    private float[]? _capturedNoiseProfile;
    private int _rangeStart;
    private int _rangeCount;
    private int _sampleRate;
    private int _sourceBitDepth;
    private int _sourceEditVersion;
    private int _previewStart;
    private int _previewLength;
    private bool _rackWasEnabled;
    private readonly DispatcherTimer _previewDebounce;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _operation;
    private float[][]? _source;
    private float[]? _noiseProfile;
    private ClickAnalysisResult? _clickAnalysis;
    private ClippingAnalysisResult? _clippingAnalysis;
    private RestorationRecommendations.Settings? _analysisRecommendations;
    private RestorationSettings? _previewWetCacheSettings;
    private float[][]? _previewWetCache;
    private double _analyzedClickSensitivity = double.NaN;
    private bool _initialized;
    private bool _initializing;
    private bool _busy;
    private bool _applying;
    private bool _previewStarted;
    private bool _previewRackBypassed;
    private bool _bypassingRackForPreview;
    private bool _suppressControlEvents;
    private bool _closed;
    private bool _closeWhenFinished;
    private bool _sourceStale;
    private bool _rangeStale;

    public RestorationWorkbenchDialog(DocumentViewModel document, MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(main);

        InitializeComponent();
        _document = document;
        _main = main;
        _rackWasEnabled = main.Master.RackEnabled;

        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
        _previewDebounce.Tick += OnPreviewDebounce;

        CaptureSource(firstCapture: true);

        // Everything this window measured can move underneath it now. Each subscription watches one
        // of those, and all four come off in OnClosed.
        document.Doc.Changed += OnSourceEdited;
        document.PropertyChanged += OnDocumentStateChanged;
        main.Master.PropertyChanged += OnMasterChanged;
        main.Documents.CollectionChanged += OnDocumentsChanged;

        _initialized = true;
        UpdateReadouts();
        UpdateUiState();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>True when the successful apply should continue into CD track preparation.</summary>
    public bool PrepareCdRequested { get; private set; }

    /// <summary>
    /// Raised once the restoration has been committed, immediately before the window closes. The
    /// argument is <see cref="PrepareCdRequested"/>: modeless, there is no <c>ShowDialog</c> return
    /// value for the caller to read it from.
    /// </summary>
    public event Action<bool>? Applied;

    /// <summary>
    /// Open — or raise — the workbench for <paramref name="document"/>. It is modeless, so the
    /// recording stays editable while it is open; <paramref name="onApplied"/> replaces what the
    /// caller used to do with the <c>ShowDialog</c> result.
    /// </summary>
    public static RestorationWorkbenchDialog ShowFor(
        DocumentViewModel document, MainViewModel main, Window? owner, Action<bool>? onApplied = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(main);
        if (OpenDialogs.TryGetValue(document, out RestorationWorkbenchDialog? existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var dialog = new RestorationWorkbenchDialog(document, main) { Owner = owner };
        OpenDialogs[document] = dialog;
        if (onApplied != null) dialog.Applied += onApplied;
        dialog.Closed += (_, _) =>
        {
            if (OpenDialogs.TryGetValue(document, out RestorationWorkbenchDialog? registered) &&
                ReferenceEquals(registered, dialog))
                OpenDialogs.Remove(document);
        };
        dialog.Show();
        return dialog;
    }

    /// <summary>
    /// Take everything the analysis is about to be built from: the range, the format, the channel
    /// arrays and the document's own noise print. Only array <em>references</em> are read on the UI
    /// thread — <see cref="AudioDocument"/> splices replace channel arrays rather than mutating
    /// them, so these stay a coherent point-in-time source while the large copy is made on a worker.
    /// </summary>
    private void CaptureSource(bool firstCapture)
    {
        (_rangeStart, _rangeCount) = _document.EditRange();
        _sampleRate = _document.Doc.SampleRate;
        _sourceBitDepth = _document.Doc.SourceBitDepth;
        _sourceEditVersion = _document.Doc.EditVersion;
        _sourceReferences = _document.Doc.Channels.ToArray();
        _capturedNoiseProfile = _document.NoiseProfile is { Length: > 0 } profile
            ? (float[])profile.Clone()
            : null;

        int maximumPreview = Math.Max(1, Math.Min(_rangeCount, checked(_sampleRate * 12)));
        int relativeCursor = _document.Cursor >= _rangeStart && _document.Cursor < _rangeStart + _rangeCount
            ? _document.Cursor - _rangeStart
            : 0;
        _previewStart = Math.Clamp(relativeCursor - _sampleRate * 2, 0,
            Math.Max(0, _rangeCount - maximumPreview));
        _previewLength = maximumPreview;

        rangeText.Text = _document.HasSelection
            ? $"Selection · {TimeFormat.Position(_rangeStart, _sampleRate)} — {TimeFormat.Position(_rangeStart + _rangeCount, _sampleRate)}"
            : "Entire recording";
        string channels = _document.Doc.ChannelCount switch
        {
            1 => "mono",
            2 => "stereo",
            var count => $"{count} channels",
        };
        formatText.Text = $"{TimeFormat.Compact((double)_rangeCount / Math.Max(1, _sampleRate))} · {_sampleRate / 1000.0:0.0} kHz · {channels}";

        // Past the ceiling the option is shown and disabled rather than hidden, and the caption
        // carries the figure and what to do about it.
        bool tooLarge = ResidualSummary.ExceedsBudget(_rangeCount, _document.Doc.ChannelCount);
        keepRemovedCheck.IsEnabled = !tooLarge;
        // Only the first capture reads the stored preference. A re-analysis that put the range past
        // the ceiling has to clear the box, but one that did not must leave a choice already made.
        if (firstCapture) keepRemovedCheck.IsChecked = !tooLarge && AppSettings.Instance.KeepRemovedMaterial;
        else if (tooLarge) keepRemovedCheck.IsChecked = false;
        keepRemovedCaption.Text = ResidualSummary.DescribeCost(_rangeCount, _document.Doc.ChannelCount);

        _sourceStale = false;
        _rangeStale = false;
        UpdateStaleChrome();
    }

    /// <summary>
    /// The recording changed. The analysis still describes the audio as it was, so it may no longer
    /// be committed — said here, at the edit, rather than discovered after a full render.
    /// </summary>
    private void OnSourceEdited(int start, int removed, int inserted)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSourceEdited(start, removed, inserted));
            return;
        }
        if (_closed || _document.Doc.EditVersion == _sourceEditVersion) return;
        _sourceStale = true;
        UpdateStaleChrome();
        UpdateUiState();
    }

    /// <summary>
    /// The selection moved. Unlike an edit this does not invalidate anything — the captured range is
    /// still the range that was analyzed — so Apply stays available and the offer is only to re-scope.
    /// </summary>
    private void OnDocumentStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DocumentViewModel.HasSelection) or
            nameof(DocumentViewModel.SelStart) or nameof(DocumentViewModel.SelEnd) or null)) return;
        if (_closed) return;
        (int start, int count) = _document.EditRange();
        bool moved = start != _rangeStart || count != _rangeCount;
        if (moved == _rangeStale) return;
        _rangeStale = moved;
        UpdateStaleChrome();
    }

    /// <summary>
    /// A preview bypasses the master rack so the A/B is of the restoration alone, and close puts the
    /// rack back. Modeless, the user can also work that switch themselves while this is open — and
    /// once they have, theirs is the state close should restore, not the one captured at open.
    /// </summary>
    private void OnMasterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MasterSectionViewModel.RackEnabled) or null)) return;
        if (_bypassingRackForPreview || _closed) return;
        _rackWasEnabled = _main.Master.RackEnabled;
        _previewRackBypassed = false;
    }

    /// <summary>The file being restored was closed; there is nothing left to apply to.</summary>
    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // An Add is a new tab — the residual one this workbench itself opens, among others — and can
        // never be the reason this document went away, so it is not worth a scan of the list.
        if (_closed || e.Action == NotifyCollectionChangedAction.Add) return;
        if (_main.Documents.Contains(_document)) return;
        // Close through the ordinary path: a render in flight cancels first and OnWindowClosing
        // re-issues this once it unwinds, which is also what stops it committing to a closed file.
        Close();
    }

    private void UpdateStaleChrome()
    {
        if (staleText == null || reanalyzeBtn == null) return;
        string message = _sourceStale
            ? "The recording changed · this analysis describes the audio as it was"
            : _rangeStale
                ? "The selection moved · this analysis still covers the captured range"
                : "";
        staleText.Text = message;
        staleText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        reanalyzeBtn.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        reanalyzeBtn.IsEnabled = !_busy;
    }

    /// <summary>
    /// Scan again from what the document is now. Equivalent to closing the workbench and reopening
    /// it — including re-tuning every control from the new measurements — without losing the window.
    /// </summary>
    private async void OnReanalyze(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        _main.StopPreview();
        _previewDebounce.Stop();
        _source = null;
        _noiseProfile = null;
        _clickAnalysis = null;
        _clippingAnalysis = null;
        _analysisRecommendations = null;
        _analyzedClickSensitivity = double.NaN;
        _noiseToProgrammeDb = null;
        _sideToMidDb = null;
        _rumbleEvidence = null;
        _rumbleConfidence = 0;
        _impulsesFound = 0;
        _previewWetCacheSettings = null;
        _previewWetCache = null;
        _previewStarted = false;
        noiseEnabled.IsEnabled = true;
        CaptureSource(firstCapture: false);
        UpdateUiState();
        await AnalyzeAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        if (_rangeCount <= 0 || _sourceReferences.Length == 0)
        {
            statusText.Text = "There is no audio in the selected range.";
            closeBtn.Content = "Close";
            return;
        }

        _initializing = true;
        var operation = BeginOperation(applying: false, "Starting full-file offline analysis…");
        var progress = CreateProgress(operation);
        try
        {
            var prepared = await Task.Run(() =>
            {
                progress.Report(new OperationProgress(
                    "Scanning the complete file offline; no audio will be played…", 0.02));
                operation.Token.ThrowIfCancellationRequested();
                bool wholeDocument = _rangeStart == 0 && _sourceReferences.Length > 0 &&
                                     _rangeCount == _sourceReferences[0].Length;
                // AudioDocument edits replace channel arrays rather than mutating
                // them, so a whole-document workbench can safely retain this stable
                // point-in-time snapshot without duplicating an album-sized buffer.
                var source = wholeDocument
                    ? _sourceReferences
                    : CopyChannels(_sourceReferences, _rangeStart, _rangeCount, operation.Token);

                NoiseProfileResult noise;
                if (_capturedNoiseProfile != null)
                {
                    noise = new NoiseProfileResult((float[])_capturedNoiseProfile.Clone(), 0, false);
                    progress.Report(new OperationProgress("Using the document's learned noise print…", 0.18));
                }
                else
                {
                    progress.Report(new OperationProgress("Scanning the full file for its quietest noise-print passage…", 0.12));
                    noise = BuildAutomaticNoiseProfile(_sourceReferences, _sampleRate, operation.Token);
                }

                var cleanup = CleanupAnalyzer.Analyze(_sourceReferences, _sampleRate,
                    CleanupProfile.VinylCleanup, operation.Token,
                    new CleanupProgressAdapter(progress, 0.18, 0.25));

                var clickProgress = new DspProgressAdapter(progress, 0.45, 0.27);
                var clicks = Restoration.AnalyzeClicks(_sourceReferences, _sampleRate,
                    new ClickAnalysisOptions
                    {
                        Sensitivity = RestorationRecommendations.ExploratoryClickSensitivity,
                        PreserveTransients = true,
                    }, operation.Token, clickProgress);

                var clipProgress = new DspProgressAdapter(progress, 0.74, 0.19);
                var clipping = Restoration.AnalyzeClipping(_sourceReferences, _sampleRate,
                    new ClippingAnalysisOptions(), operation.Token, clipProgress);
                var recommendations = RestorationRecommendations.Create(clicks, clipping, cleanup);
                if (Math.Abs(recommendations.ClickSensitivity -
                             RestorationRecommendations.ExploratoryClickSensitivity) > 0.001)
                {
                    progress.Report(new OperationProgress(
                        $"Confirming click candidates at {recommendations.ClickSensitivity:0.0}/10 sensitivity...",
                        0.94));
                    clicks = Restoration.AnalyzeClicks(_sourceReferences, _sampleRate,
                        new ClickAnalysisOptions
                        {
                            Sensitivity = recommendations.ClickSensitivity,
                            PreserveTransients = true,
                        }, operation.Token,
                        new DspProgressAdapter(progress, 0.94, 0.05));
                    recommendations = RestorationRecommendations.Create(clicks, clipping, cleanup) with
                    {
                        ClickSensitivity = recommendations.ClickSensitivity,
                    };
                }
                operation.Token.ThrowIfCancellationRequested();
                return (Source: source, Noise: noise, Clicks: clicks, Clipping: clipping,
                    Recommendations: recommendations, Cleanup: cleanup);
            }, operation.Token);

            if (!IsCurrent(operation)) return;
            _source = prepared.Source;
            _noiseProfile = prepared.Noise.Profile;
            _noiseDepthCeilingDb = AppSettings.NormalizeNoiseDepthCeilingDb(
                AppSettings.Instance.NoiseDepthCeilingDb);
            _noiseToProgrammeDb = Restoration.EstimateNoiseToProgrammeDb(
                prepared.Source, _sampleRate, _noiseDepthCeilingDb);
            // Both measurements come from the analysis that already ran; neither is taken again
            // when a slider moves. The rumble sentence is the analyzer's own, so the card cannot
            // describe the subsonic band differently from the chain that filters it.
            _sideToMidDb = prepared.Cleanup.SideToMidDb;
            CleanupRecommendation? rumble = prepared.Cleanup.Recommendations
                .FirstOrDefault(item => item.TypeId == "filter");
            _rumbleEvidence = rumble?.Evidence;
            _rumbleConfidence = rumble?.Confidence ?? 0;
            _impulsesFound = prepared.Clicks.Events.Count;
            _clickAnalysis = prepared.Clicks;
            _clippingAnalysis = prepared.Clipping;
            _analyzedClickSensitivity = prepared.Recommendations.ClickSensitivity;
            _analysisRecommendations = prepared.Recommendations;
            ApplyAnalysisRecommendations(prepared.Recommendations);
            UpdateAnalysisSummary(new AnalysisBundle(prepared.Clicks, prepared.Clipping));

            if (_capturedNoiseProfile != null)
            {
                noiseSourceText.Text = "Learned document profile";
            }
            else if (prepared.Noise.Profile != null)
            {
                long absoluteNoiseStart = prepared.Noise.RelativeStart;
                noiseSourceText.Text = $"Auto · quietest at {TimeFormat.Position(absoluteNoiseStart, _sampleRate)}";
            }
            else
            {
                noiseSourceText.Text = "Range is too short for a noise print";
                noiseEnabled.IsChecked = false;
                noiseEnabled.IsEnabled = false;
            }

            statusText.Text =
                $"Full file analyzed and settings tuned; no audio played. Optional audition is {_previewLength / (double)_sampleRate:0.#} seconds.";
            progressBar.Value = 1;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(operation)) statusText.Text = "Preparation cancelled. Close the workbench to return.";
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation))
            {
                MessageBox.Show(this, ex.Message, "Restoration analysis failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                statusText.Text = "The restoration snapshot could not be prepared.";
            }
        }
        finally
        {
            _initializing = false;
            CompleteOperation(operation);
        }
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        _previewStarted = true;
        await RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        if (_source == null || _applying || _closed) return;
        _main.StopPreview();
        if (!_previewRackBypassed)
        {
            // This workbench commits only its restoration render. Keep A/B
            // playback honest by excluding unrelated master-rack processing.
            // Flagged, so OnMasterChanged can tell this write from the user's own.
            _bypassingRackForPreview = true;
            try { _main.Master.RackEnabled = false; }
            finally { _bypassingRackForPreview = false; }
            _previewRackBypassed = true;
        }
        var settings = CaptureSettings();
        var processingSettings = settings with { WetAmount = 1.0, Bypass = false };
        float[][]? cachedWet = _previewWetCacheSettings == processingSettings
            ? _previewWetCache
            : null;
        var operation = BeginOperation(applying: false,
            settings.Bypass ? "Preparing dry comparison…"
            : cachedWet != null ? "Updating the restored/original mix…"
            : "Rendering bounded restoration preview…");
        var progress = CreateProgress(operation);
        try
        {
            var result = await Task.Run(() =>
            {
                var analyses = EnsureAnalyses(settings.ClickSensitivity, operation.Token,
                    new DspProgressAdapter(progress, 0.02, 0.42));
                operation.Token.ThrowIfCancellationRequested();
                double previewSeconds = _previewLength / (double)_sampleRate;
                string previewStatus = cachedWet != null
                    ? "Reusing the restored preview and updating the mix…"
                    : !settings.Bypass &&
                      ((settings.RemoveHum && settings.HumAmount > 0) ||
                       (settings.ReduceNoise && settings.NoiseReductionDb > 0 && _noiseProfile is { Length: > 0 }))
                        ? $"Warming continuous restoration state and rendering the {previewSeconds:0.#}-second audition…"
                        : $"Rendering the {previewSeconds:0.#}-second audition range…";
                progress.Report(new OperationProgress(previewStatus, 0.46));
                float[][]? renderedWet = null;
                if (!settings.Bypass && cachedWet == null)
                {
                    renderedWet = RenderPreviewRange(processingSettings, analyses,
                        operation.Token, progress);
                }
                var rendered = settings.Bypass
                    ? CopyChannels(_source!, _previewStart, _previewLength, operation.Token)
                    : BlendPreview(cachedWet ?? renderedWet!, settings.WetAmount, operation.Token);
                return (Analyses: analyses, Audio: rendered, WetForCache: renderedWet);
            }, operation.Token);

            if (!IsCurrent(operation)) return;
            if (result.WetForCache != null)
            {
                _previewWetCacheSettings = processingSettings;
                _previewWetCache = result.WetForCache;
            }
            UpdateAnalysisSummary(result.Analyses);
            // Channel routing does not affect rendering or the wet cache. Read it only
            // after the background render so a selection made while rendering is the
            // selection that is actually sent to playback.
            RestorationAuditionMode auditionMode = CaptureAuditionMode();
            float[][] auditionAudio = RestorationPreview.CreateAudition(result.Audio,
                auditionMode);
            string auditionDescription = AuditionDescription(auditionMode);
            var preview = new AudioDocument(auditionAudio, _sampleRate, _sourceBitDepth)
            {
                Title = settings.Bypass
                    ? $"Vinyl restoration · dry · {auditionDescription}"
                    : $"Vinyl restoration · preview · {auditionDescription}",
            };
            // PlayPreview returns false when a transport recording is active/pending or the
            // engine is awaiting recovery. Claiming the A/B is audible then makes the user
            // compare against silence and commit settings they never heard.
            if (!_main.PlayPreview(preview, loop: false))
                statusText.Text = "Preview is unavailable while recording audio is active or awaiting recovery.";
            else
                statusText.Text = settings.Bypass
                    ? $"Bypass A/B · playing {auditionDescription}, {_previewLength / (double)_sampleRate:0.#} seconds of the original source (master rack bypassed)."
                    : $"Live preview · playing {auditionDescription}, {_previewLength / (double)_sampleRate:0.#} seconds at {settings.WetAmount:P0} restored (master rack bypassed).";
            progressBar.Value = 1;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(operation)) statusText.Text = "Preview render cancelled.";
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation))
            {
                MessageBox.Show(this, ex.Message, "Preview failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                statusText.Text = "The preview could not be rendered.";
            }
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e) => await ApplyAsync(prepareCd: false);

    private async void OnApplyAndPrepare(object sender, RoutedEventArgs e) => await ApplyAsync(prepareCd: true);

    private async Task ApplyAsync(bool prepareCd)
    {
        if (_source == null || _busy || bypassCheck.IsChecked == true || _closed) return;
        if (_sourceStale)
        {
            // UpdateUiState already disables both Apply buttons here; this is the keyboard and
            // command-palette route to the same place, refused before the render rather than after.
            statusText.Text = "The recording changed · re-analyze before applying.";
            return;
        }
        _main.StopPreview();
        var settings = CaptureSettings() with { Bypass = false };
        // Not part of RestorationSettings: it changes nothing about the audio, so folding it in
        // would invalidate the wet preview cache and mark the preset custom for a choice about
        // where the output goes.
        bool keepRemoved = keepRemovedCheck.IsChecked == true;
        var operation = BeginOperation(applying: true, "Rendering the complete restoration…");
        var progress = CreateProgress(operation);
        bool committed = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var analyses = EnsureAnalyses(settings.ClickSensitivity, operation.Token,
                    new DspProgressAdapter(progress, 0.01, 0.28));
                operation.Token.ThrowIfCancellationRequested();
                progress.Report(new OperationProgress("Rendering the complete restoration range…", 0.31));
                var audio = RenderFullRange(settings, analyses, operation.Token, progress);
                // Built here, before the commit: ReplaceRange and ReplaceAllOwned both take
                // ownership of `audio`, so this is the last moment the pair is safely ours. The
                // dry/wet blend needs no special case — dry minus the blend is exactly the part
                // of the difference the blend let through.
                float[][]? removed = null;
                RestorationPreview.ResidualLevels levels = default;
                if (keepRemoved)
                {
                    progress.Report(new OperationProgress("Collecting what the restoration removed…", 0.98));
                    var dry = _source
                        ?? throw new InvalidOperationException("The restoration source is not ready.");
                    removed = RestorationPreview.Difference(dry, audio, 0, operation.Token);
                    // Measured here rather than on the UI thread: it is a full pass over a buffer
                    // the size of the range.
                    levels = RestorationPreview.MeasureLevels(removed, operation.Token);
                }
                return (Analyses: analyses, Audio: audio, Removed: removed, Levels: levels);
            }, operation.Token);

            if (!IsCurrent(operation)) return;
            UpdateAnalysisSummary(result.Analyses);
            if (_document.Doc.EditVersion != _sourceEditVersion ||
                _rangeStart < 0 || _rangeStart + _rangeCount > _document.Doc.Length)
            {
                MessageBox.Show(this,
                    "The source document changed while the restoration was rendering. Nothing was applied. Use Re-analyze to scan the current audio.",
                    "Source changed", MessageBoxButton.OK, MessageBoxImage.Information);
                statusText.Text = "Source changed · restoration was not applied.";
                _sourceStale = true;
                UpdateStaleChrome();
                return;
            }

            operation.Token.ThrowIfCancellationRequested();
            _main.PrepareForDocumentEdit(_document);
            if (_rangeStart == 0 && _rangeCount == _document.Doc.Length)
                _document.Doc.ReplaceAllOwned(result.Audio, "Vinyl Restoration");
            else
                _document.Doc.ReplaceRange(_rangeStart, _rangeCount, result.Audio, "Vinyl Restoration");
            committed = true;
            progressBar.Value = 1;
            statusText.Text = "Restoration applied as one undoable edit.";
            if (result.Removed != null)
            {
                statusText.Text = _main.AddResidualDocument(_document.Doc, result.Removed,
                    "The restoration", _rangeStart, result.Levels)
                    ? "Restoration applied · what it removed is open in a second tab."
                    : "Restoration applied · it removed nothing audible, so no second tab was made.";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(operation)) statusText.Text = "Restoration cancelled · the document was not changed.";
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation))
            {
                MessageBox.Show(this, ex.Message, "Restoration failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                statusText.Text = "Restoration failed · the document was not changed.";
            }
        }
        finally
        {
            CompleteOperation(operation);
        }

        if (!committed || _closed) return;
        PrepareCdRequested = prepareCd;
        // The commit is itself an edit to the document, so this analysis is now stale by definition
        // and the window has nothing left to offer. Closed before the handler runs, so whatever it
        // opens next is not raised behind a window on its way out.
        Close();
        Applied?.Invoke(prepareCd);
    }

    private AnalysisBundle EnsureAnalyses(double sensitivity, CancellationToken cancellationToken,
        IProgress<RestorationProgress> progress)
    {
        if (_source == null)
            throw new InvalidOperationException("The restoration source is not ready.");
        _analysisGate.Wait(cancellationToken);
        try
        {
            if (_clickAnalysis == null || Math.Abs(_analyzedClickSensitivity - sensitivity) > 0.001)
            {
                _clickAnalysis = Restoration.AnalyzeClicks(_sourceReferences, _sampleRate,
                    new ClickAnalysisOptions
                    {
                        Sensitivity = sensitivity,
                        PreserveTransients = true,
                    }, cancellationToken, progress);
                _analyzedClickSensitivity = sensitivity;
            }

            _clippingAnalysis ??= Restoration.AnalyzeClipping(_sourceReferences, _sampleRate,
                new ClippingAnalysisOptions(), cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            return new AnalysisBundle(_clickAnalysis, _clippingAnalysis);
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private AnalysisBundle AnalysisForApplyRange(AnalysisBundle fullFile)
    {
        int absoluteEnd = checked(_rangeStart + _rangeCount);
        ClickEvent[] clicks = fullFile.Clicks.Events
            .Where(item => item.StartSample >= _rangeStart + 1 && item.EndSample < absoluteEnd)
            .Select(item => item with
            {
                StartSample = item.StartSample - _rangeStart,
                EndSample = item.EndSample - _rangeStart,
                PeakSample = item.PeakSample - _rangeStart,
            })
            .ToArray();
        ClippedPeakEvent[] clipping = fullFile.Clipping.Events
            .Where(item => item.StartSample >= _rangeStart + 1 && item.EndSample < absoluteEnd)
            .Select(item => item with
            {
                StartSample = item.StartSample - _rangeStart,
                EndSample = item.EndSample - _rangeStart,
                PeakSample = item.PeakSample - _rangeStart,
            })
            .ToArray();

        return new AnalysisBundle(
            new ClickAnalysisResult(clicks, _rangeCount, fullFile.Clicks.ChannelCount,
                fullFile.Clicks.SampleRate),
            new ClippingAnalysisResult(clipping, _rangeCount, fullFile.Clipping.ChannelCount,
                fullFile.Clipping.SampleRate, fullFile.Clipping.UsedAutomaticThreshold));
    }

    private float[][] RenderPreviewRange(RestorationSettings settings, AnalysisBundle analyses,
        CancellationToken cancellationToken, IProgress<OperationProgress> progress)
    {
        var source = _source ?? throw new InvalidOperationException("The restoration source is not ready.");
        analyses = AnalysisForApplyRange(analyses);
        int padding = Math.Max(Restoration.NrFftSize * 2, _sampleRate / 10);
        // The high-pass is an IIR and the de-crackler fits its models on a block grid anchored at
        // sample zero, so both need the lead-in for the same reason hum and the gate do: started
        // cold at a preview boundary one thumps and the other disagrees with the full render about
        // what is crackle. Neither was covered by the flat fallback pad.
        bool usesSubsonicState = !settings.Bypass && settings.WetAmount > 0 && settings.RemoveSubsonic;
        bool usesCrackleGrid = !settings.Bypass && settings.WetAmount > 0 && settings.Decrackle;
        bool needsContinuousState = !settings.Bypass && settings.WetAmount > 0 &&
            ((settings.RemoveHum && settings.HumAmount > 0) ||
             (settings.ReduceNoise && settings.NoiseReductionDb > 0 && _noiseProfile is { Length: > 0 }) ||
             usesSubsonicState || usesCrackleGrid);
        bool usesNoiseState = needsContinuousState && settings.ReduceNoise &&
                              settings.NoiseReductionDb > 0 && _noiseProfile is { Length: > 0 };
        bool usesHumState = needsContinuousState && settings.RemoveHum && settings.HumAmount > 0;
        var plan = needsContinuousState
            ? RestorationPreviewPlanning.Create(_previewStart, _sampleRate,
                usesHumState, settings.HumFrequency, settings.HumQ, usesNoiseState,
                usesSubsonicState, settings.SubsonicCutoffHz,
                usesCrackleGrid
                    ? Decrackle.BlockLengthFor(DecrackleOptions.Default with
                        { Threshold = settings.DecrackleThreshold })
                    : 0)
            : default;
        int bufferStart = needsContinuousState
            ? plan.StartSample
            : Math.Max(0, _previewStart - padding);
        int previewEnd = Math.Min(source[0].Length, _previewStart + _previewLength);
        int bufferEnd = Math.Min(source[0].Length, previewEnd + padding);
        if (needsContinuousState)
        {
            string leadIn = plan.StartsAtRangeOrigin
                ? $"Matching restoration state from the range start ({plan.WarmupSamples / (double)_sampleRate:0.#} s lead-in)…"
                : $"Warming a bounded {plan.WarmupSamples / (double)_sampleRate:0.#} s lead-in (state below {RestorationPreviewPlanning.StateResidualDb:0} dB)…";
            progress.Report(new OperationProgress(leadIn, 0.48));
        }
        var work = CopyChannels(source, bufferStart, bufferEnd - bufferStart, cancellationToken);

        var clicks = analyses.Clicks.Events
            .Where(e => e.StartSample >= bufferStart + 1 && e.EndSample < bufferEnd)
            .Select(e => e with
            {
                StartSample = e.StartSample - bufferStart,
                EndSample = e.EndSample - bufferStart,
                PeakSample = e.PeakSample - bufferStart,
            }).ToArray();
        var clipping = analyses.Clipping.Events
            .Where(e => e.StartSample >= bufferStart + 1 && e.EndSample < bufferEnd)
            .Select(e => e with
            {
                StartSample = e.StartSample - bufferStart,
                EndSample = e.EndSample - bufferStart,
                PeakSample = e.PeakSample - bufferStart,
            }).ToArray();

        var mixed = RenderOwnedWork(work, source, bufferStart, settings, clicks, clipping,
            cancellationToken, progress, 0.48, 0.48);
        int cropStart = _previewStart - bufferStart;
        return CopyChannels(mixed, cropStart, _previewLength, cancellationToken);
    }

    private float[][] BlendPreview(float[][] wetPreview, double wetAmount,
        CancellationToken cancellationToken)
    {
        float wet = (float)Math.Clamp(wetAmount, 0.0, 1.0);
        if (wet >= 1f) return wetPreview;
        if (wet <= 0f)
            return CopyChannels(_source!, _previewStart, _previewLength, cancellationToken);

        var mixed = new float[wetPreview.Length][];
        float dry = 1f - wet;
        for (int channel = 0; channel < wetPreview.Length; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wetChannel = wetPreview[channel];
            var dryChannel = _source![channel];
            var output = new float[wetChannel.Length];
            for (int sample = 0; sample < output.Length; sample++)
            {
                if ((sample & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                output[sample] = dryChannel[_previewStart + sample] * dry + wetChannel[sample] * wet;
            }
            mixed[channel] = output;
        }
        return mixed;
    }

    private float[][] RenderFullRange(RestorationSettings settings, AnalysisBundle analyses,
        CancellationToken cancellationToken, IProgress<OperationProgress> progress)
    {
        var source = _source ?? throw new InvalidOperationException("The restoration source is not ready.");
        analyses = AnalysisForApplyRange(analyses);
        var work = CopyChannels(source, 0, source[0].Length, cancellationToken);
        return RenderOwnedWork(work, source, 0, settings,
            analyses.Clicks.Events, analyses.Clipping.Events,
            cancellationToken, progress, 0.31, 0.67);
    }

    private float[][] RenderOwnedWork(float[][] work, IReadOnlyList<float[]> dry, int dryOffset,
        RestorationSettings settings,
        IReadOnlyList<ClickEvent> clicks, IReadOnlyList<ClippedPeakEvent> clipping,
        CancellationToken cancellationToken, IProgress<OperationProgress> progress,
        double progressStart, double progressSpan)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Eight slots, seven increments: the last is consumed by the dry/wet blend, which reports
        // and does not advance. Adding a stage without adding its `at += step` silently compresses
        // every bar after it.
        double step = progressSpan / 8.0;
        double at = progressStart;

        // The subsonic filter runs first so that everything measured downstream - the robust
        // scales inside the two autoregressive passes, and the levels on the cards - is measured
        // on the audible band rather than on rumble that can hold half the file's energy.
        // Measured, the placement does not change the repair itself; see Restoration.RemoveSubsonic.
        if (!settings.Bypass && settings.RemoveSubsonic)
        {
            progress.Report(new OperationProgress(
                $"Removing subsonic rumble below {settings.SubsonicCutoffHz:0} Hz…", at));
            Restoration.RemoveSubsonic(work, _sampleRate, settings.SubsonicCutoffHz, 1.0,
                cancellationToken);
        }
        at += step;

        // Then the vertical noise, and before the repairers rather than after them. Collapsing the
        // side is what turns one anti-phase tick into a single coherent event that one interpolator
        // can remove - left in place, each channel's model sees a different realisation of it and
        // summing the repaired channels back reconstitutes what the other one still holds.
        if (SideStageRuns(settings.Bypass, settings.ReduceSide, settings.SideLevel))
        {
            progress.Report(new OperationProgress(settings.SideLevel <= 0
                ? "Discarding the side signal and its vertical surface noise…"
                : $"Reducing the side signal to {settings.SideLevel:P0}…", at));
            Restoration.ScaleSide(work, settings.SideLevel, cancellationToken);
        }
        at += step;

        if (!settings.Bypass && settings.Declip && settings.DeclipStrength > 0)
        {
            progress.Report(new OperationProgress("Reconstructing clipped peaks…", at));
            Restoration.RepairClippingInPlace(work, clipping,
                new DeclippingOptions
                {
                    Strength = settings.DeclipStrength,
                    MaximumReconstructionDb = settings.DeclipHeadroomDb,
                    Method = settings.DeclipMethod,
                }, cancellationToken);
        }
        at += step;

        if (!settings.Bypass && settings.RepairClicks && settings.ClickStrength > 0)
        {
            progress.Report(new OperationProgress("Repairing analyzed clicks and pops…", at));
            Restoration.RepairClicksInPlace(work, clicks,
                new ClickRepairOptions
                {
                    Strength = settings.ClickStrength,
                    MaximumOvershoot = 1.2,
                }, cancellationToken);
        }
        at += step;

        // After click repair, and after the side collapse above, which is the ordering the
        // measurement is about: on the un-collapsed stereo file this stage moves almost nothing.
        //
        // <b>It is also, by a wide margin, the most expensive stage in this chain.</b> Measured on
        // five real transfers it runs at 0.19 to 0.36x realtime - 34 to 68 seconds for a three
        // minute side, against 142-190 ms for the high-pass and about 20 ms for the side scale -
        // because it repairs some 4% of every sample rather than a bounded list of events, and
        // Janssen costs about 35x a linear bridge. Over a twelve-second preview window the rest of
        // this chain costs 428-920 ms and this stage costs 2.6-5.7 s. So it gets the two things
        // that makes bearable: the channels run at once, and it reports where it has got to.
        if (!settings.Bypass && settings.Decrackle)
        {
            string crackleMessage =
                $"Removing surface crackle at {settings.DecrackleThreshold:0.0} deviations…";
            progress.Report(new OperationProgress(crackleMessage, at));

            if (work.Length == 2)
            {
                // Stereo: run detection in the side (L−R) signal where 78% of crackle
                // lives, and classify candidates against a musical-transient model so
                // cymbals and sibilance are left alone. The mid signal is never seen
                // by the detector.
                Decrackle.ProcessStereo(work[0], work[1],
                    DecrackleOptions.Default with { Threshold = settings.DecrackleThreshold },
                    cancellationToken,
                    new SimpleFractionProgress(crackleMessage, progress, at, step));
            }
            else
            {
                // Mono or multi-channel: per-channel fallback.
                double stageStart = at, stageSpan = step;
                var fractions = new double[work.Length];
                var crackleOptions = DecrackleOptions.Default with
                {
                    Threshold = settings.DecrackleThreshold,
                };
                Parallel.For(0, work.Length,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    channel => Decrackle.Process(work[channel], crackleOptions,
                        cancellationToken,
                        new ChannelFractionProgress(fractions, channel, progress,
                            crackleMessage, stageStart, stageSpan)));
            }
        }
        at += step;

        if (!settings.Bypass && settings.RemoveHum && settings.HumAmount > 0)
        {
            progress.Report(new OperationProgress(
                $"Removing {settings.HumFrequency:0} Hz hum and harmonics at {settings.HumAmount:P0}…", at));
            Restoration.RemoveHum(work, _sampleRate, settings.HumFrequency,
                settings.HumHarmonics, settings.HumQ, settings.HumAmount, cancellationToken);
        }
        at += step;

        if (!settings.Bypass && settings.ReduceNoise && settings.NoiseReductionDb > 0 &&
            _noiseProfile is { Length: > 0 } profile)
        {
            // Scaled by how far the programme sits above its own quietest passage, because a
            // fixed depth is measurably worse than doing nothing where the hiss is already far
            // down. The chosen depth is reported rather than applied silently - a tool quietly
            // ignoring most of a slider's travel is indistinguishable from one that is broken.
            // From the estimate taken when the analysis landed, not re-measured here. Two
            // reasons, and either alone would settle it. The readout on the card was computed from
            // that same number, and a report computed separately can disagree with what actually
            // ran - the declip readout carries the same note for the same reason. And `work` has
            // already been through click repair, declip and hum removal by this point, so its floor
            // is no longer the one the user was shown a figure for.
            double depth = Restoration.SuggestReductionDepthDb(
                _noiseToProgrammeDb ?? Restoration.EstimateNoiseToProgrammeDb(
                    work, _sampleRate, _noiseDepthCeilingDb),
                settings.NoiseReductionDb, _noiseDepthCeilingDb);
            // Naming the other tool matters here rather than being a courtesy. The rule is an RMS
            // ratio, and crackle is impulsive - a surface can be plainly audible while its floor
            // sits 24 dB under the programme, which is exactly the reading that switches this stage
            // off. The rule is right and the user is not wrong; they are looking at the wrong card.
            progress.Report(new OperationProgress(depth <= 0
                ? "Little hiss under the programme — leaving broadband noise alone; surface crackle is the other card…"
                : $"Reducing broadband surface noise and hiss at {depth:0.0} dB…", at));
            if (depth > 0)
                Restoration.ReduceNoise(work, profile, depth,
                    settings.NoiseSensitivityDb, cancellationToken);
        }
        at += step;

        progress.Report(new OperationProgress(settings.Bypass
            ? "Preparing dry A/B reference…"
            : "Blending restored and original audio…", at));
        if (settings.Bypass) return work;
        float wet = (float)Math.Clamp(settings.WetAmount, 0, 1);
        if (wet >= 1) return work;
        float dryGain = 1 - wet;
        for (int channel = 0; channel < work.Length; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int sample = 0; sample < work[channel].Length; sample++)
            {
                if ((sample & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                work[channel][sample] = dry[channel][dryOffset + sample] * dryGain + work[channel][sample] * wet;
            }
        }
        return work;
    }

    /// <summary>
    /// Combines several channels' independent progress into one stage fraction.
    /// </summary>
    /// <remarks>
    /// Each channel writes its own slot and reads all of them to take the mean, so the read races
    /// the other channels' writes. <b>That is deliberate and it is why this is not locked</b>: the
    /// value is a number on a progress bar that a 10 Hz timer samples, a stale slot moves it by at
    /// most one channel's share, and putting a lock between two worker threads to make a progress
    /// figure exact would cost more than the figure is worth. Not <see cref="Progress{T}"/>, for
    /// the reason <see cref="SubProgress"/> records: it would post every report to a
    /// synchronization context this thread does not have.
    /// </remarks>
    private sealed class ChannelFractionProgress(
        double[] fractions, int channel, IProgress<OperationProgress> outer,
        string message, double offset, double span) : IProgress<double>
    {
        public void Report(double value)
        {
            fractions[channel] = Math.Clamp(value, 0, 1);
            double total = 0;
            foreach (double fraction in fractions) total += fraction;
            outer.Report(new OperationProgress(message,
                Math.Clamp(offset + span * total / Math.Max(1, fractions.Length), 0, 1)));
        }
    }

    /// <summary>
    /// A single-channel progress adapter for the stereo de-crackle path, which runs
    /// sequentially in M/S space rather than one task per channel.
    /// </summary>
    private sealed class SimpleFractionProgress(
        string message, IProgress<OperationProgress> outer,
        double offset, double span) : IProgress<double>
    {
        public void Report(double value) =>
            outer.Report(new OperationProgress(message,
                Math.Clamp(offset + span * Math.Clamp(value, 0, 1), 0, 1)));
    }

    private static float[][] CopyChannels(IReadOnlyList<float[]> source, int start, int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (start < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(start));
        var result = new float[source.Count][];
        const int chunk = 1 << 20;
        for (int channel = 0; channel < source.Count; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = source[channel] ?? throw new ArgumentException("Audio channels cannot be null.");
            if (start + count > input.Length) throw new ArgumentOutOfRangeException(nameof(count));
            var output = new float[count];
            for (int offset = 0; offset < count; offset += chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(chunk, count - offset);
                Array.Copy(input, start + offset, output, offset, length);
            }
            result[channel] = output;
        }
        return result;
    }

    private static NoiseProfileResult BuildAutomaticNoiseProfile(float[][] source, int sampleRate,
        CancellationToken cancellationToken)
    {
        if (source.Length == 0 || source[0].Length < Restoration.NrFftSize)
            return new NoiseProfileResult(null, 0, true);

        int sampleCount = source[0].Length;
        int windowLength = Math.Min(sampleCount,
            Math.Max(Restoration.NrFftSize, checked(sampleRate * 2)));
        int hop = Math.Min(windowLength, 4096);

        static double EnergyAt(IReadOnlyList<float[]> channels, int sample)
        {
            double energy = 0;
            for (int channel = 0; channel < channels.Count; channel++)
            {
                double value = channels[channel][sample];
                energy += value * value;
            }
            return energy / Math.Max(1, channels.Count);
        }

        double rollingEnergy = 0;
        for (int i = 0; i < windowLength; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            rollingEnergy += EnergyAt(source, i);
        }
        double quietestEnergy = rollingEnergy;
        int quietestStart = 0;
        int previousStart = 0;
        for (int start = hop; start + windowLength <= sampleCount; start += hop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = previousStart; i < start; i++) rollingEnergy -= EnergyAt(source, i);
            int previousEnd = previousStart + windowLength;
            int nextEnd = start + windowLength;
            for (int i = previousEnd; i < nextEnd; i++) rollingEnergy += EnergyAt(source, i);
            if (rollingEnergy < quietestEnergy)
            {
                quietestEnergy = rollingEnergy;
                quietestStart = start;
            }
            previousStart = start;
        }

        var profile = Restoration.LearnNoiseProfile(source, quietestStart, windowLength,
            cancellationToken);
        return profile.Any(value => value > 0)
            ? new NoiseProfileResult(profile, quietestStart, true)
            : new NoiseProfileResult(null, quietestStart, true);
    }

    private void ApplyAnalysisRecommendations(RestorationRecommendations.Settings recommendations)
    {
        _suppressControlEvents = true;
        try
        {
            clickEnabled.IsChecked = recommendations.RepairClicks;
            clickSensitivity.Value = recommendations.ClickSensitivity;
            clickStrength.Value = recommendations.ClickStrength * 100;
            declipEnabled.IsChecked = recommendations.Declip;
            declipStrength.Value = recommendations.DeclipStrength * 100;
            declipHeadroom.Value = recommendations.DeclipHeadroomDb;
            noiseEnabled.IsChecked = recommendations.ReduceNoise && _noiseProfile != null;
            noiseReduction.Value = recommendations.NoiseReductionDb;
            noiseSensitivity.Value = recommendations.NoiseSensitivityDb;
            humEnabled.IsChecked = recommendations.RemoveHum;
            humStrength.Value = recommendations.HumAmount * 100;
            humFrequency.SelectedIndex = recommendations.HumFrequency == 50 ? 0 : 1;
            humHarmonics.Value = recommendations.HumHarmonics;
            humQ.Value = recommendations.HumQ;
            subsonicEnabled.IsChecked = recommendations.HighPass;
            subsonicCutoff.Value = recommendations.HighPassCutoffHz;
            // The side control is enabled only where there is something to do. Recommending
            // "on at 100%" would be a stage that runs and changes nothing, and recommending
            // "off at 0%" would hide a collapse behind a switch.
            verticalEnabled.IsChecked = recommendations.SideLevel < 1.0;
            sideLevel.Value = recommendations.SideLevel * 100;
            decrackleEnabled.IsChecked = recommendations.Decrackle;
            decrackleThreshold.Value = recommendations.DecrackleThreshold;
            if (presetCombo.Items.Count >= 5)
                presetCombo.SelectedIndex = 4;
        }
        finally
        {
            _suppressControlEvents = false;
        }
        UpdateReadouts();
        _previewWetCacheSettings = null;
        _previewWetCache = null;
    }

    private DeclipMethod SelectedDeclipMethod() =>
        declipSparse.IsChecked == true ? DeclipMethod.Sparse
        : declipPeaks.IsChecked == true ? DeclipMethod.PeakReconstruction
        : DeclipMethod.Automatic;

    /// <summary>
    /// Three segments, one choice. WPF toggle buttons are independent, so the exclusion is here —
    /// and clicking the checked one leaves it checked rather than turning the whole control off,
    /// because "no method" is not a state the repair has.
    /// </summary>
    private void OnDeclipMethodChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressControlEvents) return;
        var clicked = (ToggleButton)sender;

        _suppressControlEvents = true;
        try
        {
            clicked.IsChecked = true;
            foreach (var segment in (ToggleButton[])[declipAuto, declipSparse, declipPeaks])
                if (!ReferenceEquals(segment, clicked)) segment.IsChecked = false;
        }
        finally
        {
            _suppressControlEvents = false;
        }

        UpdateDeclipMethodReadout();
        OnParameterChanged(sender, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    /// <summary>
    /// Says which method will run and, for the automatic choice, the two numbers it was made from.
    /// A-SPADE costs about 700× the peak reconstruction, so a side that suddenly takes minutes needs
    /// an explanation that can be checked rather than taken on trust.
    /// </summary>
    private void UpdateDeclipMethodReadout()
    {
        if (declipMethodText == null) return;

        var clipping = _analysisForReadout;
        if (_source == null || clipping == null)
        {
            declipMethodText.Text = "Run analysis to see the choice.";
            return;
        }
        if (clipping.Events.Count == 0)
        {
            declipMethodText.Text = "No clipping detected.";
            return;
        }

        DeclipMethod requested = SelectedDeclipMethod();
        if (requested != DeclipMethod.Automatic)
        {
            declipMethodText.Text = requested == DeclipMethod.Sparse
                ? "Sparse on every channel."
                : "Peak reconstruction on every channel.";
            return;
        }

        IReadOnlyList<DeclipChannelChoice> choices;
        try
        {
            choices = Restoration.DescribeDeclipChoices(_source, clipping.Events);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (choices.Count == 0)
        {
            declipMethodText.Text = "No clipping detected.";
            return;
        }

        declipMethodText.Text = DescribeChoices(choices);
        declipMethodText.ToolTip = string.Join(Environment.NewLine,
            choices.Select(c => $"Channel {c.Channel + 1}: {MethodName(c)} — {FullDetail(c)}"));
    }


    // ── the noise depth readout ──────────────────────────────────

    /// <summary>How far the programme sits above its own floor, cached from the analysed audio.</summary>
    /// <remarks>
    /// The estimate depends on the audio and not on any control, so it is measured once when the
    /// analysis lands rather than on every drag of a slider - it walks the whole side.
    /// </remarks>
    private double? _noiseToProgrammeDb;

    /// <summary>Whether this workbench covers the whole document rather than a selection.</summary>
    private bool WholeDocumentRange =>
        _rangeStart == 0 && _sourceReferences.Length > 0 &&
        _rangeCount == _sourceReferences[0].Length;

    /// <summary>The ceiling the cached estimate was taken under, and the render will run at.</summary>
    /// <remarks>
    /// Read once, with the estimate, rather than from <see cref="AppSettings"/> at each use. The
    /// estimate expresses "no reading" <em>in</em> the ceiling, so a pair taken under different
    /// ceilings is not a pair — and the readout would then be describing a depth the render did not
    /// apply, which is the disagreement every readout in this dialog is arranged to prevent.
    /// </remarks>
    private double _noiseDepthCeilingDb = Restoration.NoiseDepthCeilingDb;

    /// <summary>The two halves of the readout line, so the verb can be coloured on its own.</summary>
    /// <param name="Lead">Applied depth, or the reason nothing is being applied.</param>
    /// <param name="Detail">The measured number the decision was made from.</param>
    /// <param name="Declining">Whether the tool is about to leave the audio alone.</param>
    internal readonly record struct NoiseDepthLine(string Lead, string Detail, bool Declining)
    {
        public override string ToString() =>
            Detail.Length == 0 ? Lead : Lead.Length == 0 ? Detail : $"{Lead} \u00b7 {Detail}";
    }

    /// <summary>
    /// Says how much reduction will actually be applied, and the number that decided it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this the slider is a control whose travel does nothing.</b> The depth follows how
    /// much hiss there is to remove, so on a clean transfer the tool is right to ignore most of the
    /// slider - measured, a fixed depth is worse than leaving the audio alone on 46 of 108 corpus
    /// cells and scaling it takes that to 15. A tool that is right to do nothing still has to be
    /// seen doing it, which is the same argument as the declip readout above.
    /// </para>
    /// <para>
    /// Pure, so the wording is unit-tested without a window - the declip readout is written the same
    /// way and for the same reason.
    /// </para>
    /// </remarks>
    internal static NoiseDepthLine DescribeNoiseDepth(bool enabled, bool analysed, bool hasProfile,
        double requestedDb, double appliedDb, double estimateDb)
    {
        // The card's own switch comes first, or the line reports a depth for a stage that will not
        // run at all - the same disagreement between what is shown and what happens that the rest
        // of this readout exists to prevent.
        if (!enabled) return new NoiseDepthLine("Not reducing", "this card is switched off.", true);
        if (!analysed) return new NoiseDepthLine("Run analysis to see the depth.", "", false);
        if (!hasProfile) return new NoiseDepthLine("Learn a noise profile to reduce hiss.", "", false);
        if (requestedDb <= 0)
            return new NoiseDepthLine("Not reducing", "the maximum is set to zero.", true);

        string measured = $"hiss {(appliedDb <= 0 ? "already " : "sits ")}{estimateDb:0.0} dB under the programme.";
        return appliedDb <= 0
            ? new NoiseDepthLine("Not reducing", measured, true)
            : new NoiseDepthLine($"Applying {appliedDb:0.0} dB", measured, false);
    }


    // ── the output-mix ceiling readout ────────────────────────

    /// <summary>The two halves of the output-mix line, so the verb can be coloured on its own.</summary>
    /// <param name="Lead">The ceiling, or the reason there is not one.</param>
    /// <param name="Detail">What the mix returns, and over what.</param>
    /// <param name="Inert">Whether the chain's work is not reaching the output at all.</param>
    internal readonly record struct OutputMixLine(string Lead, string Detail, bool Inert)
    {
        public override string ToString() =>
            Detail.Length == 0 ? Lead : Lead.Length == 0 ? Detail : $"{Lead} · {Detail}";
    }

    /// <summary>
    /// Says what the output mix costs every stage in the chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The blend is applied once, to the whole chain output</b>, so the dry share it returns is a
    /// hard ceiling on what any stage can achieve: at the shipped 90% default nothing can be reduced
    /// by more than <b>20 dB</b> however well it works. Measured on a real transfer, a 30 Hz
    /// high-pass that takes <b>40 dB</b> off 10 Hz on its own lands at <b>19.7</b> through the
    /// dialog, and the notch bank's measured 42 dB of hum is capped at the same 20. The stage that
    /// looks weak is not weak, and nothing on screen said so.
    /// </para>
    /// <para>
    /// <b>The ceiling is a fact rather than a fault, so it is not coloured.</b> Amber is kept for
    /// the two states where the chain's work does not reach the output at all — bypassed, and a
    /// fully dry mix — which is the rule the VST3 scanner note records as the reason not to colour
    /// an ordinary reading like a warning.
    /// </para>
    /// <para>
    /// Pure, so the wording is unit-tested without a window, exactly as
    /// <see cref="DescribeNoiseDepth"/> and <c>DescribeChoices</c> are.
    /// </para>
    /// </remarks>
    internal static OutputMixLine DescribeOutputMix(double wetAmount, bool bypass)
    {
        // Bypass comes first, or the line quotes a ceiling for a chain that is not running - the
        // same disagreement between what is shown and what happens the noise readout guards against.
        if (bypass) return new OutputMixLine("Bypassed", "the mix does nothing while the chain is off.", true);

        double wet = double.IsFinite(wetAmount) ? Math.Clamp(wetAmount, 0, 1) : 1;
        if (wet >= 1) return new OutputMixLine("No ceiling", "every stage applies in full.", false);
        if (wet <= 0) return new OutputMixLine("Fully dry", "nothing the chain removes reaches the output.", true);

        double dryPercent = (1 - wet) * 100;
        // Under one percent the rounded figure would read "0% dry returns", which says the opposite
        // of the ceiling beside it. The slider is continuous, so that value is reachable.
        string returns = dryPercent >= 1 ? $"{dryPercent:0}% dry returns" : "under 1% dry returns";
        return new OutputMixLine($"Ceiling {-20 * Math.Log10(1 - wet):0.0} dB",
            $"{returns} over every stage.", false);
    }

    private void UpdateOutputMixReadout()
    {
        OutputMixLine line = DescribeOutputMix(globalMix.Value / 100.0, bypassCheck.IsChecked == true);
        mixCeilingLead.Text = line.Lead;
        mixCeilingDetail.Text = line.Detail.Length == 0 ? "" : $" · {line.Detail}";
        mixCeilingLead.Foreground = line.Inert
            ? (Brush)FindResource("Amber")
            : (Brush)FindResource("Muted");
        mixCeilingText.ToolTip =
            "The mix is applied once, to the whole chain output, so whatever share of the original "
            + "it returns is a floor under everything the chain removed. A stage that measures 40 dB "
            + "on its own reaches 20 through a 90% mix.";
    }

    // ── the vertical-noise and rumble readouts ────────────────

    /// <summary>Side-to-mid over the programme, cached from the analysed audio.</summary>
    /// <remarks>Cached for the same reason as <see cref="_noiseToProgrammeDb"/>: it is a property
    /// of the audio, not of any control, and it walks the whole side.</remarks>
    private double? _sideToMidDb;

    /// <summary>The rumble sentence the cleanup analyzer already wrote, and its confidence.</summary>
    private string? _rumbleEvidence;
    private double _rumbleConfidence;

    /// <summary>Whether the click analysis found impulses — the evidence de-crackle rides on.</summary>
    private int _impulsesFound;

    /// <summary>
    /// Says what the side control will do and the measurement that decided how far it may go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two different facts, and reporting only one of them would be misleading either way.</b>
    /// That the surface noise is vertical is true of every record measured here; that the side may
    /// be discarded is true only where the disc was cut mono. A line saying "the noise is in the
    /// side" invites collapsing a stereo record, and a line saying only "mono pressing" hides why
    /// the control exists at all.
    /// </para>
    /// <para>Pure, so the wording is unit-tested without a window.</para>
    /// </remarks>
    /// <summary>Whether the side-reduction stage will touch the audio at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because the render and the card disagreed.</b> Every other card on this dialog
    /// puts its Enabled box into <c>RestorationSettings</c> and the render reads it; this one did
    /// not, so <c>verticalEnabled</c> reached the evidence line and nothing else. Unticking it
    /// changed the caption to "the side signal is untouched" while <c>ScaleSide</c> went on reducing
    /// the side — the caption was not merely stale, it was false, on the one card that can throw
    /// away half of a stereo record.
    /// </para>
    /// <para>
    /// Pure and named, rather than three terms inlined at the call site, so the condition the
    /// caption implies can be asserted against the condition the render uses. A level of 1.0 is
    /// still nothing to do, whatever the box says: a stage that runs and multiplies by one is the
    /// case <see cref="ApplyAnalysisRecommendations"/> already refuses to recommend.
    /// </para>
    /// </remarks>
    internal static bool SideStageRuns(bool bypass, bool enabled, double sideLevel) =>
        !bypass && enabled && sideLevel < 1.0;

    internal static string DescribeSideLevel(bool enabled, bool analysed, bool stereo,
        double sideToMidDb, double level, bool wholeFile = true)
    {
        if (!stereo) return "This document is mono; there is no side signal to reduce.";
        if (!analysed) return "Run analysis to measure the side signal.";
        if (!enabled) return "This card is switched off; the side signal is untouched.";

        string pressing = sideToMidDb <= RestorationRecommendations.MonoPressingSideToMidDb
            ? $"The side sits {-sideToMidDb:0.0} dB under the mid over the programme, so this was cut mono and its side carries surface noise rather than music."
            : sideToMidDb >= RestorationRecommendations.StereoSideToMidDb
                ? $"The side sits {-sideToMidDb:0.0} dB under the mid over the programme, which is real stereo content — reducing it narrows the image as well as the noise."
                : $"The side sits {-sideToMidDb:0.0} dB under the mid over the programme, between a mono pressing and a stereo one, so some of what goes is music.";

        // A range restoration changes the stereo image inside the range and not outside it, and
        // the image snapping at the boundary is far more audible than a notch or a gate doing the
        // same thing there. The other stages share the property; this is the one where it is worth
        // saying out loud.
        string seam = wholeFile || level >= 1.0
            ? ""
            : " Restoring a selection rather than the whole file, so the stereo image will change"
              + " at its edges — the side is only reduced inside the range.";

        return level >= 1.0
            ? $"Leaving the side at full. {pressing}"
            : level <= 0
                ? $"Discarding the side entirely. {pressing}{seam}"
                : $"Reducing the side by {-20 * Math.Log10(Math.Max(level, 1e-6)):0.0} dB. {pressing}{seam}";
    }

    /// <summary>
    /// Says what the de-crackle recommendation was made from, because it is weaker evidence than
    /// the other stages have.
    /// </summary>
    /// <remarks>
    /// Every other card on this dialog is switched on by a measurement of the thing it removes.
    /// This one is not: crackle sits below the click detector's reach by definition, so nothing
    /// here counts it, and the recommendation rides on impulses having been found at all. <b>Saying
    /// so is the point of the line</b> — a control that turns itself on for a reason the user
    /// cannot see is one they cannot judge.
    /// </remarks>
    internal static string DescribeCrackle(bool enabled, bool analysed, int impulsesFound,
        double threshold)
    {
        if (!analysed) return "Run analysis to see what this went on.";
        if (!enabled) return "This card is switched off.";

        string basis = impulsesFound > 0
            ? $"Recommended because the click analysis found {impulsesFound:N0} impulses, so the surface sheds them; the crackle below that is not counted."
            : "No impulses were found, so there is no evidence of a shedding surface here.";
        string caution = threshold < 3.0
            ? " Below 3.0σ this repairs twice as many samples for a worse result — measured, 2.5σ left more audible ticks than 3.5σ did."
            : "";
        return $"{basis}{caution}";
    }

    private void UpdateVerticalNoiseReadouts()
    {
        if (sideEvidenceText == null) return;

        bool analysed = _source != null;
        bool stereo = _source is { Length: >= 2 };
        sideEvidenceText.Text = DescribeSideLevel(verticalEnabled.IsChecked == true, analysed,
            stereo, _sideToMidDb ?? 0, sideLevel.Value / 100.0, WholeDocumentRange);
        decrackleEvidenceText.Text = DescribeCrackle(decrackleEnabled.IsChecked == true, analysed,
            _impulsesFound, decrackleThreshold.Value);
        subsonicEvidenceText.Text = !analysed
            ? "Run analysis to measure the subsonic band."
            : subsonicEnabled.IsChecked != true
                ? "This card is switched off."
                : _rumbleEvidence ?? "No persistent subsonic rumble was separated from musical bass.";
        subsonicEvidenceText.ToolTip = analysed && _rumbleConfidence > 0
            ? $"Rumble confidence {_rumbleConfidence:P0}."
            : null;
    }

    private void UpdateNoiseDepthReadout()
    {
        if (noiseDepthText == null) return;

        bool analysed = _source != null && _noiseToProgrammeDb.HasValue;
        bool hasProfile = _noiseProfile is { Length: > 0 };
        double estimate = _noiseToProgrammeDb ?? 0;
        double requested = noiseReduction.Value;

        // The cached estimate, never a fresh measurement: this runs on the dispatcher on every
        // movement of every slider in the dialog, and measuring costs 388 ms on a 22-minute side.
        double applied = analysed && hasProfile
            ? Restoration.SuggestReductionDepthDb(estimate, requested, _noiseDepthCeilingDb)
            : 0;

        NoiseDepthLine line = DescribeNoiseDepth(noiseEnabled.IsChecked == true,
            analysed, hasProfile, requested, applied, estimate);
        noiseDepthLead.Text = line.Lead;
        noiseDepthDetail.Text = line.Detail.Length == 0 ? "" : $" \u00b7 {line.Detail}";

        // Amber on the verb alone. Declining is a decision, not a fault, and colouring a whole line
        // like a warning is what the VST3 scanner note records as teaching users to distrust the
        // colour - so the measured half stays in the ordinary muted grey.
        noiseDepthLead.Foreground = line.Declining
            ? (Brush)FindResource("Amber")
            : (Brush)FindResource("Muted");
        noiseDepthText.ToolTip = analysed && hasProfile
            ? $"The slider is a ceiling. Measured, a fixed depth applied to hiss already far under "
              + $"the programme costs more music than it saves noise, so the depth follows the "
              + $"programme-to-floor ratio \u2014 here {estimate:0.0} dB."
            : null;
    }

    private static string MethodName(DeclipChannelChoice choice) =>
        choice.Method == DeclipMethod.Sparse ? "sparse" : "peaks";

    /// <summary>The two numbers in full, for the tool tip, which has room for them.</summary>
    private static string FullDetail(DeclipChannelChoice choice) =>
        $"{Percent(choice)} clipped, runs of {Runs(choice)}";

    /// <summary>The same two numbers clipped to a bounded width, for the line.</summary>
    private static string ShortDetail(DeclipChannelChoice choice) =>
        $"{Percent(choice)}, runs {Runs(choice)}";

    private static string Percent(DeclipChannelChoice choice) =>
        $"{Math.Min(100, choice.ClippedFraction * 100):0.#}%";

    // Bounded so the sentence below cannot be pushed past the card by one badly damaged channel. A
    // mean run of a thousand samples is already a fifty-millisecond flat top; the exact figure past
    // that tells nobody anything the tool tip does not.
    private static string Runs(DeclipChannelChoice choice) =>
        choice.MeanRunSamples >= 999.5 ? "999+" : $"{choice.MeanRunSamples:0}";

    /// <summary>
    /// The readout line: what will run, and the numbers it was decided from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A split is the surprising case and the one worth explaining — one channel about to take
    /// several minutes while the other takes a second — so it keeps the evidence rather than
    /// dropping it. <b>The line this replaced dropped it on the grounds that two channels' numbers
    /// would not fit, and they do</b>: rendered at the dialog's 860 px minimum the readout has
    /// 365 px, and the two-channel sentence wants 329, or 346 with both channels pinned at the
    /// bounded worst case.
    /// </para>
    /// <para>
    /// Past two channels it does not fit — three wants 507 px — so the channels are grouped by
    /// method and the figures fall back to the tool tip, and past eight they are counted rather
    /// than listed, because sixteen numbers want 371. Every width here is measured off the real
    /// control rather than estimated.
    /// </para>
    /// </remarks>
    internal static string DescribeChoices(IReadOnlyList<DeclipChannelChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0) return "No clipping detected.";

        if (choices.All(c => c.Method == choices[0].Method))
            return $"Chose {MethodName(choices[0])} · {FullDetail(choices[0])}.";

        if (choices.Count == 2)
            return $"Chose {MethodName(choices[0])} on {choices[0].Channel + 1} ({ShortDetail(choices[0])}), " +
                   $"{MethodName(choices[1])} on {choices[1].Channel + 1} ({ShortDetail(choices[1])}).";

        var sparse = choices.Where(c => c.Method == DeclipMethod.Sparse).ToList();
        var peaks = choices.Where(c => c.Method != DeclipMethod.Sparse).ToList();
        static string List(IEnumerable<DeclipChannelChoice> group) =>
            string.Join(", ", group.Select(c => (c.Channel + 1).ToString()));

        return choices.Count <= 8
            ? $"Chose sparse on {List(sparse)} and peaks on {List(peaks)}."
            : $"Chose sparse on {sparse.Count} channels and peaks on {peaks.Count}.";
    }

    private RestorationSettings CaptureSettings() => new(
        clickEnabled.IsChecked == true,
        clickSensitivity.Value,
        clickStrength.Value / 100.0,
        declipEnabled.IsChecked == true,
        declipStrength.Value / 100.0,
        declipHeadroom.Value,
        SelectedDeclipMethod(),
        noiseEnabled.IsChecked == true,
        noiseReduction.Value,
        noiseSensitivity.Value,
        humEnabled.IsChecked == true,
        humStrength.Value / 100.0,
        humFrequency.SelectedIndex == 0 ? 50.0 : 60.0,
        (int)Math.Round(humHarmonics.Value),
        humQ.Value,
        subsonicEnabled.IsChecked == true,
        subsonicCutoff.Value,
        verticalEnabled.IsChecked == true,
        sideLevel.Value / 100.0,
        decrackleEnabled.IsChecked == true,
        decrackleThreshold.Value,
        globalMix.Value / 100.0,
        bypassCheck.IsChecked == true);

    /// <summary>The clipping analysis the method readout describes, so the two cannot disagree.</summary>
    private ClippingAnalysisResult? _analysisForReadout;

    private void UpdateAnalysisSummary(AnalysisBundle analyses)
    {
        _analysisForReadout = analyses.Clipping;
        UpdateDeclipMethodReadout();

        int clicks = analyses.Clicks.ClickCount;
        int pops = analyses.Clicks.PopCount;
        clickCountText.Text = $"{clicks:N0} {Plural(clicks, "click", "clicks")} · {pops:N0} {Plural(pops, "pop", "pops")}";
        int clipped = analyses.Clipping.Events.Count;
        clipCountText.Text = clipped == 0
            ? "No flat-topped peaks detected"
            : $"{clipped:N0} clipped {Plural(clipped, "peak", "peaks")}";
    }

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;

    private void OnParameterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        UpdateReadouts();
        MarkPresetCustom();
        QueueParameterRefresh();
    }

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        MarkPresetCustom();
        QueueParameterRefresh();
    }

    private void OnBypassChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        UpdateUiState();
        // The line reports bypass, and nothing else here recomputes it.
        UpdateOutputMixReadout();
        if (_previewStarted) QueueParameterRefresh(shortDelay: true);
    }

    /// <summary>
    /// Remembered across sessions, because the person who wants to hear what a repair took out
    /// wants that for a stack of records rather than for one side. It touches no audio, so it
    /// deliberately does not re-render the preview or mark the preset custom.
    /// </summary>
    private void OnKeepRemovedChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var settings = AppSettings.Instance;
        settings.KeepRemovedMaterial = keepRemovedCheck.IsChecked == true;
        settings.Save();
    }

    private void OnLivePreviewChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || livePreviewCheck.IsChecked != true || !_previewStarted) return;
        QueueParameterRefresh(shortDelay: true);
    }

    private void OnAuditionChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || !_previewStarted) return;
        QueueParameterRefresh(shortDelay: true);
    }

    private RestorationAuditionMode CaptureAuditionMode() =>
        auditionChannelCombo.SelectedIndex switch
        {
            1 when _sourceReferences.Length >= 2 => RestorationAuditionMode.Left,
            2 when _sourceReferences.Length >= 2 => RestorationAuditionMode.Right,
            _ => RestorationAuditionMode.Stereo,
        };

    private static string AuditionDescription(RestorationAuditionMode mode) => mode switch
    {
        RestorationAuditionMode.Left => "left channel soloed to both speakers",
        RestorationAuditionMode.Right => "right channel soloed to both speakers",
        _ => "stereo",
    };

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressControlEvents) return;
        if (presetCombo.SelectedIndex == 4)
        {
            if (_analysisRecommendations != null)
            {
                ApplyAnalysisRecommendations(_analysisRecommendations);
                QueueParameterRefresh();
            }
            return;
        }
        if (presetCombo.SelectedIndex is < 0 or > 2) return;
        _suppressControlEvents = true;
        try
        {
            clickEnabled.IsChecked = declipEnabled.IsChecked = true;
            noiseEnabled.IsChecked = _noiseProfile != null;
            // The three stages added after these presets were written must be set here too. A
            // control the preset does not touch keeps whatever the Analyzed pass left, so "Gentle"
            // would quietly carry a Strong-analysis high-pass and a discarded side channel.
            //
            // The side stays where the analysis put it and the presets do not reach for it: how
            // far it may go is a fact about the pressing, which a strength preset knows nothing
            // about, and collapsing a stereo record is not a thing "Strong" should mean.
            subsonicEnabled.IsChecked = true;
            subsonicCutoff.Value = 30;
            decrackleEnabled.IsChecked = true;
            switch (presetCombo.SelectedIndex)
            {
                case 0: // Gentle
                    humEnabled.IsChecked = false;
                    decrackleThreshold.Value = 4.5;
                    clickSensitivity.Value = 4;
                    clickStrength.Value = 55;
                    declipStrength.Value = 50;
                    declipHeadroom.Value = 4;
                    noiseReduction.Value = 6;
                    noiseSensitivity.Value = 3;
                    humStrength.Value = 40;
                    humHarmonics.Value = 3;
                    humQ.Value = 38;
                    globalMix.Value = 80;
                    break;
                case 1: // Balanced
                    humEnabled.IsChecked = false;
                    decrackleThreshold.Value = 3.5;
                    clickSensitivity.Value = 6;
                    clickStrength.Value = 75;
                    declipStrength.Value = 70;
                    declipHeadroom.Value = 6;
                    noiseReduction.Value = 10;
                    noiseSensitivity.Value = 5;
                    humStrength.Value = 65;
                    humHarmonics.Value = 4;
                    humQ.Value = 35;
                    globalMix.Value = 90;
                    break;
                case 2: // Strong
                    // Not below 3.0: measured, 2.5 deviations repairs twice as many samples and
                    // leaves more audible ticks than 3.5 does. "Strong" stops where the tool does.
                    decrackleThreshold.Value = 3.0;
                    humEnabled.IsChecked = true;
                    clickSensitivity.Value = 8;
                    clickStrength.Value = 92;
                    declipStrength.Value = 90;
                    declipHeadroom.Value = 8;
                    noiseReduction.Value = 16;
                    noiseSensitivity.Value = 8;
                    humStrength.Value = 85;
                    humHarmonics.Value = 5;
                    humQ.Value = 42;
                    globalMix.Value = 100;
                    break;
            }
        }
        finally
        {
            _suppressControlEvents = false;
        }
        UpdateReadouts();
        QueueParameterRefresh();
    }

    private void MarkPresetCustom()
    {
        if (_suppressControlEvents || presetCombo.Items.Count < 4) return;
        _suppressControlEvents = true;
        presetCombo.SelectedIndex = 3;
        _suppressControlEvents = false;
    }

    private void UpdateReadouts()
    {
        clickSensitivityText.Text = $"{clickSensitivity.Value:0.0} / 10";
        clickStrengthText.Text = $"{clickStrength.Value:0}%";
        declipStrengthText.Text = $"{declipStrength.Value:0}%";
        declipHeadroomText.Text = $"+{declipHeadroom.Value:0.0} dB";
        noiseReductionText.Text = $"{noiseReduction.Value:0.0} dB";
        noiseSensitivityText.Text = $"+{noiseSensitivity.Value:0.0} dB";
        humStrengthText.Text = $"{humStrength.Value:0}%";
        humHarmonicsText.Text = $"{humHarmonics.Value:0}";
        humQText.Text = $"Q {humQ.Value:0}";
        mixText.Text = $"{globalMix.Value:0}% restored";
        UpdateOutputMixReadout();
        subsonicCutoffText.Text = $"{subsonicCutoff.Value:0} Hz";
        sideLevelText.Text = $"{sideLevel.Value:0}%";
        decrackleThresholdText.Text = $"{decrackleThreshold.Value:0.0}σ";
        UpdateNoiseDepthReadout();
        UpdateVerticalNoiseReadouts();
    }

    private void QueueParameterRefresh(bool shortDelay = false)
    {
        if (_suppressControlEvents || _source == null || _applying || _closed) return;
        _previewDebounce.Stop();
        _previewDebounce.Interval = TimeSpan.FromMilliseconds(shortDelay ? 120 : 360);
        _previewDebounce.Start();
    }

    private async void OnPreviewDebounce(object? sender, EventArgs e)
    {
        _previewDebounce.Stop();
        if (_previewStarted && livePreviewCheck.IsChecked == true)
        {
            await RenderPreviewAsync();
        }
        else if (Math.Abs(_analyzedClickSensitivity - clickSensitivity.Value) > 0.001)
        {
            await RefreshAnalysisAsync();
        }
    }

    private async Task RefreshAnalysisAsync()
    {
        if (_source == null || _applying || _closed) return;
        var operation = BeginOperation(applying: false, "Refreshing click and pop analysis…");
        var progress = CreateProgress(operation);
        try
        {
            double sensitivity = clickSensitivity.Value;
            var analyses = await Task.Run(() => EnsureAnalyses(sensitivity, operation.Token,
                new DspProgressAdapter(progress, 0.02, 0.94)), operation.Token);
            if (!IsCurrent(operation)) return;
            UpdateAnalysisSummary(analyses);
            // The crackle card's recommendation rides on this count, so it has to follow the
            // analysis that actually ran. Set only in OnLoaded, it kept quoting the first pass
            // while the header showed the second - a readout disagreeing with its own analysis,
            // which is the thing every readout in this dialog is arranged to prevent.
            _impulsesFound = analyses.Clicks.Events.Count;
            UpdateVerticalNoiseReadouts();
            statusText.Text = "Analysis refreshed · press Preview to audition the current settings.";
            progressBar.Value = 1;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(operation)) statusText.Text = "Analysis refresh cancelled.";
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation)) statusText.Text = ex.Message;
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private CancellationTokenSource BeginOperation(bool applying, string status)
    {
        _previewDebounce.Stop();
        _operation?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        _busy = true;
        _applying = applying;
        statusText.Text = status;
        progressBar.Value = 0;
        UpdateUiState();
        return operation;
    }

    private void CompleteOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operation, operation))
        {
            _operation = null;
            _busy = false;
            _applying = false;
            UpdateUiState();
        }
        operation.Dispose();
        if (!_busy && _closeWhenFinished && !_closed)
        {
            _closeWhenFinished = false;
            Close();
        }
    }

    private bool IsCurrent(CancellationTokenSource operation) =>
        !_closed && ReferenceEquals(_operation, operation) && !operation.IsCancellationRequested;

    private IProgress<OperationProgress> CreateProgress(CancellationTokenSource operation) =>
        new Progress<OperationProgress>(update =>
        {
            if (!IsCurrent(operation)) return;
            statusText.Text = update.Text;
            progressBar.Value = Math.Clamp(update.Fraction, 0, 1);
        });

    private void UpdateUiState()
    {
        bool ready = _source != null && !_initializing && !_closed;
        controlsHost.IsEnabled = ready && !_applying;
        presetCombo.IsEnabled = ready && !_applying;
        previewBtn.IsEnabled = ready && !_applying;
        auditionChannelCombo.IsEnabled = ready && !_applying &&
                                         _sourceReferences.Length >= 2;
        // A stale analysis describes audio the document no longer holds. Committing it would splice
        // a render of the old samples over the new ones, so it is refused at the button.
        bool canApply = ready && !_busy && !_sourceStale && bypassCheck.IsChecked != true;
        applyBtn.IsEnabled = canApply;
        applyCdBtn.IsEnabled = canApply;
        closeBtn.Content = _busy ? "Cancel" : "Close";
        UpdateStaleChrome();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _operation?.Cancel();
            statusText.Text = _applying
                ? "Cancelling the render · the document has not been changed…"
                : "Cancelling…";
            return;
        }
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            // Remember the request: CompleteOperation re-issues the close once the work
            // unwinds, otherwise the user has to click X a second time.
            _closeWhenFinished = true;
            _operation?.Cancel();
            statusText.Text = "Cancelling…";
            return;
        }
        _closed = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _document.Doc.Changed -= OnSourceEdited;
        _document.PropertyChanged -= OnDocumentStateChanged;
        _main.Master.PropertyChanged -= OnMasterChanged;
        _main.Documents.CollectionChanged -= OnDocumentsChanged;
        _previewDebounce.Stop();
        try { _operation?.Cancel(); } catch { }
        try { _lifetime.Cancel(); } catch { }
        try { _main.StopPreview(); } catch { }
        try
        {
            if (_previewRackBypassed)
                _main.Master.RackEnabled = _rackWasEnabled;
        }
        finally
        {
            _lifetime.Dispose();
            _analysisGate.Dispose();
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
