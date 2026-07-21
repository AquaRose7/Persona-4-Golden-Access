using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using p4g64.accessibility.Native.Text;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

internal unsafe class Battle
{
    private static readonly Dictionary<Command, string> _commandNames = new()
    {
        { Command.Analysis, "Analysis" },
        { Command.Tactics, "Tactics" },
        { Command.Guard, "Guard" },
        { Command.Attack, "Attack" },
        { Command.Skill, "Skill" },
        { Command.Persona, "Persona" },
        { Command.Item, "Item" },
        { Command.Escape, "Escape" }
    };

    private Command _lastSelectedCommand = Command.None;
    private MessageBubble _messageBubble;

    private IHook<ProcessDelegate> _processHook;
    private SkillSelect _skillSelect;
    private ItemSelect _itemSelect;
    private PersonaSelect _personaSelect;
    private PersonaPanelHook _personaPanelHook;
    private PersonaMenuHook _personaMenuHook;
    private SkillInfoSelect _skillInfoSelect;
    private ResultReader _resultReader;
    private ShuffleReader _shuffleReader;
    private ShuffleText _shuffleText;   // TEMP diag — rooted here (hook delegate must not be GC'd)
    private EnemyActionHook _enemyActionHook;

    /// <summary>
    /// <c>GetUnitName(BtlUnitInfo*)</c> @ 0x1400CC1A0 — returns a NameRecord* for any
    /// combatant (party incl. the custom-named MC, and enemies). Pure / side-effect
    /// free, but we still call it only from this game-thread hook. Wrapper acquired
    /// at startup; null until the scan resolves.
    /// </summary>
    private static GetUnitNameDelegate _getUnitName;
    internal static GetUnitNameDelegate GetUnitName => _getUnitName;

    private static GetMaxDelegate _getMaxHp, _getMaxSp;
    /// <summary>Party max HP getter. Null until acquired. Verify before always-on use.</summary>
    internal static GetMaxDelegate GetMaxHp => _getMaxHp;
    internal static GetMaxDelegate GetMaxSp => _getMaxSp;
    internal delegate int GetMaxDelegate(nint statNode);

    private static GetAffinityDelegate _getAffinity;
    internal static GetAffinityDelegate GetAffinity => _getAffinity;
    internal delegate uint GetAffinityDelegate(nint statNode, int element);

    /// <summary>Total exp a persona needs to REACH a given level — the clean
    /// thunk 0x1400D8050 → FUN_15f139d20(personaEntry, level). Pure table math
    /// (species growth curve), found via the post-battle exp distributor
    /// FUN_1401013a0. NEXT EXP = this(entry, level+1) − exp(entry+0x08).</summary>
    private static PersonaExpForLevelDelegate _personaExpForLevel;
    internal delegate int PersonaExpForLevelDelegate(nint personaEntry, int level);

    // GetAffinityKnown(nameId, element) @ 0x1400993F0 — true if the analyze grid
    // has revealed this element for the shadow type (the game's discovered state).
    private static GetKnownDelegate _getAffinityKnown;
    internal delegate int GetKnownDelegate(int nameId, int element);

    // ── Enemy SKILLS on the analyze panel (2026-07-09) ──────────────────────────
    // FUN_1400d4320(statNode) returns the enemy's 8-short skill array (non-zero = a
    // skill id). The analyze render FUN_1400e0e50(param1, param2) draws those skills
    // ONLY when the panel flag *(ushort*)param2 bit 0x8 is CLEAR (the enemy-skills
    // upgrade / "?" otherwise). We hook that render to publish the reveal state + skill
    // list for the CURRENT analyzed enemy, so ProfileNav's Skills row never leaks a
    // skill the game itself is hiding. Enemy unit = *(*(param2+0x10))+0x38 (the render's
    // own chain); nameId +0xA4 / stat +0xCF0 as elsewhere.
    private static GetSkillArrayDelegate _getEnemySkills;
    internal delegate nint GetSkillArrayDelegate(nint statNode);
    private IHook<AnalyzeRenderDelegate> _analyzeHook;
    private delegate void AnalyzeRenderDelegate(nint param1, nint param2);

    internal static volatile nint AnalyzeSkillUnit;       // enemy the analyze panel is drawing (0 = closed)
    internal static volatile bool AnalyzeSkillsRevealed;   // game is showing skills (panel flag bit 0x8 clear)
    // Panel flag bit 0x8 = the "?"/hidden state for the WHOLE basic-info block (level,
    // Max HP/SP, arcana) — the analyze render gates all of it on (flag & 8) == 0
    // (FUN_1400e0e50). False = the game is showing "?", so we must NOT read the real
    // level/HP/SP/arcana from memory (they leaked on an un-analyzed boss, user 2026-07-20).
    internal static volatile bool AnalyzeBasicRevealed;
    private static volatile int[] _analyzeSkillIds = System.Array.Empty<int>();

    private void OnAnalyzeRender(nint param1, nint param2)
    {
        _analyzeHook.OriginalFunction(param1, param2);
        try { CaptureAnalyzeSkills(param2); } catch { /* never let a hook throw */ }
    }

    private static void CaptureAnalyzeSkills(nint param2)
    {
        if (!IsReadable(param2, 2)) { AnalyzeSkillUnit = 0; return; }
        ushort pflag = *(ushort*)param2;
        if ((pflag & 1) == 0) { AnalyzeSkillUnit = 0; return; }   // panel not visible
        if (!IsReadable(param2 + 0x10, 8)) { AnalyzeSkillUnit = 0; return; }
        nint obj = *(nint*)(param2 + 0x10);
        if (obj <= 0x10000 || !IsReadable(obj + 0x38, 8)) { AnalyzeSkillUnit = 0; return; }
        nint unit = *(nint*)(obj + 0x38);
        if (unit <= 0x10000) { AnalyzeSkillUnit = 0; return; }

        // The render draws real skills only inside `(uVar3 & 0x44) != 0` (skill area
        // active) AND `(uVar3 & 8) == 0` (not the "?"/hidden state). Bit 8 alone leaked
        // skills the game hides (user 2026-07-09). Verified from FUN_1400e0e50 @1021/1030.
        bool revealed = (pflag & 0x44) != 0 && (pflag & 8) == 0;
        // ([AnalyzeDiag] confirmed these bits across the tester cycle incl. the Q
        // quick-panel; removed 2026-07-11 pre-release.)
        int[] ids = System.Array.Empty<int>();
        if (revealed && IsReadable(unit + 0xCF0, 8) && _getEnemySkills != null)
        {
            nint stat = *(nint*)(unit + 0xCF0);
            if (stat > 0x10000)
            {
                nint arr = 0;
                try { arr = _getEnemySkills(stat); } catch { arr = 0; }
                if (arr > 0x10000 && IsReadable(arr, 16))
                {
                    var list = new List<int>(8);
                    for (int i = 0; i < 8; i++) { int sid = *(ushort*)(arr + i * 2); if (sid > 0 && sid < 1000) list.Add(sid); }
                    ids = list.ToArray();
                }
            }
        }
        _analyzeSkillIds = ids;          // publish contents BEFORE the unit key
        AnalyzeSkillsRevealed = revealed;
        AnalyzeBasicRevealed = (pflag & 8) == 0;   // level/HP/SP/arcana shown, not "?"
        AnalyzeSkillUnit = unit;
    }

    /// <summary>Names of the enemy skills the analyze panel is currently revealing.</summary>
    internal static string[] AnalyzeSkillNames()
    {
        var ids = _analyzeSkillIds;
        var names = new List<string>(ids.Length);
        foreach (int sid in ids)
        {
            string n; try { n = Skill.GetName(sid); } catch { n = null; }
            names.Add(string.IsNullOrEmpty(n) ? $"skill {sid}" : n);
        }
        return names.ToArray();
    }

    /// <summary>Manager-global combatant enumeration. Static + fully guarded, so it
    /// is safe to call from a background poll thread. Returns (unit, side, statNode)
    /// for every combatant; side: 1 = enemy, 0 = party (confirmed via the F12 probe).
    /// statNode = unit+0xCF0 (HP at +0x08, SP +0x0A, status +0x0C, enemyId +0x02).</summary>
    internal static List<(nint unit, int side, nint stat)> EnumerateUnits()
    {
        var list = new List<(nint, int, nint)>();
        nint mgrGlobal = unchecked((nint)0x140EC08F0L);
        if (!IsReadable(mgrGlobal, 8)) return list;
        nint mgrPtr = *(nint*)mgrGlobal;
        if (!IsReadable(mgrPtr + 0x1C0, 8)) return list;
        nint node = *(nint*)(mgrPtr + 0x1C0);
        int i = 0;
        while (node != 0 && i < 24 && IsReadable(node, 0x528))
        {
            nint unit = *(nint*)(node + 0x38);
            if (IsReadable(unit, 0xCF8))
            {
                int side = *(byte*)(unit + 0xA2);
                nint stat = *(nint*)(unit + 0xCF0);
                list.Add((unit, side, stat));
            }
            nint next = IsReadable(node + 0x520, 8) ? *(nint*)(node + 0x520) : 0;
            if (next == node) break;
            node = next;
            i++;
        }
        return list;
    }

    /// <summary>Decode any combatant's display name via GetUnitName (pure). Returns
    /// null if unavailable. Safe from a background thread.</summary>
    internal static string UnitName(nint unit)
    {
        var fn = _getUnitName;
        if (fn == null || !IsReadable(unit, 0xCF8)) return null;
        nint rec;
        try { rec = fn(unit); } catch { return null; }
        return DecodeAtlusName(rec);
    }

    /// <summary>Display name for speech: party members (side 0) get their FIRST
    /// name only (matching the party readout — "Yosuke", "haru"); enemy names are
    /// kept whole ("Lying Hablerie"). Falls back to the full name.</summary>
    internal static string UnitDisplayName(nint unit)
    {
        string full = UnitName(unit);
        if (string.IsNullOrWhiteSpace(full)) return full;
        if (IsReadable(unit, 0xCF8) && *(byte*)(unit + 0xA2) == 0)
        {
            int sp = full.IndexOf(' ');
            if (sp > 0) return full.Substring(0, sp);
        }
        return full;
    }

