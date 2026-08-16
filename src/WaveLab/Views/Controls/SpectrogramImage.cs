using WaveLab.Audio.Dsp;

namespace WaveLab.Views.Controls;

/// <summary>Colour ramp for the spectrogram.</summary>
public enum SpectrogramPalette
{
    /// <summary>Perceptually uniform; the default.</summary>
    Viridis,

    /// <summary>Perceptually uniform, warmer, with a darker floor.</summary>
    Magma,

    /// <summary>The app's own dark-to-teal ramp.</summary>
    Teal,

    /// <summary>Monochrome, for judging level without hue getting in the way.</summary>
    Grey,
}

/// <summary>How a <see cref="SpectrogramData"/> is turned into pixels.</summary>
/// <param name="Palette">Colour ramp.</param>
/// <param name="MinimumFrequency">Frequency at the bottom edge.</param>
/// <param name="MaximumFrequency">Frequency at the top edge; clamped to Nyquist.</param>
/// <param name="Logarithmic">Log frequency axis, which is how pitch is actually spaced.</param>
/// <param name="Gamma">Applied to the normalized level; above 1 darkens the floor.</param>
public readonly record struct SpectrogramImageSettings(
    SpectrogramPalette Palette = SpectrogramPalette.Viridis,
    double MinimumFrequency = 20,
    double MaximumFrequency = 20_000,
    bool Logarithmic = true,
    double Gamma = 1.35)
{
    /// <remarks>Written out rather than <c>new()</c>: see <see cref="SpectrogramSettings.Default"/>.</remarks>
    public static SpectrogramImageSettings Default { get; } = new(
        Palette: SpectrogramPalette.Viridis,
        MinimumFrequency: 20,
        MaximumFrequency: 20_000,
        Logarithmic: true,
        Gamma: 1.35);
}

/// <summary>
/// Renders analysed spectra into a pixel buffer: log frequency up the side, time across, level as
/// colour.
/// </summary>
public static class SpectrogramImage
{
    // Control points sampled from the reference ramps. Perceptually uniform ramps matter here for a
    // specific reason: the usual blue-red-yellow heat maps have uneven lightness, so they invent
    // visible contours where the data is smooth, and a spectral editor is a tool for deciding
    // whether a faint thing is really there.
    private static readonly double[][] ViridisStops =
    [
        [0.267, 0.005, 0.329], [0.283, 0.141, 0.458], [0.254, 0.265, 0.530], [0.207, 0.372, 0.553],
        [0.164, 0.471, 0.558], [0.128, 0.567, 0.551], [0.135, 0.659, 0.518], [0.267, 0.749, 0.441],
        [0.478, 0.821, 0.318], [0.741, 0.873, 0.150], [0.993, 0.906, 0.144],
    ];

    private static readonly double[][] MagmaStops =
    [
        [0.001, 0.000, 0.014], [0.078, 0.054, 0.211], [0.232, 0.059, 0.437], [0.390, 0.100, 0.502],
        [0.550, 0.161, 0.506], [0.716, 0.215, 0.475], [0.868, 0.288, 0.409], [0.968, 0.443, 0.360],
        [0.995, 0.624, 0.427], [0.996, 0.788, 0.564], [0.987, 0.991, 0.749],
    ];

    private static readonly double[][] TealStops =
    [
        [0.059, 0.067, 0.078], [0.075, 0.106, 0.125], [0.086, 0.161, 0.180], [0.090, 0.227, 0.239],
        [0.098, 0.298, 0.298], [0.110, 0.376, 0.353], [0.135, 0.459, 0.424],
        [0.192, 0.549, 0.502], [0.310, 0.663, 0.612], [0.545, 0.800, 0.757], [0.878, 0.949, 0.929],
    ];

    private static readonly double[][] GreyStops =
    [
        [0.04, 0.04, 0.045], [0.14, 0.14, 0.15], [0.24, 0.24, 0.25], [0.34, 0.34, 0.35],
        [0.44, 0.44, 0.45], [0.54, 0.54, 0.55], [0.64, 0.64, 0.65], [0.74, 0.74, 0.75],
        [0.84, 0.84, 0.85], [0.92, 0.92, 0.93], [1.00, 1.00, 1.00],
    ];

    /// <summary>Samples a ramp at <paramref name="position"/> in 0..1, returning packed BGRA.</summary>
    public static uint Sample(SpectrogramPalette palette, double position)
    {
        double[][] stops = palette switch
        {
            SpectrogramPalette.Magma => MagmaStops,
            SpectrogramPalette.Teal => TealStops,
            SpectrogramPalette.Grey => GreyStops,
            _ => ViridisStops,
        };

        position = Math.Clamp(position, 0, 1);
        double scaled = position * (stops.Length - 1);
        int index = Math.Min(stops.Length - 2, (int)scaled);
        double fraction = scaled - index;
        double[] low = stops[index], high = stops[index + 1];

        byte r = Component(low[0] + (high[0] - low[0]) * fraction);
        byte g = Component(low[1] + (high[1] - low[1]) * fraction);
        byte b = Component(low[2] + (high[2] - low[2]) * fraction);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

        static byte Component(double value) => (byte)Math.Clamp(value * 255.0 + 0.5, 0, 255);
    }

