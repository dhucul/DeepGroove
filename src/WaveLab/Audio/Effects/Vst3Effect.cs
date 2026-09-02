using WaveLab.Audio.Vst3;

namespace WaveLab.Audio.Effects;

/// <summary>
/// A VST3 plugin as an ordinary member of the effect rack.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does not derive from <see cref="EffectBase"/>, and cannot: that base reads
/// <see cref="Params"/> in its own constructor to size its value store, and a plugin's parameters are
/// not known until it has been loaded, initialised and asked. So parameter storage is the plugin's,
/// not a mirror of it — <see cref="GetParam"/> reads the controller and <see cref="SetParam"/> writes
/// it. There is no local copy to fall out of step with what the plugin thinks its own values are,
/// which matters because the user can also change them from the plugin's own editor.
/// </para>
/// <para>
/// <b>Parameters are normalised, and only the plugin knows what they mean.</b> Every VST3 parameter
/// is a number between zero and one, and the text beside the slider comes from asking the plugin to
/// format it. That is why the rack shows the plugin's own wording — <c>312 ms</c>, <c>−6.0 dB</c>,
/// <c>Bell</c> — rather than a percentage dressed up as a unit it guessed at.
/// </para>
/// <para>
/// <b>A plugin with no parameters is not broken.</b> Several publish none at all and expect to be
/// driven from their own editor, and every plugin installed on the machine this was written on
/// behaves that way. The rack says so rather than showing an empty card.
/// </para>
/// </remarks>
public sealed class Vst3Effect : IAudioEffect, IEffectState, IDisposable
{
    /// <summary>What marks a rack effect as a plugin rather than one of the built-ins.</summary>
    public const string TypeIdPrefix = "vst3:";

    /// <summary>
    /// The longest block handed to a plugin at once. Longer calls are split rather than refused, so
    /// this is a promise the host can keep rather than a limit on the caller.
    /// </summary>
    private const int MaxBlockSamples = 4096;

    /// <summary>
    /// How many parameters get a slider. A plugin with hundreds would bury the rack, and the ones
    /// past the limit are still saved and restored — they live in the plugin's own state, not in the
    /// list of sliders.
    /// </summary>
    public const int MaxRackParameters = 64;

    private readonly Vst3PluginRef _shared;
    private readonly EffectParam[] _parameterDefinitions;
    private readonly Dictionary<string, uint> _parameterIds;
    private readonly object _configureGate = new();

    private byte[]? _pendingState;
    private int _enabled = 1;
    private int _disposed;
    private volatile bool _lastProcessFailed;

    internal Vst3Effect(Vst3PluginRef shared)
    {
        _shared = shared ?? throw new ArgumentNullException(nameof(shared));

        Vst3Plugin plugin = shared.Plugin;
        DisplayName = string.IsNullOrWhiteSpace(plugin.Class.Name)
            ? System.IO.Path.GetFileNameWithoutExtension(shared.Path)
            : plugin.Class.Name;
        Vendor = plugin.Class.Vendor;
        PluginPath = shared.Path;

        // Hidden, read-only, bypass and program-change parameters are left out on purpose. The
        // bypass is the rack's own power switch, a read-only value is not something a slider should
        // pretend to set, and a hidden one was hidden for a reason.
        List<Vst3Parameter> usable =
        [
            .. plugin.Parameters.Where(p => !p.IsHidden && !p.IsReadOnly && !p.IsBypass
                                            && (p.Flags & 1 << 15) == 0)
        ];

        HiddenParameterCount = Math.Max(0, usable.Count - MaxRackParameters);
        if (usable.Count > MaxRackParameters) usable = usable[..MaxRackParameters];

        _parameterDefinitions = new EffectParam[usable.Count];
        _parameterIds = new Dictionary<string, uint>(usable.Count, StringComparer.Ordinal);
        for (int i = 0; i < usable.Count; i++)
        {
            Vst3Parameter parameter = usable[i];
            string key = ParameterKey(parameter.Id);
            uint id = parameter.Id;

            _parameterIds[key] = id;
            _parameterDefinitions[i] = new EffectParam(
                key,
                Label(parameter, i),
                0, 1,
                parameter.DefaultNormalized,
                value => Format(plugin, id, value));
        }

        // An edit made in the plugin's own editor arrives here. The controller already knows; the
        // processor does not, and without this the plugin's own UI would move while its audio did
        // not — the same failure as a rack slider that only sets the controller.
        plugin.ParameterEdited += OnPluginParameterEdited;
    }

    /// <summary>The plugin itself, for the editor window.</summary>
    internal Vst3Plugin Plugin => _shared.Plugin;

    public string TypeId => TypeIdPrefix + PluginPath;
    public string DisplayName { get; }
    public string Vendor { get; }
    public string PluginPath { get; }

    /// <summary>How many parameters were left off the rack because of <see cref="MaxRackParameters"/>.</summary>
    public int HiddenParameterCount { get; }

    /// <summary>Whether the plugin will open an editor of its own.</summary>
    public bool HasEditor
    {
        get
        {
            try { return Volatile.Read(ref _disposed) == 0 && _shared.Plugin.HasEditor; }
            catch { return false; }
        }
    }

    /// <summary>Raised when the plugin's own editor moved something, so the rack can catch up.</summary>
    public event Action? ParameterEditedExternally;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public IReadOnlyList<EffectParam> Params => _parameterDefinitions;

    public double GetParam(string key)
    {
        if (Volatile.Read(ref _disposed) != 0) return 0;
        if (!_parameterIds.TryGetValue(key, out uint id)) return 0;
        try { return Math.Clamp(_shared.Plugin.GetParameter(id), 0, 1); }
        catch { return 0; }
    }

