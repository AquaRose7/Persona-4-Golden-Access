using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Native.Text.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Speaks the battle bottom-banner "play-by-play" — the <c>jyokyohelp</c> info
/// window that carries: knockdown / Down, status ailments (Poison, Sleep, …),
/// enemy-turn flavour, "No effect!", "Missed!", "withstood the attack!", and
/// buff/debuff messages. None of these came through the existing
/// <see cref="MessageBubble"/> (that only carries the analysis / target-name
/// bubble), which is why the whole combat feed was silent in the 2026-06-04
/// audit.
///
/// Hook: <c>Btl::UI::InfoWindowProc</c> @ 0x1400EEBC0 (per-frame, single arg in
/// rcx), found by the battle-message RE hunt — see
/// <c>database/ghidra/battle_message_hunt.md</c>. Read-only, call-original-first
/// — the same safe shape as <see cref="MessageBubble"/>; we never modify the
/// function. Sig verified unique by capstone.
///
/// Layout (from the hunt; the spoken-text field is the one piece that was only
/// MEDIUM-confidence statically, hence this also logs the actor pointer for a
/// one-battle confirmation):
///   win + 0x02 (u16)  == 0 when a message is shown (gate)
///   win + 0x28        -> subA ; subA + 0x48 -> S (text sub-object)
///   S + 0x5558        -> TextStruct*  (freed via the same 0x14044FE90 the
///                        working bubble's text uses — strong signal it's right)
///   S + 0x5580        -> BtlUnitInfo* (the actor the line is about)
///
/// NOT covered here: Weak/Resist/Null/Repel/Drain/Critical/Miss, "1 More!",
/// All-Out Attack, and damage numbers — those are sprites, not text, and need a
/// separate damage-subsystem pass.
/// </summary>
internal sealed unsafe class BattleLog
{
    private const int OFF_WIN_FLAG = 0x02;
    private const int OFF_WIN_SUB = 0x28;
    private const int OFF_S_TEXT = 0x5558;
    private const int OFF_S_ACTOR = 0x5580;

    private IHook<InfoWindowProcDelegate> _hook;
    private nint _lastActor;
    private string _lastSpoken;
    // Time-based de-dup: during a hit/down the info window flashes enemy banners back
    // and forth (A/B/A/B), and the actor alternates so the (actor,text) guard misses
    // it. Suppress re-speaking the SAME line within this window. Deliberate target
    // moves are slower / change content, so they still come through.
    private readonly Dictionary<string, long> _recent = new();
    private const long RepeatMs = 700;

    internal BattleLog(IReloadedHooks hooks)
    {
        SigScan("40 53 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 66 83 7B 02 00",
            "Btl::UI::InfoWindowProc",
            address => _hook = hooks.CreateHook<InfoWindowProcDelegate>(InfoWindowProc, address).Activate());
    }

