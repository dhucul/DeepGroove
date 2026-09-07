using System.Reflection;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class RecordingClickPolicyTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public RecordingClickPolicyTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void ChangingPolicyClearsTheOldAppliedMessage()
    {
        using var vm = new RecordViewModel();
        Set(vm, "_recommendationApplied", true);
        Set(vm, "_applyRecommendationStatusText", "Applied — play again to confirm.");
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.IgnorePopsAndClicks = false;

        Assert.NotEqual("Applied", vm.ApplyRecommendationText);
        Assert.Empty(vm.ApplyRecommendationStatusText);
        Assert.Contains(nameof(vm.ApplyRecommendationStatusText), notifications);
    }

    [Fact]
    public void ChangingPolicyKeepsLiveMeterHistoryAndPublishesTheNewSnapshotFirst()
    {
        using var vm = new RecordViewModel();
        var engine = Get<RecordingEngine>(vm, "_engine");
        var analyzer = Get<RecordingLevelAnalyzer>(engine, "_levelAnalyzer");
        analyzer.Configure(8_000, 1);
        FeedClicks(analyzer);
        Set(vm, "_levelSnapshot", analyzer.Snapshot);
        Set(vm, "_isLevelChecking", true);
        vm.LevelHistory.Append(-12);
        double[] history = vm.LevelHistory.ToArray();
        bool notified = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.IgnorePopsAndClicks)) return;
            notified = true;
            Assert.Equal(RecordingLevelStatus.Clipping, Get<RecordingLevelSnapshot>(vm, "_levelSnapshot").Status);
        };

        vm.IgnorePopsAndClicks = false;

        Assert.True(notified);
        Assert.Equal(history, vm.LevelHistory.ToArray());
        Assert.Equal(12, engine.LevelSnapshot.ActiveSeconds);
    }

    [Fact]
    public void ChangingPolicyAllowsAHigherRevisedRecommendationToBeRemembered()
    {
        AppSettings.Instance.RecordingIgnorePopsAndClicks = false;
        using var vm = new RecordViewModel();
        var engine = Get<RecordingEngine>(vm, "_engine");
        var analyzer = Get<RecordingLevelAnalyzer>(engine, "_levelAnalyzer");
        analyzer.Configure(8_000, 1);
        FeedClicks(analyzer);
        Set(vm, "_selectedDevice", new CaptureDevice("policy-test", "Test input"));
        Set(vm, "_hasInputLevelControl", false);
        Set(vm, "_calibrationSavedTotalDb", -9.0);
        Set(vm, "_isLevelChecking", true);

        vm.IgnorePopsAndClicks = true;
        typeof(RecordViewModel).GetMethod("SaveCalibrationOnceSettled", Private)!
            .Invoke(vm, [engine.LevelSnapshot, true]);

        Assert.True(AppSettings.Instance.InputCalibrations.ContainsKey("policy-test"));
    }

    [Fact]
    public void InvalidInputIsNotRelabeledOrColoredAsAnIgnoredClick()
    {
        using var vm = new RecordViewModel();
        var analyzer = new RecordingLevelAnalyzer(8_000, 1);
        FeedClicks(analyzer);
        Set(vm, "_levelSnapshot", analyzer.Snapshot with { InvalidSamples = 1 });

        Assert.Equal("1 INVALID", vm.ClippingText);
        Assert.False(vm.HasOnlyIsolatedClipping);
        Assert.Contains("invalid", vm.ClippingDetailText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonClickIntersampleOverIsVisibleInTheClippingReadout()
    {
        using var vm = new RecordViewModel();
        var empty = new RecordingLevelAnalyzer(8_000, 1).Snapshot;
        Set(vm, "_levelSnapshot", empty with { Status = RecordingLevelStatus.Hot, TruePeakDb = 0.4 });
        Assert.Equal("PEAK OVER", vm.ClippingText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAndReconfigureRetainPolicyButClearClickEvidence(bool ignore)
    {
        var analyzer = new RecordingLevelAnalyzer(8_000, 1) { IgnorePopsAndClicks = ignore };
        FeedClicks(analyzer);
        Assert.True(analyzer.Snapshot.HasOnlyClickClipping);
        analyzer.Reset();
        Assert.Equal(ignore, analyzer.IgnorePopsAndClicks);
        Assert.False(analyzer.Snapshot.HasOnlyClickClipping);
        FeedClicks(analyzer);
        analyzer.Configure(48_000, 2);
        Assert.Equal(ignore, analyzer.IgnorePopsAndClicks);
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, analyzer.Snapshot.Status);
        Assert.False(analyzer.Snapshot.HasOnlyClickClipping);
    }

    private static void FeedClicks(RecordingLevelAnalyzer analyzer)
    {
        var signal = new float[8_000];
        for (int i = 0; i < signal.Length; i++)
            signal[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 100 * i / 8_000));
        signal[600] = 1;
        for (int i = 0; i < 12; i++) analyzer.Process(signal);
    }

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, Private)!.SetValue(target, value);

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
