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

    public PartyStatus()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "PartyStatus" };
        _thread.Start();
        Log("[PartyStatus] ready (O = speak party HP/SP)");
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
                if (down && !_keyWas) Announce();
                _keyWas = down;
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

            string name = id == 1
                ? (FirstName(DecodeAtlusName(McNameAddr)) ?? "Protagonist")
                : (_names.TryGetValue(id, out var n) ? n : $"Member {id}");

            // Max HP/SP via the confirmed party getters. Only call them in battle
            // (major 240) where their context is valid; the stat node is this very
            // slot pointer. Sanity-gate the result (max >= current).
            int maxHp = -1, maxSp = -1;
            if (FieldTracker.CurrentMajor == 240 && Battle.GetMaxHp != null && Battle.GetMaxSp != null
                && IsReadable(slot, 0x100))
            {
                try { maxHp = Battle.GetMaxHp(slot); maxSp = Battle.GetMaxSp(slot); } catch { }
                if (maxHp < hp || maxHp > 9999) maxHp = -1;
                if (maxSp < sp || maxSp > 9999) maxSp = -1;
            }

            string hpStr = maxHp > 0 ? $"{hp} of {maxHp} HP" : $"{hp} HP";
            string spStr = maxSp > 0 ? $"{sp} of {maxSp} SP" : $"{sp} SP";
            // Status ailment (the record IS the battle stat node; +0x0C status).
            uint status = *(uint*)(b + 0x0C);
            string ail = Battle.AilmentText(status);
            if ((status & Battle.StatusDown) != 0) ail = ail == null ? "down" : $"down, {ail}";
            // Flag-word bit 0x200 = guard stance (confirmed twice, 2026-06-10).
            if ((inParty & 0x200) != 0) ail = ail == null ? "guarding" : $"{ail}, guarding";
            sb.Append(ail == null
                ? $"{name} {hpStr}, {spStr}. "
                : $"{name} {hpStr}, {spStr}, {ail}. ");
            spoken++;
        }

        string msg = spoken > 0 ? sb.ToString().TrimEnd() : "No party data.";
        Speech.Say(msg, true);
        Log($"[PartyStatus] O ->{logSb} | {msg}");
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
