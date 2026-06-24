using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// Per-floor 2D index of the collision WALL triangles (auto-walk research #2,
/// memory/autowalk_research_findings.md): built once per floor from the
/// master wall-actor list (<see cref="FieldTracker.VisitMasterWallTriangles"/>,
/// full-floor coverage, verified 2026-05-01), filtered to steep/vertical
/// triangles (walls — floors are rejected by surface normal), projected to XZ
/// and bucketed into a uniform grid for fast queries.
///
/// Primary query: <see cref="SegmentBlocked"/> — "does this line (with body
/// width) cross a wall?" — which powers <see cref="Smooth"/>: greedy
/// line-of-walkability pruning of the cell-center waypoint list ("string
/// pulling"), so routes become a few straight legs that provably miss the
/// real geometry, not just the coarse minimap cells.
///
/// Build cost is paid once per floor on the first auto-walk (logged); when
/// the master list isn't ready the index reports unusable and callers keep
/// the unsmoothed path.
/// </summary>
internal static class WallMesh
{
    private const float BucketSize = 800f;
    private const float MaxNormalY = 0.7f;     // |unit-normal Y| above this = floor/ramp, not wall

    private struct Tri
    {
        public float X1, Z1, X2, Z2, X3, Z3;
        public float MinX, MaxX, MinZ, MaxZ;
    }

    private static readonly object _lock = new();
    private static int _major = -1, _minor = -1;
    private static long _builtAt;
    private static List<Tri> _tris = new();
    private static Dictionary<long, List<int>> _buckets = new();

    // The master actor list streams in as the floor loads/explores — the
    // first build saw 24 triangles (the spawn bubble) and latched (live
    // 2026-06-11). Rebuild when stale; a build is ~3ms.
    private const long RebuildMs = 5000;

