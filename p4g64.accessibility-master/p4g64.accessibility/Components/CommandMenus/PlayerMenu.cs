using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Components;
using p4g64.accessibility.Native;
using p4g64.accessibility.Native.Text;
using static p4g64.accessibility.Native.Party;
using static p4g64.accessibility.Native.Persona;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.CommandMenus;

/// <summary>
/// The PLAYER MENU (camp/pause menu) screen reader — replaces CommandMenu +
/// PersonaMenu (both retired, no longer constructed).
///
/// Driven entirely by the camp menu's own state, found 2026-06-12 and
/// live-verified over two test passes (see memory/camp_menu_structure.md):
///   pCamp = *(nint*)0x140EC0A40           — null while the menu is closed
///   pCamp+0x0C (int)  = OPEN-SUBMENU BITMASK: bit0 = main strip, bit(1+k)
///                       = submenu k open (k = main-menu option id)
///   pCamp+0x18        = inline main-strip struct: row +0x14 (abs +0x2C),
///                       visible option ids int[8] +0x20 (abs +0x38),
///                       count +0x40 (abs +0x58)
///   *(pCamp+0x9E70+k*8) = per-submenu heap object
/// Option ids: 0 Skill · 1 Item · 2 Equip · 3 Persona · 4 Status ·
///             5 Social Link · 6 Quest · 7 System.
///
/// All reading happens on a 50 ms poll thread (the shop lesson: never trust
/// hook cadence), every pointer hop IsReadable-guarded. F re-reads the last
/// announcement. Party HP/SP come from the persistent member array at
/// 0x1451BD9E4 (stride 0x84 — battle_structs_wip).
/// </summary>
internal sealed unsafe class PlayerMenu
{
    // ── Statics (ASLR disabled — constant across runs) ───────────────────
    private const long PCampPtrAddr = 0x140EC0A40;     // → camp object
    private const long PartyArrayAddr = 0x1451BD9E4;   // member structs, stride 0x84
    private const long UiGlobalsAddr = 0x141165900;    // → UI globals; MC persona stock @ +0xA30/+0xA34
    private const long EquipTablePtrAddr = 0x1411A5948; // → equipped-items table (= party member array)
    private const long EquipStatTblAddr = 0x1411A5940;  // runtime item-stat table base (decompile FUN_140168340: attack=rec+0x28)

    // pCamp offsets
    private const int OffOpenMask = 0x0C;
    private const int OffStripRow = 0x2C;
    private const int OffStripIds = 0x38;      // int[8]
    private const int OffStripCount = 0x58;
    private const int OffSubObjs = 0x9E70;     // + k*8

    private static readonly string[] SubMenuNames =
        { "Skill Menu", "Item Menu", "Equipment Menu", "Persona Menu",
          "Status Menu", "Social Link Menu", "Quest Menu", "System Menu" };

    // Character id → name (standard P4 ids; id 1 = protagonist custom name).
    private static readonly string[] CharNames =
        { "?", "You", "Yosuke", "Chie", "Yukiko", "Rise", "Kanji", "Naoto", "Teddie" };

    private static readonly string[] EquipSlotNames = { "Weapon", "Armor", "Accessory", "Clothes" };

    /// <summary>True while the camp menu (or a submenu) is open — the reliable pCamp+0x0C mask signal.
    /// Dungeon nav components (DungeonCursor / DungeonNav / NavBeacon) gate on it so their keys don't
    /// fire behind the menu.</summary>
    internal static volatile bool IsMenuOpen;

    // ── Trackers ──────────────────────────────────────────────────────────
    private int _lastMask = -1;
    private int _lastFocus = -2;        // -1 strip, 0..7 submenu, -2 closed
    private int _lastStripId = -1;
    private string _lastSpoken = "";

    private int _itemRow = -1, _itemTab = -1, _itemTarget = -1, _itemPersonaTarget = -1;
    private bool _itemTargeting;
    // True once the skill-card persona panel was seen during the current item-use; reset when we
    // return to the item list. Distinguishes the skill-card learn transition (which must NOT announce
    // a member) from a real heal-item member select (which never has a preceding persona target).
    private bool _wasPersonaTarget;
    private int _skillRow = -1, _skillMember = -1, _skillTarget = -1;
    private bool _skillPaneList;
    private int _equipChar = -1, _equipSlot = -1, _equipListRow = -1;
    private bool _equipInit;
    private int _personaSel = -1;
    private int _pRow, _pItem = -1;          // persona detail nav (I/K rows, J/L items)
    private bool _piW, _pkW, _pjW, _plW;
    private int _statusSel = -1;
    private int _stRow, _stItem = -1;        // status detail nav (I/K rows, J/L items)
    private bool _stiW, _stkW, _stjW, _stlW;
    private int _systemRow = -1;
    private int _slinkRow = -1;

