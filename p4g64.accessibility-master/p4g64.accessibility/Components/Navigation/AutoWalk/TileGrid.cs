using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// ★ PREFAB COLLISION PLANNER (2026-09-30, nav pt2 item 1). A maze floor is built from room PREFABS:
/// tileset T (the maze major) piece N = the hit model <c>h0TT_0NN.AMD</c> in <c>field/pack/f0TT_00x.arc</c>,
/// N == the minimap cell's SPRITE byte (+0x04). A 1×1 piece spans ±600 around its cell centre, a w×h room
/// piece ±600·w around its BLOCK centre; the cell byte +0x05 = quarter turns, (x,z)→(z,−x) per turn.
/// <c>dungeon_prefab_walls.json</c> (⚠ bundle in releases; built by <c>database/tools/tilegrid.py export</c>)
/// holds each piece's WALL segments (steep triangles standing on the floor, ≥40u tall — the town rule).
/// Offline proof (Castle 1F, 2026-09-30): with the decoder's Z negation, every 1×1 piece's openings match
/// the game's own minimap edge bits (36/36; without it 8 mismatches) and 146 walked positions stay clear of
/// the composed walls.
///
/// The 1200u minimap says a 3×3 room is ONE open space; the prefab says it is a corridor plus a closed
/// box room joined by a door passage (Castle 1F: the coarse planner walked straight at the box wall →
/// five reroutes → "Couldn't get through"). So <see cref="StairsPlan.TryPlanTo"/> asks this planner FIRST:
/// the composed walls are rastered at 50u over the explored (flag-1) cells, a clearance field says how far
/// each fine cell is from any wall, and a clearance-weighted A* walks the MIDDLE of real corridors. Doors
/// on the path are woven (a centred point in front of and beyond the gap) so the drive's door ladder works
/// unchanged. Anything unusual → no plan → the proven coarse planner runs as before.
///
/// SELF-CHECK per floor: every 1×1 cell's composed openings must agree with the minimap edge bits; a
/// floor whose pieces disagree (a scripted floor with custom geometry, an unmapped tileset) is refused.
/// </summary>
internal static class TileGrid
{
    private const float Fine = 50f;                 // fine cell size (world units)
    private const int Sub = 24;                     // fine cells per 1200u minimap cell
    private const int FCols = MinimapTracker.COLS * Sub, FRows = MinimapTracker.ROWS * Sub;
    private const float BodyClear = 60f;            // a fine cell is passable when its wall clearance ≥ this (engine body 55)
    private const float ClearCap = 320f;            // clearance is only computed this far out (costs saturate earlier)
    private const float DoorCarveUnits = 100f;      // prefab wall segments this close to a door = its closed panel
    private const float PreferClear = 260f;         // below this the step cost rises (walk the middle)
    private const float PullClearCap = 200f;        // string-pull may not go tighter than min(this, A* path)
    private const float MaxLeg = 2400f;             // longest straight leg handed to the drive
    private const float DoorWeaveUnits = 260f;      // a door this close to the path = a crossing
    private const float DoorStandoff = 220f;        // crossing points in front of / beyond the gap
    private const float ChestRadius = 130f;         // a chest is a solid box on the floor
    private const float StampRadius = 160f;         // a per-walk learned obstacle (drive stall)
    private const float StairsPlugRadius = 250f;    // the staircase opening beyond the stairs prompt
    private const float StairsPlugPush = 100f;      // plug centre = prompt + this, away from the stairs room

    internal static bool Enabled = true;

    /// <summary>Per walk thread: the last TryPlanTo answer came from this planner.</summary>
    [ThreadStatic] internal static bool LastPlanFine;
    /// <summary>Per walk thread: the last plan found NO way to the target while a LOCKED door stood in
    /// the way — the same target IS reachable with the locked doors treated as open (Castle 5F before
    /// the key, Bath #3 when the long loop is unexplored). The caller says so instead of walking into
    /// the locked door.</summary>
    [ThreadStatic] internal static bool LastLockedBlock;
    /// <summary>Per walk thread: obstacles the drive bumped into this walk (not in the prefab data —
    /// a party member, the Fox, an odd prop). Cleared at every walk start.</summary>
    [ThreadStatic] private static List<(float x, float z)>? _stamps;

    internal static void ResetWalk() { _stamps?.Clear(); LastPlanFine = false; LastLockedBlock = false; }
    internal static void AddStamp(float x, float z)
    {
        _stamps ??= new List<(float, float)>();
        _stamps.Add((x, z));
        Log($"[TileGrid] obstacle stamped at ({x:F0},{z:F0}) — {_stamps.Count} this walk");
    }

    // ── data ─────────────────────────────────────────────────────────────────
    private sealed class Piece { public float Ext; public float[][] Segs = System.Array.Empty<float[]>(); }
    private static Dictionary<int, Dictionary<int, Piece>>? _data;
    private static readonly object _dataLock = new();

