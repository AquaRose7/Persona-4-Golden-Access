using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// Room-level route planner over the minimap's own ROOM IDS (user design
/// 2026-07-06, replacing the greedy nearest-door heuristic as travel's
/// cross-room brain). Every explored cell carries a roomId, and adjacent
/// cells with different non-zero roomIds are connected by a real door —
/// verified empirically 2026-04-27 and load-bearing in the nav browser since.
///
/// So the floor IS a graph the game hands us: nodes = roomIds, edges = door
/// boundaries. BFS gives the door SEQUENCE to the target's room — "through
/// this door, across that room, through the next" — the thing the wall-mesh
/// pathfinder could never derive on floors where the engine streams almost no
/// collision geometry (Void Quest gave 48 triangles, live 2026-07-06).
///
/// The caller keeps a per-travel set of DEAD edges (doors that failed to
/// cross); BFS avoids them, so a dead-end room is exited back through its
/// entry door and another path is tried — systematic backtracking.
/// </summary>
internal static class RoomGraph
{
    internal struct Hop
    {
        public float X, Z;        // boundary crossing point (world)
        public int EdgeKey;       // stable id for the (roomA,roomB) door edge
        public long CrossKey;     // id of THIS crossing point on that edge
        public ushort ToRoom;     // roomId on the far side
    }

    private static int EdgeKeyOf(ushort a, ushort b)
        => a < b ? (a << 16) | b : (b << 16) | a;

    // A room border spans several coarse cells but only ONE is the real gap —
    // failed crossings kill the POINT, not the whole edge (killing the edge
    // stranded the walker in the interactables room, 2026-07-06: it declared
    // the only exit dead and ground the west wall for every later walk).
    private static long CrossKeyOf(int edgeKey, int pointIdx)
        => ((long)edgeKey << 8) | (uint)Math.Min(pointIdx, 255);

