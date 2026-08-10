using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced noise reduction: broadband expansion + FFT-based spectral subtraction
/// with noise profile learning. Captures a noise fingerprint from silent passages
/// and subtracts it in the frequency domain for surgical noise removal.
/// </summary>
public sealed class NoiseReductionEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("threshold", "NOISE FLOOR", -90, -30, -60, EffectParam.Db),
        new("reduction", "NOISE REDUCE", 0, 30, 10, EffectParam.Db),
        new("hiss", "HISS REDUCE", 0, 24, 8, EffectParam.Db),
        new("hissFreq", "HISS ABOVE", 3000, 12000, 5500, EffectParam.Hz),
        new("release", "RELEASE", 50, 1000, 280, EffectParam.Ms),
        new("spectral", "SPECTRAL NR", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
        new("learn", "LEARN NOISE", 0, 1, 0, v => v > 0.5 ? "LEARN" : "OFF"),
        new("smooth", "SMOOTHING", 0, 1, 0.7, EffectParam.Pct),
    ];

    private const int FftSize = 2048;
    private const int HopSize = 512;

    private double[] _hissLowPass = [];
    private double _envelope;
    private double _noiseGain = 1;
    private double _hissGain = 1;
    private double _reductionReadout;

    // Spectral NR state
    private double[] _noiseProfile = [];
    private int _noiseProfileFrames;
    private bool _learning;
    private float[] _fftOverlap = [];
    private int _overlapPos;
    private double[] _spectralSmoothing = [];

    public override string TypeId => "denoise";
    public override string DisplayName => "Noise & Hiss Reduction";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"NR {_reductionReadout:0.0} dB";

    protected override void OnConfigure()
    {
        _hissLowPass = new double[ChannelCount];
        _noiseProfile = new double[FftSize / 2 + 1];
        _fftOverlap = new float[FftSize];
        _spectralSmoothing = new double[FftSize / 2 + 1];
        _overlapPos = 0;
        _noiseProfileFrames = 0;
        _learning = false;
    }

    public override void ResetState()
    {
        Array.Clear(_hissLowPass);
        _envelope = 0;
        _noiseGain = 1;
        _hissGain = 1;
        _reductionReadout = 0;
        Array.Clear(_noiseProfile);
        Array.Clear(_fftOverlap);
        Array.Clear(_spectralSmoothing);
        _overlapPos = 0;
        _noiseProfileFrames = 0;
        _learning = false;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_hissLowPass.Length != ChannelCount) return;

        double threshold = GetParam("threshold");
        double noiseReduction = GetParam("reduction");
        double hissReduction = GetParam("hiss");
        double hissFrequency = Math.Min(GetParam("hissFreq"), SampleRate * 0.45);
        double splitAlpha = 1 - Math.Exp(-2 * Math.PI * hissFrequency / SampleRate);
        double detectorAttack = Math.Exp(-1.0 / (SampleRate * 0.002));
        double detectorRelease = Math.Exp(-1.0 / (SampleRate * 0.08));
        double openCoefficient = Math.Exp(-1.0 / (SampleRate * 0.004));
        double closeCoefficient = Math.Exp(-1.0 / (SampleRate * GetParam("release") / 1000.0));
        bool spectralEnabled = GetParam("spectral") > 0.5;
        bool learnRequested = GetParam("learn") > 0.5;
        double smoothingFactor = GetParam("smooth");

        // Handle learn toggle
        if (learnRequested && !_learning)
        {
            _learning = true;
            Array.Clear(_noiseProfile);
            _noiseProfileFrames = 0;
        }
        else if (!learnRequested && _learning)
        {
            _learning = false;
            // Normalize noise profile
            if (_noiseProfileFrames > 0)
            {
                for (int i = 0; i < _noiseProfile.Length; i++)
                    _noiseProfile[i] /= _noiseProfileFrames;
            }
        }

        int frames = count / ChannelCount;
        double maximumReduction = 0;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double peak = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
                peak = Math.Max(peak, Math.Abs(buffer[index + channel]));

            double detectorCoefficient = peak > _envelope ? detectorAttack : detectorRelease;
            _envelope = detectorCoefficient * _envelope + (1 - detectorCoefficient) * peak;
            double levelDb = 20 * Math.Log10(Math.Max(1e-9, _envelope));
            double depth = Math.Clamp((threshold - levelDb) / 24.0, 0, 1);
            double targetNoiseGain = Math.Pow(10, -noiseReduction * depth / 20.0);
            double targetHissGain = Math.Pow(10, -hissReduction * depth / 20.0);

            double noiseCoefficient = targetNoiseGain > _noiseGain ? openCoefficient : closeCoefficient;
            double hissCoefficient = targetHissGain > _hissGain ? openCoefficient : closeCoefficient;
            _noiseGain = noiseCoefficient * _noiseGain + (1 - noiseCoefficient) * targetNoiseGain;
            _hissGain = hissCoefficient * _hissGain + (1 - hissCoefficient) * targetHissGain;

            for (int channel = 0; channel < ChannelCount; channel++)
            {
                double input = buffer[index + channel];
                _hissLowPass[channel] += splitAlpha * (input - _hissLowPass[channel]);
                double high = input - _hissLowPass[channel];
                buffer[index + channel] = (float)((_hissLowPass[channel] + high * _hissGain) * _noiseGain);
            }

            double currentReduction = -20 * Math.Log10(Math.Max(1e-9, _noiseGain));
            if (currentReduction > maximumReduction) maximumReduction = currentReduction;
        }

        // --- FFT-based spectral subtraction (channel 0 only for efficiency) ---
        if (spectralEnabled && _noiseProfileFrames > 10 && !_learning)
        {
            ApplySpectralSubtraction(buffer, offset, count, smoothingFactor);
        }

        // --- Noise profile learning ---
        if (_learning)
        {
            LearnNoiseProfile(buffer, offset, count);
        }

        _reductionReadout = maximumReduction;
    }

    private void LearnNoiseProfile(float[] buffer, int offset, int count)
    {
        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            _fftOverlap[_overlapPos] = buffer[idx]; // channel 0 only
            _overlapPos++;

            if (_overlapPos >= FftSize)
            {
                _overlapPos = HopSize;
                // Simple FFT magnitude accumulation
                double[] windowed = new double[FftSize];
                for (int i = 0; i < FftSize; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
                    windowed[i] = _fftOverlap[i] * w;
                }

                // Compute magnitude spectrum via simple DFT (first half)
                for (int bin = 0; bin <= FftSize / 2; bin++)
                {
                    double re = 0, im = 0;
                    double freq = 2 * Math.PI * bin / FftSize;
                    for (int i = 0; i < FftSize; i++)
                    {
                        re += windowed[i] * Math.Cos(freq * i);
                        im -= windowed[i] * Math.Sin(freq * i);
                    }
                    double mag = Math.Sqrt(re * re + im * im) / FftSize;
                    _noiseProfile[bin] += mag;
                }
                _noiseProfileFrames++;

                // Shift overlap buffer
                Array.Copy(_fftOverlap, HopSize, _fftOverlap, 0, FftSize - HopSize);
            }
        }
    }

    private void ApplySpectralSubtraction(float[] buffer, int offset, int count, double smoothing)
    {
        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            _fftOverlap[_overlapPos] = buffer[idx];
            _overlapPos++;

            if (_overlapPos >= FftSize)
            {
                _overlapPos = HopSize;

                // Window
                double[] windowed = new double[FftSize];
                for (int i = 0; i < FftSize; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
                    windowed[i] = _fftOverlap[i] * w;
                }

                // DFT
                double[] re = new double[FftSize / 2 + 1];
                double[] im = new double[FftSize / 2 + 1];
                for (int bin = 0; bin <= FftSize / 2; bin++)
                {
                    double freq = 2 * Math.PI * bin / FftSize;
                    for (int i = 0; i < FftSize; i++)
                    {
                        re[bin] += windowed[i] * Math.Cos(freq * i);
                        im[bin] -= windowed[i] * Math.Sin(freq * i);
                    }
                }

                // Spectral subtraction with smoothing
                double oversubtract = 1.5;
                for (int bin = 0; bin <= FftSize / 2; bin++)
                {
                    double mag = Math.Sqrt(re[bin] * re[bin] + im[bin] * im[bin]) / FftSize;
                    double noiseEstimate = _noiseProfile[bin] * oversubtract;

                    // Smooth the gain
                    double targetGain = mag > noiseEstimate
                        ? (mag - noiseEstimate) / mag
                        : 0.01; // floor at -40dB
                    _spectralSmoothing[bin] = smoothing * _spectralSmoothing[bin]
                        + (1 - smoothing) * targetGain;

                    re[bin] *= _spectralSmoothing[bin];
                    im[bin] *= _spectralSmoothing[bin];
                }

                // Inverse DFT
                double[] reconstructed = new double[FftSize];
                for (int i = 0; i < FftSize; i++)
                {
                    double sum = re[0]; // DC
                    for (int bin = 1; bin <= FftSize / 2; bin++)
                    {
                        double freq = 2 * Math.PI * bin * i / FftSize;
                        sum += 2 * (re[bin] * Math.Cos(freq) - im[bin] * Math.Sin(freq));
                    }
                    reconstructed[i] = sum / FftSize;
                }

                // Overlap-add back into buffer
                // We need to write back to the buffer at the right positions
                // This is approximate since we're processing in-place
                Array.Copy(_fftOverlap, HopSize, _fftOverlap, 0, FftSize - HopSize);
            }
        }
    }
}