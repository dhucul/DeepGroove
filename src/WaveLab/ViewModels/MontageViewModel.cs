using System.Collections.ObjectModel;
using System.IO;
using WaveLab.Audio;
using WaveLab.Audio.Montage;
using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>What the pointer does on the montage lane.</summary>
public enum MontageTool
{
    /// <summary>Drag a clip along the lane.</summary>
    Move,

    /// <summary>Drag a clip's edge to trim it.</summary>
    Trim,

    /// <summary>Click to cut a clip in two.</summary>
    Split,
}

/// <summary>
/// A montage open in a tab: the document, the view window over it, and what is selected.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a <see cref="DocumentViewModel"/>. A montage has no samples of its own
/// until it is rendered, so it has no peak pyramid, no selection in samples and nothing for the
/// waveform editor to edit; giving it that shape would mean every audio command in the app had to
/// start asking whether its document was real. It shares only <see cref="TabViewModel"/>, which is
/// the little the tab strip needs.
/// </para>
/// <para>
/// The lane's zoom and scroll are held here in the same terms the editor uses — samples per pixel
/// and a start sample — so the existing time ruler and overview bar can be pointed at a montage
/// without knowing what one is.
/// </para>
/// </remarks>
public sealed class MontageViewModel : TabViewModel
{
    private readonly Dictionary<int, PeakStore> _peaks = [];
    private readonly Dictionary<int, AudioDocument> _peakDocuments = [];

    private double _samplesPerPixel = 512;
    private double _viewStart;
    private double _viewWidthPixels = 1_000;
    private MontageClip? _selected;
    private MontageTool _tool = MontageTool.Move;
    private bool _snapToZeroCrossing = true;
    private bool _dirty;
    private int _revision;

    public MontageViewModel(MontageDocument montage)
    {
        ArgumentNullException.ThrowIfNull(montage);
        Montage = montage;
        RefreshIssues();
    }

    public MontageDocument Montage { get; }

    public override string Title => Montage.Title;
    public override bool IsDirty => _dirty;
    public override string Kind => "MONTAGE";

    /// <summary>Bumped on every change to the lane; the view repaints from it.</summary>
    public int Revision => _revision;

    public ObservableCollection<MontageIssue> Issues { get; } = [];

    public MontageTool Tool
    {
        get => _tool;
        set => Set(ref _tool, value);
    }

    /// <summary>
    /// Whether a dragged edge lands on the nearest zero crossing. A clip boundary that falls
    /// mid-waveform clicks, and on a montage that is the commonest way to make one.
    /// </summary>
    public bool SnapToZeroCrossing
    {
        get => _snapToZeroCrossing;
        set => Set(ref _snapToZeroCrossing, value);
    }

