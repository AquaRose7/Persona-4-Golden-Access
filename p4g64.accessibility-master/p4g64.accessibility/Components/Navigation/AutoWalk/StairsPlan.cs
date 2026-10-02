namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// Builds the world-waypoint list for the stairs auto-walk (v1):
/// <see cref="GridRouter.FindNearestStairs"/> gives the stairs cell, then a fine
/// <see cref="GridWalk"/> cell A* traces a CENTERLINE path player→stairs over the
/// minimap grid (edge-bit OR same-room connectivity). The waypoints are cell
/// CENTERS, so the route bends AT each corner cell — no corner-cutting, and it
/// walks down the middle of the corridor, never hugging a wall.
///
/// For a cross-room walk it STOPS at the door INTO the stairs' room (the first
/// path cell whose roomId is the stairs' room) — v1 arrival "check nearest door",
/// the player takes the last step. If already in the stairs' room it drives to the
/// stairs cell itself ("the stairs are near").
///
/// <paramref name="blocked"/> holds cell keys the driver has marked impassable this
/// walk (a genuine obstacle it bumped); A* routes around them or, if none exists,
/// returns NoRoute so the driver gives up honestly instead of grinding.
///
/// History (2026-07-16): an earlier version used RoomGraph door-hops as the
/// waypoints; too sparse — the straight line between the pre-corner and post-corner
/// hop cut the inside wall and the walk wedged at every turn (live log). The fine
/// centerline path fixes that. The connectivity two-rule fix (edge OR roomId) is in
/// <see cref="GridWalk.Connected"/>.
/// </summary>
internal static class StairsPlan
{
    internal enum Result { Ok, NoStairs, NoRoute }

    private const float DoorStandoff = 220f;    // crossing waypoints sit this far in FRONT of / beyond the gap
    private const float DoorOnEdgeUnits = 300f;     // a crossing's door lies within this of the shared edge line
    private const float DoorAlongEdgeUnits = 700f;  // ...and within this along it (edge half-length 600 + slack)

    /// <summary>Stairs plan (v1 semantics): route to the nearest stairs, stopping at
    /// the door INTO the stairs' room. Thin wrapper over <see cref="TryPlanTo"/>.</summary>
    internal static Result TryPlan(float px, float pz, HashSet<int> blocked,
        out List<(float x, float z)> waypoints, out bool sameRoom, out float stairsX, out float stairsZ)
    {
        waypoints = new List<(float, float)>();
        sameRoom = false; stairsX = stairsZ = 0f;
        if (!GridRouter.FindNearestStairs(px, pz, out stairsX, out stairsZ)) return Result.NoStairs;
        return TryPlanTo(px, pz, stairsX, stairsZ, blocked, stopAtTargetRoomDoor: true,
                         doorTarget: false, out waypoints, out sameRoom);
    }

    /// <summary>Does the door at (dx,dz) sit ON the boundary between cells A and B? Score (lower =
    /// better) or MaxValue. Since the 2026-09-29 grid-center fix, cell centers are exact, so a real
    /// crossing door lies on the shared edge line (≤300u off it) within the edge's span (≤700u
    /// along it). The old "nearest door within 900u of the midpoint" rule compensated for the
    /// 333u grid offset and ADOPTED DOORS ON THE WRONG WALL (Marukyu 2F: a (5,1)→(6,1) crossing
    /// took the east door at (1800,6000) → five NoRoute retreats → "Couldn't get through").</summary>
    private static float CrossingDoorScore(float dx, float dz, float cax, float caz, float cbx, float cbz)
    {
        float mx = (cax + cbx) * 0.5f, mz = (caz + cbz) * 0.5f;
        bool zCross = MathF.Abs(cbz - caz) > MathF.Abs(cbx - cax);
        float across = zCross ? MathF.Abs(dz - mz) : MathF.Abs(dx - mx);   // off the edge line
        float along  = zCross ? MathF.Abs(dx - mx) : MathF.Abs(dz - mz);   // along the edge
        if (across > DoorOnEdgeUnits || along > DoorAlongEdgeUnits) return float.MaxValue;
        return across + along;
    }

