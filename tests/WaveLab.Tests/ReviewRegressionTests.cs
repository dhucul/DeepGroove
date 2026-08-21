using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Pins the sequencing and edge-case defects a review of the timing-critical paths found.
/// </summary>
/// <remarks>
/// Named for the behaviour rather than the fix, in the style of <see cref="AuditRegressionTests"/>.
/// A test cannot prove the absence of a race, so the concurrency ones state the invariant each
/// race violated instead: the chain lock is free while an effect resets, the metering ring reads
/// back what was written, the pyramid's version moves with the data it describes.
/// </remarks>
public sealed class ReviewRegressionTests
{
    private static float[][] Ramp(int channels, int frames, float scale = 1)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++) data[c][i] = scale * (c + 1) * (i % 97) / 97f;
        }
        return data;
    }

    // ── MasterSection: removing an effect must not reset it under the audio lock ──

    /// <summary>
    /// RemoveEffect cleared the effect's state inside the lock Read holds for the whole chain,
    /// so dropping a convolution reverb mid-playback parked the render thread on a multi-megabyte
    /// buffer clear. The effect still has to come out reset, because it may be added back.
    /// </summary>
    [Fact]
    public void RemovingAnEffectResetsItWithTheChainLockAlreadyReleased()
    {
        var master = new MasterSection();
        var effect = new ResetProbeEffect { Owner = master };
        master.ReplaceChain([.. master.ChainSnapshot, effect]);

        Assert.True(master.RemoveEffect(effect));
        Assert.Equal(1, effect.ResetCount);
        Assert.True(effect.AnotherThreadReachedTheChainDuringReset);
        Assert.DoesNotContain(effect, master.ChainSnapshot);
    }

    /// <summary>An effect that was never in the chain is not reset and reports no removal.</summary>
    [Fact]
    public void RemovingAnEffectThatIsNotInTheChainDoesNothing()
    {
        var master = new MasterSection();
        var effect = new ResetProbeEffect();

        Assert.False(master.RemoveEffect(effect));
        Assert.Equal(0, effect.ResetCount);
    }

    // ── MasterSection: the metering ring ─────────────────────────

    /// <summary>
    /// The scope/goniometer history is written without a lock now, so the masked index has to
    /// wrap exactly as the modulo did, including when the caller asks for more than the ring
    /// holds. That case is what used to index backwards out of the buffer.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(16384)]
    [InlineData(40000)]
    public void TheMeteringRingReturnsTheMostRecentSamplesAtEveryRequestSize(int requested)
    {
        var master = new MasterSection();
        master.RackEnabled = false;

        // 20 000 frames through a 16 384-frame ring: the write index wraps, and the read has to
        // follow it rather than restarting from zero.
        const int frames = 20000;
        var source = new float[2][];
        source[0] = new float[frames];
        source[1] = new float[frames];
        for (int i = 0; i < frames; i++) source[0][i] = source[1][i] = i / (float)frames;
        master.SetSource(new ArraySampleProvider(source, 48000));

        var buffer = new float[4096 * 2];
        while (master.Read(buffer, 0, buffer.Length) > 0) { }

        var mono = new float[requested];
        master.CopyLatest(mono);
        var left = new float[requested];
        var right = new float[requested];
        master.CopyLatestStereo(left, right);

        int held = Math.Min(requested, 16384);
        // The newest sample sits at the end of the retained window, and anything the ring could
        // not hold is zero-filled past it rather than wrapped round onto stale audio.
        Assert.Equal((frames - 1) / (float)frames, mono[held - 1], 3);
        Assert.Equal((frames - 1) / (float)frames, left[held - 1], 3);
        Assert.Equal((frames - 1) / (float)frames, right[held - 1], 3);
        Assert.Equal((frames - held) / (float)frames, mono[0], 3);
        for (int i = held; i < requested; i++) Assert.Equal(0f, mono[i]);
    }

    // ── PeakStore: one consistent state, not three fields ────────

    /// <summary>
    /// The pyramid, the document it describes and the version stamp are published together.
    /// A rebuild for a longer document used to be observable as the new length against the old
    /// pyramid, so the version has to move with them for a reader to tell the two apart.
    /// </summary>
    [Fact]
    public void RebuildingThePyramidAdvancesTheVersionWithTheDataItDescribes()
    {
        var doc = new AudioDocument(Ramp(1, 8192), 48000, 32);
        var peaks = new PeakStore();
        peaks.Rebuild(doc);
        int first = peaks.Version;

        doc.ReplaceRange(0, 0, Ramp(1, 8192), "Insert");
        peaks.Rebuild(doc);

        Assert.Equal(first + 1, peaks.Version);
        peaks.Query(0, 0, doc.Length, out _, out float max, out _);
        Assert.True(max > 0);
    }

    /// <summary>A query against a store nothing has been built into answers, rather than throwing.</summary>
    [Fact]
    public void QueryingAnUnbuiltPyramidIsSilentRatherThanThrowing()
    {
        var peaks = new PeakStore();
        peaks.Query(0, 0, 1000, out float min, out float max, out float rms);
        Assert.Equal(0f, min);
        Assert.Equal(0f, max);
        Assert.Equal(0f, rms);
        Assert.Equal(0, peaks.Version);
    }

    // ── Limiter: no allocation on the audio callback ─────────────

    /// <summary>
    /// Process used to reconfigure itself, six allocations, from inside the audio callback, and
    /// against the stored channel count rather than the stream's. An unconfigured limiter passes
    /// audio through instead, and says so through its readout.
    /// </summary>
    [Fact]
    public void AnUnconfiguredLimiterPassesAudioThroughUntouched()
    {
        var limiter = new Limiter { Enabled = true, ThresholdDb = -12, CeilingDb = -6 };
        Assert.False(limiter.Configured);

        var block = new float[512];
        for (int i = 0; i < block.Length; i++) block[i] = 0.99f;
        var expected = (float[])block.Clone();

        limiter.Process(block, 0, block.Length);

        Assert.Equal(expected, block);
        Assert.Equal(0, limiter.GainReductionDb);
    }

    /// <summary>Once configured it limits, and reports the reduction it applied.</summary>
    [Fact]
    public void AConfiguredLimiterHoldsTheCeiling()
    {
        var limiter = new Limiter { Enabled = true, ThresholdDb = -12, CeilingDb = -6 };
        limiter.Configure(48000, 2);
        Assert.True(limiter.Configured);

        double ceiling = Math.Pow(10, -6 / 20.0);
        var block = new float[48000 * 2];
        for (int i = 0; i < block.Length; i++) block[i] = 0.99f;

        // Two passes, so the output being checked is past the 5 ms lookahead delay.
        limiter.Process(block, 0, block.Length);
        limiter.Process(block, 0, block.Length);

        foreach (float sample in block) Assert.True(Math.Abs(sample) <= ceiling * 1.002);
        Assert.True(limiter.GainReductionDb > 0);
    }

    // ── Processing.InsertSilence: bounded duration ───────────────

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e12)]
    public void InsertingAnImpossibleLengthOfSilenceIsRefused(double seconds)
    {
        var doc = new AudioDocument(Ramp(2, 1000), 48000, 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => Processing.InsertSilence(doc, 0, seconds));
        Assert.Equal(1000, doc.Length);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void InsertingSilenceGrowsTheDocumentByTheRequestedDuration()
    {
        var doc = new AudioDocument(Ramp(2, 1000), 48000, 32);
        Processing.InsertSilence(doc, 500, 0.5);

        Assert.Equal(1000 + 24000, doc.Length);
        Assert.Equal(0f, doc.Channels[0][500]);
        Assert.True(doc.CanUndo);
    }

    /// <summary>A zero-length insert is a no-op rather than an empty undo entry.</summary>
    [Fact]
    public void InsertingNoSilenceLeavesNothingToUndo()
    {
        var doc = new AudioDocument(Ramp(2, 1000), 48000, 32);
        Processing.InsertSilence(doc, 500, 0);

        Assert.Equal(1000, doc.Length);
        Assert.False(doc.CanUndo);
    }

    // ── ChannelTools: transforms a caller can run off the UI thread ──

    /// <summary>
    /// The channel menu ran these inline on the dispatcher, at three full-length copies of the
    /// file apiece. The transform overloads take a snapshot and return the replacement, so the
    /// caller can do the arithmetic on a worker and commit with ReplaceAllOwned.
    /// </summary>
    [Fact]
    public void SwappingChannelsExchangesThemWithoutTouchingTheSource()
    {
        float[][] source = Ramp(2, 512);
        var before = new[] { (float[])source[0].Clone(), (float[])source[1].Clone() };

        float[][]? swapped = ChannelTools.SwapChannelsData(source);

        Assert.NotNull(swapped);
        Assert.Equal(before[1], swapped![0]);
        Assert.Equal(before[0], swapped[1]);
        Assert.Equal(before[0], source[0]); // the snapshot the UI still holds is untouched
        Assert.Equal(before[1], source[1]);
    }

    [Fact]
    public void SwappingChannelsOnAMonoFileHasNothingToDo() =>
        Assert.Null(ChannelTools.SwapChannelsData(Ramp(1, 64)));

    [Fact]
    public void InvertingOneChannelLeavesTheOtherAlone()
    {
        float[][] source = Ramp(2, 256);
        float[][] inverted = ChannelTools.InvertPhaseData(source, 1);

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(source[0][i], inverted[0][i]);
            Assert.Equal(-source[1][i], inverted[1][i]);
        }
    }

    [Fact]
    public void InvertingAllChannelsFlipsEveryOne()
    {
        float[][] source = Ramp(3, 128);
        float[][] inverted = ChannelTools.InvertPhaseData(source, -1);

        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 128; i++)
                Assert.Equal(-source[c][i], inverted[c][i]);
    }

    [Fact]
    public void BalancingTrimsTheLeftAndRightPairIndependently()
    {
        float[][] source = Ramp(2, 128, scale: 0.5f);
        float[][]? balanced = ChannelTools.BalanceData(source, -6, 0);

        Assert.NotNull(balanced);
        var half = (float)Math.Pow(10, -6 / 20.0);
        for (int i = 0; i < 128; i++)
        {
            Assert.Equal(source[0][i] * half, balanced![0][i], 5);
            Assert.Equal(source[1][i], balanced[1][i], 5);
        }
    }

    /// <summary>Cancellation is observed, so the progress host's Cancel actually stops the work.</summary>
    [Fact]
    public void ACancelledChannelTransformThrowsRatherThanFinishing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => ChannelTools.InvertPhaseData(Ramp(2, 4_000_000), -1, cancelled.Token));
    }

    /// <summary>
    /// The document-level wrapper commits through ReplaceAllOwned, which retains the outgoing
    /// arrays rather than copying them, and stays one undoable edit.
    /// </summary>
    /// <remarks>
    /// The new channels are clones, deliberately: ReplaceAllOwned takes ownership of what it is
    /// handed, so a transform that returned the document's own arrays would leave the undo entry
    /// and the live document aliasing the same buffers.
    /// </remarks>
    [Fact]
    public void ADocumentLevelChannelSwapIsOneUndoableEdit()
    {
        var doc = new AudioDocument(Ramp(2, 4096), 48000, 32);
        float[] originalLeft = doc.Channels[0];
        float[] originalRight = doc.Channels[1];

        ChannelTools.SwapChannels(doc);

        Assert.Equal(originalRight, doc.Channels[0]);
        Assert.Equal(originalLeft, doc.Channels[1]);
        Assert.NotSame(originalRight, doc.Channels[0]);
        Assert.NotSame(originalLeft, doc.Channels[1]);
        Assert.Equal("Swap Channels", doc.NextUndoName);

        // Undo restores the retained arrays themselves, which is what ReplaceAllOwned buys.
        doc.Undo();
        Assert.Same(originalLeft, doc.Channels[0]);
        Assert.Same(originalRight, doc.Channels[1]);
        Assert.False(doc.CanUndo);
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Proves the chain lock is free while ResetState runs, by having another thread take it.
    /// Before the fix that thread blocked until RemoveEffect returned.
    /// </summary>
    private sealed class ResetProbeEffect : IAudioEffect
    {
        public string TypeId => "test-reset-probe";
        public string DisplayName => "Reset Probe";
        public IReadOnlyList<EffectParam> Params => [];
        public bool Enabled { get; set; } = true;
        public int LatencySamples => 0;
        public string? Readout => null;

        public int ResetCount { get; private set; }
        public bool AnotherThreadReachedTheChainDuringReset { get; private set; }

        /// <summary>The section this is attached to, so ResetState can try to read its chain.</summary>
        public MasterSection? Owner { get; set; }

        public double GetParam(string key) => 0;
        public void SetParam(string key, double value) { }
        public void Configure(int sampleRate, int channels) { }
        public void Process(float[] buffer, int offset, int count) { }

        public void ResetState()
        {
            ResetCount++;
            if (Owner is not { } owner) return;
            var reader = Task.Run(() => owner.ChainSnapshot.Length);
            AnotherThreadReachedTheChainDuringReset = reader.Wait(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Feeds a fixed deinterleaved buffer through MasterSection as interleaved frames.</summary>
    private sealed class ArraySampleProvider(float[][] channels, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels.Length);

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = channels.Length;
            int framesWanted = count / ch;
            int available = Math.Min(framesWanted, channels[0].Length - _position);
            if (available <= 0) return 0;
            for (int f = 0; f < available; f++)
                for (int c = 0; c < ch; c++)
                    buffer[offset + f * ch + c] = channels[c][_position + f];
            _position += available;
            return available * ch;
        }
    }
}
