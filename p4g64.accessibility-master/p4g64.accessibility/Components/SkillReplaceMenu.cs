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
        try { Read(menu); } catch { /* never let a hook throw */ }
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
    private static Dictionary<string, int>? _nameToId;             // EXACT name -> id, lazy

    private nint OnUiText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _textHook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try
        {
            if (!RecentlyActive)
            {
                if (_capWasActive) { _capWasActive = false; _drawnSkills.Clear(); }
                return ret;
            }
            _capWasActive = true;
            if (p6 != 0) return ret;
            string s = ReadCString(p1, 48).Trim();
            if (s.Length < 3 || s.Length > 32) return ret;
            EnsureNames();
            if (_nameToId != null && _nameToId.TryGetValue(s, out int id))
                _drawnSkills[id] = Environment.TickCount64;
        }
        catch { /* never let a hook throw */ }
        return ret;
    }

    private static void EnsureNames()
    {
        if (_nameToId != null) return;
        var map = new Dictionary<string, int>();
        for (int v = 1; v <= 1024; v++)
        {
            string nm = Skill.GetName(v);
            if (!string.IsNullOrEmpty(nm) && nm.Length >= 3 && !nm.StartsWith("?") && !map.ContainsKey(nm))
                map.Add(nm, v);
        }
        if (map.Count < 300) return;                       // table not resolved yet — retry later
        _nameToId = map;
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

        string body;
        if (cursor < count)
        {
            // A current skill (the 8-slot array: id at 0x0A + i*0xC).
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
                Speech.Say(string.IsNullOrEmpty(nds)
                    ? $"Next level skill: {nnm}."
                    : $"Next level skill: {nnm}. {nds}.", interrupt: true);
                return;
            }
            // FULL persona — the real replace screen. FLOW DISPATCH (2026-07-06): in
            // BATTLE/result context this is a LEVEL-UP learn and menu+0x6E is the proven
            // id. In the FIELD (S.Link / book / scooter) +0x6E is WRONG — it's just the
            // next level-up skill (the Amrita bug) — so use the freshest DRAWN skill
            // name that isn't one of the current 8 (and isn't the level-next itself,
            // whose name the screen also draws invisibly).
            int lvlNext = IsReadable(menu + 0x6E, 2) ? *(ushort*)(menu + 0x6E) : 0;
            int nid = lvlNext;
            if (!FieldTracker.IsBattleMajor(FieldTracker.CurrentMajor))
            {
                var current = new HashSet<int>();
                for (int i = 0; i < count && i < 8; i++)
                    if (IsReadable(menu + 0x0A + i * 0xC, 2)) current.Add(*(ushort*)(menu + 0x0A + i * 0xC));
                long best = 0; int bestId = 0;
                long now = Environment.TickCount64;
                foreach (var kv in _drawnSkills)
                {
                    if (now - kv.Value > 20000 || current.Contains(kv.Key)) continue;
                    // The screen ALSO draws the next-LEVEL skill's name (log
                    // 2026-07-06: "Diarama" rendered right before "Amrita" —
                    // an invisible Next-LV element), so the pool held TWO
                    // candidates and an arbitrary tie-break flip-flopped the
                    // announcement. The level-next id is exactly what we must
                    // NOT say here — exclude it; the fallback still covers the
                    // (harmless) case where the incoming EQUALS the next-level.
                    if (kv.Key == lvlNext) continue;
                    if (kv.Value > best) { best = kv.Value; bestId = kv.Key; }
                }
                if (bestId == 0)
                {
                    // FIRST-FRAME RACE (user 2026-07-06): the menu opens with the
                    // cursor already ON this slot, before the panel name has been
                    // drawn/captured even once — announcing now speaks the wrong
                    // level-up skill. HOLD: re-enter next render until the drawn
                    // name lands; fall back to lvlNext only after ~0.7s.
                    if (_pendingIncomingSince == 0) _pendingIncomingSince = now;
                    if (now - _pendingIncomingSince < 450)
                    {
                        _lastCursor = -1;   // reprocess this slot next frame
                        return;
                    }
                }
                else nid = bestId;
            }
            _pendingIncomingSince = 0;
            if (nid < 1 || nid > 1024) return;
            string nm = Skill.GetName(nid);
            string desc = Skill.GetDescription(nid);
            body = string.IsNullOrEmpty(desc) ? $"{nm}." : $"{nm}. {desc}.";
        }

        Speech.Say(body, interrupt: true);
    }

    // Page-guarded C-string read (the CompendiumInfoText glyph-read pattern).
    private static string ReadCString(nint p, int maxLen)
    {
        if (p == 0) return "";
        ulong a = (ulong)p;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return "";
        byte* qbuf = stackalloc byte[48];
        if (VirtualQuery(p, qbuf, 48) == 0) return "";
        if (*(uint*)(qbuf + 32) != 0x1000) return "";
        uint protect = *(uint*)(qbuf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return "";
        nint regionBase = *(nint*)(qbuf + 0);
        nint regionSize = *(nint*)(qbuf + 24);
        ulong end = (ulong)regionBase + (ulong)regionSize;
        int safe = (int)System.Math.Min((ulong)maxLen, end - a);
        var sb = new System.Text.StringBuilder(maxLen);
        for (int i = 0; i < safe; i++)
        {
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString();
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
