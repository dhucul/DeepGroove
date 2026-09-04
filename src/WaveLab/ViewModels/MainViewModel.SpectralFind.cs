using WaveLab.Audio.Dsp;
using WaveLab.Util;
using System.Runtime.CompilerServices;

namespace WaveLab.ViewModels;

public sealed partial class MainViewModel
{
    public RelayCommand FindSpectralDefectCommand { get; }
    private bool CanFindSpectralDefect => CanMutateAudio && _active is { HasSelection: true };

    private sealed record SpectralSearchArea(DocumentViewModel Document, int Start, int End);
    private readonly ConditionalWeakTable<DocumentViewModel, SpectralSearchArea> _spectralSearchAreas = new();
    private SpectralSearchArea? _spectralSearchArea;
    private SpectralSelection? _automaticSpectralSelection;
    private bool _blockSpectralTimeFallback;

    private void ResetSpectralSearchState(bool forgetDocument = true)
    {
        if (forgetDocument && _active is not null) _spectralSearchAreas.Remove(_active);
        _spectralSearchArea = null;
        _automaticSpectralSelection = null;
        _blockSpectralTimeFallback = false;
    }

    private void RestoreSpectralSearchState()
    {
        if (_active is not { } document || !_spectralSearchAreas.TryGetValue(document, out var search)) return;
        if (!document.HasSelection || document.SelStart != search.Start || document.SelEnd != search.End)
        {
            _spectralSearchAreas.Remove(document);
            return;
        }
        _spectralSearchArea = search;
        _blockSpectralTimeFallback = true;
    }

    private void BeginSpectralSearch(DocumentViewModel document, int start, int end)
    {
        // An explicit search replaces the previous proposed target. Until a new target is found
        // or the user makes a manual selection, Heal must not fall back to the rough search area.
        Set(ref _spectralSelection, SpectralSelection.None, nameof(SpectralSelection));
        _automaticSpectralSelection = null;
        _spectralSearchArea = new(document, start, end);
        _spectralSearchAreas.Remove(document);
        _spectralSearchAreas.Add(document, _spectralSearchArea);
        _blockSpectralTimeFallback = true;
        RaiseSpectralSelectionState();
    }

    private void SpectralSearchTimeSelectionChanged()
    {
        if (_spectralSearchArea is not { } search) return;
        if (ReferenceEquals(_active, search.Document) && _active.HasSelection &&
            _active.SelStart == search.Start && _active.SelEnd == search.End) return;
        if (ReferenceEquals(_spectralSelection, _automaticSpectralSelection))
            Set(ref _spectralSelection, SpectralSelection.None, nameof(SpectralSelection));
        // A newly drawn waveform selection is again a deliberate manual time selection.
        ResetSpectralSearchState();
    }

    private void SpectralSearchAudioChanged()
    {
        if (_spectralSearchArea is null) return;
        if (ReferenceEquals(_spectralSelection, _automaticSpectralSelection))
            Set(ref _spectralSelection, SpectralSelection.None, nameof(SpectralSelection));
        _automaticSpectralSelection = null;
        _blockSpectralTimeFallback = true;
    }

    /// <summary>Turn a rough waveform selection into a proposed spectral patch, without editing audio.</summary>
    internal async Task FindSpectralDefectAsync()
    {
        if (!CanFindSpectralDefect || _active is not { } document) return;
        int start = document.SelStart, end = document.SelEnd;
        int sampleRate = document.Doc.SampleRate;
        BeginSpectralSearch(document, start, end);
        if ((end - start) / (double)sampleRate > SpectralDefectFinder.MaximumSearchSeconds)
        {
            ReportAction("Find Defect: select up to 10 seconds around the sound, then try again.");
            return;
        }

        int version = document.Doc.EditVersion;
        SpectralSearchArea search = _spectralSearchArea!;
        SpectralSelection previousSelection = SpectralSelection;
        float[][] channels = document.Doc.Channels.ToArray();
        SetDocumentOperationRunning(true);
        try
        {
            SpectralDefectCandidate? candidate = null;
            CancellationToken operationToken = default;
            await Progress.RunBlockingAsync("Finding defect", "Looking for a short ringing sound in the selected passage",
                async (progress, token) =>
                {
                    operationToken = token;
                    candidate = await Task.Run(() => SpectralDefectFinder.FindStrongest(
                        channels, sampleRate, start, end - start, token, progress), token);
                    token.ThrowIfCancellationRequested();
                });
            operationToken.ThrowIfCancellationRequested();

            // A suggested selection belongs to the exact document and rough area that were read.
            // Selection gestures and tab changes can happen while the worker is running.
            if (!ReferenceEquals(_active, document) || !Documents.Contains(document) ||
                !ReferenceEquals(_spectralSearchArea, search) ||
                document.Doc.EditVersion != version || !document.HasSelection ||
                document.SelStart != start || document.SelEnd != end ||
                !ReferenceEquals(SpectralSelection, previousSelection))
            {
                ReportAction("Find Defect stopped because the document or selection changed.");
                return;
            }
            if (candidate is null)
            {
                ReportAction("No clear ringing defect found. Draw a repair region or select another passage; Heal has no target.");
                return;
            }

            SpectralMask mask = candidate.CreateMask(sampleRate);
            if (mask.IsEmpty)
            {
                ReportAction("The detected area is too small to select. Draw a repair region or select a wider passage.");
                return;
            }
            _automaticSpectralSelection = new SpectralSelection(SpectralTool.Rectangle, mask, sampleRate,
                candidate.FftSize, candidate.Hop);
            Set(ref _spectralSelection, _automaticSpectralSelection, nameof(SpectralSelection));
            RaiseSpectralSelectionState();
            SpectralTool = SpectralTool.Rectangle;
            if (!ShowsSpectrogram) ShowSplitCommand.Execute(null);
            // Keep the rough time selection for audition and another search. The spectral mask is
            // the repair target; zoom the picture independently so the user can actually see it.
            double visibleSamples = Math.Min(end - start, sampleRate * .4);
            document.SamplesPerPixel = Math.Max(1 / 16.0, visibleSamples / document.ViewWidthPixels);
            document.CenterViewOn(candidate.PeakSample);
            ReportAction($"Possible ringing defect at {candidate.PeakSample / (double)sampleRate:0.000} s · " +
                $"{candidate.LowFrequency / 1000:0.0}–{candidate.HighFrequency / 1000:0.0} kHz selected. " +
                "Listen to the passage, then press Heal if this is the sound you want to remove.");
        }
        catch (OperationCanceledException)
        {
            ReportAction("Find Defect cancelled · audio unchanged.");
        }
        catch (Exception ex)
        {
            ReportAction($"Find Defect could not finish: {ex.Message}");
        }
        finally
        {
            SetDocumentOperationRunning(false);
        }
    }
}
