using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using p4g64.accessibility.Configuration;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace p4g64.accessibility;

internal class Utils
{
    // ── RPM-based guarded reads (2026-07-27, THE MENU-HEAVINESS FIX) ─────────────
    // VirtualQuery walks the process VAD tree under a lock the game's allocator
    // holds at its busiest screens — PerfDiag measured ~5ms PER CALL stalls on the
    // game thread (ConfigValueText/ShuffleText captures ate 1.5-2.7s of every 3s
    // window in velvet/battle). ReadProcessMemory validates + copies in one step
    // with no VAD walk. Use THESE on any per-frame/per-draw path, never VirtualQuery.
    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory")]
    private static extern unsafe bool Rpm(nint h, nint addr, void* buf, nint size, out nint read);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern nint SelfProc();

    /// <summary>Copy <paramref name="size"/> bytes from <paramref name="addr"/> into
    /// <paramref name="dst"/> — false (nothing copied) on any unreadable page. No VAD walk.</summary>
    internal static unsafe bool TryReadRaw(nint addr, void* dst, int size)
        => addr >= 0x10000 && (ulong)addr <= 0x00007FFFFFFFFFFFUL
           && Rpm(SelfProc(), addr, dst, size, out nint got) && got == (nint)size;

    /// <summary>Drop-in replacement for the per-component VirtualQuery IsReadable copies
    /// (2026-08-31, THE BACKGROUND-POLLER HEAVINESS FIX): VirtualQuery contends the VAD
    /// lock the game's allocator holds — ~a dozen poll threads × tens of calls/tick burned
    /// ~4.5 cores and stuttered weak machines. This probes ONE byte in every page the range
    /// touches via ReadProcessMemory (fails on NOACCESS/GUARD/uncommitted — the same pages
    /// VirtualQuery rejected), no VAD walk, ~1-2µs. Callers keep their signatures.</summary>
    internal static unsafe bool ProbeReadable(nint addr, int size)
    {
        if (addr == 0 || size <= 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte tmp;
        ulong end = a + (ulong)size - 1;
        nint self = SelfProc();
        for (ulong page = a & ~0xFFFUL; page <= (end & ~0xFFFUL); page += 0x1000)
        {
            nint pr = (nint)(page < a ? a : page);
            if (!Rpm(self, pr, &tmp, 1, out nint got) || got != 1) return false;
        }
        return true;
    }

    /// <summary>Game-text C-string read via RPM (page-boundary aware: reads to the end of
    /// each readable page, stops cleanly at the first unreadable one). The drop-in
    /// replacement for the per-component VirtualQuery ReadCStr copies.
    /// ⚠ 2026-09-07: decodes through the ACTIVE GLYPH TABLE (<see cref="ReadAtlusStringRpm"/>)
    /// instead of keeping printable ASCII only — the old filter silently emptied every
    /// Japanese string and dropped accents in European languages across nine readers
    /// (Check label, quest names, Social Link detail, compendium info, shuffle text, …).
    /// English output is byte-for-byte identical (ASCII passes through the glyph table).</summary>
    internal static unsafe string ReadCStringRpm(nint p, int maxLen)
    {
        if (maxLen > 256) maxLen = 256;
        string s = ReadAtlusStringRpm(p, maxLen);
        if (s.Length == 0) return s;
        // Keep the old contract of never returning control characters.
        foreach (char c in s)
        {
            if (c < 0x20)
            {
                var sb = new StringBuilder(s.Length);
                foreach (char d in s) if (d >= 0x20) sb.Append(d);
                return sb.ToString();
            }
        }
        return s;
    }

    /// <summary>
    /// Atlus-glyph-encoded C-string read via RPM (2026-09-07): a byte with the high bit set is
    /// the lead byte of a 2-byte glyph pair, everything else is a single byte; stops at NUL or
    /// maxLen. Decodes with the active glyph table, so accented / CJK text survives — use this
    /// instead of <see cref="ReadCStringRpm"/> wherever the text is the GAME's own drawn text.
    /// </summary>
    internal static unsafe string ReadAtlusStringRpm(nint p, int maxLen)
    {
        if (p == 0 || maxLen <= 0) return "";
        if (maxLen > 1024) maxLen = 1024;
        byte* buf = stackalloc byte[1024];
        int total = 0, len = -1;
        while (total < maxLen)
        {
            int chunk = Math.Min(maxLen - total, 0x1000 - (int)((ulong)(p + total) & 0xFFF));
            if (!TryReadRaw(p + total, buf + total, chunk)) break;
            total += chunk;
            // find the terminator honoring lead bytes
            int i = 0;
            while (i < total)
            {
                if (buf[i] == 0) { len = i; break; }
                i += (buf[i] & 0x80) != 0 ? 2 : 1;
            }
            if (len >= 0) break;
        }
        if (len < 0)
        {
            // No terminator inside maxLen: never end on a dangling lead byte (a cut glyph
            // pair would throw inside the decoder and lose the whole string).
            len = Math.Min(total, maxLen);
            int j = 0;
            while (j < len) { if ((buf[j] & 0x80) != 0) { if (j + 1 >= len) { len = j; break; } j += 2; } else j++; }
        }
        if (len <= 0) return "";
        try
        {
            var enc = Native.Text.AtlusEncoding.P4;
            if (enc == null) return Encoding.ASCII.GetString(buf, len);
            return enc.GetString(buf, len).Replace('\0', ' ');
        }
        catch { return ""; }
    }

    private static ILogger _logger;
    private static Config _config;
    internal static Config Config => _config;
    private static IStartupScanner _startupScanner;
    internal static nint BaseAddress { get; private set; }

    /// <summary>This mod's own folder (set in Mod.cs). Bundled data files + sounds
    /// live here in a release; the dev tree falls back to the game's database folder.</summary>
    internal static string ModDir { get; set; } = "";

    /// <summary>
    /// Resolve a bundled data file. RELEASE: it sits flat in the mod folder. DEV:
    /// the game's <c>database/</c> folder (optionally a sub-folder such as "sounds").
    /// Returns the first path that exists; otherwise the mod-folder path so callers
    /// log a clean "missing" message.
    /// </summary>
    internal static string DataPath(string fileName, string dbSubdir = "")
    {
        if (!string.IsNullOrEmpty(ModDir))
        {
            var m = System.IO.Path.Combine(ModDir, fileName);
            if (System.IO.File.Exists(m)) return m;
        }
        var cwd = Environment.CurrentDirectory;
        foreach (var b in new[]
                 {
                     System.IO.Path.Combine(cwd, "Persona 4 golden", "database", dbSubdir),
                     System.IO.Path.Combine(cwd, "database", dbSubdir),
                 })
        {
            var p = System.IO.Path.Combine(b, fileName);
            if (System.IO.File.Exists(p)) return p;
        }
        return System.IO.Path.Combine(ModDir ?? "", fileName);
    }

    internal static bool Initialise(ILogger logger, Config config, IModLoader modLoader)
    {
        _logger = logger;
        _config = config;
        using var thisProcess = Process.GetCurrentProcess();
        BaseAddress = thisProcess.MainModule!.BaseAddress;

        var startupScannerController = modLoader.GetController<IStartupScanner>();
        if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
        {
            LogError($"Unable to get controller for Reloaded SigScan Library, stuff won't work :(");
            return false;
        }

        return true;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    /// <summary>
    /// True when the foreground (focused) window belongs to OUR process (the game).
    /// Background key-poll threads MUST early-return when this is false so the mod's
    /// hotkeys + speech/audio don't fire while the user is alt-tabbed to another app.
    /// </summary>
    internal static bool GameHasFocus()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == GetCurrentProcessId();
    }

    internal static void LogDebug(string message)
    {
        if (_config.DebugEnabled)
            _logger.WriteLine($"[Accessibility+] {message}");
    }

    internal static void Log(string message)
    {
        _logger.WriteLine($"[Accessibility+] {message}");
    }

    internal static void LogError(string message, Exception e)
    {
        _logger.WriteLine($"[Accessibility+] {message}: {e.Message}", Color.Red);
    }

    internal static void LogError(string message)
    {
        _logger.WriteLine($"[Accessibility+] {message}", Color.Red);
    }

    internal static void SigScan(string pattern, string name, Action<nint> action)
    {
        _startupScanner.AddMainModuleScan(pattern, result =>
        {
            if (!result.Found)
            {
                LogError($"Unable to find {name}, stuff won't work :(");
                return;
            }

            LogDebug($"Found {name} at 0x{result.Offset + BaseAddress:X}");

            action(result.Offset + BaseAddress);
        });
    }

    // Pushes the value of an xmm register to the stack, saving it so it can be restored with PopXmm
    public static string PushXmm(int xmmNum)
    {
        return // Save an xmm register 
            $"sub rsp, 16\n" + // allocate space on stack
            $"movdqu dqword [rsp], xmm{xmmNum}\n";
    }

    // Pushes all xmm registers (0-15) to the stack, saving them to be restored with PopXmm
    public static string PushXmm()
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            sb.Append(PushXmm(i));
        }

