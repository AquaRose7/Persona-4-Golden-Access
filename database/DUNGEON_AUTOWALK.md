# DUNGEON_AUTOWALK.md — dungeon auto-walk (v3, 2026-07-17/18)

**Source of truth for the in-dungeon auto-walk.** The system was REBUILT from the
sensor up over 2026-07-16→18 and user-verified across 7 dungeons (stairs, chests,
doors, interactables, marks, shadows all arriving; the whole build-and-test
narrative is `memory/next_session_wall_sensor_rebuild.md`). Read §0 before touching
`AutoWalker.cs`, `StairsPlan.cs`, `GridWalk.cs`, the wall sensor in
`FieldTracker.cs`, or the `DungeonNav` Backspace dispatch. §H (history) below the
divider describes the SUPERSEDED systems — kept for the data-file decodes and the
dead-end evidence.

---

## 0. THE CURRENT SYSTEM — one honest sensor, one planner, one drive

Design law (proven the hard way, all of §0.6): **the map is innocent until the
body proves otherwise, nothing bad persists past a walk, and every loop is
bounded and speaks when it gives up.**

### 0.1 The wall sensor (the foundation everything stands on)

The game's OWN swept-sphere collision `FUN_14032B810` (**5 args** — the
stack-staged direction vec3 is REQUIRED; omitting it dereferences uninitialized
stack and AV-crashes) against the walls-only scene `sub_obj+0x10E0`, wrapped as
`FieldTracker.SweepSceneRaw`. **GAME THREAD ONLY** — there is no scene lock; every
native call runs inside the move-tick pump (`FUN_1402D53A0` detour). Scene resolve
is hoisted (`TryGetCollisionScene` once → raw calls ≈ free; 100+ calls/scan at 0ms).

- **All probes are THIN (r=20)** and march outward (`CoarseDistScene`).
  Sides use `SideDistScene`: up to 3 PARALLEL thin rays (base points ±30u along
  the axis, each shift clamped by the measured ahead/behind clearance).
- Consumers: the WALL HUM (N, camera-relative, ~5.5Hz scan) + the spoken **B**
  readout, and the walker's travel-relative `RunWalkProbe` (~12Hz).
