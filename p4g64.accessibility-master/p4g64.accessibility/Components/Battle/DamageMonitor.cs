using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Speaks combat damage and healing by watching every combatant's current HP
/// (statNode+0x08) each tick and announcing the change:
///
///   <c>"Lying Hablerie took 34. Calm Pesce took 30."</c>   (your hits, AoE batched)
///   <c>"haru took 22."</c>                                  (damage to your party)
///   <c>"Yosuke recovered 40."</c>                           (healing)
///   <c>"Lying Hablerie took 51, defeated."</c>              (killing blow)
///
/// This needs no damage-subsystem hook — HP itself is the source of truth, so it
/// covers damage dealt AND received uniformly. The exact affinity result
/// (Weak / Resist / Null / Repel / Critical) is a separate sprite/enum and is
/// being added via a dedicated RE pass; when ready it will prefix these lines.
///
/// Event-driven (only speaks on an HP change), so it isn't a continuous ambient
/// cue. Battle only (major 240); state resets when battle ends.
/// </summary>
internal sealed unsafe class DamageMonitor
{
    private const int PollMs = 40;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private readonly Dictionary<nint, (int hp, bool down, uint status)> _last = new();

    // Attacker attribution: the Turn pointer is NULL during enemy turns (verified
    // live — it only tracks player command turns), so the attacker comes from
    // Battle.LastEnemyBannerActor: the enemy unit on the most recent info-window
    // banner (BattleLog records it even when the banner speech is deduped).
    private const long AttributeWindowMs = 4000;

    // Enemy action announce by POLLING unit+0xAE (skill id) / unit+0x48 (target)
    // — the canonical action fields from battle_damage_hunt.md Item 3. The
    // resolver hook 0x14004D0A0 never fired in live battles (verified
    // 2026-06-10), but whatever protected code drives enemy turns must still
    // write these fields; a transition to a valid skill = "this enemy is acting".
    private readonly Dictionary<nint, (int skill, nint target)> _lastAction = new();

