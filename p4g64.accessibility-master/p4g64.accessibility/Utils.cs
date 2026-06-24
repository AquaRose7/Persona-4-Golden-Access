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