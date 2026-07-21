using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// THE PATHFINDER (v1.4.0 board item A): a fine per-floor routing grid that
/// sees the sub-cell walls the 24×16 minimap can't (the documented hard limit,
/// DUNGEON_AUTOWALK.md §4).
///
/// v2 — HYBRID, after the v1 live test (2026-07-06, Void Quest 43/0) proved two
/// assumptions wrong: the master wall-actor list is NOT full-floor there (a
/// ~80-triangle near-player bubble) and it carries almost no FLOOR triangles
/// (it is a wall list). So:
///
/// - WALKABLE BASE = the minimap: a fine cell is candidate-walkable iff its
///   coarse cell is explored-walkable (flag 1). The minimap transform is
///   per-axis affine (no rotation), so coarse cells are axis-aligned world
///   rects and the mapping is exact.
/// - BLOCKERS = the real wall triangles, ACCUMULATED per floor: every build
///   folds the current near-player mesh bubble into a per-floor store (the
///   MeshGrid.AccumulateScene idea), so coverage grows as the player moves.
///   The wall that just ground a walk is by definition NEXT TO the player, so
///   the stuck-recovery replan always knows it and A* bends around it.
///
/// A fine COMPLETE route therefore has minimap-level trust plus real geometry
/// near everywhere the player has been. Door cells are coarse flag-2 →
/// base-blocked → a target beyond a door yields complete=false and the caller
/// falls back to the proven minimap + door-threading path, unchanged.
/// </summary>
internal static class DungeonGrid
{
    private const float BaseCell = 50f;          // target fine cell size (world units)
    private const int MaxCells = 400_000;        // grid budget; cell grows to fit
    private const float WallMaxNormalY = 0.7f;   // |unit ny| above ⇒ floor/ramp, not a wall
    private const float WallStepClear = 40f;     // wall wholly below playerY+this = step-over
    private const float WallHeadClear = 240f;    // wall wholly above playerY+this = lintel/ceiling
    private const float BodyInflate = 60f;       // fatten walls by the ENGINE'S body radius (DAT_140965234 reads 55 — we assumed 100 for weeks and sealed ~90u of every corridor with our own ink, 2026-07-15)
    private const float EndpointSnapUnits = 420f;// max ring-search to pull an endpoint out of a wall
    private const int MaxStoredTris = 20_000;    // per-floor accumulation cap

    private sealed class Grid
    {
        public float OriginX, OriginZ, CellSize;
        public int Cols, Rows;
        public bool[] Base = System.Array.Empty<bool>();   // explored-walkable (minimap flag 1)
        public bool[] Wall = System.Array.Empty<bool>();   // inflated wall mask
        public bool[] Crumb = System.Array.Empty<bool>();  // player-walked ground — opens DERIVED walls only
        public bool[] Meas = System.Array.Empty<bool>();
        public byte[] Clear = System.Array.Empty<byte>();  // ★ BODY CLEARANCE (2026-07-15, the user's design: "even in a small
                                                           // few-cells opening this body can't enter"): chebyshev distance to
                                                           // the nearest wall, in cells. Routing requires the ENGINE'S OWN body
                                                           // radius (DAT_140965234) to fit — paper-narrow holes stop being
                                                           // routes; real squeezes route their centerline.   // MEASURED walls (scene mesh + grind stamps) — crumbs may NOT erase these
                                                           // (2026-07-14: live crumbs recorded WHILE grinding a thin railing kept
                                                           // erasing the freshly-learned wall every 250ms — the same wall was
                                                           // re-ground forever and every replan went back through the phantom hole)

        public bool InBounds(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Cols;
        public bool Walkable(int r, int c)
            => InBounds(r, c) && Base[r * Cols + c] && !Wall[r * Cols + c];
        public int NeedClear;   // cells of clearance the body needs (from the engine radius)
        public bool RouteOk(int r, int c)
            => Walkable(r, c) && Clear[r * Cols + c] >= NeedClear;
        public void WorldToCell(float x, float z, out int r, out int c)
        { c = (int)MathF.Floor((x - OriginX) / CellSize); r = (int)MathF.Floor((z - OriginZ) / CellSize); }
        public void CellToWorld(int r, int c, out float x, out float z)
        { x = OriginX + (c + 0.5f) * CellSize; z = OriginZ + (r + 0.5f) * CellSize; }
    }

    private static readonly object _lock = new();
    private static Grid? _grid;
    private static int _major = -1, _minor = -1;
    private static int _builtTris = -1, _builtExplored = -1;

    /// <summary>The current walk's TARGET is the stairs (set by AutoWalker for
    /// TravelToStairs only). STAIRS FOOTPRINT MASK (2026-07-14, the B6F
    /// stairs-as-free-floor failure): a stairs cell is walkable per the map but
    /// the staircase OBJECT fills it at body height — rays can't see it (sloped
    /// floor tris) and lingering there risks contact-triggering the floor
    /// transition. So stair-sprite cells are stamped WALL in every build,
    /// EXCEPT when the stairs are what we're walking to.</summary>
    internal static volatile bool StairsAreTarget;
    private static bool _builtStairsExempt;

    // Per-floor accumulated wall triangles (deduped by quantized centroid).
    private static readonly List<float[]> _tris = new();
    private static readonly HashSet<long> _triKeys = new();

    // ★ FLOOR triangles (2026-07-14, the Ghidra arc): the near-horizontal tris
    // the wall fold DISCARDS are the game's GROUND — and the void beyond a
    // catwalk edge is simply the ABSENCE of floor (the B7F pocket: every ray
    // open 1500u+, body pinned — nothing to hit, nothing to stand on). Keep
    // them; at build time their filled union becomes a floor mask and the
    // BOUNDARY cells (floor next to no-floor) become measured walls: the edge
    // of the world, painted from the game's own geometry.
    private static readonly List<float[]> _floorTris = new();
    private static readonly HashSet<long> _floorKeys = new();
    private const int MaxFloorTris = 20_000;

    private static int _builtWalls2 = -1;

    /// <summary>
    /// Plan a world waypoint route on the fine grid. Returns false when the
    /// grid can't be built or an endpoint isn't near it. <paramref name="complete"/>
    /// is true only when A* reached the target cell — a partial plan means
    /// something (usually a door) blocks the way, and the CALLER decides
    /// whether a partial leg is useful.
    /// </summary>
    internal static bool TryPlan(float px, float pz, float tx, float tz,
        bool includeStart, out List<(float x, float z)> pts, out bool complete)
    {
        pts = new List<(float x, float z)>();
        complete = false;
        Grid? g;
        lock (_lock) g = EnsureBuilt();
        if (g == null) return false;

        g.WorldToCell(px, pz, out int sr, out int sc);
        g.WorldToCell(tx, tz, out int tr, out int tc);
        int snapCells = (int)MathF.Ceiling(EndpointSnapUnits / g.CellSize);
        if (!NearestWalkable(g, ref sr, ref sc, snapCells)) return false;
        // TARGET snap reaches FARTHER (2026-07-14 night, sim-proven): a door/
        // mark target sits INSIDE the doorway's wall band on baked floors —
        // ~700u of wall each side — so the 420u snap failed and every walk to
        // an Events door mark degenerated into the door-threading ping-pong.
        // 1000u snaps it to the corridor in front; the travel loop's final
        // push covers the last stretch.
        if (!NearestWalkable(g, ref tr, ref tc, snapCells * 2 + 4)) return false;

        var cells = AStar(g, sr, sc, tr, tc, out complete);
        if (cells == null || cells.Count == 0) return false;
        // NO Simplify (2026-07-15, the movement rebuild): the raw cell path IS
        // the corridor line, already body-cleared. Straightening it into long
        // segments re-created beelines that cut corners into walls — the
        // sighted observer's "goes north, hits wall, feels along" was the
        // executor walking its own simplification, not the plan.
        for (int i = includeStart ? 0 : 1; i < cells.Count; i++)
        {
            g.CellToWorld(cells[i].r, cells[i].c, out float wx, out float wz);
            pts.Add((wx, wz));
        }
        // A route contained in the start cell still counts (endpoints adjacent):
        // hand back the target cell centre so the walker has one waypoint.
        if (pts.Count == 0)
        {
            g.CellToWorld(tr, tc, out float wx, out float wz);
            pts.Add((wx, wz));
        }
        return true;
    }

