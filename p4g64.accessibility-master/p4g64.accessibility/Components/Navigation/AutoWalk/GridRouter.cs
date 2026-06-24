using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// A* router over the in-game minimap grid (auto-walk plan P1,
/// plan_2026-06-12.md). The grid IS the game's own knowledge of the floor:
/// 24×16 cells at <c>0x1411AB39C</c>, flag byte 1 = walkable (doors included),
/// 2 = boundary, 0 = unexplored. We route 4-directionally through flag-1 cells
/// only, so a route can only cross terrain the player has already uncovered —
/// targets in unexplored space simply have no route yet (callers fall back to
/// straight-line distance speech).
///
/// World↔cell mapping reuses <see cref="MinimapTracker.WorldToCell"/> /
/// <see cref="MinimapTracker.CellToWorld"/> (the renderer's own transform), and
/// the world direction of "+1 col" / "+1 row" is computed at runtime from that
/// transform rather than assumed, so segment directions stay correct even if
/// an axis is mirrored.
///
/// Output is the raw cell path plus the same path compressed into straight
/// segments [(world direction, cell count)…] — the shared shape both the
/// spoken briefing (<see cref="RouteSpeech"/>) and the future AutoWalker
/// consume. One cell ≈ one walking step is spoken as "one meter" (user's
/// wording).
/// </summary>
internal static class GridRouter
{
    internal struct Segment
    {
        public float DirX, DirZ;   // unit world direction of this leg
        public int Cells;          // length in grid cells
        public int Dr, Dc;         // grid-space step, kept for the walker
    }

    internal sealed class Route
    {
        public List<(int row, int col)> Cells = new();
        public List<Segment> Segments = new();
        public int TotalCells;     // path length in cells
    }

    /// <summary>
    /// The route's cells as world points, start cell excluded. Each point is
    /// the cell center PUSHED AWAY from non-walkable neighbours (~0.2 cell) so
    /// the walker tracks the corridor middle — raw centers can sit close to a
    /// wall, and aiming at them ground the walker along walls (live
    /// 2026-06-11, repeated stalls at the same wall z).
    /// </summary>
    /// <param name="route">The A* route.</param>
    /// <param name="includeStart">Include the start cell's safe center as the
    /// first point — used after a blocked recovery so the walker re-centers
    /// in the corridor before attacking the route again (re-routing from a
    /// wall-hugging position otherwise grinds immediately).</param>
    internal static List<(float x, float z)> PathWorld(Route route, bool includeStart = false)
    {
        var pts = new List<(float, float)>();

        // World direction of one grid step, measured from the live transform.
        float offUnits = CellPitch() * 0.25f;
        (float, float) rowDir = (0f, 0f), colDir = (0f, 0f);
        bool haveDirs = false;
        if (offUnits > 0
            && MinimapTracker.CellToWorld(0, 0, out float x00, out float z00)
            && MinimapTracker.CellToWorld(1, 0, out float x10, out float z10)
            && MinimapTracker.CellToWorld(0, 1, out float x01, out float z01))
        {
            rowDir = Normalize(x10 - x00, z10 - z00);
            colDir = Normalize(x01 - x00, z01 - z00);
            haveDirs = true;
        }

        for (int i = includeStart ? 0 : 1; i < route.Cells.Count; i++)
        {
            var (r, c) = route.Cells[i];
            // A door cell's waypoint is the door's EXACT opening, not the
            // wall-pushed cell centre (which is the frame).
            if (DoorCellWorld(r, c, out float dwx, out float dwz)) { pts.Add((dwx, dwz)); continue; }
            if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
            if (haveDirs)
            {
                if (!Walkable(r + 1, c)) { wx -= rowDir.Item1 * offUnits; wz -= rowDir.Item2 * offUnits; }
                if (!Walkable(r - 1, c)) { wx += rowDir.Item1 * offUnits; wz += rowDir.Item2 * offUnits; }
                if (!Walkable(r, c + 1)) { wx -= colDir.Item1 * offUnits; wz -= colDir.Item2 * offUnits; }
                if (!Walkable(r, c - 1)) { wx += colDir.Item1 * offUnits; wz += colDir.Item2 * offUnits; }
            }
            pts.Add((wx, wz));
        }
        return pts;
    }

