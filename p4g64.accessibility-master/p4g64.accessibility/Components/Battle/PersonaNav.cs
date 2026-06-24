using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Navigable persona detail profile (the in-battle Persona panel), matching the
/// enemy-analyze feel. Active while the Persona command (5) is open and a stock
/// entry is highlighted (published by <see cref="PersonaSelect"/>). The list's
/// up/down already speaks the persona name; press I/K/J/L to explore the detail:
///
///   Row 0 — name, arcana, level
///   Row 1 — element affinities (Phys/Fire/Ice/Elec/Wind/Light/Dark)
///   Row 2 — stats (Strength/Magic/Endurance/Agility/Luck)
///   Row 3 — skills; J/L steps each one, speaking its name + full description
///           (the spoken equivalent of the game's "Skill Info" F sub-list).
///
///   I / K = up / down a row (whole row);  J / L = step the items one at a time.
///
/// TODO (deferred 2026-06-06 — revisit later):
///   * NEXT EXP (exp to next level) + the "NEXT LV n: skill" learn-on-levelup entry the
///     panel shows on the right. Exp is at PersonaInfo+0x08; the learnset/next-skill
///     source still needs RE.
///   * Native "Skill Info" (F) cursor: the game's own skill-slot cursor. The cursor
///     lives in a heap battle-overlay object (CE found it this session at object+0x2B8)
///     but tracing a STABLE pointer chain to it is unfinished (Frida deep-scan crashed
///     the game; CE pointer-scan + rebase is the safe route). J/L on Row 3 already reads
///     every skill + description, so this is polish only.
///   * Ally persona profile: allies have no stock submenu, so their persona needs a
///     different id source than PersonaCursor.
/// </summary>
internal sealed unsafe class PersonaNav
{
    private const int PollMs = 40;
    private const int VK_I = 0x49, VK_K = 0x4B, VK_J = 0x4A, VK_L = 0x4C;
    private const int Rows = 5;          // info · Elements · Stats · Skills · Experience
    private const int SkillRow = 3;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _iW, _kW, _jW, _lW;
    private int _row, _item = -1;
    private int _lastId = int.MinValue;
    private bool _panelWas;

