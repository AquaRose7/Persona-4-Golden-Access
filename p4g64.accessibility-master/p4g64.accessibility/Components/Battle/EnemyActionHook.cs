using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Announces enemy actions as they lock in: "Lying Hablerie uses Bash" (or
/// "Lying Hablerie attacks" for the basic attack). Hooks the move/target
/// resolver FUN_14004D0A0 (battle_damage_hunt.md Item 3): it writes the chosen
/// skill id to unit+0xAE and the target to unit+0x48 once per action decision;
/// rcx = the acting unit. Read-only, call-original-first.
///
/// Why this hook: the player-turn Turn pointer (BtlInfo+0xCB8) is NULL during
/// enemy turns and enemy plain attacks emit no banner/bubble text at all (both
/// verified live 2026-06-10), so the resolver is the only reliable
/// "enemy X is acting" signal. Also publishes Battle.LastEnemyAttacker so
/// DamageMonitor can attribute party damage if this announce didn't fire.
/// </summary>
internal sealed unsafe class EnemyActionHook
{
    // Clean .text per the damage hunt; ASLR off so the VA is constant.
    private static readonly nint ResolverVA = unchecked((nint)0x14004D0A0L);

    private IHook<ResolverDelegate> _hook;
    private nint _lastActor;
    private int _lastSkill = -1;
    private long _lastTick;

    internal EnemyActionHook(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<ResolverDelegate>(Resolver, ResolverVA).Activate();
        Log("[EnemyAction] hooked move resolver @ 0x14004D0A0");
    }

    private nint Resolver(nint actor, nint a2, nint a3, nint a4, nint a5, nint a6)
    {
        nint res = _hook.OriginalFunction(actor, a2, a3, a4, a5, a6);
        try { After(actor); }
        catch (Exception ex) { Log($"[EnemyAction] error: {ex.GetType().Name}: {ex.Message}"); }
        return res;
    }

    private void After(nint actor)
    {
        if (!FieldTracker.InBattle) return;   // per-dungeon battle major (240, 241, …)
        if (!IsReadable(actor, 0xCF8)) return;
        if (*(byte*)(actor + 0xA2) != 1) return;          // enemy actions only

        int skill = *(ushort*)(actor + 0xAE);
        nint target = IsReadable(actor + 0x48, 8) ? *(nint*)(actor + 0x48) : 0;

        // The resolver can run more than once while the AI evaluates — only the
        // first lock-in of (actor, skill) within a short window speaks.
        long now = Environment.TickCount64;
        if (actor == _lastActor && skill == _lastSkill && now - _lastTick < 1500) return;
        _lastActor = actor;
        _lastSkill = skill;
        _lastTick = now;

        Battle.LastEnemyAttacker = actor;
        Battle.LastEnemyAttackerTick = now;

        string name = Battle.UnitDisplayName(actor);
        if (string.IsNullOrWhiteSpace(name)) return;

        string sname = null;
        if (skill > 0 && skill < 0x8000)
        {
            try { sname = Skill.GetName(skill); } catch { }
        }
        string tname = target != 0 ? Battle.UnitDisplayName(target) : null;

        string msg = string.IsNullOrWhiteSpace(sname) || sname == "Attack"
            ? $"{name} attacks"
            : $"{name} uses {sname}";
        Log($"[EnemyAction] actor=0x{actor:X} skill={skill}(\"{sname}\") target=0x{target:X}(\"{tname}\") -> \"{msg}\"");
        Speech.Say(msg, true);
    }

    private delegate nint ResolverDelegate(nint a1, nint a2, nint a3, nint a4, nint a5, nint a6);

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
