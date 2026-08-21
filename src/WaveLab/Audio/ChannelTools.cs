namespace WaveLab.Audio;

/// <summary>Channel-layout tools. Same-layout ops are destructive (undoable); layout changes produce new documents.</summary>
public static class ChannelTools
{
    // ── whole-file transforms ────────────────────────────────────
    //
    // These take a channel snapshot and return the replacement, rather than editing the
    // document themselves. That is what lets the caller run them on a worker thread and
    // commit with ReplaceAllOwned: doing it inside the document cost three full-length
    // copies of the file (the working copy, the undo copy, and the splice) on whichever
    // thread called in — which for the channel menu was the dispatcher.

    /// <summary>The channels with left and right exchanged, or null when there is no pair to swap.</summary>
    public static float[][]? SwapChannelsData(IReadOnlyList<float[]> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2) return null;
        float[][] data = CloneChannels(source, cancellationToken);
        (data[0], data[1]) = (data[1], data[0]);
        return data;
    }

    /// <summary>The channels with the sign flipped. channel = -1 inverts all of them.</summary>
    public static float[][] InvertPhaseData(
        IReadOnlyList<float[]> source, int channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        float[][] data = CloneChannels(source, cancellationToken);
        for (int c = 0; c < data.Length; c++)
        {
            if (channel >= 0 && c != channel) continue;
            float[] ch = data[c];
            for (int i = 0; i < ch.Length; i++)
            {
                if ((i & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                ch[i] = -ch[i];
            }
        }
        return data;
    }

    /// <summary>The channels with independent left/right trims, or null when there is no pair.</summary>
    public static float[][]? BalanceData(
        IReadOnlyList<float[]> source, double leftDb, double rightDb,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2) return null;
        float[][] data = CloneChannels(source, cancellationToken);
        // Without a channel mask there is no safe way to classify centre, LFE,
        // or surround channels as left/right. Adjust the canonical L/R pair only.
        for (int c = 0; c < Math.Min(2, data.Length); c++)
        {
            var g = (float)Math.Pow(10, (c == 0 ? leftDb : rightDb) / 20.0);
            float[] ch = data[c];
            for (int i = 0; i < ch.Length; i++)
            {
                if ((i & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                ch[i] *= g;
            }
        }
        return data;
    }

    private static float[][] CloneChannels(IReadOnlyList<float[]> source, CancellationToken cancellationToken)
    {
        var data = new float[source.Count][];
        for (int c = 0; c < source.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            data[c] = (float[])source[c].Clone();
        }
        return data;
    }

    // ── document-level convenience wrappers ──────────────────────
    //
    // Synchronous and single-threaded; the UI takes the transform overloads above instead.

    public static void SwapChannels(AudioDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (SwapChannelsData(doc.Channels) is { } data)
            doc.ReplaceAllOwned(data, "Swap Channels");
    }

    /// <summary>channel = -1 inverts all channels.</summary>
    public static void InvertPhase(AudioDocument doc, int channel)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ReplaceAllOwned(InvertPhaseData(doc.Channels, channel), "Invert Phase");
    }

    public static void Balance(AudioDocument doc, double leftDb, double rightDb)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (BalanceData(doc.Channels, leftDb, rightDb) is { } data)
            doc.ReplaceAllOwned(data, "Channel Balance");
    }

    public static AudioDocument MonoMixdown(AudioDocument doc)
    {
        // This runs on a background thread against the live document. Take a
        // point-in-time snapshot and derive every bound from it: an edit publishing
        // a longer array mid-loop would otherwise grow the loop bound while the
        // destination stays sized from before.
        float[][] source = [.. doc.Channels];
        int channelCount = source.Length;
        int length = channelCount == 0 ? 0 : source[0].Length;
        var mono = new float[1][];
        mono[0] = new float[length];
        for (int i = 0; i < length; i++)
        {
            float v = 0;
            for (int c = 0; c < channelCount; c++) v += source[c][i];
            mono[0][i] = v / channelCount;
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

    public static AudioDocument ConvertSampleRate(AudioDocument doc, int targetRate,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var data = Resampler.Resample(doc.Channels, doc.SampleRate, targetRate, cancellationToken, progress);
        // Sample-rate conversion is mathematical processing even when the target
        // happens to be 44.1 kHz; retain that provenance for correct export dither.
        return new AudioDocument(data, targetRate, sourceBitDepth: 32)
        {
            Title = $"{Base(doc)} ({targetRate / 1000.0:0.#} kHz).wav",
        };
    }

    private static string Base(AudioDocument doc) => System.IO.Path.GetFileNameWithoutExtension(doc.Title);
}
