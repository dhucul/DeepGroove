using System.Text.Json;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Vst3;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// A plugin as a member of the effect rack: the change list that carries a slider to the audio, the
/// type ids that survive a preset, and — where plugins happen to be installed — the real thing.
/// </summary>
public sealed class Vst3EffectTests(ITestOutputHelper output)
{
    /// <summary>Plugins on the machine running the tests that scanned cleanly, or an empty list.</summary>
    private static List<Vst3ScanResult> Usable =>
    [
        .. Vst3Catalogue.Discover()
            .Select(Vst3Catalogue.ScanInProcess)
            .Where(r => r.IsUsable)
    ];

    // ── the change list ──────────────────────────────────────────
    //
    // Driven through the vtable, by slot, exactly as a plugin reads it. Testing the managed side
    // would prove the bookkeeping and nothing about whether a plugin can find the values.

    [Fact]
    public unsafe void AParameterMoveReachesThePluginThroughTheChangeList()
    {
        using var changes = new Vst3ParameterChanges([11u, 22u, 33u]);
        changes.Set(changes.IndexOf(22u), 0.75);

        nint pointer = changes.Prepare();
        Assert.NotEqual(0, pointer);

        Assert.Equal(1, ParameterCount(pointer));
        void* queue = ParameterData(pointer, 0);
        Assert.True(queue != null);
        Assert.Equal(22u, QueueId(queue));
        Assert.Equal(1, PointCount(queue));

        (int offset, double value) = Point(queue, 0);
        Assert.Equal(0, offset);
        Assert.Equal(0.75, value, 9);
    }

    /// <summary>
    /// A block during which nothing moved carries no list at all. Nearly every block is one of
    /// those, and a plugin handed no list takes the cheapest path it has.
    /// </summary>
    [Fact]
    public void ABlockWithNothingPendingCarriesNoList()
    {
        using var changes = new Vst3ParameterChanges([1u, 2u]);
        Assert.Equal(0, changes.Prepare());

        changes.Set(0, 0.5);
        Assert.NotEqual(0, changes.Prepare());

        // Consumed: the value is the plugin's now, and repeating it every block would be the host
        // telling a plugin something it already knows, for ever.
        Assert.Equal(0, changes.Prepare());
    }

    /// <summary>
    /// A dragged slider passes through a hundred values before it is let go. Only where it landed
    /// matters by the time the block runs, so the list carries one point, not a hundred.
    /// </summary>
    [Fact]
    public unsafe void ADraggedSliderArrivesAsOneValueAndNotAHundred()
    {
        using var changes = new Vst3ParameterChanges([5u]);
        for (int i = 0; i <= 100; i++) changes.Set(0, i / 100.0);

        nint pointer = changes.Prepare();
        Assert.Equal(1, ParameterCount(pointer));
        Assert.Equal(1.0, Point(ParameterData(pointer, 0), 0).Value, 9);
    }

    [Fact]
    public unsafe void EveryMovedParameterGetsItsOwnQueue()
    {
        using var changes = new Vst3ParameterChanges([7u, 8u, 9u]);
        changes.Set(changes.IndexOf(7u), 0.1);
        changes.Set(changes.IndexOf(9u), 0.9);

        nint pointer = changes.Prepare();
        Assert.Equal(2, ParameterCount(pointer));

        var seen = new Dictionary<uint, double>();
        for (int i = 0; i < 2; i++)
        {
            void* queue = ParameterData(pointer, i);
            seen[QueueId(queue)] = Point(queue, 0).Value;
        }

        Assert.Equal(0.1, seen[7u], 9);
        Assert.Equal(0.9, seen[9u], 9);
        Assert.False(seen.ContainsKey(8u));
    }

    [Fact]
    public unsafe void ReadingPastTheEndOfTheListGivesNothingRatherThanRubbish()
    {
        using var changes = new Vst3ParameterChanges([4u]);
        changes.Set(0, 0.5);
        nint pointer = changes.Prepare();

        Assert.True(ParameterData(pointer, 1) == null);
        Assert.True(ParameterData(pointer, -1) == null);
        Assert.False(Vst3Abi.Ok(PointResult(ParameterData(pointer, 0), 1)));
    }

