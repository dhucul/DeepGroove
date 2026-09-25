using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveLab.Audio.Transcription;

/// <summary>Streams a stable document snapshot into a bounded-rate working file, without modifying audio.</summary>
internal static class LyricsAudio
{
    public const int AnalysisRate = 44_100;
    public const int MaximumMinutes = 30;

    public static void Write(float[][] channels, int sampleRate, int start, int count, string path,
        CancellationToken token)
    {
        if (channels.Length == 0 || sampleRate <= 0 || count <= 0 || start < 0
            || channels.Any(c => c == null || (long)start + count > c.Length))
            throw new ArgumentException("Choose a non-empty audio range.");
        if ((double)count / sampleRate > MaximumMinutes * 60)
            throw new ArgumentException($"Select up to {MaximumMinutes} minutes at a time for transcription.");
        ISampleProvider provider = new SnapshotProvider(channels, sampleRate, start, count);
        if (sampleRate != AnalysisRate) provider = new WdlResamplingSampleProvider(provider, AnalysisRate);
        using var writer = new WaveFileWriter(path, provider.WaveFormat);
        var buffer = new float[16_384];
        int read;
        while ((read = provider.Read(buffer.AsSpan())) > 0)
        {
            token.ThrowIfCancellationRequested();
            writer.WriteSamples(buffer, 0, read);
        }
    }

    private sealed class SnapshotProvider : ISampleProvider
    {
        private readonly float[][] _channels;
        private readonly int _end;
        private int _position;
        public WaveFormat WaveFormat { get; }
        public SnapshotProvider(float[][] channels, int rate, int start, int count)
        {
            _channels = channels;
            _position = start;
            _end = start + count;
            // Stereo is preserved for separation. Multichannel files use an all-channel mono mix.
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels.Length == 2 ? 2 : 1);
        }
        public int Read(Span<float> buffer)
        {
            int offset = 0;
            int channels = WaveFormat.Channels;
            int frames = Math.Min(buffer.Length / channels, _end - _position);
            for (int i = 0; i < frames; i++)
            {
                if (channels == 2)
                {
                    buffer[offset++] = Safe(_channels[0][_position]);
                    buffer[offset++] = Safe(_channels[1][_position]);
                }
                else
                {
                    double sum = 0;
                    foreach (var channel in _channels) sum += Safe(channel[_position]);
                    buffer[offset++] = (float)(sum / _channels.Length);
                }
                _position++;
            }
            return frames * channels;
        }
        private static float Safe(float sample) => float.IsFinite(sample) ? sample : 0;
    }
}
