using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.CommandMenus;

/// <summary>
/// Camp QUEST menu reader — announces the SELECTED quest's DESCRIPTION (or that it
/// is not yet discovered). Reverse-engineered 2026-06-28; see
/// memory/quest_menu_deadend.md → quest_menu_solved.md for the full history.
///
/// The quest titles/descriptions have NO readable handle (a pointer-scan of the
/// title scratch buffer found zero pointers in) and the titles are built dynamically
/// ("Acquire " + item name), so nothing can be read from a struct field or BMD. The
/// only way in is the game's generic "set UI text" fn:
///   int* FUN_140450c60(char* str=param_1, byte, byte, uint, byte, int* textObj=param_6)
///
/// Two live-verified facts make a robust reader possible:
///   • The DETAIL PANEL (right side) is SELECTION-DIRECT: it shows the selected
///     quest's full description (undiscovered rows show "This quest has not yet been
///     discovered."). It renders one glyph at a time into a run of PERSISTENT text
///     objects (param_6 != 0), one object per wrapped line. The list row titles
///     render as full strings with param_6 == 0 — they are NOT selection-direct (the
///     highlighted row has no distinguishing arg) and quests sit at FIXED slots, so
///     mapping a list title to the selected row is unreliable. We therefore read the
///     selection-direct DETAIL instead, which is correct at any slot.
///   • The SELECTED absolute row = quest_obj +0x28 (in-window cursor) + +0x2A (scroll
///     top). Verified: quest 3 -> 2+0=2; quest 69 -> 5+63=68. Total = +0x32.
///
/// Per frame the render draws the list titles (param_6 == 0) then the detail panel as
/// a single consecutive run of param_6 != 0 glyph calls (one object per line). We
/// reconstruct that run into the description, tag it with the row that was selected
/// when it rendered, and a poll thread announces it once the row settles AND matches
/// (so the spoken text can never belong to a different row than the cursor is on).
///
/// PERF: FUN_140450C60 fires for EVERY UI text draw in the whole game, so the hook
/// fast-gates on a single direct read of the camp static (no syscall) — it only does
/// real work while the camp menu is open AND the Quest submenu is focused.
/// </summary>
internal sealed unsafe class QuestMenu : IDisposable
{
    // FUN_140450C60 — hardcoded VA (ASLR off, image base 0x140000000). Generic shared
    // helper whose prologue isn't unique, so a SigScan would risk a wrong match.
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60);

    private const long PCampPtrAddr = 0x140EC0A40;
    private const int  OffOpenMask  = 0x0C;
    private const int  OffSubObjs   = 0x9E70;             // + k*8
    private const int  QuestK       = 6;                  // submenu id
    private const int  QuestMaskBit = 1 << (QuestK + 1);  // 0x80

    private const string UndiscoveredSentinel = "not yet been discovered";

    private IHook<SetTextDelegate>? _hook;

    private delegate nint SetTextDelegate(
        nint param_1, byte param_2, byte param_3, uint param_4, byte param_5, nint param_6);

    // Selection-direct detail, written by the hook (game) thread, read by the poll thread.
    private readonly object _lock = new();
    private int _selRow = int.MinValue;
    private string _selDesc = "";

    internal QuestMenu(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnSetText, SetUiTextVA).Activate();
        Log("[QuestMenu] quest reader hook active on FUN_140450C60.");
        var t = new Thread(Poll) { IsBackground = true, Name = "QuestMenuPoll" };
        t.Start();
    }

    // ── Hook: reconstruct the selected quest's detail description ──────────────────
    private nint OnSetText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try { Capture(p1, p6); } catch { /* never let a hook throw */ }
        return ret;
    }

    // Detail-run reconstruction state (hook thread only).
    private nint _descObj = -1;       // current wrapped-line text object
    private nint _runFirstObj = -1;   // first line object of the detail run (frame-boundary marker)
    private bool _leftFirst;          // have we drawn something OTHER than the first line obj since?
    private volatile bool _needReset; // set by the poll thread when the Quest submenu is left
    private readonly System.Text.StringBuilder _descLine = new();
    private readonly System.Text.StringBuilder _descFull = new();

    private void Capture(nint strPtr, nint textObj)
    {
        // FAST GATE — direct static read (always-mapped image global, NO VirtualQuery).
        // Exits in one deref for the ~100% of UI-text draws made while the camp menu is closed.
        nint pCamp = *(nint*)PCampPtrAddr;
        if (pCamp == 0) return;
        if (!IsReadable(pCamp + OffOpenMask, 4)) return;
        if ((*(int*)(pCamp + OffOpenMask) & QuestMaskBit) == 0) return;

        if (_needReset)
        {
            _needReset = false;
            _runFirstObj = -1; _descObj = -1; _leftFirst = false;
            _descLine.Clear(); _descFull.Clear();
        }

        // Track when the render moves OFF the first line object (incl. param_6 == 0 list
        // titles / scratch that interleave between detail lines). Only after we've left it
        // and come back does a new frame begin.
        if (_runFirstObj != -1 && textObj != _runFirstObj) _leftFirst = true;

        // List titles + render-scratch (param_6 == 0) carry no detail content — ignore them.
        if (textObj == 0) return;

        if (_runFirstObj == -1) { _runFirstObj = textObj; _leftFirst = false; }

        // Frame boundary: the detail panel re-renders from its first line object each frame.
        // We're at a boundary only when that object reappears AFTER we'd moved away from it.
        if (textObj == _runFirstObj && _leftFirst)
        {
            FlushLine();
            StoreDesc(pCamp);
            _descFull.Clear();
            _descObj = -1;
            _leftFirst = false;
        }

        // A change of (non-zero) text object = a new wrapped line.
        if (textObj != _descObj) { FlushLine(); _descObj = textObj; }

        string g = ReadCString(strPtr, 16);
        // Word separators are multi-byte (full-width) glyphs that decode to empty — treat as space.
        _descLine.Append(g.Length > 0 ? g : " ");
    }

    private void FlushLine()
    {
        if (_descLine.Length == 0) return;
        if (_descFull.Length > 0) _descFull.Append(' ');
        _descFull.Append(_descLine);
        _descLine.Clear();
    }

    private void StoreDesc(nint pCamp)
    {
        string desc = _descFull.ToString().Trim();
        if (desc.Length == 0) return;
        nint q = ReadPtr(pCamp + OffSubObjs + QuestK * 8);
        int row = (q != 0 && IsReadable(q, 0x34)) ? *(short*)(q + 0x28) + *(short*)(q + 0x2A) : -1;
        if (row < 0) return;
        lock (_lock) { _selRow = row; _selDesc = desc; }
    }

    // ── Poll: announce the selected row, row-synced with the reconstructed detail ──
    private int _lastSpokenRow = int.MinValue;
    private int _pendingRow = int.MinValue, _pendingHits;

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(60);
            try { Tick(); }
            catch { Thread.Sleep(500); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) { _lastSpokenRow = int.MinValue; _needReset = true; return; }

        nint pCamp = ReadPtr(unchecked((nint)PCampPtrAddr));
        if (pCamp == 0 || !IsReadable(pCamp + OffOpenMask, 4)) { _lastSpokenRow = int.MinValue; _needReset = true; return; }
        if ((*(int*)(pCamp + OffOpenMask) & QuestMaskBit) == 0) { _lastSpokenRow = int.MinValue; _needReset = true; return; }

        nint q = ReadPtr(pCamp + OffSubObjs + QuestK * 8);
        if (q == 0 || !IsReadable(q, 0x34)) return;

        int row   = *(short*)(q + 0x28) + *(short*)(q + 0x2A);
        int total = *(short*)(q + 0x32);
        if (row < 0 || total <= 0 || row >= total) return;

        // Debounce: wait for the row to settle (scroll animation jitters it).
        if (row != _pendingRow) { _pendingRow = row; _pendingHits = 1; return; }
        if (++_pendingHits < 2) return;
        if (row == _lastSpokenRow) return;

        // Only speak once the reconstructed detail belongs to THIS row (so the description
        // can never lag onto the wrong quest). If not yet in sync, try again next tick.
        string? desc = null;
        lock (_lock) { if (_selRow == row) desc = _selDesc; }
        if (desc == null) return;

        _lastSpokenRow = row;
        if (desc.Length == 0 || desc.Contains(UndiscoveredSentinel))
            Speech.Say($"Quest {row + 1} of {total}, not yet discovered.");
        else
            Speech.Say($"Quest {row + 1} of {total}. {desc}");
    }

    // One VirtualQuery, then read up to maxLen ASCII bytes within the validated region.
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

    private static nint ReadPtr(nint addr)
    {
        if (!IsReadable(addr, 8)) return 0;
        nint p = *(nint*)addr;
        return IsReadable(p, 0x20) ? p : 0;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint protect = *(uint*)(buf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }

    public void Dispose() { _hook?.Disable(); }
}
