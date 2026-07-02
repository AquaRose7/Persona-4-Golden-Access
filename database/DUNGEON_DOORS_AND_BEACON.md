# Dungeon nav upgrades — All-doors, door self-marking, unified beacon, wall-hit (2026-07-02)

Design agreed with the user (2026-07-02), built incrementally with a test after each feature.
Source of truth for these FOUR dungeon-nav additions — all user-verified working. Pairs with
`database/DUNGEON_AUTOWALK.md`. **Also gated:** `DungeonNav`/`DungeonCursor`/`NavBeacon`/`WallBump`
skip their key handling while the camp menu is open (`CommandMenus.PlayerMenu.IsMenuOpen` = the reliable
`pCamp+0x0C` submenu mask; the raw `pCamp` pointer is garbage on dungeon floors).

## Feature 1 — "All doors" category
- Add a NEW category to the `-`/`=` cycle **next to "Doors"**: **"Doors"** = near/active doors
  (unchanged), **"All doors"** = every door on the loaded floor.
- Implementation: `EnumerateInteractables(activeOnly)` — the existing scene walk gated the door/chest
  nodes on the `+0x28 & 2` "active/near" bit; `activeOnly:false` drops that so ALL door pairs on the
  loaded floor are listed. `Cat.AllDoors` → `BuildInteractableEntries(Kind.Door, "Door", activeOnly:false)`.
- **Scope caveat:** the scene list is what's LOADED. Dungeon floors are small (usually fully loaded),
  but if a big floor only loads actors near the player, far doors won't appear until explored. Fallback
  if that shows up in testing: supplement with explored-minimap doors (room-id transitions). Truly
  unexplored doors can't be known.
- Guard: skip `(0,0)`/non-finite positions (inactive nodes may carry stale transforms).
- **Scope: dungeon FLOORS only.** Removed from the entrance/lobby (major 20 = `LobbyCategories`) 2026-07-02
  (user) — the hub isn't a real floor, so its `-`/`=` cycle is Doors·Chests·Shadows·Stairs·Interactables.

## Feature 2 — Self-marking doors OPEN (cross-only)
- **Signal = the validated crossing detector** (`DetectDoorPassThrough`): a door has a facing (its
  transform normal via `TryDoorAxis`); when the player/auto-walker's signed perpendicular offset FLIPS
  sign while close, they crossed it. Works for any orientation; parallel walk-bys and never-crossed
  (locked) doors don't fire. VALIDATED live 2026-07-01.
- On a crossing → mark that door **open** in a per-FLOOR in-memory set (door position key). Cleared on
  floor change (procedural floors regenerate → never persist).
- **Browser:** a marked door reads **"open door, …"** instead of "door, …" (both Doors + All doors).
- **Auto-walk:** on a door already marked open, SKIP the open-tap and just thread straight through.
- Locked doors: never crossed → never marked → auto-walk still tries + its give-up/reroute handles them.
- Opened-but-not-crossed doors stay unmarked (harmless redundant tap). **Decided cross-only** — the
  direct open/closed read was spiked (door actor pose `node+0x300`) and came back ALL ZEROS on Marukyu/
  Yukiko doors (dead offset; the pose likely lives on the visual-panel actor, a different node). Not
  worth a full snapshot-diff hunt for a harmless edge case. See `memory/bathhouse_dungeon.md`.
- No manual mark key (automatic only); add one later only if auto-detection ever misses.

## Feature 3 — Unified 3D beacon (`P`, CAMERA-relative)  — `NavBeacon.cs`  ✅ user-verified "perfect"
- Press **`P`** to beacon the current target; it **SNAPSHOTS + LOCKS** at press time and holds FIXED —
  browsing `[`/`]` to check distances does NOT move it (user's explicit workflow: mark a spot, beacon
  it, then browse freely). A SHADOW target is the exception → live-tracked (snap to nearest live shadow
  to the running track pos). Reuses `BeaconVoice` + shared `DungeonAudio`.
