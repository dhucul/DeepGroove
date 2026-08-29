namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced FDN reverb: 8×8 feedback delay network with pre-delay, early reflections,
/// per-line modulation, and room type presets. Replaces the Freeverb-style comb+allpass cascade.
/// </summary>
public sealed class ReverbEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("size", "SIZE", 0, 1, 0.5, EffectParam.Plain),
        new("damp", "DAMP", 0, 1, 0.4, EffectParam.Plain),
        new("width", "WIDTH", 0, 1, 1, EffectParam.Plain),
        new("mix", "MIX", 0, 1, 0.25, EffectParam.Pct),
        new("preDelay", "PRE-DELAY", 0, 120, 20, EffectParam.Ms),
        new("erLevel", "ER LEVEL", 0, 1, 0.3, EffectParam.Pct),
        new("modRate", "MOD RATE", 0, 3, 0.5, v => $"{v:0.00} Hz"),
        new("modDepth", "MOD DEPTH", 0, 5, 1.5, EffectParam.Ms),
        new("roomType", "ROOM", 0, 4, 2, v => ((int)v) switch
        {
            0 => "Hall",
            1 => "Room",
            2 => "Plate",
            3 => "Chamber",
            _ => "Spring",
        }),
    ];

    // FDN: 8 delay lines with Hadamard mixing matrix
    private const int FdnSize = 8;
    private static readonly double[] BaseDelaysMs = [29.7, 37.1, 41.3, 43.7, 47.9, 53.3, 59.1, 61.9];
    private static readonly double[] ErDelaysMs = [3.2, 5.8, 8.1, 11.4, 14.7, 17.3, 21.6, 25.9];

    /// <summary>Constant 8×8 Hadamard sign matrix — built once, not per sample.</summary>
    private static readonly float[][] Hadamard = BuildHadamard();

    /// <summary>Per-line LFO phase offsets (i·π/8), so one sin/cos per frame covers all eight.</summary>
    private static readonly double[] ModOffsetSin = BuildModOffsets(sine: true);
    private static readonly double[] ModOffsetCos = BuildModOffsets(sine: false);

    private static float[][] BuildHadamard()
    {
        var matrix = new float[FdnSize][];
        for (int i = 0; i < FdnSize; i++)
        {
            matrix[i] = new float[FdnSize];
            for (int j = 0; j < FdnSize; j++)
                matrix[i][j] = (PopCount(i & j) & 1) == 0 ? 1f : -1f;
        }
        return matrix;
    }

    private static double[] BuildModOffsets(bool sine)
    {
        var offsets = new double[FdnSize];
        for (int i = 0; i < FdnSize; i++)
        {
            double angle = i * (Math.PI / FdnSize);
            offsets[i] = sine ? Math.Sin(angle) : Math.Cos(angle);
        }
        return offsets;
    }

    private sealed class DelayLine
    {
        public float[] Buf = [];
        public int Pos;
        public float Process(float input)
        {
            float output = Buf[Pos];
            Buf[Pos] = input;
            if (++Pos >= Buf.Length) Pos = 0;
            return output;
        }
    }

    private sealed class OnePoleLp
    {
        public float State;
        public float Process(float input, float coeff)
        {
            State += coeff * (input - State);
            return State;
        }
    }

    private DelayLine[] _fdnLines = [];
    private OnePoleLp[] _fdnFilters = [];
    private DelayLine[] _erLines = [];
    private DelayLine[] _preDelayLines = [];
    private float[] _preDelayed = [];
    private double _modPhase;
    private ReverbParameters _parameters = new(0.5f, 0.4f, 1f, 0.25f, 0.02f, 0.3f, 0.5f, 0.0015f, 2);

    // Scratch buffers allocated at Configure — the audio thread must not allocate per sample.
    private float[] _fdnOut = [];
    private float[] _fdnIn = [];
    private float[] _fdnExcitation = [];

    private sealed record ReverbParameters(
        float Size, float Damp, float Width, float Mix,
        float PreDelaySec, float ErLevel, float ModRate, float ModDepthSec, int RoomType);

    public override string TypeId => "reverb";
    public override string DisplayName => "Reverb";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        // Delay-line lengths are specified in milliseconds — convert to samples.
        _fdnLines = new DelayLine[FdnSize];
        _fdnFilters = new OnePoleLp[FdnSize];
        for (int i = 0; i < FdnSize; i++)
        {
            _fdnLines[i] = new DelayLine { Buf = new float[Math.Max(4, (int)(BaseDelaysMs[i] * SampleRate / 1000.0))] };
            _fdnFilters[i] = new OnePoleLp();
        }

        _erLines = new DelayLine[FdnSize];
        for (int i = 0; i < FdnSize; i++)
            _erLines[i] = new DelayLine { Buf = new float[Math.Max(4, (int)(ErDelaysMs[i] * SampleRate / 1000.0))] };


        int preDelayLength = Math.Max(4, (int)(SampleRate * 0.15));
        _preDelayLines = new DelayLine[ChannelCount];
        for (int channel = 0; channel < ChannelCount; channel++)
            _preDelayLines[channel] = new DelayLine { Buf = new float[preDelayLength] };
        _preDelayed = new float[ChannelCount];
        _fdnOut = new float[FdnSize];
        _fdnIn = new float[FdnSize];
        _fdnExcitation = new float[FdnSize];
        _modPhase = 0;
    }

    protected override void OnParamsChanged()
    {
        int roomType = (int)Math.Round(GetParam("roomType"));
        var updated = new ReverbParameters(
            (float)(0.3 + GetParam("size") * 0.65),
            (float)(GetParam("damp") * 0.95),
            (float)GetParam("width"),
            (float)GetParam("mix"),
            (float)(GetParam("preDelay") / 1000.0),
            (float)GetParam("erLevel"),
            (float)GetParam("modRate"),
            (float)(GetParam("modDepth") / 1000.0),
            roomType);
        Volatile.Write(ref _parameters, updated);
    }

    public override void ResetState()
    {
        foreach (var line in _fdnLines) { Array.Clear(line.Buf); line.Pos = 0; }
        foreach (var f in _fdnFilters) f.State = 0;
        foreach (var line in _erLines) { Array.Clear(line.Buf); line.Pos = 0; }
        foreach (DelayLine line in _preDelayLines) { Array.Clear(line.Buf); line.Pos = 0; }
        Array.Clear(_preDelayed);
        Array.Clear(_fdnOut);
        Array.Clear(_fdnIn);
        Array.Clear(_fdnExcitation);
        _modPhase = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var parameters = Volatile.Read(ref _parameters);
        if (_fdnLines.Length == 0) return;

        int channels = ChannelCount;
        float mix = parameters.Mix;
        float dry = 1 - mix;
        float width = parameters.Width;
        float erLevel = parameters.ErLevel;
        // DAMP follows convention: higher = darker (stronger absorption in the loop).
        float dampCoeff = 0.05f + (1 - parameters.Damp) * 0.95f;
        float modRate = parameters.ModRate;
        float modDepthSamples = parameters.ModDepthSec * SampleRate;
        int preDelaySamples = Math.Min(
            (int)(parameters.PreDelaySec * SampleRate), _preDelayLines[0].Buf.Length - 1);

        // Room-type scaling factors
        double rtScale = parameters.RoomType switch
        {
            0 => 1.0,    // Hall: longest
            1 => 0.55,   // Room: medium
            2 => 0.7,    // Plate: bright, medium-long
            3 => 0.8,    // Chamber: medium-long
            _ => 0.35,   // Spring: short, twangy
        };

        double modPhaseInc = 2 * Math.PI * modRate / SampleRate;
        const float fdnInputGain = 0.125f; // 1/sqrt(8) for energy conservation

        float[] fdnOut = _fdnOut;
        float[] fdnIn = _fdnIn;

        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * channels;

            // Preserve each real input channel through its own pre-delay. Selecting only
            // L/R made centre and surround channels receive a room they never excited.
            for (int channel = 0; channel < channels; channel++)
            {
                DelayLine line = _preDelayLines[channel];
                int prePosition = line.Pos;
                line.Buf[prePosition] = buffer[idx + channel] * 0.5f;
                int preRead = (prePosition - preDelaySamples + line.Buf.Length) % line.Buf.Length;
                _preDelayed[channel] = line.Buf[preRead];
                line.Pos = (prePosition + 1) % line.Buf.Length;
            }

            // Keep the established channel-to-line routing for common layouts. When
            // there are more inputs than FDN lines, fold every input into a line and
            // normalize each bucket so no channel is silently discarded.
            if (channels <= FdnSize)
            {
                for (int i = 0; i < FdnSize; i++)
                    _fdnExcitation[i] = _preDelayed[i % channels];
            }
            else
            {
                Array.Clear(_fdnExcitation);
                for (int channel = 0; channel < channels; channel++)
                    _fdnExcitation[channel % FdnSize] += _preDelayed[channel];

                for (int i = 0; i < FdnSize; i++)
                {
                    int contributors = 1 + ((channels - 1 - i) / FdnSize);
                    _fdnExcitation[i] /= MathF.Sqrt(contributors);
                }
            }

            // --- Early reflections ---
            float erOut = 0;
            for (int i = 0; i < FdnSize; i++)
            {
                float source = _fdnExcitation[i];
                float er = _erLines[i].Process(source * (0.3f / FdnSize));
                erOut += er * (1.0f - i * 0.08f); // decaying ER pattern
            }

            // --- FDN late reverb ---
            // One sin/cos per frame for all eight lines: each line's staggered phase
            // is then sin(_modPhase + i·π/8) via the angle-sum identity.
            double modSin = 0, modCos = 0;
            if (modDepthSamples > 0.01f) (modSin, modCos) = Math.SinCos(_modPhase);

            // Read FDN outputs, with per-line modulated (fractional) read positions.
            for (int i = 0; i < FdnSize; i++)
            {
                float[] lineBuf = _fdnLines[i].Buf;
                int lineLen = lineBuf.Length;
                float raw;
                if (modDepthSamples > 0.01f)
                {
                    // Stagger each line's LFO phase so the modulated delays decorrelate.
                    double mod = (modSin * ModOffsetCos[i] + modCos * ModOffsetSin[i])
                        * modDepthSamples * rtScale;
                    double readPos = _fdnLines[i].Pos + mod;
                    int i0 = (int)Math.Floor(readPos);
                    double frac = readPos - i0;
                    i0 = ((i0 % lineLen) + lineLen) % lineLen;
                    int i1 = (i0 + 1) % lineLen;
                    raw = (float)(lineBuf[i0] * (1 - frac) + lineBuf[i1] * frac);
                }
                else
                {
                    raw = lineBuf[_fdnLines[i].Pos];
                }
                fdnOut[i] = _fdnFilters[i].Process(raw, dampCoeff);
            }

            // Hadamard mixing matrix (normalized), signs precomputed at startup
            for (int i = 0; i < FdnSize; i++)
            {
                float[] signs = Hadamard[i];
                float sum = 0;
                for (int j = 0; j < FdnSize; j++)
                    sum += fdnOut[j] * signs[j];
                fdnIn[i] = sum * 0.3536f; // 1/sqrt(8)
            }

            _modPhase += modPhaseInc;
            if (_modPhase > 2 * Math.PI) _modPhase -= 2 * Math.PI;

            // Feed FDN inputs back with size scaling
            float sizeFeedback = parameters.Size * 0.95f;
            for (int i = 0; i < FdnSize; i++)
            {
                float source = _fdnExcitation[i];
                float drive = source * fdnInputGain + fdnIn[i] * sizeFeedback;
                _fdnLines[i].Buf[_fdnLines[i].Pos] = drive;
                _fdnLines[i].Pos = (_fdnLines[i].Pos + 1) % _fdnLines[i].Buf.Length;
            }

            // Sum FDN outputs for wet signal
            float fdnSum = 0;
            for (int i = 0; i < FdnSize; i++)
                fdnSum += fdnOut[i] * 0.25f;

            float wetMono = erOut * erLevel + fdnSum * (1 - erLevel * 0.5f);

            // Stereo spread
            float wet1 = width / 2 + 0.5f;
            float wet2 = (1 - width) / 2;

            if (channels == 1)
            {
                buffer[idx] = buffer[idx] * dry + wetMono * mix;
            }
            else
            {
                // Decorrelate left/right using alternating FDN taps
                float wetL = 0, wetR = 0;
                for (int i = 0; i < FdnSize; i++)
                {
                    float tap = fdnOut[i] * 0.25f;
                    if ((i & 1) == 0) wetL += tap;
                    else wetR += tap;
                }
                // Symmetric crossmix (uses the unmodified taps for both sides)
                float l = wetL * wet1 + wetR * wet2 + erOut * erLevel * 0.5f;
                float r = wetR * wet1 + wetL * wet2 + erOut * erLevel * 0.5f;

                buffer[idx] = buffer[idx] * dry + l * mix;
                buffer[idx + 1] = buffer[idx + 1] * dry + r * mix;

                for (int c = 2; c < channels; c++)
                    buffer[idx + c] = buffer[idx + c] * dry + wetMono * mix;
            }
        }
    }

    private static int PopCount(int x)
    {
        x -= (x >> 1) & 0x55555555;
        x = (x & 0x33333333) + ((x >> 2) & 0x33333333);
        x = (x + (x >> 4)) & 0x0F0F0F0F;
        return (x * 0x01010101) >> 24;
    }
}