    /// <summary>ROOM INTERIOR (2026-09-29, Heaven): path cell i sits INSIDE a multi-cell room when
    /// both its path neighbours share its roomId — skip its center so the walk crosses the room in a
    /// straight line from where it enters to where it leaves. Heaven's walkways are DIAGONAL bands
    /// inside square room footprints: visiting every cell center zig-zagged the player into the
    /// bridge ledges (Paradise #1: five NoRoute retreats at one center). 1-cell corridor rooms still
    /// turn at their centers (never interior), and stairs blocks keep every center (their one
    /// roomId hides internal walls — GridWalk.Connected).</summary>
    // Per walk thread: the drive turns room smoothing OFF (cell-by-cell for the rest of the walk)
    // after its first stall on a smoothed plan — a straight line across a room can hit furniture
    // (Secret Lab B1F consoles: five NoRoute retreats on a diagonal across a 3×3 room).
    [ThreadStatic] internal static bool NoRoomSmoothing;
    [ThreadStatic] internal static bool LastPlanSmoothed;

    private static bool IsRoomInterior(List<(int r, int c)> path, int i)
    {
        if (NoRoomSmoothing) return false;
        if (i <= 0 || i >= path.Count - 1) return false;
        ushort rid = GridWalk.RoomIdOf(path[i].r, path[i].c);
        if (rid == 0) return false;
        if (GridWalk.RoomIdOf(path[i - 1].r, path[i - 1].c) != rid || GridWalk.RoomIdOf(path[i + 1].r, path[i + 1].c) != rid) return false;
        return !(MinimapTracker.ReadCell(path[i].r, path[i].c, out var cell) && GridRouter.IsStairSprite(cell.Sprite));
    }

