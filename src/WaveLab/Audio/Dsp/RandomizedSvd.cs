namespace WaveLab.Audio.Dsp;

/// <summary>
/// The top few singular triplets of a matrix, by the randomized range finder of Halko, Martinsson
/// and Tropp.
/// </summary>
/// <remarks>
/// <para>
/// A full singular value decomposition of a spectrogram is a Golub–Kahan bidiagonalisation followed
/// by a QR sweep, and .NET ships neither. It also computes hundreds of singular values in order to
/// use a dozen. The randomized method computes only what is asked for: project the matrix onto a
/// small random subspace, orthonormalise, and decompose the little matrix that results. The error is
/// bounded in expectation by a factor of the first singular value it did <em>not</em> take, which for
/// a matrix that is genuinely low rank plus noise is exactly the part nobody wanted.
/// </para>
/// <para>
/// <b>Power iterations are what make it work on audio.</b> A spectrogram's singular values decay
/// slowly, and a plain random projection on a slowly-decaying spectrum mixes the directions it was
/// meant to separate. Multiplying by <c>AᵀA</c> a couple of times raises the spectrum to an odd
/// power, which drives the ratio between the wanted and unwanted directions apart without changing
/// the singular vectors at all. Each pass is re-orthonormalised, because the whole point of raising
/// the spectrum is that it also destroys the numerical independence of the columns.
/// </para>
/// <para>
/// The small decomposition is done as a symmetric eigenproblem on <c>BBᵀ</c> by cyclic Jacobi, which
/// is unconditionally convergent, needs no pivoting strategy and is a page of code. Squaring costs
/// half the digits of the small singular values — irrelevant here, where the small ones are being
/// thresholded to zero anyway.
/// </para>
/// </remarks>
internal static class RandomizedSvd
{
    /// <summary>Extra columns taken beyond the requested rank, so the projection has room to miss.</summary>
    public const int DefaultOversampling = 8;

    /// <summary>How many times the spectrum is driven apart before the basis is taken.</summary>
    public const int DefaultPowerIterations = 2;

    /// <summary>
    /// The leading singular triplets of a row-major matrix.
    /// </summary>
    /// <param name="a">The matrix, <paramref name="rows"/> × <paramref name="cols"/>, row-major.</param>
    /// <param name="rank">How many triplets to return. Clamped to what the matrix can hold.</param>
    /// <returns>
    /// <c>U</c> is rows × k column-major-free (row-major, k columns), <c>S</c> is k singular values
    /// in descending order, and <c>V</c> is cols × k. The product <c>U·diag(S)·Vᵀ</c> is the best
    /// rank-k approximation this method found.
    /// </returns>
    public static (double[] U, double[] S, double[] V, int K) Compute(
        double[] a, int rows, int cols, int rank,
        int oversampling = DefaultOversampling, int powerIterations = DefaultPowerIterations,
        int seed = 0x5EED)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (rows <= 0 || cols <= 0 || a.Length < (long)rows * cols)
            throw new ArgumentException("The matrix is smaller than its stated shape.", nameof(a));

        int limit = Math.Min(rows, cols);
        int sketch = Math.Clamp(Math.Max(1, rank) + Math.Max(0, oversampling), 1, limit);

        // Y = A·Ω, the matrix seen through a random subspace.
        var y = new double[(long)rows * sketch <= int.MaxValue ? rows * sketch : 0];
        if (y.Length == 0) throw new ArgumentException("The matrix is too large to sketch.", nameof(a));

        var random = new Random(seed);
        var omega = new double[cols * sketch];
        for (int i = 0; i < omega.Length; i++) omega[i] = Gaussian(random);

        Multiply(a, rows, cols, omega, sketch, y);
        Orthonormalize(y, rows, sketch);

        var z = new double[cols * sketch];
        for (int q = 0; q < Math.Max(0, powerIterations); q++)
        {
            MultiplyTranspose(a, rows, cols, y, sketch, z);
            Orthonormalize(z, cols, sketch);
            Multiply(a, rows, cols, z, sketch, y);
            Orthonormalize(y, rows, sketch);
        }

