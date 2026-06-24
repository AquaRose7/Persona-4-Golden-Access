using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Reads the in-game minimap data structure to identify the player's current
/// room and its connections to other rooms. Backs the higher-level
/// MinimapAnnouncer which speaks room changes via Tolk.
///
/// Address +0x1411AB39C holds a 24-row × 16-col grid of 16-byte cell records
/// (interpreted from the renderer FUN_1402C16B0). The world-to-cell transform
/// uses 8 float constants in .rdata that the renderer also reads. The dungeon
/// auto-generation populates this grid as the player explores; cells with
/// flag != 0 are "known" cells, and cells sharing a roomId are part of the
/// same room. Adjacent cells with different non-zero room IDs are connected
/// by doors (verified empirically 2026-04-27 via live trace — every detected
/// "exit" turned out to correspond to a real door the player can walk
/// through).
///
/// Per-cell layout (16 bytes):
///   +0x00 byte  flag         0 = unexplored / 1 = drawable / 2 = boundary
///   +0x01 byte  state        lower nibble 1 = drawable anchor; high nibble = side flags
///   +0x02 u16   roomId       unique per room on the floor
///   +0x04 byte  sprite       icon variant
///   +0x05 byte  modifier     color / rotation
///   +0x06 byte  width        room footprint width in cells
///   +0x07 byte  height       room footprint height in cells
///   +0x08..    misc          unidentified (animation, fade-in, etc.)
/// </summary>
internal static unsafe class MinimapTracker
{
    public static readonly nint GRID_BASE = (nint)0x1411AB39CL;
    public const int COLS = 16;
    public const int ROWS = 24;
    public const int CELL_SIZE = 16;

    // World→screen transform constants from FUN_1402C16B0 decompile.
    private static readonly nint ADDR_BIAS  = (nint)0x140965C30L;   // fVar4
    private static readonly nint ADDR_DIV_X = (nint)0x140966174L;   // fVar5
    private static readonly nint ADDR_SCL_X = (nint)0x140965744L;   // fVar3
    private static readonly nint ADDR_OFF_X = (nint)0x140965570L;   // fVar17
    private static readonly nint ADDR_DIV_Z = (nint)0x14096617CL;   // fVar6
    private static readonly nint ADDR_SCL_Z = (nint)0x1409659CCL;   // fVar18
    private static readonly nint ADDR_OFF_Z = (nint)0x14096504CL;   // fVar19
    private static readonly nint ADDR_CORR  = (nint)0x140965014L;   // fVar16

    public struct CellInfo
    {
        public byte Flag;
        public byte State;
        public ushort RoomId;
        public byte Sprite;     // +0x04 — icon variant; suspected to mark stair/save/treasure cells
        public byte Modifier;   // +0x05 — color/rotation modifier
        public byte Width;
        public byte Height;
    }

    public sealed class RoomInfo
    {
        public ushort RoomId;
        public int Width;
        public int Height;
        public List<(int row, int col)> Footprint = new();
        public List<(int row, int col, ushort roomId, string dir)> Exits = new();
        public int CurrentRow;
        public int CurrentCol;
    }

    // ── Readability caches ───────────────────────────────────────────────────
    // The grid and the transform constants are STATIC addresses (ASLR off);
    // once committed they stay committed. Per-cell VirtualQuery made every
    // 384-cell scan (StairFinder, GridRouter A*, FindNearestStairs) take
    // seconds on systems where an AV hooks the syscall — the 2026-06-11
    // "Exits category freezes the mod" report. Check once, cache only success.
    private static bool _gridOk, _xformOk;

    private static bool GridOk
        => _gridOk || (_gridOk = IsReadable(GRID_BASE, ROWS * COLS * CELL_SIZE));

    private static bool XformOk
        => _xformOk || (_xformOk =
               IsReadable(ADDR_BIAS, 4) && IsReadable(ADDR_DIV_X, 4) &&
               IsReadable(ADDR_SCL_X, 4) && IsReadable(ADDR_OFF_X, 4) &&
               IsReadable(ADDR_DIV_Z, 4) && IsReadable(ADDR_SCL_Z, 4) &&
               IsReadable(ADDR_OFF_Z, 4) && IsReadable(ADDR_CORR, 4));

    public static bool WorldToCell(float wx, float wz, out int row, out int col)
    {
        row = -1; col = -1;
        if (!float.IsFinite(wx) || !float.IsFinite(wz)) return false;
        if (!XformOk) return false;

        float bias = *(float*)ADDR_BIAS;
        float divX = *(float*)ADDR_DIV_X;
        float sclX = *(float*)ADDR_SCL_X;
        float offX = *(float*)ADDR_OFF_X;
        float divZ = *(float*)ADDR_DIV_Z;
        float sclZ = *(float*)ADDR_SCL_Z;
        float offZ = *(float*)ADDR_OFF_Z;
        float corr = *(float*)ADDR_CORR;
        if (!float.IsFinite(divX) || divX == 0f || !float.IsFinite(divZ) || divZ == 0f) return false;

        float sx = ((wx + bias) / divX) * sclX + offX - corr;
        float sz = ((wz + bias) / divZ) * sclZ + offZ - corr;
        col = (int)MathF.Round((sx - 200f) / 18f);
        row = (int)MathF.Round((sz - 6f)   / 18f);
        return row >= 0 && row < ROWS && col >= 0 && col < COLS;
    }