    private void InfoWindowProc(nint win)
    {
        _hook.OriginalFunction(win);
        try
        {
            Read(win);
        }
        catch (Exception ex)
        {
            Log($"[BattleLog] read error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Read(nint win)
    {
        if (!IsReadable(win, 0x30)) return;

        // Only a shown message has flag==0; reset de-dup when it hides so the
        // same thing shown again later re-announces.
        ushort flag = *(ushort*)(win + OFF_WIN_FLAG);
        if (flag != 0) { _lastActor = 0; _lastSpoken = null; _recent.Clear(); Battle.LastTargetedEnemy = 0; return; }

        nint subA = *(nint*)(win + OFF_WIN_SUB);
        if (!IsReadable(subA, 0x50)) return;
        nint s = *(nint*)(subA + 0x48);
        if (!IsReadable(s + OFF_S_TEXT, 8)) return;

        nint textPtr = *(nint*)(s + OFF_S_TEXT);
        if (textPtr == 0 || !IsReadable(textPtr, 0x48)) return;

        string text = ((TextStruct*)textPtr)->ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        nint actor = IsReadable(s + OFF_S_ACTOR, 8) ? *(nint*)(s + OFF_S_ACTOR) : 0;

        // Attribution source: ANY banner event about an enemy-side unit marks it
        // as the most recent enemy actor — enemy attack banners flow through here
        // even when speech is deduped, and the player-turn Turn pointer is NULL
        // during enemy turns (verified live), so this is the attacker signal
        // DamageMonitor uses to name who hit the party.
        if (actor != 0 && Battle.UnitSide(actor) == 1)
        {
            if (actor != Battle.LastEnemyBannerActor)
                Log($"[BattleLog] enemy banner actor=0x{actor:X} \"{Battle.UnitDisplayName(actor)}\"");
            Battle.LastEnemyBannerActor = actor;
            Battle.LastEnemyBannerTick = Environment.TickCount64;
        }

        // De-dup on (actor, text): moving the target cursor to a different enemy
        // changes the actor pointer even when the displayed name is identical, so
        // each target move re-announces (the old text-pointer de-dup went silent
        // on same-named enemies).
        if (actor == _lastActor && text == _lastSpoken) return;

        // If the window is just showing a unit's NAME (target cursor / info), say
        // the name + HP percentage. Otherwise it's a status/combat banner — read
        // it verbatim (poison, "withstood", "No effect", enemy-turn lines, …).
        // Match the window text against the unit's full name AND its display
        // (first) name — ally bubbles show the first name ("Yosuke") while
        // GetUnitName returns the full name ("Yosuke Hanamura").
        string uname = actor != 0 ? Battle.UnitName(actor) : null;
        string dname = actor != 0 ? Battle.UnitDisplayName(actor) : null;
        string t = text.Trim();
        bool isThisActorsName =
            (uname != null && string.Equals(uname.Trim(), t, StringComparison.OrdinalIgnoreCase)) ||
            (dname != null && string.Equals(dname.Trim(), t, StringComparison.OrdinalIgnoreCase));

        string speak;
        if (isThisActorsName)
        {
            bool isEnemy = IsReadable(actor + 0xA2, 1) && *(byte*)(actor + 0xA2) == 1;
            // Remember this enemy so ProfileNav knows which to read.
            if (isEnemy) Battle.LastTargetedEnemy = actor;

            // Defer to ProfileNav only when the analyze profile is actually resolving an
            // enemy (cmd 0 AND AnalyzeSelectedEnemy set = the open panel). While you're
            // still picking which enemy to analyze, read the name+ordinal here so moving
            // between enemies isn't silent.
            if (isEnemy && Battle.CurrentCommand == 0 && Battle.AnalyzeSelectedEnemy() != 0)
            {
                _lastActor = actor;
                _lastSpoken = text;
                return;
            }
            else if (isEnemy)
            {
                // Enemy target cursor: name first, then HP% + "down", with the
                // "X of N" ordinal LAST (user preference 2026-06-10: name and HP
                // up front, ordinal at the end).
                var (idx, cnt) = Battle.UnitOrdinal(actor);
                int pct = Battle.UnitHpPercent(actor);
                bool down = Battle.IsUnitDown(actor);

                var sb = new System.Text.StringBuilder(Battle.UnitDisplayName(actor) ?? text);
                if (pct >= 0) sb.Append($", {pct}%");
                if (down) sb.Append(", down");
                string ail = Battle.AilmentText(Battle.UnitStatusOf(actor));
                if (ail != null) sb.Append($", {ail}");
                if (cnt > 1 && idx > 0) sb.Append($", {idx} of {cnt}");
                speak = sb.ToString();
            }
            else
            {
                // Ally target (heal / item): current HP/SP (the part that matters
                // for who needs healing) read straight from the stat node, plus
                // max when the getter resolves it.
                var sb = new System.Text.StringBuilder(Battle.UnitDisplayName(actor) ?? text);
                nint stat = IsReadable(actor + 0xCF0, 8) ? *(nint*)(actor + 0xCF0) : 0;
                if (IsReadable(stat, 0x10))
                {
                    int curHp = *(ushort*)(stat + 0x08);
                    int curSp = *(ushort*)(stat + 0x0A);
                    int maxHp = Battle.TryUnitHp(actor, out _, out int mh) ? mh : 0;
                    int maxSp = Battle.TryUnitSp(actor, out _, out int ms) ? ms : 0;
                    sb.Append(maxHp > 0 ? $", {curHp} of {maxHp} HP" : $", {curHp} HP");
                    sb.Append(maxSp > 0 ? $", {curSp} of {maxSp} SP" : $", {curSp} SP");
                    string ailA = Battle.AilmentText(*(uint*)(stat + 0x0C));
                    if (ailA != null) sb.Append($", {ailA}");
                    Log($"[BattleLog] ally stat=0x{stat:X} cur={curHp}/{curSp} max={maxHp}/{maxSp}");
                }
                speak = sb.ToString();
            }
        }
        else if (Battle.IsCombatantName(text))
        {
            // A combatant name, but NOT this window's own actor: a second info-window
            // instance echoing the current target. Skip to avoid the double read.
            _lastActor = actor;
            _lastSpoken = text;
            Log($"[BattleLog] skip dup-window name \"{text}\" (actor 0x{actor:X} is \"{uname}\")");
            return;
        }
        else
        {
            // Status / combat banner (poison, "withstood", "No effect", enemy turn…).
            speak = text;
        }

        _lastActor = actor;
        _lastSpoken = text;

        // Suppress the same spoken line repeating within RepeatMs (banner flicker spam).
        long now = Environment.TickCount64;
        if (_recent.TryGetValue(speak, out long prevT) && now - prevT < RepeatMs) return;
        _recent[speak] = now;
        if (_recent.Count > 16)   // prune stale entries so it can't grow unbounded
            foreach (var k in new List<string>(_recent.Keys))
                if (now - _recent[k] >= RepeatMs) _recent.Remove(k);

        Log($"[BattleLog] actor=0x{actor:X} text=\"{text}\" -> \"{speak}\"");
        Speech.Say(speak, true);
    }

    private delegate void InfoWindowProcDelegate(nint win);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48;
        const int OFF_STATE = 32;
        const int OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        ulong end = (ulong)regionBase + (ulong)regionSize;
        return a + (ulong)size <= end;
    }
}
