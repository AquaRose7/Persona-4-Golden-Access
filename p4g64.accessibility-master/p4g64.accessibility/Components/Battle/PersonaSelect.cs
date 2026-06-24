using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Speaks the highlighted persona in the in-battle PERSONA-change menu (the MC's
/// stock) as the cursor moves, mirroring <see cref="SkillSelect"/>. Per-row draw
/// function @ 0x1400E65B0. The MC persona stock is the same array the mod already
/// models (<see cref="Persona.PersonaInfo"/>, stride 0x30) at
/// <c>*(0x141165900) + 0xA34</c>; row N's entry is <c>arr[N]</c>. If
/// <c>Registered</c>, its <c>PersonaId</c> → <see cref="Persona.GetName"/>.
/// <c>isSelected != 0</c> marks the cursor row.
/// </summary>
internal sealed unsafe class PersonaSelect
{
    private static readonly nint StockGlobal = unchecked((nint)0x141165900L);

    private IHook<DrawDelegate> _hook;
    private int _lastId = -1;

    internal PersonaSelect(IReloadedHooks hooks)
    {
        SigScan("48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 56 48 81 EC 90 00 00",
            "BtlPersonaDraw", a => _hook = hooks.CreateHook<DrawDelegate>(Draw, a).Activate());
    }

    private void Draw(Battle.BtlInfo* btl, int row, float p3, float p4, byte alpha, int isSelected)
    {
        _hook.OriginalFunction(btl, row, p3, p4, alpha, isSelected);

        if (isSelected == 0 || row < 0 || row > 12) return;
        if (!IsReadable(StockGlobal, 8)) return;
        nint baseObj = *(nint*)StockGlobal;
        if (baseObj == 0) return;

        var entry = (Persona.PersonaInfo*)(baseObj + 0xA34) + row;
        if (!IsReadable((nint)entry, 0x30) || !entry->Registered) return;

        int id = entry->PersonaId;
        // Publish the highlighted entry so PersonaNav can read its full profile.
        Battle.LastPersonaEntry = (nint)entry;
        Battle.LastPersonaId = id;
        Battle.LastPersonaTick = Environment.TickCount64;
        // This fires only for the PERSONA submenu, and the generic menu update
        // (FUN_1400edf80) already ran this frame — so PersonaMenuObj is the persona
        // menu object right now. Latch it to isolate the persona panel from Analyze.
        Battle.PersonaMenuObjForPersona = Battle.PersonaMenuObj;
        Battle.PersonaEntered = true;   // the submenu rendered → a real entry (not ring hover)
        if (id == _lastId) return;
        _lastId = id;

        string name = Persona.GetName(id);
        if (string.IsNullOrEmpty(name)) return;
        int lvl = entry->Level;
        string spoken = $"{name}, level {lvl}";   // submenu shows persona + level
        Log($"[PersonaSelect] {spoken}");
        Speech.Say(spoken, true);
    }

    private delegate void DrawDelegate(Battle.BtlInfo* btl, int row, float p3, float p4,
        byte alpha, int isSelected);

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
