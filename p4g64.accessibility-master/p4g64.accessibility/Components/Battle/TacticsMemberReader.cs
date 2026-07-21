using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// The TACTICS member-selection cursor (2026-07-11 — the screen the June hunts
/// declared unreadable: "the cursor exists only as render state"). It does: the
/// member rows are drawn by FUN_1400e6900 with a per-call <c>p6 = selected</c>
/// argument, which <see cref="PersonaPanelHook"/> publishes as
/// <see cref="Battle.TacticsSelUnit"/>/<see cref="Battle.TacticsSelTick"/> (+
/// <see cref="Battle.TacticsRowTick"/> = rows are drawing at all). This reader
/// speaks the highlighted row while the Tactics command (1) is active:
/// a fresh p6==1 row → that member's name; rows drawing but NO selected row →
/// "All members" (the top row carries no unit). The options-entry header
/// ("Yosuke, now Act Freely") still confirms every pick as before.
/// </summary>
internal sealed class TacticsMemberReader
{
    private const int PollMs = 60;
    private const int FreshMs = 200;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private string _last;

    public TacticsMemberReader()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "TacticsMemberReader" };
        _thread.Start();
        Log("[TacticsMember] ready (member-row p6 cursor)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[TacticsMember] error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        bool screen = FieldTracker.InBattle
                      && Battle.CurrentCommand == 1                    // Tactics
                      && now - Battle.TacticsRowTick < FreshMs;        // member rows drawing
        if (!screen)
        {
            _last = null;   // re-announce on next entry
            return;
        }
        if (!Utils.GameHasFocus()) return;

        string cur;
        if (now - Battle.TacticsSelTick < FreshMs)
        {
            nint unit = Battle.TacticsSelUnit;
            string name = null;
            try { name = Battle.UnitName(unit); } catch { }
            cur = string.IsNullOrEmpty(name) ? "Member" : name;
        }
        else
        {
            cur = "All members";   // rows drawing, none selected = the top row
        }

        if (cur == _last) return;
        _last = cur;
        Speech.Say(cur, true);
        Log($"[TacticsMember] -> {cur}");
    }
}