    /// <summary>Diagnostic: dump every panel flag + selected unit so we can see
    /// what's actually live when the F persona panel (and the enemy analyze) are
    /// open. Game-thread (called from the F12 handler).</summary>
    internal static void DumpPanelFlags()
    {
        nint info = ActiveBtlInfo;
        var sb = new System.Text.StringBuilder("[PanelDiag]");
        if (IsReadable(info, 0x1540))
        {
            nint turn = *(nint*)(info + 0xCB8);
            int cmd = IsReadable(turn + 0xAE, 2) ? *(ushort*)(turn + 0xAC) : -1;
            sb.Append($" cmd={cmd} D08={*(ushort*)(info + 0xD08)} D10=0x{*(nint*)(info + 0xD10):X} D18={*(ushort*)(info + 0xD18)}");

            // F-panel candidates from persona_fpanel_hunt.md:
            nint bt1530 = IsReadable(info + 0x1530, 8) ? *(nint*)(info + 0x1530) : 0;
            int subTab = IsReadable(bt1530 + 4, 2) ? *(short*)(bt1530 + 4) : -999;
            nint acting = IsReadable(turn + 0x40, 8) ? *(nint*)(turn + 0x38) : 0;
            int actPid = IsReadable(acting + 0xA4, 2) ? *(ushort*)(acting + 0xA4) : -1;
            nint d10 = *(nint*)(info + 0xD10);
            int d10Pid = IsReadable(d10 + 0xA4, 2) ? *(ushort*)(d10 + 0xA4) : -1;
            sb.Append($" subTab={subTab} acting=0x{acting:X} actPid={actPid}(\"{Native.Persona.GetName(actPid >= 0 ? actPid : 0)}\") d10Pid={d10Pid}");
        }
        else sb.Append(" info-unreadable");

        nint s = AnalyzeSubObject();
        if (s != 0 && IsReadable(s + 0x52F2, 2))
            sb.Append($" S=0x{s:X} 52F0={*(ushort*)(s + 0x52F0)} 52F2={*(ushort*)(s + 0x52F2)}");
        else sb.Append(" S=0");

        nint au = AnalyzeSelectedEnemy();
        sb.Append($" analyzeUnit=0x{au:X}");
        if (au != 0) sb.Append($"(side={*(byte*)(au + 0xA2)} name={UnitName(au)})");

        nint pu = PersonaDetailUnit();
        sb.Append($" personaUnit=0x{pu:X}");
        if (pu != 0 && IsReadable(pu + 0xA4, 2))
            sb.Append($"(side={*(byte*)(pu + 0xA2)} pid={*(ushort*)(pu + 0xA4)} name={UnitName(pu)})");

        // Full-panel persona array at BtlInfo+0xCE0 (count BtlInfo+0xD08); each
        // entry's +0xA4 = persona id → name.
        if (IsReadable(info, 0xD0A))
        {
            int cnt = *(ushort*)(info + 0xD08);
            sb.Append($" | CE0[] count={cnt}:");
            for (int i = 0; i < cnt && i < 14; i++)
            {
                nint e = IsReadable(info + 0xCE0 + i * 8, 8) ? *(nint*)(info + 0xCE0 + i * 8) : 0;
                int pid = (e != 0 && IsReadable(e + 0xA4, 2)) ? *(ushort*)(e + 0xA4) : -1;
                sb.Append($" [{i}]e=0x{e:X} pid={pid}(\"{Native.Persona.GetName(pid >= 0 ? pid : 0)}\")");
            }
        }

        Log(sb.ToString());
    }

    /// <summary>Enemy max HP/SP from the static table (enemyId = statNode+0x02).
    /// Returns false if unreadable.</summary>
    internal static bool TryEnemyMax(nint stat, out int maxHp, out int maxSp)
    {
        maxHp = maxSp = 0;
        nint tblGlobal = unchecked((nint)0x140EC0920L);
        if (!IsReadable(stat, 4) || !IsReadable(tblGlobal, 8)) return false;
        ushort eid = *(ushort*)(stat + 2);
        nint tbl = *(nint*)tblGlobal;
        nint rec = tbl + eid * 0x3C;
        if (!IsReadable(rec + 8, 8)) return false;
        maxHp = *(ushort*)(rec + 4);
        maxSp = *(ushort*)(rec + 6);
        return true;
    }

