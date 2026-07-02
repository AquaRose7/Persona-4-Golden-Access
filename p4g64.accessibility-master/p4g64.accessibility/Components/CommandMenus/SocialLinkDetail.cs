using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.CommandMenus;

/// <summary>
/// Reads the Social Link DETAILS screen (Space/"Details" from the S.Link list): the SL blurb, each
/// member's name + bio, and the rank status notes. That text is rendered glyph-by-glyph through the
/// shared UI-text fn FUN_140450C60 (no buffer), so we hook the renderer and reconstruct the panel.
///
/// Layout (confirmed 2026-06-30): the description panel uses TWO reused line objects (line 1 then line
/// 2) drawn with param_6 != 0; the rank/arcana/member-list/prompts draw with param_6 == 0 (ignored).
/// We accumulate glyphs per line object and treat the first object's reappearance as the frame
/// boundary (the quest-menu pattern), then speak the 2-line text when it changes. The poll thread does
/// the speaking so the hook never blocks.
/// </summary>
internal sealed unsafe class SocialLinkDetail
{
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60);
    private const long PCampPtrAddr = 0x140EC0A40;
    private const int OffOpenMask = 0x0C;
    private const int SLinkMaskBit = 1 << (5 + 1);   // submenu k=5 (Social Link) → mask bit 6 = 0x40

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);

    // Reconstruction state (hook thread only).
    private nint _lineObj = -1;
    private nint _firstObj = -1;
    private bool _leftFirst;
    private readonly System.Text.StringBuilder _line = new();
    private readonly System.Text.StringBuilder _full = new();

    // The panel renders its descriptions (blurb + selected member's bio + notes) into the same two
    // line objects EVERY frame, so we collect the set of complete descriptions seen since the last
    // poll (the "current panel"), then speak only the ones that are NEW vs the previous poll's set.
    // A static panel re-renders the same set → nothing new → silent; navigating (or exit/re-enter)
    // changes the set → the changed descriptions re-read. Speaks queued so blurb + bio read in full.
    private readonly object _lock = new();
    private readonly List<string> _frame = new();          // ordered, deduped descriptions this window
    private readonly HashSet<string> _frameDedup = new();
    private HashSet<string> _announced = new();            // poll thread: previous window's set
    private volatile bool _needReset;                      // poll → hook: panel went idle, drop stale buffer
    private bool _wasOpen;

    internal SocialLinkDetail(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnSetText, SetUiTextVA).Activate();
        var t = new Thread(Poll) { IsBackground = true, Name = "SLinkDetailPoll" };
        t.Start();
        Log("[SLinkDetail] ready");
    }

    private nint OnSetText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try { Capture(p1, p6); } catch { /* never throw from a hook */ }
        return ret;
    }

    private void Capture(nint strPtr, nint textObj)
    {
        // Fast gate: direct static read, no syscall.
        nint pCamp = *(nint*)PCampPtrAddr;
        if (pCamp == 0 || !IsReadable(pCamp + OffOpenMask, 4)) return;
        if ((*(int*)(pCamp + OffOpenMask) & SLinkMaskBit) == 0)
        {
            if (_firstObj != -1) { _firstObj = -1; _lineObj = -1; _leftFirst = false; _line.Clear(); _full.Clear(); }
            return;
        }
        // Panel went idle (exited Details to the list) since the last render — drop any half-built
        // line so the next panel doesn't inherit a stale leftover.
        if (_needReset) { _needReset = false; _firstObj = -1; _lineObj = -1; _leftFirst = false; _line.Clear(); _full.Clear(); }

        if (_firstObj != -1 && textObj != _firstObj) _leftFirst = true;
        if (textObj == 0) return;                            // rank / arcana / member-list / prompts
        if (_firstObj == -1) { _firstObj = textObj; _leftFirst = false; }

        // Frame boundary: the first line object reappears after we'd moved away → panel complete.
        if (textObj == _firstObj && _leftFirst)
        {
            FlushLine();
            string desc = _full.ToString().Trim();
            if (desc.Length >= 3)
                lock (_lock) { if (_frame.Count < 40 && _frameDedup.Add(desc)) _frame.Add(desc); }
            _full.Clear();
            _lineObj = -1;
            _leftFirst = false;
        }

        if (textObj != _lineObj) { FlushLine(); _lineObj = textObj; }
        string g = ReadCString(strPtr, 16);
        _line.Append(g.Length > 0 ? g : " ");
    }

    private void FlushLine()
    {
        if (_line.Length == 0) return;
        if (_full.Length > 0) _full.Append(' ');
        _full.Append(_line);
        _line.Clear();
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(120);
            try
            {
                if (!GameHasFocus()) continue;
                if (!InSLinkMenu())
                {
                    if (_wasOpen) { _wasOpen = false; lock (_lock) { _frame.Clear(); _frameDedup.Clear(); } _announced = new(); }
                    continue;
                }
                _wasOpen = true;

                List<string> snap;
                lock (_lock) { snap = new List<string>(_frame); _frame.Clear(); _frameDedup.Clear(); }
                // Empty window = the Details panel isn't rendering (we're on the list) → reset so a
                // later re-entry re-reads, and tell the hook to drop any half-built leftover line.
                if (snap.Count == 0) { _announced = new(); _needReset = true; continue; }

                // Combine the window's NEW descriptions into one utterance and INTERRUPT — so blurb+bio
                // read together on entry, but navigating cuts the stale bio (no queue backlog/lag).
                var sb = new System.Text.StringBuilder();
                foreach (var d in snap)
                {
                    if (_announced.Contains(d)) continue;
                    if (sb.Length > 0)
                    {
                        char last = sb[sb.Length - 1];          // don't double up sentence punctuation
                        sb.Append(last is '.' or '!' or '?' ? " " : ". ");
                    }
                    sb.Append(d);
                }
                if (sb.Length > 0) Speech.Say(sb.ToString(), true);
                _announced = new HashSet<string>(snap);
            }
            catch { }
        }
    }

    private static bool InSLinkMenu()
    {
        if (!IsReadable((nint)PCampPtrAddr, 8)) return false;
        nint pCamp = *(nint*)PCampPtrAddr;
        if (pCamp == 0 || !IsReadable(pCamp + OffOpenMask, 4)) return false;
        return (*(int*)(pCamp + OffOpenMask) & SLinkMaskBit) != 0;
    }

    private static string ReadCString(nint p, int maxLen)
    {
        if (p == 0) return "";
        ulong a = (ulong)p;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return "";
        byte* qbuf = stackalloc byte[48];
        if (VirtualQuery(p, qbuf, 48) == 0) return "";
        if (*(uint*)(qbuf + 32) != 0x1000) return "";
        uint protect = *(uint*)(qbuf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return "";
        nint regionBase = *(nint*)(qbuf + 0);
        nint regionSize = *(nint*)(qbuf + 24);
        ulong end = (ulong)regionBase + (ulong)regionSize;
        int safe = (int)System.Math.Min((ulong)maxLen, end - a);
        var sb = new System.Text.StringBuilder(maxLen);
        for (int i = 0; i < safe; i++)
        {
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString();
    }

    [DllImport("kernel32.dll")] private static extern nint VirtualQuery(nint a, byte* b, nint l);
    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint pr = *(uint*)(buf + 36);
        if ((pr & 0x01) != 0 || (pr & 0x100) != 0) return false;
        nint rb = *(nint*)(buf + 0); nint rs = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)rb + (ulong)rs;
    }
}
