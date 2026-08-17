using System.IO;
using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>One class a plugin binary offers.</summary>
/// <param name="ClassId">The sixteen-byte identifier <c>createInstance</c> takes.</param>
/// <param name="Name">What it calls itself.</param>
/// <param name="Category">
/// <c>Audio Module Class</c> for a processor, <c>Component Controller Class</c> for its controller.
/// </param>
/// <param name="SubCategories">e.g. <c>Fx|Restoration</c>, or empty when the factory is first-generation.</param>
/// <param name="Vendor">Who made it.</param>
/// <param name="Version">Its version, as it states it.</param>
/// <param name="SdkVersion">Which VST3 SDK it was built against.</param>
public sealed record Vst3ClassInfo(
    byte[] ClassId, string Name, string Category, string SubCategories,
    string Vendor, string Version, string SdkVersion)
{
    /// <summary>Whether this is a processor rather than a controller or something else.</summary>
    public bool IsAudioModule =>
        Category.Equals("Audio Module Class", StringComparison.Ordinal);

    /// <summary>Whether it is an effect rather than an instrument — the only kind this host uses.</summary>
    public bool IsEffect =>
        IsAudioModule && !SubCategories.Contains("Instrument", StringComparison.OrdinalIgnoreCase);

    public string ClassIdText => Vst3Abi.ClassIdText(ClassId);
}

/// <summary>What a binary turned out to contain.</summary>
public sealed record Vst3ModuleInfo(
    string Path, string Vendor, string Url, string Email,
    IReadOnlyList<Vst3ClassInfo> Classes, int FactoryGeneration)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public IEnumerable<Vst3ClassInfo> Effects => Classes.Where(c => c.IsEffect);
}

/// <summary>
/// A loaded VST3 binary: its factory, and the classes it offers.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.vst3</c> on Windows is either a bare DLL or a bundle folder with the DLL at
/// <c>Contents\x86_64-win\</c>. Both are handled, because both are installed in practice — the
/// plugins on this machine are the flat kind and the SDK's own examples are the bundle kind.
/// </para>
/// <para>
/// <b>Loading a plugin runs somebody else's code in this process.</b> Nothing here can make that
/// safe, which is why <see cref="Vst3Catalogue"/> does its scanning in a separate process and only
/// loads in-process what has already survived being scanned.
/// </para>
/// </remarks>
public sealed unsafe class Vst3Module : IDisposable
{
    private delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> _queryInterface;
    private delegate* unmanaged[Stdcall]<void*, uint> _release;

    private nint _library;
    private void* _factory;
    private bool _initialised;

    private Vst3Module(nint library, void* factory, string path)
    {
        _library = library;
        _factory = factory;
        Path = path;

        void** vtable = *(void***)factory;
        _queryInterface = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtable[0];
        _release = (delegate* unmanaged[Stdcall]<void*, uint>)vtable[2];
    }

    public string Path { get; }
    public Vst3ModuleInfo? Info { get; private set; }

    internal void* Factory => _factory;

    /// <summary>
    /// Loads a binary and reads its factory. Returns null with a reason rather than throwing, because
    /// a scan meets binaries that are the wrong architecture or not plugins at all.
    /// </summary>
    public static Vst3Module? Load(string path, out string error)
    {
        error = "";
        string? binary = ResolveBinary(path);
        if (binary == null)
        {
            error = "No Windows 64-bit binary inside the bundle.";
            return null;
        }

        // Load with the plugin's own folder on the search path: many plugins ship helper DLLs beside
        // themselves and will not resolve them otherwise.
        nint library = LoadWithDirectory(binary, out int win32);
        if (library == 0)
        {
            // The Win32 code, not a guess. 126 is a missing dependency and 193 is the wrong
            // architecture, and telling a user "wrong architecture" when a plugin is merely missing
            // a DLL sends them looking for a 32-bit build that does not exist.
            error = win32 switch
            {
                126 => "A DLL it depends on could not be found (error 126).",
                193 => "Not a 64-bit binary (error 193).",
                _ => $"The binary could not be loaded (Win32 error {win32}).",
            };
            return null;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "GetPluginFactory", out nint entry))
            {
                error = "No GetPluginFactory export; this is not a VST3 plugin.";
                NativeLibrary.Free(library);
                return null;
            }

            // InitDll is optional and older than the bundle convention, but plugins that export it
            // may not have set themselves up until it has been called.
            if (NativeLibrary.TryGetExport(library, "InitDll", out nint initDll))
                ((delegate* unmanaged[Stdcall]<byte>)initDll)();

