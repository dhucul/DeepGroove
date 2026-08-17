using System.Collections.ObjectModel;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Vst3;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed class EffectParamViewModel(IAudioEffect fx, EffectParam param, Action onChanged) : ObservableObject
{
    public string Label => param.Label;
    public double Min => param.Min;
    public double Max => param.Max;

    public double Value
    {
        get => fx.GetParam(param.Key);
        set
        {
            fx.SetParam(param.Key, value);
            Raise();
            Raise(nameof(ValueText));
            onChanged();
        }
    }

    public string ValueText => param.Format(fx.GetParam(param.Key));

    public void Refresh()
    {
        Raise(nameof(Value));
        Raise(nameof(ValueText));
    }
}

public sealed class EffectViewModel : ObservableObject
{
    private readonly Action<EffectViewModel, int> _move;
    private readonly Func<EffectViewModel, int, bool> _canMove;
    private readonly Action<EffectViewModel> _remove;
    private readonly Action<EffectViewModel, bool> _setEnabled;
    private readonly Action<EffectViewModel> _changed;

    public EffectViewModel(IAudioEffect effect, Action<EffectViewModel, int> move,
        Func<EffectViewModel, int, bool> canMove,
        Action<EffectViewModel> remove, Action<EffectViewModel, bool> setEnabled,
        Action<EffectViewModel> changed)
    {
        Effect = effect;
        _move = move;
        _canMove = canMove;
        _remove = remove;
        _setEnabled = setEnabled;
        _changed = changed;
        foreach (var p in effect.Params)
            Params.Add(new EffectParamViewModel(effect, p, OnParamChanged));

        // Asking a plugin whether it has an editor means creating one and throwing it away, so it is
        // asked once. The answer cannot change for the life of an instance.
        if (effect is Vst3Effect plugin)
        {
            _plugin = plugin;
            _hasEditor = plugin.HasEditor;
            plugin.ParameterEditedExternally += OnPluginEdited;
        }

        MoveUpCommand = new RelayCommand(() => _move(this, -1), () => _canMove(this, -1));
        MoveDownCommand = new RelayCommand(() => _move(this, +1), () => _canMove(this, +1));
        RemoveCommand = new RelayCommand(() => _remove(this));
        ResetCommand = new RelayCommand(ResetToDefaults);
    }

    private readonly Vst3Effect? _plugin;
    private readonly bool _hasEditor;

    public IAudioEffect Effect { get; }
    public ObservableCollection<EffectParamViewModel> Params { get; } = [];
    public string DisplayName => Effect.DisplayName;
    public bool IsEq => Effect is EqEffect;

    // ── plugins ──────────────────────────────────────────────────

    /// <summary>The plugin behind this card, for the editor window. Null for the built-ins.</summary>
    public Vst3Effect? Plugin => _plugin;

    public bool IsPlugin => _plugin != null;

    /// <summary>Whether to offer the button that opens the plugin's own editor.</summary>
    public bool HasPluginEditor => _hasEditor;

    /// <summary>
    /// Set when a plugin offers a card with nothing on it, which needs saying rather than showing.
    /// </summary>
    /// <remarks>
    /// A plugin that publishes neither parameters nor an editor is not broken and is not rare — every
    /// plugin installed on the machine this was written on is one. An empty card with no explanation
    /// reads as a failure, so the card carries the reason instead.
    /// </remarks>
    public string? PluginNote
    {
        get
        {
            if (_plugin == null) return null;
            if (Params.Count == 0)
                return _hasEditor
                    ? "This plugin publishes no parameters to the host. Open its own editor to "
                      + "operate it."
                    : "This plugin publishes neither parameters nor an editor. It processes audio, "
                      + "but nothing about it can be adjusted from here.";
            return _plugin.HiddenParameterCount > 0
                ? $"Showing the first {Vst3Effect.MaxRackParameters} of "
                  + $"{Params.Count + _plugin.HiddenParameterCount} parameters. The rest are kept in "
                  + "presets, and reachable from the plugin's own editor."
                : null;
        }
    }

    public bool HasPluginNote => PluginNote != null;

    private void OnPluginEdited()
    {
        // The plugin's own editor moved something. The sliders here are reading the plugin, so they
        // are not wrong — they simply have not been told to look again.
        foreach (var p in Params) p.Refresh();
    }

    /// <summary>
    /// Detaches from the plugin. Called when the rack is rebuilt, because these view models are
    /// discarded and replaced wholesale and a plugin outlives several of them.
    /// </summary>
    public void Unhook()
    {
        if (_plugin != null) _plugin.ParameterEditedExternally -= OnPluginEdited;
    }

    /// <summary>Category key used by the rack UI to color-code the slot strip.</summary>
    public string Category => _plugin != null ? "vst3" : Effect.TypeId switch
    {
        "compressor" or "gate" or "limiter" or "normalizer" => "dynamics",
        "eq" or "filter" => "eq",
        "reverb" or "delay" or "chorus" => "time",
        "denoise" or "dehum" => "restoration",
        "saturation" => "color",
        _ => "utility",
    };

    /// <summary>Small caption shown under the effect name in the rack.</summary>
    public string CategoryLabel => _plugin != null
        ? string.IsNullOrWhiteSpace(_plugin.Vendor)
            ? "VST3 PLUG-IN"
            : $"VST3 · {_plugin.Vendor.ToUpperInvariant()}"
        : Category switch
    {
        "dynamics" => "DYNAMICS",
        "eq" => "EQ & FILTER",
        "time" => "TIME & MOD",
        "restoration" => "RESTORATION",
        "color" => "COLOR",
        _ => "UTILITY",
    };


    private string _numberText = "01";
    public string NumberText { get => _numberText; set => Set(ref _numberText, value); }

    public bool Enabled
    {
        get => Effect.Enabled;
        set
        {
            if (Effect.Enabled == value) return;
            _setEnabled(this, value);
            Raise();
        }
    }

    public string? Readout => Effect.Readout;
    public bool HasReadout => Effect.Readout != null;

    public double EqLow => Effect.GetParam("lowGain");
    public double EqLowMid => Effect.GetParam("lmGain");
    public double EqMid => Effect.GetParam("midGain");
    public double EqHighMid => Effect.GetParam("hmGain");
    public double EqHigh => Effect.GetParam("highGain");

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ResetCommand { get; }

    private void ResetToDefaults()
    {
        if (Effect is EffectBase b) b.RestoreDefaults();
        foreach (var p in Params) p.Refresh();
        OnParamChanged();
    }

    private void OnParamChanged()
    {
        if (IsEq)
        {
            Raise(nameof(EqLow));
            Raise(nameof(EqLowMid));
            Raise(nameof(EqMid));
            Raise(nameof(EqHighMid));
            Raise(nameof(EqHigh));
        }
        _changed(this);
    }

    public void TickReadout()
    {
        if (HasReadout) Raise(nameof(Readout));
    }

    public void RefreshAllParams()
    {
        foreach (var p in Params) p.Refresh();
        OnParamChanged();
        Raise(nameof(Enabled));
    }
}
