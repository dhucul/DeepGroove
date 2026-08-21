using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// What the analyzer's lock actually costs the capture thread, and what a reader sees while it is
/// being written to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RecordingLevelAnalyzer.Process"/> is fed from the WASAPI capture callback, where an
/// overrun drops recorded audio, and it holds one lock for a whole buffer. Every snapshot read
/// takes the same lock. The question is not whether that is untidy — it is how many microseconds
/// each side holds it for, because a lock held for a microsecond is not a problem and one held for
/// milliseconds on that thread is.
/// </para>
/// <para>
/// <b>Measured, not reasoned about.</b> The same audit that wrote these tests replaced a
/// <c>Math.Log10</c> on this thread with a binary search on the strength of counting the calls,
/// and the replacement turned out 8.5x slower. Counting is not measuring.
/// </para>
/// </remarks>
public sealed class RecordingLevelAnalyzerConcurrencyTests(ITestOutputHelper output)
{
    private const int Rate = 48_000, Channels = 2;

    private static float[] Buffer(int frames, int seed)
    {
        var random = new Random(seed);
        var buffer = new float[frames * Channels];
        for (int i = 0; i < frames; i++)
        {
            double tone = 0.28 * Math.Sin(2 * Math.PI * 440 * i / (double)Rate);
            for (int c = 0; c < Channels; c++)
                buffer[i * Channels + c] = (float)(tone + (random.NextDouble() - 0.5) * 0.01);
        }
        return buffer;
    }

    /// <summary>
    /// How long each side holds the lock, at the buffer size the engine actually uses and at the
    /// history length a whole-side scan reaches.
    /// </summary>
    [Fact]
    public void MeasureWhatTheLockCosts()
    {
        // A measurement rather than an assertion, and it walks twenty minutes of audio
        // through the analyzer, so it is gated like the corpus runs are.
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var analyzer = new RecordingLevelAnalyzer(Rate, Channels);
        analyzer.FullDurationScanEnabled = true;

        // 100 ms is the engine's default capture buffer; 500 ms is its ceiling.
        foreach (int bufferMs in new[] { 100, 500 })
        {
            var buffer = Buffer(Rate * bufferMs / 1000, seed: bufferMs);
            for (int warm = 0; warm < 20; warm++) analyzer.Process(buffer);

            var sw = Stopwatch.StartNew();
            const int passes = 50;
            for (int i = 0; i < passes; i++) analyzer.Process(buffer);
            double perBuffer = sw.Elapsed.TotalMilliseconds / passes;
            output.WriteLine($"Process({bufferMs} ms buffer): {perBuffer:F3} ms held, " +
                $"{perBuffer / bufferMs * 100:F2}% of the audio it represents");
        }

        // Build up the history a long scan reaches, then time the read the UI does.
        var block = Buffer(Rate / 10, seed: 7);
        for (int minute = 0; minute < 20 * 60 * 10; minute++) analyzer.Process(block);
        output.WriteLine($"history: {analyzer.Snapshot.ElapsedSeconds / 60:F1} minutes of blocks");

        var readSw = Stopwatch.StartNew();
        const int reads = 200;
        for (int i = 0; i < reads; i++) _ = analyzer.Snapshot;
        output.WriteLine($"Snapshot (cached): {readSw.Elapsed.TotalMilliseconds / reads * 1000:F1} us");

        // A fresh snapshot is the miss path: it recomputes the gated integrated loudness over the
        // whole history, and does it holding the lock the capture thread needs.
        readSw.Restart();
        const int fresh = 20;
        for (int i = 0; i < fresh; i++)
        {
            analyzer.Process(block);            // invalidate, as a real capture would
            _ = analyzer.GetFreshSnapshot();
        }
        output.WriteLine($"GetFreshSnapshot (miss): {readSw.Elapsed.TotalMilliseconds / fresh:F3} ms");
    }

    /// <summary>
    /// A snapshot read while capture is running must never show a half-updated analyzer.
    /// </summary>
    /// <remarks>
    /// A torn read here does not throw and does not fail a unit test — it produces a gain
    /// recommendation that is quietly wrong, occasionally, under load. So the assertions are the
    /// invariants that cannot survive one: counters that only ever rise, an active span that cannot
    /// exceed the elapsed one, and no arithmetic that has been handed a value from the wrong
    /// generation.
    /// </remarks>
    [Fact]
    public async Task SnapshotsStaySelfConsistentWhileCaptureIsRunning()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, Channels);
        var buffer = Buffer(Rate / 10, seed: 11);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var faults = new List<string>();

        var capture = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) analyzer.Process(buffer);
        });

        double lastElapsed = 0;
        long lastClipped = 0, lastInvalid = 0;
        int reads = 0;
        while (!stop.IsCancellationRequested)
        {
            RecordingLevelSnapshot s = reads % 8 == 0 ? analyzer.GetFreshSnapshot() : analyzer.Snapshot;
            reads++;

            if (double.IsNaN(s.ElapsedSeconds) || double.IsNaN(s.ActiveSeconds)
                || double.IsNaN(s.SuggestedGainDb) || double.IsNaN(s.ReserveDb)
                || double.IsNaN(s.Confidence))
                faults.Add($"NaN in snapshot at {s.ElapsedSeconds:F3} s");

            if (s.ActiveSeconds > s.ElapsedSeconds + 1e-6)
                faults.Add($"active {s.ActiveSeconds:F3} > elapsed {s.ElapsedSeconds:F3}");
            if (s.ElapsedSeconds < lastElapsed - 1e-9)
                faults.Add($"elapsed went backwards: {lastElapsed:F3} -> {s.ElapsedSeconds:F3}");
            if (s.ClippedSamples < lastClipped)
                faults.Add($"clipped count fell: {lastClipped} -> {s.ClippedSamples}");
            if (s.InvalidSamples < lastInvalid)
                faults.Add($"invalid count fell: {lastInvalid} -> {s.InvalidSamples}");
            if (s.Confidence is < 0 or > 1)
                faults.Add($"confidence out of range: {s.Confidence}");

            lastElapsed = s.ElapsedSeconds;
            lastClipped = s.ClippedSamples;
            lastInvalid = s.InvalidSamples;
        }

        await capture.WaitAsync(TimeSpan.FromSeconds(5));
        // The read count is reported rather than asserted: it depends on the machine, and what it
        // measures is contention rather than correctness. It is worth printing because a reader
        // starved by a writer in a tight loop is the exact shape of the complaint against this
        // lock, and this is the only place the number is visible.
        output.WriteLine($"{reads} snapshots read in 2 s against a capture thread with no " +
            $"real-time pacing, {faults.Count} faults");
        Assert.True(faults.Count == 0, string.Join("\n", faults.Take(10)));
        Assert.True(reads > 0, "the reader never ran at all");
    }
}