- The minimap-grid boundary sense (`GridWallDist`, edge-bit march) is a SECOND,
  INDEPENDENT source (Heaven-style open edges collision can't see). The two are
  additive and never gate each other.

### 0.2 The planner — `StairsPlan.TryPlanTo` (any target)

4-connected A* over the LIVE minimap grid (`GridWalk.cs`): cells 1200u, flag +0x00
(1=walkable), edge byte +0x0A low nibble (0x01 S/0x02 E/0x04 N/0x08 W, SET=open),
roomId +0x02. **Two-rule connectivity** (load-bearing): adjacent cells connect if
the edge bit is open OR they share a non-zero roomId — corridors are 1-cell rooms
joined by edge bits; room interiors are roomId blobs with no interior bits.

Waypoints = cell-center centerline, plus:
- **Re-center prepend** when the player starts >300u off their own cell's center
  (post-reroute wedges cut through clutter otherwise).
- **Door WEAVING**: every room-boundary crossing adopts the nearest live door
  within 900u of the crossing midpoint and inserts door±220u points on the door's
  transform axis (`DungeonNav.TryDoorAxis`) — cell centers sit up to half a cell
  off the gap; a bare center-to-center leg crossed the doorway WALL (Heaven
  frame-wedge, screenshot-proven).
- Per-kind endings: stairs = stop CENTERED at the stairs-room door (v1 semantics);
  Door target = stop centered in FRONT of it; everything else = the target's EXACT
  position as the final waypoint.

### 0.3 The drive — `AutoWalker.DriveRoute` (shared by every walk)

Steering: live camera (`CameraForward3D`) + PWM W/A/S/D (`Steerer`), sign
calibrated each walk; `LaneAdjust` centering off the live walk probe. Waypoint
consumption is TURN-TIGHT: bends + door points 80u, straights 170u (loose
consumption at a bend cut the corner into the wall and the game's collision slid
the player 2400u off-route). **Slide detection**: displacement vs commanded
direction dot <0.30 for ~500ms → release, re-target the nearest waypoint in a
one-step window (max 6 per walk).

**The stall ladder** (1.5s no-progress), in order:
1. CHECK prompt lit → press confirm (a door right at the nose).
2. **Consume-if-close** (within arrive+180u): a stall a hand's width from a
   waypoint IS arrival — 5 reroutes were once burned on stalls 92-222u out.
3. **wp0-skip**: the first waypoint is a positioning aid, never a gate (cell
   centers can sit INSIDE furniture — Secret Lab console room; and blocking the
   START cell never changes a replan).
4. **Door open** — `TryStallDoor` progress test: ANY door ≤700u (side/behind
   included — a direction cone missed a door on the player's LEFT) qualifies iff
   its FAR side is ≥150u closer to the waypoint (the came-from door can never
   qualify → no circling). `OpenDoorAt` = creep to CHECK, prompt-gated taps (max
   4, stairs-prompt guard), swing wait. Crossing = INJECTED centered waypoints
   (door±220u) driven by the main loop — the old `ThreadDoorAt` through-push is
   DEAD for v2 (it clipped frames; the normal drive crosses an open gap cleanly).
   Max 2 opens per waypoint → "A door would not open."
5. **Two-strike reroute**: first stall = back off 250ms and re-approach (the map
   is innocent); second = block the waypoint's CELL + replan (max 5 reroutes).
6. **Escalating retreat on NoRoute**: blocking one cell severing the route means
   the BLOCK was wrong (the obstruction is smaller than the cell) — un-block,
   back out the way we came 1.0s/1.4s/1.8s…, replan from the new spot. This is
   the PRIMARY mid-route recovery: the offline sim shows 66% of single-cell
   blocks sever scripted-floor routes.

Every poll checks cancel (Backspace), battle load, floor change, focus — a walk
can always be killed in ≤50ms.

### 0.4 Dispatch & per-kind arrivals (`DungeonNav.StartWalk` → `AutoWalker`)

| Target | Entry | Final behavior |
|---|---|---|
| Stairs (Exits) | `WalkToStairs` | stop centered at the stairs-room door — "Arrived. Check nearest door." |
| Chest | `WalkTarget(..., Chest)` | drive to the chest, then ease-on until CHECK (2.5s budget) — "At the chest. Press to open." |
| Door / All doors | `WalkTarget(..., Door)` | plan ends centered in front — "At the door. Press to open." |
| Interactables / Events marks / H-cursor marker | `WalkTarget(..., Spot)` | exact point + prompt note |
| Shadow | `WalkToShadowV2` | drive near (shadow re-acquired on every replan — they move), then the unchanged back-strike `Hunt` |
| Events floor-jump labels | teleport (unchanged) | |
| **Gridless areas** (TV-world hub, dungeon lobbies — `!GridRouter.HasGrid()`) | old straight-line `Walk` fallback | lobby behavior since 2026-06-17 |

### 0.5 What persists (soft-lock safety)

- `blocked` cells: per-walk, in-memory, die when the walk ends.
- Door open-marks: ONLY on a genuine body-crossing of the door plane
  (`DetectDoorPassThrough` sign flip) — grinding a frame can never mark; per-floor.
- Breadcrumbs record ONLY manual walking (`!AutoWalker.IsActive`).
- Every give-up is spoken. There is no silent exit path.

### 0.6 Dead ends — DO NOT RE-WALK (each cost real days; evidence in memory)

- **Mesh-transform reconstruction** (actor mesh +0xF60 × matrix +0x330): 6
  interpretations ear-tested, ALL wrong. Nav asks the game's collision instead.
- **Fat side probes (r=55)**: marching parallel to a near wall they overlap it the
  whole way → guaranteed fake "left 45 right 45" while hugging.
- **The grid hugging-gate** (delete collision readings the grid calls open): it
  deleted REAL walls — the actual cause of walls going silent while hugged.
- **Any filter on the sweep's out-normal**: the native loop never stops at a hit;
  the buffer holds the scene's LAST-tested triangle — the same vector for every
  probe. The "dotAxis convention" was a falsified camera-orientation coincidence.
- **`ThreadDoorAt`'s through-push** for v2 crossings (clipped frames — "both axes
  clipped"); **reactive veer/sidestep steering** (circles, wrong-way — user-rejected
  twice); **blocking cells for door stalls** (B7F: 11 doors → blocked-cell cascade →
  false NoRoute); **plain retry after NoRoute-unblock** (treadmills in a pocket —
  five identical cycles post-battle).
- Older: position-write teleport, unbounded Simplify, mesh-as-map fine routing,
  greedy coarse travel, auto-walk recording its own breadcrumbs (§H).

### 0.7 Verification tools

- **Offline planner sim**: `database/tools/sim_v2_planner.py` — emulates
  GridWalk+TryPlanTo over all 24 scripted floors from `dungeon_floor_maps.json`
  (masks proven == runtime). Baseline in its header (95.6%/6576 pairs; 63_1 =
  teleporter islands, correct; 69_3 has 2 one-way edges). **Re-run after ANY
  planner change.**
- **Log signatures**: `stairs walk: N cells` / `target walk (Kind) label` /
  `shadow walk:` / `wall slide #N … probe a= l= r=` / `stall at wpN … consuming` /
  `stall at start wp0 — skipping` / `door @(x,z) — opening` / `door: opened` /
  `door open done — crossing via waypoints` / `{tag} reroute #N around (r,c)` /
  `reroute #N failed … retreating Nms` / `replanned after retreat` / `give-up at
  wpN` / `stop: Battle.` / arrival lines per kind. **B** key = one
  `[Collision] cardinals` line.
- Key code: `AutoWalker.cs` (`DriveRoute`, `WalkTarget`, `WalkToShadowV2`,
  `StairsBody`, `OpenDoorAt`, `TryStallDoor`, `BendFlags`, `Steerer`),
  `StairsPlan.cs` (`TryPlanTo`), `GridWalk.cs` (connectivity + A*),
  `FieldTracker.cs` (sensor + move-tick pump), `DungeonNav.cs` (`StartWalk`).

Pairs with `OVERWORLD_AUTOWALK.md` (separate code path). Wall-hum/sensor RE:
`memory/collision_query_sweep_re.md`.

---

# H. HISTORY — superseded systems (data decodes + dead-end evidence live here)

> Everything below describes the 2026-06-23 → 2026-07-14 systems. The code paths
> (`TravelLoop`, `TravelTo*`, `Start`, `ThreadDoorAt`, RoomGraph travel, fine-grid
> routing, learned walls, tile collision, WallSense raycast stamps) are DEAD or
> demoted for v2 — but the DATA FILE DECODES remain live knowledge:
> `dungeon_floor_maps.json` (.MAP pre-image; feeds the v2 offline sim + FineGrid),
> `dungeon_tile_walls.json`, `dungeon_player_paths.json` (breadcrumbs — still
> recorded, manual-only), `dungeon_waypoints.json` (Events marks — still used by
> the v2 Spot dispatch). Gridless-lobby straight-line `Walk` is still live as the
> v2 fallback.

## H.0 PATHFINDER v2 (2026-07-06) — rooms-first + learned walls [SUPERSEDED]

- **`RoomGraph.cs`** — travel's strategic brain. Minimap roomIds = nodes, cross-room cell
  adjacencies = door edges; BFS yields the door-crossing sequence to the target's room.
  A failed crossing kills only that crossing POINT (a border spans several cells; only one
  is the gap) — an edge dies only when all its points do. Archway midpoints get 1 try,
  real door actors 2. Target room resolves with ring radius 1 (radius 2 walked away from a
  wall-hugging chest); player side radius 2.
- **`DungeonGrid.cs`** — 50u fine grid. Walkable base = explored flag-1 minimap rects;
  blockers = accumulated master-list wall tris **plus LEARNED WALLS: every confirmed grind
  stamps a 5-cell bar across the heading** (the body is the wall sensor — some floors
  stream almost no collision mesh: Void Quest gave 41-48 tris and the ground-on walls were
  absent). Per-floor only, cleared on floor change; door-open clears stamps near that door.
  String-pull horizon = 24 cells; NEVER WallMesh.Smooth a fine route (its near-empty index
  re-collapses corridor paths into straight lines).
- **Learned walls PERSIST on FIXED floors (2026-07-12):** `dungeon_learned_walls.json`
  (floorKey → stamps; dual-write ModDir+database). Fixed floor = major 20-69 except
  procedural mazes (40-49 with minor 0).
- **★★★ TILE COLLISION (2026-07-13) — asset-grade SUB-CELL walls:**
  `dungeon_tile_walls.json` (⚠ ship in releases) = every dungeon tile piece's REAL wall
  geometry, decoded offline from the game's own hit models (`h0XX_0NN.AMD` inside
  `field\pack\f04X_*.arc`; piece NN == the .MAP/minimap SPRITE id; local ±600 cell space,
  Y up = negative; the fc_XX_YY.AMD files are CHARACTER models — dead end, don't re-walk).
  Validated vs live grinds on 65/2: 14/16 stamps within 100u (avg 54u). Rotation = the
  cell's MODIFIER byte, 90° CW steps; tileset = maze major or scripted major − 20 (holds
  through the 5/6 swap).
