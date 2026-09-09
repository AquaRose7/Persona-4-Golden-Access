using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the game's system confirmation / info / progress popups that have NO
/// Yes/No choice (e.g. "Now loading…", "Save complete", "Load failed",
/// "Internet connection…"). The Yes/No popups are handled by
/// <see cref="InternetDialog"/>.
///
/// HOW: every popup looks up its message via the shared lookup thunk at
/// 0x14042DCA0 (-> FUN_1665C5AC0), passing a message index in ECX. The lookup
/// returns <c>MsgTable + index*0x10</c>; each 16-byte entry is
/// {+0x00 char* text, +0x08 flags}. The flags low word is the button type:
/// 2 = Yes/No, 1 = OK, 0 = progress/none. We hook the thunk, read the index,
/// and speak every message whose type isn't Yes/No (those would double up with
/// InternetDialog). Dedupe on index; a poll thread clears it once the popup
/// stops re-looking-up (closed).
/// </summary>
internal unsafe class SystemMessage : IDisposable
{
    // English system-message table (ASLR off -> constant). Same table the
    // InternetDialog reader uses.
    private static readonly nint MsgTable = (nint)0x140AB17E0;
    private const long ThunkAddr = 0x14042DCA0;

    private IHook<LookupDelegate>? _hook;

    // A screen can show several messages at once (each looked up every frame),
    // so we dedupe PER index: announce a message once when it appears, then
    // suppress it while it keeps being looked up. An index not seen for the
    // window is "gone" and may announce again next time.
    private readonly System.Collections.Generic.Dictionary<int, long> _seen = new();
    private const long SuppressMs = 1500;

    private delegate nint LookupDelegate(int index);

    internal SystemMessage(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<LookupDelegate>(OnLookup, ThunkAddr).Activate();
        Log("System message reader hook active.");
    }

    /// <summary>
    /// The system-message table base for the CURRENT LANGUAGE (2026-09-09): the game's own
    /// lookup returns <c>base + index*0x10</c>, so the first lookup teaches us the base
    /// (English = 0x140AB17E0; other languages live in their own blocks). InternetDialog
    /// resolves its Yes/No text through this too.
    /// </summary>
    internal static nint LiveTableBase;

    private nint OnLookup(int index)
    {
        var res = _hook!.OriginalFunction(index);
        try
        {
            if (index >= 0 && index <= 0x200 && res != 0)
            {
                nint b = res - index * 0x10;
                if (b != LiveTableBase) { LiveTableBase = b; Log($"[SystemMessage] language table base = 0x{b:X} (english = 0x{MsgTable:X})"); }
            }
            Handle(index, res);
        }
        catch { /* never let a hook throw */ }
        return res;
    }

    private void Handle(int index, nint entry)
    {
        if (index < 0 || index > 0x200) return;
        long now = Environment.TickCount64;
        bool recent = _seen.TryGetValue(index, out var t) && now - t < SuppressMs;
        _seen[index] = now;
        if (recent) return; // already announced while this message is on screen

        if (entry == 0) entry = MsgTable + index * 0x10;   // fallback: the English block
        if (!IsReadable(entry + 8)) return;
        int flags = *(short*)(entry + 8) & 0xFFFF;
        if (flags == 2) return; // Yes/No -> InternetDialog

        nint msgPtr = *(nint*)entry;
        if (!IsReadable(msgPtr)) return;
        var msg = DecodeMessage(msgPtr);
        if (string.IsNullOrWhiteSpace(msg)) return;

        // Queue (don't interrupt) so a multi-line screen reads in full.
        Speech.Say(msg, interrupt: false);
    }

    // 2026-09-09: proper Atlus MSG decode (function codes skipped by their real length,
    // glyph pairs decoded) — the old loop dropped every high byte, i.e. all non-English text.
    private static string DecodeMessage(nint p) => Native.Text.GameText.DecodeMsg(p, 512);

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nint VQ(nint a, byte* b, nint l);
    private static bool IsReadable(nint a)
        => Utils.ProbeReadable(a, 8);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable

    public void Dispose() { }
}
