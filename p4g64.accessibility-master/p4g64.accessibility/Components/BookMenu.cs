using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the "Which will you read?" BOOK menu (opened when reading a book at
/// home). It is a standalone field menu with no existing hook; reverse-engineered
/// 2026-06-26 via the snapshot method (cursor found at struct+0x1DA) + CE
/// find-what-accesses (instructions at 0x140153D30 / 0x140152229) + Ghidra.
///
/// Hooks the menu's per-frame RENDER fn FUN_140151F50(menuStruct). The struct
/// (first arg) layout, all verified live:
///   +0x28  byte   flag; bit 0x4 = menu visible/active (the render's own gate)
///   +0x1D8 short  book COUNT
///   +0x1DA short  CURSOR — WINDOW-relative row (caps at the visible-row count)
///   +0x1DC short  scroll (top visible row) — TRUE book index = cursor + scroll
///                 (the +0x78 array is the full list; long lists scroll, 2026-06-30)
///   +0x78 + i*8   book entry i (8 bytes), as shorts:
///       +0x00  book ITEM id  -> Item.GetName (the title)
///       +0x04  chapters READ   (grey buttons on screen)
///       +0x06  chapters TOTAL  (read..total-1 = unread / orange)
///
/// Book titles are plain item names (e.g. id 0x04EB = "The Lovely Man"), so the
/// existing Item.GetName resolves every book automatically — no hardcoded map.
/// Announces the highlighted book + its chapter read-state, deduped on cursor.
/// </summary>
internal unsafe class BookMenu : IDisposable
{
    private IHook<RenderDelegate>? _hook;
    private nint _lastMenu;
    private int  _lastCursor = -1;
    private int  _lastInfo = -1;   // +0x348 flag: 1 = Help/description panel open

    private delegate void RenderDelegate(nint menu);

    internal BookMenu(IReloadedHooks hooks)
    {
        // Prologue of FUN_140151F50; the lea/call displacements are wildcarded.
        // Ends on `F6 47 28 04` = test byte [rdi+0x28],4 (the menu-active gate).
        SigScan(
            "40 55 53 57 41 54 41 55 48 8D 6C 24 E0 48 81 EC 20 01 00 00 48 8B F9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? F6 47 28 04",
            "BookMenu::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Book menu reader hook active.");
            });
    }

    private void OnRender(nint menu)
    {
        _hook!.OriginalFunction(menu);
        try { Read(menu); } catch { /* never let a hook throw */ }
    }

    private void Read(nint menu)
    {
        if (!IsReadable(menu + 0x28, 1)) return;
        // The render itself only draws when this flag bit is set; mirror that so
        // we never announce a closing/hidden menu.
        if ((*(byte*)(menu + 0x28) & 0x4) == 0) { _lastMenu = 0; _lastCursor = -1; _lastInfo = -1; return; }

        if (!IsReadable(menu + 0x1D8, 6)) return;
        int count  = *(short*)(menu + 0x1D8);
        // +0x1DA is the WINDOW-relative row (caps at the visible-row count); the list
        // scrolls via +0x1DC. The +0x78 array is the FULL book list, so the true book
        // index = cursor + scroll. Using cursor alone read the wrong entry and (worse)
        // dedupe-silenced every book reached by scrolling. Verified live 2026-06-30.
        int cursor = *(short*)(menu + 0x1DA);
        int scroll = IsReadable(menu + 0x1DC, 2) ? *(short*)(menu + 0x1DC) : 0;
        if (scroll < 0) scroll = 0;
        int abs = cursor + scroll;
        if (count < 1 || count > 64 || abs < 0 || abs >= count) return;

        // +0x348 = the "Show Info"/Help panel flag (1 = description showing, 0 = list).
        // Set by FUN_140153000 (list state -> 0, info state -> 1).
        int info = IsReadable(menu + 0x348, 2) ? *(short*)(menu + 0x348) : 0;

        // New menu instance -> force a re-announce of the current row.
        if (menu != _lastMenu) { _lastMenu = menu; _lastCursor = -1; _lastInfo = -1; }

        // Re-announce when the cursor moves OR when we toggle between list <-> info.
        if (abs == _lastCursor && info == _lastInfo) return;
        bool infoChanged = info != _lastInfo;
        _lastCursor = abs;
        _lastInfo = info;

        nint entry = menu + 0x78 + abs * 8;
        if (!IsReadable(entry, 8)) return;
        int id    = *(short*)(entry + 0x00);
        int read  = *(short*)(entry + 0x04);
        int total = *(short*)(entry + 0x06);

        string name = Item.GetName(id);
        if (string.IsNullOrEmpty(name)) name = $"Book {abs + 1}";

        if (info == 1)
        {
            // Description / Help panel is open: speak the book's flavor text.
            string desc = Item.GetDescription(id);
            if (string.IsNullOrEmpty(desc))
                Speech.Say($"{name}. No description.", interrupt: true);
            else
                Speech.Say(infoChanged ? $"{name}. {desc}" : desc, interrupt: true);
            Log($"[BookMenu] info id=0x{id:X} '{name}' desc=\"{desc}\"");
            return;
        }

        Speech.Say($"{name}. {Chapters(read, total)}. {abs + 1} of {count}.", interrupt: true);
    }

    private static string Chapters(int read, int total)
    {
        if (total <= 0) return "no chapters";
        int unread = total - read;
        string ch = total == 1 ? "1 chapter" : $"{total} chapters";
        if (read <= 0)    return $"{ch}, none read";
        if (unread <= 0)  return $"{ch}, all read";
        return $"{ch}, {unread} unread";
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

    public void Dispose() { }
}
