using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The linear edit history the Edit History panel is built on: the timeline it reads, jumping to a
/// point on it, and discarding a step and its tail.
/// </summary>
/// <remarks>
/// <see cref="AudioDocument.UndoBudgetBytes"/> is static, so these share the budget collection with
/// <see cref="UndoBudgetTests"/> and restore it afterwards.
/// </remarks>
[Collection(UndoBudgetCollection.Name)]
public sealed class EditHistoryTests : IDisposable
{
    private readonly long _originalBudget = AudioDocument.UndoBudgetBytes;

    public void Dispose() => AudioDocument.UndoBudgetBytes = _originalBudget;

    private static AudioDocument Document(int frames = 400)
    {
        var channels = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            channels[c] = new float[frames];
            for (int i = 0; i < frames; i++) channels[c][i] = i * 0.001f + c;
        }
        return new AudioDocument(channels, 44_100, 32);
    }

    /// <summary>A same-length replacement, so the timeline never moves and every edit costs alike.</summary>
    private static void Edit(AudioDocument document, string name, int start = 0, int frames = 40, float value = 1f)
    {
        var replacement = new float[document.ChannelCount][];
        for (int c = 0; c < replacement.Length; c++)
        {
            replacement[c] = new float[frames];
            Array.Fill(replacement[c], value);
        }
        document.ReplaceRange(start, frames, replacement, name);
    }

    /// <summary>A splice that changes the length, which is what makes composition interesting.</summary>
    private static void Splice(AudioDocument document, string name, int start, int remove, int insert)
    {
        var replacement = new float[document.ChannelCount][];
        for (int c = 0; c < replacement.Length; c++)
        {
            replacement[c] = new float[insert];
            Array.Fill(replacement[c], 0.5f + c);
        }
        document.ReplaceRange(start, remove, replacement, name);
    }

    private static float[][] Snapshot(AudioDocument document)
    {
        var copy = new float[document.ChannelCount][];
        for (int c = 0; c < copy.Length; c++) copy[c] = [.. document.Channels[c]];
        return copy;
    }

    // ── the timeline ────────────────────────────────────────────

    /// <summary>
    /// <c>_redo</c> is a stack, so it is stored back to front. A timeline that simply concatenated
    /// the two lists would look entirely plausible and jump to the wrong state.
    /// </summary>
    [Fact]
    public void TheTimelineListsUndoneStepsAfterTheCurrentPositionInTheOrderRedoWouldReapplyThem()
    {
        var document = Document();
        Edit(document, "first");
        Edit(document, "second");
        Edit(document, "third");
        document.Undo();
        document.Undo();

        var history = document.GetHistory();

        Assert.Equal(["first", "second", "third"], history.Entries.Select(e => e.Name));
        Assert.Equal(1, history.Position);
        Assert.True(history.Entries[0].IsApplied);
        Assert.True(history.Entries[0].IsCurrent);
        Assert.False(history.Entries[1].IsApplied);
        Assert.False(history.Entries[2].IsApplied);
    }

    [Fact]
    public void TheBaselineIsTheSavepointOfAFreshlyOpenedDocument()
    {
        var document = Document();
        var history = document.GetHistory();

        Assert.Empty(history.Entries);
        Assert.Equal(0, history.Position);
        Assert.True(history.BaselineIsSavepoint);
        Assert.True(history.SavepointReachable);
    }

    /// <summary>The saved mark follows the state it was taken at, wherever that sits afterwards.</summary>
    [Fact]
    public void TheSavedStepIsMarkedWhereverItSitsInTheTimeline()
    {
        var document = Document();
        Edit(document, "first");
        Edit(document, "second");
        document.MarkSaved();
        Edit(document, "third");

        Assert.True(document.GetHistory().Entries[1].IsSavepoint);

        document.JumpToHistoryPosition(1);
        Assert.True(document.Dirty);

        document.JumpToHistoryPosition(2);
        Assert.False(document.Dirty);
        Assert.True(document.GetHistory().Entries[1].IsSavepoint);
    }

    // ── jumping ─────────────────────────────────────────────────

    /// <summary>
    /// A jump has to be exactly the run of steps it replaces, or the panel is a different editor
    /// from Ctrl+Z. Compared sample for sample against a second document driven the long way.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void JumpingLandsOnExactlyTheSamplesSteppingWouldHaveReached(int position)
    {
        AudioDocument Build()
        {
            var document = Document();
            Splice(document, "insert", 40, 0, 25);
            Edit(document, "gain", 10, 30, 0.7f);
            Splice(document, "cut", 120, 35, 0);
            Edit(document, "fade", 200, 40, 0.2f);
            return document;
        }

        var jumped = Build();
        var stepped = Build();

        jumped.JumpToHistoryPosition(position);
        while (stepped.HistoryPosition > position) stepped.Undo();

        Assert.Equal(stepped.Length, jumped.Length);
        for (int c = 0; c < stepped.ChannelCount; c++)
            Assert.Equal(stepped.Channels[c], jumped.Channels[c]);

        // And forward again, by the same comparison.
        jumped.JumpToHistoryPosition(4);
        while (stepped.HistoryPosition < 4) stepped.Redo();
        Assert.Equal(stepped.Length, jumped.Length);
        for (int c = 0; c < stepped.ChannelCount; c++)
            Assert.Equal(stepped.Channels[c], jumped.Channels[c]);
    }

    [Fact]
    public void AJumpAcrossManyStepsRaisesExactlyOneChangeAndBumpsTheVersionOnce()
    {
        var document = Document();
        for (int i = 0; i < 5; i++) Edit(document, $"edit {i}", i * 10, 20, i * 0.1f);

        int changes = 0;
        document.Changed += (_, _, _) => changes++;
        int before = document.EditVersion;

        int moved = document.JumpToHistoryPosition(0);

        Assert.Equal(-5, moved);
        Assert.Equal(1, changes);
        Assert.Equal(before + 1, document.EditVersion);
    }

    /// <summary>
    /// The load-bearing one. The single event carries the composition of the whole run, and
    /// everything anchored to the timeline — cursor, selection, markers, regions — is remapped
    /// through it. A wrong composition produces a span that looks reasonable and silently moves
    /// metadata to the wrong samples, so this asserts the definition rather than a magic triple.
    /// </summary>
    [Fact]
    public void TheSingleChangeEventSpansEveryRegionTheRunTouched()
    {
        var document = Document(600);
        Splice(document, "first cut", 80, 40, 10);
        Splice(document, "second insert", 300, 0, 55);
        Splice(document, "third cut", 450, 30, 5);

        var before = Snapshot(document);
        (int start, int removed, int inserted) = (-1, -1, -1);
        document.Changed += (s, r, i) => (start, removed, inserted) = (s, r, i);

        document.JumpToHistoryPosition(0);
        var after = Snapshot(document);

        Assert.True(start >= 0 && removed >= 0 && inserted >= 0);
        Assert.Equal(before[0].Length - removed + inserted, after[0].Length);

        for (int c = 0; c < before.Length; c++)
        {
            for (int i = 0; i < start; i++)
            {
                Assert.True(
                    before[c][i] == after[c][i],
                    $"channel {c} sample {i} is inside the reported prefix but differs.");
            }

            int oldTail = before[c].Length - (start + removed);
            int newTail = after[c].Length - (start + inserted);
            Assert.Equal(oldTail, newTail);
            for (int i = 0; i < oldTail; i++)
            {
                Assert.True(
                    before[c][start + removed + i] == after[c][start + inserted + i],
                    $"channel {c} suffix sample {i} is outside the reported span but differs.");
            }
        }
    }

    /// <summary>
    /// A run of same-length edits must compose to a span of equal lengths, because that is what
    /// tells the view model the timeline did not move and no marker needs touching.
    /// </summary>
    [Fact]
    public void ARunOfSameLengthEditsComposesToASpanThatMovesNothing()
    {
        var document = Document();
        Edit(document, "one", 10, 20, 0.3f);
        Edit(document, "two", 200, 40, 0.6f);
        Edit(document, "three", 100, 15, 0.9f);

        (int removed, int inserted) = (-1, -2);
        document.Changed += (_, r, i) => (removed, inserted) = (r, i);
        document.JumpToHistoryPosition(0);

        Assert.Equal(removed, inserted);
    }

    [Fact]
    public void AJumpAcrossAWholeDocumentRenderRestoresTheChannelCount()
    {
        var document = new AudioDocument([new float[100]], 44_100, 32);
        document.ReplaceAllOwned([new float[80], new float[80]], "Render Master Chain");
        Edit(document, "gain", 0, 40, 0.5f);

        document.JumpToHistoryPosition(0);

        Assert.Equal(1, document.ChannelCount);
        Assert.Equal(100, document.Length);
    }

    [Fact]
    public void JumpingToWhereYouAlreadyAreChangesNothing()
    {
        var document = Document();
        Edit(document, "only");

        int changes = 0;
        document.Changed += (_, _, _) => changes++;
        int before = document.EditVersion;

        Assert.Equal(0, document.JumpToHistoryPosition(document.HistoryPosition));
        Assert.Equal(0, changes);
        Assert.Equal(before, document.EditVersion);
    }

    /// <summary>
    /// Refused rather than clamped. The panel is modeless and its indices go stale under an
    /// eviction, and a silently wrong jump is much harder to notice than a thrown one.
    /// </summary>
    [Fact]
    public void APositionOutsideTheTimelineIsRefusedRatherThanClamped()
    {
        var document = Document();
        Edit(document, "only");

        Assert.Throws<ArgumentOutOfRangeException>(() => document.JumpToHistoryPosition(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.JumpToHistoryPosition(2));
    }

    /// <summary>
    /// The budget is enforced once, at the end of a jump rather than per step. This is the
    /// <see cref="UndoBudgetTests"/> assertion reached the new way: a jump must not be a hole in the
    /// memory limit.
    /// </summary>
    [Fact]
    public void JumpingDoesNotLetRetainedHistoryGrowWithoutBound()
    {
        const int frames = 2_000;
        long perEdit = 2L * 2 * frames * sizeof(float);
        AudioDocument.UndoBudgetBytes = 3 * perEdit;

        var document = Document(8_000);
        for (int i = 0; i < 12; i++) Edit(document, $"edit {i}", 0, frames, i * 0.01f);

        document.JumpToHistoryPosition(0);

        Assert.True(
            document.RetainedHistoryBytes <= AudioDocument.UndoBudgetBytes,
            $"a jump left {document.RetainedHistoryBytes} bytes retained against a "
            + $"{AudioDocument.UndoBudgetBytes} byte budget.");
    }

    [Fact]
    public void HistoryReportsWhatTheBudgetReleased()
    {
        const int frames = 2_000;
        long perEdit = 2L * 2 * frames * sizeof(float);
        AudioDocument.UndoBudgetBytes = 3 * perEdit;

        var document = Document(8_000);
        for (int i = 0; i < 10; i++) Edit(document, $"edit {i}", 0, frames, i * 0.01f);

        var history = document.GetHistory();

        Assert.True(history.DiscardedOlderSteps > 0);
        Assert.NotEqual(0, history.Generation);
        Assert.Equal(document.HistoryCount, history.Entries.Count);
        Assert.Equal(document.RetainedHistoryBytes, history.RetainedBytes);
    }

    // ── discarding ──────────────────────────────────────────────

    [Fact]
    public void DeletingFromAStepDiscardsItAndEverythingAfterIt()
    {
        var document = Document();
        Edit(document, "first");
        Edit(document, "second");
        Edit(document, "third");

        Assert.True(document.TruncateHistoryFrom(1));

        Assert.Equal(1, document.HistoryCount);
        Assert.Equal(1, document.HistoryPosition);
        Assert.False(document.CanRedo);
        Assert.Equal("first", document.NextUndoName);
    }

    [Fact]
    public void DeletingFromAnUndoneStepLeavesTheAppliedOnesAlone()
    {
        var document = Document();
        Edit(document, "first");
        Edit(document, "second");
        Edit(document, "third");
        document.JumpToHistoryPosition(1);

        Assert.True(document.TruncateHistoryFrom(2));

        Assert.Equal(2, document.HistoryCount);
        Assert.Equal(1, document.HistoryPosition);
        Assert.Equal(["first", "second"], document.GetHistory().Entries.Select(e => e.Name));
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void DeletingAStepThatIsStillAppliedTakesItOutOfTheAudioFirst()
    {
        var document = Document();
        var opened = Snapshot(document);
        Edit(document, "first", 0, 40, 0.25f);
        Edit(document, "second", 0, 40, 0.75f);

        document.TruncateHistoryFrom(0);

        Assert.Equal(0, document.HistoryCount);
        for (int c = 0; c < opened.Length; c++) Assert.Equal(opened[c], document.Channels[c]);
    }

    /// <summary>
    /// Discarding the step that produced the last saved state means the file can no longer be
    /// brought back to the bytes on disk, and it has to keep saying so.
    /// </summary>
    [Fact]
    public void DeletingTheStepThatWasSavedLeavesTheDocumentPermanentlyUnsaved()
    {
        var document = Document();
        Edit(document, "first");
        Edit(document, "second");
        document.MarkSaved();
        Edit(document, "third");

        document.TruncateHistoryFrom(1);

        Assert.True(document.Dirty);
        Assert.False(document.GetHistory().SavepointReachable);

        for (int position = 0; position <= document.HistoryCount; position++)
        {
            document.JumpToHistoryPosition(position);
            Assert.True(document.Dirty, $"position {position} cleared the dirty mark it cannot reach.");
        }
    }

    /// <summary>
    /// Editing after an undo throws the forward chain away, and the last saved state can be one of
    /// the steps it takes with it. Leaving the id behind would make "the savepoint is reachable or
    /// absent" merely nearly true, with only the snapshot knowing the difference.
    /// </summary>
    [Fact]
    public void EditingAfterAnUndoGivesUpASavepointTheForwardChainTookWithIt()
    {
        var document = Document();
        Edit(document, "first");
        document.MarkSaved();
        Assert.False(document.Dirty);

        document.Undo();
        Assert.True(document.Dirty);

        Edit(document, "second");

        var history = document.GetHistory();
        Assert.False(history.SavepointReachable);
        Assert.DoesNotContain(history.Entries, entry => entry.IsSavepoint);
        Assert.False(history.BaselineIsSavepoint);

        // And it stays gone: no position on the timeline clears the mark.
        for (int position = 0; position <= document.HistoryCount; position++)
        {
            document.JumpToHistoryPosition(position);
            Assert.True(document.Dirty, $"position {position} cleared a savepoint it cannot reach.");
        }
    }

    [Fact]
    public void AnIndexOutsideTheTimelineIsNotADiscard()
    {
        var document = Document();
        Edit(document, "only");

        Assert.False(document.TruncateHistoryFrom(-1));
        Assert.False(document.TruncateHistoryFrom(1));
        Assert.Equal(1, document.HistoryCount);
    }
}
