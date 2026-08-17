using System.IO;
using WaveLab.Audio;
using WaveLab.Audio.Montage;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class MontageTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-montage").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private string At(string name) => Path.Combine(_directory, name);

    private static float[][] Tone(int frames, double frequency, double amplitude = 0.5, int channels = 2)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
        }
        return data;
    }

    private static MontageSource Source(int frames, double frequency, string name = "s",
        double amplitude = 0.5) =>
        MontageSource.From(Tone(frames, frequency, amplitude), Rate, Rate, 2, name);

    /// <summary>Windowed RMS in dB, for looking at what the renderer actually produced.</summary>
    private static double RmsDb(float[][] audio, int start, int count)
    {
        double energy = 0;
        int taken = 0;
        for (int c = 0; c < audio.Length; c++)
            for (int i = start; i < start + count && i < audio[c].Length; i++)
            {
                energy += audio[c][i] * (double)audio[c][i];
                taken++;
            }
        return taken == 0 ? double.NegativeInfinity : 10 * Math.Log10(energy / taken);
    }

    // ── the lane ─────────────────────────────────────────────────

    [Fact]
    public void ClipsAreKeptInTimelineOrderHoweverTheyAreAdded()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate, 440));

        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = Rate * 2, Name = "third" });
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = 0, Name = "first" });
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = Rate, Name = "second" });

        Assert.Equal(["first", "second", "third"], montage.Clips.Select(c => c.Name));
        Assert.Equal(Rate * 3, montage.Length);
    }

    [Fact]
    public void AppendingWithAnOverlapPlacesTheClipBackByThatMuch()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate * 2, 440));

        montage.Append(s);
        MontageClip second = montage.Append(s, overlapSamples: Rate / 2);

        Assert.Equal(Rate * 2 - Rate / 2, second.TimelineStart);
        Assert.Equal(Rate / 2, MontageDocument.Overlap(montage.Clips[0], montage.Clips[1]));
        Assert.Equal(Rate * 2 + Rate * 2 - Rate / 2, montage.Length);
    }

    /// <summary>One source, many clips — the reason a montage references files rather than copying.</summary>
    [Fact]
    public void TheSameFileLoadedTwiceIsOneSource()
    {
        string path = At("tone.wav");
        WavCodec.Save(new AudioDocument(Tone(Rate, 440), Rate, 24), path, 24, dither: false);

        var montage = new MontageDocument(Rate, 2);
        int first = montage.AddSource(MontageSource.Load(path, Rate, 2));
        int second = montage.AddSource(MontageSource.Load(path, Rate, 2));

        Assert.Equal(first, second);
        Assert.Single(montage.Sources);
    }

    [Fact]
    public void ASourceIsBroughtOntoTheMontagesClockAndChannelCount()
    {
        // 48 kHz mono into a 44.1 kHz stereo montage.
        MontageSource source = MontageSource.From(Tone(48_000, 440, channels: 1), 48_000, Rate, 2, "s");

        output.WriteLine($"{source.OriginalSampleRate} Hz mono → {source.SampleRate} Hz, " +
                         $"{source.ChannelCount} ch, {source.Length} samples");

        Assert.Equal(Rate, source.SampleRate);
        Assert.Equal(2, source.ChannelCount);
        Assert.True(source.WasResampled);
        Assert.InRange(source.Length, Rate - 200, Rate + 200);

        // Copied, not aliased: an edit to one channel must not show on the other.
        source.Channels[0][0] = 0.9f;
        Assert.NotEqual(0.9f, source.Channels[1][0]);
    }

    /// <summary>
    /// Summing two correlated channels is +6 dB, so a stereo file dropped into a mono montage would
    /// clip on arrival if the downmix did not average.
    /// </summary>
    [Fact]
    public void AStereoSourceIsAveragedRatherThanSummedIntoAMonoMontage()
    {
        MontageSource source = MontageSource.From(Tone(1_000, 440, amplitude: 0.8), Rate, Rate, 1, "s");

        Assert.Single(source.Channels);
        Assert.True(source.Channels[0].Max(Math.Abs) <= 0.81f,
            $"peak {source.Channels[0].Max(Math.Abs)} — a summed downmix would be near 1.6");
    }

    [Fact]
    public void ASourceOnTheWrongClockIsRefused()
    {
        var montage = new MontageDocument(Rate, 2);
        MontageSource wrongRate = MontageSource.From(Tone(1_000, 440), 48_000, 48_000, 2, "s");
        Assert.Throws<ArgumentException>(() => montage.AddSource(wrongRate));
    }

    // ── rendering ────────────────────────────────────────────────

    [Fact]
    public void ButtedClipsRenderEndToEndAtTheRightPlaces()
    {
        var montage = new MontageDocument(Rate, 2);
        int quiet = montage.AddSource(Source(Rate, 440, "quiet", amplitude: 0.1));
        int loud = montage.AddSource(Source(Rate, 440, "loud", amplitude: 0.8));

        montage.Add(new MontageClip { SourceIndex = quiet, Length = Rate, TimelineStart = 0 });
        montage.Add(new MontageClip { SourceIndex = loud, Length = Rate, TimelineStart = Rate });

        MontageRenderResult result = MontageRenderer.Render(montage);

        double first = RmsDb(result.Channels, Rate / 4, 4_096);
        double second = RmsDb(result.Channels, Rate + Rate / 4, 4_096);
        output.WriteLine($"first clip {first:0.0} dB, second {second:0.0} dB, peak {result.PeakDb:0.0} dBFS");

        Assert.Equal(Rate * 2, result.Length);
        Assert.Equal(0, result.Crossfades);
        Assert.InRange(second - first, 17, 19);   // 0.8 against 0.1 is 18 dB
    }

    [Fact]
    public void AGapBetweenClipsRendersAsSilence()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate, 440));

        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = 0 });
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = Rate * 2 });

        MontageRenderResult result = MontageRenderer.Render(montage);

        Assert.Equal(Rate * 3, result.Length);
        for (int c = 0; c < 2; c++)
            for (int i = Rate; i < Rate * 2; i++)
                Assert.Equal(0, result.Channels[c][i]);
    }

    [Fact]
    public void ClipGainIsApplied()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate, 440, amplitude: 0.5));

        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = 0 });
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = Rate, GainDb = -6 });

        MontageRenderResult result = MontageRenderer.Render(montage);
        double plain = RmsDb(result.Channels, Rate / 4, 4_096);
        double trimmed = RmsDb(result.Channels, Rate + Rate / 4, 4_096);

        output.WriteLine($"plain {plain:0.00} dB, −6 dB clip {trimmed:0.00} dB");
        Assert.InRange(plain - trimmed, 5.9, 6.1);
    }

    /// <summary>
    /// The headline claim: the join between two unrelated pieces neither dips nor bumps, because the
    /// law is chosen from what the two clips actually contain.
    /// </summary>
    [Fact]
    public void TheLevelHoldsThroughAJoinBetweenUnrelatedMaterial()
    {
        var montage = new MontageDocument(Rate, 2);
        int a = montage.AddSource(Source(Rate * 2, 440, "a"));
        int b = montage.AddSource(Source(Rate * 2, 1_100, "b"));

        montage.Add(new MontageClip { SourceIndex = a, Length = Rate * 2, TimelineStart = 0 });
        montage.Add(new MontageClip { SourceIndex = b, Length = Rate * 2, TimelineStart = Rate });

        MontageRenderResult result = MontageRenderer.Render(montage);

        double before = RmsDb(result.Channels, Rate / 2, 8_192);
        double middle = RmsDb(result.Channels, Rate + Rate / 2 - 4_096, 8_192);
        double after = RmsDb(result.Channels, Rate * 2 + Rate / 2, 8_192);

        output.WriteLine($"ρ measured {result.MeanCorrelation:0.000}");
        output.WriteLine($"before {before:0.00} dB · through the join {middle:0.00} dB · after {after:0.00} dB");

        Assert.Equal(1, result.Crossfades);
        Assert.True(result.MeanCorrelation < 0.05);
        Assert.True(Math.Abs(middle - before) < 0.3, $"the join moved by {middle - before:0.00} dB");
        Assert.True(Math.Abs(after - before) < 0.1);
    }

    /// <summary>
    /// And the case that would bump by 3 dB under the same law: two clips of the same recording,
    /// which is what a repair splice or a loop join is.
    /// </summary>
    [Fact]
    public void TheLevelHoldsThroughAJoinBetweenTwoTakesOfTheSameThing()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate * 3, 440));

        // The same source, and the overlapping spans read the same samples — so what is summed
        // through the join is a signal with itself.
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate * 2, TimelineStart = 0, SourceStart = 0 });
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate * 2, TimelineStart = Rate, SourceStart = Rate });

        MontageRenderResult result = MontageRenderer.Render(montage);

        double before = RmsDb(result.Channels, Rate / 2, 8_192);
        double middle = RmsDb(result.Channels, Rate + Rate / 2 - 4_096, 8_192);

        output.WriteLine($"ρ measured {result.MeanCorrelation:0.000}");
        output.WriteLine($"before {before:0.00} dB · through the join {middle:0.00} dB");

        Assert.True(result.MeanCorrelation > 0.99, $"ρ was only {result.MeanCorrelation:0.000}");
        Assert.True(Math.Abs(middle - before) < 0.3, $"the join moved by {middle - before:0.00} dB");
    }

    /// <summary>An overlap is a crossfade; the clips' own fades describe free edges, not joins.</summary>
    [Fact]
    public void AnOverlapOverridesTheClipsOwnFades()
    {
        var montage = new MontageDocument(Rate, 2);
        int a = montage.AddSource(Source(Rate * 2, 440, "a"));
        int b = montage.AddSource(Source(Rate * 2, 1_100, "b"));

        // Fades far shorter than the overlap: if they were applied, the middle would go silent.
        montage.Add(new MontageClip
        {
            SourceIndex = a, Length = Rate * 2, TimelineStart = 0, FadeOutSamples = 128,
        });
        montage.Add(new MontageClip
        {
            SourceIndex = b, Length = Rate * 2, TimelineStart = Rate, FadeInSamples = 128,
        });

        MontageRenderResult result = MontageRenderer.Render(montage);
        double before = RmsDb(result.Channels, Rate / 2, 8_192);
        double middle = RmsDb(result.Channels, Rate + Rate / 2 - 4_096, 8_192);

        output.WriteLine($"before {before:0.00} dB · through the join {middle:0.00} dB");
        Assert.True(Math.Abs(middle - before) < 0.3, $"the join moved by {middle - before:0.00} dB");
    }

    [Fact]
    public void AFreeEdgeStillGetsTheClipsOwnFade()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate, 440));
        montage.Add(new MontageClip
        {
            SourceIndex = s, Length = Rate, TimelineStart = 0,
            FadeInSamples = Rate / 2, FadeInShape = FadeShape.Linear,
        });

        MontageRenderResult result = MontageRenderer.Render(montage);

        Assert.Equal(0, result.Channels[0][0]);
        double quarter = RmsDb(result.Channels, Rate / 4 - 512, 1_024);
        double full = RmsDb(result.Channels, Rate * 3 / 4, 1_024);

        // A quarter of the way in a linear fade is at half amplitude — 6 dB down.
        output.WriteLine($"a quarter in {quarter:0.0} dB, past the fade {full:0.0} dB");
        Assert.InRange(full - quarter, 5.5, 6.5);
    }

    /// <summary>
    /// Two fades that will not both fit are shortened together, so neither vanishes and the clip
    /// keeps some unfaded middle.
    /// </summary>
    [Fact]
    public void FadesLongerThanTheClipAreShortenedInProportion()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(1_000, 440));
        montage.Add(new MontageClip
        {
            SourceIndex = s, Length = 1_000, TimelineStart = 0,
            FadeInSamples = 900, FadeOutSamples = 900,
        });

        MontageRenderResult result = MontageRenderer.Render(montage);

        Assert.Equal(0, result.Channels[0][0]);
        Assert.True(result.PeakAmplitude > 0.3, "the clip should not have been faded to nothing");

        var issues = montage.Validate();
        Assert.Contains(issues, i => i.Severity == MontageIssueSeverity.Warning &&
                                     i.Message.Contains("fades are longer"));
    }

    [Fact]
    public void ReadingPastTheEndOfASourceIsSilenceRatherThanAnError()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(1_000, 440));
        montage.Add(new MontageClip { SourceIndex = s, Length = 4_000, TimelineStart = 0 });

        MontageRenderResult result = MontageRenderer.Render(montage);

        Assert.Equal(4_000, result.Length);
        for (int i = 1_000; i < 4_000; i++) Assert.Equal(0, result.Channels[0][i]);
        Assert.Contains(montage.Validate(), i => i.Message.Contains("reads past the end"));
    }

    [Fact]
    public void OverlappingClipsThatSumPastFullScaleAreReportedNotClamped()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate, 440, amplitude: 0.9));

        // Butted, both at full tilt with a gain lift: nothing crossfades, so they simply add.
        montage.Add(new MontageClip { SourceIndex = s, Length = Rate, TimelineStart = 0, GainDb = 6 });

        MontageRenderResult result = MontageRenderer.Render(montage);
        output.WriteLine($"peak {result.PeakAmplitude:0.000} ({result.PeakDb:0.0} dBFS), clips: {result.Clips}");

        Assert.True(result.Clips);
        Assert.True(result.PeakAmplitude > 1.5, "the peak should be reported as it is, not limited");
    }

    [Fact]
    public void AnEmptyMontageIsRefusedRatherThanRenderedAsNothing()
    {
        var montage = new MontageDocument(Rate, 2);
        Assert.Throws<InvalidOperationException>(() => MontageRenderer.Render(montage));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate * 10, 440));
        for (int i = 0; i < 8; i++)
            montage.Add(new MontageClip { SourceIndex = s, Length = Rate * 10, TimelineStart = i * Rate * 9 });

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MontageRenderer.Render(montage, cancellation.Token));
    }

    [Fact]
    public void ThreeWayOverlapsAreWarnedAboutRatherThanGuessedAt()
    {
        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(Source(Rate * 2, 440));
        for (int i = 0; i < 3; i++)
            montage.Add(new MontageClip { SourceIndex = s, Length = Rate * 2, TimelineStart = i * (Rate / 2) });

        var issues = montage.Validate();
        foreach (MontageIssue issue in issues) output.WriteLine($"{issue.Severity}: {issue.Message}");

        Assert.Contains(issues, i => i.Message.Contains("all overlap"));
        Assert.Equal(2, MontageRenderer.Render(montage).Crossfades);
    }

    [Fact]
    public void RenderingToADocumentGivesSomethingEverythingElseCanWorkOn()
    {
        var montage = new MontageDocument(Rate, 2) { Title = "Side A" };
        int s = montage.AddSource(Source(Rate, 440));
        montage.Append(s);
        montage.Append(s, overlapSamples: Rate / 4);

        AudioDocument document = MontageRenderer.RenderToDocument(montage);

        Assert.Equal("Side A", document.Title);
        Assert.Equal(Rate, document.SampleRate);
        Assert.Equal(2, document.ChannelCount);
        Assert.Equal(montage.Length, document.Length);
    }

    // ── persistence ──────────────────────────────────────────────

    [Fact]
    public void AMontageRoundTripsThroughItsFile()
    {
        string wav = At("side-a.wav");
        WavCodec.Save(new AudioDocument(Tone(Rate * 2, 440), Rate, 24), wav, 24, dither: false);

        var montage = new MontageDocument(Rate, 2) { Title = "Side A" };
        int s = montage.AddSource(MontageSource.Load(wav, Rate, 2));
        montage.Add(new MontageClip
        {
            SourceIndex = s, Name = "Opening", Length = Rate, TimelineStart = 0,
            SourceStart = 100, GainDb = -3.5,
            FadeInSamples = 512, FadeOutSamples = 1_024,
            FadeInShape = FadeShape.SCurve, FadeOutShape = FadeShape.DecibelLinear,
        });
        montage.Add(new MontageClip
        {
            SourceIndex = s, Name = "Second", Length = Rate, TimelineStart = Rate - 4_410,
        });

        string path = At("side-a" + MontageStore.Extension);
        MontageStore.Save(montage, path);
        MontageLoadResult loaded = MontageStore.Load(path);

        Assert.Empty(loaded.MissingSources);
        Assert.Equal("Side A", loaded.Montage.Title);
        Assert.Equal(2, loaded.Montage.Clips.Count);
        Assert.Single(loaded.Montage.Sources);

        MontageClip first = loaded.Montage.Clips[0];
        Assert.Equal("Opening", first.Name);
        Assert.Equal(100, first.SourceStart);
        Assert.Equal(-3.5, first.GainDb, 6);
        Assert.Equal(512, first.FadeInSamples);
        Assert.Equal(FadeShape.SCurve, first.FadeInShape);
        Assert.Equal(FadeShape.DecibelLinear, first.FadeOutShape);
        Assert.Equal(montage.Length, loaded.Montage.Length);
    }

    /// <summary>The audio is not in the file — that is the point of referencing rather than copying.</summary>
    [Fact]
    public void TheMontageFileHoldsDecisionsNotAudio()
    {
        string wav = At("big.wav");
        WavCodec.Save(new AudioDocument(Tone(Rate * 10, 440), Rate, 24), wav, 24, dither: false);

        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(MontageSource.Load(wav, Rate, 2));
        montage.Append(s);

        string path = At("small" + MontageStore.Extension);
        MontageStore.Save(montage, path);

        long audioBytes = new FileInfo(wav).Length;
        long montageBytes = new FileInfo(path).Length;
        output.WriteLine($"{audioBytes / 1024} kB of audio described by {montageBytes} bytes");
        Assert.True(montageBytes < 2_048, $"the montage file was {montageBytes} bytes");
    }

    /// <summary>
    /// A montage and its audio should survive being moved together, so paths are stored relative
    /// where a relative form exists.
    /// </summary>
    [Fact]
    public void AMontageSurvivesBeingMovedWithItsAudio()
    {
        string first = Path.Combine(_directory, "before");
        string second = Path.Combine(_directory, "after");
        Directory.CreateDirectory(first);

        string wav = Path.Combine(first, "tone.wav");
        WavCodec.Save(new AudioDocument(Tone(Rate, 440), Rate, 24), wav, 24, dither: false);

        var montage = new MontageDocument(Rate, 2);
        montage.Append(montage.AddSource(MontageSource.Load(wav, Rate, 2)));
        MontageStore.Save(montage, Path.Combine(first, "m" + MontageStore.Extension));

        Directory.Move(first, second);
        MontageLoadResult loaded = MontageStore.Load(Path.Combine(second, "m" + MontageStore.Extension));

        Assert.Empty(loaded.MissingSources);
        Assert.Equal(Rate, loaded.Montage.Length);
    }

    /// <summary>
    /// A source that has gone missing is named, and its clips are kept — the arrangement is the
    /// work, and it should survive a file being moved out from under it.
    /// </summary>
    [Fact]
    public void AMissingSourceIsReportedAndItsClipsSurvive()
    {
        string wav = At("temporary.wav");
        WavCodec.Save(new AudioDocument(Tone(Rate, 440), Rate, 24), wav, 24, dither: false);

        var montage = new MontageDocument(Rate, 2);
        int s = montage.AddSource(MontageSource.Load(wav, Rate, 2));
        montage.Append(s);
        montage.Append(s, overlapSamples: 1_000);

        string path = At("orphan" + MontageStore.Extension);
        MontageStore.Save(montage, path);
        File.Delete(wav);

        MontageLoadResult loaded = MontageStore.Load(path);

        output.WriteLine($"missing: {string.Join(", ", loaded.MissingSources.Select(Path.GetFileName))}");
        Assert.Single(loaded.MissingSources);
        Assert.Equal(2, loaded.Montage.Clips.Count);
        Assert.Single(loaded.Montage.Sources);
    }

    /// <summary>
    /// Clips name sources by index, so a source that failed to load must still take up its place or
    /// every clip after it silently re-points at the wrong audio.
    /// </summary>
    [Fact]
    public void AMissingSourceStillHoldsItsPlaceInTheIndex()
    {
        string missing = At("gone.wav");
        string kept = At("kept.wav");
        WavCodec.Save(new AudioDocument(Tone(Rate, 440), Rate, 24), missing, 24, dither: false);
        WavCodec.Save(new AudioDocument(Tone(Rate, 880), Rate, 24), kept, 24, dither: false);

        var montage = new MontageDocument(Rate, 2);
        int first = montage.AddSource(MontageSource.Load(missing, Rate, 2));
        int second = montage.AddSource(MontageSource.Load(kept, Rate, 2));
        montage.Add(new MontageClip { SourceIndex = second, Name = "the kept one", Length = Rate });
        Assert.Equal(0, first);

        string path = At("indexed" + MontageStore.Extension);
        MontageStore.Save(montage, path);
        File.Delete(missing);

        MontageLoadResult loaded = MontageStore.Load(path);

        Assert.Equal(2, loaded.Montage.Sources.Count);
        Assert.Equal(1, loaded.Montage.Clips[0].SourceIndex);
        Assert.Equal("kept", loaded.Montage.Sources[1].Name);
    }
}
