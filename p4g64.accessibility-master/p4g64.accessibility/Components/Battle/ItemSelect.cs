using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Speaks the highlighted entry in the in-battle ITEM menu as the cursor moves,
/// mirroring <see cref="SkillSelect"/>. The list has two sibling per-row draw
/// functions (0x1400E5FE0 / 0x1400E62D0 — identical but for the last 3 prologue
/// bytes); both are hooked. The highlighted row's item id is at
/// <c>btl + 0xD2C + row*4</c> (u16), quantity at +0x02 (byte); <c>isSelected != 0</c>
/// marks the cursor row. Item id → <see cref="Item.GetName"/>.
/// </summary>
internal sealed unsafe class ItemSelect
{
    private IHook<DrawDelegate> _hook1, _hook2;
    private int _lastId = -1;
    private bool _lastDrawDesc;

    internal ItemSelect(IReloadedHooks hooks)
    {
        SigScan("48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 57 41 56 41 57 48 81 EC 90 00 00 00 0F 29 70 D8 48 8B E9",
            "BtlItemDraw1", a => _hook1 = hooks.CreateHook<DrawDelegate>(Draw1, a).Activate());
        SigScan("48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 57 41 56 41 57 48 81 EC 90 00 00 00 0F 29 70 D8 48 8B F9",
            "BtlItemDraw2", a => _hook2 = hooks.CreateHook<DrawDelegate>(Draw2, a).Activate());
    }

    private void Draw1(Battle.BtlInfo* btl, int row, float p3, float p4, byte alpha, int isSelected, int drawDesc)
    {
        _hook1.OriginalFunction(btl, row, p3, p4, alpha, isSelected, drawDesc);
        Handle(btl, row, alpha, isSelected, drawDesc);
    }

    private void Draw2(Battle.BtlInfo* btl, int row, float p3, float p4, byte alpha, int isSelected, int drawDesc)
    {
        _hook2.OriginalFunction(btl, row, p3, p4, alpha, isSelected, drawDesc);
        Handle(btl, row, alpha, isSelected, drawDesc);
    }

    // Mirrors SkillSelect: announce name+qty on a new highlighted item; when the
    // "show more info" box is toggled on for the same item, speak its description.
    private void Handle(Battle.BtlInfo* btl, int row, byte alpha, int isSelected, int drawDesc)
    {
        if (isSelected == 0 || btl == null || row < 0 || row > 64) return;
        nint idAddr = (nint)btl + 0xD2C + row * 4;
        if (!IsReadable(idAddr, 3)) return;
        ushort id = *(ushort*)idAddr;
        byte qty = *(byte*)(idAddr + 2);
        if (id == 0) return;

        // Remember the highlighted item so the use-time bubble that echoes it
        // isn't spoken twice (MessageBubble consumes this).
        Battle.PendingEchoItemId = id;
        Battle.PendingEchoItemTick = Environment.TickCount64;

        if (id == _lastId)
        {
            // The game calls this twice when the help box opens; only act on the
            // opaque pass (alpha 255), same trick as SkillSelect.
            if (alpha == 255)
            {
                if (!_lastDrawDesc && drawDesc != 0)
                {
                    string desc = Item.GetDescription(id);
                    if (!string.IsNullOrEmpty(desc)) { Log($"[ItemSelect] desc: {desc}"); Speech.Say(desc, true); }
                }
                _lastDrawDesc = drawDesc != 0;
            }
            return;
        }

        _lastId = id;
        _lastDrawDesc = drawDesc != 0;

        string name = Item.GetName(id);
        if (string.IsNullOrEmpty(name)) return;
        string msg = qty > 0 ? $"{name}, {qty}" : name;
        if (drawDesc != 0)
        {
            string desc = Item.GetDescription(id);
            if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
        }
        Log($"[ItemSelect] {msg}");
        Speech.Say(msg, true);
    }

    private delegate void DrawDelegate(Battle.BtlInfo* btl, int row, float p3, float p4,
        byte alpha, int isSelected, int drawDesc);

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
}
