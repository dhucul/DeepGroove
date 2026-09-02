using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// An equaliser band whose gain follows the level in its own frequency range.
/// </summary>
/// <remarks>
/// <para>
/// A static equaliser cutting 3 dB at 300 Hz cuts it always — on the one passage that was boomy and
/// on the twenty that were not. A dynamic band cuts only while the energy <em>in that range</em> is
/// above its threshold, so the correction arrives with the problem and leaves with it. It is the
/// difference between fixing a resonance and dulling a record.
/// </para>
/// <para>
/// Built on <see cref="StateVariableFilter"/> rather than the RBJ biquads used elsewhere, because
/// the gain here moves continuously by design. Biquad coefficients cannot be swapped under a running
/// filter without reinterpreting its delay-line state, which clicks; a topology-preserving structure
/// keeps its state meaningful through the change, which is the whole reason to use one.
/// </para>
/// <para>
/// The detector listens through a band-pass at the same frequency, not to the whole signal. A
/// full-band detector makes this a compressor with a tone control on it: a loud passage anywhere in
/// the spectrum would pull the band down whether or not the range it covers had anything wrong with
/// it.
/// </para>
/// </remarks>
public sealed class DynamicEqEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("freq", "FREQ", 30, 18_000, 300, EffectParam.Hz),
        new("q", "Q", 0.3, 8, 1.2, EffectParam.Plain),
        new("range", "RANGE", -18, 18, -6, EffectParam.Db1),
        new("threshold", "THRESHOLD", -60, 0, -24, EffectParam.Db),
        new("ratio", "AMOUNT", 0, 1, 1, EffectParam.Pct),
        new("attack", "ATTACK", 1, 200, 15, EffectParam.Ms),
        new("release", "RELEASE", 20, 1_000, 150, EffectParam.Ms),
        new("mode", "MODE", 0, 1, 0, v => v > 0.5 ? "SHELF" : "BELL"),
    ];

    private StateVariableFilter[] _band = [];
    private StateVariableFilter[] _detector = [];
    private double _envelope;
    private DynamicParameters _parameters = new(300, 1.2, 0, 0.1, 1, 0.99, 0.999, SvfMode.Peaking);
    private double _reduction;

    private sealed record DynamicParameters(
        double Frequency, double Q, double RangeDb, double Threshold, double Amount,
        double Attack, double Release, SvfMode Mode);

    public override string TypeId => "dyneq";
    public override string DisplayName => "Dynamic EQ";
    public override IReadOnlyList<EffectParam> Params => P;

    public override string Readout
    {
        get
        {
            double reduction = Volatile.Read(ref _reduction);
            return Math.Abs(reduction) < 0.05 ? "—" : $"{reduction:+0.0;-0.0} dB";
        }
    }

    protected override void OnConfigure()
    {
        _band = new StateVariableFilter[ChannelCount];
        _detector = new StateVariableFilter[ChannelCount];
        OnParamsChanged();

        var parameters = Volatile.Read(ref _parameters);
        for (int c = 0; c < ChannelCount; c++)
        {
            _band[c].Set(parameters.Mode, SampleRate, parameters.Frequency, parameters.Q, 0);
            _detector[c].Set(SvfMode.BandPass, SampleRate, parameters.Frequency, parameters.Q);
        }
    }

    protected override void OnParamsChanged()
    {
        double attackMs = GetParam("attack");
        double releaseMs = GetParam("release");

        Volatile.Write(ref _parameters, new DynamicParameters(
            Math.Min(GetParam("freq"), SampleRate * 0.45),
            GetParam("q"),
            GetParam("range"),
            Math.Pow(10, GetParam("threshold") / 20.0),
            GetParam("ratio"),
            Coefficient(attackMs),
            Coefficient(releaseMs),
            GetParam("mode") > 0.5
                ? GetParam("range") >= 0 ? SvfMode.HighShelf : SvfMode.LowShelf
                : SvfMode.Peaking));
    }

    private double Coefficient(double milliseconds) =>
        Math.Exp(-1.0 / Math.Max(1, milliseconds * 0.001 * SampleRate));

    public override void ResetState()
    {
        for (int c = 0; c < _band.Length; c++)
        {
            _band[c].Reset();
            _detector[c].Reset();
        }
        _envelope = 0;
        Volatile.Write(ref _reduction, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var parameters = Volatile.Read(ref _parameters);
        if (_band.Length != ChannelCount || _detector.Length != ChannelCount) return;

        double worst = 0;
        // Frequency and Q can move while the stream is running. The processing band was already
        // retuned below, but the detector used to remain at the values from Configure, so after a
        // sweep the effect changed one band while listening to another. StateVariableFilter keeps
        // its topology and delay state through Set, which makes this safe at a block boundary.
        for (int channel = 0; channel < ChannelCount; channel++)
            _detector[channel].Set(
                SvfMode.BandPass, SampleRate, parameters.Frequency, parameters.Q);

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double magnitude = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                // The detector hears only the range this band covers. Link its result across the
                // frame so a resonance on one side cannot pull the stereo image toward the other.
                float sensed = _detector[channel].Process(buffer[index + channel]);
                magnitude = Math.Max(magnitude, Math.Abs(sensed));
            }
            double coefficient = magnitude > _envelope ? parameters.Attack : parameters.Release;
            _envelope = magnitude + coefficient * (_envelope - magnitude);

            // How far above threshold, in dB, scaled by how much of it to act on.
            double over = _envelope > parameters.Threshold && parameters.Threshold > 0
                ? 20 * Math.Log10(_envelope / parameters.Threshold)
                : 0;
            double gain = Math.Clamp(over * parameters.Amount, 0, Math.Abs(parameters.RangeDb))
                        * Math.Sign(parameters.RangeDb);

            if (Math.Abs(gain) > Math.Abs(worst)) worst = gain;

            // Retuned every frame. That is affordable here and impossible with a biquad: this is
            // what the topology-preserving structure buys.
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                _band[channel].Set(
                    parameters.Mode, SampleRate, parameters.Frequency, parameters.Q, gain);
                buffer[index + channel] = _band[channel].Process(buffer[index + channel]);
            }
        }

        Volatile.Write(ref _reduction, worst);
    }
}
