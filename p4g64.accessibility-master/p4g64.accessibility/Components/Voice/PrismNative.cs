using System.Runtime.InteropServices;
using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Voice;

/// <summary>
/// Prism's C API from prism.dll (v0.18.3, github.com/ethindp/prism, include/prism.h; cdecl, UTF-8 strings, C bool as one
/// byte). Replaced Tolk in v2.2.1 (the FE3H Access port, "D:\Downloads\Prism speech for mods - notes.txt"). A context with
/// an availability callback: Prism's poll thread calls it when a backend starts or stops being available, and it only sets a
/// flag (<see cref="TakeChanged"/>); the speaking stays on the caller's thread under <see cref="PrismOutput"/>'s lock.
/// </summary>
internal sealed class PrismNative : IDisposable
{
    private const string Dll = "prism.dll";
    private const int NotImplemented = 3;           // PRISM_ERROR_NOT_IMPLEMENTED: a backend without output; speak instead

    /// <summary>PrismConfig (include/prism.h), blittable: the bool as a byte.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PrismConfig
    {
        public byte Version;
        public nint Registry;
        public nint AvailabilityCallback;
        public nint AvailabilityUserdata;
        public uint AvailabilityPollIntervalMs;
        public uint AvailabilityDebounceSamples;
        public uint AvailabilityBackoffMaxMs;
        public byte AvailabilityAutoPowerManage;
        public nint AvailabilityBaselineCallback;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvailabilityCallback(nint userdata, ulong backend, nint name, byte available);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern PrismConfig prism_config_init();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint prism_init(ref PrismConfig cfg);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void prism_shutdown(nint ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nuint prism_registry_count(nint ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern ulong prism_registry_id_at(nint ctx, nuint index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint prism_registry_name(nint ctx, ulong id);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint prism_registry_create(nint ctx, ulong id);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int prism_backend_initialize(nint backend);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint prism_backend_name(nint backend);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int prism_backend_output(nint backend, byte[] text, byte interrupt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int prism_backend_speak(nint backend, byte[] text, byte interrupt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void prism_backend_free(nint backend);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint prism_error_string(int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint prism_version_string();

    private readonly nint _ctx;
    private readonly AvailabilityCallback _callback;        // kept alive while Prism holds its pointer
    private int _changed;

    private PrismNative()
    {
        _callback = (_, _, _, _) => Interlocked.Exchange(ref _changed, 1);
        var cfg = prism_config_init();
        cfg.AvailabilityCallback = Marshal.GetFunctionPointerForDelegate(_callback);
        _ctx = prism_init(ref cfg);
        if (_ctx == 0) throw new InvalidOperationException("prism_init returned no context");
        Log($"[Speech] Prism {Marshal.PtrToStringUTF8(prism_version_string())}");
    }

    /// <summary>Prism, or null when prism.dll can't be loaded or started (logged). The DLL is loaded from the mod's own
    /// folder by full path first, so the bare-name imports above bind to that copy.</summary>
    internal static PrismNative? TryOpen(string modDir)
    {
        try
        {
            string path = System.IO.Path.Combine(modDir, Dll);
            if (!NativeLibrary.TryLoad(path, out _)) Log($"[Speech] could not load {path}; trying the default search");
            return new PrismNative();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            LogError("[Speech] Prism failed to start", ex);
            return null;
        }
    }

    /// <summary>The registered backends' IDs in descending priority order (screen readers before SAPI and OneCore).</summary>
    public IReadOnlyList<ulong> Backends()
    {
        var list = new List<ulong>();
        for (nuint i = 0, n = prism_registry_count(_ctx); i < n; i++) list.Add(prism_registry_id_at(_ctx, i));
        return list;
    }

    public string NameOf(ulong id) => Marshal.PtrToStringUTF8(prism_registry_name(_ctx, id)) ?? id.ToString("X16");

    /// <summary>A new instance of the backend, initialized; 0 when it can't start (not installed, not running).</summary>
    public nint Start(ulong id)
    {
        nint b = prism_registry_create(_ctx, id);
        if (b == 0) return 0;
        if (prism_backend_initialize(b) == 0) return b;
        prism_backend_free(b);
        return 0;
    }

    public string Name(nint backend) => Marshal.PtrToStringUTF8(prism_backend_name(backend)) ?? "a screen reader";

    /// <summary>Speaks the text (and brailles it where the backend can); a PrismError, 0 when it worked.</summary>
    public int Output(nint backend, string text, bool interrupt)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");
        int error = prism_backend_output(backend, utf8, interrupt ? (byte)1 : (byte)0);
        return error == NotImplemented ? prism_backend_speak(backend, utf8, interrupt ? (byte)1 : (byte)0) : error;
    }

    public void Free(nint backend) => prism_backend_free(backend);

    public string ErrorName(int error) => Marshal.PtrToStringUTF8(prism_error_string(error)) ?? $"error {error}";

    /// <summary>Whether a backend's availability changed since the last call (Prism's poll thread: NVDA started or quit).</summary>
    public bool TakeChanged() => Interlocked.Exchange(ref _changed, 0) == 1;

    public void Dispose() => prism_shutdown(_ctx);
}
