using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Yomenaido BOOKSTORE reader (RE'd 2026-06-18 via Cheat Engine "find what
/// accesses" the cursor → render <c>FUN_140213190</c>).
///
/// The bookstore is a custom menu that REUSES the shop menu object's layout but
/// does NOT go through ShopUpdate, so the shop hook never sees it. Layout (live-
/// verified): menu object = <c>*(*param_1 + 0x48)</c>; within it,
/// <list type="bullet">
///   <item>+0x04 state (0x0A = list)</item>
///   <item>+0x32 window cursor, +0x34 scroll (abs row = cursor + scroll)</item>
///   <item>+0x60 list pointer, +0x68 count</item>
///   <item>list entries stride 0x14: +0x00 price (yen), +0x04 item id (u16)</item>
/// </list>
/// Books are key-item ids (1024-1280, HelpBmd.Event), e.g. Expert Study Methods
/// 1136 / Beginner Fishing 1146 / The Lovely Man 1259. Announces the highlighted
/// book's name + price + description on cursor change.
/// </summary>
internal sealed unsafe class BookstoreMenu
{
    private readonly IHook<DrawDelegate> _hook;
    private int _last = -1;
    private static readonly nint WalletAddr = unchecked((nint)0x1451BCD70L);

    public BookstoreMenu(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<DrawDelegate>(Draw, 0x140213190).Activate();
        Log("[Bookstore] hooked book-list render FUN_140213190");
    }

    private void Draw(nint param_1)
    {
        _hook.OriginalFunction(param_1);
        try { Read(param_1); } catch { }
    }

    private void Read(nint param_1)
    {
        if (!IsReadable(param_1, 8)) return;
        nint container = *(nint*)param_1;
        if (!IsReadable(container + 0x48, 8)) return;
        nint obj = *(nint*)(container + 0x48);                 // shop-shaped menu object
        if (obj == 0 || !IsReadable(obj, 0x70)) { _last = -1; return; }
        // Read only the LIST (0x0A) and Show-Info window (0x0B); the other states
        // are just the menu's open/close transitions (0x03/0x06/0x09 … 0x1E-0x22).
        short st = *(short*)(obj + 0x04);
        if (st != 0x0A && st != 0x0B) { _last = -1; return; }
        // Don't fight the real shop reader: while a normal shop is active it owns
        // ShopMenu.ShopState; the bookstore leaves it at ST_NONE (0xFFFF).
        if (ShopMenu.ShopState != 0xFFFF) { _last = -1; return; }

        nint list = *(nint*)(obj + 0x60);
        int count = *(int*)(obj + 0x68);
        int abs = *(short*)(obj + 0x32) + *(short*)(obj + 0x34);   // window cursor + scroll
        if (list == 0 || count <= 0 || count > 256 || abs < 0 || abs >= count) return;
        nint e = list + (nint)abs * 0x14;
        if (!IsReadable(e, 0x14)) return;
        int price = *(int*)e;
        ushort id = *(ushort*)(e + 4);
        if (id == 0 || id > 0x3000 || price < 0 || price > 9_999_999) { _last = -1; return; }

        int key = (id << 8) | (abs & 0xFF);
        if (key == _last) return;
        _last = key;

        string name = Item.GetName(id);
        if (string.IsNullOrEmpty(name)) name = $"Book {id}";
        string msg = name;
        if (price > 0) msg += $". {price} yen";
        uint wallet = IsReadable(WalletAddr, 4) ? *(uint*)WalletAddr : 0;
        if (wallet > 0) msg += $". You have {wallet} yen";
        string desc = Item.GetDescription(id);
        if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
        Log($"[Bookstore] [{abs}/{count}] id={id} price={price} \"{name}\"");
        Speech.Say(msg, interrupt: true);
    }

    private delegate void DrawDelegate(nint param_1);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;          // MEM_COMMIT
        uint protect = *(uint*)(buf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return false;  // NOACCESS / GUARD
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }
}
