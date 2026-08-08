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

    public DocumentViewModel(AudioDocument doc)
    {
        Doc = doc;
        Peaks.Rebuild(doc);
        doc.Changed += OnDocChanged;
        var (markers, regions) = MarkerStore.Load(doc.FilePath);
        foreach (var m in markers) Markers.Add(m);
        foreach (var r in regions) Regions.Add(r);
        ZoomFull();
    }

    public AudioDocument Doc { get; }
    public PeakStore Peaks { get; } = new();
    public int PeaksVersion => Peaks.Version;

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
        MarkerStore.Save(Doc.FilePath, Markers, Regions);
    }

    public void AddMarker(int position, string? name = null)
    {
        Markers.Add(new Marker { Position = Math.Clamp(position, 0, Doc.Length), Name = name ?? $"Marker {Markers.Count + 1}" });
        NotifyMarkersChanged();
    }

    public void AddRegionFromSelection()
    {
        if (!HasSelection) return;
        Regions.Add(new NamedRegion { Start = _selStart, End = _selEnd, Name = $"Region {Regions.Count + 1}" });
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
        // keep markers/regions anchored through splices
        int delta = inserted - removed;
        if (delta != 0 || removed > 0)
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
            if (changed) NotifyMarkersChanged();
        }

        Peaks.Rebuild(Doc);
        Cursor = Math.Clamp(_cursor, 0, Math.Max(0, Doc.Length - 1));
        PlayheadSample = Math.Clamp(_playhead, 0, Math.Max(0, Doc.Length - 1));
        if (HasSelection && (_selStart > Doc.Length || _selEnd > Doc.Length))
            ClearSelection();
        ClampView();
        Raise(nameof(PeaksVersion));
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
