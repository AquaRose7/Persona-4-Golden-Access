using System.Collections.Generic;
using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the overworld fast-travel TOWN MAP (the lmap field menu opened from
/// street exits). The on-screen destination labels are drawn as sprites, not
/// text, so we map each option's sprite id to a name.
///
/// Hooks the menu's render/update FUN_1402F4B00(controller). The menu struct is
/// at controller+0x48:
///   +0x14  int  cursor (highlighted option)
///   +0x28  int  option count
///   +0x2C  int[] per-option sprite ids (id = key into the global label sheet)
/// Sprite ids were captured live and named by the user (id->name below).
/// Unmapped ids (e.g. destinations that unlock later — Okina, the farm, the ski
/// lodge) are spoken as "Destination N" and logged so they can be named.
/// </summary>
internal unsafe class TownMapReader : IDisposable
{
    private static readonly Dictionary<int, string> Names = new()
    {
        { 0, "Yasogami High School" },
        { 1, "Central Shopping District" },
        { 2, "Junes Department Store" },
        { 3, "Samegawa Flood Plain" },
        { 5, "Dojima Residence" },
        { 6, "Okina City" },
        { 8, "Shichiri Beach" },   // user-confirmed 2026-07-07
    };

    private IHook<RenderDelegate>? _hook;
    private nint _lastMenu;
    private int  _lastCursor = -1;

    private delegate nint RenderDelegate(nint controller);

    internal TownMapReader(IReloadedHooks hooks)
    {
        SigScan(
            "48 8B C4 48 89 48 08 55 56 57 41 54 41 55 41 56 41 57 48 8D A8 08 FD FF FF 48 81 EC C0 03 00 00",
            "TownMap::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Town map reader hook active.");
            });
    }

    private nint OnRender(nint controller)
    {
        var res = _hook!.OriginalFunction(controller);
        try { Read(controller); } catch { /* never let a hook throw */ }
        return res;
    }

    private void Read(nint controller)
    {
        if (!IsReadable(controller + 0x48)) return;
        nint menu = *(nint*)(controller + 0x48);
        if (!IsReadable(menu + 0x2C)) return;

        int cursor = *(int*)(menu + 0x14);
        int count  = *(int*)(menu + 0x28);
        if (count < 1 || count > 32 || cursor < 0 || cursor >= count) return;

        // New menu instance -> re-announce the current option.
        if (menu != _lastMenu) { _lastMenu = menu; _lastCursor = -1; }
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        if (!IsReadable(menu + 0x2C + cursor * 4)) return;
        int spriteId = *(int*)(menu + 0x2C + cursor * 4);

        if (Names.TryGetValue(spriteId, out var name))
        {
            Speech.Say(name, interrupt: true);
        }
        else
        {
            Log($"[TownMap] unmapped spriteId={spriteId} (cursor {cursor + 1} of {count})");
            Speech.Say($"Destination {cursor + 1}", interrupt: true);
        }
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
