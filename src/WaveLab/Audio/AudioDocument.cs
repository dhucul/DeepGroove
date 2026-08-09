namespace WaveLab.Audio;

/// <summary>
/// In-memory audio document. Samples are stored deinterleaved as 32-bit float
/// (lossless container for 16-bit and 24-bit PCM sources). All edits go through
/// ReplaceRange so undo/redo and change notification stay consistent.
/// </summary>
public sealed class AudioDocument
{
    private float[][] _channels;
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];
    private long _currentStateId;
    private long? _savedStateId = 0;
    private long _nextStateId = 1;

    /// <summary>Stable identity for autosave/crash-recovery bookkeeping.</summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>Byte budget for undo history; oldest edits are evicted beyond this.</summary>
    public static long UndoBudgetBytes { get; set; } = 512L * 1024 * 1024;

    public AudioDocument(float[][] channels, int sampleRate, int sourceBitDepth)
    {
        _channels = channels;
        SampleRate = sampleRate;
        SourceBitDepth = sourceBitDepth;
    }

    public static AudioDocument CreateEmpty(int sampleRate, int channelCount)
    {
        var ch = new float[channelCount][];
        for (int i = 0; i < channelCount; i++) ch[i] = [];
        return new AudioDocument(ch, sampleRate, 32);
    }

    public IReadOnlyList<float[]> Channels => _channels;
    public int ChannelCount => _channels.Length;
    public int Length => _channels.Length == 0 ? 0 : _channels[0].Length;
    public int SampleRate { get; set; }
    /// <summary>16, 24 or 32 (32 = IEEE float).</summary>
    public int SourceBitDepth { get; set; }
    public string? FilePath { get; set; }
    public string Title { get; set; } = "Untitled";
    public bool Dirty { get; private set; }

    /// <summary>Increments on every content change; used to skip redundant autosaves.</summary>
    public int EditVersion { get; private set; }

    public double Duration => SampleRate > 0 ? (double)Length / SampleRate : 0;

    /// <summary>Raised after any content change (start, removedCount, insertedCount).</summary>
    public event Action<int, int, int>? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? NextRedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    public float[][] CopyRange(int start, int count)
    {
        var result = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            result[c] = new float[count];
            Array.Copy(_channels[c], start, result[c], 0, count);
        }
        return result;
    }

    /// <summary>Replace [start, start+removeCount) with newData (may be empty).</summary>
    public void ReplaceRange(int start, int removeCount, float[][] newData, string opName)
    {
        if (newData.Length != ChannelCount)
            throw new ArgumentException($"Channel count mismatch in edit '{opName}' ({newData.Length} vs {ChannelCount}).");
        long beforeStateId = _currentStateId;
        long afterStateId = _nextStateId++;
        var edit = new Edit(opName, start, CopyRange(start, removeCount), CloneData(newData), false,
            beforeStateId, afterStateId);
        Splice(start, removeCount, newData);
        _undo.Add(edit);
        _redo.Clear();
        EnforceUndoBudget();
        _currentStateId = afterStateId;
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(start, removeCount, newData.Length == 0 ? 0 : newData[0].Length);
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
        if (newData.Length != ChannelCount)
            throw new ArgumentException($"Channel count mismatch in edit '{opName}' ({newData.Length} vs {ChannelCount}).");
        int newLength = newData.Length == 0 ? 0 : newData[0].Length;
        if (newData.Any(channel => channel.Length != newLength))
            throw new ArgumentException($"Channel lengths do not match in edit '{opName}'.", nameof(newData));

        var oldData = _channels;
        int oldLength = Length;
        long beforeStateId = _currentStateId;
        long afterStateId = _nextStateId++;
        _channels = newData;
        _undo.Add(new Edit(opName, 0, oldData, newData, true, beforeStateId, afterStateId));
        _redo.Clear();
        EnforceUndoBudget();
        _currentStateId = afterStateId;
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(0, oldLength, newLength);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        int insertedLen = e.New.Length == 0 ? 0 : e.New[0].Length;
        if (e.OwnsFullDocument)
            _channels = e.Old;
        else
            Splice(e.Start, insertedLen, e.Old);
        _redo.Add(e);
        _currentStateId = e.BeforeStateId;
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(e.Start, insertedLen, e.Old.Length == 0 ? 0 : e.Old[0].Length);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        int oldLen = e.Old.Length == 0 ? 0 : e.Old[0].Length;
        if (e.OwnsFullDocument)
            _channels = e.New;
        else
            Splice(e.Start, oldLen, e.New);
        _undo.Add(e);
        _currentStateId = e.AfterStateId;
        UpdateDirtyFromSavepoint();
        EditVersion++;
        Changed?.Invoke(e.Start, oldLen, e.New.Length == 0 ? 0 : e.New[0].Length);
    }

    private void EnforceUndoBudget()
    {
        static long Bytes(Edit e)
        {
            long n = 0;
            foreach (var ch in e.Old) n += ch.Length;
            foreach (var ch in e.New) n += ch.Length;
            return n * sizeof(float);
        }
        long total = 0;
        for (int i = _undo.Count - 1; i >= 0; i--) total += Bytes(_undo[i]);
        while (_undo.Count > 1 && total > UndoBudgetBytes)
        {
            total -= Bytes(_undo[0]);
            _undo.RemoveAt(0);
        }
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
        int ch = ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int s = start + f;
            for (int c = 0; c < ch; c++)
                dest[destOffset + f * ch + c] = (uint)s < (uint)Length ? _channels[c][s] : 0f;
        }
    }

    private void Splice(int start, int removeCount, float[][] insert)
    {
        int insertCount = insert.Length == 0 ? 0 : insert[0].Length;
        int newLen = Length - removeCount + insertCount;
        var next = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            var dst = new float[newLen];
            Array.Copy(_channels[c], 0, dst, 0, start);
            if (insertCount > 0) Array.Copy(insert[c], 0, dst, start, insertCount);
            Array.Copy(_channels[c], start + removeCount, dst, start + insertCount, Length - start - removeCount);
            next[c] = dst;
        }
        _channels = next;
    }

    private static float[][] CloneData(float[][] data)
    {
        var copy = new float[data.Length][];
        for (int i = 0; i < data.Length; i++) copy[i] = (float[])data[i].Clone();
        return copy;
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
