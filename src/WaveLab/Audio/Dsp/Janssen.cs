namespace WaveLab.Audio.Dsp;

/// <summary>Tuning for <see cref="Janssen"/>.</summary>
/// <param name="ContextLength">Clean samples to fit the model from, on each side of the gap.</param>
/// <param name="Order">Autoregressive model order.</param>
/// <param name="Iterations">Maximum model/sample refinement passes.</param>
/// <param name="OutputLimit">Largest magnitude the reconstruction may reach.</param>
public readonly record struct JanssenOptions(int ContextLength, int Order, int Iterations, double OutputLimit)
{
    /// <summary>Defaults scaled to the gap.</summary>
    /// <remarks>
    /// <para>
    /// Two rules, both structural rather than fitted. The order must be large enough to describe
    /// every partial that has to be carried across the gap — measured against five-partial material,
    /// a 220-sample gap scores 4 dB at order 16 and around 47 dB at order 128 — and it scales with
    /// the gap because the solve costs grow with it and short clicks vastly outnumber long ones. The
    /// context must then be substantially larger than the order, or the autocorrelation is estimated
    /// from too little data to support a model that size.
    /// </para>
    /// <para>
    /// These are deliberately <em>not</em> tuned to squeeze the last decibel out of synthetic tones.
    /// On perfectly periodic test material the measured quality swings by 20 dB for a 12% change in
    /// context, because the fit becomes sensitive to how many whole periods land in the window —
    /// an artefact of the test signal, not a property of real audio. Chasing it would be fitting to
    /// measurement noise.
    /// </para>
    /// </remarks>
    public static JanssenOptions For(int gapLength, double outputLimit)
    {
        int order = Math.Clamp(gapLength * 3, 48, 256);
        return new JanssenOptions(
            ContextLength: Math.Clamp(order * 8, 512, 2048),
            Order: order,
            Iterations: 4,
            OutputLimit: outputLimit);
    }
}

/// <summary>
/// Janssen's iterative autoregressive interpolation of missing samples
/// (Janssen, Veldhuis &amp; Vries, 1986) — the method behind professional de-click.
/// </summary>
/// <remarks>
/// <para>
/// The difference from the bidirectional extrapolation this sits beside is that the model is fitted
/// to the whole signal <em>including</em> the samples being estimated, and the two are refined
/// together. Extrapolating inward from each side and cross-fading, as
/// <c>TryBidirectionalLinearPrediction</c> does, fits two models that have never seen the gap and
/// then blends two guesses that disagree in the middle; the audible result on a sustained note is a
/// short dip in level and a smeared phase. Solving jointly makes the reconstruction a stationary
/// point of the same prediction error the model itself is measured by, so the gap is filled with
/// what the surrounding waveform implies rather than with a fade between two extrapolations.
/// </para>
/// <para>
/// Each pass estimates an AR model, then solves for the missing samples that minimise the residual
/// under it. The normal equations reduce to <c>Σ_j A[m-j]·x[j] = 0</c> over the missing positions,
/// where <c>A</c> is the autocorrelation of the prediction-error filter — a small symmetric
/// positive-definite system, one per gap.
/// </para>
/// </remarks>
public static class Janssen
{
    /// <summary>Beyond this the solve costs more than the repair is worth; callers fall back.</summary>
    public const int MaximumGapLength = 2048;

    /// <summary>
    /// Estimates <paramref name="samples"/> over <c>[gapStart, gapEnd)</c> from the audio either side.
    /// </summary>
    /// <returns>False when the geometry or the audio cannot support a fit; the caller should fall back.</returns>
    public static bool TryInterpolate(float[] samples, int gapStart, int gapEnd,
        JanssenOptions options, out double[] reconstruction)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int gapLength = gapEnd - gapStart;
        reconstruction = gapLength > 0 ? new double[gapLength] : [];
        if (gapLength <= 0 || gapLength > MaximumGapLength) return false;
        if (gapStart <= 0 || gapEnd >= samples.Length) return false;

        int context = Math.Max(16, options.ContextLength);
        int from = Math.Max(0, gapStart - context);
        int to = Math.Min(samples.Length, gapEnd + context);
        int length = to - from;
        int gapOffset = gapStart - from;

        // The fit needs appreciably more clean audio than the model has coefficients, or the
        // autocorrelation is estimated from too little to mean anything.
        int order = Math.Min(options.Order, (length - gapLength) / 3);
        if (order < 8 || length <= gapLength + 2 * order) return false;

        var signal = new double[length];
        double mean = 0;
        int known = 0;
        for (int i = 0; i < length; i++)
        {
            double value = samples[from + i];
            if (!double.IsFinite(value)) return false;
            signal[i] = value;
            if (i < gapOffset || i >= gapOffset + gapLength) { mean += value; known++; }
        }
        if (known == 0) return false;
        mean /= known;
        for (int i = 0; i < length; i++) signal[i] -= mean;

        // Start from a straight line across the gap; the first model pass then has something
        // continuous to fit rather than a hole.
        double left = signal[gapOffset - 1];
        double right = signal[gapOffset + gapLength];
        for (int i = 0; i < gapLength; i++)
            signal[gapOffset + i] = left + (right - left) * (i + 1.0) / (gapLength + 1.0);

        var filter = new double[order + 1];
        var filterAutocorrelation = new double[order + 1];
        var matrix = new double[gapLength * gapLength];
        var rightHandSide = new double[gapLength];
        var previous = new double[gapLength];