    /// <summary>Current + max HP for any combatant. Enemy max from the static
    /// table; party max from the (confirmed) getter. Call on the game thread (it
    /// may invoke the party getter). Returns false if unavailable.</summary>
    internal static bool TryUnitHp(nint unit, out int cur, out int max)
    {
        cur = 0; max = 0;
        if (!IsReadable(unit, 0xCF8)) return false;
        int side = *(byte*)(unit + 0xA2);
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 0x10)) return false;
        cur = *(ushort*)(stat + 0x08);
        if (side == 1)
        {
            if (!TryEnemyMax(stat, out max, out _)) return false;
        }
        else
        {
            var fn = _getMaxHp;
            if (fn == null || !IsReadable(stat, 0x100)) return false;
            try { max = fn(stat); } catch { return false; }
        }
        return max > 0;
    }

    /// <summary>Current + max SP for any combatant (enemy max from table, party
    /// from the getter). Game-thread for party. False if unavailable.</summary>
    internal static bool TryUnitSp(nint unit, out int cur, out int max)
    {
        cur = 0; max = 0;
        if (!IsReadable(unit, 0xCF8)) return false;
        int side = *(byte*)(unit + 0xA2);
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 0x10)) return false;
        cur = *(ushort*)(stat + 0x0A);
        if (side == 1)
        {
            if (!TryEnemyMax(stat, out _, out max)) return false;
        }
        else
        {
            var fn = _getMaxSp;
            if (fn == null || !IsReadable(stat, 0x100)) return false;
            try { max = fn(stat); } catch { return false; }
        }
        return max > 0;
    }

    /// <summary>HP as a 0–100 percentage, or -1 if unavailable.</summary>
    internal static int UnitHpPercent(nint unit)
    {
        if (!TryUnitHp(unit, out int cur, out int max) || max <= 0) return -1;
        int pct = (int)Math.Round(cur * 100.0 / max);
        if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
        return pct;
    }

    /// <summary>This unit's 1-based position among living units on its own side,
    /// plus that side's living count — so target narration can say "2 of 3" to
    /// distinguish same-named enemies. Returns (0,0) if not found.</summary>
    internal static (int index, int count) UnitOrdinal(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return (0, 0);
        int side = *(byte*)(unit + 0xA2);
        int count = 0, index = 0;
        foreach (var (u, s, stat) in EnumerateUnits())
        {
            if (s != side || !IsReadable(stat, 0x10)) continue;
            if (*(ushort*)(stat + 0x08) == 0) continue; // skip defeated
            count++;
            if (u == unit) index = count;
        }
        return (index, count);
    }

    /// <summary>True if <paramref name="text"/> exactly matches some combatant's
    /// name. Used to suppress the plain top-bubble announcement for target names
    /// (BattleLog speaks those with HP%/ordinal), while leaving other bubble text
    /// (e.g. Analyze details) alone.</summary>
    internal static bool IsCombatantName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        foreach (var (u, _, _) in EnumerateUnits())
        {
            string n = UnitName(u);
            if (n != null && string.Equals(n.Trim(), t, StringComparison.OrdinalIgnoreCase)) return true;
            string d = UnitDisplayName(u);
            if (d != null && string.Equals(d.Trim(), t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ===== Enemy affinity profile — reads the GAME's analyze data =====

    /// <summary>The enemy the player last targeted (set by BattleLog). The profile
    /// reads this one; cleared when battle ends.</summary>
    internal static volatile nint LastTargetedEnemy;

    // ── TACTICS member screen (2026-07-11) ─────────────────────────────────
    // The member rows are drawn by the "full panel" renderer FUN_1400e6900, once
    // per row per frame, and its p6 ARGUMENT = "this row is selected". The June
    // hunts never found the cursor in DATA because it only exists as this call
    // argument (raw TextSpy + FULL-render capture, cursor tracked 1→2→3 live).
    // PersonaPanelHook publishes; TacticsMemberReader speaks. Row units come from
    // BtlInfo+0xCE0 + index*8. No p6==1 row while rows draw = "All members" (top row).
    internal static volatile nint TacticsSelUnit;   // unit of the p6==1 row
    internal static long TacticsSelTick;            // last time a p6==1 row drew
    internal static long TacticsRowTick;            // last time ANY member row drew

    /// <summary>The enemy-side unit of the most recent info-window banner event
    /// (set by BattleLog on EVERY enemy banner, spoken or deduped). The player-turn
    /// Turn pointer is NULL during enemy turns, so this is the attacker signal for
    /// DamageMonitor's "X attacks" attribution. Cleared when battle ends.</summary>
    internal static volatile nint LastEnemyBannerActor;
    internal static long LastEnemyBannerTick;

    /// <summary>The enemy whose action most recently locked in (set by
    /// EnemyActionHook from the move resolver). Cleared when battle ends.</summary>
    internal static volatile nint LastEnemyAttacker;
    internal static long LastEnemyAttackerTick;

    /// <summary>Pending HP-cost of the skill the player has highlighted (set by
    /// SkillSelect every frame the row is selected; unit = the acting member).
    /// When that member's HP then drops by EXACTLY this amount, DamageMonitor
    /// speaks "spent N HP" instead of "took N damage" and never attributes it
    /// to an enemy — the Turn pointer is already null when the cost lands, so
    /// this is the only reliable way to tell a Bash/Cleave self-cost from a hit
    /// (live bug 2026-06-10: Bash's 7 HP cost spoke as "Lying Hablerie attacks").</summary>
    internal static volatile nint PendingHpCostUnit;
    internal static volatile int PendingHpCost;
    internal static long PendingHpCostTick;

    /// <summary>The skill/item the player most recently highlighted in a battle
    /// menu (published by SkillSelect/ItemSelect). MessageBubble suppresses the
    /// cast-time bubble that just echoes that choice — one-shot, so an enemy
    /// casting the same skill moments later still speaks.</summary>
    internal static volatile int PendingEchoSkillId = -1;
    internal static long PendingEchoSkillTick;
    internal static volatile int PendingEchoItemId = -1;
    internal static long PendingEchoItemTick;

    /// <summary>The skill currently highlighted/selected in the Skill menu (set by SkillSelect,
    /// NOT consumed) — used by TargetReader to read the targeted enemy's affinity to that skill's
    /// element during target select.</summary>
    internal static volatile int SelectedSkillId = -1;

    /// <summary>True if the text is one of the battle command names
    /// (Attack/Guard/Skill/...). Their bubbles only echo the player's confirmed
    /// command (and the enemy's basic attack, which the damage attribution line
    /// already names) — suppressed as noise.</summary>
    internal static bool IsCommandName(string t)
    {
        foreach (var n in _commandNames.Values)
            if (string.Equals(n, t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The battle command the cursor is on (0 = Analysis). Lets the
    /// narrator give the full affinity profile when you're Analyzing vs the short
    /// "name, HP%" when targeting for Attack.</summary>
    internal static volatile short CurrentCommand = -1;

    // Analyze grid as the game shows it: element ids 0,1,2,3,4,6,7 (CONFIRMED:
    // 6=Light, 7=Dark; 5=Almighty, not in the grid).
    private static readonly (int id, string name)[] _profileElements =
    {
        (0, "Physical"), (1, "Fire"), (2, "Ice"), (3, "Electric"),
        (4, "Wind"), (6, "Light"), (7, "Dark")
    };

    private static string ClassifyAffinity(uint r)
    {
        // Affinity TYPE is a FLAG in the high byte (bits 24-31); the low word is just
        // the damage magnitude, NOT a weak/resist indicator. CONFIRMED 2026-06-17 via
        // [AffDump] (Phantom Mage nameId=178): Fire/Ice/Wind=0x0000007D NORMAL (no
        // flag), Electric=0x0800007D WEAK, Physical=0x10000032 RESIST. Flag bits match
        // the confirmed persona-table decode (ClassifyPersonaRaw): 0x10=resist,
        // 0x08=weak, 0x20=repel, 0x04=drain, 0x01=null, 0x02=block (×0x1000000 here).
        if ((r & 0x20000000) != 0) return "repel";
        if ((r & 0x04000000) != 0) return "drain";
        if ((r & 0x01000000) != 0) return "null";
        // 0x02 is a DISTINCT no-damage flag (Ghidra: the merge layer treats 0x01 and 0x02 as two
        // separate parallel flags, and FUN_1400d2840 zeroes damage for the whole 0x27 group). It was
        // wrongly read as "null"; USER-CONFIRMED live 2026-07-02 that this affinity actually REPELS
        // (damage bounced back). So 0x02 = repel (the game's repel encoding on the enemy live-cache).
        if ((r & 0x02000000) != 0) return "repel";
        if ((r & 0x10000000) != 0) return "resist";
        if ((r & 0x08000000) != 0) return "weak";
        return "normal";
    }

    /// <summary>Classify a RAW affinity u16 straight out of the persona species table
    /// (*(0x140EC0988)). This table is flag-encoded in the HIGH byte, unlike the
    /// enemy live-cache (which is multiplier-encoded), so it needs its own decode.
    /// CONFIRMED against the on-screen panel (Izanagi: Elec 0x1000=resist, Wind
    /// 0x0800=weak, Dark 0x0100=null; Pixie: Fire 0x0800=weak, Wind 0x1000=resist;
    /// 0x0014/0x000A high-byte 0 = "—"/normal). The panel shows weak/resist/null/
    /// repel/drain only via the high-byte flags; a bare low-byte multiplier renders
    /// as "—" unless it exceeds 100% (then weak).
    /// 0x02/0x04/0x20 (repel/drain/block) are best-guess until seen on screen — the
    /// AffDump diagnostic logs the raw so they can be confirmed.</summary>
    internal static string ClassifyPersonaRaw(ushort w)
    {
        int hi = (w >> 8) & 0xFF;
        if (hi == 0)
        {
            int mult = (w & 0xFF) * 5;
            return mult > 100 ? "weak" : "normal";
        }
        if ((hi & 0x10) != 0) return "resist"; // CONFIRMED
        if ((hi & 0x08) != 0) return "weak";   // CONFIRMED
        if ((hi & 0x20) != 0) return "repel";  // guess
        if ((hi & 0x04) != 0) return "drain";  // guess
        if ((hi & 0x02) != 0) return "repel";  // 0x02 = repel (user-confirmed on enemy affinities; same high-byte flag layout)
        if ((hi & 0x01) != 0) return "null";   // 0x01 CONFIRMED (null)
        return "normal";
    }

    /// <summary>Spoken affinity profile that mirrors the in-game Analyze grid:
    /// each element reads "unknown" until the GAME has revealed it
    /// (<c>GetAffinityKnown(nameId, element)</c> over the global analysis bitmap,
    /// keyed by the shadow name id at <c>unit+0xA4</c>), then shows
    /// weak/resist/null/drain/repel/normal via the affinity classifier.</summary>
    internal static string BuildEnemyProfile(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return null;
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 0x10)) return null;
        int nameId = *(ushort*)(unit + 0xA4);
        var known = _getAffinityKnown;
        var aff = _getAffinity;

        var sb = new System.Text.StringBuilder(UnitDisplayName(unit) ?? "Enemy");
        sb.Append(". ");
        foreach (var (id, ename) in _profileElements)
        {
            string val = "unknown";
            bool isKnown = false;
            if (known != null) { try { isKnown = known(nameId, id) != 0; } catch { } }
            if (isKnown && aff != null)
            {
                uint r = 0;
                try { r = aff(stat, id); } catch { }
                val = ClassifyAffinity(r);
            }
            sb.Append($"{ename} {val}. ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>For an ALL-enemies skill, summarises how it lands on the living enemies by counting
    /// DISCOVERED affinities, e.g. "2 weak, 1 repel". Null when the element has no grid (Almighty /
    /// status) or nothing notable is known. elemId aligns with Skill.ElementalType.</summary>
    internal static string AoeAffinitySummary(int elemId)
    {
        if (elemId < 0 || elemId == 5 || elemId > 7) return null;
        int total = 0;
        var cnt = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var (unit, side, stat) in EnumerateUnits())
        {
            if (side != 1) continue;                                   // enemies
            if (!IsReadable(stat, 0x10) || *(ushort*)(stat + 0x08) == 0) continue;   // dead
            total++;
            // (Discovered-only by design: KnownAffinity gates on the game's own per-SPECIES
            // permanent discovery flag — a weakness found in ANY past battle stays known,
            // matching the analyze grid. Verified live 2026-07-03 via a temporary AoEDiag:
            // same enemy read unknown 4× then "drain" only after the player discovered it.)
            string aff = KnownAffinity(unit, elemId);
            if (aff == null || aff == "normal") continue;
            cnt[aff] = cnt.TryGetValue(aff, out int c) ? c + 1 : 1;
        }
        if (total == 0 || cnt.Count == 0) return null;
        var parts = new System.Collections.Generic.List<string>();
        foreach (var key in _affOrder)
            if (cnt.TryGetValue(key, out int c)) parts.Add($"{c} {key}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
    private static readonly string[] _affOrder = { "weak", "resist", "null", "drain", "repel" };

    /// <summary>Active stat buffs/debuffs on a unit as spoken text ("attack up, defense down"), or
    /// null when none. Layout: NIBBLE-PAIRED channels in +0x1C..+0x1F (signed-nibble stage) with
    /// per-nibble turn counters in +0x25..+0x28 — see the channel map in BuffTextFromStat
    /// (re-cracked 2026-07-03 after the Suku pair misread).</summary>
    internal static string BuffText(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return null;
        return BuffTextFromStat(*(nint*)(unit + 0xCF0));
    }

    /// <summary>Buff/debuff text straight from a stat node (for allies, the persistent party record
    /// IS the in-battle stat node — see PartyStatus).</summary>
    internal static string BuffTextFromStat(nint stat)
    {
        if (!IsReadable(stat, 0x30)) return null;
        var b = (byte*)stat;
        var parts = new System.Collections.Generic.List<string>(4);
        // Buff bytes are NIBBLE-PAIRED (cracked live 2026-07-03: Rakunda alone → +0x1D=0xE0 /
        // +0x26=0x30 ticking 0x30→0x20 in the HIGH nibble; Sukunda added → 0xEE / 0x23 — the LOW
        // nibble joins with an independent timer; Sukukaja → low nibble 0x1). Each stage byte
        // +0x1C..+0x1F holds TWO channels: a SIGNED-NIBBLE stage (0x1 = up, 0xE = down → nibble
        // bit 3 = down) with its turn counter in the SAME nibble of +0x25..+0x28. Channels:
        // attack = +0x1C hi · defense = +0x1D hi · hit = +0x1D lo · evasion = +0x1E lo ·
        // crit = +0x1F (either nibble). The old "byte high bit = down" only held for HIGH-nibble
        // channels and misread the Suku pair as "up" (player report 2026-07-03).
        string Chan(int t, bool hiNibble)
        {
            int timer = hiNibble ? (b[0x25 + t] >> 4) : (b[0x25 + t] & 0xF);
            if (timer == 0) return null;                    // not active
            int stage = hiNibble ? (b[0x1C + t] >> 4) : (b[0x1C + t] & 0xF);
            if (stage == 0) return null;
            return (stage & 0x8) != 0 ? "down" : "up";
        }
        // ATTACK spans BOTH nibbles of +0x1C (player log 2026-07-06: a lone
        // attack debuff on Shadow Teddie set +1C=0xEE, a party-wide attack
        // buff set +1C=0x11 on every member — hi and lo always together, same
        // sign and timers, with no other channel lit). Speak from either.
        // ([BuffDiag] rode along through the whole v1.4.x tester cycle and
        // never fired a disagreement — removed 2026-07-11 pre-release.)
        string atk = Chan(0, true) ?? Chan(0, false);
        if (atk != null) parts.Add($"attack {atk}");
        string def = Chan(1, true);
        if (def != null) parts.Add($"defense {def}");
        // Suku spells set hit + evasion together — speak the pair as the familiar
        // "agility"; a lone half (some boss skills) reads by its own name.
        string hit = Chan(1, false), eva = Chan(2, false);
        if (hit != null && hit == eva) parts.Add($"agility {hit}");
        else
        {
            if (hit != null) parts.Add($"hit {hit}");
            if (eva != null) parts.Add($"evasion {eva}");
        }
        string crit = Chan(3, true) ?? Chan(3, false);
        // Enemy Rebellion (crit up) uses a FIFTH channel with a different pairing:
        // stage @+0x1E HIGH nibble, timer @+0x14 HIGH nibble (dump-diffed 2026-07-03:
        // buffed +0x14=0x20/+0x1E=0x10, expired both 0, identical on the re-buff —
        // NOT the +0x27-hi timer the +9 pattern predicts).
        if (crit == null && (b[0x14] >> 4) != 0)
        {
            int st = b[0x1E] >> 4;
            if (st != 0) crit = (st & 0x8) != 0 ? "down" : "up";
        }
        if (crit != null) parts.Add($"critical rate {crit}");
        // (+0x1C lo was the long-standing "unmapped channel" — resolved above as
        // the attack pair's second half, player log 2026-07-06.)
        // Mind Charge (Concentrate — 2.5x next magic attack): stat +0x16 bit 0x10. Verified live
        // 2026-07-02: toggles ON when a Shadow Concentrates, OFF when it fires the charged hit
        // (two isolated captures). It's a next-attack multiplier flag, not a stat buff, but reads
        // naturally alongside the buffs on the enemy target / status readouts.
        if ((b[0x16] & 0x10) != 0) parts.Add("mind charged");
        // Power Charge (2.5x next physical attack): stat +0x16 bit 0x08 — captured live
        // 2026-07-02 via the ChargeDiag after the user's controlled Power Charge use
        // (sibling bit of Mind Charge 0x10, same flag byte).
        if ((b[0x16] & 0x08) != 0) parts.Add("power charged");
        // (The ChargeDiag unknown-bits log was removed in the v1.3.5 cleanup; the
        // remaining bits in 0x16/0x17 — guard? enrage? — are still unmapped.)
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The enemy's affinity to ONE element ("weak"/"resist"/"null"/"drain"/"repel"/
    /// "block"/"normal"), or null if the player hasn't analyzed/revealed it yet (so we never
    /// leak unknown weaknesses — mirrors the game's mark). elemId: Physical=0, Fire=1, Ice=2,
    /// Electric=3, Wind=4, Light=6, Dark=7 (aligns with Skill.ElementalType).</summary>
    internal static string KnownAffinity(nint unit, int elemId)
    {
        if (!IsReadable(unit, 0xCF8)) return null;
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 0x10)) return null;
        int nameId = *(ushort*)(unit + 0xA4);
        var known = _getAffinityKnown;
        var aff = _getAffinity;
        if (known == null || aff == null) return null;
        bool isKnown = false;
        try { isKnown = known(nameId, elemId) != 0; } catch { return null; }
        if (!isKnown) return null;
        uint r = 0;
        try { r = aff(stat, elemId); } catch { return null; }
        return ClassifyAffinity(r);
    }

    // Standard Megaten arcana order, ids 1..22 (Fool=1 .. World=22).
    private static readonly string[] _arcana =
    {
        "Fool", "Magician", "Priestess", "Empress", "Emperor", "Hierophant", "Lovers",
        "Chariot", "Justice", "Hermit", "Fortune", "Strength", "Hanged Man", "Death",
        "Temperance", "Devil", "Tower", "Star", "Moon", "Sun", "Judgement", "World"
    };

    /// <summary>Arcana display name from a 1-based arcana id (Fool=1). Golden's added
    /// arcana sit at 25=Jester (live-verified on the SLink screen, Adachi) / 26=Hunger
    /// (Jester's evolution) / 27=Aeon (live-verified, Marie + Ame-no-Uzume) — the old
    /// 25-entry table had them at 23/24/25, two slots early, so Aeon read "arcana 27".</summary>
    internal static string ArcanaName(int aid) => aid switch
    {
        >= 1 and <= 22 => _arcana[aid - 1],
        25 => "Jester",
        26 => "Hunger",
        27 => "Aeon",
        _ => $"arcana {aid}"
    };

    /// <summary>Enemy level (statNode+0x06), or -1.</summary>
    internal static int EnemyLevel(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return -1;
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 8)) return -1;
        return *(byte*)(stat + 0x06);
    }

    /// <summary>Enemy arcana name from the enemy table (record+0x02), or null.</summary>
    internal static string EnemyArcanaName(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return null;
        nint stat = *(nint*)(unit + 0xCF0);
        nint tblG = unchecked((nint)0x140EC0920L);
        if (!IsReadable(stat, 4) || !IsReadable(tblG, 8)) return null;
        ushort eid = *(ushort*)(stat + 2);
        nint rec = *(nint*)tblG + eid * 0x3C;
        if (!IsReadable(rec + 2, 1)) return null;
        int aid = *(byte*)(rec + 2); // arcana ids are 1-based (Fool=1, Magician=2…)
        return ArcanaName(aid);
    }

    /// <summary>Enemy max HP/SP from the static table. False if unavailable.</summary>
    internal static bool EnemyMaxHpSp(nint unit, out int maxHp, out int maxSp)
    {
        maxHp = maxSp = 0;
        if (!IsReadable(unit, 0xCF8)) return false;
        nint stat = *(nint*)(unit + 0xCF0);
        return TryEnemyMax(stat, out maxHp, out maxSp);
    }

    /// <summary>Affinity text for one element on the analyze grid: "Fire weak",
    /// "Ice unknown", etc. — "unknown" until the game has revealed it.</summary>
    internal static string ElementAffinityText(nint unit, int elemId, string elemName)
    {
        if (!IsReadable(unit, 0xCF8)) return $"{elemName} unknown";
        nint stat = *(nint*)(unit + 0xCF0);
        int nameId = *(ushort*)(unit + 0xA4);
        var known = _getAffinityKnown;
        var aff = _getAffinity;
        bool isKnown = false;
        if (known != null) { try { isKnown = known(nameId, elemId) != 0; } catch { } }
        if (!isKnown || aff == null) return $"{elemName} unknown";
        uint r = 0;
        try { r = aff(stat, elemId); } catch { }
        string cls = ClassifyAffinity(r);
        return $"{elemName} {cls}";
    }

    /// <summary>The analyze grid's element ids + names, in on-screen order.</summary>
    internal static (int id, string name)[] ProfileElements => _profileElements;

    // ---- Persona detail (set by PersonaSelect when a stock entry is highlighted).
    // LastPersonaEntry is the highlighted stock PersonaInfo* (id +0x02, level +0x04,
    // skills +0x0C, stats +0x1C) — the authoritative source for the MC persona panel.
    // LastPersonaTick is refreshed every render so PersonaMenuOpen() is a live gate.
    internal static volatile nint LastPersonaEntry;
    internal static volatile int LastPersonaId = -1;
    internal static long LastPersonaTick;

    // ---- Persona menu state, published every frame by PersonaMenuHook (FUN_1400edf80).
    // PersonaMenuMode = *menuObj: 0 = closed, 2 = full detail panel (list hidden),
    // other = submenu list. PersonaMenuTick = freshness (the update runs each frame the
    // menu is open). This replaces the earlier blind BtlInfo-offset guesses.
    internal static volatile nint PersonaMenuObj;
    internal static volatile int PersonaMenuMode = -1;
    internal static long PersonaMenuTick;

    // FUN_1400edf80 is a GENERIC menu-widget update — it also drives the Analyze panel
    // with mode 2. To isolate the PERSONA menu we latch its object: PersonaSelect (which
    // only ever fires for the persona submenu) records the current PersonaMenuObj, and
    // we only count mode 2 as the persona panel when the live object still matches.
    internal static volatile nint PersonaMenuObjForPersona;

    /// <summary>True once the persona SUBMENU has actually rendered this session (set by
    /// PersonaSelect, cleared by PersonaNav when you leave the Persona command). Lets
    /// PersonaNav auto-read the panel only after a real entry — not when you merely
    /// highlight the Persona choice in the command ring.</summary>
    internal static volatile bool PersonaEntered;

    private static bool PersonaMenuLive => Environment.TickCount64 - PersonaMenuTick < 200;

    /// <summary>True while the full persona DETAIL panel is open. FUN_1400edf80 is a
    /// GENERIC battle-UI container (mode flickers 0/1/2 all battle), so mode 2 alone is
    /// not enough — require the latched persona object AND that the enemy-Analyze panel
    /// is NOT up (the two screens are mutually exclusive; without this, mode 2 leaks
    /// into Analyze and blocks ProfileNav).</summary>
    internal static bool PersonaPanelScreenOpen()
        => PersonaMenuLive && PersonaMenuMode == 2
           && PersonaMenuObjForPersona != 0 && PersonaMenuObj == PersonaMenuObjForPersona
           && AnalyzeSelectedEnemy() == 0;

    /// <summary>The persona the panel is showing. Prefer the submenu-tracked selection
    /// (PersonaSelect publishes LastPersonaEntry/Id for the highlighted row): when you
    /// open the panel, the live stock cursor (*(0x141165900)+0xA30) reverts to the
    /// EQUIPPED persona, so reading it would wrongly show e.g. Izanagi after you scrolled
    /// to Pixie. Falls back to the live cursor only if no selection is published.</summary>
    internal static (nint entry, int id) PersonaCursor()
    {
        if (IsReadable(LastPersonaEntry, 0x30) && LastPersonaId >= 0)
        {
            // The stock array REORDERS when a persona change is CONFIRMED (the
            // equipped persona swaps slots), so the remembered entry pointer can
            // now hold a NEIGHBOR's record — the announce then mixed the right
            // name/arcana (from the id) with the neighbor's LEVEL/stats (from the
            // stale entry; user 2026-07-11). Trust the entry only while its id
            // still matches; else re-find the id's record in the stock array.
            if (*(short*)(LastPersonaEntry + 2) == LastPersonaId)
                return (LastPersonaEntry, LastPersonaId);
            nint relocated = FindStockEntryById(LastPersonaId);
            if (relocated != 0) return (relocated, LastPersonaId);
        }
        nint g = unchecked((nint)0x141165900L);
        if (IsReadable(g, 8))
        {
            nint baseObj = *(nint*)g;
            if (baseObj != 0 && IsReadable(baseObj + 0xA30, 2))
            {
                int cur = *(short*)(baseObj + 0xA30);
                if (cur >= 0 && cur < 12)
                {
                    nint e = baseObj + 0xA34 + cur * 0x30;
                    if (IsReadable(e, 0x30) && *(byte*)e != 0)
                        return (e, *(short*)(e + 2));
                }
            }
        }
        return (0, -1);
    }

    /// <summary>Locate a persona's live record in the MC's 12-slot stock array by
    /// its persona id (id at entry+0x02) — the recovery path for when a confirmed
    /// change reorders the slots under a remembered entry pointer.</summary>
    private static nint FindStockEntryById(int pid)
    {
        nint g = unchecked((nint)0x141165900L);
        if (!IsReadable(g, 8)) return 0;
        nint baseObj = *(nint*)g;
        if (baseObj == 0) return 0;
        for (int k = 0; k < 12; k++)
        {
            nint e = baseObj + 0xA34 + k * 0x30;
            if (!IsReadable(e, 0x30) || *(byte*)e == 0) continue;
            if (*(short*)(e + 2) == pid) return e;
        }
        return 0;
    }

    /// <summary>Full profile row for the highlighted STOCK persona (MC). Everything
    /// comes from the stock PersonaInfo entry + species tables by the real persona
    /// id — not unit+0xA4 (which is a party-slot index, not a persona id).</summary>
    internal static (string label, string[] items) PersonaStockRow(int row)
    {
        var (e, pid) = PersonaCursor();
        if (pid < 0 || !IsReadable(e, 0x30)) return (null, Array.Empty<string>());
        switch (row)
        {
            case 0:
            {
                var items = new List<string>();
                string name = Persona.GetName(pid);
                if (!string.IsNullOrEmpty(name)) items.Add(name);
                string arcana = PersonaArcanaName(pid);
                if (!string.IsNullOrEmpty(arcana)) items.Add(arcana);
                int lvl = *(byte*)(e + 0x04);            // PersonaInfo.Level
                items.Add($"level {lvl}");
                return (null, items.ToArray());
            }
            case 1:
            {
                var items = new List<string>();
                foreach (var (eid, nm) in _profileElements)
                    items.Add(PersonaElementAffinityText(pid, eid, nm));
                return ("Elements", items.ToArray());
            }
            case 2:
            {
                // Displayed stats are the SUM of base+growth+bonus arrays.
                var t = PersonaTotalStats(e);
                if (t == null) return ("Stats", new[] { "unknown" });
                return ("Stats", new[]
                {
                    $"Strength {t[0]}", $"Magic {t[1]}", $"Endurance {t[2]}",
                    $"Agility {t[3]}", $"Luck {t[4]}"
                });
            }
            case 3:
            {
                // Skill NAMES for the row overview; J/L stepping pulls the full
                // description via PersonaSkillDetail (the in-game "Skill Info" F view).
                var names = new List<string>();
                short* sk = (short*)((byte*)e + 0x0C);    // PersonaInfo.Skills[8]
                for (int s = 0; s < 8; s++)
                {
                    int sid = sk[s];
                    if (sid <= 0) continue;
                    string snm = Skill.GetName(sid);
                    if (!string.IsNullOrEmpty(snm)) names.Add(snm);
                }
                if (names.Count == 0) names.Add("no skills");
                return ("Skills", names.ToArray());
            }
            case 4:
                return PersonaGrowthRow(e, pid);
        }
        return (null, Array.Empty<string>());
    }

    /// <summary>Remaining EXP until this persona's next level (entry = a
    /// PersonaInfo: MC stock entry or the ally embedded entry at record+0x54).
    /// -1 if unavailable / max level.</summary>
    internal static int PersonaNextExp(nint entry)
    {
        var fn = _personaExpForLevel;
        if (fn == null || !IsReadable(entry, 0x30)) return -1;
        int lvl = *(byte*)(entry + 0x04);
        if (lvl < 1 || lvl >= 99) return -1;
        int exp = *(int*)(entry + 0x08);
        int req;
        try { req = fn(entry, lvl + 1); } catch { return -1; }
        int need = req - exp;
        return need < 0 ? 0 : need;
    }

    /// <summary>EXP gap between <paramref name="level"/> and level+1 for persona
    /// SPECIES <paramref name="id"/>, independent of any live entry — used by the
    /// fusion result preview (whose entry layout is NOT a stock PersonaInfo). The
    /// exp-for-level fn only reads the species id at entry+0x02, so we synthesize a
    /// minimal entry. A freshly-fused result starts at its level's exp threshold, so
    /// this gap == the panel's "NEXT EXP". -1 if unavailable.</summary>
    internal static int PersonaExpGapForLevel(int id, int level)
    {
        var fn = _personaExpForLevel;
        if (fn == null || id <= 0 || id > 0x3FF || level < 1 || level >= 99) return -1;
        byte* buf = stackalloc byte[0x10];
        for (int i = 0; i < 0x10; i++) buf[i] = 0;
        *(ushort*)(buf + 2) = (ushort)id;
        try
        {
            int cur = fn((nint)buf, level);
            int nxt = fn((nint)buf, level + 1);
            int gap = nxt - cur;
            return gap < 0 ? -1 : gap;
        }
        catch { return -1; }
    }

    /// <summary>The next skill this persona will learn (first learnset entry
    /// above the current level — the panel's "NEXT LV n" box). Learnset tables
    /// from FUN_1400D7A90: party personas (id 0xC0..0xD7) at
    /// *(0x140EC0980)+(id-0xC0)*0x26E+4 (32 entries, absolute levels); stock
    /// personas at *(0x140EC0978)+id*0x46+6 (16 entries, levels relative to the
    /// species base level at *(0x140EC0958)+id*0xE+3). Entry = [lvl u8, flag u8,
    /// skill u16], flag 0 ends the list.</summary>
    internal static bool PersonaNextLearn(int pid, int level, out int learnLevel, out int skillId)
    {
        learnLevel = 0;
        skillId = 0;
        if (pid <= 0) return false;
        nint ls;
        int cap, absBase;
        if (pid >= 0xC0 && pid < 0xD8)
        {
            nint tG = unchecked((nint)0x140EC0980L);
            if (!IsReadable(tG, 8)) return false;
            ls = *(nint*)tG + (pid - 0xC0) * 0x26E + 4;
            cap = 0x20;
            absBase = 0;
        }
        else
        {
            nint tG = unchecked((nint)0x140EC0978L);
            nint sG = unchecked((nint)0x140EC0958L);
            if (!IsReadable(tG, 8) || !IsReadable(sG, 8)) return false;
            ls = *(nint*)tG + pid * 0x46 + 6;
            cap = 0x10;
            nint rec = *(nint*)sG + pid * 0x0E;
            absBase = IsReadable(rec + 3, 1) ? *(byte*)(rec + 3) : 0;
        }
        if (!IsReadable(ls, cap * 4)) return false;
        int eff = level - absBase;
        for (int i = 0; i < cap; i++)
        {
            int flag = *(byte*)(ls + i * 4 + 1);
            if (flag == 0) break;
            int lv = *(byte*)(ls + i * 4);
            int sid = *(ushort*)(ls + i * 4 + 2);
            if (lv > eff && sid > 0)
            {
                learnLevel = absBase + lv;
                skillId = sid;
                return true;
            }
        }
        return false;
    }

    /// <summary>Skill ids a persona SPECIES has learned by <paramref name="level"/>
    /// (learnset entries with relative level &lt;= effective level). Used to show the
    /// default skills of a persona that isn't in stock (e.g. a fusion-search result).
    /// A persona holds at most 8 skills, so the latest 8 are kept. Same learnset
    /// tables as PersonaNextLearn.</summary>
    internal static List<int> PersonaLearnedSkills(int pid, int level)
    {
        var result = new List<int>();
        if (pid <= 0) return result;
        nint ls; int cap, absBase;
        if (pid >= 0xC0 && pid < 0xD8)
        {
            nint tG = unchecked((nint)0x140EC0980L);
            if (!IsReadable(tG, 8)) return result;
            ls = *(nint*)tG + (pid - 0xC0) * 0x26E + 4;
            cap = 0x20; absBase = 0;
        }
        else
        {
            nint tG = unchecked((nint)0x140EC0978L);
            nint sG = unchecked((nint)0x140EC0958L);
            if (!IsReadable(tG, 8) || !IsReadable(sG, 8)) return result;
            ls = *(nint*)tG + pid * 0x46 + 6;
            cap = 0x10;
            nint rec = *(nint*)sG + pid * 0x0E;
            absBase = IsReadable(rec + 3, 1) ? *(byte*)(rec + 3) : 0;
        }
        if (!IsReadable(ls, cap * 4)) return result;
        int eff = level - absBase;
        for (int i = 0; i < cap; i++)
        {
            int flag = *(byte*)(ls + i * 4 + 1);
            if (flag == 0) break;
            int lv = *(byte*)(ls + i * 4);
            int sid = *(ushort*)(ls + i * 4 + 2);
            if (lv <= eff && sid > 0) result.Add(sid);
        }
        if (result.Count > 8) result = result.GetRange(result.Count - 8, 8);
        return result;
    }

    /// <summary>Displayed persona stats = the SUM of the three 5-byte arrays at
    /// entry+0x1C / +0x21 / +0x26 (base + growth + bonus — the same sum the
    /// level-up code caps at 99, FUN_1400D7A90).</summary>
    internal static int[] PersonaTotalStats(nint entry)
    {
        if (!IsReadable(entry, 0x30)) return null;
        var b = (byte*)entry;
        var r = new int[5];
        for (int i = 0; i < 5; i++)
            r[i] = b[0x1C + i] + b[0x21 + i] + b[0x26 + i];
        return r;
    }

    /// <summary>The "Experience" row shared by the MC and ally panels:
    /// current EXP · EXP to next level · next learned skill + its level.</summary>
    internal static (string label, string[] items) PersonaGrowthRow(nint entry, int pid)
    {
        var items = new List<string>();
        if (IsReadable(entry, 0x30))
        {
            items.Add($"EXP {*(int*)(entry + 0x08)}");
            int next = PersonaNextExp(entry);
            if (next >= 0) items.Add($"next level in {next}");
            int lvl = *(byte*)(entry + 0x04);
            if (PersonaNextLearn(pid, lvl, out int ll, out int sid))
            {
                string sn = null;
                try { sn = Skill.GetName(sid); } catch { }
                items.Add(string.IsNullOrEmpty(sn)
                    ? $"learns skill {sid} at level {ll}"
                    : $"learns {sn} at level {ll}");
            }
            else items.Add("no more skills to learn");
        }
        if (items.Count == 0) items.Add("unknown");
        return ("Experience", items.ToArray());
    }

    /// <summary>Detailed next-learn line: "learns X at level N. Description" —
    /// spoken when the user steps onto the learns item (J/L), so the overview
    /// row stays short. Null if nothing left to learn.</summary>
    internal static string PersonaNextLearnDetail(nint entry, int pid)
    {
        if (!IsReadable(entry, 0x30)) return null;
        int lvl = *(byte*)(entry + 0x04);
        if (!PersonaNextLearn(pid, lvl, out int ll, out int sid)) return null;
        string sn = null, d = null;
        try { sn = Skill.GetName(sid); } catch { }
        try { d = Skill.GetDescription(sid); } catch { }
        if (string.IsNullOrEmpty(sn)) return null;
        return string.IsNullOrEmpty(d)
            ? $"learns {sn} at level {ll}"
            : $"learns {sn} at level {ll}. {d}";
    }

    /// <summary>index-th non-empty skill id of the highlighted stock persona, or -1.</summary>
    internal static int PersonaStockSkillId(int index)
    {
        var (e, _) = PersonaCursor();
        if (!IsReadable(e, 0x30)) return -1;
        short* sk = (short*)((byte*)e + 0x0C);
        int seen = -1;
        for (int s = 0; s < 8; s++)
        {
            int sid = sk[s];
            if (sid <= 0) continue;
            if (++seen == index) return sid;
        }
        return -1;
    }

    /// <summary>"Name. Description" for the index-th non-empty skill of the highlighted
    /// stock persona — the spoken form of the game's "Skill Info" sub-list. Returns null
    /// if out of range.</summary>
    internal static string PersonaSkillDetail(int index)
    {
        var (e, _) = PersonaCursor();
        if (!IsReadable(e, 0x30)) return null;
        short* sk = (short*)((byte*)e + 0x0C);
        int seen = -1;
        for (int s = 0; s < 8; s++)
        {
            int sid = sk[s];
            if (sid <= 0) continue;
            if (++seen != index) continue;
            string snm = Skill.GetName(sid);
            if (string.IsNullOrEmpty(snm)) return null;
            string desc = null;
            try { desc = Skill.GetDescription(sid); } catch { }
            return string.IsNullOrEmpty(desc) ? snm : $"{snm}. {desc}";
        }
        return null;
    }

    // ---- F persona panel, captured at RENDER TIME by PersonaPanelHook (reliable,
    // unlike polling the flickering globals). FPanelOpen() = a renderer ran in the
    // last ~150ms with a valid persona.
    internal static volatile nint FPanelUnit;
    internal static long FPanelTick;

    /// <summary>The persona affinity widget renderer ran for this party unit (set
    /// at render time by PersonaPanelHook). The panel's affinities come from the
    /// unit's live stat node, NOT a persona-id table — that's the reliable source.</summary>
    internal static void SetFPanelUnit(nint unit) { FPanelUnit = unit; FPanelTick = Environment.TickCount64; }

    internal static bool FPanelOpen()
        => Environment.TickCount64 - FPanelTick < 150 && IsReadable(FPanelUnit, 0xCF8) && UnitSide(FPanelUnit) == 0;

    /// <summary>Live element affinity text for a unit (party persona panel — no
    /// discovery gating, you always know your own): GetAffinityResult on the unit's
    /// stat node, classified.</summary>
    internal static string UnitAffinityText(nint statNode, int elemId, string name)
    {
        var aff = _getAffinity;
        if (aff == null) return $"{name} unknown";
        uint r = 0;
        try { r = aff(statNode, elemId); } catch { }
        return $"{name} {ClassifyAffinity(r)}";
    }

    /// <summary>Arcana name for a persona species (table *(0x140EC0958), stride
    /// 0x0E, arcana byte at +0x02, 1-based). Null if unreadable.</summary>
    internal static string PersonaArcanaName(int personaId)
    {
        nint tG = unchecked((nint)0x140EC0958L);
        if (personaId < 0 || !IsReadable(tG, 8)) return null;
        nint rec = *(nint*)tG + personaId * 0x0E;
        if (!IsReadable(rec + 2, 1)) return null;
        int aid = *(byte*)(rec + 2);
        return ArcanaName(aid);
    }

    /// <summary>The persona BtlUnitInfo* shown on the in-battle Persona DETAIL
    /// panel, or 0 when that panel isn't open. Gates: Turn+0xAC==5 (Persona
    /// command) and BtlInfo+0xD18!=0 (a specific persona's detail is shown). This
    /// is mutually exclusive with the Analyze panel (S+0x52F2). From the panel
    /// setup 0x1400EBA10 / renderer 0x1400EC280.</summary>
    internal static nint PersonaDetailUnit()
    {
        nint info = ActiveBtlInfo;
        if (!IsReadable(info, 0xD20)) return 0;
        nint turn = *(nint*)(info + 0xCB8);
        if (!IsReadable(turn + 0xAE, 2) || *(ushort*)(turn + 0xAC) != 5) return 0;
        if (*(ushort*)(info + 0xD18) == 0) return 0;
        nint unit = *(nint*)(info + 0xD10);
        return IsReadable(unit, 0xCF8) ? unit : 0;
    }

    /// <summary>Find the MC's persona-stock entry (PersonaInfo*) for a species id,
    /// for level/stats/skills. 0 if not in stock.</summary>
    internal static nint FindStockPersonaEntry(int personaId)
    {
        nint g = unchecked((nint)0x141165900L);
        if (personaId < 0 || !IsReadable(g, 8)) return 0;
        nint baseObj = *(nint*)g;
        if (baseObj == 0) return 0;
        for (int i = 0; i < 12; i++)
        {
            nint e = baseObj + 0xA34 + i * 0x30;
            if (!IsReadable(e, 0x30)) continue;
            if (*(byte*)e == 0) continue;            // not registered
            if (*(short*)(e + 2) == personaId) return e;
        }
        return 0;
    }

    /// <summary>Element affinity text for a persona species. Table pointer at
    /// *(0x140EC0988), indexed by row*0x10 + elem (shorts), decoded by the same packing
    /// as the game (FUN_1400d6660 / FUN_1400d34c0). The row is the species index from
    /// the persona's species object (FUN_15ee84bc0) — see PersonaAffinityRow.</summary>
    internal static string PersonaElementAffinityText(int personaId, int elemId, string name)
    {
        nint tG = unchecked((nint)0x140EC0988L);
        if (personaId < 0 || !IsReadable(tG, 8)) return $"{name} unknown";
        nint addr = *(nint*)tG + personaId * 0x20 + elemId * 2;
        if (!IsReadable(addr, 2)) return $"{name} unknown";
        ushort w = *(ushort*)addr;
        return $"{name} {ClassifyPersonaRaw(w)}";
    }

    // ---- Analyze panel detection (global chain off the battle manager 0x140EC08F0).
    // S = *(*(mgr+0x14E8)+0x48); open flag at S+0x52F2 (set on open, zeroed on close).
    // Selected enemy: S+0x5308 -> group; idx = *(u16)(group+0x6A);
    //   unit = *(*(group + idx*8) + 0x38). All reads guarded — mgr is null outside
    // battle and group can be null between frames.

    private static nint AnalyzeSubObject()
    {
        nint mgrG = unchecked((nint)0x140EC08F0L);
        if (!IsReadable(mgrG, 8)) return 0;
        nint mgr = *(nint*)mgrG;
        if (!IsReadable(mgr + 0x14E8, 8)) return 0;
        nint inner = *(nint*)(mgr + 0x14E8);
        if (!IsReadable(inner + 0x48, 8)) return 0;
        nint s = *(nint*)(inner + 0x48);
        return IsReadable(s + 0x52F2, 2) ? s : 0;
    }

    /// <summary>Weakness/affinity note for the enemy <paramref name="unit"/> against the element of
    /// the command currently being set up (Attack = Physical, Skill = the selected skill's element).
    /// Returns "weak"/"resists"/"nulls"/"drains"/"repels"/"blocks", or null when the element is
    /// non-damaging, irrelevant (Item/other), or the affinity hasn't been DISCOVERED yet (only hitting
    /// the enemy with that element reveals it — <see cref="KnownAffinity"/> gates on that, matching the
    /// game's mark; Analyze only displays what's already discovered).</summary>
    internal static string TargetWeaknessNote(nint unit)
    {
        int cmd = CurrentCommand;
        int elem;
        if (cmd == 3) elem = 0;                          // Attack = Physical
        else if (cmd == 4)                               // Skill = the selected skill's element
        {
            if (SelectedSkillId < 0) return null;
            try { elem = (int)Native.Skill.GetSkillElement(SelectedSkillId); } catch { return null; }
        }
        else return null;                                // Item / other — no element to compare
        if (elem < 0 || elem == 5 || elem > 7) return null;   // Almighty (5) + status/heal/support: no grid

        return KnownAffinity(unit, elem) switch
        {
            "weak"   => "weak",
            "resist" => "resists",
            "null"   => "nulls",
            "drain"  => "drains",
            "repel"  => "repels",
            _        => null,
        };
    }

    /// <summary>True while the in-battle Analyze profile panel is on screen.</summary>
    internal static bool AnalyzePanelOpen()
    {
        nint s = AnalyzeSubObject();
        return s != 0 && *(ushort*)(s + 0x52F2) != 0;
    }

    /// <summary>The unit whose turn is being resolved right now (Turn+0x38), or
    /// 0. Fully guarded — safe from a background poll thread.</summary>
    internal static nint ActingUnit()
    {
        nint info = ActiveBtlInfo;
        if (info == 0 || !IsReadable(info + 0xCB8, 8)) return 0;
        nint turn = *(nint*)(info + 0xCB8);
        if (!IsReadable(turn + 0x38, 8)) return 0;
        nint unit = *(nint*)(turn + 0x38);
        return IsReadable(unit, 0xCF8) ? unit : 0;
    }

    /// <summary>The enemy unit currently shown on the Analyze panel, or 0 when the
    /// panel is closed / not resolvable.</summary>
    internal static nint AnalyzeSelectedEnemy()
    {
        nint s = AnalyzeSubObject();
        if (s == 0 || *(ushort*)(s + 0x52F2) == 0) return 0;
        if (!IsReadable(s + 0x5308, 8)) return 0;
        nint group = *(nint*)(s + 0x5308);
        if (!IsReadable(group + 0x6A, 2)) return 0;
        ushort idx = *(ushort*)(group + 0x6A);
        nint slotPtr = group + idx * 8;
        if (!IsReadable(slotPtr, 8)) return 0;
        nint slot = *(nint*)slotPtr;
        if (!IsReadable(slot + 0x38, 8)) return 0;
        nint unit = *(nint*)(slot + 0x38);
        return IsReadable(unit, 0xCF8) ? unit : 0;
    }

    /// <summary>Unit side: 1 = enemy, 0 = party, -1 if unreadable.</summary>
    internal static int UnitSide(nint unit)
        => IsReadable(unit + 0xA2, 1) ? *(byte*)(unit + 0xA2) : -1;

    /// <summary>True if a party unit has a real equipped persona (valid id + name).
    /// Guards against junk side-0 "units" (e.g. during Shuffle Time) that would
    /// otherwise read as "000, Fool, level unknown".</summary>
    internal static bool HasRealPersona(nint unit)
    {
        if (!IsReadable(unit + 0xA4, 2)) return false;
        int pid = *(ushort*)(unit + 0xA4);
        if (pid <= 0 || pid > 1000) return false;
        string n = Persona.GetName(pid);
        return !string.IsNullOrWhiteSpace(n) && n != "000";
    }

    /// <summary>Persona profile for a PARTY unit (the analyze/status panel shows a
    /// party member's persona). Element affinities from the persona species
    /// tables; level from the unit's stat node; stats from the MC stock when the
    /// persona is in it.</summary>
    internal static (string label, string[] items) PersonaRow(nint unit, int row)
    {
        int pid = IsReadable(unit + 0xA4, 2) ? *(ushort*)(unit + 0xA4) : -1;
        switch (row)
        {
            case 0:
            {
                var items = new List<string>();
                string name = Persona.GetName(pid);
                if (!string.IsNullOrEmpty(name)) items.Add(name);
                string arcana = PersonaArcanaName(pid);
                if (!string.IsNullOrEmpty(arcana)) items.Add(arcana);
                int lvl = EnemyLevel(unit); // statNode+0x06 = level for any unit
                items.Add(lvl >= 0 ? $"level {lvl}" : "level unknown");
                return (null, items.ToArray());
            }
            case 1:
            {
                var items = new List<string>();
                foreach (var (eid, nm) in _profileElements)
                    items.Add(PersonaElementAffinityText(pid, eid, nm));
                return ("Elements", items.ToArray());
            }
            case 2:
            {
                nint e = FindStockPersonaEntry(pid);
                if (!IsReadable(e, 0x30)) return ("Stats", new[] { "unknown" });
                var b = (byte*)e + 0x1C;
                return ("Stats", new[]
                {
                    $"Strength {b[0]}", $"Magic {b[1]}", $"Endurance {b[2]}",
                    $"Agility {b[3]}", $"Luck {b[4]}"
                });
            }
        }
        return (null, Array.Empty<string>());
    }

    /// <summary>True if the unit is knocked down / dizzy (status &amp; 0x100000).</summary>
    internal static bool IsUnitDown(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return false;
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 0x10)) return false;
        return (*(uint*)(stat + 0x0C) & 0x100000) != 0;
    }

    // ---- status ailments (statNode+0x0C u32) -------------------------------
    // Known bits: 0x80000 dead, 0x100000 down. The per-ailment bits (Sleep,
    // Poison, Silence, …) are being MAPPED: DamageMonitor logs every status
    // change next to the game's infliction banner that names it. Until the
    // table is filled, readers speak a generic "status effect".
    internal const uint StatusDead = 0x80000;
    internal const uint StatusDown = 0x100000;

    /// <summary>Raw status word from a stat node (0 if unreadable).</summary>
    internal static uint StatusRaw(nint stat)
        => IsReadable(stat, 0x10) ? *(uint*)(stat + 0x0C) : 0;

    /// <summary>Raw status word for a unit (via its stat node), 0 if unreadable.</summary>
    internal static uint UnitStatusOf(nint unit)
    {
        if (!IsReadable(unit, 0xCF8)) return 0;
        return StatusRaw(*(nint*)(unit + 0xCF0));
    }

    /// <summary>The unresolved ailment/state bits (status minus dead/down).</summary>
    internal static uint AilmentBits(uint status) => status & ~(StatusDead | StatusDown);

    /// <summary>Spoken ailment for a status word, or null when none. Generic
    /// until the bit map is learned from the logs; add entries to
    /// _ailmentNames as they're confirmed.</summary>
    internal static string AilmentText(uint status)
    {
        uint bits = AilmentBits(status);
        if (bits == 0) return null;
        foreach (var (bit, name) in _ailmentNames)
            if ((bits & bit) != 0) return name;
        return "status effect";
    }

    /// <summary>The tactics options in the game's canonical ORDER_SEL order
    /// (init_free_006.msg); index = the tactic value at party record+0x10.</summary>
    internal static readonly string[] TacticNames =
    {
        "Act Freely", "Full Assault", "Conserve SP", "Heal/Support",
        "Direct Commands", "Don't change tactics",
    };

    /// <summary>A party member's current tactic name from record+0x10, or null.</summary>
    internal static string MemberTacticName(nint record)
    {
        if (!IsReadable(record + 0x10, 1)) return null;
        int t = *(byte*)(record + 0x10);
        return t >= 0 && t < TacticNames.Length ? TacticNames[t] : null;
    }

    // Confirmed ailment bits go here as (bit, spoken name); first match wins.
    // 0x4 = Fear — confirmed 2026-06-10: Evil Touch landed, banner said afraid,
    // [Status] logged 0x0 -> 0x4 (and the enemy then fled: 0x4 -> 0x80000).
    // 0x1 = Dizzy — confirmed 2026-06-11: HARU knocked down, banner said dizzy,
    // [Status] logged 0x0 -> 0x100001 (Down 0x100000 + the new 0x1).
    private static readonly (uint bit, string name)[] _ailmentNames =
    {
        (0x4, "afraid"),
        (0x1, "dizzy"),
        (0x20, "poisoned"),   // CONFIRMED 2026-06-17 (MC "YUKI 0x0 -> 0x20" = Poison)
        (0x8, "silenced"),    // CONFIRMED 2026-06-18 (MC "YUKI 0x0 -> 0x8" = Silence)
        (0x80, "enervated"),  // CONFIRMED 2026-06-29 (Yukiko "0x0 -> 0x80" + "Enervation" bubble)
        (0x2, "enraged"),     // CONFIRMED 2026-07-03 (Marukyu: Chie/Kanji/Haru "0x0 -> 0x2" = Rage, user-seen)
    };

    /// <summary>Set by the F12 diagnostic to ask the next battle frame (game thread)
    /// to dump the active unit's NameRecord so we can decode the name string.</summary>
    internal static volatile bool RequestNameDump;

    /// <summary>
    /// Live pointer to the current battle's <see cref="BtlInfo"/>, cached every
    /// time the battle UI main processor runs. Zero when not in battle / before
    /// the first frame. Consumers (diagnostics, HP/SP readout) must re-validate
    /// with <c>IsReadable</c> before dereferencing — the allocation is freed when
    /// the battle ends.
    /// ⚠ The raw pointer is NEVER cleared by the hook (it only runs in battle),
    /// so it goes STALE after every battle. The getter therefore returns 0
    /// unless the field major says a battle is running (the 220-299 band) —
    /// without this, TurnReader/MultiTargetReader polled the freed struct during
    /// normal play and spoke garbage once the heap page was reused (the
    /// player-reported "random numbers and text, fixed by restart" bug,
    /// 2026-07-07: e.g. "16024's turn").
    /// </summary>
    internal static nint ActiveBtlInfo
    {
        get => FieldTracker.InBattle ? _activeBtlInfoRaw : 0;
        set => _activeBtlInfoRaw = value;
    }
    private static volatile nint _activeBtlInfoRaw;

    internal Battle(IReloadedHooks hooks)
    {
        _messageBubble = new MessageBubble(hooks);
        _skillSelect = new SkillSelect(hooks);
        _itemSelect = new ItemSelect(hooks);
        _personaSelect = new PersonaSelect(hooks);
        _personaPanelHook = new PersonaPanelHook(hooks);
        _personaMenuHook = new PersonaMenuHook(hooks);
        _skillInfoSelect = new SkillInfoSelect(hooks);
        _resultReader = new ResultReader(hooks);
        _shuffleReader = new ShuffleReader();
        _shuffleText = new ShuffleText(hooks);
        _enemyActionHook = new EnemyActionHook(hooks);
        // Tactics member screen: NO reader by user decision (2026-06-11) — the
        // real cursor is render-only and the virtual key-tracking cursor was
        // removed. The options-entry header (SkillInfoSelect) names the picked
        // member + current tactic, which is the supported flow.
        // SkillInfoReader (native F skill-cursor signature scan) REMOVED
        // 2026-06-10 at user request: the heap widget is recreated per panel
        // open and the scan was fragile/heavy (and starved ShuffleReader before
        // the state gate). The panel's Skills row (I/K to Skills, J/L per skill)
        // is the supported reading path. Signature + snapshots are documented in
        // BATTLE_SYSTEM.md if this is ever revisited.

        SigScan("48 8B C4 48 89 48 ?? 53 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 08 01 00 00", "Btl::UI::Main",
            address => { _processHook = hooks.CreateHook<ProcessDelegate>(Process, address).Activate(); });

        SigScan("40 53 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 83 A2 00 00 00",
            "Btl::GetUnitName",
            address => { _getUnitName = hooks.CreateWrapper<GetUnitNameDelegate>(address, out _); });

        // Pure party max-HP / max-SP getters (rcx = stat node). Acquired from
        // their absolute VAs (ASLR off). Pointer-type still being verified via the
        // F12 probe before any always-on use.
        _getMaxHp = hooks.CreateWrapper<GetMaxDelegate>(unchecked((nint)0x1400CF3F0L), out _);
        _getMaxSp = hooks.CreateWrapper<GetMaxDelegate>(unchecked((nint)0x1400CF480L), out _);
        // Affinity classifier GetAffinityResult(statNode, element) — packed u32
        // (low16 = dmg mult%, high bits = null/drain/repel/block). For the enemy
        // profile. Verifying via the F12 probe before always-on use.
        _getAffinity = hooks.CreateWrapper<GetAffinityDelegate>(unchecked((nint)0x1400D35B0L), out _);
        _getAffinityKnown = hooks.CreateWrapper<GetKnownDelegate>(unchecked((nint)0x1400993F0L), out _);
        _personaExpForLevel = hooks.CreateWrapper<PersonaExpForLevelDelegate>(unchecked((nint)0x1400D8050L), out _);

        // Enemy-analyze SKILLS: getter FUN_1400d4320 + a hook on the analyze render
        // FUN_1400e0e50 that publishes the reveal state (see CaptureAnalyzeSkills).
        _getEnemySkills = hooks.CreateWrapper<GetSkillArrayDelegate>(unchecked((nint)0x1400D4320L), out _);
        _analyzeHook = hooks.CreateHook<AnalyzeRenderDelegate>(OnAnalyzeRender, unchecked((nint)0x1400E0E50L)).Activate();
    }

    private void Process(BtlCommandsInfo* commands, BtlInfo* info, float* param_3)
    {
        _processHook.OriginalFunction(commands, info, param_3);

        // Cache the battle-info pointer so on-demand readouts (HP/SP, diagnostics)
        // can reach the unit list from a background poll thread.
        ActiveBtlInfo = (nint)info;
        CurrentCommand = (short)commands->SelectedCommand;


        if (RequestNameDump && _getUnitName != null)
        {
            RequestNameDump = false;
            try { DumpActiveUnitName(info); DumpBattleEnumeration(); DumpPanelFlags(); }
            catch (Exception ex) { Log($"[BtlDiag] name dump error: {ex.GetType().Name}: {ex.Message}"); }
        }

        var selectedCommand = commands->SelectedCommand;
        if (_lastSelectedCommand != selectedCommand)
        {
            var command = _commandNames[selectedCommand];

            Log($"Outputting currently selected battle command \"{command}\"");
            TitleMenu.GameEventFired();
            Speech.Say(command, true);

            _lastSelectedCommand = selectedCommand;
        }
    }

    /// <summary>
    /// Diagnostic: call GetUnitName on the active unit and hex-dump the returned
    /// NameRecord so we can locate the name string (and decode the custom MC name).
    /// Game-thread only (called from Process).
    /// </summary>
    private void DumpActiveUnitName(BtlInfo* info)
    {
        var turn = info->Turn;
        if (turn == null) { Log("[BtlDiag] name dump: no turn"); return; }
        var unit = turn->Unit;
        if (unit == null) { Log("[BtlDiag] name dump: no active unit"); return; }

        nint rec = _getUnitName((nint)unit);
        Log($"[BtlDiag] GetUnitName(unit 0x{(nint)unit:X}) -> NameRecord=0x{rec:X}");
        if (!IsReadable(rec, 0x40)) { Log("[BtlDiag] name dump: NameRecord not readable"); return; }

        var b = (byte*)rec;
        for (int off = 0; off < 0x40; off += 16)
        {
            var hex = new System.Text.StringBuilder();
            var asc = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                byte v = b[off + i];
                hex.Append($"{v:X2} ");
                asc.Append(v >= 0x20 && v < 0x7F ? (char)v : '.');
            }
            Log($"[BtlDiag] name+0x{off:X2}: {hex.ToString().TrimEnd()}  |{asc}|");
        }
    }

    /// <summary>
    /// Verification probe for the battle-unit RE findings (read-only, fully
    /// guarded — can't crash even if the chain interpretation is wrong). Walks
    /// the combatant list from the manager global and logs each unit's side,
    /// HP/SP/status, enemy max-HP table entry, and name. Game-thread only.
    /// Manager 0x140EC08F0 -> list head +0x1C0, next +0x520, unit +0x38;
    /// unit+0xA2 = side (1=party); statnode = unit+0xCF0 (+0x08 HP, +0x0A SP,
    /// +0x0C status: &amp;0x60 down, &amp;0x80000 dead; +0x02 enemyId).
    /// Enemy max table: *0x140EC0920 + enemyId*0x3C + {0x4 maxHP, 0x6 maxSP}.
    /// </summary>
    private void DumpBattleEnumeration()
    {
        nint mgrGlobal = unchecked((nint)0x140EC08F0L);
        nint enemyTbl = unchecked((nint)0x140EC0920L);
        if (!IsReadable(mgrGlobal, 8)) { Log("[BtlProbe] 0x140EC08F0 unreadable"); return; }

        nint mgrPtr = *(nint*)mgrGlobal;
        Log($"[BtlProbe] [0x140EC08F0]=0x{mgrPtr:X}");

        // Two readings of the list head: manager-as-pointer vs manager-as-base.
        nint headA = IsReadable(mgrPtr + 0x1C0, 8) ? *(nint*)(mgrPtr + 0x1C0) : 0;
        EnumerateFrom(headA, enemyTbl, "A");
        nint headB = IsReadable(mgrGlobal + 0x1C0, 8) ? *(nint*)(mgrGlobal + 0x1C0) : 0;
        if (headB != headA) EnumerateFrom(headB, enemyTbl, "B");
    }

    private void EnumerateFrom(nint node, nint enemyTbl, string tag)
    {
        if (node == 0) { Log($"[BtlProbe-{tag}] null head"); return; }
        int i = 0;
        while (node != 0 && i < 24 && IsReadable(node, 0x528))
        {
            nint unit = *(nint*)(node + 0x38);
            var sb = new System.Text.StringBuilder($"[BtlProbe-{tag}][{i}] node=0x{node:X} unit=0x{unit:X}");
            if (IsReadable(unit, 0xCF8))
            {
                byte side = *(byte*)(unit + 0xA2);
                nint stat = *(nint*)(unit + 0xCF0);
                sb.Append($" side={side} stat=0x{stat:X}");
                if (IsReadable(stat, 0x10))
                {
                    ushort hp = *(ushort*)(stat + 8), spv = *(ushort*)(stat + 0xA), eid = *(ushort*)(stat + 2);
                    uint status = *(uint*)(stat + 0xC);
                    // side: 1 = enemy, 0 = party (confirmed). down bit = 0x100000.
                    sb.Append($" HP={hp} SP={spv} eid={eid} status=0x{status:X} down={(status & 0x100000) != 0} dead={(status & 0x80000) != 0}");
                    // Resistance nibbles (RE: null bitfield +0x14; base res +0x1C..; buff +0x25..).
                    // Pure dump to pin element numbering vs shadow_table.json.
                    if (IsReadable(stat + 0x14, 0x1A))
                    {
                        var rb = new System.Text.StringBuilder();
                        for (int o = 0x1C; o < 0x2E; o++) rb.Append($"{*(byte*)(stat + o):X2}");
                        sb.Append($" null=0x{*(ushort*)(stat + 0x14):X4} res[1C-2D]={rb}");
                    }
                    // Call the affinity classifier per element to read the true
                    // resistance profile + pin element numbering.
                    if (_getAffinity != null)
                    {
                        var ab = new System.Text.StringBuilder();
                        foreach (int e in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18 })
                        {
                            uint r = 0;
                            try { r = _getAffinity(stat, e); } catch { }
                            ab.Append($"{e}:{r:X8} ");
                        }
                        sb.Append($" aff[{ab.ToString().Trim()}]");
                    }
                    if (side == 1 && IsReadable(enemyTbl, 8))
                    {
                        nint et = *(nint*)enemyTbl;
                        nint rec = et + eid * 0x3C;
                        if (IsReadable(rec + 8, 8))
                            sb.Append($" eMaxHP={*(ushort*)(rec + 4)} eMaxSP={*(ushort*)(rec + 6)}");
                    }
                    else if (side == 0 && IsReadable(stat, 0x100) && _getMaxHp != null && _getMaxSp != null)
                    {
                        // Verify the party max getters (controlled F12 call only).
                        int pmh = -1, pms = -1;
                        try { pmh = _getMaxHp(stat); pms = _getMaxSp(stat); } catch { }
                        sb.Append($" pMaxHP={pmh} pMaxSP={pms}");
                    }
                }
                if (_getUnitName != null)
                {
                    nint nrec = _getUnitName((nint)unit);
                    sb.Append($" name=\"{DecodeAtlusName(nrec)}\"");
                }
            }
            Log(sb.ToString());

            nint next = IsReadable(node + 0x520, 8) ? *(nint*)(node + 0x520) : 0;
            if (next == node) break;
            node = next;
            i++;
        }
        if (i == 0) Log($"[BtlProbe-{tag}] head 0x{node:X} yielded no readable nodes");
    }

    private static string DecodeAtlusName(nint addr)
    {
        if (!IsReadable(addr, 0x40)) return null;
        var b = (byte*)addr;
        int len = 0;
        while (len < 0x3E) { byte lead = b[len]; if (lead == 0) break; len += lead >= 0x80 ? 2 : 1; }
        if (len == 0) return null;
        try { return AtlusEncoding.P4.GetString(b, len).Trim('\0', ' '); } catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
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

    internal delegate nint GetUnitNameDelegate(nint unit);

    [StructLayout(LayoutKind.Explicit)]
    internal struct BtlInfo
    {
        [FieldOffset(0xd1a)] internal fixed short ActiveMemberSkills[8];

        [FieldOffset(0xcb8)] internal BtlTurnInfo* Turn;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct BtlCommandsInfo
    {
        [FieldOffset(4)] internal Command SelectedCommand;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct BtlTurnInfo
    {
        [FieldOffset(0x38)] internal BtlUnitInfo* Unit;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct BtlUnitInfo
    {
        [FieldOffset(0xcf0)] internal PartyMember.PartyMemberInfo* PartyMemberInfo;
    }

    internal enum Command : short
    {
        None = -1,
        Analysis = 0,
        Tactics = 1,
        Guard = 2,
        Attack = 3,
        Skill = 4,
        Persona = 5,
        Item = 6,
        Escape = 7,
    }

    private delegate void ProcessDelegate(BtlCommandsInfo* commands, BtlInfo* info, float* param_3);
}