    internal PlayerMenu()
    {
        var t = new Thread(Poll) { IsBackground = true, Name = "PlayerMenuPoll" };
        t.Start();
        Log("[PlayerMenu] ready (poll thread started)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(50);
            try { Tick(); }
            catch (Exception ex)
            {
                Log($"[PlayerMenu] Tick error: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private void Tick()
    {
        // The in-game Change Difficulty screen lives OUTSIDE the camp menu and
        // OVERLAYS it. When it's open, stop reading the camp menu — otherwise
        // the System submenu behind it keeps re-announcing "Change Difficulty".
        if (ReadChangeDifficulty()) return;

        // The "replace a skill" overlay (skill card → full slots) sits on top of the camp
        // menu; while it's up, SkillReplaceMenu speaks the skills, so the camp Item poll must
        // stay silent (else its stale member-target leaks a "name. HP. SP." through).
        if (SkillReplaceMenu.RecentlyActive) return;

        nint pCamp = ReadPtr((nint)PCampPtrAddr);
        int mask = 0;
        if (pCamp != 0 && IsReadable(pCamp, 0x60))
            mask = *(int*)(pCamp + OffOpenMask);

        // The reliable "camp menu is open" signal. The pointer alone is garbage-non-zero on dungeon
        // floors, and that garbage can even yield a non-zero mask — so require a SANE submenu mask
        // (only bits 0-8: bit0 = strip, bits 1-8 = the eight submenus). A garbage mask has high bits set
        // and is rejected, so this never wrongly gates the dungeon nav keys.
        IsMenuOpen = mask != 0 && (mask & ~0x1FF) == 0;

        if (pCamp == 0 || mask == 0)
        {
            if (_lastFocus != -2) { ResetAll(); _lastFocus = -2; _lastMask = 0; }
            return;
        }

        if (mask != _lastMask)
        {
            Log($"[PlayerMenu] mask 0x{_lastMask:X} -> 0x{mask:X}");
            _lastMask = mask;
        }

        // Focus = highest open bit. bit0 → strip (-1), bit(1+k) → submenu k.
        int focus = -1;
        for (int k = 7; k >= 0; k--)
            if ((mask & (1 << (k + 1))) != 0) { focus = k; break; }

        if (focus != _lastFocus)
        {
            ResetCursors();
            _lastFocus = focus;
        }

        if (focus == -1) ReadStrip(pCamp);
        else ReadSubMenu(pCamp, focus);
    }

    // ── Main strip ────────────────────────────────────────────────────────
    private void ReadStrip(nint pCamp)
    {
        int row = *(int*)(pCamp + OffStripRow);
        int count = *(int*)(pCamp + OffStripCount);
        if (row < 0 || row > 7 || count <= 0 || count > 8) return;
        if (row >= count) return;
        int id = *(int*)(pCamp + OffStripIds + row * 4);
        if (id < 0 || id > 7) return;
        if (id == _lastStripId) return;
        _lastStripId = id;
        Speak(SubMenuNames[id]);
    }

    // ── Submenus ──────────────────────────────────────────────────────────
    private void ReadSubMenu(nint pCamp, int k)
    {
        nint obj = ReadPtr(pCamp + OffSubObjs + k * 8);
        if (obj == 0 || !IsReadable(obj, 0x80)) return;

        switch (k)
        {
            case 0: ReadSkill(obj); break;
            case 1: ReadItem(obj); break;
            case 2: ReadEquip(obj); break;
            case 3: ReadPersona(obj); break;
            case 4: ReadStatus(obj); break;
            case 5: ReadSocialLink(obj); break;
            case 6: ReadQuest(obj); break;
            case 7: ReadSystem(obj); break;
        }
    }

    // Skill: pane flags +0x20 (1 = member pane, bit1 set = skill list
    // focused); member cursor +0x24; skill list scroll +0x28 + in-page
    // cursor +0x26; records inline at +0x40, stride 0xC: {u16 ?, u16 id @+2,
    // u16 hpCost @+4, ?, u16 spCost @+8} (live: Cleave hp=5 @+4, Zio sp=4
    // @+8); total count +0x4C0.
    private void ReadSkill(nint obj)
    {
        if (!IsReadable(obj, 0x4D0)) return;
        int flags = *(ushort*)(obj + 0x20);
        bool listFocused = (flags & 2) != 0;
        int member = *(short*)(obj + 0x24);
        int row = *(short*)(obj + 0x26) + *(short*)(obj + 0x28);
        int total = *(short*)(obj + 0x4C0);

        // Healing/buff skills open a "use on whom" target pane. Cursor (list position) = +0x2A
        // (the skill obj's cursor is +2 vs the item obj's +0x28); active-party COUNT = +0x20
        // (same field as the item obj — live-verified 2026-06-28: +0x20=4, cursor +0x2A=3 on
        // Yukiko). The OLD code read +0x38 as the count, but +0x38 is the 4th slot-list entry
        // (=3), so it rejected member index 3 and the newest member was always silent.
        bool targeting = (flags & 4) != 0;
        if (targeting)
        {
            int cnt = *(short*)(obj + 0x20);
            if (cnt < 1 || cnt > 8) cnt = 8;
            int m = *(short*)(obj + 0x2A);
            if (m < 0 || m >= cnt) return; // transient/out of range
            if (m == _skillTarget) return;
            _skillTarget = m;
            Log($"[PlayerMenu][Skill] target index {m}/{cnt}");
            Speak(MemberLineByCursor(obj, m));
            return;
        }
        if (_skillTarget != -1) _skillTarget = -1;

        if (listFocused != _skillPaneList)
        {
            _skillPaneList = listFocused;
            // entering the list re-announces the selected skill; leaving it
            // re-announces the member
            if (listFocused) _skillRow = -1; else _skillMember = -1;
        }

        if (!listFocused)
        {
            if (member == _skillMember) return;
            _skillMember = member;
            Speak(MemberLineByCursor(obj, member));
            Log($"[PlayerMenu][Skill] member={member}");
            return;
        }

        if (row == _skillRow || row < 0 || row >= total || total <= 0 || total > 64) return;
        _skillRow = row;

        nint rec = obj + 0x40 + row * 0xC;
        ushort skillId = *(ushort*)(rec + 2);
        ushort hpCost = *(ushort*)(rec + 4);
        ushort spCost = *(ushort*)(rec + 8);
        if (skillId == 0 || skillId > 0x3000) { Log($"[PlayerMenu][Skill] row={row} odd id={skillId}"); return; }

        string skill = Skill.GetName(skillId);
        if (string.IsNullOrEmpty(skill)) skill = $"Skill {skillId}";
        // The game displays the bare number for HP costs — match the screen.
        string cost = hpCost > 0 ? $"{hpCost} HP" : $"{spCost} SP";
        string msg = $"{skill}, {cost}";
        string desc = Skill.GetDescription(skillId);
        if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
        Log($"[PlayerMenu][Skill] row={row} id={skillId} hp={hpCost} sp={spCost}");
        Speak(msg);
    }

    // Item: the inventory is ONE unified scrolling list of {u16 itemId, u16 count}
    // pairs at +0x3E (consumables, skill cards, Golden items, key items AND
    // materials, all mixed). The ABSOLUTE row = window-cursor (+0x24, caps near the
    // bottom of the visible window) + scroll offset (+0x26). The old reader used
    // +0x24 alone, so once the list scrolled it capped and went silent/wrong past
    // ~row 7 — every item below the first screen (incl. all shadow materials) was
    // unreadable. (Diagnosed 2026-06-18 from a full scroll-through: +0x24+0x26 = abs
    // index, verified item 19 = Big Incisor 1297.) +0x2C/+0x2E are the animation
    // trail (ignore); +0x28 stays 0 (single list, no tabs). Mode flags +0x20:
    // 2 = item list, 4 = USE TARGET-SELECT pane.
    private void ReadItem(nint obj)
    {
        if (!IsReadable(obj, 0x300)) return;
        int flags = *(ushort*)(obj + 0x20);
        int tab = *(short*)(obj + 0x28);
        int row = *(short*)(obj + 0x24) + *(short*)(obj + 0x26);

        // "Use on whom?" pane. +0x24 is the carried-over ITEM row (stale —
        // it was 1 because Soul Drop is item row 1, which announced
        // "Member 2"). The real target cursor is **+0x2C, 0-based** (live:
        // 0 = protagonist/Haru).
        bool targeting = (flags & 4) != 0;
        if (targeting != _itemTargeting)
        {
            _itemTargeting = targeting;
            _itemTarget = -1;
            _itemPersonaTarget = -1;
            if (!targeting) { _itemRow = -1; _wasPersonaTarget = false; } // back on the list — re-announce
        }
        if (targeting)
        {
            // SKILL CARD "Use on which Persona?" — a SEPARATE target panel that shows the
            // MC's persona stock instead of party members. The render (FUN_14016EE20) gates
            // the persona panel on +0x30A8 bit 4 (vs the member panel at +0x3090); when it's
            // set, read the persona list, not the member index. RE 2026-06-26 (snapshot +
            // CE find-what-accesses [rdi+0x2A] -> render). Check FIRST so we never speak the
            // stale "Member 1" the member branch produced (the reported silent bug).
            if (IsReadable(obj + 0x30A8, 1) && (*(byte*)(obj + 0x30A8) & 4) != 0)
            {
                _wasPersonaTarget = true;
                ReadPersonaTarget(obj);
                return;
            }
            // A skill card's persona-confirm transition keeps targeting flagged (and even the member
            // panel flag set) for a few frames before the "replace a skill" screen loads — the member
            // branch then leaks a stale "name. HP. SP." (member 0). A real heal-item member select never
            // follows a persona target, so suppress the member announce when one was seen this item-use.
            // The flag resets the moment we return to the item list, so switching to a heal item speaks
            // immediately (no timer).
            if (_wasPersonaTarget) { _itemTarget = -1; return; }
            // "Use on whom?" target pane. Cursor (list position) = +0x28; the active-party
            // COUNT = +0x20. (Live-verified 2026-06-28 with a 4-member party: +0x20=4 constant
            // as the cursor swept 0→3; +0x32+i*2 = {0,1,2,3} are the list's roster slots.)
            // The OLD code read +0x38 as the count — but +0x38 is actually the 4th SLOT-LIST
            // entry (=3), constant at 3, so it rejected member index 3 (the newest member,
            // e.g. Yukiko) as out-of-range → the last member to join was always silent.
            int tgtCount = *(short*)(obj + 0x20);
            if (tgtCount < 1 || tgtCount > 8) tgtCount = 8;
            int member = *(short*)(obj + 0x28);
            if (member < 0 || member >= tgtCount) return; // transient/out of range
            if (member == _itemTarget) return;
            _itemTarget = member;
            Log($"[PlayerMenu][Item] target index {member}/{tgtCount}");
            Speak(MemberLineByCursor(obj, member));
            return;
        }

        if (tab != _itemTab)
        {
            _itemTab = tab;
            _itemRow = -1;
            Log($"[PlayerMenu][Item] tab={tab}");
        }

        if (row == _itemRow || row < 0 || row > 128) return;

        nint pair = obj + 0x3E + row * 4;
        if (!IsReadable(pair, 4)) return;
        ushort id = *(ushort*)pair;
        ushort count = *(ushort*)(pair + 2);
        Log($"[PlayerMenu][Item] row={row} id={id} cnt={count}");
        if (id == 0) return;
        _itemRow = row;

        string name = Item.GetName(id);
        if (string.IsNullOrEmpty(name)) name = $"Item {id}";
        string msg = count > 1 ? $"{name}, {count}" : name;
        string desc = DescribeItem(id);
        if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
        Speak(msg);

    }

    // Skill-card "Use on which Persona?" list (the persona-target panel, +0x30A8 bit 4).
    // Shows the MC's persona stock. Fields in the Item submenu obj (RE 2026-06-26, snapshot +
    // CE find-what-accesses on the cursor -> render FUN_14016EE20):
    //   +0x2A   short  cursor (highlighted list row)
    //   +0x2858 short  count (personas listed)
    //   +0x2840 u16[]  per-row STOCK SLOT index -> ProtagPersonas[slot] (id @+2, level @+4)
    private void ReadPersonaTarget(nint obj)
    {
        if (!IsReadable(obj + 0x2858, 2)) return;
        int count = *(short*)(obj + 0x2858);
        int cursor = *(short*)(obj + 0x2A);
        if (count < 1 || count > 12 || cursor < 0 || cursor >= count) return;
        if (cursor == _itemPersonaTarget) return;
        _itemPersonaTarget = cursor;

        if (!IsReadable(obj + 0x2840 + cursor * 2, 2)) return;
        int slot = *(short*)(obj + 0x2840 + cursor * 2);
        var party = PartyInfoSafe;
        if (party == null || slot < 0 || slot > 11) return;
        nint entry = (nint)(&party->ProtagPersonas) + (nint)slot * 0x30;
        if (!IsReadable(entry, 0x30)) return;
        int pid = *(short*)(entry + 2);
        int level = *(byte*)(entry + 4);
        string nm = (pid >= 1 && pid <= 512) ? GetName(pid) : $"Persona {cursor + 1}";
        Log($"[PlayerMenu][Item] persona target cursor={cursor} slot={slot} id={pid} lv={level}");
        Speak($"{nm}, level {level}, {cursor + 1} of {count}");
    }

    // Equip: character cursor +0x28 (ids u16[] at +0x38, count +0x4C),
    // slot +0x2A (0 Weapon · 1 Armor · 2 Accessory · 3 Clothes), candidate
    // list rows {u16 itemId @+0x4E, u16 @+0x50} stride 4, row = +0x2C++0x2E.
    // Currently equipped = *(u16*)(*(0x1411A5948) - 0x38 + (charId*0x42 +
    // slot)*2).
    private void ReadEquip(nint obj)
    {
        if (!IsReadable(obj, 0x100)) return;
        int chr = *(short*)(obj + 0x28);
        int slot = *(short*)(obj + 0x2A);
        int listRow = *(short*)(obj + 0x2C) + *(short*)(obj + 0x2E);
        int charId = chr >= 0 && chr < 10 ? *(ushort*)(obj + 0x38 + chr * 2) : -1;

        if (!_equipInit)
        {
            // On open: announce the character AND the starting slot+item+stats
            // (cursor lands on Weapons) in ONE utterance — two Speak calls
            // would interrupt each other.
            _equipInit = true;
            _equipChar = chr;
            _equipSlot = slot;
            _equipListRow = listRow;
            Log($"[PlayerMenu][Equip] open charCursor={chr} charId={charId} slot={slot}");
            Speak($"{CharIdName(charId, chr)}. {EquipSlotPart(charId, slot)}");
            return;
        }

        // Char/slot changes are slot-select navigation. Sync _equipListRow to
        // the current value so the candidate block below does NOT fire a stale
        // announce (the "Plain Ring after switching to Haru" bug). On a char
        // switch, re-read the current slot for the NEW character (gear differs
        // per character — that's the whole point of switching).
        if (chr != _equipChar)
        {
            _equipChar = chr;
            _equipListRow = listRow;
            Log($"[PlayerMenu][Equip] charCursor={chr} charId={charId} slot={slot}");
            Speak($"{CharIdName(charId, chr)}. {EquipSlotPart(charId, slot)}");
            return;
        }
        if (slot != _equipSlot)
        {
            _equipSlot = slot;
            _equipListRow = listRow;
            Log($"[PlayerMenu][Equip] slot={slot}");
            Speak(EquipSlotPart(charId, slot));
            return;
        }
        if (listRow != _equipListRow)
        {
            _equipListRow = listRow;
            if (listRow < 0 || listRow > 128) return;
            nint entry = obj + 0x4E + listRow * 4;
            if (!IsReadable(entry, 4)) return;
            ushort id = *(ushort*)entry;
            ushort extra = *(ushort*)(entry + 2);
            Log($"[PlayerMenu][Equip] listRow={listRow} id={id} extra={extra}");
            if (id == 0) { Speak("Nothing"); return; }
            string name = Item.GetName(id);
            if (string.IsNullOrEmpty(name)) name = $"Item {id}";
            string msg = extra > 1 ? $"{name}, {extra}" : name;
            msg += EquipStatText(id, charId, slot);
            string desc = DescribeItem(id);
            if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
            Speak(msg);
        }
    }

    // ── In-game Change Difficulty (System menu) ───────────────────────────
    // Separate screen (NOT camp). Found via live snapshot 2026-06-13: a GENERAL
    // "active UI object" static pointer at 0x15E41A720 points to obj+0x1C; the
    // screen object base is *(0x15E41A720) − 0x1C. Settings cursor at base+0x00
    // (0..6), settings count at base+0x06 (= 6 for this screen — the validity
    // signature, since the global also points at other UI when this is closed).
    // Setting names are a FIXED list. Values/tabs are a follow-up.
    private const long ActiveUiPtrAddr = 0x15E41A720;
    private static readonly string[] DiffSettings =
        { "Damage taken", "Damage given", "EXP won", "Money won",
          "Retries in dungeons", "Retries in battles", "OK" };
    // Top tab row at base+0x04 (live-verified: Current=0, Very Easy=1).
    private static readonly string[] DiffTabs =
        { "Current", "Very Easy", "Easy", "Normal", "Hard", "Very Hard" };
    // Per-setting submenu option labels (game docs). Order Great/Normal/Small
    // etc. is a best guess — user verifies vs the on-screen order.
    private static readonly string[][] DiffOptions =
    {
        new[] { "Great", "Normal", "Small" },   // 0 Damage taken
        new[] { "Great", "Normal", "Small" },   // 1 Damage given
        new[] { "More", "Normal", "Less" },     // 2 EXP won
        new[] { "More", "Normal", "Less" },     // 3 Money won
        new[] { "Use", "Don't use" },           // 4 Retries in dungeons
        new[] { "Use", "Don't use" },           // 5 Retries in battles
        new string[0],                          // 6 OK (no submenu)
    };
    private int _diffRow = -1, _diffTab = -1, _diffOpt = -1, _diffMode = -1;
    private bool _diffActiveWas;

    // Returns true while the Change Difficulty screen is open (so Tick skips
    // the camp menu underneath it).
    private bool ReadChangeDifficulty()
    {
        nint p = ReadPtr((nint)ActiveUiPtrAddr);
        nint baseObj = p == 0 ? 0 : p - 0x1C;
        bool active = baseObj != 0 && IsReadable(baseObj, 0x10)
                      && *(short*)(baseObj + 0x06) == 6           // settings count signature
                      && *(short*)(baseObj + 0x00) is >= 0 and <= 6;
        if (!active)
        {
            if (_diffActiveWas) { _diffActiveWas = false; _diffRow = -1; }
            return false;
        }
        int row = *(short*)(baseObj + 0x00);
        int tab = *(short*)(baseObj + 0x04);
        int opt = *(short*)(baseObj + 0x02);
        int mode = *(short*)(baseObj + 0x14C);   // 2 = settings list, 1 = option submenu open
        if (!_diffActiveWas) { _diffActiveWas = true; _diffRow = -1; _diffTab = tab; _diffOpt = -1; _diffMode = mode; Log("[PlayerMenu][Difficulty] screen open"); }

        if (mode == 1)
        {
            // Option submenu open: announce the highlighted choice (re-announce
            // on first open and on every move).
            var opts = row >= 0 && row < DiffOptions.Length ? DiffOptions[row] : System.Array.Empty<string>();
            if (mode != _diffMode || opt != _diffOpt)
            {
                _diffMode = mode; _diffOpt = opt;
                string label = opt >= 0 && opt < opts.Length ? opts[opt] : $"Option {opt + 1}";
                Log($"[PlayerMenu][Difficulty] submenu setting={row} opt={opt} {label}");
                Speak(label);
            }
            return true;
        }

        // Settings list. A submenu just closed → re-announce the setting.
        if (_diffMode == 1) { _diffMode = mode; _diffRow = -1; _diffOpt = -1; }
        _diffMode = mode;
        if (tab != _diffTab)
        {
            _diffTab = tab;
            string tn = tab >= 0 && tab < DiffTabs.Length ? DiffTabs[tab] : $"Tab {tab}";
            Log($"[PlayerMenu][Difficulty] tab={tab} {tn}");
            Speak(tn);
            return true;
        }
        if (row != _diffRow)
        {
            _diffRow = row;
            string name = row >= 0 && row < DiffSettings.Length ? DiffSettings[row] : $"Row {row}";
            Log($"[PlayerMenu][Difficulty] row={row} {name}");
            Speak(name);
        }
        return true;
    }

    // Persona menu. Selected stock slot at +0x56 → MC stock PersonaInfo
    // (&PartyInfo->ProtagPersonas)[sel]. The list up/down auto-announces
    // name+level; I/K/J/L explore the full detail panel (mirrors the
    // in-battle PersonaNav), reusing the Battle.cs persona helpers:
    //   Row 0 name/arcana/level · 1 Elements · 2 Stats · 3 Skills · 4 Experience
    //   I/K = row up/down (whole row), J/L = step items (skills read
    //   name + description).
    private void ReadPersona(nint obj)
    {
        int sel = *(short*)(obj + 0x56);
        var party = PartyInfoSafe;
        if (party == null || sel < 0 || sel > 11) return;
        nint entry = (nint)(&party->ProtagPersonas) + (nint)sel * 0x30;
        if (!IsReadable(entry, 0x30)) return;
        int pid = *(short*)(entry + 2);

        if (sel != _personaSel)
        {
            _personaSel = sel;
            _pRow = 0; _pItem = -1;
            if (pid >= 1 && pid <= 512)
            {
                Log($"[PlayerMenu][Persona] sel={sel} id={pid}");
                Speak($"{GetName(pid)} level {*(byte*)(entry + 4)}");
            }
        }

        if (pid < 1 || pid > 512) return;

        // Detail nav (I/K rows, J/L items) is available throughout the persona
        // submenu — the camp status panel exposes no reliable open-flag, so we
        // simply let the user read the selected persona's detail on demand. It
        // is naturally scoped: ReadPersona only runs while the persona submenu
        // (k=3) is focused, so the keys never act in other menus or the field.
        bool i = NavKey(0x49), k = NavKey(0x4B), j = NavKey(0x4A), l = NavKey(0x4C);
        if (i && !_piW) { _pRow = Math.Max(0, _pRow - 1); _pItem = -1; PersonaAnnounceRow(entry, pid); }
        if (k && !_pkW) { _pRow = Math.Min(4, _pRow + 1); _pItem = -1; PersonaAnnounceRow(entry, pid); }
        if (j && !_pjW) PersonaStep(entry, pid, -1);
        if (l && !_plW) PersonaStep(entry, pid, +1);
        _piW = i; _pkW = k; _pjW = j; _plW = l;
    }

    private void PersonaAnnounceRow(nint entry, int pid)
    {
        var (label, items) = PersonaDetailRow(entry, pid, _pRow);
        string body = items.Length > 0 ? string.Join(", ", items) : "nothing";
        Speak(label != null ? $"{label}. {body}" : body);
        Log($"[PlayerMenu][Persona] row {_pRow}: {body}");
    }

    private void PersonaStep(nint entry, int pid, int dir)
    {
        var (_, items) = PersonaDetailRow(entry, pid, _pRow);
        if (items.Length == 0) return;
        _pItem = _pItem < 0 ? (dir > 0 ? 0 : items.Length - 1)
                            : Math.Clamp(_pItem + dir, 0, items.Length - 1);
        string text = items[_pItem];
        if (_pRow == 3) // Skills: speak name + description
        {
            int sid = PersonaNthSkill(entry, _pItem);
            if (sid > 0)
            {
                string nm = Skill.GetName(sid), d = Skill.GetDescription(sid);
                if (!string.IsNullOrEmpty(nm)) text = string.IsNullOrEmpty(d) ? nm : $"{nm}. {d}";
            }
        }
        else if (_pRow == 4 && text.StartsWith("learns"))
        {
            text = Battle.Battle.PersonaNextLearnDetail(entry, pid) ?? text;
        }
        Speak(text);
        Log($"[PlayerMenu][Persona] row {_pRow} item {_pItem}: {text}");
    }

    // Mirrors Battle.PersonaStockRow but for an explicit camp stock entry.
    private static (string label, string[] items) PersonaDetailRow(nint entry, int pid, int row)
    {
        switch (row)
        {
            case 0:
            {
                var items = new List<string>();
                string name = GetName(pid);
                if (!string.IsNullOrEmpty(name)) items.Add(name);
                string arcana = Battle.Battle.PersonaArcanaName(pid);
                if (!string.IsNullOrEmpty(arcana)) items.Add(arcana);
                items.Add($"level {*(byte*)(entry + 4)}");
                return (null, items.ToArray());
            }
            case 1:
            {
                var items = new List<string>();
                foreach (var (eid, nm) in Battle.Battle.ProfileElements)
                    items.Add(Battle.Battle.PersonaElementAffinityText(pid, eid, nm));
                return ("Elements", items.ToArray());
            }
            case 2:
            {
                var t = Battle.Battle.PersonaTotalStats(entry);
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
                short* sk = (short*)(entry + 0x0C); // PersonaInfo.Skills[8]
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
                return Battle.Battle.PersonaGrowthRow(entry, pid);
        }
        return (null, Array.Empty<string>());
    }

    private static int PersonaNthSkill(nint entry, int index)
    {
        short* sk = (short*)(entry + 0x0C);
        int seen = -1;
        for (int s = 0; s < 8; s++)
        {
            int sid = sk[s];
            if (sid <= 0) continue;
            if (++seen == index) return sid;
        }
        return -1;
    }

    private static bool NavKey(int vk) => IsGameFocused() && (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Social Link: record at +0x40 — {u16 arcana (1-based, live 2 =
    // Magician ✓), u16 ? (live 7), u16 rank (live 1)}. Holder names from
    // the fixed P4G arcana→character map (user wants "Yosuke Hanamura,
    // Magician, rank 1"). Stride/cursor unconfirmed until 2+ links exist —
    // announce record 0 on open, log the first 4 candidate records.
    // Social Link list (cursor + records live-verified 2026-06-17 with 3 links).
    // Cursor = the ABSOLUTE 0-based index at +0x28 (it stepped 0,1,2 across the
    // 3 links). +0x30 is the viewport scroll offset — do NOT add it (it hit 1 at
    // the last of 3 items, which would over-count). Records are a contiguous
    // array of the player's UNLOCKED links at +0x40, stride 0x0C:
    //   +0x00 u16 arcana (in-game id: 1=Investigation Team, 2=Yosuke, 8=Chie …)
    //   +0x04 u16 rank.
    private void ReadSocialLink(nint obj)
    {
        if (!IsReadable(obj, 0x100)) return;
        // ABSOLUTE row = visible-window cursor (+0x28) + scroll offset (+0x2A). The OLD code used
        // only +0x28, so scrolling the list (which moves the window while the visible cursor stays
        // put) didn't change `row` → the dedup silenced it → "a lot of choices don't read."
        // (Live-verified 2026-06-28: cursor on Margaret/Empress had +0x28=2, +0x2A=1 → row 3, and
        // record[3] @+0x40+3*0xC = {arcana 4, mid 20, rank 1} = Empress, rank 1. ✓)
        int row = *(short*)(obj + 0x28) + *(short*)(obj + 0x2A);
        if (row < 0 || row > 23) return;
        if (row == _slinkRow) return;
        nint rec = obj + 0x40 + (nint)row * 0x0C;
        int arcana = *(ushort*)(rec + 0);
        int rank = *(ushort*)(rec + 4);
        // Golden remaps the late arcana ids vs the tarot table, so they need explicit names here
        // (verified live 2026-06-28 against the on-screen labels): internal id 25 = Jester (Adachi —
        // the shared ArcanaName wrongly gives "Hunger"), 27 = Aeon (Marie). Both are past the 25-entry
        // tarot table, so the old "> 25" guard also silenced Marie's row.
        if (arcana < 1 || arcana > 27) return;   // empty/invalid row — don't latch
        _slinkRow = row;
        Log($"[PlayerMenu][SLink] row {row} arcana {arcana} rank {rank} (mid={*(ushort*)(rec + 2)})");
        string holder = SLinkHolder(arcana);
        string arcName = arcana switch
        {
            25 => "Jester",   // Golden internal id 25 = Jester (Adachi); ArcanaName(25) wrongly = "Hunger"
            27 => "Aeon",     // Golden internal id 27 = Aeon (Marie)
            _  => Battle.Battle.ArcanaName(arcana)
        };
        string rk = (rank >= 1 && rank <= 10) ? $", rank {rank}" : "";
        Speak(holder.Length > 0 ? $"{holder}, {arcName}{rk}" : $"{arcName}{rk}");
    }

    // Fixed P4G social-link holders by arcana (1-based). Strength and Sun
    // depend on the player's choice — arcana-only for those.
    private static string SLinkHolder(int arcana) => arcana switch
    {
        1 => "Investigation Team",
        2 => "Yosuke Hanamura",
        3 => "Yukiko Amagi",
        4 => "Margaret",
        5 => "Kanji Tatsumi",
        6 => "Ryotaro Dojima",
        7 => "Rise Kujikawa",
        8 => "Chie Satonaka",
        9 => "Nanako Dojima",
        10 => "Fox",
        11 => "Naoto Shirogane",
        13 => "Naoki Konishi",
        14 => "Hisano Kuroda",
        15 => "Eri Minami",
        16 => "Sayoko Uehara",
        17 => "Shu Nakajima",
        18 => "Teddie",
        19 => "Ai Ebihara",
        21 => "Seekers of Truth",
        23 => "Marie",
        24 => "Tohru Adachi",
        25 => "Tohru Adachi",
        27 => "Marie",            // Golden internal Aeon id (records use 27, not the tarot 23)
        _ => ""
    };

    // Quest: handled by QuestMenu.cs (hooks the UI-text fn to read real titles). This is a
    // no-op so the two don't double-speak. See memory/quest_menu_deadend.md → quest_menu_solved.
    private void ReadQuest(nint obj) { }

    // System: option ids s16[] at +0x3E, scroll +0x32, in-page cursor +0x2C,
    // visible count +0x4E. Ids live: [1,2,3,5,4,6,7]. id1=Config CONFIRMED
    // (entering it fired the ConfigMenu hooks); id6=Return to Title
    // CONFIRMED by user. Others best-guess pending user verification.
    private void ReadSystem(nint obj)
    {
        int scroll = *(short*)(obj + 0x32);
        int cur = *(short*)(obj + 0x2C);
        int count = *(short*)(obj + 0x4E);
        int row = scroll + cur;
        if (row == _systemRow || cur < 0 || count <= 0 || count > 16 || row < 0 || row > 15) return;
        _systemRow = row;
        int id = *(short*)(obj + 0x3E + row * 2);
        Log($"[PlayerMenu][System] row={row} id={id}");
        Speak(SystemOptionName(id));
    }

    // EXACT on-screen labels (systemMenu.jpeg, 2026-06-13). Display order is
    // [1,2,3,5,4,6,7] so Load Data appears above Delete Data on screen.
    private static string SystemOptionName(int id) => id switch
    {
        1 => "Config",
        2 => "Change Difficulty",
        3 => "Suspend",
        4 => "Delete Data",
        5 => "Load Data",
        6 => "Return to Title",
        7 => "End Game",
        _ => $"Option {id}"
    };

    // ── Party member array (0x1451BD9E4, stride 0x84) ─────────────────────
    // member+0x04 char id · +0x08 cur HP · +0x0A cur SP. The +0x00 word is an
    // in-BATTLE flag (only the controlled char has it set in the field), so
    // it can't be used to enumerate the roster — index the array directly by
    // the menu cursor (join order == charId-1 == array slot for the early
    // party; revisit if a custom/gapped party ever mis-maps).
    private static string MemberLine(int idx)
    {
        nint arr = (nint)PartyArrayAddr;
        if (idx >= 0 && idx < 12 && IsReadable(arr, 0x84 * (idx + 1)))
        {
            nint m = arr + (nint)idx * 0x84;
            int charId = *(ushort*)(m + 4);
            if (charId >= 1 && charId <= 16)
                return $"{CharIdName(charId, idx)}. HP {*(ushort*)(m + 8)}. SP {*(ushort*)(m + 0xA)}";
        }
        return $"Member {idx + 1}";
    }

    // Map a skill/item member CURSOR (display position) → roster slot. The menu obj's list at +0x32
    // (u16 per entry) holds a position into the RECRUITED list (recruited members only, in slot order)
    // — NOT a roster slot. Confirmed 2026-06-30: party MC/Yosuke/Yukiko/Kanji gave +0x32=[0,1,3,4],
    // and since Rise (id5) isn't recruited yet, recruited-pos 4 = roster slot 5 (Kanji id6), NOT slot 4
    // (Rise). So we walk the recruited roster entries (flag&1) and return the pos-th one's slot.
    // Cursor 0 is ALWAYS the MC (roster slot 0) — shortcut it to avoid a transient mis-read on open.
    // Read a skill/item member line by the menu CURSOR. The active-party CHAR-ID list lives at
    // obj+0x34 (u16 per entry = char id directly, in display order) — confirmed 2026-06-30: party
    // MC/Chie/Yukiko/Kanji gave +0x34=[1,3,4,6]. This tracks the live party (unlike the +0x32 list,
    // which goes stale to the default layout after a swap). Falls back to the old slot path if absent.
    private static string MemberLineByCursor(nint obj, int cursor)
    {
        if (cursor >= 0 && cursor <= 7 && IsReadable(obj + 0x34 + (nint)cursor * 2, 2))
        {
            int id = *(ushort*)(obj + 0x34 + (nint)cursor * 2);
            if (id >= 1 && id <= 16) return MemberLineById(id);
        }
        return MemberLine(cursor);
    }

    private static string MemberLineById(int charId)
    {
        nint arr = (nint)PartyArrayAddr;
        if (charId >= 1 && charId <= 16 && IsReadable(arr, 0x84 * 16))
        {
            nint m = arr + (charId - 1) * 0x84;
            if (*(ushort*)(m + 4) == charId)
                return $"{CharIdName(charId, -1)}. HP {*(ushort*)(m + 8)}. SP {*(ushort*)(m + 0xA)}";
        }
        return CharIdName(charId, -1);
    }

    // ── Equip stat readout ────────────────────────────────────────────────
    // The COMPUTED stats the game draws live in the runtime table at
    // *(0x1411A5940), indexed by itemId*0x44 (decompiled FUN_140168340,
    // live-verified 2026-06-13):
    //   rec+0x00 = category (0 weapon · 1 armor · 2 accessory)
    //   weapon: attack rec+0x08, hit rec+0x0A   (Golf Club 42/93 on screen)
    //   armor:  defense rec+0x10, evade rec+0x12 (T-Shirt def 26 on screen)
    //   accessory: no attack/defense numbers (effects only → description)
    // These already fold in the character's bonus, so they match the screen
    // exactly. The table is only populated while the equip menu is open —
    // which is precisely when we read it.
    private enum EquipCat { Weapon = 0, Armor = 1, Accessory = 2, Unknown = -1 }

    private static EquipCat EquipItemStats(int id, out int statA, out int statB)
    {
        statA = statB = 0;
        if (id <= 0) return EquipCat.Unknown;
        nint baseAddr = ReadPtr((nint)EquipStatTblAddr);
        if (baseAddr == 0) return EquipCat.Unknown;
        nint rec = baseAddr + (nint)id * 0x44;
        if (!IsReadable(rec, 0x44)) return EquipCat.Unknown;
        int cat = *(short*)rec;
        switch (cat)
        {
            case 0: statA = *(short*)(rec + 0x08); statB = *(short*)(rec + 0x0A); return EquipCat.Weapon;
            case 1: statA = *(short*)(rec + 0x10); statB = *(short*)(rec + 0x12); return EquipCat.Armor;
            case 2: return EquipCat.Accessory;
            default: return EquipCat.Unknown;
        }
    }

    // "Weapon: Golf Club, attack 42, hit 93" — the slot label + its currently
    // equipped item + stats. Used by open, char-switch, and slot navigation.
    private static string EquipSlotPart(int charId, int slot)
    {
        string label = slot >= 0 && slot < EquipSlotNames.Length ? EquipSlotNames[slot] : $"Slot {slot}";
        string current = EquippedItemName(charId, slot);
        if (string.IsNullOrEmpty(current)) return label;
        return $"{label}: {current}{EquippedStatText(charId, slot)}";
    }

    // ", attack 42, hit 93" — the EQUIPPED item's stats (no delta), appended
    // to a slot announcement.
    private static string EquippedStatText(int charId, int slot)
    {
        int id = EquippedItemId(charId, slot);
        if (id <= 0) return "";
        var cat = EquipItemStats(id, out int a, out int b);
        if (cat == EquipCat.Weapon) return $", attack {a}, hit {b}";
        if (cat == EquipCat.Armor) return $", defense {a}, evade {b}";
        return "";
    }

    // "attack 53, up 11, hit 92, down 1" — candidate computed stats plus the
    // delta against the currently equipped item in the same slot.
    private static string EquipStatText(int id, int charId, int slot)
    {
        var cat = EquipItemStats(id, out int a, out int b);
        if (cat != EquipCat.Weapon && cat != EquipCat.Armor) return ""; // accessory/unknown: no numbers
        string nameA = cat == EquipCat.Weapon ? "attack" : "defense";
        string nameB = cat == EquipCat.Weapon ? "hit" : "evade";
        int curId = EquippedItemId(charId, slot);
        int ca = 0, cb = 0;
        var curCat = curId > 0 ? EquipItemStats(curId, out ca, out cb) : EquipCat.Unknown;
        bool haveCur = curCat == cat;
        string text = $". {nameA} {a}";
        if (haveCur && ca != a) text += a > ca ? $", up {a - ca}" : $", down {ca - a}";
        text += $", {nameB} {b}";
        if (haveCur && cb != b) text += b > cb ? $", up {b - cb}" : $", down {cb - b}";
        return text;
    }

    // ── Equipped-item table ───────────────────────────────────────────────
    private static int EquippedItemId(int charId, int slot)
    {
        if (charId < 1 || charId > 16 || slot < 0 || slot > 3) return -1;
        nint tbl = ReadPtr((nint)EquipTablePtrAddr);
        if (tbl == 0) return -1;
        nint addr = tbl - 0x38 + (charId * 0x42 + slot) * 2;
        if (!IsReadable(addr, 2)) return -1;
        return *(ushort*)addr;
    }

    private static string EquippedItemName(int charId, int slot)
    {
        int id = EquippedItemId(charId, slot);
        if (id < 0) return "";
        if (id == 0) return "Nothing";
        var name = Item.GetName(id);
        return string.IsNullOrEmpty(name) ? $"Item {id}" : name;
    }

    // ── Item description by id block (extends Item.GetDescription) ───────
    // 0-255 weapon · 256-511 armor · 512-767 accessory · 768-1023 consumable
    // · 1024+ key/event items (HelpBmd.Event).
    private static string DescribeItem(int id)
    {
        Dialog.HelpBmd bmd;
        int idx;
        if (id < 256) { bmd = Dialog.HelpBmd.Weapon; idx = id; }
        else if (id < 512) { bmd = Dialog.HelpBmd.Armor; idx = id - 256; }
        else if (id < 768) { bmd = Dialog.HelpBmd.Accessory; idx = id - 512; }
        else if (id < 1024) { bmd = Dialog.HelpBmd.Item; idx = id - 768; }
        else if (id < 1281) { bmd = Dialog.HelpBmd.Event; idx = id - 1024; }     // key items (Velvet Key 1025…)
        else if (id < 1536) { bmd = Dialog.HelpBmd.Material; idx = id - 1280; }  // materials (Big Incisor 1297→idx17 "Lying Hablerie" ✓)
        else if (id < 1792) { bmd = Dialog.HelpBmd.SkillCard; idx = id - 1536; } // skill cards (Sharp Student 1744)
        else if (id < 2048) { bmd = Dialog.HelpBmd.Dress; idx = id - 1792; }     // clothes/costumes
        else if (id < 2304) { bmd = Dialog.HelpBmd.Item2; idx = id - 2048; }     // Golden consumables (Tiny Soul Tomato 2097→idx49)
        else if (id < 2560) { bmd = Dialog.HelpBmd.Weapon2; idx = id - 2304; }   // Golden weapons
        else { Log($"[PlayerMenu] no desc mapping for item id {id}"); return ""; }
        string txt = ReadHelpText(bmd, idx);
        Log($"[PlayerMenu] desc id {id} -> bmd {bmd} idx {idx} -> \"{txt}\"");
        return txt;
    }

    private static string ReadHelpText(Dialog.HelpBmd helpBmd, int idx)
    {
        try
        {
            if (idx < 0) return "";
            var exec = Dialog.GetExecution(helpBmd);
            if (!IsReadable((nint)exec, 0x10)) return "";
            var info = exec->Info;
            if (!IsReadable((nint)info, 0x10)) return "";
            var bmd = info->Bmd;
            if (!IsReadable((nint)bmd, 0x30)) return "";
            if (idx >= bmd->Header.DialogCount) return "";
            var header = (&bmd->DialogHeaders) + idx;
            if (!IsReadable((nint)header, 0x10)) return "";
            var md = header->MessageDialog;
            if (!IsReadable((nint)md, 0x28) || md->PageCount < 1) return "";
            var page = md->Pages;
            int size = page.TextSize;
            if (size < 1 || size > 1024 || !IsReadable((nint)page.Text, size)) return "";
            return AtlusEncoding.P4.GetString(page.Text, size).Replace('\n', ' ').Replace('\0', ' ').Trim();
        }
        catch { return ""; }
    }

    // Status: SOLVED via the input handler FUN_140199DE0 — selected member
    // = char-id list at +0x24 indexed by the cursor at **+0x3A** (the 0/1
    // toggle the early diags dismissed as animation).
    // Status menu (k=4). Member-select cursor +0x3A → charId list +0x24. The
    // member-select speaks name+level+HP/SP; I/K/J/L explore the detail of the
    // selected character (vitals · attack/defense · persona · equipment),
    // mirroring the persona/equip pattern. Per-member data from the persistent
    // member struct (0x1451BD9E4, stride 0x84): curHP +0x08, curSP +0x0A,
    // MC level +0x34, gear +0x4C/+0x4E/+0x50/+0x52, ally persona id +0x56
    // (level +0x58). DEFERRED: max HP/SP, EXP, and the F social-stats page
    // (those live in the separate status-panel renderer).
    private void ReadStatus(nint obj)
    {
        if (!IsReadable(obj, 0x40)) return;
        int sel = *(short*)(obj + 0x3A);
        if (sel < 0 || sel > 9) return;
        int charId = *(ushort*)(obj + 0x24 + sel * 2);
        if (charId < 1 || charId > 16) return;

        if (sel != _statusSel)
        {
            _statusSel = sel;
            _stRow = 0; _stItem = -1;
            Log($"[PlayerMenu][Status] sel={sel} charId={charId}");
            Speak(StatusVitals(charId));
        }

        // Row 4 = social stats, MC only (they're the protagonist's).
        int maxRow = charId == 1 ? 4 : 3;
        bool i = NavKey(0x49), k = NavKey(0x4B), j = NavKey(0x4A), l = NavKey(0x4C);
        if (i && !_stiW) { _stRow = Math.Max(0, _stRow - 1); _stItem = -1; StatusAnnounceRow(charId); }
        if (k && !_stkW) { _stRow = Math.Min(maxRow, _stRow + 1); _stItem = -1; StatusAnnounceRow(charId); }
        if (j && !_stjW) StatusStep(charId, -1);
        if (l && !_stlW) StatusStep(charId, +1);
        _stiW = i; _stkW = k; _stjW = j; _stlW = l;
    }

    private void StatusAnnounceRow(int charId)
    {
        var (label, items) = StatusRow(charId, _stRow);
        string body = items.Length > 0 ? string.Join(", ", items) : "nothing";
        Speak(label != null ? $"{label}. {body}" : body);
        Log($"[PlayerMenu][Status] row {_stRow}: {body}");
    }

    private void StatusStep(int charId, int dir)
    {
        var (_, items) = StatusRow(charId, _stRow);
        if (items.Length == 0) return;
        _stItem = _stItem < 0 ? (dir > 0 ? 0 : items.Length - 1)
                              : Math.Clamp(_stItem + dir, 0, items.Length - 1);
        Speak(items[_stItem]);
    }

    private static nint StatusMember(int charId) => (nint)PartyArrayAddr + (nint)(charId - 1) * 0x84;

    private static int StatusLevel(int charId, nint m)
    {
        // MC's protagonist level is at +0x06 (live-verified: Haru = 4). Allies'
        // level == their persona level (+0x58 in the embedded persona). (+0x34
        // is the Courage social stat, NOT level — earlier mistake.)
        if (charId == 1) return IsReadable(m + 0x06, 1) ? *(byte*)(m + 0x06) : 0;
        return IsReadable(m + 0x58, 2) ? *(ushort*)(m + 0x58) : 0;
    }

    private static string StatusVitals(int charId)
    {
        nint m = StatusMember(charId);
        if (!IsReadable(m, 0x84)) return CharIdName(charId, -1);
        return $"{CharIdName(charId, -1)}, level {StatusLevel(charId, m)}, " +
               $"HP {*(ushort*)(m + 8)}, SP {*(ushort*)(m + 0xA)}";
    }

    private static (string label, string[] items) StatusRow(int charId, int row)
    {
        nint m = StatusMember(charId);
        if (!IsReadable(m, 0x84)) return (null, Array.Empty<string>());
        switch (row)
        {
            case 0:
                return (null, new[]
                {
                    CharIdName(charId, -1), $"level {StatusLevel(charId, m)}",
                    $"HP {*(ushort*)(m + 8)}", $"SP {*(ushort*)(m + 0xA)}"
                });
            case 1:
            {
                // Attack/Defense = the computed values the equip screen shows,
                // from the equipped weapon/armor in the runtime stat table.
                int wId = *(ushort*)(m + 0x4C), aId = *(ushort*)(m + 0x4E);
                var items = new List<string>();
                if (EquipItemStats(wId, out int atk, out _) == EquipCat.Weapon) items.Add($"Attack {atk}");
                if (EquipItemStats(aId, out int def, out _) == EquipCat.Armor) items.Add($"Defense {def}");
                if (items.Count == 0) items.Add("unknown");
                return ("Attack and defense", items.ToArray());
            }
            case 2:
            {
                // Persona: ally = embedded (+0x56); MC = equipped stock persona.
                int pid; nint entry;
                if (charId == 1) (entry, pid) = McEquippedPersona();
                else { pid = *(ushort*)(m + 0x56); entry = m + 0x54; }
                if (pid < 1 || pid > 512) return ("Persona", new[] { "none" });
                var items = new List<string>();
                string name = GetName(pid);
                if (!string.IsNullOrEmpty(name)) items.Add(name);
                string arc = Battle.Battle.PersonaArcanaName(pid);
                if (!string.IsNullOrEmpty(arc)) items.Add(arc);
                int plv = IsReadable(entry + 4, 1) ? *(byte*)(entry + 4) : 0;
                if (plv > 0) items.Add($"level {plv}");
                return ("Persona", items.ToArray());
            }
            case 3:
                return ("Equipment", new[]
                {
                    $"Weapon, {EquipName(*(ushort*)(m + 0x4C))}",
                    $"Armor, {EquipName(*(ushort*)(m + 0x4E))}",
                    $"Accessory, {EquipName(*(ushort*)(m + 0x50))}",
                    $"Clothes, {EquipName(*(ushort*)(m + 0x52))}"
                });
            case 4: // social stats (MC only)
                return ("Social stats", SocialStats(m));
        }
        return (null, Array.Empty<string>());
    }

    // Social stats: 5 point values at MC member +0x34..+0x3C (cheat-table
    // _segParty+0x34). Rank word from the cumulative thresholds; verified vs
    // the on-screen page (Haru: Courage 3 → "Average", all rank 1).
    private static readonly string[] _courageW = { "Average", "Reliable", "Brave", "Daring", "Heroic" };
    private static readonly string[] _knowledgeW = { "Aware", "Informed", "Expert", "Professor", "Sage" };
    private static readonly string[] _diligenceW = { "Callow", "Persistent", "Strong", "Persuasive", "Rock Solid" };
    private static readonly string[] _understandW = { "Basic", "Kindly", "Generous", "Motherly", "Saintly" };
    private static readonly string[] _expressionW = { "Rough", "Eloquent", "Persuasive", "Touching", "Enthralling" };

    private static string SocialRank(int pts, int[] thr, string[] words)
    {
        int rank = 1;
        foreach (int t in thr) if (pts >= t) rank++;
        return words[Math.Clamp(rank - 1, 0, 4)];
    }

    private static string[] SocialStats(nint m)
    {
        // +0x34 Courage · +0x36 Knowledge · +0x38 Diligence · +0x3A
        // Understanding · +0x3C Expression. Announced in the screen's order.
        return new[]
        {
            $"Knowledge, {SocialRank(*(ushort*)(m + 0x36), new[] { 30, 80, 150, 240 }, _knowledgeW)}",
            $"Courage, {SocialRank(*(ushort*)(m + 0x34), new[] { 16, 40, 80, 140 }, _courageW)}",
            $"Diligence, {SocialRank(*(ushort*)(m + 0x38), new[] { 16, 40, 80, 130 }, _diligenceW)}",
            $"Understanding, {SocialRank(*(ushort*)(m + 0x3A), new[] { 16, 40, 80, 140 }, _understandW)}",
            $"Expression, {SocialRank(*(ushort*)(m + 0x3C), new[] { 13, 33, 53, 85 }, _expressionW)}",
        };
    }

    private static string EquipName(int id)
    {
        if (id == 0) return "none";
        var n = Item.GetName(id);
        return string.IsNullOrEmpty(n) ? $"item {id}" : n;
    }

    // The MC's currently equipped stock persona (entry, id).
    private static (nint entry, int pid) McEquippedPersona()
    {
        nint g = (nint)UiGlobalsAddr;
        if (!IsReadable(g, 8)) return (0, -1);
        nint baseObj = *(nint*)g;
        if (baseObj == 0 || !IsReadable(baseObj + 0xA30, 2)) return (0, -1);
        int cur = *(short*)(baseObj + 0xA30);
        if (cur < 0 || cur > 11) return (0, -1);
        nint e = baseObj + 0xA34 + (nint)cur * 0x30;
        if (!IsReadable(e, 0x30) || *(byte*)e == 0) return (0, -1);
        return (e, *(short*)(e + 2));
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static string CharIdName(int charId, int cursorFallback)
    {
        if (charId == 1) return Protagonist();
        if (charId >= 2 && charId < CharNames.Length) return CharNames[charId];
        if (cursorFallback >= 0 && cursorFallback + 1 < CharNames.Length)
            return cursorFallback == 0 ? Protagonist() : CharNames[cursorFallback + 1];
        return $"Character {charId}";
    }

    private static string Protagonist()
    {
        var n = ShopMenu.ProtagonistName();
        return string.IsNullOrEmpty(n) ? "You" : n;
    }

    private long _lastSpokeTick;
    private void Speak(string msg)
    {
        // Suppress back-to-back identical announcements (menu close/reopen
        // flicker re-fires the same row — e.g. "Config" repeating after you
        // return from a System submenu). A genuine re-visit of the same item
        // is always preceded by a different one, which clears this.
        long now = Environment.TickCount64;
        if (msg == _lastSpoken && now - _lastSpokeTick < 350) { _lastSpokeTick = now; return; }
        _lastSpoken = msg;
        _lastSpokeTick = now;
        Log($"[PlayerMenu] speak: {msg}");
        Speech.Say(msg, true);
    }

    private void ResetCursors()
    {
        _lastStripId = -1;
        _itemRow = _itemTab = -1;
        _skillRow = _skillMember = -1;
        _skillPaneList = false;
        _itemTarget = -1;
        _itemTargeting = false;
        _equipChar = _equipSlot = _equipListRow = -1;
        _equipInit = false;
        _personaSel = -1;
        _pRow = 0; _pItem = -1;
        _statusSel = -1;
        _stRow = 0; _stItem = -1;
        _systemRow = -1;
        _slinkRow = -1;
    }

    private void ResetAll()
    {
        ResetCursors();
        _lastSpoken = "";
        Log("[PlayerMenu] menu closed");
    }

    private static nint ReadPtr(nint addr)
    {
        if (!IsReadable(addr, 8)) return 0;
        nint p = *(nint*)addr;
        return IsReadable(p, 0x20) ? p : 0;
    }

    // ── AV-safe read guard (VirtualQuery; AVE is uncatchable on .NET 9) ───
    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;     // MEM_COMMIT
        uint protect = *(uint*)(buf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return false; // NOACCESS / GUARD
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsGameFocused() => Utils.GameHasFocus();
}
