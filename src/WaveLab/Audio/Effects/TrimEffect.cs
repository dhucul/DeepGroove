using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced gain staging: click-free gain trim, per-channel polarity invert,
/// mid/side gain mode, phase rotation (all-pass), and channel swap.
/// </summary>
public sealed class TrimEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("gain", "GAIN", -24, 24, 0, EffectParam.Db1),
        new("polarityL", "Ø LEFT", 0, 1, 0, v => v > 0.5 ? "INV" : "NORM"),
        new("polarityR", "Ø RIGHT", 0, 1, 0, v => v > 0.5 ? "INV" : "NORM"),
        new("mode", "MODE", 0, 2, 0, v => ((int)v) switch
        {
            0 => "L/R",
            1 => "M/S",
            _ => "SWAP",
        }),
        new("midGain", "MID GAIN", -24, 24, 0, EffectParam.Db1),
        new("sideGain", "SIDE GAIN", -24, 24, 0, EffectParam.Db1),
        new("phaseRotate", "PHASE ROT", 0, 180, 0, v => $"{v:0}°"),
    ];

    private double _currentGain = 1;
    private double _targetGain = 1;
    private double _currentMidGain = 1;
    private double _targetMidGain = 1;
    private double _currentSideGain = 1;
    private double _targetSideGain = 1;
    private Biquad[] _phaseAllPass = [];

    public override string TypeId => "trim";
    public override string DisplayName => "Gain & Trim";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => GetParam("mode") switch
    {
        > 0.5 and < 1.5 => $"M/S M:{GetParam("midGain"):+0.0;-0.0;0.0} S:{GetParam("sideGain"):+0.0;-0.0;0.0} dB",
        > 1.5 => "L↔R SWAP",
        _ => $"{GetParam("gain"):+0.0;-0.0;0.0} dB",
    };

    protected override void OnConfigure()
    {
        _phaseAllPass = new Biquad[ChannelCount];
        RebuildPhaseRotator();
    }

    private void RebuildPhaseRotator()
    {
        // Presets set parameters before MasterSection configures the effect. The
        // filter bank does not exist at that point; OnConfigure will build it.
        if (_phaseAllPass.Length != ChannelCount) return;

        double degrees = GetParam("phaseRotate");
        // Phase rotation via all-pass filter at ~600Hz with Q adjusted for phase shift
        double q = degrees > 1 ? 0.3 + degrees / 180.0 * 1.5 : 0;
        for (int c = 0; c < ChannelCount; c++)
            _phaseAllPass[c] = degrees > 1
                ? Biquad.AllPass(SampleRate, 600, q)
                : Biquad.Identity();
    }

    protected override void OnParamsChanged()
    {
        _targetGain = Math.Pow(10, GetParam("gain") / 20.0);
        _targetMidGain = Math.Pow(10, GetParam("midGain") / 20.0);
        _targetSideGain = Math.Pow(10, GetParam("sideGain") / 20.0);
        RebuildPhaseRotator();
    }

    public override void ResetState()
    {
        _currentGain = _targetGain;
        _currentMidGain = _targetMidGain;
        _currentSideGain = _targetSideGain;
        foreach (var f in _phaseAllPass) f.Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        double smoothing = 1 - Math.Exp(-1.0 / (SampleRate * 0.005));
        bool invertL = GetParam("polarityL") > 0.5;
        bool invertR = GetParam("polarityR") > 0.5;
        int mode = (int)GetParam("mode");

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            _currentGain += smoothing * (_targetGain - _currentGain);
            _currentMidGain += smoothing * (_targetMidGain - _currentMidGain);
            _currentSideGain += smoothing * (_targetSideGain - _currentSideGain);

            int index = offset + frame * ChannelCount;

            if (mode == 2 && ChannelCount >= 2)
            {
                // Channel swap
                float tmp = buffer[index];
                buffer[index] = buffer[index + 1];
                buffer[index + 1] = tmp;
            }
            else if (mode == 1 && ChannelCount >= 2)
            {
                // M/S mode
                double left = buffer[index];
                double right = buffer[index + 1];
                double mid = (left + right) * 0.5;
                double side = (left - right) * 0.5;

                mid *= (float)_currentMidGain;
                side *= (float)_currentSideGain;

                buffer[index] = (float)(mid + side);
                buffer[index + 1] = (float)(mid - side);
            }
            else
            {
                // L/R mode
                float gain = (float)_currentGain;
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    float sample = buffer[index + channel] * gain;

                    // Polarity invert
                    if (channel == 0 && invertL) sample = -sample;
                    if (channel == 1 && invertR) sample = -sample;

                    // Phase rotation
                    sample = _phaseAllPass[channel].Process(sample);

                    buffer[index + channel] = sample;
                }
            }
        }
    }
}
