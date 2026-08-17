using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveLab.Audio.Vst3;

/// <summary>How a plugin came out of a scan.</summary>
public enum Vst3ScanOutcome
{
    /// <summary>Loaded, instantiated and processed a block without complaint.</summary>
    Usable,

    /// <summary>It loaded but will not do something this host needs.</summary>
    Rejected,

    /// <summary>It would not load at all.</summary>
    Failed,

    /// <summary>The scanner did not come back — it faulted or hung inside the plugin.</summary>
    Crashed,
}

/// <summary>What a scan learned about one plugin.</summary>
public sealed record Vst3ScanResult
{
    public string Path { get; init; } = "";
    public Vst3ScanOutcome Outcome { get; init; } = Vst3ScanOutcome.Failed;
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Category { get; init; } = "";
    public string Version { get; init; } = "";
    public string SdkVersion { get; init; } = "";
    public string ClassId { get; init; } = "";
    public int Parameters { get; init; }
    public int LatencySamples { get; init; }
    public int InputChannels { get; init; }
    public int OutputChannels { get; init; }
    public string Message { get; init; } = "";

    /// <summary>When the binary was last written, so a reinstall re-scans and nothing else does.</summary>
    public long BinaryStamp { get; init; }

    public bool IsUsable => Outcome == Vst3ScanOutcome.Usable;
}

/// <summary>
/// Finds the VST3 plugins installed on this machine, and remembers what happened to each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every plugin is scanned in a separate process.</b> Loading one runs a stranger's code, and a
/// plugin that faults during initialisation — which is exactly when a broken or half-installed one
/// does — would otherwise take the app and every unsaved document with it. The scanner is this same
/// executable re-run with <c>--vst3-scan</c>, so there is one binary and the scan exercises the code
/// the host will actually use rather than a parallel copy of it.
/// </para>
/// <para>
/// The result is cached against the binary's timestamp, because a scan costs a process launch per
/// plugin and the answer only changes when the plugin does. A plugin that crashed the scanner is
/// remembered as crashed and <b>not tried again</b> until it is reinstalled: retrying it on every
/// start would make one bad plugin a permanent tax on launching the app.
/// </para>
/// </remarks>
public sealed class Vst3Catalogue
{
    /// <summary>The argument that turns this executable into a scanner for one plugin.</summary>
    public const string ScanArgument = "--vst3-scan";

    /// <summary>
    /// How long a plugin gets to be scanned. Generous, because a plugin that authorises over the
    /// network on first load is slow rather than broken — but bounded, because one that waits for a
    /// dialog nobody can see would otherwise hang the scan for ever.
    /// </summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, Vst3ScanResult> _results =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Vst3ScanResult> Results => _results.Values;
    public IEnumerable<Vst3ScanResult> Usable => _results.Values.Where(r => r.IsUsable);

    /// <summary>Where Windows keeps VST3 plugins, in the order the SDK says to look.</summary>
    public static IReadOnlyList<string> DefaultFolders { get; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Common", "VST3"),
    ];