    private static void EnsureData()
    {
        if (_data != null) return;
        lock (_dataLock)
        {
            if (_data != null) return;
            var d = new Dictionary<int, Dictionary<int, Piece>>();
            try
            {
                string path = DataPath("dungeon_prefab_walls.json");
                if (System.IO.File.Exists(path))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                    foreach (var ts in doc.RootElement.EnumerateObject())
                    {
                        var pieces = new Dictionary<int, Piece>();
                        foreach (var p in ts.Value.EnumerateObject())
                        {
                            var segs = new List<float[]>();
                            foreach (var s in p.Value.GetProperty("segs").EnumerateArray())
                            {
                                var a = new float[4]; int i = 0;
                                foreach (var v in s.EnumerateArray()) { if (i < 4) a[i++] = v.GetSingle(); }
                                if (i == 4) segs.Add(a);
                            }
                            pieces[int.Parse(p.Name)] = new Piece { Ext = p.Value.GetProperty("ext").GetSingle(), Segs = segs.ToArray() };
                        }
                        d[int.Parse(ts.Name)] = pieces;
                    }
                }
                Log($"[TileGrid] prefab walls loaded: {d.Count} tileset(s) from {path}");
            }
            catch (Exception e) { Log($"[TileGrid] prefab walls load failed: {e.Message}"); }
            _data = d;
        }
    }

    /// <summary>Maze majors 40-49 are their own tileset; scripted floors 60-69 borrow major−20 (the 5/6
    /// swap holds: 64↔44, 65↔45). The per-floor self-check decides whether the borrowed pieces fit.</summary>
    private static int TilesetOf(int major)
        => major >= 40 && major <= 49 ? major : major >= 60 && major <= 69 ? major - 20 : -1;

    // ── static floor raster (cached per floor + minimap state) ────────────────
    private sealed class Floor
    {
        public List<float[]> WSegs = new();              // composed prefab wall segments, world space
        public bool[] Base = new bool[FCols * FRows];   // inside an explored flag-1 cell whose piece was placed
        public float[] BaseDist = System.Array.Empty<float>();   // distance to the nearest non-base cell
        public int Segs, Cells, EdgeOk, EdgeBad, Missing, Partial, Checked, BadCells;
        public bool Usable;
        public string Why = "";
    }
    private static Floor? _floor;
    private static int _fMajor = -1, _fMinor = -1;
    private static long _fEpoch = -1;
    private static ulong _fHash;
    private static readonly object _floorLock = new();

    private static float OX, OZ;   // world X/Z of fine cell (0,0)'s low corner

    private static ulong GridHash(byte[] raw)
    {
        ulong h = 1469598103934665603UL;
        for (int i = 0; i < raw.Length; i++) { h ^= raw[i]; h *= 1099511628211UL; }
        return h;
    }

    private static Floor? EnsureFloor()
    {
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        int ts = TilesetOf(major);
        if (ts < 0) return null;
        EnsureData();
        if (_data == null || !_data.TryGetValue(ts, out var pieces)) return null;

        const int CS = MinimapTracker.CELL_SIZE;
        var raw = new byte[MinimapTracker.ROWS * MinimapTracker.COLS * CS];
        var one = new byte[CS];
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
            {
                if (!MinimapTracker.ReadCellRawBytes(r, c, one)) return null;
                System.Array.Copy(one, 0, raw, (r * MinimapTracker.COLS + c) * CS, CS);
            }
        ulong hash = GridHash(raw);
        lock (_floorLock)
        {
            if (_floor != null && major == _fMajor && minor == _fMinor && FieldTracker.LastAreaChangeMs == _fEpoch && hash == _fHash)
                return _floor;
            if (!MinimapTracker.CellToWorld(0, 0, out float x00, out float z00)) return null;
            if (!MinimapTracker.CellToWorld(1, 1, out float x11, out float z11)) return null;
            if (MathF.Abs(x11 - x00 - 1200f) > 1f || MathF.Abs(z11 - z00 - 1200f) > 1f)
            { Log($"[TileGrid] unexpected minimap pitch ({x11 - x00:F0},{z11 - z00:F0}) — planner off"); return null; }
            OX = x00 - 600f; OZ = z00 - 600f;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var f = Build(raw, pieces, x00, z00);
            _floor = f; _fMajor = major; _fMinor = minor; _fEpoch = FieldTracker.LastAreaChangeMs; _fHash = hash;
            Log($"[TileGrid] floor {major}/{minor} tileset {ts}: {f.Cells} cells, {f.Segs} wall segments, edge check {f.EdgeOk} ok / {f.EdgeBad} bad ({f.BadCells}/{f.Checked} cells), "
                + $"{f.Missing} unknown piece(s), {f.Partial} partly-seen room cell(s) — {(f.Usable ? "USABLE" : "OFF: " + f.Why)} ({sw.ElapsedMilliseconds} ms)");
            return f;
        }
    }

    private static void Rot(ref float x, ref float z, int k)
    {
        for (int i = 0; i < (k & 3); i++) { float t = x; x = z; z = -t; }
    }

    private static Floor Build(byte[] raw, Dictionary<int, Piece> pieces, float x00, float z00)
    {
        const int CS = MinimapTracker.CELL_SIZE;
        int R = MinimapTracker.ROWS, C = MinimapTracker.COLS;
        var f = new Floor();
        byte Flag(int r, int c) => raw[(r * C + c) * CS];
        int RoomOf(int r, int c) => raw[(r * C + c) * CS + 2] | (raw[(r * C + c) * CS + 3] << 8);
        byte Spr(int r, int c) => raw[(r * C + c) * CS + 4];
        byte Mod(int r, int c) => raw[(r * C + c) * CS + 5];
        byte W(int r, int c) => raw[(r * C + c) * CS + 6];
        byte H(int r, int c) => raw[(r * C + c) * CS + 7];
        byte Edge(int r, int c) => raw[(r * C + c) * CS + 0x0A];

        var placed = new List<(int spr, int mod, float cx, float cz, List<(int r, int c)> mem)>();
        var rooms = new Dictionary<int, List<(int r, int c)>>();
        for (int r = 0; r < R; r++)
            for (int c = 0; c < C; c++)
            {
                if (Flag(r, c) != 1) continue;
                f.Cells++;
                if (W(r, c) <= 1 && H(r, c) <= 1)
                    placed.Add((Spr(r, c), Mod(r, c), x00 + c * 1200f, z00 + r * 1200f, new List<(int, int)> { (r, c) }));
                else
                {
                    int id = RoomOf(r, c);
                    if (!rooms.TryGetValue(id, out var l)) rooms[id] = l = new List<(int, int)>();
                    l.Add((r, c));
                }
            }
        foreach (var (id, mem) in rooms)
        {
            var (r0, c0) = mem[0];
            int rlo = int.MaxValue, rhi = int.MinValue, clo = int.MaxValue, chi = int.MinValue;
            foreach (var (r, c) in mem) { rlo = Math.Min(rlo, r); rhi = Math.Max(rhi, r); clo = Math.Min(clo, c); chi = Math.Max(chi, c); }
            int w = W(r0, c0), h = H(r0, c0);
            bool whole = mem.Count == w * h && rhi - rlo + 1 == h && chi - clo + 1 == w;
            // stairs blocks read w/h as their own footprint too; a square 2×2/3×3 bbox fully seen also counts
            if (!whole && rhi - rlo == chi - clo && (rhi - rlo == 1 || rhi - rlo == 2) && mem.Count == (rhi - rlo + 1) * (chi - clo + 1))
                whole = true;
            if (!whole) { f.Partial += mem.Count; continue; }
            placed.Add((Spr(r0, c0), Mod(r0, c0), x00 + (clo + chi) * 600f, z00 + (rlo + rhi) * 600f, mem));
        }

        foreach (var (spr, mod, cx, cz, mem) in placed)
        {
            if (!pieces.TryGetValue(spr, out var piece)) { f.Missing++; continue; }
            // 3×3 STAIRS blocks: exactly two edge-0 cells = the staircase FOOTPRINT (every stairs block of
            // every tileset, entrance and progress, 2026-09-30) — sealed in the minimap, open floor in the
            // hit model. A room's interior cells also read 0, so the rule needs exactly-two + ±1800.
            int zero = 0;
            foreach (var (r, c) in mem) if ((Edge(r, c) & 0x0F) == 0) zero++;
            bool stairsBlock = piece.Ext > 1500 && mem.Count == 9 && zero == 2;
            foreach (var (r, c) in mem)
            {
                if (stairsBlock && (Edge(r, c) & 0x0F) == 0) continue;
                FillCell(f.Base, r, c);
            }
            foreach (var s in piece.Segs)
            {
                float ax = s[0], az = s[1], bx = s[2], bz = s[3];
                Rot(ref ax, ref az, mod); Rot(ref bx, ref bz, mod);
                f.WSegs.Add(new[] { cx + ax, cz + az, cx + bx, cz + bz });
                f.Segs++;
            }
            // SELF-CHECK (1×1 pieces): probe centre → 700u past each side; open iff no wall of THIS piece
            // crosses it. Must agree with the minimap edge bits (0x01 −Z, 0x02 −X, 0x04 +Z, 0x08 +X).
            if (mem.Count == 1)
            {
                var (r, c) = mem[0];
                byte e = Edge(r, c);
                int badHere = 0;
                Span<(int bit, float vx, float vz)> sides = stackalloc (int, float, float)[] { (0x01, 0, -1), (0x02, -1, 0), (0x04, 0, 1), (0x08, 1, 0) };
                // Probe ORIGIN = an interior point: the mean of the map's open-side directions × 300u. A
                // dead-end STUB doesn't reach its cell centre (Heaven / Yomotsu sprite 5) and a 460u-wide
                // L-corner's inner wall sits 5u from a 450u pull (Void Quest / Yomotsu sprite 3).
                float ox = 0, oz = 0; int nOpen = 0;
                foreach (var (bit, vx, vz) in sides) if ((e & bit) != 0) { ox += vx; oz += vz; nOpen++; }
                if (nOpen == 0) continue;
                ox = ox * 300f / nOpen; oz = oz * 300f / nOpen;
                foreach (var (bit, vx, vz) in sides)
                {
                    // two probes ±23u off the centre-line: ONE probe on x=0 slips through the joint where
                    // two end-cap segments meet (strict intersection) — Bathhouse, 11 false "open" sides
                    bool open = true;
                    foreach (var s in piece.Segs)
                    {
                        float ax = s[0], az = s[1], bx = s[2], bz = s[3];
                        Rot(ref ax, ref az, mod); Rot(ref bx, ref bz, mod);
                        bool hit = false;
                        for (int o = -23; o <= 23 && !hit; o += 46)
                            hit = SegCross(ox - vz * o, oz + vx * o, vx * 700f - vz * o, vz * 700f + vx * o, ax, az, bx, bz);
                        if (hit) { open = false; break; }
                    }
                    if (open == ((e & bit) != 0)) f.EdgeOk++; else { f.EdgeBad++; badHere++; }
                }
                f.Checked++;
                if (badHere > 0) f.BadCells++;
            }
        }
        var notBase = new bool[FCols * FRows];
        for (int i = 0; i < notBase.Length; i++) notBase[i] = !f.Base[i];
        f.BaseDist = Clearance(notBase);
        if (f.Missing > 0) { f.Why = $"{f.Missing} piece(s) missing from the data"; return f; }
        if (f.Checked < 2) { f.Why = "too few pieces to check"; return f; }
        // Judged per CELL: a dead-end STUB piece (Heaven sprite 5) does not cover its cell centre, so the
        // centre probe misreads all four sides of that one cell — the geometry itself is right. A wrong
        // convention / custom scripted geometry disagrees on MANY cells.
        if (f.BadCells > Math.Max(2, f.Checked / 10)) { f.Why = $"{f.BadCells} of {f.Checked} pieces disagree with the minimap"; return f; }
        f.Usable = true;
        return f;
    }

    private static void FillCell(bool[] mask, int r, int c)
    {
        for (int i = 0; i < Sub; i++)
            for (int j = 0; j < Sub; j++)
            {
                int fr = r * Sub + i, fc = c * Sub + j;
                if (fr >= 0 && fr < FRows && fc >= 0 && fc < FCols) mask[fr * FCols + fc] = true;
            }
    }

    private static void RasterSeg(bool[] mask, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        int n = Math.Max(1, (int)MathF.Ceiling(len / (Fine * 0.4f)));
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            int fc = (int)MathF.Floor((ax + dx * t - OX) / Fine), fr = (int)MathF.Floor((az + dz * t - OZ) / Fine);
            if (fr >= 0 && fr < FRows && fc >= 0 && fc < FCols) mask[fr * FCols + fc] = true;
        }
    }

    private static bool SegCross(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
    {
        static float O(float px, float pz, float qx, float qz, float rx, float rz) => (qx - px) * (rz - pz) - (qz - pz) * (rx - px);
        float d1 = O(cx, cz, dx, dz, ax, az), d2 = O(cx, cz, dx, dz, bx, bz);
        float d3 = O(ax, az, bx, bz, cx, cz), d4 = O(ax, az, bx, bz, dx, dz);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    // ── clearance ────────────────────────────────────────────────────────────

    /// <summary>Clearance field (world units to the nearest obstacle, capped at ClearCap) from the EXACT
    /// point-to-segment distance of every prefab wall — a 50u raster ate ~50u of every 160u Bathhouse
    /// doorway and sealed them all. DOORS: the prefab hit model holds each door's CLOSED PANEL (a
    /// 320×160 box across the corridor; the game drops it when the door opens — the player's recorded
    /// path runs straight through it), so segments within DoorCarveUnits of an unlocked door are
    /// removed and a body-wide passage is forced along the door axis. Locked doors keep their panel
    /// plus a seal across the gap. Chests / this walk's stamps are round obstacles.</summary>
    private static float[] ComputeClear(Floor f, HashSet<int>? blocked, List<(float x, float z)> doors,
        bool withChests, float tx, float tz, bool withStamps, bool ignoreLocks = false)
    {
        int N = FCols * FRows;
        var d = new float[N];
        for (int i = 0; i < N; i++) d[i] = MathF.Min(f.BaseDist[i], ClearCap);

        var locked = new List<(float x, float z)>();
        var open = new List<(float x, float z)>();
        foreach (var (dx, dz) in doors)
        {
            bool l = false;
            if (!ignoreLocks) try { l = DungeonNav.IsDoorLocked(dx, dz); } catch { }
            (l ? locked : open).Add((dx, dz));
        }
        foreach (var sg in f.WSegs)
        {
            bool carved = false;
            foreach (var (dx, dz) in open)
                if (SegDist(dx, dz, sg[0], sg[1], sg[2], sg[3]) < DoorCarveUnits) { carved = true; break; }
            if (!carved) StampSeg(d, sg[0], sg[1], sg[2], sg[3], 0f);
        }
        foreach (var (dx, dz) in locked)
        {
            if (DungeonNav.TryDoorAxis(dx, dz, out float nx, out float nz))
                StampSeg(d, dx - nz * 450f, dz + nx * 450f, dx + nz * 450f, dz - nx * 450f, 0f);
            else StampSeg(d, dx, dz, dx, dz, 320f);
        }
        if (withChests)
        {
            try
            {
                foreach (var (cx, cz) in DungeonNav.Chests())
                    if (float.IsNaN(tx) || (cx - tx) * (cx - tx) + (cz - tz) * (cz - tz) > 250f * 250f)
                        StampSeg(d, cx, cz, cx, cz, ChestRadius);
            }
            catch { }
        }
        if (withStamps && _stamps != null) foreach (var (sx, sz) in _stamps) StampSeg(d, sx, sz, sx, sz, StampRadius);
        // STAIRCASE PLUG (2026-09-30, Castle 5F): the staircase is an ACTOR like the doors — the prefab
        // leaves its opening as floor that leads out of the stairs room into the space behind the
        // corridor walls. With the room's door locked, that fake exit let the planner "leave" (two
        // stalls at the stair foot). Plug it just past the known prompt point, away from the room; a
        // stairs walk then ends at the foot and its tail steps onto the prompt.
        try
        {
            foreach (var b in GridRouter.AllStairsBlocks())
            {
                if (!b.Known) continue;
                float ux = b.TX - b.CX, uz = b.TZ - b.CZ, ul = MathF.Sqrt(ux * ux + uz * uz);
                if (ul < 50f) continue;
                StampSeg(d, b.TX + ux / ul * StairsPlugPush, b.TZ + uz / ul * StairsPlugPush,
                         b.TX + ux / ul * StairsPlugPush, b.TZ + uz / ul * StairsPlugPush, StairsPlugRadius);
            }
        }
        catch { }
        if (blocked != null)
            foreach (int k in blocked)
            {
                int r = k / MinimapTracker.COLS, c = k % MinimapTracker.COLS;
                for (int i = 0; i < Sub; i++) for (int j = 0; j < Sub; j++)
                    { int fr = r * Sub + i, fc = c * Sub + j; if (fr < FRows && fc < FCols) d[fr * FCols + fc] = 0f; }
            }
        // Door passages: a body-wide lane through each open door along its axis (live transform, else the
        // cell-boundary rule — doors sit at k·1200±600 on their passage axis, i.e. on a cell edge).
        foreach (var (dx, dz) in open)
        {
            float ax, az;
            if (!DungeonNav.TryDoorAxis(dx, dz, out ax, out az))
            {
                float fx = ((dx - OX) / 1200f) % 1f, fz = ((dz - OZ) / 1200f) % 1f;   // OX = centre−600 → edge ↔ frac 0
                if (fx < 0) fx += 1f;
                if (fz < 0) fz += 1f;
                bool bx = fx < 0.08f || fx > 0.92f, bz = fz < 0.08f || fz > 0.92f;
                if (bx == bz) continue;
                ax = bx ? 1f : 0f; az = bx ? 0f : 1f;
            }
            for (float t = -400f; t <= 400f; t += 25f)
            {
                int c = (int)MathF.Floor((dx + ax * t - OX) / Fine), r = (int)MathF.Floor((dz + az * t - OZ) / Fine);
                if (r < 0 || r >= FRows || c < 0 || c >= FCols) continue;
                int i = r * FCols + c;
                if (f.Base[i] && d[i] >= 20f && d[i] < BodyClear) d[i] = BodyClear;
            }
        }
        return d;
    }

    private static float SegDist(float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az, L2 = dx * dx + dz * dz;
        float t = L2 < 1e-6f ? 0f : Math.Clamp(((px - ax) * dx + (pz - az) * dz) / L2, 0f, 1f);
        float qx = ax + t * dx - px, qz = az + t * dz - pz;
        return MathF.Sqrt(qx * qx + qz * qz);
    }

    /// <summary>d[i] = min(d[i], distance from cell i's centre to the segment − radius), within ClearCap.</summary>
    private static void StampSeg(float[] d, float ax, float az, float bx, float bz, float radius)
    {
        float R = ClearCap + radius;
        int c0 = Math.Max(0, (int)MathF.Floor((MathF.Min(ax, bx) - R - OX) / Fine));
        int c1 = Math.Min(FCols - 1, (int)MathF.Floor((MathF.Max(ax, bx) + R - OX) / Fine));
        int r0 = Math.Max(0, (int)MathF.Floor((MathF.Min(az, bz) - R - OZ) / Fine));
        int r1 = Math.Min(FRows - 1, (int)MathF.Floor((MathF.Max(az, bz) + R - OZ) / Fine));
        for (int r = r0; r <= r1; r++)
        {
            float z = OZ + (r + 0.5f) * Fine;
            for (int c = c0; c <= c1; c++)
            {
                float x = OX + (c + 0.5f) * Fine;
                float v = SegDist(x, z, ax, az, bx, bz) - radius;
                int i = r * FCols + c;
                if (v < d[i]) d[i] = MathF.Max(0f, v);
            }
        }
    }

    // ── planning ─────────────────────────────────────────────────────────────

    /// <summary>Fine plan player→target. False = no usable prefab data / no fine path (the caller falls
    /// back to the coarse planner). <paramref name="blocked"/> = the drive's per-walk blocked minimap
    /// cells (honoured). doorTarget = the plan ends centred in front of the target door.</summary>
    internal static bool TryPlan(float px, float pz, float tx, float tz, HashSet<int>? blocked, bool doorTarget,
        out List<(float x, float z)> waypoints)
    {
        waypoints = new List<(float, float)>();
        LastLockedBlock = false;
        if (!Enabled || float.IsNaN(px) || float.IsNaN(tx)) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Floor? f;
        try { f = EnsureFloor(); } catch (Exception e) { Log($"[TileGrid] build failed: {e.Message}"); return false; }
        if (f == null || !f.Usable) return false;

        // EVERY door on the floor (Doors() lists only the ACTIVE ones near the body — the far door on
        // the route was never woven, Castle 1F 2026-09-30).
        var doors = new List<(float x, float z)>();
        try { foreach (var d in DungeonNav.DoorsAll()) doors.Add(d); } catch { }
        var clear = ComputeClear(f, blocked, doors, withChests: true, tx, tz, withStamps: true);
        if (!Snap(clear, px, pz, 8, out int s0)) { Log($"[TileGrid] no free cell near the player ({px:F0},{pz:F0})"); return false; }
        // Snap the goal to the nearest REACHABLE free cell: a thick double wall's hollow core reads as
        // "free" (≈80u clearance) but is sealed — snapping into it failed a door walk (Castle 1F).
        var reach = Reach(clear, s0);
        if (!Snap(clear, tx, tz, 12, out int g0, reach))
        {
            // Is a LOCKED door the reason? Re-flood with the locked doors treated as open.
            bool anyLocked = false;
            foreach (var (dx, dz) in doors) { try { if (DungeonNav.IsDoorLocked(dx, dz)) { anyLocked = true; break; } } catch { } }
            if (anyLocked)
            {
                var clearOpen = ComputeClear(f, blocked, doors, withChests: true, tx, tz, withStamps: true, ignoreLocks: true);
                if (Snap(clearOpen, px, pz, 8, out int so) && Snap(clearOpen, tx, tz, 12, out _, Reach(clearOpen, so)))
                {
                    LastLockedBlock = true;
                    Log($"[TileGrid] target ({tx:F0},{tz:F0}) is reachable only through a LOCKED door — refusing");
                    return false;
                }
            }
            Log($"[TileGrid] no reachable cell near the target ({tx:F0},{tz:F0}) — coarse planner");
            return false;
        }

        var path = AStar(clear, s0, g0);
        if (path == null) { Log($"[TileGrid] no fine path ({px:F0},{pz:F0})→({tx:F0},{tz:F0}) — coarse planner"); return false; }

        // Door crossings: a door within DoorWeaveUnits of the path whose plane the path CROSSES → the drive
        // gets a centred point in front of the gap and one beyond it (its door ladder opens a closed one).
        int last = path.Count - 1;
        var cross = new List<(int i0, int i1, float fx, float fz, float bx, float bz, bool target)>();
        foreach (var (dx, dz) in doors)
        {
            int bi = -1; float bd = DoorWeaveUnits * DoorWeaveUnits;
            for (int i = 0; i <= last; i++)
            {
                CellCenter(path[i], out float cx, out float cz);
                float d2 = (cx - dx) * (cx - dx) + (cz - dz) * (cz - dz);
                if (d2 < bd) { bd = d2; bi = i; }
            }
            if (bi < 0) continue;
            CellCenter(path[Math.Max(0, bi - 8)], out float ax0, out float az0);
            if (!DungeonNav.TryDoorAxis(dx, dz, out float nx, out float nz))
            {
                // far door (no live transform): the passage axis = the path's own direction through it
                CellCenter(path[Math.Min(last, bi + 8)], out float fx1, out float fz1);
                nx = fx1 - ax0; nz = fz1 - az0;
                float nl = MathF.Sqrt(nx * nx + nz * nz);
                if (nl < 1f) continue;
                nx /= nl; nz /= nl;
            }
            if ((ax0 - dx) * nx + (az0 - dz) * nz < 0) { nx = -nx; nz = -nz; }   // n̂ → the side we come from
            bool isTarget = doorTarget && (dx - tx) * (dx - tx) + (dz - tz) * (dz - tz) < 500f * 500f;
            CellCenter(path[Math.Min(last, bi + 8)], out float ax1, out float az1);
            if (!isTarget && (ax1 - dx) * nx + (az1 - dz) * nz >= 0) continue;   // passes beside, never through
            float Side(int i) { CellCenter(path[i], out float qx, out float qz); return (qx - dx) * nx + (qz - dz) * nz; }
            int i0 = bi; while (i0 > 0 && Side(i0) < DoorStandoff * 0.8f) i0--;
            int i1 = bi; while (i1 < last && Side(i1) > -DoorStandoff * 0.8f) i1++;
            cross.Add((i0, i1, dx + nx * DoorStandoff, dz + nz * DoorStandoff, dx - nx * DoorStandoff, dz - nz * DoorStandoff, isTarget));
        }
        cross.Sort((a, b) => a.i0.CompareTo(b.i0));

        var pts = new List<(float x, float z)>();
        int cur = 0; bool ended = false; int woven = 0;
        foreach (var cr in cross)
        {
            if (cr.i0 < cur) continue;                       // overlaps the previous crossing
            Pull(clear, path, cur, cr.i0, pts);
            if (pts.Count > 0 && (pts[^1].x - cr.fx) * (pts[^1].x - cr.fx) + (pts[^1].z - cr.fz) * (pts[^1].z - cr.fz) < 150f * 150f)
                pts.RemoveAt(pts.Count - 1);                 // path point ≈ the front point
            pts.Add((cr.fx, cr.fz));
            woven++;
            if (cr.target) { ended = true; break; }         // this door IS the target — stop centred in front
            pts.Add((cr.bx, cr.bz));
            cur = cr.i1;
        }
        if (!ended)
        {
            Pull(clear, path, cur, last, pts);
            if (!doorTarget) pts.Add((tx, tz));             // the true final point is the target itself
        }
        // Drop a first point the player already stands on.
        while (pts.Count > 1 && (pts[0].x - px) * (pts[0].x - px) + (pts[0].z - pz) * (pts[0].z - pz) < 120f * 120f) pts.RemoveAt(0);
        waypoints = pts;
        Log($"[TileGrid] plan ({px:F0},{pz:F0})→({tx:F0},{tz:F0}): {path.Count} fine cells → {pts.Count} waypoints, "
            + $"{woven} door crossing(s) ({sw.ElapsedMilliseconds} ms)");
        return pts.Count > 0;
    }

    private static void CellCenter(int i, out float x, out float z)
    { x = OX + (i % FCols + 0.5f) * Fine; z = OZ + (i / FCols + 0.5f) * Fine; }

    private static void Disk(bool[] m, float x, float z, float rad)
    {
        int r = (int)MathF.Ceiling(rad / Fine);
        int c0 = (int)MathF.Floor((x - OX) / Fine), r0 = (int)MathF.Floor((z - OZ) / Fine);
        for (int dr = -r; dr <= r; dr++)
            for (int dc = -r; dc <= r; dc++)
            {
                if (dr * dr + dc * dc > r * r) continue;
                int fr = r0 + dr, fc = c0 + dc;
                if (fr >= 0 && fr < FRows && fc >= 0 && fc < FCols) m[fr * FCols + fc] = true;
            }
    }

    /// <summary>Distance (world units) from each fine cell to the nearest obstacle cell — two-pass
    /// chamfer (1 / √2), minus half a cell so a cell touching a wall reads ~0.</summary>
    private static float[] Clearance(bool[] obst)
    {
        int N = FCols * FRows;
        var d = new float[N];
        const float INF = 1e9f, D1 = 1f, D2 = 1.41421356f;
        for (int i = 0; i < N; i++) d[i] = obst[i] ? 0f : INF;
        for (int r = 0; r < FRows; r++)
            for (int c = 0; c < FCols; c++)
            {
                int i = r * FCols + c; float v = d[i];
                if (v == 0f) continue;
                if (c > 0) v = MathF.Min(v, d[i - 1] + D1);
                if (r > 0)
                {
                    v = MathF.Min(v, d[i - FCols] + D1);
                    if (c > 0) v = MathF.Min(v, d[i - FCols - 1] + D2);
                    if (c < FCols - 1) v = MathF.Min(v, d[i - FCols + 1] + D2);
                }
                d[i] = v;
            }
        for (int r = FRows - 1; r >= 0; r--)
            for (int c = FCols - 1; c >= 0; c--)
            {
                int i = r * FCols + c; float v = d[i];
                if (v == 0f) continue;
                if (c < FCols - 1) v = MathF.Min(v, d[i + 1] + D1);
                if (r < FRows - 1)
                {
                    v = MathF.Min(v, d[i + FCols] + D1);
                    if (c < FCols - 1) v = MathF.Min(v, d[i + FCols + 1] + D2);
                    if (c > 0) v = MathF.Min(v, d[i + FCols - 1] + D2);
                }
                d[i] = v;
            }
        for (int i = 0; i < N; i++) d[i] = d[i] >= INF ? 1e6f : MathF.Max(0f, (d[i] - 0.5f) * Fine);
        return d;
    }

    /// <summary>Cells reachable from <paramref name="start"/> with the A*'s own passability.</summary>
    private static bool[] Reach(float[] clear, int start)
    {
        var seen = new bool[FCols * FRows];
        var q = new Queue<int>();
        seen[start] = true; q.Enqueue(start);
        while (q.Count > 0)
        {
            int cur = q.Dequeue(), r = cur / FCols, c = cur % FCols;
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int nr = r + dr, nc = c + dc;
                    if (nr < 0 || nr >= FRows || nc < 0 || nc >= FCols) continue;
                    int ni = nr * FCols + nc;
                    if (seen[ni] || clear[ni] < BodyClear) continue;
                    if (dr != 0 && dc != 0 && (clear[r * FCols + nc] < BodyClear || clear[nr * FCols + c] < BodyClear)) continue;
                    seen[ni] = true; q.Enqueue(ni);
                }
        }
        return seen;
    }

    private static bool Snap(float[] clear, float x, float z, int maxRing, out int idx, bool[]? only = null)
    {
        idx = -1;
        int c0 = (int)MathF.Floor((x - OX) / Fine), r0 = (int)MathF.Floor((z - OZ) / Fine);
        for (int ring = 0; ring <= maxRing; ring++)
        {
            float best = float.MaxValue;
            for (int dr = -ring; dr <= ring; dr++)
                for (int dc = -ring; dc <= ring; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != ring) continue;
                    int r = r0 + dr, c = c0 + dc;
                    if (r < 0 || r >= FRows || c < 0 || c >= FCols) continue;
                    int i = r * FCols + c;
                    if (clear[i] < BodyClear || (only != null && !only[i])) continue;
                    float d2 = dr * dr + dc * dc;
                    if (d2 < best) { best = d2; idx = i; }
                }
            if (idx >= 0) return true;
        }
        return false;
    }

    private static List<int>? AStar(float[] clear, int s, int g)
    {
        int N = FCols * FRows;
        var gs = new float[N];
        var came = new int[N];
        var closed = new bool[N];
        System.Array.Fill(gs, float.MaxValue);
        var open = new PriorityQueue<int, float>();
        int gr = g / FCols, gc = g % FCols;
        float Hh(int i)
        {
            int dr = Math.Abs(i / FCols - gr), dc = Math.Abs(i % FCols - gc);
            return (Math.Max(dr, dc) + 0.41421356f * Math.Min(dr, dc)) * Fine;
        }
        gs[s] = 0; came[s] = -1;
        open.Enqueue(s, Hh(s));
        Span<(int dr, int dc, float w)> nb = stackalloc (int, int, float)[]
        {
            (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
            (1, 1, 1.41421356f), (1, -1, 1.41421356f), (-1, 1, 1.41421356f), (-1, -1, 1.41421356f),
        };
        int expanded = 0;
        while (open.Count > 0)
        {
            int cur = open.Dequeue();
            if (closed[cur]) continue;
            closed[cur] = true;
            if (cur == g)
            {
                var path = new List<int>();
                for (int i = g; i >= 0; i = came[i]) path.Add(i);
                path.Reverse();
                return path;
            }
            if (++expanded > 200_000) return null;
            int r = cur / FCols, c = cur % FCols;
            foreach (var (dr, dc, w) in nb)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nr >= FRows || nc < 0 || nc >= FCols) continue;
                int ni = nr * FCols + nc;
                if (closed[ni] || clear[ni] < BodyClear) continue;
                if (dr != 0 && dc != 0 && (clear[r * FCols + nc] < BodyClear || clear[nr * FCols + c] < BodyClear)) continue;
                float pen = clear[ni] >= PreferClear ? 0f : (PreferClear - clear[ni]) / PreferClear;
                float ng = gs[cur] + w * Fine * (1f + 3f * pen);
                if (ng < gs[ni]) { gs[ni] = ng; came[ni] = cur; open.Enqueue(ni, ng + Hh(ni)); }
            }
        }
        return null;
    }

    /// <summary>Greedy line-of-sight string-pull of path(from..to] (path[from] itself excluded) into pts.
    /// A shortcut is taken only when every sample keeps at least min(PullClearCap, the tightest
    /// clearance the A* path itself had over that stretch) — never tighter than the centred path.</summary>
    private static void Pull(float[] clear, List<int> path, int from, int to, List<(float x, float z)> pts)
    {
        int a = from;
        while (a < to)
        {
            int best = a + 1;
            float minC = clear[path[a + 1]];
            for (int b = a + 2; b <= to; b++)
            {
                minC = MathF.Min(minC, clear[path[b]]);
                CellCenter(path[a], out float ax, out float az);
                CellCenter(path[b], out float bx, out float bz);
                if ((bx - ax) * (bx - ax) + (bz - az) * (bz - az) > MaxLeg * MaxLeg) break;
                if (!Los(clear, ax, az, bx, bz, MathF.Min(PullClearCap, minC) - 10f)) continue;
                best = b;
            }
            CellCenter(path[best], out float px, out float pz);
            pts.Add((px, pz));
            a = best;
        }
    }

    private static bool Los(float[] clear, float ax, float az, float bx, float bz, float need)
    {
        float dx = bx - ax, dz = bz - az;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        int n = Math.Max(1, (int)MathF.Ceiling(len / (Fine * 0.5f)));
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            int c = (int)MathF.Floor((ax + dx * t - OX) / Fine), r = (int)MathF.Floor((az + dz * t - OZ) / Fine);
            if (r < 0 || r >= FRows || c < 0 || c >= FCols) return false;
            if (clear[r * FCols + c] < need) return false;
        }
        return true;
    }

    // ── route distances for the browser (item 2: "10 steps" to a chest was a 44-waypoint walk) ──
    private static float[]? _field;          // walking distance from the player to every fine cell
    private static float[]? _fieldClear;
    private static bool[]? _fieldReach;
    private static int _fieldFrom = -1;
    private static long _fieldMs;
    private static Floor? _fieldFloor;
    private static readonly object _fieldLock = new();

    /// <summary>Walking distance (world units) from the player to each target along the prefab corridors,
    /// NaN where no route is known (unexplored, other floor, planner off). One Dijkstra from the player's
    /// fine cell serves every target; it is reused while the player stays in the same ~150u patch
    /// (≤1.5 s). Doors count as passable (the walk opens them); chests are ignored (never a full block).</summary>
    internal static float[] RouteLengths(float px, float pz, IReadOnlyList<(float x, float z)> targets)
    {
        var res = new float[targets.Count];
        System.Array.Fill(res, float.NaN);
        if (!Enabled || float.IsNaN(px) || targets.Count == 0) return res;
        Floor? f;
        try { f = EnsureFloor(); } catch { return res; }
        if (f == null || !f.Usable) return res;
        lock (_fieldLock)
        {
            int s0 = (int)MathF.Floor((pz - OZ) / Fine) / 3 * 100000 + (int)MathF.Floor((px - OX) / Fine) / 3;
            if (_field == null || _fieldFloor != f || _fieldFrom != s0 || Environment.TickCount64 - _fieldMs > 1500)
            {
                int N = FCols * FRows;
                var doors = new List<(float x, float z)>();
                try { foreach (var d in DungeonNav.DoorsAll()) doors.Add(d); } catch { }
                var clear = ComputeClear(f, null, doors, withChests: false, float.NaN, float.NaN, withStamps: false);
                if (!Snap(clear, px, pz, 8, out int start)) { _field = null; return res; }
                var dist = new float[N];
                System.Array.Fill(dist, float.MaxValue);
                var q = new PriorityQueue<int, float>();
                dist[start] = 0; q.Enqueue(start, 0);
                while (q.Count > 0)
                {
                    q.TryDequeue(out int cur, out float cd);
                    if (cd > dist[cur]) continue;
                    int r = cur / FCols, c = cur % FCols;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            if (dr == 0 && dc == 0) continue;
                            int nr = r + dr, nc = c + dc;
                            if (nr < 0 || nr >= FRows || nc < 0 || nc >= FCols) continue;
                            int ni = nr * FCols + nc;
                            if (clear[ni] < BodyClear) continue;
                            if (dr != 0 && dc != 0 && (clear[r * FCols + nc] < BodyClear || clear[nr * FCols + c] < BodyClear)) continue;
                            float nd = cd + (dr != 0 && dc != 0 ? 1.41421356f : 1f) * Fine;
                            if (nd < dist[ni]) { dist[ni] = nd; q.Enqueue(ni, nd); }
                        }
                }
                var reached = new bool[N];
                for (int i = 0; i < N; i++) reached[i] = dist[i] < float.MaxValue / 2;
                _field = dist; _fieldClear = clear; _fieldReach = reached; _fieldFrom = s0; _fieldMs = Environment.TickCount64; _fieldFloor = f;
            }
            for (int k = 0; k < targets.Count; k++)
            {
                var (tx, tz) = targets[k];
                if (!Snap(_fieldClear!, tx, tz, 12, out int gi, _fieldReach)) continue;
                float d = _field[gi];
                if (d >= float.MaxValue / 2) continue;
                CellCenter(gi, out float gx, out float gz);
                // 8-connected steps run a few % long vs the true straight line — shave 4%
                res[k] = d * 0.96f + MathF.Sqrt((gx - tx) * (gx - tx) + (gz - tz) * (gz - tz));
            }
        }
        return res;
    }

    /// <summary>Route length (world units) of a waypoint list starting at (px,pz).</summary>
    internal static float RouteLength(float px, float pz, List<(float x, float z)> w)
    {
        float L = 0, x = px, z = pz;
        foreach (var (wx, wz) in w) { L += MathF.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z)); x = wx; z = wz; }
        return L;
    }
}
