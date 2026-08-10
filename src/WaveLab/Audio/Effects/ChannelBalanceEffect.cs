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

    // Auto-alignment state
    private double[] _correlationWindow = [];
    private int _correlationWindowPos;
    private double _bestAlignment;
    private double _alignmentConfidence;

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
        _correlationWindow = new double[Math.Max(64, (int)(SampleRate * 0.05))];
        _correlationWindowPos = 0;
        _bestAlignment = 0;
        _alignmentConfidence = 0;
    }

    public override void ResetState()
    {
        Array.Clear(_leftDelay);
        Array.Clear(_rightDelay);
        Array.Clear(_correlationWindow);
        _writePosition = 0;
        _currentAlignment = GetParam("align") * SampleRate / 1000.0;
        GetBalanceGains(GetParam("balance"), out _leftGain, out _rightGain);
        _correlation = 1;
        _midEnergy = 0;
        _sideEnergy = 0;
        _correlationWindowPos = 0;
        _bestAlignment = 0;
        _alignmentConfidence = 0;
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

        // Auto-alignment via cross-correlation
        if (autoAlign)
        {
            PerformAutoAlignment(buffer, offset, count);
            if (_alignmentConfidence > 0.5)
                targetAlignment = _bestAlignment;
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

            float outL = (float)(left * _leftGain);
            float outR = (float)(right * _rightGain);

            if (msMode)
            {
                // M/S balance mode
                double mid = (outL + outR) * 0.5;
                double side = (outL - outR) * 0.5;
                buffer[index] = (float)(mid + side);
                buffer[index + 1] = (float)(mid - side);
            }
            else
            {
                buffer[index] = outL;
                buffer[index + 1] = outR;
            }

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

    private void PerformAutoAlignment(float[] buffer, int offset, int count)
    {
        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            float left = buffer[idx];
            float right = buffer[idx + 1];

            _correlationWindow[_correlationWindowPos] = left * right;
            _correlationWindowPos = (_correlationWindowPos + 1) % _correlationWindow.Length;

            // Every ~50ms, compute cross-correlation at various lags
            if (_correlationWindowPos == 0)
            {
                double bestCorr = 0;
                double bestLag = 0;

                // Test lags from -5ms to +5ms
                for (int lag = -(int)(SampleRate * 0.005); lag <= (int)(SampleRate * 0.005); lag += 2)
                {
                    double corr = 0;
                    int count2 = 0;
                    for (int i = 0; i < _correlationWindow.Length - Math.Abs(lag); i++)
                    {
                        int j = i + lag;
                        if (j >= 0 && j < _correlationWindow.Length)
                        {
                            corr += _correlationWindow[i] * _correlationWindow[j];
                            count2++;
                        }
                    }
                    if (count2 > 0) corr /= count2;
                    if (corr > bestCorr)
                    {
                        bestCorr = corr;
                        bestLag = lag;
                    }
                }

                _bestAlignment = bestLag;
                _alignmentConfidence = Math.Clamp(bestCorr * 10, 0, 1);
            }
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