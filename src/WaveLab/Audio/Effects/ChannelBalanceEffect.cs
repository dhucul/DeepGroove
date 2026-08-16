namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced channel balance: stereo balance, sub-sample timing alignment,
/// auto-alignment via cross-correlation, real-time correlation meter,
/// and mid/side balance mode.
/// </summary>
public sealed class ChannelBalanceEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("balance", "BALANCE", -12, 12, 0, v => v switch
        {
            < -0.05 => $"L {Math.Abs(v):0.0} dB",
            > 0.05 => $"R {v:0.0} dB",
            _ => "Center",
        }),
        new("align", "ALIGN", -10, 10, 0, v => v switch
        {
            < -0.005 => $"L +{Math.Abs(v):0.00} ms",
            > 0.005 => $"R +{v:0.00} ms",
            _ => "0.00 ms",
        }),
        new("autoAlign", "AUTO ALIGN", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
        new("mode", "MODE", 0, 1, 0, v => v > 0.5 ? "M/S" : "L/R"),
    ];

    private float[] _leftDelay = [];
    private float[] _rightDelay = [];
    private int _writePosition;
    private double _currentAlignment;
    private double _leftGain = 1;
    private double _rightGain = 1;
    private double _correlation = 1;
    private double _midEnergy;
    private double _sideEnergy;

    // Auto-alignment state. The audio thread only fills the two windows and
    // publishes a snapshot; the correlation sweep itself runs on the thread pool.
    private double[] _leftWindow = [];
    private double[] _rightWindow = [];
    private double[] _analysisLeft = [];
    private double[] _analysisRight = [];
    private int _correlationWindowPos;
    private int _analysisBusy;
    private double _bestAlignment;
    private double _alignmentConfidence;

    private static readonly Action<ChannelBalanceEffect> AnalyseCallback = static effect => effect.AnalyseSnapshot();

    public override string TypeId => "channel-balance";
    public override string DisplayName => "Channel Balance & Alignment";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => ChannelCount < 2
        ? "STEREO ONLY"
        : $"CORR {_correlation:+0.00;-0.00;0.00}";

    protected override void OnConfigure()
    {
        int length = Math.Max(32, (int)Math.Ceiling(SampleRate * 0.012) + 2);
        _leftDelay = new float[length];
        _rightDelay = new float[length];
        int windowLength = Math.Max(64, (int)(SampleRate * 0.05));
        _leftWindow = new double[windowLength];
        _rightWindow = new double[windowLength];
        _analysisLeft = new double[windowLength];
        _analysisRight = new double[windowLength];
        _correlationWindowPos = 0;
        _bestAlignment = 0;
        _alignmentConfidence = 0;
    }

    public override void ResetState()
    {
        Array.Clear(_leftDelay);
        Array.Clear(_rightDelay);
        Array.Clear(_leftWindow);
        Array.Clear(_rightWindow);
        _writePosition = 0;
        _currentAlignment = GetParam("align") * SampleRate / 1000.0;
        GetBalanceGains(GetParam("balance"), out _leftGain, out _rightGain);
        _correlation = 1;
        _midEnergy = 0;
        _sideEnergy = 0;
        _correlationWindowPos = 0;
        Volatile.Write(ref _bestAlignment, 0);
        Volatile.Write(ref _alignmentConfidence, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (ChannelCount < 2 || _leftDelay.Length == 0) return;

        double targetAlignment = GetParam("align") * SampleRate / 1000.0;
        targetAlignment = Math.Clamp(targetAlignment, -_leftDelay.Length + 2, _leftDelay.Length - 2);
        GetBalanceGains(GetParam("balance"), out double targetLeftGain, out double targetRightGain);
        double smoothing = 1 - Math.Exp(-1.0 / (SampleRate * 0.01));
        bool autoAlign = GetParam("autoAlign") > 0.5;
        bool msMode = GetParam("mode") > 0.5;
        double energyCoefficient = Math.Exp(-1.0 / (SampleRate * 0.1));

        // Auto-alignment via cross-correlation. The audio thread only collects the
        // window and reads whatever the background analysis last published.
        if (autoAlign)
        {
            CollectAlignmentWindow(buffer, offset, count);
            if (Volatile.Read(ref _alignmentConfidence) > 0.5)
                targetAlignment = Math.Clamp(Volatile.Read(ref _bestAlignment),
                    -_leftDelay.Length + 2, _leftDelay.Length - 2);
        }

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            float left = buffer[index];
            float right = buffer[index + 1];
            _leftDelay[_writePosition] = left;
            _rightDelay[_writePosition] = right;

            _currentAlignment += smoothing * (targetAlignment - _currentAlignment);
            _leftGain += smoothing * (targetLeftGain - _leftGain);
            _rightGain += smoothing * (targetRightGain - _rightGain);

            if (_currentAlignment > 0.0001)
                right = ReadDelayed(_rightDelay, _writePosition, _currentAlignment);
            else if (_currentAlignment < -0.0001)
                left = ReadDelayed(_leftDelay, _writePosition, -_currentAlignment);

            float outL, outR;
            if (msMode)
            {
                // M/S balance mode: BALANCE drives mid against side. Applying the
                // gains to L/R and then converting back is an algebraic identity,
                // so the mode has to act on the mid/side pair itself.
                double mid = (left + right) * 0.5 * _leftGain;
                double side = (left - right) * 0.5 * _rightGain;
                outL = (float)(mid + side);
                outR = (float)(mid - side);
            }
            else
            {
                outL = (float)(left * _leftGain);
                outR = (float)(right * _rightGain);
            }

            buffer[index] = outL;
            buffer[index + 1] = outR;

            // Correlation metering
            double m = (outL + outR) * 0.5;
            double s = (outL - outR) * 0.5;
            _midEnergy = energyCoefficient * _midEnergy + (1 - energyCoefficient) * m * m;
            _sideEnergy = energyCoefficient * _sideEnergy + (1 - energyCoefficient) * s * s;
            double denom = _midEnergy + _sideEnergy;
            _correlation = denom > 1e-12 ? (_midEnergy - _sideEnergy) / denom : 1;

            if (++_writePosition == _leftDelay.Length) _writePosition = 0;
        }
    }

    /// <summary>Audio thread: fill the L/R windows, hand full ones to the analyser.</summary>
    private void CollectAlignmentWindow(float[] buffer, int offset, int count)
    {
        if (_leftWindow.Length == 0 || _leftWindow.Length != _analysisLeft.Length) return;

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;

            // The channels must stay separate: storing left * right and
            // autocorrelating it can only ever peak at lag 0.
            _leftWindow[_correlationWindowPos] = buffer[idx];
            _rightWindow[_correlationWindowPos] = buffer[idx + 1];
            if (++_correlationWindowPos < _leftWindow.Length) continue;
            _correlationWindowPos = 0;

            // The sweep is hundreds of thousands of multiply-adds — far too much
            // for one audio callback. Snapshot the window and analyse it off-thread.
            if (Interlocked.CompareExchange(ref _analysisBusy, 1, 0) != 0) continue;
            Array.Copy(_leftWindow, _analysisLeft, _leftWindow.Length);
            Array.Copy(_rightWindow, _analysisRight, _rightWindow.Length);
            ThreadPool.UnsafeQueueUserWorkItem(AnalyseCallback, this, preferLocal: false);
        }
    }

    /// <summary>Background: energy-normalised cross-correlation of L against lagged R.</summary>
    private void AnalyseSnapshot()
    {
        try
        {
            double[] left = _analysisLeft, right = _analysisRight;
            int n = Math.Min(left.Length, right.Length);
            if (n == 0) return;

            double leftEnergy = 0, rightEnergy = 0;
            for (int i = 0; i < n; i++)
            {
                leftEnergy += left[i] * left[i];
                rightEnergy += right[i] * right[i];
            }
            double normalizer = Math.Sqrt(Math.Max(1e-12, leftEnergy * rightEnergy));

            int maxLag = Math.Min(Math.Max(1, (int)(SampleRate * 0.005)), n - 1);
            double bestCorr = 0;
            int bestLag = 0;
            for (int lag = -maxLag; lag <= maxLag; lag += 2)
            {
                double corr = 0;
                int start = Math.Max(0, -lag);
                int end = Math.Min(n, n - lag);
                for (int i = start; i < end; i++)
                    corr += left[i] * right[i + lag];
                corr /= normalizer;
                if (corr > bestCorr)
                {
                    bestCorr = corr;
                    bestLag = lag;
                }
            }

            // corr peaks at lag = -d when right leads left by d samples, and the
            // alignment control delays right for positive values — hence the sign.
            Volatile.Write(ref _bestAlignment, -bestLag);
            Volatile.Write(ref _alignmentConfidence, Math.Clamp(bestCorr, 0, 1));
        }
        finally
        {
            Volatile.Write(ref _analysisBusy, 0);
        }
    }

    private static void GetBalanceGains(double balanceDb, out double left, out double right)
    {
        left = Math.Pow(10, -Math.Max(0, balanceDb) / 20.0);
        right = Math.Pow(10, Math.Min(0, balanceDb) / 20.0);
    }

    private static float ReadDelayed(float[] line, int writePosition, double delay)
    {
        double readPosition = writePosition - delay;
        if (readPosition < 0) readPosition += line.Length;
        int read0 = (int)readPosition;
        int read1 = read0 + 1;
        if (read1 == line.Length) read1 = 0;
        double fraction = readPosition - read0;
        return (float)(line[read0] * (1 - fraction) + line[read1] * fraction);
    }
}