    public void SetParam(string key, double value)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_parameterIds.TryGetValue(key, out uint id) || !double.IsFinite(value)) return;
        try { _shared.Plugin.ApplyParameter(id, Math.Clamp(value, 0, 1)); }
        catch { /* a plugin that refuses an edit is not a reason to fail the UI */ }
    }

    public void Configure(int sampleRate, int channels)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_configureGate)
        {
            // Restored before the plugin is activated, which is the order a plugin expects and the
            // order that lets one size its buffers from the state it is about to run with.
            if (_pendingState is { Length: > 0 } state)
            {
                _pendingState = null;
                try { _shared.Plugin.RestoreState(state); } catch { }
            }

            try
            {
                Configured = _shared.Plugin.Configure(
                    Math.Max(8000, sampleRate), Math.Clamp(channels, 1, 8), MaxBlockSamples);
            }
            catch { Configured = false; }
            _lastProcessFailed = false;
        }
    }

    /// <summary>False when the plugin refused the sample rate or channel count it was given.</summary>
    public bool Configured { get; private set; }

    public void ResetState()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _shared.Plugin.FlushProcessingState(); } catch { }
        _lastProcessFailed = false;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref _disposed) != 0 || !Configured) return;
        try
        {
            // Nothing may leave this method. It is called from the audio callback, and an exception
            // unwinding out of here stops playback for a plugin misbehaving on one block.
            _lastProcessFailed = !_shared.Plugin.ProcessInterleaved(buffer, offset, count);
        }
        catch { _lastProcessFailed = true; }
    }

    public int LatencySamples
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            try { return Math.Max(0, _shared.Plugin.LatencySamples); }
            catch { return 0; }
        }
    }

    // VST3 does not expose a reliable tail length through the interfaces this host supports.
    public int TailSamples => 0;

    /// <summary>
    /// Only ever non-null when something is wrong, because a silent failure inside somebody else's
    /// code is indistinguishable from a plugin that simply does nothing audible.
    /// </summary>
    public string? Readout =>
        Volatile.Read(ref _disposed) != 0 ? null
        : !Configured ? "refused this sample rate"
        : _lastProcessFailed ? "returned no audio"
        : null;

    // ── state ────────────────────────────────────────────────────

    /// <summary>The plugin's settings, for a chain preset. <see cref="IEffectState"/>.</summary>
    public string? SaveStateText() => NullIfEmpty(SaveStateBase64());

    /// <summary><see cref="IEffectState"/>. Immediate, for the reason on <see cref="ApplyStateNow"/>.</summary>
    public void RestoreStateText(string? state) => ApplyStateNow(state);

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The plugin's settings, for a chain preset.</summary>
    public string SaveStateBase64()
    {
        if (Volatile.Read(ref _disposed) != 0) return "";
        try
        {
            byte[] state = _shared.Plugin.SaveState();
            return state.Length == 0 ? "" : Convert.ToBase64String(state);
        }
        catch { return ""; }
    }

    /// <summary>
    /// Puts a saved state into the plugin now — for one just created and not yet in the chain.
    /// </summary>
    /// <remarks>
    /// Immediate rather than deferred because of what follows it. Loading a preset restores the
    /// state and then applies the individual parameter values over the top, and a state that landed
    /// afterwards would undo every one of them.
    /// </remarks>
    public bool ApplyStateNow(string? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (Decode(state) is not { } bytes) return false;
        try { return _shared.Plugin.RestoreState(bytes); }
        catch { return false; }
    }

    /// <summary>
    /// Holds a saved state until the effect is configured, which is when the plugin is ready for it.
    /// </summary>
    /// <remarks>
    /// Deferred, and it has to be: this is how an A/B snapshot carries its settings, and the plugin
    /// it would be writing them into is the live one still playing the other side of the comparison.
    /// </remarks>
    public void DeferState(string? state) => _pendingState = Decode(state);

    private static byte[]? Decode(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(state);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// A copy for an A/B snapshot: the same plugin, and this moment's settings kept beside it.
    /// </summary>
    internal Vst3Effect CloneShared()
    {
        _shared.AddRef();
        var copy = new Vst3Effect(_shared) { Enabled = Enabled };
        copy.DeferState(SaveStateBase64());
        return copy;
    }

    private void OnPluginParameterEdited(Vst3ParameterEdit edit)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _shared.Plugin.QueueParameter(edit.Id, edit.Normalized);
            ParameterEditedExternally?.Invoke();
        }
        catch { }
    }

    // ── presentation helpers ─────────────────────────────────────

    internal static string ParameterKey(uint id) => $"p{id}";

    /// <summary>The plugin's path back out of a rack preset's type id.</summary>
    public static string? PathFromTypeId(string? typeId) =>
        typeId != null && typeId.StartsWith(TypeIdPrefix, StringComparison.Ordinal)
            ? typeId[TypeIdPrefix.Length..]
            : null;

    private static string Label(Vst3Parameter parameter, int index)
    {
        string title = parameter.Title.Trim();
        if (title.Length == 0) title = $"Param {index + 1}";
        return title.ToUpperInvariant();
    }

    /// <summary>
    /// The plugin's own wording for a value, falling back to a percentage when it offers none.
    /// </summary>
    private static string Format(Vst3Plugin plugin, uint id, double normalized)
    {
        try
        {
            string text = plugin.DisplayValue(id, normalized);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { }
        return $"{Math.Clamp(normalized, 0, 1) * 100:0.0} %";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _shared.Plugin.ParameterEdited -= OnPluginParameterEdited; } catch { }
        ParameterEditedExternally = null;

        // Releases a reference, not the plugin: an A/B snapshot may still be holding it.
        _shared.Release();
        GC.SuppressFinalize(this);
    }
}
