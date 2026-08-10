using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Lookahead brickwall limiter with true-peak oversampling, ISP protection,
/// and program-dependent release (5 ms lookahead, adaptive release).
/// THRESH drives the program into the fixed ceiling maximizer-style; at the
/// 0 dB default it is transparent peak protection that only catches overs.
/// </summary>
public sealed class LimiterEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("thresh", "THRESH", -24, 0, 0, EffectParam.Db),
        new("ceiling", "CEILING", -12, 0, -1, v => $"{v:0.0} dBTP"),
        new("oversample", "TRUE PEAK", 0, 1, 1, v => v > 0.5 ? "ON" : "OFF"),
    ];


    private readonly Limiter _limiter = new();

    public override string TypeId => "limiter";
    public override string DisplayName => "Precision Limiter";
    public override IReadOnlyList<EffectParam> Params => P;
    public override int LatencySamples => Math.Max(1, SampleRate * 5 / 1000);
    public override string? Readout => $"GR −{_limiter.GainReductionDb:0.0} dB";

    protected override void OnConfigure() => _limiter.Configure(SampleRate, ChannelCount);

    protected override void OnParamsChanged()
    {
        _limiter.ThresholdDb = GetParam("thresh");
        _limiter.CeilingDb = GetParam("ceiling");
        _limiter.Oversample = GetParam("oversample") > 0.5;
    }

    public override void ResetState() => _limiter.Configure(SampleRate, ChannelCount);

    public override void Process(float[] buffer, int offset, int count)
    {
        _limiter.Enabled = true;
        _limiter.Process(buffer, offset, count);
    }
}