using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the anime/movie cutscene subtitles (English text over the Japanese-audio
/// movies; toggled in-game by "Anime Subtitles"). P4G renders these itself, NOT
/// via CRIWARE's subtitle channel.
///
/// The current subtitle line is plain UTF-8 in a fixed static buffer at
/// 0x14624CCB0 (.xpdata; ASLR off). It's loaded AHEAD of display, so we hook the
/// per-frame subtitle controller FUN_14054B6E0 and only speak a line once the
/// movie time is inside its [start,end) window (synced to what's on screen):
///   - schedule current entry ptr @ DAT_14624CD90 → [start(double), end(double), ...]
///   - movie time (0..1) from FUN_14054B370.
/// Hooking the controller also means we ONLY run during a movie (no false reads).
///
/// On/off via <see cref="ReaderEnabled"/> / <see cref="ToggleReader"/>.
/// </summary>
internal unsafe class SubtitleReader : IDisposable
{
    private const long ControllerVA = 0x14054B6E0;
    private const long TimeVA = 0x14054B370;
    private const long SchedulePtrVA = 0x14624CD90;  // *(ptr) = current entry [start,end,textptr]
    private const long SubtitleTextVA = 0x14624CCB0;
    private const int MaxBytes = 1024;

    internal static bool ReaderEnabled = true;

    internal static void ToggleReader()
    {
        ReaderEnabled = !ReaderEnabled;
        Speech.Say(ReaderEnabled ? "Subtitle reader on." : "Subtitle reader off.", true);
    }

    private delegate void ControllerDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate float GetTimeDelegate();

    private IHook<ControllerDelegate>? _hook;
    private readonly GetTimeDelegate _getTime;
    private string _lastShown = "";

    internal SubtitleReader(IReloadedHooks hooks)
    {
        _getTime = Marshal.GetDelegateForFunctionPointer<GetTimeDelegate>((nint)TimeVA);
        _hook = hooks.CreateHook<ControllerDelegate>(OnController, ControllerVA).Activate();
        Log($"[Subtitle] subtitle-controller hook @0x{ControllerVA:X}");
    }

    private void OnController()
    {
        _hook!.OriginalFunction();
        try { Tick(); } catch (Exception ex) { Log($"[Subtitle] {ex.Message}"); }
    }

    private void Tick()
    {
        // Current schedule entry (set/advanced by the controller we just ran):
        //   entry+0x00 start(double) · +0x08 end(double) · +0x10 textptr.
        if (!IsReadable(SchedulePtrVA, 8)) return;
        nint entry = *(nint*)SchedulePtrVA;
        if (entry == 0) { _lastShown = ""; return; }       // subtitles finished
        if (!IsReadable(entry, 0x18)) return;
        double start = *(double*)entry;
        double end   = *(double*)(entry + 8);

        float t = _getTime();
        if (!(t >= start && t < end)) return;              // not on screen yet (pre-load / gap)

        // Read the ORIGINAL source text (the render buffer keeps stale tail bytes
        // from longer previous lines). Source uses '\n' line breaks → spaces.
        nint textptr = *(nint*)(entry + 0x10);
        string text = ReadString(textptr);
        if (text.Length == 0 || text == _lastShown) return;
        _lastShown = text;

        Log($"[Subtitle] -> \"{text}\"");
        if (ReaderEnabled && Utils.GameHasFocus()) Speech.Say(text, true);
    }

    private string ReadString(nint addr)
    {
        if (addr == 0 || !IsReadable(addr, 4)) return "";
        byte* p = (byte*)addr;
        var bytes = new List<byte>(256);
        for (int i = 0; i < MaxBytes; i++)
        {
            byte b = p[i];
            if (b == 0) break;                                   // single null = end
            bytes.Add(b == (byte)'\n' ? (byte)' ' : b);          // line break → space
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(long addr, int size)
    {
        if (addr == 0) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery((nint)addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;            // MEM_COMMIT
        return (*(uint*)(buf + 36) & (0x01 | 0x100)) == 0;         // not NOACCESS/GUARD
    }

    public void Dispose() => _hook?.Disable();
}
