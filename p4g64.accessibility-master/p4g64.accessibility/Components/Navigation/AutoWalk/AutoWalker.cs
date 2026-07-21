using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// Auto-walk (v5): <b>Backspace</b> in the DungeonNav browser walks the player
/// to the selected target — or, for Shadows, runs the stealth HUNT (P4).
/// Backspace again cancels.
///
/// ### Steering core (<see cref="Steerer"/>, memory/camera_struct_found.md)
/// Movement is camera-relative and the camera is readable live
/// (<see cref="FieldTracker.CameraForward3D"/>). Each poll the Steerer PWMs
/// between the two 45° WASD combos flanking the exact bearing. The read's
/// sign convention is NOT stable across reads — the in-walk auto-flip
/// misfired on turn-around pivots (two opposite flips in one session, live
/// 2026-06-11) — so each walk starts with a deliberate ~300ms W-press
/// CALIBRATION that measures the sign unambiguously, then locks it.
/// Velocity feedback classifies wall slides (50°+ off bearing) and grinding
/// (keys held, barely moving); both trigger blocked recovery.
///
/// ### Walk (chests/doors/stairs)
/// A* route → wall-pushed cell centers → string-pulled against the real wall
/// mesh (<see cref="WallMesh"/>) → corridor-centering bias while walking →
/// arrival on the game's CHECK prompt (radius fallback). Doors stage an
/// approach point in front of the frame. Blocked → recenter, re-route once,
/// then abort with steps remaining ("right there, a step ahead" when
/// physically at the target without a prompt).
///
/// ### Hunt (Shadows — user spec 2026-06-11)
/// Track the shadow live (it moves), route to a point BEHIND it, and:
/// - while far: approach like a normal walk, re-planning when it relocates;
/// - if it faces us inside stealth range: freeze, "Hold, it's looking" —
///   but only briefly (they can spot you even standing behind them), then
///   commit to the CHARGE — face-to-face at worst, per user;
/// - inside strike range: straight pursuit at its live position until the
///   battle triggers (contact needs to be REALLY close — no stop radius).
/// Battle start ends the hunt with "Engaged!"; losing the shadow, the floor,
/// or 6s of failed strike ends it honestly.
/// </summary>
internal class AutoWalker
{
    private const int PollMs = 50;
    private const float WorldPerStep = 250f;

    // Scan codes (set 1)
    private const byte SC_W = 0x11, SC_A = 0x1E, SC_S = 0x1F, SC_D = 0x20;

    private const int VK_ESCAPE = 0x1B;
    private static readonly int[] CancelVKeys = { VK_ESCAPE, 0x25, 0x26, 0x27, 0x28 }; // Esc + arrows

    // Arrival (CHECK prompt is primary; radii deliberately tight).
    private const float FinalTolUnits = 160f;
    private const float DoorTolUnits = 300f;       // door centroid sits inside the wall
    private const int DoorPushMs = 2500;           // straight-into-the-door push budget
    private const int DoorThroughMs = 3000;        // max time ignoring the frame while passing through
    private const float StallArriveUnits = 480f;   // blocked this close = at the target

    private const float TurnSpeechDeg = 30f;
    private const int NoProgressMs = 2000;
    private const int CountdownStepInterval = 5;

    // Hunt: autonomous approach-and-strike (Backspace on a Shadow). Walks to
    // the shadow's back and swings; commits face-to-face if it keeps turning.
    private const float StrikeRange = 950f;        // inside this: drive at the shadow
    private const float StealthRange = 2600f;      // inside this: "Closing in"
    private const float BehindOffset = 380f;       // aim point behind the shadow
    private const int CommitMs = 3500;             // circling-to-behind stalls → swing anyway
    private const int DangerCommitMs = 900;        // close + it's facing us → commit fast
    private const float ShadowTrackRange = 1600f;  // re-acquire radius per poll
    // The encounter ADVANTAGE comes from landing a weapon swing (action
    // button), not from body contact (user, 2026-06-11).
    private const byte SC_ENTER = 0x1C;
    // The weapon lunge has a SWEET SPOT, not just a max range: live hits
    // clustered at d=410-451, while d=445+ (just short of reach) AND d=195
    // (so close the lunge overshoots) both whiffed (2026-06-11). Swing only
    // inside the band; below it, keep driving in so contact/next-poll lands.
    private const byte SC_FORWARD = 0x11;           // W — lunge into the swing
    private const float SwingNear = 230f;
    private const float SwingFar = 470f;
    private const float SwingFaceDeg = 30f;         // tighter = cleaner connect
    private const int SwingIntervalMs = 450;

    private static volatile bool _active;
    private static volatile bool _cancelRequested;
    private static long _startTick;

    // Camera-forward sign, measured by CalibrateSign at each walk start.
    private static float _camSign = 1f;

    internal static bool IsActive => _active;
    internal static long AgeMs => Environment.TickCount64 - _startTick;

    /// <summary>Request a stop from outside (DungeonNav's Backspace-while-walking).</summary>
    internal static void Cancel() => _cancelRequested = true;

    /// <summary>Start walking to a world target. One walk at a time.</summary>
    internal static void Start(string label, float tx, float tz, (float x, float z)? approach = null)
        => Launch(() => Walk(label, tx, tz, approach));

    /// <summary>Start the stealth hunt on the shadow nearest (ix, iz).</summary>
    internal static void StartHunt(float ix, float iz)
        => Launch(() => Hunt(ix, iz));

    /// <summary>Outcome of one discrete step (see <see cref="StepOneCell"/>).</summary>
    internal enum StepResult { Moved, Blocked, Refused, Cancelled }

    /// <summary>
    /// Step the player a short distance (<paramref name="cellFraction"/> × one
    /// minimap cell) along the pure cardinal world direction (dirX,dirZ), then stop
    /// — the primitive behind discrete stepping. Drives a CONSTANT cardinal bearing
    /// (never toward a point) so the path is straight, and the Steerer re-aims every
    /// poll off the LIVE camera, so a camera swing (e.g. a door's auto-face) is
    /// handled automatically. If a swing ever inverts the camera→movement mapping,
    /// the step detects backward motion and flips the sign mid-step; a fast swing
    /// pauses steering until it settles. <paramref name="onDone"/>(result, realRow,
    /// realCol) fires on the walker thread. Single-flighted via <see cref="Launch"/>
    /// — gate the caller on <see cref="IsActive"/>.
    /// </summary>
    internal static void Step(float dirX, float dirZ, float cellFraction,
                              Action<StepResult, int, int> onDone)
        => Launch(() => StepBody(dirX, dirZ, cellFraction, onDone));

    /// <summary>
    /// Auto-walk to the next-floor STAIRS: follow A*-routed legs over EXPLORED cells
    /// toward the stairs sprite; when a door blocks, go to it and OPEN it; when the path
    /// isn't revealed yet, reach the frontier closest to the stairs (revealing more),
    /// then re-plan — until it arrives. Never bee-lines into walls. One at a time (Launch).
    /// </summary>
    internal static void TravelToStairs() => Launch(() => TravelLoop(
        (px, pz) => { bool ok = GridRouter.FindNearestStairs(px, pz, out float tx, out float tz); return (ok, tx, tz); },
        DoorTolUnits, "Heading to the stairs.", "Arrived. Find the door to go to the next floor."),
        stairsTarget: true);

    /// <summary>Travel to a FIXED point (an Event mark) using the full explore/route/open-doors loop
    /// — robust through doors and around walls, unlike the one-shot Walk that bee-lines and gives up.
    /// For tricky scripted-floor marks (e.g. the Bathhouse door whose path A* can't route in one shot).
    /// Chain several marks (browse nearest-first, Backspace each) to thread a winding route.</summary>
    internal static void TravelTo(string label, float tx, float tz) => Launch(() => TravelLoop(
        (px, pz) => (true, tx, tz), DoorTolUnits, $"Traveling to {label}.", $"Reached {label}."));

    // Travel (explore + open doors) to a chest, then EASE ONTO the openable spot — drive in
    // until the game's CHECK prompt shows (like the old chest walk) so the player can open it.
    internal static void TravelToChest(float cx, float cz) => Launch(() =>
    {
        if (!TravelLoop((px, pz) => (true, cx, cz), FinalTolUnits, "Heading to the chest.", null)) return;
        float pitch = GridRouter.CellPitch(); if (pitch <= 0) pitch = WorldPerStep * 5f;
        long until = Environment.TickCount64 + 2500;
        bool chk = FieldTracker.CheckPromptActive;
        while (!chk && Environment.TickCount64 < until && !_cancelRequested && Utils.GameHasFocus())
        {
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (float.IsNaN(px)) break;
            if ((cx - px) * (cx - px) + (cz - pz) * (cz - pz) <= (pitch * 0.18f) * (pitch * 0.18f)) break;   // basically on it
            DriveToward(cx, cz, pitch * 0.12f, 300);
            chk = FieldTracker.CheckPromptActive;
        }
        ReleaseAll();
        Speech.Say(chk ? "At the chest. Press to open." : "At the chest.", true);
    });

