using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// One STA thread carrying one <see cref="Application"/> with the real theme on it, shared by every
/// test that needs a live WPF element.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="Application"/> is per-process rather than per-thread, and a second one throws — so
/// two test classes each standing one up fail whenever xUnit happens to run them at the same time.
/// One shared thread also means calls queue instead of racing, so no test has to know which
/// collection any other test is in.
/// </para>
/// <para>
/// The theme is merged when the thread starts and never per test, because
/// <c>InitializeComponent</c> resolves every <c>StaticResource</c> as it parses: resources that
/// arrive after a dialog is constructed are resources that dialog never saw. The thread is a
/// background one and goes with the process; shutting the application down would leave later tests
/// with no resources and no way to get them back.
/// </para>
/// </remarks>
internal static class Wpf
{
    private static readonly object Gate = new();
    private static readonly object RunGate = new();
    private static readonly List<Exception> Failures = [];
    private static Dispatcher? _ui;
    private static int _uiThreadId;

    private static Dispatcher Ui
    {
        get
        {
            lock (Gate)
            {
                if (_ui != null) return _ui;

                using var started = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(
                            "pack://application:,,,/WaveLab;component/Themes/Theme.xaml",
                            UriKind.Absolute),
                    });

                    _uiThreadId = Environment.CurrentManagedThreadId;
                    _ui = Dispatcher.CurrentDispatcher;

                    // An exception raised inside a window message -- a Deactivated handler, say --
                    // reaches the dispatcher rather than the caller, and an unhandled one takes the
                    // test host down with no indication of which test caused it. The real app
                    // installs a handler of its own for the same reason, so catching it here is
                    // also the more faithful arrangement.
                    _ui.UnhandledException += (_, e) =>
                    {
                        e.Handled = true;
                        lock (Failures) Failures.Add(e.Exception);
                    };

                    started.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "WaveLab test UI",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the test UI thread did not start.");
                return _ui!;
            }
        }
    }

    /// <summary>The shared UI thread, so a listener can tell this app's noise from another test's.</summary>
    public static int ThreadId
    {
        get
        {
            _ = Ui;
            return _uiThreadId;
        }
    }

    /// <summary>Take and clear whatever the dispatcher has swallowed since it was last drained.</summary>
    public static Exception[] DrainFailures()
    {
        lock (Failures)
        {
            Exception[] taken = [.. Failures];
            Failures.Clear();
            return taken;
        }
    }

    /// <summary>Run on the shared UI thread and rethrow anything it raised on the caller's.</summary>
    /// <remarks>
    /// One test body at a time, and the lock is held by the calling thread rather than the UI one.
    /// <see cref="Pump"/> pushes a dispatcher frame, which runs whatever else is queued — so
    /// without this, another test's work executes inside the window this one is holding open, and
    /// its binding errors are collected as though they belonged to this dialog. Queue nothing and
    /// there is nothing to run.
    /// </remarks>
    public static void Run(Action action)
    {
        lock (RunGate) Ui.Invoke(action);
    }

    public static T Run<T>(Func<T> action)
    {
        lock (RunGate) return Ui.Invoke(action);
    }

    /// <summary>
    /// Let the dispatcher finish everything already queued — loaded handlers, layout, the render
    /// pass — which is what turns a constructed window into one that has actually been through the
    /// path a user's would.
    /// </summary>
    public static void Pump() => Pump(DispatcherPriority.ContextIdle);

    /// <summary>
    /// Drain everything queued above <paramref name="until"/>, then return.
    /// </summary>
    /// <remarks>
    /// The priority is the whole of it. A frame ends when its own callback runs, so anything queued
    /// *below* that priority is still waiting when the pump returns — and <c>MainWindow</c> queues
    /// its final close at <see cref="DispatcherPriority.ApplicationIdle"/>, which sits under
    /// <see cref="DispatcherPriority.ContextIdle"/>. Pumped at the default the shell simply never
    /// closed, and it looked like a hang in its shutdown rather than a pump that stopped too soon.
    /// </remarks>
    public static void Pump(DispatcherPriority until)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(until, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Show a window where no monitor reaches, run it through load and layout, and close it again.
    /// </summary>
    /// <remarks>
    /// Showing it is the point: an unshown window lays its content out but never raises
    /// <c>Loaded</c>, and several of these dialogs do their real work there. Offscreen and
    /// unactivated so a test run does not steal focus — and note that <c>MainWindow</c> may not be
    /// driven this way, because its close path writes its position to the real settings file.
    /// </remarks>
    public static void Show(Window window, Action<Window> inspect)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10_000;
        window.Top = -10_000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        DrainFailures();
        try
        {
            window.Show();
            Pump();
            inspect(window);
        }
        finally
        {
            window.Close();

            // Not one pump: a window may cancel its own Closing, do asynchronous shutdown work and
            // re-close itself afterwards, which is exactly what MainWindow does. The wait is in
            // time rather than in pumps, because the work it is waiting on is on other threads and
            // a tight loop of pumps outruns it. Give up loudly rather than leave it open for the
            // next test.
            var deadline = Environment.TickCount64 + 15_000;
            while (window.IsVisible && Environment.TickCount64 < deadline)
            {
                Pump(DispatcherPriority.SystemIdle);
                Thread.Sleep(5);
            }
            // A window that will not close has usually thrown on the way out, and that exception is
            // on the dispatcher rather than in this stack — so say both or the useful half is lost.
            if (window.IsVisible)
            {
                Exception[] onTheWayOut = DrainFailures();
                Assert.Fail($"{window.GetType().Name} would not close. Dispatcher raised " +
                            $"{onTheWayOut.Length}: " +
                            string.Join(" | ", onTheWayOut.Select(f => $"{f.GetType().Name}: {f.Message}")));
            }
        }

        // Closing counts: a window that throws on its way out is a window that throws every time
        // it is used, and the exception surfaces here rather than in the caller's stack.
        Exception[] failures = DrainFailures();
        Assert.True(failures.Length == 0,
            $"{window.GetType().Name} raised {failures.Length} unhandled exception(s): " +
            string.Join(" | ", failures.Select(f => f.Message)));
    }
}

