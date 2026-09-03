using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Opt-in replay of the already auto-trimmed flat recording which exposed a lost fade history.
/// WAVELAB_RUNOUT_PROBE names that WAV. The test reads it without modifying or exporting audio.
/// Synthetic coverage in RunOutDetectorTests runs without this external recording.
/// </summary>
public sealed class RunOutRecordingTests(ITestOutputHelper output)
{
    [Fact]
    public void PreviouslyAutoTrimmedFlatFadeIsRecognized()
    {
        string? path = Environment.GetEnvironmentVariable("WAVELAB_RUNOUT_PROBE");
        if (string.IsNullOrWhiteSpace(path)) return;

        AudioDocument doc = WavCodec.Load(path);
        int rate = doc.SampleRate, channels = doc.ChannelCount;
        var detector = new RunOutDetector(rate, channels);
        int packetFrames = Math.Max(1, rate / 100);
        var packet = new float[packetFrames * channels];
        double firstFade = double.NaN;
        for (int start = 0; start < doc.Length; start += packetFrames)
        {
            int frames = Math.Min(packetFrames, doc.Length - start);
            for (int i = 0; i < frames; i++)
            for (int channel = 0; channel < channels; channel++)
                packet[i * channels + channel] = doc.Channels[channel][start + i];

            Assert.False(detector.Process(packet, frames * channels, channels),
                "the detector stopped inside the already-trimmed source recording");
            if (detector.PreservedFadingTail && double.IsNaN(firstFade))
                firstFade = (start + frames) / (double)rate;
        }

        Assert.True(detector.PreservedFadingTail,
            "the real flat fade was never recognized before reaching its groove floor");
        output.WriteLine($"Fade first recognized at {firstFade:0.000} s; source ends at {doc.Length / (double)rate:0.000} s.");
    }
}