            void* factory = ((delegate* unmanaged[Stdcall]<void*>)entry)();
            if (factory == null)
            {
                error = "The plugin returned no factory.";
                NativeLibrary.Free(library);
                return null;
            }

            var module = new Vst3Module(library, factory, path) { _initialised = true };
            module.Info = module.ReadInfo();
            return module;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { NativeLibrary.Free(library); } catch { }
            return null;
        }
    }

    /// <summary>
    /// The DLL inside a <c>.vst3</c>, whether it is a bundle folder or a bare file.
    /// </summary>
    private static string? ResolveBinary(string path)
    {
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return null;

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string[] candidates =
        [
            System.IO.Path.Combine(path, "Contents", "x86_64-win", name + ".vst3"),
            System.IO.Path.Combine(path, "Contents", "x86_64-win", name + ".dll"),
        ];
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        // Some bundles name the inner binary differently from the folder.
        string architecture = System.IO.Path.Combine(path, "Contents", "x86_64-win");
        if (!Directory.Exists(architecture)) return null;
        return Directory.EnumerateFiles(architecture, "*.vst3").FirstOrDefault()
               ?? Directory.EnumerateFiles(architecture, "*.dll").FirstOrDefault();
    }

    /// <summary>
    /// Loads a plugin binary with its own folder searched for dependencies, reporting the Win32
    /// error when it will not load.
    /// </summary>
    private static nint LoadWithDirectory(string binary, out int win32Error)
    {
        win32Error = 0;
        string? directory = System.IO.Path.GetDirectoryName(binary);

        // LOAD_WITH_ALTERED_SEARCH_PATH puts the binary's own folder first, which is where plugins
        // that ship helper DLLs keep them. NativeLibrary.TryLoad does not offer the flag, so this is
        // LoadLibraryEx directly.
        const uint loadWithAlteredSearchPath = 0x00000008;
        nint handle = LoadLibraryEx(binary, 0, loadWithAlteredSearchPath);
        if (handle != 0) return handle;
        win32Error = Marshal.GetLastWin32Error();

        // Second chance with the folder explicitly added, which covers plugins whose dependencies
        // sit beside them but which the altered search path did not reach.
        nint cookie = 0;
        try
        {
            if (directory != null) cookie = AddDllDirectory(directory);
            handle = LoadLibraryEx(binary, 0, 0x00001000 | 0x00000100);   // USER_DIRS | APPLICATION_DIR
            if (handle == 0) win32Error = Marshal.GetLastWin32Error();
            return handle;
        }
        catch { return 0; }
        finally { if (cookie != 0) RemoveDllDirectory(cookie); }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint reserved, uint flags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string path);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDllDirectory(nint cookie);

    // ── the factory ──────────────────────────────────────────────

    private Vst3ModuleInfo ReadInfo()
    {
        void** vtable = *(void***)_factory;
        var getFactoryInfo = (delegate* unmanaged[Stdcall]<void*, Vst3Abi.PFactoryInfo*, int>)vtable[3];
        var countClasses = (delegate* unmanaged[Stdcall]<void*, int>)vtable[4];
        var getClassInfo = (delegate* unmanaged[Stdcall]<void*, int, Vst3Abi.PClassInfo*, int>)vtable[5];

        string vendor = "", url = "", email = "";
        Vst3Abi.PFactoryInfo info;
        if (Vst3Abi.Ok(getFactoryInfo(_factory, &info)))
        {
            vendor = Vst3Abi.Ascii(info.Vendor, 64);
            url = Vst3Abi.Ascii(info.Url, 256);
            email = Vst3Abi.Ascii(info.Email, 128);
        }

        // Which generation of factory this is decides how much a class will say about itself. Asking
        // for the newer descriptor from an older factory is not an error to recover from — it simply
        // is not there — so the generation is established once and the right call used thereafter.
        int generation = 1;
        void* factory2 = QueryFactory(Vst3Abi.IPluginFactory2);
        void* factory3 = QueryFactory(Vst3Abi.IPluginFactory3);
        if (factory3 != null) generation = 3;
        else if (factory2 != null) generation = 2;

        var classes = new List<Vst3ClassInfo>();
        int count = countClasses(_factory);
        for (int i = 0; i < Math.Clamp(count, 0, 512); i++)
        {
            Vst3ClassInfo? described = generation >= 2 && factory2 != null
                ? ReadClassInfo2(factory2, i)
                : null;

            if (described == null)
            {
                Vst3Abi.PClassInfo basic;
                if (!Vst3Abi.Ok(getClassInfo(_factory, i, &basic))) continue;
                described = new Vst3ClassInfo(
                    Vst3Abi.ClassIdBytes(basic.ClassId),
                    Vst3Abi.Ascii(basic.Name, 64),
                    Vst3Abi.Ascii(basic.Category, 32),
                    "", vendor, "", "");
            }
            classes.Add(described);
        }

        if (factory2 != null) Release(factory2);
        if (factory3 != null) Release(factory3);
        return new Vst3ModuleInfo(Path, vendor, url, email, classes, generation);
    }

    private Vst3ClassInfo? ReadClassInfo2(void* factory2, int index)
    {
        // Slot 7. IPluginFactory2 extends IPluginFactory, so its own first method sits after the
        // three FUnknown slots and the four it inherits — slot 6 is createInstance, and calling that
        // with an index where a pointer belongs dereferences the index. It is an access violation
        // the first time it runs, which is how this was found.
        void** vtable = *(void***)factory2;
        var getClassInfo2 = (delegate* unmanaged[Stdcall]<void*, int, Vst3Abi.PClassInfo2*, int>)vtable[7];

        Vst3Abi.PClassInfo2 info;
        if (!Vst3Abi.Ok(getClassInfo2(factory2, index, &info))) return null;

        return new Vst3ClassInfo(
            Vst3Abi.ClassIdBytes(info.ClassId),
            Vst3Abi.Ascii(info.Name, 64),
            Vst3Abi.Ascii(info.Category, 32),
            Vst3Abi.Ascii(info.SubCategories, 128),
            Vst3Abi.Ascii(info.Vendor, 64),
            Vst3Abi.Ascii(info.Version, 64),
            Vst3Abi.Ascii(info.SdkVersion, 64));
    }

    private void* QueryFactory(Guid iid)
    {
        void* result;
        return Vst3Abi.Ok(_queryInterface(_factory, &iid, &result)) ? result : null;
    }

    /// <summary>
    /// Creates an instance of a class, asking for one of its interfaces.
    /// </summary>
    /// <remarks>
    /// <c>createInstance</c> takes the class identifier and the interface identifier as raw sixteen
    /// byte runs, not as anything the marshaller knows about, so both are pinned and passed as
    /// addresses.
    /// </remarks>
    internal void* CreateInstance(byte[] classId, Guid iid)
    {
        if (_factory == null || classId.Length != 16) return null;

        void** vtable = *(void***)_factory;
        var createInstance = (delegate* unmanaged[Stdcall]<void*, byte*, byte*, void**, int>)vtable[6];

        void* instance = null;
        fixed (byte* cid = classId)
        {
            Span<byte> iidBytes = stackalloc byte[16];
            iid.TryWriteBytes(iidBytes);
            fixed (byte* iidPointer = iidBytes)
            {
                if (!Vst3Abi.Ok(createInstance(_factory, cid, iidPointer, &instance))) return null;
            }
        }
        return instance;
    }

    internal static uint Release(void* unknown)
    {
        if (unknown == null) return 0;
        void** vtable = *(void***)unknown;
        var release = (delegate* unmanaged[Stdcall]<void*, uint>)vtable[2];
        return release(unknown);
    }

    internal static void* QueryInterface(void* unknown, Guid iid)
    {
        if (unknown == null) return null;
        void** vtable = *(void***)unknown;
        var queryInterface = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtable[0];

        void* result;
        return Vst3Abi.Ok(queryInterface(unknown, &iid, &result)) ? result : null;
    }

    public void Dispose()
    {
        if (!_initialised) return;
        _initialised = false;

        if (_factory != null)
        {
            _release(_factory);
            _factory = null;
        }

        if (_library != 0)
        {
            // ExitDll mirrors InitDll, and a plugin that has one usually frees global state in it.
            if (NativeLibrary.TryGetExport(_library, "ExitDll", out nint exitDll))
            {
                try { ((delegate* unmanaged[Stdcall]<byte>)exitDll)(); } catch { }
            }

            // The library is deliberately *not* freed. A plugin that has spun up a thread, or that
            // registered something with the CRT, can fault after its code has been unmapped, and a
            // leaked module is a far smaller problem than a crash on the way out.
            _library = 0;
        }
        GC.SuppressFinalize(this);
    }
}
