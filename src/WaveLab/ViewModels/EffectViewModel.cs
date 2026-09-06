using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Vst3;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed class EffectParamViewModel(IAudioEffect fx, EffectParam param, Action onChanged) : ObservableObject
{
    public string Label => param.Label;
    public double Min => param.Min;
    public double Max => param.Max;
    public double TickFrequency => param.Step > 0 ? param.Step : 1;
    public double SmallChange => param.Step > 0 ? param.Step : 0.1;
    public double LargeChange => param.Step > 0 ? param.Step : 1;
    public bool SnapsToTicks => param.Step > 0;

    // Plugin values are normalized; their display strings need not be numeric or invertible.
    // Explicitly offer slider position for those, never guess a dB/Hz conversion from the text.
    public bool EntryIsPluginPosition => fx is Vst3Effect;
    private double EntryScale => EntryIsPluginPosition || param.Format == (Func<double, string>)EffectParam.Pct ? 100 : 1;
    public string EntryUnit
    {
        get
        {
            if (EntryScale == 100) return "%";
            if (param.Format == (Func<double, string>)EffectParam.Hz) return "Hz";
            // Numeric custom formatters include e.g. fractional milliseconds. Restrict this
            // to a single number plus unit, so a named mode such as "CD 50/15 µs" is not a unit.
            var match = Regex.Match(param.Format(param.Max), @"^[+\-−]?\d+(?:[.,]\d+)?\s*(kHz|Hz|ms|dB|:1)$");
            return match.Success ? match.Groups[1].Value.Replace("kHz", "Hz") : "";
        }
    }

    public string EntryText => (Value * EntryScale).ToString("G15", CultureInfo.CurrentCulture);
    public string EntryHint
    {
        get
        {
            string range = $"Enter {Min * EntryScale:G} to {Max * EntryScale:G} {EntryUnit}.".Replace(" .", ".");
            if (EntryIsPluginPosition)
                return $"Slider position: 0–100%. The plugin's displayed value is {ValueText}.";
            if (param.Step > 0) range += $" Step: {param.Step * EntryScale:G} {EntryUnit}.".Replace(" .", ".");
            return range + $"\nMinimum: {param.Format(Min)} · Maximum: {param.Format(Max)}";
        }
    }

    public string FormatEntryValue(double value)
    {
        if (EntryIsPluginPosition) return param.Format(value);
        string number = (value * EntryScale).ToString("G15", CultureInfo.CurrentCulture);
        if (EntryUnit.Length > 0) return $"{number} {EntryUnit}";
        return param.Format == (Func<double, string>)EffectParam.Plain
            ? number : $"{number} ({param.Format(value)})";
    }

    public bool TryParseEntry(string text, out double value, out string error)
    {
        value = 0;
        error = EntryHint;
        string number = text.Trim().Replace('−', '-');
        double scale = EntryScale;
        string unit = EntryUnit;
        if (unit == "Hz" && number.EndsWith("kHz", StringComparison.OrdinalIgnoreCase))
        {
            number = number[..^3].TrimEnd();
            scale = 0.001;
        }
        else if (unit.Length > 0 && number.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
            number = number[..^unit.Length].TrimEnd();

        if (!(double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out double entered)
            || double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out entered))
            || !double.IsFinite(entered)) return false;
        value = entered / scale;
        if (!double.IsFinite(value) || value < Min || value > Max) return false;
        if (param.Step > 0)
        {
            double steps = (value - Min) / param.Step;
            double nearest = Math.Round(steps, MidpointRounding.AwayFromZero);
            if (Math.Abs(steps - nearest) > 1e-8) return false;
            value = Math.Clamp(Min + nearest * param.Step, Min, Max);
        }
        error = "";
        return true;
    }

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

    // ── the convolution reverb's response ────────────────────────

    private ConvolutionReverbEffect? Reverb => Effect as ConvolutionReverbEffect;

    /// <summary>Whether this card carries the impulse-response row.</summary>
    public bool IsConvolution => Reverb != null;

    /// <summary>
    /// The response's name, or what stands in for it. Never "Load…" — the row has to say which room
    /// you are in, which a control naming only the action it offers cannot.
    /// </summary>
    public string ResponseTitle => Reverb switch
    {
        null => "",
        { ResponseName: { Length: > 0 } name } => name,
        _ => "No impulse response",
    };

    /// <summary>
    /// What the response is, or why there is not one. Written short on purpose: the row is about
    /// 130 px across once the icon and the button have taken theirs, and a line that trims lands
    /// mid-unit — "48 k…" says less than nothing.
    /// </summary>
    public string ResponseDetail => Reverb switch
    {
        null => "",
        { ResponseMissing: true } => "the file is not there",
        { HasResponse: false } => "passes audio through",
        { } reverb =>
            $"{(reverb.ResponseChannels > 1 ? "stereo" : "mono")} · {reverb.ResponseSeconds:0.00} s"
            + (reverb.ResponseSourceRate > 0 ? $" · {reverb.ResponseSourceRate / 1000.0:0.#}k" : ""),
    };

    /// <summary>The full path, for the tool tip — the row itself only has space for a name.</summary>
    public string? ResponseTooltip => Reverb?.ResponsePath;

    /// <summary>True when a response was chosen and is not there, which the row says in amber.</summary>
    public bool ResponseMissing => Reverb?.ResponseMissing == true;

    public bool ResponseLoaded => Reverb?.HasResponse == true;

    /// <summary>Three words for three states, because they are three different requests.</summary>
    public string ResponseAction => Reverb switch
    {
        { ResponseMissing: true } => "Find…",
        { HasResponse: true } => "Change…",
        _ => "Load…",
    };

    /// <summary>
    /// Re-reads everything the row shows, after the effect has been given a new response.
    /// </summary>
    public void RefreshResponse()
    {
        Raise(nameof(ResponseTitle));
        Raise(nameof(ResponseDetail));
        Raise(nameof(ResponseTooltip));
        Raise(nameof(ResponseMissing));
        Raise(nameof(ResponseLoaded));
        Raise(nameof(ResponseAction));
        Raise(nameof(Readout));
        Raise(nameof(HasReadout));
        _changed(this);
    }

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
        "reverb" or "convolution" or "delay" or "chorus" => "time",
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