    public PersonaNav()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "PersonaNav" };
        _thread.Start();
        Log("[PersonaNav] ready (Persona menu: I/K rows, J/L items)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[PersonaNav] error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!Utils.GameHasFocus()) return;   // ignore I/K/J/L while alt-tabbed
        // ALLY persona panel (2026-06-10): on an ally's turn the Persona command
        // opens their single persona's panel directly (no stock submenu). The
        // ally's persona id lives in the party record at stat+0x56 (mirror
        // +0x64) — found via the AllyPanelProbe (Yosuke → 192 "Jiraiya"). There
        // is no reliable "panel open" signal for allies, so rows are read on
        // I/K/J/L only (no auto-read; keyed reads can't leak).
        if (Battle.CurrentCommand == 5)
        {
            nint acting = Battle.ActingUnit();
            if (acting != 0 && Battle.UnitSide(acting) == 0)
            {
                nint stat = IsReadable(acting + 0xCF0, 8) ? *(nint*)(acting + 0xCF0) : 0;
                if (stat != 0 && stat != PartyArrayBase)
                {
                    AllyTick(acting, stat);
                    return;
                }
            }
        }

        // Locked to the PERSONA command (CurrentCommand == 5) — the only way into the
        // persona menu/panel — so I/K/J/L never read in skill targeting or combat. Data
        // comes from the live stock cursor (Battle.PersonaCursor). The submenu's name+
        // level is owned by PersonaSelect; we AUTO-read the profile only when the submenu
        // is NOT rendering (LastPersonaTick stale = we're in the full panel), so there's
        // no double-speak. I/K/J/L works throughout (submenu and panel).
        var (_, id) = Battle.PersonaCursor();
        if (Battle.CurrentCommand != 5 || id < 0)
        {
            // Left the persona command: clear the "entered" latch so hovering the Persona
            // choice later (submenu not yet rendered) won't auto-read.
            Battle.PersonaEntered = false;
            _lastId = int.MinValue; _row = 0; _item = -1; _panelWas = false;
            _allyRow = 0; _allyItem = -1;
            _iW = IsKeyDown(VK_I); _kW = IsKeyDown(VK_K); _jW = IsKeyDown(VK_J); _lW = IsKeyDown(VK_L);
            return;
        }
        // Auto-read the profile ONLY in the full panel (submenu hidden = LastPersonaTick
        // stale) AND only after we've genuinely entered (the submenu rendered at least
        // once — PersonaSelect sets PersonaEntered). Just highlighting the Persona choice
        // in the ring never renders the submenu, so it stays silent. I/K/J/L works anytime
        // we're on the Persona command.
        bool inPanel = Environment.TickCount64 - Battle.LastPersonaTick >= 200;
        if (Battle.PersonaEntered && inPanel && (id != _lastId || !_panelWas))
        {
            if (!_panelWas) Log("[PersonaNav] panel open");
            _lastId = id; _row = 0; _item = -1;
            Announce();
        }
        _lastId = id;
        _panelWas = inPanel;

        bool i = IsKeyDown(VK_I), k = IsKeyDown(VK_K), j = IsKeyDown(VK_J), l = IsKeyDown(VK_L);
        if (i && !_iW) { _row = Math.Max(0, _row - 1); _item = -1; Announce(); }
        if (k && !_kW) { _row = Math.Min(Rows - 1, _row + 1); _item = -1; Announce(); }
        if (j && !_jW) StepItem(-1);
        if (l && !_lW) StepItem(+1);
        _iW = i; _kW = k; _jW = j; _lW = l;
    }

    private void StepItem(int dir)
    {
        var (_, items) = Battle.PersonaStockRow(_row);
        if (items.Length == 0) return;
        _item = _item < 0 ? (dir > 0 ? 0 : items.Length - 1)
                          : Math.Clamp(_item + dir, 0, items.Length - 1);
        // On the Skills row, stepping speaks element + cost + description like
        // the battle skill menu; other rows speak the item text directly.
        string text = (_row == SkillRow ? RichSkillDetail(Battle.PersonaStockSkillId(_item)) : null)
                      ?? items[_item];
        // Stepping onto the Experience row's "learns ..." item adds the skill's
        // description (the overview row stays short).
        if (_row == Rows - 1 && text.StartsWith("learns "))
        {
            var (e2, pid2) = Battle.PersonaCursor();
            text = Battle.PersonaNextLearnDetail(e2, pid2) ?? text;
        }
        Speech.Say(text, true);
        Log($"[PersonaNav] row {_row} item {_item}: {text}");
    }

    /// <summary>"Name: Element skill that costs N HP/SP. Description" — the same
    /// composition as the battle skill menu, for the panel Skills row (MC and
    /// ally). Falls back to just the name if any native lookup fails.</summary>
    private static string RichSkillDetail(int sid)
    {
        if (sid <= 0) return null;
        string nm = Skill.GetName(sid);
        if (string.IsNullOrEmpty(nm)) return null;
        var sb = new System.Text.StringBuilder(nm);
        try
        {
            if (Skill.GetSkillType(sid) == Skill.SkillType.Passive)
            {
                sb.Append(": Passive skill.");
            }
            else
            {
                var elem = Skill.GetSkillElement(sid);
                string elemText = elem > Skill.ElementalType.Dark ? "Support" : elem.ToString();
                sb.Append($": {elemText} skill");
                nint unit = Battle.ActingUnit();
                var costType = Skill.GetActiveSkillData(sid)->CostType;
                if (unit != 0 && costType != Skill.SkillCostType.None && IsReadable(unit + 0xCF0, 8))
                {
                    var member = *(PartyMember.PartyMemberInfo**)(unit + 0xCF0);
                    int cost = PartyMember.GetSkillCost(member, sid);
                    if (cost > 0) sb.Append($" that costs {cost} {costType}");
                }
                sb.Append('.');
            }
        }
        catch { return nm; }
        try
        {
            string d = Skill.GetDescription(sid);
            if (!string.IsNullOrEmpty(d)) sb.Append(' ').Append(d);
        }
        catch { }
        return sb.ToString();
    }

    private void Announce()
    {
        var (label, items) = Battle.PersonaStockRow(_row);
        string body = items.Length > 0 ? string.Join(", ", items) : "nothing";
        string msg = label != null ? $"{label}. {body}" : body;
        Speech.Say(msg, true);
        Log($"[PersonaNav] row {_row}: {msg}");
    }

    // ---- ally persona panel -------------------------------------------------

    private static readonly nint PartyArrayBase = unchecked((nint)0x1451BD9E4L);
    private const int AllyRows = 5;          // info · Elements · Stats · Skills · Experience
    private const int AllySkillRow = 3;

    /// <summary>The ally's persona data is an EMBEDDED PersonaInfo at party
    /// record +0x54 (same layout as MC stock entries: id +0x02, level +0x04,
    /// exp +0x08, skills +0x0C, stat arrays +0x1C/+0x21/+0x26) — confirmed via
    /// FUN_14019FC30/FUN_15ee84bc0 (2026-06-10).</summary>
    private static nint AllyPersonaEntry(nint stat) => stat + 0x54;
    private int _allyRow;
    private int _allyItem = -1;

    /// <summary>Ally persona id from the party record: stat+0x56 (mirror +0x64).
    /// Found 2026-06-10 (Yosuke → 192 "Jiraiya"). -1 if neither validates.</summary>
    private static int AllyPersonaId(nint stat)
    {
        foreach (int off in new[] { 0x56, 0x64 })
        {
            if (!IsReadable(stat + off, 2)) continue;
            int v = *(ushort*)(stat + off);
            if (v < 1 || v > 512) continue;
            string n = null;
            try { n = Persona.GetName(v); } catch { }
            if (!string.IsNullOrWhiteSpace(n) && n != "000") return v;
        }
        return -1;
    }

    private void AllyTick(nint unit, nint stat)
    {
        bool i = IsKeyDown(VK_I), k = IsKeyDown(VK_K), j = IsKeyDown(VK_J), l = IsKeyDown(VK_L);
        if (i && !_iW) { _allyRow = Math.Max(0, _allyRow - 1); _allyItem = -1; AllyAnnounce(unit, stat); }
        if (k && !_kW) { _allyRow = Math.Min(AllyRows - 1, _allyRow + 1); _allyItem = -1; AllyAnnounce(unit, stat); }
        if (j && !_jW) AllyStepItem(unit, stat, -1);
        if (l && !_lW) AllyStepItem(unit, stat, +1);
        _iW = i; _kW = k; _jW = j; _lW = l;
    }

    private void AllyAnnounce(nint unit, nint stat)
    {
        var (label, items) = AllyRowItems(unit, stat, _allyRow);
        string body = items.Length > 0 ? string.Join(", ", items) : "nothing";
        string msg = label != null ? $"{label}. {body}" : body;
        Speech.Say(msg, true);
        Log($"[PersonaNav] ally row {_allyRow}: {msg}");
    }

    private void AllyStepItem(nint unit, nint stat, int dir)
    {
        var (_, items) = AllyRowItems(unit, stat, _allyRow);
        if (items.Length == 0) return;
        _allyItem = _allyItem < 0 ? (dir > 0 ? 0 : items.Length - 1)
                                  : Math.Clamp(_allyItem + dir, 0, items.Length - 1);
        // Skills row: J/L speaks element + cost + description like the MC panel.
        string text = items[_allyItem];
        if (_allyRow == AllySkillRow)
            text = RichSkillDetail(AllySkillId(_allyItem)) ?? text;
        // Experience row "learns ..." item: add the skill description.
        if (_allyRow == AllyRows - 1 && text.StartsWith("learns "))
            text = Battle.PersonaNextLearnDetail(AllyPersonaEntry(stat), AllyPersonaId(stat)) ?? text;
        Speech.Say(text, true);
        Log($"[PersonaNav] ally row {_allyRow} item {_allyItem}: {text}");
    }

    /// <summary>index-th valid skill id from the acting member's skill array
    /// (BtlInfo+0xD1A — the ally's own list during their turn), or -1.
    /// Also used by SkillInfoReader for the native F skill-cursor readout.</summary>
    internal static int AllySkillId(int index)
    {
        nint info = Battle.ActiveBtlInfo;
        if (!IsReadable(info + 0xD1A, 16)) return -1;
        int seen = -1;
        for (int s = 0; s < 8; s++)
        {
            int sid = *(short*)(info + 0xD1A + s * 2);
            if (sid <= 0) continue;
            if (++seen == index) return sid;
        }
        return -1;
    }

    private (string label, string[] items) AllyRowItems(nint unit, nint stat, int row)
    {
        int pid = AllyPersonaId(stat);
        switch (row)
        {
            case 0:
            {
                var items = new List<string>();
                string name = pid > 0 ? Persona.GetName(pid) : null;
                items.Add(string.IsNullOrWhiteSpace(name) ? "persona unknown" : name);
                string arcana = pid > 0 ? Battle.PersonaArcanaName(pid) : null;
                if (!string.IsNullOrEmpty(arcana)) items.Add(arcana);
                // Level from the EMBEDDED persona entry (+0x04) — record+0x06
                // read 1 while the panel showed LV 5 (verified vs allypersona.jpeg).
                nint e = AllyPersonaEntry(stat);
                int lvl = IsReadable(e + 0x04, 1) ? *(byte*)(e + 0x04) : -1;
                items.Add(lvl >= 1 ? $"level {lvl}" : "level unknown");
                return (null, items.ToArray());
            }
            case 1:
            {
                var items = new List<string>();
                foreach (var (eid, nm) in Battle.ProfileElements)
                    items.Add(pid > 0
                        ? Battle.PersonaElementAffinityText(pid, eid, nm)
                        : Battle.UnitAffinityText(stat, eid, nm));
                return ("Elements", items.ToArray());
            }
            case 2:
            {
                var t = Battle.PersonaTotalStats(AllyPersonaEntry(stat));
                if (t == null) return ("Stats", new[] { "unknown" });
                return ("Stats", new[]
                {
                    $"Strength {t[0]}", $"Magic {t[1]}", $"Endurance {t[2]}",
                    $"Agility {t[3]}", $"Luck {t[4]}"
                });
            }
            case 3:
            {
                var names = new List<string>();
                nint info = Battle.ActiveBtlInfo;
                if (IsReadable(info + 0xD1A, 16))
                    for (int s = 0; s < 8; s++)
                    {
                        int sid = *(short*)(info + 0xD1A + s * 2);
                        if (sid <= 0) continue;
                        string snm = Skill.GetName(sid);
                        if (!string.IsNullOrEmpty(snm)) names.Add(snm);
                    }
                if (names.Count == 0) names.Add("no skills");
                return ("Skills", names.ToArray());
            }
            case 4:
                return Battle.PersonaGrowthRow(AllyPersonaEntry(stat), pid);
        }
        return (null, Array.Empty<string>());
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

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