- **★★ PRE-BAKED FLOOR LAYOUTS (2026-07-12):** `dungeon_floor_maps.json` (⚠ ship in
  releases) = the walkable cells of all 24 scripted floors (majors 60-69), extracted
  offline from the game's own `field\map\f0XX_YYY.MAP` files — the runtime minimap's
  pre-image (Ctrl+F8 correlation on 65/2: 86% byte-identical; file record = runtime record
  shifted +4; file[+4]==1 = walkable; the +14 byte = the CONNECTIVITY MASK, proven ==
  the runtime edge byte by [MaskDiag]). Cell record schema (7 ints):
  `[row, col, tileSprite, mask(+14), roomW, roomH, rotation]`.
- **★ WALLSENSE raycast stamps (2026-07-12)** + CANE + scripted-floor room-graph skip +
  door front-point alignment — all superseded by the v2 sensor/drive.

---

## H.1 What shipped 2026-06-23 [SUPERSEDED — see §0.4 for the v2 dispatch]

| Category (DungeonNav) | Backspace does | Code |
|---|---|---|
| **Exits (stairs)** | **Full travel** — explore the floor, route leg-by-leg, **open doors**, re-route around walls, until it reaches the stairs. Announces *"Arrived. Find the door to go to the next floor."* | `AutoWalker.TravelToStairs` → `TravelLoop` |
| **Chests** | **Old one-shot hunter** (`AutoWalker.Start`) — walks straight to the chest over the known map, stops on it. | reverted 2026-06-23 |
| **Shadows** | **Old position-gated hunt** (`AutoWalker.StartHunt`) — you walk close, Backspace strikes when you're behind it. | reverted 2026-06-23 |
| **Doors / Places** | **Old one-shot walk** (`AutoWalker.Start`). | unchanged |
| **Events** *(added 2026-06-24)* | **Old one-shot walk** to a hand-recorded named point — present ONLY on SCRIPTED (fixed-layout) floors that have a mark. | `DungeonNav` `Cat.Events` → `AutoWalker.Start` |

