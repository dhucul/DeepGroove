using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced noise reduction: broadband expansion + FFT spectral subtraction with a
/// learned noise profile. The spectral path uses a radix-2 FFT with 75% overlap
/// WOLA reconstruction (Hann analysis + synthesis), per channel, and reports its
/// pipeline latency for offline render compensation.
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
    private const int HopSize = 512;              // 75% overlap
    private const int OlaLength = FftSize * 2;    // OLA ring capacity per channel
    private const double Oversubtract = 1.5;

    private double[] _hissLowPass = [];
    private double _envelope;
    private double _noiseGain = 1;
    private double _hissGain = 1;
    private double _reductionReadout;

    // Learned noise profile (channel 0)
    private double[] _noiseProfile = [];
    private int _noiseProfileFrames;
    private bool _learning;
    private float[] _learnBuf = [];
    private int _learnPos;

    // Spectral subtraction state (per channel, WOLA streaming)
    private float[] _window = [];
    private double _windowSum;
    private double _olaNorm;
    private float[][] _specIn = [];      // input rings (FftSize per channel)
    private float[][] _specOla = [];     // OLA accumulators (OlaLength per channel)
    private double[][] _specSmooth = []; // per-channel per-bin gain smoothing
    private int _specInPos;              // shared input ring position
    private int _specInFilled;           // valid samples in the rings (≤ FftSize)
    private int _specSinceFrame;         // input samples since the last processed frame
    private int _specOlaRead;            // shared OLA read position
    private int _specOlaWrite;           // shared OLA frame-start position
    private int _specOutAvail;           // OLA samples ready to read
    private int _spectralActive;         // bool as int (Volatile): drives LatencySamples
    private float[] _fftRe = [];
    private float[] _fftIm = [];

    public override string TypeId => "denoise";
    public override string DisplayName => "Noise & Hiss Reduction";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"NR {_reductionReadout:0.0} dB";

    // The spectral path delays the signal by one analysis frame; report it only
    // while subtraction is actually running so offline renders stay aligned.
    public override int LatencySamples => Volatile.Read(ref _spectralActive) != 0 ? FftSize : 0;

    protected override void OnConfigure()
    {
        _hissLowPass = new double[ChannelCount];
        _noiseProfile = new double[FftSize / 2 + 1];
        _learnBuf = new float[FftSize];
        _window = Fft.HannWindow(FftSize);
        _windowSum = 0;
        foreach (float w in _window) _windowSum += w;

        // COLA normalization for Hann² at 75% overlap (constant for any phase)
        _olaNorm = 0;
        for (int k = 0; k < FftSize / HopSize; k++)
        {
            double w = _window[k * HopSize + HopSize / 2];
            _olaNorm += w * w;
        }

        _specIn = new float[ChannelCount][];
        _specOla = new float[ChannelCount][];
        _specSmooth = new double[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _specIn[c] = new float[FftSize];
            _specOla[c] = new float[OlaLength];
            _specSmooth[c] = new double[FftSize / 2 + 1];
        }
        _fftRe = new float[FftSize];
        _fftIm = new float[FftSize];
        ResetSpectralState();
    }

    private void ResetSpectralState()
    {
        Array.Clear(_noiseProfile);
        Array.Clear(_learnBuf);
        _learnPos = 0;
        foreach (var ring in _specIn) Array.Clear(ring);
        foreach (var ola in _specOla) Array.Clear(ola);
        foreach (var sm in _specSmooth) Array.Clear(sm);
        _specInPos = 0;
        _specInFilled = 0;
        _specSinceFrame = 0;
        _specOlaRead = 0;
        _specOlaWrite = 0;
        _specOutAvail = 0;
        _noiseProfileFrames = 0;
        _learning = false;
        Volatile.Write(ref _spectralActive, 0);
    }

    public override void ResetState()
    {
        Array.Clear(_hissLowPass);
        _envelope = 0;
        _noiseGain = 1;
        _hissGain = 1;
        _reductionReadout = 0;
        if (_specIn.Length == ChannelCount) ResetSpectralState();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_hissLowPass.Length != ChannelCount || _specIn.Length != ChannelCount) return;

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
            Array.Clear(_learnBuf);
            _learnPos = 0;
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

        bool spectralActive = spectralEnabled && !_learning && _noiseProfileFrames > 10;
        Volatile.Write(ref _spectralActive, spectralActive ? 1 : 0);

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

            // --- spectral stage (learned profile, per channel, WOLA) ---
            if (spectralActive)
            {
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    _specIn[channel][_specInPos] = buffer[index + channel];

                    float y = 0;
                    if (_specOutAvail > 0)
                    {
                        y = _specOla[channel][_specOlaRead];
                        _specOla[channel][_specOlaRead] = 0;
                    }
                    buffer[index + channel] = y;
                }

                _specInPos = (_specInPos + 1) % FftSize;
                if (_specInFilled < FftSize) _specInFilled++;
                _specSinceFrame++;
                if (_specOutAvail > 0) _specOutAvail--;
                _specOlaRead = (_specOlaRead + 1) % OlaLength;

                if (_specSinceFrame >= HopSize && _specInFilled >= FftSize)
                {
                    _specSinceFrame -= HopSize;
                    for (int channel = 0; channel < ChannelCount; channel++)
                        ProcessSpectralFrame(channel, smoothingFactor);
                    _specOlaWrite = (_specOlaWrite + HopSize) % OlaLength;
                    _specOutAvail += HopSize;
                }
            }
            else if (_learning)
            {
                // Accumulate the noise fingerprint from channel 0
                _learnBuf[_learnPos++] = buffer[index];
                if (_learnPos >= FftSize)
                {
                    AccumulateNoiseProfileFrame();
                    Array.Copy(_learnBuf, HopSize, _learnBuf, 0, FftSize - HopSize);
                    _learnPos = FftSize - HopSize;
                }
            }
        }

        _reductionReadout = maximumReduction;
    }

    /// <summary>Window + FFT one input frame and fold its magnitudes into the profile.</summary>
    private void AccumulateNoiseProfileFrame()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftRe[i] = _learnBuf[i] * _window[i];
            _fftIm[i] = 0;
        }
        Fft.Forward(_fftRe, _fftIm);

        double norm = 2.0 / _windowSum;
        for (int bin = 0; bin <= FftSize / 2; bin++)
            _noiseProfile[bin] += Math.Sqrt(_fftRe[bin] * _fftRe[bin] + _fftIm[bin] * _fftIm[bin]) * norm;
        _noiseProfileFrames++;
    }

    /// <summary>Spectral-subtract one frame of one channel and overlap-add the result.</summary>
    private void ProcessSpectralFrame(int channel, double smoothing)
    {
        float[] ring = _specIn[channel];
        for (int i = 0; i < FftSize; i++)
        {
            _fftRe[i] = ring[(_specInPos - FftSize + i + FftSize) % FftSize] * _window[i];
            _fftIm[i] = 0;
        }
        Fft.Forward(_fftRe, _fftIm);

        double norm = 2.0 / _windowSum;
        double[] smooth = _specSmooth[channel];
        int bins = FftSize / 2;
        for (int bin = 0; bin <= bins; bin++)
        {
            double mag = Math.Sqrt(_fftRe[bin] * _fftRe[bin] + _fftIm[bin] * _fftIm[bin]) * norm;
            double noiseEstimate = _noiseProfile[bin] * Oversubtract;
            double targetGain = mag > noiseEstimate
                ? (mag - noiseEstimate) / mag
                : 0.01; // −40 dB floor

            smooth[bin] = smoothing * smooth[bin] + (1 - smoothing) * targetGain;
            float g = (float)smooth[bin];
            _fftRe[bin] *= g;
            _fftIm[bin] *= g;

            // Mirror bin (conjugate symmetry keeps the output real)
            if (bin > 0 && bin < bins)
            {
                int mirror = FftSize - bin;
                _fftRe[mirror] *= g;
                _fftIm[mirror] *= g;
            }
        }

        Inverse(_fftRe, _fftIm);

        // Synthesis window + overlap-add
        float[] ola = _specOla[channel];
        float normFactor = (float)(1.0 / _olaNorm);
        for (int i = 0; i < FftSize; i++)
            ola[(_specOlaWrite + i) % OlaLength] += _fftRe[i] * _window[i] * normFactor;
    }

    /// <summary>Real-input inverse FFT via the conjugate-symmetry trick.</summary>
    private static void Inverse(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 0; i < n; i++) im[i] = -im[i];
        Fft.Forward(re, im);
        float scale = 1f / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= scale;
            im[i] = -im[i] * scale;
        }
    }
}
