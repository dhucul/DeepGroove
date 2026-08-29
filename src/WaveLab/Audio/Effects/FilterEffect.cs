using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Multi-mode resonant filter: low-pass, high-pass, band-pass, band-stop.
/// Selectable 12dB/oct or 24dB/oct slope. Registered as a single unified effect.
/// </summary>
public sealed class FilterEffect : EffectBase
{
    private const int LinearBlock = 256;

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
        new("phase", "HP PHASE", 0, 1, 0, v => v > 0.5 ? "RESTORE" : "LIVE"),
    ];

    private Biquad[] _filters1 = [];   // audio-thread state only
    /// <summary>Q of a second-order Butterworth: the reference the user's Q is expressed against.</summary>
    private const double ButterworthQ = 0.70710678118654752;

    /// <summary>The two section Qs that make a fourth-order Butterworth.</summary>
    private static readonly double[] FourthOrderQ = [0.54119610014619698, 1.30656296487637652];

    private Biquad[] _filters2 = [];   // second stage for 24dB
    private BiquadCoefficients _stages = new(Biquad.Identity(), Biquad.Identity());
    private sealed record LinearState(PartitionedConvolver Convolver, int KernelDelay);
    private LinearState? _linearState;
    private float[][] _linearIncoming = [];
    private float[][] _linearReady = [];
    private int _linearCursor;
    private int _linearResetRequested;
    private int _iirResetRequested;

    public override string TypeId => "filter";
    public override string DisplayName => "Multi-Mode Filter";
    public override IReadOnlyList<EffectParam> Params => P;
    public override int LatencySamples => Volatile.Read(ref _linearState) is { } state
        ? LinearBlock + state.KernelDelay
        : 0;

    protected override void OnConfigure()
    {
        _filters1 = new Biquad[ChannelCount];
        _filters2 = new Biquad[ChannelCount];
        _linearIncoming = new float[ChannelCount][];
        _linearReady = new float[ChannelCount][];
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            _linearIncoming[channel] = new float[LinearBlock];
            _linearReady[channel] = new float[LinearBlock];
        }
        _linearCursor = 0;
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
        bool wasLinear = Volatile.Read(ref _linearState) != null;
        double cutoff = Math.Min(GetParam("cutoff"), SampleRate * 0.45);
        double q = GetParam("q");
        int mode = (int)GetParam("mode");
        bool is24Db = GetParam("slope") > 0.5;

        // A real-time path cannot look into the future, so the same symmetric response used by
        // Vinyl Restoration is emitted with its group delay intact. MasterSection compensates the
        // reported delay for offline render; live playback hears only an ordinary fixed latency.
        // Restrict this mode to high-pass, because the shared kernel is specifically the
        // restoration subsonic response and the control is labelled accordingly.
        if (mode == 1 && GetParam("phase") > 0.5)
        {
            float[] kernel = Restoration.SubsonicKernel(SampleRate, cutoff);
            Volatile.Write(ref _linearState, new LinearState(
                new PartitionedConvolver(kernel, ChannelCount, LinearBlock), kernel.Length / 2));
            Interlocked.Exchange(ref _linearResetRequested, 1);
            return;
        }
        Volatile.Write(ref _linearState, null);
        if (wasLinear) Interlocked.Exchange(ref _iirResetRequested, 1);

        // Publish a whole snapshot instead of writing coefficients into the live
        // filters: a half-applied update could pair a1 from one cutoff with a2 from
        // another, putting the poles outside the unit circle and latching NaN.
        Biquad stage1, stage2;
        if (is24Db)
        {
            // A fourth-order Butterworth is two second-order sections at these two specific Q
            // values — they are the pole pair's positions on the unit circle, not a tuning choice.
            // What was here before scaled the user's Q by 1.3 for both stages and then by a further
            // 0.8 for one of them, which is flat at no setting: with Q left at 0.707 the response
            // peaked about 1.5 dB before the corner instead of being maximally flat, and the
            // resonance control stopped meaning anything recognisable.
            double scale = q / ButterworthQ;
            stage1 = BuildStage(mode, cutoff, FourthOrderQ[0] * scale);
            stage2 = BuildStage(mode, cutoff, FourthOrderQ[1] * scale);
        }
        else
        {
            stage1 = BuildStage(mode, cutoff, q);
            stage2 = Biquad.Identity();
        }
        Volatile.Write(ref _stages, new BiquadCoefficients(stage1, stage2));
    }

    public override void ResetState()
    {
        for (int c = 0; c < _filters1.Length; c++)
        {
            _filters1[c].Reset();
            _filters2[c].Reset();
        }
        Volatile.Read(ref _linearState)?.Convolver.Reset();
        foreach (float[] channel in _linearIncoming) Array.Clear(channel);
        foreach (float[] channel in _linearReady) Array.Clear(channel);
        _linearCursor = 0;
        Interlocked.Exchange(ref _linearResetRequested, 0);
        Interlocked.Exchange(ref _iirResetRequested, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        LinearState? linear = Volatile.Read(ref _linearState);
        if (linear != null)
        {
            if (_linearIncoming.Length != ChannelCount) return;
            if (Interlocked.Exchange(ref _linearResetRequested, 0) != 0)
            {
                linear.Convolver.Reset();
                foreach (float[] channel in _linearIncoming) Array.Clear(channel);
                foreach (float[] channel in _linearReady) Array.Clear(channel);
                _linearCursor = 0;
            }
            int frames = count / ChannelCount;
            for (int frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    int index = offset + frame * ChannelCount + channel;
                    float incoming = buffer[index];
                    buffer[index] = _linearReady[channel][_linearCursor];
                    _linearIncoming[channel][_linearCursor] = incoming;
                }

                if (++_linearCursor < LinearBlock) continue;
                for (int channel = 0; channel < ChannelCount; channel++)
                    linear.Convolver.ProcessBlock(channel, _linearIncoming[channel]);
                (_linearIncoming, _linearReady) = (_linearReady, _linearIncoming);
                _linearCursor = 0;
            }
            return;
        }

        var filters1 = _filters1;
        var filters2 = _filters2;
        if (filters1.Length != ChannelCount || filters2.Length != ChannelCount) return;
        if (Interlocked.Exchange(ref _iirResetRequested, 0) != 0)
        {
            // Biquad is a value type, so foreach would reset only a copy and leave the
            // live per-channel delay lines untouched when returning from restoration phase.
            for (int channel = 0; channel < filters1.Length; channel++)
            {
                filters1[channel].Reset();
                filters2[channel].Reset();
            }
        }

        // Copy the published coefficients in once per block. Preserving each
        // biquad's delay-line state keeps cutoff and resonance sweeps free of
        // clicks and zipper noise, and the audio thread owns that state alone.
        var stages = Volatile.Read(ref _stages);
        stages.ApplyTo(filters1, 0);
        stages.ApplyTo(filters2, 1);

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