    [Fact]
    public void AValueOutsideTheNormalisedRangeIsBroughtIntoIt()
    {
        using var changes = new Vst3ParameterChanges([1u]);
        changes.Set(0, 4.2);

        nint pointer = changes.Prepare();
        Assert.Equal(1.0, ReadFirstValue(pointer), 9);

        changes.Set(0, -3);
        Assert.Equal(0.0, ReadFirstValue(changes.Prepare()), 9);
    }

    [Fact]
    public void AnUnknownParameterIsNotACrash()
    {
        using var changes = new Vst3ParameterChanges([1u]);
        Assert.Equal(-1, changes.IndexOf(999u));

        changes.Set(-1, 0.5);
        changes.Set(50, 0.5);
        Assert.Equal(0, changes.Prepare());
    }

    private static unsafe double ReadFirstValue(nint pointer) =>
        Point(ParameterData(pointer, 0), 0).Value;

    private static unsafe int ParameterCount(nint pointer)
    {
        void* self = (void*)pointer;
        var call = (delegate* unmanaged[Stdcall]<void*, int>)
            (*(void***)self)[3];
        return call(self);
    }

    private static unsafe void* ParameterData(nint pointer, int index)
    {
        void* self = (void*)pointer;
        var call = (delegate* unmanaged[Stdcall]<void*, int, void*>)
            (*(void***)self)[4];
        return call(self, index);
    }

    private static unsafe uint QueueId(void* queue)
    {
        var call = (delegate* unmanaged[Stdcall]<void*, uint>)
            (*(void***)queue)[3];
        return call(queue);
    }

    private static unsafe int PointCount(void* queue)
    {
        var call = (delegate* unmanaged[Stdcall]<void*, int>)
            (*(void***)queue)[4];
        return call(queue);
    }

    private static unsafe (int Offset, double Value) Point(void* queue, int index)
    {
        var call = (delegate* unmanaged[Stdcall]<void*, int, int*, double*, int>)(*(void***)queue)[5];

        int offset;
        double value;
        return Vst3Abi.Ok(call(queue, index, &offset, &value)) ? (offset, value) : (-1, double.NaN);
    }

    private static unsafe int PointResult(void* queue, int index)
    {
        var call = (delegate* unmanaged[Stdcall]<void*, int, int*, double*, int>)(*(void***)queue)[5];

        int offset;
        double value;
        return call(queue, index, &offset, &value);
    }

    // ── type ids, presets ────────────────────────────────────────

    [Fact]
    public void APluginTypeIdCarriesItsPathAndABuiltInOneDoesNot()
    {
        const string path = @"C:\Program Files\Common Files\VST3\Example.vst3";
        Assert.Equal(path, Vst3Effect.PathFromTypeId(Vst3Effect.TypeIdPrefix + path));

        Assert.Null(Vst3Effect.PathFromTypeId("compressor"));
        Assert.Null(Vst3Effect.PathFromTypeId(""));
        Assert.Null(Vst3Effect.PathFromTypeId(null));
    }

    /// <summary>
    /// A preset saved where a plugin is installed has to load where it is not. The plugin's slot is
    /// lost — there is nothing to put in it — and every other effect in the chain still arrives.
    /// </summary>
    [Fact]
    public void APresetNamingAMissingPluginStillLoadsTheRestOfTheChain()
    {
        var preset = new EffectFactory.ChainPreset
        {
            Name = "with a plugin in the middle",
            Effects =
            [
                new EffectFactory.EffectState { TypeId = "eq" },
                new EffectFactory.EffectState
                {
                    TypeId = Vst3Effect.TypeIdPrefix + @"Z:\nothing\here\Absent.vst3",
                    State = Convert.ToBase64String([1, 2, 3]),
                },
                new EffectFactory.EffectState { TypeId = "limiter" },
            ],
        };

        List<IAudioEffect> chain = EffectFactory.Instantiate(preset);
        Assert.Equal(["eq", "limiter"], chain.Select(fx => fx.TypeId));
    }

