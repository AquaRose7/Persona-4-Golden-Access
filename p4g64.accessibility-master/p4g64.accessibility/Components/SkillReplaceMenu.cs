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
/// frame; this SAME drawer also renders a persona's skill grid in the battle Persona
/// detail, so gate carefully). Its 4th arg (param_4 / r9) is the menu struct:
///   +0x04  byte   cursor (0..count-1 = a current skill; == count = the incoming-skill slot)
///   +0x68  u16    count of current skills (8 when full)
///   +0x0A + i*0xC  u16  current skill id for row i  (-> Skill.GetName/GetDescription)
///   +0x6E  u16    the persona's NEXT LEVEL-UP skill — correct as "the incoming skill" ONLY in
///                 the battle/result LEVEL-UP flow. ⚠ For field flows (S.Link / book / scooter)
///                 it is the WRONG skill (the Amrita bug, fixed 2026-07-06): the whole struct is
///                 just current-8 + the future level-learn table (levels at +0x1EC), and the true
///                 incoming id exists NOWHERE stable in memory (proven: full struct dumps, drawer
///                 decompile — the special row only draws the "?" boxes — and an A/B memory hunt).
///                 Field flows resolve the incoming skill from the DRAWN panel name instead (the
///                 UI-text capture below). NOTE: +0x232 was an older wrong guess — battle detail
///                 held 0x600+nextSkill there; the learn screen reads 0x600 (nothing).
/// cursor 0..count-1 reads the current skill; cursor==count reads the incoming skill ONLY when
/// full (count>=8), plain name+description (no "do not learn"). Non-full = auto-fills, so that
/// slot stays silent. Deduped on cursor.
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

    // FUN_140428D80(p1, p2, p3, menu, p5, p6, p7, p8). p2 is a full pointer for
    // the SPECIAL row (p5=0: the S.Link/incoming-skill row passes a context
    // record here — 07-06 arg capture) and 0 for plain skill rows.
    private delegate void RowDelegate(nint p1, nint p2, byte p3, nint menu, byte p5,
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

        // Shared UI-text renderer — the incoming-skill NAME source for field flows.
        // Same VA the Quest/Compendium/SL-detail readers hook.
        try
        {
            _textHook = hooks.CreateHook<SetTextDelegate>(OnUiText, SetUiTextVA).Activate();
            Log("[SkillReplace] UI-text capture hook active (incoming-skill name)");
        }
        catch (Exception e) { Log($"[SkillReplace] text hook failed: {e.Message}"); }

        // Pre-warm the name→id map off-thread (retrying until the game's table
        // resolves) so the first capture doesn't stall the announce (user
        // 2026-07-06: "lagging a little before reading").
        new Thread(() =>
        {
            for (int i = 0; i < 60 && _nameToId == null; i++)
            {
                Thread.Sleep(2000);
                try { EnsureNames(); } catch { }
            }
        })
        { IsBackground = true, Name = "SkillNameWarm" }.Start();
    }

    private void OnRow(nint p1, nint p2, byte p3, nint menu, byte p5, float p6, float p7, int p8)
    {
        _hook!.OriginalFunction(p1, p2, p3, menu, p5, p6, p7, p8);
        long t0 = PerfDiag.Begin();
        try { Read(menu); } catch { /* never let a hook throw */ }
        PerfDiag.End(PerfDiag.B.SkillRepRow, t0);
    }

    // ── Incoming-skill NAME capture (2026-07-06, v1.4.0 bug 2) ───────────────
    // For NON-level-up flows (S.Link / book / scooter) the menu struct does NOT
    // hold the incoming skill (see the header). The one reliable source is what
    // the game DRAWS: the S.Link panel renders the skill's NAME through the
    // shared UI-text fn FUN_140450C60. p6 == 0 calls carry a COMPLETE string
    // (names/labels/headers — the ShuffleText/Compendium finding); p6 != 0
    // streams glyphs (long text bodies), never a bare skill name. EXACT
    // whole-string match only — the first-cut rolling-soup capture matched junk
    // table entries INSIDE other words ("iko" out of "Yukiko", user 2026-07-06).
    // Gated to while the replace screen renders so it costs nothing elsewhere.
    private IHook<SetTextDelegate>? _textHook;
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);

    private readonly Dictionary<int, long> _drawnSkills = new();   // skill id -> last-drawn tick
    private bool _capWasActive;
    private long _pendingIncomingSince;                            // first-frame hold (see Read)
    private long _poolFloorMs;                                     // drawn names at/before this tick belong to a PREVIOUS prompt
    private int _resolvedIncoming;                                 // what this prompt announced — re-entering the slot repeats it
    private int _holdFirstLvlNext;                                 // +0x6E as seen on the FIRST held frame (staleness diag)
    private static Dictionary<string, int>? _nameToId;             // EXACT name -> id, lazy
    private bool _wantNames;                                       // capture drawn names only during the incoming-skill hold

    private nint OnUiText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _textHook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        long t0 = PerfDiag.Begin();
        try
        {
            if (!RecentlyActive)
            {
                if (_capWasActive) { _capWasActive = false; _drawnSkills.Clear(); _resolvedIncoming = 0; _wantNames = false; }
                return ret;
            }
            _capWasActive = true;
            // Only while an incoming name is being WAITED for (2026-10-02): this hook sees every
            // drawn string (~3000/s on this screen) and decoding them all cost the game thread
            // ~2.4s of every 3s for as long as the screen was up.
            if (p6 != 0 || !_wantNames) return ret;
            string s = ReadCString(p1, 48).Trim();
            if (s.Length < 3 || s.Length > 32) return ret;
            if (_nameToId == null) PerfDiag.Bump(PerfDiag.B.EnsureNamesLoop);
            EnsureNames();
            if (_nameToId != null && _nameToId.TryGetValue(s, out int id))
                _drawnSkills[id] = Environment.TickCount64;
        }
        catch { /* never let a hook throw */ }
        finally { PerfDiag.End(PerfDiag.B.SkillRepText, t0); }
        return ret;
    }

    private static void EnsureNames()
    {
        if (_nameToId != null) return;
        var map = new Dictionary<string, int>();
        for (int v = 1; v <= 1024; v++)
        {
            string nm = Skill.GetName(v);
            if (string.IsNullOrEmpty(nm) || nm.Length < 3 || nm.StartsWith("?")) continue;
            if (map.TryGetValue(nm, out int prev))
            {
                // DUPLICATE NAME (Teddie bug 2026-07-28): "Kamui Miracle" appears ×10 in the
                // skill table — a 9-entry dummy block (help text = the literal "Skill06D"-style
                // placeholder) + the REAL one. First-id-wins mapped the drawn name to a dummy,
                // so the camp panel announced the dummy as the incoming skill ("Kamui Miracle.
                // Skill06D") instead of the true next skill. On a collision keep the id whose
                // help text is REAL; among equals keep the first.
                if (HasRealDesc(prev) || !HasRealDesc(v)) continue;
                map[nm] = v;
            }
            else map.Add(nm, v);
        }
        if (map.Count < 300) return;                       // table not resolved yet — retry later
        _nameToId = map;
    }

    /// <summary>True when the skill's help text is genuine — the table's dummy/reserved
    /// entries carry a "Skill06D"-style placeholder (or nothing) as their description.</summary>
    private static bool HasRealDesc(int id)
    {
        string d = Skill.GetDescription(id);
        return !string.IsNullOrEmpty(d) && !IsDummyText(d);
    }

    private static bool IsDummyText(string s)
        => System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^Skill[0-9A-Fa-f]{3}$");

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

        string body;
        if (cursor < count)
        {
            _wantNames = false;
            // A current skill (the 8-slot array: id at 0x0A + i*0xC).
            if (!IsReadable(menu + 0x0A + cursor * 0xC, 2)) return;
            int id = *(short*)(menu + 0x0A + cursor * 0xC);
            string nm = (id >= 1 && id <= 1024) ? Skill.GetName(id) : $"Skill {cursor + 1}";
            string desc = (id >= 1 && id <= 1024) ? Skill.GetDescription(id) : "";
            if (IsDummyText(desc)) desc = "";   // never speak a "Skill06D" placeholder
            body = string.IsNullOrEmpty(desc)
                ? $"{nm}. {cursor + 1} of {count}"
                : $"{nm}. {desc}. {cursor + 1} of {count}";
        }
        else
        {
            // cursor == count: the INCOMING skill slot. NON-FULL persona: there is no
            // replace here (a new skill auto-fills), and +0x6E is simply the persona's
            // next LEVEL-UP skill — now that its meaning is proven, announce it AS that
            // (user 2026-07-06: the old silence made the next-level skill unreadable).
            if (count < 8)
            {
                int nx = IsReadable(menu + 0x6E, 2) ? *(ushort*)(menu + 0x6E) : 0;
                if (nx < 1 || nx > 1024) return;
                string nnm = Skill.GetName(nx);
                if (string.IsNullOrEmpty(nnm) || nnm.StartsWith("?")) return;
                string nds = Skill.GetDescription(nx);
                if (IsDummyText(nds)) nds = "";   // never speak a "Skill06D" placeholder
                Speech.Say(string.IsNullOrEmpty(nds)
                    ? $"Next level skill: {nnm}."
                    : $"Next level skill: {nnm}. {nds}.", interrupt: true);
                return;
            }
            // FULL persona — the real replace screen. ONE RULE FOR BOTH FLOWS (2026-09-04,
            // player reports "an ally's new skill sometimes reads wrong"): the incoming skill
            // is the freshest DRAWN skill name that (a) is not one of the current 8 by id OR
            // name (Teddie twin bug), and (b) was drawn AFTER this prompt opened — names left
            // over from a PREVIOUS prompt (another ally's learn in the same result, the first
            // of two skills learned at once) are fenced off by _poolFloorMs. Within the newest
            // draw batch prefer an id ≠ +0x6E: the screen also draws +0x6E's name invisibly
            // (the Amrita bug) so when the pool holds both, the OTHER one is the incoming.
            // +0x6E alone (the persona's next LEVEL skill) was the whole battle path before —
            // proven for a single level-up learn, never for the ally multi-learn cases — so it
            // stays as the timed-out FALLBACK only.
            int lvlNext = IsReadable(menu + 0x6E, 2) ? *(ushort*)(menu + 0x6E) : 0;
            int nid = lvlNext;
            bool battle = FieldTracker.IsBattleMajor(FieldTracker.CurrentMajor);
            var current = new HashSet<int>();
            var currentNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count && i < 8; i++)
                if (IsReadable(menu + 0x0A + i * 0xC, 2))
                {
                    int cid = *(ushort*)(menu + 0x0A + i * 0xC);
                    current.Add(cid);
                    string cnm = (cid >= 1 && cid <= 1024) ? Skill.GetName(cid) : "";
                    if (!string.IsNullOrEmpty(cnm)) currentNames.Add(cnm);
                }
            long now = Environment.TickCount64;
            long best = 0; int bestId = 0;
            var pool = new System.Text.StringBuilder();
            foreach (var kv in _drawnSkills)
            {
                pool.Append(kv.Key).Append('(').Append(Skill.GetName(kv.Key)).Append(")@")
                    .Append(now - kv.Value).Append("ms ");
                if (now - kv.Value > 20000 || kv.Value <= _poolFloorMs) continue;
                if (current.Contains(kv.Key)) continue;
                if (currentNames.Contains(Skill.GetName(kv.Key))) continue;
                // Freshest wins; inside the newest ~100ms draw batch, ≠lvlNext beats ==lvlNext.
                bool fresher = kv.Value > best + 100;
                bool sameBatch = Math.Abs(kv.Value - best) <= 100;
                if (bestId == 0 || fresher || (sameBatch && bestId == lvlNext && kv.Key != lvlNext))
                { best = kv.Value; bestId = kv.Key; }
            }
            string via;
            // HOT SPRING / regain flow (player report 2026-10-02: "it shows the incorrect skill
            // when trying to forget the new skill"): the skill being learned is the one picked on
            // the regain list, which stays open under this screen. The drawn-name pool can't
            // answer here — that list draws EVERY forgotten skill's name.
            int regainId = SkillRegainMenu.ActiveSkillId();
            // S.LINK / BOOK / SCOOTER learns (2026-10-08, a player's Chie rank 8: "Tomoe learned Bufula!" was
            // announced as Power Charge, her next LEVEL skill — the drawn-name pool stayed empty): these run
            // the game's own learn task cmp_skill_add_proc, and its work u16 +0x28 IS the skill being taught.
            // The task names it for "X learned Y!" (FUN_1400d59b0) and adds it with that id (task update
            // FUN_14017E710; created by FUN_14017ED60 from the S.Link table / the learn-skill script call).
            int taskId = regainId == 0 && !battle ? LearnTaskSkill(current) : 0;
            if (regainId != 0) { nid = regainId; via = "regain list"; }
            else if (taskId != 0) { nid = taskId; via = "learn task"; }
            // BATTLE level-up (2026-10-08, Haru: "a little late"): +0x6E is the truth there — it advances per
            // prompt on multi-learns and never once changed during a hold across 20 logged prompts — and the
            // battle screen never draws the name, so the 450 ms hold only ever delayed the same answer.
            else if (battle && bestId == 0) { nid = lvlNext; via = "battle level-up +0x6E"; }
            else if (bestId == 0)
            {
                _wantNames = true;   // start capturing drawn names for the hold
                // FIRST-FRAME RACE (user 2026-07-06): the menu opens with the cursor already
                // ON this slot, before the panel name has been drawn/captured even once.
                // HOLD: re-enter next render until the drawn name lands; fall back to lvlNext
                // only after ~1s.
                if (_pendingIncomingSince == 0) { _pendingIncomingSince = now; _holdFirstLvlNext = lvlNext; }
                // Deadline, not a delay: the announce fires the moment the drawn name lands.
                // FIELD flows without the learn task get 1s (2026-09-04: slower PCs missed the old
                // 450ms window). Battle no longer holds at all (above).
                if (now - _pendingIncomingSince < 1000)
                {
                    _lastCursor = -1;   // reprocess this slot next frame
                    return;
                }
                // Re-entering the slot (cursor moved off and back): the names of THIS prompt
                // are already fenced off, so repeat what we resolved before rather than
                // regress to +0x6E (the Amrita bug on a second visit).
                if (_resolvedIncoming != 0) { nid = _resolvedIncoming; via = "resolved earlier"; }
                else via = "FALLBACK +0x6E after hold";
            }
            else { nid = bestId; via = "drawn"; }
            _resolvedIncoming = nid;
            Log($"[SkillRepDiag] {(battle ? "battle" : "field")} major={FieldTracker.CurrentMajor} " +
                $"count={count} lvlNext={lvlNext}({Skill.GetName(lvlNext)}) current=[{string.Join(",", current)}] " +
                $"pool=[{pool.ToString().TrimEnd()}] floor={now - _poolFloorMs}ms ago -> {nid}({Skill.GetName(nid)}) via {via}" +
                (via.StartsWith("FALLBACK") && _holdFirstLvlNext != lvlNext
                    ? $" ⚠ +0x6E CHANGED during the hold: first frame {_holdFirstLvlNext}({Skill.GetName(_holdFirstLvlNext)})" : ""));
            _poolFloorMs = now;   // everything drawn so far belongs to THIS prompt — fence it off for the next one
            _pendingIncomingSince = 0;
            _wantNames = false;
            if (nid < 1 || nid > 1024) return;
            string nm = Skill.GetName(nid);
            string desc = Skill.GetDescription(nid);
            if (IsDummyText(desc)) desc = "";   // never speak a "Skill06D" placeholder
            body = string.IsNullOrEmpty(desc) ? $"{nm}." : $"{nm}. {desc}.";
        }

        Speech.Say(body, interrupt: true);
    }

    // ── the learn task (cmp_skill_add_proc) ─────────────────────────────────
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] LearnTaskName = System.Text.Encoding.ASCII.GetBytes("cmp_skill_add_proc");

    /// <summary>The skill the game's learn task (cmp_skill_add_proc, work +0x28) is teaching, or 0 when that task
    /// isn't running (battle level-ups, the hot spring's regain list) or holds no usable id.</summary>
    private static int LearnTaskSkill(HashSet<int> current)
    {
        nint node = FindTask(LearnTaskName);
        if (node == 0) return 0;
        nint work; ushort id;
        if (!Utils.TryReadRaw(node + 0x48, &work, 8) || work <= 0x10000) return 0;
        if (!Utils.TryReadRaw(work + 0x28, &id, 2)) return 0;
        if (id < 1 || id > 1024 || current.Contains(id)) return 0;
        string nm = Skill.GetName(id);
        return string.IsNullOrEmpty(nm) || nm.StartsWith("?") ? 0 : id;
    }

    /// <summary>A named task's node (name @+0x00, work @+0x48, next @+0x50). The name ends in any non-printable
    /// byte, not always NUL (the work-offset anchors rule).</summary>
    private static nint FindTask(byte[] name)
    {
        byte* hdr = stackalloc byte[0x58];
        foreach (nint head in TaskHeads)
        {
            nint node;
            if (!Utils.TryReadRaw(head, &node, 8)) continue;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!Utils.TryReadRaw(node, hdr, 0x58)) break;
                bool match = true;
                for (int j = 0; j < name.Length && match; j++) match = hdr[j] == name[j];
                byte end = hdr[name.Length];
                if (match && (end < 0x20 || end > 0x7E)) return node;
                node = *(nint*)(hdr + 0x50);
            }
        }
        return 0;
    }

    // Guarded C-string read — RPM-based since 2026-07-27 (menu-heaviness fix): the
    // VirtualQuery version stalled under allocator contention; RecentlyActive spans
    // the battle persona grid too, so this paid per drawn string there.
    private static string ReadCString(nint p, int maxLen) => Utils.ReadCStringRpm(p, maxLen);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
        => Utils.ProbeReadable(addr, size);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable

    public void Dispose() { }
}