    /// <summary>
    /// Make sure the index matches the current floor and isn't stale.
    /// Returns false when no usable geometry is available (callers proceed
    /// without smoothing).
    /// </summary>
    internal static bool EnsureBuilt()
    {
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        lock (_lock)
        {
            if (major == _major && minor == _minor && _tris.Count > 0
                && Environment.TickCount64 - _builtAt < RebuildMs)
                return true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tris = new List<Tri>();
            int rawCount = 0;
            bool any = FieldTracker.VisitMasterWallTriangles(t =>
            {
                rawCount++;
                // t = 9 world-space floats (x,y,z × 3). Reject non-walls by
                // surface normal — keep steep triangles only.
                float ux = t[3] - t[0], uy = t[4] - t[1], uz = t[5] - t[2];
                float vx = t[6] - t[0], vy = t[7] - t[1], vz = t[8] - t[2];
                float nx = uy * vz - uz * vy;
                float ny = uz * vx - ux * vz;
                float nz = ux * vy - uy * vx;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!float.IsFinite(len) || len < 1e-3f) return;
                if (MathF.Abs(ny / len) > MaxNormalY) return;

                var tri = new Tri { X1 = t[0], Z1 = t[2], X2 = t[3], Z2 = t[5], X3 = t[6], Z3 = t[8] };
                tri.MinX = MathF.Min(tri.X1, MathF.Min(tri.X2, tri.X3));
                tri.MaxX = MathF.Max(tri.X1, MathF.Max(tri.X2, tri.X3));
                tri.MinZ = MathF.Min(tri.Z1, MathF.Min(tri.Z2, tri.Z3));
                tri.MaxZ = MathF.Max(tri.Z1, MathF.Max(tri.Z2, tri.Z3));
                tris.Add(tri);
            });

            if (!any || tris.Count == 0)
            {
                Log($"[WallMesh] no geometry for floor {major}/{minor} (master list not ready?)");
                // Don't latch the floor key — retry on the next call.
                return _major == major && _minor == minor && _tris.Count > 0;
            }

            // A stale rebuild can race a floor mid-stream; never downgrade.
            if (major == _major && minor == _minor && tris.Count <= _tris.Count)
            {
                _builtAt = Environment.TickCount64;
                return _tris.Count > 0;
            }

            var buckets = new Dictionary<long, List<int>>();
            for (int i = 0; i < tris.Count; i++)
            {
                var tr = tris[i];
                int gx0 = (int)MathF.Floor(tr.MinX / BucketSize), gx1 = (int)MathF.Floor(tr.MaxX / BucketSize);
                int gz0 = (int)MathF.Floor(tr.MinZ / BucketSize), gz1 = (int)MathF.Floor(tr.MaxZ / BucketSize);
                for (int gx = gx0; gx <= gx1; gx++)
                for (int gz = gz0; gz <= gz1; gz++)
                {
                    long key = ((long)gx << 32) ^ (uint)gz;
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                    list.Add(i);
                }
            }

            _tris = tris;
            _buckets = buckets;
            _major = major; _minor = minor;
            _builtAt = Environment.TickCount64;
            // rawCount vs kept tells WHERE low coverage comes from: few raw
            // triangles = the master-list enumeration sees few actors (RE
            // needed); many raw but few kept = the normal filter is wrong.
            Log($"[WallMesh] built floor {major}/{minor}: {tris.Count} wall tris " +
                $"(of {rawCount} raw, {FieldTracker.GetMasterWallActors().Count} actors), " +
                $"{buckets.Count} buckets, {sw.ElapsedMilliseconds}ms");
            return true;
        }
    }

    /// <summary>
    /// True when a body of half-width <paramref name="inflate"/> moving in a
    /// straight line from (x0,z0) to (x1,z1) would cross a wall triangle.
    /// Tests the center line plus both laterally offset lines.
    /// </summary>
    internal static bool SegmentBlocked(float x0, float z0, float x1, float z1, float inflate)
    {
        float dx = x1 - x0, dz = z1 - z0;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1f) return false;
        float px = -dz / len * inflate, pz = dx / len * inflate;

        return LineBlocked(x0, z0, x1, z1)
            || LineBlocked(x0 + px, z0 + pz, x1 + px, z1 + pz)
            || LineBlocked(x0 - px, z0 - pz, x1 - px, z1 - pz);
    }

    /// <summary>
    /// Greedy string pulling: from (px,pz), keep the farthest waypoint
    /// reachable in a straight clear line, repeat from there. The final point
    /// (exact target) is always kept. Input order is preserved; returns the
    /// pruned list (or the input when the mesh isn't usable).
    /// </summary>
    internal static List<(float x, float z)> Smooth(float px, float pz,
        List<(float x, float z)> pts, float inflate)
    {
        if (pts.Count <= 1 || !EnsureBuilt()) return pts;

        var outp = new List<(float, float)>();
        float cx = px, cz = pz;
        int i = 0;
        while (i < pts.Count)
        {
            int best = i;       // worst case: keep the immediate next point
            for (int j = pts.Count - 1; j > i; j--)
            {
                if (!SegmentBlocked(cx, cz, pts[j].x, pts[j].z, inflate)) { best = j; break; }
            }
            outp.Add(pts[best]);
            (cx, cz) = pts[best];
            i = best + 1;
        }
        return outp;
    }

    // ── geometry ────────────────────────────────────────────────────────────

    private static bool LineBlocked(float x0, float z0, float x1, float z1)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minZ = MathF.Min(z0, z1), maxZ = MathF.Max(z0, z1);
        int gx0 = (int)MathF.Floor(minX / BucketSize), gx1 = (int)MathF.Floor(maxX / BucketSize);
        int gz0 = (int)MathF.Floor(minZ / BucketSize), gz1 = (int)MathF.Floor(maxZ / BucketSize);

        var tris = _tris;
        var buckets = _buckets;
        var tested = new HashSet<int>();
        for (int gx = gx0; gx <= gx1; gx++)
        for (int gz = gz0; gz <= gz1; gz++)
        {
            long key = ((long)gx << 32) ^ (uint)gz;
            if (!buckets.TryGetValue(key, out var list)) continue;
            foreach (int ti in list)
            {
                if (!tested.Add(ti)) continue;
                var tr = tris[ti];
                if (maxX < tr.MinX || minX > tr.MaxX || maxZ < tr.MinZ || minZ > tr.MaxZ) continue;
                if (SegSeg(x0, z0, x1, z1, tr.X1, tr.Z1, tr.X2, tr.Z2)) return true;
                if (SegSeg(x0, z0, x1, z1, tr.X2, tr.Z2, tr.X3, tr.Z3)) return true;
                if (SegSeg(x0, z0, x1, z1, tr.X3, tr.Z3, tr.X1, tr.Z1)) return true;
            }
        }
        return false;
    }

    private static bool SegSeg(float ax, float az, float bx, float bz,
                               float cx, float cz, float dx, float dz)
    {
        float d1 = Cross(cx, cz, dx, dz, ax, az);
        float d2 = Cross(cx, cz, dx, dz, bx, bz);
        float d3 = Cross(ax, az, bx, bz, cx, cz);
        float d4 = Cross(ax, az, bx, bz, dx, dz);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static float Cross(float ax, float az, float bx, float bz, float px, float pz)
        => (bx - ax) * (pz - az) - (bz - az) * (px - ax);
}
