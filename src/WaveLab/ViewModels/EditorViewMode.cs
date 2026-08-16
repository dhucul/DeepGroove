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
