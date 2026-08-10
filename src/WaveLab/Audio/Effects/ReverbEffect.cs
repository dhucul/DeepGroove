namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced FDN reverb: 8×8 feedback delay network with pre-delay, early reflections,
/// modulation, and room type presets. Replaces the Freeverb-style comb+allpass cascade.
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
    private DelayLine _preDelayLine = new();
    private double _modPhase;
    private ReverbParameters _parameters = new(0.5f, 0.4f, 1f, 0.25f, 0.02f, 0.3f, 0.5f, 0.0015f, 2);

    private sealed record ReverbParameters(
        float Size, float Damp, float Width, float Mix,
        float PreDelaySec, float ErLevel, float ModRate, float ModDepthSec, int RoomType);

    public override string TypeId => "reverb";
    public override string DisplayName => "Reverb";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        double scale = SampleRate / 44100.0;
        int chans = ChannelCount;

        _fdnLines = new DelayLine[FdnSize];
        _fdnFilters = new OnePoleLp[FdnSize];
        for (int i = 0; i < FdnSize; i++)
        {
            _fdnLines[i] = new DelayLine { Buf = new float[Math.Max(4, (int)(BaseDelaysMs[i] * scale))] };
            _fdnFilters[i] = new OnePoleLp();
        }

        _erLines = new DelayLine[FdnSize];
        for (int i = 0; i < FdnSize; i++)
            _erLines[i] = new DelayLine { Buf = new float[Math.Max(4, (int)(ErDelaysMs[i] * scale))] };

        _preDelayLine = new DelayLine { Buf = new float[Math.Max(4, (int)(SampleRate * 0.15))] };
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
        Array.Clear(_preDelayLine.Buf);
        _preDelayLine.Pos = 0;
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
        float dampCoeff = parameters.Damp;
        float modRate = parameters.ModRate;
        float modDepthSamples = parameters.ModDepthSec * SampleRate;
        int preDelaySamples = Math.Min((int)(parameters.PreDelaySec * SampleRate), _preDelayLine.Buf.Length - 1);

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

        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * channels;

            // Mono input sum
            float input = 0;
            for (int c = 0; c < channels; c++)
                input += buffer[idx + c];
            input /= channels;

            // --- Pre-delay ---
            float preDelayed = _preDelayLine.Process(input * 0.5f);
            _preDelayLine.Buf[_preDelayLine.Pos > 0 ? _preDelayLine.Pos - 1 : _preDelayLine.Buf.Length - 1]
                = input * 0.5f;
            // Re-insert at correct position
            int pdWrite = (_preDelayLine.Pos - 1 + _preDelayLine.Buf.Length) % _preDelayLine.Buf.Length;
            _preDelayLine.Buf[pdWrite] = input * 0.5f;

            // --- Early reflections ---
            float erOut = 0;
            for (int i = 0; i < FdnSize; i++)
            {
                float er = _erLines[i].Process(preDelayed * (0.3f / FdnSize));
                erOut += er * (1.0f - i * 0.08f); // decaying ER pattern
            }

            // --- FDN late reverb ---
            // Read FDN outputs
            float[] fdnOut = new float[FdnSize];
            for (int i = 0; i < FdnSize; i++)
                fdnOut[i] = _fdnFilters[i].Process(_fdnLines[i].Buf[_fdnLines[i].Pos], dampCoeff);

            // Hadamard mixing matrix (normalized)
            float[] fdnIn = new float[FdnSize];
            for (int i = 0; i < FdnSize; i++)
            {
                float sum = 0;
                for (int j = 0; j < FdnSize; j++)
                {
                    // Hadamard(8) sign pattern
                    int bit = (i & j);
                    int sign = (PopCount(bit) & 1) == 0 ? 1 : -1;
                    sum += fdnOut[j] * sign;
                }
                fdnIn[i] = sum * 0.3536f; // 1/sqrt(8)
            }

            // Modulation of delay read positions
            double modOffset = Math.Sin(_modPhase) * modDepthSamples * rtScale;
            _modPhase += modPhaseInc;
            if (_modPhase > 2 * Math.PI) _modPhase -= 2 * Math.PI;

            // Feed FDN inputs back with size scaling
            float sizeFeedback = parameters.Size * 0.95f;
            for (int i = 0; i < FdnSize; i++)
            {
                float drive = preDelayed * fdnInputGain + fdnIn[i] * sizeFeedback;
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
                wetL = wetL * wet1 + wetR * wet2 + erOut * erLevel * 0.5f;
                wetR = wetR * wet1 + wetL * wet2 + erOut * erLevel * 0.5f;

                buffer[idx] = buffer[idx] * dry + wetL * mix;
                buffer[idx + 1] = buffer[idx + 1] * dry + wetR * mix;

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