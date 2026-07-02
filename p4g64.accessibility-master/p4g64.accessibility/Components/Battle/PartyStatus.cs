using System.Runtime.InteropServices;
using System.Text;
using DavyKager;
using p4g64.accessibility.Native.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Press <c>O</c> to hear every active party member's current HP and SP:
///
///   <c>"Yu 94 HP 57 SP. Yosuke 92 HP 63 SP."</c>
///
/// The persistent party stats live in a FIXED global array inside the EXE image
/// (ASLR off, so the address is constant — confirmed by the same member address
/// 0x1451BD9E4 reappearing across separate play sessions). Each record is 0x84
/// bytes:
///   +0x00 (u16)  in-active-party flag (1 = in the current battle party)
///   +0x04 (u16)  character id (1 = protagonist, then party join order)
///   +0x08 (u16)  CURRENT HP   (confirmed: dropped by exactly the damage taken)
///   +0x0A (u16)  CURRENT SP   (confirmed: dropped by exactly a skill's SP cost)
///
/// Because the array is global, this works in battle AND in the field. Max
/// HP/SP offsets aren't located yet (a Ghidra follow-up), so only current
/// values are spoken for now.
///
/// See <c>memory/battle_structs_wip.md</c> for the reverse-engineering trail.
/// </summary>
internal sealed unsafe class PartyStatus
{
    private const int PollMs = 50;
    private const int VK_O = 0x4F;
    private const int VK_SHIFT = 0x10;                   // Shift+O — read the current acting character
    private const int VK_SEMI = 0xBA, VK_QUOTE = 0xDE;   // ; / ' — cycle one ally's status (battle only)
    private bool _semiWas, _quoteWas;
    private int _cursor;

    // Base of the persistent party-member array (slot 0 = protagonist) and the
    // record stride. Both confirmed from runtime dumps; static under ASLR-off.
    private static readonly nint PartyArrayBase = unchecked((nint)0x1451BD9E4L);
    private const int Stride = 0x84;
    private const int MaxSlots = 8;

    // The protagonist's NameRecord (a static global; found via GetUnitName on the
    // MC). +0x00 holds the player's CUSTOM name as a null-terminated Atlus string,
    // so the readout shows the real entered name instead of a hardcoded "Yu".
    private static readonly nint McNameAddr = unchecked((nint)0x141165908L);

    // Offsets within a record.
    private const int OFF_IN_PARTY = 0x00;
    private const int OFF_CHAR_ID = 0x04;
    private const int OFF_HP = 0x08;
    private const int OFF_SP = 0x0A;

    // Character id -> display name. Best-guess P4G join order; refine once the
    // user confirms which names line up with which slot.
    private static readonly Dictionary<int, string> _names = new()
    {
        { 1, "Yu" },
        { 2, "Yosuke" },
        { 3, "Chie" },
        { 4, "Yukiko" },
        { 5, "Rise" },
        { 6, "Kanji" },
        { 7, "Naoto" },
        { 8, "Teddie" },
    };

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _keyWas;

    /// Shared instance so ControllerInput can route RT+shoulders / RT+L3 to the same readers.
    internal static PartyStatus Instance;
    /// Cycle one ally (battle only) — controller RT + left/right shoulder.
    internal static void CycleCurrentAlly(int dir) { if (Battle.ActiveBtlInfo != 0) Instance?.CycleAlly(dir); }
    /// Read the current acting character — controller RT + L3 (in battle).
    internal static void ReadCurrentCharacter() => Instance?.AnnounceCurrent();

