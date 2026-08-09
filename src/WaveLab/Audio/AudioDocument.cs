namespace WaveLab.Audio;

/// <summary>
/// In-memory audio document. Samples are stored deinterleaved as 32-bit float
/// (lossless container for 16-bit and 24-bit PCM sources). All edits go through
/// ReplaceRange so undo/redo and change notification stay consistent.
/// </summary>
public sealed class AudioDocument
{
    private float[][] _channels;
    private int _sampleRate;
    private int _sourceBitDepth;
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

    /// <summary>Replace [start, start+removeCount) with newData (may be empty).</summary>
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
        var edit = new Edit(opName, start, oldData, CloneData(newData), false,
            beforeStateId, afterStateId);
        Splice(channels, start, removeCount, newData);
        _undo.Add(edit);
        _redo.Clear();
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
        ValidateReplacementData(newData, oldData.Length, opName);
        int newLength = newData[0].Length;

        int oldLength = oldData[0].Length;
        long beforeStateId = _currentStateId;
        long afterStateId = _nextStateId++;
        Volatile.Write(ref _channels, newData);
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
            Volatile.Write(ref _channels, e.Old);
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
            Volatile.Write(ref _channels, e.New);
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
