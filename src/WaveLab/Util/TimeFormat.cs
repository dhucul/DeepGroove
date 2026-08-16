namespace WaveLab.Util;

public static class TimeFormat
{
    /// <summary>hh:mm:ss.mmm from a sample position.</summary>
    public static string Position(long samples, int sampleRate)
    {
        // A negative position has no timecode: the 00 specifier does not pad a
        // negative field, and rounding carries a small negative into a positive
        // second. Clamp like Compact does.
        if (sampleRate <= 0 || samples < 0) return "00:00:00.000";
        double totalSec = (double)samples / sampleRate;
        int h = (int)(totalSec / 3600);
        int m = (int)(totalSec / 60) % 60;
        int s = (int)totalSec % 60;
        int ms = (int)Math.Round((totalSec - Math.Floor(totalSec)) * 1000);
        if (ms == 1000) { ms = 0; s++; if (s == 60) { s = 0; m++; if (m == 60) { m = 0; h++; } } }
        return $"{h:00}:{m:00}:{s:00}.{ms:000}";
    }

    /// <summary>m:ss or h:mm:ss — compact, for rulers and status.</summary>
    public static string Compact(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int h = (int)(seconds / 3600);
        int m = (int)(seconds / 60) % 60;
        double s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{(int)s:00}" : $"{m}:{(int)s:00}";
    }

    /// <summary>Choose a "nice" tick spacing in seconds for a ruler.</summary>
    public static double NiceTick(double targetSeconds)
    {
        double[] steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];
        foreach (var s in steps) if (s >= targetSeconds) return s;
        return 7200;
    }
}
