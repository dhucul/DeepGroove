using System.Windows;
using NAudio.MediaFoundation;

namespace WaveLab;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { MediaFoundationApi.Startup(); } catch { /* Compressed import/export unavailable */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { MediaFoundationApi.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