    /// <summary>Unwalkable cell → the walkable 8-neighbour whose centre is nearest (x,z).</summary>
    private static void SnapToWalkable(float x, float z, ref int r, ref int c)
    {
        if (GridWalk.IsWalkable(r, c)) return;
        int br = r, bc = c; float best = float.MaxValue;
        for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nr = r + dr, nc = c + dc;
                if (!GridWalk.IsWalkable(nr, nc)) continue;
                if (!MinimapTracker.CellToWorld(nr, nc, out float cx, out float cz)) continue;
                float d = (cx - x) * (cx - x) + (cz - z) * (cz - z);
                if (d < best) { best = d; br = nr; bc = nc; }
            }
        if (br != r || bc != c) Utils.Log($"[StairsPlan] cell ({r},{c}) not walkable — snapped to ({br},{bc})");
        r = br; c = bc;
    }

    /// <summary>General door-woven centerline plan player→(tx,tz) — the v2 drive's
    /// planner for EVERY dungeon target (stairs/chests/doors/interactables/marks/
    /// shadows). stopAtTargetRoomDoor = the stairs semantic (halt at the door into
    /// the target's room); otherwise the final waypoint is the target's EXACT spot.
    /// doorTarget = (tx,tz) IS a door: the plan ends CENTERED in front of it
    /// instead of weaving through or aiming at the frame-embedded centroid.</summary>
    internal static Result TryPlanTo(float px, float pz, float tx, float tz, HashSet<int> blocked,
        bool stopAtTargetRoomDoor, bool doorTarget,
        out List<(float x, float z)> waypoints, out bool sameRoom)
    {
        waypoints = new List<(float, float)>();
        sameRoom = false;

        if (!MinimapTracker.WorldToCell(px, pz, out int r0, out int c0)) return Result.NoRoute;
        if (!MinimapTracker.WorldToCell(tx, tz, out int r1, out int c1)) return Result.NoRoute;
        // A corridor tile can straddle its cell's edge, so the body may stand over the
        // NEIGHBOURING cell, which reads as boundary → "No route to the stairs." from inside a
        // real corridor (Bathhouse B1, 2026-09-29). Snap to the nearest walkable neighbour.
        SnapToWalkable(px, pz, ref r0, ref c0);
        SnapToWalkable(tx, tz, ref r1, ref c1);

        ushort targetRoom = GridWalk.RoomIdOf(r1, c1);
        ushort playerRoom = GridWalk.RoomIdOf(r0, c0);
        sameRoom = targetRoom != 0 && targetRoom == playerRoom;

        // ★ PREFAB COLLISION FIRST (2026-09-30): the fine planner walks the real corridors of the
        // placed room prefabs; it refuses floors it cannot vouch for → the coarse plan below.
        LastPlanSmoothed = false;
        TileGrid.LastPlanFine = false;
        if (!stopAtTargetRoomDoor && TileGrid.TryPlan(px, pz, tx, tz, blocked, doorTarget, out var fine))
        {
            TileGrid.LastPlanFine = true;
            waypoints = fine;
            return Result.Ok;
        }
        // Reachable ONLY through a locked door: never hand this to the coarse plan (it keeps the direct
        // route through the lock and grinds the door) — the caller tells the player.
        if (TileGrid.LastLockedBlock) return Result.NoRoute;

        var path = new List<(int r, int c)>();
        if (!GridWalk.TryCellPath(r0, c0, r1, c1, path, blocked)) return Result.NoRoute;

        // LOCKED DOORS (2026-09-04, Bath #3: six players parked at the locked west door of the
        // stairs room): a crossing whose door is locked (DungeonNav.IsDoorLocked — the game's
        // own lock bits) is a WALL for this plan. Block the far cell and re-plan, up to a few
        // rounds; if nothing else routes, keep the original path (the drive's door ladder then
        // reports what it can).
        {
            var lockedDoors = new List<(float x, float z)>();
            try { foreach (var d in DungeonNav.Doors()) if (DungeonNav.IsDoorLocked(d.x, d.z)) lockedDoors.Add(d); } catch { }
            if (lockedDoors.Count > 0)
            {
                var added = new List<int>();
                for (int round = 0; round < 6; round++)
                {
                    int bad = -1;
                    for (int i = 0; i + 1 < path.Count && bad < 0; i++)
                    {
                        var (ar, ac) = path[i]; var (br, bc) = path[i + 1];
                        if (GridWalk.RoomIdOf(ar, ac) == GridWalk.RoomIdOf(br, bc)) continue;
                        if (!MinimapTracker.CellToWorld(ar, ac, out float cax, out float caz)) continue;
                        if (!MinimapTracker.CellToWorld(br, bc, out float cbx, out float cbz)) continue;
                        foreach (var (lx, lz) in lockedDoors)
                            if (CrossingDoorScore(lx, lz, cax, caz, cbx, cbz) < float.MaxValue) { bad = i; break; }
                    }
                    if (bad < 0) break;
                    var (fr, fc) = path[bad + 1];
                    int key = fr * MinimapTracker.COLS + fc;
                    Utils.Log($"[StairsPlan] locked door on the route between cell ({path[bad].r},{path[bad].c}) and ({fr},{fc}) — routing around");
                    if (!blocked.Add(key)) break;
                    added.Add(key);
                    var alt = new List<(int r, int c)>();
                    if (GridWalk.TryCellPath(r0, c0, r1, c1, alt, blocked)) { path = alt; continue; }
                    // No way around: give the blocks back and keep the original route.
                    foreach (var k in added) blocked.Remove(k);
                    Utils.Log("[StairsPlan] no route around the locked door — keeping the direct route");
                    path.Clear();
                    GridWalk.TryCellPath(r0, c0, r1, c1, path, blocked);
                    break;
                }
            }
        }

        // Cross-room stairs: stop at the FIRST path cell inside the target's room.
        // Everything else: go all the way to the target cell.
        int cut = path.Count - 1;
        if (stopAtTargetRoomDoor && !sameRoom)
            for (int i = 1; i < path.Count; i++)
                if (GridWalk.RoomIdOf(path[i].r, path[i].c) == targetRoom) { cut = i; break; }

        // Post-reroute starts often leave the player wedged OFF the centerline —
        // driving straight at a far next-cell center from there cuts through
        // clutter and stalls at wp0 (log-proven, 07-18). Re-center on the
        // player's own cell first when meaningfully off it.
        if (MinimapTracker.CellToWorld(path[0].r, path[0].c, out float p0x, out float p0z))
        {
            float odx = p0x - px, odz = p0z - pz;
            if (odx * odx + odz * odz > 300f * 300f) waypoints.Add((p0x, p0z));
        }

        // Live door positions, snapshotted once (scene walk — AV-guarded).
        var doors = new List<(float x, float z)>();
        try { foreach (var d in DungeonNav.Doors()) doors.Add(d); } catch { }

        // Waypoints = cell centers, but every ROOM-BOUNDARY crossing is WOVEN
        // THROUGH ITS DOOR: cell centers sit up to half a cell off the door's gap,
        // so a center-to-center leg crossed the doorway WALL and wedged the player
        // on the frame (Heaven, 2026-07-17 screenshot+log: door z=21600, leg
        // z=21370). Insert a point centered in FRONT of the gap and one beyond it
        // (door axis from the actor transform), so the drive threads the middle.
        // The FINAL crossing into the stairs' room keeps v1 semantics — stop AT
        // the door, but now CENTERED on it instead of at the offset cell.
        for (int i = 0; i < cut; i++)
        {
            var (ar, ac) = path[i];
            var (br, bc) = path[i + 1];
            bool interior = i > 0 && IsRoomInterior(path, i);
            if (interior) LastPlanSmoothed = true;
            if (i > 0 && !interior && MinimapTracker.CellToWorld(ar, ac, out float awx, out float awz))
                waypoints.Add((awx, awz));

            if (GridWalk.RoomIdOf(ar, ac) == GridWalk.RoomIdOf(br, bc)) continue;
            if (!MinimapTracker.CellToWorld(ar, ac, out float cax, out float caz)) continue;
            if (!MinimapTracker.CellToWorld(br, bc, out float cbx, out float cbz)) continue;
            int di = -1; float bestD = float.MaxValue;
            for (int d = 0; d < doors.Count; d++)
            {
                float sc = CrossingDoorScore(doors[d].x, doors[d].z, cax, caz, cbx, cbz);
                if (sc < bestD) { bestD = sc; di = d; }
            }
            if (di < 0) continue;                       // doorless archway — plain leg is fine
            var (dx, dz) = doors[di];
            if (!DungeonNav.TryDoorAxis(dx, dz, out float nx, out float nz)) { nx = cbx - cax; nz = cbz - caz; }
            float nl = MathF.Sqrt(nx * nx + nz * nz);
            if (nl < 1e-3f) continue;
            nx /= nl; nz /= nl;
            if ((cax - dx) * nx + (caz - dz) * nz < 0f) { nx = -nx; nz = -nz; }   // n̂ → approach side
            waypoints.Add((dx + nx * DoorStandoff, dz + nz * DoorStandoff));      // centered in front of the gap
            if (stopAtTargetRoomDoor && !sameRoom && i + 1 == cut) return Result.Ok;   // stop AT the target-room door
            if (doorTarget && (dx - tx) * (dx - tx) + (dz - tz) * (dz - tz) < 500f * 500f)
                return Result.Ok;                        // this door IS the target — stop centered in front
            waypoints.Add((dx - nx * DoorStandoff, dz - nz * DoorStandoff));      // beyond the gap, still centered
        }

        if (stopAtTargetRoomDoor)
        {
            if (MinimapTracker.CellToWorld(path[cut].r, path[cut].c, out float lwx, out float lwz))
                waypoints.Add((lwx, lwz));
        }
        else
        {
            // The true final waypoint is the TARGET ITSELF, not its cell center
            // (a chest/door/mark can sit half a cell off the center).
            waypoints.Add((tx, tz));
        }
        if (waypoints.Count == 0 && MinimapTracker.CellToWorld(r1, c1, out float ex2, out float ez2))
            waypoints.Add((ex2, ez2));   // player adjacent to the goal cell
        return Result.Ok;
    }
}
