using System.IO;
using WaveLab.Audio.Effects;
using WaveLab.Util;

namespace WaveLab.Audio.Vst3;

/// <summary>
/// One loaded plugin instance, shared by everything that refers to it and disposed when nothing does.
/// </summary>
/// <remarks>
/// <para>
/// The rack copies effects for reasons that have nothing to do with plugins: A/B comparison snapshots
/// the chain, and a snapshot is a list of clones. For a built-in effect a clone is a handful of
/// doubles. For a plugin it would be a second copy of somebody else's DSP — a second load of the
/// binary, a second allocation of its buffers, twice the CPU, and on some plugins a second licence
/// check. Sharing the instance and counting the references gives A/B the semantics it actually wants:
/// <b>a snapshot of the plugin's settings, not a duplicate of the plugin</b>, which is how a
/// comparison works in every host that has one.
/// </para>
/// <para>
/// Counting matters because the last reference is not predictable. A plugin can be dropped from the
/// chain while a snapshot still holds it, or dropped from both at once when a preset replaces
/// everything, and disposing on the wrong one leaves either a leak or a dangling instance the audio
/// thread will walk into.
/// </para>
/// </remarks>
internal sealed class Vst3PluginRef
{
    private int _count = 1;

    public Vst3PluginRef(Vst3Plugin plugin, string path, Vst3ScanResult? scan)
    {
        Plugin = plugin;
        Path = path;
        Scan = scan;
    }

    public Vst3Plugin Plugin { get; }
    public string Path { get; }
    public Vst3ScanResult? Scan { get; }

    public void AddRef() => Interlocked.Increment(ref _count);

    public void Release()
    {
        if (Interlocked.Decrement(ref _count) == 0) Plugin.Dispose();
    }
}

/// <summary>
/// The application's VST3 registry: what is installed, what survived a scan, and how to open one.
/// </summary>
/// <remarks>
/// Modules are cached by path and never unloaded. That is not laziness — <see cref="Vst3Module"/>
/// deliberately declines to free a plugin's library, because a plugin that has started a thread or
/// registered with the C runtime can fault after its code has been unmapped. Since the library stays
/// anyway, keeping the factory alongside it means a second instance of the same plugin costs a
/// <c>createInstance</c> rather than a reload.
/// </remarks>
public sealed class Vst3PluginHost
{
    private static readonly Lazy<Vst3PluginHost> Singleton = new(() => new Vst3PluginHost());

    private readonly Dictionary<string, Vst3Module> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _catalogueLoaded;

    private Vst3PluginHost() { }

    public static Vst3PluginHost Instance => Singleton.Value;

    /// <summary>What the last scan found. Loaded from disk on first use.</summary>
    public Vst3Catalogue Catalogue { get; } = new();

    /// <summary>Where the scan results live, beside the settings and the presets.</summary>
    public static string CataloguePath => Path.Combine(AppSettings.AppDataDir, "vst3-plugins.json");

    /// <summary>Folders the scan looks in — the Windows defaults plus whatever the user added.</summary>
    public static IReadOnlyList<string> ScanFolders
    {
        get
        {
            var folders = new List<string>(Vst3Catalogue.DefaultFolders);
            foreach (string extra in AppSettings.Instance.Vst3ExtraFolders ?? [])
                if (!string.IsNullOrWhiteSpace(extra)
                    && !folders.Contains(extra, StringComparer.OrdinalIgnoreCase))
                    folders.Add(extra);
            return folders;
        }
    }

    public void EnsureCatalogueLoaded()
    {
        lock (_gate)
        {
            if (_catalogueLoaded) return;
            _catalogueLoaded = true;
            Catalogue.Load(CataloguePath);
        }
    }

    public void SaveCatalogue()
    {
        try { Catalogue.Save(CataloguePath); }
        catch { /* a catalogue that cannot be written is re-scanned, not fatal */ }
    }

    /// <summary>The plugins fit to appear in the Add Effect menu, in the order they should appear.</summary>
    public IReadOnlyList<Vst3ScanResult> UsablePlugins
    {
        get
        {
            EnsureCatalogueLoaded();
            var blocked = new HashSet<string>(
                AppSettings.Instance.Vst3BlockedPlugins ?? [], StringComparer.OrdinalIgnoreCase);
            return
            [
                .. Catalogue.Usable.Where(r => !blocked.Contains(r.Path))
                    .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            ];
        }
    }

    /// <summary>Whether a plugin is offered in the menu. Only usable ones can be.</summary>
    public static bool IsAllowed(string path) =>
        !(AppSettings.Instance.Vst3BlockedPlugins ?? []).Contains(path, StringComparer.OrdinalIgnoreCase);

    public static void SetAllowed(string path, bool allowed)
    {
        var blocked = AppSettings.Instance.Vst3BlockedPlugins ??= [];
        if (allowed) blocked.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        else if (!blocked.Contains(path, StringComparer.OrdinalIgnoreCase)) blocked.Add(path);
    }

    /// <summary>
    /// Instantiates a plugin as a rack effect. Returns null with a reason rather than throwing.
    /// </summary>
    public Vst3Effect? Open(string path, out string error)
    {
        Vst3PluginRef? shared = OpenShared(path, out error);
        return shared == null ? null : new Vst3Effect(shared);
    }

    internal Vst3PluginRef? OpenShared(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No plugin path.";
            return null;
        }

        EnsureCatalogueLoaded();
        Vst3ScanResult? scan = Catalogue.Results.FirstOrDefault(
            r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

        // A plugin that crashed the scanner is not loaded here, whatever the caller thinks it is
        // asking for. The scan is the only thing standing between this process and a plugin that
        // faults on load, and honouring a stale preset entry would walk straight past it.
        if (scan is { Outcome: Vst3ScanOutcome.Crashed })
        {
            error = $"{scan.Name} crashed the last time it was scanned and is not loaded.";
            return null;
        }

        lock (_gate)
        {
            if (!_modules.TryGetValue(path, out Vst3Module? module))
            {
                module = Vst3Module.Load(path, out error);
                if (module?.Info == null)
                {
                    if (string.IsNullOrWhiteSpace(error)) error = "The plugin would not load.";
                    return null;
                }
                _modules[path] = module;
            }

            Vst3ClassInfo? effect = module.Info!.Effects.FirstOrDefault();
            if (effect == null)
            {
                error = "The plugin offers no audio effect class.";
                return null;
            }

            Vst3Plugin? plugin = Vst3Plugin.Create(module, effect, out error);
            return plugin == null ? null : new Vst3PluginRef(plugin, path, scan);
        }
    }

    /// <summary>Rescans, keeping cached results for binaries that have not changed.</summary>
    public async Task<int> RefreshAsync(bool full,
        IProgress<(int Done, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCatalogueLoaded();
        if (full)
            foreach (Vst3ScanResult result in Catalogue.Results.ToArray())
                Catalogue.Forget(result.Path);

        int scanned = await Catalogue.RefreshAsync(ScanFolders, progress, cancellationToken);
        SaveCatalogue();
        return scanned;
    }
}
