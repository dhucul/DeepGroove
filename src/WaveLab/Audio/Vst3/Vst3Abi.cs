using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>
/// The VST3 binary interface: result codes, identifiers and the structures passed across it.
/// </summary>
/// <remarks>
/// <para>
/// VST3 is a C++ vtable ABI in the shape of COM — an <c>FUnknown</c> with the same three slots and
/// the same signatures as <c>IUnknown</c>. It is dispatched here through function pointers rather
/// than CLR COM interop for two reasons. The layouts end up written down in one place where they can
/// be read and checked, which matters because <b>a struct laid out wrongly is not a wrong answer, it
/// is memory corruption</b>. And nothing sits between this app and a plugin deciding when to release
/// it: an RCW finalised on a background thread would call <c>release</c> from somewhere a plugin has
/// no reason to expect.
/// </para>
/// <para>
/// Windows only, and that simplifies one thing worth knowing about: a VST3 <c>TUID</c> is sixteen
/// raw bytes, and on Windows the SDK lays them out to match the COM <c>GUID</c> struct — first field
/// little-endian — while on other platforms it uses a flat big-endian order. The identifiers below
/// are in the Windows form, which is the only one this build can meet.
/// </para>
/// </remarks>
internal static class Vst3Abi
{
    // ── result codes ─────────────────────────────────────────────

    /// <summary>
    /// VST3's success code. Note that <c>kResultTrue</c> is the same value and <c>kResultFalse</c>
    /// is 1 — a plugin answering "no" returns 1, not an error, so a bare non-zero test reads a
    /// legitimate "no" as a failure.
    /// </summary>
    public const int ResultOk = 0;

    public const int ResultTrue = 0;
    public const int ResultFalse = 1;
    public const int NoInterface = unchecked((int)0x80004002);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int InvalidArgument = unchecked((int)0x80070057);

    public static bool Ok(int code) => code == ResultOk;

    // ── identifiers ──────────────────────────────────────────────
    //
    // Taken from the VST3 SDK headers in their Windows (COM-compatible) form. Every one of these is
    // checked against the plugins actually installed rather than trusted: an identifier that is
    // wrong simply fails to resolve, and Vst3Module reports which ones did.

    public static readonly Guid IPluginFactory = new("7a4d811c-5211-4a1f-aed9-d2ee0b43bf9f");
    public static readonly Guid IPluginFactory2 = new("0007b650-f24b-4c0b-a464-edb9f00b2abb");
    public static readonly Guid IPluginFactory3 = new("4555a2ab-c123-4e57-9b12-291036878931");
    public static readonly Guid IPluginBase = new("22888ddb-156e-45ae-8358-b34808190625");
    public static readonly Guid IComponent = new("e831ff31-f2d5-4301-928e-bbee25697802");
    public static readonly Guid IAudioProcessor = new("42043f99-b7da-453c-a569-e79d9aaec33d");
    public static readonly Guid IEditController = new("dcd7bbe3-7742-448d-a874-aacc979c759e");
    public static readonly Guid IConnectionPoint = new("70a4156f-6e6e-4026-9891-48bfaa60d8d1");
    public static readonly Guid IUnitInfo = new("3d4bd6b5-9137-4a05-b6e5-b0d2b5e0d0c9");

    // ── enumerations ─────────────────────────────────────────────

    public const int MediaTypeAudio = 0;
    public const int MediaTypeEvent = 1;

    public const int BusDirectionInput = 0;
    public const int BusDirectionOutput = 1;

    public const int BusTypeMain = 0;
    public const int BusTypeAux = 1;

    public const int SampleSize32 = 0;
    public const int SampleSize64 = 1;

    public const int ProcessModeRealtime = 0;
    public const int ProcessModePrefetch = 1;
    public const int ProcessModeOffline = 2;

    /// <summary>Speaker arrangement bits: the two this host asks for.</summary>
    public const ulong SpeakerMono = 0x1;

    /// <summary>Left and right.</summary>
    public const ulong SpeakerStereo = 0x1 | 0x2;

    // ── structures ───────────────────────────────────────────────