    /// <summary>
    /// A point ~<paramref name="legUnits"/> along a COMPLETE fine route to the
    /// target — the travel-leg primitive. False when no complete route exists
    /// (caller uses the minimap explore/door/frontier machinery instead).
    /// </summary>
    internal static bool TryStepAlong(float px, float pz, float tx, float tz,
        float legUnits, out float wx, out float wz)
    {
        wx = wz = 0;
        if (!TryPlan(px, pz, tx, tz, includeStart: false, out var pts, out bool complete)
            || pts.Count == 0)
            return false;
        if (!complete)
        {
            // PARTIAL fine routes are usable when they make REAL progress toward
            // the target (2026-07-14): the old "complete or nothing" rule pushed
            // travel onto the WALL-BLIND coarse leg, which drove a straight line
            // into a corner the fine grid already knew ([PocketDiag] patch: the
            // body pinned at the inside corner of two known walls). A partial
            // route bends around every known wall to the reachable cell nearest
            // the target — strictly better than blind. No real progress → false
            // (the caller escalates to doors / frontier machinery instead).
            var (ex, ez) = pts[^1];
            float d0 = MathF.Sqrt((tx - px) * (tx - px) + (tz - pz) * (tz - pz));
            float d1 = MathF.Sqrt((tx - ex) * (tx - ex) + (tz - ez) * (tz - ez));
            if (d1 > d0 - 150f) return false;
        }

        float cx = px, cz = pz, acc = 0;
        foreach (var (x, z) in pts)
        {
            float dx = x - cx, dz = z - cz;
            float seg = MathF.Sqrt(dx * dx + dz * dz);
            if (acc + seg >= legUnits && seg > 1f)
            {
                float t = (legUnits - acc) / seg;
                wx = cx + dx * t; wz = cz + dz * t;
                return true;
            }
            acc += seg; cx = x; cz = z;
        }
        wx = cx; wz = cz;    // route shorter than a leg — hand back its end
        return true;
    }

    /// <summary>
    /// Stamp a LEARNED wall just ahead of a confirmed grind: a short bar
    /// perpendicular to the blocked heading (walls are lines, not dots), both
    /// into the live grid and into the per-floor store so rebuilds keep it.
    /// Call only on a real blocked/stuck event — never while sliding.
    /// </summary>
    
    /// <summary>
    /// Stamp a SENSED wall point — the WallSense sight upgrade (2026-07-12): the
    /// game's inline scene collision (sub_obj+0x10e0, the thing that physically
    /// stops the body) reports walls by raycast, so the walker stamps them BEFORE
    /// contact. Verdict probe: 12/12 grinds had the wall visible at 14-35u even on
    /// floors where the master wall list streams ~nothing. Shares the learned
    /// store (same trust — it IS the collision), so fixed floors persist sight
    /// the same way they persist grinds.
    /// </summary>
    /// <summary>A stamp near a DOOR actor walls the doorway itself (the 65/2
    /// door-align fight sprayed ~25 stamps into an open door's gap, 2026-07-13):
    /// the door's own collision handles "closed", Invalidate handles "opened" —
    /// learned ink there is only ever wrong. 450u ≈ the door-frame footprint.</summary>
    
    
    
    /// <summary>Forget this floor's bump-stamps — called at every user-initiated
    /// auto-walk start (2026-07-13): stamps are working memory for ONE walk, not
    /// a biography. One bad walk used to poison every walk after it.</summary>
    
    // ── BREADCRUMBS — player-verified open ground (2026-07-13) ────────────────
    // "The proof of walkable is where the player has walked." Recorded every
    // ~250ms during dungeon play (manual AND auto), quantized to 100u, persisted
    // per FIXED floor (dual-write like the event marks), and force-opened
    // through every wall layer at grid build. Where map/tiles disagree with
    // reality (the B6F junction: the real passage sits where the tiles claim
    // wall), the player's own footsteps paint the truth once, forever.
    private static Dictionary<string, List<int[]>>? _crumbFile;   // floorKey → [qx,qz]
    private static string? _crumbFloor;
    private static HashSet<long>? _crumbSet;
    private static long _crumbDue, _crumbSaveDue;

    internal static void RecordBreadcrumb()
    {
        // HUMAN footsteps ONLY (2026-07-15): the walker grinding a barrier for
        // minutes dropped a crumb LINE along it — its own failure recorded as
        // "proven walkable", saved to disk, and every route then crossed the
        // barrier through the paper hole forever. Crumbs are the PLAYER's
        // ground truth; the machine may not testify for itself.
        if (AutoWalker.IsActive) return;
        long now = Environment.TickCount64;
        if (now < _crumbDue) return;
        _crumbDue = now + 250;
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        if (major < 20 || major >= 220) return;
        float x = FieldTracker.LivePlayerX, z = FieldTracker.LivePlayerZ;
        if (float.IsNaN(x) || float.IsNaN(z)) return;
        // Transition frames read garbage positions ((0,0), huge floats → int
        // overflow keys) — 5 junk crumbs landed in the first recorded session.
        if ((x == 0f && z == 0f) || MathF.Abs(x) > 1e6f || MathF.Abs(z) > 1e6f) return;
        long key = ((long)(int)MathF.Round(x / 100f) << 32) ^ (uint)(int)MathF.Round(z / 100f);
        lock (_lock)
        {
            EnsureCrumbsFor(major, minor);
            if (_crumbSet == null || !_crumbSet.Add(key)) return;
            var g = _grid;
            if (g != null) OpenCrumbCell(g, x, z);   // open the lane live, as it's walked
            if (IsFixedFloor(major, minor) && now >= _crumbSaveDue)
            { _crumbSaveDue = now + 5000; SaveCrumbs(major, minor); }
        }
    }

    // call under _lock
    private static void EnsureCrumbsFor(int major, int minor)
    {
        string fk = $"{major}_{minor}";
        if (_crumbFloor == fk && _crumbSet != null) return;
        _crumbFloor = fk;
        _crumbSet = new HashSet<long>();
        try
        {
            if (_crumbFile == null)
            {
                string path = Utils.DataPath("dungeon_player_paths.json");
                _crumbFile = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<int[]>>>(
                          System.IO.File.ReadAllText(path)) ?? new()
                    : new();
            }
            if (IsFixedFloor(major, minor) && _crumbFile.TryGetValue(fk, out var pts) && pts != null)
                foreach (var p in pts)
                    if (p is { Length: 2 })
                        _crumbSet.Add(((long)p[0] << 32) ^ (uint)p[1]);
        }
        catch (Exception e) { Log($"[FineGrid] crumb load failed: {e.Message}"); _crumbFile ??= new(); }
    }