- **`P` smart toggle:** OFF → lock candidate + on. ON → **same spot** (within `SameTargetUnits`=250u)
  toggles OFF; a **DIFFERENT** spot RETARGETS (no off-then-on) with "Beacon moved". Candidate = the
  H-cursor cell if the cursor is in Look mode, else the pathfinder selection — so once the cursor is
  closed, P moves a cursor-mark beacon onto whatever you're browsing.
- **CAMERA-relative** (`FieldTracker.CameraForward3D`, = the way W moves; user changed from body-facing
  2026-07-02): pan = left/right of camera forward (+ BeaconVoice ITD; right vector `(fz,-fx)` × PanSign
  = **-1**, flipped after the user reported reversed), muffle (`BeaconVoice.Openness`) = ahead(bright)/
  behind(dark), volume + TICK RATE = distance.
- **Sound + distance model = the overworld beacon** (user asked to match): 760 Hz `MakePing`, `NearGap`/
  `FarGap` (faster ticks close) + `NearGain`/`FarGain`, `FarDist`=2500. Auto-stops on floor change.
- `P` gated Shift-up (Shift+P = speech history). Dungeon-only; OverworldNav's `P` is inactive there.
  `DungeonNav.TryGetSelectionTarget(out x,z,out isShadow)` feeds the pathfinder target; `_selIsShadow`
  from `Entry.Label == "Shadow"`. Existing `/` (stairs) + `,` (chest) beacons LEFT in place for now.

## Feature 4 — Wall-hit thud (`WallBump.cs`)  ✅ user-verified
- Plays a short descending "thunk" (440→300 Hz `Beep`) while the player pushes INTO a wall, repeating
  every ~550ms while held. The retired wall audio false-fired off velocity/proximity; this uses:
  **movement INTENT** (left stick / D-pad via `ControllerInput.LeftStickX/Y`, or keyboard WASD/arrows) +
  **not advancing** (`moved < 15u`/poll for ≥3 polls) + **pushing ~the way you were just moving**
  (intent·lastMoveDir > **-0.5** — only a REVERSAL, dot ≤ -0.5, is excluded; a perpendicular corner-turn
  into a wall fires) + **moved freshly** just before (excludes standstill starts).
- Intent is camera-relative → rotated to world via `CameraForward3D` (**right vector `(-fz, fx)`** — the
  opposite sign from the beacon; verified by the left/right-not-firing bug).
- Gated OFF during dialog (`Dialogue.LastDialogTick`, set only when real dialog TEXT draws — `DrawDialog`
  fires every field frame otherwise), the camp menu (`PlayerMenu.IsMenuOpen`), and auto-walk.
- **KEY RE LESSONS (cost a long debug):** (a) the position DOES update live (walk = 53-92u/poll, wall = 0);
  (b) WASD reads fine via GetAsyncKeyState — earlier "empty" samples were just idle moments; (c) the OLD
  dialog gate (any `DrawDialog` call, or `pCamp != 0`) was ALWAYS-true in dungeons and silently killed the
  whole detector — fixed to real-text-only + the mask-based menu signal.

## Build order (all shipped + user-verified 2026-07-02)
1. All doors → 2. Self-marking + "Open door" label + auto-walk skip-open → 3. `P` beacon → 4. Wall thud.

## Spikes done (2026-07-01, foundation validation)
- **A crossing** (`DetectDoorPassThrough`, DungeonNav) — VALIDATED → became the self-marking signal.
- **B open/closed pose read** (`node+0x300`) — FAILED (all zeros on Marukyu/Yukiko doors; the pose likely
  lives on the visual-panel actor, a different node), removed. Cross-only marking chosen instead.

## Follow-up fixes + additions (2026-07-02, all user-verified)
- **Chest read as a door — FIXED.** Some chests are modelled as a co-located node PAIR, so the "2+ nodes
  = door" heuristic tagged them as doors (Marukyu chest @(16800,26400)). `EnumerateInteractables` now
  RECLASSIFIES a door cluster to Chest if it sits within `DedupUnits` of an ACTIVE treasure-array entry
  (`ActiveChestPositions`, opened or not). Chests come from the dedicated array anyway, so this only
  cleans the door list.