    public MontageClip? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedCrossfade));
        }
    }

    public bool HasSelection => _selected != null;

    // ── the view window ──────────────────────────────────────────

    public double SamplesPerPixel
    {
        get => _samplesPerPixel;
        set
        {
            double clamped = Math.Clamp(value, 1, Math.Max(1, Montage.Length / 4.0 + 1));
            if (!Set(ref _samplesPerPixel, clamped)) return;
            ClampViewStart();
            Raise(nameof(ViewStart));
        }
    }

    public double ViewStart
    {
        get => _viewStart;
        set
        {
            _viewStart = value;
            ClampViewStart();
            Raise(nameof(ViewStart));
        }
    }

    /// <summary>How wide the lane is on screen, so scrolling can be clamped against it.</summary>
    public double ViewWidthPixels
    {
        get => _viewWidthPixels;
        set
        {
            if (value <= 0 || Math.Abs(_viewWidthPixels - value) < 0.5) return;
            _viewWidthPixels = value;
            ClampViewStart();
            Raise(nameof(ViewStart));
        }
    }

    private void ClampViewStart()
    {
        double visible = _viewWidthPixels * _samplesPerPixel;
        double most = Math.Max(0, Montage.Length - visible);
        _viewStart = Math.Clamp(_viewStart, 0, most);
    }

    public void ZoomFull()
    {
        if (Montage.Length <= 0 || _viewWidthPixels <= 0) return;
        SamplesPerPixel = Math.Max(1, Montage.Length / _viewWidthPixels);
        ViewStart = 0;
    }

    public void Zoom(double factor, double anchorSample)
    {
        double before = _samplesPerPixel;
        SamplesPerPixel = before * factor;
        if (Math.Abs(before - _samplesPerPixel) < 1e-9) return;

        // Hold the sample under the pointer still, which is what makes wheel zoom feel attached to
        // the audio rather than to the scrollbar.
        double pixel = (anchorSample - _viewStart) / before;
        ViewStart = anchorSample - pixel * _samplesPerPixel;
    }

    public double SampleAt(double x) => _viewStart + x * _samplesPerPixel;
    public double PixelOf(double sample) => (sample - _viewStart) / _samplesPerPixel;

    // ── peaks ────────────────────────────────────────────────────

    /// <summary>
    /// A peak pyramid for a source, built once and kept. The wrapper document shares the source's
    /// arrays rather than copying them.
    /// </summary>
    public PeakStore PeaksFor(int sourceIndex)
    {
        if (_peaks.TryGetValue(sourceIndex, out PeakStore? cached)) return cached;

        MontageSource source = Montage.Sources[sourceIndex];
        var document = new AudioDocument(
            source.Channels.Length > 0 ? source.Channels : [[]],
            source.SampleRate, sourceBitDepth: 32);
        var store = new PeakStore();
        store.Rebuild(document);

        _peakDocuments[sourceIndex] = document;
        _peaks[sourceIndex] = store;
        return store;
    }

    // ── edits ────────────────────────────────────────────────────

    /// <summary>Records a change: the lane repaints, the tab goes dirty, the issues re-run.</summary>
    public void Touch(bool structural = true)
    {
        _revision++;
        _dirty = true;
        if (structural) Montage.Sort();
        RefreshIssues();
        Raise(nameof(Revision));
        Raise(nameof(IsDirty));
        Raise(nameof(Title));
        Raise(nameof(SelectedCrossfade));
        Raise(nameof(Summary));
    }

    public void MarkSaved()
    {
        _dirty = false;
        Raise(nameof(IsDirty));
    }

    public void RefreshIssues()
    {
        Issues.Clear();
        foreach (MontageIssue issue in Montage.Validate()) Issues.Add(issue);
        Raise(nameof(Summary));
    }

    public string Summary
    {
        get
        {
            MontageIssue? worst = Issues.FirstOrDefault(i => i.Severity == MontageIssueSeverity.Error)
                                  ?? Issues.FirstOrDefault(i => i.Severity == MontageIssueSeverity.Warning);
            return worst?.Message
                   ?? Issues.FirstOrDefault(i => i.Severity == MontageIssueSeverity.Information)?.Message
                   ?? "";
        }
    }

    /// <summary>
    /// Moves a clip along the lane, keeping it on the timeline and out of negative time.
    /// </summary>
    public void MoveClip(MontageClip clip, int timelineStart)
    {
        ArgumentNullException.ThrowIfNull(clip);
        clip.TimelineStart = Math.Max(0, timelineStart);
        Touch();
    }

    /// <summary>
    /// Trims an edge. Trimming the head moves the clip's start in its source too, so the audio under
    /// the pointer stays where it is instead of sliding.
    /// </summary>
    public void TrimClip(MontageClip clip, bool head, int timelinePosition)
    {
        ArgumentNullException.ThrowIfNull(clip);
        MontageSource source = Montage.Sources[clip.SourceIndex];

        if (head)
        {
            int limit = clip.TimelineEnd - 1;
            int start = Math.Clamp(timelinePosition, Math.Max(0, clip.TimelineStart - clip.SourceStart), limit);
            int delta = start - clip.TimelineStart;

            clip.TimelineStart = start;
            clip.SourceStart = Math.Max(0, clip.SourceStart + delta);
            clip.Length = Math.Max(1, clip.Length - delta);
        }
        else
        {
            int most = source.Length - clip.SourceStart;
            int length = Math.Clamp(timelinePosition - clip.TimelineStart, 1, Math.Max(1, most));
            clip.Length = length;
        }
        Touch();
    }

    /// <summary>Cuts a clip in two at a timeline position, leaving both halves selected-able.</summary>
    public MontageClip? SplitClip(MontageClip clip, int timelinePosition)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int offset = timelinePosition - clip.TimelineStart;
        if (offset <= 0 || offset >= clip.Length) return null;

        var right = clip.Clone();
        right.Name = clip.Name + " B";
        right.TimelineStart = clip.TimelineStart + offset;
        right.SourceStart = clip.SourceStart + offset;
        right.Length = clip.Length - offset;

        // The left half keeps its fade-in and loses its fade-out; the right half the reverse.
        // Carrying both to both halves would put a fade in the middle of what was continuous.
        right.FadeInSamples = 0;
        clip.FadeOutSamples = 0;
        clip.Length = offset;

        Montage.Add(right);
        Touch();
        return right;
    }

    public void RemoveSelected()
    {
        if (_selected == null) return;
        Montage.Remove(_selected);
        Selected = null;
        Touch();
    }

    /// <summary>
    /// The nearest zero crossing to a source position, so a trimmed edge lands where the waveform
    /// is already at rest.
    /// </summary>
    public int SnapSource(int sourceIndex, int position, int radius = 512)
    {
        if (!SnapToZeroCrossing) return position;
        if (sourceIndex < 0 || sourceIndex >= Montage.Sources.Count) return position;

        float[][] channels = Montage.Sources[sourceIndex].Channels;
        if (channels.Length == 0 || channels[0].Length == 0) return position;

        float[] channel = channels[0];
        int best = position;
        double bestMagnitude = double.MaxValue;

        int from = Math.Max(1, position - radius);
        int to = Math.Min(channel.Length - 1, position + radius);
        for (int i = from; i <= to; i++)
        {
            // A rising crossing specifically: landing on any near-zero sample is not enough, since
            // a quiet passage is full of them and the edge would wander.
            if (channel[i - 1] > 0 || channel[i] < 0) continue;
            double magnitude = Math.Abs(channel[i]);
            if (magnitude >= bestMagnitude) continue;
            bestMagnitude = magnitude;
            best = i;
        }
        return best;
    }

    // ── the crossfade under the selection ────────────────────────

    /// <summary>What the join after the selected clip is doing, or null if it has no join.</summary>
    public MontageCrossfadeInfo? SelectedCrossfade
    {
        get
        {
            if (_selected == null) return null;
            int index = Montage.Clips.ToList().IndexOf(_selected);
            if (index < 0 || index + 1 >= Montage.Clips.Count) return null;

            MontageClip next = Montage.Clips[index + 1];
            int overlap = MontageDocument.Overlap(_selected, next);
            if (overlap <= 0) return null;

            int overlapStart = Math.Max(_selected.TimelineStart, next.TimelineStart);
            MontageSource a = Montage.Sources[_selected.SourceIndex];
            MontageSource b = Montage.Sources[next.SourceIndex];

            // Signed, so the panel can tell "unrelated" from "cancelling" — the law itself floors it.
            double correlation = Crossfade.MeasureSignedCorrelation(
                a.Channels, _selected.SourceStart + (overlapStart - _selected.TimelineStart),
                b.Channels, next.SourceStart + (overlapStart - next.TimelineStart),
                overlap);

            return new MontageCrossfadeInfo(next.Name, overlap,
                (double)overlap / Montage.SampleRate, correlation, next.FadeInShape);
        }
    }

    public string DescribeLength() => TimeFormat.Compact(Montage.Duration);

    public static string SuggestedFileName(MontageDocument montage) =>
        montage.FilePath is { } path
            ? Path.GetFileName(path)
            : (string.IsNullOrWhiteSpace(montage.Title) ? "Montage" : montage.Title) + MontageStore.Extension;
}

