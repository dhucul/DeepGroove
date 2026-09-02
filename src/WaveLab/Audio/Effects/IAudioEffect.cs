namespace WaveLab.Audio.Effects;

/// <summary>Descriptor for one automatable effect parameter (drives the auto-generated UI).</summary>
public sealed record EffectParam(
    string Key,
    string Label,
    double Min,
    double Max,
    double Default,
    Func<double, string> Format,
    double Step = 0)
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

    /// <summary>
    /// Additional output after the last input frame. Offline copy renders may preserve it; an
    /// in-place render stays length-stable.
    /// </summary>
    int TailSamples => 0;

    /// <summary>Optional live readout for the UI (e.g. gain reduction), or null.</summary>
    string? Readout { get; }
}

/// <summary>Base class handling parameter storage and defaults.</summary>
public abstract class EffectBase : IAudioEffect
{
    private readonly EffectParam[] _parameterDefinitions;
    private readonly Dictionary<string, int> _parameterIndices;
    private readonly double[] _values;
    private int _enabled = 1;

    protected EffectBase()
    {
        // ReSharper disable once VirtualMemberCallInConstructor
        _parameterDefinitions = Params.ToArray();
        _parameterIndices = new Dictionary<string, int>(_parameterDefinitions.Length, StringComparer.Ordinal);
        _values = new double[_parameterDefinitions.Length];
        for (int index = 0; index < _parameterDefinitions.Length; index++)
        {
            var parameter = _parameterDefinitions[index];
            if (!_parameterIndices.TryAdd(parameter.Key, index))
                throw new InvalidOperationException($"Effect '{GetType().Name}' repeats parameter key '{parameter.Key}'.");
            _values[index] = parameter.Default;
        }
    }

    public abstract string TypeId { get; }
    public abstract string DisplayName { get; }
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }
    public abstract IReadOnlyList<EffectParam> Params { get; }

    protected int SampleRate { get; private set; } = 48_000;
    protected int ChannelCount { get; private set; } = 2;

    public double GetParam(string key) => _parameterIndices.TryGetValue(key, out int index)
        ? Volatile.Read(ref _values[index])
        : 0;

    public void SetParam(string key, double value)
    {
        if (!_parameterIndices.TryGetValue(key, out int index)) return;
        if (!double.IsFinite(value)) return;
        var parameter = _parameterDefinitions[index];
        double normalized = Math.Clamp(value, parameter.Min, parameter.Max);
        if (double.IsFinite(parameter.Step) && parameter.Step > 0)
        {
            double steps = Math.Round(
                (normalized - parameter.Min) / parameter.Step,
                MidpointRounding.AwayFromZero);
            normalized = Math.Clamp(
                parameter.Min + steps * parameter.Step,
                parameter.Min,
                parameter.Max);
        }
        if (Volatile.Read(ref _values[index]) == normalized) return;
        Volatile.Write(ref _values[index], normalized);
        OnParamsChanged();
    }

    public void RestoreDefaults()
    {
        for (int index = 0; index < _parameterDefinitions.Length; index++)
            Volatile.Write(ref _values[index], _parameterDefinitions[index].Default);
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
    public virtual int TailSamples => 0;
    public virtual string? Readout => null;

    /// <summary>Called after Configure (allocate per-channel state).</summary>
    protected virtual void OnConfigure() { }
    /// <summary>Called whenever a parameter changes (recompute coefficients).</summary>
    protected virtual void OnParamsChanged() { }
}
