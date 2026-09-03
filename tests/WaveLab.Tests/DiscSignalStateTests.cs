using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class DiscSignalStateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-disc-state").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static AudioDocument Document() =>
        new([[0.1f, -0.1f, 0.2f], [0.1f, -0.1f, 0.2f]], 48_000, 32);

    [Fact]
    public void DiscStateFollowsTheAudioThroughUndoAndRedo()
    {
        AudioDocument document = Document();
        float[][] equalized = document.Channels.Select(channel => (float[])channel.Clone()).ToArray();
        document.ReplaceAllOwned(equalized, "Flat Vinyl Transfer", DiscSignalState.PlaybackEqualized);
        document.ReplaceRange(0, 1, [[0.05f], [0.05f]], "Gain");

        Assert.Equal(DiscSignalState.PlaybackEqualized, document.DiscSignalState);
        document.Undo();
        Assert.Equal(DiscSignalState.PlaybackEqualized, document.DiscSignalState);
        document.Undo();
        Assert.Equal(DiscSignalState.Unknown, document.DiscSignalState);
        document.Redo();
        Assert.Equal(DiscSignalState.PlaybackEqualized, document.DiscSignalState);
    }

    [Theory]
    [InlineData("state.wav")]
    [InlineData("state.aiff")]
    [InlineData("state.w64")]
    public void DiscStateSurvivesSaveAndReopen(string name)
    {
        string path = Path.Combine(_directory, name);
        AudioDocument document = Document();
        document.ReplaceAllOwned(
            document.Channels.Select(channel => (float[])channel.Clone()).ToArray(),
            "Flat Vinyl Transfer", DiscSignalState.PlaybackEqualized);

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".wav") WavCodec.Save(document, path, 24, dither: false);
        else if (extension == ".aiff") AiffCodec.Save(document, path, 24, dither: false);
        else Wave64Codec.Save(document, path, 24, dither: false);

        AudioDocument reopened = extension switch
        {
            ".wav" => WavCodec.Load(path),
            ".aiff" => AiffCodec.Load(path),
            _ => Wave64Codec.Load(path),
        };
        Assert.Equal(DiscSignalState.PlaybackEqualized, reopened.DiscSignalState);
    }
}
