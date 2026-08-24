using WaveLab.Audio;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// <see cref="AudioDocument.UndoBudgetBytes"/> is static, so these run alone.
/// </summary>
[Collection(UndoBudgetCollection.Name)]
public sealed class UndoBudgetTests : IDisposable
{
    private readonly long _originalBudget = AudioDocument.UndoBudgetBytes;

    public void Dispose() => AudioDocument.UndoBudgetBytes = _originalBudget;

    private const int EditFrames = 10_000;

    /// <summary>
    /// What one edit retains: the removed range and the inserted range, both channels. Splitting it
    /// out keeps the budgets below readable as a number of edits rather than a pile of factors.
    /// </summary>
    private const long PerEditBytes = 2L * 2 * EditFrames * sizeof(float);

    private static AudioDocument Document(int frames = 40_000) =>
        new([new float[frames], new float[frames]], 44_100, 32);

    /// <summary>Replaces a slice with an equally sized one, so every edit costs the same.</summary>
    private static void Edit(AudioDocument document, int frames, float value)
    {
        var replacement = new float[document.ChannelCount][];
        for (int c = 0; c < replacement.Length; c++)
        {
            replacement[c] = new float[frames];
            Array.Fill(replacement[c], value);
        }
        document.ReplaceRange(0, frames, replacement, "test edit");
    }

    [Fact]
    public void EditingBeyondTheBudgetEvictsTheOldestHistory()
    {
        AudioDocument.UndoBudgetBytes = 2 * PerEditBytes;
        var document = Document();

        for (int i = 0; i < 20; i++) Edit(document, EditFrames, i * 0.01f);

        Assert.True(document.UndoDepth >= 1);
        Assert.True(document.UndoDepth <= 2, $"undo depth {document.UndoDepth} exceeds the budget");
    }

    [Fact]
    public void AtLeastOneUndoStepSurvivesAnEditLargerThanTheWholeBudget()
    {
        AudioDocument.UndoBudgetBytes = 1;
        var document = Document();

        Edit(document, EditFrames, 0.5f);
        Edit(document, EditFrames, 0.25f);

        Assert.Equal(1, document.UndoDepth);
        document.Undo();
    }

    /// <summary>
    /// The defect this covers: undoing moves edits onto the redo stack, which used to be neither
    /// counted nor bounded, so a long undo run grew memory without limit while the document still
    /// reported itself inside budget.
    /// </summary>
    [Fact]
    public void UndoingDoesNotLetRetainedHistoryGrowWithoutBound()
    {
        AudioDocument.UndoBudgetBytes = 3 * PerEditBytes;
        var document = Document();

        for (int i = 0; i < 12; i++) Edit(document, EditFrames, i * 0.01f);
        int afterEditing = document.UndoDepth + document.RedoDepth;

        for (int i = 0; i < 12; i++) document.Undo();

        int afterUndoing = document.UndoDepth + document.RedoDepth;
        Assert.True(afterUndoing <= afterEditing + 1,
            $"history grew from {afterEditing} to {afterUndoing} entries while undoing");
    }

    [Fact]
    public void RedoStillWorksForTheMostRecentUndoWithinBudget()
    {
        AudioDocument.UndoBudgetBytes = 512L * 1024 * 1024;
        var document = Document();

        Edit(document, EditFrames, 0.5f);
        Edit(document, EditFrames, 0.25f);
        document.Undo();

        Assert.Equal(1, document.RedoDepth);
        document.Redo();
        Assert.Equal(0, document.RedoDepth);
        Assert.Equal(0.25f, document.Channels[0][0], 5);
    }

    [Fact]
    public void AGenerousBudgetRetainsEveryStep()
    {
        AudioDocument.UndoBudgetBytes = 512L * 1024 * 1024;
        var document = Document();

        for (int i = 0; i < 8; i++) Edit(document, 1_000, i * 0.1f);

        Assert.Equal(8, document.UndoDepth);
    }

    // ── shared buffers ──────────────────────────────────────────

    /// <summary>What one whole-document render's worth of samples costs, both channels.</summary>
    private const long DocumentBytes = 2L * EditFrames * sizeof(float);

    /// <summary>Replaces the whole document, the way an offline render commits.</summary>
    private static void Render(AudioDocument document, float value)
    {
        var data = new float[document.ChannelCount][];
        for (int c = 0; c < data.Length; c++)
        {
            data[c] = new float[EditFrames];
            Array.Fill(data[c], value);
        }
        document.ReplaceAllOwned(data, $"render {value:0.00}");
    }