    /// <summary>
    /// Draws <paramref name="data"/> into <paramref name="pixels"/> (BGRA, one uint per pixel,
    /// row-major from the top-left).
    /// </summary>
    /// <param name="floorDb">Level mapped to the bottom of the ramp.</param>
    /// <param name="ceilingDb">Level mapped to the top of the ramp.</param>
    public static void Render(SpectrogramData data, Span<uint> pixels, int width, int height,
        double floorDb, double ceilingDb, SpectrogramImageSettings settings = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (settings.MaximumFrequency <= 0) settings = SpectrogramImageSettings.Default;
        if (width <= 0 || height <= 0) return;
        if (pixels.Length < width * height)
            throw new ArgumentException("Pixel buffer is smaller than the requested image.", nameof(pixels));

        uint background = Sample(settings.Palette, 0);
        if (data.Frames <= 0 || data.Bins <= 0)
        {
            pixels[..(width * height)].Fill(background);
            return;
        }

        double nyquist = data.SampleRate / 2.0;
        double lowest = Math.Clamp(settings.MinimumFrequency, 1, nyquist - 1);
        double highest = Math.Clamp(settings.MaximumFrequency, lowest + 1, nyquist);
        double span = Math.Max(1e-9, ceilingDb - floorDb);
        double gamma = settings.Gamma > 0 ? settings.Gamma : 1;

        // Row edges in bin space, computed once: every column reduces over the same ranges.
        var rowFromBin = new int[height];
        var rowToBin = new int[height];
        double binsPerHz = data.FftSize / (double)data.SampleRate;

        for (int y = 0; y < height; y++)
        {
            // y = 0 is the top of the image and the top of the band.
            double upper = FrequencyAt(1.0 - y / (double)height);
            double lower = FrequencyAt(1.0 - (y + 1) / (double)height);
            int from = (int)Math.Floor(lower * binsPerHz);
            int to = (int)Math.Ceiling(upper * binsPerHz);
            rowFromBin[y] = Math.Clamp(from, 0, data.Bins - 1);
            rowToBin[y] = Math.Clamp(Math.Max(to, from + 1), 1, data.Bins);
        }

        for (int x = 0; x < width; x++)
        {
            int frameFrom = (int)((long)x * data.Frames / width);
            int frameTo = (int)((long)(x + 1) * data.Frames / width);
            if (frameTo <= frameFrom) frameTo = frameFrom + 1;
            frameFrom = Math.Clamp(frameFrom, 0, data.Frames - 1);
            frameTo = Math.Clamp(frameTo, frameFrom + 1, data.Frames);

            for (int y = 0; y < height; y++)
            {
                // The reduction is a maximum, not a mean. When many bins fall into one row — which
                // is most of the image on a log axis — averaging buries a single-bin partial in the
                // floor either side of it, and thin partials are exactly what this view is for.
                float peak = float.NegativeInfinity;
                for (int f = frameFrom; f < frameTo; f++)
                {
                    int row = f * data.Bins;
                    for (int b = rowFromBin[y]; b < rowToBin[y]; b++)
                    {
                        float value = data.MagnitudeDb[row + b];
                        if (value > peak) peak = value;
                    }
                }

                double normalized = (peak - floorDb) / span;
                normalized = Math.Clamp(normalized, 0, 1);
                if (gamma != 1) normalized = Math.Pow(normalized, gamma);
                pixels[y * width + x] = Sample(settings.Palette, normalized);
            }
        }

        double FrequencyAt(double fraction) => settings.Logarithmic
            ? lowest * Math.Pow(highest / lowest, fraction)
            : lowest + (highest - lowest) * fraction;
    }

    /// <summary>Image row a frequency falls on, for drawing rulers and selections over the same axis.</summary>
    public static double RowForFrequency(double frequency, int height, SpectrogramImageSettings settings,
        double nyquist)
    {
        if (settings.MaximumFrequency <= 0) settings = SpectrogramImageSettings.Default;
        double lowest = Math.Clamp(settings.MinimumFrequency, 1, nyquist - 1);
        double highest = Math.Clamp(settings.MaximumFrequency, lowest + 1, nyquist);
        frequency = Math.Clamp(frequency, lowest, highest);

        double fraction = settings.Logarithmic
            ? Math.Log(frequency / lowest) / Math.Log(highest / lowest)
            : (frequency - lowest) / (highest - lowest);
        return (1.0 - fraction) * height;
    }

    /// <summary>Inverse of <see cref="RowForFrequency"/>.</summary>
    public static double FrequencyForRow(double row, int height, SpectrogramImageSettings settings,
        double nyquist)
    {
        if (settings.MaximumFrequency <= 0) settings = SpectrogramImageSettings.Default;
        double lowest = Math.Clamp(settings.MinimumFrequency, 1, nyquist - 1);
        double highest = Math.Clamp(settings.MaximumFrequency, lowest + 1, nyquist);
        double fraction = height <= 0 ? 0 : Math.Clamp(1.0 - row / height, 0, 1);

        return settings.Logarithmic
            ? lowest * Math.Pow(highest / lowest, fraction)
            : lowest + (highest - lowest) * fraction;
    }
}
