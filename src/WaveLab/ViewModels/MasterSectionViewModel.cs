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
    private string _rackStatusText = "Rack ready.";
    private int _tick;

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
            _master.RackEnabled = value;
            Raise();
            Raise(nameof(RackStateText));
            RackStatusText = value
                ? "Rack enabled · effects active for playback; source unchanged until render."
                : "Rack bypassed · effects processing disabled; source unchanged.";
            NotifyTopologyChanged(expandedMonoBefore);
        }
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
            if (!Set(ref _selectedPreset, value) || value == null || _applyingPreset) return;
            var preset = EffectFactory.LoadPresets().FirstOrDefault(p => p.Name == value);
            if (preset == null) return;
            bool expandedMonoBefore = _master.ExpandsMonoToStereo;
            _master.ReplaceChain(EffectFactory.Instantiate(preset));
            SyncFromMaster();
            RackStatusText = $"Preset ‘{preset.Name}’ loaded · active in rack; source unchanged until render.";
            NotifyTopologyChanged(expandedMonoBefore);
        }
    }

    // ── chain ops ────────────────────────────────────────────────

    private void AddEffect(string typeId)
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        var effect = _master.AddEffect(typeId);
        MarkChainCustom();
        SyncFromMaster();
        RackStatusText = $"{effect.DisplayName} added · active in rack; source unchanged until render.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private void MoveEffect(EffectViewModel vm, int delta)
    {
        if (!_master.MoveEffect(vm.Effect, delta)) return;
        MarkChainCustom();
        SyncFromMaster();
        RackStatusText = "Effects reordered · active rack updated; source unchanged until render.";
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
        if (_master.RemoveEffect(vm.Effect))
        {
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
        MarkChainCustom();
        RackStatusText = enabled
            ? $"{vm.DisplayName} enabled · active in rack; source unchanged until render."
            : $"{vm.DisplayName} bypassed · source unchanged.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private void ResetChain()
    {
        bool expandedMonoBefore = _master.ExpandsMonoToStereo;
        _master.ReplaceChain(EffectFactory.Instantiate(EffectFactory.CreateFactoryPreset("Default")));
        _applyingPreset = true;
        SelectedPreset = null;
        _applyingPreset = false;
        SyncFromMaster();
        RackStatusText = "Rack reset to Studio EQ with Precision Limiter bypassed · source unchanged until render.";
        NotifyTopologyChanged(expandedMonoBefore);
    }

    private void NotifyTopologyChanged(bool expandedMonoBefore)
    {
        if (expandedMonoBefore != _master.ExpandsMonoToStereo)
            ProcessingTopologyChanged?.Invoke();
    }

    private void OnEffectChanged(EffectViewModel effect)
    {
        MarkChainCustom();
        RackStatusText = $"{effect.DisplayName} adjusted · active in rack; source unchanged until render.";
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
        _master.ReplaceChain(effects);
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

    public void Tick(double dt, bool isPlaying)
    {
        _tick++;
        double ToDb(float linear) => linear <= 1e-5f ? -60 : Math.Max(-60, 20 * Math.Log10(linear));
        double DecayTo(double current, double target, double rate) =>
            target >= current ? target : Math.Max(target, current - rate * dt);

        PeakLDb = DecayTo(_peakL, ToDb(_master.PeakL), 40);
        PeakRDb = DecayTo(_peakR, ToDb(_master.PeakR), 40);
        RmsLDb = DecayTo(_rmsL, ToDb(_master.RmsL), 30);
        RmsRDb = DecayTo(_rmsR, ToDb(_master.RmsR), 30);
        HoldLDb = Math.Max(ToDb(_master.PeakL), _holdL - 6 * dt);
        HoldRDb = Math.Max(ToDb(_master.PeakR), _holdR - 6 * dt);

        Raise(nameof(LufsMText));
        Raise(nameof(LufsSText));
        Raise(nameof(TruePeakText));
        Raise(nameof(PeakLrText));
        Raise(nameof(CorrelationText));
        Raise(nameof(Correlation));
        Raise(nameof(BalanceDb));

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
