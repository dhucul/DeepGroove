using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Removes sibilance by attenuating the bins that carry it, not the band that contains it.
/// </summary>
/// <remarks>
/// <para>
/// The usual de-esser is a compressor on a high band: when it detects an "s" it ducks everything
/// above 5 kHz, cymbals and air included, for as long as the "s" lasts. What is heard is not a
/// quieter "s" so much as the top of the record moving.
/// </para>
/// <para>
/// Working in the frequency domain lets the attenuation land only where the sibilance actually is.
/// A frame is analysed, the bins that look sibilant are turned down individually, and the rest of
/// the band is passed through untouched — so a cymbal ringing through a vocal "s" keeps its own
/// bins even while the "s" loses its.
/// </para>
/// <para>
/// Sibilance is recognised by two things together, because neither alone is sufficient: the share of
/// the frame's energy sitting in the sibilant range, and the <b>zero-crossing rate</b>, which is high
/// for noise-like sounds and low for pitched ones. A cymbal has the energy but so does a bright
/// vowel; the crossing rate is what separates the hiss of an "s" from the harmonics of an "ee".
/// </para>
/// </remarks>
public sealed class DeEsserEffect : EffectBase
{
    private const int FftSize = 1024;
    private const int Hop = FftSize / 4;

    private static readonly EffectParam[] P =
    [
        new("freq", "FROM", 2_000, 12_000, 5_000, EffectParam.Hz),
        new("threshold", "THRESHOLD", -60, 0, -30, EffectParam.Db),
        new("range", "RANGE", 0, 24, 10, EffectParam.Db),
        new("sharpness", "SHARPNESS", 0, 1, 0.5, EffectParam.Pct),
    ];

    private float[][] _inputRing = [];
    private float[][] _ola = [];
    private float[][] _frame = [];
    private float[][] _binRe = [];
    private float[][] _binIm = [];
    private float[] _window = [];
    private double[] _channelEnergy = [];
    private int[] _channelCrossings = [];
    private long _streamFrame;
    private long _nextFrameStart;
    private double _olaScale;
    private DeEsserParameters _parameters = new(5_000, 0.03, 0.3, 0.5);
    private double _reduction;

    private sealed record DeEsserParameters(double FromHz, double Threshold, double Floor, double Sharpness);

    public override string TypeId => "deesser";
    public override string DisplayName => "De-Esser";
    public override IReadOnlyList<EffectParam> Params => P;

    public override int LatencySamples => FftSize;

    public override string Readout
    {
        get
        {
            double reduction = Volatile.Read(ref _reduction);
            return reduction < 0.05 ? "—" : $"{reduction:0.0} dB";
        }
    }

