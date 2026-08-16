using WaveLab.Audio.Dsp;

namespace WaveLab.ViewModels;

/// <summary>How the user is drawing on the spectrogram.</summary>
/// <remarks>
/// Four tools because defects have shapes: a rectangle suits a thump, a lasso follows a swept
/// squeal, the wand grows through the connected energy of a cough, and harmonic selection takes a
/// fundamental together with its partials — which is what a buzz actually is.
/// </remarks>
public enum SpectralTool
{
    /// <summary>A time span crossed with a frequency band.</summary>
    Rectangle,

    /// <summary>A freehand outline.</summary>
    Lasso,

    /// <summary>Grown from the clicked cell through connected energy.</summary>
    MagicWand,

    /// <summary>The clicked frequency and its partials, across the dragged time span.</summary>
    Harmonic,
}

/// <summary>
/// What the user currently has selected on the spectrogram: the mask a repair will act through, and
/// the bounds the toolbar reads out.
/// </summary>
/// <remarks>
/// <para>
/// The mask, not the rectangle, is the selection. Only one of the four tools produces something a
/// rectangle can describe, so carrying the region and rebuilding a mask from it later would quietly
/// discard everything a lasso or a wand had drawn. <see cref="Bounds"/> exists for the readout and
/// for nothing else.
/// </para>
/// <para>
/// The mask is always in the <em>repair</em> grid — anchored at sample zero, at the hop and transform
/// length spectral edits use — rather than in whatever grid the display happened to analyse at. The
/// display's hop tracks the zoom, so a mask built in it would mean something different at every zoom
/// level and could not be resynthesised cleanly at all when the view is far enough out that the hop
/// reaches the transform length.
/// </para>
/// </remarks>
public sealed class SpectralSelection(
    SpectralTool tool, SpectralMask mask, int sampleRate, int fftSize, int hop)
{
    public SpectralTool Tool { get; } = tool;
    public SpectralMask Mask { get; } = mask;
    public int SampleRate { get; } = sampleRate;
    public int FftSize { get; } = fftSize;
    public int Hop { get; } = hop;

    public bool IsEmpty => Mask.IsEmpty;

    /// <summary>Sample position of a frame's centre.</summary>
    public int SampleAt(int frame) => frame * Hop;

    /// <summary>Centre frequency of a bin.</summary>
    public double FrequencyAt(int bin) => (double)bin * SampleRate / FftSize;

    /// <summary>The enclosing time span and frequency band, for the toolbar readout.</summary>
    public SpectralRegion Bounds => Mask.IsEmpty
        ? SpectralRegion.None
        : new SpectralRegion(
            SampleAt(Mask.FrameOffset),
            SampleAt(Mask.FrameOffset + Mask.Frames),
            FrequencyAt(Mask.BinOffset),
            FrequencyAt(Mask.BinOffset + Mask.Bins));

    /// <summary>An empty selection, which every consumer treats as nothing drawn.</summary>
    public static SpectralSelection None { get; } =
        new(SpectralTool.Rectangle, SpectralMask.Empty, 44_100, 2048, 512);

    /// <summary>
    /// The cells the selection covers, as horizontal runs — one entry per contiguous stretch of bins
    /// within a frame. This is what the overlay is drawn from, for every tool alike, so what is shown
    /// is what will actually be repaired rather than a tidied outline of it.
    /// </summary>
    public List<(int Frame, int FromBin, int ToBin)> Runs(float threshold = 0.02f)
    {
        var runs = new List<(int, int, int)>();
        if (Mask.IsEmpty) return runs;

        for (int f = 0; f < Mask.Frames; f++)
        {
            int start = -1;
            for (int b = 0; b <= Mask.Bins; b++)
            {
                bool inside = b < Mask.Bins && Mask.Weight[f * Mask.Bins + b] > threshold;
                if (inside && start < 0) start = b;
                else if (!inside && start >= 0)
                {
                    runs.Add((Mask.FrameOffset + f, Mask.BinOffset + start, Mask.BinOffset + b));
                    start = -1;
                }
            }
        }
        return runs;
    }
}
