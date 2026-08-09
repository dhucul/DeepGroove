using System.Collections.ObjectModel;
using WaveLab.Audio;
using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>Per-tab state: the document plus view window, selection, cursor, playhead, markers and regions.</summary>
public sealed class DocumentViewModel : ObservableObject
{
    private double _viewStart;
    private double _spp = 512;
    private double _viewWidthPixels = 1200;
    private double _ampZoom = 1;
    private int _selStart = -1, _selEnd = -1;
    private int _cursor;
    private int _playhead;
    private int _markersVersion;

    private bool _rebuildRunning;
    private bool _rebuildQueued;
    private readonly object _markerSaveLock = new();
    private Task _markerSaveChain = Task.CompletedTask;
    private MarkerSaveRequest? _pendingMarkerSave;
    private MarkerSaveRequest? _failedMarkerSave;
    private bool _markerSaveRunning;

    private sealed record MarkerSaveRequest(string Path, List<Marker> Markers, List<NamedRegion> Regions);

    public DocumentViewModel(AudioDocument doc, PeakStore? prebuiltPeaks = null)
    {
        Doc = doc;
        Peaks = prebuiltPeaks ?? new PeakStore();
        if (prebuiltPeaks == null) ScheduleRebuild();
        doc.Changed += OnDocChanged;
        var (markers, regions) = MarkerStore.Load(doc.FilePath);
        foreach (var m in markers)
        {
            if (m is null) continue;
            Markers.Add(new Marker { Name = SafeName(m.Name, "Marker"), Position = Math.Clamp(m.Position, 0, doc.Length) });
        }
        foreach (var r in regions)
        {
            if (r is null) continue;
            int start = Math.Clamp(r.Start, 0, doc.Length);
            int end = Math.Clamp(r.End, 0, doc.Length);
            if (end > start)
                Regions.Add(new NamedRegion
                {
                    Name = SafeName(r.Name, "Region"), Start = start, End = end,
                    CdTrackOrder = r.CdTrackOrder is > 0 ? r.CdTrackOrder : null,
                });
        }
        ZoomFull();
    }

    public AudioDocument Doc { get; }
    public PeakStore Peaks { get; }
    public int PeaksVersion => Peaks.Version;

    /// <summary>Rebuild the peak pyramid off the UI thread; coalesces bursts of edits.</summary>
    private async void ScheduleRebuild()
    {
        if (_rebuildRunning) { _rebuildQueued = true; return; }
        _rebuildRunning = true;
        try
        {
            do
            {
                _rebuildQueued = false;
                try
                {
                    var snapshot = Doc.Channels.ToArray(); // stable refs — splices never mutate old arrays
                    await Task.Run(() => Peaks.Rebuild(Doc, snapshot));
                    Raise(nameof(PeaksVersion));
                }
                catch
                {
                    // best-effort: keep the stale pyramid; the next edit schedules another attempt
                }
            } while (_rebuildQueued);
        }
        finally
        {
            _rebuildRunning = false;
        }
    }

    public string Title => Doc.Title + (Doc.Dirty ? " •" : "");
    public bool IsDirty => Doc.Dirty;

    public double ViewStart
    {
        get => _viewStart;
        set { if (Set(ref _viewStart, value)) { } }
    }

    public double SamplesPerPixel
    {
        get => _spp;
        set { if (Set(ref _spp, Math.Max(1 / 16.0, value))) Raise(nameof(ZoomText)); }
    }

    public double ViewWidthPixels
    {
        get => _viewWidthPixels;
        set { if (Set(ref _viewWidthPixels, Math.Max(64, value))) ClampView(); }
    }

    public int SelStart { get => _selStart; private set => Set(ref _selStart, value); }
    public int SelEnd { get => _selEnd; private set => Set(ref _selEnd, value); }
    public bool HasSelection => _selEnd > _selStart && _selStart >= 0;

    public int Cursor { get => _cursor; private set => Set(ref _cursor, value); }
    public int PlayheadSample { get => _playhead; set { if (Set(ref _playhead, value)) Raise(nameof(PositionText)); } }