    /// <summary>
    /// Inverse of <see cref="WorldToCell"/>. Maps a (row, col) pair back to the
    /// approximate world XZ at the cell's center. Used by StairFinder to take
    /// a candidate cell from the minimap grid and turn it into "go to (X, Z)
    /// in world coords" for distance + direction speech. Returns false if the
    /// transform constants aren't readable.
    /// </summary>
    public static bool CellToWorld(int row, int col, out float wx, out float wz)
    {
        wx = 0; wz = 0;
        if (!XformOk) return false;

        float bias = *(float*)ADDR_BIAS;
        float divX = *(float*)ADDR_DIV_X;
        float sclX = *(float*)ADDR_SCL_X;
        float offX = *(float*)ADDR_OFF_X;
        float divZ = *(float*)ADDR_DIV_Z;
        float sclZ = *(float*)ADDR_SCL_Z;
        float offZ = *(float*)ADDR_OFF_Z;
        float corr = *(float*)ADDR_CORR;
        if (!float.IsFinite(sclX) || sclX == 0f || !float.IsFinite(sclZ) || sclZ == 0f) return false;

        // Inverse of: sx = ((wx + bias) / divX) * sclX + offX - corr
        //             col = round((sx - 200) / 18)
        // Recover the cell's screen-pixel center, then unwind the world transform.
        float sx = col * 18f + 200f;
        float sz = row * 18f + 6f;
        wx = ((sx + corr - offX) / sclX) * divX - bias;
        wz = ((sz + corr - offZ) / sclZ) * divZ - bias;
        return float.IsFinite(wx) && float.IsFinite(wz);
    }

    public static bool ReadCell(int row, int col, out CellInfo cell)
    {
        cell = default;
        if (row < 0 || row >= ROWS || col < 0 || col >= COLS) return false;
        if (!GridOk) return false;
        byte* p = (byte*)(GRID_BASE + (row * COLS + col) * CELL_SIZE);
        cell.Flag = p[0];
        cell.State = p[1];
        cell.RoomId = *(ushort*)(p + 2);
        cell.Sprite = p[4];
        cell.Modifier = p[5];
        cell.Width = p[6];
        cell.Height = p[7];
        return true;
    }

    /// <summary>
    /// Read all 16 raw bytes of a cell — used by the cursor's diagnostic
    /// logs so we can identify which byte signatures correspond to special
    /// cells like stairs/treasure/save points (the +0x04, +0x05 and
    /// +0x08..+0x0F bytes haven't been fully decoded yet). Returns false
    /// if the cell address isn't readable.
    /// </summary>
    public static bool ReadCellRawBytes(int row, int col, byte[] dest)
    {
        if (dest == null || dest.Length < CELL_SIZE) return false;
        if (row < 0 || row >= ROWS || col < 0 || col >= COLS) return false;
        if (!GridOk) return false;
        byte* p = (byte*)(GRID_BASE + (row * COLS + col) * CELL_SIZE);
        for (int i = 0; i < CELL_SIZE; i++) dest[i] = p[i];
        return true;
    }

    /// <summary>
    /// Resolve the player's current cell, room, and connections. Returns null
    /// if player coords don't map to a known cell or the cell isn't a room.
    /// </summary>
    public static RoomInfo? GetCurrentRoom(float playerX, float playerZ)
    {
        if (!WorldToCell(playerX, playerZ, out int row, out int col)) return null;
        if (!ReadCell(row, col, out var cur)) return null;
        if (cur.Flag == 0 || cur.RoomId == 0) return null;

        var info = new RoomInfo
        {
            RoomId = cur.RoomId,
            Width = cur.Width,
            Height = cur.Height,
            CurrentRow = row,
            CurrentCol = col,
        };

        for (int r = 0; r < ROWS; r++)
        {
            for (int c = 0; c < COLS; c++)
            {
                if (!ReadCell(r, c, out var nb)) continue;
                if (nb.Flag != 0 && nb.RoomId == cur.RoomId) info.Footprint.Add((r, c));
            }
        }

        var seenExitRooms = new HashSet<ushort>();
        foreach (var (r, c) in info.Footprint)
        {
            (int dr, int dc, string name)[] sides = new[]
            {
                (-1, 0, "back"),
                (+1, 0, "ahead"),
                (0, +1, "right"),
                (0, -1, "left"),
            };
            foreach (var (dr, dc, dirName) in sides)
            {
                int nr = r + dr, nc = c + dc;
                if (!ReadCell(nr, nc, out var nb)) continue;
                if (nb.Flag == 0 || nb.RoomId == cur.RoomId || nb.RoomId == 0) continue;
                if (!seenExitRooms.Add(nb.RoomId)) continue;
                info.Exits.Add((nr, nc, nb.RoomId, dirName));
            }
        }
        return info;
    }

    // VirtualQuery-backed safety net so a bad address doesn't kill the process.
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
        return true;
    }
}
