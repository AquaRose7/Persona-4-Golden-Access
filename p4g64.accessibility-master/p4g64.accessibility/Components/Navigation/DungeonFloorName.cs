using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Resolves the CORRECT current dungeon floor name by reading the game's own
/// banner string — robust for every dungeon/floor, unlike the old hardcoded
/// (major,minor) table (which broke from 3F up because deep floors all report
/// the same major/minor, RE'd 2026-06-17).
///
/// How it works (found via live memory analysis 2026-06-17):
/// - The game keeps the English area-name strings in a static blob at
///   <see cref="NameLo"/>..<see cref="NameHi"/> (Yukiko/Bathhouse/Marukyu/Void
///   Quest/Secret Lab/Heaven/Magatsu/Yomotsu/Memories + the floor suffixes).
/// - The live floor banner is a heap UI object whose <b>+0xA0</b> field points
///   at the current floor's string. Stale banners from previous floors LINGER in
///   memory, but only the ACTIVE one has a populated render header at <b>+0x00</b>
///   (non-zero); stale ones are zeroed there. During the brief cross-fade right
///   after a floor change, the previous floor's banner is also still active — so
///   we additionally prefer the candidate whose name != the previously announced
///   one.
/// - The floor "code" at 0x140EC4018 is an internal AREA id (5/7/9 for 1F/3F/4F),
///   NOT the floor number, so it can't be used directly — don't retry that.
///
/// All reads go through ReadProcessMemory-on-self so a region freed mid-scan
/// fails gracefully instead of raising an uncatchable AccessViolationException.
/// </summary>
internal static unsafe class DungeonFloorName
{
    private const ulong NameLo = 0x140931940UL;   // first English area-name string
    private const ulong NameHi = 0x140932940UL;   // just past the last (floor suffixes)
    private const int NameOff = 0xA0;             // banner object → name-string pointer
    private const int ActiveOff = 0x00;           // render header: non-zero = on-screen

    /// <summary>
    /// Scan the heap for every ACTIVE floor-banner object (render header at +0x00
    /// non-zero) and return each as (name, objectAddress). The caller disambiguates
    /// (stale banners — e.g. the dungeon Gate — can stay "active", so a name/avoid
    /// filter isn't enough; the resolver picks the object it hasn't announced yet).
    /// </summary>
    internal static List<(string name, nint obj)> ScanActiveBanners()
    {
        var list = new List<(string, nint)>();
        // Banners observed in the low 4 GB; scan there first, fall back to the rest.
        CollectRange(0x10000, 0x1_0000_0000, list);
        if (list.Count == 0) CollectRange(0x1_0000_0000, 0x7FFF_0000_0000, list);
        return list;
    }

    private static void CollectRange(ulong lo, ulong hi, List<(string, nint)> outList)
    {
        byte[] buf = new byte[0x100000];
        ulong addr = lo;
        var mbi = new MBI();
        while (addr < hi)
        {
            if (VirtualQuery((nint)addr, out mbi, (nint)sizeof(MBI)) == 0) break;
            ulong regionEnd = (ulong)mbi.BaseAddress + (ulong)mbi.RegionSize;
            bool scannable = mbi.State == 0x1000 && mbi.Type == 0x20000 &&
                             (mbi.Protect & 0xEE) != 0 && (mbi.Protect & 0x100) == 0;
            if (scannable)
            {
                for (ulong p = (ulong)mbi.BaseAddress; p < regionEnd; p += (ulong)buf.Length)
                {
                    int len = (int)Math.Min((ulong)buf.Length, regionEnd - p);
                    if (!ReadSelf((nint)p, buf, len)) break;
                    fixed (byte* b = buf)
                    {
                        for (int i = 0; i + 8 <= len; i += 8)
                        {
                            ulong v = *(ulong*)(b + i);
                            if (v < NameLo || v >= NameHi) continue;
                            nint obj = (nint)(p + (ulong)i) - NameOff;
                            Span<byte> hdr = stackalloc byte[4];
                            if (!ReadSelf(obj + ActiveOff, hdr, 4)) continue;
                            if (MemoryMarshal.Read<int>(hdr) == 0) continue;   // stale/recycled
                            string? name = ReadName((nint)v);
                            if (name != null) outList.Add((name, obj));
                        }
                    }
                }
            }
            addr = regionEnd;
        }
    }

    private static string? ReadName(nint addr)
    {
        Span<byte> s = stackalloc byte[0x28];
        if (!ReadSelf(addr, s, s.Length)) return null;
        int end = s.IndexOf((byte)0);
        if (end <= 0) return null;
        try { return Encoding.ASCII.GetString(s[..end]); } catch { return null; }
    }

    private static bool ReadSelf(nint addr, Span<byte> dst, int len)
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

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, out MBI lpBuffer, nint dwLength);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte* lpBuffer, nint nSize, out nint got);
}
