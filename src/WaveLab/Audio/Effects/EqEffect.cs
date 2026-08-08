using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>3-band Studio EQ: low shelf 80 Hz, peaking 650 Hz, high shelf 8 kHz.</summary>
public sealed class EqEffect : EffectBase
{
    public const double LowFreq = StudioEq.LowFreq, MidFreq = StudioEq.MidFreq, HighFreq = StudioEq.HighFreq;

    private static readonly EffectParam[] P =
    [
        new("low", "LOW", -12, 12, 0, EffectParam.Db1),
        new("mid", "MID", -12, 12, 0, EffectParam.Db1),
        new("high", "HIGH", -12, 12, 0, EffectParam.Db1),
    ];

    private readonly object _lock = new();
    private Biquad[][] _filters = [];

    public override string TypeId => "eq";
    public override string DisplayName => "Studio EQ";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnParamsChanged()
    {
        lock (_lock)
        {
            _filters = new Biquad[3][];
            for (int b = 0; b < 3; b++)
            {
                _filters[b] = new Biquad[ChannelCount];
                for (int c = 0; c < ChannelCount; c++)
                    _filters[b][c] = b switch
                    {
                        0 => Biquad.LowShelf(SampleRate, LowFreq, GetParam("low")),
                        1 => Biquad.Peaking(SampleRate, MidFreq, StudioEq.MidQ, GetParam("mid")),
                        _ => Biquad.HighShelf(SampleRate, HighFreq, GetParam("high")),
                    };
            }
        }
    }

    public override void ResetState() => OnParamsChanged();

    public override void Process(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_filters.Length == 0) return;
            for (int i = offset; i < offset + count; i++)
            {
                int c = (i - offset) % ChannelCount;
                float v = buffer[i];
                for (int b = 0; b < 3; b++) v = _filters[b][c].Process(v);
                buffer[i] = v;
            }
        }
    }
}
