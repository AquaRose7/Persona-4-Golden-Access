using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the "replace a skill" screen — when a persona is about to learn a skill
/// but its 8 slots are full, you pick which existing skill to forget. Appears
/// from level-up, fusion, and skill-card learning. Reverse-engineered 2026-06-27
/// (snapshot found the cursor; CE find-what-accesses → render FUN_140428D80;
/// Ghidra gave the field layout).
///
/// Hooks FUN_140428D80 — the per-ROW skill-button drawer (called once per row each
/// frame). Its 4th arg (param_4 / r9) is the menu struct:
///   +0x04  byte   cursor (0..count-1 = a current skill; == count = decline/new-skill slot)
///   +0x68  u16    count of current skills (8 when full)
///   +0x0A + i*0xC  u16  current skill id for row i  (-> Skill.GetName/GetDescription)
///   +0x232 u16    the NEW skill being learned
/// We ignore which row is being drawn; just read the cursor + its skill, deduped.
/// </summary>
internal unsafe class SkillReplaceMenu : IDisposable
{
    private IHook<RowDelegate>? _hook;
    private nint _lastMenu;
    private int  _lastCursor = -1;

    // Set every frame this overlay renders. PlayerMenu reads it to stay quiet while the
    // replace screen is up (otherwise its camp Item poll bleeds a stale "name. HP. SP." through).
    private static long _lastActiveMs = -10000;
    internal static bool RecentlyActive => Environment.TickCount64 - _lastActiveMs < 300;

    // FUN_140428D80(p1, p2, p3, menu, p5, p6, p7, p8) — only param_4 (menu) matters.
    private delegate void RowDelegate(nint p1, int p2, byte p3, nint menu, byte p5,
                                      float p6, float p7, int p8);

    internal SkillReplaceMenu(IReloadedHooks hooks)
    {
        SigScan(
            "48 8B C4 44 88 40 18 53 55 57 41 56 48 81 EC 38 01 00 00 0F 29 70 B8 0F 29 78 A8",
            "SkillReplace::RowRender",
            address =>
            {
                _hook = hooks.CreateHook<RowDelegate>(OnRow, address).Activate();
                Log("Skill-replace reader hook active.");
            });
    }

    private void OnRow(nint p1, int p2, byte p3, nint menu, byte p5, float p6, float p7, int p8)
    {
        _hook!.OriginalFunction(p1, p2, p3, menu, p5, p6, p7, p8);
        try { Read(menu); } catch { /* never let a hook throw */ }
    }

    private void Read(nint menu)
    {
        if (!IsReadable(menu + 0x68, 2)) return;
        int count = *(short*)(menu + 0x68);
        int cursor = *(byte*)(menu + 0x04);
        if (count < 1 || count > 16 || cursor < 0 || cursor > count) return;
        _lastActiveMs = Environment.TickCount64;   // overlay is up → mute the camp poll

        bool freshMenu = menu != _lastMenu;
        if (freshMenu) { _lastMenu = menu; _lastCursor = -1; }
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        // The new skill being learned (for context on open + the decline slot).
        int newId = IsReadable(menu + 0x232, 2) ? *(short*)(menu + 0x232) : 0;
        string newName = (newId >= 1 && newId <= 1024) ? Skill.GetName(newId) : "";

        string body;
        if (cursor < count)
        {
            if (!IsReadable(menu + 0x0A + cursor * 0xC, 2)) return;
            int id = *(short*)(menu + 0x0A + cursor * 0xC);
            string nm = (id >= 1 && id <= 1024) ? Skill.GetName(id) : $"Skill {cursor + 1}";
            string desc = (id >= 1 && id <= 1024) ? Skill.GetDescription(id) : "";
            body = string.IsNullOrEmpty(desc)
                ? $"{nm}. {cursor + 1} of {count}"
                : $"{nm}. {desc}. {cursor + 1} of {count}";
        }
        else
        {
            // cursor == count: the "?" slot = decline learning the new skill.
            body = string.IsNullOrEmpty(newName)
                ? "Do not learn the new skill."
                : $"Do not learn {newName}.";
        }

        // On open, lead with what's being learned so the player has context.
        string lead = (freshMenu && !string.IsNullOrEmpty(newName))
            ? $"Replace a skill. Learning {newName}. " : "";
        Log($"[SkillReplace] cursor={cursor}/{count} newId={newId} '{newName}'");
        Speech.Say(lead + body, interrupt: true);
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
