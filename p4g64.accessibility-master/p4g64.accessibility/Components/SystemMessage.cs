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

    private nint OnLookup(int index)
    {
        var res = _hook!.OriginalFunction(index);
        try { Handle(index); } catch { /* never let a hook throw */ }
        return res;
    }

    private void Handle(int index)
    {
        if (index < 0 || index > 0x200) return;
        long now = Environment.TickCount64;
        bool recent = _seen.TryGetValue(index, out var t) && now - t < SuppressMs;
        _seen[index] = now;
        if (recent) return; // already announced while this message is on screen

        nint entry = MsgTable + index * 0x10;
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

    // Decode an Atlus message: ASCII bytes are text, 0x0A is a line break
    // (spoken as a space), control codes (>= 0x80) are 2-byte sequences we
    // skip, 0x00 terminates.
    private static string DecodeMessage(nint p)
    {
        byte* b = (byte*)p;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 512; )
        {
            byte c = b[i];
            if (c == 0) break;
            if (c >= 0x80)      i += 2;
            else if (c == 0x0A) { sb.Append(' '); i++; }
            else if (c >= 0x20) { sb.Append((char)c); i++; }
            else                i++;
        }
        return sb.ToString().Trim();
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nint VQ(nint a, byte* b, nint l);
    private static bool IsReadable(nint a)
    {
        if (a < 0x10000) return false;
        byte* buf = stackalloc byte[48];
        if (VQ(a, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint p = *(uint*)(buf + 36);
        return (p & 0x01) == 0 && (p & 0x100) == 0;
    }

    public void Dispose() { }
}