    /// <summary>
    /// World units per grid cell, measured live from the renderer transform.
    /// ~1,200-1,500u in Yukiko's Castle (2026-06-11 log) — i.e. one cell is
    /// ~5-6 walking steps at 250u/step, NOT one step. Returns 0 if the
    /// transform isn't readable. Cached per floor would be nicer; it's cheap
    /// enough to recompute.
    /// </summary>
    internal static float CellPitch()
    {
        if (!MinimapTracker.CellToWorld(0, 0, out float x0, out float z0)) return 0;
        if (!MinimapTracker.CellToWorld(1, 0, out float x1, out float z1)) return 0;
        float dx = x1 - x0, dz = z1 - z0;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Is a usable minimap grid present? False in open hub areas like the
    /// TV-world lobby ("Entrance", major 20/1), which have no generated floor
    /// grid — there the renderer transform is unreadable (CellPitch 0) and no
    /// cell is walkable, so A* can't route. AutoWalker uses this to fall back
    /// to a straight-line walk instead of aborting with "no mapped path".
    /// </summary>
    internal static bool HasGrid()
    {
        if (CellPitch() <= 0) return false;
        for (int r = 0; r < MinimapTracker.ROWS; r++)
        for (int c = 0; c < MinimapTracker.COLS; c++)
            if (Walkable(r, c)) return true;
        return false;
    }

    /// <summary>
    /// Route from world (px,pz) to world (tx,tz). Returns null when either end
    /// doesn't map onto the grid, the target's cell (and its 4 neighbours) is
    /// unwalkable/unexplored, or no flag-1 path connects them.
    /// </summary>
    internal static Route? FindRoute(float px, float pz, float tx, float tz)
    {
        if (!MinimapTracker.WorldToCell(px, pz, out int sr, out int sc)) return null;
        if (!MinimapTracker.WorldToCell(tx, tz, out int gr, out int gc)) return null;

        // The player's own cell occasionally reads flag!=1 mid-transition;
        // accept it regardless — we're standing on it, it's walkable.
        // The goal cell may be a wall-adjacent marker (chest against a wall):
        // if unwalkable, retarget to its most start-ward walkable neighbour.
        if (!Walkable(gr, gc))
        {
            int bestR = -1, bestC = -1; float bestD = float.MaxValue;
            foreach (var (dr, dc) in Steps4)
            {
                int nr = gr + dr, nc = gc + dc;
                if (!Walkable(nr, nc)) continue;
                float d = MathF.Abs(nr - sr) + MathF.Abs(nc - sc);
                if (d < bestD) { bestD = d; bestR = nr; bestC = nc; }
            }
            if (bestR < 0) return null;
            gr = bestR; gc = bestC;
        }

        var cells = AStar(sr, sc, gr, gc);
        if (cells == null) return null;

        var route = new Route { Cells = cells, TotalCells = cells.Count - 1 };
        BuildSegments(route);
        return route;
    }

    /// <summary>
    /// World position of the cell ~<paramref name="cellsAhead"/> steps along the A* route
    /// to (tx,tz) — a NEAR waypoint so the walker takes a SHORT leg and stays inside the
    /// wall-mesh's near-player bubble (where the string-pull is valid), instead of
    /// bee-lining a far target straight into walls the mesh can't see. False if no route.
    /// </summary>
    internal static bool StepToward(float px, float pz, float tx, float tz, int cellsAhead, out float wx, out float wz)
    {
        wx = wz = 0;
        var route = FindRoute(px, pz, tx, tz);
        if (route == null || route.Cells.Count == 0) return false;
        int i = Math.Min(Math.Max(1, cellsAhead), route.Cells.Count - 1);
        var (r, c) = route.Cells[i];
        return MinimapTracker.CellToWorld(r, c, out wx, out wz);
    }

    /// <summary>
    /// World position of the nearest stairs cell (flag=1, sprite=0x0C — the
    /// StairFinder signature), for routing the Exits category. Returns false
    /// if the stairs aren't on the explored map yet.
    /// </summary>
    internal static bool FindNearestStairs(float px, float pz, out float wx, out float wz)
    {
        wx = wz = 0;
        float best = float.MaxValue;
        for (int r = 0; r < MinimapTracker.ROWS; r++)
        for (int c = 0; c < MinimapTracker.COLS; c++)
        {
            if (!MinimapTracker.ReadCell(r, c, out var cell)) continue;
            if (cell.Flag != 1 || cell.Sprite != 0x0C) continue;
            if (!MinimapTracker.CellToWorld(r, c, out float x, out float z)) continue;
            float dx = x - px, dz = z - pz;
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; wx = x; wz = z; }
        }
        return best < float.MaxValue;
    }