    public DamageMonitor()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "DamageMonitor" };
        _thread.Start();
        Log("[DamageMonitor] ready (announces HP changes in battle)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log($"[DamageMonitor] error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Tick()
    {
        if (!FieldTracker.InBattle)   // battle major is per-dungeon (240, 241, …), not just 240
        {
            if (_last.Count > 0) _last.Clear();
            if (_lastAction.Count > 0) _lastAction.Clear();
            Battle.LastTargetedEnemy = 0;
            Battle.CurrentCommand = -1;
            Battle.LastEnemyBannerActor = 0;
            Battle.LastEnemyAttacker = 0;
            Battle.PendingHpCost = 0;
            return;
        }

        // A pending HP cost is only valid while the Skill command is still the
        // active flow (selection → targeting → cast all keep cmd 4). Backing
        // out to the ring and picking Guard left the latch armed for 8s — an
        // enemy hit on the would-be caster for exactly the cost then spoke as
        // "spent N HP" (live bug 2026-06-10).
        if (Battle.CurrentCommand != 4 && Battle.PendingHpCost != 0)
            Battle.PendingHpCost = 0;

        var units = Battle.EnumerateUnits();
        if (units.Count == 0) return;

        var present = new HashSet<nint>();
        var msgs = new List<string>();
        var damagedParty = new List<nint>();
        bool partyDamaged = false;

        foreach (var (unit, side, stat) in units)
        {
            if (!IsReadable(stat, 0x10)) continue;
            int hp = *(ushort*)((byte*)stat + 0x08);
            uint status = *(uint*)((byte*)stat + 0x0C);
            bool down = (status & Battle.StatusDown) != 0;
            present.Add(unit);

            // Enemy action watch: announce "X uses Y" when a living enemy's
            // action fields transition. First sighting is a silent baseline.
            if (side == 1 && hp > 0)
            {
                int skill = *(ushort*)((byte*)unit + 0xAE);
                nint tgt = *(nint*)((byte*)unit + 0x48);
                if (!_lastAction.TryGetValue(unit, out var pa))
                    _lastAction[unit] = (skill, tgt);
                else if (skill != pa.skill || tgt != pa.target)
                {
                    _lastAction[unit] = (skill, tgt);
                    if (skill > 0 && skill < 0x8000) AnnounceEnemyAction(unit, skill, tgt);
                }
            }

            if (!_last.TryGetValue(unit, out var prev))
            {
                _last[unit] = (hp, down, status); // first sighting — baseline, don't announce
                continue;
            }

            // Ailment-bit mapping: log every status-word change with the unit
            // name — the infliction banner that speaks right around it names
            // the ailment, which is how the bit table gets filled in.
            if (status != prev.status)
                Log($"[Status] {Battle.UnitDisplayName(unit) ?? "?"} 0x{prev.status:X} -> 0x{status:X}");

            if (hp == prev.hp && down == prev.down && status == prev.status) continue;
            _last[unit] = (hp, down, status);

            var parts = new List<string>();
            if (hp != prev.hp)
            {
                int delta = prev.hp - hp;
                // A party member losing EXACTLY the pending HP cost of the skill
                // they just picked = the skill's self-cost, not an enemy hit.
                bool isCost = side == 0 && delta > 0 && hp > 0
                              && unit == Battle.PendingHpCostUnit
                              && Battle.PendingHpCost > 0 && delta == Battle.PendingHpCost
                              && Environment.TickCount64 - Battle.PendingHpCostTick < 8000;
                if (isCost)
                {
                    Battle.PendingHpCost = 0;   // consume — one cast, one cost
                    parts.Add($"spent {delta} HP");
                }
                else if (delta > 0)
                {
                    parts.Add(hp == 0 ? $"took {delta} damage, defeated" : $"took {delta} damage");
                    if (side == 0) { partyDamaged = true; damagedParty.Add(unit); }
                }
                else parts.Add($"recovered {-delta} HP");
            }
            // A weakness or critical hit knocks the target down (→ "1 More") — the
            // real-time "you hit a weakness" cue.
            if (down && !prev.down) parts.Add("knocked down");
            // An enemy whose dead-bit sets while its HP is still positive FLED
            // (seen live: a feared Pesce left at full HP, silently).
            if (side == 1 && hp > 0
                && (status & Battle.StatusDead) != 0 && (prev.status & Battle.StatusDead) == 0)
                parts.Add("fled");
            if (parts.Count == 0) continue;

            string name = Battle.UnitDisplayName(unit) ?? (side == 1 ? "Enemy" : "Ally");
            msgs.Add($"{name} {string.Join(", ", parts)}");
        }

        // Forget units that left the fight so a future battle re-baselines them.
        if (present.Count != _last.Count)
        {
            var gone = new List<nint>();
            foreach (var k in _last.Keys) if (!present.Contains(k)) gone.Add(k);
            foreach (var k in gone) { _last.Remove(k); _lastAction.Remove(k); }
        }

        if (msgs.Count > 0)
        {
            string line = string.Join(". ", msgs);

            // Attacker attribution — ONLY for enemies attacking the party, and
            // ONLY while it's actually an enemy acting: the Turn pointer is
            // party-side during the player's own action, which is when HP-cost
            // skills (Cleave etc.) drop the actor's HP — those must never be
            // blamed on an enemy (live bug 2026-06-10). Turn is null on enemy
            // turns, so the gate lets real enemy hits through.
            // Fallbacks: action-transition attacker → enemy targeting a victim →
            // the only living enemy → banner actor.
            nint acting = Battle.ActingUnit();
            bool playerActing = acting != 0 && Battle.UnitSide(acting) == 0;
            if (partyDamaged && !playerActing)
            {
                long now = Environment.TickCount64;
                bool lockinFresh = Battle.LastEnemyAttacker != 0
                                   && now - Battle.LastEnemyAttackerTick < 6000;
                if (!lockinFresh)
                {
                    nint atk = 0;
                    nint onlyLiving = 0;
                    int livingEnemies = 0;
                    foreach (var (u, s, st) in units)
                    {
                        if (s != 1 || !IsReadable(st, 0x10) || *(ushort*)((byte*)st + 0x08) == 0) continue;
                        livingEnemies++;
                        onlyLiving = u;
                        nint tgt = *(nint*)((byte*)u + 0x48);
                        if (atk == 0 && damagedParty.Contains(tgt)) atk = u;
                    }
                    if (atk == 0 && livingEnemies == 1) atk = onlyLiving;
                    if (atk == 0 && now - Battle.LastEnemyBannerTick < AttributeWindowMs)
                        atk = Battle.LastEnemyBannerActor;
                    string an = atk != 0 && Battle.UnitSide(atk) == 1 ? Battle.UnitDisplayName(atk) : null;
                    if (!string.IsNullOrWhiteSpace(an)) line = $"{an} attacks. {line}";
                    else Log($"[DamageMonitor] party damage, no attacker (banner=0x{Battle.LastEnemyBannerActor:X})");
                }
            }

            Log($"[DamageMonitor] {line}");
            // Queue, don't interrupt: the game's skill-name bubble ("enemy uses
            // Agi") often speaks right before the damage lands — interrupting
            // here cut those names off (user report 2026-06-10).
            Speech.Say(line, false);
        }
    }

    /// <summary>Speak an enemy's freshly-locked action: "X uses Y" (named skill)
    /// or "X attacks" (basic attack / unnamed). Also publishes the attacker so
    /// the damage line that follows doesn't repeat the name.</summary>
    private void AnnounceEnemyAction(nint unit, int skill, nint target)
    {
        Battle.LastEnemyAttacker = unit;
        Battle.LastEnemyAttackerTick = Environment.TickCount64;

        string name = Battle.UnitDisplayName(unit);
        if (string.IsNullOrWhiteSpace(name)) return;
        string sname = null;
        try { sname = Skill.GetName(skill); } catch { }
        string tname = target != 0 && IsReadable(target, 0xCF8) ? Battle.UnitDisplayName(target) : null;

        string msg = string.IsNullOrWhiteSpace(sname) || sname == "Attack"
            ? $"{name} attacks"
            : $"{name} uses {sname}";
        Log($"[EnemyAction] poll: unit=0x{unit:X} skill={skill}(\"{sname}\") target=0x{target:X}(\"{tname}\") -> \"{msg}\"");
        Speech.Say(msg, false);
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
        uint state = *(uint*)(buf + 32);
        uint protect = *(uint*)(buf + 36);
        if (state != 0x1000) return false;
        if ((protect & 0x01) != 0) return false;
        if ((protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }
}