/// <summary>What the crossfade after a clip is doing, for the inspector to show.</summary>
public sealed record MontageCrossfadeInfo(
    string NextClip, int OverlapSamples, double OverlapSeconds, double Correlation, FadeShape Shape)
{
    /// <summary>The correlation the law actually uses: the measurement, floored at zero.</summary>
    public double EffectiveCorrelation => Math.Max(0, Correlation);

    /// <summary>
    /// Which of the familiar laws this correlation is near, in the words a user thinks in.
    /// </summary>
    public string LawName => EffectiveCorrelation switch
    {
        < 0.15 => "equal power",
        < 0.45 => "between the two",
        < 0.85 => "nearer equal gain",
        _ => "equal gain",
    };

    /// <summary>
    /// How far out the join would be if the other familiar law had been used instead — the number
    /// that says why the measurement was worth taking.
    /// </summary>
    public double FixedLawErrorDb
    {
        get
        {
            // At the middle of the fade, with the wrong law and equal-power sources.
            double rho = EffectiveCorrelation;
            double a = Fades.In(Shape, 0.5);
            double wrong = rho < 0.5 ? 1 : 0;   // the law that would otherwise be chosen
            double b = Crossfade.Partner(a, wrong);
            double power = a * a + b * b + 2 * a * b * rho;
            return power > 0 ? 10 * Math.Log10(power) : 0;
        }
    }

    /// <summary>
    /// Whether the two sides partly cancel — a <em>negative</em> measurement, not merely a small one.
    /// </summary>
    /// <remarks>
    /// Uncorrelated material measures at about zero and is the ordinary case; anti-correlated
    /// material also floors at zero once the law has clamped it, which is why this asks the signed
    /// measurement. Reading a clamped zero as cancellation flagged every perfectly good join between
    /// two unrelated pieces of music as a polarity fault.
    /// </remarks>
    public bool Cancels => Correlation < -0.05;
}
