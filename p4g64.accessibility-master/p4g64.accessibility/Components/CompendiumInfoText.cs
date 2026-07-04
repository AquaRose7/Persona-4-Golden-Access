using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Compendium persona INFO-tab flavor-text reader (v1.3.5, user idea 2026-07-04: "A
/// winter fairy of European descent…" becomes a "Description" section in the compendium
/// I/K/J/L panel). Hooks the shared UI-text fn FUN_140450C60 (the QuestMenu/ShuffleText
/// pattern), gated on <see cref="VelvetFusion.CompendiumProfileTick"/> freshness.
/// Log-mapped 2026-07-04: the flavor BODY renders as glyph runs into sequential
/// persistent line objects (p6 != 0); every p6 == 0 full string on this screen is
/// list-side scratch (names, skill labels, headers) — so the description = the ordered
/// glyph lines, joined. Resets when the shown persona changes
/// (<see cref="VelvetFusion.CompendiumProfileId"/>); published via
/// <see cref="LatestDesc"/>/<see cref="DescForId"/> for VelvetFusion to append as a
/// panel section.
/// </summary>
internal sealed unsafe class CompendiumInfoText
{
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60);

    /// <summary>The captured flavor text of the persona in <see cref="DescForId"/>.</summary>
    internal static volatile string? LatestDesc;
    internal static volatile int DescForId = -1;
    /// <summary>TickCount64 of the last LatestDesc growth — consumers wait for a
    /// ~250ms-settled value so the tail line is guaranteed in.</summary>
    internal static long LastChangeTick;

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(
        nint param_1, byte param_2, byte param_3, uint param_4, byte param_5, nint param_6);

    private nint _lineObj = -1;
    private readonly System.Text.StringBuilder _line = new();
    private readonly System.Collections.Generic.List<string> _lines = new();
    private readonly System.Collections.Generic.HashSet<string> _lineSet = new();
    private bool _wasActive;
    private int _curId = -1;

    internal CompendiumInfoText(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnSetText, SetUiTextVA).Activate();
        Log("[CompInfoText] compendium description hook active on FUN_140450C60");
    }

    private nint OnSetText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try { Capture(p1, p6); } catch { /* never let a hook throw */ }
        return ret;
    }

    private void Capture(nint strPtr, nint textObj)
    {
        // FAST GATE — only live while the compendium profile draws.
        if (Environment.TickCount64 - VelvetFusion.CompendiumProfileTick > 250)
        {
            if (_wasActive)
            {
                _wasActive = false;
                Reset(-1);
            }
            return;
        }
        _wasActive = true;

        int pid = VelvetFusion.CompendiumProfileId;
        if (pid != _curId) Reset(pid);

        long now = Environment.TickCount64;
        // The flavor body draws ONCE (not per frame), so the LAST line never sees a
        // "next line started" flush and its tail went missing (user 2026-07-04). The
        // list side draws p6==0 text constantly, so this staleness check has a
        // heartbeat: flush a line that stopped growing ~100ms ago.
        if (_line.Length > 0 && now - _lastGlyphTick > 100) FlushLine();

        // Speak a SETTLED capture once per render burst — the Info tab redraws the
        // text every time it's (re)opened, so this reads on every visit (user
        // 2026-07-04: "read it every time when opening the description panel").
        // Appended (no interrupt) so the panel's own announce isn't clipped.
        if (!_announced && _lines.Count > 0 && now - LastChangeTick > 250)
        {
            _announced = true;
            string d = LatestDesc ?? "";
            if (d.Length > 8 && GameHasFocus())
            {
                Log($"[CompInfoText] announce ({_curId}): {d}");
                Speech.Say(d, interrupt: false);
            }
        }

        if (textObj == 0) return;   // list-side scratch — never part of the flavor body
        // Glyph activity after a long idle = a FRESH render pass (the Info tab was
        // re-entered) — recollect and re-announce.
        if (now - _lastGlyphTick > 400 && _lines.Count > 0)
        {
            _lines.Clear();
            _lineSet.Clear();
            _announced = false;
        }
        if (textObj != _lineObj) { FlushLine(); _lineObj = textObj; }
        string g = ReadCString(strPtr, 16);
        _line.Append(g.Length > 0 ? g : " ");
        _lastGlyphTick = now;
        if (_line.Length > 200) FlushLine();
    }
    private long _lastGlyphTick;
    private bool _announced;

    private void Reset(int pid)
    {
        _curId = pid;
        _lineObj = -1;
        _line.Clear();
        _lines.Clear();
        _lineSet.Clear();
        _announced = false;
        LatestDesc = null;
        DescForId = pid;
    }

    private void FlushLine()
    {
        if (_line.Length == 0) return;
        string s = _line.ToString().Trim();
        _line.Clear();
        if (s.Length < 2 || !_lineSet.Add(s)) return;
        _lines.Add(s);
        LatestDesc = string.Join(" ", _lines);
        DescForId = _curId;
        LastChangeTick = Environment.TickCount64;
    }

    // Guarded ASCII read (the QuestMenu helper).
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);
}
