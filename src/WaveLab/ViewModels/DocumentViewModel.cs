using System.Collections.ObjectModel;
using WaveLab.Audio;
using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>Per-tab state: the document plus view window, selection, cursor, playhead, markers and regions.</summary>
public sealed class DocumentViewModel : TabViewModel, IDocumentEditState
{
    private double _viewStart;
    private double _spp = 512;
    private double _viewWidthPixels = 1200;
    private double _ampZoom = 1;
    private int _selStart = -1, _selEnd = -1;
    private int _cursor;
    private int _playhead;
    private int _markersVersion;
    private int _embeddedMarkersVersion;
    private int _historyVersion;

    private bool _rebuildRunning;
    private bool _rebuildQueued;
    private readonly object _markerSaveLock = new();
    private Task _markerSaveChain = Task.CompletedTask;
    private MarkerSaveRequest? _pendingMarkerSave;
    private MarkerSaveRequest? _failedMarkerSave;
    private bool _markerSaveRunning;
    private bool _anchorsChanged;

    private sealed record MarkerSaveRequest(string Path, List<Marker> Markers, List<NamedRegion> Regions);

    public DocumentViewModel(AudioDocument doc, PeakStore? prebuiltPeaks = null)
    {
        Doc = doc;
        Peaks = prebuiltPeaks ?? new PeakStore();
        if (prebuiltPeaks == null) ScheduleRebuild();
        doc.Changed += OnDocChanged;
        doc.TimelineChanged += OnTimelineChanged;
        doc.EditState = this;
        var (markers, regions) = MarkerStore.Load(doc.FilePath);

        // Failing that, the marks the file itself carries. The sidecar wins where both exist,
        // because it also holds regions and CD track order, which cue points cannot express.
        if (markers.Count == 0 && regions.Count == 0) markers = MarkerStore.FromRiff(doc.Riff);

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

    /// <summary>
    /// The tab's label. Deliberately without a dirty mark of its own: the tab strip draws an amber
    /// dot from <see cref="IsDirty"/>, and appending a bullet here as well said it twice.
    /// </summary>
    public override string Title => Doc.Title;
    public override bool IsDirty => Doc.Dirty || _markersVersion != _embeddedMarkersVersion;
    public override string Kind => "WAV";

    public double ViewStart
    {
        get => _viewStart;
        set => Set(ref _viewStart, ClampViewStart(value));
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

    /// <summary>
    /// Bumps whenever the edit history moved — a new step, a jump, a discard, an eviction, or the
    /// savepoint mark moving. The Edit History panel is modeless and re-reads on this, exactly as
    /// the markers panel re-reads on <see cref="MarkersVersion"/>.
    /// </summary>
    public int HistoryVersion => _historyVersion;

    /// <summary>Announces that the edit history moved. Cheap: the panel does the reading.</summary>
    public void NotifyHistoryChanged()
    {
        _historyVersion++;
        Raise(nameof(HistoryVersion));
    }

    /// <summary>Noise print learned for spectral noise reduction (magnitude spectrum), or null.</summary>
    public float[]? NoiseProfile { get; set; }

    public void NotifyMarkersChanged()
    {
        _markersVersion++;
        Raise(nameof(MarkersVersion));
        Raise(nameof(IsDirty));
        // Markers share the audio's save transaction. Autosave preserves unsaved metadata;
        // writing it beside the original audio here would persist anchors from a different timeline.
    }

    /// <summary>
    /// Persists the exact anchors captured alongside a completed audio save, even if the
    /// live document has acquired newer edits while that save was writing.
    /// </summary>
    internal void PersistMarkers(string path, List<Marker> markers, List<NamedRegion> regions)
    {
        lock (_markerSaveLock)
        {
            _pendingMarkerSave = new MarkerSaveRequest(path, markers, regions);
            _failedMarkerSave = null;
            if (!_markerSaveRunning) StartMarkerSaveWorkerLocked();
        }
    }

    /// <summary>Marks the exact marker version embedded by a completed audio-file save.</summary>
    internal void MarkMarkersEmbedded(int version)
    {
        if (_markersVersion != version) return;
        if (_embeddedMarkersVersion == version) return;
        _embeddedMarkersVersion = version;
        Raise(nameof(IsDirty));
    }

    /// <summary>
    /// Replaces marker metadata from an autosave manifest without writing it back to the original
    /// file's sidecar. Recovery metadata is newer than that sidecar and must win, but the recovered
    /// tab remains unsaved until the user explicitly chooses a destination.
    /// </summary>
    internal void RestoreAutosavedMarkers(
        IReadOnlyList<Marker>? markers,
        IReadOnlyList<NamedRegion>? regions)
    {
        if (markers == null && regions == null) return; // legacy manifest: keep file/RIFF metadata

        Markers.Clear();
        Regions.Clear();
        foreach (Marker marker in markers ?? [])
        {
            if (marker == null) continue;
            Markers.Add(new Marker
            {
                Name = SafeName(marker.Name, "Marker"),
                Position = Math.Clamp(marker.Position, 0, Doc.Length),
            });
        }
        foreach (NamedRegion region in regions ?? [])
        {
            if (region == null) continue;
            int regionStart = Math.Clamp(region.Start, 0, Doc.Length);
            int regionEnd = Math.Clamp(region.End, 0, Doc.Length);
            if (regionEnd <= regionStart) continue;
            Regions.Add(new NamedRegion
            {
                Name = SafeName(region.Name, "Region"),
                Start = regionStart,
                End = regionEnd,
                CdTrackOrder = region.CdTrackOrder is > 0 ? region.CdTrackOrder : null,
            });
        }
        _markersVersion++;
        Raise(nameof(MarkersVersion));
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

    public void ZoomBy(double factor, double anchorSample)
    {
        double anchorPixel = (anchorSample - _viewStart) / _spp;
        if (anchorPixel >= 0 && anchorPixel <= _viewWidthPixels)
        {
            ZoomAt(anchorPixel, factor);
            return;
        }

        SamplesPerPixel = Math.Clamp(_spp * factor, 1 / 16.0, MaxSpp());
        ViewStart = anchorSample - _viewWidthPixels * _spp / 2;
        ClampView();
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
        double viewSpan = _viewWidthPixels * _spp;
        if (viewSpan <= 0) return;

        // Keep some waveform visible ahead of the transport. Once playback reaches
        // this anchor, each timer tick advances the view by only the playhead delta.
        // The previous edge-only behaviour waited for the playhead to leave the
        // screen and then moved a whole page, which looked like a periodic jump.
        const double followAnchor = 0.75;
        double anchorSample = _viewStart + viewSpan * followAnchor;
        if (_playhead > anchorSample)
            ViewStart = _playhead - viewSpan * followAnchor;
        else if (_playhead < _viewStart)
            ViewStart = _playhead;
        else
            return;

        ClampView();
    }

    private double MaxSpp() => Math.Max(1 / 16.0, Doc.Length / Math.Max(100, _viewWidthPixels));

    private double ClampViewStart(double value)
    {
        double maxStart = Math.Max(0, Doc.Length - _viewWidthPixels * _spp);
        return Math.Clamp(value, 0, maxStart);
    }

    private void ClampView()
    {
        ViewStart = _viewStart;
    }

    /// <summary>
    /// Where a sample position lands after a splice replaced <paramref name="removedCount"/> samples
    /// at <paramref name="editStart"/> with <paramref name="insertedCount"/>. Positions before the
    /// edit do not move, positions after it shift by the length delta, and a position inside the
    /// removed span collapses onto the nearest surviving sample.
    /// </summary>
    /// <remarks>
    /// Public because the cursor, playhead and selection are not the only things anchored to the
    /// timeline: <c>CdTransferDialog</c> is modeless and holds source ranges of its own, which have
    /// to survive an edit made while it is open the same way these do.
    /// </remarks>
    public static int MapEditAnchor(int value, int editStart, int removedCount, int insertedCount)
    {
        if (value <= editStart) return value;
        int oldEnd = editStart + removedCount;
        if (value >= oldEnd) return value + insertedCount - removedCount;
        return editStart + Math.Min(value - editStart, insertedCount);
    }

    private void OnTimelineChanged(int start, int removed, int inserted)
    {
        int mappedCursor = MapEditAnchor(_cursor, start, removed, inserted);
        int mappedPlayhead = MapEditAnchor(_playhead, start, removed, inserted);
        int mappedSelectionStart = HasSelection ? MapEditAnchor(_selStart, start, removed, inserted) : -1;
        int mappedSelectionEnd = HasSelection ? MapEditAnchor(_selEnd, start, removed, inserted) : -1;

        // Keep every timeline anchor on the same boundary convention. In particular, an insertion
        // exactly at an anchor leaves that anchor before the new material instead of moving marker,
        // region, selection and CD-plan boundaries in different directions.
        int delta = inserted - removed;
        // A same-length replacement changes samples but not the timeline. Only a
        // true length-changing splice should move or collapse anchored metadata.
        if (delta != 0)
        {
            bool changed = false;
            foreach (var m in Markers)
            {
                int mapped = Math.Clamp(MapEditAnchor(m.Position, start, removed, inserted), 0, Doc.Length);
                if (mapped != m.Position) { m.Position = mapped; changed = true; }
            }
            foreach (var r in Regions)
            {
                int mappedStart = Math.Clamp(MapEditAnchor(r.Start, start, removed, inserted), 0, Doc.Length);
                int mappedEnd = Math.Clamp(MapEditAnchor(r.End, start, removed, inserted), 0, Doc.Length);
                if (mappedStart == r.Start && mappedEnd == r.End) continue;
                r.Start = mappedStart;
                r.End = mappedEnd;
                changed = true;
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
            _anchorsChanged |= changed;
        }

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
    }

    private void OnDocChanged(int start, int removed, int inserted)
    {
        if (_anchorsChanged)
        {
            _anchorsChanged = false;
            NotifyMarkersChanged();
        }
        ScheduleRebuild();
        Raise(nameof(Title));
        Raise(nameof(IsDirty));
        Raise(nameof(FormatText));
        RaiseSelection();
        NotifyHistoryChanged();
    }

    public void NotifySaved()
    {
        Raise(nameof(Title));
        Raise(nameof(IsDirty));
        Raise(nameof(FormatText));
        // Saving moves no samples, but it does move the savepoint mark the history draws.
        NotifyHistoryChanged();
    }

    /// <summary>
    /// Detaches from the document. Call when the tab closes.
    /// </summary>
    /// <remarks>
    /// The document holds this view model through its Changed event, and this view model
    /// holds a PeakStore — the whole min/max/RMS pyramid. The pair is collectable
    /// together while nothing else refers to the document, which is why this was never a
    /// leak, but it was the one subscription in the view layer with no matching detach.
    /// </remarks>
    public void Unhook()
    {
        Doc.Changed -= OnDocChanged;
        Doc.TimelineChanged -= OnTimelineChanged;
        if (ReferenceEquals(Doc.EditState, this)) Doc.EditState = null;
    }

    private sealed record AnchorState(
        (Marker Item, int Position)[] Markers,
        (NamedRegion Item, int Start, int End, int Index)[] Regions);

    object IDocumentEditState.Capture() => new AnchorState(
        Markers.Select(m => (m, m.Position)).ToArray(),
        Regions.Select((r, i) => (r, r.Start, r.End, i)).ToArray());

    void IDocumentEditState.Restore(object state, object counterpart)
    {
        var target = (AnchorState)state;
        var other = (AnchorState)counterpart;
        // Preserve object identity and independent name edits. Newly added anchors are mapped
        // by TimelineChanged; only anchors belonging to this audio edit are restored here.
        foreach (var (marker, position) in target.Markers)
            if (Markers.Contains(marker) && marker.Position != position)
            {
                marker.Position = position;
                _anchorsChanged = true;
            }
        foreach (var (region, _, _, _) in other.Regions)
            if (!target.Regions.Any(r => ReferenceEquals(r.Item, region)) && Regions.Remove(region))
                _anchorsChanged = true;
        foreach (var (region, start, end, index) in target.Regions)
        {
            if (!Regions.Contains(region))
            {
                if (other.Regions.Any(r => ReferenceEquals(r.Item, region))) continue;
                Regions.Insert(Math.Min(index, Regions.Count), region);
                _anchorsChanged = true;
            }
            if (region.Start != start || region.End != end) _anchorsChanged = true;
            region.Start = start;
            region.End = end;
        }
    }
}