**"Events" marks (scripted floors).** Fixed-layout story/boss floors keep the same geometry every run,
so a few hand-recorded named points are universal. `DungeonNav` shows an **"Events"** category in the
`-`/`=` cycle **only when the current floor has a recorded mark** (that presence check IS the
"scripted-floor" gate; procedural floors are untouched). Floor key = `"major_minor"` — verified that
scripted floors get a distinct scene-major (Yukiko 2F = `23_3`, 5F = `60_1`) while procedural maze floors
all collide on `40_0` (**never mark a `40_0` floor**). Backspace auto-walks via the existing
`AutoWalker.Start`. Data: `dungeon_waypoints.json` (`floorKey → [{X,Z,Label}]`, bundled universal). The
record/promote keys are currently UNBOUND (the `RecordMark`/`UndoMark`/`PromoteSelectionToEvents` methods
+ a Ctrl-guarded re-bind recipe live in `DungeonNav.Tick`). Shipped marks: 2F "Stairs", 5F "Event door".

**Why only stairs use travel:** the generalized travel (`TravelLoop`) was briefly wired to
chests + shadows too, but on the **coarse minimap** it hugged walls / circled on far targets
(see §4). The user asked to restore the simple hunters for chests + shadows. The travel +
door improvements still benefit the **stairs travel** and **any door** the one-shot walkers hit
(they all share `ThreadDoorAt`).