    // call under _lock; throttled by the caller
    private static void SaveCrumbs(int major, int minor)
    {
        if (_crumbFile == null || _crumbSet == null) return;
        var list = new List<int[]>(_crumbSet.Count);
        foreach (long k in _crumbSet) list.Add(new[] { (int)(k >> 32), unchecked((int)k) });
        _crumbFile[$"{major}_{minor}"] = list;
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(_crumbFile);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Utils.ModDir))
                paths.Add(System.IO.Path.Combine(Utils.ModDir, "dungeon_player_paths.json"));
            var cwd = Environment.CurrentDirectory;
            foreach (var b in new[] { System.IO.Path.Combine(cwd, "Persona 4 golden", "database"),
                                      System.IO.Path.Combine(cwd, "database") })
                if (System.IO.Directory.Exists(b))
                { paths.Add(System.IO.Path.Combine(b, "dungeon_player_paths.json")); break; }
            foreach (var p in paths)
                try { System.IO.File.WriteAllText(p, json); } catch { }
        }
        catch { }
    }

    private static void OpenCrumbCell(Grid g, float wx, float wz)
    {
        // Crumbs erase DERIVED walls only (map paint, masks, tiles — the layers
        // that can lie). MEASURED walls (scene mesh, grind stamps) survive:
        // the ±1-cell opening around a 100u-quantized footstep is blunt enough
        // to punch holes through a thin real railing walked on both sides.
        g.WorldToCell(wx, wz, out int r, out int c);
        for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
            {
                int rr = r + dr, cc = c + dc;
                if (!g.InBounds(rr, cc)) continue;
                int i = rr * g.Cols + cc;
                g.Base[i] = true; g.Crumb[i] = true;
                if (!g.Meas[i]) g.Wall[i] = false;
            }
    }


    /// <summary>Is a straight world segment free of KNOWN walls? Used to VETO
    /// wall-blind fallback legs (coarse minimap / frontier) — driving a straight
    /// line through a wall we already know is never right. The first ~3 cells
    /// are exempt: the body legitimately stands inside a wall's inflation band
    /// after a grind. No grid = no knowledge = no veto.</summary>
    internal static bool SegmentClearOfKnownWalls(float ax, float az, float bx, float bz)
    {
        lock (_lock)
        {
            var g = _grid;
            if (g == null) return true;
            g.WorldToCell(ax, az, out int r0, out int c0);
            g.WorldToCell(bx, bz, out int r1, out int c1);
            int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
            int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1;
            int err = dc - dr, r = r0, c = c0, step = 0;
            for (int guard = 0; guard < 8192; guard++)
            {
                if (step > 3 && g.InBounds(r, c) && g.Wall[r * g.Cols + c]) return false;
                if (r == r1 && c == c1) return true;
                int e2 = 2 * err;
                if (e2 > -dr) { err -= dr; c += sc; }
                if (e2 < dc) { err += dc; r += sr; }
                step++;
            }
            return true;
        }
    }

    // ── [ManualDiag] — reality-vs-model probes while the PLAYER walks ────────
    // (2026-07-15, user request: "diagnose me walking myself"). Two directions:
    //  A) standing where the model says WALL  → the model has a FALSE wall;
    //  B) WallBump fires where the model says OPEN → the model MISSES a wall.
    private static long _mdDue; private static int _mdLastCell = -1;
    internal static void ManualDiag(float px, float pz)
    {
        long now = Environment.TickCount64;
        if (now < _mdDue) return;
        _mdDue = now + 500;
        Grid? g;
        lock (_lock) g = EnsureBuilt();   // also accumulates mesh/floor while walking
        if (g == null) return;
        g.WorldToCell(px, pz, out int r, out int c);
        if (!g.InBounds(r, c)) return;
        int i = r * g.Cols + c;
        if (g.Wall[i])
        {
            if (i == _mdLastCell) return;
            _mdLastCell = i;
            Log($"[ManualDiag] STANDING IN MODEL-WALL at ({px:F0},{pz:F0}) cell({r},{c}) " +
                $"meas={g.Meas[i]} base={g.Base[i]} clear={g.Clear[i]} — the model has a FALSE wall here");
        }
        else _mdLastCell = -1;
    }

    /// <summary>The model's opinion about a push from (px,pz) along (dx,dz):
    /// does it KNOW a wall within ~170u? For the WallBump probe.</summary>
    internal static string ModelOpinion(float px, float pz, float dx, float dz)
    {
        lock (_lock)
        {
            var g = _grid;
            if (g == null) return "no grid";
            float m = MathF.Sqrt(dx * dx + dz * dz);
            if (m < 1e-3f) return "no dir";
            dx /= m; dz /= m;
            for (int step = 1; step <= 4; step++)
            {
                g.WorldToCell(px + dx * step * 50f, pz + dz * step * 50f, out int r, out int c);
                if (!g.InBounds(r, c)) return "edge of grid";
                int i = r * g.Cols + c;
                if (g.Wall[i]) return $"wall KNOWN at {step * 50}u ({(g.Meas[i] ? "measured" : "derived")})";
            }
            return "model says OPEN — a wall is MISSING here";
        }
    }

    /// <summary>[PocketDiag] helper: ASCII map of the fine grid around (px,pz)
    /// — which layer claims each cell. P=player, #=measured wall, D=derived
    /// wall, c=crumb-opened, .=walkable, space=outside base.</summary>
    internal static void LogGridPatch(float px, float pz)
    {
        lock (_lock)
        {
            var g = _grid;
            if (g == null) { Log("[PocketDiag] no grid"); return; }
            g.WorldToCell(px, pz, out int pr, out int pc);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[PocketDiag] grid patch around cell ({pr},{pc}), cell={g.CellSize:F0}u, rows top→bottom = -Z→+Z:");
            for (int dr = -12; dr <= 12; dr++)
            {
                sb.Append("  ");
                for (int dc = -20; dc <= 20; dc++)
                {
                    int r = pr + dr, c = pc + dc;
                    char ch;
                    if (!g.InBounds(r, c)) ch = '?';
                    else
                    {
                        int i = r * g.Cols + c;
                        if (dr == 0 && dc == 0) ch = 'P';
                        else if (g.Wall[i]) ch = g.Meas[i] ? '#' : 'D';
                        else if (!g.Base[i]) ch = ' ';
                        else ch = g.Crumb[i] ? 'c' : '.';
                    }
                    sb.Append(ch);
                }
                sb.AppendLine();
            }
            Log(sb.ToString());
        }
    }

    /// <summary>True when the current floor's full layout came pre-baked from
    /// dungeon_floor_maps.json — the fine grid then knows every corridor, so
    /// wall-blind coarse legs and frontier exploration add nothing but grinding.</summary>
    internal static bool HasFloorMap { get { lock (_lock) return _mapWalk != null && _grid != null; } }

    // ── learned-wall PERSISTENCE for FIXED-layout floors (2026-07-12) ─────────
    // The 65/2 door-dance log: 226 walls learned in ONE walk on a scripted floor
    // whose minimap carries NO boundary flags (mapWalls=0) — then thrown away on
    // floor change, so every visit re-grinds them. Scripted/gate floors have
    // FIXED geometry (the dungeon_waypoints precedent: universal across players),
    // so their learned walls persist per floorKey, dual-written like SaveMarks
    // (ModDir = what the loader reads; database/ = the bundled dev source).
    // PROCEDURAL maze floors (majors 40-49, minor 0) regenerate per visit and
    // are NEVER persisted.
    /// <summary>Fixed-layout floor (gate/scripted — everything except the
    /// procedural mazes, majors 40-49 minor 0). Gates the wall persistence AND
    /// the room-graph skip (scripted minimaps are degenerate: no flag-2 walls,
    /// junk roomIds → 18-door phantom plans, live log 2026-07-12).</summary>
    internal static bool IsFixedFloor(int major, int minor)
        => major >= 20 && major < 70 && !(major >= 40 && major <= 49 && minor == 0);

    // ── PRE-BAKED FLOOR LAYOUTS (2026-07-12 — the field\map\*.MAP decode) ─────
    // dungeon_floor_maps.json = all 24 scripted floors' walkable cells extracted
    // offline from the game's own f0XX_YYY.MAP files (the runtime minimap's
    // pre-image; file[+4]==1 = walkable, proven by the 65/2 Ctrl+F8 correlation —
    // 86% byte-identical, 65/65 cells). On these floors the grid knows the WHOLE
    // layout from step zero: every walkable cell is Base, every other minimap
    // cell is WALL — no exploration, no grinding, no reveal needed.
    private static Dictionary<string, List<int[]>>? _floorMaps;
    private static HashSet<int>? _mapWalk;    // current floor: set of r*COLS+c
    private static Dictionary<int, byte>? _mapMask;  // r*COLS+c → connectivity mask (+14)
    private static HashSet<int>? _mapRoom;    // cells belonging to multi-cell ROOM records (w/h > 1)
    private static Dictionary<int, (byte w, byte h)>? _mapRoomWH;  // room cell → the room's full size
    private static Dictionary<int, (byte spr, byte mod)>? _mapTile;  // cell → (tile piece, rotation)
    private static string? _maskDiagFloor;    // [MaskDiag] one-shot guard

    // ── TILE COLLISION (2026-07-13 — the asset endgame) ──────────────────────
    // dungeon_tile_walls.json (⚠ ship in releases) = per-tileset piece wall
    // SEGMENTS (local space, rotation 0, Y-band filtered) decoded OFFLINE from
    // the game's own hit models (h0XX_0NN.AMD inside field\pack\f04X_*.arc).
    // Validated against 65/2 ground truth: 14/16 real grind stamps landed within
    // 100u of the composed walls (avg 54u). Rotation = the cell's MODIFIER byte
    // (90° CW steps, (x,z)→(z,−x)); tileset = the maze major (40-49) or the
    // scripted major − 20 (holds through the dungeon-5/6 swap: 64↔44, 65↔45).
    // Scripted floors stamp every layout cell; MAZE floors stamp LIVE from the
    // runtime minimap's own sprite/modifier as cells reveal — asset-grade
    // sub-cell walls (catwalk railings, scaffolds, room furniture) everywhere.
    private static Dictionary<string, Dictionary<string, List<int[]>>>? _tileWalls;

    private static void EnsureTileWalls()
    {
        if (_tileWalls != null) return;
        try
        {
            string path = Utils.DataPath("dungeon_tile_walls.json");
            _tileWalls = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<
                      Dictionary<string, Dictionary<string, List<int[]>>>>(
                      System.IO.File.ReadAllText(path)) ?? new()
                : new();
            Log($"[FineGrid] tile walls loaded: {_tileWalls.Count} tileset(s)");
        }
        catch (Exception e) { Log($"[FineGrid] tile-walls load failed: {e.Message}"); _tileWalls = new(); }
    }

    // Tile collision applies to PROCEDURAL MAZE floors ONLY (2026-07-13).
    // Scripted floors (60-69) borrow the maze tileset's ART but customize the
    // physical geometry — the player's recorded footsteps on 65/2 stood INSIDE
    // claimed tile walls 92/407 times under EVERY rotation/mirror convention,
    // and the junction's real passage (player-verified x 9400-9800) sat OUTSIDE
    // the tiles' claimed corridor (east wall 9517), so the phantom wall hid the
    // one true lane from A* while the real fence wasn't in the tiles at all.
    // False walls in a planner are worse than no walls: scripted floors run on
    // layout + edge masks + cane rays + per-walk stamps + breadcrumbs.
    private static int TilesetMajor(int major)
        => major >= 40 && major <= 49 ? major : -1;

    private static void LoadFloorMap(int major, int minor)
    {
        _mapWalk = null;
        _mapMask = null;
        _mapRoom = null;
        _mapRoomWH = null;
        _mapTile = null;
        try
        {
            if (_floorMaps == null)
            {
                string path = Utils.DataPath("dungeon_floor_maps.json");
                _floorMaps = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<int[]>>>(
                          System.IO.File.ReadAllText(path)) ?? new()
                    : new();
            }
            if (_floorMaps.TryGetValue($"{major}_{minor}", out var cells) && cells != null && cells.Count > 0)
            {
                _mapWalk = new HashSet<int>();
                _mapMask = new Dictionary<int, byte>();
                _mapRoom = new HashSet<int>();
                _mapRoomWH = new Dictionary<int, (byte, byte)>();
                _mapTile = new Dictionary<int, (byte, byte)>();
                foreach (var c in cells)
                    if (c is { Length: >= 2 })
                    {
                        int key = c[0] * MinimapTracker.COLS + c[1];
                        _mapWalk.Add(key);
                        if (c.Length >= 4) _mapMask[key] = (byte)c[3];
                        if (c.Length >= 6 && (c[4] > 1 || c[5] > 1))
                        { _mapRoom.Add(key); _mapRoomWH[key] = ((byte)c[4], (byte)c[5]); }
                        if (c.Length >= 7) _mapTile[key] = ((byte)c[2], (byte)c[6]);
                    }
                Log($"[FineGrid] floor map {major}_{minor}: {_mapWalk.Count} walkable cells pre-baked ({_mapRoom.Count} room cells)");
            }
        }
        catch (Exception e) { Log($"[FineGrid] floor-map load failed: {e.Message}"); _floorMaps ??= new(); }
    }

    /// <summary>
    /// Drop the accumulated MESH walls + grid after a door at (doorX,doorZ)
    /// OPENS — its collision is in the store, and without a reset the fine
    /// grid would keep routing the now-open doorway as a wall. LEARNED stamps
    /// near the door go too (walks ground on it while it was closed); learned
    /// walls elsewhere are real experiences and stay.
    /// </summary>
    internal static void Invalidate(float doorX = float.NaN, float doorZ = float.NaN)
    {
        lock (_lock)
        {
            _grid = null;
            _tris.Clear(); _triKeys.Clear();
            _floorTris.Clear(); _floorKeys.Clear();
            _builtTris = _builtExplored = _builtWalls2 = -1;
            if (float.IsNaN(doorX)) { _major = _minor = -1; }
        }
    }

    // ── build ────────────────────────────────────────────────────────────────

    private static long _areaEpochSeen = -1;   // FieldTracker.LastAreaChangeMs at last build

    private static Grid? EnsureBuilt()
    {
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        // Same-floor compare alone MISSES leave-and-return (65_2 → 45_0 → 65_2
        // with no build in between looked like "never left" and kept the whole
        // stale session state — every walk after re-entry failed instantly,
        // 2026-07-13). Any area transition = a fresh scene = a fresh grid.
        if (major != _major || minor != _minor || FieldTracker.LastAreaChangeMs != _areaEpochSeen)
        {
            _grid = null; _tris.Clear(); _triKeys.Clear();
            _floorTris.Clear(); _floorKeys.Clear();
            _builtTris = _builtExplored = _builtWalls2 = -1;
            _major = major; _minor = minor;
            _areaEpochSeen = FieldTracker.LastAreaChangeMs;
            LoadFloorMap(major, minor);     // scripted floors start with the WHOLE layout
        }

        float playerY = FieldTracker.LivePlayerY;
        if (float.IsNaN(playerY)) return _grid;

        // Fold the current mesh bubble into the per-floor store. Near-
        // horizontal tris (floors/ramps) are not walls; Y-band vs the player
        // keeps under-floor geometry and high lintels from sealing doorways
        // (dungeon floors are flat, so player Y is a valid floor reference).
        // ★ FILTER RULE (2026-07-14, two live tests deep): keep the SLOPE filter
        // (|ny| > 0.7 = floor/ramp — with no slope filter, tilted floor panels
        // rastered as walls ON the corridors and every plan failed instantly),
        // but DROP the Y height band — it discarded knee-high railing collision
        // (walls topping 8u above the feet, [MeshDiag] yLow) that the body can
        // NOT step over. P4G movement stops at any steep poly regardless of
        // height; the pathing model must match the physics model.
        void FoldTri(float[] t)
        {
            if (t.Length < 9 || _tris.Count >= MaxStoredTris) return;
            for (int v = 0; v < 9; v++) if (!float.IsFinite(t[v]) || MathF.Abs(t[v]) > 1e6f) return;
            float ux = t[3] - t[0], uy = t[4] - t[1], uz = t[5] - t[2];
            float vx = t[6] - t[0], vy = t[7] - t[1], vz = t[8] - t[2];
            float ny = uz * vx - ux * vz;
            float nx = uy * vz - uz * vy, nz = ux * vy - uy * vx;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!float.IsFinite(len) || len < 1e-3f) return;
            if (MathF.Abs(ny / len) > WallMaxNormalY)
            {
                // FLOOR tri — keep it for the floor mask (player-level only:
                // under-catwalk ground and upper walkways are other storeys).
                float fMaxY = MathF.Max(t[1], MathF.Max(t[4], t[7]));
                if (fMaxY <= playerY + 60f && fMaxY >= playerY - 500f
                    && _floorTris.Count < MaxFloorTris)
                {
                    float fcx = (t[0] + t[3] + t[6]) / 3f, fcz = (t[2] + t[5] + t[8]) / 3f;
                    long fqx = (long)MathF.Round(fcx / 10f) & 0xFFFFFF;
                    long fqz = (long)MathF.Round(fcz / 10f) & 0xFFFFFF;
                    long fkey = fqx | (fqz << 24);
                    if (_floorKeys.Add(fkey)) _floorTris.Add((float[])t.Clone());
                }
                return;
            }
            float minY = MathF.Min(t[1], MathF.Min(t[4], t[7]));
            float cx = (t[0] + t[3] + t[6]) / 3f, cz = (t[2] + t[5] + t[8]) / 3f;
            long qx = (long)MathF.Round(cx / 10f) & 0xFFFFFF;
            long qz = (long)MathF.Round(cz / 10f) & 0xFFFFFF;
            long qy = (long)MathF.Round(minY / 20f) & 0xFFF;
            long key = qx | (qz << 24) | (qy << 48);
            if (_triKeys.Add(key)) _tris.Add((float[])t.Clone());
        }
        // ★ MESH LAYER OFF (2026-07-15, option A of the stocktake): the actor
        // transform format is still not correctly understood — every convention
        // tried painted floor-length phantom walls across walkable corridors
        // (ManualDiag surveys: 243 → 190 → 108 false-wall hits through three
        // "fixes"). Until the transform is derived from DATA ([XformDump]),
        // the map = layout + boundary + doors + room pieces + footsteps ONLY.
        const bool MeshLayerEnabled = true;   // back ON: rotation data-confirmed + volume filter in place
        if (MeshLayerEnabled)
        {
        FieldTracker.VisitMasterWallTriangles(FoldTri);
        // ★ THE SIGHT FIX (2026-07-14). The master list streams ~nothing on some
        // floors (65/2: 48 junk tris) — but the INLINE SCENE CACHE (sub_obj+0x10e0,
        // the structure that PHYSICALLY STOPS the body and the cane's ray source)
        // always holds the real geometry of the rooms around the player, and its
        // rays confirmed every grinding wall at 17-31u in the 07-14 log while the
        // planner drew lines straight through them. Fold its full triangles into
        // the same per-floor store: real walls become known as rooms stream in,
        // BEFORE contact — A* bends instead of grinding down a corridor wall.
        FieldTracker.VisitWallTrianglesInScene(FoldTri);
        }
        // ([XformDump] capture call removed at the 2026-07-18 diagnostics strip —
        // the mesh-transform question is CLOSED: nav asks the game's collision.)

        // Count explored coarse cells — new exploration widens the walkable
        // base, so it forces a rebuild just like new wall coverage does. Flag-2
        // (wall/boundary) cells count too: a newly revealed wall must re-block.
        int explored = 0, walls2 = 0;
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
                if (MinimapTracker.ReadCell(r, c, out var mc))
                {
                    if (mc.Flag == 1) explored++;
                    else if (mc.Flag == 2) walls2++;
                }
        if (explored == 0 && _mapWalk == null) return null;   // no usable minimap (open hub) — caller falls back

        if (_grid != null && _tris.Count + _floorTris.Count == _builtTris && explored == _builtExplored
            && walls2 == _builtWalls2 && StairsAreTarget == _builtStairsExempt)
            return _grid;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Frame the grid on the coarse map's world AABB (per-axis affine
        // transform ⇒ coarse cells are axis-aligned world rects).
        if (!MinimapTracker.CellToWorld(0, 0, out float ax, out float az)) return _grid;
        if (!MinimapTracker.CellToWorld(MinimapTracker.ROWS - 1, MinimapTracker.COLS - 1,
                out float bx, out float bz)) return _grid;
        if (!MinimapTracker.CellToWorld(0, 1, out float cx1, out float cz1)) return _grid;
        if (!MinimapTracker.CellToWorld(1, 0, out float rx1, out float rz1)) return _grid;
        float pitchX = MathF.Abs(cx1 - ax), pitchZ = MathF.Abs(rz1 - az);
        if (pitchX < 1f || pitchZ < 1f) return _grid;

        float mnx = MathF.Min(ax, bx) - pitchX / 2, mxx = MathF.Max(ax, bx) + pitchX / 2;
        float mnz = MathF.Min(az, bz) - pitchZ / 2, mxz = MathF.Max(az, bz) + pitchZ / 2;

        float cell = BaseCell;
        int cols, rows;
        for (int guard = 0; ; guard++)
        {
            cols = (int)MathF.Ceiling((mxx - mnx) / cell) + 3;
            rows = (int)MathF.Ceiling((mxz - mnz) / cell) + 3;
            if ((long)cols * rows <= MaxCells || guard > 8) break;
            cell *= 1.25f;
        }
        if ((long)cols * rows > MaxCells || cols < 4 || rows < 4) return _grid;

        var g = new Grid
        {
            OriginX = mnx - cell, OriginZ = mnz - cell,
            CellSize = cell, Cols = cols, Rows = rows,
            Base = new bool[cols * rows],
            Wall = new bool[cols * rows],
            Crumb = new bool[cols * rows],
            Meas = new bool[cols * rows],
            Clear = new byte[cols * rows],
        };

        // Walkable base: fill each explored coarse cell's world rect — and on a
        // pre-baked floor, EVERY walkable cell from the .MAP layout (the walker
        // can route through unexplored space; the map is the game's own truth).
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
            {
                bool walk = _mapWalk != null && _mapWalk.Contains(r * MinimapTracker.COLS + c);
                if (!walk)
                {
                    if (!MinimapTracker.ReadCell(r, c, out var ci) || ci.Flag != 1) continue;
                    // ⚠ On a pre-baked floor an explored flag-1 cell OUTSIDE the map
                    // layout would punch a hole in the baked walls — trust the map.
                    if (_mapWalk != null) continue;
                }
                if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
                g.WorldToCell(wx - pitchX / 2, wz - pitchZ / 2, out int r0, out int c0);
                g.WorldToCell(wx + pitchX / 2, wz + pitchZ / 2, out int r1, out int c1);
                if (r1 < r0) (r0, r1) = (r1, r0);
                if (c1 < c0) (c0, c1) = (c1, c0);
                for (int fr = Math.Max(r0, 0); fr <= Math.Min(r1, g.Rows - 1); fr++)
                    for (int fc = Math.Max(c0, 0); fc <= Math.Min(c1, g.Cols - 1); fc++)
                        g.Base[fr * g.Cols + fc] = true;
            }

        // (The MESH raster moved to the END of the build, 2026-07-14 — it is
        //  MEASURED geometry and must outrank the crumb layer; see below.)

        // KNOWN WALLS FROM THE GAME'S OWN MAP (2026-07-12 — the "door dance" fix).
        // Minimap flag 2 = wall/boundary, and DOORS ARE FLAG-1 walkable cells (the
        // H-cursor has trusted both since 06-18) — so every flag-2 cell is a pure
        // wall. Travel's fine grid was LEARNING these same walls BY GRINDING into
        // them, which is why leaving a dead-end room took so long that players
        // cancelled the walk. Marked AFTER InflateWalls on purpose: the coarse
        // rects are already a full cell wide, and body-inflating them can pinch a
        // one-cell corridor shut.
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
            {
                // Pre-baked floor: every cell NOT in the .MAP's walkable set is a
                // WALL — the complete boundary, no exploration required. Otherwise:
                // the minimap's own flag-2 boundary cells (maze floors).
                bool wall;
                if (_mapWalk != null)
                    wall = !_mapWalk.Contains(r * MinimapTracker.COLS + c);
                else
                    wall = MinimapTracker.ReadCell(r, c, out var ci) && ci.Flag == 2;
                if (!wall) continue;
                if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
                g.WorldToCell(wx - pitchX / 2, wz - pitchZ / 2, out int r0, out int c0);
                g.WorldToCell(wx + pitchX / 2, wz + pitchZ / 2, out int r1, out int c1);
                if (r1 < r0) (r0, r1) = (r1, r0);
                if (c1 < c0) (c0, c1) = (c1, c0);
                for (int fr = Math.Max(r0, 0); fr <= Math.Min(r1, g.Rows - 1); fr++)
                    for (int fc = Math.Max(c0, 0); fc <= Math.Min(c1, g.Cols - 1); fc++)
                        g.Wall[fr * g.Cols + fc] = true;
            }

        // EDGE WALLS from the .MAP connectivity masks (2026-07-12, decoded at 99.2%
        // pairing consistency across all 24 floors: mask HIGH nibble = open-edge
        // bits — bit0 = row−1, bit1 = col−1, bit2 = row+1, bit3 = col+1). Two
        // ADJACENT WALKABLE cells with a closed edge = the parallel-catwalk
        // railings the cell-level layout can't express (the B6F stuck spots:
        // routes crossed between side-by-side corridors through the railing).
        // Closed if EITHER side says closed (the 0.8% mismatches = one-way/locked
        // edges — conservative). Drawn 3 fine-cells thick so drives keep clear.
        if (_mapWalk != null && _mapMask != null)
        {
            int[] edr = { -1, 0, 1, 0 };
            int[] edc = { 0, -1, 0, 1 };
            int[] ebit = { 0, 1, 2, 3 };
            int[] eopp = { 2, 3, 0, 1 };
            for (int r = 0; r < MinimapTracker.ROWS; r++)
                for (int c = 0; c < MinimapTracker.COLS; c++)
                {
                    int key = r * MinimapTracker.COLS + c;
                    if (!_mapWalk.Contains(key) || !_mapMask.TryGetValue(key, out byte mask)) continue;
                    if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
                    int hi = (mask >> 4) & 0xF;
                    for (int e = 0; e < 4; e++)
                    {
                        int nk = (r + edr[e]) * MinimapTracker.COLS + (c + edc[e]);
                        if (!_mapWalk.Contains(nk)) continue;   // neighbor is a full-cell wall already
                        // ROOM records (w/h > 1) use DIFFERENT mask semantics — reading them
                        // per-cell walled every room's INSIDE into a 3×3 jail and killed all
                        // routes near rooms (the "plans like walls don't exist" bug: no fine
                        // route → wall-blind fallback → straight-line grinding; 2026-07-12).
                        // Rule: room↔room = open; room↔corridor = only the CORRIDOR cell's
                        // bit speaks; corridor↔corridor = closed if either side says closed.
                        bool aRoom = _mapRoom != null && _mapRoom.Contains(key);
                        bool bRoom = _mapRoom != null && _mapRoom.Contains(nk);
                        bool open;
                        if (aRoom && bRoom) open = true;
                        else if (aRoom)
                            open = _mapMask.TryGetValue(nk, out byte nb) && (((nb >> 4) >> eopp[e]) & 1) != 0;
                        else if (bRoom)
                            open = ((hi >> ebit[e]) & 1) != 0;
                        else
                        {
                            open = ((hi >> ebit[e]) & 1) != 0;
                            if (open && _mapMask.TryGetValue(nk, out byte nm))
                                open = (((nm >> 4) >> eopp[e]) & 1) != 0;
                        }
                        if (open) continue;
                        float ex0 = wx - pitchX / 2, ez0 = wz - pitchZ / 2;
                        float ex1 = wx + pitchX / 2, ez1 = wz + pitchZ / 2;
                        for (int off = -1; off <= 1; off++)
                        {
                            float o = off * g.CellSize;
                            if (edr[e] == -1)      RasterWallEdge(g, ex0, ez0 + o, ex1, ez0 + o);
                            else if (edr[e] == 1)  RasterWallEdge(g, ex0, ez1 + o, ex1, ez1 + o);
                            else if (edc[e] == -1) RasterWallEdge(g, ex0 + o, ez0, ex0 + o, ez1);
                            else                   RasterWallEdge(g, ex1 + o, ez0, ex1 + o, ez1);
                        }
                    }
                }
        }

        // [MaskDiag] TEMP (2026-07-13): does the RUNTIME minimap carry different
        // connectivity than the .MAP pre-image? The B6F scaffold barrier is
        // invisible to the file map — if the LIVE grid closes that edge, runtime
        // masks become the authority and barriers stop needing touch-learning.
        // Runtime record = file record shifted −4: file[+14] mask → live[+10].
        if (_mapMask != null && _maskDiagFloor != $"{major}_{minor}")
        {
            _maskDiagFloor = $"{major}_{minor}";
            try
            {
                var raw = new byte[16];
                var diffs = new List<string>();
                int unpop = 0;
                foreach (var kv in _mapMask)
                {
                    int dr = kv.Key / MinimapTracker.COLS, dc = kv.Key % MinimapTracker.COLS;
                    if (!MinimapTracker.ReadCellRawBytes(dr, dc, raw)) continue;
                    if (raw[0] == 0) { unpop++; continue; }        // not populated live
                    if (raw[10] != kv.Value && diffs.Count < 12)
                        diffs.Add($"r{dr}c{dc} file={kv.Value:X2} live={raw[10]:X2}");
                }
                Log(diffs.Count == 0
                    ? $"[MaskDiag] runtime masks match the file map ({unpop} cells unpopulated)"
                    : $"[MaskDiag] {diffs.Count} runtime-mask diffs ({unpop} unpopulated): {string.Join(", ", diffs)}");
            }
            catch (Exception e) { Log($"[MaskDiag] failed: {e.Message}"); }
        }

        // TILE COLLISION stamps — the real sub-cell geometry (railings, scaffold
        // poles, room furniture) from the decoded hit models. Scripted floors:
        // every layout cell; maze floors: every EXPLORED cell live (the runtime
        // minimap carries each revealed cell's sprite + rotation).
        int tileSegs = 0;
        EnsureTileWalls();
        int tset = TilesetMajor(major);
        // ROOM pieces get their own tileset resolution: scripted floors' CORRIDOR
        // tiles are banned (proven wrong by the 92/407 crumb audit), but their
        // 3×3 ROOM pieces validated CLEAN against 586 crumbs on 65/2 (2026-07-14:
        // 23 edge-brushes within quantization error, zero deep violations, and
        // the scaffold pocket at (13022,17171) sits 30u from a claimed wall).
        // Scripted tileset = major − 20 (holds through the 5/6 swap 64↔44 65↔45).
        int roomTset = major >= 40 && major <= 49 ? major
                     : major >= 60 && major <= 69 ? major - 20 : -1;
        if (tset > 0 && _tileWalls != null && _tileWalls.TryGetValue(tset.ToString(), out var tw))
        {
            for (int r = 0; r < MinimapTracker.ROWS; r++)
                for (int c = 0; c < MinimapTracker.COLS; c++)
                {
                    byte spr, mod;
                    if (_mapTile != null)
                    {
                        int key = r * MinimapTracker.COLS + c;
                        // ROOM cells: their sprite is the whole room's 3600u collision shell —
                        // stamping it per member cell cages the room shut (proven on 65_2:
                        // 28/65 cells reachable stamped vs 65/65 skipped). Mask edge walls
                        // already fence the room perimeter.
                        if (_mapRoom != null && _mapRoom.Contains(key)) continue;
                        if (!_mapTile.TryGetValue(key, out var t)) continue;
                        (spr, mod) = t;
                    }
                    else
                    {
                        if (!MinimapTracker.ReadCell(r, c, out var ci) || ci.Flag != 1) continue;
                        if (ci.Width > 1 || ci.Height > 1) continue;   // same room rule, live minimap
                        spr = ci.Sprite; mod = ci.Modifier;
                    }
                    if (!tw.TryGetValue(spr.ToString(), out var segs) || segs == null) continue;
                    if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
                    foreach (var s in segs)
                    {
                        if (s is not { Length: 4 }) continue;
                        float sax = s[0], saz = s[1], sbx = s[2], sbz = s[3];
                        for (int k = 0; k < (mod & 3); k++)
                        { (sax, saz) = (saz, -sax); (sbx, sbz) = (sbz, -sbx); }   // 90° CW per modifier step
                        float dx = sbx - sax, dz = sbz - saz;
                        float len = MathF.Sqrt(dx * dx + dz * dz);
                        float ox = len > 1f ? dz / len * g.CellSize : 0f;
                        float oz = len > 1f ? -dx / len * g.CellSize : 0f;
                        for (int off = -1; off <= 1; off++)
                            RasterWallEdge(g,
                                wx + sax + ox * off, wz + saz + oz * off,
                                wx + sbx + ox * off, wz + sbz + oz * off);
                        tileSegs++;
                    }
                }
        }

        // ROOM-PIECE INTERIORS (2026-07-14 — the wedge-pocket fix): both open
        // auto-walk failures were 3×3 ROOM interiors (B6F scaffold, 45/0
        // railing) — furniture invisible to rays and to every map layer,
        // because room cells deliberately skip per-cell tile stamping (the
        // room-cage bug: 9 offset copies of a 3600u shell seal the room).
        // The right stamping is the room piece ONCE, centered on the room's
        // full w×h block — that paints the real interior walls A* has been
        // planning straight through.
        if (roomTset > 0 && _tileWalls != null
            && _tileWalls.TryGetValue(roomTset.ToString(), out var rtw))
            tileSegs += StampRoomPieces(g, rtw);


        // BREADCRUMBS — player-walked ground outranks every DERIVED layer
        // (map paint, tile geometry, edge masks). Where the player's feet have
        // been IS walkable — but only against layers that can LIE. Measured
        // geometry (the scene mesh, grind stamps) rasters AFTER this and wins.
        EnsureCrumbsFor(major, minor);
        int crumbs = 0;
        if (_crumbSet != null)
            foreach (long ck in _crumbSet)
            { OpenCrumbCell(g, (ck >> 32) * 100f, unchecked((int)ck) * 100f); crumbs++; }

        // ★ MEASURED GEOMETRY — rasters AFTER the crumb layer on purpose
        // (2026-07-14): these triangles are the game's own collision (the
        // structure that physically stops the body — scene cache + master
        // list), not a derived guess. Rastered into a scratch mask, body-
        // inflated, then merged into Wall AND Meas so neither build-time nor
        // LIVE breadcrumbs can erase them (a thin catwalk railing walked on
        // both sides was being erased every 250ms by the recorder mid-walk).
        int voidRing = 0;
        {
            var mesh = new bool[g.Cols * g.Rows];
            foreach (var t in _tris)
            {
                RasterEdgeInto(g, mesh, t[0], t[2], t[3], t[5]);
                RasterEdgeInto(g, mesh, t[3], t[5], t[6], t[8]);
                RasterEdgeInto(g, mesh, t[6], t[8], t[0], t[2]);
            }
            InflateMask(mesh, g.Rows, g.Cols, Math.Max(1, (int)MathF.Ceiling(BodyInflate / g.CellSize)));
            // THE BOUNDARY IS THE MAP'S (2026-07-15, the user's model: "the
            // minimap shows the boundary as a white line around the floor").
            // Collision may only add INTERIOR detail — cells inside the
            // walkable region. It never redraws the outline.
            for (int i = 0; i < mesh.Length; i++)
                if (mesh[i] && g.Base[i]) { g.Wall[i] = true; g.Meas[i] = true; }

            // FLOOR/VOID: fill the accumulated floor tris, then wall the EDGE
            // OF THE WORLD — any no-floor cell touching floor. Catwalk edges
            // and holes (ray-invisible, unmapped) become measured walls.
            // ⚠ NEAR THE PLAYER ONLY (v2, 2026-07-14 night): floor streams in
            // a small bubble, so "no floor" far from the body means UNSTREAMED,
            // not void — v1 ringed the streaming frontier, the planner couldn't
            // route past it, the walker never advanced, the floor never
            // streamed: a deadlock ("straight fail without moving", B6F).
            // Within ~1000u of the body the streaming is complete and absence
            // of floor is real void — exactly where the wedges happen.
            if (_floorTris.Count > 0)
            {
                float ppx2 = FieldTracker.LivePlayerX, ppz2 = FieldTracker.LivePlayerZ;
                bool havePP = !float.IsNaN(ppx2) && !float.IsNaN(ppz2);
                var floor = new bool[g.Cols * g.Rows];
                foreach (var t in _floorTris) FillTriangleInto(g, floor, t);
                // Crumbs count as floor: player-proven ground IS lane.
                for (int i = 0; i < floor.Length; i++) if (g.Crumb[i]) floor[i] = true;
                // FLOOR decides where ground ENDS, not where it is (2026-07-15,
                // the calibrated version of the user's floor design): the floor
                // stream is SPARSE (~4 pieces per look vs dozens of walls), so a
                // "walk only on confirmed floor" whitelist walled un-streamed
                // corridors inside the seen circle and planning died ("no way
                // there" in the first corridor). The supported strength: a
                // no-floor cell becomes wall only DIRECTLY BESIDE confirmed
                // floor — real edges (catwalk rims, holes), never absence of
                // data. Near the body only (streaming-certain 450u).
                for (int r = 0; r < g.Rows; r++)
                    for (int c = 0; c < g.Cols; c++)
                    {
                        if (!floor[r * g.Cols + c]) continue;
                        foreach (var (dr2, dc2) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                        {
                            int nr = r + dr2, nc = c + dc2;
                            if (!g.InBounds(nr, nc)) continue;
                            int ni = nr * g.Cols + nc;
                            if (floor[ni] || g.Wall[ni]) continue;
                            if (havePP)
                            {
                                g.CellToWorld(nr, nc, out float vwx, out float vwz);
                                if ((vwx - ppx2) * (vwx - ppx2) + (vwz - ppz2) * (vwz - ppz2) > 450f * 450f)
                                    continue;
                            }
                            g.Wall[ni] = true; g.Meas[ni] = true; voidRing++;
                        }
                    }

            }
        }


        // STAIRS FOOTPRINT MASK — after crumbs ON PURPOSE: the player's feet DO
        // touch stairs cells (that's how floors change), so the crumb layer
        // would reopen them; but a route through a staircase wedges the body on
        // the slope (rays can't see it) or rides the floor transition by
        // accident. The stairs are a real, known interactable footprint — wall
        // their cells out of every plan unless they ARE the target.
        int stairCells = 0;
        if (!StairsAreTarget)
            stairCells = MaskStairCells(g, pitchX, pitchZ);

        // ★ DOORS ARE PASSAGES, NOT WALLS (2026-07-15, the user's routing
        // model: "the door isn't a wall — it's a passage you can open with
        // pressing"). Every door actor gets a small zone carved OPEN through
        // every layer (mesh panels, map walls, edge masks, floor ring), so a
        // COMPLETE route exists on paper through each doorway and the walker
        // commits to whole ways — out this door, down that corridor, through
        // the next — instead of greedily hugging the wall nearest the target.
        // The body handles the physical opening on arrival (prompt-gated tap,
        // the proven threading). Applied LAST: nothing may re-seal a doorway.
        int doorCarve = 0;
        try
        {
            foreach (var (ddx, ddz) in DungeonNav.DoorsAll())
            {
                g.WorldToCell(ddx - 170f, ddz - 170f, out int dr0, out int dc0);
                g.WorldToCell(ddx + 170f, ddz + 170f, out int dr1, out int dc1);
                if (dr1 < dr0) (dr0, dr1) = (dr1, dr0);
                if (dc1 < dc0) (dc0, dc1) = (dc1, dc0);
                for (int r = Math.Max(dr0, 0); r <= Math.Min(dr1, g.Rows - 1); r++)
                    for (int c = Math.Max(dc0, 0); c <= Math.Min(dc1, g.Cols - 1); c++)
                    {
                        g.CellToWorld(r, c, out float cwx2, out float cwz2);
                        if ((cwx2 - ddx) * (cwx2 - ddx) + (cwz2 - ddz) * (cwz2 - ddz) > 170f * 170f) continue;
                        int i = r * g.Cols + c;
                        g.Base[i] = true; g.Wall[i] = false; g.Meas[i] = false;
                    }
                doorCarve++;
            }
        }
        catch { }

        // Clearance field + the body's requirement (engine radius, cell units).
        ComputeClearance(g);
        float bodyR = FieldTracker.BodyCollisionRadius();
        g.NeedClear = Math.Clamp((int)MathF.Round(bodyR / g.CellSize * 0.8f), 1, 4);

        _grid = g;
        _builtTris = _tris.Count + _floorTris.Count;
        _builtExplored = explored;
        _builtWalls2 = walls2;
        _builtStairsExempt = StairsAreTarget;
        Log($"[FineGrid] built {major}/{minor}: {g.Cols}x{g.Rows} cell={g.CellSize:F0} " +
            $"explored={explored} baked={(_mapWalk?.Count ?? 0)} tileSegs={tileSegs} mapWalls={walls2} wallTris={_tris.Count} floorTris={_floorTris.Count} floorEdge={voidRing} crumbs={crumbs} doors={doorCarve} body={bodyR:F0}/{g.NeedClear}" +
            (stairCells > 0 ? $" stairMask={stairCells}" : StairsAreTarget ? " stairMask=off(target)" : "") +
            $" {sw.ElapsedMilliseconds}ms");
        return g;
    }

    /// <summary>Stamp every known stairs cell's world rect as WALL. Sources:
    /// the baked .MAP tile sprites (scripted floors — stairs known even
    /// unexplored) and the live minimap (maze floors, as cells reveal). The
    /// stair-sprite set is GridRouter.IsStairSprite — the same one the Exits
    /// browser and beacons trust. Returns the number of coarse cells masked.</summary>
    private static int MaskStairCells(Grid g, float pitchX, float pitchZ)
    {
        int masked = 0;
        // The player may be STANDING in a stairs cell (they just arrived down
        // them) — walling that cell would strand the route start (the 420u
        // endpoint snap can't escape a 1200u walled cell). Leave it open.
        int pRow = -1, pCol = -1;
        float ppx = FieldTracker.LivePlayerX, ppz = FieldTracker.LivePlayerZ;
        if (!float.IsNaN(ppx) && !float.IsNaN(ppz))
            MinimapTracker.WorldToCell(ppx, ppz, out pRow, out pCol);
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
            {
                if (r == pRow && c == pCol) continue;
                bool stairs = false;
                if (_mapTile != null && _mapTile.TryGetValue(r * MinimapTracker.COLS + c, out var t))
                    stairs = GridRouter.IsStairSprite(t.spr);
                if (!stairs && MinimapTracker.ReadCell(r, c, out var ci)
                    && ci.Flag == 1 && GridRouter.IsStairSprite(ci.Sprite))
                    stairs = true;
                if (!stairs) continue;
                if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
                g.WorldToCell(wx - pitchX / 2, wz - pitchZ / 2, out int r0, out int c0);
                g.WorldToCell(wx + pitchX / 2, wz + pitchZ / 2, out int r1, out int c1);
                if (r1 < r0) (r0, r1) = (r1, r0);
                if (c1 < c0) (c0, c1) = (c1, c0);
                for (int fr = Math.Max(r0, 0); fr <= Math.Min(r1, g.Rows - 1); fr++)
                    for (int fc = Math.Max(c0, 0); fc <= Math.Min(c1, g.Cols - 1); fc++)
                        g.Wall[fr * g.Cols + fc] = true;   // outranks crumbs — deliberate
                masked++;
            }
        return masked;
    }

    /// <summary>Stamp each ROOM's tile piece ONCE at the room's w×h block
    /// CENTER (never per member cell — that's the room-cage bug). Rooms come
    /// from the baked .MAP (scripted floors: every member carries the room's
    /// size; partition into rectangles anchored at the topmost-leftmost
    /// unclaimed member) or the live minimap (maze floors: group revealed
    /// cells by roomId; stamp only rooms revealed WHOLE — a partial reveal
    /// can't locate the anchor). Rotation/thickness conventions match the
    /// corridor-tile stamper. Returns segments stamped.</summary>
    private static int StampRoomPieces(Grid g, Dictionary<string, List<int[]>> tw)
    {
        var rooms = new List<(int r0, int c0, int w, int h, byte spr, byte mod)>();
        if (_mapTile != null && _mapRoomWH != null)
        {
            var unclaimed = new SortedSet<int>(_mapRoomWH.Keys);   // row-major order
            while (unclaimed.Count > 0)
            {
                int key = unclaimed.Min;
                var (w, h) = _mapRoomWH[key];
                int r0 = key / MinimapTracker.COLS, c0 = key % MinimapTracker.COLS;
                bool whole = w >= 1 && h >= 1;
                for (int dr = 0; dr < h && whole; dr++)
                    for (int dc = 0; dc < w && whole; dc++)
                        if (!unclaimed.Contains((r0 + dr) * MinimapTracker.COLS + c0 + dc)) whole = false;
                if (!whole) { unclaimed.Remove(key); continue; }   // odd shape — skip, never guess
                for (int dr = 0; dr < h; dr++)
                    for (int dc = 0; dc < w; dc++)
                        unclaimed.Remove((r0 + dr) * MinimapTracker.COLS + c0 + dc);
                if (_mapTile.TryGetValue(key, out var t))
                    rooms.Add((r0, c0, w, h, t.spr, t.mod));
            }
        }
        else
        {
            // Live minimap: bbox + count per roomId over revealed room cells.
            var acc = new Dictionary<ushort, (int r0, int c0, int r1, int c1, int n, byte spr, byte mod, byte w, byte h)>();
            for (int r = 0; r < MinimapTracker.ROWS; r++)
                for (int c = 0; c < MinimapTracker.COLS; c++)
                {
                    if (!MinimapTracker.ReadCell(r, c, out var ci)) continue;
                    if (ci.Flag != 1 || (ci.Width <= 1 && ci.Height <= 1)) continue;
                    if (acc.TryGetValue(ci.RoomId, out var a))
                        acc[ci.RoomId] = (Math.Min(a.r0, r), Math.Min(a.c0, c),
                                          Math.Max(a.r1, r), Math.Max(a.c1, c),
                                          a.n + 1, a.spr, a.mod, a.w, a.h);
                    else
                        acc[ci.RoomId] = (r, c, r, c, 1, ci.Sprite, ci.Modifier, ci.Width, ci.Height);
                }
            foreach (var a in acc.Values)
                if (a.n == a.w * a.h && a.r1 - a.r0 + 1 == a.h && a.c1 - a.c0 + 1 == a.w)
                    rooms.Add((a.r0, a.c0, a.w, a.h, a.spr, a.mod));
        }

        int segs = 0;
        foreach (var (r0, c0, w, h, spr, mod) in rooms)
        {
            if (!tw.TryGetValue(spr.ToString(), out var piece) || piece == null) continue;
            if (!MinimapTracker.CellToWorld(r0, c0, out float ax2, out float az2)) continue;
            if (!MinimapTracker.CellToWorld(r0 + h - 1, c0 + w - 1, out float bx2, out float bz2)) continue;
            float cx = (ax2 + bx2) / 2f, cz = (az2 + bz2) / 2f;
            foreach (var s in piece)
            {
                if (s is not { Length: 4 }) continue;
                float sax = s[0], saz = s[1], sbx = s[2], sbz = s[3];
                for (int k = 0; k < (mod & 3); k++)
                { (sax, saz) = (saz, -sax); (sbx, sbz) = (sbz, -sbx); }   // 90° CW per modifier step
                float dx = sbx - sax, dz = sbz - saz;
                float len = MathF.Sqrt(dx * dx + dz * dz);
                float ox = len > 1f ? dz / len * g.CellSize : 0f;
                float oz = len > 1f ? -dx / len * g.CellSize : 0f;
                for (int off = -1; off <= 1; off++)
                    RasterWallEdge(g,
                        cx + sax + ox * off, cz + saz + oz * off,
                        cx + sbx + ox * off, cz + sbz + oz * off);
                segs++;
            }
        }
        if (rooms.Count > 0 && _roomStampFloor != $"{_major}_{_minor}")
        {
            _roomStampFloor = $"{_major}_{_minor}";
            Log($"[FineGrid] room pieces: {rooms.Count} room(s) stamped at block centers ({segs} segs)");
        }
        return segs;
    }
    private static string? _roomStampFloor;   // one-shot log guard

    private static void RasterWallEdge(Grid g, float ax, float az, float bx, float bz)
        => RasterEdgeInto(g, g.Wall, ax, az, bx, bz);

    /// <summary>Fill a triangle's XZ interior into a cell mask (cell centers
    /// point-in-triangle; bbox-bounded). Used for the floor mask.</summary>
    private static void FillTriangleInto(Grid g, bool[] mask, float[] t)
    {
        float ax = t[0], az = t[2], bx = t[3], bz = t[5], cx = t[6], cz = t[8];
        float mnx = MathF.Min(ax, MathF.Min(bx, cx)), mxx = MathF.Max(ax, MathF.Max(bx, cx));
        float mnz = MathF.Min(az, MathF.Min(bz, cz)), mxz = MathF.Max(az, MathF.Max(bz, cz));
        g.WorldToCell(mnx, mnz, out int r0, out int c0);
        g.WorldToCell(mxx, mxz, out int r1, out int c1);
        if (r1 < r0) (r0, r1) = (r1, r0);
        if (c1 < c0) (c0, c1) = (c1, c0);
        r0 = Math.Max(r0, 0); c0 = Math.Max(c0, 0);
        r1 = Math.Min(r1, g.Rows - 1); c1 = Math.Min(c1, g.Cols - 1);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                g.CellToWorld(r, c, out float px, out float pz);
                float d1 = (bx - ax) * (pz - az) - (bz - az) * (px - ax);
                float d2 = (cx - bx) * (pz - bz) - (cz - bz) * (px - bx);
                float d3 = (ax - cx) * (pz - cz) - (az - cz) * (px - cx);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                if (!(neg && pos)) mask[r * g.Cols + c] = true;
            }
    }

    private static void RasterEdgeInto(Grid g, bool[] target, float ax, float az, float bx, float bz)
    {
        g.WorldToCell(ax, az, out int r0, out int c0);
        g.WorldToCell(bx, bz, out int r1, out int c1);
        int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
        int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1;
        int err = dc - dr, r = r0, c = c0;
        for (int guard = 0; guard < 8192; guard++)
        {
            if (g.InBounds(r, c)) target[r * g.Cols + c] = true;
            if (r == r1 && c == c1) break;
            int e2 = 2 * err;
            if (e2 > -dr) { err -= dr; c += sc; }
            if (e2 < dc) { err += dc; r += sr; }
        }
    }

    /// <summary>Two-pass chebyshev distance transform: Clear[i] = cell steps
    /// to the nearest wall (capped 30). Feeds body-aware routing.</summary>
    private static void ComputeClearance(Grid g)
    {
        var cl = g.Clear; int cols = g.Cols, rows = g.Rows;
        for (int i = 0; i < cl.Length; i++) cl[i] = g.Wall[i] ? (byte)0 : (byte)30;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c; int best = cl[i];
                if (r > 0)
                {
                    best = Math.Min(best, cl[i - cols] + 1);
                    if (c > 0) best = Math.Min(best, cl[i - cols - 1] + 1);
                    if (c < cols - 1) best = Math.Min(best, cl[i - cols + 1] + 1);
                }
                if (c > 0) best = Math.Min(best, cl[i - 1] + 1);
                cl[i] = (byte)best;
            }
        for (int r = rows - 1; r >= 0; r--)
            for (int c = cols - 1; c >= 0; c--)
            {
                int i = r * cols + c; int best = cl[i];
                if (r < rows - 1)
                {
                    best = Math.Min(best, cl[i + cols] + 1);
                    if (c < cols - 1) best = Math.Min(best, cl[i + cols + 1] + 1);
                    if (c > 0) best = Math.Min(best, cl[i + cols - 1] + 1);
                }
                if (c < cols - 1) best = Math.Min(best, cl[i + 1] + 1);
                cl[i] = (byte)best;
            }
    }

    private static void InflateMask(bool[] mask, int rows, int cols, int rad)
    {
        var src = (bool[])mask.Clone();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                if (!src[r * cols + c]) continue;
                for (int dr = -rad; dr <= rad; dr++)
                    for (int dc = -rad; dc <= rad; dc++)
                    {
                        int rr = r + dr, cc = c + dc;
                        if (rr >= 0 && rr < rows && cc >= 0 && cc < cols) mask[rr * cols + cc] = true;
                    }
            }
    }

    // ── routing ──────────────────────────────────────────────────────────────

    private static bool NearestWalkable(Grid g, ref int r, ref int c, int maxRad)
    {
        // Prefer a cell the BODY FITS in; fall back to any walkable cell so a
        // tight start (hugging a wall) can still anchor a plan.
        if (g.RouteOk(r, c)) return true;
        for (int rad = 1; rad <= maxRad; rad++)
            for (int dr = -rad; dr <= rad; dr++)
                for (int dc = -rad; dc <= rad; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != rad) continue;
                    if (g.RouteOk(r + dr, c + dc)) { r += dr; c += dc; return true; }
                }
        if (g.Walkable(r, c)) return true;
        for (int rad = 1; rad <= maxRad; rad++)
            for (int dr = -rad; dr <= rad; dr++)
                for (int dc = -rad; dc <= rad; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != rad) continue;
                    if (g.Walkable(r + dr, c + dc)) { r += dr; c += dc; return true; }
                }
        return false;
    }

    /// <summary>4-connected A*. Complete when the target cell is reached; else a
    /// PARTIAL path to the reachable cell nearest the target (the frontier —
    /// usually right at a door), so callers can still judge progress.</summary>
    private static List<(int r, int c)>? AStar(Grid g, int sr, int sc, int tr, int tc,
        out bool complete)
    {
        complete = false;
        int n = g.Rows * g.Cols, S = sr * g.Cols + sc, T = tr * g.Cols + tc;
        var gScore = new float[n];
        var prev = new int[n];
        var closed = new bool[n];
        System.Array.Fill(gScore, float.MaxValue);
        System.Array.Fill(prev, -1);

        var open = new PriorityQueue<int, float>();
        gScore[S] = 0;
        open.Enqueue(S, H(g, S, tr, tc));
        int bestCell = S; float bestH = H(g, S, tr, tc);
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
        int iter = 0, cap = n * 4;

        while (open.TryDequeue(out int cur, out _) && iter++ < cap)
        {
            if (closed[cur]) continue;
            closed[cur] = true;
            int cr = cur / g.Cols, cc = cur % g.Cols;
            float h = H(g, cur, tr, tc);
            if (h < bestH) { bestH = h; bestCell = cur; }
            if (cur == T) { complete = true; return Recon(g, prev, S, T); }

            for (int k = 0; k < 4; k++)
            {
                int nr = cr + dR[k], nc = cc + dC[k];
                // The start cell is exempt (we're standing there); every other
                // cell must be walkable AND wide enough for the body.
                if (!g.RouteOk(nr, nc)) continue;
                int ni = nr * g.Cols + nc;
                if (closed[ni]) continue;
                float ng = gScore[cur] + 1f;
                if (ng >= gScore[ni]) continue;
                gScore[ni] = ng; prev[ni] = cur;
                open.Enqueue(ni, ng + H(g, ni, tr, tc));
            }
        }
        return bestCell == S ? null : Recon(g, prev, S, bestCell);
    }

    private static float H(Grid g, int i, int tr, int tc)
        => MathF.Abs(i / g.Cols - tr) + MathF.Abs(i % g.Cols - tc);

    private static List<(int r, int c)> Recon(Grid g, int[] prev, int S, int target)
    {
        var path = new List<(int, int)>();
        int c = target;
        while (c != -1) { path.Add((c / g.Cols, c % g.Cols)); if (c == S) break; c = prev[c]; }
        path.Reverse();
        return path;
    }

    // String-pull HORIZON in cells (~1200u = one coarse cell). Walls inside
    // explored cells are UNKNOWN until ground on, so "line of sight" over long
    // spans is meaningless — an unbounded Simplify collapsed a corridor-
    // following A* path into ONE 11,000u straight line through unknown space
    // (the Teddie walk, 2026-07-06). Local shortcuts only; the cell path's
    // corridor shape survives at range.
    private const int SimplifyHorizonCells = 24;

    /// <summary>Collapse the cell path to corners via grid line-of-sight,
    /// bounded to <see cref="SimplifyHorizonCells"/> per shortcut.</summary>
    private static List<(int r, int c)> Simplify(Grid g, List<(int r, int c)> path)
    {
        if (path.Count <= 2) return path;
        var outp = new List<(int, int)> { path[0] };
        int anchor = 0;
        for (int i = 2; i < path.Count; i++)
        {
            bool tooFar = Math.Max(Math.Abs(path[i].r - path[anchor].r),
                                   Math.Abs(path[i].c - path[anchor].c)) > SimplifyHorizonCells;
            if (tooFar || !LineClear(g, path[anchor], path[i])) { outp.Add(path[i - 1]); anchor = i - 1; }
        }
        outp.Add(path[^1]);
        return outp;
    }

    private static bool LineClear(Grid g, (int r, int c) a, (int r, int c) b)
    {
        int r0 = a.r, c0 = a.c, r1 = b.r, c1 = b.c;
        int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
        int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1, err = dc - dr, r = r0, c = c0;
        for (int guard = 0; guard < 8192; guard++)
        {
            if (!g.RouteOk(r, c)) return false;
            if (r == r1 && c == c1) return true;
            int e2 = 2 * err;
            if (e2 > -dr) { err -= dr; c += sc; }
            if (e2 < dc) { err += dc; r += sr; }
        }
        return false;
    }
}
