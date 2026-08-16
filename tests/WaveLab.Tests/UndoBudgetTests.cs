using WaveLab.Audio;
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
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UndoBudgetCollection
{
    public const string Name = "Undo budget";
}
