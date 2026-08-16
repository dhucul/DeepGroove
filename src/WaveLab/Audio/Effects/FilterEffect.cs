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

    /// <summary>Immutable coefficient snapshot handed from the UI thread to the audio thread.</summary>
    private sealed record Stages(Biquad One, Biquad Two);

    private Biquad[] _filters1 = [];   // audio-thread state only
    private Biquad[] _filters2 = [];   // second stage for 24dB
    private Stages _stages = new(Biquad.Identity(), Biquad.Identity());

    public override string TypeId => "filter";
    public override string DisplayName => "Multi-Mode Filter";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        _filters1 = new Biquad[ChannelCount];
        _filters2 = new Biquad[ChannelCount];
        Rebuild();
    }

    protected override void OnParamsChanged() => Rebuild();

    private Biquad BuildStage(int mode, double cutoff, double q) => mode switch
    {
        0 => Biquad.LowPass(SampleRate, cutoff, q),
        1 => Biquad.HighPass(SampleRate, cutoff, q),
        2 => Biquad.BandPass(SampleRate, cutoff, q),
        _ => Biquad.BandStop(SampleRate, cutoff, q),
    };

    private void Rebuild()
    {
        double cutoff = Math.Min(GetParam("cutoff"), SampleRate * 0.45);
        double q = GetParam("q");
        int mode = (int)GetParam("mode");
        bool is24Db = GetParam("slope") > 0.5;

        // For 24dB, use two cascaded 12dB stages with adjusted Q
        double q12 = is24Db ? q * 1.3 : q;

        // Publish a whole snapshot instead of writing coefficients into the live
        // filters: a half-applied update could pair a1 from one cutoff with a2 from
        // another, putting the poles outside the unit circle and latching NaN.
        Biquad stage1 = BuildStage(mode, cutoff, q12);
        Biquad stage2 = is24Db
            ? BuildStage(mode, cutoff, mode >= 2 ? q12 * 0.8 : q12)
            : Biquad.Identity();
        Volatile.Write(ref _stages, new Stages(stage1, stage2));
    }

    public override void ResetState()
    {
        for (int c = 0; c < _filters1.Length; c++)
        {
            _filters1[c].Reset();
            _filters2[c].Reset();
        }
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var filters1 = _filters1;
        var filters2 = _filters2;
        if (filters1.Length != ChannelCount || filters2.Length != ChannelCount) return;

        // Copy the published coefficients in once per block. Preserving each
        // biquad's delay-line state keeps cutoff and resonance sweeps free of
        // clicks and zipper noise, and the audio thread owns that state alone.
        var stages = Volatile.Read(ref _stages);
        for (int c = 0; c < ChannelCount; c++)
        {
            filters1[c].CopyCoefficientsFrom(stages.One);
            filters2[c].CopyCoefficientsFrom(stages.Two);
        }

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float v = filters2[c].Process(filters1[c].Process(buffer[i]));
            if (!float.IsFinite(v))
            {
                // Never let a non-finite value latch in the delay lines: without
                // this the channel would stay dead until the effect is reconfigured.
                filters1[c].Reset();
                filters2[c].Reset();
                v = 0;
            }
            buffer[i] = v;
        }
    }
}
