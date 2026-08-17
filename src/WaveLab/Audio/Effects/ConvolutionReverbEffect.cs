using System.IO;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// An effect that carries something a preset cannot express as a number.
/// </summary>
/// <remarks>
/// A rack preset stores each effect's parameters as a dictionary of doubles, which is everything the
/// built-ins have and not everything every effect has. A plugin's settings are an opaque run of
/// bytes; a convolution reverb's identity is which file it loaded. Both go in the same place, and
/// both are written last and read first so that a preset saved before either existed still loads.
/// </remarks>
public interface IEffectState
{
    /// <summary>The effect's own state as text, or null when it has none worth saving.</summary>
    string? SaveStateText();

    /// <summary>Puts a saved state back. Called before the individual parameters are applied.</summary>
    void RestoreStateText(string? state);
}

/// <summary>
/// Convolution reverb: a real room, or a real plate, or a real spring, as measured.
/// </summary>
/// <remarks>
/// <para>
/// The expensive part already existed. <see cref="PartitionedConvolver"/> was built for the
/// linear-phase equaliser, and uniformly-partitioned overlap-save convolution does not care whether
/// the kernel is four thousand taps of equaliser or four hundred thousand taps of cathedral — the
/// cost per sample is set by the partition size, not by the length of the response. That is the
/// whole reason this is a small file.
/// </para>
/// <para>
/// <b>The response is normalised to unit power, not to unit peak.</b> Impulse responses in the wild
/// differ by tens of decibels for reasons that have nothing to do with how loud the room was — how
/// far the microphone was from the source, what the interface's gain was set to, whether anyone
/// normalised it afterwards. Matching peaks would make a dense hall quieter than a sparse room,
/// because a dense one spreads the same energy over more samples. Matching power means the wet
/// signal arrives at roughly the level the dry one left at, whatever was loaded, which is what makes
/// the mix control mean something across two different files.
/// </para>
/// <para>
/// <b>A stereo response is two responses, not one.</b> The left channel is convolved with the left
/// of the file and the right with the right, which is what preserves the width the room was recorded
/// with. A mono response is used for both, which is a mono room and sounds like one.
/// </para>
/// <para>
/// Pre-delay is a delay line on the wet path rather than zeros in front of the kernel. Both are
/// correct; only one of them avoids rebuilding a hundred thousand taps of partitioned convolution
/// every time the control moves.
/// </para>
/// </remarks>
public sealed class ConvolutionReverbEffect : EffectBase, IEffectState
{
    private const int Block = 256;

    /// <summary>
    /// The longest response accepted, in seconds. Past this the cost stops being about reverb and
    /// starts being about somebody having handed the effect a whole song.
    /// </summary>
    public const double MaximumSeconds = 12;

    /// <summary>The longest pre-delay offered, in milliseconds.</summary>
    public const double MaximumPreDelayMs = 250;

    private static readonly EffectParam[] P =
    [
        new("mix", "MIX", 0, 1, 0.25, EffectParam.Pct),
        new("predelay", "PRE-DELAY", 0, MaximumPreDelayMs, 0, EffectParam.Ms),
        new("trim", "TRIM", -24, 12, 0, EffectParam.Db1),
        new("damp", "DAMPING", 1_000, DampingOffHz, DampingOffHz, EffectParam.Hz),
    ];

    /// <summary>
    /// The top of the damping control, which means off.
    /// </summary>
    /// <remarks>
    /// Off has to be exactly off, and comparing the corner against some fraction of the sample rate
    /// does not give that: at 48 kHz a 20 kHz corner sits below nine tenths of Nyquist, so the
    /// filter stayed switched in at the control's maximum and quietly cost the reverb its top. The
    /// test that measures the output against a direct convolution found it at 22 dB where it should
    /// have read 100.
    /// </remarks>
    public const double DampingOffHz = 20_000;

    // The loaded response, per channel, already at the session's sample rate.
    private float[][] _response = [];
    private string? _responsePath;
    private string _responseName = "";
    private double _responseSeconds;

    private PartitionedConvolver?[] _convolvers = [];
    private float[][] _incoming = [];   // the block being filled
    private float[][] _wet = [];        // that block, convolved, being handed out
    private float[][] _dry = [];        // that block, untouched, being handed out
    private int _cursor;

