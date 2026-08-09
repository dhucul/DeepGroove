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
        ResetMetersCommand = new RelayCommand(ResetMeters);
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
            _master.RackEnabled = value;
            Raise();
            Raise(nameof(RackStateText));
            RackStatusText = value
                ? "Rack enabled — effects processing active."
                : "Rack bypassed — all effects processing disabled.";
        }
    }

    public string RackStateText => RackEnabled ? "ACTIVE" : "BYPASSED";
    public string RackStatusText { get => _rackStatusText; private set => Set(ref _rackStatusText, value); }

    /// <summary>The window prompts for a preset name and calls SavePresetAs.</summary>
    public event Action? RequestSavePreset;

    public string? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value) || value == null || _applyingPreset) return;
            var preset = EffectFactory.LoadPresets().FirstOrDefault(p => p.Name == value);
            if (preset == null) return;
            _master.ReplaceChain(EffectFactory.Instantiate(preset));
            SyncFromMaster();
            RackStatusText = $"Preset ‘{preset.Name}’ loaded.";
        }
    }

    // ── chain ops ────────────────────────────────────────────────

    private void AddEffect(string typeId)
    {
        var effect = _master.AddEffect(typeId);
        SyncFromMaster();
        RackStatusText = $"{effect.DisplayName} added to the rack.";
    }

    private void MoveEffect(EffectViewModel vm, int delta)
    {
        _master.MoveEffect(vm.Effect, delta);
        SyncFromMaster();
    }

    private void RemoveEffect(EffectViewModel vm)
    {
        string name = vm.DisplayName;
        if (_master.RemoveEffect(vm.Effect))
        {
            SyncFromMaster();
            RackStatusText = $"{name} removed — processing stopped.";
        }
    }

    private void SetEffectEnabled(EffectViewModel vm, bool enabled)
    {
        if (!_master.SetEffectEnabled(vm.Effect, enabled)) return;
        RackStatusText = enabled
            ? $"{vm.DisplayName} enabled."
            : $"{vm.DisplayName} bypassed.";
    }

    private void ResetChain()
    {
        _master.ReplaceChain([EffectFactory.Create("eq"), EffectFactory.Create("limiter")]);
        _applyingPreset = true;
        SelectedPreset = null;
        _applyingPreset = false;
        SyncFromMaster();
        RackStatusText = "Rack reset to Studio EQ and Precision Limiter.";
    }

    public void SyncFromMaster()
    {
        Effects.Clear();
        int n = 1;
        foreach (var fx in _master.ChainSnapshot)
        {
            var vm = new EffectViewModel(fx, MoveEffect, RemoveEffect, SetEffectEnabled) { NumberText = $"{n:00}" };
            Effects.Add(vm);
            n++;
        }
    }

    public void SavePresetAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            EffectFactory.SavePreset(EffectFactory.Capture(name.Trim(), _master.ChainSnapshot));
            RefreshPresets();
            _applyingPreset = true;
            SelectedPreset = name.Trim();
            _applyingPreset = false;
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

    public void ResetMeters()
    {
        PeakLDb = PeakRDb = RmsLDb = RmsRDb = HoldLDb = HoldRDb = -60;
        _master.ResetMeters();
        Raise(nameof(LufsIntText));
        Raise(nameof(LufsMText));
        Raise(nameof(LufsSText));
        Raise(nameof(LraText));
        Raise(nameof(TruePeakText));
        Raise(nameof(PeakLrText));
        Raise(nameof(CorrelationText));
        Raise(nameof(Correlation));
        Raise(nameof(BalanceDb));
    }
}
