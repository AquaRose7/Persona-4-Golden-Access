using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Runtime occupancy grid built by RASTERIZING the collision wall mesh (2026-06-20, Path A of the
/// auto-walk nav rework). Field areas like the school have no minimap grid, but the live scene-cache
/// wall triangles span the whole sub-area, so we voxelize them into a walkable/blocked grid and run
/// A* on it — global planning, which the old greedy/clearance steering lacked (that orbited in
/// local minima). See memory/autowalk_nav_research_2026-06-20.md.
///
/// World space = <see cref="FieldTracker.WorldPlayerPos"/> (the mesh is in that space; verified live).
/// Cell = 120 world units (= one walking step). Blocked cells are inflated by the player radius so a
/// straight leg between two cell centres physically clears the wall.
/// </summary>
internal sealed class MeshGrid
{
    public const float Cell = 60f;           // world units per cell. Fine enough that ~240u school
                                             // corridors stay several cells wide (survive raster bleed).
    private const int Inflate = 1;           // dilate blocked cells by ~1 cell (player half-width)
    private const float WallMinHeight = 60f; // skip near-horizontal tris (floor/ceiling), keep walls
    private const float WallBandTop = 220f;  // only keep walls anchored within this of the floor —
                                             // drops door-frame LINTELS/ceilings that (when Y is
                                             // flattened) would project down and SEAL doorways.

    public readonly float OriginX, OriginZ;  // world coord of cell (0,0)'s min corner
    public readonly int Cols, Rows;
    private readonly bool[] _blocked;         // row-major [r*Cols + c]
    public int BlockedCount { get; private set; }
    public static int LastSceneTris, LastWallTris;   // diag: last BuildFromScene triangle counts
    public static float LastMinY, LastMaxY;          // diag: Y-range of wall tris (to calibrate a height filter)

    private MeshGrid(float ox, float oz, int cols, int rows)
    { OriginX = ox; OriginZ = oz; Cols = cols; Rows = rows; _blocked = new bool[cols * rows]; }

    /// <summary>Mark a small blocked disc at a world point — used to stamp OTHER interactables/NPCs
    /// as "bumps" so A* routes around them and lands on the intended target, not a packed neighbour.</summary>
    public void StampObstacle(float x, float z, int radCells)
    {
        WorldToCell(x, z, out int r, out int c);
        for (int dr = -radCells; dr <= radCells; dr++)
            for (int dc = -radCells; dc <= radCells; dc++)
                Mark(r + dr, c + dc);
    }

    /// <summary>Force a small disc around a world point to WALKABLE. The voxel grid marks cells near
    /// walls as blocked, but a user-recorded spot is verified-standable — clear it so A* will route there.</summary>
    public void ClearAround(float x, float z, int radCells)
    {
        WorldToCell(x, z, out int r, out int c);
        for (int dr = -radCells; dr <= radCells; dr++)
            for (int dc = -radCells; dc <= radCells; dc++)
                if (InBounds(r + dr, c + dc) && _blocked[(r + dr) * Cols + (c + dc)])
                { _blocked[(r + dr) * Cols + (c + dc)] = false; BlockedCount--; }
    }

    /// <summary>Build a grid from a PREBUILT walkable mask (the offline-decoded field collision —
    /// database/overworld_walkgrid.json). walkable[r*cols+c] == true ⇒ floor; false ⇒ wall/void.
    /// This is the COMPLETE, accurate per-area layout (vs the runtime near-player wall bubble).</summary>
    public static MeshGrid FromWalkable(float ox, float oz, int cols, int rows, bool[] walkable)
    {
        var g = new MeshGrid(ox, oz, cols, rows);
        int n = Math.Min(cols * rows, walkable.Length), blocked = 0;
        for (int i = 0; i < n; i++) if (!walkable[i]) { g._blocked[i] = true; blocked++; }
        g.BlockedCount = blocked;
        return g;
    }

    public bool InBounds(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Cols;
    public bool Blocked(int r, int c) => !InBounds(r, c) || _blocked[r * Cols + c];
    private void Mark(int r, int c) { if (InBounds(r, c)) _blocked[r * Cols + c] = true; }

    public void WorldToCell(float x, float z, out int r, out int c)
    { c = (int)MathF.Floor((x - OriginX) / Cell); r = (int)MathF.Floor((z - OriginZ) / Cell); }
    public void CellToWorld(int r, int c, out float x, out float z)
    { x = OriginX + (c + 0.5f) * Cell; z = OriginZ + (r + 0.5f) * Cell; }

    // ── accumulated wall coverage (grows as the player walks; the scene cache only shows nearby
    // walls, so we UNION each scan into a per-area store → the grid sees more of the building over
    // time, closing the "only sees near things" gap). Reset on area change.
    private static readonly List<float[]> _accum = new();
    private static readonly HashSet<long> _accumKeys = new();
    private static string _accumArea = "";
    public static int AccumCount => _accum.Count;

    /// <summary>Fold the current scene-cache walls into the per-area accumulation store (deduped).</summary>
    public static void AccumulateScene(string area)
    {
        if (area != _accumArea) { _accum.Clear(); _accumKeys.Clear(); _accumArea = area; }
        FieldTracker.VisitWallTrianglesInScene(t =>
        {
            if (t.Length < 9) return;
            float cx = (t[0] + t[3] + t[6]) / 3f, cz = (t[2] + t[5] + t[8]) / 3f;
            float minY = MathF.Min(t[1], MathF.Min(t[4], t[7]));
            long qx = (long)MathF.Round(cx / 20f) & 0xFFFFF;
            long qz = (long)MathF.Round(cz / 20f) & 0xFFFFF;
            long qy = (long)MathF.Round(minY / 20f) & 0xFFF;
            long key = qx | (qz << 20) | (qy << 40);
            if (_accumKeys.Add(key) && _accum.Count < 6000) _accum.Add((float[])t.Clone());
        });
    }

    /// <summary>Reset accumulation (e.g. on a fresh walk you want re-scanned from scratch).</summary>
    public static void ResetAccum() { _accum.Clear(); _accumKeys.Clear(); _accumArea = ""; }

    /// <summary>Build from the per-area ACCUMULATED walls (preferred — wider coverage than one scan).
    /// Pass the player + target so the grid SPANS them (optimistic: unknown space between = walkable, so
    /// A* always has a complete route to a far target instead of giving up at the wall-bubble edge).</summary>
    public static MeshGrid? BuildAccumulated(float floorY, int inflate = Inflate,
        float ix1 = float.NaN, float iz1 = float.NaN, float ix2 = float.NaN, float iz2 = float.NaN)
        => BuildFromTris(_accum, floorY, inflate, ix1, iz1, ix2, iz2);

    /// <summary>Build from the live scene-cache wall triangles around the player. Null if too few.</summary>
    public static MeshGrid? BuildFromScene(float floorY, int inflate = Inflate)
    {
        var tris = new List<float[]>();
        FieldTracker.VisitWallTrianglesInScene(t => { if (t.Length >= 9) tris.Add(t); });
        return BuildFromTris(tris, floorY, inflate);
    }

    private static MeshGrid? BuildFromTris(List<float[]> tris, float floorY, int inflate,
        float ix1 = float.NaN, float iz1 = float.NaN, float ix2 = float.NaN, float iz2 = float.NaN)
    {
        LastSceneTris = tris.Count; LastWallTris = 0;
        if (tris.Count < 3) return null;

        // AABB over wall triangles (XZ), skipping near-horizontal (floor) tris.
        float mnx = 9e9f, mxx = -9e9f, mnz = 9e9f, mxz = -9e9f, mny = 9e9f, mxy = -9e9f; int walls = 0;
        foreach (var t in tris)
        {
            if (!IsWall(t, floorY)) continue;
            walls++;
            for (int v = 0; v + 2 < t.Length; v += 3)
            { mnx = MathF.Min(mnx, t[v]); mxx = MathF.Max(mxx, t[v]); mnz = MathF.Min(mnz, t[v + 2]); mxz = MathF.Max(mxz, t[v + 2]); }
            for (int v = 1; v + 1 < t.Length; v += 3)
            { mny = MathF.Min(mny, t[v]); mxy = MathF.Max(mxy, t[v]); }
        }
        LastMinY = mny; LastMaxY = mxy;
        LastWallTris = walls;
        if (walls == 0) return null;

        // OPTIMISTIC SPAN: stretch the AABB to include the player + target so the route's endpoints are
        // in-grid. Cells with no wall = walkable, so A* always returns a complete path to a far target
        // (no more PARTIAL/give-up-short at the wall-bubble edge).
        if (!float.IsNaN(ix1)) { mnx = MathF.Min(mnx, ix1); mxx = MathF.Max(mxx, ix1); mnz = MathF.Min(mnz, iz1); mxz = MathF.Max(mxz, iz1); }
        if (!float.IsNaN(ix2)) { mnx = MathF.Min(mnx, ix2); mxx = MathF.Max(mxx, ix2); mnz = MathF.Min(mnz, iz2); mxz = MathF.Max(mxz, iz2); }

        float pad = Cell * 2f;
        float ox = mnx - pad, oz = mnz - pad;
        int cols = (int)MathF.Ceiling((mxx + pad - ox) / Cell) + 1;
        int rows = (int)MathF.Ceiling((mxz + pad - oz) / Cell) + 1;
        if (cols < 2 || rows < 2 || (long)cols * rows > 200000) return null;   // sanity cap

        var g = new MeshGrid(ox, oz, cols, rows);
        foreach (var t in tris)
        {
            if (!IsWall(t, floorY)) continue;
            g.RasterEdge(t[0], t[2], t[3], t[5]);
            g.RasterEdge(t[3], t[5], t[6], t[8]);
            g.RasterEdge(t[6], t[8], t[0], t[2]);
        }
        g.InflateBlocked(inflate);
        return g;
    }

    private static bool IsWall(float[] t, float floorY)
    {
        float minY = MathF.Min(t[1], MathF.Min(t[4], t[7]));
        float maxY = MathF.Max(t[1], MathF.Max(t[4], t[7]));
        if ((maxY - minY) < WallMinHeight) return false;   // near-horizontal = floor/ceiling, not a wall
        return minY <= floorY + WallBandTop;               // floor-anchored wall, not a high lintel/ceiling
    }

    /// <summary>Thin (standard Bresenham) edge raster — minimal bleed so narrow doorways/corridors
    /// stay open. Under-marking is safer than over-marking here: A* finding a slightly-tight route is
    /// fine (the follower + the game's own collision keep the player off the wall), whereas
    /// over-marking SEALS doorways and A* fails outright.</summary>
    private void RasterEdge(float ax, float az, float bx, float bz)
    {
        WorldToCell(ax, az, out int r0, out int c0);
        WorldToCell(bx, bz, out int r1, out int c1);
        int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
        int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1;
        int err = dc - dr; int r = r0, c = c0;
        for (int guard = 0; guard < 4096; guard++)
        {
            Mark(r, c);
            if (r == r1 && c == c1) break;
            int e2 = 2 * err;
            if (e2 > -dr) { err -= dr; c += sc; }
            if (e2 < dc) { err += dc; r += sr; }
        }
    }

    private void InflateBlocked(int inflate)
    {
        if (inflate > 0)
        {
            var src = (bool[])_blocked.Clone();
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (src[r * Cols + c])
                        for (int dr = -inflate; dr <= inflate; dr++)
                            for (int dc = -inflate; dc <= inflate; dc++)
                                Mark(r + dr, c + dc);
        }
        BlockedCount = 0;
        foreach (var b in _blocked) if (b) BlockedCount++;
    }

    /// <summary>Nearest free cell to (r,c) via outward ring search (player/target may sit in a wall).</summary>
    public bool NearestFree(int r, int c, out int fr, out int fc)
    {
        if (InBounds(r, c) && !_blocked[r * Cols + c]) { fr = r; fc = c; return true; }
        for (int rad = 1; rad < Math.Max(Cols, Rows); rad++)
            for (int dr = -rad; dr <= rad; dr++)
                for (int dc = -rad; dc <= rad; dc++)
                {
                    if (Math.Abs(dr) != rad && Math.Abs(dc) != rad) continue;   // ring only
                    int nr = r + dr, nc = c + dc;
                    if (InBounds(nr, nc) && !_blocked[nr * Cols + nc]) { fr = nr; fc = nc; return true; }
                }
        fr = r; fc = c; return false;
    }

    /// <summary>True if the last <see cref="AStar"/> reached the actual target (vs a partial route).</summary>
    public bool LastReachedTarget { get; private set; }

    /// <summary>4-connected A* from (sr,sc) to (tr,tc). If the target is unreachable in this grid
    /// (e.g. its room's door reads as a wall), returns a PARTIAL path to the reachable cell CLOSEST
    /// to the target (the frontier — usually right at the doorway) so the walker still makes progress
    /// and we re-scan + replan from there. Null only if the player can't move at all.</summary>
    public List<(int r, int c)>? AStar(int sr, int sc, int tr, int tc)
    {
        LastReachedTarget = false;
        if (!InBounds(sr, sc) || !InBounds(tr, tc)) return null;
        int n = Rows * Cols, S = sr * Cols + sc, T = tr * Cols + tc;
        var g = new float[n]; var f = new float[n]; var prev = new int[n];
        var closed = new bool[n]; var inOpen = new bool[n];
        for (int i = 0; i < n; i++) { g[i] = float.MaxValue; prev[i] = -1; }
        g[S] = 0; f[S] = H(sr, sc, tr, tc);
        var open = new List<int> { S }; inOpen[S] = true;
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
        int bestCell = S; float bestH = H(sr, sc, tr, tc);
        int iter = 0;
        while (open.Count > 0 && iter++ < n * 4)
        {
            int bi = 0; for (int i = 1; i < open.Count; i++) if (f[open[i]] < f[open[bi]]) bi = i;
            int cur = open[bi]; open[bi] = open[^1]; open.RemoveAt(open.Count - 1); inOpen[cur] = false;
            int cr = cur / Cols, cc = cur % Cols;
            float curH = H(cr, cc, tr, tc);
            if (curH < bestH) { bestH = curH; bestCell = cur; }
            if (cur == T) { LastReachedTarget = true; return Recon(prev, S, T); }
            closed[cur] = true;
            for (int k = 0; k < 4; k++)
            {
                int nr = cr + dR[k], nc = cc + dC[k];
                if (!InBounds(nr, nc)) continue;
                int ni = nr * Cols + nc;
                if (closed[ni]) continue;
                if (_blocked[ni] && ni != T) continue;
                float ng = g[cur] + 1f;
                if (ng < g[ni]) { g[ni] = ng; f[ni] = ng + H(nr, nc, tr, tc); prev[ni] = cur; if (!inOpen[ni]) { open.Add(ni); inOpen[ni] = true; } }
            }
        }
        // Target unreachable in this grid → partial route to the closest frontier cell.
        return bestCell == S ? null : Recon(prev, S, bestCell);
    }

    private List<(int, int)> Recon(int[] prev, int S, int target)
    {
        var path = new List<(int, int)>(); int c = target;
        while (c != -1) { path.Add((c / Cols, c % Cols)); if (c == S) break; c = prev[c]; }
        path.Reverse(); return path;
    }

    private static float H(int r, int c, int tr, int tc) => Math.Abs(r - tr) + Math.Abs(c - tc);

    /// <summary>Collapse a cell path to corners via grid line-of-sight (string-pull).</summary>
    public List<(int r, int c)> Simplify(List<(int r, int c)> path)
    {
        if (path.Count <= 2) return path;
        var outp = new List<(int, int)> { path[0] };
        int anchor = 0;
        for (int i = 2; i < path.Count; i++)
            if (!LineClear(path[anchor], path[i])) { outp.Add(path[i - 1]); anchor = i - 1; }
        outp.Add(path[^1]);
        return outp;
    }

    private bool LineClear((int r, int c) a, (int r, int c) b)
    {
        int r0 = a.r, c0 = a.c, r1 = b.r, c1 = b.c;
        int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
        int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1, err = dc - dr, r = r0, c = c0;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (Blocked(r, c)) return false;
            if (r == r1 && c == c1) return true;
            int e2 = 2 * err;
            if (e2 > -dr) { err -= dr; c += sc; }
            if (e2 < dc) { err += dc; r += sr; }
        }
        return false;
    }

    /// <summary>Plan a world-space waypoint path from (px,pz) to (tx,tz). Null if no route.</summary>
    public List<(float x, float z)>? PlanWorld(float px, float pz, float tx, float tz)
    {
        WorldToCell(px, pz, out int pr, out int pc);
        WorldToCell(tx, tz, out int tr, out int tc);
        if (!NearestFree(pr, pc, out pr, out pc)) return null;
        if (!NearestFree(tr, tc, out tr, out tc)) return null;
        var cells = AStar(pr, pc, tr, tc);
        if (cells == null) return null;
        cells = Simplify(cells);
        var pts = new List<(float, float)>();
        foreach (var (r, c) in cells) { CellToWorld(r, c, out float x, out float z); pts.Add((x, z)); }
        return pts;
    }

    /// <summary>Crude ASCII map for verifying coverage. '.'=free '#'=wall 'P'=player 'T'=target.</summary>
    public string Ascii(int pr, int pc, int tr, int tc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"grid {Cols}x{Rows} cell={Cell} origin=({OriginX:F0},{OriginZ:F0}) blocked={BlockedCount}\n");
        // print top (high z) first
        for (int r = Rows - 1; r >= 0; r--)
        {
            for (int c = 0; c < Cols; c++)
            {
                if (r == pr && c == pc) sb.Append('P');
                else if (r == tr && c == tc) sb.Append('T');
                else sb.Append(_blocked[r * Cols + c] ? '#' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
