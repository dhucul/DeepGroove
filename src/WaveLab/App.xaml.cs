using System.Windows;
using System.Windows.Threading;
using NAudio.MediaFoundation;

namespace WaveLab;

public partial class App : Application
{
    /// <summary>Guards against stacking one error dialog on top of another.</summary>
    private bool _reportingFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Without these, anything escaping a UI event handler or command body
        // kills the process with the bare Windows crash dialog and takes every
        // unsaved document with it.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalUnhandledException;

        base.OnStartup(e);
        try { MediaFoundationApi.Startup(); } catch { /* Compressed import/export unavailable */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { MediaFoundationApi.Shutdown(); } catch { }
        base.OnExit(e);
    }

    /// <summary>
    /// An exception reached the dispatcher. Tell the user in plain language and
    /// keep the process alive so open documents can still be saved.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Marked handled before anything else: a failure while reporting must
        // not come back round as a second unhandled exception.
        e.Handled = true;
        ReportFailure(e.Exception);
    }

    /// <summary>
    /// A background task faulted with nobody awaiting it. Observing it stops the
    /// finalizer thread from escalating the failure, and the user still hears
    /// about it instead of the work silently disappearing.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Exception error = e.Exception;
        e.SetObserved();
        try { Dispatcher.BeginInvoke(new Action(() => ReportFailure(error))); } catch { }
    }

    /// <summary>
    /// Terminal failure: the runtime is tearing the process down and almost
    /// nothing can be relied on here, so this only leaves a breadcrumb behind.
    /// </summary>
    private static void OnFatalUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaveLab-crash.log");
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:u} {e.ExceptionObject}{Environment.NewLine}");
        }
        catch { }
    }

    private void ReportFailure(Exception error)
    {
        if (_reportingFailure) return;
        _reportingFailure = true;
        try
        {
            MessageBox.Show(
                "WaveLab ran into an unexpected problem and stopped what it was doing.\n\n" +
                "Your open files are still here — save anything you need, then restart " +
                "WaveLab if it keeps misbehaving.\n\n" +
                "Details: " + error.Message,
                "WaveLab", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        finally { _reportingFailure = false; }
    }
}
