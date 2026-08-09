namespace WaveLab.Audio;

/// <summary>Channel-layout tools. Same-layout ops are destructive (undoable); layout changes produce new documents.</summary>
public static class ChannelTools
{
    public static void SwapChannels(AudioDocument doc)
    {
        if (doc.ChannelCount < 2) return;
        var data = doc.CopyRange(0, doc.Length);
        (data[0], data[1]) = (data[1], data[0]);
        doc.ReplaceRange(0, doc.Length, data, "Swap Channels");
    }

    /// <summary>channel = -1 inverts all channels.</summary>
    public static void InvertPhase(AudioDocument doc, int channel)
    {
        var data = doc.CopyRange(0, doc.Length);
        for (int c = 0; c < data.Length; c++)
        {
            if (channel >= 0 && c != channel) continue;
            var ch = data[c];
            for (int i = 0; i < ch.Length; i++) ch[i] = -ch[i];
        }
        doc.ReplaceRange(0, doc.Length, data, "Invert Phase");
    }

    public static void Balance(AudioDocument doc, double leftDb, double rightDb)
    {
        if (doc.ChannelCount < 2) return;
        var data = doc.CopyRange(0, doc.Length);
        // Without a channel mask there is no safe way to classify centre, LFE,
        // or surround channels as left/right. Adjust the canonical L/R pair only.
        for (int c = 0; c < Math.Min(2, data.Length); c++)
        {
            float g = (float)Math.Pow(10, (c == 0 ? leftDb : rightDb) / 20.0);
            var ch = data[c];
            for (int i = 0; i < ch.Length; i++) ch[i] *= g;
        }
        doc.ReplaceRange(0, doc.Length, data, "Channel Balance");
    }

    public static AudioDocument MonoMixdown(AudioDocument doc)
    {
        var mono = new float[1][];
        mono[0] = new float[doc.Length];
        for (int i = 0; i < doc.Length; i++)
        {
            float v = 0;
            for (int c = 0; c < doc.ChannelCount; c++) v += doc.Channels[c][i];
            mono[0][i] = v / doc.ChannelCount;
        }
        // Averaging channels creates values between the source PCM quantization
        // steps. Mark the result as float-derived so later 16-bit CD export dithers it.
        return new AudioDocument(mono, doc.SampleRate, sourceBitDepth: 32)
        {
            Title = Base(doc) + " (mono).wav",
        };
    }

    public static AudioDocument MonoToStereo(AudioDocument doc)
    {
        var stereo = new float[2][];
        stereo[0] = (float[])doc.Channels[0].Clone();
        stereo[1] = (float[])doc.Channels[0].Clone();
        return new AudioDocument(stereo, doc.SampleRate, doc.SourceBitDepth)
        {
            Title = Base(doc) + " (stereo).wav",
        };
    }

    public static AudioDocument ExtractChannel(AudioDocument doc, int channel)
    {
        var mono = new float[1][];
        mono[0] = (float[])doc.Channels[channel].Clone();
        string suffix = doc.ChannelCount == 2 ? (channel == 0 ? "L" : "R") : $"ch{channel + 1}";
        return new AudioDocument(mono, doc.SampleRate, doc.SourceBitDepth)
        {
            Title = $"{Base(doc)} ({suffix}).wav",
        };
    }

    public static AudioDocument ConvertSampleRate(AudioDocument doc, int targetRate)
    {
        var data = Resampler.Resample(doc.Channels, doc.SampleRate, targetRate);
        // Sample-rate conversion is mathematical processing even when the target
        // happens to be 44.1 kHz; retain that provenance for correct export dither.
        return new AudioDocument(data, targetRate, sourceBitDepth: 32)
        {
            Title = $"{Base(doc)} ({targetRate / 1000.0:0.#} kHz).wav",
        };
    }

    private static string Base(AudioDocument doc) => System.IO.Path.GetFileNameWithoutExtension(doc.Title);
}
