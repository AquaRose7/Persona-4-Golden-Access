using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Velvet Room — root menu reader (Fuse Personas / Check Compendium / Utilize
/// Skill Cards / Manage Rescue Requests / Check on Dwellers / Leave Velvet Room).
///
/// The root menu is a generic list widget with no usable hook point (every
/// candidate handler belongs to the fusion SUB-screens, not the root). We POLL:
/// a background thread finds the menu object by its signature and reads the
/// highlighted label. CRITICAL: all game-memory reads go through
/// ReadProcessMemory on OUR OWN process so a bad address returns false instead of
/// an uncatchable AccessViolation (direct in-process scanning crashed the game).
///
/// Object layout (confirmed live, memory/velvet_room_fusion_re.md):
///   count @obj+0x15E · 0xFFFF @obj+0x160 · cursor @obj+0x162 · scroll @obj+0x164
///   entries @obj+0x70 stride 0x18, +0x00 = char* label (plain ASCII)
/// Fingerprint vs other widgets: the always-present "Leave Velvet Room" entry.
/// </summary>
internal unsafe class VelvetMenu : IDisposable
{
    private const string Anchor = "Leave Velvet Room";

    private readonly Thread _thread;
    private volatile bool _running = true;
    private readonly byte[] _chunk = new byte[0x100000]; // 1 MB scan buffer
    private readonly byte[] _small = new byte[64];

    private nint _obj;
    private int  _lastSel = -1;
    private int  _idleTicks;
    // While >0 we just saw the velvet menu, so re-scan fast (the user is likely
    // bouncing between the root menu and a sub-screen). Decays to the cheap slow
    // scan during normal gameplay. 100ms/tick -> ~30s window.
    private int  _recency;

    internal VelvetMenu(IReloadedHooks hooks)
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "VelvetPoll" };
        _thread.Start();
        Log("[Velvet] poll reader started (RPM-safe).");
    }

    private void PollLoop()
    {
        while (_running)
        {
            try { Tick(); } catch { }
            Thread.Sleep(100);
        }
    }

    private void Tick()
    {
        if (_recency > 0) _recency--;

        if (_obj != 0)
        {
            if (TryReadMenu(_obj, out int sel, out int count, out string label))
            {
                _recency = 300; // ~30s fast-rescan window
                if (sel != _lastSel && label != null)
                {
                    _lastSel = sel;
                    Speech.Say(label, interrupt: true);
                }
                return;
            }
            _obj = 0;
            _lastSel = -1;
        }

        // Not cached: scan. Fast (~0.3s) right after we've been in the velvet
        // menu so returning from a sub-screen re-reads quickly; slow (~2s)
        // otherwise to keep the RPM sweep cheap during normal play.
        int interval = _recency > 0 ? 3 : 20;
        if (_idleTicks++ < interval) return;
        _idleTicks = 0;

        nint found = ScanForVelvetMenu();
        if (found != 0)
        {
            _obj = found;
            _recency = 300;
            _lastSel = -1;
            if (TryReadMenu(found, out int sel, out _, out string label) && label != null)
            {
                _lastSel = sel;
                Speech.Say(label, interrupt: true);
            }
        }
    }

    /// <summary>Read the menu state + highlighted label via RPM; also re-validates
    /// the object is still the velvet menu (anchor label present).</summary>
    private bool TryReadMenu(nint obj, out int sel, out int count, out string label)
    {
        sel = -1; count = 0; label = null;
        if (!RpmU16(obj + 0x160, out ushort sentinel) || sentinel != 0xFFFF) return false;
        if (!RpmU16(obj + 0x15E, out ushort c) || c < 2 || c > 16) return false;
        if (!RpmU16(obj + 0x162, out ushort cur) || cur >= c) return false;
        if (!RpmU16(obj + 0x164, out ushort scr) || scr > c) return false;
        count = c;
        sel = cur + scr;
        if (sel >= c) return false;

        bool anchorSeen = false;
        for (int i = 0; i < c; i++)
        {
            string s = RpmLabel(obj, i);
            if (s == Anchor) anchorSeen = true;
            if (i == sel) label = s;
        }
        if (!anchorSeen) return false;        // not the velvet menu
        return true;
    }

    private string RpmLabel(nint obj, int index)
    {
        if (!RpmPtr(obj + 0x70 + index * 0x18, out nint p)) return null;
        if (p <= 0x10000) return null;
        return RpmString(p, 48);
    }

    // ---- region scan (RPM-safe) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MBI
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect; public ushort PartitionId;
        public nint RegionSize; public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")] private static extern nint VirtualQuery(nint addr, out MBI mbi, nint len);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint h, nint addr, byte[] buf, nint size, out nint read);

    private const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, PAGE_GUARD = 0x100;

    private nint ScanForVelvetMenu()
    {
        nint addr = 0x10000;
        var mbiSize = (nint)Marshal.SizeOf<MBI>();
        while (addr < 0x7FFFFFFF0000 && _running)
        {
            if (VirtualQuery(addr, out MBI m, mbiSize) == 0) break;
            nint regionBase = m.BaseAddress;
            long regionSize = (long)m.RegionSize;
            if (regionSize <= 0) break;

            bool ok = m.State == MEM_COMMIT && m.Type == MEM_PRIVATE
                      && (m.Protect & 0xEE) != 0 && (m.Protect & PAGE_GUARD) == 0;
            if (ok && regionSize <= 0x4000000)
            {
                nint hit = ScanRegion(regionBase, regionSize);
                if (hit != 0) return hit;
            }
            addr = regionBase + (nint)regionSize;
        }
        return 0;
    }

    private nint ScanRegion(nint baseAddr, long size)
    {
        nint self = GetCurrentProcess();
        long off = 0;
        while (off < size && _running)
        {
            int want = (int)Math.Min(_chunk.Length, size - off);
            if (!ReadProcessMemory(self, baseAddr + (nint)off, _chunk, want, out nint got) || (long)got < 0x168)
            {
                off += want; // unreadable slice -> skip
                continue;
            }
            int n = (int)got;
            fixed (byte* p = _chunk)
            {
                for (int o = 0x160; o + 8 < n; o += 2)
                {
                    if (*(ushort*)(p + o) != 0xFFFF) continue;
                    int count = *(short*)(p + o - 2);
                    if (count < 2 || count > 16) continue;
                    int cursor = *(short*)(p + o + 2);
                    if (cursor < 0 || cursor >= count) continue;
                    int scroll = *(short*)(p + o + 4);
                    if (scroll < 0 || scroll > count) continue;
                    nint obj = baseAddr + (nint)(off + o - 0x160);
                    // Validate fully via RPM (object may straddle chunk end).
                    if (TryReadMenu(obj, out _, out _, out _)) return obj;
                }
            }
            // overlap so a signature split across chunks isn't missed
            off += want - 0x200;
            if (want < _chunk.Length) break;
        }
        return 0;
    }

    // ---- RPM helpers (never crash) ----

    private bool RpmU16(nint addr, out ushort v)
    {
        v = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), addr, _small, 2, out nint got) || (long)got < 2) return false;
        v = (ushort)(_small[0] | (_small[1] << 8));
        return true;
    }

    private bool RpmPtr(nint addr, out nint v)
    {
        v = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), addr, _small, 8, out nint got) || (long)got < 8) return false;
        long x = 0;
        for (int i = 0; i < 8; i++) x |= (long)_small[i] << (8 * i);
        v = (nint)x;
        return true;
    }

    private string RpmString(nint addr, int max)
    {
        if (!ReadProcessMemory(GetCurrentProcess(), addr, _small, Math.Min(max, _small.Length), out nint got) || (long)got <= 0)
            return null;
        var sb = new StringBuilder();
        for (int i = 0; i < (int)got; i++)
        {
            byte b = _small[i];
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else return sb.Length > 0 ? sb.ToString() : null;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    public void Dispose() { _running = false; }
}
