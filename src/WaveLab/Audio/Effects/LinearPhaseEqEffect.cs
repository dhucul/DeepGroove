using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// A four-band equaliser that shifts no phase at all.
/// </summary>
/// <remarks>
/// <para>
/// Every minimum-phase equaliser rotates phase around the frequencies it touches — that is not a
/// defect, it is what a filter is, and on most material it is inaudible or even wanted. It matters
/// when several corrections stack on a mastering chain, or when a boost and a cut sit close enough
/// for their phase shifts to interact and produce a dip neither of them asked for.
/// </para>
/// <para>
/// A linear-phase equaliser has no such interaction: its impulse response is symmetric, so every
/// frequency is delayed by exactly the same amount and nothing is rotated relative to anything else.
/// The price is that delay — half the kernel — and pre-ringing, which is the symmetric response's
/// other half arriving <em>before</em> the transient it belongs to. That is a real artefact, audible
/// on sharp attacks with heavy low-frequency correction, and the reason this exists beside the
/// minimum-phase equaliser rather than replacing it.
/// </para>
/// <para>
/// The curve is built by sampling the wanted magnitude response and taking the zero-phase impulse
/// response of it, windowed. It is run through <see cref="PartitionedConvolver"/> so the latency is
/// one block rather than the whole kernel.
/// </para>
/// </remarks>
public sealed class LinearPhaseEqEffect : EffectBase
{
    private const int Taps = 4_096;
    private const int Block = 256;

    private static readonly EffectParam[] P =
    [
        new("lowfreq", "LOW", 20, 500, 100, EffectParam.Hz),
        new("lowgain", "LOW GAIN", -18, 18, 0, EffectParam.Db1),
        new("lowmidfreq", "LOW MID", 100, 2_000, 400, EffectParam.Hz),
        new("lowmidgain", "LM GAIN", -18, 18, 0, EffectParam.Db1),
        new("highmidfreq", "HIGH MID", 800, 8_000, 2_500, EffectParam.Hz),
        new("highmidgain", "HM GAIN", -18, 18, 0, EffectParam.Db1),
        new("highfreq", "HIGH", 2_000, 18_000, 8_000, EffectParam.Hz),
        new("highgain", "HIGH GAIN", -18, 18, 0, EffectParam.Db1),
        new("q", "WIDTH", 0.3, 4, 1, EffectParam.Plain),
    ];

    private PartitionedConvolver? _convolver;
    private float[][] _incoming = [];   // the block being filled
    private float[][] _ready = [];      // the block already convolved, being handed out
    private int _cursor;
    private bool _flat = true;

    public override string TypeId => "linphase-eq";
    public override string DisplayName => "Linear Phase EQ";
    public override IReadOnlyList<EffectParam> Params => P;

    /// <summary>
    /// One block of buffering plus half the kernel. The convolver itself adds nothing; the block
    /// comes from having to fill one before it can be processed, and the half-kernel is the group
    /// delay of a symmetric response — the unavoidable cost of linear phase.
    /// </summary>
    public override int LatencySamples =>
        Volatile.Read(ref _convolver) is null ? 0 : Block + Taps / 2;

    protected override void OnConfigure()
    {
        _incoming = new float[ChannelCount][];
        _ready = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _incoming[c] = new float[Block];
            _ready[c] = new float[Block];
        }
        _cursor = 0;
        Rebuild();
    }

    protected override void OnParamsChanged() => Rebuild();

    private void Rebuild()
    {
        if (ChannelCount <= 0 || SampleRate <= 0) return;

        var bands = new (double Frequency, double GainDb)[]
        {
            (GetParam("lowfreq"), GetParam("lowgain")),
            (GetParam("lowmidfreq"), GetParam("lowmidgain")),
            (GetParam("highmidfreq"), GetParam("highmidgain")),
            (GetParam("highfreq"), GetParam("highgain")),
        };

        _flat = true;
        foreach ((double _, double gainDb) in bands) if (Math.Abs(gainDb) > 0.01) _flat = false;
        if (_flat)
        {
            Volatile.Write(ref _convolver, null);
            return;
        }

        double q = GetParam("q");
        int size = Taps * 2;
        var magnitude = new double[size];

        for (int k = 0; k <= size / 2; k++)
        {
            double frequency = Math.Max(1, (double)k * SampleRate / size);
            double db = 0;
            foreach ((double centre, double gainDb) in bands)
            {
                if (Math.Abs(gainDb) < 0.01) continue;

                // A bell in log-frequency, which is how an equaliser's width is actually perceived:
                // an octave is an octave whether it sits at 100 Hz or at 10 kHz.
                double octaves = Math.Log2(frequency / centre) * q;
                db += gainDb * Math.Exp(-octaves * octaves * 2);
            }
            magnitude[k] = Math.Pow(10, db / 20.0);
            if (k > 0 && k < size / 2) magnitude[size - k] = magnitude[k];
        }

        double[] impulse = MinimumPhase.ZeroPhase(magnitude);
        var kernel = new float[Taps];
        int half = Taps / 2;
        for (int i = 0; i < Taps; i++)
        {
            // Symmetric about the centre — that symmetry is precisely what makes it linear phase —
            // and windowed, because a truncated response ripples.
            int source = (i - half + size) % size;
            double window = 0.42
                          - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1))
                          + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            kernel[i] = (float)(impulse[source] * window);
        }

        Volatile.Write(ref _convolver, new PartitionedConvolver(kernel, ChannelCount, Block));
    }

    public override void ResetState()
    {
        Volatile.Read(ref _convolver)?.Reset();
        foreach (float[] channel in _incoming) Array.Clear(channel);
        foreach (float[] channel in _ready) Array.Clear(channel);
        _cursor = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        PartitionedConvolver? convolver = Volatile.Read(ref _convolver);
        if (convolver == null || _incoming.Length != ChannelCount) return;

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            // Hand out the block convolved last time and take in this sample, both at the same
            // cursor. That is what makes the latency exactly one block, and it is why the two
            // buffers are swapped rather than written through: a single buffer would overwrite
            // samples still waiting to be handed out.
            for (int c = 0; c < ChannelCount; c++)
            {
                int index = offset + f * ChannelCount + c;
                float incoming = buffer[index];
                buffer[index] = _ready[c][_cursor];
                _incoming[c][_cursor] = incoming;
            }

            if (++_cursor < Block) continue;

            for (int c = 0; c < ChannelCount; c++) convolver.ProcessBlock(c, _incoming[c]);
            (_incoming, _ready) = (_ready, _incoming);
            _cursor = 0;
        }
    }
}
