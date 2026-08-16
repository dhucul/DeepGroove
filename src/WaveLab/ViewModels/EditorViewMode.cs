namespace WaveLab.ViewModels;

/// <summary>Which representation the editor area shows.</summary>
public enum EditorViewMode
{
    /// <summary>Waveform only — the app's original behaviour.</summary>
    Waveform,

    /// <summary>Waveform above, spectrogram below, on one shared time axis.</summary>
    Split,

    /// <summary>Spectrogram only, for spectral work that needs the whole pane.</summary>
    Spectrogram,
}

/// <summary>
/// A region of the time-frequency plane, in the units the user sees.
/// </summary>
/// <remarks>
/// This lives with the view models rather than with the spectral editor control because it is what
/// the toolbar binds to: the repair actions and the selection readout both need it, and neither
/// should have to reach into a control to find out what is selected.
/// </remarks>
public readonly record struct SpectralRegion(
    int StartSample, int EndSample, double LowFrequency, double HighFrequency)
{
    public bool IsEmpty => EndSample <= StartSample || HighFrequency <= LowFrequency;
    public static SpectralRegion None => new(0, 0, 0, 0);
}
