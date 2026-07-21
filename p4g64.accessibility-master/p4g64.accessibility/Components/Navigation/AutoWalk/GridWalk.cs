namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// Route planning over the minimap grid's OWN per-cell passage bits (decoded
/// 2026-07-16 — see memory/floor_boundary_in_minimap_grid.md). This REPLACES the
/// old mesh/learned-walls DungeonGrid planner: the grid is the game's own coarse
/// walkability map (1200u cells), read straight from <see cref="MinimapTracker"/>.
///
/// Grid↔world (from the decode): <c>+col → world +X</c>, <c>+row → world +Z</c>.
/// Cell flag (+0x00): 0 = void/unexplored, 1 = walkable, 2 = boundary.
/// Edge byte (+0x0A) LOW nibble = OPEN passages per side, BIT SET = open, CLEAR =
/// wall/boundary: 0x01 = south (−row), 0x02 = east (−col), 0x04 = north (+row),
/// 0x08 = west (+col).
///
/// The cell path this produces is a COARSE guide; the live collision sensor does
/// the fine in-lane steering while driving (that is what makes 1200u cells OK).
/// </summary>
internal static class GridWalk
{
    // Edge byte +0x0A low-nibble bit for the side toward (row+dRow, col+dCol).
    private static int SideBit(int dRow, int dCol)
        => dRow > 0 ? 0x04 : dRow < 0 ? 0x01 : dCol > 0 ? 0x08 : dCol < 0 ? 0x02 : 0;

    [ThreadStatic] private static byte[]? _raw;

    /// <summary>Cell flag == 1 (walkable). False for void/boundary/off-grid.</summary>
    internal static bool IsWalkable(int row, int col)
    {
        _raw ??= new byte[MinimapTracker.CELL_SIZE];
        return MinimapTracker.ReadCellRawBytes(row, col, _raw) && _raw[0] == 1;
    }

    /// <summary>Cell roomId (0 = void/unreadable).</summary>
    internal static ushort RoomIdOf(int row, int col)
    {
        _raw ??= new byte[MinimapTracker.CELL_SIZE];
        if (!MinimapTracker.ReadCellRawBytes(row, col, _raw)) return 0;
        return (ushort)(_raw[2] | (_raw[3] << 8));
    }

    /// <summary>Is the shared side toward (row+dRow, col+dCol) an OPEN passage
    /// per THIS cell's edge bits?</summary>
    internal static bool EdgeOpen(int row, int col, int dRow, int dCol)
    {
        _raw ??= new byte[MinimapTracker.CELL_SIZE];
        int bit = SideBit(dRow, dCol);
        if (bit == 0 || !MinimapTracker.ReadCellRawBytes(row, col, _raw)) return false;
        return (_raw[0x0A] & bit) != 0;
    }

    /// <summary>Are two ADJACENT cells walk-connected? True if the neighbour is
    /// walkable AND (the edge bit toward it is open — corridor/door — OR they share
    /// the same non-zero roomId — same open room interior, where edge bits are 0).
    /// This two-rule test is load-bearing: corridors are 1-cell rooms joined by edge
    /// bits, big rooms are roomId blobs with no interior edge bits (grid dump
    /// 2026-07-16). Edge-only A* could not cross a room interior; roomId-only could
    /// not cross a corridor. Both together trace the whole floor.</summary>
    internal static bool Connected(int row, int col, int dRow, int dCol)
    {
        if (!IsWalkable(row + dRow, col + dCol)) return false;
        if (EdgeOpen(row, col, dRow, dCol)) return true;
        ushort a = RoomIdOf(row, col), b = RoomIdOf(row + dRow, col + dCol);
        return a != 0 && a == b;
    }

    /// <summary>A* over walkable cells (edge-bit OR same-room connected). outPath =
    /// start..goal inclusive. Manhattan heuristic, 4-connected. <paramref name="blocked"/>
    /// cell keys (r*COLS+c) are treated as non-walkable (per-walk detours around a
    /// genuine obstacle). False = no path / bad endpoints.</summary>
    internal static bool TryCellPath(int r0, int c0, int r1, int c1, List<(int r, int c)> outPath,
        HashSet<int>? blocked = null)
    {
        outPath.Clear();
        if (!IsWalkable(r0, c0) || !IsWalkable(r1, c1)) return false;
        if (r0 == r1 && c0 == c1) { outPath.Add((r0, c0)); return true; }

        int Key(int r, int c) => r * MinimapTracker.COLS + c;
        var open = new PriorityQueue<(int r, int c), int>();
        var came = new Dictionary<int, (int r, int c)>();
        var g = new Dictionary<int, int>();
        open.Enqueue((r0, c0), 0);
        g[Key(r0, c0)] = 0;
        int H(int r, int c) => Math.Abs(r - r1) + Math.Abs(c - c1);
        Span<(int dr, int dc)> dirs = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

        while (open.Count > 0)
        {
            var (cr, cc) = open.Dequeue();
            if (cr == r1 && cc == c1)
            {
                var node = (r: cr, c: cc);
                while (true)
                {
                    outPath.Add(node);
                    if (node.r == r0 && node.c == c0) break;
                    node = came[Key(node.r, node.c)];
                }
                outPath.Reverse();
                return true;
            }
            int cg = g[Key(cr, cc)];
            foreach (var (dr, dc) in dirs)
            {
                int nr = cr + dr, nc = cc + dc;
                if (!Connected(cr, cc, dr, dc)) continue;
                int nk = Key(nr, nc);
                if (blocked != null && blocked.Contains(nk)) continue;
                int ng = cg + 1;
                if (g.TryGetValue(nk, out int old) && old <= ng) continue;
                g[nk] = ng; came[nk] = (cr, cc);
                open.Enqueue((nr, nc), ng + H(nr, nc));
            }
        }
        return false;
    }

    /// <summary>World-space wrapper: resolve endpoints to cells, path, emit cell CENTERS.</summary>
    internal static bool TryWorldPath(float px, float pz, float tx, float tz, List<(float x, float z)> outPts)
    {
        outPts.Clear();
        if (!MinimapTracker.WorldToCell(px, pz, out int r0, out int c0)) return false;
        if (!MinimapTracker.WorldToCell(tx, tz, out int r1, out int c1)) return false;
        var cells = new List<(int r, int c)>();
        if (!TryCellPath(r0, c0, r1, c1, cells)) return false;
        foreach (var (r, c) in cells)
            if (MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) outPts.Add((wx, wz));
        return outPts.Count > 0;
    }
}