        // B = Qᵀ·A, the whole matrix expressed in the small basis.
        var b = new double[sketch * cols];
        for (int i = 0; i < sketch; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                double sum = 0;
                for (int r = 0; r < rows; r++) sum += y[r * sketch + i] * a[r * cols + j];
                b[i * cols + j] = sum;
            }
        }

        // C = B·Bᵀ, small and symmetric; its eigenvectors are B's left singular vectors.
        var c = new double[sketch * sketch];
        for (int i = 0; i < sketch; i++)
        {
            for (int j = i; j < sketch; j++)
            {
                double sum = 0;
                for (int t = 0; t < cols; t++) sum += b[i * cols + t] * b[j * cols + t];
                c[i * sketch + j] = sum;
                c[j * sketch + i] = sum;
            }
        }

        (double[] eigenvalues, double[] eigenvectors) = JacobiEigen(c, sketch);

        // Descending, because a singular value decomposition is expected in that order and the
        // caller thresholds from the top.
        int[] order = [.. Enumerable.Range(0, sketch).OrderByDescending(i => eigenvalues[i])];
        int k = Math.Clamp(rank, 1, sketch);

        var s = new double[k];
        var u = new double[rows * k];
        var v = new double[cols * k];

        for (int t = 0; t < k; t++)
        {
            int e = order[t];
            double value = Math.Sqrt(Math.Max(0, eigenvalues[e]));
            s[t] = value;

            // U = Q·W: the small basis's eigenvector lifted back into the original row space.
            for (int r = 0; r < rows; r++)
            {
                double sum = 0;
                for (int i = 0; i < sketch; i++) sum += y[r * sketch + i] * eigenvectors[i * sketch + e];
                u[r * k + t] = sum;
            }

            // V = Bᵀ·W / σ. A singular value at zero has no direction to speak of, so its column is
            // left at zero rather than divided into existence.
            if (value > 1e-300)
            {
                double inverse = 1 / value;
                for (int j = 0; j < cols; j++)
                {
                    double sum = 0;
                    for (int i = 0; i < sketch; i++) sum += b[i * cols + j] * eigenvectors[i * sketch + e];
                    v[j * k + t] = sum * inverse;
                }
            }
        }

        return (u, s, v, k);
    }

    /// <summary>The largest singular value, which is what an ALM iteration scales its step by.</summary>
    public static double SpectralNorm(double[] a, int rows, int cols, int iterations = 12,
        int seed = 0x5EED)
    {
        var random = new Random(seed);
        var vector = new double[cols];
        for (int i = 0; i < cols; i++) vector[i] = Gaussian(random);

        var work = new double[rows];
        double norm = 0;
        for (int t = 0; t < Math.Max(1, iterations); t++)
        {
            // work = A·v
            for (int r = 0; r < rows; r++)
            {
                double sum = 0;
                int row = r * cols;
                for (int j = 0; j < cols; j++) sum += a[row + j] * vector[j];
                work[r] = sum;
            }

            // v = Aᵀ·work, then normalise; the length is the squared singular value.
            Array.Clear(vector);
            for (int r = 0; r < rows; r++)
            {
                double value = work[r];
                if (value == 0) continue;
                int row = r * cols;
                for (int j = 0; j < cols; j++) vector[j] += a[row + j] * value;
            }

            double length = 0;
            foreach (double value in vector) length += value * value;
            length = Math.Sqrt(length);
            if (!(length > 0)) return 0;

            double inverse = 1 / length;
            for (int j = 0; j < cols; j++) vector[j] *= inverse;
            norm = Math.Sqrt(length);
        }
        return norm;
    }

    // ── the pieces ───────────────────────────────────────────────

    /// <summary>Box–Muller, because <see cref="Random"/> offers no normal deviate.</summary>
    private static double Gaussian(Random random)
    {
        double u = 1 - random.NextDouble();
        double v = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    /// <summary>result = A·B, with A rows×cols row-major and B cols×k row-major.</summary>
    private static void Multiply(double[] a, int rows, int cols, double[] b, int k, double[] result)
    {
        Array.Clear(result, 0, rows * k);
        Parallel.For(0, rows, r =>
        {
            int aRow = r * cols, outRow = r * k;
            for (int j = 0; j < cols; j++)
            {
                double value = a[aRow + j];
                if (value == 0) continue;
                int bRow = j * k;
                for (int i = 0; i < k; i++) result[outRow + i] += value * b[bRow + i];
            }
        });
    }

    /// <summary>result = Aᵀ·B, with A rows×cols row-major and B rows×k row-major.</summary>
    private static void MultiplyTranspose(double[] a, int rows, int cols, double[] b, int k,
        double[] result)
    {
        Array.Clear(result, 0, cols * k);
        Parallel.For(0, cols, j =>
        {
            for (int r = 0; r < rows; r++)
            {
                double value = a[r * cols + j];
                if (value == 0) continue;
                int bRow = r * k;
                int outRow = j * k;
                for (int i = 0; i < k; i++) result[outRow + i] += value * b[bRow + i];
            }
        });
    }

    /// <summary>
    /// Makes the columns of an <c>n×k</c> matrix orthonormal, in place, by modified Gram–Schmidt run
    /// twice.
    /// </summary>
    /// <remarks>
    /// Twice is not superstition. One pass of modified Gram–Schmidt loses orthogonality in
    /// proportion to the matrix's condition number, and the whole purpose of the power iterations
    /// above is to make the columns nearly parallel — which is to say, to make it badly conditioned
    /// on purpose. A second pass restores orthogonality to machine precision, and is the standard
    /// answer.
    /// </remarks>
    private static void Orthonormalize(double[] matrix, int n, int k)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    double dot = 0;
                    for (int r = 0; r < n; r++) dot += matrix[r * k + i] * matrix[r * k + j];
                    if (dot == 0) continue;
                    for (int r = 0; r < n; r++) matrix[r * k + i] -= dot * matrix[r * k + j];
                }

                double length = 0;
                for (int r = 0; r < n; r++) length += matrix[r * k + i] * matrix[r * k + i];
                length = Math.Sqrt(length);

                if (length > 1e-12)
                {
                    double inverse = 1 / length;
                    for (int r = 0; r < n; r++) matrix[r * k + i] *= inverse;
                }
                else
                {
                    // A column that collapsed carries no direction. Zeroing it is honest; leaving
                    // the noise in it would hand the decomposition a basis vector made of round-off.
                    for (int r = 0; r < n; r++) matrix[r * k + i] = 0;
                }
            }
        }
    }

    /// <summary>
    /// Eigenvalues and eigenvectors of a small symmetric matrix, by cyclic Jacobi rotations.
    /// </summary>
    /// <remarks>
    /// Unconditionally convergent, needs no shift strategy, and gets the small eigenvalues to high
    /// relative accuracy — which the QR algorithm does not. It is quadratic in the dimension per
    /// sweep and the dimension here is the sketch size, a few dozen at most.
    /// </remarks>
    private static (double[] Values, double[] Vectors) JacobiEigen(double[] matrix, int n)
    {
        var a = (double[])matrix.Clone();
        var vectors = new double[n * n];
        for (int i = 0; i < n; i++) vectors[i * n + i] = 1;

        for (int sweep = 0; sweep < 60; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                    off += a[p * n + q] * a[p * n + q];

            if (off <= 1e-30) break;

            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p * n + q];
                    if (Math.Abs(apq) <= 1e-300) continue;

                    double app = a[p * n + p], aqq = a[q * n + q];
                    double theta = (aqq - app) / (2 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;

                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;

                    for (int i = 0; i < n; i++)
                    {
                        double aip = a[i * n + p], aiq = a[i * n + q];
                        a[i * n + p] = c * aip - s * aiq;
                        a[i * n + q] = s * aip + c * aiq;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double api = a[p * n + i], aqi = a[q * n + i];
                        a[p * n + i] = c * api - s * aqi;
                        a[q * n + i] = s * api + c * aqi;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double vip = vectors[i * n + p], viq = vectors[i * n + q];
                        vectors[i * n + p] = c * vip - s * viq;
                        vectors[i * n + q] = s * vip + c * viq;
                    }
                }
            }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i * n + i];
        return (values, vectors);
    }
}
