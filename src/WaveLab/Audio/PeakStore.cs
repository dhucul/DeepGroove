namespace WaveLab.Audio;

/// <summary>
/// Multi-resolution min/max/RMS pyramid for fast waveform rendering at any zoom.
/// Base bin = 256 samples; each level above aggregates 4 bins.
/// Rebuild is safe to run on a background thread: it builds into fresh lists from a
/// channel-array snapshot and swaps the level list atomically at the end.
/// </summary>
public sealed class PeakStore
{
    public const int BaseBin = 256;
    private const int LevelFactor = 4;

    private sealed record Level(int BinSize, float[][] Min, float[][] Max, float[][] SumSq);

    private List<Level> _levels = [];
    private AudioDocument? _doc;

    public int Version { get; private set; }

    /// <summary>Rebuild from a point-in-time snapshot of the channel arrays (splices never mutate old arrays).</summary>
    public void Rebuild(AudioDocument doc, float[][]? snapshot = null)
    {
        snapshot ??= doc.Channels.ToArray();
        var newLevels = new List<Level>();
        int channels = snapshot.Length;
        int length = channels > 0 ? snapshot[0].Length : 0;

        // base level
        int bins = (length + BaseBin - 1) / BaseBin;
        var min = NewJagged(channels, bins);
        var max = NewJagged(channels, bins);
        var sumsq = NewJagged(channels, bins);
        for (int c = 0; c < channels; c++)
        {
            var src = snapshot[c];
            for (int b = 0; b < bins; b++)
            {
                int s0 = b * BaseBin, s1 = Math.Min(s0 + BaseBin, length);
                float mn = float.MaxValue, mx = float.MinValue;
                double sq = 0;
                for (int s = s0; s < s1; s++)
                {
                    float v = src[s];
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                    sq += v * v;
                }
                if (s1 <= s0) { mn = 0; mx = 0; }
                min[c][b] = mn; max[c][b] = mx;
                sumsq[c][b] = (float)(sq / Math.Max(1, s1 - s0));
            }
        }
        newLevels.Add(new Level(BaseBin, min, max, sumsq));

        // aggregate levels
        while (bins > 64)
        {
            var prev = newLevels[^1];
            int nbins = (bins + LevelFactor - 1) / LevelFactor;
            var lmin = NewJagged(channels, nbins);
            var lmax = NewJagged(channels, nbins);
            var lsq = NewJagged(channels, nbins);
            for (int c = 0; c < channels; c++)
                for (int b = 0; b < nbins; b++)
                {
                    float mn = float.MaxValue, mx = float.MinValue, sq = 0;
                    int n = 0;
                    for (int k = 0; k < LevelFactor; k++)
                    {
                        int pb = b * LevelFactor + k;
                        if (pb >= bins) break;
                        mn = Math.Min(mn, prev.Min[c][pb]);
                        mx = Math.Max(mx, prev.Max[c][pb]);
                        sq += prev.SumSq[c][pb];
                        n++;
                    }
                    lmin[c][b] = n > 0 ? mn : 0;
                    lmax[c][b] = n > 0 ? mx : 0;
                    lsq[c][b] = n > 0 ? sq / n : 0;
                }
            newLevels.Add(new Level(prev.BinSize * LevelFactor, lmin, lmax, lsq));
            bins = nbins;
        }

        // atomic publish
        _doc = doc;
        _levels = newLevels;
        Version++;
    }

    /// <summary>Min/max/rms over [s0, s1). Falls back to raw samples when the range is small.</summary>
    public void Query(int channel, int s0, int s1, out float min, out float max, out float rms)
    {
        var doc = _doc;
        var levels = _levels; // local capture — Rebuild swaps the reference atomically
        min = 0; max = 0; rms = 0;
        if (doc == null || doc.Length == 0 || s1 <= s0) return;
        if (channel >= doc.ChannelCount) return;
        s0 = Math.Clamp(s0, 0, doc.Length);
        s1 = Math.Clamp(s1, 0, doc.Length);
        if (s1 <= s0) return;
        int range = s1 - s0;

        if (range < BaseBin * 2 || levels.Count == 0)
        {
            var src = doc.Channels[channel];
            s1 = Math.Min(s1, src.Length);
            if (s1 <= s0) return;
            range = s1 - s0;
            float mn = float.MaxValue, mx = float.MinValue;
            double sq = 0;
            for (int s = s0; s < s1; s++)
            {
                float v = src[s];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
                sq += v * v;
            }
            min = mn; max = mx; rms = (float)Math.Sqrt(sq / range);
            return;
        }

        // pick the coarsest level that still gives at least two bins for this range
        Level level = levels[0];
        foreach (var l in levels)
        {
            if (range / l.BinSize >= 2) level = l;
            else break;
        }
        if (channel >= level.Min.Length) return;
        int b0 = s0 / level.BinSize;
        int b1 = Math.Max(b0 + 1, (s1 + level.BinSize - 1) / level.BinSize);
        b1 = Math.Min(b1, level.Min[channel].Length);
        if (b0 >= b1) return;
        float rmn = float.MaxValue, rmx = float.MinValue, rsq = 0;
        int count = 0;
        for (int b = b0; b < b1; b++)
        {
            rmn = Math.Min(rmn, level.Min[channel][b]);
            rmx = Math.Max(rmx, level.Max[channel][b]);
            rsq += level.SumSq[channel][b];
            count++;
        }
        if (count == 0) return;
        min = rmn; max = rmx; rms = (float)Math.Sqrt(rsq / count);
    }

    private static float[][] NewJagged(int channels, int bins)
    {
        var a = new float[channels][];
        for (int c = 0; c < channels; c++) a[c] = new float[bins];
        return a;
    }
}
