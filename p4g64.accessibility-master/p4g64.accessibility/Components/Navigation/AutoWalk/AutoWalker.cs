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
        DoorTolUnits, "Heading to the stairs.", "Arrived. Find the door to go to the next floor."));

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
    internal static void TravelToShadow(float sx, float sz) => Launch(() =>
    {
        float near = GridRouter.CellPitch(); if (near <= 0) near = WorldPerStep * 5f; near *= 1.6f;
        if (TravelLoop((px, pz) => (true, sx, sz), near, "Heading to the shadow.", null))
            Hunt(sx, sz);
    });

    // Per-cancel-key arming. A cancel key (Esc/arrow) HELD at launch must NOT
    // cancel — the blind player navigates with the arrow keys, so one is
    // routinely down when Backspace starts a walk, which cancelled it on the
    // first poll every time (live 2026-06-11). A key only arms (can cancel)
    // after it's been released once; then a FRESH press during the walk =
    // genuine manual override.
    private static readonly bool[] _cancelArmed = new bool[5];

    private static void Launch(Action body)
    {
        if (_active) return;
        _active = true;
        _cancelRequested = false;
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
            finally { ReleaseAll(); _active = false; }
        }) { IsBackground = true, Name = "AutoWalker" }.Start();
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
        float advTol = pitch * 0.28f;
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
        int wi = 0, rerouted = 0, nanPolls = 0, slipRecoveries = 0;
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

        // Blocked recovery. Returns: 0 = abort (announced), 1 = recovered,
        // 2 = arrived (CHECK confirms), 3 = at the target but no prompt.
        int HandleBlocked(string reason)
        {
            steer.Release();
            float adx = tx - px, adz = tz - pz;
            if (MathF.Sqrt(adx * adx + adz * adz) <= MathF.Max(StallArriveUnits, finalTol))
                return FieldTracker.CheckPromptActive ? 2 : 3;

            // If a door is close, THREAD it (line up with the gap + drive straight
            // through the passage) — a narrow doorway needs alignment, not a 45° slip.
            // Then the generic slip for protrusions / walls. On success, replan from
            // where we ended up and resume.
            // A real door close by → thread it (that IS the way through; always worth it).
            if (ThreadDoor(tx, tz))
            {
                var slid = PlanPoints(tx, tz, includeStart: true, approach: approach);
                if (slid != null) { pts = slid; wi = 0; prevBearX = 0; prevBearZ = 0; bestWpDist = float.MaxValue; }
                lastProgress = Environment.TickCount64;
                Log($"[AutoWalker] {reason} — recovered (door), resuming");
                return 1;
            }
            // Otherwise slip along the obstacle. But in TRAVEL mode, do NOT slip a wall forever:
            // after a few slips with no door found, BAIL the leg (return abort) so the travel
            // loop re-routes to a door/frontier instead of grinding the wall indefinitely.
            if (SlipPastToward(tx, tz))
            {
                if (_travelMode && ++slipRecoveries > 3)
                { _travelLegStuck = true; Log($"[AutoWalker] {reason} — slipping a wall, bailing leg to re-route"); return 0; }
                var slid = PlanPoints(tx, tz, includeStart: true, approach: approach);
                if (slid != null) { pts = slid; wi = 0; prevBearX = 0; prevBearZ = 0; bestWpDist = float.MaxValue; }
                lastProgress = Environment.TickCount64;
                Log($"[AutoWalker] {reason} — recovered (slip), resuming");
                return 1;
            }

            if (++rerouted > 1)
            { Announce($"{reason} {RemainingSpeech(px, pz, tx, tz)}"); return 0; }

            Log($"[AutoWalker] {reason} at ({px:F0},{pz:F0}) — backing up and re-routing");
            KeyDown(SC_S); Thread.Sleep(300); KeyUp(SC_S);

            var fresh = PlanPoints(tx, tz, includeStart: true, approach: approach);
            if (fresh == null)
            { Announce($"{reason} No new path. {RemainingSpeech(px, pz, tx, tz)}"); return 0; }
            pts = fresh; wi = 0;
            prevBearX = 0; prevBearZ = 0;
            bestWpDist = float.MaxValue;
            lastProgress = Environment.TickCount64;
            return 1;
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

                // Waypoint consumption + radius fallback.
                while (wi < pts.Count - 1 && Dist(px, pz, pts[wi]) < advTol)
                { wi++; bestWpDist = float.MaxValue; }
                float dist = Dist(px, pz, pts[wi]);
                if (wi == pts.Count - 1 && dist <= finalTol) break;

                float bx = pts[wi].x - px, bz = pts[wi].z - pz;

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

                // Corridor centering (skipped on the final approach / doorways).
                bool finalApproach = wi == pts.Count - 1 && dist < pitch;
                var (sbx, sbz) = (finalApproach || throughDoor)
                    ? (bx, bz)
                    : CenterBias(px, pz, bx, bz, pitch, haveAxes, rowAxis, colAxis);

                switch (steer.Tick(px, pz, sbx, sbz))
                {
                    case Steerer.Ev.CamLost: Announce("Camera unreadable, stopping."); return;
                    case Steerer.Ev.NoCam: continue;
                    case Steerer.Ev.Wall:
                    case Steerer.Ev.Grind:
                        if (throughDoor) break;   // brushing the door frame — keep going
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
        Log("[AutoWalker] travel start");

        long deadline = Environment.TickCount64 + 180_000;
        long lastProgress = Environment.TickCount64;
        int stuckLegs = 0;   // consecutive legs that made no real progress → switch route
        float bestDist = float.MaxValue;   // closest we've gotten to the target (real progress metric)
        var tried = new HashSet<int>();

        _travelMode = true;
        try
        {
            while (true)
            {
                if (CheckStop(sMaj, sMin, out string? why)) { Announce(why!); return false; }
                long now = Environment.TickCount64;
                if (now > deadline || now - lastProgress > 8000)
                { ReleaseAll(); Speech.Say("Couldn't reach it from here. Try getting a little closer.", true); Log("[AutoWalker] travel give up"); return false; }

                float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
                if (float.IsNaN(px) || float.IsNaN(pz)) { Thread.Sleep(PollMs); continue; }

                var (have, tx, tz) = getTarget(px, pz);
                float towardX = have ? tx : px, towardZ = have ? tz : pz;

                if (have && (tx - px) * (tx - px) + (tz - pz) * (tz - pz) <= arriveDist * arriveDist)
                { ReleaseAll(); if (!string.IsNullOrEmpty(arriveMsg)) Speech.Say(arriveMsg, true); Log("[AutoWalker] travel arrived"); return true; }

                _travelLegStuck = false;   // Walk sets this if it bails a wall-slip this leg
                // Pick the sub-goal, then take a SHORT leg toward it (the proven walker stays
                // in the wall-mesh bubble + follows corridors). Priority:
                //   1. target reachable over the known map → straight at it;
                //   2. else CROSS the door that leads toward the target — rooms join by doors,
                //      so the walker must go VIA the door, not grind the wall between them;
                //   3. else explore the frontier nearest the target.
                // If the straight line keeps grinding (no progress for 2 legs), STOP re-driving
                // it — fall through to the door/frontier branch to try a DIFFERENT way around.
                if (have && stuckLegs < 2 && GridRouter.FindRoute(px, pz, tx, tz) != null)
                {
                    if (GridRouter.StepToward(px, pz, tx, tz, 2, out float wx, out float wz))
                    { Log($"[AutoWalker] travel: leg to target via ({wx:F0},{wz:F0})"); Walk("ahead", wx, wz, null); }
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
                        Log($"[AutoWalker] travel: head to door @({bdx:F0},{bdz:F0})");
                        if (GridRouter.StepToward(px, pz, bdx, bdz, 2, out float wx, out float wz)) Walk("ahead", wx, wz, null);
                        float npx = FieldTracker.LivePlayerX, npz = FieldTracker.LivePlayerZ;
                        if (!float.IsNaN(npx) && (bdx - npx) * (bdx - npx) + (bdz - npz) * (bdz - npz) <= (pitch * 1.4f) * (pitch * 1.4f))
                        {
                            ThreadDoorAt(bdx, bdz, towardX, towardZ, open: true);   // open + pass into the next room
                            if (MinimapTracker.WorldToCell(bdx, bdz, out int br, out int bc)) tried.Add(br * MinimapTracker.COLS + bc);
                        }
                    }
                    else if (GridRouter.FindFrontier(px, pz, towardX, towardZ, tried, out float fx, out float fz))
                    {
                        if (GridRouter.StepToward(px, pz, fx, fz, 2, out float wx, out float wz))
                        { Log($"[AutoWalker] travel: explore frontier ({fx:F0},{fz:F0})"); Walk("ahead", wx, wz, null); }
                        else if (MinimapTracker.WorldToCell(fx, fz, out int br, out int bc)) tried.Add(br * MinimapTracker.COLS + bc);
                    }
                    else { ReleaseAll(); Speech.Say("Can't find a way there.", true); Log("[AutoWalker] travel no subgoal"); return false; }
                }

                // Progress = the player got meaningfully CLOSER to the target — NOT merely "moved".
                // Circling a door (the Bathhouse loop) keeps moving without approaching, so a
                // movement-based metric never gives up and never switches route. Distance-based:
                // only resetting on real gain means a circle trips both the 8s give-up AND the
                // stuckLegs route-switch (so it tries another door/frontier, then bails cleanly).
                float ax = FieldTracker.LivePlayerX, az = FieldTracker.LivePlayerZ;
                float newDist = (have && !float.IsNaN(ax))
                    ? MathF.Sqrt((tx - ax) * (tx - ax) + (tz - az) * (tz - az)) : float.MaxValue;
                if (newDist < bestDist - pitch * 0.5f)
                { bestDist = newDist; lastProgress = Environment.TickCount64; stuckLegs = 0; }
                else if (_travelLegStuck) stuckLegs = 2;       // bailed a wall-slip → force a door/frontier re-route
                else stuckLegs++;
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

        var (rx, rz) = approach ?? (tx, tz);

        // Door cells are tracked for the NearDoorCell frame-suppression, but
        // NOT made walkable in A* — coarse-cell routing through doors produced
        // backwards routes (live 2026-06-11). Route normally; if the target is
        // unreachable (behind a door), go VIA the door: route to the door, then
        // a STRAIGHT leg through the opening to the target.
        var doors = DungeonNav.Doors();
        GridRouter.SetDoorCells(doors);
        GridRouter.AllowDoorRouting(false);

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

        for (int side = 0; side < 2; side++)
        {
            float ps = side == 0 ? 1f : -1f;
            float sbx = bx + (-bz * ps), sbz = bz + (bx * ps);   // 45° toward target + perpendicular
            var s = new Steerer();
            long until = Environment.TickCount64 + 350;
            while (Environment.TickCount64 < until)
            {
                Thread.Sleep(PollMs);
                if (_cancelRequested || !Utils.GameHasFocus()) { s.Release(); return false; }
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

    /// <summary>Drive toward a world point until within stopDist (or maxMs / camera lost). True if it ended close.</summary>
    private static bool DriveToward(float tx, float tz, float stopDist, int maxMs)
    {
        var s = new Steerer();
        long until = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(PollMs);
            if (_cancelRequested || !Utils.GameHasFocus()) { s.Release(); return false; }
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
    private static bool ThreadDoor(float tx, float tz)
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return false;

        float dxn = 0, dzn = 0, best = DoorwayRadius * DoorwayRadius; bool found = false;
        foreach (var (x, z) in DungeonNav.Doors())
        {
            float e2 = (x - px) * (x - px) + (z - pz) * (z - pz);   // nearest door (the obstacle in our way)
            if (e2 < best) { best = e2; dxn = x; dzn = z; found = true; }
        }
        return found && ThreadDoorAt(dxn, dzn, tx, tz);
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
        long until = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < until && !_cancelRequested && Utils.GameHasFocus())
        {
            if (FieldTracker.CheckPromptActive) break;
            float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
            if (!float.IsNaN(px) && (doorX - px) * (doorX - px) + (doorZ - pz) * (doorZ - pz) <= CheckRange * CheckRange) break;
            DriveToward(doorX, doorZ, pitch * 0.18f, 280);
        }
        if (_cancelRequested || !Utils.GameHasFocus()) return false;

        // 2. OPEN: tap confirm; LOG the CHECK prompt before AND after each tap so we know
        //    for certain whether the confirm press is what opens the door.
        // Tap confirm to open. NOTE: the CHECK prompt is FACING-aware, so a player merely hugging a
        // CLOSED door from the side shows no prompt — "no prompt" does NOT reliably mean "open" (that
        // heuristic was wrong). So always TRY: tapping is harmless on an already-open door and opens a
        // closed one. (The proper fix is the real open/closed flag off the door actor — TODO, needs a
        // live closed-vs-open node dump to locate the field.)
        if (open)
        {
            for (int i = 0; i < 4 && !_cancelRequested && Utils.GameHasFocus(); i++)
            {
                bool before = FieldTracker.CheckPromptActive;
                PressConfirm();
                Thread.Sleep(450);
                bool after = FieldTracker.CheckPromptActive;
                Log($"[AutoWalker] door: confirm #{i} CHECK {before}->{after}");
                if (before && !after) { Log("[AutoWalker] door: opened"); break; }   // prompt cleared = opened
                if (!before && i >= 1) break;   // no prompt after a couple taps → already open / not interactable
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

        // CENTER on the gap FIRST, then drive straight through. The gap runs PERPENDICULAR to the
        // passage axis, so aligning the player onto the door's perpendicular coordinate (a pure
        // sideways move) before advancing avoids the diagonal "correct + advance" that clips the frame
        // corner when you approach a narrow door off-centre (the Bathhouse clip). Passage X → align Z
        // to doorZ; passage Z → align X to doorX.
        {
            float cpx = FieldTracker.LivePlayerX, cpz = FieldTracker.LivePlayerZ;
            if (!float.IsNaN(cpx) && !float.IsNaN(cpz))
            {
                if (aX != 0) DriveToward(cpx, doorZ, pitch * 0.12f, 700);   // passage X: center on Z
                else         DriveToward(doorX, cpz, pitch * 0.12f, 700);   // passage Z: center on X
            }
            if (_cancelRequested || !Utils.GameHasFocus()) return false;
        }

        if (DriveToward(doorX + aX * pitch * 0.9f, doorZ + aZ * pitch * 0.9f, pitch * 0.35f, 1300)) return true;
        if (_cancelRequested || !Utils.GameHasFocus()) return false;
        Log("[AutoWalker] door: axis A clipped — trying the perpendicular");
        if (DriveToward(doorX + bX * pitch * 0.9f, doorZ + bZ * pitch * 0.9f, pitch * 0.35f, 1300)) return true;
        if (_cancelRequested || !Utils.GameHasFocus()) return false;
        Log("[AutoWalker] door: both axes clipped — slip + retry");
        SlipPastToward(doorX + bX * pitch * 0.9f, doorZ + bZ * pitch * 0.9f);
        return DriveToward(doorX + bX * pitch * 0.9f, doorZ + bZ * pitch * 0.9f, pitch * 0.35f, 1200);
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