    /// <summary>Every <c>.vst3</c> under the given folders, bundles and bare binaries alike.</summary>
    public static List<string> Discover(IEnumerable<string>? folders = null)
    {
        var found = new List<string>();
        foreach (string folder in folders ?? DefaultFolders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            try
            {
                // Both files and directories: a .vst3 is a bundle folder on some installs and a bare
                // DLL on others, and both are in use on the same machine.
                found.AddRange(Directory.EnumerateFileSystemEntries(
                    folder, "*.vst3", SearchOption.AllDirectories));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    // ── the scan ─────────────────────────────────────────────────

    /// <summary>
    /// Scans every plugin that has not been scanned since it was last written.
    /// </summary>
    public async Task<int> RefreshAsync(IEnumerable<string>? folders = null,
        IProgress<(int Done, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<string> paths = Discover(folders);
        int scanned = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = paths[i];
            progress?.Report((i, paths.Count, Path.GetFileNameWithoutExtension(path)));

            long stamp = BinaryStamp(path);
            if (_results.TryGetValue(path, out Vst3ScanResult? cached) && cached.BinaryStamp == stamp)
                continue;

            _results[path] = await ScanOneAsync(path, stamp, cancellationToken);
            scanned++;
        }

        progress?.Report((paths.Count, paths.Count, ""));
        return scanned;
    }

    private static long BinaryStamp(string path)
    {
        try
        {
            return File.Exists(path)
                ? new FileInfo(path).LastWriteTimeUtc.Ticks
                : Directory.Exists(path)
                    ? new DirectoryInfo(path).LastWriteTimeUtc.Ticks
                    : 0;
        }
        catch { return 0; }
    }

    /// <summary>Runs the scanner for one plugin, in its own process.</summary>
    private static async Task<Vst3ScanResult> ScanOneAsync(string path, long stamp,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "WaveLab.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(ScanArgument);
        start.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(start);
            if (process == null)
                return Crashed(path, stamp, "The scanner could not be started.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ScanTimeout);

            string output;
            try
            {
                // Read before waiting: a plugin that prints more than the pipe buffer holds would
                // block on the write while the host blocks on the exit, and neither would move.
                Task<string> reading = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                output = await reading;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Crashed(path, stamp,
                    $"The plugin did not finish loading within {ScanTimeout.TotalSeconds:0} seconds.");
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                // A non-zero exit with no report is a fault inside the plugin. This is the case the
                // separate process exists for: the app is still running to say so.
                return Crashed(path, stamp,
                    $"The plugin faulted while loading (exit code 0x{process.ExitCode:X8}).");
            }

            Vst3ScanResult? parsed = JsonSerializer.Deserialize<Vst3ScanResult>(output, Json);
            return parsed is null
                ? Crashed(path, stamp, "The scanner produced nothing readable.")
                : parsed with { Path = path, BinaryStamp = stamp };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Crashed(path, stamp, ex.Message); }
    }

    private static Vst3ScanResult Crashed(string path, long stamp, string message) => new()
    {
        Path = path,
        Outcome = Vst3ScanOutcome.Crashed,
        Name = Path.GetFileNameWithoutExtension(path),
        Message = message,
        BinaryStamp = stamp,
    };

    /// <summary>
    /// Loads one plugin and reports what it is. <b>Runs in the scanner process</b>, where a fault is
    /// survivable.
    /// </summary>
    public static Vst3ScanResult ScanInProcess(string path)
    {
        var result = new Vst3ScanResult { Path = path, Name = Path.GetFileNameWithoutExtension(path) };

        Vst3Module? module = Vst3Module.Load(path, out string error);
        if (module?.Info is not { } info)
            return result with { Outcome = Vst3ScanOutcome.Failed, Message = error };

        try
        {
            Vst3ClassInfo? effect = info.Effects.FirstOrDefault();
            if (effect == null)
            {
                return result with
                {
                    Outcome = Vst3ScanOutcome.Rejected,
                    Vendor = info.Vendor,
                    Message = info.Classes.Count == 0
                        ? "The factory offers no classes."
                        : "No audio effect class; this is an instrument or a helper.",
                };
            }

            Vst3Plugin? plugin = Vst3Plugin.Create(module, effect, out string instantiationError);
            if (plugin == null)
            {
                return result with
                {
                    Outcome = Vst3ScanOutcome.Rejected,
                    Name = effect.Name,
                    Vendor = info.Vendor,
                    Category = effect.SubCategories,
                    Version = effect.Version,
                    Message = instantiationError,
                };
            }

            try
            {
                // Configured and run once, because a plugin that loads and then refuses the shape of
                // audio this host produces is no use and should be found out now, not mid-session.
                bool configured = plugin.Configure(44_100, 2, 512);
                bool processed = false;
                if (configured)
                {
                    var block = new float[512 * 2];
                    processed = plugin.ProcessInterleaved(block, 0, block.Length);
                    processed &= block.All(float.IsFinite);
                }

                return result with
                {
                    Outcome = configured && processed ? Vst3ScanOutcome.Usable : Vst3ScanOutcome.Rejected,
                    Name = effect.Name,
                    Vendor = string.IsNullOrWhiteSpace(effect.Vendor) ? info.Vendor : effect.Vendor,
                    Category = effect.SubCategories,
                    Version = effect.Version,
                    SdkVersion = effect.SdkVersion,
                    ClassId = effect.ClassIdText,
                    Parameters = plugin.Parameters.Count,
                    LatencySamples = plugin.LatencySamples,
                    InputChannels = plugin.InputChannels,
                    OutputChannels = plugin.OutputChannels,
                    Message = !configured
                        ? "The plugin would not accept 44.1 kHz stereo."
                        : !processed
                            ? "The plugin produced nothing usable from a test block."
                            : plugin.Parameters.Count == 0
                                ? "No host-visible parameters; this plugin is driven by its own editor."
                                : "",
                };
            }
            finally { plugin.Dispose(); }
        }
        catch (Exception ex)
        {
            return result with { Outcome = Vst3ScanOutcome.Rejected, Message = ex.Message };
        }
        finally { module.Dispose(); }
    }

    /// <summary>The scanner's whole job: load one plugin, print what it is, exit.</summary>
    public static int RunScanner(string path, TextWriter output)
    {
        try
        {
            Vst3ScanResult result = ScanInProcess(path);
            output.Write(JsonSerializer.Serialize(result, Json));
            output.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            output.Write(JsonSerializer.Serialize(new Vst3ScanResult
            {
                Path = path,
                Outcome = Vst3ScanOutcome.Failed,
                Name = Path.GetFileNameWithoutExtension(path),
                Message = ex.Message,
            }, Json));
            output.Flush();
            return 0;
        }
    }

    // ── the cache ────────────────────────────────────────────────

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;
        public List<Vst3ScanResult> Plugins { get; set; } = [];
    }

    public void Load(string path)
    {
        _results.Clear();
        try
        {
            if (!File.Exists(path)) return;
            CacheFile? cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), Json);
            foreach (Vst3ScanResult result in cache?.Plugins ?? [])
                if (!string.IsNullOrWhiteSpace(result.Path)) _results[result.Path] = result;
        }
        catch { _results.Clear(); }
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("The catalogue path has no directory.");
        Directory.CreateDirectory(directory);

        var cache = new CacheFile { Plugins = [.. _results.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)] };
        string stage = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(stage, JsonSerializer.Serialize(cache, Json), new UTF8Encoding(false));
            File.Move(stage, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(stage); } catch { }
            throw;
        }
    }

    /// <summary>Forgets one plugin's result, so the next refresh scans it again.</summary>
    public bool Forget(string path) => _results.Remove(path);

    internal void Record(Vst3ScanResult result) => _results[result.Path] = result;
}
