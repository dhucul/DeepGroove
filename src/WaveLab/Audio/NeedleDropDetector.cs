namespace WaveLab.Audio;

/// <summary>
/// Finds the sharp, high-crest transient made when a stylus touches a record.
/// The detector learns the input noise for a short settling period, then uses
/// both absolute and noise-relative thresholds so normal interface hiss or hum
/// cannot arm the recorder by itself.
/// </summary>
internal sealed class NeedleDropDetector
{
    private const double WarmupSeconds = 0.15;
    private const double MinimumPeak = 0.007943; // -42 dBFS
    private const double MinimumStep = 0.003981; // -48 dBFS
    private const double MinimumCrest = 2.5;

    private long _observedFrames;
    private double _noiseSquare = 1e-10;
    private double _stepSquare = 1e-10;
    private float[] _previousSamples = [];

    public bool IsTriggered { get; private set; }

    public bool Process(float[] samples, int count, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (count < 0 || count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (IsTriggered || count < channels) return IsTriggered;

        if (_previousSamples.Length != channels) _previousSamples = new float[channels];

        int completeCount = count - count % channels;
        int frames = completeCount / channels;
        double sumSquares = 0;
        double sumStepSquares = 0;
        double peak = 0;
        double maximumStep = 0;

        for (int index = 0; index < completeCount; index++)
        {
            int channel = index % channels;
            float finiteSample = float.IsFinite(samples[index]) ? samples[index] : 0;
            double magnitude = Math.Abs(finiteSample);
            double step = Math.Abs(finiteSample - _previousSamples[channel]);
            _previousSamples[channel] = finiteSample;
            sumSquares += finiteSample * finiteSample;
            sumStepSquares += step * step;
            peak = Math.Max(peak, magnitude);
            maximumStep = Math.Max(maximumStep, step);
        }

        double packetRms = Math.Sqrt(sumSquares / completeCount);
        double packetStepRms = Math.Sqrt(sumStepSquares / completeCount);
        long warmupFrames = Math.Max(1, (long)Math.Round(sampleRate * WarmupSeconds));
        bool warmedUp = _observedFrames >= warmupFrames;
        double noiseRms = Math.Sqrt(_noiseSquare);
        double normalStepRms = Math.Sqrt(_stepSquare);
        double peakThreshold = Math.Max(MinimumPeak, noiseRms * 8);
        double stepThreshold = Math.Max(MinimumStep, normalStepRms * 10);
        double crestReference = Math.Max(packetRms, noiseRms);

        if (warmedUp
            && peak >= peakThreshold
            && maximumStep >= stepThreshold
            && peak >= crestReference * MinimumCrest)
        {
            IsTriggered = true;
            return true;
        }

        // Learn slowly and cap each update. A hand near the tonearm or the first
        // hint of contact therefore cannot raise the baseline enough to hide the
        // actual drop transient in the following packet.
        double limitedRms = Math.Min(packetRms, Math.Max(noiseRms * 2, MinimumPeak / 4));
        double limitedStepRms = Math.Min(packetStepRms, Math.Max(normalStepRms * 2, MinimumStep / 4));
        double weight = Math.Clamp(frames / (sampleRate * 1.5), 0.001, 0.08);
        _noiseSquare += (limitedRms * limitedRms - _noiseSquare) * weight;
        _stepSquare += (limitedStepRms * limitedStepRms - _stepSquare) * weight;
        _observedFrames += frames;
        return false;
    }
}