    private float[][] _preDelay = [];   // ring buffer on the wet path
    private int _preDelayCursor;
    private float[] _dampState = [];

    public override string TypeId => "convolution";
    public override string DisplayName => "Convolution Reverb";
    public override IReadOnlyList<EffectParam> Params => P;

    /// <summary>The file the response came from, or null when none is loaded.</summary>
    public string? ResponsePath => _responsePath;

    /// <summary>What to call the loaded response in the UI.</summary>
    public string ResponseName => _responseName;

    /// <summary>How long the loaded response is, in seconds. Zero when none is loaded.</summary>
    public double ResponseSeconds => _responseSeconds;

    public bool HasResponse => Volatile.Read(ref _convolvers).Length > 0;

    /// <summary>
    /// One block, and no more. The convolver itself is zero-latency by construction; the block is
    /// the cost of having to fill one before it can be processed, and the dry path is delayed by the
    /// same block so the two still line up.
    /// </summary>
    public override int LatencySamples => HasResponse ? Block : 0;

    public override string? Readout => HasResponse
        ? null
        : "no impulse response loaded";

    // ── loading ──────────────────────────────────────────────────

    /// <summary>
    /// Loads an impulse response from an audio file. Returns false with a reason rather than
    /// throwing, because the caller is a file picker and a bad file is an ordinary outcome.
    /// </summary>
    public bool LoadResponse(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No file.";
            return false;
        }

        try
        {
            AudioDocument document = AudioImporter.Load(path);
            if (document.Length == 0 || document.Channels.Count == 0)
            {
                error = "That file holds no audio.";
                return false;
            }

            var loaded = new float[document.Channels.Count][];
            for (int c = 0; c < loaded.Length; c++) loaded[c] = (float[])document.Channels[c].Clone();

            _responsePath = path;
            _responseName = Path.GetFileNameWithoutExtension(path);
            SetResponse(loaded, document.SampleRate);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Drops the response, leaving the effect a bypass.</summary>
    public void ClearResponse()
    {
        _responsePath = null;
        _responseName = "";
        _responseSeconds = 0;
        _response = [];
        Volatile.Write(ref _convolvers, []);
    }

    /// <summary>
    /// Installs a response given at some sample rate, resampling and normalising it.
    /// </summary>
    internal void SetResponse(float[][] channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length == 0 || sampleRate <= 0) { ClearResponse(); return; }

        float[][] working = channels;

        // A response recorded at another rate is a response of the same room at the wrong speed.
        if (sampleRate != SampleRate)
            working = Resampler.Resample(working, sampleRate, SampleRate);

        var maximum = (int)(MaximumSeconds * SampleRate);
        if (working.Length > 0 && working[0].Length > maximum)
        {
            var trimmed = new float[working.Length][];
            for (int c = 0; c < working.Length; c++) trimmed[c] = working[c][..maximum];
            working = trimmed;
        }

        // Unit power, measured across the whole response rather than per channel, so a response that
        // is louder on one side stays louder on that side.
        double energy = 0;
        foreach (float[] channel in working)
            foreach (float sample in channel)
                energy += (double)sample * sample;

        if (!(energy > 0)) { ClearResponse(); return; }

        var scale = (float)(1.0 / Math.Sqrt(energy / working.Length));
        var normalised = new float[working.Length][];
        for (int c = 0; c < working.Length; c++)
        {
            normalised[c] = new float[working[c].Length];
            for (int i = 0; i < working[c].Length; i++) normalised[c][i] = working[c][i] * scale;
        }

        _response = normalised;
        _responseSeconds = normalised[0].Length / (double)SampleRate;
        BuildConvolvers();
    }

    private void BuildConvolvers()
    {
        if (_response.Length == 0 || ChannelCount <= 0 || SampleRate <= 0)
        {
            Volatile.Write(ref _convolvers, []);
            return;
        }

        var built = new PartitionedConvolver?[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            // A mono response serves every channel; a stereo one is taken channel for channel, and
            // a response with more channels than the programme has is truncated to it.
            float[] kernel = _response[Math.Min(c, _response.Length - 1)];
            built[c] = kernel.Length > 0 ? new PartitionedConvolver(kernel, 1, Block) : null;
        }
        Volatile.Write(ref _convolvers, built);
    }

