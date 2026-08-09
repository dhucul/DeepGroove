namespace WaveLab.Audio.Effects;

/// <summary>
/// Stereo channel balance and sub-sample timing alignment. Positive alignment delays
/// the right channel; negative alignment delays the left channel.
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
    ];

    private float[] _leftDelay = [];
    private float[] _rightDelay = [];
    private int _writePosition;
    private double _currentAlignment;
    private double _leftGain = 1;
    private double _rightGain = 1;

    public override string TypeId => "channel-balance";
    public override string DisplayName => "Channel Balance & Alignment";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        int length = Math.Max(32, (int)Math.Ceiling(SampleRate * 0.012) + 2);
        _leftDelay = new float[length];
        _rightDelay = new float[length];
    }

    public override void ResetState()
    {
        Array.Clear(_leftDelay);
        Array.Clear(_rightDelay);
        _writePosition = 0;
        _currentAlignment = GetParam("align") * SampleRate / 1000.0;
        GetBalanceGains(GetParam("balance"), out _leftGain, out _rightGain);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (ChannelCount < 2 || _leftDelay.Length == 0) return;

        double targetAlignment = GetParam("align") * SampleRate / 1000.0;
        targetAlignment = Math.Clamp(targetAlignment, -_leftDelay.Length + 2, _leftDelay.Length - 2);
        GetBalanceGains(GetParam("balance"), out double targetLeftGain, out double targetRightGain);
        double smoothing = 1 - Math.Exp(-1.0 / (SampleRate * 0.01));

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

            buffer[index] = (float)(left * _leftGain);
            buffer[index + 1] = (float)(right * _rightGain);
            if (++_writePosition == _leftDelay.Length) _writePosition = 0;
        }
    }

    private static void GetBalanceGains(double balanceDb, out double left, out double right)
    {
        // Balance only attenuates the opposite side, avoiding an unexpected level boost.
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