    // Travel to NEAR a (possibly distant) shadow, opening doors on the way, then hand off to
    // the back-strike Hunt — so the hunt works on long-range enemies across the floor.
    // The shadow MOVES while we travel: re-acquire the nearest live shadow to the last
    // known spot each poll (a fixed stale target walked to empty floor, 2026-07-06).
    internal static void TravelToShadow(float sx, float sz) => Launch(() =>
    {
        float near = GridRouter.CellPitch(); if (near <= 0) near = WorldPerStep * 5f; near *= 1.6f;
        float cx = sx, cz = sz;
        bool ok = TravelLoop((px, pz) =>
        {
            float best = ShadowTrackRange * ShadowTrackRange;
            float nx = cx, nz = cz;
            foreach (var (x, z, _, _) in DungeonNav.ShadowsWithFacing())
            {
                float dx = x - cx, dz = z - cz, d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; nx = x; nz = z; }
            }
            cx = nx; cz = nz;
            return (true, cx, cz);
        }, near, "Heading to the shadow.", null);
        if (ok) Hunt(cx, cz);
    });

    // Per-cancel-key arming. A cancel key (Esc/arrow) HELD at launch must NOT
    // cancel — the blind player navigates with the arrow keys, so one is
    // routinely down when Backspace starts a walk, which cancelled it on the
    // first poll every time (live 2026-06-11). A key only arms (can cancel)
    // after it's been released once; then a FRESH press during the walk =
    // genuine manual override.
    private static readonly bool[] _cancelArmed = new bool[5];

    private static void Launch(Action body, bool stairsTarget = false)
    {
        if (_active) return;
        _active = true;
        _cancelRequested = false;
        // STAIRS FOOTPRINT MASK exemption: only a stairs walk may route into a
        // stairs cell — every other walk treats the staircase as the solid
        // object it physically is (set BEFORE ClearLearned so the forced
        // rebuild composes with the right mask).
        DungeonGrid.StairsAreTarget = stairsTarget;

        // Arm only the cancel keys that are UP right now.
        for (int i = 0; i < CancelVKeys.Length; i++)
            _cancelArmed[i] = !IsKeyDown(CancelVKeys[i]);
        _startTick = Environment.TickCount64;
        new Thread(() =>
        {
            try { body(); }
            catch (Exception ex)
            {
                Log($"[AutoWalker] error: {ex.GetType().Name}: {ex.Message}");
                Speech.Say("Auto-walk failed.", true);
            }
            finally { ReleaseAll(); DungeonGrid.StairsAreTarget = false; _active = false; }
        }) { IsBackground = true, Name = "AutoWalker" }.Start();
    }

    /// <summary>TEMP [StairsDiag] (remove after auto-walk verification): log the
    /// planned stairs route from the player's current position so we can read it
    /// back from the log without driving. Logs the RoomGraph hops AND a direct
    /// player→stairs cell A* so we can see whether a zigzag is in the raw A* or
    /// in the hop stitching. Exercises GridWalk + RoomGraph + StairsPlan.</summary>
    internal static void DiagStairsPlan()
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) { Log("[StairsDiag] position unavailable"); return; }
        if (!GridRouter.FindNearestStairs(px, pz, out float sx, out float sz)) { Log("[StairsDiag] no stairs on map"); return; }
        Log($"[StairsDiag] pos=({px:F0},{pz:F0}) stairs=({sx:F0},{sz:F0})");

        // (1) RoomGraph hops (the cross-room door sequence).
        if (RoomGraph.TryDoorPlan(px, pz, sx, sz, new HashSet<long>(), out var hops))
        {
            Log($"[StairsDiag] hops={hops.Count}");
            for (int i = 0; i < hops.Count; i++)
                Log($"[StairsDiag]   hop{i}=({hops[i].X:F0},{hops[i].Z:F0}) toRoom={hops[i].ToRoom}");
        }
        else Log("[StairsDiag] no door plan");

        // (2) Direct player→stairs cell A* + RAW GRID DUMP of the route's bounding
        // box so we can see WHY the fine path is disconnected. Per cell:
        // "flag/edgeLowNibble/roomId" (flag 0 void,1 walk,2 boundary; edge low nibble
        // 1=S 2=E 4=N 8=W open). '@'=player cell, '*'=stairs cell.
        if (MinimapTracker.WorldToCell(px, pz, out int r0, out int c0) &&
            MinimapTracker.WorldToCell(sx, sz, out int r1, out int c1))
        {
            var cells = new List<(int r, int c)>();
            if (GridWalk.TryCellPath(r0, c0, r1, c1, cells))
                Log($"[StairsDiag] directCells n={cells.Count}");
            else Log($"[StairsDiag] no direct cell path ({r0},{c0})->({r1},{c1})");

            int rlo = Math.Max(0, Math.Min(r0, r1) - 2), rhi = Math.Min(MinimapTracker.ROWS - 1, Math.Max(r0, r1) + 2);
            int clo = Math.Max(0, Math.Min(c0, c1) - 2), chi = Math.Min(MinimapTracker.COLS - 1, Math.Max(c0, c1) + 2);
            Log($"[StairsDiag] GRID rows {rlo}..{rhi} cols {clo}..{chi} (player=({r0},{c0}) stairs=({r1},{c1}))");
            var raw = new byte[MinimapTracker.CELL_SIZE];
            for (int r = rlo; r <= rhi; r++)
            {
                var sb = new System.Text.StringBuilder($"[StairsDiag] r{r,2}: ");
                for (int c = clo; c <= chi; c++)
                {
                    string mark = (r == r0 && c == c0) ? "@" : (r == r1 && c == c1) ? "*" : " ";
                    if (MinimapTracker.ReadCellRawBytes(r, c, raw))
                        sb.Append($"{mark}{raw[0]}/{raw[0x0A] & 0x0F:X}/{(raw[2] | (raw[3] << 8)):X4} ");
                    else sb.Append($"{mark}--/-/---- ");
                }
                Log(sb.ToString());
            }
        }

        // (3) The actual plan waypoints.
        var res = StairsPlan.TryPlan(px, pz, new HashSet<int>(), out var wps, out bool same, out _, out _);
        Log($"[StairsDiag] plan result={res} sameRoom={same} waypoints={wps.Count}");
        for (int i = 0; i < wps.Count; i++) Log($"[StairsDiag]   wp{i}=({wps[i].x:F0},{wps[i].z:F0})");
    }

    // ── Stairs auto-walk (v1, 2026-07-16) ────────────────────────────────────
    // Rebuilt on the minimap ROOM graph: StairsPlan gives an ordered door-hop
    // sequence to the stairs' room (the hops ARE the route — clean + monotonic,
    // proven live). We drive hop-to-hop with the shared Steerer; the live
    // collision wall-sensor does the fine in-lane steering between doors. We STOP
    // at the last hop (the door INTO the stairs' room) — "check nearest door".
    // Never crosses that final door and never presses without the game's own
    // CHECK prompt showing (no accidental interaction).

    private const float StairsCellUnits = 170f;       // reach a STRAIGHT centerline cell → advance
    private const float TurnArriveUnits = 80f;        // a BEND must be reached tightly — consuming a turn
                                                      // early cut the corner into the wall and the game's
                                                      // collision SLID the player 2400u off-route (B6F, 07-17)
    private const float StairsFinalDoorUnits = 260f;  // stop at the stairs-room door
    private const float StairsNearUnits = 300f;       // sameRoom: stop near the stairs
    private const int StairsStallMs = 1500;           // driving-but-not-closer this long = stalled
    private const int StairsMaxReroutes = 5;          // hard cap → can never grind forever
    private const float SlideDot = 0.30f;             // actual motion vs commanded below this = wall slide
    private const int SlidePolls = 10;                // ~500ms of disagreement = a confirmed slide
    private const int StairsMaxSlides = 6;            // recoveries before an honest give-up
    private const float DoorSeekUnits = 700f;         // a stall with a door this near = a closed door, not a wall
    private const int MaxDoorTries = 2;               // door threads per waypoint before an honest give-up
    private const float StallConsumeSlack = 180f;     // stalled this close past the radius = we ARE there — consume,
                                                      // don't reroute (Heaven 07-17: five reroutes burned on stalls
                                                      // 92-222u from their waypoints → budget gone → false give-up)

    // Center-in-lane + veer-off-wall steering (live wall probe). Tunable by ear.
    private const float WalkProbeMax = 520f;          // normalization span for ∞ (open) probe reads
    private const float WalkCenterK = 0.35f;          // lane-centering strength
    private const float WalkCenterSpan = 320f;        // L/R clearance imbalance → full center push
    private const float WalkNearAhead = 300f;         // wall this close ahead → start veering
    private const float WalkVeerK = 1.0f;             // veer strength when a wall is close ahead

    /// <summary>Backspace on the Exits/Stairs selection: walk to the stairs' room door.</summary>
    internal static void WalkToStairs() => Launch(StairsBody, stairsTarget: true);

    /// <summary>Target kinds for the v2 walk — the drive is identical; only the
    /// final approach and arrival announcement differ.</summary>
    internal enum TargetKind { Chest, Door, Spot }

    /// <summary>v2 walk for chests / doors / interactables / marks — the SAME
    /// drive as the stairs walk (turn-tight, slide-aware, door-opening), with the
    /// per-kind final approach the old walkers had (chest = ease onto CHECK; door
    /// = stop centered in front; spot = arrive + prompt note).</summary>
    internal static void WalkTarget(string label, float tx, float tz, TargetKind kind)
        => Launch(() => TargetBody(label, tx, tz, kind));

    /// <summary>v2 shadow walk: drive near the shadow (re-acquired on every
    /// replan — they move), then hand off to the proven back-strike Hunt.</summary>
    internal static void WalkToShadowV2(float sx, float sz) => Launch(() => ShadowBody(sx, sz));

    /// <summary>Replan callback for <see cref="DriveRoute"/> — same signature as
    /// StairsPlan.TryPlanTo minus the fixed arguments the closure carries.</summary>
    private delegate StairsPlan.Result PlanFn(float px, float pz, HashSet<int> blocked,
        out List<(float x, float z)> wps, out bool sameRoom);

    private static void StairsBody()
    {
        var blocked = new HashSet<int>();
        float sx = 0, sz = 0;
        StairsPlan.Result Planner(float ppx, float ppz, HashSet<int> blk,
            out List<(float x, float z)> w, out bool same)
            => StairsPlan.TryPlan(ppx, ppz, blk, out w, out same, out sx, out sz);

        var res = Planner(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, blocked,
                          out var wps, out bool sameRoom);
        if (res == StairsPlan.Result.NoStairs) { Speech.Say("Stairs not found. Explore more.", true); return; }
        if (res == StairsPlan.Result.NoRoute || wps.Count == 0) { Speech.Say("No route to the stairs.", true); return; }
        Speech.Say("Walking to the stairs.", true);
        Log($"[AutoWalker] stairs walk: {wps.Count} cells, sameRoom={sameRoom}, stairs=({sx:F0},{sz:F0})");
        CalibrateSign();
        FieldTracker.SetWalkProbe(true);   // live wall sensing on the game thread
        try
        {
            if (!DriveRoute(Planner, ref wps, ref sameRoom, blocked,
                            StairsNearUnits, StairsFinalDoorUnits, "stairs")) return;
            Speech.Say(sameRoom ? "Arrived. The stairs are near." : "Arrived. Check nearest door.", true);
            Log("[AutoWalker] stairs walk: arrived");
        }
        finally { FieldTracker.SetWalkProbe(false); }
    }

    private static void TargetBody(string label, float tx, float tz, TargetKind kind)
    {
        // GRIDLESS areas (the TV-world Entrance hub, dungeon lobbies) have no
        // minimap grid for the v2 planner — fall back to the proven one-shot
        // straight-line walker, the lobby behavior since 2026-06-17
        // (memory/dungeon_lobby_places.md). Lobbies are open floors with
        // walk-up interactables; the straight-liner is the right tool there.
        if (!GridRouter.HasGrid()) { Walk(label, tx, tz, null); return; }

        var blocked = new HashSet<int>();
        StairsPlan.Result Planner(float ppx, float ppz, HashSet<int> blk,
            out List<(float x, float z)> w, out bool same)
            => StairsPlan.TryPlanTo(ppx, ppz, tx, tz, blk, stopAtTargetRoomDoor: false,
                                    doorTarget: kind == TargetKind.Door, out w, out same);

        var res = Planner(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, blocked,
                          out var wps, out bool sameRoom);
        if (res != StairsPlan.Result.Ok || wps.Count == 0) { Speech.Say($"No route to {label}.", true); return; }
        Speech.Say($"Walking to {label}.", true);
        Log($"[AutoWalker] target walk ({kind}) {label} ({tx:F0},{tz:F0}): {wps.Count} waypoints");
        CalibrateSign();
        FieldTracker.SetWalkProbe(true);
        try
        {
            float arrive = kind == TargetKind.Chest ? FinalTolUnits
                         : kind == TargetKind.Door ? DoorTolUnits : 260f;
            if (!DriveRoute(Planner, ref wps, ref sameRoom, blocked, arrive, arrive, "target")) return;

            if (kind == TargetKind.Chest)
            {
                // Ease onto the openable spot until the game's CHECK prompt shows
                // (the proven TravelToChest tail — the player presses to open).
                float pitch = GridRouter.CellPitch(); if (pitch <= 0) pitch = WorldPerStep * 5f;
                int maj0 = FieldTracker.CurrentMajor, min0 = FieldTracker.CurrentMinor;
                long til = Environment.TickCount64 + 2500;
                bool chk = FieldTracker.CheckPromptActive;
                while (!chk && Environment.TickCount64 < til && !_cancelRequested && Utils.GameHasFocus()
                       && !FieldInterrupted(maj0, min0))
                {
                    float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
                    if (float.IsNaN(px)) break;
                    if ((tx - px) * (tx - px) + (tz - pz) * (tz - pz) <= (pitch * 0.18f) * (pitch * 0.18f)) break;
                    DriveToward(tx, tz, pitch * 0.12f, 300);
                    chk = FieldTracker.CheckPromptActive;
                }
                ReleaseAll();
                Speech.Say(chk ? "At the chest. Press to open." : "At the chest.", true);
            }
            else if (kind == TargetKind.Door)
                Speech.Say(FieldTracker.CheckPromptActive ? "At the door. Press to open." : "At the door.", true);
            else
                Speech.Say(FieldTracker.CheckPromptActive ? $"Reached {label}. Press to interact."
                                                          : $"Reached {label}.", true);
            Log($"[AutoWalker] target walk arrived: {label}");
        }
        finally { FieldTracker.SetWalkProbe(false); }
    }

    private static void ShadowBody(float sx, float sz)
    {
        float cx = sx, cz = sz;
        StairsPlan.Result Planner(float ppx, float ppz, HashSet<int> blk,
            out List<(float x, float z)> w, out bool same)
        {
            // Re-acquire the nearest live shadow to the last-known spot — they
            // move while we walk (a fixed stale target walked to empty floor).
            float best = ShadowTrackRange * ShadowTrackRange;
            float nx = cx, nz = cz;
            foreach (var (x, z, _, _) in DungeonNav.ShadowsWithFacing())
            {
                float ddx = x - cx, ddz = z - cz, d2 = ddx * ddx + ddz * ddz;
                if (d2 < best) { best = d2; nx = x; nz = z; }
            }
            cx = nx; cz = nz;
            return StairsPlan.TryPlanTo(ppx, ppz, cx, cz, blk, false, false, out w, out same);
        }

        var blocked = new HashSet<int>();
        var res = Planner(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, blocked,
                          out var wps, out bool sameRoom);
        if (res != StairsPlan.Result.Ok || wps.Count == 0) { Speech.Say("No route to the shadow.", true); return; }
        Speech.Say("Heading to the shadow.", true);
        Log($"[AutoWalker] shadow walk: {wps.Count} waypoints to ({cx:F0},{cz:F0})");
        CalibrateSign();
        FieldTracker.SetWalkProbe(true);
        float near = GridRouter.CellPitch(); if (near <= 0) near = WorldPerStep * 5f;
        near *= 1.6f;
        bool arrived;
        try { arrived = DriveRoute(Planner, ref wps, ref sameRoom, blocked, near, near, "shadow"); }
        finally { FieldTracker.SetWalkProbe(false); }
        if (arrived) Hunt(cx, cz);   // the proven back-strike hunt takes it from here
    }

    /// <summary>The SHARED v2 waypoint drive — every dungeon auto-walk runs on
    /// this one loop: turn-tight consumption, slide detection, and the stall
    /// ladder (prompt press → consume-if-close → door open + centered crossing →
    /// two-strike reroute with un-block). Returns TRUE when the final waypoint is
    /// consumed; FALSE when stopped (cancel/battle/give-up — already announced).
    /// The caller owns start/arrival speech, the walk probe, and any final tail.</summary>
    private static bool DriveRoute(PlanFn replan, ref List<(float x, float z)> wps, ref bool sameRoom,
        HashSet<int> blocked, float finalArriveSame, float finalArriveCross, string tag)
    {
        int maj0 = FieldTracker.CurrentMajor, min0 = FieldTracker.CurrentMinor;
        // ONE continuous Steerer over the fine centerline waypoints — keys stay
        // pressed across cell boundaries (smooth), we only switch the target bearing.
        var steer = new Steerer();
        int wi = 0, reroutes = 0, slides = 0, slideRun = 0, noRouteCycles = 0;
        int stallWi = -1, stallCount = 0, doorWi = -1, doorTries = 0;
        float best = float.MaxValue;
        long lastProgress = Environment.TickCount64;
        float lastPx = float.NaN, lastPz = float.NaN;

        // Bend flags: waypoint i is a TURN when the centerline changes direction
        // there. A turn must be reached TIGHTLY — consuming it at the loose radius
        // cut the corner into the corridor wall and the game's collision slid the
        // player 2400u off-route (B6F junction, 2026-07-17 log).
        bool[] bend = BendFlags(wps);

        while (wi < wps.Count)
        {
            Thread.Sleep(PollMs);
            if (CheckStop(maj0, min0, out string? why)) { steer.Release(); Announce(why ?? "Auto-walk stopped."); return false; }
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (float.IsNaN(px) || float.IsNaN(pz)) continue;

            var (wx, wz) = wps[wi];
            float dx = wx - px, dz = wz - pz, dist = MathF.Sqrt(dx * dx + dz * dz);
            bool last = wi == wps.Count - 1;
            float arrive = last ? (sameRoom ? finalArriveSame : finalArriveCross)
                         : bend[wi] ? TurnArriveUnits : StairsCellUnits;

            if (dist <= arrive) { wi++; best = float.MaxValue; lastProgress = Environment.TickCount64; slideRun = 0; continue; }

            // WALL SLIDE: we command one direction, the game moves us another (a
            // diagonal push into a wall slides along it at full speed — position
            // changes fast, so the stall detector never sees it). Displacement vs
            // commanded bearing disagreeing for ~500ms = a confirmed slide.
            if (!float.IsNaN(lastPx))
            {
                float mx = px - lastPx, mz = pz - lastPz;
                float ml = MathF.Sqrt(mx * mx + mz * mz);
                if (ml > 10f && dist > 1f && (mx * dx + mz * dz) / (ml * dist) < SlideDot) slideRun++;
                else slideRun = 0;
            }
            lastPx = px; lastPz = pz;
            if (slideRun >= SlidePolls)
            {
                slideRun = 0;
                slides++;
                steer.Release();
                var (wa, wl, wr) = FieldTracker.WalkProbe();
                Log($"[AutoWalker] wall slide #{slides} at ({px:F0},{pz:F0}) wp{wi}=({wx:F0},{wz:F0}) probe a={wa:F0} l={wl:F0} r={wr:F0}");
                if (slides > StairsMaxSlides) { Announce("Stopped. Kept sliding off the route."); return false; }
                Thread.Sleep(180);
                // Re-target the NEAREST waypoint in a one-step window — the slide
                // may have carried us past the turn or back down the corridor.
                // Never jump further: the plan is door-gated.
                int bi = wi; float bd = float.MaxValue;
                for (int i = Math.Max(0, wi - 1); i <= Math.Min(wps.Count - 1, wi + 1); i++)
                {
                    float ex = wps[i].x - px, ez = wps[i].z - pz, d2 = ex * ex + ez * ez;
                    if (d2 < bd) { bd = d2; bi = i; }
                }
                if (bi != wi) { Log($"[AutoWalker] slide recovery: re-target wp{bi}"); wi = bi; }
                best = float.MaxValue; lastProgress = Environment.TickCount64;
                continue;
            }

            if (dist < best - 20f) { best = dist; lastProgress = Environment.TickCount64; }
            else if (Environment.TickCount64 - lastProgress > StairsStallMs)
            {
                // Stalled. A closed door ahead → open it and carry on.
                steer.Release();
                if (FieldTracker.CheckPromptActive) { PressConfirm(); best = float.MaxValue; lastProgress = Environment.TickCount64; continue; }

                // Stalled ESSENTIALLY AT the waypoint (wedged on a prop/frame lip
                // for the last few units) — that's arrival, not an obstacle.
                // Consume it and continue; treating it as blocked burned the whole
                // reroute budget on points we were standing on (Heaven, 07-17).
                if (!last && dist <= arrive + StallConsumeSlack)
                {
                    Log($"[AutoWalker] stall at wp{wi} dist={dist:F0} — close enough, consuming");
                    wi++; best = float.MaxValue; lastProgress = Environment.TickCount64; slideRun = 0;
                    continue;
                }

                // DOOR: a stall with a known door toward the waypoint (or beside it)
                // is a CLOSED DOOR, not a wall. B7F 2026-07-17: 11 doors each read as
                // solid → their cells got blocked one by one → NoRoute. Thread the
                // door with the proven prompt-gated procedure and resume this leg.
                if (TryStallDoor(px, pz, wx, wz, out float doorX, out float doorZ))
                {
                    if (doorWi != wi) { doorWi = wi; doorTries = 0; }
                    if (doorTries < MaxDoorTries)
                    {
                        doorTries++;
                        Log($"[AutoWalker] stall at wp{wi}: door @({doorX:F0},{doorZ:F0}) — opening (try {doorTries})");
                        bool okDoor = OpenDoorAt(doorX, doorZ);
                        // Cross via INJECTED centered waypoints — the MAIN drive
                        // (turn-tight, slide-aware) threads the gap. ThreadDoorAt's
                        // own through-push clipped door frames (Heaven 07-18: "both
                        // axes clipped") while the normal drive crossed cleanly.
                        if (!DungeonNav.TryDoorAxis(doorX, doorZ, out float cnx, out float cnz))
                        { cnx = px - doorX; cnz = pz - doorZ; }
                        float cnl = MathF.Sqrt(cnx * cnx + cnz * cnz);
                        if (cnl > 1e-3f)
                        {
                            cnx /= cnl; cnz /= cnl;
                            if ((px - doorX) * cnx + (pz - doorZ) * cnz < 0f) { cnx = -cnx; cnz = -cnz; }
                            wps.Insert(wi, (doorX - cnx * 220f, doorZ - cnz * 220f));   // far side
                            wps.Insert(wi, (doorX + cnx * 220f, doorZ + cnz * 220f));   // near front first
                            bend = BendFlags(wps);
                            Log($"[AutoWalker] door open {(okDoor ? "done" : "interrupted")} — crossing via waypoints");
                        }
                        steer = new Steerer();
                        best = float.MaxValue; lastProgress = Environment.TickCount64;
                        stallWi = -1; stallCount = 0; slideRun = 0;
                        continue;
                    }
                    Announce("Stopped. A door would not open.");
                    return false;
                }

                // The FIRST waypoint is a positioning aid (the re-center point or
                // the start cell's center), never a gate — a cell center can sit
                // INSIDE furniture (Secret Lab control-room consoles, 07-18: the
                // walker ground at wp0 dist=463 through five identical reroutes,
                // because blocking the START cell never changes the plan). Skip
                // it; the real route ahead has the full recovery ladder.
                if (wi == 0 && !last)
                {
                    Log($"[AutoWalker] stall at start wp0 dist={dist:F0} — skipping the positioning point");
                    wi++; best = float.MaxValue; lastProgress = Environment.TickCount64; slideRun = 0;
                    stallWi = -1; stallCount = 0;
                    continue;
                }

                if (stallWi != wi) { stallWi = wi; stallCount = 0; }
                stallCount++;
                if (stallCount == 1)
                {
                    // First stall on this waypoint: usually WE slid or orbited —
                    // the map is innocent. Back off and re-approach before blaming
                    // a cell (blocking an unblocked cell severed real routes).
                    Log($"[AutoWalker] stall #1 at wp{wi} ({px:F0},{pz:F0}) dist={dist:F0} — re-approaching");
                    KeyDown(SC_S); Thread.Sleep(250); KeyUp(SC_S);
                    best = float.MaxValue; lastProgress = Environment.TickCount64;
                    continue;
                }
                // Second stall on the same waypoint → a genuine obstacle the grid
                // can't see → block that cell + reroute around it.
                if (reroutes < StairsMaxReroutes && MinimapTracker.WorldToCell(wx, wz, out int br, out int bc))
                {
                    reroutes++;
                    blocked.Add(br * MinimapTracker.COLS + bc);
                    KeyDown(SC_S); Thread.Sleep(250); KeyUp(SC_S);   // back off the obstacle
                    var rr = replan(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, blocked,
                                    out var fresh, out sameRoom);
                    if (rr == StairsPlan.Result.Ok && fresh.Count > 0)
                    {
                        wps = fresh; wi = 0; best = float.MaxValue; lastProgress = Environment.TickCount64;
                        stallWi = -1; stallCount = 0;
                        bend = BendFlags(wps);
                        steer = new Steerer();
                        Log($"[AutoWalker] {tag} reroute #{reroutes} around ({br},{bc}): {fresh.Count} cells");
                        continue;
                    }
                    // NoRoute after blocking ONE cell = the block itself was wrong:
                    // that cell was the only way through, so the obstruction is
                    // smaller than the cell (a prop / an unnoticed door). Un-block
                    // — and RETREAT before retrying: a plain retry resumed from the
                    // same wedged pocket and ground the same prop five identical
                    // cycles (post-battle wedge, 07-18; the user escaping the
                    // pocket by hand made the same route work). Back out the way
                    // we came, longer each cycle, then replan from the new spot.
                    // The reroute counter still bounds the whole ladder.
                    blocked.Remove(br * MinimapTracker.COLS + bc);
                    noRouteCycles++;
                    int backMs = 600 + 400 * noRouteCycles;   // 1000ms, 1400ms, 1800ms…
                    Log($"[AutoWalker] reroute #{reroutes} failed ({rr}) — unblocked ({br},{bc}), retreating {backMs}ms");
                    KeyDown(SC_S); Thread.Sleep(backMs); KeyUp(SC_S);
                    var rr2 = replan(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, blocked,
                                     out var fresh2, out sameRoom);
                    if (rr2 == StairsPlan.Result.Ok && fresh2.Count > 0)
                    {
                        wps = fresh2; wi = 0; bend = BendFlags(wps); steer = new Steerer();
                        Log($"[AutoWalker] replanned after retreat: {fresh2.Count} cells");
                    }
                    stallWi = -1; stallCount = 0; slideRun = 0;
                    best = float.MaxValue; lastProgress = Environment.TickCount64;
                    continue;
                }
                Log($"[AutoWalker] give-up at wp{wi}: reroutes={reroutes}/{StairsMaxReroutes}");
                Announce("Stopped. Couldn't get through.");
                return false;
            }

            // Center in the lane + veer off boundaries using the live wall probe.
            FieldTracker.SetWalkDir(dx, dz);
            var (abx, abz) = LaneAdjust(dx, dz);
            steer.Tick(px, pz, abx, abz);
        }

        steer.Release();
        return true;
    }

    /// <summary>Open (only) the door at (doorX,doorZ): creep at it until the
    /// game's facing-aware CHECK prompt lights, tap confirm while lit, wait out
    /// the swing. NO through-drive — the caller injects centered crossing
    /// waypoints and the main drive threads the gap (ThreadDoorAt's blind
    /// through-push clipped frames; the drive crosses an open gap cleanly).
    /// False = interrupted (cancel / focus loss / battle).</summary>
    private static bool OpenDoorAt(float doorX, float doorZ)
    {
        float pitch = GridRouter.CellPitch(); if (pitch <= 0) pitch = WorldPerStep * 5f;
        int maj0 = FieldTracker.CurrentMajor, min0 = FieldTracker.CurrentMinor;
        long until = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < until && !_cancelRequested && Utils.GameHasFocus()
               && !FieldInterrupted(maj0, min0))
        {
            if (FieldTracker.CheckPromptActive) break;
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (!float.IsNaN(px) && (doorX - px) * (doorX - px) + (doorZ - pz) * (doorZ - pz) <= 300f * 300f) break;
            DriveToward(doorX, doorZ, pitch * 0.18f, 280);
        }
        if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(maj0, min0)) return false;

        // Only a REAL door actor gets confirm taps (a prompt near an archway may
        // belong to the stairs / another interactable — tapping ACTS on it).
        bool realDoor = false;
        try
        {
            foreach (var (dax, daz) in DungeonNav.Doors())
                if ((dax - doorX) * (dax - doorX) + (daz - doorZ) * (daz - doorZ) < 450f * 450f)
                { realDoor = true; break; }
        }
        catch { }
        if (!realDoor || DungeonNav.IsDoorOpenMarked(doorX, doorZ)) return true;   // archway / already open

        long promptBy = Environment.TickCount64 + 600;
        while (!FieldTracker.CheckPromptActive && Environment.TickCount64 < promptBy
               && !_cancelRequested && Utils.GameHasFocus() && !FieldInterrupted(maj0, min0))
            Thread.Sleep(100);
        for (int i = 0; i < 4 && !_cancelRequested && Utils.GameHasFocus()
             && !FieldInterrupted(maj0, min0); i++)
        {
            if (!FieldTracker.CheckPromptActive)
            {
                if (i == 0) Log("[AutoWalker] door: no prompt — not pressing (open/archway/side-on)");
                break;
            }
            if (StairsPromptRisk(doorX, doorZ))
            {
                Log("[AutoWalker] door: stairs own this prompt — not pressing");
                break;
            }
            PressConfirm();
            Thread.Sleep(450);
            if (!FieldTracker.CheckPromptActive)
            {
                Log("[AutoWalker] door: opened");
                DungeonGrid.Invalidate(doorX, doorZ);
                // Let the leaf swing out of the way before the drive crosses.
                for (int w = 0; w < 9 && !_cancelRequested && Utils.GameHasFocus()
                     && !FieldInterrupted(maj0, min0); w++)
                    Thread.Sleep(100);
                break;
            }
        }
        return !_cancelRequested && Utils.GameHasFocus() && !FieldInterrupted(maj0, min0);
    }

    /// <summary>The door whose CROSSING makes real progress toward the stalled
    /// waypoint (wx,wz) — direction-independent. The old version filtered by
    /// "roughly toward the waypoint", which is a blind cone: the Heaven give-up
    /// (2026-07-18 screenshot) happened grinding beside a door on the LEFT that
    /// the cone excluded. Now every live door within <see cref="DoorSeekUnits"/>
    /// of the player is scored by the distance from its FAR SIDE to the waypoint;
    /// a door qualifies only if standing beyond it is meaningfully closer than we
    /// are now. A side/behind door toward the target qualifies; the door we just
    /// came through never does (its far side moves us away) — so no circling.</summary>
    private static bool TryStallDoor(float px, float pz, float wx, float wz, out float dx, out float dz)
    {
        dx = dz = 0;
        float dCur = MathF.Sqrt((wx - px) * (wx - px) + (wz - pz) * (wz - pz));
        if (dCur < 1f) return false;
        float bestFar = float.MaxValue; bool found = false;
        try
        {
            foreach (var (x, z) in DungeonNav.Doors())
            {
                float ex = x - px, ez = z - pz;
                if (ex * ex + ez * ez > DoorSeekUnits * DoorSeekUnits) continue;
                // Far side of the door: its passage axis, oriented away from us.
                if (!DungeonNav.TryDoorAxis(x, z, out float nx, out float nz)) { nx = ex; nz = ez; }
                float nl = MathF.Sqrt(nx * nx + nz * nz);
                if (nl < 1e-3f) continue;
                nx /= nl; nz /= nl;
                if (ex * nx + ez * nz < 0f) { nx = -nx; nz = -nz; }
                float fx = x + nx * 220f, fz = z + nz * 220f;
                float dFar = MathF.Sqrt((wx - fx) * (wx - fx) + (wz - fz) * (wz - fz));
                if (dFar > dCur - 150f) continue;        // crossing it gains nothing
                if (dFar < bestFar) { bestFar = dFar; dx = x; dz = z; found = true; }
            }
        }
        catch { }
        return found;
    }

    /// <summary>Which plan waypoints must be reached TIGHTLY: BENDS (the centerline
    /// changes direction there) and DOOR-CROSSING points (loose consumption crosses
    /// the gap off-center → frame wedge). First and last are never bend-marked —
    /// the first is the player's own cell, the last has its own arrival rule —
    /// but a door point is tight wherever it sits.</summary>
    private static bool[] BendFlags(List<(float x, float z)> wps)
    {
        var bend = new bool[wps.Count];
        for (int i = 1; i < wps.Count - 1; i++)
        {
            float ix = wps[i].x - wps[i - 1].x, iz = wps[i].z - wps[i - 1].z;
            float ox = wps[i + 1].x - wps[i].x, oz = wps[i + 1].z - wps[i].z;
            float il = MathF.Sqrt(ix * ix + iz * iz), ol = MathF.Sqrt(ox * ox + oz * oz);
            bend[i] = il > 1f && ol > 1f && (ix * ox + iz * oz) / (il * ol) < 0.9f;
        }
        try
        {
            foreach (var (dx, dz) in DungeonNav.Doors())
                for (int i = 0; i < wps.Count; i++)
                    if ((wps[i].x - dx) * (wps[i].x - dx) + (wps[i].z - dz) * (wps[i].z - dz) < 300f * 300f)
                        bend[i] = true;
        }
        catch { }
        return bend;
    }

    /// <summary>Center-in-lane + veer-off-wall correction to a raw world bearing,
    /// from the live travel-relative wall probe. perpL = (-bz, bx). Returns the
    /// adjusted (bx,bz) to steer toward. ∞ (open) reads treated as WalkProbeMax.</summary>
    private static (float x, float z) LaneAdjust(float dx, float dz)
    {
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-3f) return (dx, dz);
        float bx = dx / len, bz = dz / len;
        float plx = -bz, plz = bx;                     // world "left" of travel
        var (wa, wl, wr) = FieldTracker.WalkProbe();
        float eL = float.IsFinite(wl) ? wl : WalkProbeMax;
        float eR = float.IsFinite(wr) ? wr : WalkProbeMax;

        // Centering: push toward the side with MORE clearance (equalize L/R).
        float lateral = WalkCenterK * Math.Clamp((eL - eR) / WalkCenterSpan, -1f, 1f);
        // Veer: a wall close ahead → stronger push toward the more-open side.
        if (float.IsFinite(wa) && wa < WalkNearAhead)
            lateral += (eL >= eR ? 1f : -1f) * WalkVeerK * (1f - wa / WalkNearAhead);
        lateral = Math.Clamp(lateral, -1.3f, 1.3f);

        return (bx + plx * lateral, bz + plz * lateral);
    }

    // ── Shared steering core ─────────────────────────────────────────────────

    private sealed class Steerer
    {
        public enum Ev { Ok, NoCam, CamLost, Wall, Grind }

        private const float DriftMinVel = 110f;    // trust velocity above this (per ~200ms)
        private const float GrindVel = 60f;        // keys held but slower = grinding
        private const float WallErrDeg = 50f;

        public byte[] Held = Array.Empty<byte>();
        private float _pwmAcc;
        private readonly float[] _rx = new float[4];
        private readonly float[] _rz = new float[4];
        private int _rn, _ri;
        private int _wallHits, _slowHits, _camMisses;

        /// <summary>One steering poll toward bearing (bx,bz) from (px,pz).</summary>
        public Ev Tick(float px, float pz, float bx, float bz)
        {
            var (cfx, cfz) = FieldTracker.CameraForward3D();
            if (cfx == 0 && cfz == 0)
                return ++_camMisses >= 20 ? Ev.CamLost : Ev.NoCam;
            _camMisses = 0;
            float camX = cfx * _camSign, camZ = cfz * _camSign;

            // PWM between the two flanking 45° combos = any exact angle.
            float angCam = Norm(RouteSpeech.SignedAngleDeg(camX, camZ, bx, bz));
            float baseOff = MathF.Floor(angCam / 45f) * 45f;
            float frac = (angCam - baseOff) / 45f;
            _pwmAcc += frac;
            float useOff;
            if (_pwmAcc >= 1f) { _pwmAcc -= 1f; useOff = baseOff + 45f; }
            else useOff = baseOff;
            var combo = ComboFor(useOff);
            if (!SameKeys(Held, combo))
            {
                SwitchKeys(Held, combo);
                Held = combo;
            }

            // Velocity classification against the commanded bearing.
            _rx[_ri] = px; _rz[_ri] = pz;
            _ri = (_ri + 1) % 4; if (_rn < 4) _rn++;
            if (_rn < 4) return Ev.Ok;

            int oldest = _ri;
            float vx = px - _rx[oldest], vz = pz - _rz[oldest];
            float sp2 = vx * vx + vz * vz;
            if (sp2 > DriftMinVel * DriftMinVel)
            {
                _slowHits = 0;
                float aerr = MathF.Abs(RouteSpeech.SignedAngleDeg(bx, bz, vx, vz));
                if (aerr > WallErrDeg)
                {
                    if (++_wallHits >= 4)
                    {
                        Log($"[AutoWalker] wall slide (err={aerr:F0}° vel=({vx:F0},{vz:F0}))");
                        _wallHits = 0;
                        return Ev.Wall;
                    }
                }
                else if (_wallHits > 0) _wallHits--;
            }
            else if (sp2 < GrindVel * GrindVel && Held.Length > 0)
            {
                if (++_slowHits >= 10)
                {
                    Log($"[AutoWalker] grinding (speed²={sp2:F0})");
                    _slowHits = 0;
                    return Ev.Grind;
                }
            }
            else if (_slowHits > 0) _slowHits--;
            return Ev.Ok;
        }

        /// <summary>Release keys and forget motion history (after stops/turn-arounds).</summary>
        public void Release()
        {
            ReleaseAll();
            Held = Array.Empty<byte>();
            ResetMotion();
        }

        public void ResetMotion()
        {
            _rn = 0; _wallHits = 0; _slowHits = 0; _pwmAcc = 0;
        }
    }

    /// <summary>
    /// Measure the camera-forward sign with one deliberate W press: from
    /// standstill, W moves along the camera view direction — if the measured
    /// motion opposes the raw read, the sign is −1. Replaces the in-walk
    /// auto-flip, which misfired on turn-around pivots (live 2026-06-11).
    /// Leaves the previous sign untouched when the measurement is unusable
    /// (blocked against a wall, camera unreadable).
    /// </summary>
    private static void CalibrateSign()
    {
        var (cfx, cfz) = FieldTracker.CameraForward3D();
        if (cfx == 0 && cfz == 0) return;
        float px0 = FieldTracker.LivePlayerX, pz0 = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px0)) return;

        KeyDown(SC_W);
        Thread.Sleep(300);
        KeyUp(SC_W);
        Thread.Sleep(80);

        float px1 = FieldTracker.LivePlayerX, pz1 = FieldTracker.LivePlayerZ;
        float mx = px1 - px0, mz = pz1 - pz0;
        if (float.IsNaN(px1) || mx * mx + mz * mz < 60f * 60f) return;   // blocked/no data

        float err = MathF.Abs(RouteSpeech.SignedAngleDeg(cfx, cfz, mx, mz));
        float sign = err > 90f ? -1f : 1f;
        if (sign != _camSign)
        {
            _camSign = sign;
            Log($"[AutoWalker] sign calibrated → {_camSign} (err={err:F0}°)");
        }
    }

    // ── Discrete one-cell step (the tile-stepping primitive) ─────────────────

    private static void StepBody(float dirX, float dirZ, float cellFraction,
                                 Action<StepResult, int, int> onDone)
    {
        int sMaj = FieldTracker.CurrentMajor, sMin = FieldTracker.CurrentMinor;
        float pitch = GridRouter.CellPitch();
        if (pitch <= 0) pitch = WorldPerStep * 5f;
        float distance = MathF.Max(WorldPerStep, cellFraction * pitch);   // at least one game-pace

        // Drive a CONSTANT cardinal world direction (never "toward a point") so the
        // path is a straight line along the grid axis.
        float dm = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
        if (dm < 1e-3f) { onDone(StepResult.Refused, -1, -1); return; }
        dirX /= dm; dirZ /= dm;

        float sx = FieldTracker.LivePlayerX, sz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(sx) || float.IsNaN(sz)) { onDone(StepResult.Refused, -1, -1); return; }
        if (!MinimapTracker.WorldToCell(sx, sz, out int sr, out int sc)) { sr = -1; sc = -1; }

        // DOOR AHEAD → thread it: if a door sits within ~1.3 tiles in the step
        // direction, line up with the gap and walk THROUGH it (one press = through the
        // door) instead of ramming the frame. This is what lets the tile stay big and
        // clean without doors blocking it.
        {
            float maxD = pitch * 1.3f, bestD = maxD; float bdx = 0, bdz = 0; bool door = false;
            foreach (var (x, z) in DungeonNav.Doors())
            {
                float vx = x - sx, vz = z - sz; float d = MathF.Sqrt(vx * vx + vz * vz);
                if (d < 1f || d > maxD) continue;
                if ((vx * dirX + vz * dirZ) / d < 0.4f) continue;   // must be ahead in the step direction
                if (d < bestD) { bestD = d; bdx = x; bdz = z; door = true; }
            }
            if (door && ThreadDoorAt(bdx, bdz, sx + dirX * pitch * 2f, sz + dirZ * pitch * 2f))
            {
                MinimapTracker.WorldToCell(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, out int tr, out int tc);
                Log($"[AutoWalker] step threaded door → ({tr},{tc})");
                onDone(StepResult.Moved, tr, tc); return;
            }
        }

        var steer = new Steerer();
        float travelCapSq = (distance * 3f) * (distance * 3f);
        long deadline = Environment.TickCount64 + 2000;
        var (pcx, pcz) = FieldTracker.CameraForward3D();   // previous camera-forward
        float prevPx = sx, prevPz = sz;
        int backHits = 0; bool flipped = false;

        // Obstacle slip: if forward progress STALLS (a small protrusion sticking out
        // of an otherwise-walkable tile, or a minimap cell that's marked walkable but
        // physically blocked), try short diagonal nudges to slide past it — first one
        // side, then the other — instead of grinding to a halt. Forward progress
        // resets the counter; a genuine wall gives up after both sides fail, with
        // only a little sideways wander.
        float bestProj = 0f;
        long lastAdvance = Environment.TickCount64;
        bool slipping = false; long slipUntil = 0; int slipTries = 0;
        float slipBx = dirX, slipBz = dirZ;
        const long StuckMs = 200, SlipMs = 200; const int MaxSlip = 2;

        Log($"[AutoWalker] step start dir=({dirX:F2},{dirZ:F2}) dist={distance:F0} fromCell=({sr},{sc}) sign={_camSign}");

        while (true)
        {
            Thread.Sleep(PollMs);
            if (CheckStop(sMaj, sMin, out string? why))
            {
                steer.Release(); Log($"[AutoWalker] step cancelled: {why}");
                MinimapTracker.WorldToCell(FieldTracker.LivePlayerX, FieldTracker.LivePlayerZ, out int qr, out int qc);
                onDone(StepResult.Cancelled, qr, qc); return;
            }

            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (float.IsNaN(px) || float.IsNaN(pz)) continue;

            // Camera swung (door auto-face / scripted). The Steerer re-aims off the
            // live camera each poll, but if it's swinging FAST, pause a beat so a
            // transient mid-swing reading doesn't throw a stray sideways push.
            var (cx, cz) = FieldTracker.CameraForward3D();
            if ((cx != 0 || cz != 0) && (pcx != 0 || pcz != 0)
                && MathF.Abs(RouteSpeech.SignedAngleDeg(pcx, pcz, cx, cz)) > 15f)
            { steer.Release(); slipping = false; pcx = cx; pcz = cz; prevPx = px; prevPz = pz; lastAdvance = Environment.TickCount64; continue; }
            if (cx != 0 || cz != 0) { pcx = cx; pcz = cz; }

            // Arrived: crossed into a NEW minimap cell = one tile per press (the map
            // unit). Robust to where in the cell you started; no overshoot into the
            // tile beyond. (proj is still tracked below for stall detection.)
            float proj = (px - sx) * dirX + (pz - sz) * dirZ;
            if (MinimapTracker.WorldToCell(px, pz, out int curR, out int curC) && (curR != sr || curC != sc))
            {
                steer.Release();
                Log($"[AutoWalker] step → ({curR},{curC}) Moved{(slipTries > 0 ? " (slipped)" : "")}");
                onDone(StepResult.Moved, curR, curC); return;
            }

            long now = Environment.TickCount64;

            // Forward progress resets the stall timer + slip counter.
            if (proj > bestProj + 20f) { bestProj = proj; lastAdvance = now; slipTries = 0; }

            // Runaway / time cap.
            float ox = px - sx, oz = pz - sz;
            if (ox * ox + oz * oz >= travelCapSq || now > deadline)
            {
                steer.Release();
                MinimapTracker.WorldToCell(px, pz, out int cr, out int cc);
                Log($"[AutoWalker] step → ({cr},{cc}) Blocked [cap/time]");
                onDone(StepResult.Blocked, cr, cc); return;
            }

            // Self-correct the camera sign: clearly moving BACKWARD vs the cardinal
            // = a camera-convention flip → flip once and re-steer. (Safe: the bearing
            // is a fixed cardinal, so there's no turn-around to confuse it.)
            float vx = px - prevPx, vz = pz - prevPz; prevPx = px; prevPz = pz;
            if (!slipping && vx * vx + vz * vz > 35f * 35f
                && MathF.Abs(RouteSpeech.SignedAngleDeg(dirX, dirZ, vx, vz)) > 140f)
            {
                if (++backHits >= 2 && !flipped)
                { _camSign = -_camSign; flipped = true; backHits = 0; steer.Release();
                  Log($"[AutoWalker] step sign auto-flip → {_camSign}"); continue; }
            }
            else backHits = 0;

            // Slip control: end a finished slip; start one when progress has stalled.
            if (slipping && now >= slipUntil) { slipping = false; steer.Release(); }
            if (!slipping && now - lastAdvance > StuckMs)
            {
                if (slipTries >= MaxSlip)
                {
                    steer.Release();
                    MinimapTracker.WorldToCell(px, pz, out int cr, out int cc);
                    Log($"[AutoWalker] step → ({cr},{cc}) Blocked [wall, slip failed]");
                    onDone(StepResult.Blocked, cr, cc); return;
                }
                slipTries++;
                float ps = (slipTries % 2 == 1) ? 1f : -1f;       // alternate side
                float bx = dirX + (-dirZ * ps), bz = dirZ + (dirX * ps);   // 45° toward perpendicular
                float bm = MathF.Sqrt(bx * bx + bz * bz);
                slipBx = bx / bm; slipBz = bz / bm;
                slipping = true; slipUntil = now + SlipMs; steer.Release();
                Log($"[AutoWalker] step slip {slipTries} (side {ps:F0})");
            }

            float ubx = slipping ? slipBx : dirX, ubz = slipping ? slipBz : dirZ;
            // Ev.Wall / Ev.Grind are NOT terminal here — the stall/slip logic above
            // owns the "give up" decision so we try to slide past small obstacles.
            if (steer.Tick(px, pz, ubx, ubz) == Steerer.Ev.CamLost)
            {
                steer.Release();
                Log("[AutoWalker] step Blocked [camera lost]");
                onDone(StepResult.Blocked, sr, sc); return;
            }
        }
    }

    // ── Walk (static targets: chests, doors, stairs) ─────────────────────────

    private static void Walk(string label, float tx, float tz, (float x, float z)? approach)
    {
        int startMajor = FieldTracker.CurrentMajor, startMinor = FieldTracker.CurrentMinor;
        float pitch = GridRouter.CellPitch();
        if (pitch <= 0) pitch = WorldPerStep * 5f;
        float advTol = 120f;   // consume a waypoint only when REACHED (movement rebuild)
        // Stairs behave like doors for the final approach: walk STRAIGHT in and
        // let contact trigger the floor transition (scripted stairs transition
        // on contact, no press). So treat both door-like.
        bool doorLike = label == "Door" || label == "Stairs";
        float finalTol = doorLike ? DoorTolUnits : FinalTolUnits;

        var pts = PlanPoints(tx, tz, approach: approach);
        if (pts == null) { if (!_travelMode) Speech.Say("No mapped path to walk.", true); return; }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        int totalSteps = RouteSpeech.StepsFromUnits(
            MathF.Sqrt((tx - px) * (tx - px) + (tz - pz) * (tz - pz)));
        Log($"[AutoWalker] start {label} target=({tx:F0},{tz:F0}) pts={pts.Count} pitch={pitch:F0}");
        if (!_travelMode) Speech.Say($"Walking to {label.ToLowerInvariant()}, {totalSteps} step{(totalSteps == 1 ? "" : "s")}.", true);

        CalibrateSign();

        var steer = new Steerer();
        int wi = 0, rerouted = 0, nanPolls = 0;
        float prevBearX = 0, prevBearZ = 0;
        int lastAnnouncedSteps = RouteSpeech.StepsFromUnits(
            MathF.Sqrt((tx - px) * (tx - px) + (tz - pz) * (tz - pz)));
        bool haveAxes = GridRouter.TryGetStepAxes(out var rowAxis, out var colAxis);

        float bestWpDist = float.MaxValue;
        long lastProgress = Environment.TickCount64;
        bool doorPushing = false;
        long doorPushUntil = 0;
        long throughDoorSince = 0;        // bounds the "ignore the frame" window
        float doorProgressDist = float.MaxValue;   // best distance-to-target while at a doorway
        var threadedDoors = new HashSet<long>();   // doors this walk already went through (no re-thread)

        // ★ THE LEGO REBUILD (2026-07-15, user-directed): ONE recovery ladder.
        //   Stage 1: a real door ahead IS the way through — thread it once.
        //   Stage 2: back up + replan fresh — once per leg.
        //   Stage 3: the leg has failed — travel escalates, a one-shot reports.
        // Everything else (wall stamps, wedge escapes, ray sidesteps, slips,
        // same-spot bookkeeping) is OUT — the committee couldn't decide; this
        // ladder can. Returns: 0 = abort/leg-failed, 1 = recovered,
        // 2 = arrived (CHECK confirms), 3 = at the target but no prompt.
        int HandleBlocked(string reason)
        {
            steer.Release();
            float adx = tx - px, adz = tz - pz;
            if (MathF.Sqrt(adx * adx + adz * adz) <= MathF.Max(StallArriveUnits, finalTol))
            {
                // Stalling NEXT TO the goal is "arrival" only for a REAL target
                // (a chest/door one-shot: "it's right there"). For a TRAVEL leg
                // point it just means A WALL sits between us and a routing dot —
                // calling it arrival re-ordered the same leg forever (7× in the
                // 07-15 log, the computer-room exit). Travel legs fall through
                // to the ladder: the door ahead is usually the actual answer.
                if (!(_travelMode && label == "ahead"))
                    return FieldTracker.CheckPromptActive ? 2 : 3;
                if (FieldTracker.CheckPromptActive) return 2;
            }

            float hbx = prevBearX, hbz = prevBearZ;
            if (MathF.Abs(hbx) < 1e-3f && MathF.Abs(hbz) < 1e-3f) { hbx = adx; hbz = adz; }

            // Stage 1 — doors (only ones roughly AHEAD; never re-thread).
            if (ThreadDoor(tx, tz, threadedDoors, hbx, hbz))
            {
                var slid = PlanPoints(tx, tz, includeStart: true, approach: approach);
                if (slid != null) { pts = slid; wi = 0; prevBearX = 0; prevBearZ = 0; bestWpDist = float.MaxValue; }
                lastProgress = Environment.TickCount64;
                Log($"[AutoWalker] {reason} — recovered (door), resuming");
                return 1;
            }

            // Stage 2 — back up + fresh plan, once per leg.
            if (++rerouted <= 1)
            {
                Log($"[AutoWalker] {reason} at ({px:F0},{pz:F0}) — backing up and re-routing");
                KeyDown(SC_S); Thread.Sleep(300); KeyUp(SC_S);
                var fresh = PlanPoints(tx, tz, includeStart: true, approach: approach);
                if (fresh != null)
                {
                    pts = fresh; wi = 0;
                    prevBearX = 0; prevBearZ = 0;
                    bestWpDist = float.MaxValue;
                    lastProgress = Environment.TickCount64;
                    return 1;
                }
            }

            // Stage 3 — leg failed. Keep the black box recording.
            if (Environment.TickCount64 >= _pocketDiagDue)
            { _pocketDiagDue = Environment.TickCount64 + 25000; DumpPocketDiag(px, pz); }
            if (_travelMode)
            { _travelLegStuck = true; Log($"[AutoWalker] {reason} — leg failed, escalating"); return 0; }
            Announce($"{reason} {RemainingSpeech(px, pz, tx, tz)}");
            return 0;
        }

        try
        {
            while (true)
            {
                Thread.Sleep(PollMs);

                if (CheckStop(startMajor, startMinor, out string? why)) { Announce(why!); return; }

                px = FieldTracker.LivePlayerX; pz = FieldTracker.LivePlayerZ;
                if (float.IsNaN(px) || float.IsNaN(pz))
                {
                    if (++nanPolls >= 8) { Announce("Position lost, stopping."); return; }
                    continue;
                }
                nanPolls = 0;

                // Game's own arrival signal, every poll (the prompt zone can
                // be crossed before the final waypoint). The radius must be
                // TIGHT for doors: a door's CHECK zone spans ~6 steps along a
                // corridor, so the old pitch*1.2 ended the walk 1-6 steps
                // before the door — "ahead of / behind it" (user 2026-06-11).
                // Doors centre on the leaf; chests/stairs may stop as soon as
                // they're interactable.
                float ftx = tx - px, ftz = tz - pz;
                float fdist = MathF.Sqrt(ftx * ftx + ftz * ftz);
                float checkRadius = doorLike ? finalTol * 1.2f : pitch * 0.7f;
                if (fdist <= checkRadius && FieldTracker.CheckPromptActive) break;

                // DOOR FINAL APPROACH. The door's XZ is EXACT (the co-located
                // actor pair, confirmed 2026-06-11). Within ~1.2 cells, walk
                // STRAIGHT at it (no centering, no smoothing) and STOP at the
                // door — the player opens it themselves (user pref 2026-06-11:
                // stopping at the door / CHECK prompt is enough, don't
                // auto-press). The top-of-loop CHECK arrival usually fires
                // first; this handles reaching the frame without a prompt.
                if (doorLike && fdist <= pitch * 1.2f)
                {
                    long nowt = Environment.TickCount64;
                    if (!doorPushing)
                    {
                        doorPushing = true;
                        doorPushUntil = nowt + DoorPushMs;
                        steer.ResetMotion();
                    }

                    bool atDoor = fdist <= finalTol;
                    if (!atDoor && nowt <= doorPushUntil)
                    {
                        switch (steer.Tick(px, pz, ftx, ftz))
                        {
                            case Steerer.Ev.CamLost: Announce("Camera unreadable, stopping."); return;
                            case Steerer.Ev.Wall:
                            case Steerer.Ev.Grind:
                                if (fdist <= pitch * 0.7f) atDoor = true;   // at the closed frame
                                else goto closeStop;                        // real wall short of it
                                break;
                        }
                    }
                    if (!atDoor && nowt > doorPushUntil)
                        atDoor = fdist <= pitch * 0.7f;

                    if (!atDoor) { if (nowt > doorPushUntil) goto closeStop; continue; }

                    // AT THE DOOR/STAIRS — stop here. A door the player opens;
                    // stairs transition on contact (often already changed floor).
                    ReleaseAll();
                    if (!_travelMode) Speech.Say(label == "Stairs" ? "At the stairs." : "At the door. Press to open.", true);
                    Log($"[AutoWalker] {label} reached at ({tx:F0},{tz:F0}) fdist={fdist:F0}");
                    return;
                }

                // Waypoint consumption: only when actually REACHED (120u).
                while (wi < pts.Count - 1 && Dist(px, pz, pts[wi]) < advTol)
                { wi++; bestWpDist = float.MaxValue; }
                float dist = Dist(px, pz, pts[wi]);
                if (wi == pts.Count - 1 && dist <= finalTol) break;

                // PURE PURSUIT (movement rebuild): aim ~200u ahead ALONG the
                // path — dense 50u waypoints steer glassy-smooth this way,
                // and the body never leaves the cleared corridor line.
                int look = wi;
                {
                    float accL = dist; float lpx = pts[wi].x, lpz = pts[wi].z;
                    while (look < pts.Count - 1 && accL < 200f)
                    {
                        float seg = Dist(lpx, lpz, pts[look + 1]);
                        accL += seg; lpx = pts[look + 1].x; lpz = pts[look + 1].z; look++;
                    }
                }
                float bx = pts[look].x - px, bz = pts[look].z - pz;

                // Spoken turn cue when the route actually bends.
                if (prevBearX != 0 || prevBearZ != 0)
                {
                    float bend = RouteSpeech.SignedAngleDeg(prevBearX, prevBearZ, bx, bz);
                    if (MathF.Abs(bend) > TurnSpeechDeg)
                    {
                        int legSteps = Math.Max(1, RouteSpeech.StepsFromUnits(dist));
                        if (!_travelMode) Speech.Say($"{(bend > 0 ? "Right" : "Left")}, {legSteps}.", true);
                    }
                }
                prevBearX = bx; prevBearZ = bz;

                // Passing THROUGH a door (target beyond it): at a doorway, steer
                // straight at the waypoint (no centering) and DON'T abort on the
                // frame. The window EXTENDS while we keep closing on the target
                // (an active crossing never times out — a flat 3s cap blocked
                // slow crossings mid-doorway, live 2026-06-11). It only expires
                // after DoorThroughMs of NO progress = a closed/blocked door.
                long now = Environment.TickCount64;
                bool nearDoor = NearDoorCell(px, pz);
                if (nearDoor)
                {
                    if (throughDoorSince == 0) { throughDoorSince = now; doorProgressDist = fdist; }
                    else if (fdist < doorProgressDist - 60f)
                    { throughDoorSince = now; doorProgressDist = fdist; }   // progress → extend
                }
                else { throughDoorSince = 0; doorProgressDist = float.MaxValue; }
                bool throughDoor = nearDoor && now - throughDoorSince < DoorThroughMs;

                // NO steering biases (movement rebuild): the plan's line is
                // already the corridor centerline — walk IT, not a nudged
                // version of it. (CenterBias/CaneBias fought the path.)
                var (sbx, sbz) = (bx, bz);

                switch (steer.Tick(px, pz, sbx, sbz))
                {
                    case Steerer.Ev.CamLost: Announce("Camera unreadable, stopping."); return;
                    case Steerer.Ev.NoCam: continue;
                    case Steerer.Ev.Wall:
                    case Steerer.Ev.Grind:
                        if (throughDoor) break;   // brushing the door frame — keep going
                        // ★ SLIDE-TOLERANT STEERING (2026-07-15 — the user's own
                        // sighted-play insight): P4G movement is bump-and-slide BY
                        // DESIGN — players hold a direction and let the walls
                        // channel them; the minimap only supplies the heading.
                        // Wall contact while the waypoint distance is still
                        // DROPPING is locomotion, not failure — a whole day of
                        // logs shows us interrupting healthy 300u/s slides with
                        // stop-learn-backup-replan. Recovery now waits for a TRUE
                        // stall (~1s without approach progress).
                        if (Environment.TickCount64 - lastProgress < 1000) break;
                        switch (HandleBlocked("Blocked by a wall."))
                        { case 0: return; case 2: goto arrived; case 3: goto closeStop; }
                        continue;
                }

                // Countdown to the final target every ~5 steps (bare number).
                int remSteps = RouteSpeech.StepsFromUnits(fdist);
                if (remSteps + CountdownStepInterval <= lastAnnouncedSteps && remSteps > 1)
                {
                    lastAnnouncedSteps = remSteps;
                    if (!_travelMode) Speech.Say($"{remSteps}.", false);
                }

                // Approach watchdog (paused while pushing through a doorway —
                // brushing the frame isn't "stuck").
                if (throughDoor)
                {
                    lastProgress = Environment.TickCount64;
                }
                else if (dist < bestWpDist - 60f)
                {
                    bestWpDist = dist;
                    lastProgress = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - lastProgress > NoProgressMs)
                {
                    switch (HandleBlocked("Stuck."))
                    { case 0: return; case 2: goto arrived; case 3: goto closeStop; }
                }
            }
        }
        finally { ReleaseAll(); }

    arrived:
        ReleaseAll();
        if (!_travelMode) Speech.Say($"Arrived. {label}.", true);
        Log($"[AutoWalker] arrived at {label} ({tx:F0},{tz:F0})");
        return;

    closeStop:
        ReleaseAll();
        if (!_travelMode) Speech.Say($"{label} is right there, a step ahead.", true);
        Log($"[AutoWalker] stopped at {label} without CHECK ({tx:F0},{tz:F0})");
    }

    // ── Travel to stairs (reuses the PROVEN Walk per leg; no bee-lining) ──────

    // While true, Walk() runs SILENTLY — TravelBody owns the speech — so multi-leg
    // exploration doesn't narrate every leg / turn / countdown.
    private static volatile bool _travelMode;
    private static volatile bool _travelLegStuck;   // Walk sets this when it bails a wall-slip in travel mode

    /// <summary>
    /// Generic explore-replan travel to a (possibly moving) target — stairs, a chest, a
    /// shadow, anything. <paramref name="getTarget"/> returns the current target (have=false
    /// means "unknown, explore outward"). Reuses the PROVEN Walk per short leg (so it stays
    /// in the wall-mesh bubble and follows corridors), opens doors en route, and frontier-
    /// explores toward the target when the path isn't revealed yet. Returns TRUE if it got
    /// within <paramref name="arriveDist"/> of the target (so a caller can hand off, e.g. the
    /// shadow strike), false on give-up / cancel.
    /// </summary>
    private static bool TravelLoop(Func<float, float, (bool have, float tx, float tz)> getTarget,
                                   float arriveDist, string startMsg, string? arriveMsg)
    {
        int sMaj = FieldTracker.CurrentMajor, sMin = FieldTracker.CurrentMinor;
        float pitch = GridRouter.CellPitch(); if (pitch <= 0) pitch = WorldPerStep * 5f;
        if (!string.IsNullOrEmpty(startMsg)) Speech.Say(startMsg, true);
        {
            float px0 = FieldTracker.LivePlayerX, pz0 = FieldTracker.LivePlayerZ;
            var (h0, tx0, tz0) = getTarget(px0, pz0);
            Log($"[AutoWalker] travel start → target {(h0 ? $"({tx0:F0},{tz0:F0})" : "unknown")} from ({px0:F0},{pz0:F0})");
        }

        long deadline = Environment.TickCount64 + 180_000;
        long lastProgress = Environment.TickCount64;
        int legFails = 0;    // consecutive failed/no-progress legs → escalate to doors
        float bestDist = float.MaxValue;   // closest we've gotten to the target (real progress metric)
        var tried = new HashSet<int>();
        var deadCrossings = new HashSet<long>();       // room-border POINTS that failed to cross
        var crossTries = new Dictionary<long, int>();  // thread attempts per crossing point

        _travelMode = true;
        try
        {
            while (true)
            {
                if (CheckStop(sMaj, sMin, out string? why)) { Announce(why!); return false; }
                long now = Environment.TickCount64;
                // 15s of no progress (was 8s): each blocked leg burns ~3s and TEACHES
                // the grid a wall — give the learn-and-reroute loop room to converge
                // before declaring the target unreachable (2026-07-06 rethink).
                if (now > deadline || now - lastProgress > 25000)
                { ReleaseAll(); Speech.Say("Couldn't reach it from here. Try getting a little closer.", true); Log("[AutoWalker] travel give up"); return false; }

                float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
                if (float.IsNaN(px) || float.IsNaN(pz)) { Thread.Sleep(PollMs); continue; }

                var (have, tx, tz) = getTarget(px, pz);
                float towardX = have ? tx : px, towardZ = have ? tz : pz;

                float tgtDist2 = have ? (tx - px) * (tx - px) + (tz - pz) * (tz - pz) : float.MaxValue;
                if (tgtDist2 <= arriveDist * arriveDist)
                { ReleaseAll(); if (!string.IsNullOrEmpty(arriveMsg)) Speech.Say(arriveMsg, true); Log("[AutoWalker] travel arrived"); return true; }
                // The game's CHECK prompt lit NEAR the target = arrived — the old
                // chest walk stopped right at the prompt; travel must too, not
                // walk past a lit chest to shave the last 100u (user 2026-07-06).
                if (have && FieldTracker.CheckPromptActive
                    && tgtDist2 <= (pitch * 0.7f) * (pitch * 0.7f))
                { ReleaseAll(); if (!string.IsNullOrEmpty(arriveMsg)) Speech.Say(arriveMsg, true); Log("[AutoWalker] travel arrived (CHECK)"); return true; }

                _travelLegStuck = false;   // Walk sets this if it bails a wall-slip this leg
                // Pick the sub-goal, then take a SHORT leg toward it (the proven walker stays
                // in the wall-mesh bubble + follows corridors). Priority:
                //   1. target reachable over the known map → straight at it;
                //   2. else CROSS the door that leads toward the target — rooms join by doors,
                //      so the walker must go VIA the door, not grind the wall between them;
                //   3. else explore the frontier nearest the target.
                // If the straight line keeps grinding (no progress for 2 legs), STOP re-driving
                // it — fall through to the door/frontier branch to try a DIFFERENT way around.
                // ROOM-GRAPH TRAVEL FIRST (user design 2026-07-06): think in
                // ROOMS — BFS the minimap's own roomId graph to the target's
                // room and cross the NEXT door on that path. A door that fails
                // to cross twice goes DEAD and the next BFS routes around it,
                // so a dead-end room is exited back through its entry door and
                // another way is tried (systematic backtracking). This runs
                // BEFORE the direct-leg branches: crossing rooms is a DOOR
                // problem, not a straight-line problem (07-06 log: the
                // "complete" fine route straight-lined a phantom-clear wall
                // for whole walks while this branch never got a turn).
                // (Near targets skip room planning outright — within ~a cell the
                // room resolver's ring search can misattribute a wall-hugging
                // chest to the NEXT room and walk away from it.)
                // FIXED-layout floors SKIP the room graph (2026-07-12): their minimap
                // is degenerate — no flag-2 walls and junk roomIds that fragment one
                // floor into phantom rooms (live log: "room path 18 doors" on 65/2).
                // The pre-v2 machinery below (fine grid with PERSISTED walls → minimap
                // leg → greedy door → frontier) is what historically handled them.
                if (have && legFails < 4 && tgtDist2 > (pitch * 1.2f) * (pitch * 1.2f)
                    && !DungeonGrid.IsFixedFloor(FieldTracker.CurrentMajor, FieldTracker.CurrentMinor)
                    && RoomGraph.TryDoorPlan(px, pz, tx, tz, deadCrossings, out var hops)
                    && hops.Count > 0)
                {
                    var hop = hops[0];
                    // A real door actor near the boundary carries the CHECK
                    // prompt — thread its exact XZ; a bare boundary (open
                    // archway) threads the midpoint (no prompt = walks through).
                    float hx = hop.X, hz = hop.Z;
                    float snap2 = (pitch * 1.2f) * (pitch * 1.2f);
                    bool doorActor = false;
                    foreach (var (ddx, ddz) in DungeonNav.Doors())
                    {
                        float d2 = (ddx - hx) * (ddx - hx) + (ddz - hz) * (ddz - hz);
                        if (d2 < snap2) { snap2 = d2; hx = ddx; hz = ddz; doorActor = true; }
                    }

                    float hd2 = (hx - px) * (hx - px) + (hz - pz) * (hz - pz);
                    if (hd2 > (pitch * 1.4f) * (pitch * 1.4f))
                    {
                        // Not at the crossing yet — take a short leg toward it.
                        Log($"[AutoWalker] travel: room path {hops.Count} doors, heading to ({hx:F0},{hz:F0})");
                        float wx1, wz1;
                        bool got = DungeonGrid.TryStepAlong(px, pz, hx, hz, SeenHorizonU, out wx1, out wz1);
                        if (!got) got = GridRouter.StepToward(px, pz, hx, hz, 2, out wx1, out wz1);
                        if (got) Walk("ahead", wx1, wz1, null);
                        else Walk("ahead", hx, hz, null);
                    }
                    else
                    {
                        Log($"[AutoWalker] travel: room hop → room {hop.ToRoom} via ({hx:F0},{hz:F0})");
                        ThreadDoorAt(hx, hz, towardX, towardZ, open: true);
                        int tries = crossTries.TryGetValue(hop.CrossKey, out int tv) ? tv + 1 : 1;
                        crossTries[hop.CrossKey] = tries;
                        float apx = FieldTracker.LivePlayerX, apz = FieldTracker.LivePlayerZ;
                        bool crossed = !float.IsNaN(apx)
                            && RoomGraph.TryRoomAt(apx, apz, out ushort nowRoom) && nowRoom == hop.ToRoom;
                        if (crossed)
                        {
                            crossTries.Remove(hop.CrossKey);
                            lastProgress = Environment.TickCount64;   // a new room IS progress
                        }
                        // A border MIDPOINT gets one try (most border cells are
                        // wall — move on to the next candidate along the border);
                        // a real DOOR actor gets two (opening can take a retry).
                        else if (tries >= (doorActor ? 2 : 1))
                        {
                            deadCrossings.Add(hop.CrossKey);
                            // Ruling out a crossing point is knowledge, not a
                            // stall — keep the search alive while candidates
                            // remain (the 15s cutoff killed the gap hunt mid-way).
                            lastProgress = Environment.TickCount64;
                            Log($"[AutoWalker] travel: crossing {hop.CrossKey:X} dead — next candidate");
                        }
                    }
                }
                // SAME ROOM (or rooms unknown): direct legs — the fine grid
                // first (it knows the LEARNED walls), then the coarse minimap.
                else if (have && legFails < 2
                    && DungeonGrid.TryStepAlong(px, pz, tx, tz, SeenHorizonU, out float flx, out float flz))
                {
                    // AT THE SNAPPED END (2026-07-14 night, sim-proven): the leg
                    // point equals our position = we stand on the CLOSEST
                    // REACHABLE ground to the target. A door mark's last stretch
                    // is a doorway inset the baked map calls wall but is
                    // physically walkable — finish with one straight push (no
                    // recoveries, CHECK/prompt ends it) instead of looping the
                    // same zero-length leg forever.
                    if ((flx - px) * (flx - px) + (flz - pz) * (flz - pz) < 120f * 120f)
                    {
                        if (tgtDist2 <= (pitch * 1.6f) * (pitch * 1.6f))
                        {
                            Log($"[AutoWalker] travel: at closest reachable point — final push to ({tx:F0},{tz:F0})");
                            long pushUntil = Environment.TickCount64 + 3000;
                            while (Environment.TickCount64 < pushUntil && !_cancelRequested
                                   && Utils.GameHasFocus() && !FieldInterrupted(sMaj, sMin))
                            {
                                float cpx2 = FieldTracker.LivePlayerX, cpz2 = FieldTracker.LivePlayerZ;
                                if (float.IsNaN(cpx2)) break;
                                if (FieldTracker.CheckPromptActive) break;
                                if ((tx - cpx2) * (tx - cpx2) + (tz - cpz2) * (tz - cpz2) <= arriveDist * arriveDist) break;
                                if (!DriveToward(tx, tz, 150f, 450)) break;   // blocked = as far as it goes
                            }
                            ReleaseAll();
                            if (!string.IsNullOrEmpty(arriveMsg)) Speech.Say(arriveMsg, true);
                            else Speech.Say("It's right ahead.", true);
                            Log("[AutoWalker] travel arrived (closest reachable + push)");
                            return true;
                        }
                        ReleaseAll();
                        Speech.Say("This is as close as I can get from here.", true);
                        Log("[AutoWalker] travel: closest reachable point reached, target still far — stopping honestly");
                        return false;
                    }
                    Log($"[AutoWalker] travel: fine leg via ({flx:F0},{flz:F0})");
                    Walk("ahead", flx, flz, null);
                }
                // The coarse minimap leg is WALL-BLIND (no learned walls, no tile
                // geometry) — on a baked floor the fine grid already covers every
                // corridor, so when a fine route fails there the coarse leg can only
                // re-drive the same blocked line forever (65/2 barrier loop,
                // 2026-07-13). Baked floors: fine grid or bust.
                else if (have && legFails < 2 && !DungeonGrid.HasFloorMap
                    && GridRouter.FindRoute(px, pz, tx, tz) != null)
                {
                    if (GridRouter.StepToward(px, pz, tx, tz, 2, out float wx, out float wz))
                    {
                        // The coarse leg is WALL-BLIND — never drive it through a
                        // wall the fine grid KNOWS (the corner-wedge of 07-14).
                        if (DungeonGrid.SegmentClearOfKnownWalls(px, pz, wx, wz))
                        { Log($"[AutoWalker] travel: leg to target via ({wx:F0},{wz:F0})"); Walk("ahead", wx, wz, null); }
                        else
                        { legFails = 2; Log("[AutoWalker] travel: coarse leg crosses a KNOWN wall — escalating to doors"); }
                    }
                }
                else
                {
                    // Nearest reachable, untried door closest to the target = the way between rooms.
                    float bdx = 0, bdz = 0, bsc = float.MaxValue; bool haveDoor = false;
                    foreach (var (dx, dz) in DungeonNav.Doors())
                    {
                        if (!MinimapTracker.WorldToCell(dx, dz, out int dr2, out int dc2)) continue;
                        if (tried.Contains(dr2 * MinimapTracker.COLS + dc2)) continue;
                        if (GridRouter.FindRoute(px, pz, dx, dz) == null) continue;   // can't reach this door yet
                        float sc = MathF.Sqrt((dx - px) * (dx - px) + (dz - pz) * (dz - pz))           // reach the door
                                 + MathF.Sqrt((dx - towardX) * (dx - towardX) + (dz - towardZ) * (dz - towardZ));  // then onward to target
                        if (sc < bsc) { bsc = sc; bdx = dx; bdz = dz; haveDoor = true; }
                    }
                    if (haveDoor)
                    {
                        // Walk the FULL walker to the door's ON-AXIS FRONT POINT
                        // (2026-07-12, the corridor back-and-forward trap): the old
                        // coarse StepToward was BLIND to the learned walls, so it
                        // drove back into the same stamped bar every cycle (identical
                        // blocked coords each pass) while the door threading never ran
                        // — the walker could never get within 1.4 cells of the door.
                        // The full Walk plans on the fine grid (knows every learned
                        // wall), grinds-and-learns where it must, and converges on a
                        // FIXED goal instead of re-deciding each loop.
                        float fpx = bdx, fpz = bdz;
                        if (DungeonNav.TryDoorAxis(bdx, bdz, out float dnx, out float dnz))
                        {
                            float side = MathF.Sign((px - bdx) * dnx + (pz - bdz) * dnz);
                            if (side == 0f) side = 1f;
                            fpx = bdx + dnx * side * pitch * 0.25f;
                            fpz = bdz + dnz * side * pitch * 0.25f;
                        }
                        Log($"[AutoWalker] travel: head to door @({bdx:F0},{bdz:F0}) via front ({fpx:F0},{fpz:F0})");
                        Walk("ahead", fpx, fpz, null);
                        float npx = FieldTracker.LivePlayerX, npz = FieldTracker.LivePlayerZ;
                        if (!float.IsNaN(npx) && (bdx - npx) * (bdx - npx) + (bdz - npz) * (bdz - npz) <= (pitch * 1.4f) * (pitch * 1.4f))
                        {
                            ThreadDoorAt(bdx, bdz, towardX, towardZ, open: true);   // open + pass into the next room
                            if (MinimapTracker.WorldToCell(bdx, bdz, out int br, out int bc)) tried.Add(br * MinimapTracker.COLS + bc);
                        }
                    }
                    // Frontier exploration is for UNKNOWN layouts; a baked floor is
                    // fully known, and frontier legs use the same wall-blind coarse
                    // stepper — skip so a truly sealed route ends honestly instead.
                    else if (!DungeonGrid.HasFloorMap
                        && GridRouter.FindFrontier(px, pz, towardX, towardZ, tried, out float fx, out float fz))
                    {
                        if (GridRouter.StepToward(px, pz, fx, fz, 2, out float wx, out float wz)
                            && DungeonGrid.SegmentClearOfKnownWalls(px, pz, wx, wz))
                        { Log($"[AutoWalker] travel: explore frontier ({fx:F0},{fz:F0})"); Walk("ahead", wx, wz, null); }
                        else if (MinimapTracker.WorldToCell(fx, fz, out int br, out int bc)) tried.Add(br * MinimapTracker.COLS + bc);
                    }
                    else
                    {
                        DungeonGrid.LogGridPatch(px, pz);   // black box: why was there no subgoal?
                        ReleaseAll(); Speech.Say("Can't find a way there.", true); Log("[AutoWalker] travel no subgoal"); return false;
                    }
                }

                // Progress = the player got meaningfully CLOSER to the target — NOT merely "moved".
                // Circling a door (the Bathhouse loop) keeps moving without approaching, so a
                // movement-based metric never gives up and never switches route. Distance-based:
                // only resetting on real gain means a circle trips both the 8s give-up AND the
                // stuckLegs route-switch (so it tries another door/frontier, then bails cleanly).
                float ax = FieldTracker.LivePlayerX, az = FieldTracker.LivePlayerZ;
                float newDist = (have && !float.IsNaN(ax))
                    ? MathF.Sqrt((tx - ax) * (tx - ax) + (tz - az) * (tz - az)) : float.MaxValue;
                // Progress threshold matched to SEE-THEN-STEP legs (480u): the
                // old pitch*0.5 (600u) would call every single short leg
                // "no progress" and starve the smart branches.
                if (newDist < bestDist - 260f)
                { bestDist = newDist; lastProgress = Environment.TickCount64; legFails = 0; }
                else if (_travelLegStuck) legFails += 2;       // a STALLED leg → next stop, doors
                // Legs that ARRIVE without closing on the target are DETOURS
                // (a corridor running perpendicular to the goal) — never punish
                // them. See-then-step legs are short: two flat detour legs used
                // to lock the planner out entirely ("no subgoal" in a wide-open
                // crumb-proven corridor, 2026-07-15). True circling is caught by
                // the no-progress timeout, not by counting flat legs.
            }
        }
        finally { _travelMode = false; ReleaseAll(); }
    }

    // ── Hunt (Shadows — live target, stealth, strike) ────────────────────────

    // Autonomous approach-and-strike (Backspace on a Shadow). Walks to the
    // shadow's BACK (re-aimed every poll so it circles to the rear as the
    // shadow turns), drives into it, and swings in the connect sweet-spot.
    // Commits to a face-to-face swing if it keeps wheeling to face us (a
    // losing circle race ends with OUR back exposed). Useful for normal
    // fights, when stuck, and as a planning tool (user 2026-06-11). The
    // browser announces each shadow's facing so the user can pick a good one.
    private static void Hunt(float ix, float iz)
    {
        int startMajor = FieldTracker.CurrentMajor, startMinor = FieldTracker.CurrentMinor;
        float pitch = GridRouter.CellPitch();
        if (pitch <= 0) pitch = WorldPerStep * 5f;
        float advTol = pitch * 0.28f;

        float sx = ix, sz = iz, sfx = 0, sfz = 0;
        bool TrackShadow()
        {
            var list = DungeonNav.ShadowsWithFacing();
            float best = ShadowTrackRange * ShadowTrackRange;
            bool found = false;
            float nx = sx, nz = sz, nfx = 0, nfz = 0;
            foreach (var (x, z, fx, fz) in list)
            {
                float dx = x - sx, dz = z - sz;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; nx = x; nz = z; nfx = fx; nfz = fz; found = true; }
            }
            if (found) { sx = nx; sz = nz; sfx = nfx; sfz = nfz; }
            return found;
        }

        if (!TrackShadow()) { Speech.Say("No shadow nearby.", true); return; }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px)) { Speech.Say("Position unavailable.", true); return; }
        int steps0 = RouteSpeech.StepsFromUnits(
            MathF.Sqrt((sx - px) * (sx - px) + (sz - pz) * (sz - pz)));
        Log($"[AutoWalker] hunt start shadow=({sx:F0},{sz:F0}) steps={steps0}");
        Speech.Say($"Walking to shadow, {steps0} step{(steps0 == 1 ? "" : "s")}.", true);

        CalibrateSign();

        var steer = new Steerer();
        bool haveAxes = GridRouter.TryGetStepAxes(out var rowAxis, out var colAxis);
        bool announcedStrike = false;
        long strikeSince = 0, lastSwing = 0, lastAmbushWarn = 0;
        int nanPolls = 0, blockedCount = 0;
        List<(float x, float z)>? plan = null;
        int wi = 0;
        float goalX = float.MaxValue, goalZ = float.MaxValue;
        long closeFacingSince = 0;

        try
        {
            while (true)
            {
                Thread.Sleep(PollMs);

                if (CheckStop(startMajor, startMinor, out string? why))
                {
                    if (why == "Battle.") { ReleaseAll(); Speech.Say("Engaged!", true); Log("[AutoWalker] hunt → battle"); return; }
                    Announce(why!);
                    return;
                }

                px = FieldTracker.LivePlayerX; pz = FieldTracker.LivePlayerZ;
                if (float.IsNaN(px) || float.IsNaN(pz))
                {
                    if (++nanPolls >= 8) { Announce("Position lost, stopping."); return; }
                    continue;
                }
                nanPolls = 0;

                if (!TrackShadow()) { Announce("Shadow lost."); return; }

                float dxs = sx - px, dzs = sz - pz;
                float dist = MathF.Sqrt(dxs * dxs + dzs * dzs);
                bool facingUs = (sfx != 0 || sfz != 0) && sfx * -dxs + sfz * -dzs > 0;

                long now = Environment.TickCount64;

                // Ambush watch: another shadow closing on the player's back.
                if (now - lastAmbushWarn > 4000)
                {
                    foreach (var (ox, oz, _, _) in DungeonNav.ShadowsWithFacing())
                    {
                        float tdx = ox - sx, tdz = oz - sz;
                        if (tdx * tdx + tdz * tdz < 400f * 400f) continue;
                        float pdx = ox - px, pdz = oz - pz;
                        if (pdx * pdx + pdz * pdz < 1100f * 1100f)
                        { lastAmbushWarn = now; Speech.Say("Another shadow close!", false); break; }
                    }
                }

                if (!announcedStrike && dist <= StealthRange)
                {
                    announcedStrike = true;
                    strikeSince = now;
                    Speech.Say("Closing in.", true);
                }

                // Behind point = a step behind the shadow along its facing.
                bool haveFacing = sfx != 0 || sfz != 0;
                float bhx = haveFacing ? sx - sfx * BehindOffset : sx;
                float bhz = haveFacing ? sz - sfz * BehindOffset : sz;

                // Commit when getting behind stalls — fast when it's close and
                // facing us (don't circle into a back-attack), slow otherwise.
                if (facingUs && dist <= StrikeRange)
                { if (closeFacingSince == 0) closeFacingSince = now; }
                else closeFacingSince = 0;
                bool danger = closeFacingSince != 0 && now - closeFacingSince > DangerCommitMs;
                bool commit = announcedStrike && (now - strikeSince > CommitMs || danger);

                // Behind & close → drive into its back; else circle to behind.
                bool behindAndClose = !facingUs && dist <= StrikeRange;
                float gx = behindAndClose || commit ? sx : bhx;
                float gz = behindAndClose || commit ? sz : bhz;

                if (plan == null || wi >= plan.Count
                    || (goalX - gx) * (goalX - gx) + (goalZ - gz) * (goalZ - gz) > 500f * 500f)
                {
                    plan = PlanPoints(gx, gz);
                    wi = 0;
                    goalX = gx; goalZ = gz;
                }

                float bx, bz;
                if (plan != null && plan.Count > 0)
                {
                    while (wi < plan.Count - 1 && Dist(px, pz, plan[wi]) < advTol) wi++;
                    bx = plan[wi].x - px; bz = plan[wi].z - pz;
                }
                else { bx = gx - px; bz = gz - pz; }

                var (sbx, sbz) = dist < pitch
                    ? (bx, bz)
                    : CenterBias(px, pz, bx, bz, pitch, haveAxes, rowAxis, colAxis);
                if (dist >= pitch) (sbx, sbz) = CaneBias(sbx, sbz);

                switch (steer.Tick(px, pz, sbx, sbz))
                {
                    case Steerer.Ev.CamLost: Announce("Camera unreadable, stopping."); return;
                    case Steerer.Ev.Wall:
                    case Steerer.Ev.Grind:
                        steer.Release();
                        plan = null;
                        if (++blockedCount > 6) { Announce("Can't reach it."); return; }
                        KeyDown(SC_S); Thread.Sleep(220); KeyUp(SC_S);
                        break;
                }

                // Sweet-spot lunge-swing: in the connect band, facing the
                // shadow squarely, and behind it (advantage) or committed.
                var (gfx, gfz) = FieldTracker.PlayerForwardViaGaze();
                float faceErr = (gfx == 0 && gfz == 0)
                    ? 0f : MathF.Abs(RouteSpeech.SignedAngleDeg(gfx, gfz, dxs, dzs));
                if (dist >= SwingNear && dist <= SwingFar && faceErr <= SwingFaceDeg
                    && (!facingUs || commit) && now - lastSwing >= SwingIntervalMs)
                {
                    lastSwing = now;
                    KeyDown(SC_FORWARD);
                    KeyDown(SC_ENTER);
                    Thread.Sleep(50);
                    KeyUp(SC_ENTER);
                    Thread.Sleep(60);
                    KeyUp(SC_FORWARD);
                    Log($"[AutoWalker] swing d={dist:F0} face={faceErr:F0}° behind={!facingUs} commit={commit}");
                }
            }
        }
        finally { ReleaseAll(); }
    }

    // ── Planning ─────────────────────────────────────────────────────────────

    private static List<(float x, float z)>? PlanPoints(float tx, float tz, bool includeStart = false,
                                                        (float x, float z)? approach = null)
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return null;

        var (rx, rz) = approach ?? (tx, tz);

        // Door cells are tracked for the NearDoorCell frame-suppression, but
        // NOT made walkable in A* — coarse-cell routing through doors produced
        // backwards routes (live 2026-06-11). Route normally; if the target is
        // unreachable (behind a door), go VIA the door: route to the door, then
        // a STRAIGHT leg through the opening to the target.
        var doors = DungeonNav.Doors();
        GridRouter.SetDoorCells(doors);
        GridRouter.AllowDoorRouting(false);

        // THE PATHFINDER (v1.4.0 item A): plan on the fine wall-mesh grid first —
        // it sees the sub-cell walls the minimap can't (DUNGEON_AUTOWALK.md §4).
        // Only a COMPLETE fine route is authoritative; a partial one means
        // something (usually a closed door) blocks the way, and the minimap +
        // door-threading fallback below owns that case.
        if (DungeonGrid.TryPlan(px, pz, rx, rz, includeStart, out var fine, out bool fineDone)
            && fineDone && fine.Count > 0)
        {
            if (approach != null) fine.Add(approach.Value);
            fine.Add((tx, tz));
            // NO WallMesh.Smooth here: it string-pulls against the (often
            // near-empty) live mesh index, re-collapsing the horizon-capped
            // fine path into the very straight lines the cap forbids. The
            // fine grid already simplified against ALL our wall knowledge.
            Log($"[AutoWalker] fine route: {fine.Count} pts to ({tx:F0},{tz:F0})");
            return fine;
        }

        // PARTIAL fine route (2026-07-14, travel only): when no complete route
        // exists, a partial one still bends around every KNOWN wall toward the
        // target — strictly better than the wall-blind coarse route below.
        // Walk it to its end; the travel loop re-evaluates from there with
        // fresher knowledge. (Not for one-shot walks: their end-of-route speaks
        // "Arrived", which would lie at a partial endpoint.)
        if (_travelMode && !fineDone && fine != null && fine.Count > 0)
        {
            var (pex, pez) = fine[^1];
            float pd0 = MathF.Sqrt((rx - px) * (rx - px) + (rz - pz) * (rz - pz));
            float pd1 = MathF.Sqrt((rx - pex) * (rx - pex) + (rz - pez) * (rz - pez));
            if (pd1 < pd0 - 300f)
            {
                Log($"[AutoWalker] fine route (partial): {fine.Count} pts toward ({tx:F0},{tz:F0})");
                return fine;
            }
        }

        // No minimap grid (open hub like the TV-world lobby): A* can't route,
        // but the area is open and targets are close — steer straight at the
        // target. ONE waypoint; the Steerer + wall-slide recovery handle the
        // rest, arrival on the CHECK prompt / final radius. This is distinct
        // from "grid exists but the target is unreachable" (a real dungeon),
        // which still returns null below so the walker reports it honestly.
        if (!GridRouter.HasGrid())
        {
            Log($"[AutoWalker] no grid — straight-line to ({tx:F0},{tz:F0})");
            return new List<(float, float)> { (tx, tz) };
        }

        var route = GridRouter.FindRoute(px, pz, rx, rz);
        List<(float x, float z)> pts;
        if (route != null)
        {
            pts = GridRouter.PathWorld(route, includeStart);
            if (approach != null) pts.Add(approach.Value);
            pts.Add((tx, tz));
        }
        else
        {
            // Behind a door: pick the door between us and the target, route to
            // it, then go straight through the opening to the target.
            var door = PickDoorToward(px, pz, tx, tz, doors);
            if (door == null) return null;
            var toDoor = GridRouter.FindRoute(px, pz, door.Value.x, door.Value.z);
            pts = toDoor != null ? GridRouter.PathWorld(toDoor, includeStart) : new();
            pts.Add(door.Value);          // exact opening
            pts.Add((tx, tz));            // straight through to the target
            Log($"[AutoWalker] via door ({door.Value.x:F0},{door.Value.z:F0}) → target ({tx:F0},{tz:F0})");
            return pts;                   // no smoothing — the through-door leg must stay straight
        }

        // Standing at a door-frame edge, the first leg can clip the frame and
        // read as a wall. If the opening move is blocked, lead with the
        // current cell's safe center — a small sidestep into the opening.
        if (!includeStart && pts.Count > 0 && WallMesh.EnsureBuilt()
            && WallMesh.SegmentBlocked(px, pz, pts[0].x, pts[0].z, 170f))
        {
            var withStart = GridRouter.PathWorld(route, includeStart: true);
            if (withStart.Count > 0) pts.Insert(0, withStart[0]);
        }

        // String-pull against the real wall geometry: collapse the cell-center
        // zigzag into straight legs that provably miss walls.
        pts = WallMesh.Smooth(px, pz, pts, 170f);
        return pts;
    }

    /// <summary>
    /// Corridor centering: when the player drifts toward a wall-adjacent cell
    /// edge, blend a push back toward the centerline into the bearing.
    /// </summary>
    // ── The CANE (2026-07-13, user design call: "map = the navigator, live rays
    // = the feet"). Every steering poll casts the game's OWN collision rays
    // along the DRIVE bearing (FieldTracker.TryComputeWallDistancesOriented —
    // the same mesh that physically stops the body, so it sees scene-actor
    // barriers no map has). Wall closing ahead → lean toward the wider side
    // BEFORE contact; two walls near → self-center between them. This replaces
    // discovering geometry with the walker's face.
    private const float CaneAheadU = 260f;   // start veering this far from a wall ahead
    private const float CaneTouchU = 95f;    // basically touching — blocked-handling owns it
    private static long _caneDue;
    private static float _cF = float.PositiveInfinity, _cL = float.PositiveInfinity, _cR = float.PositiveInfinity;
    private static float _canePushSmooth;

    private static (float x, float z) CaneBias(float bx, float bz)
    {
        float bm = MathF.Sqrt(bx * bx + bz * bz);
        if (bm < 1e-3f) return (bx, bz);
        float fx = bx / bm, fz = bz / bm;
        long now = Environment.TickCount64;
        if (now >= _caneDue)   // rays are a mesh scan — refresh at ~8Hz, steer at 20Hz
        {
            _caneDue = now + 120;
            if (!FieldTracker.TryComputeWallDistancesOriented(fx, fz, out _cF, out _, out _cR, out _cL))
            { _cF = _cR = _cL = float.PositiveInfinity; }
        }
        float rx = fz, rz = -fx;   // right of the drive bearing (matches the ray frame)
        float push = 0f;

        // Obstacle ahead → veer toward the wider side, harder the closer it is.
        if (_cF < CaneAheadU)
        {
            float t = Math.Clamp(1f - (_cF - CaneTouchU) / (CaneAheadU - CaneTouchU), 0f, 1f);
            push += (_cR >= _cL ? 1f : -1f) * t * 1.2f;
        }
        // Corridor centering, GENTLE: a strong equalizer made the walk visibly
        // zigzag ("a little drunk man", user 2026-07-13) — small gain, a
        // deadband around center, and a low-pass below so corrections glide.
        if (_cL < 420f && _cR < 420f)
        {
            float c = Math.Clamp((_cR - _cL) / (_cR + _cL + 1f), -1f, 1f);
            if (MathF.Abs(c) > 0.18f) push += c * 0.18f;
        }
        else if (_cL < 130f) push += 0.15f;
        else if (_cR < 130f) push -= 0.15f;

        push = Math.Clamp(push, -1.4f, 1.4f);
        _canePushSmooth = _canePushSmooth * 0.6f + push * 0.4f;
        if (MathF.Abs(_canePushSmooth) < 0.03f) return (bx, bz);
        return (bx + rx * bm * _canePushSmooth * 0.7f, bz + rz * bm * _canePushSmooth * 0.7f);
    }

    // Wedge tracking (2026-07-13): consecutive blocks at the same coordinate =
    // the body is pinned; planning is useless until it physically moves.
    private static long _pocketDiagDue;   // [PocketDiag] black-box throttle

    // ★ SEE-THEN-STEP (2026-07-15, the user's design): the game streams TRUE
    // geometry only ~600u around the body; a travel leg must NEVER commit
    // beyond what has been SEEN. Old legs were pitch*2 = 2400u — four times
    // deeper than sight — so every walk planned through unmeasured space and
    // met the railing at full speed (the bubble-chase, the whole 07-14 arc).
    // Legs capped at the seen circle: walk, stop, look (the fan repaints),
    // step again — the minimap experience, mechanized.
    private const float SeenHorizonU = 480f;

    /// <summary>The body is WEDGED (blocked twice at the same spot). Rank all 8
    /// directions by ray openness and walk the first that actually MOVES the
    /// body a couple of body-widths. Deliberately target-agnostic: freedom
    /// first, route second. True if the body moved.</summary>
    
    /// <summary>[PocketDiag] TEMP (2026-07-14, the 45/0 maze-pocket hunt): when a
    /// wedge ABORTS a walk, dump what the pocket physically is — a 16-direction
    /// ray fan plus the minimap cell under the body (sprite/rotation/room/mask)
    /// and its 4 neighbours. One shot per abort; remove after the pocket's root
    /// cause is identified (prop cluster vs lying tile walls vs door frame).</summary>
    private static void DumpPocketDiag(float px, float pz)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                float a = i * MathF.PI / 8f;
                float dx = MathF.Cos(a), dz = MathF.Sin(a);
                bool ok = FieldTracker.TryComputeWallDistancesOriented(dx, dz, out float f, out _, out _, out _);
                sb.Append($"{i * 225 / 10}°={(ok ? ((int)MathF.Min(f, 9999)).ToString() : "?")} ");
            }
            Log($"[PocketDiag] wedge abort at ({px:F0},{pz:F0}) ray fan (deg=dist): {sb}");
            if (MinimapTracker.WorldToCell(px, pz, out int pr, out int pc))
            {
                var raw = new byte[16];
                bool haveRaw = MinimapTracker.ReadCellRawBytes(pr, pc, raw);
                if (MinimapTracker.ReadCell(pr, pc, out var ci))
                    Log($"[PocketDiag] cell r{pr}c{pc}: flag={ci.Flag} sprite=0x{ci.Sprite:X2} mod=0x{ci.Modifier:X2} " +
                        $"room={ci.RoomId} w={ci.Width} h={ci.Height} mask=0x{(haveRaw ? raw[10] : 0):X2}");
                var nsb = new System.Text.StringBuilder();
                foreach (var (dr, dc, name) in new[] { (-1, 0, "r-1"), (1, 0, "r+1"), (0, -1, "c-1"), (0, 1, "c+1") })
                    if (MinimapTracker.ReadCell(pr + dr, pc + dc, out var nc2))
                        nsb.Append($"{name}:flag={nc2.Flag},spr=0x{nc2.Sprite:X2} ");
                if (nsb.Length > 0) Log($"[PocketDiag] neighbours: {nsb}");
                if (MinimapTracker.CellToWorld(pr, pc, out float ccx, out float ccz))
                    Log($"[PocketDiag] offset inside cell: ({px - ccx:F0},{pz - ccz:F0}) of pitch {GridRouter.CellPitch():F0}");
            }
            // Which layer claims what around the pin — the raster-vs-plan truth.
            DungeonGrid.LogGridPatch(px, pz);
            // The raw scene collision near the pin: what does the blocking
            // geometry actually look like (vertex coords + slope)? A tri the
            // rays see but the fold drops shows up here with |ny| > 0.7.
            int shown = 0;
            FieldTracker.VisitWallTrianglesInScene(t =>
            {
                if (shown >= 24 || t.Length < 9) return;
                float cx = (t[0] + t[3] + t[6]) / 3f, cz = (t[2] + t[5] + t[8]) / 3f;
                if ((cx - px) * (cx - px) + (cz - pz) * (cz - pz) > 500f * 500f) return;
                float ux = t[3] - t[0], uy = t[4] - t[1], uz = t[5] - t[2];
                float vx = t[6] - t[0], vy = t[7] - t[1], vz = t[8] - t[2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                float nyn = len > 1e-6f ? ny / len : 99f;
                shown++;
                Log($"[PocketDiag] tri ({t[0]:F0},{t[1]:F0},{t[2]:F0})({t[3]:F0},{t[4]:F0},{t[5]:F0})({t[6]:F0},{t[7]:F0},{t[8]:F0}) ny={nyn:F2}");
            });
            Log($"[PocketDiag] scene tris within 500u: {shown} shown (cap 24)");
        }
        catch (Exception e) { Log($"[PocketDiag] failed: {e.Message}"); }
    }

    /// <summary>Blocked → LOOK with the rays and sidestep along the open side
    /// until the way ahead opens (the blind-player move the user described:
    /// bump, feel, step aside a little, continue straight). True = an opening
    /// was found and the caller should resume toward its waypoint.</summary>
    
    private static (float x, float z) CenterBias(float px, float pz, float bx, float bz,
        float pitch, bool haveAxes, (float x, float z) rowAxis, (float x, float z) colAxis)
    {
        if (!haveAxes
            || !MinimapTracker.WorldToCell(px, pz, out int prow, out int pcol)
            || !MinimapTracker.CellToWorld(prow, pcol, out float ccx, out float ccz))
            return (bx, bz);

        float ox = px - ccx, oz = pz - ccz;
        float offRow = ox * rowAxis.x + oz * rowAxis.z;
        float offCol = ox * colAxis.x + oz * colAxis.z;
        float edge = pitch * 0.18f;
        float span = pitch * 0.14f;
        float pushX = 0, pushZ = 0;
        if (offRow > edge && !GridRouter.CellWalkable(prow + 1, pcol))
        { float t = MathF.Min(1f, (offRow - edge) / span); pushX -= rowAxis.x * t; pushZ -= rowAxis.z * t; }
        if (-offRow > edge && !GridRouter.CellWalkable(prow - 1, pcol))
        { float t = MathF.Min(1f, (-offRow - edge) / span); pushX += rowAxis.x * t; pushZ += rowAxis.z * t; }
        if (offCol > edge && !GridRouter.CellWalkable(prow, pcol + 1))
        { float t = MathF.Min(1f, (offCol - edge) / span); pushX -= colAxis.x * t; pushZ -= colAxis.z * t; }
        if (-offCol > edge && !GridRouter.CellWalkable(prow, pcol - 1))
        { float t = MathF.Min(1f, (-offCol - edge) / span); pushX += colAxis.x * t; pushZ += colAxis.z * t; }
        if (pushX == 0 && pushZ == 0) return (bx, bz);

        float bmag = MathF.Sqrt(bx * bx + bz * bz);
        return (bx + pushX * bmag * 0.6f, bz + pushZ * bmag * 0.6f);
    }

    // ── 8-way key combos ─────────────────────────────────────────────────────

    private static readonly (float offset, byte[] keys)[] Combos =
    {
        (0f,    new[] { SC_W }),
        (45f,   new[] { SC_W, SC_D }),
        (90f,   new[] { SC_D }),
        (135f,  new[] { SC_S, SC_D }),
        (180f,  new[] { SC_S }),
        (-135f, new[] { SC_S, SC_A }),
        (-90f,  new[] { SC_A }),
        (-45f,  new[] { SC_W, SC_A }),
    };

    private static byte[] ComboFor(float offsetDeg)
    {
        float o = Norm(offsetDeg);
        foreach (var (offset, k) in Combos)
            if (MathF.Abs(Norm(o - offset)) < 1f) return k;
        return Combos[0].keys;
    }

    private static float Norm(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg <= -180f) deg += 360f;
        return deg;
    }

    private static bool SameKeys(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void SwitchKeys(byte[] from, byte[] to)
    {
        foreach (byte k in from) if (Array.IndexOf(to, k) < 0) KeyUp(k);
        foreach (byte k in to) if (Array.IndexOf(from, k) < 0) KeyDown(k);
    }

    // ── Stop conditions ──────────────────────────────────────────────────────

    private static bool CheckStop(int startMajor, int startMinor, out string? why)
    {
        why = null;
        if (_cancelRequested) { why = "Cancelled."; return true; }
        // Alt-tabbed away: stop. Synthesized WASD keystrokes go to the FOCUSED
        // window, so continuing would inject movement keys into the other app.
        if (!Utils.GameHasFocus()) { why = "Lost focus."; return true; }
        for (int i = 0; i < CancelVKeys.Length; i++)
        {
            bool down = IsKeyDown(CancelVKeys[i]);
            if (!down) { _cancelArmed[i] = true; continue; }   // released → now armable
            if (_cancelArmed[i])
            { why = $"Cancelled (key 0x{CancelVKeys[i]:X2})."; return true; }
            // down but not armed = it was held at launch; ignore until released.
        }
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        if (FieldTracker.IsBattleMajor(major)) { why = "Battle."; return true; }
        if (major != startMajor || minor != startMinor) { why = "Floor changed."; return true; }
        return false;
    }

    private static void Announce(string why)
    {
        ReleaseAll();
        Speech.Say(why, true);
        Log($"[AutoWalker] stop: {why}");
    }

    private static string RemainingSpeech(float px, float pz, float tx, float tz)
    {
        float dx = tx - px, dz = tz - pz;
        int steps = RouteSpeech.StepsFromUnits(MathF.Sqrt(dx * dx + dz * dz));
        return $"{steps} step{(steps == 1 ? "" : "s")} remaining.";
    }

    private static float Dist(float px, float pz, (float x, float z) p)
    {
        float dx = p.x - px, dz = p.z - pz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// The door to pass through to reach (tx,tz) when no direct route exists:
    /// the one most "between" the player and the target — small perpendicular
    /// offset from the player→target line and ahead of the player toward it.
    /// Null if none qualifies.
    /// </summary>
    private static (float x, float z)? PickDoorToward(float px, float pz, float tx, float tz,
                                                      List<(float x, float z)> doors)
    {
        float lx = tx - px, lz = tz - pz;
        float llen = MathF.Sqrt(lx * lx + lz * lz);
        if (llen < 1f || doors.Count == 0) return null;
        float ux = lx / llen, uz = lz / llen;

        (float x, float z)? best = null;
        float bestScore = float.MaxValue;
        foreach (var d in doors)
        {
            float dx = d.x - px, dz = d.z - pz;
            float along = dx * ux + dz * uz;                 // projection onto the line
            if (along < 100f || along > llen + 1500f) continue;   // must be ahead, not past
            float perp = MathF.Abs(dx * uz - dz * ux);        // distance off the line
            if (perp > 1800f) continue;                       // ~1.5 cells off the line max
            float score = perp + along * 0.3f;                // prefer near-line + nearer
            if (score < bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    // Within this distance of a door's exact XZ, treat the spot as a doorway:
    // steer straight through and ignore the frame. Cell-adjacency missed
    // frames ~1.5 steps out and the walker blocked there (live 2026-06-11);
    // distance-based is robust. ~1 cell ≈ 5 steps.
    private const float DoorwayRadius = 1200f;

    /// <summary>
    /// Is the player at a doorway (within DoorwayRadius of a known door)? Used
    /// to pass straight through without corridor-centering or wall-abort.
    /// </summary>
    private static bool NearDoorCell(float px, float pz)
        => GridRouter.NearestDoorDist(px, pz) <= DoorwayRadius;

    /// <summary>
    /// Recovery for a blocked WALK (same idea as the step's slip): drive a short
    /// diagonal — toward the target plus a perpendicular bias, one side then the
    /// other — to slide past a protrusion / door frame / the room wall beside an
    /// opening, instead of grinding out. Returns true if it got meaningfully closer
    /// to the target (slipped past). Blocking (~up to 700ms); bails on cancel/focus.
    /// </summary>
    private static bool SlipPastToward(float tx, float tz)
    {
        float px0 = FieldTracker.LivePlayerX, pz0 = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px0) || float.IsNaN(pz0)) return false;
        float bx = tx - px0, bz = tz - pz0;
        float bl = MathF.Sqrt(bx * bx + bz * bz);
        if (bl < 1f) return false;
        bx /= bl; bz /= bl;
        float bestD = bl;

        int smaj0 = FieldTracker.CurrentMajor, smin0 = FieldTracker.CurrentMinor;
        for (int side = 0; side < 2; side++)
        {
            float ps = side == 0 ? 1f : -1f;
            float sbx = bx + (-bz * ps), sbz = bz + (bx * ps);   // 45° toward target + perpendicular
            var s = new Steerer();
            long until = Environment.TickCount64 + 350;
            while (Environment.TickCount64 < until)
            {
                Thread.Sleep(PollMs);
                if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(smaj0, smin0)) { s.Release(); return false; }
                float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
                if (float.IsNaN(px) || float.IsNaN(pz)) continue;
                if (s.Tick(px, pz, sbx, sbz) == Steerer.Ev.CamLost) break;
            }
            s.Release();
            float npx = FieldTracker.LivePlayerX, npz = FieldTracker.LivePlayerZ;
            if (float.IsNaN(npx) || float.IsNaN(npz)) continue;
            float nd = MathF.Sqrt((tx - npx) * (tx - npx) + (tz - npz) * (tz - npz));
            if (nd < bestD - 100f)
            { Log($"[AutoWalker] slip-past worked (side {ps:F0}, {bestD - nd:F0}u closer)"); return true; }
            bestD = MathF.Min(bestD, nd);
        }
        return false;
    }

    /// <summary>
    /// The field context changed under a low-level drive: a BATTLE loaded or
    /// the floor transitioned. Every blocking drive/press loop must bail at
    /// once — keys pressed after the battle screen loads land in the BATTLE
    /// UI (auto-walk kept "walking" inside battles, user 2026-07-06).
    /// </summary>
    private static bool FieldInterrupted(int maj, int min)
    {
        int m = FieldTracker.CurrentMajor;
        return m != maj || FieldTracker.CurrentMinor != min || FieldTracker.IsBattleMajor(m);
    }

    /// <summary>Drive toward a world point until within stopDist (or maxMs / camera lost). True if it ended close.</summary>
    private static bool DriveToward(float tx, float tz, float stopDist, int maxMs)
    {
        int maj0 = FieldTracker.CurrentMajor, min0 = FieldTracker.CurrentMinor;
        var s = new Steerer();
        long until = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(PollMs);
            if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(maj0, min0)) { s.Release(); return false; }
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (float.IsNaN(px) || float.IsNaN(pz)) continue;
            float dx = tx - px, dz = tz - pz;
            if (dx * dx + dz * dz <= stopDist * stopDist) { s.Release(); return true; }
            if (s.Tick(px, pz, dx, dz) == Steerer.Ev.CamLost) break;
        }
        s.Release();
        float ex = FieldTracker.LivePlayerX, ez = FieldTracker.LivePlayerZ;
        return !float.IsNaN(ex) && (tx - ex) * (tx - ex) + (tz - ez) * (tz - ez) <= (stopDist * 1.6f) * (stopDist * 1.6f);
    }

    private static bool IsWallCell(int r, int c)
        => !MinimapTracker.ReadCell(r, c, out var cell) || cell.Flag == 2;   // out-of-bounds counts as wall

    /// <summary>
    /// Thread the nearest door: a door is a 1-cell GAP in a wall, so the wall cells sit
    /// PERPENDICULAR to the way through. Read that axis off the minimap, then drive to a
    /// point just in front of the gap (lining up with it) and STRAIGHT through along the
    /// passage — instead of grinding the frame at an angle. True if it reached the far
    /// side. Returns false (→ fall back to slip/reroute) if no door is close or the
    /// passage axis is ambiguous.
    /// </summary>
    private static bool ThreadDoor(float tx, float tz, HashSet<long>? threaded = null,
        float headX = 0, float headZ = 0)
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return false;

        float dxn = 0, dzn = 0, best = DoorwayRadius * DoorwayRadius; bool found = false;
        foreach (var (x, z) in DungeonNav.Doors())
        {
            // A door this walk already threaded is never the answer again —
            // re-threading it oscillated back and forth through the same
            // doorway 15× until cancelled (live 2026-07-06, Void Quest).
            if (threaded != null && threaded.Contains(DoorKey(x, z))) continue;
            // The obstacle is AHEAD along the blocked heading — a door behind
            // the player is not it (Marukyu ground a wall while dutifully
            // threading the door 250u behind-right, every cycle, 2026-07-06).
            if ((headX != 0 || headZ != 0) && (x - px) * headX + (z - pz) * headZ < 0) continue;
            float e2 = (x - px) * (x - px) + (z - pz) * (z - pz);   // nearest door (the obstacle in our way)
            if (e2 < best) { best = e2; dxn = x; dzn = z; found = true; }
        }
        if (!found) return false;
        threaded?.Add(DoorKey(dxn, dzn));
        return ThreadDoorAt(dxn, dzn, tx, tz);
    }

    private static long DoorKey(float x, float z)
        => ((long)MathF.Round(x / 100f) << 32) ^ (uint)(int)MathF.Round(z / 100f);

    /// <summary>Could the lit CHECK prompt belong to the STAIRS instead of the
    /// door about to be tapped? True only when the PLAYER is inside the stairs'
    /// check zone AND the stairs are closer than the door — measured at PRESS
    /// time. (v1 was a 900u no-press zone around the door point: it swallowed a
    /// legitimate closed door that merely stands near the staircase, and the
    /// whole floor became unopenable — 07-14 evening log.) Exempt when the
    /// stairs ARE the walk target.</summary>
    private static bool StairsPromptRisk(float doorX, float doorZ)
    {
        if (DungeonGrid.StairsAreTarget) return false;
        try
        {
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (float.IsNaN(px) || float.IsNaN(pz)) return false;
            if (!GridRouter.FindNearestStairs(px, pz, out float sx, out float sz)) return false;
            float sd2 = (sx - px) * (sx - px) + (sz - pz) * (sz - pz);
            float dd2 = (doorX - px) * (doorX - px) + (doorZ - pz) * (doorZ - pz);
            return sd2 < 500f * 500f && sd2 < dd2;
        }
        catch { return false; }
    }

    /// <summary>
    /// Walk THROUGH a door at the actor XZ (doorX,doorZ), opening it if it's closed.
    /// The actor XZ sits INSIDE the frame/wall, so we first SNAP to the walkable GAP
    /// (the doorway cell centre) — driving at the raw actor XZ grinds the frame, even on
    /// an OPEN door. Then: line up in front of the gap, creep in until the game's CHECK
    /// prompt lights up (= in range, facing it), tap confirm to OPEN, and pass straight
    /// through the gap toward the (towardX,towardZ) side. Per-stage logging so any
    /// residual grind is diagnosable. True if it reached the far side.
    /// </summary>
    // ── WallSense: the SIGHT upgrade (2026-07-12) ─────────────────────────────
    // While driving, raycast the game's OWN inline scene collision (sub_obj+
    // 0x10e0 — the structure that physically stops the body) every ~200ms and
    // stamp each wall hit into the fine grid BEFORE contact. Probe verdict that
    // green-lit this: 12/12 confirmed grinds had the wall visible at 14-35u,
    // including floors whose master wall-actor list streams almost nothing
    // (Secret Lab B6F: 48 junk tris). Grinding remains the fallback sensor.
    
    private static bool ThreadDoorAt(float doorX, float doorZ, float towardX, float towardZ, bool open = true)
    {
        float pitch = GridRouter.CellPitch(); if (pitch <= 0) pitch = WorldPerStep * 5f;

        // Through-direction = the way we're APPROACHING the door (player → door). The walker
        // reaches a door via its walkable GAP, so this IS the passage axis — drive it straight
        // through. (Cell-mapping a real door, 2026-06-23, proved the passage runs perpendicular
        // to the frame posts; aiming at the far TARGET instead veered into a post = the bug.)
        float pxs = FieldTracker.LivePlayerX, pzs = FieldTracker.LivePlayerZ;
        if (float.IsNaN(pxs) || float.IsNaN(pzs)) return false;
        float dirX = doorX - pxs, dirZ = doorZ - pzs;
        float dm = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
        if (dm < 1f)
        {
            dirX = towardX - doorX; dirZ = towardZ - doorZ; dm = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
            if (dm < 1f) return false;
        }
        dirX /= dm; dirZ /= dm;
        Log($"[AutoWalker] door @({doorX:F0},{doorZ:F0}) approachDir=({dirX:F2},{dirZ:F2})");

        // 1. APPROACH the ACTUAL door (not a snapped-aside cell — that's why CHECK never
        //    fired before). Creep toward it until the game's CHECK prompt lights up (in
        //    range + facing it) or we're basically at it. Driving toward the door is what
        //    makes us face it, which is what raises the prompt.
        //    The CHECK prompt only fires within ~350u of the interactable. The old fallback
        //    distance (pitch*0.30 ≈ 360u at pitch 1200) stopped us JUST OUTSIDE that range, so
        //    the prompt never lit and the confirm-tap did nothing (the "reaches check, leaves,
        //    presses" loop). Cap it well INSIDE the prompt range so we keep closing until CHECK
        //    fires; the close fallback only triggers if the door isn't checkable at all.
        const float CheckRange = 300f;
        int dmaj0 = FieldTracker.CurrentMajor, dmin0 = FieldTracker.CurrentMinor;
        long until = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < until && !_cancelRequested && Utils.GameHasFocus()
               && !FieldInterrupted(dmaj0, dmin0))
        {
            if (FieldTracker.CheckPromptActive) break;
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (!float.IsNaN(px) && (doorX - px) * (doorX - px) + (doorZ - pz) * (doorZ - pz) <= CheckRange * CheckRange) break;
            DriveToward(doorX, doorZ, pitch * 0.18f, 280);
        }
        if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(dmaj0, dmin0)) return false;

        // 2. OPEN: tap confirm; LOG the CHECK prompt before AND after each tap so we know
        //    for certain whether the confirm press is what opens the door.
        // Tap confirm to open. NOTE: the CHECK prompt is FACING-aware, so a player merely hugging a
        // CLOSED door from the side shows no prompt — "no prompt" does NOT reliably mean "open" (that
        // heuristic was wrong). So always TRY: tapping is harmless on an already-open door and opens a
        // closed one. (The proper fix is the real open/closed flag off the door actor — TODO, needs a
        // live closed-vs-open node dump to locate the field.)
        // Skip the open sequence entirely if we've already walked THROUGH this door (it was
        // marked open by the crossing detector / a prior thread). Dungeon doors stay open for the
        // floor visit, so a marked-open door is genuinely open → tapping is wasted; go straight to
        // threading. (Marks clear on floor change, so a regenerated closed door is never skipped.)
        // A CHECK prompt near the hop point may belong to a NON-door interactable:
        // the B6F stairs got confirm-tapped mid-travel and changed floors
        // (2026-07-13). Tap only when a REAL door actor stands at this point;
        // a bare room boundary / archway threads with no presses at all.
        if (open)
        {
            bool realDoor = false;
            try
            {
                foreach (var (dax, daz) in DungeonNav.Doors())
                    if ((dax - doorX) * (dax - doorX) + (daz - doorZ) * (daz - doorZ) < 450f * 450f)
                    { realDoor = true; break; }
            }
            catch { }
            if (!realDoor)
            {
                Log($"[AutoWalker] door @({doorX:F0},{doorZ:F0}): no door actor here — threading without taps");
                open = false;
            }
        }

        if (open && DungeonNav.IsDoorOpenMarked(doorX, doorZ))
        {
            Log($"[AutoWalker] door @({doorX:F0},{doorZ:F0}) already open — skipping open-tap");
        }
        else if (open)
        {
            // PROMPT-GATED taps only (2026-07-06). The old loop tapped Enter
            // blindly ("harmless") — but room-graph travel threads every
            // boundary incl. doorless archways, so the player heard constant
            // pointless action presses, and a blind tap in front of the wrong
            // interactable ACTS on it. Give the facing-aware prompt a short
            // beat to light, then press only while it's actually lit; a door
            // that never shows a prompt is handled by the THROUGH stage (an
            // unopened one grinds → learned wall → the plan routes elsewhere).
            long promptBy = Environment.TickCount64 + 600;
            while (!FieldTracker.CheckPromptActive && Environment.TickCount64 < promptBy
                   && !_cancelRequested && Utils.GameHasFocus()
                   && !FieldInterrupted(dmaj0, dmin0))
                Thread.Sleep(100);
            for (int i = 0; i < 4 && !_cancelRequested && Utils.GameHasFocus()
                 && !FieldInterrupted(dmaj0, dmin0); i++)
            {
                bool before = FieldTracker.CheckPromptActive;
                if (!before)
                {
                    if (i == 0) Log("[AutoWalker] door: no prompt — not pressing (open/archway/side-on)");
                    break;
                }
                // STAIRS GUARD at PRESS time (2026-07-14): if the player stands
                // inside the STAIRS' check zone and the stairs are closer than
                // the door, this lit prompt is likely theirs — a tap rode them
                // down a floor mid-travel (log-proven twice). Skip the press.
                if (StairsPromptRisk(doorX, doorZ))
                {
                    Log("[AutoWalker] door: stairs own this prompt — not pressing");
                    break;
                }
                PressConfirm();
                Thread.Sleep(450);
                bool after = FieldTracker.CheckPromptActive;
                Log($"[AutoWalker] door: confirm #{i} CHECK {before}->{after}");
                if (!after)
                {
                    Log("[AutoWalker] door: opened");
                    // The door's collision sits in the fine grid's accumulated
                    // walls (and grinds on it left learned stamps) — drop both
                    // for this doorway so it stops routing as a wall.
                    DungeonGrid.Invalidate(doorX, doorZ);
                    // LET IT SWING (2026-07-12): the prompt clears the moment the
                    // open is ACCEPTED, but the door animates out of the way for a
                    // beat — driving instantly ground the still-solid leaf, which
                    // is why "axis A clipped / both axes clipped" fired right after
                    // "opened" even from a perfect on-axis approach (live log).
                    for (int w = 0; w < 9 && !_cancelRequested && Utils.GameHasFocus()
                         && !FieldInterrupted(dmaj0, dmin0); w++)
                        Thread.Sleep(100);
                    break;
                }   // prompt cleared = opened
            }
        }

        // 3. THROUGH along the approach direction SNAPPED to the nearest world cardinal.
        //    Dungeon passages are axis-aligned and we entered via the gap, so the snapped
        //    approach IS the straight way through. (F8 door-mapping 2026-06-23 proved the
        //    coarse minimap mis-reads the posts — it called a N-S door's passage "E-W" and
        //    drove into a post — while the approach direction was correct. Trust the approach.)
        // THROUGH: passages are axis-aligned, but neither the coarse minimap nor a diagonal
        // approach reliably says WHICH axis (F8 mapping 2026-06-23 showed each can be wrong on
        // different doors). So TRY the approach-dominant cardinal, then the PERPENDICULAR one
        // (signed toward the target). One of the two IS the real passage; drive straight.
        // Passage axis: prefer the door's REAL transform normal (DungeonNav.TryDoorAxis) — the exact
        // orientation, which fixes the diagonal-approach mis-guess that clipped narrow Bathhouse doors.
        // Fall back to the approach-direction guess if no door transform is near. Either way the SIGN
        // is toward the target (the side beyond the door); axisB is the perpendicular fallback.
        int aX, aZ, bX, bZ;
        if (DungeonNav.TryDoorAxis(doorX, doorZ, out float pax, out float paz))
        {
            // Transform picks the AXIS (X vs Z). SIGN = the APPROACH direction (continue THROUGH to the
            // far side), NOT the target: the door-subgoal can pick an off-path door whose far side is
            // away from the target, and threading must still mechanically cross it so the travel loop
            // re-routes from the other side. Aiming the through-drive at a near-side target reverses it
            // straight back into the wall (the Bathhouse circling loop).
            if (MathF.Abs(pax) >= MathF.Abs(paz))
            { aX = dirX >= 0f ? 1 : -1; aZ = 0; bX = 0; bZ = dirZ >= 0f ? 1 : -1; }
            else
            { aX = 0; aZ = dirZ >= 0f ? 1 : -1; bX = dirX >= 0f ? 1 : -1; bZ = 0; }
            Log($"[AutoWalker] door: transform normal=({pax:F2},{paz:F2}) approach=({dirX:F2},{dirZ:F2}) → axisA=({aX},{aZ}) axisB=({bX},{bZ})");
        }
        else
        {
            aX = MathF.Abs(dirX) >= MathF.Abs(dirZ) ? Math.Sign(dirX) : 0;
            aZ = aX == 0 ? Math.Sign(dirZ) : 0;
            if (aX != 0) { bX = 0; bZ = towardZ - doorZ >= 0f ? 1 : -1; }
            else { bX = towardX - doorX >= 0f ? 1 : -1; bZ = 0; }
            Log($"[AutoWalker] door: through axisA=({aX},{aZ}) axisB=({bX},{bZ}) approach=({dirX:F2},{dirZ:F2}) [guess]");
        }

        // ALIGN to the door's ON-AXIS FRONT POINT, then drive straight through.
        // 2026-07-12 fix (the "stuck to the door frame" logs): threading can begin
        // up to pitch*1.4 from the door, but the old centering was a FIXED 700ms
        // sideways nudge with a pitch*0.12 tolerance — a player 751u off-axis
        // (live: x=8849 vs door x=9600) ran out of budget mid-slide and the
        // through-drive clipped the frame on BOTH axes, every attempt. Now the
        // walker drives to door − through*standoff (a point in FRONT of the gap
        // on its own side, pulled OFF the wall plane so the drive can't hug the
        // frame), with a distance-scaled budget and a tight tolerance.
        {
            float cpx = FieldTracker.LivePlayerX, cpz = FieldTracker.LivePlayerZ;
            if (!float.IsNaN(cpx) && !float.IsNaN(cpz))
            {
                float standoff = pitch * 0.25f;
                float fpx = doorX - aX * standoff, fpz = doorZ - aZ * standoff;
                float cdx = fpx - cpx, cdz = fpz - cpz;
                float cdist = MathF.Sqrt(cdx * cdx + cdz * cdz);
                if (cdist > 50f)
                {
                    int budget = Math.Clamp((int)(cdist * 3f) + 400, 900, 3000);
                    Log($"[AutoWalker] door: align to front point ({fpx:F0},{fpz:F0}) dist={cdist:F0}");
                    DriveToward(fpx, fpz, 60f, budget);
                }
            }
            if (_cancelRequested || !Utils.GameHasFocus()) return false;
        }

        // POST-ALIGN TAP (2026-07-14): a SIDE-ON approach shows no prompt (it is
        // facing-aware), so the tap stage above skipped a genuinely CLOSED door
        // ("no prompt — not pressing" → both axes clipped, walk failed). The
        // align drive just turned the body to FACE the door — if the prompt is
        // lit now and this is a real un-opened door, press once before driving.
        if (open && !DungeonNav.IsDoorOpenMarked(doorX, doorZ) && FieldTracker.CheckPromptActive
            && !StairsPromptRisk(doorX, doorZ)
            && !_cancelRequested && Utils.GameHasFocus() && !FieldInterrupted(dmaj0, dmin0))
        {
            PressConfirm();
            Thread.Sleep(450);
            if (!FieldTracker.CheckPromptActive)
            {
                Log("[AutoWalker] door: opened (post-align tap)");
                DungeonGrid.Invalidate(doorX, doorZ);
                for (int w = 0; w < 9 && !_cancelRequested && Utils.GameHasFocus()
                     && !FieldInterrupted(dmaj0, dmin0); w++)
                    Thread.Sleep(100);   // let the leaf swing clear
            }
        }
        if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(dmaj0, dmin0)) return false;

        if (DriveToward(doorX + aX * pitch * 0.9f, doorZ + aZ * pitch * 0.9f, pitch * 0.35f, 1300))
        { DungeonNav.MarkDoorOpen(doorX, doorZ); return true; }
        if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(dmaj0, dmin0)) return false;
        Log("[AutoWalker] door: axis A clipped — trying the perpendicular");
        if (DriveToward(doorX + bX * pitch * 0.9f, doorZ + bZ * pitch * 0.9f, pitch * 0.35f, 1300))
        { DungeonNav.MarkDoorOpen(doorX, doorZ); return true; }
        if (_cancelRequested || !Utils.GameHasFocus() || FieldInterrupted(dmaj0, dmin0)) return false;
        Log("[AutoWalker] door: both axes clipped — slip + retry");
        SlipPastToward(doorX + bX * pitch * 0.9f, doorZ + bZ * pitch * 0.9f);
        bool through = DriveToward(doorX + bX * pitch * 0.9f, doorZ + bZ * pitch * 0.9f, pitch * 0.35f, 1200);
        if (through) DungeonNav.MarkDoorOpen(doorX, doorZ);
        return through;
    }

    // ── Input ────────────────────────────────────────────────────────────────

    private static void KeyDown(byte scan) => keybd_event(0, scan, KEYEVENTF_SCANCODE, UIntPtr.Zero);
    private static void KeyUp(byte scan) => keybd_event(0, scan, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);

    // Tap the field ACTION/confirm key (Enter) — opens a door when its prompt is up.
    private static void PressConfirm() { KeyDown(SC_ENTER); Thread.Sleep(60); KeyUp(SC_ENTER); }

    private static void ReleaseAll()
    {
        KeyUp(SC_W); KeyUp(SC_A); KeyUp(SC_S); KeyUp(SC_D);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}