        return sb.ToString();
    }

    // Pops the value of an xmm register to the stack, restoring it after being saved with PushXmm
    public static string PopXmm(int xmmNum)
    {
        return //Pop back the value from stack to xmm
            $"movdqu xmm{xmmNum}, dqword [rsp]\n" +
            $"add rsp, 16\n"; // re-align the stack
    }

    // Pops all xmm registers (0-7) from the stack, restoring them after being saved with PushXmm
    public static string PopXmm()
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 7; i >= 0; i--)
        {
            sb.Append(PopXmm(i));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the address of a global from something that references it
    /// </summary>
    /// <param name="ptrAddress">The address to the pointer to the global (like in a mov instruction or something)</param>
    /// <returns>The address of the global</returns>
    internal static unsafe nuint GetGlobalAddress(nint ptrAddress)
    {
        return (nuint)((*(int*)ptrAddress) + ptrAddress + 4);
    }

    /// <summary>
    /// Gets the length of a null terminated string
    /// </summary>
    /// <param name="stringPtr">The pointer to the string</param>
    /// <param name="maxLength">The maximum possible length of the string</param>
    /// <returns>The length of the string</returns>
    public static unsafe int GetStringLength(byte* stringPtr, int maxLength)
    {
        for (int i = 0; i < maxLength; i++)
        {
            if (stringPtr[i] == 0)
                return i;
        }

        return maxLength;
    }
}