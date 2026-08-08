using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>Lookahead brickwall limiter (5 ms lookahead, 80 ms release).</summary>
public sealed class LimiterEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("thresh", "THRESH", -24, 0, 0, EffectParam.Db),
        new("ceiling", "CEILING", -12, 0, -1, v => $"{v:0.0} dBTP"),
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
    }

    public override void ResetState() => _limiter.Configure(SampleRate, ChannelCount);

    public override void Process(float[] buffer, int offset, int count)
    {
        _limiter.Enabled = true;
        _limiter.Process(buffer, offset, count);
    }
}
