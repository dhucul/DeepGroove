using WaveLab.Audio;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class PlaybackOwnershipTests
{
    [Fact]
    public void PlaybackZoomModeRequiresTheVisibleDocumentToOwnTheTransport()
    {
        var playingDocument = CreateDocument();
        var otherTab = CreateDocument();

        Assert.True(MainViewModel.IsPlaybackActiveForDocument(
            playingDocument, playingDocument, isPlaying: true));

        // Playback can continue while another tab is visible, but that tab's stale
        // playhead is not a valid zoom anchor.
        Assert.False(MainViewModel.IsPlaybackActiveForDocument(
            otherTab, playingDocument, isPlaying: true));

        // Preview playback has no owning editor document.
        Assert.False(MainViewModel.IsPlaybackActiveForDocument(
            playingDocument, playbackDocument: null, isPlaying: true));

        Assert.False(MainViewModel.IsPlaybackActiveForDocument(
            playingDocument, playingDocument, isPlaying: false));
    }

    private static DocumentViewModel CreateDocument() =>
        new(new AudioDocument([[0f]], 48_000, 32));
}
