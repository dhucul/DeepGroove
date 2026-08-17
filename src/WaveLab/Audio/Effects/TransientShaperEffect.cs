using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Shapes attacks and sustains independently of how loud they are.
/// </summary>
/// <remarks>
/// <para>
/// A compressor acts on level: it cannot tell a loud attack from a loud sustain, so bringing out a
/// snare means also bringing up the room behind it. A transient shaper acts on <em>change</em>
/// instead, by comparing two envelope followers of the same signal — one quick, one slow. Where the
/// quick one is above the slow one the signal is rising, which is an attack; where it is below, the
/// signal is decaying, which is sustain. Neither test involves the absolute level, so a quiet attack
/// is treated like a loud one.
/// </para>
/// <para>
/// That is the whole idea, and it is why the difference is taken in decibels rather than as a ratio
/// of the two envelopes: the difference in dB is the same for a transient at −40 as at −6, which is
/// what makes the effect independent of level rather than merely less dependent on it.
/// </para>
/// </remarks>
public sealed class TransientShaperEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("attack", "ATTACK", -12, 12, 0, EffectParam.Db1),
        new("sustain", "SUSTAIN", -12, 12, 0, EffectParam.Db1),
        new("fast", "FAST", 0.5, 20, 3, EffectParam.Ms),
        new("slow", "SLOW", 20, 400, 120, EffectParam.Ms),
        new("output", "OUTPUT", -12, 12, 0, EffectParam.Db1),
    ];

    private double[] _fast = [];
    private double[] _slow = [];
    private ShaperParameters _parameters = new(0, 0, 0.9, 0.99, 1);
    private double _applied;

    private sealed record ShaperParameters(
        double AttackDb, double SustainDb, double FastCoefficient, double SlowCoefficient, float Output);

    public override string TypeId => "transient";
    public override string DisplayName => "Transient Shaper";
    public override IReadOnlyList<EffectParam> Params => P;

    public override string Readout
    {
        get
        {
            double applied = Volatile.Read(ref _applied);
            return Math.Abs(applied) < 0.05 ? "—" : $"{applied:+0.0;-0.0} dB";
        }
    }

    protected override void OnConfigure()
    {
        _fast = new double[ChannelCount];
        _slow = new double[ChannelCount];
        OnParamsChanged();
    }

    protected override void OnParamsChanged()
    {
        Volatile.Write(ref _parameters, new ShaperParameters(
            GetParam("attack"),
            GetParam("sustain"),
            Coefficient(GetParam("fast")),
            Coefficient(GetParam("slow")),
            (float)Math.Pow(10, GetParam("output") / 20.0)));
    }

    private double Coefficient(double milliseconds) =>
        Math.Exp(-1.0 / Math.Max(1, milliseconds * 0.001 * SampleRate));

    public override void ResetState()
    {
        Array.Clear(_fast);
        Array.Clear(_slow);
        Volatile.Write(ref _applied, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var parameters = Volatile.Read(ref _parameters);
        if (_fast.Length != ChannelCount || _slow.Length != ChannelCount) return;
        if (parameters.AttackDb == 0 && parameters.SustainDb == 0 && parameters.Output == 1) return;

        double peak = 0;
        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float x = buffer[i];
            double magnitude = Math.Abs(x);

            // The fast follower rises instantly; the slow one has to rise at its own rate too. If
            // both jump on the way up they are equal at every attack, their difference is zero
            // exactly where the attack is, and the effect does nothing at all — measured, it moved
            // a struck note by a factor of 1.00.
            _fast[c] = magnitude > _fast[c]
                ? magnitude
                : magnitude + parameters.FastCoefficient * (_fast[c] - magnitude);
            _slow[c] = magnitude + parameters.SlowCoefficient * (_slow[c] - magnitude);

            // In decibels, so a transient at -40 is shaped exactly as one at -6.
            double difference = 20 * Math.Log10(Math.Max(_fast[c], 1e-9) / Math.Max(_slow[c], 1e-9));

            double gainDb = difference > 0
                ? parameters.AttackDb * Math.Min(1, difference / 6.0)
                : parameters.SustainDb * Math.Min(1, -difference / 6.0);

            if (Math.Abs(gainDb) > Math.Abs(peak)) peak = gainDb;
            buffer[i] = (float)(x * Math.Pow(10, gainDb / 20.0)) * parameters.Output;
        }

        Volatile.Write(ref _applied, peak);
    }
}
