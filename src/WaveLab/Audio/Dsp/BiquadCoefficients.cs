namespace WaveLab.Audio.Dsp;

/// <summary>
/// An immutable set of biquad coefficients, published whole from the parameter path to the
/// audio thread.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Biquad"/> carries five coefficients and two delay-line samples in one struct, and
/// the two halves belong to different threads: a parameter move decides the coefficients, the audio
/// callback owns the state. Writing coefficients straight into a filter the callback is running is
/// five unordered writes, so it can read <c>a1</c> from one cutoff paired with <c>a2</c> from
/// another. That is not a slightly wrong sound — the pole pair can land outside the unit circle, and
/// then the delay line latches NaN for the rest of the stream.
/// </para>
/// <para>
/// Publishing a reference is one atomic write. The audio thread reads it once a block and copies the
/// coefficients into filters it alone owns, which is also what carries each delay line across the
/// change and so keeps a live sweep free of clicks. Coefficients cross the thread boundary; state
/// never does.
/// </para>
/// </remarks>
public sealed class BiquadCoefficients
{
    private readonly Biquad[] _stages;

    /// <summary>
    /// Takes ownership of <paramref name="stages"/> — the caller must not keep a reference, or the
    /// snapshot is no longer immutable and the whole point of it is gone.
    /// </summary>
    public BiquadCoefficients(params Biquad[] stages) => _stages = stages;

    /// <summary>One pass-through stage, for a filter that exists but has not been tuned yet.</summary>
    public static BiquadCoefficients Identity { get; } = new(Biquad.Identity());

    public int Count => _stages.Length;

    public ref readonly Biquad this[int stage] => ref _stages[stage];

    /// <summary>
    /// Copy one stage into every channel's filter, preserving each one's delay line. Call this from
    /// the thread that runs the filters, never from the one that built the coefficients.
    /// </summary>
    public void ApplyTo(Biquad[] filters, int stage = 0)
    {
        for (int channel = 0; channel < filters.Length; channel++)
            filters[channel].CopyCoefficientsFrom(_stages[stage]);
    }
}