        for (int iteration = 0; iteration < Math.Max(1, options.Iterations); iteration++)
        {
            if (!TryFitModel(signal, length, order, filter)) return iteration > 0;

            for (int lag = 0; lag <= order; lag++)
            {
                double sum = 0;
                for (int k = 0; k + lag <= order; k++) sum += filter[k] * filter[k + lag];
                filterAutocorrelation[lag] = sum;
            }

            for (int i = 0; i < gapLength; i++)
            {
                int m = gapOffset + i;
                for (int j = 0; j < gapLength; j++)
                {
                    int lag = Math.Abs(m - (gapOffset + j));
                    matrix[i * gapLength + j] = lag <= order ? filterAutocorrelation[lag] : 0;
                }

                double sum = 0;
                int lowest = Math.Max(0, m - order);
                int highest = Math.Min(length - 1, m + order);
                for (int j = lowest; j <= highest; j++)
                {
                    if (j >= gapOffset && j < gapOffset + gapLength) continue;   // unknown
                    sum += filterAutocorrelation[Math.Abs(m - j)] * signal[j];
                }
                rightHandSide[i] = -sum;
            }

            Array.Copy(signal, gapOffset, previous, 0, gapLength);
            if (!TrySolveSymmetric(matrix, rightHandSide, gapLength)) return iteration > 0;

            double change = 0, scale = 1e-12;
            for (int i = 0; i < gapLength; i++)
            {
                double value = rightHandSide[i];
                if (!double.IsFinite(value) || Math.Abs(value + mean) > options.OutputLimit * 4)
                {
                    Array.Copy(previous, 0, signal, gapOffset, gapLength);
                    return iteration > 0;
                }
                change = Math.Max(change, Math.Abs(value - previous[i]));
                scale = Math.Max(scale, Math.Abs(value));
                signal[gapOffset + i] = value;
            }

            // The model and the samples stop moving each other well before the iteration cap on
            // ordinary material; stopping there saves most of the cost on a dense click population.
            if (change <= scale * 1e-3) break;
        }

        for (int i = 0; i < gapLength; i++)
            reconstruction[i] = signal[gapOffset + i] + mean;
        return true;
    }

    /// <summary>
    /// Autocorrelation-method AR fit by Levinson-Durbin, returning the prediction-error filter with
    /// <c>filter[0] == 1</c>.
    /// </summary>
    private static bool TryFitModel(double[] signal, int length, int order, double[] filter)
    {
        Span<double> autocorrelation = order + 1 <= 512 ? stackalloc double[order + 1] : new double[order + 1];
        for (int lag = 0; lag <= order; lag++)
        {
            double sum = 0;
            for (int i = lag; i < length; i++) sum += signal[i] * signal[i - lag];
            autocorrelation[lag] = sum;
        }

        double power = autocorrelation[0];
        if (!double.IsFinite(power) || power <= 1e-20) return false;

        // White-noise correction: lifts the condition number away from singular on material that is
        // very nearly periodic, which is exactly the material this is used on.
        autocorrelation[0] = power * 1.0000001 + 1e-12;

        Array.Clear(filter);
        filter[0] = 1;
        var scratch = new double[order + 1];
        double error = autocorrelation[0];
        int fitted = 0;

        for (int current = 1; current <= order; current++)
        {
            double residual = autocorrelation[current];
            for (int i = 1; i < current; i++) residual += filter[i] * autocorrelation[current - i];

            double reflection = -residual / error;
            if (!double.IsFinite(reflection)) break;
            // Keeping the reflection coefficients inside the unit circle keeps the model stable; the
            // gap is then filled by a decaying resonance rather than a diverging one.
            reflection = Math.Clamp(reflection, -0.999, 0.999);

            Array.Copy(filter, scratch, order + 1);
            for (int i = 1; i < current; i++)
                scratch[i] = filter[i] + reflection * filter[current - i];
            scratch[current] = reflection;
            Array.Copy(scratch, filter, order + 1);

            error *= 1.0 - reflection * reflection;
            fitted = current;
            if (!double.IsFinite(error) || error <= autocorrelation[0] * 1e-12) break;
        }

        return fitted >= 4;
    }

    /// <summary>
    /// Cholesky solve of a symmetric positive-definite system, in place; the solution replaces
    /// <paramref name="vector"/>. The matrix is the Gram matrix of a filter, so it is definite by
    /// construction — a failure here means the fit degenerated and the caller should fall back.
    /// </summary>
    private static bool TrySolveSymmetric(double[] matrix, double[] vector, int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = matrix[i * n + j];
                for (int k = 0; k < j; k++) sum -= matrix[i * n + k] * matrix[j * n + k];

                if (i == j)
                {
                    if (!double.IsFinite(sum) || sum <= 1e-18) return false;
                    matrix[i * n + i] = Math.Sqrt(sum);
                }
                else
                {
                    matrix[i * n + j] = sum / matrix[j * n + j];
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            double sum = vector[i];
            for (int k = 0; k < i; k++) sum -= matrix[i * n + k] * vector[k];
            vector[i] = sum / matrix[i * n + i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = vector[i];
            for (int k = i + 1; k < n; k++) sum -= matrix[k * n + i] * vector[k];
            vector[i] = sum / matrix[i * n + i];
        }
        return true;
    }
}
