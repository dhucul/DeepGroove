using System.IO;
using System.Runtime.InteropServices;
using WaveLab.Audio.Vst3;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The VST3 host: the binary layouts it depends on, the catalogue, and — where plugins happen to be
/// installed — the real thing.
/// </summary>
public sealed class Vst3Tests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-vst3").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    /// <summary>Plugins installed on the machine running the tests, or an empty list.</summary>
    private static List<string> Installed => Vst3Catalogue.Discover();

    // ── the binary layouts ───────────────────────────────────────

    /// <summary>
    /// The sizes the C++ side expects. These are not style: a structure laid out wrongly is not a
    /// wrong answer, it is a plugin writing past the end of what was allocated for it. Every one of
    /// these was checked against a real plugin before being written down here.
    /// </summary>
    [Theory]
    [InlineData(typeof(Vst3Abi.PFactoryInfo), 452)]
    [InlineData(typeof(Vst3Abi.PClassInfo), 116)]
    [InlineData(typeof(Vst3Abi.PClassInfo2), 440)]
    [InlineData(typeof(Vst3Abi.BusInfo), 276)]
    [InlineData(typeof(Vst3Abi.ProcessSetup), 24)]
    [InlineData(typeof(Vst3Abi.AudioBusBuffers), 24)]
    [InlineData(typeof(Vst3Abi.ProcessData), 80)]
    [InlineData(typeof(Vst3Abi.ParameterInfo), 792)]
    public void TheAbiStructuresAreTheSizeTheCppSideExpects(Type type, int expected)
    {
        int actual = Marshal.SizeOf(type);
        output.WriteLine($"{type.Name} is {actual} bytes, expected {expected}");
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The one padding rule that is easy to get wrong and impossible to notice afterwards: a 32-bit
    /// field followed by a 64-bit one puts the second at offset eight, not four. Packing this
    /// tightly moves the channel-buffer pointer and hands a plugin an address that is not one.
    /// </summary>
    [Fact]
    public void AudioBusBuffersPutsItsPointerWhereTheCompilerWould()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<Vst3Abi.AudioBusBuffers>("ChannelCount"));
        Assert.Equal(8, (int)Marshal.OffsetOf<Vst3Abi.AudioBusBuffers>("SilenceFlags"));
        Assert.Equal(16, (int)Marshal.OffsetOf<Vst3Abi.AudioBusBuffers>("ChannelBuffers"));
    }

    [Fact]
    public void ProcessDataPutsItsBusPointersWhereTheCompilerWould()
    {
        Assert.Equal(24, (int)Marshal.OffsetOf<Vst3Abi.ProcessData>("Inputs"));
        Assert.Equal(32, (int)Marshal.OffsetOf<Vst3Abi.ProcessData>("Outputs"));
    }

    /// <summary>
    /// <c>kResultTrue</c> is zero and <c>kResultFalse</c> is one, so a plugin answering "no" returns
    /// 1 and not an error. Testing a result for non-zero reads a legitimate refusal as a failure.
    /// </summary>
    [Fact]
    public void SuccessIsZeroAndFalseIsNotAnError()
    {
        Assert.True(Vst3Abi.Ok(Vst3Abi.ResultOk));
        Assert.True(Vst3Abi.Ok(Vst3Abi.ResultTrue));
        Assert.False(Vst3Abi.Ok(Vst3Abi.ResultFalse));
        Assert.False(Vst3Abi.Ok(Vst3Abi.NotImplemented));
        Assert.Equal(0, Vst3Abi.ResultTrue);
        Assert.Equal(1, Vst3Abi.ResultFalse);
    }

    // ── the stream a plugin is handed ────────────────────────────

    [Fact]
    public unsafe void TheHostStreamReadsBackWhatWasWrittenToIt()
    {
        using var stream = new Vst3MemoryStream();
        Assert.True(stream.Pointer != null);

        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.True(WriteTo(stream, payload));
        Assert.Equal(payload, stream.ToArray());

        stream.Rewind();
        byte[] read = ReadFrom(stream, payload.Length);
        Assert.Equal(payload, read);
    }

    [Fact]
    public unsafe void TheHostStreamGrowsPastItsInitialBuffer()
    {
        using var stream = new Vst3MemoryStream();
        var payload = new byte[40_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        Assert.True(WriteTo(stream, payload));
        Assert.Equal(payload.Length, stream.ToArray().Length);
        Assert.Equal(payload, stream.ToArray());
    }

    /// <summary>
    /// Reading at the end is success with a count of zero, not a failure — a plugin reading its own
    /// state until exhaustion would otherwise treat the end of it as an error.
    /// </summary>
    [Fact]
    public unsafe void ReadingPastTheEndSucceedsWithNothing()
    {
        using var stream = new Vst3MemoryStream([1, 2, 3]);
        stream.Rewind();

        Assert.Equal([1, 2, 3], ReadFrom(stream, 3));
        Assert.Empty(ReadFrom(stream, 8));
    }

    private static unsafe bool WriteTo(Vst3MemoryStream stream, byte[] data)
    {
        void** self = (void**)stream.Pointer;
        var write = (delegate* unmanaged[Stdcall]<void*, void*, int, int*, int>)(*(void***)self)[4];

        int written;
        fixed (byte* source = data)
        {
            if (!Vst3Abi.Ok(write(self, source, data.Length, &written))) return false;
        }
        return written == data.Length;
    }

    private static unsafe byte[] ReadFrom(Vst3MemoryStream stream, int count)
    {
        void** self = (void**)stream.Pointer;
        var read = (delegate* unmanaged[Stdcall]<void*, void*, int, int*, int>)(*(void***)self)[3];

        var buffer = new byte[count];
        int got;
        fixed (byte* destination = buffer)
        {
            if (!Vst3Abi.Ok(read(self, destination, count, &got))) return [];
        }
        return buffer.AsSpan(0, got).ToArray();
    }

    // ── the objects the host exposes to a plugin ─────────────────

    [Fact]
    public void AViewRectIsTheSizeTheCppSideExpects()
    {
        Assert.Equal(16, Marshal.SizeOf<ViewRect>());

        var rect = new ViewRect { Left = 10, Top = 20, Right = 810, Bottom = 620 };
        Assert.Equal(800, rect.Width);
        Assert.Equal(600, rect.Height);
    }

    /// <summary>
    /// The host's edit handler is a managed object with a native vtable, called <em>by</em> the
    /// plugin. If the machinery is wrong the plugin calls into nothing, so this drives it the way a
    /// plugin would — through the vtable, by slot.
    /// </summary>
    [Fact]
    public unsafe void ThePluginCanReportAnEditThroughTheHostHandler()
    {
        using var handler = new Vst3ComponentHandler();
        Vst3ParameterEdit? seen = null;
        handler.ParameterEdited += edit => seen = edit;

        void** self = (void**)handler.Pointer;
        void** vtable = *(void***)self;

        var beginEdit = (delegate* unmanaged[Stdcall]<void*, uint, int>)vtable[3];
        var performEdit = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)vtable[4];
        var endEdit = (delegate* unmanaged[Stdcall]<void*, uint, int>)vtable[5];

        Assert.True(Vst3Abi.Ok(beginEdit(self, 7)));
        Assert.True(Vst3Abi.Ok(performEdit(self, 7, 0.25)));
        Assert.True(Vst3Abi.Ok(endEdit(self, 7)));

        Assert.NotNull(seen);
        Assert.Equal(7u, seen!.Value.Id);
        Assert.Equal(0.25, seen.Value.Normalized, 6);
    }

    [Fact]
    public unsafe void AnEditOutsideTheNormalisedRangeIsBroughtIntoIt()
    {
        using var handler = new Vst3ComponentHandler();
        var seen = new List<double>();
        handler.ParameterEdited += edit => seen.Add(edit.Normalized);

        void** self = (void**)handler.Pointer;
        var performEdit = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)(*(void***)self)[4];

        performEdit(self, 1, 5.0);
        performEdit(self, 1, -3.0);

        Assert.Equal([1.0, 0.0], seen);
    }

    [Fact]
    public unsafe void ThePluginCanAskToBeRestarted()
    {
        using var handler = new Vst3ComponentHandler();
        int flags = 0;
        handler.RestartRequested += f => flags = f;

        void** self = (void**)handler.Pointer;
        var restart = (delegate* unmanaged[Stdcall]<void*, int, int>)(*(void***)self)[6];

        Assert.True(Vst3Abi.Ok(restart(self, Vst3ComponentHandler.RestartLatencyChanged)));
        Assert.Equal(Vst3ComponentHandler.RestartLatencyChanged, flags);
    }

    /// <summary>
    /// A handler that throws must not let the exception cross back into the plugin: it would unwind
    /// through a C++ frame that has no idea what a managed exception is.
    /// </summary>
    [Fact]
    public unsafe void AFailingHandlerAnswersThePluginRatherThanThrowingIntoIt()
    {
        using var handler = new Vst3ComponentHandler();
        handler.ParameterEdited += _ => throw new InvalidOperationException("boom");

        void** self = (void**)handler.Pointer;
        var performEdit = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)(*(void***)self)[4];

        int result = performEdit(self, 1, 0.5);
        Assert.Equal(Vst3Abi.ResultFalse, result);
    }

    [Fact]
    public unsafe void TheHostObjectsAnswerOnlyTheInterfacesTheyImplement()
    {
        using var handler = new Vst3ComponentHandler();
        void** self = (void**)handler.Pointer;
        var queryInterface = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)(*(void***)self)[0];

        void* result;
        Guid mine = Vst3ComponentHandler.IComponentHandlerIid;
        Assert.True(Vst3Abi.Ok(queryInterface(self, &mine, &result)));
        Assert.True(result == self);

        Guid other = Vst3Abi.IAudioProcessor;
        Assert.Equal(Vst3Abi.NoInterface, queryInterface(self, &other, &result));
        Assert.True(result == null);
    }

    // ── the catalogue ────────────────────────────────────────────

    [Fact]
    public void DiscoveryLooksWhereWindowsKeepsPlugins()
    {
        Assert.NotEmpty(Vst3Catalogue.DefaultFolders);
        Assert.Contains(Vst3Catalogue.DefaultFolders, f => f.EndsWith("VST3", StringComparison.Ordinal));

        // Never throws, whatever is or is not there.
        Assert.NotNull(Vst3Catalogue.Discover([Path.Combine(_directory, "does-not-exist")]));
    }

    [Fact]
    public void DiscoveryFindsBothBundleFoldersAndBareBinaries()
    {
        string bundle = Path.Combine(_directory, "Bundled.vst3", "Contents", "x86_64-win");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "Bundled.vst3"), "not really a plugin");
        File.WriteAllText(Path.Combine(_directory, "Flat.vst3"), "not really a plugin either");

        List<string> found = Vst3Catalogue.Discover([_directory]);
        output.WriteLine(string.Join("\n", found));

        Assert.Contains(found, p => p.EndsWith("Flat.vst3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, p => p.EndsWith("Bundled.vst3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SomethingThatIsNotAPluginIsReportedRatherThanThrowing()
    {
        string path = Path.Combine(_directory, "NotAPlugin.vst3");
        File.WriteAllText(path, "just text");

        Vst3ScanResult result = Vst3Catalogue.ScanInProcess(path);
        output.WriteLine($"{result.Outcome}: {result.Message}");

        Assert.Equal(Vst3ScanOutcome.Failed, result.Outcome);
        Assert.False(result.IsUsable);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void TheCatalogueRoundTripsThroughItsCacheFile()
    {
        var catalogue = new Vst3Catalogue();
        catalogue.Record(new Vst3ScanResult
        {
            Path = @"C:\plugins\Good.vst3", Outcome = Vst3ScanOutcome.Usable,
            Name = "Good", Vendor = "Someone", Parameters = 12, LatencySamples = 64,
            InputChannels = 2, OutputChannels = 2, BinaryStamp = 1234,
        });
        catalogue.Record(new Vst3ScanResult
        {
            Path = @"C:\plugins\Bad.vst3", Outcome = Vst3ScanOutcome.Crashed,
            Name = "Bad", Message = "faulted while loading", BinaryStamp = 5678,
        });

        string path = Path.Combine(_directory, "vst3.json");
        catalogue.Save(path);

        var reloaded = new Vst3Catalogue();
        reloaded.Load(path);

        Assert.Equal(2, reloaded.Results.Count);
        Assert.Single(reloaded.Usable);
        Assert.Equal("Good", reloaded.Usable.First().Name);

        Vst3ScanResult bad = reloaded.Results.First(r => r.Name == "Bad");
        Assert.Equal(Vst3ScanOutcome.Crashed, bad.Outcome);
        Assert.Equal(5678, bad.BinaryStamp);
    }

    [Fact]
    public void ADamagedCacheIsDiscardedRatherThanThrowing()
    {
        string path = Path.Combine(_directory, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        var catalogue = new Vst3Catalogue();
        catalogue.Load(path);
        Assert.Empty(catalogue.Results);
    }

    [Fact]
    public void ForgettingAPluginMakesItScannableAgain()
    {
        var catalogue = new Vst3Catalogue();
        catalogue.Record(new Vst3ScanResult { Path = @"C:\p\X.vst3", Name = "X" });

        Assert.True(catalogue.Forget(@"C:\p\X.vst3"));
        Assert.False(catalogue.Forget(@"C:\p\X.vst3"));
        Assert.Empty(catalogue.Results);
    }

    [Fact]
    public async Task RefreshPrunesCachedPluginsThatDiscoveryNoLongerFinds()
    {
        string removed = Path.Combine(_directory, "Removed.vst3");
        var catalogue = new Vst3Catalogue();
        catalogue.Record(new Vst3ScanResult
        {
            Path = removed,
            Name = "Removed",
            Outcome = Vst3ScanOutcome.Usable,
        });

        int scanned = await catalogue.RefreshAsync([_directory]);

        Assert.Equal(0, scanned);
        Assert.Empty(catalogue.Results);
    }

    /// <summary>
    /// The scan runs as this same executable with an argument, so the code the scanner exercises is
    /// the code the host will use rather than a parallel copy of it.
    /// </summary>
    [Fact]
    public void TheScannerIsThisExecutableWithAnArgument()
    {
        Assert.Equal("--vst3-scan", Vst3Catalogue.ScanArgument);
        Assert.True(Vst3Catalogue.ScanTimeout > TimeSpan.FromSeconds(5),
            "a plugin authorising over the network on first load is slow, not broken");
    }

    [Fact]
    public void TheScannerWritesAReportForSomethingThatIsNotAPlugin()
    {
        string path = Path.Combine(_directory, "Nonsense.vst3");
        File.WriteAllText(path, "nope");

        var writer = new StringWriter();
        int code = Vst3Catalogue.RunScanner(path, writer);
        string report = writer.ToString();
        output.WriteLine(report);

        // Zero even for a plugin it could not read: a non-zero exit is reserved for the scanner
        // having faulted, which is how the catalogue tells a bad plugin from a bad scan.
        Assert.Equal(0, code);
        Assert.Contains("Failed", report, StringComparison.Ordinal);
        Assert.Contains("Nonsense", report, StringComparison.Ordinal);
    }

    // ── against whatever is actually installed ───────────────────

    /// <summary>
    /// The interop against real plugins. Skipped where none are installed, because a machine without
    /// plugins should not fail the suite — but where they are, this is the test that matters.
    /// </summary>
    [Fact]
    public void EveryInstalledPluginLoadsAndDescribesItself()
    {
        List<string> paths = Installed;
        if (paths.Count == 0)
        {
            output.WriteLine("No VST3 plugins installed; nothing to check against.");
            return;
        }

        int loaded = 0;
        foreach (string path in paths)
        {
            Vst3Module? module = Vst3Module.Load(path, out string error);
            if (module?.Info is not { } info)
            {
                output.WriteLine($"  could not load {Path.GetFileName(path)}: {error}");
                continue;
            }

            loaded++;
            output.WriteLine($"  {info.Name}: {info.Classes.Count} class(es), " +
                             $"generation {info.FactoryGeneration}, vendor '{info.Vendor}'");

            // A factory that reports classes must describe them: an empty name means the descriptor
            // was read at the wrong offset, which is the failure this is really watching for.
            foreach (Vst3ClassInfo cls in info.Classes)
            {
                Assert.NotEmpty(cls.Name);
                Assert.Equal(16, cls.ClassId.Length);
                Assert.NotEqual(new byte[16], cls.ClassId);
            }
            module.Dispose();
        }

        output.WriteLine($"{loaded} of {paths.Count} loaded");
        Assert.True(loaded > 0, "plugins are installed but none of them loaded");
    }

    /// <summary>
    /// The claim that matters: audio goes in, audio comes out, and it is finite. A plugin that
    /// returns a NaN poisons everything downstream of it in the chain.
    /// </summary>
    [Fact]
    public void EveryInstalledEffectProcessesABlockWithoutPoisoningIt()
    {
        List<string> paths = Installed;
        if (paths.Count == 0)
        {
            output.WriteLine("No VST3 plugins installed; nothing to check against.");
            return;
        }

        int processed = 0, rejected = 0;
        foreach (string path in paths)
        {
            Vst3ScanResult result = Vst3Catalogue.ScanInProcess(path);
            if (result.Outcome == Vst3ScanOutcome.Usable) processed++;
            else rejected++;

            output.WriteLine($"  {result.Outcome,-8} {result.Name,-34} " +
                             $"{result.Parameters,3} params  {result.InputChannels}/{result.OutputChannels}ch  " +
                             $"lat {result.LatencySamples,5}  {result.Message}");
        }

        output.WriteLine($"{processed} usable, {rejected} not");
        Assert.True(processed > 0, "plugins are installed but none of them processed a block");
    }
}
