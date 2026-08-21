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

    private readonly DocumentViewModel _document;
    private readonly MainViewModel _main;
    private readonly float[][] _sourceReferences;
    private readonly float[]? _capturedNoiseProfile;
    private readonly int _rangeStart;
    private readonly int _rangeCount;
    private readonly int _sampleRate;
    private readonly int _sourceBitDepth;
    private readonly int _sourceEditVersion;
    private readonly int _previewStart;
    private readonly int _previewLength;
    private readonly bool _rackWasEnabled;
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
    private bool _suppressControlEvents;
    private bool _closed;
    private bool _closeWhenFinished;

    public RestorationWorkbenchDialog(DocumentViewModel document, MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(main);

        InitializeComponent();
        _document = document;
        _main = main;
        _rackWasEnabled = main.Master.RackEnabled;
        (_rangeStart, _rangeCount) = document.EditRange();
        _sampleRate = document.Doc.SampleRate;
        _sourceBitDepth = document.Doc.SourceBitDepth;
        _sourceEditVersion = document.Doc.EditVersion;

        // Capture only array references on the UI thread. AudioDocument splices replace
        // channel arrays, so these remain a coherent point-in-time source while the
        // potentially large copy is made on a worker thread.
        _sourceReferences = document.Doc.Channels.ToArray();
        _capturedNoiseProfile = document.NoiseProfile is { Length: > 0 } profile
            ? (float[])profile.Clone()
            : null;

        int maximumPreview = Math.Max(1, Math.Min(_rangeCount, checked(_sampleRate * 12)));
        int relativeCursor = document.Cursor >= _rangeStart && document.Cursor < _rangeStart + _rangeCount
            ? document.Cursor - _rangeStart
            : 0;
        _previewStart = Math.Clamp(relativeCursor - _sampleRate * 2, 0,
            Math.Max(0, _rangeCount - maximumPreview));
        _previewLength = maximumPreview;

        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
        _previewDebounce.Tick += OnPreviewDebounce;

        rangeText.Text = document.HasSelection
            ? $"Selection · {TimeFormat.Position(_rangeStart, _sampleRate)} — {TimeFormat.Position(_rangeStart + _rangeCount, _sampleRate)}"
            : "Entire recording";
        string channels = document.Doc.ChannelCount switch
        {
            1 => "mono",
            2 => "stereo",
            var count => $"{count} channels",
        };
        formatText.Text = $"{TimeFormat.Compact((double)_rangeCount / Math.Max(1, _sampleRate))} · {_sampleRate / 1000.0:0.0} kHz · {channels}";

        _initialized = true;
        UpdateReadouts();
        UpdateUiState();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>True when the successful apply should continue into CD track preparation.</summary>
    public bool PrepareCdRequested { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
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
                    Recommendations: recommendations);
            }, operation.Token);

            if (!IsCurrent(operation)) return;
            _source = prepared.Source;
            _noiseProfile = prepared.Noise.Profile;
            _noiseToProgrammeDb = Restoration.EstimateNoiseToProgrammeDb(prepared.Source, _sampleRate);
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
            _main.Master.RackEnabled = false;
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
        _main.StopPreview();
        var settings = CaptureSettings() with { Bypass = false };
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
                return (Analyses: analyses, Audio: audio);
            }, operation.Token);

            if (!IsCurrent(operation)) return;
            UpdateAnalysisSummary(result.Analyses);
            if (_document.Doc.EditVersion != _sourceEditVersion ||
                _rangeStart < 0 || _rangeStart + _rangeCount > _document.Doc.Length)
            {
                MessageBox.Show(this,
                    "The source document changed while the restoration workbench was open. Nothing was applied. Reopen the workbench to analyze the current audio.",
                    "Source changed", MessageBoxButton.OK, MessageBoxImage.Information);
                statusText.Text = "Source changed · restoration was not applied.";
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
        DialogResult = true;
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
        bool needsContinuousState = !settings.Bypass && settings.WetAmount > 0 &&
            ((settings.RemoveHum && settings.HumAmount > 0) ||
             (settings.ReduceNoise && settings.NoiseReductionDb > 0 && _noiseProfile is { Length: > 0 }));
        bool usesNoiseState = needsContinuousState && settings.ReduceNoise &&
                              settings.NoiseReductionDb > 0 && _noiseProfile is { Length: > 0 };
        bool usesHumState = needsContinuousState && settings.RemoveHum && settings.HumAmount > 0;
        var plan = needsContinuousState
            ? RestorationPreviewPlanning.Create(_previewStart, _sampleRate,
                usesHumState, settings.HumFrequency, settings.HumQ, usesNoiseState)
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
        double step = progressSpan / 5.0;
        double at = progressStart;

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
            double depth = Restoration.SuggestReductionDepthDb(work, _sampleRate,
                settings.NoiseReductionDb);
            progress.Report(new OperationProgress(depth <= 0
                ? "Little hiss under the programme — leaving broadband noise alone…"
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
    internal static NoiseDepthLine DescribeNoiseDepth(bool analysed, bool hasProfile,
        double requestedDb, double appliedDb, double estimateDb)
    {
        if (!analysed) return new NoiseDepthLine("Run analysis to see the depth.", "", false);
        if (!hasProfile) return new NoiseDepthLine("Learn a noise profile to reduce hiss.", "", false);
        if (requestedDb <= 0)
            return new NoiseDepthLine("Not reducing", "the maximum is set to zero.", true);

        string measured = $"hiss {(appliedDb <= 0 ? "already " : "sits ")}{estimateDb:0.0} dB under the programme.";
        return appliedDb <= 0
            ? new NoiseDepthLine("Not reducing", measured, true)
            : new NoiseDepthLine($"Applying {appliedDb:0.0} dB", measured, false);
    }

    private void UpdateNoiseDepthReadout()
    {
        if (noiseDepthText == null) return;

        bool analysed = _source != null && _noiseToProgrammeDb.HasValue;
        bool hasProfile = _noiseProfile is { Length: > 0 };
        double estimate = _noiseToProgrammeDb ?? 0;
        double requested = noiseReduction.Value;
        double applied = analysed && hasProfile
            ? Restoration.SuggestReductionDepthDb(_source!, _sampleRate, requested)
            : 0;

        NoiseDepthLine line = DescribeNoiseDepth(analysed, hasProfile, requested, applied, estimate);
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
        if (_previewStarted) QueueParameterRefresh(shortDelay: true);
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
            switch (presetCombo.SelectedIndex)
            {
                case 0: // Gentle
                    humEnabled.IsChecked = false;
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
        UpdateNoiseDepthReadout();
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
        bool canApply = ready && !_busy && bypassCheck.IsChecked != true;
        applyBtn.IsEnabled = canApply;
        applyCdBtn.IsEnabled = canApply;
        closeBtn.Content = _busy ? "Cancel" : "Close";
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
