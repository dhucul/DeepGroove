namespace WaveLab.Audio;

/// <summary>
/// One step in the linear edit history, as the Edit History panel shows it.
/// </summary>
/// <param name="Index">Position in the combined timeline, oldest first.</param>
/// <param name="Name">The operation name the edit was committed under, e.g. "Gain +3.0 dB".</param>
/// <param name="IsApplied">Whether this step is part of the document as it stands.</param>
/// <param name="IsCurrent">Whether this step produced the state the document is in now.</param>
/// <param name="IsSavepoint">Whether the state this step produced is the last one saved to disk.</param>
/// <param name="ChangesLength">
/// Whether the splice changed the frame count. Length-changing edits are the ones that move
/// markers and can collapse a region, which is why the panel marks them.
/// </param>
/// <param name="OwnsFullDocument">A whole-document render, which may also change the channel count.</param>
/// <param name="RetainedBytes">What this step costs against the undo memory budget.</param>
public readonly record struct HistoryEntry(
    int Index,
    string Name,
    bool IsApplied,
    bool IsCurrent,
    bool IsSavepoint,
    bool ChangesLength,
    bool OwnsFullDocument,
    long RetainedBytes);

/// <summary>
/// The whole retained history of one document, read in a single call.
/// </summary>
/// <remarks>
/// Read this fresh whenever it may have moved rather than holding entries across a mutation. The
/// byte budget can release steps from either end at any time, so an index captured earlier may name
/// a different step afterwards; <see cref="Generation"/> is how a caller tells "the list grew" from
/// "the list renumbered".
/// </remarks>
/// <param name="Entries">The timeline, oldest first: applied steps, then undone ones in redo order.</param>
/// <param name="Position">How many steps are applied. Also the index of the first undone step.</param>
/// <param name="BaselineIsSavepoint">Whether the state before <c>Entries[0]</c> is the last saved state.</param>
/// <param name="SavepointReachable">
/// Whether the last saved state can still be returned to. False once the step that produced it has
/// been discarded, which is the point at which the document can no longer be brought back to the
/// bytes on disk by undoing.
/// </param>
/// <param name="DiscardedOlderSteps">Steps released from the oldest end to stay inside the budget.</param>
/// <param name="DiscardedNewerSteps">Steps released from the furthest-future end for the same reason.</param>
/// <param name="Generation">Bumps whenever anything was discarded or truncated, so indices renumbered.</param>
/// <param name="RetainedBytes">What the whole history costs.</param>
/// <param name="BudgetBytes">The budget it is being held against.</param>
public readonly record struct HistorySnapshot(
    IReadOnlyList<HistoryEntry> Entries,
    int Position,
    bool BaselineIsSavepoint,
    bool SavepointReachable,
    int DiscardedOlderSteps,
    int DiscardedNewerSteps,
    int Generation,
    long RetainedBytes,
    long BudgetBytes);
