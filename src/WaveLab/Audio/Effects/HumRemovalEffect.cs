using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Adaptive hum removal: auto-detects 50/60 Hz mains frequency, phase-matched
/// harmonic notch bank with dynamic depth control to preserve musical content
/// at hum frequencies.
/// </summary>
public sealed class HumRemovalEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("frequency", "MAINS", 45, 65, 60, v => $"{v:0.0} Hz"),
        new("harmonics", "HARMONICS", 1, 12, 6, v => $"{Math.Round(v):0}"),
        new("q", "PRECISION", 10, 80, 35, v => $"Q {v:0}"),
        new("amount", "AMOUNT", 0, 1, 0.85, EffectParam.Pct),
        new("autoDetect", "AUTO DETECT", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
        new("dynamic", "DYNAMIC", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
    ];

    private const int MaxHarmonics = 12; // the HARMONICS parameter maximum

    /// <summary>What the parameters ask the notch bank to be, published whole by whichever thread
    /// moved a knob.</summary>
    /// <remarks>
    /// The other effects publish finished coefficients. This one publishes the request instead,
    /// because the auto-detector retunes the same bank from inside <see cref="Process"/>, and only
    /// one thread may write it. So the audio thread is the one that turns a tuning into
    /// coefficients — twelve notches is a dozen sine and cosine pairs, and only when the tuning
    /// actually moves — and the parameter path never touches a filter at all.
    /// </remarks>
    private sealed record HumTuning(double Frequency, double Q, int Requested, bool AutoDetect);

    private Biquad[][] _notches = [];        // audio-thread state only, [channel][harmonic]
    private HumTuning _requested = new(60, 35, 6, false);
    private HumTuning? _applied;             // audio-thread only
    private int _activeHarmonics;
    private double _detectedFundamental = 60;
    private double _appliedFundamental = 60; // the frequency the notch bank is tuned to
    private double _fundamentalConfidence;
    private double[] _harmonicEnergy = [];
    private double[] _harmonicSmoothing = [];

    public override string TypeId => "dehum";
    public override string DisplayName => "Hum Removal";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"{_activeHarmonics} NOTCHES @ {_detectedFundamental:0.0} Hz";

    protected override void OnConfigure()
    {
        _harmonicEnergy = new double[MaxHarmonics];
        _harmonicSmoothing = new double[MaxHarmonics];

        // The bank is allocated here and nowhere else, always sized for the maximum harmonic count
        // so its shape is fixed: the audio thread owns it, and the detector retunes it from inside
        // Process, where allocating is not allowed.
        var bank = new Biquad[ChannelCount][];
        for (int channel = 0; channel < ChannelCount; channel++)
            bank[channel] = new Biquad[MaxHarmonics];
        _notches = bank;
        _applied = null;
        _activeHarmonics = 0;
    }

    protected override void OnParamsChanged() => Volatile.Write(ref _requested, new HumTuning(
        GetParam("frequency"),
        GetParam("q"),
        (int)Math.Round(GetParam("harmonics")),
        GetParam("autoDetect") > 0.5));

    /// <summary>The frequency the bank should be tuned to, manual until the detector has locked.</summary>
    private double EffectiveFundamental(HumTuning tuning) =>
        tuning.AutoDetect && _fundamentalConfidence > 0.3 ? _detectedFundamental : tuning.Frequency;

    /// <summary>
    /// Retune the bank in place. Audio thread only, and it allocates nothing: keeping the existing
    /// bank is what carries every notch's delay-line state across a retune mid-stream.
    /// </summary>
    private void ApplyTuning(HumTuning tuning, double fundamental)
    {
        var bank = _notches;
        int active = 0;
        for (int harmonic = 1; harmonic <= tuning.Requested; harmonic++)
            if (fundamental * harmonic < SampleRate * 0.475) active++;

        for (int harmonic = 1; harmonic <= active; harmonic++)
        {
            Biquad proto = Biquad.Notch(SampleRate, fundamental * harmonic, tuning.Q);
            for (int channel = 0; channel < bank.Length; channel++)
                bank[channel][harmonic - 1].CopyCoefficientsFrom(proto);
        }

        // Stages coming back into use start from a stale delay line — clear those.
        for (int harmonic = _activeHarmonics; harmonic < active; harmonic++)
            for (int channel = 0; channel < bank.Length; channel++)
                bank[channel][harmonic].Reset();

        _applied = tuning;
        _appliedFundamental = fundamental;
        Volatile.Write(ref _activeHarmonics, active);
    }

    public override void ResetState()
    {
        var notches = _notches;
        for (int channel = 0; channel < notches.Length; channel++)
            for (int harmonic = 0; harmonic < notches[channel].Length; harmonic++)
                notches[channel][harmonic].Reset();
        Array.Clear(_harmonicEnergy);
        Array.Clear(_harmonicSmoothing);
        _detectedFundamental = GetParam("frequency");
        _fundamentalConfidence = 0;
        // The detector has been rewound, so the next block retunes to the manual frequency.
        _applied = null;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var notches = _notches;
        if (notches.Length != ChannelCount) return;

        float amount = (float)GetParam("amount");
        float dry = 1 - amount;
        bool dynamic = GetParam("dynamic") > 0.5;

        var tuning = Volatile.Read(ref _requested);

        // Adaptive fundamental detection via energy-gated Goertzel analysis. It runs before the
        // retune below, so a lock acquired this block is acted on in this block.
        if (tuning.AutoDetect) DetectFundamental(buffer, offset, count);

        double fundamental = EffectiveFundamental(tuning);
        if (!ReferenceEquals(tuning, _applied) || Math.Abs(fundamental - _appliedFundamental) > 0.05)
            ApplyTuning(tuning, fundamental);

        int active = Math.Min(_activeHarmonics, notches[0].Length);

        for (int i = 0; i < count; i++)
        {
            int channel = i % ChannelCount;
            int index = offset + i;
            float input = buffer[index];
            float filtered = input;

            for (int harmonic = 0; harmonic < active; harmonic++)
            {
                // Blend against what entered this stage, never the raw input:
                // mixing `input` back in would re-inject the hum the earlier
                // notches already removed.
                float pre = filtered;
                float notchOut = notches[channel][harmonic].Process(pre);

                // Dynamic depth: reduce notch depth when harmonic has significant energy
                // (likely musical content, not hum)
                if (dynamic && harmonic < _harmonicEnergy.Length)
                {
                    double energy = Math.Abs(pre);
                    _harmonicEnergy[harmonic] = 0.95 * _harmonicEnergy[harmonic] + 0.05 * energy;
                    _harmonicSmoothing[harmonic] = 0.9 * _harmonicSmoothing[harmonic] + 0.1 * _harmonicEnergy[harmonic];

                    // The global AMOUNT is applied once, after the bank; this stage
                    // only decides how deep its own notch goes.
                    double depth = 1.0;
                    if (_harmonicSmoothing[harmonic] > 0.01)
                    {
                        // Reduce notch depth when sustained energy is present
                        double reduction = Math.Clamp(_harmonicSmoothing[harmonic] * 20, 0, 1);
                        depth = 1 - reduction * 0.7;
                    }
                    filtered = (float)(pre * (1 - depth) + notchOut * depth);
                }
                else
                {
                    filtered = notchOut;
                }
            }
            buffer[index] = input * dry + filtered * amount;
        }
    }

    private void DetectFundamental(float[] buffer, int offset, int count)
    {
        // Goertzel power at 50 and 60 Hz, gated against the total signal power:
        // a mains fundamental must carry a meaningful share of the energy, so
        // bass-heavy music (e.g. a 55 Hz A1 note) cannot false-lock the detector.
        int frames = count / ChannelCount;
        if (frames < 64) return;

        double w50 = 2 * Math.PI * 50 / SampleRate, w60 = 2 * Math.PI * 60 / SampleRate;
        double c50 = 2 * Math.Cos(w50), c60 = 2 * Math.Cos(w60);
        double s50a = 0, s50b = 0, s60a = 0, s60b = 0, total = 0;

        for (int f = 0; f < frames; f++)
        {
            double x = buffer[offset + f * ChannelCount];
            double y50 = x + c50 * s50a - s50b;
            s50b = s50a; s50a = y50;
            double y60 = x + c60 * s60a - s60b;
            s60b = s60a; s60a = y60;
            total += x * x;
        }
        if (total <= 1e-9) return;

        double p50 = s50a * s50a + s50b * s50b - c50 * s50a * s50b;
        double p60 = s60a * s60a + s60b * s60b - c60 * s60a * s60b;

        // Normalized share of signal energy at each mains candidate (1 = all of it)
        double share50 = 2 * p50 / (frames * total);
        double share60 = 2 * p60 / (frames * total);
        double candidate = share50 > share60 ? 50 : 60;
        double share = Math.Max(share50, share60);

        // Full confidence once the candidate holds ≳17% of the signal energy
        double confidence = Math.Clamp((share - 0.02) / 0.15, 0, 1);

        // Smooth the detection
        if (confidence > 0)
        {
            // The caller retunes the notch bank once the locked estimate has actually moved —
            // updating the readout alone left the notches on the manual frequency.
            _detectedFundamental = 0.9 * _detectedFundamental + 0.1 * candidate;
            _fundamentalConfidence = 0.9 * _fundamentalConfidence + 0.1 * confidence;
        }
        else
        {
            _fundamentalConfidence *= 0.95;
        }
    }

}