- **⚠ KNOWN LIMITATION — non-chest "fake doors".** A prop can also be a 2-node cluster with no treasure-
  array backing (Marukyu @(11375,10800)). Tried to filter by node **radius `+0x430`** (all 0 — dead) and
  by **nearest master-table cat** (polluted: cat=7 = the MOVING following party; the prop matched no
  static entry). No clean discriminator found — left as-is (user: not worth a long dig). The array-chest
  case above IS handled.
- **Dungeon-floor Interactables (benched party member + Fox) — ADDED.** The "Interactables" category now
  shows on dungeon FLOORS (was lobby-only), filtered to **cat=9** master-table rows (the walk-up NPCs;
  the rest of the floor table is garbage). Naming (`PlaceLabel` cat=9): the id's **HIGH byte = the party
  char id** (Yosuke 0x020A → char 2 → "Yosuke"), so benched members self-name via `DungeonCharNames`; the
  **Fox** (0x120A) is bound explicitly. cat=7 (following party) and cat=6 (doors) are excluded.
- **Dungeon ENTRANCE floor names — FIXED** (`_floorNameOverrides`): 23/1 "Yukiko's Castle, Gate", 24/1
  "Bathhouse, Changing Area", 25/1 "Marukyu Striptease Seats" (the banner-strip fallback had mangled them).
- **`P` beacon in overworld — SILENCED** (`NavBeacon.Toggle` returns silently when not on a dungeon floor;
  the overworld has its own `P` beacon, so the old "only works in dungeons" line talked over it). Controller
  **LT + B** → `P` drives NavBeacon in dungeons too (needs a nav selection first, like the keyboard `P`).
- **`PlayerMenu.IsMenuOpen` HARDENED** — a garbage `pCamp` on dungeon floors could yield a non-zero mask
  and wrongly gate the dungeon nav keys (intermittent "can't browse/auto-walk"). Now requires a SANE
  submenu mask (`mask != 0 && (mask & ~0x1FF) == 0`).

## ⚠ CRASH WATCH (2026-07-02)
This update added background scene-list walks that run during play (the door detector's per-tick
`Doors()`/`EnumerateInteractables` scene walk, the All-doors scan, NavBeacon's shadow scan). One crash was
seen entering the **Velvet Room** (an area transition) — the danger window, since the game frees/rebuilds
scene structures there and an in-process walk can hit freed memory (uncatchable AVE). Mitigations
applied (2026-07-02, after a 2nd crash **loading into the Entrance/major-20 hub**): the door detector's
scene walk is **throttled to ~130ms**, **bails on NaN position**, **never runs in the major-20 hub**
(no doors there), and **backs off for ~1.5s after ANY area change** (`FieldTracker.LastAreaChangeMs` —
the scene is still rebuilding in that window). **UPDATE 2026-07-02 (round 2):** the hub-skip + door-cooldown was NOT enough — it crashed again going
**Velvet Room (major 8) → Entrance (major 20)**. Root cause generalized: `WallBump` + `NavBeacon` (added
this session) read `CameraForward3D` + live position every 50-60ms and **re-activate the instant the major
re-enters the 20-220 range — mid-rebuild**. That's what took the rate from ~1/20 launches to back-to-back.
Fix: a UNIVERSAL **`FieldTracker.InAreaTransition`** (true for ~1.5s after any major/minor change) now
gates EVERY background poller that reads live pos/camera/scene — `WallBump`, `NavBeacon`, `EnemyRadar`,
`ProximityBeacon` (exit/chest), `DungeonCursor`, and the door detector — so none of them touch game memory
during the rebuild window. The timing is safe because a poller only sees the new major AFTER FieldTracker
sets both `CurrentMajor` and `LastAreaChangeMs` in the same update. If crashes STILL persist, the
definitive fix is routing the scene walks through **`ReadProcessMemory`** (the `VelvetMenu.cs` pattern). See parent `CLAUDE.md`
platform-constraints note on in-process scanning.
