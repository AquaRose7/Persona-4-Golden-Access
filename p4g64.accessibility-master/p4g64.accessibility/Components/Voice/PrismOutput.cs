using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Voice;

/// <summary>
/// Speech through Prism (v2.2.1, replaced Tolk): the first backend in Prism's priority order that starts, without SAPI or
/// OneCore when the "Windows voice" setting is off. Picks again when Prism reports an availability change (only the backends
/// above the one in use are tried, so a change doesn't reconnect to NVDA) and when a line fails (the one in use dropped; the
/// line is said again once). Prism's Tolk shim fell back to SAPI when NVDA closed and never went back — hence the direct
/// calls. Backends aren't thread-safe and <see cref="Speech.Say"/> runs on many threads: one lock.
/// </summary>
internal static class PrismOutput
{
    /// <summary>Prism's IDs of the Windows voices (include/prism.h: PRISM_BACKEND_SAPI, PRISM_BACKEND_ONE_CORE).</summary>
    private const ulong Sapi = 0x1D6DF72422CEEE66, OneCore = 0x6797D32F0D994CB4;

    private static PrismNative? _api;
    private static bool _windowsVoice = true;
    private static readonly object _lock = new();
    private static nint _backend;
    private static ulong _id;
    private static bool _noneLogged;

    /// <summary>The backend in use by name, or null when none started.</summary>
    internal static string? Using { get; private set; }

    /// <summary>Loads prism.dll from the mod folder and picks a backend. A failure is logged and the mod keeps running silent.</summary>
    internal static void Open(string modDir, bool windowsVoice)
    {
        _windowsVoice = windowsVoice;
        _api = PrismNative.TryOpen(modDir);
        if (_api is null)
        {
            LogError("[Speech] Prism couldn't be loaded (prism.dll): nothing will be spoken. Your mod files may be incomplete.");
            return;
        }
        Log($"[Speech] Prism's backends in order: {string.Join(", ", _api.Backends().Select(_api.NameOf))}");
        lock (_lock) Pick();
    }

    /// <summary>The F1 setting "Windows voice when no screen reader runs": applied at once (a full re-pick).</summary>
    internal static void SetWindowsVoice(bool on)
    {
        if (_api is null) return;
        lock (_lock)
        {
            if (_windowsVoice == on) return;
            _windowsVoice = on;
            Drop();
            _noneLogged = false;
            Pick();
        }
    }

    internal static void Output(string text, bool interrupt)
    {
        if (_api is null) return;
        lock (_lock)
        {
            if (_api.TakeChanged()) Pick();
            if (_backend == 0) return;
            int error = _api.Output(_backend, text, interrupt);
            if (error == 0) return;
            Log($"[Speech] {Using} failed ({_api.ErrorName(error)}): choosing again");
            Drop();
            Pick();
            if (_backend != 0) _api.Output(_backend, text, interrupt);
        }
    }

    /// <summary>The first backend that starts, from the top of the order down to the one in use (kept when none above it starts).</summary>
    private static void Pick()
    {
        foreach (ulong id in _api!.Backends())
        {
            if (_backend != 0 && id == _id) return;
            if (!_windowsVoice && id is Sapi or OneCore) continue;
            nint b = _api.Start(id);
            if (b == 0) continue;
            Drop();
            (_backend, _id, Using, _noneLogged) = (b, id, _api.Name(b), false);
            Log($"[Speech] using {Using}");
            return;
        }
        if (_backend == 0 && !_noneLogged)
        {
            _noneLogged = true;
            Log(_windowsVoice ? "[Speech] no screen reader or Windows voice available"
                              : "[Speech] no screen reader available (the Windows voice setting is off)");
        }
    }

    private static void Drop()
    {
        if (_backend != 0) _api!.Free(_backend);
        (_backend, _id, Using) = (0, 0, null);
    }
}
