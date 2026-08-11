using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// 5-band parametric EQ: low shelf, low-mid peaking, mid peaking, high-mid peaking, high shelf.
/// Each band has sweepable frequency, adjustable Q, and ±12 dB gain.
/// </summary>
public sealed class EqEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        // Band 1: Low Shelf
        new("lowGain", "LOW GAIN", -12, 12, 0, EffectParam.Db1),
        new("lowFreq", "LOW FREQ", 30, 500, 80, EffectParam.Hz),
        new("lowQ", "LOW Q", 0.3, 2, 0.707, EffectParam.Plain),

        // Band 2: Low-Mid Peaking
        new("lmGain", "L-MID GAIN", -12, 12, 0, EffectParam.Db1),
        new("lmFreq", "L-MID FREQ", 100, 2000, 250, EffectParam.Hz),
        new("lmQ", "L-MID Q", 0.3, 6, 1.0, EffectParam.Plain),

        // Band 3: Mid Peaking
        new("midGain", "MID GAIN", -12, 12, 0, EffectParam.Db1),
        new("midFreq", "MID FREQ", 200, 8000, 650, EffectParam.Hz),
        new("midQ", "MID Q", 0.3, 6, 1.0, EffectParam.Plain),

        // Band 4: High-Mid Peaking
        new("hmGain", "H-MID GAIN", -12, 12, 0, EffectParam.Db1),
        new("hmFreq", "H-MID FREQ", 500, 12000, 2500, EffectParam.Hz),
        new("hmQ", "H-MID Q", 0.3, 6, 1.0, EffectParam.Plain),

        // Band 5: High Shelf
        new("highGain", "HIGH GAIN", -12, 12, 0, EffectParam.Db1),
        new("highFreq", "HIGH FREQ", 2000, 20000, 8000, EffectParam.Hz),
        new("highQ", "HIGH Q", 0.3, 2, 0.707, EffectParam.Plain),
    ];

    private readonly object _lock = new();
    private Biquad[][] _filters = [];
    private const int BandCount = 5;

    public override string TypeId => "eq";
    public override string DisplayName => "Parametric EQ";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnParamsChanged()
    {
        lock (_lock)
        {
            if (_filters.Length != BandCount || _filters[0].Length != ChannelCount)
            {
                _filters = new Biquad[BandCount][];
                for (int b = 0; b < BandCount; b++)
                    _filters[b] = new Biquad[ChannelCount];
            }

            // Update coefficients in place: preserving each biquad's delay-line
            // state keeps live sweeps free of clicks and zipper noise.
            for (int b = 0; b < BandCount; b++)
            {
                Biquad updated = BuildBand(b);
                for (int c = 0; c < ChannelCount; c++)
                    _filters[b][c].CopyCoefficientsFrom(updated);
            }
        }
    }


    private Biquad BuildBand(int band)
    {
        return band switch
        {
            0 => Biquad.LowShelf(SampleRate, GetParam("lowFreq"), GetParam("lowGain"), GetParam("lowQ")),
            1 => Biquad.Peaking(SampleRate, GetParam("lmFreq"), GetParam("lmQ"), GetParam("lmGain")),
            2 => Biquad.Peaking(SampleRate, GetParam("midFreq"), GetParam("midQ"), GetParam("midGain")),
            3 => Biquad.Peaking(SampleRate, GetParam("hmFreq"), GetParam("hmQ"), GetParam("hmGain")),
            _ => Biquad.HighShelf(SampleRate, GetParam("highFreq"), GetParam("highGain"), GetParam("highQ")),
        };
    }

    public override void ResetState()
    {
        lock (_lock)
            foreach (var band in _filters)
                for (int c = 0; c < band.Length; c++)
                    band[c].Reset();
    }


    public override void Process(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_filters.Length == 0) return;
            for (int i = offset; i < offset + count; i++)
            {
                int c = (i - offset) % ChannelCount;
                float v = buffer[i];
                for (int b = 0; b < BandCount; b++)
                    v = _filters[b][c].Process(v);
                buffer[i] = v;
            }
        }
    }
}