/// <summary>
/// Collects the binding failures WPF otherwise only writes to a debugger's output window.
/// </summary>
/// <remarks>
/// A broken binding does not throw. The property keeps its default, the control looks plausible,
/// and the only trace is a line in a trace source nobody is listening to — which is exactly why the
/// audit could say nothing about whether any XAML binding in this app resolves.
/// </remarks>
internal sealed class BindingErrors : IDisposable
{
    private readonly Collector _collector = new();
    private readonly SourceLevels _previousLevel;

    public BindingErrors()
    {
        PresentationTraceSources.Refresh();
        _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(_collector);
    }

    public IReadOnlyList<string> Messages => _collector.Messages;

    public void Dispose()
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(_collector);
        PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
        _collector.Dispose();
    }

    /// <remarks>
    /// The trace source is process-wide, so a test building WPF elements on a thread of its own --
    /// several do -- writes into the same listener. Only what the shared UI thread reports is this
    /// dialog's, and without that filter this assertion fails on whichever dialog happened to be
    /// open when some other test ran.
    /// </remarks>
    private sealed class Collector : TraceListener
    {
        private readonly List<string> _messages = [];
        private string _pending = string.Empty;

        public IReadOnlyList<string> Messages => _messages;

        private static bool Ours => Environment.CurrentManagedThreadId == Wpf.ThreadId;

        public override void Write(string? message)
        {
            if (Ours) _pending += message;
        }

        public override void WriteLine(string? message)
        {
            if (!Ours) return;
            string line = (_pending + message).Trim();
            _pending = string.Empty;
            if (line.Length > 0) _messages.Add(line);
        }
    }
}