    /// <summary>
    /// <see cref="AudioDocument.ReplaceAllOwned"/> hands the outgoing render to the next edit as its
    /// <c>Old</c> side — the same object that edit kept as its <c>New</c> — so a chain of N renders
    /// holds N+1 documents, not 2N.
    /// </summary>
    [Fact]
    public void ConsecutiveWholeDocumentRendersAreChargedOncePerBuffer()
    {
        AudioDocument.UndoBudgetBytes = long.MaxValue;
        var document = Document(EditFrames);

        for (int i = 0; i < 4; i++) Render(document, (i + 1) * 0.1f);

        Assert.Equal(5 * DocumentBytes, document.RetainedHistoryBytes);
    }

    /// <summary>The panel's rows have to add up to the figure in its header.</summary>
    [Fact]
    public void TheStepsSumToWhatTheHistorySaysItRetains()
    {
        AudioDocument.UndoBudgetBytes = long.MaxValue;
        var document = Document(EditFrames);

        for (int i = 0; i < 4; i++) Render(document, (i + 1) * 0.1f);
        document.Undo();

        HistorySnapshot history = document.GetHistory();
        long summed = 0;
        foreach (HistoryEntry entry in history.Entries) summed += entry.RetainedBytes;

        Assert.Equal(history.RetainedBytes, summed);
        Assert.Equal(document.RetainedHistoryBytes, history.RetainedBytes);
    }

    /// <summary>
    /// The defect behind "I undid everything and the file was still changed": counting a shared
    /// array once per step that holds it reported twice the memory actually retained, so the budget
    /// released about twice as much history as the limit asked for.
    /// </summary>
    [Fact]
    public void TheBudgetDoesNotReleaseHistoryThatWouldNotActuallyBeFreed()
    {
        // Room for the original and four renders. Charged gross this reads as eight documents and
        // all four steps but one would go.
        AudioDocument.UndoBudgetBytes = 5 * DocumentBytes;
        var document = Document(EditFrames);

        for (int i = 0; i < 4; i++) Render(document, (i + 1) * 0.1f);

        Assert.Equal(4, document.UndoDepth);
        Assert.Equal(0, document.DiscardedOlderSteps);
        Assert.True(document.RetainedHistoryBytes <= AudioDocument.UndoBudgetBytes);

        for (int i = 0; i < 4; i++) document.Undo();

        Assert.False(document.CanUndo);
        Assert.Equal(0f, document.Channels[0][0]);
    }

    // ── saying so ───────────────────────────────────────────────

    [Fact]
    public void ReleasingHistoryIsAnnouncedRatherThanBeingSilent()
    {
        AudioDocument.UndoBudgetBytes = 2 * PerEditBytes;
        var document = Document();
        var released = new List<(int Older, int Newer)>();
        document.HistoryReleased += (older, newer) => released.Add((older, newer));

        for (int i = 0; i < 6; i++) Edit(document, EditFrames, i * 0.01f);

        Assert.NotEmpty(released);
        int announced = 0;
        foreach (var (older, _) in released) announced += older;
        Assert.Equal(document.DiscardedOlderSteps, announced);
        Assert.True(announced > 0, "steps were released from the oldest end without being announced");
    }

    /// <summary>
    /// Lowering the limit under a long history evicts the whole of it in one call, and that call is
    /// on the dispatcher.
    /// </summary>
    /// <remarks>
    /// A timing assertion, which this suite otherwise avoids — but the defect it guards is a
    /// complexity class rather than a constant, so it is the shape of the number that matters and
    /// the bound can be loose enough not to be flaky. Re-reading the deduplicated total after every
    /// drop measured <b>1 190 ms</b> here; counting references measures <b>2</b>. The ceiling is
    /// two orders of magnitude above the second and an order below the first.
    /// </remarks>
    [Fact]
    public void ReleasingAWholeHistoryAtOnceStaysLinearInItsDepth()
    {
        const int steps = 5_000;
        AudioDocument.UndoBudgetBytes = long.MaxValue;
        var document = Document(1_000);
        for (int i = 0; i < steps; i++) Edit(document, 100, i * 0.0001f);
        Assert.Equal(steps, document.UndoDepth);

        // What the Settings dialog does: lower the limit, and let the next edit pay for it.
        AudioDocument.UndoBudgetBytes = 8 * 1024;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Edit(document, 100, 1f);
        watch.Stop();

        Assert.True(document.DiscardedOlderSteps > steps - 10,
            $"only {document.DiscardedOlderSteps} of {steps} steps were released");
        Assert.True(watch.Elapsed.TotalMilliseconds < 150,
            $"releasing {document.DiscardedOlderSteps} steps took "
            + $"{watch.Elapsed.TotalMilliseconds:F0} ms, which is the quadratic shape again");
    }