    /// <summary>
    /// The plugin state field has to survive the file. It is written last and read first so that a
    /// preset saved before it existed still loads, and this is what proves the round trip.
    /// </summary>
    [Fact]
    public void APluginsOwnStateSurvivesBeingWrittenToAPresetAndReadBack()
    {
        var preset = new EffectFactory.ChainPreset
        {
            Name = "state",
            Effects =
            [
                new EffectFactory.EffectState
                {
                    TypeId = Vst3Effect.TypeIdPrefix + @"C:\x\Y.vst3",
                    Params = { ["p3"] = 0.25 },
                    State = Convert.ToBase64String([9, 8, 7, 6]),
                },
            ],
        };

        string json = JsonSerializer.Serialize(preset);
        EffectFactory.ChainPreset? read = JsonSerializer.Deserialize<EffectFactory.ChainPreset>(json);

        Assert.NotNull(read);
        EffectFactory.EffectState state = read!.Effects[0];
        Assert.Equal(Convert.ToBase64String([9, 8, 7, 6]), state.State);
        Assert.Equal(0.25, state.Params["p3"], 9);
    }

    /// <summary>A preset written before plugins existed has no such field, and must still load.</summary>
    [Fact]
    public void APresetFromBeforePluginStateExistedStillLoads()
    {
        const string json = """
            {"Name":"old","Effects":[{"TypeId":"eq","Enabled":true,"Params":{"lowGain":1.5}}]}
            """;

        EffectFactory.ChainPreset? preset =
            JsonSerializer.Deserialize<EffectFactory.ChainPreset>(json);
        Assert.NotNull(preset);
        Assert.Null(preset!.Effects[0].State);

        List<IAudioEffect> chain = EffectFactory.Instantiate(preset);
        Assert.Single(chain);
        Assert.Equal(1.5, chain[0].GetParam("lowGain"), 6);
    }

    [Fact]
    public void ARackWithNoPluginInItSavesNoPluginState()
    {
        var master = new MasterSection();
        EffectFactory.ChainPreset captured = EffectFactory.Capture("built-ins", master.ChainSnapshot);

        Assert.NotEmpty(captured.Effects);
        Assert.All(captured.Effects, state => Assert.Null(state.State));
    }

    // ── the real thing ───────────────────────────────────────────

    /// <summary>
    /// Every plugin that scanned cleanly, opened as a rack effect and run. This is the whole path a
    /// user takes: the menu builds from the same list, and the rack calls exactly these methods.
    /// </summary>
    [Fact]
    public void EveryUsablePluginWorksAsARackEffect()
    {
        List<Vst3ScanResult> usable = Usable;
        if (usable.Count == 0)
        {
            output.WriteLine("No usable VST3 plugins installed; nothing to check against.");
            return;
        }

        int worked = 0;
        foreach (Vst3ScanResult scan in usable)
        {
            Vst3Effect? effect = Vst3PluginHost.Instance.Open(scan.Path, out string error);
            if (effect == null)
            {
                output.WriteLine($"{scan.Name}: would not open — {error}");
                continue;
            }

            try
            {
                effect.Configure(44_100, 2);
                Assert.True(effect.Configured, $"{scan.Name} refused 44.1 kHz stereo as a rack effect.");
                Assert.StartsWith(Vst3Effect.TypeIdPrefix, effect.TypeId, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(effect.DisplayName));

                var block = new float[512 * 2];
                for (int i = 0; i < block.Length; i++) block[i] = 0.25f * MathF.Sin(i * 0.05f);
                effect.Process(block, 0, block.Length);

                Assert.All(block, sample => Assert.True(float.IsFinite(sample)));
                Assert.Null(effect.Readout);
                worked++;

                output.WriteLine(
                    $"{scan.Name} ({scan.Vendor}): {effect.Params.Count} rack parameters, "
                    + $"latency {effect.LatencySamples}, editor {effect.HasEditor}");
            }
            finally { effect.Dispose(); }
        }

        output.WriteLine($"{worked} of {usable.Count} usable plugins ran as rack effects.");
        Assert.True(worked > 0, "No usable plugin could be run as a rack effect.");
    }

