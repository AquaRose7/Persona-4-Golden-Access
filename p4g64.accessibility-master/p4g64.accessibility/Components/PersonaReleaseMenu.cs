using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the "your personas are FULL — choose one to release" menu (appears when you
/// gain a new persona with a full stock: Shuffle Time persona card / fusion /
/// negotiation). RE'd 2026-06-17 — see memory/persona_release_menu.md.
///
/// The menu is a HEAP struct with NO static pointer chain, so it's located by
/// SIGNATURE: at menuBase+0x54 is an array (stride 8) of pointers to the player's
/// PersonaInfo records, which live in the STATIC party/persona region
/// [0x1451B0000, 0x1451C0000) and are consecutive (+0x30 apart). We scan the heap
/// for two consecutive such pointers, derive menuBase, validate cursor/count, then
/// announce the persona (name + level) at the cursor on open and on every move.
///
///   menuBase+0x00 (int)  cursor (0-based)
///   menuBase+0x08 (int)  count
///   menuBase+0x54 + i*8  PersonaInfo* (static);  id = +0x02 (u16), level = +0x04 (u8)
///
/// All reads go through ReadProcessMemory-on-self so a region freed mid-scan fails
/// gracefully instead of an uncatchable AccessViolationException.
/// </summary>
internal sealed unsafe class PersonaReleaseMenu
{
    private const ulong PersonaLo = 0x1451B0000UL;   // static party/persona data region
    private const ulong PersonaHi = 0x1451C0000UL;
    private const int CursorOff = 0x00;
    private const int CountOff = 0x08;
    private const int ListOff = 0x54;                // pointer array (stride 8)
    private const int RecId = 0x02, RecLevel = 0x04;

    private readonly Thread _thread;
    private volatile bool _stopped;

    private nint _menu;            // located struct (0 = not open)
    private int _lastCursor = -1;
    private int _lastEmbId = -1;   // embedded new-persona id — changes = a NEW menu at the same address
    private nint _lastPanelRec;    // View Status panel dedupe (record pointer + id)
    private int _lastPanelId = -1;
    private ulong _scanCursor = ScanLo;   // resumable sliced-scan position
    private const ulong ScanLo = 0x10000, ScanHi = 0x1_0000_0000;
    private const long SliceBudget = 48 * 1024 * 1024;   // bytes read per 120ms tick

    public PersonaReleaseMenu()
    {
        _thread = new Thread(Poll) { IsBackground = true, Name = "PersonaReleaseMenu" };
        _thread.Start();
        Log("[PersonaRelease] ready (release-a-persona menu reader)");
    }

    public void Stop() => _stopped = true;

    private void Poll()
    {
        while (!_stopped)
        {
            Thread.Sleep(120);
            try
            {
                // CONTEXT GATE v2 (2026-07-19): SHUFFLE TIME is the ONLY source
                // of this menu — fusion CONSUMES personas (can't overflow, user-
                // confirmed) and P4G has no negotiation gifts. So the scanner
                // exists only while a battle_shuffle* named task is alive (task
                // dump proved they run exactly through the shuffle/result flow).
                // This also killed the mid-TURN ghost latch: scanning during
                // ordinary battle rounds found a stale two-record pair and spoke
                // "new persona" on the player's turn, then the stuck latch
                // silenced the real menu that opened later.
                bool inContext = FieldTracker.InBattle && ShuffleFlowActive();
                if (!inContext)
                {
                    if (_menu != 0) { _menu = 0; _lastCursor = -1; _lastEmbId = -1; _lastPanelRec = 0; _lastPanelId = -1; }
                    _scanCursor = ScanLo;
                    continue;
                }

                // Re-validate the latched menu; if gone, drop it.
                if (_menu != 0 && !IsMenu(_menu))
                {
                    _menu = 0;
                    _lastCursor = -1;
                    _lastEmbId = -1;
                    _lastPanelRec = 0; _lastPanelId = -1;
                }

                // Locate the menu. ★ ANCHOR FIRST (2026-07-19): the menu IS part
                // of the battle_shuffle_result task's work struct, at +0x3C
                // (anchors log, d=3C). Registry walk + strict IsMenu validation
                // replaces the heap slices; the sliced sweep stays as fallback.
                if (_menu == 0)
                {
                    // ★ HEAP SLICES DELETED (2026-07-19): the menu ONLY exists at
                    // battle_shuffle_result work+0x3C (d=3C across every capture).
                    // No result task → no menu; candidate not valid yet → next
                    // poll retries. (ScanSlice kept below as dead code.)
                    nint anch = ResultMenuAnchor();
                    _menu = (anch != 0 && IsMenu(anch)) ? anch : 0;
                    if (_menu != 0)
                    {
                        _lastCursor = -1;
                        // TEMP DIAGNOSTIC: dump the header ints + every list entry's id,
                        // to fix the off-by-one and locate the new-persona slot.
                        int cnt = RdI(_menu + CountOff);
                        var sb = new System.Text.StringBuilder($"[PersonaRelease] menu @ 0x{_menu:X} cur={RdI(_menu)} count={cnt} | ints:");
                        for (int o = 0; o < 0x54; o += 4) sb.Append($" +{o:X2}={RdI(_menu + o)}");
                        sb.Append(" | list:");
                        for (int i = 0; i < cnt + 1; i++)
                        {
                            nint pp = RdP(_menu + ListOff + i * 8);
                            int id = (pp != 0 && (ulong)pp >= PersonaLo && (ulong)pp < PersonaHi) ? RdU16(pp + RecId) : -1;
                            sb.Append($" [{i}]0x{pp:X}=id{id}");
                        }
                        Log(sb.ToString());
                        // TEMP: does THIS menu run as a NAMED TASK? If yes we can
                        // replace the whole heap scan with a free registry walk
                        // (the TvListings/SkillRegain pattern). One dump per latch.
                        DumpTaskNames();
                        // TEMP ANCHOR HUNT: is the menu reachable FROM a
                        // battle_shuffle* task's work struct? A hit here deletes
                        // the heap scan permanently (the scan's VirtualQuery walk
                        // contends the game's allocation lock = the reward lag).
                        DumpShuffleAnchors(_menu);
                    }
                    continue;
                }

                int count = RdI(_menu + CountOff);
                int cursor = RdI(_menu + CursorOff);

                // BACK-TO-BACK MENUS (2026-07-19): two persona cards in one
                // shuffle open the menu twice AT THE SAME ADDRESS with cursor 0
                // both times — the cursor dedupe ate the second menu entirely.
                // The embedded new-persona id is the menu's identity: when it
                // changes, this is a NEW menu — re-announce from scratch.
                int embId = RdU16(_menu + 0x1E);
                if (embId != _lastEmbId) { _lastEmbId = embId; _lastCursor = -1; }

                // F "View Status" panel (task shdPersonaStatusDraw): its work+0x00
                // points at the DISPLAYED PersonaInfo record (live hunt 2026-07-19).
                ReadPanel();

                if (cursor == _lastCursor || cursor < 0 || cursor >= count) continue;
                _lastCursor = cursor;
                string line = DescribePersona(_menu, cursor, count);
                Log($"[PersonaRelease] {line}");
                Speech.Say(line, true);
            }
            catch { }
        }
    }

    private string DescribePersona(nint menu, int cursor, int count)
    {
        // Display order = [ newly-obtained persona, then the stock ]. The +0x54
        // pointer array is ONLY the stock, so cursor 0 = the new persona (not in
        // the array) and cursor i≥1 = stock[i-1]. (User-confirmed 2026-06-17.)
        if (cursor == 0)
        {
            // The NEW persona is embedded BY VALUE in the menu header at +0x1C —
            // a PersonaInfo prefix: id u16 @+0x1E, level u8 @+0x20, exp u32 @+0x24.
            // Live-proven 2026-07-19: screen showed Girimehkala Lv48; the header
            // dump read +1E=127 +20=48 +24=431672 while the stock pointers matched
            // the on-screen stock exactly. (The old guess — "stock end + 0x30" —
            // landed on an empty template record and spoke "Izanagi, level 1".)
            int nid = RdU16(menu + 0x1E);
            int nlv = RdU8(menu + 0x20);
            if (nid >= 1 && nid <= 512 && nlv >= 1 && nlv <= 99)
                return $"New persona, {SafeName(nid)}, level {nlv}. 1 of {count}";
            return $"New persona. 1 of {count}";
        }
        nint pr = RdP(menu + ListOff + (cursor - 1) * 8);
        if (pr == 0 || (ulong)pr < PersonaLo || (ulong)pr >= PersonaHi)
            return $"Item {cursor + 1} of {count}";
        return $"{SafeName(RdU16(pr + RecId))}, level {RdU8(pr + RecLevel)}. {cursor + 1} of {count}";
    }

    private static readonly byte[] PanelTaskName = System.Text.Encoding.ASCII.GetBytes("shdPersonaStatusDraw");

    /// <summary>The F "View Status" panel (task shdPersonaStatusDraw): work+0x00
    /// points at the DISPLAYED PersonaInfo record — the menu's embedded new-persona
    /// record or a stock record (live hunt 2026-07-19: ptr == menu+0x1C, id/level
    /// echoed by value at work+0x0E/+0x10). Speaks name, level, stats and skills
    /// once per displayed record; closing the panel resets the dedupe.</summary>
    private void ReadPanel()
    {
        nint rec = 0;
        foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
        {
            nint node = RdP((nint)head);
            for (int i = 0; i < 300 && node != 0 && rec == 0; i++)
            {
                Span<byte> nm = stackalloc byte[24];
                if (!Read(node, nm, 24)) break;
                bool match = true;
                for (int j = 0; j < PanelTaskName.Length; j++)
                    if (nm[j] != PanelTaskName[j]) { match = false; break; }
                if (match && (nm[PanelTaskName.Length] < 0x20 || nm[PanelTaskName.Length] >= 0x7F))
                {
                    nint work = RdP(node + 0x48);
                    if (work > 0x10000)
                    {
                        nint p = RdP(work);
                        if (p > 0x10000) rec = p;
                    }
                }
                node = RdP(node + 0x50);
            }
            if (rec != 0) break;
        }
        if (rec == 0) { _lastPanelRec = 0; _lastPanelId = -1; return; }

        int id = RdU16(rec + RecId), lv = RdU8(rec + RecLevel);
        if (id < 1 || id > 512 || lv < 1 || lv > 99) return;
        if (rec == _lastPanelRec && id == _lastPanelId) return;
        _lastPanelRec = rec; _lastPanelId = id;

        int st = RdU8(rec + 0x1C), ma = RdU8(rec + 0x1D), en = RdU8(rec + 0x1E);
        int ag = RdU8(rec + 0x1F), lu = RdU8(rec + 0x20);
        var skills = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            int sid = RdU16(rec + 0x0C + i * 2);
            if (sid <= 0 || sid >= 2048) continue;
            try
            {
                string sn = Native.Skill.GetName(sid);
                if (!string.IsNullOrWhiteSpace(sn)) skills.Add(sn);
            }
            catch { }
        }
        string line = $"{SafeName(id)}, level {lv}. Strength {st}, Magic {ma}, Endurance {en}, " +
                      $"Agility {ag}, Luck {lu}." +
                      (skills.Count > 0 ? $" Skills: {string.Join(", ", skills)}." : "");
        Log($"[PersonaRelease] panel: {line}");
        Speech.Say(line, true);
    }

    private static string SafeName(int id)
    {
        string n = "Persona";
        try { n = Native.Persona.GetName(id); } catch { }
        return string.IsNullOrWhiteSpace(n) ? $"Persona {id}" : n;
    }

    /// <summary>Strict release-menu validation (v2, 2026-07-19). The old
    /// two-pointer signature was too weak: any stale pair of adjacent stock
    /// records in freed heap passed it (the mid-turn ghost, count=1). A REAL
    /// release menu always has: full stock + the new persona (count ≥ 5), the
    /// ENTIRE stock pointer chain consecutive (+0x30 steps), a sane cursor, and
    /// a VALID embedded new-persona record at +0x1C (id 1..512, level 1..99 —
    /// the ghost read id 0x4500).</summary>
    private bool IsMenu(nint menu)
    {
        int count = RdI(menu + CountOff);
        int cursor = RdI(menu + CursorOff);
        if (count < 5 || count > 16 || cursor < 0 || cursor >= count) return false;
        nint p0 = RdP(menu + ListOff);
        if ((ulong)p0 < PersonaLo || (ulong)p0 >= PersonaHi) return false;
        for (int i = 1; i <= count - 2; i++)
            if (RdP(menu + ListOff + i * 8) != p0 + i * 0x30) return false;
        if (RdU16(p0 + RecId) is < 1 or > 512) return false;
        int nid = RdU16(menu + 0x1E), nlv = RdU8(menu + 0x20);
        return nid >= 1 && nid <= 512 && nlv >= 1 && nlv <= 99;
    }

    private static readonly byte[] ShufflePrefix = System.Text.Encoding.ASCII.GetBytes("battle_shuffle");
    private static readonly byte[] ResultTaskName = System.Text.Encoding.ASCII.GetBytes("battle_shuffle_result");

    /// <summary>The menu ANCHOR: task named EXACTLY "battle_shuffle_result" →
    /// work + 0x3C. 0 when the task isn't running.</summary>
    private nint ResultMenuAnchor()
    {
        foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
        {
            nint node = RdP((nint)head);
            for (int i = 0; i < 300 && node != 0; i++)
            {
                Span<byte> nm = stackalloc byte[24];
                if (!Read(node, nm, 24)) break;
                bool match = true;
                for (int j = 0; j < ResultTaskName.Length; j++)
                    if (nm[j] != ResultTaskName[j]) { match = false; break; }
                // Exact task = name + a NON-PRINTABLE byte (not necessarily NUL —
                // the ==0 check silently missed the anchor, 2026-07-19).
                if (match && (nm[ResultTaskName.Length] < 0x20 || nm[ResultTaskName.Length] >= 0x7F))
                {
                    nint work = RdP(node + 0x48);
                    return work > 0x10000 ? work + 0x3C : 0;
                }
                node = RdP(node + 0x50);
            }
        }
        return 0;
    }

    /// <summary>Is any battle_shuffle* named task alive? (registry heads walk —
    /// a few dozen nodes, effectively free; node name @+0x00, next @+0x50).</summary>
    private bool ShuffleFlowActive()
    {
        foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
        {
            nint node = RdP((nint)head);
            for (int i = 0; i < 300 && node != 0; i++)
            {
                Span<byte> nm = stackalloc byte[16];
                if (!Read(node, nm, 16)) break;
                bool match = true;
                for (int j = 0; j < ShufflePrefix.Length; j++)
                    if (nm[j] != ShufflePrefix[j]) { match = false; break; }
                if (match) return true;
                node = RdP(node + 0x50);
            }
        }
        return false;
    }

    /// <summary>TEMP diagnostic: log every named task in the 3 registry lists
    /// (node name @+0x00, next @+0x50) — hunting this menu's own task so the
    /// heap scan can be deleted. RPM-safe reads only.</summary>
    private void DumpTaskNames()
    {
        var sb = new System.Text.StringBuilder("[PersonaRelease] tasks:");
        foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
        {
            nint node = RdP((nint)head);
            for (int i = 0; i < 200 && node != 0; i++)
            {
                Span<byte> nm = stackalloc byte[24];
                if (!Read(node, nm, 24)) break;
                int len = 0;
                while (len < 24 && nm[len] >= 0x20 && nm[len] < 0x7F) len++;
                if (len > 0) sb.Append(' ').Append(System.Text.Encoding.ASCII.GetString(nm.Slice(0, len)));
                node = RdP(node + 0x50);
            }
            sb.Append(" |");
        }
        Log(sb.ToString());
    }

    /// <summary>TEMP: for every battle_shuffle* task, log its WORK pointer, the
    /// delta to the found menu, and any u64 INSIDE work[0..0x400] that equals the
    /// menu address (that offset = the anchor that replaces the heap scan).</summary>
    private void DumpShuffleAnchors(nint menu)
    {
        var sb = new System.Text.StringBuilder("[PersonaRelease] anchors:");
        foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
        {
            nint node = RdP((nint)head);
            for (int i = 0; i < 300 && node != 0; i++)
            {
                Span<byte> nm = stackalloc byte[24];
                if (!Read(node, nm, 24)) break;
                bool match = true;
                for (int j = 0; j < ShufflePrefix.Length; j++)
                    if (nm[j] != ShufflePrefix[j]) { match = false; break; }
                if (match)
                {
                    int len = 0;
                    while (len < 24 && nm[len] >= 0x20 && nm[len] < 0x7F) len++;
                    string name = System.Text.Encoding.ASCII.GetString(nm.Slice(0, len));
                    nint work = RdP(node + 0x48);
                    sb.Append($" {name}: work=0x{work:X} d={(long)menu - (long)work:X}");
                    for (int off = 0; off < 0x400; off += 8)
                        if (RdP(work + off) == menu) sb.Append($" [work+0x{off:X}=MENU]");
                }
                node = RdP(node + 0x50);
            }
        }
        Log(sb.ToString());
    }

    // TEMP cost meter for the sliced scan (per-slice ms, logged sparsely).
    private int _sliceCtr;

    /// <summary>One bounded slice of the heap sweep (≤SliceBudget bytes), resuming
    /// at <see cref="_scanCursor"/> and wrapping at ScanHi. Returns the menu when
    /// its signature is found in this slice, else 0.</summary>
    private nint ScanSlice()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] buf = new byte[0x100000];
        long budget = SliceBudget;
        ulong addr = _scanCursor;
        var mbi = new MBI();
        while (budget > 0)
        {
            if (addr >= ScanHi) { addr = ScanLo; break; }   // wrapped — resume next tick
            if (VirtualQuery((nint)addr, out mbi, (nint)sizeof(MBI)) == 0) { addr = ScanLo; break; }
            ulong end = (ulong)mbi.BaseAddress + (ulong)mbi.RegionSize;
            if (mbi.State == 0x1000 && mbi.Type == 0x20000 && (mbi.Protect & 0xEE) != 0 && (mbi.Protect & 0x100) == 0)
            {
                ulong start = Math.Max(addr, (ulong)mbi.BaseAddress);
                for (ulong p = start; p < end; p += (ulong)buf.Length - 0x40)
                {
                    int len = (int)Math.Min((ulong)buf.Length, end - p);
                    if (!Read((nint)p, buf, len)) break;
                    budget -= len;
                    fixed (byte* b = buf)
                    {
                        for (int i = 0; i + 16 <= len; i += 8)
                        {
                            ulong v0 = *(ulong*)(b + i);
                            if (v0 < PersonaLo || v0 >= PersonaHi) continue;
                            ulong v1 = *(ulong*)(b + i + 8);
                            if (v1 != v0 + 0x30) continue;
                            // (p+i) is list[0]; menuBase = that − 0x54.
                            nint cand = (nint)(p + (ulong)i) - ListOff;
                            if (IsMenu(cand)) { _scanCursor = p; return cand; }
                        }
                    }
                    if (budget <= 0) { addr = p + (ulong)buf.Length - 0x40; goto done; }
                }
            }
            addr = end;
        }
        done:
        _scanCursor = addr;
        if (++_sliceCtr % 40 == 0)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log($"[PersonaRelease] scan slice {ms:F1}ms (read {(SliceBudget - budget) / 1048576}MB)");
        }
        return 0;
    }

    // ---- RPM-safe reads ----
    private static int RdI(nint a) { Span<byte> s = stackalloc byte[4]; return Read(a, s, 4) ? MemoryMarshal.Read<int>(s) : -1; }
    private static int RdU16(nint a) { Span<byte> s = stackalloc byte[2]; return Read(a, s, 2) ? MemoryMarshal.Read<ushort>(s) : 0; }
    private static int RdU8(nint a) { Span<byte> s = stackalloc byte[1]; return Read(a, s, 1) ? s[0] : 0; }
    private static nint RdP(nint a) { Span<byte> s = stackalloc byte[8]; return Read(a, s, 8) ? (nint)MemoryMarshal.Read<long>(s) : 0; }

    private static bool Read(nint addr, Span<byte> dst, int len)
    {
        fixed (byte* d = dst)
            return ReadProcessMemory(GetCurrentProcess(), addr, d, (nint)len, out nint got) && (int)got == len;
    }
    private static bool Read(nint addr, byte[] dst, int len)
    {
        fixed (byte* d = dst)
            return ReadProcessMemory(GetCurrentProcess(), addr, d, (nint)len, out nint got) && (int)got == len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MBI
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")] private static extern nint VirtualQuery(nint a, out MBI b, nint l);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint h, nint a, byte* buf, nint n, out nint got);
}
