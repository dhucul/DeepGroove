using System.Collections.ObjectModel;
using System.Windows;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>Bindable surface over the master rack (effect chain), its presets, and the meters.</summary>
public sealed class MasterSectionViewModel : ObservableObject
{
    private readonly MasterSection _master;
    private double _peakL = -60, _peakR = -60, _rmsL = -60, _rmsR = -60;
    private double _holdL = -60, _holdR = -60;
    private string? _selectedPreset;
    private bool _applyingPreset;
    private bool _automaticallyBypassedAfterRender;
    private string _rackStatusText = "Rack ready.";
    private int _tick;
    // last rendered value of each formatted readout, so a tick only notifies
    // when the string the UI would show has actually moved
    private string? _lufsMCache, _lufsSCache, _truePeakCache, _peakLrCache, _correlationTextCache;
    private double _correlationCache = double.NaN, _balanceCache = double.NaN;

    // shared loudness-history ring (~2 min at 10 Hz), sampled here so the graph
    // records regardless of which analysis tab is visible
    public const int HistoryCapacity = 1200;
    public double[] HistoryMomentary { get; } = new double[HistoryCapacity];
    public double[] HistoryShortTerm { get; } = new double[HistoryCapacity];
    public int HistoryCount { get; private set; }
    public int HistoryPos { get; private set; }

    public MasterSectionViewModel(MasterSection master)
    {
        _master = master;
        AddEffectCommand = new RelayCommand<string>(typeId => { if (typeId != null) AddEffect(typeId); });
        SavePresetCommand = new RelayCommand(() => RequestSavePreset?.Invoke());
        ResetChainCommand = new RelayCommand(ResetChain);
        ResetMetersCommand = new RelayCommand(() => ResetMeters(reportStatus: true));
        SyncFromMaster();
        RefreshPresets();
    }

    public ObservableCollection<EffectViewModel> Effects { get; } = [];
    public ObservableCollection<string> PresetNames { get; } = [];

    public RelayCommand<string> AddEffectCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand ResetChainCommand { get; }
    public RelayCommand ResetMetersCommand { get; }

    public bool RackEnabled
    {
        get => _master.RackEnabled;
        set
        {
            if (_master.RackEnabled == value) return;
            bool expandedMonoBefore = _master.ExpandsMonoToStereo;
            _automaticallyBypassedAfterRender = false;
            _master.RackEnabled = value;
            Raise();
            Raise(nameof(RackStateText));
            RackStatusText = value
                ? "Rack enabled · effects active for playback; source unchanged until render."
                : "Rack bypassed · effects processing disabled; source unchanged.";
            NotifyTopologyChanged(expandedMonoBefore);
        }
    }

