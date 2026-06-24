# DUNGEON_AUTOWALK.md — dungeon auto-walk & door threading

**Source of truth for the in-dungeon auto-walk / travel system.** Built & user-tested
2026-06-23. Read this before touching `AutoWalker.cs` travel, `ThreadDoorAt`, or the
`DungeonNav` Backspace dispatch.

Pairs with `OVERWORLD_AUTOWALK.md` (the overworld/town arrival system — separate code path).
Older dungeon design notes: `memory/dungeon_phase_closed.md`, `SPATIAL_AWARENESS.md`.

---

## 1. What ships now (2026-06-23)

| Category (DungeonNav) | Backspace does | Code |
|---|---|---|
| **Exits (stairs)** | **Full travel** — explore the floor, route leg-by-leg, **open doors**, re-route around walls, until it reaches the stairs. Announces *"Arrived. Find the door to go to the next floor."* | `AutoWalker.TravelToStairs` → `TravelLoop` |
| **Chests** | **Old one-shot hunter** (`AutoWalker.Start`) — walks straight to the chest over the known map, stops on it. | reverted 2026-06-23 |
| **Shadows** | **Old position-gated hunt** (`AutoWalker.StartHunt`) — you walk close, Backspace strikes when you're behind it. | reverted 2026-06-23 |
| **Doors / Places** | **Old one-shot walk** (`AutoWalker.Start`). | unchanged |

**Why only stairs use travel:** the generalized travel (`TravelLoop`) was briefly wired to
chests + shadows too, but on the **coarse minimap** it hugged walls / circled on far targets
(see §4). The user asked to restore the simple hunters for chests + shadows. The travel +
door improvements still benefit the **stairs travel** and **any door** the one-shot walkers hit
(they all share `ThreadDoorAt`).

`-`/`=` cycle category, `[`/`]` move within it, `\` = brief (name + distance), **Backspace** =
act. Audio beacons: `.` enemy radar, `,` chest, `/` stairs. (The `;` door beacon was **removed**
2026-06-23.)

---

## 2. The travel engine — `TravelLoop` (AutoWalker.cs)

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

## 3. Door threading — `ThreadDoorAt(doorX, doorZ, towardX, towardZ, open)`

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

## 4. The hard limit — the coarse minimap (READ THIS before "fixing" residual grinding)

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

## 5. Future improvement points

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

## 6. Key code locations

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

## 7. Log signatures (check `database/log.txt` after a test)
- `travel start` / `travel: leg to target via (…)` / `travel: head to door @(…)` /
  `travel: explore frontier (…)` / `travel arrived` / `travel give up`.
- `door @(…) approachDir=(…)` / `door: confirm #i CHECK before->after` / `door: opened` /
  `door: through axisA=(…) axisB=(…)` / `door: axis A clipped — trying the perpendicular`.
- `slipping a wall, bailing leg to re-route` = the anti-wedge bail fired.
- `stop: Battle.` = a shadow caught the player mid-travel (normal; re-run after the fight).
