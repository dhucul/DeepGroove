using System.Text.Json;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
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
    [InlineData(ExportFormat.Aiff32)]
    [InlineData(ExportFormat.Aiff24)]
    [InlineData(ExportFormat.Aiff16)]
    [InlineData(ExportFormat.Aiff16Undithered)]
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
    public void DitheredOpenAsDefersItsSingleDitherPassUntilSave()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "source.wav");
        string savedPath = Path.Combine(directory, "saved.wav");
        try
        {
            float[] samples = [0.1234567f, -0.2345678f, 0.3456789f];
            var source = new AudioDocument([samples], 44_100, 32);
            WavCodec.Save(source, sourcePath, 32, dither: false);

            AudioDocument opened = AudioImporter.LoadAs(sourcePath, OpenBitDepth.Pcm16Dithered);

            Assert.Equal(16, opened.SourceBitDepth);
            Assert.True(opened.Dither16BitOnSave);
            Assert.Equal(samples, opened.Channels[0]);

            WavCodec.Save(opened, savedPath, 16, dither: opened.Dither16BitOnSave);
            AudioDocument saved = WavCodec.Load(savedPath);
            var tpdf = new TpdfDither();
            for (int index = 0; index < samples.Length; index++)
            {
                int quantized = Math.Clamp(
                    (int)Math.Round(samples[index] * 32768.0 + tpdf.Next()),
                    short.MinValue,
                    short.MaxValue);
                Assert.Equal(quantized / 32768f, saved.Channels[0][index]);
            }
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(savedPath)) File.Delete(savedPath);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void RecoveryMetadataRoundTripsBitDepthAndDitherPolicy()
    {
        var entry = new AutosaveService.Entry
        {
            AutosaveFile = "recovery.wav",
            Title = "source.wav",
            OriginalPath = "source.wav",
            SourceBitDepth = 16,
            Dither16BitOnSave = false,
            RequiresSaveAs = true,
            SavedAt = DateTime.Now,
        };
        string json = JsonSerializer.Serialize(entry);
        AutosaveService.Entry restoredEntry =
            JsonSerializer.Deserialize<AutosaveService.Entry>(json)!;
        var recoveredDocument = new AudioDocument([[0.25f]], 48_000, 32);

        AutosaveService.RestoreFormatMetadata(recoveredDocument, restoredEntry);

        Assert.Equal(16, recoveredDocument.SourceBitDepth);
        Assert.False(recoveredDocument.Dither16BitOnSave);
        Assert.True(recoveredDocument.RequiresSaveAs);

        var legacyDocument = new AudioDocument([[0.25f]], 48_000, 32);
        AutosaveService.Entry legacyEntry =
            JsonSerializer.Deserialize<AutosaveService.Entry>("{}")!;
        AutosaveService.RestoreFormatMetadata(legacyDocument, legacyEntry);
        Assert.Equal(32, legacyDocument.SourceBitDepth);
        Assert.True(legacyDocument.Dither16BitOnSave);
        Assert.False(legacyDocument.RequiresSaveAs);
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
