using System.Collections.ObjectModel;
using WaveLab.Audio.Effects;
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
        MoveUpCommand = new RelayCommand(() => _move(this, -1), () => _canMove(this, -1));
        MoveDownCommand = new RelayCommand(() => _move(this, +1), () => _canMove(this, +1));
        RemoveCommand = new RelayCommand(() => _remove(this));
        ResetCommand = new RelayCommand(ResetToDefaults);
    }

    public IAudioEffect Effect { get; }
    public ObservableCollection<EffectParamViewModel> Params { get; } = [];
    public string DisplayName => Effect.DisplayName;
    public bool IsEq => Effect is EqEffect;

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

    public double EqLow => Effect.GetParam("low");
    public double EqMid => Effect.GetParam("mid");
    public double EqHigh => Effect.GetParam("high");

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
            Raise(nameof(EqMid));
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