    /// <summary>Walkability of one cell — exposed for AutoWalker's corridor centering.</summary>
    internal static bool CellWalkable(int r, int c) => Walkable(r, c);

    /// <summary>
    /// The EXPLORED walkable cell that borders UNexplored space (flag 0) and is CLOSEST
    /// to (towardX,towardZ) — a "frontier" the walker can reach (over known cells) and,
    /// by standing on it, reveal more map. Skips cells in <paramref name="tried"/>
    /// (key = r*COLS+c). Returns its world XZ; false when none remain. Used by
    /// TravelToStairs to fan out toward the stairs until a clean route opens up.
    /// </summary>
    internal static bool FindFrontier(float px, float pz, float towardX, float towardZ,
                                      HashSet<int> tried, out float fx, out float fz)
    {
        fx = fz = 0;
        float best = float.MaxValue; int bestKey = -1;
        for (int r = 0; r < MinimapTracker.ROWS; r++)
        for (int c = 0; c < MinimapTracker.COLS; c++)
        {
            if (!Walkable(r, c)) continue;
            if (!(IsUnexplored(r - 1, c) || IsUnexplored(r + 1, c) || IsUnexplored(r, c - 1) || IsUnexplored(r, c + 1)))
                continue;
            int key = r * MinimapTracker.COLS + c;
            if (tried.Contains(key)) continue;
            if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
            float score = (wx - towardX) * (wx - towardX) + (wz - towardZ) * (wz - towardZ);
            if (score < best) { best = score; fx = wx; fz = wz; bestKey = key; }
        }
        return bestKey >= 0;
    }

    private static bool IsUnexplored(int r, int c)
        => r >= 0 && r < MinimapTracker.ROWS && c >= 0 && c < MinimapTracker.COLS
           && MinimapTracker.ReadCell(r, c, out var cell) && cell.Flag == 0;

    // Door cells are PASSABLE even though the minimap marks them as wall-
    // boundary (flag 2) — but ONLY when explicitly enabled (a normal route
    // failed because the target is behind a door). Enabling them for every
    // route pulled A* toward door frames for ordinary chests (live 2026-06-11:
    // "it loves the door frame"). Each cell maps to the door's EXACT world XZ
    // so the waypoint is the opening, not the offset cell centre.
    private static readonly Dictionary<int, (float x, float z)> _doorCells = new();

    // Door cells are tracked for PROXIMITY (NearDoorCell suppression) always,
    // but only made WALKABLE in A* when _allowDoorRouting — coarse-cell
    // routing through doors produced backwards paths (live 2026-06-11), so the
    // walker goes via the door explicitly instead (route-to-door + straight
    // through). Keep this off for A*.
    private static bool _allowDoorRouting;

