using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Speaks the AoE breakdown when you confirm an ALL-target skill into the target step — the
/// multi-target equivalent of the single-target "name + weakness" read. Single-target skills get a
/// per-enemy cursor (handled by BattleLog); all-target skills have NO cursor, so nothing fired
/// before. Here we announce "All enemies, 2 weak, 1 repel" (discovered affinities only) once the
/// skill list has closed on an all-skill.
///
/// Trigger heuristic: <see cref="Battle.SelectedSkillId"/> is an all-skill (scope==1) while
/// <see cref="Battle.CurrentCommand"/>==4 (Skill) and the skill list is no longer drawing
/// (<see cref="Battle.PendingEchoSkillTick"/> stale) — i.e. you've confirmed into targeting.
/// </summary>
internal sealed class MultiTargetReader
{
    private const long ListClosedMs = 250;
    private bool _announced;
    private bool _listWasOpen;   // the skill list was actually opened this cycle (a real confirm, not a stale id)

    internal MultiTargetReader()
    {
        var t = new Thread(Poll) { IsBackground = true, Name = "MultiTargetReader" };
        t.Start();
        Log("[MultiTargetReader] ready");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(50);
            try { Tick(); }
            catch (Exception ex) { Log($"[MultiTargetReader] {ex.Message}"); Thread.Sleep(500); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus() || Battle.ActiveBtlInfo == 0) { _announced = false; _listWasOpen = false; return; }

        // Leaving the Skill command (ring moved elsewhere) clears everything — a later return to Skill
        // must re-open the list to count.
        if (Battle.CurrentCommand != 4) { _announced = false; _listWasOpen = false; return; }

        bool listOpen = Environment.TickCount64 - Battle.PendingEchoSkillTick < ListClosedMs;
        if (listOpen) { _listWasOpen = true; _announced = false; return; }   // in the skill list — picking

        // List closed while still on Skill: a real confirm into target-select ONLY if the list was
        // actually open this cycle. Just hovering "Skill" in the ring with a stale all-skill id does
        // NOT count (that was the false "all enemies" on every hover).
        int sid = Battle.SelectedSkillId;
        if (!_listWasOpen || sid < 0 || SafeScope(sid) != 1) return;
        if (_announced) return;
        _announced = true;

        int elem = SafeElement(sid);
        string msg;
        if (elem == (int)Skill.ElementalType.Healing) msg = "All allies.";
        else if (elem >= 0 && elem != 5 && elem <= 7)
        {
            string aoe = Battle.AoeAffinitySummary(elem);
            msg = aoe != null ? $"All enemies. {aoe}." : "All enemies.";
        }
        else if (elem >= 0 && elem <= 15) msg = "All enemies.";   // ailment offensive
        else msg = "All.";                                         // ambiguous support
        Speech.Say(msg);
        Log($"[MultiTargetReader] skill={sid} elem={elem} -> \"{msg}\"");
    }

    private static int SafeScope(int sid) { try { return Skill.GetTargetScope(sid); } catch { return -1; } }
    private static int SafeElement(int sid) { try { return (int)Skill.GetSkillElement(sid); } catch { return -1; } }
}
