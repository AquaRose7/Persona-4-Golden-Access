using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Row reader for the drawer at <c>0x1400E7020</c> — which turned out to be the
/// TACTICS menu drawer (2026-06-10: it never fired for the persona panel, but
/// fired per-row in Tactics, reading wrong skill names). It draws BOTH tactics
/// screens; the widget word at <c>menuObj+0x04</c> distinguishes them
/// (bit 0x10 set = member screen, clear = options screen, from the
/// TacticsProbe captures):
///   member screen rows — "Everyone", then each non-MC party member
///   options screen rows — the ORDER_SEL tactics in canonical order
/// Outside Tactics it falls back to reading the rows as skills from
/// <c>BtlInfo+0xD1A</c> (its original assumed role; harmless if it ever fires).
/// </summary>
internal sealed unsafe class SkillInfoSelect
{
    private IHook<DrawDelegate> _hook;
    private int _lastSkillId = -1;
    private long _lastDrawTick;
    private int _lastTacticRow = -1;

    internal SkillInfoSelect(IReloadedHooks hooks)
    {
        SigScan("48 8B C4 48 89 58 08 48 89 68 10 56 57 41 55 41 56 41 57 48 81 EC 90 00 00 00 44 0F B6 B4 24 E0 00 00",
            "PersonaSkillRow", a => { _hook = hooks.CreateHook<DrawDelegate>(Draw, a).Activate(); Log($"[SkillInfo] skill-row hooked @ 0x{a:X}"); });
    }

    private void Draw(nint rcx, int row, float p3, float p4, byte alpha, int isSelected)
    {
        _hook.OriginalFunction(rcx, row, p3, p4, alpha, isSelected);
        if (isSelected == 0 || row < 0 || row > 8) return;

        if (Battle.CurrentCommand == 1)   // Command.Tactics
        {
            HandleTactics(row);
            return;
        }
        _lastTacticRow = -1;

        // Non-tactics: a gap in draws means the list closed — re-arm so
        // reopening on the same row re-reads.
        long now = Environment.TickCount64;
        if (now - _lastDrawTick > 600) _lastSkillId = -1;
        _lastDrawTick = now;

        nint info = Battle.ActiveBtlInfo;
        nint addr = info + 0xD1A + row * 2; // ActiveMemberSkills[row]
        if (!IsReadable(addr, 2)) return;
        short skillId = *(short*)addr;
        if (skillId <= 0 || skillId == _lastSkillId) return;
        _lastSkillId = skillId;

        string name = Skill.GetName(skillId);
        if (string.IsNullOrEmpty(name)) return;
        // Name + the panel's Help text (the description box the cursor drives).
        string desc = null;
        try { desc = Skill.GetDescription(skillId); } catch { }
        string msg = string.IsNullOrEmpty(desc) ? name : $"{name}. {desc}";
        Log($"[SkillInfo] row {row} skill {skillId} -> {msg}");
        Speech.Say(msg, true);
    }

    private void HandleTactics(int row)
    {
        // This drawer renders ONLY the tactics OPTIONS list. The member screen
        // has no readable cursor (probed twice) — instead, entering the options
        // announces WHO they're for and their current tactic (BtlInfo+0xD10/18
        // = the chosen member; current tactic byte = party record+0x10, both
        // from the FUN_1400EC280 decompile).
        long now = Environment.TickCount64;
        if (now - _lastDrawTick > 600) _lastTacticRow = -1;   // list closed — re-arm
        _lastDrawTick = now;

        if (row == _lastTacticRow) return;
        bool entering = _lastTacticRow < 0;
        _lastTacticRow = row;

        string text = row >= 0 && row < Battle.TacticNames.Length
            ? Battle.TacticNames[row] : $"option {row + 1}";
        if (entering) text = $"{TacticsHeader()}. {text}";
        Log($"[Tactics] options row {row} -> \"{text}\"");
        Speech.Say(text, true);
    }

    /// <summary>Whose tactics the options screen is editing + their current
    /// tactic, e.g. "Yosuke, now Act Freely" — or "Everyone".</summary>
    private static string TacticsHeader()
    {
        nint info = Battle.ActiveBtlInfo;
        if (IsReadable(info + 0xD18, 2) && *(ushort*)(info + 0xD18) != 0
            && IsReadable(info + 0xD10, 8))
        {
            nint unit = *(nint*)(info + 0xD10);
            string n = Battle.UnitDisplayName(unit);
            int slot = IsReadable(unit + 0xA4, 2) ? *(ushort*)(unit + 0xA4) : 0;
            if (slot >= 1)
            {
                nint rec = unchecked((nint)0x1451BD9E4L) + (slot - 1) * 0x84;
                string t = Battle.MemberTacticName(rec);
                if (t != null && !string.IsNullOrEmpty(n))
                    return $"{n}, now {t}";
            }
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return "All Members";   // the game's own label for the party-wide row
    }

    private delegate void DrawDelegate(nint rcx, int row, float p3, float p4, byte alpha, int isSelected);

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
