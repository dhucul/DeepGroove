using WaveLab.Audio.Vst3;

namespace WaveLab;

/// <summary>
/// The process entry point, ahead of WPF.
/// </summary>
/// <remarks>
/// <para>
/// WPF generates one of these from <c>App.xaml</c> that goes straight to <c>Run()</c>. This one
/// exists so <c>--vst3-scan</c> can be answered <b>without starting an application at all</b>: the
/// scanner has no window and wants no message loop, and a plugin handed a UI thread it can post to
/// is exactly the fault the separate process is there to contain. Suppressing the startup window
/// from inside <c>OnStartup</c> was tried first and cannot be done — <c>StartupUri</c> refuses null.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (App.IsScanRequest(args)) return Vst3Catalogue.RunScanner(args[1], Console.Out);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