    /// <summary>
    /// Prevent an already-rendered document from immediately running through the same rack again.
    /// Unlike an intentional user bypass, the next rack edit leaves this temporary state so the
    /// card controls continue to be a live preview.
    /// </summary>
    internal void BypassAfterRender()
    {
        if (!_master.RackEnabled) return; // preserve an intentional bypass
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        _automaticallyBypassedAfterRender = true;
        _master.RackEnabled = false;
        Raise(nameof(RackEnabled));
        Raise(nameof(RackStateText));
        RackStatusText = "Rack bypassed after render · edit or switch an effect to resume live preview.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    /// <summary>Resume preview only from the temporary render bypass, never from a user bypass.</summary>
    private void ResumePreviewForRackEdit()
    {
        if (!_automaticallyBypassedAfterRender) return;
        _automaticallyBypassedAfterRender = false;
        _master.RackEnabled = true;
        Raise(nameof(RackEnabled));
        Raise(nameof(RackStateText));
    }

    public string RackStateText => RackEnabled ? "ACTIVE" : "BYPASSED";
    public string RackStatusText
    {
        get => _rackStatusText;
        private set
        {
            if (Set(ref _rackStatusText, value)) StatusChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Says something in the rack's status line on behalf of a card that did something the rack
    /// cannot describe for itself — loading an impulse response, and nothing else so far.
    /// </summary>
    public void ReportStatus(string message) => RackStatusText = message;

    /// <summary>The window prompts for a preset name and calls SavePresetAs.</summary>
    public event Action? RequestSavePreset;
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Raised when mono source material starts or stops requiring a stereo
    /// playback stream. WASAPI must be reopened when that topology changes.
    /// </summary>
    public event Action? ProcessingTopologyChanged;

    public string? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (_applyingPreset || value == null)
            {
                Set(ref _selectedPreset, value);
                return;
            }
            if (string.Equals(_selectedPreset, value, StringComparison.Ordinal)) return;

            var preset = EffectFactory.LoadPresets().FirstOrDefault(p => p.Name == value);
            if (preset == null)
            {
                RackStatusText = $"Preset ‘{value}’ is unavailable · current rack unchanged.";
                Raise(nameof(SelectedPreset));
                return;
            }

            try
            {
                ApplyStoredPreset(preset);
                Set(ref _selectedPreset, value);
            }
            catch (Exception ex)
            {
                RackStatusText = $"Could not load preset ‘{value}’: {ex.Message} · current rack unchanged.";
                Raise(nameof(SelectedPreset));
            }
        }
    }

    internal void ApplyStoredPreset(EffectFactory.ChainPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var effects = EffectFactory.Instantiate(preset);
        if (effects.Count == 0)
            throw new ArgumentException("The preset contains no supported effects.", nameof(preset));

        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        ChainReplacing?.Invoke();
        _master.ReplaceChain(effects);
        _automaticallyBypassedAfterRender = false;
        _master.RackEnabled = true;
        Raise(nameof(RackEnabled));
        Raise(nameof(RackStateText));
        SyncFromMaster();
        RackStatusText = $"Preset ‘{preset.Name}’ loaded · rack activated; source unchanged until render.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    // ── chain ops ────────────────────────────────────────────────

    /// <summary>
    /// Raised before one effect leaves the rack, and before it is disposed of.
    /// </summary>
    /// <remarks>
    /// A VST3 plugin's editor is a native window holding the plugin's controller. It has to be shut
    /// before the plugin is released, so this fires while the effect is still whole.
    /// </remarks>
    public event Action<EffectViewModel>? EffectRemoving;

    /// <summary>Raised before the whole chain is swapped, for the same reason.</summary>
    public event Action? ChainReplacing;

    /// <summary>
    /// Adds an effect and sets named parameters on it, for a caller that knows what it is for.
    /// Returns its display name, or null if it could not be created — the rack is unchanged then,
    /// and the status line says so.
    /// </summary>
    public string? AddConfiguredEffect(string typeId, params (string Key, double Value)[] settings) =>
        AddEffect(typeId, settings);

    /// <summary>
    /// Adds an effect, or re-aims the one the rack already holds, for a caller adding it for a
    /// purpose rather than because the user asked for it. Returns its display name, or null if it
    /// could not be created.
    /// </summary>
    /// <remarks>
    /// A second copy of an effect added for a reason is a fault rather than a choice: two limiters
    /// are twice the latency and a second gain stage doing the first one's job. Re-aiming
    /// overwrites settings the user may have dialled in, so the status line says that is what
    /// happened rather than reporting it as an ordinary add.
    /// </remarks>
    public string? ConfigureOrAddEffect(string typeId, params (string Key, double Value)[] settings)
    {
        IAudioEffect? existing = Array.Find(_master.ChainSnapshot, fx => fx.TypeId == typeId);
        if (existing == null) return AddEffect(typeId, settings);

        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        foreach (var (key, value) in settings) existing.SetParam(key, value);
        _master.SetEffectEnabled(existing, true);
        ResumePreviewForRackEdit();
        MarkChainCustom();
        SyncFromMaster();
        RackStatusText = RackEnabled
            ? $"{existing.DisplayName} was already in the rack · re-aimed and active."
            : $"{existing.DisplayName} was already in the rack · re-aimed; rack remains bypassed.";
        NotifyTopologyChanged(expandedMonoBefore);
        return existing.DisplayName;
    }

    private string? AddEffect(string typeId, IReadOnlyList<(string Key, double Value)>? settings = null)
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;

        IAudioEffect effect;
        try
        {
            // The settings travel with the creation so they are on the effect before the chain can
            // see it — Read holds the chain lock for a whole block, so an effect published at its
            // defaults processes at its defaults until the next one. SetParam clamps to each
            // parameter's own range, so a caller cannot put an effect somewhere its UI could not.
            effect = _master.AddEffect(typeId, settings);
        }
        catch (Exception ex)
        {
            // A plugin that will not load is reported here rather than thrown out of a menu click.
            // The rack is unchanged, which is what the message says.
            RackStatusText = $"That effect could not be added: {ex.Message} · rack unchanged.";
            return null;
        }

        MarkChainCustom();
        ResumePreviewForRackEdit();
        SyncFromMaster();
        RackStatusText = RackEnabled
            ? $"{effect.DisplayName} added · active in rack; source unchanged until render."
            : $"{effect.DisplayName} added · rack remains bypassed; source unchanged.";
        NotifyTopologyChanged(expandedMonoBefore);
        return effect.DisplayName;
    }

    private void MoveEffect(EffectViewModel vm, int delta)
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        if (!_master.MoveEffect(vm.Effect, delta)) return;
        ResumePreviewForRackEdit();
        MarkChainCustom();
        SyncFromMaster();
        RackStatusText = RackEnabled
            ? "Effects reordered · active rack updated; source unchanged until render."
            : "Effects reordered · rack remains bypassed; source unchanged.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private bool CanMoveEffect(EffectViewModel vm, int delta)
    {
        int index = Effects.IndexOf(vm);
        int destination = index + delta;
        return index >= 0 && destination >= 0 && destination < Effects.Count;
    }

    private void RemoveEffect(EffectViewModel vm)
    {
        string name = vm.DisplayName;
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        EffectRemoving?.Invoke(vm);
        if (_master.RemoveEffect(vm.Effect))
        {
            ResumePreviewForRackEdit();
            MarkChainCustom();
            SyncFromMaster();
            RackStatusText = $"{name} removed — processing stopped.";
            NotifyTopologyChanged(expandedMonoBefore);
        }
    }

    private void SetEffectEnabled(EffectViewModel vm, bool enabled)
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        if (!_master.SetEffectEnabled(vm.Effect, enabled)) return;
        ResumePreviewForRackEdit();
        MarkChainCustom();
        RackStatusText = enabled
            ? RackEnabled
                ? $"{vm.DisplayName} enabled · active in rack; source unchanged until render."
                : $"{vm.DisplayName} enabled · rack remains bypassed; source unchanged."
            : $"{vm.DisplayName} bypassed · source unchanged.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private void ResetChain()
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        ChainReplacing?.Invoke();
        _master.ReplaceChain(EffectFactory.Instantiate(EffectFactory.CreateFactoryPreset("Default")));
        ResumePreviewForRackEdit();
        _applyingPreset = true;
        SelectedPreset = null;
        _applyingPreset = false;
        SyncFromMaster();
        RackStatusText = RackEnabled
            ? "Rack reset to Studio EQ with Precision Limiter bypassed · live preview active."
            : "Rack reset to Studio EQ with Precision Limiter bypassed · rack remains bypassed.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private void NotifyTopologyChanged(bool expandedMonoBefore)
    {
        if (expandedMonoBefore != _master.ExpandsMonoToStereo)
            ProcessingTopologyChanged?.Invoke();
    }

    private void OnEffectChanged(EffectViewModel effect)
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        ResumePreviewForRackEdit();
        MarkChainCustom();
        RackStatusText = RackEnabled
            ? $"{effect.DisplayName} adjusted · active in rack; source unchanged until render."
            : $"{effect.DisplayName} adjusted · rack remains bypassed; source unchanged.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private void MarkChainCustom()
    {
        if (_selectedPreset == null) return;
        _applyingPreset = true;
        SelectedPreset = null;
        _applyingPreset = false;
    }

    public void SyncFromMaster()
    {
        // Detached before they are dropped: a plugin card subscribes to its plugin, and the plugin
        // outlives the card by several rebuilds of this list.
        foreach (var old in Effects) old.Unhook();
        Effects.Clear();
        int n = 1;
        foreach (var fx in _master.ChainSnapshot)
        {
            var vm = new EffectViewModel(fx, MoveEffect, CanMoveEffect, RemoveEffect, SetEffectEnabled, OnEffectChanged)
                { NumberText = $"{n:00}" };
            Effects.Add(vm);
            n++;
        }
    }

    /// <summary>Atomically replace the live rack with a reviewed analysis result.</summary>
    public void ApplyAnalyzedPreset(EffectFactory.ChainPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var effects = EffectFactory.Instantiate(preset);
        if (effects.Count == 0)
            throw new ArgumentException("The analyzed rack contains no supported effects.", nameof(preset));

        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        ChainReplacing?.Invoke();
        _master.ReplaceChain(effects);
        ResumePreviewForRackEdit();
        _applyingPreset = true;
        SelectedPreset = null;
        _applyingPreset = false;
        SyncFromMaster();
        RackStatusText = RackEnabled
            ? $"{preset.Name} applied · source audio unchanged."
            : $"{preset.Name} applied · rack remains bypassed.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    public void SavePresetAs(string name)
    {
        name = new string((name ?? "").Trim().Where(ch => !char.IsControl(ch)).Take(80).ToArray());
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            EffectFactory.SavePreset(EffectFactory.Capture(name, _master.ChainSnapshot));
            RefreshPresets();
            _applyingPreset = true;
            SelectedPreset = name;
            _applyingPreset = false;
            RackStatusText = $"Preset ‘{name}’ saved.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void RefreshPresets()
    {
        PresetNames.Clear();
        foreach (var p in EffectFactory.LoadPresets()) PresetNames.Add(p.Name);
    }

    // ── meters ───────────────────────────────────────────────────

    public double PeakLDb { get => _peakL; private set => Set(ref _peakL, value); }
    public double PeakRDb { get => _peakR; private set => Set(ref _peakR, value); }
    public double RmsLDb { get => _rmsL; private set => Set(ref _rmsL, value); }
    public double RmsRDb { get => _rmsR; private set => Set(ref _rmsR, value); }
    public double HoldLDb { get => _holdL; private set => Set(ref _holdL, value); }
    public double HoldRDb { get => _holdR; private set => Set(ref _holdR, value); }

    public string LufsIntText => Fmt(_master.Loudness.IntegratedLufs);
    public string LufsMText => Fmt(_master.Loudness.MomentaryLufs);
    public string LufsSText => Fmt(_master.Loudness.ShortTermLufs);
    public string LraText => $"{_master.Loudness.LoudnessRangeLu:0.0}";
    public string TruePeakText => Fmt(_master.Loudness.TruePeakDb);
    public string PeakLrText => $"{Fmt(_holdL)} / {Fmt(_holdR)}";
    public string CorrelationText => $"{_master.Correlation:+0.00;-0.00;0.00}";
    public double Correlation => _master.Correlation;
    public double BalanceDb => _master.BalanceDb;

    private static string Fmt(double v) => double.IsFinite(v) && v > -99 ? $"{v:0.0}" : "—";

    private static double ToDb(float linear) => linear <= 1e-5f ? -60 : Math.Max(-60, 20 * Math.Log10(linear));

    /// <summary>
    /// True while a displayed meter still differs from the level feeding it, so
    /// the UI timer knows a tick would change something. Not bindable: it is
    /// polled, never notified.
    /// </summary>
    public bool NeedsDecay
    {
        get
        {
            double peakL = ToDb(_master.PeakL), peakR = ToDb(_master.PeakR);
            return _peakL != peakL || _peakR != peakR
                || _holdL != peakL || _holdR != peakR
                || _rmsL != ToDb(_master.RmsL) || _rmsR != ToDb(_master.RmsR);
        }
    }

    private void RaiseIfChanged(ref string? cache, string value, string name)
    {
        if (string.Equals(cache, value, StringComparison.Ordinal)) return;
        cache = value;
        Raise(name);
    }

    private void RaiseIfChanged(ref double cache, double value, string name)
    {
        if (cache == value) return;
        cache = value;
        Raise(name);
    }

    /// <summary>Forces the next tick to re-notify every cached readout.</summary>
    private void InvalidateReadoutCaches()
    {
        _lufsMCache = _lufsSCache = _truePeakCache = _peakLrCache = _correlationTextCache = null;
        _correlationCache = _balanceCache = double.NaN;
    }

    public void Tick(double dt, bool isPlaying)
    {
        _tick++;
        double DecayTo(double current, double target, double rate) =>
            target >= current ? target : Math.Max(target, current - rate * dt);

        PeakLDb = DecayTo(_peakL, ToDb(_master.PeakL), 40);
        PeakRDb = DecayTo(_peakR, ToDb(_master.PeakR), 40);
        RmsLDb = DecayTo(_rmsL, ToDb(_master.RmsL), 30);
        RmsRDb = DecayTo(_rmsR, ToDb(_master.RmsR), 30);
        HoldLDb = Math.Max(ToDb(_master.PeakL), _holdL - 6 * dt);
        HoldRDb = Math.Max(ToDb(_master.PeakR), _holdR - 6 * dt);

        // Each of these getters allocates a fresh interpolated string, so notify
        // only when the rendered text (or value) actually moved.
        RaiseIfChanged(ref _lufsMCache, LufsMText, nameof(LufsMText));
        RaiseIfChanged(ref _lufsSCache, LufsSText, nameof(LufsSText));
        RaiseIfChanged(ref _truePeakCache, TruePeakText, nameof(TruePeakText));
        RaiseIfChanged(ref _peakLrCache, PeakLrText, nameof(PeakLrText));
        RaiseIfChanged(ref _correlationTextCache, CorrelationText, nameof(CorrelationText));
        RaiseIfChanged(ref _correlationCache, Correlation, nameof(Correlation));
        RaiseIfChanged(ref _balanceCache, BalanceDb, nameof(BalanceDb));

        // gated-integrated recompute is O(blocks) — 2 Hz is plenty for the readout
        if (_tick % 15 == 0)
        {
            Raise(nameof(LufsIntText));
            Raise(nameof(LraText));
        }

        // ~10 Hz loudness-history sampling while playing
        if (isPlaying && _tick % 3 == 0)
        {
            double m = _master.Loudness.MomentaryLufs;
            double s = _master.Loudness.ShortTermLufs;
            HistoryMomentary[HistoryPos] = double.IsFinite(m) ? m : -40;
            HistoryShortTerm[HistoryPos] = double.IsFinite(s) ? s : -40;
            HistoryPos = (HistoryPos + 1) % HistoryCapacity;
            if (HistoryCount < HistoryCapacity) HistoryCount++;
        }

        foreach (var fx in Effects) fx.TickReadout();
    }

    public void ResetMeters(bool reportStatus = false)
    {
        PeakLDb = PeakRDb = RmsLDb = RmsRDb = HoldLDb = HoldRDb = -60;
        _master.ResetMeters();
        // The unconditional raises below re-read every readout; drop the caches
        // so a later tick cannot mistake a stale entry for the shown value.
        InvalidateReadoutCaches();
        Raise(nameof(LufsIntText));
        Raise(nameof(LufsMText));
        Raise(nameof(LufsSText));
        Raise(nameof(LraText));
        Raise(nameof(TruePeakText));
        if (reportStatus) RackStatusText = "Level and loudness meters reset.";
        Raise(nameof(PeakLrText));
        Raise(nameof(CorrelationText));
        Raise(nameof(Correlation));
        Raise(nameof(BalanceDb));
    }
}