    /// <summary>Room id at a world point, with a ring search so a spot on a
    /// door/boundary cell still resolves to a neighbouring room. maxRad is in
    /// COARSE cells (~1250u each): 2 suits the PLAYER (can stand mid-boundary),
    /// but a TARGET must use 1 — a chest against a wall "resolved" through the
    /// wall into the next room two cells over, and the planner walked AWAY
    /// from a chest one step ahead (user 2026-07-06).</summary>
    internal static bool TryRoomAt(float wx, float wz, out ushort room, int maxRad = 2)
    {
        room = 0;
        if (!MinimapTracker.WorldToCell(wx, wz, out int r0, out int c0)) return false;
        for (int rad = 0; rad <= maxRad; rad++)
            for (int dr = -rad; dr <= rad; dr++)
                for (int dc = -rad; dc <= rad; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != rad) continue;
                    if (!MinimapTracker.ReadCell(r0 + dr, c0 + dc, out var cell)) continue;
                    if (cell.Flag == 0 || cell.RoomId == 0) continue;
                    room = cell.RoomId;
                    return true;
                }
        return false;
    }

    /// <summary>
    /// BFS the room graph from (px,pz) to (tx,tz), skipping <paramref name="deadCrossings"/>
    /// (an edge is only unusable once ALL its crossing points are dead).
    /// True with EMPTY hops = same room (nothing to cross — an intra-room
    /// problem the caller solves another way). False = either end doesn't
    /// resolve to an explored room, or no path avoids the dead crossings.
    /// </summary>
    internal static bool TryDoorPlan(float px, float pz, float tx, float tz,
        HashSet<long> deadCrossings, out List<Hop> hops)
    {
        hops = new List<Hop>();
        if (!TryRoomAt(px, pz, out ushort pRoom, maxRad: 2)) return false;
        if (!TryRoomAt(tx, tz, out ushort tRoom, maxRad: 1)) return false;
        if (pRoom == tRoom) return true;

        // Snapshot the coarse map + collect edges with their boundary points
        // (midpoint of each adjacent cross-room cell pair).
        var edgePts = new Dictionary<int, List<(float x, float z)>>();
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
            {
                if (!MinimapTracker.ReadCell(r, c, out var a) || a.Flag == 0 || a.RoomId == 0) continue;
                foreach (var (dr, dc) in new[] { (1, 0), (0, 1) })
                {
                    if (!MinimapTracker.ReadCell(r + dr, c + dc, out var b) || b.Flag == 0 || b.RoomId == 0) continue;
                    if (b.RoomId == a.RoomId) continue;
                    if (!MinimapTracker.CellToWorld(r, c, out float ax, out float az)) continue;
                    if (!MinimapTracker.CellToWorld(r + dr, c + dc, out float bx, out float bz)) continue;
                    int key = EdgeKeyOf(a.RoomId, b.RoomId);
                    if (!edgePts.TryGetValue(key, out var pts)) edgePts[key] = pts = new List<(float, float)>();
                    pts.Add(((ax + bx) / 2f, (az + bz) / 2f));
                }
            }

        // Stable point order (CrossKey indices must mean the same thing every
        // plan), then adjacency from edges that still have a LIVE point.
        var nbrs = new Dictionary<ushort, HashSet<ushort>>();
        foreach (var kv in edgePts)
        {
            kv.Value.Sort((u, v) => u.x != v.x ? u.x.CompareTo(v.x) : u.z.CompareTo(v.z));
            bool alive = false;
            for (int i = 0; i < kv.Value.Count && !alive; i++)
                if (!deadCrossings.Contains(CrossKeyOf(kv.Key, i))) alive = true;
            if (!alive) continue;
            ushort ea = (ushort)(kv.Key >> 16), eb = (ushort)(kv.Key & 0xFFFF);
            if (!nbrs.TryGetValue(ea, out var la)) nbrs[ea] = la = new HashSet<ushort>();
            if (!nbrs.TryGetValue(eb, out var lb)) nbrs[eb] = lb = new HashSet<ushort>();
            la.Add(eb); lb.Add(ea);
        }

        if (!nbrs.ContainsKey(pRoom) || !nbrs.ContainsKey(tRoom)) return false;

        var prev = new Dictionary<ushort, ushort>();
        var q = new Queue<ushort>();
        q.Enqueue(pRoom); prev[pRoom] = pRoom;
        while (q.Count > 0)
        {
            ushort cur = q.Dequeue();
            if (cur == tRoom) break;
            if (!nbrs.TryGetValue(cur, out var outs)) continue;
            foreach (ushort nxt in outs)
            {
                if (prev.ContainsKey(nxt)) continue;
                prev[nxt] = cur;
                q.Enqueue(nxt);
            }
        }
        if (!prev.ContainsKey(tRoom)) return false;

        var roomPath = new List<ushort>();
        for (ushort cur = tRoom; ; cur = prev[cur])
        {
            roomPath.Add(cur);
            if (cur == pRoom) break;
        }
        roomPath.Reverse();

        // Emit crossing points: per edge, the best LIVE boundary point on the
        // "via" path (closest to where we are, then onward to the target).
        float cx = px, cz = pz;
        for (int i = 0; i + 1 < roomPath.Count; i++)
        {
            int key = EdgeKeyOf(roomPath[i], roomPath[i + 1]);
            if (!edgePts.TryGetValue(key, out var pts) || pts.Count == 0) return false;
            float bestScore = float.MaxValue; float bx = 0, bz = 0; int bestIdx = -1;
            for (int j = 0; j < pts.Count; j++)
            {
                if (deadCrossings.Contains(CrossKeyOf(key, j))) continue;
                var (x, z) = pts[j];
                float s = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz))
                        + MathF.Sqrt((tx - x) * (tx - x) + (tz - z) * (tz - z));
                if (s < bestScore) { bestScore = s; bx = x; bz = z; bestIdx = j; }
            }
            if (bestIdx < 0) return false;
            hops.Add(new Hop { X = bx, Z = bz, EdgeKey = key, CrossKey = CrossKeyOf(key, bestIdx), ToRoom = roomPath[i + 1] });
            cx = bx; cz = bz;
        }
        return true;
    }
}
