using System.Reflection;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Guards the binding paths the overlay and status strip use in MainWindow.xaml.
/// </summary>
/// <remarks>
/// WPF bindings fail silently: a renamed or mistyped property leaves a blank control and a trace
/// message nobody reads, and nothing in a build or a normal test run notices. These assertions are
/// the compile-time check the XAML cannot give us — if a property here is renamed without the XAML
/// following, this fails instead of the overlay quietly going empty.
/// </remarks>
public sealed class ProgressBindingTests
{
    private static void AssertBindable(Type type, string propertyName)
    {
        PropertyInfo? property = type.GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance);

        Assert.True(property is not null,
            $"MainWindow.xaml binds to {type.Name}.{propertyName}, which does not exist");
        Assert.True(property!.CanRead, $"{type.Name}.{propertyName} must be readable to bind to");
    }

    [Theory]
    [InlineData(nameof(ProgressHost.Blocking))]
    [InlineData(nameof(ProgressHost.Background))]
    [InlineData(nameof(ProgressHost.IsBlockingVisible))]
    [InlineData(nameof(ProgressHost.IsBackgroundVisible))]
    public void HostExposesWhatTheWindowBindsTo(string propertyName) =>
        AssertBindable(typeof(ProgressHost), propertyName);

    [Theory]
    [InlineData(nameof(OperationProgress.Title))]
    [InlineData(nameof(OperationProgress.Detail))]
    [InlineData(nameof(OperationProgress.HasDetail))]
    [InlineData(nameof(OperationProgress.Fraction))]
    [InlineData(nameof(OperationProgress.IsIndeterminate))]
    [InlineData(nameof(OperationProgress.PercentText))]
    [InlineData(nameof(OperationProgress.RemainingText))]
    [InlineData(nameof(OperationProgress.CancelCommand))]
    public void OperationExposesWhatTheOverlayBindsTo(string propertyName) =>
        AssertBindable(typeof(OperationProgress), propertyName);

    [Fact]
    public void MainViewModelExposesTheHostUnderTheNameTheWindowUses() =>
        AssertBindable(typeof(MainViewModel), "Progress");

    [Fact]
    public void ProgressPropertiesRaiseChangeNotifications()
    {
        // Everything the overlay shows is refreshed from a timer, so each must notify or the card
        // would draw once and then sit still for the whole operation.
        var operation = new OperationProgress("Rendering", "detail", DateTime.UtcNow);
        var raised = new List<string>();
        operation.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        operation.Report(0.5);
        operation.GetType()
            .GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(operation, [DateTime.UtcNow.AddSeconds(10)]);

        Assert.Contains(nameof(OperationProgress.Fraction), raised);
        Assert.Contains(nameof(OperationProgress.PercentText), raised);
        Assert.Contains(nameof(OperationProgress.RemainingText), raised);
        Assert.Contains(nameof(OperationProgress.IsIndeterminate), raised);
    }
}