    /// <summary>What a factory says about its vendor. Fixed-width ASCII, as the ABI declares it.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct PFactoryInfo
    {
        public fixed byte Vendor[64];
        public fixed byte Url[256];
        public fixed byte Email[128];
        public int Flags;
    }

    /// <summary>The first-generation class descriptor, which every factory can produce.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct PClassInfo
    {
        public fixed byte ClassId[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
    }

    /// <summary>
    /// The second-generation descriptor, which adds the subcategories that say what a plugin is for.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct PClassInfo2
    {
        public fixed byte ClassId[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
        public uint ClassFlags;
        public fixed byte SubCategories[128];
        public fixed byte Vendor[64];
        public fixed byte Version[64];
        public fixed byte SdkVersion[64];
    }

    /// <summary>One audio or event bus a component declares.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct BusInfo
    {
        public int MediaType;
        public int Direction;
        public int ChannelCount;
        public fixed char Name[128];
        public int BusType;
        public uint Flags;
    }

    /// <summary>How the host intends to call <c>process</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessSetup
    {
        public int ProcessMode;
        public int SymbolicSampleSize;
        public int MaxSamplesPerBlock;
        public double SampleRate;
    }

    /// <summary>
    /// One bus's buffers for one <c>process</c> call.
    /// </summary>
    /// <remarks>
    /// The padding between <see cref="ChannelCount"/> and <see cref="SilenceFlags"/> is not
    /// incidental: the C++ struct has a 32-bit field followed by a 64-bit one, so the compiler
    /// aligns the second to eight. Packing this tightly would shift the channel-buffer pointer by
    /// four bytes and hand the plugin an address that is not one.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AudioBusBuffers
    {
        public int ChannelCount;
        private readonly int _padding;
        public ulong SilenceFlags;
        public float** ChannelBuffers;
    }

    /// <summary>Everything one <c>process</c> call is given.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ProcessData
    {
        public int ProcessMode;
        public int SymbolicSampleSize;
        public int NumSamples;
        public int NumInputs;
        public int NumOutputs;
        private readonly int _padding;
        public AudioBusBuffers* Inputs;
        public AudioBusBuffers* Outputs;
        public nint InputParameterChanges;
        public nint OutputParameterChanges;
        public nint InputEvents;
        public nint OutputEvents;
        public nint ProcessContext;
    }

    /// <summary>What a controller says about one parameter.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ParameterInfo
    {
        public uint Id;
        public fixed char Title[128];
        public fixed char ShortTitle[128];
        public fixed char Units[128];
        public int StepCount;
        public double DefaultNormalizedValue;
        public int UnitId;
        public int Flags;
    }

    /// <summary>Parameter flags worth acting on.</summary>
    public const int ParamCanAutomate = 1 << 0;
    public const int ParamIsReadOnly = 1 << 1;
    public const int ParamIsList = 1 << 2;
    public const int ParamIsHidden = 1 << 4;
    public const int ParamIsProgramChange = 1 << 15;
    public const int ParamIsBypass = 1 << 16;

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>Reads a fixed-width ASCII field, stopping at the first NUL.</summary>
    public static unsafe string Ascii(byte* start, int capacity)
    {
        int length = 0;
        while (length < capacity && start[length] != 0) length++;
        return length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(start, length).Trim();
    }

    /// <summary>Reads a fixed-width UTF-16 field, stopping at the first NUL.</summary>
    public static unsafe string Utf16(char* start, int capacity)
    {
        int length = 0;
        while (length < capacity && start[length] != '\0') length++;
        return length == 0 ? string.Empty : new string(start, 0, length).Trim();
    }

    /// <summary>The sixteen bytes of a class identifier, as the ABI passes them.</summary>
    public static unsafe byte[] ClassIdBytes(byte* start)
    {
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = start[i];
        return bytes;
    }

    /// <summary>A class identifier as the hexadecimal string the SDK's own tools print.</summary>
    public static string ClassIdText(byte[] classId) =>
        classId.Length == 16 ? Convert.ToHexString(classId) : string.Empty;
}
