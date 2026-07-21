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

    private const float DoorSnapUnits = 900f;   // a room-boundary crossing adopts a door within this range
                                                // (650 missed a door offset along a wide Heaven cell edge —
                                                // cells are 1200u, so a boundary door can sit ~600 from the
                                                // crossing midpoint; 2026-07-18 give-up beside the left door)
    private const float DoorStandoff = 220f;    // crossing waypoints sit this far in FRONT of / beyond the gap

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

        ushort targetRoom = GridWalk.RoomIdOf(r1, c1);
        ushort playerRoom = GridWalk.RoomIdOf(r0, c0);
        sameRoom = targetRoom != 0 && targetRoom == playerRoom;

        var path = new List<(int r, int c)>();
        if (!GridWalk.TryCellPath(r0, c0, r1, c1, path, blocked)) return Result.NoRoute;

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
            if (i > 0 && MinimapTracker.CellToWorld(ar, ac, out float awx, out float awz))
                waypoints.Add((awx, awz));

            if (GridWalk.RoomIdOf(ar, ac) == GridWalk.RoomIdOf(br, bc)) continue;
            if (!MinimapTracker.CellToWorld(ar, ac, out float cax, out float caz)) continue;
            if (!MinimapTracker.CellToWorld(br, bc, out float cbx, out float cbz)) continue;
            float mx = (cax + cbx) * 0.5f, mz = (caz + cbz) * 0.5f;
            int di = -1; float bestD2 = DoorSnapUnits * DoorSnapUnits;
            for (int d = 0; d < doors.Count; d++)
            {
                float ex = doors[d].x - mx, ez = doors[d].z - mz, d2 = ex * ex + ez * ez;
                if (d2 < bestD2) { bestD2 = d2; di = d; }
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
