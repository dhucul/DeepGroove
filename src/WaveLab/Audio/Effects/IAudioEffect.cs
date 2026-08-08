namespace WaveLab.Audio.Effects;

/// <summary>Descriptor for one automatable effect parameter (drives the auto-generated UI).</summary>
public sealed record EffectParam(string Key, string Label, double Min, double Max, double Default, Func<double, string> Format)
{
    public static string Db(double v) => $"{v:0.0} dB";
    public static string Db1(double v) => $"{v:+0.0;-0.0;0.0} dB";
    public static string Ms(double v) => $"{v:0} ms";
    public static string Hz(double v) => v >= 1000 ? $"{v / 1000:0.0} kHz" : $"{v:0} Hz";
    public static string Pct(double v) => $"{v * 100:0} %";
    public static string Ratio(double v) => $"{v:0.0}:1";
    public static string Plain(double v) => $"{v:0.00}";
}

/// <summary>
/// A real-time audio effect processing interleaved 32-bit float buffers in place.
/// Implementations must be usable both live (master chain) and offline (render/apply).
/// </summary>
public interface IAudioEffect
{
    string TypeId { get; }
    string DisplayName { get; }
    bool Enabled { get; set; }

    IReadOnlyList<EffectParam> Params { get; }
    double GetParam(string key);
    void SetParam(string key, double value);

    /// <summary>Prepare for a stream: sample rate / channel count. Also resets state.</summary>
    void Configure(int sampleRate, int channels);

    /// <summary>Clear processing state (delay lines, envelopes) without touching parameters.</summary>
    void ResetState();

    void Process(float[] buffer, int offset, int count);

    /// <summary>Samples of latency this effect introduces (for offline compensation).</summary>
    int LatencySamples { get; }

    /// <summary>Optional live readout for the UI (e.g. gain reduction), or null.</summary>
    string? Readout { get; }
}

/// <summary>Base class handling parameter storage and defaults.</summary>
public abstract class EffectBase : IAudioEffect
{
    private readonly Dictionary<string, double> _values = new();

    protected EffectBase()
    {
        // ReSharper disable once VirtualMemberCallInConstructor
        foreach (var p in Params) _values[p.Key] = p.Default;
    }

    public abstract string TypeId { get; }
    public abstract string DisplayName { get; }
    public bool Enabled { get; set; } = true;
    public abstract IReadOnlyList<EffectParam> Params { get; }

    protected int SampleRate { get; private set; } = 48000;
    protected int ChannelCount { get; private set; } = 2;

    public double GetParam(string key) => _values.TryGetValue(key, out var v) ? v : 0;

    public void SetParam(string key, double value)
    {
        var p = Params.FirstOrDefault(x => x.Key == key);
        if (p == null) return;
        _values[key] = Math.Clamp(value, p.Min, p.Max);
        OnParamsChanged();
    }

    public void RestoreDefaults()
    {
        foreach (var p in Params) _values[p.Key] = p.Default;
        OnParamsChanged();
    }

    public void Configure(int sampleRate, int channels)
    {
        SampleRate = Math.Max(8000, sampleRate);
        ChannelCount = Math.Max(1, channels);
        OnConfigure();
        OnParamsChanged();
        ResetState();
    }

    public abstract void ResetState();
    public abstract void Process(float[] buffer, int offset, int count);
    public virtual int LatencySamples => 0;
    public virtual string? Readout => null;

    /// <summary>Called after Configure (allocate per-channel state).</summary>
    protected virtual void OnConfigure() { }
    /// <summary>Called whenever a parameter changes (recompute coefficients).</summary>
    protected virtual void OnParamsChanged() { }
}
