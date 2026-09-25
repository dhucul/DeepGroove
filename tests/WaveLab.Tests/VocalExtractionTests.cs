using System.IO;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using Xunit;

namespace WaveLab.Tests;

public sealed class VocalExtractionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WaveLab.VocalsTests." + Guid.NewGuid().ToString("N"));
    public VocalExtractionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(false, "Song - vocals.wav")]
    [InlineData(true, "Song - vocals (selection).wav")]
    public void ExtractedAudioIsAnIndependentUnsavedFloatDocument(bool selection, string title)
    {
        string temporary = Path.Combine(_directory, "vocals.wav");
        float[][] channels = [Enumerable.Repeat(1.25f, 4410).ToArray(), Enumerable.Repeat(-.5f, 4410).ToArray()];
        WavCodec.Save(new AudioDocument(channels, 44100, 32), temporary, 32);
        var vocals = LyricsEngine.ReadVocals(temporary, "Song.flac", .1, selection, default);
        File.Delete(temporary);
        Assert.Null(vocals.FilePath);
        Assert.Equal(title, vocals.Title);
        Assert.True(vocals.Dirty);
        Assert.True(vocals.RequiresSaveAs);
        Assert.False(vocals.CanUndo);
        Assert.Equal(32, vocals.SourceBitDepth);
        Assert.Equal(44100, vocals.SampleRate);
        Assert.Equal(2, vocals.ChannelCount);
        Assert.Equal(channels[0], vocals.Channels[0]);
        Assert.Equal(channels[1], vocals.Channels[1]);
        // The temporary worker file can be gone before Save As writes the new audio.
        string saved = Path.Combine(_directory, "saved-vocals.wav");
        WavCodec.Save(vocals, saved, 32);
        Assert.Equal(channels[0], WavCodec.Load(saved).Channels[0]);
    }

    [Theory]
    [InlineData(48000, 2, 32, .1)]
    [InlineData(44100, 1, 32, .1)]
    [InlineData(44100, 2, 24, .1)]
    [InlineData(44100, 2, 32, 2)]
    public void IncorrectWorkerAudioIsRejectedBeforeOpeningATab(int rate, int channels, int bits, double expectedDuration)
    {
        string path = Path.Combine(_directory, "unexpected.wav");
        var audio = new AudioDocument(Enumerable.Range(0, channels).Select(_ => new float[rate / 10]).ToArray(), rate, bits);
        WavCodec.Save(audio, path, bits);
        Assert.Throws<InvalidDataException>(() => LyricsEngine.ReadVocals(path, "Song", expectedDuration, false, default));
    }

    [Fact]
    public void PreparingAudioForVocalExtractionPreservesFloatHeadroom()
    {
        string path = Path.Combine(_directory, "input.wav");
        float[] source = [1.5f, -1.25f, .5f, float.NaN];
        LyricsAudio.Write([source], 44100, 0, source.Length, path, default);
        Assert.Equal(new float[] { 1.5f, -1.25f, .5f, 0 }, WavCodec.Load(path).Channels[0]);
        Assert.True(float.IsNaN(source[3]));
    }
}