    public string PositionText => TimeFormat.Position(_playhead, Doc.SampleRate);
    public string SelInText => HasSelection ? TimeFormat.Position(_selStart, Doc.SampleRate) : "—";
    public string SelOutText => HasSelection ? TimeFormat.Position(_selEnd, Doc.SampleRate) : "—";
    public string SelLenText => HasSelection ? TimeFormat.Position(_selEnd - _selStart, Doc.SampleRate) : "—";
    public string ZoomText => _spp >= 1 ? $"1:{Math.Round(_spp)}" : $"{Math.Round(1 / _spp)}:1";

    public string FormatText
    {
        get
        {
            string depth = Doc.SourceBitDepth == 32 ? "32-bit float" : $"{Doc.SourceBitDepth}-bit";
            string ch = Doc.ChannelCount switch { 1 => "Mono", 2 => "Stereo", var n => $"{n} ch" };
            return $"{Doc.SampleRate / 1000.0:0.0} kHz · {depth} · {ch}";
        }
    }

    // ── selection / cursor ───────────────────────────────────────

    public void SetCursor(int sample, bool clearSelection)
    {
        Cursor = Math.Clamp(sample, 0, Math.Max(0, Doc.Length - 1));
        if (clearSelection) ClearSelection();
        if (clearSelection) PlayheadSample = Cursor;
        RaiseSelection();
    }

    public void SetSelection(int start, int end)
    {
        SelStart = Math.Clamp(start, 0, Doc.Length);
        SelEnd = Math.Clamp(end, 0, Doc.Length);
        RaiseSelection();
    }

    public void SelectAll() => SetSelection(0, Doc.Length);

