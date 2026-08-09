using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class AudioDocumentTests
{
    [Fact]
    public void FullDocumentRenderCanChangeChannelLayoutAndStillUndoAndRedo()
    {
        float[][] original = [[0.1f, 0.2f, 0.3f]];
        float[][] rendered = [[0.4f, 0.5f], [-0.4f, -0.5f]];
        var document = new AudioDocument(original, 48_000, 24);

        document.ReplaceAllOwned(rendered, "Render Master Chain");

        Assert.Equal(2, document.ChannelCount);
        Assert.Equal(2, document.Length);
        Assert.True(document.CanUndo);
        Assert.Equal("Render Master Chain", document.NextUndoName);
        Assert.Same(rendered[0], document.Channels[0]);

        document.Undo();

        Assert.Single(document.Channels);
        Assert.Equal(original[0], document.Channels[0]);
        Assert.True(document.CanRedo);

        document.Redo();

        Assert.Equal(2, document.ChannelCount);
        Assert.Equal(rendered[0], document.Channels[0]);
        Assert.Equal(rendered[1], document.Channels[1]);
    }

    [Theory]
    [InlineData(ExportFormat.Wav32Float)]
    [InlineData(ExportFormat.Wav24)]
    [InlineData(ExportFormat.Wav16)]
    [InlineData(ExportFormat.Wav16Undithered)]
    [InlineData(ExportFormat.Flac)]
    public void LosslessAndUncompressedFormatsDoNotUseLossyBitrateControls(ExportFormat format)
    {
        Assert.False(AudioExporter.IsLossy(format));
    }

    [Theory]
    [InlineData(ExportFormat.Mp3)]
    [InlineData(ExportFormat.Aac)]
    [InlineData(ExportFormat.Wma)]
    public void LossyFormatsUseBitrateControls(ExportFormat format)
    {
        Assert.True(AudioExporter.IsLossy(format));
    }

    [Fact]
    public void OpenAs16BitWithoutDitherCreatesAnUnsavedQuantizedCopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.wav");
        try
        {
            float sample = 0.1234567f;
            var source = new AudioDocument([[sample, -sample]], 44_100, 32);
            WavCodec.Save(source, path, 32, dither: false);

            AudioDocument opened = AudioImporter.LoadAs(path, OpenBitDepth.Pcm16Undithered);

            float expected = Math.Clamp(
                (int)Math.Round(sample * 32768.0), short.MinValue, short.MaxValue) / 32768f;
            Assert.Equal(16, opened.SourceBitDepth);
            Assert.False(opened.Dither16BitOnSave);
            Assert.True(opened.Dirty);
            Assert.Null(opened.FilePath);
            Assert.Contains("16-bit no dither", opened.Title);
            Assert.Equal(expected, opened.Channels[0][0]);
            Assert.Equal(-expected, opened.Channels[0][1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void Undithered16BitExportUsesDirectPcmQuantization()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "undithered.wav");
        try
        {
            float sample = 0.2345678f;
            var source = new AudioDocument([[sample]], 48_000, 32);

            AudioExporter.Export(source, path, ExportFormat.Wav16Undithered,
                bitrateKbps: 0, start: 0, count: 1, targetRate: 0);
            AudioDocument exported = WavCodec.Load(path);

            float expected = Math.Clamp(
                (int)Math.Round(sample * 32768.0), short.MinValue, short.MaxValue) / 32768f;
            Assert.Equal(16, exported.SourceBitDepth);
            Assert.Equal(expected, exported.Channels[0][0]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