    /// <summary>
    /// A plugin's settings have to come back. On this machine none of them publish parameters, so
    /// the opaque state is the only thing a preset can carry — which makes this the test that says
    /// whether saving a rack containing a plugin means anything at all.
    /// </summary>
    [Fact]
    public void APluginsStateSurvivesBeingSavedAndPutBack()
    {
        Vst3ScanResult? scan = Usable.FirstOrDefault();
        if (scan == null)
        {
            output.WriteLine("No usable VST3 plugins installed; nothing to check against.");
            return;
        }

        Vst3Effect? effect = Vst3PluginHost.Instance.Open(scan.Path, out _);
        Assert.NotNull(effect);

        try
        {
            effect!.Configure(44_100, 2);
            string state = effect.SaveStateBase64();
            if (state.Length == 0)
            {
                output.WriteLine($"{scan.Name} gives up no state; nothing to round-trip.");
                return;
            }

            Assert.True(effect.ApplyStateNow(state), $"{scan.Name} would not take back its own state.");
            Assert.Equal(state, effect.SaveStateBase64());
        }
        finally { effect?.Dispose(); }
    }

    /// <summary>
    /// A snapshot shares the plugin rather than loading a second one, so letting go of one side must
    /// not take the other with it. This is the counting, checked where it would actually bite.
    /// </summary>
    [Fact]
    public void ASnapshotOfAPluginOutlivesTheEffectItWasTakenFrom()
    {
        Vst3ScanResult? scan = Usable.FirstOrDefault();
        if (scan == null)
        {
            output.WriteLine("No usable VST3 plugins installed; nothing to check against.");
            return;
        }

        Vst3Effect? original = Vst3PluginHost.Instance.Open(scan.Path, out _);
        Assert.NotNull(original);
        original!.Configure(44_100, 2);

        var clone = (Vst3Effect)EffectFactory.Clone(original);
        Assert.NotSame(original, clone);
        Assert.Equal(original.TypeId, clone.TypeId);

        // The original goes. The plugin behind it must not.
        original.Dispose();

        clone.Configure(44_100, 2);
        Assert.True(clone.Configured, "The shared plugin died with the effect that was cloned from it.");

        var block = new float[256 * 2];
        clone.Process(block, 0, block.Length);
        Assert.All(block, sample => Assert.True(float.IsFinite(sample)));

        clone.Dispose();
    }

    /// <summary>
    /// The scanner has to be provably able to run before any plugin is judged by it.
    /// </summary>
    /// <remarks>
    /// A scanner that cannot start fails every plugin identically, and is indistinguishable from a
    /// plugin fault — which is recorded as a crash and deliberately never retried. Found by running
    /// a refresh from a host that was not WaveLab: it condemned all twenty-two plugins on this
    /// machine in one pass, permanently, over a problem belonging to none of them.
    /// </remarks>
    [Fact]
    public async Task TheScannerAnswersBeforeAnyPluginIsJudgedByIt()
    {
        Assert.True(
            await Vst3Catalogue.ScannerRespondsAsync(),
            "The scanner did not answer; a refresh would have condemned every installed plugin.");
    }

    /// <summary>
    /// A plugin the scanner could not survive is never loaded in this process, whatever asks for it.
    /// The scan is the only thing between the app and a plugin that faults on load.
    /// </summary>
    [Fact]
    public void APluginRememberedAsHavingCrashedIsNotLoaded()
    {
        Vst3PluginHost host = Vst3PluginHost.Instance;
        host.EnsureCatalogueLoaded();

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"wavelab-crasher-{Guid.NewGuid():N}.vst3");
        host.Catalogue.Record(new Vst3ScanResult
        {
            Path = path,
            Outcome = Vst3ScanOutcome.Crashed,
            Name = "Crasher",
            Message = "faulted while loading",
        });

        try
        {
            Assert.Null(host.Open(path, out string error));
            Assert.Contains("crashed", error, StringComparison.OrdinalIgnoreCase);
        }
        finally { host.Catalogue.Forget(path); }
    }
}