    public PartyStatus()
    {
        Instance = this;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "PartyStatus" };
        _thread.Start();
        Log("[PartyStatus] ready (O = party HP/SP, Shift+O = current character, ; ' = cycle ally)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try
            {
                if (!Utils.GameHasFocus()) continue;   // ignore O while alt-tabbed
                bool down = IsKeyDown(VK_O);
                if (down && !_keyWas) { if (IsKeyDown(VK_SHIFT)) AnnounceCurrent(); else Announce(); }
                _keyWas = down;

                // ; / ' cycle through one ally's status at a time — battle only, so they don't
                // clash with ; (NPC id) in the overworld.
                if (Battle.ActiveBtlInfo != 0)
                {
                    bool semi = IsKeyDown(VK_SEMI);
                    if (semi && !_semiWas) CycleAlly(-1);
                    _semiWas = semi;
                    bool quote = IsKeyDown(VK_QUOTE);
                    if (quote && !_quoteWas) CycleAlly(+1);
                    _quoteWas = quote;
                }
                else { _semiWas = _quoteWas = false; }
            }
            catch (Exception ex)
            {
                Log($"[PartyStatus] poll error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Announce()
    {
        var sb = new StringBuilder();
        int spoken = 0;
        var logSb = new StringBuilder();

        for (int i = 0; i < MaxSlots; i++)
        {
            nint slot = PartyArrayBase + i * Stride;
            if (!IsReadable(slot, 0x10)) continue;

            var b = (byte*)slot;
            ushort inParty = *(ushort*)(b + OFF_IN_PARTY);
            ushort id = *(ushort*)(b + OFF_CHAR_ID);
            ushort hp = *(ushort*)(b + OFF_HP);
            ushort sp = *(ushort*)(b + OFF_SP);

            logSb.Append($" [slot{i} flag={inParty} id={id} hp={hp} sp={sp}]");

            // Active party only — BIT test, not equality: the flag word gains
            // other bits during battle (0x200 observed while Yosuke guarded,
            // which made `!= 1` skip him — the "O only speaks the MC" bug).
            if ((inParty & 1) == 0) continue;
            if (id == 0 || id > 32) continue;

            sb.Append(DescribeMember(slot, id)).Append(". ");
            spoken++;
        }

        string msg = spoken > 0 ? sb.ToString().TrimEnd() : "No party data.";
        Speech.Say(msg, true);
        Log($"[PartyStatus] O ->{logSb} | {msg}");
    }

    /// Shift+O — read the CURRENT acting character (whose turn it is): name, HP/SP, ailments, buffs.
    private void AnnounceCurrent()
    {
        if (Battle.ActiveBtlInfo == 0) { Speech.Say("Only in battle.", true); return; }
        nint unit = Battle.ActingUnit();
        if (unit == 0 || !IsReadable(unit + 0xCF8, 8)) { Speech.Say("No current character.", true); return; }
        nint stat = *(nint*)(unit + 0xCF0);
        if (!IsReadable(stat, 0x28)) { Speech.Say("No current character.", true); return; }
        var b = (byte*)stat;
        ushort hp = *(ushort*)(b + 0x08);
        ushort sp = *(ushort*)(b + 0x0A);

        string name = FirstName(Battle.UnitName(unit)) ?? "Current";
        int maxHp = -1, maxSp = -1;
        if (Battle.GetMaxHp != null && Battle.GetMaxSp != null)
        {
            try { maxHp = Battle.GetMaxHp(stat); maxSp = Battle.GetMaxSp(stat); } catch { }
            if (maxHp < hp || maxHp > 9999) maxHp = -1;
            if (maxSp < sp || maxSp > 9999) maxSp = -1;
        }
        string hpStr = maxHp > 0 ? $"{hp} of {maxHp} HP" : $"{hp} HP";
        string spStr = maxSp > 0 ? $"{sp} of {maxSp} SP" : $"{sp} SP";

        uint status = *(uint*)(b + 0x0C);
        string ail = Battle.AilmentText(status);
        if ((status & Battle.StatusDown) != 0) ail = ail == null ? "down" : $"down, {ail}";
        string buffs = Battle.BuffTextFromStat(stat);
        if (buffs != null) ail = ail == null ? buffs : $"{ail}, {buffs}";

        string line = ail == null ? $"{name}, {hpStr}, {spStr}" : $"{name}, {hpStr}, {spStr}, {ail}";
        Speech.Say(line, true);
        Log($"[PartyStatus] Shift+O current -> {line}");
    }

    /// One member's spoken line: "Name N of M HP, K of L SP[, ailment]".
    private string DescribeMember(nint slot, int id)
    {
        var b = (byte*)slot;
        ushort inParty = *(ushort*)(b + OFF_IN_PARTY);
        ushort hp = *(ushort*)(b + OFF_HP);
        ushort sp = *(ushort*)(b + OFF_SP);

        string name = id == 1
            ? (FirstName(DecodeAtlusName(McNameAddr)) ?? "Protagonist")
            : (_names.TryGetValue(id, out var n) ? n : $"Member {id}");

        // Max HP/SP via the confirmed party getters (battle context only); sanity-gated.
        int maxHp = -1, maxSp = -1;
        if (FieldTracker.InBattle && Battle.GetMaxHp != null && Battle.GetMaxSp != null
            && IsReadable(slot, 0x100))
        {
            try { maxHp = Battle.GetMaxHp(slot); maxSp = Battle.GetMaxSp(slot); } catch { }
            if (maxHp < hp || maxHp > 9999) maxHp = -1;
            if (maxSp < sp || maxSp > 9999) maxSp = -1;
        }
        string hpStr = maxHp > 0 ? $"{hp} of {maxHp} HP" : $"{hp} HP";
        string spStr = maxSp > 0 ? $"{sp} of {maxSp} SP" : $"{sp} SP";

        uint status = *(uint*)(b + 0x0C);
        string ail = Battle.AilmentText(status);
        if ((status & Battle.StatusDown) != 0) ail = ail == null ? "down" : $"down, {ail}";
        if ((inParty & 0x200) != 0) ail = ail == null ? "guarding" : $"{ail}, guarding";
        // Stat buffs (in battle the slot pointer IS the stat node, so read straight from it).
        string buffs = FieldTracker.InBattle ? Battle.BuffTextFromStat(slot) : null;
        if (buffs != null) ail = ail == null ? buffs : $"{ail}, {buffs}";

        return ail == null ? $"{name} {hpStr}, {spStr}" : $"{name} {hpStr}, {spStr}, {ail}";
    }

    /// ; / ' move a cursor through the active party and speak ONE member's status.
    private void CycleAlly(int dir)
    {
        var actives = new List<(nint slot, int id)>();
        for (int i = 0; i < MaxSlots; i++)
        {
            nint slot = PartyArrayBase + i * Stride;
            if (!IsReadable(slot, 0x10)) continue;
            var b = (byte*)slot;
            ushort inParty = *(ushort*)(b + OFF_IN_PARTY);
            ushort id = *(ushort*)(b + OFF_CHAR_ID);
            if ((inParty & 1) == 0 || id == 0 || id > 32) continue;
            actives.Add((slot, id));
        }
        if (actives.Count == 0) { Speech.Say("No party data.", true); return; }
        _cursor = ((_cursor + dir) % actives.Count + actives.Count) % actives.Count;
        var (cslot, cid) = actives[_cursor];
        Speech.Say(DescribeMember(cslot, cid), true);
    }

    /// <summary>
    /// Decode a null-terminated Atlus-encoded name string (2-byte glyphs with a
    /// 0x80 lead byte, plus single-byte ASCII like space). The terminator is a
    /// 0x00 LEAD byte — we only test lead bytes so a glyph whose low byte is 0x00
    /// isn't mistaken for the end.
    /// </summary>
    /// <summary>First whitespace-separated token (the MC name is "first last";
    /// we speak just the first to match the single-name party members).</summary>
    private static string FirstName(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return null;
        return full.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string DecodeAtlusName(nint addr)
    {
        if (!IsReadable(addr, 0x40)) return null;
        var b = (byte*)addr;
        int len = 0;
        while (len < 0x3E)
        {
            byte lead = b[len];
            if (lead == 0) break;
            len += lead >= 0x80 ? 2 : 1;
        }
        if (len == 0) return null;
        try { return AtlusEncoding.P4.GetString(b, len).Trim('\0', ' '); }
        catch { return null; }
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
