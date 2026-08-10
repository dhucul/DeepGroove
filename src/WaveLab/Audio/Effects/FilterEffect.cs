using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Multi-mode resonant filter: low-pass, high-pass, band-pass, band-stop.
/// Selectable 12dB/oct or 24dB/oct slope. Registered as a single unified effect.
/// </summary>
public sealed class FilterEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("mode", "MODE", 0, 3, 0, v => ((int)v) switch
        {
            0 => "LP",
            1 => "HP",
            2 => "BP",
            _ => "BS",
        }),
        new("cutoff", "CUTOFF", 20, 20000, 1000, EffectParam.Hz),
        new("q", "RES", 0.5, 8, 0.707, EffectParam.Plain),
        new("slope", "SLOPE", 0, 1, 0, v => v > 0.5 ? "24dB" : "12dB"),
    ];

    private Biquad[] _filters1 = [];
    private Biquad[] _filters2 = []; // second stage for 24dB

    public override string TypeId => "filter";
    public override string DisplayName => "Multi-Mode Filter";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure() => Rebuild();
    protected override void OnParamsChanged() => Rebuild();

    private void Rebuild()
    {
        double cutoff = Math.Min(GetParam("cutoff"), SampleRate * 0.45);
        double q = GetParam("q");
        int mode = (int)GetParam("mode");
        bool is24Db = GetParam("slope") > 0.5;

        // For 24dB, use two cascaded 12dB stages with adjusted Q
        double q12 = is24Db ? q * 1.3 : q;

        var rebuilt1 = new Biquad[ChannelCount];
        var rebuilt2 = new Biquad[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            rebuilt1[c] = mode switch
            {
                0 => Biquad.LowPass(SampleRate, cutoff, q12),
                1 => Biquad.HighPass(SampleRate, cutoff, q12),
                2 => Biquad.BandPass(SampleRate, cutoff, q12),
                _ => Biquad.BandStop(SampleRate, cutoff, q12),
            };

            if (is24Db)
            {
                rebuilt2[c] = mode switch
                {
                    0 => Biquad.LowPass(SampleRate, cutoff, q12),
                    1 => Biquad.HighPass(SampleRate, cutoff, q12),
                    2 => Biquad.BandPass(SampleRate, cutoff, q12 * 0.8),
                    _ => Biquad.BandStop(SampleRate, cutoff, q12 * 0.8),
                };
            }
            else
            {
                rebuilt2[c] = Biquad.Identity();
            }
        }
        Volatile.Write(ref _filters1, rebuilt1);
        Volatile.Write(ref _filters2, rebuilt2);
    }

    public override void ResetState() => Rebuild();

    public override void Process(float[] buffer, int offset, int count)
    {
        var filters1 = Volatile.Read(ref _filters1);
        var filters2 = Volatile.Read(ref _filters2);
        if (filters1.Length != ChannelCount) return;

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float v = filters1[c].Process(buffer[i]);
            v = filters2[c].Process(v);
            buffer[i] = v;
        }
    }
}