    // ── state ────────────────────────────────────────────────────

    public string? SaveStateText() => _responsePath;

    public void RestoreStateText(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return;

        // A preset naming a response this machine does not have loads without one rather than
        // failing: the rest of the chain is still worth having, and the card says what is missing.
        if (!File.Exists(state)) { _responsePath = state; _responseName = Path.GetFileNameWithoutExtension(state); return; }
        LoadResponse(state, out _);
    }

    // ── processing ───────────────────────────────────────────────

    protected override void OnConfigure()
    {
        _incoming = new float[ChannelCount][];
        _wet = new float[ChannelCount][];
        _dry = new float[ChannelCount][];
        _preDelay = new float[ChannelCount][];
        _dampState = new float[ChannelCount];

        var preDelaySamples = (int)(MaximumPreDelayMs * 0.001 * SampleRate) + 1;
        for (int c = 0; c < ChannelCount; c++)
        {
            _incoming[c] = new float[Block];
            _wet[c] = new float[Block];
            _dry[c] = new float[Block];
            _preDelay[c] = new float[preDelaySamples];
        }
        _cursor = 0;
        _preDelayCursor = 0;

        // The response was resampled to whatever the rate was when it was loaded, so a change of
        // rate means loading it again rather than reusing it at the wrong speed.
        if (_responsePath is { } path && File.Exists(path)) LoadResponse(path, out _);
        else BuildConvolvers();
    }

    public override void ResetState()
    {
        foreach (PartitionedConvolver? convolver in Volatile.Read(ref _convolvers)) convolver?.Reset();
        foreach (float[] channel in _incoming) Array.Clear(channel);
        foreach (float[] channel in _wet) Array.Clear(channel);
        foreach (float[] channel in _dry) Array.Clear(channel);
        foreach (float[] channel in _preDelay) Array.Clear(channel);
        Array.Clear(_dampState);
        _cursor = 0;
        _preDelayCursor = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        PartitionedConvolver?[] convolvers = Volatile.Read(ref _convolvers);
        if (convolvers.Length != ChannelCount || _incoming.Length != ChannelCount) return;

        var mix = (float)GetParam("mix");
        var trim = (float)Math.Pow(10, GetParam("trim") / 20.0);
        int preDelay = Math.Clamp(
            (int)(GetParam("predelay") * 0.001 * SampleRate), 0, _preDelay[0].Length - 1);

        // One-pole lowpass on the wet path. At the control's own maximum it is a wire — tested
        // against a direct convolution, which is the only way to notice that it was not.
        double corner = GetParam("damp");
        float damp = corner >= DampingOffHz
            ? 1
            : (float)(1 - Math.Exp(-2 * Math.PI * Math.Clamp(corner, 20, SampleRate * 0.49) / SampleRate));

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                int index = offset + f * ChannelCount + c;
                float incoming = buffer[index];

                // The wet block leaves through the pre-delay line; the dry block travels beside it,
                // delayed by exactly the same block, so the two still describe the same moment.
                float[] line = _preDelay[c];
                line[_preDelayCursor] = _wet[c][_cursor];
                int read = _preDelayCursor - preDelay;
                if (read < 0) read += line.Length;

                float wet = line[read];
                _dampState[c] += damp * (wet - _dampState[c]);
                wet = _dampState[c];

                buffer[index] = _dry[c][_cursor] * (1 - mix) + wet * trim * mix;
                _incoming[c][_cursor] = incoming;
            }

            _preDelayCursor++;
            if (_preDelayCursor >= _preDelay[0].Length) _preDelayCursor = 0;

            if (++_cursor < Block) continue;

            // The block is full. Keep a copy for the dry path before the convolution overwrites it.
            for (int c = 0; c < ChannelCount; c++)
            {
                _incoming[c].CopyTo(_dry[c].AsSpan());
                _incoming[c].CopyTo(_wet[c].AsSpan());
                convolvers[c]?.ProcessBlock(0, _wet[c]);
            }
            _cursor = 0;
        }
    }
}
