using System.Diagnostics;
using System.Reflection;
using NAudio.Wave;
using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class ClickTrackTests
{
    [Fact]
    public async Task StopDoesNotWaitForMetronomeEndpointCleanup()
    {
        using var clickTrack = new ClickTrack();
        var output = new DelayedDisposePlayer();
        typeof(ClickTrack)
            .GetField("_out", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(clickTrack, output);

        try
        {
            var watch = Stopwatch.StartNew();
            clickTrack.Stop();
            watch.Stop();

            await output.DisposeStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Metronome Stop waited {watch.Elapsed.TotalMilliseconds:0} ms for endpoint cleanup.");
        }
        finally
        {
            output.AllowDispose.TrySetResult();
        }
    }

    private sealed class DelayedDisposePlayer : IWavePlayer, IAsyncDisposable
    {
        public TaskCompletionSource DisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public PlaybackState PlaybackState => PlaybackState.Playing;
        public WaveFormat OutputWaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 1);

#pragma warning disable CS0067
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
#pragma warning restore CS0067

#pragma warning disable CS0618
        public float Volume { get; set; } = 1;
#pragma warning restore CS0618

        public void Init(IWaveProvider waveProvider) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }

        public void Dispose() => AllowDispose.Task.Wait(TimeSpan.FromSeconds(2));

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await AllowDispose.Task.ConfigureAwait(false);
        }
    }
}
