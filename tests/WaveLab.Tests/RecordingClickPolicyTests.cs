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
    public void RequiredHalfDecibelReductionIsNotPresentedAsNoChange()
    {
        using var vm = new RecordViewModel();
        var snapshot = new RecordingLevelAnalyzer(8_000, 1).Snapshot with
        {
            Status = RecordingLevelStatus.Hot,
            SuggestedGainDb = -0.5,
        };
        Set(vm, "_levelSnapshot", snapshot);
        Set(vm, "_inputLevelDb", 0.0);
        Set(vm, "_inputFineTrimDb", 0.0);
        Set(vm, "_heldRecommendedTotalDb", -0.5);
        Assert.Equal("REDUCE 0.5 dB", vm.SuggestedGainText);
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
    [InlineData(-0.5)]
    [InlineData(-1.0)]
    public void RememberedSmallReductionsStillSayReduceAfterReload(double reduction)
    {
        Assert.True(AppSettings.Instance.SetInputCalibration("memory-test",
            new AppSettings.InputCalibrationInfo(reduction, -5.5, DateTime.UtcNow)));
        AppSettings.AppDataDir = _sandbox;
        using var vm = new RecordViewModel();
        Set(vm, "_selectedDevice", new CaptureDevice("memory-test", "Test input"));

        Assert.Contains($"reduce {Math.Abs(reduction):0.0} dB", vm.DeviceMemoryText);
        Assert.DoesNotContain("no change", vm.DeviceMemoryText);
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

    [Fact]
    public void FinishedWholeScanReplacesTheConservativeHeldSetting()
    {
        using var vm = new RecordViewModel();
        Set(vm, "_inputLevelDb", 0.0);
        Set(vm, "_inputFineTrimDb", 0.0);
        Set(vm, "_heldRecommendedTotalDb", -2.0);
        Set(vm, "_heldProgramPeakDb", -10.0);
        var final = new RecordingLevelAnalyzer(8_000, 1).Snapshot with
        {
            IsCompletedFullScan = true,
            Status = RecordingLevelStatus.TooLow,
            ActiveSeconds = 87.2,
            ProgramPeakDb = -9.1,
            TruePeakDb = -6.9,
            SuggestedGainDb = 5.5,
        };
        typeof(RecordViewModel).GetMethod("UpdateHeldRecommendation", Private)!.Invoke(vm, [final]);
        Set(vm, "_levelSnapshot", final);
        Assert.Equal(5.5, Get<double>(vm, "_heldRecommendedTotalDb"));
        Assert.Equal(-9.1, Get<double>(vm, "_heldProgramPeakDb"));
        Assert.Equal("OPTIONAL +5.5 dB", vm.SuggestedGainText);

        // A bad final pass must not leave a previously usable setting behind.
        typeof(RecordViewModel).GetMethod("UpdateHeldRecommendation", Private)!
            .Invoke(vm, [final with { InvalidSamples = 1 }]);
        Assert.True(double.IsNaN(Get<double>(vm, "_heldRecommendedTotalDb")));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(130)]
    public void IncompleteFullScanDoesNotOverwriteMemoryOrClaimACompletedCaptureNote(int activeSeconds)
    {
        var remembered = new AppSettings.InputCalibrationInfo(-1, -7, DateTime.UtcNow);
        Assert.True(AppSettings.Instance.SetInputCalibration("incomplete-test", remembered));
        using var vm = new RecordViewModel();
        Set(vm, "_selectedDevice", new CaptureDevice("incomplete-test", "Test input"));
        Set(vm, "_hasInputLevelControl", false);
        Set(vm, "_heldRecommendedTotalDb", 5.5);
        Set(vm, "_heldProgramPeakDb", -9.1);
        var incomplete = new RecordingLevelAnalyzer(8_000, 1).Snapshot with
        {
            Status = RecordingLevelStatus.Good,
            ActiveSeconds = activeSeconds,
            IsCompletedFullScan = false,
        };

        typeof(RecordViewModel).GetMethod("SaveWholeRecordCalibration", Private)!.Invoke(vm, [incomplete]);
        Assert.Equal(remembered, AppSettings.Instance.GetInputCalibration("incomplete-test"));
        Assert.Null(typeof(RecordViewModel).GetMethod("BuildWholeRecordCaptureNote", Private)!.Invoke(vm, [incomplete]));
    }

    [Theory]
    [InlineData(RecordingLevelStatus.Clipping, 0)]
    [InlineData(RecordingLevelStatus.UpstreamClipping, 0)]
    [InlineData(RecordingLevelStatus.Good, 1)]
    public void StoppedFullScanWithInputProblemsDoesNotPromiseAFinalSetting(RecordingLevelStatus status, long invalid)
    {
        using var vm = new RecordViewModel();
        Set(vm, "_levelCheckStopped", true);
        Set(vm, "_stoppedLevelCheckWasWholeRecord", true);
        Set(vm, "_levelSnapshot", new RecordingLevelAnalyzer(8_000, 1).Snapshot with
        {
            IsCompletedFullScan = true,
            Status = status,
            InvalidSamples = invalid,
        });
        Assert.Contains("needs attention", vm.LevelStatusTitle);
        Assert.Contains("no final setting", vm.LevelStatusDetail);
    }

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, Private)!.SetValue(target, value);

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
