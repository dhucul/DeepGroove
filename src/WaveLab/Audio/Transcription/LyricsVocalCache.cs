namespace WaveLab.Audio.Transcription;

/// <summary>One immutable vocal stem per open tab. No audio is persisted outside job files.</summary>
public sealed class LyricsVocalCache
{
    private Entry? _entry;
    private sealed record Entry(float[][] Source, int Rate, int Version, int Start, int Count,
        bool CpuOnly, string Engine, float[][] Vocals);

    internal sealed record Slice(float[][] Channels, int Start, int Count)
    {
        public AudioDocument ToDocument(string title, bool selection)
        {
            var audio = new AudioDocument(Channels.Select(c => c.AsSpan(Start, Count).ToArray()).ToArray(),
                LyricsAudio.AnalysisRate, 32)
            {
                Title = System.IO.Path.GetFileNameWithoutExtension(title)
                    + (selection ? " - vocals (selection).wav" : " - vocals.wav"),
                RequiresSaveAs = true,
            };
            audio.MarkUnsaved();
            return audio;
        }
    }

    internal Slice? Find(float[][] source, int rate, int version, int start, int count,
        bool cpuOnly, string engine)
    {
        var entry = _entry;
        if (entry == null || entry.Rate != rate || entry.Version != version || entry.CpuOnly != cpuOnly
            || entry.Engine != engine || source.Length != entry.Source.Length
            || source.Where((channel, index) => !ReferenceEquals(channel, entry.Source[index])).Any()
            || start < entry.Start || count <= 0 || (long)start + count > (long)entry.Start + entry.Count)
            return null;
        int first = (int)Math.Round((double)(start - entry.Start) * LyricsAudio.AnalysisRate / rate);
        int last = (int)Math.Round((double)(start - entry.Start + count) * LyricsAudio.AnalysisRate / rate);
        // The original resampler can differ by a few frames at the end of the range.
        last = Math.Min(last, entry.Vocals[0].Length);
        return last > first ? new Slice(entry.Vocals, first, last - first) : null;
    }

    internal void Store(float[][] source, int rate, int version, int start, int count,
        bool cpuOnly, string engine, AudioDocument vocals)
    {
        if (Find(source, rate, version, start, count, cpuOnly, engine) != null) return;
        _entry = new Entry(source.ToArray(), rate, version, start, count, cpuOnly, engine,
            vocals.Channels.ToArray());
    }

    public void Clear() => _entry = null;
}