    public void ClearSelection()
    {
        SelStart = -1;
        SelEnd = -1;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(SelInText));
        Raise(nameof(SelOutText));
        Raise(nameof(SelLenText));
    }

    /// <summary>The range edits apply to: the selection, or the whole file.</summary>
    public (int Start, int Count) EditRange() =>
        HasSelection ? (_selStart, _selEnd - _selStart) : (0, Doc.Length);

    // ── markers & regions ────────────────────────────────────────

    public ObservableCollection<Marker> Markers { get; } = [];
    public ObservableCollection<NamedRegion> Regions { get; } = [];
    public int MarkersVersion => _markersVersion;

    /// <summary>Noise print learned for spectral noise reduction (magnitude spectrum), or null.</summary>
    public float[]? NoiseProfile { get; set; }

    public void NotifyMarkersChanged()
    {
        _markersVersion++;
        Raise(nameof(MarkersVersion));
        var path = Doc.FilePath;
        if (path == null) return;
        // snapshot for the background write so UI mutations can't tear the serialization,
        // and chain writes so they always land in order (latest state wins)
        var markers = Markers.Select(m => new Marker
        {
            Name = SafeName(m.Name, "Marker"), Position = Math.Clamp(m.Position, 0, Doc.Length),
        }).ToList();
        var regions = Regions.Select(r => new NamedRegion
        {
            Name = SafeName(r.Name, "Region"),
            Start = Math.Clamp(r.Start, 0, Doc.Length),
            End = Math.Clamp(r.End, 0, Doc.Length),
            CdTrackOrder = r.CdTrackOrder is > 0 ? r.CdTrackOrder : null,
        }).Where(r => r.End > r.Start).ToList();
        lock (_markerSaveLock)
        {
            _pendingMarkerSave = new MarkerSaveRequest(path, markers, regions);
            _failedMarkerSave = null; // this newer snapshot supersedes any retained failure
            if (_markerSaveRunning) return;
            StartMarkerSaveWorkerLocked();
        }
    }

    public async Task FlushMarkersAsync()
    {
        bool retried = false;
        while (true)
        {
            Task observed;
            lock (_markerSaveLock) observed = _markerSaveChain;

            Exception? failure = null;
            try { await observed; }
            catch (Exception ex) { failure = ex; }

            bool stable;
            bool retryStarted = false;
            lock (_markerSaveLock)
            {
                stable = ReferenceEquals(observed, _markerSaveChain)
                    && !_markerSaveRunning && _pendingMarkerSave == null;
                if (stable && failure != null && !retried && _failedMarkerSave != null)
                {
                    retried = true;
                    _pendingMarkerSave = _failedMarkerSave;
                    _failedMarkerSave = null;
                    StartMarkerSaveWorkerLocked();
                    retryStarted = true;
                }
            }
            if (!stable) continue;
            if (retryStarted) continue;
            if (failure != null) throw failure;
            return;
        }
    }

    /// <summary>Starts a worker while <see cref="_markerSaveLock"/> is held.</summary>
    private void StartMarkerSaveWorkerLocked()
    {
        _markerSaveRunning = true;
        var previous = _markerSaveChain;
        if (previous.IsFaulted) _ = previous.Exception;
        else if (!previous.IsCompleted)
            _ = previous.ContinueWith(task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        _markerSaveChain = Task.Run(DrainMarkerSaves);
    }

    private void DrainMarkerSaves()
    {
        while (true)
        {
            MarkerSaveRequest request;
            lock (_markerSaveLock)
            {
                request = _pendingMarkerSave!;
                _pendingMarkerSave = null;
            }

            Exception? failure = null;
            try { MarkerStore.Save(request.Path, request.Markers, request.Regions); }
            catch (Exception ex) { failure = ex; }

            lock (_markerSaveLock)
            {
                if (_pendingMarkerSave != null) continue; // a newer snapshot can recover an older write failure
                _markerSaveRunning = false;
                if (failure != null)
                {
                    _failedMarkerSave = request;
                    throw failure;
                }
                _failedMarkerSave = null;
                return;
            }
        }
    }

    private static string SafeName(string? value, string fallback)
    {
        string name = (value ?? "").Trim();
        name = new string(name.Where(ch => !char.IsControl(ch)).Take(200).ToArray());
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    public void AddMarker(int position, string? name = null)
    {
        Markers.Add(new Marker
        {
            Position = Math.Clamp(position, 0, Doc.Length),
            Name = SafeName(name, $"Marker {Markers.Count + 1}"),
        });
        NotifyMarkersChanged();
    }

    public void AddMarkers(IEnumerable<(int Position, string? Name)> markers)
    {
        bool added = false;
        foreach (var (position, name) in markers)
        {
            Markers.Add(new Marker
            {
                Position = Math.Clamp(position, 0, Doc.Length),
                Name = SafeName(name, $"Marker {Markers.Count + 1}"),
            });
            added = true;
        }
        if (added) NotifyMarkersChanged();
    }

    public void AddRegionFromSelection()
    {
        if (!HasSelection) return;
        Regions.Add(new NamedRegion { Start = _selStart, End = _selEnd, Name = $"Region {Regions.Count + 1}" });
        NotifyMarkersChanged();
    }

    public void RenameMarker(Marker marker, string name)
    {
        if (!Markers.Contains(marker)) return;
        marker.Name = SafeName(name, "Marker");
        NotifyMarkersChanged();
    }

    public void RenameRegion(NamedRegion region, string name)
    {
        if (!Regions.Contains(region)) return;
        region.Name = SafeName(name, "Region");
        NotifyMarkersChanged();
    }

    public void JumpToMarker(Marker marker)
    {
        SetCursor(marker.Position, clearSelection: true);
        CenterViewOn(marker.Position);
    }

    public void JumpToNextMarker(bool forward)
    {
        var ordered = Markers.OrderBy(m => m.Position).ToList();
        if (ordered.Count == 0) return;
        Marker? target = forward
            ? ordered.FirstOrDefault(m => m.Position > _cursor + 1)
            : ordered.LastOrDefault(m => m.Position < _cursor - 1);
        target ??= forward ? ordered[0] : ordered[^1];
        JumpToMarker(target);
    }

    public double AmpZoom
    {
        get => _ampZoom;
        set => Set(ref _ampZoom, Math.Clamp(value, 1, 8));
    }

    // ── view ─────────────────────────────────────────────────────

    public void ZoomAt(double px, double factor)
    {
        double anchor = _viewStart + px * _spp;
        SamplesPerPixel = Math.Clamp(_spp * factor, 1 / 16.0, MaxSpp());
        ViewStart = anchor - px * _spp;
        ClampView();
    }

    public void ZoomFull()
    {
        SamplesPerPixel = MaxSpp();
        ViewStart = 0;
        ClampView();
    }

    public void ZoomToSelection()
    {
        if (!HasSelection) return;
        SamplesPerPixel = Math.Clamp((_selEnd - _selStart) / Math.Max(64, _viewWidthPixels), 1 / 16.0, MaxSpp());
        ViewStart = _selStart;
        ClampView();
    }

    public void ZoomBy(double factor)
    {
        ZoomAt(_viewWidthPixels / 2, factor);
    }

    public void ScrollBy(double samples)
    {
        ViewStart = _viewStart + samples;
        ClampView();
    }

    public void CenterViewOn(double sample)
    {
        ViewStart = sample - _viewWidthPixels * _spp / 2;
        ClampView();
    }

    public void EnsurePlayheadVisible()
    {
        double viewEnd = _viewStart + _viewWidthPixels * _spp;
        if (_playhead < _viewStart || _playhead > viewEnd)
            ViewStart = _playhead;
        ClampView();
    }

    private double MaxSpp() => Math.Max(1 / 16.0, Doc.Length / Math.Max(100, _viewWidthPixels));

    private void ClampView()
    {
        double maxStart = Math.Max(0, Doc.Length - _viewWidthPixels * _spp);
        _viewStart = Math.Clamp(_viewStart, 0, maxStart);
        Raise(nameof(ViewStart));
    }

    private void OnDocChanged(int start, int removed, int inserted)
    {
        static int MapAnchor(int value, int editStart, int removedCount, int insertedCount)
        {
            if (value <= editStart) return value;
            int oldEnd = editStart + removedCount;
            if (value >= oldEnd) return value + insertedCount - removedCount;
            return editStart + Math.Min(value - editStart, insertedCount);
        }

        int mappedCursor = MapAnchor(_cursor, start, removed, inserted);
        int mappedPlayhead = MapAnchor(_playhead, start, removed, inserted);
        int mappedSelectionStart = HasSelection ? MapAnchor(_selStart, start, removed, inserted) : -1;
        int mappedSelectionEnd = HasSelection ? MapAnchor(_selEnd, start, removed, inserted) : -1;

        // keep markers/regions anchored through splices
        int delta = inserted - removed;
        // A same-length replacement changes samples but not the timeline. Only a
        // true length-changing splice should move or collapse anchored metadata.
        if (delta != 0)
        {
            bool changed = false;
            foreach (var m in Markers)
            {
                if (m.Position >= start + removed) { m.Position += delta; changed = true; }
                else if (m.Position > start) { m.Position = start; changed = true; }
            }
            foreach (var r in Regions)
            {
                if (r.Start >= start + removed) { r.Start += delta; r.End += delta; changed = true; }
                else if (r.End > start)
                {
                    r.Start = Math.Min(r.Start, start);
                    r.End = Math.Max(start, r.End + (r.End >= start + removed ? delta : start - r.End));
                    changed = true;
                }
            }
            for (int i = Regions.Count - 1; i >= 0; i--)
            {
                var region = Regions[i];
                region.Start = Math.Clamp(region.Start, 0, Doc.Length);
                region.End = Math.Clamp(region.End, 0, Doc.Length);
                if (region.End <= region.Start)
                {
                    Regions.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed) NotifyMarkersChanged();
        }

        ScheduleRebuild();
        Cursor = Math.Clamp(mappedCursor, 0, Math.Max(0, Doc.Length - 1));
        PlayheadSample = Math.Clamp(mappedPlayhead, 0, Doc.Length);
        if (mappedSelectionStart >= 0 && mappedSelectionEnd > mappedSelectionStart)
        {
            int clampedStart = Math.Clamp(mappedSelectionStart, 0, Doc.Length);
            int clampedEnd = Math.Clamp(mappedSelectionEnd, 0, Doc.Length);
            if (clampedEnd > clampedStart)
            {
                SelStart = clampedStart;
                SelEnd = clampedEnd;
            }
            else SelStart = SelEnd = -1;
        }
        else if (_selStart >= 0 || _selEnd >= 0)
        {
            SelStart = SelEnd = -1;
        }
        ClampView();
        Raise(nameof(Title));
        Raise(nameof(IsDirty));
        RaiseSelection();
    }

    public void NotifySaved()
    {
        Raise(nameof(Title));
        Raise(nameof(IsDirty));
        Raise(nameof(FormatText));
    }
}