    /// <summary>
    /// What the user actually hits. Undo runs out with the document still carrying the edits whose
    /// steps went, and the only thing that separates that from a finished undo is this count.
    /// </summary>
    [Fact]
    public void UndoingEverythingDoesNotReachTheOriginalOnceStepsHaveBeenReleased()
    {
        AudioDocument.UndoBudgetBytes = 2 * PerEditBytes;
        var document = Document();

        for (int i = 1; i <= 6; i++) Edit(document, EditFrames, i * 0.1f);
        while (document.CanUndo) document.Undo();

        Assert.True(document.DiscardedOlderSteps > 0);
        Assert.NotEqual(0f, document.Channels[0][0]);
    }
}

/// <summary>
/// The shell's half of the budget: an eviction has to reach the status line, whichever operation
/// caused it.
/// </summary>
/// <remarks>
/// The budget is enforced from inside the commit, before the change event the status line is
/// written from — so a history move that writes its own line afterwards used to overwrite the
/// eviction note and take it with it. Undo is the case that bites: it moves an edit onto the redo
/// stack, which is retained too, so undoing on a tight budget can itself release older steps.
/// </remarks>
[Collection(UndoBudgetCollection.Name)]
public sealed class UndoBudgetReportingTests : IDisposable
{
    private readonly long _originalBudget = AudioDocument.UndoBudgetBytes;

    public void Dispose() => AudioDocument.UndoBudgetBytes = _originalBudget;

    private const int Frames = 100;
    private static long PerEdit => 2L * 2 * Frames * sizeof(float);

    private static void Edit(AudioDocument document, float value)
    {
        var data = new float[document.ChannelCount][];
        for (int c = 0; c < data.Length; c++)
        {
            data[c] = new float[Frames];
            Array.Fill(data[c], value);
        }
        document.ReplaceRange(0, Frames, data, $"edit {value:0.0}");
    }

    private static MainViewModel Loaded()
    {
        AudioDocument.UndoBudgetBytes = long.MaxValue;
        var document = new AudioDocument([new float[1_000], new float[1_000]], 44_100, 32);
        var vm = new MainViewModel();
        var tab = new DocumentViewModel(document);
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        for (int i = 1; i <= 6; i++) Edit(document, i * 0.1f);
        return vm;
    }

    [Fact]
    public void AnEvictionDuringUndoReachesTheStatusLine()
    {
        MainViewModel vm = Loaded();
        AudioDocument doc = vm.ActiveDocument!.Doc;

        AudioDocument.UndoBudgetBytes = 3 * PerEdit;
        vm.UndoCommand.Execute(null);

        Assert.True(doc.DiscardedOlderSteps > 0, "the budget released nothing, so there is nothing to report");
        Assert.Contains("undone", vm.ActionStatusText);
        Assert.Contains("released", vm.ActionStatusText);
    }

    /// <summary>An eviction note must not survive onto the next operation's line.</summary>
    [Fact]
    public void TheNoteIsConsumedRatherThanRepeated()
    {
        MainViewModel vm = Loaded();
        AudioDocument doc = vm.ActiveDocument!.Doc;

        AudioDocument.UndoBudgetBytes = 3 * PerEdit;
        vm.UndoCommand.Execute(null);
        Assert.Contains("released", vm.ActionStatusText);

        AudioDocument.UndoBudgetBytes = long.MaxValue;
        Edit(doc, 0.9f);

        Assert.DoesNotContain("released", vm.ActionStatusText);
    }

    /// <summary>
    /// Undo running out says so, and says it instead of the per-eviction note rather than as well
    /// as it — both name the same fact.
    /// </summary>
    [Fact]
    public void RunningOutOfUndoSaysWhyExactlyOnce()
    {
        MainViewModel vm = Loaded();
        AudioDocument doc = vm.ActiveDocument!.Doc;

        AudioDocument.UndoBudgetBytes = 2 * PerEdit;
        while (doc.CanUndo) vm.UndoCommand.Execute(null);

        Assert.True(doc.DiscardedOlderSteps > 0);
        string status = vm.ActionStatusText;
        Assert.Contains("cannot be taken back further", status);
        Assert.Equal(1, status.Split("released").Length - 1);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UndoBudgetCollection
{
    public const string Name = "Undo budget";
}
