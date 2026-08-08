namespace WaveLab.Audio.Effects;

/// <summary>Feed-forward compressor: peak detection, soft knee (6 dB), attack/release smoothing, makeup gain.</summary>
public sealed class CompressorEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("thresh", "THRESH", -60, 0, -18, EffectParam.Db),
        new("ratio", "RATIO", 1, 20, 3, EffectParam.Ratio),
        new("attack", "ATTACK", 0.1, 100, 12, v => $"{v:0.0} ms"),
        new("release", "RELEASE", 10, 1000, 140, EffectParam.Ms),
        new("makeup", "MAKEUP", 0, 24, 0, EffectParam.Db),
    ];

    private double _envDb = -90;
    private double _grDb;

    public override string TypeId => "compressor";
    public override string DisplayName => "Compressor";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"GR −{_grDb:0.0} dB";

    public override void ResetState() { _envDb = -90; _grDb = 0; }

    public override void Process(float[] buffer, int offset, int count)
    {
        double thresh = GetParam("thresh");
        double ratio = GetParam("ratio");
        double attCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("attack") / 1000.0));
        double relCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("release") / 1000.0));
        double makeup = Math.Pow(10, GetParam("makeup") / 20.0);
        const double knee = 6.0;

        int frames = count / ChannelCount;
        double maxGr = 0;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            float peak = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                float a = Math.Abs(buffer[idx + c]);
                if (a > peak) peak = a;
            }
            double inDb = 20 * Math.Log10(Math.Max(1e-6, peak));

            // envelope in dB domain
            _envDb = inDb > _envDb
                ? attCoeff * _envDb + (1 - attCoeff) * inDb
                : relCoeff * _envDb + (1 - relCoeff) * inDb;

            // soft-knee gain computer
            double over = _envDb - thresh;
            double grDb;
            if (over <= -knee / 2) grDb = 0;
            else if (over >= knee / 2) grDb = over * (1 - 1 / ratio);
            else
            {
                double x = over + knee / 2;
                grDb = x * x / (2 * knee) * (1 - 1 / ratio);
            }
            if (grDb > maxGr) maxGr = grDb;

            float gain = (float)(Math.Pow(10, -grDb / 20.0) * makeup);
            for (int c = 0; c < ChannelCount; c++)
                buffer[idx + c] *= gain;
        }
        _grDb = maxGr;
    }
}
