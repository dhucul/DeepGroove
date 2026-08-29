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

    // Learned shared noise profile. Two buffers let the audio thread learn into the spare and
    // publish the finished profile atomically without allocating or exposing a half-written frame.
    private double[] _noiseProfile = [];
    private double[] _learningProfile = [];
    private double[] _spareProfile = [];
    private int _noiseProfileFrames;
    private int _learningProfileFrames;
    private int _profileSampleRate;
    private bool _learning;
    private int _learningActive;
    private float[][] _learnBuf = [];
    private int _learnPos;
    private double[]? _pendingProfile;
    private int _pendingProfileFrames;
    private int _pendingProfileSampleRate;

    // Spectral subtraction state (per channel, WOLA streaming)
    private float[] _window = [];
    private double _windowSum;
    private double _olaNorm;
    private float[][] _specIn = [];      // input rings (FftSize per channel)
    private float[][] _specOla = [];     // OLA accumulators (OlaLength per channel)
    private double[] _specSmooth = [];   // one linked mask for every channel
    private float[][] _specRe = [];
    private float[][] _specIm = [];
    private int _specInPos;              // shared input ring position
    private int _specInFilled;           // valid samples in the rings (≤ FftSize)
    private int _specSinceFrame;         // input samples since the last processed frame
    private bool _specPrimed;            // the first frame has been processed
    private int _specOlaRead;            // shared OLA read position
    private int _specOlaWrite;           // shared OLA frame-start position
    private int _specPipelineDelay;      // countdown before reconstructed output is read
    private bool _spectralWasActive;

    private float[] _fftRe = [];
    private float[] _fftIm = [];

    public override string TypeId => "denoise";
    public override string DisplayName => "Noise & Hiss Reduction";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"NR {_reductionReadout:0.0} dB";

    // The spectral path delays the signal by one analysis frame plus one hop;
    // report it only while subtraction is actually running so renders stay aligned.
    public override int LatencySamples =>
        GetParam("spectral") > 0.5 && Volatile.Read(ref _learningActive) == 0 &&
        Volatile.Read(ref _noiseProfileFrames) > 10
            ? FftSize + HopSize
            : 0;


    protected override void OnConfigure()
    {
        double[] previousProfile = Volatile.Read(ref _noiseProfile);
        int previousFrames = Volatile.Read(ref _noiseProfileFrames);
        int previousRate = Volatile.Read(ref _profileSampleRate);

        _hissLowPass = new double[ChannelCount];
        var profileA = new double[FftSize / 2 + 1];
        var profileB = new double[FftSize / 2 + 1];
        double[]? retained = _pendingProfileSampleRate == SampleRate ? _pendingProfile
            : previousRate == SampleRate ? previousProfile
            : null;
        int retainedFrames = _pendingProfileSampleRate == SampleRate ? _pendingProfileFrames
            : previousRate == SampleRate ? previousFrames
            : 0;
        if (retained is { Length: > 0 } && retained.Length == profileA.Length)
            Array.Copy(retained, profileA, retained.Length);
        else retainedFrames = 0;
        Volatile.Write(ref _noiseProfile, profileA);
        _learningProfile = profileB;
        _spareProfile = profileB;
        Volatile.Write(ref _noiseProfileFrames, retainedFrames);
        Volatile.Write(ref _profileSampleRate, retainedFrames > 0 ? SampleRate : 0);
        _pendingProfile = null;
        _pendingProfileFrames = 0;
        _pendingProfileSampleRate = 0;

        _learnBuf = new float[ChannelCount][];
        for (int channel = 0; channel < ChannelCount; channel++)
            _learnBuf[channel] = new float[FftSize];
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
        _specSmooth = new double[FftSize / 2 + 1];
        _specRe = new float[ChannelCount][];
        _specIm = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _specIn[c] = new float[FftSize];
            _specOla[c] = new float[OlaLength];
            _specRe[c] = new float[FftSize];
            _specIm[c] = new float[FftSize];
        }
        _fftRe = new float[FftSize];
        _fftIm = new float[FftSize];
        ResetSpectralState();
    }

    /// <summary>Full reset, including the learned profile (rate/channel changes invalidate it).</summary>
    private void ResetSpectralState()
    {
        ResetStreamState();
        _learning = false;
        Volatile.Write(ref _learningActive, 0);
        _spectralWasActive = false;
    }

    /// <summary>Streaming state only: rings, OLA accumulators, positions, pipeline delay.</summary>
    private void ResetStreamState()
    {
        foreach (float[] channel in _learnBuf) Array.Clear(channel);
        _learnPos = 0;
        foreach (var ring in _specIn) Array.Clear(ring);
        foreach (var ola in _specOla) Array.Clear(ola);
        Array.Fill(_specSmooth, 1.0);
        _specInPos = 0;
        _specInFilled = 0;
        _specSinceFrame = 0;
        _specPrimed = false;
        _specOlaRead = 0;
        _specOlaWrite = 0;
        _specPipelineDelay = FftSize + HopSize;
    }

    private void ResetSpectralPipeline()
    {
        foreach (float[] ring in _specIn) Array.Clear(ring);
        foreach (float[] ola in _specOla) Array.Clear(ola);
        Array.Fill(_specSmooth, 1.0);
        _specInPos = 0;
        _specInFilled = 0;
        _specSinceFrame = 0;
        _specPrimed = false;
        _specOlaRead = 0;
        _specOlaWrite = 0;
        _specPipelineDelay = FftSize + HopSize;
    }

    /// <summary>Copies content-specific learned state into a new rack instance for A/B or render.</summary>
    internal void CopyLearnedProfileFrom(NoiseReductionEffect source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Volatile.Read(ref source._learningActive) != 0) return;
        double[] profile = Volatile.Read(ref source._noiseProfile);
        int frames = Volatile.Read(ref source._noiseProfileFrames);
        int rate = Volatile.Read(ref source._profileSampleRate);
        if (frames <= 0 || rate <= 0 || profile.Length == 0) return;
        _pendingProfile = (double[])profile.Clone();
        _pendingProfileFrames = frames;
        _pendingProfileSampleRate = rate;
    }

    public override void ResetState()
    {
        Array.Clear(_hissLowPass);
        _envelope = 0;
        _noiseGain = 1;
        _hissGain = 1;
        _reductionReadout = 0;
        // The learned noise profile is captured user data, not processing state:
        // ResetState is documented to clear delay lines and envelopes without
        // touching parameters, and the master section calls it on every transport
        // start. Discarding the profile here silently turned SPECTRAL NR into a
        // no-op and dropped LatencySamples from a full frame to 0 mid-render.
        if (_specIn.Length == ChannelCount) ResetStreamState();
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
            Volatile.Write(ref _learningActive, 1);
            _learningProfile = _spareProfile;
            Array.Clear(_learningProfile);
            foreach (float[] channel in _learnBuf) Array.Clear(channel);
            _learnPos = 0;
            _learningProfileFrames = 0;
            ResetSpectralPipeline();
        }
        else if (!learnRequested && _learning)
        {
            _learning = false;
            // Normalize noise profile
            if (_learningProfileFrames > 0)
            {
                for (int i = 0; i < _learningProfile.Length; i++)
                    _learningProfile[i] /= _learningProfileFrames;
                double[] oldProfile = Volatile.Read(ref _noiseProfile);
                Volatile.Write(ref _noiseProfile, _learningProfile);
                _spareProfile = oldProfile;
                Volatile.Write(ref _noiseProfileFrames, _learningProfileFrames);
                Volatile.Write(ref _profileSampleRate, SampleRate);
            }
            Volatile.Write(ref _learningActive, 0);
            ResetSpectralPipeline();
        }

        bool spectralActive = spectralEnabled && !_learning &&
                              Volatile.Read(ref _noiseProfileFrames) > 10;
        if (spectralActive != _spectralWasActive)
        {
            ResetSpectralPipeline();
            _spectralWasActive = spectralActive;
        }

        // Fully-reduced gains are block constants: computing them per frame cost a
        // Math.Pow apiece. The readout tracks the linear minimum and converts once.
        double fullNoiseGain = Math.Pow(10, -noiseReduction / 20.0);
        double fullHissGain = Math.Pow(10, -hissReduction / 20.0);

        int frames = count / ChannelCount;
        double minimumGain = 1;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;

            // Learn the unprocessed input and keep each channel separate until magnitudes are
            // measured. A mono fold-down can cancel vertical noise, and learning after the
            // broadband stage teaches the spectral stage a fingerprint this effect already altered.
            if (_learning)
            {
                for (int channel = 0; channel < ChannelCount; channel++)
                    _learnBuf[channel][_learnPos] = buffer[index + channel];
                if (++_learnPos >= FftSize)
                {
                    AccumulateNoiseProfileFrame();
                    for (int channel = 0; channel < ChannelCount; channel++)
                        Array.Copy(_learnBuf[channel], HopSize, _learnBuf[channel], 0,
                            FftSize - HopSize);
                    _learnPos = FftSize - HopSize;
                }
            }

            double peak = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
                peak = Math.Max(peak, Math.Abs(buffer[index + channel]));

            double detectorCoefficient = peak > _envelope ? detectorAttack : detectorRelease;
            _envelope = detectorCoefficient * _envelope + (1 - detectorCoefficient) * peak;
            double levelDb = 20 * Math.Log10(Math.Max(1e-9, _envelope));
            double depth = Math.Clamp((threshold - levelDb) / 24.0, 0, 1);
            double targetNoiseGain = 1 + (fullNoiseGain - 1) * depth;
            double targetHissGain = 1 + (fullHissGain - 1) * depth;

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

            if (_noiseGain < minimumGain) minimumGain = _noiseGain;

            // --- spectral stage (learned profile, per channel, WOLA) ---
            if (spectralActive)
            {
                // Reads lag writes by the full pipeline delay, so every output
                // sample is read only after its final contributing frame landed.
                bool outputReady = _specPipelineDelay <= 0;
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    _specIn[channel][_specInPos] = buffer[index + channel];

                    float y = 0;
                    if (outputReady)
                    {
                        y = _specOla[channel][_specOlaRead];
                        _specOla[channel][_specOlaRead] = 0;
                    }
                    buffer[index + channel] = y;
                }

                _specInPos = (_specInPos + 1) % FftSize;
                if (_specInFilled < FftSize) _specInFilled++;
                _specSinceFrame++;
                if (_specPipelineDelay > 0) _specPipelineDelay--;
                else _specOlaRead = (_specOlaRead + 1) % OlaLength;

                // First frame the moment the analysis ring fills, then every HopSize
                if (_specInFilled >= FftSize && (!_specPrimed || _specSinceFrame >= HopSize))
                {
                    _specPrimed = true;
                    _specSinceFrame = 0;
                    ProcessSpectralFrame(smoothingFactor);
                    _specOlaWrite = (_specOlaWrite + HopSize) % OlaLength;
                }
            }
        }

        _reductionReadout = -20 * Math.Log10(Math.Max(1e-9, minimumGain));
    }

    /// <summary>Window + FFT one input frame and fold its magnitudes into the profile.</summary>
    private void AccumulateNoiseProfileFrame()
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            for (int i = 0; i < FftSize; i++)
            {
                _fftRe[i] = _learnBuf[channel][i] * _window[i];
                _fftIm[i] = 0;
            }
            Fft.Forward(_fftRe, _fftIm);

            double norm = 2.0 / _windowSum / ChannelCount;
            for (int bin = 0; bin <= FftSize / 2; bin++)
                _learningProfile[bin] += Math.Sqrt(
                    _fftRe[bin] * _fftRe[bin] + _fftIm[bin] * _fftIm[bin]) * norm;
        }
        _learningProfileFrames++;
    }

    /// <summary>Build one shared spectral mask, apply it to every channel, and overlap-add.</summary>
    private void ProcessSpectralFrame(double smoothing)
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            float[] ring = _specIn[channel];
            float[] re = _specRe[channel], im = _specIm[channel];
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = ring[(_specInPos - FftSize + i + FftSize) % FftSize] * _window[i];
                im[i] = 0;
            }
            Fft.Forward(re, im);
        }

        double norm = 2.0 / _windowSum;
        double[] profile = Volatile.Read(ref _noiseProfile);
        int bins = FftSize / 2;
        for (int bin = 0; bin <= bins; bin++)
        {
            double mag = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
                mag += Math.Sqrt(_specRe[channel][bin] * _specRe[channel][bin] +
                                 _specIm[channel][bin] * _specIm[channel][bin]) * norm /
                       ChannelCount;
            double noiseEstimate = profile[bin] * Oversubtract;
            double targetGain = mag > noiseEstimate
                ? (mag - noiseEstimate) / mag
                : 0.01; // −40 dB floor

            _specSmooth[bin] = smoothing * _specSmooth[bin] + (1 - smoothing) * targetGain;
            float g = (float)_specSmooth[bin];
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                _specRe[channel][bin] *= g;
                _specIm[channel][bin] *= g;
                if (bin > 0 && bin < bins)
                {
                    int mirror = FftSize - bin;
                    _specRe[channel][mirror] *= g;
                    _specIm[channel][mirror] *= g;
                }
            }
        }

        float normFactor = (float)(1.0 / _olaNorm);
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            Inverse(_specRe[channel], _specIm[channel]);
            float[] ola = _specOla[channel];
            for (int i = 0; i < FftSize; i++)
                ola[(_specOlaWrite + i) % OlaLength] +=
                    _specRe[channel][i] * _window[i] * normFactor;
        }
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