    internal static void SetDoorCells(List<(float x, float z)> doors)
    {
        _doorCells.Clear();
        foreach (var (x, z) in doors)
            if (MinimapTracker.WorldToCell(x, z, out int r, out int c))
                _doorCells[r * MinimapTracker.COLS + c] = (x, z);
    }

    internal static void ClearDoorCells() => _doorCells.Clear();
    internal static void AllowDoorRouting(bool on) => _allowDoorRouting = on;

    /// <summary>Is (r,c) a known door cell (passable despite the wall flag)?</summary>
    internal static bool IsDoorCell(int r, int c)
        => _doorCells.ContainsKey(r * MinimapTracker.COLS + c);

    /// <summary>
    /// Distance to the nearest known door's exact XZ, or +∞ if none. Used for a
    /// distance-based "am I at a doorway" test (cell-adjacency missed frames
    /// ~1.5 steps out — live 2026-06-11).
    /// </summary>
    internal static float NearestDoorDist(float px, float pz)
    {
        float best = float.PositiveInfinity;
        foreach (var w in _doorCells.Values)
        {
            float dx = w.x - px, dz = w.z - pz;
            float d2 = dx * dx + dz * dz;
            if (d2 < best) best = d2;
        }
        return best == float.PositiveInfinity ? best : MathF.Sqrt(best);
    }

    private static bool DoorCellWorld(int r, int c, out float x, out float z)
    {
        if (_doorCells.TryGetValue(r * MinimapTracker.COLS + c, out var w)) { x = w.x; z = w.z; return true; }
        x = z = 0; return false;
    }

