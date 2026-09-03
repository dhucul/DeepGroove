using NAudio.Wave;
using WaveLab.Util;

namespace WaveLab.Audio;

/// <summary>Metronome click player for count-in and recording (WASAPI, independent of the main engine).</summary>
public sealed class ClickTrack : IDisposable
{
    private IWavePlayer? _out;

    public void Start(double bpm, int beatsPerBar)
    {
        Stop();
        var provider = new ClickProvider(bpm, beatsPerBar);
        IWavePlayer? output = null;
        try
        {
            output = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(60)
                .WithMmcssThreadPriority()
                .Build();
            output.Init(provider);
            output.Play();
            _out = output;
        }
        catch
        {
            if (output != null) _ = DisposeOutputAsync(output);
            throw;
        }
    }

    public void Stop()
    {
        var output = _out;
        _out = null;
        if (output == null) return;
        // WasapiPlayer.DisposeAsync signals its render thread before yielding.
        // Let the join and endpoint release finish away from the Record dialog's
        // dispatcher so Stop, Cancel and the end of a count-in stay responsive.
        _ = DisposeOutputAsync(output);
    }

    public void Dispose() => Stop();

    private static async Task DisposeOutputAsync(IWavePlayer output)
    {
        try
        {
            if (output is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                await Task.Run(output.Dispose).ConfigureAwait(false);
        }
        catch { }
    }

    private sealed class ClickProvider(double bpm, int beatsPerBar) : ISampleProvider
    {
        private const int Rate = 44100;
        private readonly int _beatSamples = Math.Max(1, (int)(60.0 / Math.Clamp(bpm, 30, 300) * Rate));
        private long _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                long p = _pos + i;
                long beatIndex = p / _beatSamples;
                int within = (int)(p % _beatSamples);
                bool downbeat = beatsPerBar > 0 && beatIndex % beatsPerBar == 0;
                double t = (double)within / Rate;
                double freq = downbeat ? 1500 : 1000;
                double env = t < 0.03 ? Math.Exp(-t * 130) : 0;
                buffer[i] = (float)(Math.Sin(2 * Math.PI * freq * t) * env * 0.5);
            }
            _pos += buffer.Length;
            return buffer.Length;
        }
    }

    /// <summary>Duration of a count-in in milliseconds.</summary>
    public static int CountInMs(double bpm, int beatsPerBar, int bars) =>
        (int)(60000.0 / Math.Clamp(bpm, 30, 300) * beatsPerBar * bars);
}
