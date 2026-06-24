# OVERWORLD AUTO-WALK & ARRIVAL — source of truth (2026-06-22)

How Backspace "walk me to the selected thing and tell me when I can interact" works in the
**overworld** (`Components/Navigation/OverworldNav.cs`). This is the layer that was rebuilt over the
2026-06-21/22 sessions. For the world *model* (arcs/FBN/HBN, unit registry, player chain) read
`OVERWORLD.md`; for the iteration history read `memory/pathfinder_research_2026-06-21.md`.

Current deployed build at time of writing: **388bd957** (Debug). School + town both user-verified ("now
it's perfect").

---

## 1. The core principle (what stopped the months of thrashing)

**Confirmation is gated on the game's OWN check flag, never on our guesses.**

- `0x1411BC7F4` — **CheckPromptActive** (`FieldTracker.CheckPromptActive`). When set, the game is *really*
  offering an interaction right now (a press will do something). It is **reliable**.
- `0x1411BC720` — **InInteractZone** (`FieldTracker.InInteractZone`). Position-only: you're inside an
  interaction volume (used for exits, which don't set 0x7F4).

Both are **global** (they don't say *which* interactable). Every past failure came from trying to answer
"which one?" with a runtime signal — all of which proved unreliable (see Dead ends). The fix: don't.
Confirm only when **the real flag is on AND we're at the selected target's own spot** — attribution by
*position*, not by a flag that names the object.

So the announcement rule is: **never say "Check available" / "Talk available" unless 0x7F4 is genuinely on
at that moment.** Worst case we say "You're at X, turn to face it" (honest). It can't lie.

---

## 2. Area model — three separate worlds

| Area | Major | Render | Handler | Path style |
|---|---|---|---|---|
| **School** (Yasogami) | `6` | 3D, rotating camera | OverworldNav **school branch** | A* maze-route + live-camera steering + position-write nudge |
| **Town / other overworld** | `<20`, not 6 | 2.5D, fixed camera | OverworldNav **town branch** | door-snap target + straight self-calibrating stick |
| **Dungeons** | `>=20` (not 240) | 3D | **DungeonNav / AutoWalker** (separate component) | OverworldNav yields (`return false`, OverworldNav.cs ~L300) |

`bool school = FieldTracker.CurrentMajor == 6` gates everything school-specific. The school path is **older,
proven, and untouched** by the town work. Battle = major 240 (not nav).

---

## 3. Target resolution (where "the spot" comes from)

Per selected interactable, in priority:

1. **Learned spot** (`calib`) — if `overworld_calibration.json` has an entry for this interactable, drive to
   THAT exact pos+facing. Ground truth (the player physically stood there and a real check fired). See §7.
2. **Bound follow-point** — `build_overworld_catalog.py` binds an HBN cat-14 follower stand-point sitting
   inside the trigger box to the interactable (`Target.X/Z`). Often the exact door/check spot.
3. **Reachable-door snap** (town only) — if the catalog point is a trigger-box CENTRE that sits *inside* a
   building (non-walkable), the straight stick rams the wall ~90u short and never arrives. So at walk start
   (`OverworldNav.cs` ~L944) we snap the target to the nearest **walkable** cell of `overworld_walkgrid.json`
   = the street-side door. No-op if already walkable / out of grid. **Skipped for** calib, school, and NPCs.
4. **Box centre** — fallback.

`Target` struct (`OverworldNav.cs` L67): `Name, Proc, X/Y/Z` (walk target), `BX/BZ/ExtX/ExtZ` (raw trigger
volume), `Kind`, **`PersonId`** (live cat-3 unit handle; `0` = static target = the NPC marker).

`overworld_walkgrid.json` = per-area walkable bitmask decoded offline from the `h*.AMD` "atari" collision
meshes (`tools/decode_collision.py` → `tools/build_walkgrid.py`). **The collision Z axis is FLIPPED** vs
WorldPlayerPos/catalog — the decoder negates Z (one line; ~40% of areas were mis-framed before this fix).

---

## 4. Type dispatch + confirm rules (the priority chain)

In `OverworldNav.WalkLoop`, every target matches **exactly one** branch — they cannot conflict:

```
if (school)                 ready = calib ? (0x7F4 && d<40 || d<12)   // nudge places+holds facing
                                          : (0x7F4 && d<45);
else if (IsExit(t))         ready = InInteractZone(0x720) && d<75;     // walk-into transition, no 0x7F4
else if (t.PersonId != 0)   ready = 0x7F4 && d<140;                    // NPC / person (talk)
else                        ready = 0x7F4 && d<75;                     // building / shop / save / Velvet
```
(`d` = `distArr` = distance to the resolved target.)

- **Exit** = `IsExit(t)` (proc starts `call_*`). Exits never set 0x7F4 — you walk *into* them. Announce
  "At X. Walk forward to leave." Confirm on being in the zone (0x720) near the target.
- **NPC** = `PersonId != 0`. Confirm window is **140u**, not 75u, because `distArr` is to the person's
  CENTRE and their **body stops you ~100-123u short** (measured: every NPC bumped + got 0x7F4 at distArr
  103-123; the old 90u was physically unreachable → fell through to the 5s timeout = a long wait + no beep).
  Announce "X. Talk available."
- **Building / shop / save point / Velvet Room** = everything else (the "object" bucket). 75u is tight
  enough to exclude a NEIGHBOUR's check (a loose InsideBox test grabbed Marukyu's check from 320u via its
  huge box). Announce "X. Check available."
  - **Per-object override (2026-06-24) — the two tiny Dojima interiors.** The Living Room (`7_2`) and Your
    Room (`7_3`, house major 7) pack interactables so close the 75u window grabbed a neighbour ("goes to
    another check"). Fix: `bool tight50 = IsRoomArea() && (t.Name == "check_reizou" || t.Name ==
    "check_sofa_p4p"); win = tight50 ? 50f : 75f;` — i.e. the **fridge** and **sofa** confirm at **50u**,
    everything else (both rooms + all town) stays 75u. `IsRoomArea()` checks `_roomAreas = {(7,2),(7,3)}`
    so the rest of the overworld is byte-for-byte unchanged. To tune another room object, add its
    `overworld_catalog.json` `name` (areas `007_002`/`007_003`). NPCs/exits/school/door-snap untouched.
    Full notes: `memory/overworld_house_room_confirm.md`. (The learned-spots/Shift+Backspace approach was
    considered and dropped — user preferred tuning the confirm distance.)

---

## 5. The arrival motion (per type)

- **Static object (town, `PersonId==0`)** — when within 60u, **genuine tiny-step search**: stick toward 8
  small offsets (~30u) around the spot. Real movement sets a **persistent** facing and physically lands the
  player ON the check spot. (We learned the hard way that `WritePlayerForward` is **temporary in 2.5D** — a
  written facing reverts the instant the mod stops, giving false "check available"; genuine motion holds.)
- **NPC (`PersonId != 0`)** — `t.X/Z` is **live-refreshed** each loop from `TryGetPersonPos(PersonId)` so the
  chase tracks walkers. Walk STRAIGHT at the live position; **never search** (the wander walked the player
  13 steps AWAY from a talk prompt already lit). Speed **eases to a near-stop** (`Clamp(dist*0.85,8,110)`) so
  we don't blow past and spin on the noisy point-blank aim vector. The wall-slide **sidestep is disabled
  while arriving** (`&& !arriving`) — bumping a person was being mistaken for a wall and we'd circle them.
- **School** — A* over the prebuilt grid + a bounded position-write nudge for the last ~130u (verified safe
  in major 6), squaring up the learned facing. This nudge (`PosDrive`) is **permanently on** — the old O-key
  toggle was removed 2026-06-22 (the user never wants it off, and **O is the party HP/SP key**). OverworldNav
  has no O binding.

---

## 6. Limits (known, honest)

- **Town has NO pathfinding.** The 2.5D walker is a straight self-calibrating stick. Where the route isn't
  roughly straight — behind a slope, around a corner — it **wedges and oscillates** ("close then far, plays
  a game"). Confirmed case: **"Down to riverbank"** (Samegawa 10_1) stalls ~290u short; "Examine field"
  (Dojima 7_1) similar. **Town A* was tried and made things WORSE** (the user reverted it) — town stays
  straight-line by deliberate choice. Door-snap only helps building-interior targets, not slope/around-wall
  routes.
- **No facing write in 2.5D** — `WritePlayerForward` doesn't persist outside the school, so town facing must
  come from real motion (the tiny-step search / the approach).
- **Fast NPCs** (Cyclists) outrun the eased chase → honest timeout rather than a catch.
- **Occasional wrong facing on a static target** (e.g. a Boys' Restroom landed exactly on the spot but faced
  wrong → 0x7F4 stayed False → honest timeout). The learning system (§7) fixes these per-spot once.
- **Follow-points are sparse** general follower points, only *coincidentally* at a door — which is why the
  door-snap (§3.3) exists as the reliable fallback.

---

## 7. Learning system + SHARING (object positions for future releases)

**`overworld_calibration.json`** — when a walk confirms on a **real** check (0x7F4 genuinely on), the mod
records that exact pos+facing keyed **`"<BX>_<BZ>_<Name>"`** (the interactable's box anchor + name). Next
time, that target drives straight to the learned spot and trusts it (Tier-1 reliable). A spot learned even
"by mistake" (you happened to face a real check) is captured — powerful and self-healing.

### These spots are UNIVERSAL — bundle them in future mod updates.
P4G PC has **ASLR off** and ships identical field geometry to every player, so a learned interaction spot is
**correct for every player**, not just the one who recorded it. The key is interactable-based
(`BX_BZ_Name`), not tied to the recorder.

- **Default today:** each player's calibration file is their own and starts empty. The base nav (door-snap +
  tiny-step search + 0x7F4 confirm) works without any learning, so a fresh install navigates fine and only
  sharpens with play.
- **Future-release action (do this when cutting the next package):** copy the curated
  `overworld_calibration.json` into the release mod folder (next to the DLL / data files, resolved via
  `Utils.DataPath`). Every player then **starts with all the dialed-in spots** — a head start, pure upside.
  - Curate first: drop any spot that looks off (large offset from its box, or a known bad-facing target);
    keep the verified ones. The dev backup is `overworld_calibration.json.bak`.
  - Players keep learning on top of the bundled set (their file is read/written in the same place), so the
    shipped spots are a floor, not a ceiling.
  - Because spots are universal, future versions can keep **accumulating** a community-curated reference set.

---

## 8. Future improvements (backlog)

1. **Town hard-path routing** — the riverbank/slope wedges. Options: a *waypoint graph* from catalog
   follow-points (lighter than full A*), or a carefully-scoped A* that only engages when a straight shot
   stalls (must not regress the straight-line cases that town A* broke before).
2. **Ship the learned-spots reference set** (§7) — the user's explicit ask for the next release.
3. **Per-target facing for stubborn statics** (e.g. restroom) — either learn-once, or use the HBN
   follow-point `rot` (facing) field, which the catalog currently ignores (degrees→vector convention
   needs deriving in-game first).
4. **Intercept fast NPCs** — lead the chase ahead of a moving person instead of tailing (cyclists).
5. **School NPC robustness** — Yosuke works (distArr 25), but moving school students lean on the same
   chase ideas the town path uses; unify if school people become a common target.

---

## Dead ends (do NOT re-try — each cost real time)

- **`0x140FFDE70`** as a "focused interactable id" — decompiled to a **GPU instance buffer** (`FUN_1405979B0`).
- **Press-selector replica** (`FieldTracker.TryFacedInteractable`, scene-actor list `0x140AA8098`→+8→+50,
  `+0xf88&1`) — false-confirmed the Save point. Scene-actor flags don't isolate the town interactable.
- **`WritePlayerForward` to fake the facing in 2.5D** — fires 0x7F4 for one frame then reverts → false
  "check available". Use genuine motion.
- **InsideBox confirm** — too loose for big trigger boxes (grabbed neighbours at 320u).
- **Whole-walk position-write drive** — bypasses collision, drove the player OFF THE MAP. Town walk is
  fully stick (game collision = safety net).
- **Town A\*** (full maze-routing) — made town worse than the straight-line walker; reverted.
