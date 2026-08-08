namespace WaveLab.Audio.Dsp;

/// <summary>TPDF dither at ±1 LSB for 16-bit export.</summary>
public sealed class TpdfDither
{
    private readonly Random _rng = new(0x5EED);

    /// <summary>Triangular noise in [-1, 1] LSB (already scaled for 16-bit quantization).</summary>
    public double Next() => _rng.NextDouble() - _rng.NextDouble();
}
