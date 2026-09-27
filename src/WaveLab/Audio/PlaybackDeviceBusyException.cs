namespace WaveLab.Audio;

/// <summary>A previous output stream has not released its device yet; starting playback can be retried.</summary>
internal sealed class PlaybackDeviceBusyException() : InvalidOperationException(
    "The previous output stream is still releasing its device. Try Play again in a moment.");
