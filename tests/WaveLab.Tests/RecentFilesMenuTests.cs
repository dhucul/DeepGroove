using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Util;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The Recent Files submenu, which is the one menu in the shell whose contents are half bound and
/// half declared.
/// </summary>
/// <remarks>
/// A <c>MenuItem</c> cannot carry both an <c>ItemsSource</c> and a fixed child, so adding Clear
/// turned the submenu into a <c>CompositeCollection</c> — and the container holding the bound half
/// sits in neither tree, so it reaches the view model only through a
/// <see cref="BindingProxy"/> in the window's resources. That indirection fails silently: a broken
/// proxy binding leaves an empty container, so the submenu still opens, still shows Clear, and
/// simply never lists a file again. Hence a test that counts the paths rather than one that only
/// looks for the new entry.
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class RecentFilesMenuTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public RecentFilesMenuTests(ITestOutputHelper output)
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
    public void TheSubmenuListsTheRecentPathsAndClearsThemOnDemand()
    {
        AppSettings settings = AppSettings.Instance;
        Assert.True(settings.AddRecentFile(@"C:\audio\one.wav"), settings.LastSaveError);
        Assert.True(settings.AddRecentFile(@"C:\audio\two.wav"), settings.LastSaveError);

        IReadOnlyList<string> failures = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            Wpf.Show(new MainWindow(), window =>
            {
                MenuItem recent = RecentFilesMenu(window);

                // Most recent first, then the separator, then Clear.
                Assert.Equal(new[] { @"C:\audio\two.wav", @"C:\audio\one.wav" }, Paths(recent));

                // Escaped where it is read, raw where it is acted on. One binding, two jobs, and
                // a converter on the wrong one would open a path that was never stored.
                Assert.IsType<AccessKeyEscapeConverter>(
                    BindingFor(recent.ItemContainerStyle, MenuItem.HeaderProperty).Converter);
                Assert.Null(BindingFor(recent.ItemContainerStyle, MenuItem.CommandParameterProperty).Converter);

                Separator separator = recent.Items.OfType<Separator>().Single();
                Assert.Equal(Visibility.Visible, separator.Visibility);

                MenuItem clear = ClearEntry(recent);
                ICommand command = clear.Command
                    ?? throw new InvalidOperationException("Clear has no command; its binding did not resolve.");
                Assert.True(command.CanExecute(null), "Clear was disabled with two paths listed.");

                command.Execute(null);
                Wpf.Pump();

                Assert.Empty(Paths(recent));
                Assert.Empty(AppSettings.Instance.RecentFilesSnapshot());
                Assert.False(command.CanExecute(null), "Clear stayed enabled with nothing to clear.");

                // Nothing above it any more, so the rule would be a line at the top of the menu.
                Assert.Equal(Visibility.Collapsed, separator.Visibility);

                // The entry itself survives the collection it sits beside going empty.
                Assert.Same(clear, ClearEntry(recent));
            });
            return errors.Messages.ToArray();
        });

        foreach (string failure in failures) _output.WriteLine(failure);
        Assert.Empty(failures);
    }

    /// <summary>
    /// A path is text, not a mnemonic. The submenu template recognises access keys, so an
    /// underscore in a file name is eaten from the display and claims the character after it as a
    /// shortcut — <c>take_1.wav</c> listing as <c>take1.wav</c>, and 1 invoking it.
    /// </summary>
    [Fact]
    public void AnUnderscoreInAPathIsEscapedRatherThanEaten()
    {
        var converter = new AccessKeyEscapeConverter();

        Assert.Equal(@"C:\takes\take__1.wav", Escape(converter, @"C:\takes\take_1.wav"));
        Assert.Equal(@"C:\takes\__leading.wav", Escape(converter, @"C:\takes\_leading.wav"));
        Assert.Equal(@"C:\takes\plain.wav", Escape(converter, @"C:\takes\plain.wav"));
        Assert.Null(Escape(converter, null));
    }

    private static object? Escape(IValueConverter converter, string? path) =>
        converter.Convert(path, typeof(object), null, CultureInfo.InvariantCulture);

    private static Binding BindingFor(Style style, DependencyProperty property) =>
        (Binding)style.Setters.OfType<Setter>().Single(setter => setter.Property == property).Value;

    private static MenuItem RecentFilesMenu(Window window)
    {
        Menu menu = FindVisual<Menu>(window) ?? throw new InvalidOperationException("no menu bar.");
        MenuItem file = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "_File");
        return file.Items.OfType<MenuItem>().Single(item => item.Header as string == "Recent Files");
    }

    private static string[] Paths(MenuItem recent) => [.. recent.Items.OfType<string>()];

    private static MenuItem ClearEntry(MenuItem recent) =>
        recent.Items.OfType<MenuItem>().Single(item => item.Header as string == "Clear Recent Files");

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            T? found = FindVisual<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
