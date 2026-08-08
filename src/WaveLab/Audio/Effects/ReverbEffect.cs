namespace WaveLab.Audio.Effects;

/// <summary>Freeverb-style reverb: 8 parallel damped combs + 4 series allpasses per channel, stereo spread.</summary>
public sealed class ReverbEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("size", "SIZE", 0, 1, 0.5, EffectParam.Plain),
        new("damp", "DAMP", 0, 1, 0.4, EffectParam.Plain),
        new("width", "WIDTH", 0, 1, 1, EffectParam.Plain),
        new("mix", "MIX", 0, 1, 0.25, EffectParam.Pct),
    ];

    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;

    private sealed class Comb
    {
        public float[] Buf = [];
        public int Pos;
        public float FilterStore;
        public float Feedback, Damp1, Damp2;
        public float Process(float input)
        {
            float output = Buf[Pos];
            FilterStore = output * Damp2 + FilterStore * Damp1;
            Buf[Pos] = input + FilterStore * Feedback;
            if (++Pos >= Buf.Length) Pos = 0;
            return output;
        }
    }

    private sealed class Allpass
    {
        public float[] Buf = [];
        public int Pos;
        public float Process(float input)
        {
            float bufout = Buf[Pos];
            float output = -input + bufout;
            Buf[Pos] = input + bufout * 0.5f;
            if (++Pos >= Buf.Length) Pos = 0;
            return output;
        }
    }

    private Comb[][] _combs = [];
    private Allpass[][] _allpasses = [];

    public override string TypeId => "reverb";
    public override string DisplayName => "Reverb";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        double scale = SampleRate / 44100.0;
        int chans = Math.Min(2, ChannelCount);
        _combs = new Comb[chans][];
        _allpasses = new Allpass[chans][];
        for (int c = 0; c < chans; c++)
        {
            int spread = c * StereoSpread;
            _combs[c] = CombTunings.Select(t => new Comb { Buf = new float[Math.Max(4, (int)((t + spread) * scale))] }).ToArray();
            _allpasses[c] = AllpassTunings.Select(t => new Allpass { Buf = new float[Math.Max(2, (int)((t + spread) * scale))] }).ToArray();
        }
    }

    protected override void OnParamsChanged()
    {
        float feedback = (float)(0.7 + GetParam("size") * 0.28);
        float damp = (float)(GetParam("damp") * 0.9);
        foreach (var chain in _combs)
            foreach (var comb in chain)
            {
                comb.Feedback = feedback;
                comb.Damp1 = damp;
                comb.Damp2 = 1 - damp;
            }
    }

    public override void ResetState()
    {
        foreach (var chain in _combs)
            foreach (var comb in chain) { Array.Clear(comb.Buf); comb.FilterStore = 0; comb.Pos = 0; }
        foreach (var chain in _allpasses)
            foreach (var ap in chain) { Array.Clear(ap.Buf); ap.Pos = 0; }
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_combs.Length == 0) return;
        float mix = (float)GetParam("mix");
        float width = (float)GetParam("width");
        float dry = 1 - mix;
        const float fixedGain = 0.015f;

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            // mono input drive
            float input = 0;
            for (int c = 0; c < ChannelCount; c++) input += buffer[idx + c];
            input = input / ChannelCount * fixedGain;

            float outL = 0, outR = 0;
            foreach (var comb in _combs[0]) outL += comb.Process(input);
            foreach (var ap in _allpasses[0]) outL = ap.Process(outL);
            if (_combs.Length > 1)
            {
                foreach (var comb in _combs[1]) outR += comb.Process(input);
                foreach (var ap in _allpasses[1]) outR = ap.Process(outR);
            }
            else outR = outL;

            float wet1 = width / 2 + 0.5f;
            float wet2 = (1 - width) / 2;
            float wetL = outL * wet1 + outR * wet2;
            float wetR = outR * wet1 + outL * wet2;

            if (ChannelCount == 1)
                buffer[idx] = buffer[idx] * dry + wetL * mix;
            else
            {
                buffer[idx] = buffer[idx] * dry + wetL * mix;
                buffer[idx + 1] = buffer[idx + 1] * dry + wetR * mix;
                for (int c = 2; c < ChannelCount; c++) buffer[idx + c] *= dry;
            }
        }
    }
}
