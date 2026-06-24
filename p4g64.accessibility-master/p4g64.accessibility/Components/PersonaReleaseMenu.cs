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
    private long _nextScan;

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
                // Re-validate the latched menu; if gone, drop it.
                if (_menu != 0 && !IsMenu(_menu))
                {
                    _menu = 0;
                    _lastCursor = -1;
                }

                // Locate the menu (slow scan when not open; the scan IS the detector).
                if (_menu == 0)
                {
                    long now = Environment.TickCount64;
                    if (now < _nextScan) continue;
                    _nextScan = now + 1200;
                    _menu = FindMenu();
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
                    }
                    continue;
                }

                int count = RdI(_menu + CountOff);
                int cursor = RdI(_menu + CursorOff);
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
            // The new persona isn't in the array; best-effort, the static stock
            // appends it right after the last listed persona (+0x30).
            nint last = count >= 2 ? RdP(menu + ListOff + (count - 2) * 8) : 0;
            if (last != 0 && (ulong)last >= PersonaLo && (ulong)last < PersonaHi)
            {
                nint rec = last + 0x30;
                int nid = RdU16(rec + RecId);
                if (nid >= 1 && nid <= 512)
                    return $"New persona, {SafeName(nid)}, level {RdU8(rec + RecLevel)}. 1 of {count}";
            }
            return $"New persona. 1 of {count}";
        }
        nint pr = RdP(menu + ListOff + (cursor - 1) * 8);
        if (pr == 0 || (ulong)pr < PersonaLo || (ulong)pr >= PersonaHi)
            return $"Item {cursor + 1} of {count}";
        return $"{SafeName(RdU16(pr + RecId))}, level {RdU8(pr + RecLevel)}. {cursor + 1} of {count}";
    }

    private static string SafeName(int id)
    {
        string n = "Persona";
        try { n = Native.Persona.GetName(id); } catch { }
        return string.IsNullOrWhiteSpace(n) ? $"Persona {id}" : n;
    }

    // A struct is the release menu if list[0] and list[1] are consecutive PersonaInfo
    // pointers (in the static region, 0x30 apart) with a valid id, and cursor/count
    // are sane.
    private bool IsMenu(nint menu)
    {
        int count = RdI(menu + CountOff);
        int cursor = RdI(menu + CursorOff);
        if (count < 1 || count > 16 || cursor < 0 || cursor >= count) return false;
        nint p0 = RdP(menu + ListOff), p1 = RdP(menu + ListOff + 8);
        if ((ulong)p0 < PersonaLo || (ulong)p0 >= PersonaHi) return false;
        if (p1 != p0 + 0x30) return false;
        int id = RdU16(p0 + RecId);
        return id >= 1 && id <= 512;
    }

    private nint FindMenu()
    {
        if (Scan(0x10000, 0x1_0000_0000, out nint m)) return m;
        return 0;
    }

    private bool Scan(ulong lo, ulong hi, out nint menu)
    {
        menu = 0;
        byte[] buf = new byte[0x100000];
        ulong addr = lo;
        var mbi = new MBI();
        while (addr < hi)
        {
            if (VirtualQuery((nint)addr, out mbi, (nint)sizeof(MBI)) == 0) break;
            ulong end = (ulong)mbi.BaseAddress + (ulong)mbi.RegionSize;
            if (mbi.State == 0x1000 && mbi.Type == 0x20000 && (mbi.Protect & 0xEE) != 0 && (mbi.Protect & 0x100) == 0)
            {
                for (ulong p = (ulong)mbi.BaseAddress; p < end; p += (ulong)buf.Length - 0x40)
                {
                    int len = (int)Math.Min((ulong)buf.Length, end - p);
                    if (!Read((nint)p, buf, len)) break;
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
                            if (IsMenu(cand)) { menu = cand; return true; }
                        }
                    }
                }
            }
            addr = end;
        }
        return false;
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