`-`/`=` cycle category, `[`/`]` move within it, `\` = brief (name + distance), **Backspace** =
act. Audio beacons: `.` enemy radar, `,` chest, `/` stairs. (The `;` door beacon was **removed**
2026-06-23.)

---

## H.2 The travel engine — `TravelLoop` [DEAD CODE since v2]

`TravelLoop(getTarget, arriveDist, startMsg, arriveMsg)` — generic explore-replan to a
(possibly moving) target. Returns true if it got within `arriveDist`. Runs each leg through the
**proven `Walk`** so it stays in the wall-mesh bubble and follows corridors (no bee-lining).
`_travelMode` silences Walk's per-leg speech.

**Per-iteration sub-goal priority:**
1. **Target reachable** over the known minimap (`FindRoute != null`) **and not stuck** → take a
   short leg (`StepToward`, 2 cells) straight at it.
2. **Else cross a DOOR toward the target** — rooms join by doors, so go *via* the door, not
   grind the wall between. Pick the door minimising **dist(player→door) + dist(door→target)**
   (the door on the best path *through*, not merely nearest the target). Walk to it; when within
   ~1.4 cells, `ThreadDoorAt(open:true)`; mark its cell `tried` so it isn't re-threaded.
3. **Else explore the frontier** nearest the target (`FindFrontier`).
4. Else give up: *"Can't find a way there."*

**Anti-wedge ("change the way") — the key robustness piece:**
- A leg that makes no real progress increments `stuckLegs`. After **2 stuck legs**, the loop
  **skips the straight-line branch** (`stuckLegs < 2`) and falls through to door/frontier — so it
  tries a *different* way instead of re-driving the same wall.
- Walk itself, in travel mode, **bails a wall-slip after 3 slips** (`slipRecoveries > 3` →
  sets `_travelLegStuck`, returns abort). Without this Walk slipped a wall *forever* (each slip
  "recovered" and reset the timer, so the loop never regained control). A bailed leg forces
  `stuckLegs = 2` → immediate re-route.
- No-progress give-up = **8 s** (then *"Couldn't reach it from here. Try getting a little
  closer."* — matches the manual-nudge workflow).

`TravelToStairs` re-finds the nearest stairs sprite each iteration (`FindNearestStairs`); if not
yet mapped it explores frontiers generally until the sprite appears.

---

## H.3 Door threading — `ThreadDoorAt` [DEAD for v2 — OpenDoorAt + injected waypoints replaced it; the RE facts below remain true]

Used by the travel door sub-goal, by `Walk`'s `HandleBlocked` → `ThreadDoor` (nearest door, when
a leg grinds), and by the H-cursor door-ahead step. Three stages:

1. **APPROACH** — creep toward the door actor XZ until the game's CHECK prompt
   (`FieldTracker.CheckPromptActive`, `0x1411BC7F4`) lights or we're basically on it. Driving
   *toward* the door is what makes us face it, which raises the prompt.
2. **OPEN** — tap confirm (Enter, `SC_ENTER` 0x1C) up to 4×; logs `CHECK before->after`. Prompt
   clearing = opened. No prompt at all (CHECK False->False) = already open / not a real door.
3. **THROUGH — try BOTH cardinal axes.** Dungeon passages are world-axis-aligned, but **neither
   the coarse minimap nor a diagonal approach reliably says which axis** (proven by F8 cell-maps
   2026-06-23: the minimap called a N-S door "E-W"; a diagonal approach to another door snapped
   to the wrong cardinal). So drive the **approach-dominant cardinal first, then the
   perpendicular** (signed toward the target). One of the two IS the real passage. Then
   `SlipPastToward` as a final fallback.

`ThreadDoor` (the nearest-door finder used by Walk) picks the **nearest** door (the obstacle in
the way) — an earlier "only doors toward the target" filter was a **regression** (it skipped the
correct door when the path turns) and was removed.

### Door RE facts (from F8 cell-mapping)
- A door's stored position is the **scene-actor XZ, which sits in the frame**, not the walkable
  gap. The minimap is too coarse (24×16, ~1250u cells) to resolve the frame posts — door cells +
  their neighbours often all read flag-1 walkable even where posts physically block.
- Doors are **closed-solid** until opened with the confirm/Enter action while the CHECK prompt is
  up and you're facing them.
- F8 `DoorCellMap` (DungeonNav, temporary) dumps a 7×7 cell grid + the door cell — the tool that
  cracked the passage-axis logic. Keep it for future hard doors.

---

## H.4 The coarse-minimap limit [still true — v2 answers it with the honest sensor + recovery ladder, not a finer map]

The minimap (`0x1411AB39C`, 24×16 cells × 16 bytes, flag `+0x00`: 0 unexplored / 1 walkable /
2 boundary) is the **only** floor map we route on, and a **wall thinner than a cell is invisible
to it**. Consequences, all observed live:
- `FindRoute` returns a route **through a wall** the minimap marks walkable → the leg grinds.
- A target **behind a sub-cell wall with no usable door** is genuinely unreachable by auto-walk;
  the player must get a little closer (the 8 s give-up + "get closer" message is the honest
  fallback, and it matches what unsticks it in practice).
- The slip/`change-the-way`/`bail` machinery makes this **fail gracefully and re-route**, but it
  cannot conjure a path the map doesn't contain.

**The real cure is routing on the accurate wall mesh, not the minimap** — see §5.

---

## H.5 Future improvement points [historical — item 1 (mesh routing) is a PROVEN DEAD END; item 2 shipped in v2]

1. **Wall-mesh routing grid (the big one).** Raster the game's real collision/wall mesh into a
   fine grid and A* on that instead of the 24×16 minimap. Kills the sub-cell-wall grinding at the
   root, and would let chests + shadows safely use travel again. Blocker noted in
   `memory/autowalk_nav_research_2026-06-20.md`: the runtime wall mesh is only a near-player
   bubble + has a coverage bug — needs the master wall-actor list (`*(nint*)0x140AA8098`, see
   `memory/ghidra_master_wall_actor_list.md`) rasterised per floor.
2. **Bring chests + shadows back onto travel** once §1 lands (so far chests/shadows are reachable
   through doors). For shadows, travel-to-near then hand to the existing back-strike `Hunt`
   (`TravelToShadow` already exists, just unwired).
3. **Up/down-stairs disambiguation.** Travel targets the nearest stairs *sprite*; it doesn't know
   up vs down. Tie into `GET_FLOOR_ID` context.
4. **Faster door open.** The 4-tap confirm loop + approach can take a few seconds on the first
   door; tighten once the prompt timing is characterised.
5. **Door list coverage.** Threading depends on `DungeonNav.Doors()` containing the door. If a
   door isn't enumerated, travel falls back to frontier/slip. Audit the door enumerator on floors
   where threading never fires (no `door @` line in the log).

---

## H.6 Key code locations [of the DEAD system]

| Thing | Where |
|---|---|
| `TravelLoop`, `TravelToStairs/Chest/Shadow` | `Components/Navigation/AutoWalk/AutoWalker.cs` |
| `ThreadDoorAt`, `ThreadDoor`, `HandleBlocked`, slip-bail | same file |
| `Walk` (per-leg driver), `Steerer`, `DriveToward`, `SlipPastToward` | same file |
| `FindRoute`/`StepToward`/`FindFrontier`/`FindNearestStairs`/`CellWalkable` | `AutoWalk/GridRouter.cs` |
| Backspace dispatch (category → action) | `Components/Navigation/DungeonNav.cs` `StartWalk()` |
| F8 door cell-map dump (temporary) | `DungeonNav.DoorCellMap()` |
| Minimap cells | `MinimapTracker`; grid `0x1411AB39C` |
| Live camera (steering) | `FieldTracker.CameraForward3D()`, `*(*(0x1462487C0)+8)+0x40/+0x48` |
| CHECK prompt | `FieldTracker.CheckPromptActive`, `0x1411BC7F4` |

## H.7 Log signatures [of the DEAD system — v2 signatures are in §0.7]
- `travel start` / `travel: leg to target via (…)` / `travel: head to door @(…)` /
  `travel: explore frontier (…)` / `travel arrived` / `travel give up`.
- `door @(…) approachDir=(…)` / `door: confirm #i CHECK before->after` / `door: opened` /
  `door: through axisA=(…) axisB=(…)` / `door: axis A clipped — trying the perpendicular`.
- `slipping a wall, bailing leg to re-route` = the anti-wedge bail fired.
- `stop: Battle.` = a shadow caught the player mid-travel (normal; re-run after the fight).
