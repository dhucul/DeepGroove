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
        public float Process(float input, float feedback, float damp)
        {
            float output = Buf[Pos];
            FilterStore = output * (1 - damp) + FilterStore * damp;
            Buf[Pos] = input + FilterStore * feedback;
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

    private ReverbState _state = new([], [], 0);
    private ReverbParameters _parameters = new(0.84f, 0.36f, 1f, 0.25f);

    private sealed record ReverbState(Comb[][] Combs, Allpass[][] Allpasses, int Channels);
    private sealed record ReverbParameters(float Feedback, float Damp, float Width, float Mix);

    public override string TypeId => "reverb";
    public override string DisplayName => "Reverb";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        double scale = SampleRate / 44100.0;
        int chans = ChannelCount;
        var combs = new Comb[chans][];
        var allpasses = new Allpass[chans][];
        for (int c = 0; c < chans; c++)
        {
            int spread = c * StereoSpread;
            combs[c] = CombTunings.Select(t => new Comb { Buf = new float[Math.Max(4, (int)((t + spread) * scale))] }).ToArray();
            allpasses[c] = AllpassTunings.Select(t => new Allpass { Buf = new float[Math.Max(2, (int)((t + spread) * scale))] }).ToArray();
        }
        Volatile.Write(ref _state, new ReverbState(combs, allpasses, chans));
    }

    protected override void OnParamsChanged()
    {
        var updated = new ReverbParameters(
            (float)(0.7 + GetParam("size") * 0.28),
            (float)(GetParam("damp") * 0.9),
            (float)GetParam("width"),
            (float)GetParam("mix"));
        Volatile.Write(ref _parameters, updated);
    }

    public override void ResetState()
    {
        var state = Volatile.Read(ref _state);
        foreach (var chain in state.Combs)
            foreach (var comb in chain) { Array.Clear(comb.Buf); comb.FilterStore = 0; comb.Pos = 0; }
        foreach (var chain in state.Allpasses)
            foreach (var ap in chain) { Array.Clear(ap.Buf); ap.Pos = 0; }
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var state = Volatile.Read(ref _state);
        int channels = state.Channels;
        if (channels == 0) return;
        var parameters = Volatile.Read(ref _parameters);
        float mix = parameters.Mix;
        float width = parameters.Width;
        float dry = 1 - mix;
        const float fixedGain = 0.015f;

        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * channels;
            // mono input drive
            float input = 0;
            for (int c = 0; c < channels; c++) input += buffer[idx + c];
            input = input / channels * fixedGain;

            float outL = 0, outR = 0;
            foreach (var comb in state.Combs[0]) outL += comb.Process(input, parameters.Feedback, parameters.Damp);
            foreach (var ap in state.Allpasses[0]) outL = ap.Process(outL);
            if (channels > 1)
            {
                foreach (var comb in state.Combs[1]) outR += comb.Process(input, parameters.Feedback, parameters.Damp);
                foreach (var ap in state.Allpasses[1]) outR = ap.Process(outR);
            }
            else outR = outL;

            float wet1 = width / 2 + 0.5f;
            float wet2 = (1 - width) / 2;
            float wetL = outL * wet1 + outR * wet2;
            float wetR = outR * wet1 + outL * wet2;

            if (channels == 1)
                buffer[idx] = buffer[idx] * dry + wetL * mix;
            else
            {
                buffer[idx] = buffer[idx] * dry + wetL * mix;
                buffer[idx + 1] = buffer[idx + 1] * dry + wetR * mix;
                for (int c = 2; c < channels; c++)
                {
                    float wet = 0;
                    foreach (var comb in state.Combs[c])
                        wet += comb.Process(input, parameters.Feedback, parameters.Damp);
                    foreach (var ap in state.Allpasses[c]) wet = ap.Process(wet);
                    buffer[idx + c] = buffer[idx + c] * dry + wet * mix;
                }
            }
        }
    }
}
