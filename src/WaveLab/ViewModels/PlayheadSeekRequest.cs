namespace WaveLab.ViewModels;

public enum PlayheadSeekPhase
{
    Begin,
    Update,
    End,
}

public sealed record PlayheadSeekRequest(
    DocumentViewModel Document,
    int Sample,
    PlayheadSeekPhase Phase);
