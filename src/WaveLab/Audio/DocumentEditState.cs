namespace WaveLab.Audio;

/// <summary>Anchored metadata participating in the lifetime of an audio edit.</summary>
internal interface IDocumentEditState
{
    object Capture();
    void Restore(object state, object counterpart);
}
