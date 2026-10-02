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
        long t0 = PerfDiag.Begin();
        try { Capture(p1, p6); } catch { /* never let a hook throw */ }
        PerfDiag.End(PerfDiag.B.QuestCap, t0);
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
        { int m = *(int*)(pCamp + OffOpenMask); if (m == 0 || (m & ~0x1FF) != 0 || (m & QuestMaskBit) == 0) return; }   // + camp-mask sanity (audit 2026-08-31)

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
        { int m = *(int*)(pCamp + OffOpenMask); if (m == 0 || (m & ~0x1FF) != 0 || (m & QuestMaskBit) == 0) { _lastSpokenRow = int.MinValue; _needReset = true; return; } }   // + camp-mask sanity (audit 2026-08-31)

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
            Speech.Say($"Quest {row + 1} of {total}{QuestStatus(row)}. {desc}");
    }

    // The "Completed" stamp and the "New" tag are sprites (no text for the hook), but they come
    // from the game's quest table *(0x140EC0A48): +4 count, 16-byte entries @+0x10 — +0x0 "New"
    // BIT, +0x4 accepted BIT, +0x8 COMPLETED BIT, +0xC quest number. Menu row r = table entry r
    // (live-verified 2026-10-02 against the screen; COMPLETE_QUEST sets +8 — FUN_14017B1A0).
    private const long QuestTablePtrAddr = 0x140EC0A48;
    private const long FlagBitmapPtrAddr = 0x1451FF7A0;

    /// <summary>", completed" / ", new" for the quest on menu row <paramref name="row"/>, else "".</summary>
    private static string QuestStatus(int row)
    {
        nint table;
        if (!TryReadRaw((nint)QuestTablePtrAddr, &table, 8) || table == 0) return "";
        int count;
        if (!TryReadRaw(table + 4, &count, 4) || row < 0 || row >= count) return "";
        uint* entry = stackalloc uint[3];
        if (!TryReadRaw(table + 0x10 + row * 0x10, entry, 12)) return "";
        if (FlagSet(entry[2])) return ", completed";
        if (FlagSet(entry[0])) return ", new";
        return "";
    }

    private static bool FlagSet(uint bitId)
    {
        if (bitId == 0 || bitId >= 0x10000) return false;
        nint bitmap;
        if (!TryReadRaw((nint)FlagBitmapPtrAddr, &bitmap, 8) || bitmap == 0) return false;
        uint word;
        if (!TryReadRaw(bitmap + (nint)(bitId >> 5) * 4, &word, 4)) return false;
        return (word & (1u << (int)(bitId & 31))) != 0;
    }

    // One VirtualQuery, then read up to maxLen ASCII bytes within the validated region.
    private static string ReadCString(nint p, int maxLen) => ReadCStringRpm(p, maxLen);  // RPM since 2026-07-27 (menu-heaviness fix: VirtualQuery stalls under allocator contention)

    private static nint ReadPtr(nint addr)
    {
        if (!IsReadable(addr, 8)) return 0;
        nint p = *(nint*)addr;
        return IsReadable(p, 0x20) ? p : 0;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
        => Utils.ProbeReadable(addr, size);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable

    public void Dispose() { _hook?.Disable(); }
}
