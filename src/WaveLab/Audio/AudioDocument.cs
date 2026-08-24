namespace WaveLab.Audio;

/// <summary>
/// In-memory audio document. Samples are stored deinterleaved as 32-bit float
/// (lossless container for 16-bit and 24-bit PCM sources). All edits go through
/// ReplaceRange so undo/redo and change notification stay consistent.
/// </summary>
public sealed class AudioDocument
{
    private float[][] _channels;
    private float _monitorGain = 1f;
    private int _sampleRate;
    private int _sourceBitDepth;
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];
    private long _currentStateId;
    private long? _savedStateId = 0;
    private long _nextStateId = 1;
    private int _historyGeneration;
    private int _discardedOlder;
    private int _discardedNewer;

    /// <summary>Stable identity for autosave/crash-recovery bookkeeping.</summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>Byte budget for undo history; oldest edits are evicted beyond this.</summary>
    public static long UndoBudgetBytes { get; set; } = 512L * 1024 * 1024;

    public AudioDocument(float[][] channels, int sampleRate, int sourceBitDepth)
    {
        ValidateChannelData(channels, nameof(channels));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");

        _channels = channels;
        _sampleRate = sampleRate;
        SourceBitDepth = sourceBitDepth;
    }

    public static AudioDocument CreateEmpty(int sampleRate, int channelCount)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        if (channelCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be positive.");

        var ch = new float[channelCount][];
        for (int i = 0; i < channelCount; i++) ch[i] = [];
        return new AudioDocument(ch, sampleRate, 32);
    }

    public IReadOnlyList<float[]> Channels => Volatile.Read(ref _channels);
    public int ChannelCount => Volatile.Read(ref _channels).Length;
    public int Length
    {
        get
        {
            var channels = Volatile.Read(ref _channels);
            return channels.Length == 0 ? 0 : channels[0].Length;
        }
    }
    public int SampleRate
    {
        get => _sampleRate;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Sample rate must be positive.");
            _sampleRate = value;
        }
    }
    /// <summary>16, 24 or 32 (32 = IEEE float).</summary>
    public int SourceBitDepth
    {
        get => _sourceBitDepth;
        set
        {
            if (value is not (16 or 24 or 32))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "Source bit depth must be 16, 24, or 32 bits.");
            }
            _sourceBitDepth = value;
        }
    }
    /// <summary>Whether ordinary Save should apply TPDF dither when writing 16-bit PCM.</summary>
    public bool Dither16BitOnSave { get; set; } = true;
    /// <summary>
    /// Whether Save must prompt for a new output path instead of replacing <see cref="FilePath"/>.
    /// Used for imported containers whose non-audio chunks WaveLab cannot preserve.
    /// </summary>
    public bool RequiresSaveAs { get; set; }

    /// <summary>
    /// The ancillary chunks of the RIFF file this document came from, carried through to the save.
    /// </summary>
    /// <remarks>
    /// A WAV holds far more than audio — broadcast metadata, iXML from a field recorder, loop
    /// points, a producer's notes — and every one of those chunks used to be discarded on load and
    /// absent on save. Keeping them is what lets a file be written back over itself without quietly
    /// losing what somebody else put in it.
    /// </remarks>
    public RiffMetadata Riff { get; set; } = new();
    public string? FilePath { get; set; }
    public string Title { get; set; } = "Untitled";

    /// <summary>
    /// True when this document holds what a restoration pass removed rather than programme.
    /// It is what puts the monitor control on screen: a residual is the one kind of file the
    /// app makes that cannot be judged at its own level, and every other document should be
    /// left alone by that control.
    /// </summary>
    public bool IsResidual { get; set; }

    /// <summary>
    /// Linear gain applied <b>only</b> on the way to the speakers, never to the samples.
    /// A residual — what a restoration pass removed — sits tens of dB below programme, so it
    /// has to be lifted to be audible at all; baking that lift into the audio would destroy
    /// the one property that makes a residual worth keeping, which is that it is the exact
    /// difference and mixes back to the original. Save, export, the peak pyramid, statistics
    /// and loudness all read <see cref="Channels"/> and so are unaffected by construction.
    /// </summary>
    /// <remarks>
    /// Read by the audio thread from inside <c>PlaybackEngine.DocumentProvider.Read</c> while
    /// the UI thread may be moving a slider. A float write is atomic, so the volatile pair is
    /// enough: the callback sees the old value or the new one, never a torn one.
    /// </remarks>
    public float MonitorGain
    {
        get => Volatile.Read(ref _monitorGain);
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Monitor gain must be finite and not negative.");
            Volatile.Write(ref _monitorGain, value);
        }
    }

    /// <summary>
    /// For captured takes: the level-check outcome that preceded the recording,
    /// e.g. "Level check: programme peak −8.2 dBTP, suggested input change −4.0 dB".
    /// Null when the take was not preceded by a settled check.
    /// </summary>
    public string? CaptureNote { get; set; }
    public bool Dirty { get; private set; }

    /// <summary>Increments on every content change; used to skip redundant autosaves.</summary>
    public int EditVersion { get; private set; }

    public double Duration => SampleRate > 0 ? (double)Length / SampleRate : 0;

    /// <summary>Raised after any content change (start, removedCount, insertedCount).</summary>
    public event Action<int, int, int>? Changed;

    /// <summary>
    /// Records that something other than the audio changed — a tag, a broadcast timestamp — so an
    /// ordinary Save writes it out.
    /// </summary>
    /// <remarks>
    /// Deliberately does <em>not</em> raise <see cref="Changed"/>. Nothing about the samples moved,
    /// so the peak pyramid has nothing to rebuild and the marker anchors have nothing to follow;
    /// firing it would schedule a full re-scan of the file for a change to a text field.
    /// </remarks>
    public void MarkMetadataChanged()
    {
        Dirty = true;
        EditVersion++;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Retained history depth, for budget diagnostics and tests.</summary>
    internal int UndoDepth => _undo.Count;
    internal int RedoDepth => _redo.Count;
    public string? NextUndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? NextRedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>How many steps the retained history holds, applied and undone together.</summary>
    public int HistoryCount => _undo.Count + _redo.Count;

    /// <summary>How many of them are applied. Also the timeline index the document sits at.</summary>
    public int HistoryPosition => _undo.Count;

    /// <summary>What the retained history costs, against <see cref="UndoBudgetBytes"/>.</summary>
    /// <remarks>
    /// Each buffer is counted once however many steps hold it. Consecutive whole-document renders
    /// share their arrays by construction — <see cref="ReplaceAllOwned"/> hands the outgoing render
    /// to the next edit as its <c>Old</c> side, which is the same object it kept as its own
    /// <c>New</c> — so summing the steps charged one album-sized array twice and had the budget
    /// release history about twice as early as the memory warranted.
    /// </remarks>
    public long RetainedHistoryBytes => RetainedBytes();

    /// <summary>How many steps the budget has released from the oldest end of the timeline.</summary>
    /// <remarks>
    /// Non-zero means undo can no longer reach the state the file was opened in. That is the one
    /// thing about the limit a user has to be told rather than left to discover, because an undo
    /// that stops early is indistinguishable from an undo that has finished.
    /// </remarks>
    public int DiscardedOlderSteps => _discardedOlder;

    /// <summary>How many steps the budget has released from the furthest-future end.</summary>
    public int DiscardedNewerSteps => _discardedNewer;

    /// <summary>
    /// Raised when the byte budget releases steps, as (older, newer). The shell reports it: an
    /// eviction that says nothing leaves the history quietly shorter than the user's edits.
    /// </summary>
    /// <remarks>
    /// <b>A handler must record and return, not read the document.</b> The budget is enforced from
    /// the middle of a commit — before <see cref="Dirty"/>, <see cref="EditVersion"/> and the
    /// current state id have been moved on, and before <see cref="Changed"/> — so anything read
    /// here describes the edit that is still happening. Measured: <c>EditVersion</c> reads 2 inside
    /// the handler where it reads 3 a moment later.
    /// That ordering is deliberate rather than incidental: it is what lets the shell hold the count
    /// and fold it into the line it writes when <see cref="Changed"/> arrives, instead of writing a
    /// line that the change event immediately overwrites.
    /// </remarks>
    public event Action<int, int>? HistoryReleased;

    /// <summary>
    /// The retained history as one linear timeline: the applied steps in the order they were
    /// applied, followed by the undone ones in the order redo would reapply them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>_redo</c> is used as a stack, so it is stored back to front — <c>_redo[^1]</c> is the next
    /// redo and <c>_redo[0]</c> the furthest future. The timeline is therefore <c>_undo</c> in order
    /// followed by <c>_redo</c> <b>reversed</b>, which is what the indexing below does. Getting that
    /// the wrong way round produces a list that looks entirely plausible and jumps to the wrong
    /// state, so it is pinned by a test rather than left to reading.
    /// </para>
    /// <para>
    /// Build it fresh whenever it may have moved instead of caching entries. The byte budget can
    /// release steps from either end at any time, so an index captured earlier may name a different
    /// step; <see cref="HistorySnapshot.Generation"/> is what says so.
    /// </para>
    /// </remarks>
    public HistorySnapshot GetHistory()
    {
        int applied = _undo.Count;
        int total = applied + _redo.Count;
        var entries = new HistoryEntry[total];
        // Charged to the first step on the timeline that holds each buffer, so the rows add up to
        // the retained total instead of counting a shared array once per step that refers to it.
        var counted = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        long retained = 0;
        bool savepointOnAStep = false;
        for (int i = 0; i < total; i++)
        {
            var edit = i < applied ? _undo[i] : _redo[total - 1 - i];
            long bytes = UncountedBytes(edit, counted);
            retained += bytes;
            bool isSavepoint = _savedStateId is { } saved && saved == edit.AfterStateId;
            savepointOnAStep |= isSavepoint;
            entries[i] = new HistoryEntry(
                i,
                edit.Name,
                i < applied,
                i == applied - 1,
                isSavepoint,
                Frames(edit.Old) != Frames(edit.New),
                edit.OwnsFullDocument,
                bytes);
        }

        long baselineStateId = applied > 0 ? _undo[0].BeforeStateId : _currentStateId;
        bool baselineIsSavepoint = _savedStateId is { } id && id == baselineStateId;
        return new HistorySnapshot(
            entries,
            applied,
            baselineIsSavepoint,
            baselineIsSavepoint || savepointOnAStep,
            _discardedOlder,
            _discardedNewer,
            _historyGeneration,
            retained,
            UndoBudgetBytes);
    }

    private static int Frames(float[][] data) => data.Length == 0 ? 0 : data[0].Length;

    public float[][] CopyRange(int start, int count)
    {
        var channels = Volatile.Read(ref _channels);
        int length = channels[0].Length;
        ValidateRange(start, count, length);

        var result = new float[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            result[c] = new float[count];
            Array.Copy(channels[c], start, result[c], 0, count);
        }
        return result;
    }

    /// <summary>
    /// Replace [start, start+removeCount) with newData (may be empty). The undo
    /// entry retains <paramref name="newData"/>, so the caller must not mutate it
    /// after this method returns.
    /// </summary>
    public void ReplaceRange(int start, int removeCount, float[][] newData, string opName)
    {
        ArgumentNullException.ThrowIfNull(newData);
        ArgumentException.ThrowIfNullOrWhiteSpace(opName);
        var channels = Volatile.Read(ref _channels);
        ValidateRange(start, removeCount, channels[0].Length);
        ValidateReplacementData(newData, channels.Length, opName);

        long beforeStateId = _currentStateId;
        long afterStateId = _nextStateId++;
        var oldData = CopyRange(channels, start, removeCount);
        // Splice copies out of newData into freshly allocated channels, so the
        // document never aliases it and the edit can retain the caller's array.
        // Cloning here would allocate a second full-size copy of every edit.
        var edit = new Edit(opName, start, oldData, newData, false,
            beforeStateId, afterStateId);
        Splice(channels, start, removeCount, newData);
        _undo.Add(edit);
        DiscardRedo();
        EnforceUndoBudget();
        _currentStateId = afterStateId;
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(start, removeCount, newData[0].Length);
    }

    /// <summary>
    /// Replace the entire document by taking ownership of a completed render.
    /// This avoids cloning several album-sized buffers at commit time while still
    /// retaining the previous and new arrays as one undoable edit. The caller must
    /// not mutate <paramref name="newData"/> after this method returns.
    /// </summary>
    public void ReplaceAllOwned(float[][] newData, string opName)
    {
        ArgumentNullException.ThrowIfNull(newData);
        ArgumentException.ThrowIfNullOrWhiteSpace(opName);
        var oldData = Volatile.Read(ref _channels);
        // A complete render may legitimately change the channel topology (for
        // example, an undoable mono-to-stereo master). Region splices still
        // require the existing channel count.
        ValidateChannelData(newData, nameof(newData));
        int newLength = newData[0].Length;

        int oldLength = oldData[0].Length;
        long beforeStateId = _currentStateId;
        long afterStateId = _nextStateId++;
        Volatile.Write(ref _channels, newData);
        _undo.Add(new Edit(opName, 0, oldData, newData, true, beforeStateId, afterStateId));
        DiscardRedo();
        EnforceUndoBudget();
        _currentStateId = afterStateId;
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(0, oldLength, newLength);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (start, removed, inserted) = UndoCore();
        // Undo moved an edit rather than releasing it, so the budget has to be re-checked here too.
        EnforceUndoBudget();
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(start, removed, inserted);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (start, removed, inserted) = RedoCore();
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(start, removed, inserted);
    }

    /// <summary>
    /// Takes the document back to any point on the timeline in one action, and returns how many
    /// steps that moved (negative backwards, zero when it was already there).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A jump is a run of ordinary undo/redo steps that raises <b>one</b> <see cref="Changed"/> and
    /// bumps <see cref="EditVersion"/> <b>once</b>. That is not an optimisation. Every step of a
    /// per-step run would re-enter the peak rebuild, queue its own marker sidecar write, requery
    /// three dozen commands and push a different operation name onto the status line — so a
    /// ten-step jump would cost ten of each and settle on the right answer only at the end.
    /// </para>
    /// <para>
    /// The single event carries the composition of the run: the first offset that differs, the
    /// length of the changed span before the jump, and its length after. Anything anchored to the
    /// timeline is remapped through that triple, so it has to be tight — a lazy whole-document
    /// triple would collapse every marker to sample 0. A run of same-length edits composes to
    /// <c>removed == inserted</c> and moves no markers at all, and a run containing a whole-document
    /// render composes to the whole document, which is exactly what that step raises on its own.
    /// </para>
    /// <para>
    /// The budget is enforced once, at the end. Enforcing it mid-run could release entries while the
    /// loop is still counting against <c>_undo.Count</c>, and the retained total is invariant under a
    /// stack-to-stack move anyway, so deferring it costs nothing and removes the hazard.
    /// </para>
    /// </remarks>
    public int JumpToHistoryPosition(int position)
    {
        if (position < 0 || position > HistoryCount)
        {
            // Refused rather than clamped: the panel is modeless and its indices can go stale under
            // a budget eviction, and a silently wrong jump is far worse than a loud one.
            throw new ArgumentOutOfRangeException(
                nameof(position), position, "Position is outside the retained history.");
        }

        int moved = position - _undo.Count;
        if (moved == 0) return 0;

        int start = 0, removed = 0, inserted = 0;
        bool first = true;

        void Compose((int Start, int Removed, int Inserted) step)
        {
            if (first)
            {
                (start, removed, inserted) = step;
                first = false;
                return;
            }

            // The hull of the span already accumulated and the span this step touches, measured in
            // the document as it stands. Everything outside it maps one to one onto the document as
            // it was before the jump, which is why `extra` and `deficit` count the same in both.
            int end = Math.Max(start + inserted, step.Start + step.Removed);
            int extra = Math.Max(0, end - (start + inserted));
            int deficit = Math.Max(0, start - step.Start);
            start = Math.Min(start, step.Start);
            removed += extra + deficit;
            inserted = end - start + (step.Inserted - step.Removed);
        }

        while (_undo.Count > position) Compose(UndoCore());
        while (_undo.Count < position) Compose(RedoCore());

        EnforceUndoBudget();
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(start, removed, inserted);
        return moved;
    }

    /// <summary>
    /// Discards the step at <paramref name="index"/> and every step after it, permanently. Returns
    /// false when the index names nothing.
    /// </summary>
    /// <remarks>
    /// The document is first taken back to the state that step was applied to — what is being thrown
    /// away must not still be in the audio. No <see cref="Changed"/> is raised for the discard
    /// itself, because no samples move; the caller has to refresh whatever reads
    /// <see cref="CanRedo"/>.
    /// </remarks>
    public bool TruncateHistoryFrom(int index)
    {
        if (index < 0 || index >= HistoryCount) return false;

        if (_undo.Count > index)
        {
            JumpToHistoryPosition(index);
            // Everything unapplied is now exactly the run that was asked for. Recomputing a count
            // from `index` would be wrong here: the jump's budget check can release older steps and
            // renumber the timeline underneath us, but it cannot change which steps are undone.
            _redo.Clear();
        }
        else
        {
            // _redo is stored back to front, so the far-future end — combined indices `index`
            // upwards — sits at the front of the list.
            _redo.RemoveRange(0, HistoryCount - index);
        }

        _historyGeneration++;
        DropSavepointIfUnreachable();
        return true;
    }

    /// <summary>
    /// Moves one step back, splicing the samples and nothing else: no budget check, no version bump,
    /// no notification. <see cref="Undo"/> adds those for one step; a jump adds them once for a run.
    /// Returns the splice as (start, removed from the document, inserted into it).
    /// </summary>
    private (int Start, int Removed, int Inserted) UndoCore()
    {
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        int insertedLen = Frames(e.New);
        if (e.OwnsFullDocument)
            Volatile.Write(ref _channels, e.Old);
        else
            Splice(e.Start, insertedLen, e.Old);
        _redo.Add(e);
        _currentStateId = e.BeforeStateId;
        return (e.Start, insertedLen, Frames(e.Old));
    }

    /// <summary>The mirror of <see cref="UndoCore"/>, one step forward.</summary>
    private (int Start, int Removed, int Inserted) RedoCore()
    {
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        int oldLen = Frames(e.Old);
        if (e.OwnsFullDocument)
            Volatile.Write(ref _channels, e.New);
        else
            Splice(e.Start, oldLen, e.New);
        _undo.Add(e);
        _currentStateId = e.AfterStateId;
        return (e.Start, oldLen, Frames(e.New));
    }

    /// <summary>
    /// Drops the forward chain, as any new edit does, and gives up the savepoint if it went with it.
    /// </summary>
    /// <remarks>
    /// Editing after an undo discards the steps that were undone, and the last saved state can be one
    /// of them — save, undo, edit, and the bytes on disk are no longer anywhere on the timeline.
    /// Without this the id would linger: harmless for <see cref="Dirty"/>, which was already true,
    /// but it would leave <see cref="HistorySnapshot.SavepointReachable"/> as the only thing that
    /// knew, and the invariant "the savepoint is reachable or absent" merely nearly true.
    /// </remarks>
    private void DiscardRedo()
    {
        if (_redo.Count == 0) return;
        _redo.Clear();
        _historyGeneration++;
        DropSavepointIfUnreachable();
    }

    /// <summary>What one step holds, counting every buffer whatever else refers to it.</summary>
    /// <remarks>
    /// Only ever an upper bound on what the step costs, which is what makes it usable as the cheap
    /// screen in <see cref="EnforceUndoBudget"/>: a document inside the budget by this measure is
    /// inside it by the exact one too, so the exact walk is paid for only when it can change an
    /// answer.
    /// </remarks>
    private static long EditBytes(Edit edit)
    {
        long samples = 0;
        foreach (var channel in edit.Old) samples += channel.Length;
        foreach (var channel in edit.New) samples += channel.Length;
        return samples * sizeof(float);
    }

    /// <summary>
    /// What one step adds to a running total, given the buffers <paramref name="counted"/> already
    /// holds. Adds this step's to it.
    /// </summary>
    private static long UncountedBytes(Edit edit, HashSet<float[]> counted)
    {
        long samples = 0;
        foreach (var channel in edit.Old) if (counted.Add(channel)) samples += channel.Length;
        foreach (var channel in edit.New) if (counted.Add(channel)) samples += channel.Length;
        return samples * sizeof(float);
    }

    /// <summary>
    /// What the whole retained history costs, counting each buffer once however many steps hold it.
    /// </summary>
    private long RetainedBytes()
    {
        var counted = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        long total = 0;
        for (int i = 0; i < _undo.Count; i++) total += UncountedBytes(_undo[i], counted);
        for (int i = 0; i < _redo.Count; i++) total += UncountedBytes(_redo[i], counted);
        return total;
    }

    /// <summary>The gross sum of the steps, which can only over-state <see cref="RetainedBytes"/>.</summary>
    private long GrossRetainedBytes()
    {
        long total = 0;
        for (int i = 0; i < _undo.Count; i++) total += EditBytes(_undo[i]);
        for (int i = 0; i < _redo.Count; i++) total += EditBytes(_redo[i]);
        return total;
    }

    /// <summary>
    /// How many retained steps hold each buffer, so that releasing one can be costed by decrement.
    /// </summary>
    /// <remarks>
    /// The alternative is re-reading the deduplicated total after every eviction, which is what this
    /// replaced: correct, and quadratic in the retained depth, because each read walks every step
    /// again. Measured on a history squeezed in one go — the Settings dialog lowering the limit, and
    /// the next edit paying for it — that cost <b>19 ms at 500 steps, 316 at 2 000 and 1 190 at
    /// 5 000</b>, on the dispatcher. Counting references is one walk and then arithmetic.
    /// </remarks>
    private static long CountReferences(List<Edit> edits, Dictionary<float[], int> held)
    {
        long samples = 0;
        foreach (Edit edit in edits)
        {
            foreach (var channel in edit.Old) samples += Hold(channel, held);
            foreach (var channel in edit.New) samples += Hold(channel, held);
        }
        return samples * sizeof(float);

        static long Hold(float[] channel, Dictionary<float[], int> held)
        {
            if (held.TryGetValue(channel, out int count))
            {
                held[channel] = count + 1;
                return 0;
            }
            held[channel] = 1;
            return channel.Length;
        }
    }

    /// <summary>What releasing one step actually frees: the buffers no other retained step holds.</summary>
    private static long ReleaseReferences(Edit edit, Dictionary<float[], int> held)
    {
        long samples = 0;
        foreach (var channel in edit.Old) samples += Drop(channel, held);
        foreach (var channel in edit.New) samples += Drop(channel, held);
        return samples * sizeof(float);

        static long Drop(float[] channel, Dictionary<float[], int> held)
        {
            if (!held.TryGetValue(channel, out int count)) return 0;
            if (count > 1)
            {
                held[channel] = count - 1;
                return 0;
            }
            held.Remove(channel);
            return channel.Length;
        }
    }

    /// <summary>
    /// Keeps the retained history inside <see cref="UndoBudgetBytes"/>, which is a per-document
    /// figure: several open tabs each hold up to that much.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both stacks count. Undoing does not free an edit, it moves it to the redo stack, so an
    /// accounting that looked only at <c>_undo</c> — as this did — reported a document as being
    /// inside its budget while undoing repeatedly grew memory without limit. Redo is also the side
    /// to give up first: it is only reachable once the user has already stepped backwards, so
    /// dropping the far end of the forward chain costs less than throwing away undo history.
    /// </para>
    /// <para>
    /// Steps share buffers, so a drop is costed by <see cref="ReleaseReferences"/> rather than by
    /// subtracting what the step held: releasing a whole-document render frees only the array its
    /// neighbour is not also holding, and subtracting the step's gross size would report memory
    /// reclaimed that is still live — releasing about twice as much history as the limit asks for.
    /// The gross sum is still the screen, because it can only over-state the exact one, so the
    /// reference walk is paid for only when it can change an answer.
    /// </para>
    /// </remarks>
    private void EnforceUndoBudget()
    {
        if (GrossRetainedBytes() <= UndoBudgetBytes) return;

        var held = new Dictionary<float[], int>(ReferenceEqualityComparer.Instance);
        long total = CountReferences(_undo, held) + CountReferences(_redo, held);
        if (total <= UndoBudgetBytes) return;

        // _redo[0] is the furthest-future edit, so trimming from the front keeps the next redo step
        // available for as long as possible. Counted first and removed in one range, because
        // removing from the front of a list one at a time is itself quadratic in the depth.
        int dropped = 0;
        while (dropped < _redo.Count && total > UndoBudgetBytes)
            total -= ReleaseReferences(_redo[dropped++], held);
        if (dropped > 0) _redo.RemoveRange(0, dropped);

        int droppedOlder = 0;
        while (_undo.Count - droppedOlder > 1 && total > UndoBudgetBytes)
            total -= ReleaseReferences(_undo[droppedOlder++], held);
        if (droppedOlder > 0) _undo.RemoveRange(0, droppedOlder);

        if (dropped == 0 && droppedOlder == 0) return;
        // The Edit History panel shows what was released and renumbers when it happens, so the
        // counts are kept rather than the eviction being silent.
        _discardedNewer += dropped;
        _discardedOlder += droppedOlder;
        _historyGeneration++;
        DropSavepointIfUnreachable();
        // Last of the eviction's own work, and still inside the commit that caused it — see the
        // event's remarks for what a handler may and may not read from here.
        HistoryReleased?.Invoke(droppedOlder, dropped);
    }

    /// <summary>
    /// Gives up the savepoint once no step on the timeline can return the document to it.
    /// </summary>
    /// <remarks>
    /// This changes no observable <see cref="Dirty"/> value today: state ids are never reused, so an
    /// unreachable savepoint already compared unequal to every current state and the document
    /// already read as dirty forever. What it buys is that the state now says what is true rather
    /// than leaving a dangling id behind, which is what lets the history report whether the last
    /// saved state can still be reached — and it closes the same gap on the path that was already
    /// there, where the budget releases the oldest edit and takes the savepoint with it.
    /// </remarks>
    private void DropSavepointIfUnreachable()
    {
        if (_savedStateId is not { } saved) return;
        if (saved == _currentStateId) return;
        if (_undo.Count > 0 && saved == _undo[0].BeforeStateId) return;
        foreach (var edit in _undo) if (saved == edit.AfterStateId) return;
        foreach (var edit in _redo) if (saved == edit.AfterStateId) return;
        MarkUnsaved();
    }

    public void MarkSaved()
    {
        _savedStateId = _currentStateId;
        Dirty = false;
    }

    /// <summary>
    /// Mark generated or recovered audio as needing its first save. This does not
    /// create an undo entry because no user edit has replaced source samples yet.
    /// </summary>
    public void MarkUnsaved()
    {
        _savedStateId = null;
        Dirty = true;
    }

    private void UpdateDirtyFromSavepoint() =>
        Dirty = !_savedStateId.HasValue || _currentStateId != _savedStateId.Value;

    /// <summary>Interleaved copy of a range (for playback/export).</summary>
    public void ReadInterleaved(int start, int frames, float[] dest, int destOffset)
    {
        ArgumentNullException.ThrowIfNull(dest);
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames));

        // Edits publish a completely new jagged array. Keep one point-in-time
        // reference for this whole callback so a concurrent splice cannot make
        // the bounds check observe one version and the indexer another.
        var channels = Volatile.Read(ref _channels);
        int ch = channels.Length;
        int length = ch == 0 ? 0 : channels[0].Length;
        int required = checked(frames * ch);
        if (destOffset < 0 || destOffset > dest.Length - required)
            throw new ArgumentOutOfRangeException(nameof(destOffset));

        for (int f = 0; f < frames; f++)
        {
            int s = start + f;
            for (int c = 0; c < ch; c++)
                dest[destOffset + f * ch + c] = (uint)s < (uint)length ? channels[c][s] : 0f;
        }
    }

    private void Splice(int start, int removeCount, float[][] insert)
    {
        var channels = Volatile.Read(ref _channels);
        Splice(channels, start, removeCount, insert);
    }

    private void Splice(float[][] channels, int start, int removeCount, float[][] insert)
    {
        int insertCount = insert[0].Length;
        int oldLength = channels[0].Length;
        int newLen = checked(oldLength - removeCount + insertCount);
        var next = new float[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            var dst = new float[newLen];
            Array.Copy(channels[c], 0, dst, 0, start);
            if (insertCount > 0) Array.Copy(insert[c], 0, dst, start, insertCount);
            Array.Copy(
                channels[c],
                start + removeCount,
                dst,
                start + insertCount,
                oldLength - start - removeCount);
            next[c] = dst;
        }
        Volatile.Write(ref _channels, next);
    }

    private static float[][] CopyRange(float[][] channels, int start, int count)
    {
        var result = new float[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            result[c] = new float[count];
            Array.Copy(channels[c], start, result[c], 0, count);
        }
        return result;
    }

    private static void ValidateChannelData(float[][] channels, string paramName)
    {
        ArgumentNullException.ThrowIfNull(channels, paramName);
        if (channels.Length == 0)
            throw new ArgumentException("At least one audio channel is required.", paramName);
        if (channels[0] is null)
            throw new ArgumentException("Audio channels cannot be null.", paramName);

        int length = channels[0].Length;
        for (int channel = 1; channel < channels.Length; channel++)
        {
            if (channels[channel] is null)
                throw new ArgumentException("Audio channels cannot be null.", paramName);
            if (channels[channel].Length != length)
                throw new ArgumentException("Audio channel lengths must match.", paramName);
        }
    }

    private static void ValidateReplacementData(float[][] data, int channelCount, string opName)
    {
        if (data.Length != channelCount)
        {
            throw new ArgumentException(
                $"Channel count mismatch in edit '{opName}' ({data.Length} vs {channelCount}).",
                nameof(data));
        }

        ValidateChannelData(data, nameof(data));
    }

    private static void ValidateRange(int start, int count, int length)
    {
        if (start < 0 || start > length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0 || count > length - start)
            throw new ArgumentOutOfRangeException(nameof(count));
    }

    private sealed record Edit(
        string Name,
        int Start,
        float[][] Old,
        float[][] New,
        bool OwnsFullDocument,
        long BeforeStateId,
        long AfterStateId);
}
