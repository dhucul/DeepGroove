using NAudio.CoreAudioApi;
using NAudio.Wave;
using WaveLab.Util;

namespace WaveLab.Audio;

/// <summary>Metronome click player for count-in and recording (WASAPI, independent of the main engine).</summary>
public sealed class ClickTrack : IDisposable
{
    private WasapiOut? _out;

    public void Start(double bpm, int beatsPerBar)
    {
        Stop();
        var provider = new ClickProvider(bpm, beatsPerBar);
        _out = new WasapiOut(AudioClientShareMode.Shared, 60);
        _out.Init(provider);
        _out.Play();
    }

    public void Stop()
    {
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null;
    }

    public void Dispose() => Stop();

    private sealed class ClickProvider(double bpm, int beatsPerBar) : ISampleProvider
    {
        private const int Rate = 44100;
        private readonly int _beatSamples = Math.Max(1, (int)(60.0 / Math.Clamp(bpm, 30, 300) * Rate));
        private long _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                long p = _pos + i;
                long beatIndex = p / _beatSamples;
                int within = (int)(p % _beatSamples);
                bool downbeat = beatsPerBar > 0 && beatIndex % beatsPerBar == 0;
                double t = (double)within / Rate;
                double freq = downbeat ? 1500 : 1000;
                double env = t < 0.03 ? Math.Exp(-t * 130) : 0;
                buffer[offset + i] = (float)(Math.Sin(2 * Math.PI * freq * t) * env * 0.5);
            }
            _pos += count;
            return count;
        }
    }

    /// <summary>Duration of a count-in in milliseconds.</summary>
    public static int CountInMs(double bpm, int beatsPerBar, int bars) =>
        (int)(60000.0 / Math.Clamp(bpm, 30, 300) * beatsPerBar * bars);
}
