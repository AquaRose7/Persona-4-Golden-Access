using System.Runtime.InteropServices;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Hooks the two F-persona-panel renderers read-only to capture WHICH persona the
/// panel is drawing — at render time, when the pointer chain is valid (polling the
/// globals caught flicker/garbage). Both renderers are MS-x64 with NO float args
/// (the xmm6/7 saves are callee-saved), arg1 (rdx) = BtlInfo:
///
///   Ally status  0x1400EA980 : persona = *(u16)(*(*(info+0xCB8)+0x38)+0xA4)  (acting unit)
///   MC detail    0x1400EC280 : persona = *(u16)(*(info+0xD10)+0xA4)          (shown unit)
///
/// A renderer running = the panel is open, so <see cref="Battle.FPanelOpen"/>
/// becomes a reliable gate for PersonaNav. See database/ghidra/persona_renderer_sig.md.
/// </summary>
internal sealed unsafe class PersonaPanelHook
{
    private IHook<RenderDelegate> _allyHook, _mcHook;
    private IHook<FullDelegate> _fullHook;
    private nint _lastLogUnit;
    private int _lastFullKey = int.MinValue;

    private void Note(string which, nint unit)
    {
        if (unit == _lastLogUnit) return;
        _lastLogUnit = unit;
        Log($"[PersonaPanelHook] {which} render unit=0x{unit:X} side={Battle.UnitSide(unit)} a4={CandU(unit, 0xA4)}");
        // Diagnostic: the equipped persona id lives in the STAT NODE (FUN_1400d35b0
        // reads statNode+0x02 / +0x04 as the species index). Log the candidates with
        // their names so we can see which one resolves to e.g. "Jiraiya" for Yosuke.
        if (IsReadable(unit + 0xCF0, 8))
        {
            nint stat = *(nint*)((byte*)unit + 0xCF0);
            Log($"[PersonaPanelHook]   stat=0x{stat:X} s02={CandN(stat, 0x02)} s04={CandN(stat, 0x04)} s06={CandN(stat, 0x06)}");
        }
    }

    private static string CandU(nint baseAddr, int off)
    {
        if (!IsReadable(baseAddr + off, 2)) return "?";
        return ((ushort)*(short*)((byte*)baseAddr + off)).ToString();
    }

    private static string CandN(nint baseAddr, int off)
    {
        if (!IsReadable(baseAddr + off, 2)) return "?";
        int id = (ushort)*(short*)((byte*)baseAddr + off);
        string nm; try { nm = Persona.GetName(id); } catch { nm = "<err>"; }
        return $"{id}(\"{nm}\")";
    }

    internal PersonaPanelHook(IReloadedHooks hooks)
    {
        SigScan("48 8B C4 48 89 58 08 48 89 68 18 56 57 41 56 48 81 EC C0 00 00 00 0F 29 70 D8 48 8B E9 0F 29 78 C8 48",
            "PersonaAllyRenderer", a => { _allyHook = hooks.CreateHook<RenderDelegate>(AllyRender, a).Activate(); Log($"[PersonaPanelHook] ally renderer hooked @ 0x{a:X} (want 0x1400EA980)"); });
        SigScan("48 8B C4 48 89 58 08 48 89 68 18 56 57 41 56 48 81 EC D0 00 00 00 0F 29 70 D8 4C 8B F1 0F 29 78 C8 48",
            "PersonaMcRenderer", a => { _mcHook = hooks.CreateHook<RenderDelegate>(McRender, a).Activate(); Log($"[PersonaPanelHook] mc renderer hooked @ 0x{a:X} (want 0x1400EC280)"); });
        SigScan("48 8B C4 48 81 EC D8 00 00 00 48 89 58 18 48 89 68 F8 48 89 70 F0 48 89 78 E8 4C 89 60 E0",
            "PersonaFullPanel", a => { _fullHook = hooks.CreateHook<FullDelegate>(FullRender, a).Activate(); Log($"[PersonaPanelHook] full panel hooked @ 0x{a:X} (want 0x1400E6900)"); });
    }

    private void AllyRender(nint rcx, nint info, nint pos, nint r9)
    {
        _allyHook.OriginalFunction(rcx, info, pos, r9);
        try
        {
            // The panel's data is keyed on the ACTING unit's stat node (decompiled:
            // statNode = *(*(*(BtlInfo+0xCB8)+0x38)+0xCF0)). Capture that unit.
            nint unit = ActingUnit(info);
            if (unit != 0) { Battle.SetFPanelUnit(unit); Note("ally", unit); }
        }
        catch { }
    }

    private void McRender(nint rcx, nint info, nint pos, nint r9)
    {
        _mcHook.OriginalFunction(rcx, info, pos, r9);
        try
        {
            nint unit = ActingUnit(info);
            if (unit != 0) { Battle.SetFPanelUnit(unit); Note("mc", unit); }
        }
        catch { }
    }

    // FUN_1400e6900(BtlInfo rcx, int index edx, float, float, byte alpha, int p6, int p7).
    // Persona for row `index` = *(short*)(*(BtlInfo+0xCE0 + index*8) + 0xA4).
    private void FullRender(nint btl, int index, float p3, float p4, byte alpha, int p6, int p7)
    {
        _fullHook.OriginalFunction(btl, index, p3, p4, alpha, p6, p7);
        try
        {
            if (index < 0 || index > 16 || !IsReadable(btl + 0xCE0 + index * 8, 8)) return;
            nint e = *(nint*)((byte*)btl + 0xCE0 + index * 8);
            if (e == 0 || !IsReadable(e + 0xA4, 2)) return;
            int pid = *(ushort*)((byte*)e + 0xA4);
            int key = index * 100000 + pid;
            if (key == _lastFullKey) return;
            _lastFullKey = key;
            Log($"[PersonaPanelHook] FULL idx={index} pid={pid}(\"{Persona.GetName(pid)}\") p6={p6} alpha={alpha}");
        }
        catch { }
    }

    private delegate void FullDelegate(nint btl, int index, float p3, float p4, byte alpha, int p6, int p7);

    private static nint ActingUnit(nint info)
    {
        if (!IsReadable(info + 0xCB8, 8)) return 0;
        nint turn = *(nint*)((byte*)info + 0xCB8);
        if (!IsReadable(turn + 0x38, 8)) return 0;
        return *(nint*)((byte*)turn + 0x38);
    }

    private delegate void RenderDelegate(nint rcx, nint info, nint pos, nint r9);

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
