using System.IO;
using System.Windows;
using WaveLab.Util;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The two windows <see cref="DialogLoadTests"/> leaves out, and the reasons it left them out.
/// </summary>
/// <remarks>
/// <para>
/// <c>SettingsDialog</c> reads <see cref="AppSettings"/> and writes it back, and <c>MainWindow</c>
/// does both plus autosave, session restore and the window's own placement — so both belong in the
/// collection that redirects the app-data root, and neither may run against a developer's real
/// <c>%AppData%\WaveLab</c>.
/// </para>
/// <para>
/// <c>MainWindow</c> was excluded for a second reason: its close path writes its position to that
/// file, and a window parked where no monitor reaches would be restored there next launch — the app
/// would look like it had failed to start. That is not a reason to leave it untested; it is the
/// thing to test, and <see cref="AnOffscreenShellDoesNotStoreAPositionNoMonitorCanReach"/> is it.
/// </para>
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class ShellWindowTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public ShellWindowTests(ITestOutputHelper output)
    {
        _output = output;
        AppSettings.AppDataDir = _sandbox;
    }

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    [Fact]
    public void TheSettingsDialogOpensAgainstTheShippedThemeWithoutABindingFailure()
    {
        IReadOnlyList<string> failures = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            Wpf.Show(new SettingsDialog(), window =>
            {
                Assert.True(window.IsLoaded);
                Assert.True(window.ActualWidth > 0 && window.ActualHeight > 0);
            });
            return errors.Messages.ToArray();
        });

        Assert.Empty(failures);
    }

    /// <summary>
    /// Opening the settings dialog and closing it again is not a settings change: nothing is
    /// written until something is chosen.
    /// </summary>
    [Fact]
    public void OpeningSettingsWritesNothing()
    {
        Wpf.Run(() => Wpf.Show(new SettingsDialog(), _ => { }));

        Assert.False(File.Exists(AppSettings.SettingsPath),
            "the settings file was written by a dialog that was only looked at.");
    }

    [Fact]
    public void TheMainWindowOpensAgainstTheShippedThemeWithoutABindingFailure()
    {
        IReadOnlyList<string> failures = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            Wpf.Show(new MainWindow(), window =>
            {
                Assert.True(window.IsLoaded);
                Assert.True(window.ActualWidth > 0 && window.ActualHeight > 0);
            });
            return errors.Messages.ToArray();
        });

        foreach (string failure in failures) _output.WriteLine(failure);
        Assert.Empty(failures);
    }

    /// <summary>
    /// A window shown where no monitor reaches must not have that position remembered.
    /// </summary>
    /// <remarks>
    /// The shell saves its placement as it closes. Saved from here it would be restored at −10,000
    /// on the next real launch: focused, in the task bar, and invisible — a program that appears not
    /// to start, and that survives a restart and a reinstall because the position outlives the
    /// process. <see cref="WindowPlacement.IsReachable"/> gates the save for exactly this, and this
    /// is the test that says so.
    /// </remarks>
    [Fact]
    public void AnOffscreenShellDoesNotStoreAPositionNoMonitorCanReach()
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), _ => { }));

        // The shell does write its settings on a clean exit, and the size is worth keeping — it is
        // only the position that has to be refused, so assert the file is there and that the size
        // reached it. A test that passed because nothing was written would prove nothing.
        Assert.True(File.Exists(AppSettings.SettingsPath), "the shell wrote no settings at all.");

        string saved = File.ReadAllText(AppSettings.SettingsPath);
        _output.WriteLine(saved);
        Assert.Contains("\"WindowWidth\"", saved);
        Assert.Contains("\"WindowLeft\": null", saved);
        Assert.Contains("\"WindowTop\": null", saved);
        Assert.DoesNotContain("-10000", saved);
    }
}