    /// <summary>
    /// Nearest WALKABLE cell-centre world position to (wx,wz), searched in a
    /// small ring. Doors report their actor XZ which sits INSIDE the wall/frame
    /// (live 2026-06-11: walker drove at it, hit the frame, stopped off to the
    /// side). The doorway itself is a walkable cell — snapping the target there
    /// puts the player in the opening where CHECK fires. Returns false if no
    /// walkable cell is near or the transform is unreadable.
    /// </summary>
    internal static bool NearestWalkableWorld(float wx, float wz, out float ox, out float oz)
    {
        ox = wx; oz = wz;
        if (!MinimapTracker.WorldToCell(wx, wz, out int r0, out int c0)) return false;
        for (int ring = 0; ring <= 2; ring++)
        {
            int bestR = -1, bestC = -1; float bestD = float.MaxValue;
            for (int dr = -ring; dr <= ring; dr++)
            for (int dc = -ring; dc <= ring; dc++)
            {
                if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != ring) continue;   // ring shell only
                int r = r0 + dr, c = c0 + dc;
                if (!Walkable(r, c)) continue;
                if (!MinimapTracker.CellToWorld(r, c, out float cx, out float cz)) continue;
                float d = (cx - wx) * (cx - wx) + (cz - wz) * (cz - wz);
                if (d < bestD) { bestD = d; bestR = r; bestC = c; ox = cx; oz = cz; }
            }
            if (bestR >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Unit world directions of one +row / +col grid step (measured from the
    /// live transform). False when the transform isn't readable.
    /// </summary>
    internal static bool TryGetStepAxes(out (float x, float z) rowDir, out (float x, float z) colDir)
    {
        rowDir = colDir = (0, 0);
        if (!MinimapTracker.CellToWorld(0, 0, out float x00, out float z00)) return false;
        if (!MinimapTracker.CellToWorld(1, 0, out float x10, out float z10)) return false;
        if (!MinimapTracker.CellToWorld(0, 1, out float x01, out float z01)) return false;
        rowDir = Normalize(x10 - x00, z10 - z00);
        colDir = Normalize(x01 - x00, z01 - z00);
        return (rowDir.x != 0 || rowDir.z != 0) && (colDir.x != 0 || colDir.z != 0);
    }

    // ── internals ────────────────────────────────────────────────────────────

    private static readonly (int dr, int dc)[] Steps4 = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    private static bool Walkable(int r, int c)
    {
        if (r < 0 || r >= MinimapTracker.ROWS || c < 0 || c >= MinimapTracker.COLS) return false;
        if (_allowDoorRouting && _doorCells.ContainsKey(r * MinimapTracker.COLS + c)) return true;
        return MinimapTracker.ReadCell(r, c, out var cell) && cell.Flag == 1;
    }

    private static List<(int row, int col)>? AStar(int sr, int sc, int gr, int gc)
    {
        const int Rows = MinimapTracker.ROWS, Cols = MinimapTracker.COLS;
        int Idx(int r, int c) => r * Cols + c;

        var open = new PriorityQueue<(int r, int c), int>();
        var gScore = new int[Rows * Cols];
        var cameFrom = new int[Rows * Cols];
        Array.Fill(gScore, int.MaxValue);
        Array.Fill(cameFrom, -1);

        gScore[Idx(sr, sc)] = 0;
        open.Enqueue((sr, sc), Math.Abs(gr - sr) + Math.Abs(gc - sc));

        while (open.TryDequeue(out var cur, out _))
        {
            var (r, c) = cur;
            if (r == gr && c == gc)
            {
                var path = new List<(int, int)>();
                int i = Idx(r, c);
                while (i >= 0) { path.Add((i / Cols, i % Cols)); i = cameFrom[i]; }
                path.Reverse();
                return path;
            }
            int g = gScore[Idx(r, c)];
            foreach (var (dr, dc) in Steps4)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                // Start cell is exempt from the walkability check (see caller);
                // every other cell on the path must be flag-1.
                if (!Walkable(nr, nc)) continue;
                int ng = g + 1;
                if (ng >= gScore[Idx(nr, nc)]) continue;
                gScore[Idx(nr, nc)] = ng;
                cameFrom[Idx(nr, nc)] = Idx(r, c);
                open.Enqueue((nr, nc), ng + Math.Abs(gr - nr) + Math.Abs(gc - nc));
            }
        }
        return null;
    }

    private static void BuildSegments(Route route)
    {
        if (route.Cells.Count < 2) return;

        // World direction of one grid step, measured from the live transform.
        if (!MinimapTracker.CellToWorld(0, 0, out float x00, out float z00)) return;
        if (!MinimapTracker.CellToWorld(1, 0, out float x10, out float z10)) return;
        if (!MinimapTracker.CellToWorld(0, 1, out float x01, out float z01)) return;
        var rowDir = Normalize(x10 - x00, z10 - z00);
        var colDir = Normalize(x01 - x00, z01 - z00);

        int curDr = 0, curDc = 0, len = 0;
        for (int i = 1; i < route.Cells.Count; i++)
        {
            int dr = route.Cells[i].row - route.Cells[i - 1].row;
            int dc = route.Cells[i].col - route.Cells[i - 1].col;
            if (dr == curDr && dc == curDc) { len++; continue; }
            if (len > 0) route.Segments.Add(MakeSegment(curDr, curDc, len, rowDir, colDir));
            curDr = dr; curDc = dc; len = 1;
        }
        if (len > 0) route.Segments.Add(MakeSegment(curDr, curDc, len, rowDir, colDir));
    }

    private static Segment MakeSegment(int dr, int dc, int len, (float x, float z) rowDir, (float x, float z) colDir)
        => new()
        {
            DirX = dr * rowDir.x + dc * colDir.x,
            DirZ = dr * rowDir.z + dc * colDir.z,
            Cells = len,
            Dr = dr,
            Dc = dc,
        };

    private static (float x, float z) Normalize(float x, float z)
    {
        float m = MathF.Sqrt(x * x + z * z);
        return m < 1e-6f ? (0, 0) : (x / m, z / m);
    }
}
