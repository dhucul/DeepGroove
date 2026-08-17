using WaveLab.Audio;
using WaveLab.Audio.Montage;
using WaveLab.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The montage's view model and the tab model it shares with audio documents.
/// </summary>
public sealed class MontageViewTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    private static float[][] Tone(int frames, double frequency, double amplitude = 0.5, double phase = 0)
    {
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate + phase));
        }
        return data;
    }

    private static MontageViewModel Montage(out MontageDocument document, int frames = Rate * 4)
    {
        document = new MontageDocument(Rate, 2) { Title = "Side A" };
        document.AddSource(MontageSource.From(Tone(frames, 440), Rate, Rate, 2, "take one"));
        return new MontageViewModel(document);
    }

    // ── signed correlation ───────────────────────────────────────

    /// <summary>
    /// Uncorrelated and anti-correlated both floor to zero for the law, and they are opposite
    /// situations: one is the ordinary case and the other needs a polarity fix. Reporting them as
    /// the same number flagged every good join between two unrelated pieces as a fault.
    /// </summary>
    [Fact]
    public void UnrelatedAndCancellingMaterialAreToldApart()
    {
        float[][] a = Tone(8_192, 440);
        float[][] unrelated = Tone(8_192, 1_100);
        float[][] inverted = Tone(8_192, 440, phase: Math.PI);

        double unrelatedSigned = Crossfade.MeasureSignedCorrelation(a, 0, unrelated, 0, 8_192);
        double invertedSigned = Crossfade.MeasureSignedCorrelation(a, 0, inverted, 0, 8_192);

        output.WriteLine($"unrelated {unrelatedSigned:0.000}, inverted {invertedSigned:0.000}");
        Assert.True(Math.Abs(unrelatedSigned) < 0.02, $"unrelated measured {unrelatedSigned}");
        Assert.True(invertedSigned < -0.95, $"inverted measured {invertedSigned}");

        // The law sees the same floored value for both, which is what it must do.
        Assert.Equal(0, Crossfade.MeasureCorrelation(a, 0, inverted, 0, 8_192));
        Assert.True(Crossfade.MeasureCorrelation(a, 0, unrelated, 0, 8_192) < 0.02);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.003, false)]
    [InlineData(-0.01, false)]
    [InlineData(-0.4, true)]
    [InlineData(-1.0, true)]
    public void OnlyANegativeMeasurementCountsAsCancelling(double correlation, bool cancels)
    {
        var info = new MontageCrossfadeInfo("next", 1_000, 0.02, correlation, FadeShape.EqualPower);
        Assert.Equal(cancels, info.Cancels);
        Assert.Equal(Math.Max(0, correlation), info.EffectiveCorrelation);
    }

    [Theory]
    [InlineData(0.0, "equal power")]
    [InlineData(-0.5, "equal power")]
    [InlineData(0.3, "between the two")]
    [InlineData(0.7, "nearer equal gain")]
    [InlineData(0.95, "equal gain")]
    public void TheLawIsNamedInTheWordsAUserThinksIn(double correlation, string expected) =>
        Assert.Equal(expected, new MontageCrossfadeInfo("n", 100, 0.01, correlation, FadeShape.EqualPower).LawName);

    /// <summary>
    /// What the panel puts under the number: how far out the join would be under the fixed law that
    /// would otherwise have been chosen.
    /// </summary>
    [Fact]
    public void TheCostOfTheWrongLawIsReported()
    {
        var unrelated = new MontageCrossfadeInfo("n", 100, 0.01, 0.0, FadeShape.EqualPower);
        var identical = new MontageCrossfadeInfo("n", 100, 0.01, 1.0, FadeShape.EqualPower);

        output.WriteLine($"unrelated under equal gain: {unrelated.FixedLawErrorDb:+0.00;-0.00} dB");
        output.WriteLine($"identical under equal power: {identical.FixedLawErrorDb:+0.00;-0.00} dB");

        Assert.InRange(unrelated.FixedLawErrorDb, -2.6, -2.1);
        Assert.InRange(identical.FixedLawErrorDb, 2.7, 3.3);
    }

    // ── the view window ──────────────────────────────────────────

    [Fact]
    public void ZoomHoldsTheSampleUnderThePointerStill()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        document.Append(0);
        vm.ViewWidthPixels = 1_000;
        vm.SamplesPerPixel = 200;
        vm.ViewStart = 10_000;

        const double anchor = 40_000;
        double pixelBefore = vm.PixelOf(anchor);
        vm.Zoom(0.5, anchor);
        double pixelAfter = vm.PixelOf(anchor);

        output.WriteLine($"anchor at pixel {pixelBefore:0.0} → {pixelAfter:0.0}");
        Assert.True(Math.Abs(pixelBefore - pixelAfter) < 0.5);
    }

    [Fact]
    public void TheViewCannotScrollPastTheEndsOfTheMontage()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        document.Append(0);
        vm.ViewWidthPixels = 500;
        vm.SamplesPerPixel = 100;

        vm.ViewStart = -50_000;
        Assert.Equal(0, vm.ViewStart);

        vm.ViewStart = document.Length * 10;
        Assert.Equal(document.Length - 500 * 100, vm.ViewStart, 3);
    }

    [Fact]
    public void ZoomFullFitsTheWholeMontage()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        document.Append(0);
        vm.ViewWidthPixels = 800;
        vm.ZoomFull();

        Assert.Equal(0, vm.ViewStart);
        Assert.InRange(vm.SamplesPerPixel * 800, document.Length - 1, document.Length + 1);
    }

    // ── edits ────────────────────────────────────────────────────

    [Fact]
    public void SplittingCutsTheClipAndKeepsBothHalvesOnTheSameAudio()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        MontageClip left = document.Append(0);
        left.FadeInSamples = 100;
        left.FadeOutSamples = 200;

        MontageClip? right = vm.SplitClip(left, Rate);

        Assert.NotNull(right);
        Assert.Equal(Rate, left.Length);
        Assert.Equal(Rate, right!.TimelineStart);
        Assert.Equal(Rate, right.SourceStart);
        Assert.Equal(document.Sources[0].Length - Rate, right.Length);

        // The cut is in the middle of what was continuous, so neither half gets a fade there.
        Assert.Equal(0, left.FadeOutSamples);
        Assert.Equal(0, right.FadeInSamples);
        Assert.Equal(100, left.FadeInSamples);
    }

    [Fact]
    public void SplittingOutsideTheClipDoesNothing()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        MontageClip clip = document.Append(0);

        Assert.Null(vm.SplitClip(clip, clip.TimelineStart));
        Assert.Null(vm.SplitClip(clip, clip.TimelineEnd));
        Assert.Single(document.Clips);
    }

    /// <summary>
    /// Trimming the head moves the clip's start in its source too, so the audio under the pointer
    /// stays where it is rather than sliding along the lane.
    /// </summary>
    [Fact]
    public void TrimmingTheHeadKeepsTheAudioStill()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        vm.SnapToZeroCrossing = false;
        MontageClip clip = document.Append(0);
        clip.TimelineStart = 10_000;
        clip.SourceStart = 0;
        int end = clip.TimelineEnd;

        vm.TrimClip(clip, head: true, 15_000);

        Assert.Equal(15_000, clip.TimelineStart);
        Assert.Equal(5_000, clip.SourceStart);
        Assert.Equal(end, clip.TimelineEnd);
    }

    [Fact]
    public void TrimmingTheTailShortensTheClipWithoutMovingIt()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        vm.SnapToZeroCrossing = false;
        MontageClip clip = document.Append(0);

        vm.TrimClip(clip, head: false, Rate);

        Assert.Equal(0, clip.TimelineStart);
        Assert.Equal(Rate, clip.Length);
        Assert.Equal(0, clip.SourceStart);
    }

    [Fact]
    public void AClipCannotBeTrimmedToNothingOrDraggedBeforeZero()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        vm.SnapToZeroCrossing = false;
        MontageClip clip = document.Append(0);

        vm.TrimClip(clip, head: false, -50_000);
        Assert.True(clip.Length >= 1);

        vm.MoveClip(clip, -1_000);
        Assert.Equal(0, clip.TimelineStart);
    }

    /// <summary>
    /// A rising zero crossing specifically. Any near-zero sample would do in a quiet passage, and
    /// the edge would then wander to wherever the noise happened to be smallest.
    /// </summary>
    [Fact]
    public void SnappingFindsARisingZeroCrossing()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        float[] channel = document.Sources[0].Channels[0];

        int asked = 1_000;
        int snapped = vm.SnapSource(0, asked);

        output.WriteLine($"asked {asked}, snapped {snapped}, " +
                         $"sample {channel[snapped]:0.0000} after {channel[snapped - 1]:0.0000}");

        Assert.True(Math.Abs(snapped - asked) <= 512);
        Assert.True(channel[snapped - 1] <= 0 && channel[snapped] >= 0,
            "the snap should land on a rising crossing");
    }

    [Fact]
    public void SnappingIsSkippedWhenItIsTurnedOff()
    {
        MontageViewModel vm = Montage(out _);
        vm.SnapToZeroCrossing = false;
        Assert.Equal(1_000, vm.SnapSource(0, 1_000));
    }

    [Fact]
    public void AnEditMarksTheMontageAndBumpsTheRevision()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        MontageClip clip = document.Append(0);
        vm.MarkSaved();

        int revision = vm.Revision;
        vm.MoveClip(clip, 500);

        Assert.True(vm.IsDirty);
        Assert.True(vm.Revision > revision);

        vm.MarkSaved();
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void RemovingTheSelectionClearsIt()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        vm.Selected = document.Append(0);
        Assert.True(vm.HasSelection);

        vm.RemoveSelected();
        Assert.False(vm.HasSelection);
        Assert.Empty(document.Clips);
    }

    // ── the crossfade the inspector shows ────────────────────────

    [Fact]
    public void TheSelectedClipsJoinIsMeasuredFromTheAudioAtTheJoin()
    {
        var document = new MontageDocument(Rate, 2) { Title = "Side A" };
        int a = document.AddSource(MontageSource.From(Tone(Rate * 2, 440), Rate, Rate, 2, "a"));
        int b = document.AddSource(MontageSource.From(Tone(Rate * 2, 1_100), Rate, Rate, 2, "b"));

        var vm = new MontageViewModel(document);
        MontageClip first = document.Add(new MontageClip { SourceIndex = a, Length = Rate * 2 });
        document.Add(new MontageClip { SourceIndex = b, Length = Rate * 2, TimelineStart = Rate });
        vm.Selected = first;

        MontageCrossfadeInfo? crossfade = vm.SelectedCrossfade;
        Assert.NotNull(crossfade);
        output.WriteLine($"ρ {crossfade!.Correlation:0.000}, {crossfade.OverlapSeconds:0.000} s, " +
                         $"{crossfade.LawName}");

        Assert.Equal(Rate, crossfade.OverlapSamples);
        Assert.Equal(1.0, crossfade.OverlapSeconds, 3);
        Assert.True(Math.Abs(crossfade.Correlation) < 0.05);
        Assert.Equal("equal power", crossfade.LawName);
        Assert.False(crossfade.Cancels);
    }

    [Fact]
    public void AClipWithNoJoinAfterItHasNoCrossfadePanel()
    {
        MontageViewModel vm = Montage(out MontageDocument document);
        vm.Selected = document.Append(0);
        Assert.Null(vm.SelectedCrossfade);

        document.Append(0);                      // butted, not overlapping
        Assert.Null(vm.SelectedCrossfade);
    }

    // ── the tab model ────────────────────────────────────────────

    /// <summary>
    /// The point of the base class: a montage tab makes <c>ActiveDocument</c> null, so every audio
    /// command becomes unavailable rather than operating on the tab the user has just left.
    /// </summary>
    [Fact]
    public void SelectingAMontageTabLeavesNoActiveDocument()
    {
        var main = new MainViewModel();
        var document = new DocumentViewModel(
            new AudioDocument([new float[1_000], new float[1_000]], Rate, 32));
        main.Documents.Add(document);
        main.ActiveTab = document;

        Assert.Same(document, main.ActiveDocument);
        Assert.True(main.HasDocument);
        Assert.Null(main.ActiveMontage);

        MontageViewModel montage = main.AddMontage(new MontageDocument(Rate, 2));

        Assert.Same(montage, main.ActiveTab);
        Assert.Null(main.ActiveDocument);
        Assert.False(main.HasDocument);
        Assert.False(main.HasAudioDocument);
        Assert.True(main.HasMontage);

        main.ActiveTab = document;
        Assert.Same(document, main.ActiveDocument);
        Assert.False(main.HasMontage);
    }

    [Fact]
    public void SettingTheActiveDocumentAlsoSelectsItsTab()
    {
        var main = new MainViewModel();
        var document = new DocumentViewModel(
            new AudioDocument([new float[1_000], new float[1_000]], Rate, 32));
        main.Documents.Add(document);
        main.AddMontage(new MontageDocument(Rate, 2));

        main.ActiveDocument = document;

        Assert.Same(document, main.ActiveTab);
        Assert.Null(main.ActiveMontage);
    }

    [Fact]
    public void TheTabStripKnowsWhatEachTabIs()
    {
        var main = new MainViewModel();
        var document = new DocumentViewModel(
            new AudioDocument([new float[10]], Rate, 32) { Title = "Side A.wav" });
        main.Documents.Add(document);
        MontageViewModel montage = main.AddMontage(
            new MontageDocument(Rate, 2) { Title = "Side A montage" });

        Assert.Equal("WAV", document.Kind);
        Assert.Equal("Side A.wav", document.Title);
        Assert.Equal("MONTAGE", montage.Kind);
        Assert.Equal("Side A montage", montage.Title);

        // The tab's own dirty mark is the strip's amber dot, so the title must not carry one too.
        Assert.DoesNotContain("•", document.Title);
        Assert.DoesNotContain("•", montage.Title);
    }

    [Fact]
    public void AudioDocumentsAreNotTheSameThingAsTabs()
    {
        var main = new MainViewModel();
        main.Documents.Add(new DocumentViewModel(new AudioDocument([new float[10]], Rate, 32)));
        main.AddMontage(new MontageDocument(Rate, 2));
        main.Documents.Add(new DocumentViewModel(new AudioDocument([new float[10]], Rate, 32)));

        Assert.Equal(3, main.Documents.Count);
        Assert.Equal(2, main.AudioDocuments.Count());
    }
}
