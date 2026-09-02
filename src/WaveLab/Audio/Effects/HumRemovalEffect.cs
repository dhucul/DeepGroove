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
        new("harmonicMask", "PARTIAL MASK", 1, 0xFFF, 0xFFF,
            v => $"0x{(int)Math.Round(v):X3}"),
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
    private sealed record HumTuning(
        double Frequency, double Q, int Requested, int HarmonicMask, bool AutoDetect);

    private Biquad[][] _notches = [];        // audio-thread state only, [channel][harmonic]
    private HumTuning _requested = new(60, 35, 6, 0xFFF, false);
    private HumTuning? _applied;             // audio-thread only
    private int _activeHarmonics;
    private double _detectedFundamental = 60;
    private double _appliedFundamental = 60; // the frequency the notch bank is tuned to
    private double _fundamentalConfidence;
    private double[] _harmonicEnergy = [];
    private double[] _harmonicSmoothing = [];
    private float[] _frameInput = [];
    private float[] _frameFiltered = [];

    // Detection probes: each mains candidate with its first two harmonics, then three frequencies
    // that are harmonics of neither, which measure what the programme alone is doing.
    private static readonly double[] ProbeFrequencies =
        [50, 100, 150, 60, 120, 180, 37, 73, 137];
    private const int MainsProbesEach = 3;
    private const double DetectionWindowSeconds = 0.25;

    private double[] _probeCoefficient = [];
    private double[] _probeS1 = [];
    private double[] _probeS2 = [];
    private int _windowFrames;
    private int _windowFilled;
    private double _score50, _score60;

    public override string TypeId => "dehum";
    public override string DisplayName => "Hum Removal";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout =>
        $"{SupportedNotchCount()} NOTCHES @ {_detectedFundamental:0.0} Hz";

    protected override void OnConfigure()
    {
        _harmonicEnergy = new double[MaxHarmonics];
        _harmonicSmoothing = new double[MaxHarmonics];
        _frameInput = new float[ChannelCount];
        _frameFiltered = new float[ChannelCount];

        // The bank is allocated here and nowhere else, always sized for the maximum harmonic count
        // so its shape is fixed: the audio thread owns it, and the detector retunes it from inside
        // Process, where allocating is not allowed.
        var bank = new Biquad[ChannelCount][];
        for (int channel = 0; channel < ChannelCount; channel++)
            bank[channel] = new Biquad[MaxHarmonics];
        _notches = bank;
        _applied = null;
        _activeHarmonics = 0;

        // The detector's accumulators, allocated here for the same reason the bank is: it runs on
        // the audio thread and may allocate nothing there.
        _probeCoefficient = new double[ProbeFrequencies.Length];
        _probeS1 = new double[ProbeFrequencies.Length * ChannelCount];
        _probeS2 = new double[ProbeFrequencies.Length * ChannelCount];
        for (int probe = 0; probe < ProbeFrequencies.Length; probe++)
            _probeCoefficient[probe] = 2 * Math.Cos(2 * Math.PI * ProbeFrequencies[probe] / SampleRate);

        // A quarter of a second resolves 50 Hz from 60: the analysis lobe is 1/T wide, so 4 Hz
        // here, and the two candidates sit fifteen lobes apart. A block-sized window does not --
        // at a 10 ms buffer a 50 Hz cycle has not finished, and the two are indistinguishable.
        _windowFrames = Math.Max(1, (int)(SampleRate * DetectionWindowSeconds));
        _windowFilled = 0;
    }

    protected override void OnParamsChanged() => Volatile.Write(ref _requested, new HumTuning(
        GetParam("frequency"),
        GetParam("q"),
        (int)Math.Round(GetParam("harmonics")),
        (int)Math.Round(GetParam("harmonicMask")),
        GetParam("autoDetect") > 0.5));

    private int SupportedNotchCount()
    {
        HumTuning tuning = Volatile.Read(ref _applied) ?? Volatile.Read(ref _requested);
        int count = 0;
        int active = Math.Min(Volatile.Read(ref _activeHarmonics), MaxHarmonics);
        for (int harmonic = 0; harmonic < active; harmonic++)
            if ((tuning.HarmonicMask & (1 << harmonic)) != 0) count++;
        return count;
    }

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
        int previousActive = _activeHarmonics;
        int previousMask = _applied?.HarmonicMask ?? 0;
        int active = 0;
        for (int harmonic = 1; harmonic <= tuning.Requested; harmonic++)
            if (fundamental * harmonic < SampleRate * 0.475) active++;

        for (int harmonic = 1; harmonic <= active; harmonic++)
        {
            Biquad proto = Biquad.Notch(SampleRate, fundamental * harmonic, tuning.Q);
            for (int channel = 0; channel < bank.Length; channel++)
                bank[channel][harmonic - 1].CopyCoefficientsFrom(proto);
        }

        // Stages coming back into use start from a stale delay line — clear those. That includes a
        // stage restored by PARTIAL MASK, not only one restored by increasing HARMONICS.
        for (int harmonic = 0; harmonic < active; harmonic++)
        {
            bool newlyActive = harmonic >= previousActive;
            bool newlyUnmasked = (previousMask & (1 << harmonic)) == 0
                                 && (tuning.HarmonicMask & (1 << harmonic)) != 0;
            if (!newlyActive && !newlyUnmasked) continue;
            for (int channel = 0; channel < bank.Length; channel++)
                bank[channel][harmonic].Reset();
        }

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
        _score50 = 0;
        _score60 = 0;
        _windowFilled = 0;
        Array.Clear(_probeS1);
        Array.Clear(_probeS2);
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

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                float input = buffer[index + channel];
                _frameInput[channel] = input;
                _frameFiltered[channel] = input;
            }

            for (int harmonic = 0; harmonic < active; harmonic++)
            {
                // Cleanup analysis supplies a sparse mask. A missing second or fourth partial is
                // evidence that music, not mains, occupies that line; leave it untouched exactly
                // as the offline restoration path does.
                if ((tuning.HarmonicMask & (1 << harmonic)) == 0) continue;

                // Dynamic depth: reduce notch depth when harmonic has significant energy
                // (likely musical content, not hum). Measure once across the complete frame and
                // apply one depth to every channel: channel-by-channel decisions move a centred
                // sound sideways whenever one side happens to be louder.
                double depth = 1.0;
                if (dynamic && harmonic < _harmonicEnergy.Length)
                {
                    double energy = 0;
                    for (int channel = 0; channel < ChannelCount; channel++)
                        energy = Math.Max(energy, Math.Abs(_frameFiltered[channel]));
                    _harmonicEnergy[harmonic] = 0.95 * _harmonicEnergy[harmonic] + 0.05 * energy;
                    _harmonicSmoothing[harmonic] = 0.9 * _harmonicSmoothing[harmonic] + 0.1 * _harmonicEnergy[harmonic];

                    // The global AMOUNT is applied once, after the bank; this stage
                    // only decides how deep its own notch goes.
                    if (_harmonicSmoothing[harmonic] > 0.01)
                    {
                        // Reduce notch depth when sustained energy is present
                        double reduction = Math.Clamp(_harmonicSmoothing[harmonic] * 20, 0, 1);
                        depth = 1 - reduction * 0.7;
                    }
                }

                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    // Blend against what entered this stage, never the raw input: mixing the raw
                    // sample back in would re-inject hum removed by the earlier notches.
                    float pre = _frameFiltered[channel];
                    float notchOut = notches[channel][harmonic].Process(pre);
                    _frameFiltered[channel] = (float)(pre * (1 - depth) + notchOut * depth);
                }
            }

            for (int channel = 0; channel < ChannelCount; channel++)
                buffer[index + channel] = _frameInput[channel] * dry + _frameFiltered[channel] * amount;
        }
    }

    /// <summary>
    /// Decide whether the mains here is 50 or 60 Hz, from a fixed window and by prominence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things this must not do, and the version it replaces did both. It must not measure over
    /// whatever block the caller happens to bring: the resolution of the analysis is one over its
    /// length, so a 10 ms buffer cannot tell 50 Hz from 60 at all, and a 1.37 s offline block
    /// yields so few windows that nothing converges. The window is a fixed quarter of a second
    /// whatever the block, accumulated a sample at a time so no buffer is held and no cost arrives
    /// in a burst.
    /// </para>
    /// <para>
    /// And it must not gate on <em>share of total energy</em>. Mains hum 22 dB under programme
    /// holds well under a percent of it and is still perfectly visible, because it is a narrow line
    /// and the programme is not — so the old 2% gate meant the detector fired on hum with nothing
    /// over it and never on a real transfer. What decides it here is **prominence**: each
    /// candidate's fundamental and first harmonics against three frequencies that are harmonics of
    /// neither, which is what the music alone is doing. `HumTracker` reached the same conclusion
    /// for the same reason.
    /// </para>
    /// <para>
    /// The estimate is one of the two candidates and never a blend of them. Smoothing a frequency
    /// toward whichever candidate won each block put the notches at 54.4 Hz — a frequency no mains
    /// supply has ever run at, and between the two it was choosing from. Confidence is smoothed;
    /// the answer is voted.
    /// </para>
    /// </remarks>
    private void DetectFundamental(float[] buffer, int offset, int count)
    {
        if (_probeS1.Length != ProbeFrequencies.Length * ChannelCount) return;

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int index = offset + f * ChannelCount;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                // Keep the channels separate through the linear Goertzel accumulators and combine
                // their powers only at the end of the window. A mono fold-down cancels vertical
                // hum; selecting a channel sample-by-sample creates switching products in music.
                double x = buffer[index + channel];
                int state = channel * ProbeFrequencies.Length;
                for (int probe = 0; probe < ProbeFrequencies.Length; probe++, state++)
                {
                    double s0 = x + _probeCoefficient[probe] * _probeS1[state] - _probeS2[state];
                    _probeS2[state] = _probeS1[state];
                    _probeS1[state] = s0;
                }
            }

            if (++_windowFilled >= _windowFrames) CompleteDetectionWindow();
        }
    }

    /// <summary>Read the accumulators, score the two candidates, and start the next window.</summary>
    private void CompleteDetectionWindow()
    {
        // What the programme is doing where no mains harmonic can be: the median of the three, so
        // one bass note landing on a probe cannot pass for the whole background.
        double a = PowerAt(6), b = PowerAt(7), c = PowerAt(8);
        double background = Math.Max(Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c)), 1e-24);

        double strength50 = Strength(0, background);
        double strength60 = Strength(3, background);

        _windowFilled = 0;
        Array.Clear(_probeS1);
        Array.Clear(_probeS2);

        double winner = Math.Max(strength50, strength60);
        if (winner <= 0)
        {
            // Nothing that looks like mains. Let the lock decay rather than dropping it, so a
            // passage that briefly buries the hum does not retune the bank away from it.
            _score50 *= ScoreDecay;
            _score60 *= ScoreDecay;
        }
        else if (strength50 >= strength60)
        {
            _score50 += (1 - _score50) * ScoreRise;
            _score60 *= ScoreDecay;
        }
        else
        {
            _score60 += (1 - _score60) * ScoreRise;
            _score50 *= ScoreDecay;
        }

        _detectedFundamental = _score50 >= _score60 ? 50 : 60;
        _fundamentalConfidence = Math.Max(_score50, _score60);
    }

    private const double ScoreRise = 0.15;
    private const double ScoreDecay = 0.8;

    /// <summary>
    /// How far a candidate stands above the programme, or zero if it does not stand above it at
    /// all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the fundamental is required. Requiring the second partial as well — which is what
    /// `HumTracker` does, and it is right to, having a whole spectrum to search — rejects a hum
    /// that is very nearly a sine, and magnetically induced mains hum often is. It cost nothing
    /// here anyway: there are two candidates rather than a spectrum of them, so the comb cannot
    /// pick out a subharmonic of the music the way it can there.
    /// </para>
    /// <para>
    /// The partials still count, because they are added into the strength: between two candidates
    /// that both stand clear, the one that looks like a comb wins. What guards against a sustained
    /// bass note near a mains frequency is that it has to hold for about three quarters of a
    /// second before the score crosses the confidence the tuning needs — mains hum runs for the
    /// whole side, and a note does not.
    /// </para>
    /// </remarks>
    private double Strength(int firstProbe, double background)
    {
        if (PowerAt(firstProbe) < background * MinimumProminence) return 0;

        double total = PowerAt(firstProbe) + PowerAt(firstProbe + 1) + PowerAt(firstProbe + 2);
        return total / (MainsProbesEach * background);
    }

    /// <summary>A mains line has to stand 8 dB clear of what the music is doing off the comb.</summary>
    private const double MinimumProminence = 6.3;

    private double PowerAt(int probe)
    {
        double power = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            int state = channel * ProbeFrequencies.Length + probe;
            power += _probeS1[state] * _probeS1[state] + _probeS2[state] * _probeS2[state]
                   - _probeCoefficient[probe] * _probeS1[state] * _probeS2[state];
        }
        double scale = 1.0 / ((double)_windowFrames * _windowFrames * ChannelCount);
        return power * scale;
    }
}
