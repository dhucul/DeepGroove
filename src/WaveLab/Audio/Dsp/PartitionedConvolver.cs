namespace WaveLab.Audio.Dsp;

/// <summary>
/// Uniformly-partitioned overlap-save convolution: long kernels at short latency.
/// </summary>
/// <remarks>
/// <para>
/// Convolving directly costs one multiply per tap per sample, which for a kernel of any useful
/// length is hopeless in real time. Doing it in one FFT is cheap but forces the whole kernel's worth
/// of latency, because no output can be produced until a full block of input has arrived — for an
/// eight-thousand-tap linear-phase equaliser that is a fifth of a second before anything comes out.
/// </para>
/// <para>
/// Partitioning splits the difference. The kernel is cut into equal blocks, each transformed once at
/// setup; the input is transformed one block at a time and multiplied against every partition, with
/// the results accumulated into a frequency-domain delay line. Latency is then one <em>block</em>
/// rather than one kernel, while the arithmetic stays logarithmic. This is Gardner's method, and it
/// is what makes a long linear-phase filter usable on a live path at all.
/// </para>
/// <para>
/// Overlap-<em>save</em> rather than overlap-add: each transform is of twice the block length with
/// the previous block still in the first half, and the first half of the result — the part corrupted
/// by circular wrap-around — is discarded. It avoids keeping a separate tail buffer and is the
/// natural fit for a partitioned scheme, where the tail is already represented by the delay line.
/// </para>
/// </remarks>
public sealed class PartitionedConvolver
{
    private readonly int _blockSize;
    private readonly int _fftSize;
    private readonly int _bins;
    private readonly int _partitions;
    private readonly int _channels;

    private readonly float[][] _kernelRe;   // [partition][bin]
    private readonly float[][] _kernelIm;

    private readonly float[][][] _historyRe; // [channel][slot][bin]
    private readonly float[][][] _historyIm;
    private readonly int[] _slot;
    private readonly float[][] _overlap;     // [channel][blockSize] — the previous input block

    private readonly float[] _scratch;
    private readonly float[] _spectrumRe;
    private readonly float[] _spectrumIm;
    private readonly float[] _accumulateRe;
    private readonly float[] _accumulateIm;

    /// <summary>
    /// None. Given a block, this returns that block's output — the delay in a streaming user comes
    /// from having to accumulate a block before calling, and belongs to whoever does the
    /// accumulating rather than here.
    /// </summary>
    public int LatencySamples => 0;

    public int BlockSize => _blockSize;
    public int Partitions => _partitions;

    public PartitionedConvolver(float[] kernel, int channels, int blockSize = 256)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (blockSize < 16) throw new ArgumentOutOfRangeException(nameof(blockSize));

        _blockSize = Fft.NextPowerOfTwo(blockSize);
        _fftSize = _blockSize * 2;
        _bins = _fftSize / 2 + 1;
        _channels = channels;
        _partitions = Math.Max(1, (kernel.Length + _blockSize - 1) / _blockSize);

        _kernelRe = new float[_partitions][];
        _kernelIm = new float[_partitions][];
        var padded = new float[_fftSize];
        for (int p = 0; p < _partitions; p++)
        {
            Array.Clear(padded);
            int from = p * _blockSize;
            int take = Math.Min(_blockSize, kernel.Length - from);
            if (take > 0) kernel.AsSpan(from, take).CopyTo(padded);

            _kernelRe[p] = new float[_bins];
            _kernelIm[p] = new float[_bins];
            Fft.RealForward(padded, _kernelRe[p], _kernelIm[p]);
        }

        _historyRe = new float[channels][][];
        _historyIm = new float[channels][][];
        _overlap = new float[channels][];
        _slot = new int[channels];
        for (int c = 0; c < channels; c++)
        {
            _historyRe[c] = new float[_partitions][];
            _historyIm[c] = new float[_partitions][];
            for (int p = 0; p < _partitions; p++)
            {
                _historyRe[c][p] = new float[_bins];
                _historyIm[c][p] = new float[_bins];
            }
            _overlap[c] = new float[_blockSize];
        }

        _scratch = new float[_fftSize];
        _spectrumRe = new float[_bins];
        _spectrumIm = new float[_bins];
        _accumulateRe = new float[_bins];
        _accumulateIm = new float[_bins];
    }

    /// <summary>
    /// Convolves exactly one block of one channel. <paramref name="block"/> must be
    /// <see cref="BlockSize"/> long and is replaced by the result.
    /// </summary>
    public void ProcessBlock(int channel, Span<float> block)
    {
        if (block.Length != _blockSize)
            throw new ArgumentException($"A block must be exactly {_blockSize} samples.", nameof(block));

        // Overlap-save: this transform covers the previous block and this one, and the first half of
        // the result is thrown away as the part circular convolution corrupts.
        _overlap[channel].CopyTo(_scratch, 0);
        block.CopyTo(_scratch.AsSpan(_blockSize));
        block.CopyTo(_overlap[channel]);

        Fft.RealForward(_scratch, _spectrumRe, _spectrumIm);

        int slot = _slot[channel];
        _spectrumRe.CopyTo(_historyRe[channel][slot], 0);
        _spectrumIm.CopyTo(_historyIm[channel][slot], 0);

        Array.Clear(_accumulateRe);
        Array.Clear(_accumulateIm);
        for (int p = 0; p < _partitions; p++)
        {
            // Partition p multiplies the input from p blocks ago, which is what the ring gives.
            int index = slot - p;
            if (index < 0) index += _partitions;

            float[] inputRe = _historyRe[channel][index];
            float[] inputIm = _historyIm[channel][index];
            float[] filterRe = _kernelRe[p];
            float[] filterIm = _kernelIm[p];

            for (int b = 0; b < _bins; b++)
            {
                _accumulateRe[b] += inputRe[b] * filterRe[b] - inputIm[b] * filterIm[b];
                _accumulateIm[b] += inputRe[b] * filterIm[b] + inputIm[b] * filterRe[b];
            }
        }

        _slot[channel] = slot + 1 >= _partitions ? 0 : slot + 1;

        Fft.RealInverse(_accumulateRe, _accumulateIm, _scratch);
        _scratch.AsSpan(_blockSize, _blockSize).CopyTo(block);
    }

    public void Reset()
    {
        for (int c = 0; c < _channels; c++)
        {
            for (int p = 0; p < _partitions; p++)
            {
                Array.Clear(_historyRe[c][p]);
                Array.Clear(_historyIm[c][p]);
            }
            Array.Clear(_overlap[c]);
            _slot[c] = 0;
        }
    }
}
