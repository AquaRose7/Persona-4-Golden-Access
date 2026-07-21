using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// The GAME OVER Velvet Room monologue ("Life is truth, and never a dream…"),
/// 2026-07-11. The death capture proved both halves: the scene runs as the
/// named task <b>evtGameOver</b> (registry), and the poem scrolls as GLYPH
/// lines through the shared UI-text fn FUN_140450C60 — so this reader speaks
/// the screen VERBATIM (no hardcoded text; NG+/variant wording reads itself).
///
/// Pattern = CompendiumInfoText: glyph runs accumulate per persistent line
/// object; a line flushes when its object changes or goes stale; per-text
/// dedupe absorbs the per-frame re-renders. Lines are spoken APPENDED
/// (non-interrupting) so the poem queues in order at the scroll's pace.
/// "Game over." announces once on the task's rising edge.
/// </summary>
internal sealed unsafe class GameOverReader
{
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);

    private volatile bool _active;          // evtGameOver task alive (poll thread)
    private bool _wasActive;

    private nint _lineObj = -1;
    private long _lastGlyphTick;
    private readonly System.Text.StringBuilder _line = new();
    private readonly System.Collections.Generic.HashSet<string> _spoken = new();

    internal GameOverReader(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnText, SetUiTextVA).Activate();
        var t = new Thread(Poll) { IsBackground = true, Name = "GameOverReader" };
        t.Start();
        Log("[GameOver] reader ready (task evtGameOver + 450C60 glyph capture)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(400);
            try
            {
                bool alive = IsTaskAlive();
                _active = alive;
                if (alive && !_wasActive)
                {
                    _spoken.Clear(); _line.Clear(); _lineObj = -1;
                    Log("[GameOver] evtGameOver task alive — reading the monologue");
                    Speech.Say("Game over.", true);
                }
                else if (!alive && _wasActive)
                {
                    FlushLine();
                    Log("[GameOver] task gone");
                }
                _wasActive = alive;
            }
            catch { }
        }
    }

    private nint OnText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try
        {
            if (!_active || p6 == 0) return ret;   // poem lines are glyph runs only
            long now = Environment.TickCount64;
            // a stale line (scroll paused / last line) flushes on the heartbeat
            if (_line.Length > 0 && now - _lastGlyphTick > 300 && p6 == _lineObj) FlushLine();
            if (p6 != _lineObj) { FlushLine(); _lineObj = p6; }
            string g = ReadCStr(p1, 16);
            _line.Append(g.Length > 0 ? g : " ");
            _lastGlyphTick = now;
            if (_line.Length > 200) FlushLine();
        }
        catch { /* never let a hook throw */ }
        return ret;
    }

    private void FlushLine()
    {
        if (_line.Length == 0) return;
        string s = _line.ToString().Trim();
        _line.Clear();
        if (s.Length < 2 || !_spoken.Add(s)) return;   // per-frame re-renders dedupe here
        Log($"[GameOver] line: {s}");
        Speech.Say(s, interrupt: false);               // queue the poem in order
    }

    // ── evtGameOver in the named-task registry ──────────────────────────────
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] TaskName = System.Text.Encoding.ASCII.GetBytes("evtGameOver");

    private static bool IsTaskAlive()
    {
        foreach (nint head in TaskHeads)
        {
            if (!IsReadableStatic(head, 8)) continue;
            nint n = *(nint*)head;
            for (int i = 0; i < 256 && n != 0; i++)
            {
                if (!IsReadableStatic(n, 0x58)) break;
                bool match = true;
                for (int k = 0; k < TaskName.Length; k++)
                    if (*(byte*)(n + k) != TaskName[k]) { match = false; break; }
                if (match && *(byte*)(n + TaskName.Length) == 0) return true;
                n = *(nint*)(n + 0x50);
            }
        }
        return false;
    }

    private static string ReadCStr(nint p, int maxLen)
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

    private static bool IsReadableStatic(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint protect = *(uint*)(buf + 36);
        return (protect & 0x101) == 0;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);
}