    protected override void OnConfigure()
    {
        int bins = FftSize / 2 + 1;
        _inputRing = new float[ChannelCount][];
        _ola = new float[ChannelCount][];
        _frame = new float[ChannelCount][];
        _binRe = new float[ChannelCount][];
        _binIm = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _inputRing[c] = new float[FftSize];
            _ola[c] = new float[FftSize * 2];
            _frame[c] = new float[FftSize];
            _binRe[c] = new float[bins];
            _binIm[c] = new float[bins];
        }
        _window = WindowFunctions.Sqrt(WindowFunctions.Hann(FftSize, periodic: true));
        double[] overlap = WindowFunctions.OverlapSum(_window, _window, Hop);
        _olaScale = 1.0 / overlap.Average();
        _channelEnergy = new double[ChannelCount];
        _channelCrossings = new int[ChannelCount];
        OnParamsChanged();
    }

    protected override void OnParamsChanged() =>
        Volatile.Write(ref _parameters, new DeEsserParameters(
            Math.Min(GetParam("freq"), SampleRate * 0.45),
            Math.Pow(10, GetParam("threshold") / 20.0),
            Math.Pow(10, -GetParam("range") / 20.0),
            GetParam("sharpness")));

    public override void ResetState()
    {
        foreach (float[] channel in _inputRing) Array.Clear(channel);
        foreach (float[] channel in _ola) Array.Clear(channel);
        foreach (float[] channel in _frame) Array.Clear(channel);
        foreach (float[] channel in _binRe) Array.Clear(channel);
        foreach (float[] channel in _binIm) Array.Clear(channel);
        Array.Clear(_channelEnergy);
        Array.Clear(_channelCrossings);
        _streamFrame = 0;
        _nextFrameStart = -(FftSize - Hop);
        Volatile.Write(ref _reduction, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var parameters = Volatile.Read(ref _parameters);
        if (_inputRing.Length != ChannelCount || _window.Length != FftSize) return;

        int frames = count / ChannelCount;
        if (frames == 0) return;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            int inputSlot = (int)(_streamFrame % FftSize);
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                float sample = buffer[index + channel];
                _inputRing[channel][inputSlot] = float.IsFinite(sample) ? sample : 0f;
            }

            if (_streamFrame == _nextFrameStart + FftSize - 1)
            {
                ProcessSpectralFrame(_nextFrameStart, parameters);
                _nextFrameStart += Hop;
            }

            long outputFrame = _streamFrame - LatencySamples;
            if (outputFrame < 0)
            {
                for (int channel = 0; channel < ChannelCount; channel++)
                    buffer[index + channel] = 0f;
            }
            else
            {
                int outputSlot = (int)(outputFrame % (FftSize * 2));
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    buffer[index + channel] = _ola[channel][outputSlot];
                    _ola[channel][outputSlot] = 0f;
                }
            }

            _streamFrame++;
        }
    }

    private void ProcessSpectralFrame(long start, DeEsserParameters parameters)
    {
        int bins = FftSize / 2 + 1;
        Array.Clear(_channelEnergy);
        Array.Clear(_channelCrossings);

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            float previous = 0;
            bool havePrevious = false;
            for (int i = 0; i < FftSize; i++)
            {
                long absolute = start + i;
                float sample = absolute < 0
                    ? 0f
                    : _inputRing[channel][(int)(absolute % FftSize)];
                _frame[channel][i] = sample * _window[i];
                _channelEnergy[channel] += (double)sample * sample;
                if (havePrevious && (sample >= 0) != (previous >= 0))
                    _channelCrossings[channel]++;
                previous = sample;
                havePrevious = true;
            }
            Fft.RealForward(_frame[channel], _binRe[channel], _binIm[channel]);
        }

        int fromBin = Math.Clamp(
            (int)(parameters.FromHz * FftSize / SampleRate), 1, bins - 1);
        double total = 0, sibilant = 0;
        for (int bin = 1; bin < bins; bin++)
        {
            double power = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                double re = _binRe[channel][bin], im = _binIm[channel][bin];
                power += re * re + im * im;
            }
            power /= ChannelCount;
            total += power;
            if (bin >= fromBin) sibilant += power;
        }

        double gain = 1;
        if (total > 1e-20)
        {
            int guideChannel = 0;
            for (int channel = 1; channel < ChannelCount; channel++)
            {
                if (_channelEnergy[channel] > _channelEnergy[guideChannel])
                    guideChannel = channel;
            }

            double crossings = _channelCrossings[guideChannel] / (double)FftSize;
            double sibilanceScore = sibilant / total * crossings;
            double level = Math.Sqrt(sibilant / bins);
            if (level >= parameters.Threshold && sibilanceScore >= 0.08)
            {
                double strength = Math.Clamp((sibilanceScore - 0.08) / 0.25, 0, 1) *
                                  Math.Clamp(level / Math.Max(parameters.Threshold, 1e-9) - 1, 0, 1);
                gain = 1 - (1 - parameters.Floor) * strength;
            }
        }

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            if (gain < 1)
            {
                for (int bin = fromBin; bin < bins; bin++)
                {
                    double into = (bin - fromBin) / (double)Math.Max(1, bins - fromBin);
                    double taper = parameters.Sharpness +
                                   (1 - parameters.Sharpness) * Math.Min(1, into * 4);
                    float applied = (float)(1 - (1 - gain) * taper);
                    _binRe[channel][bin] *= applied;
                    _binIm[channel][bin] *= applied;
                }
            }

            Fft.RealInverse(_binRe[channel], _binIm[channel], _frame[channel]);
            for (int i = 0; i < FftSize; i++)
            {
                long absolute = start + i;
                if (absolute < 0) continue;
                int slot = (int)(absolute % (FftSize * 2));
                _ola[channel][slot] +=
                    (float)(_frame[channel][i] * _window[i] * _olaScale);
            }
        }

        Volatile.Write(ref _reduction,
            gain < 1 ? -20 * Math.Log10(Math.Max(gain, 1e-9)) : 0);
    }
}

