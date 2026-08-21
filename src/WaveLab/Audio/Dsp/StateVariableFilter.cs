namespace WaveLab.Audio.Dsp;

/// <summary>Which output of the state-variable structure is taken.</summary>
public enum SvfMode
{
    LowPass,
    BandPass,
    HighPass,
    Notch,
    Peaking,
    LowShelf,
    HighShelf,
    AllPass,
}

/// <summary>
/// A topology-preserving state-variable filter, for cutoffs and gains that move while running.
/// </summary>
/// <remarks>
/// <para>
/// The RBJ biquads elsewhere in this app are the right tool for a filter that is set once. They are
/// the wrong tool for one that is <em>modulated</em>: their coefficients are derived through the
/// bilinear transform, and the delay-line state that goes with one set of coefficients does not mean
/// the same thing under another. Change them while audio is flowing and the state is reinterpreted,
/// which is heard as a click on a fast move and a zipper on a slow one — and at high Q the poles can
/// leave the unit circle mid-transition and latch.
/// </para>
/// <para>
/// A topology-preserving transform keeps the analog structure's integrators explicit, so the state
/// is a voltage on a capacitor rather than a coefficient-dependent history. Changing the cutoff
/// between samples is then exactly what turning a knob on the analog original does: the state stays
/// meaningful, the filter stays stable, and nothing clicks. That is what a dynamic equaliser needs,
/// because its gain moves with the music by design.
/// </para>
/// <para>
/// All the outputs are available from the same two integrators, so a mode change costs nothing and
/// shares state — the structure computes low, band and high simultaneously and the mode only selects
/// how they are mixed.
/// </para>
/// </remarks>
public struct StateVariableFilter
{
    private double _ic1, _ic2;      // integrator state
    private double _g, _k, _a1, _a2, _a3;
    private double _m0, _m1, _m2;

    /// <summary>Clears the integrators without disturbing the tuning.</summary>
    public void Reset()
    {
        _ic1 = 0;
        _ic2 = 0;
    }

    /// <summary>
    /// Retunes in place. Safe to call between any two samples — that is the entire point of the
    /// structure, and the reason a dynamic equaliser can be built on it at all.
    /// </summary>
    public void Set(SvfMode mode, double sampleRate, double frequency, double q, double gainDb = 0)
    {
        double nyquist = sampleRate * 0.5;
        frequency = Math.Clamp(frequency, 1, nyquist * 0.99);
        q = Math.Max(q, 0.025);

        // The prewarped integrator gain: tan maps the analog frequency onto the digital one so the
        // corner lands where it was asked for rather than where the bilinear transform put it.
        double g = Math.Tan(Math.PI * frequency / sampleRate);
        double a = Math.Pow(10, gainDb / 40.0);

        switch (mode)
        {
            case SvfMode.Peaking:
                _g = g;
                _k = 1 / (q * a);
                (_m0, _m1, _m2) = (1, _k * (a * a - 1), 0);
                break;

            case SvfMode.LowShelf:
                _g = g / Math.Sqrt(a);
                _k = 1 / q;
                (_m0, _m1, _m2) = (1, _k * (a - 1), a * a - 1);
                break;

            case SvfMode.HighShelf:
                _g = g * Math.Sqrt(a);
                _k = 1 / q;
                (_m0, _m1, _m2) = (a * a, _k * (1 - a) * a, 1 - a * a);
                break;

            case SvfMode.BandPass:
                _g = g;
                _k = 1 / q;
                (_m0, _m1, _m2) = (0, 1, 0);
                break;

            case SvfMode.HighPass:
                _g = g;
                _k = 1 / q;
                (_m0, _m1, _m2) = (1, -_k, -1);
                break;

            case SvfMode.Notch:
                _g = g;
                _k = 1 / q;
                (_m0, _m1, _m2) = (1, -_k, 0);
                break;

            case SvfMode.AllPass:
                _g = g;
                _k = 1 / q;
                (_m0, _m1, _m2) = (1, -2 * _k, 0);
                break;

            default:
                _g = g;
                _k = 1 / q;
                (_m0, _m1, _m2) = (0, 0, 1);
                break;
        }

        _a1 = 1 / (1 + _g * (_g + _k));
        _a2 = _g * _a1;
        _a3 = _g * _a2;
    }

    /// <summary>Processes one sample.</summary>
    public float Process(float input)
    {
        double v3 = input - _ic2;
        double v1 = _a1 * _ic1 + _a2 * v3;
        double v2 = _ic2 + _a2 * _ic1 + _a3 * v3;

        _ic1 = 2 * v1 - _ic1;
        _ic2 = 2 * v2 - _ic2;

        return (float)(_m0 * input + _m1 * v1 + _m2 * v2);
    }

    /// <summary>
    /// Magnitude response in dB at a frequency, measured by running a tone through a copy of this
    /// filter rather than modelled.
    /// </summary>
    /// <remarks>
    /// Deliberately measured. A hand-derived transfer function is a second implementation of the
    /// filter that has to be kept in step with the first, and when the two disagree the drawn curve
    /// is the one that gets believed. Running the actual structure cannot disagree with itself.
    /// </para>
    /// <para>
    /// <b>Nothing calls this.</b> Measuring costs <c>settle + measure</c> filter evaluations and as
    /// many sine evaluations per frequency point — over five thousand — so a curve drawn at a few
    /// hundred points is millions of operations per repaint. That is affordable once, cached; it is
    /// not affordable per frame. Anything adopting it should measure a coarse grid off the UI thread
    /// and interpolate, the way the spectrogram does.
    /// </remarks>
    public readonly double MagnitudeDb(double frequency, double sampleRate, int settle = 4_096)
    {
        StateVariableFilter probe = this;
        probe.Reset();

        double re = 0, im = 0, weight = 0;
        int measure = Math.Max(1024, settle);
        for (int i = 0; i < settle + measure; i++)
        {
            double omega = 2 * Math.PI * frequency * i / sampleRate;
            float output = probe.Process((float)Math.Sin(omega));
            if (i < settle) continue;

            int n = i - settle;
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / measure);
            re += output * window * Math.Cos(omega);
            im -= output * window * Math.Sin(omega);
            weight += window;
        }

        return 20 * Math.Log10(Math.Max(Math.Sqrt(re * re + im * im) / Math.Max(1, weight) * 2, 1e-12));
    }
}
