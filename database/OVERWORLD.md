# OVERWORLD — research + navigation data layer

Phase started 2026-06-11 (after dungeon phase closed). This is the source of
truth for the overworld world-model research and the asset-driven navigation
data layer that replaces the old record-by-hand teleport targets.

## The world model (cracked 2026-06-11)

P4G overworld areas are `(major, minor)` fields (e.g. 8 = Shopping District).
Each `data_e.cpk:field/pack/fd{major:03}_{sub}.arc` bundles everything for a
field group. **Container format** (AreaArcUnpack.py):

```
[u32 entry_count] then per entry: [char[32] name][u32 size][data]
```

For fd008_001.arc (99 entries): `f008.bf` (area FlowScript), `h008_001.bin` +
`n008_001(_yoru).bin` registries, `n008_001(_yoru).bf` (street-NPC dialog BF),
`*.ENV` (lighting, per sub-area), `f008_001.CMR` (camera), `f008_001.HBN`,
and ~70 `f008_*_NNN.FBN` placement files.

### The four asset layers

| Layer | File | Contents | Decoder |
|---|---|---|---|
| Placements | `*.FBN` | NPC/prop model placements: unit handle (cat 3), rotation matrix, world XYZ at rec+0x50 | `tools/decode_fbn.py` |
| Hit/spawn | `*.HBN` | Section A: trigger boxes (cat 13, `0x3400+i`) with XYZ+extents. Section B: spawn/entry points (cat 14, `0x3800+i`) with XYZ+rotation-degrees | `tools/decode_hbn.py` |
| Interaction binding | `h{maj}_{min}.bin` | 44-byte rows; row i binds HBN box i → **BF procedure index** (u16 at +0x10, e.g. 0x32 → `tyuuka()`); +0x20 u32 = trigger kind 1..4 | `tools/probe_field_table.py` |
| Schedule | `n{maj}_{min}.bin` | 32-byte rows `(month, day, …, value)` — per-day street-NPC/dialog variation table | (format known, decoder TBD) |

The decompiled flow (`extract/bf_per_area/fd008_001.flow`) provides the
semantic names: procedure index → `bookstore()`, `tyuuka()` (Aiya),
`touhuya()` (tofu), `tatumiya()`, `jinjya()` (shrine), `sakaya()`, `tokoya()`
(barber), `yakuten()` (pharmacy), `denki()`, `basutei()` (bus stop),
`velvet_room()`, `save_point()`, `call_lmap()` (exit/map board), … plus one
`f008_NNN_init()` per minor area.

### Presence model — why we never need to re-implement the calendar

`f008_001_init()` toggles each placement with `FLD_FUNCTION_0007(id, 0/1)`
gated on `GET_WEATHER()`, `GET_TIME_OF_DAY()`, `CHECK_TIME_SPAN(m,d,m,d)`,
`DATE_CHK()`, `BIT_CHK()`, `GET_SL_LEVEL()`. The game evaluates this at area
load — so the **runtime visibility bit already encodes "is it really there
today"**. We read it instead of re-deriving schedules.

## Native side (Ghidra, 2026-06-11)

Field COMM dispatch table at `0x140AA80D0` (16-byte entries) is file-backed —
full dump in `ghidra/` notes. Key functions:

- **`FLD_FUNCTION_0007` = `0x1403204E0`** `(id, on)`: looks up unit, sets/clears
  **bit 0x2 of `unit+0x28`** (the script show/hide flag).
- **`FLD_FUNCTION_0008` = `0x140320550`** `(id, anim, …)`: same lookup;
  handle encodes **`cat = id >> 10`, `idx = id & 0x3FF`**. Cat 10 (0x2800+) =
  scripted field objects (same family as dungeon door IDs 10240+). Model
  sub-object at `unit+0x168` (cat 10) / `unit+0x190` (cat 1/3).
- **`FLD_FUNCTION_0000` = `0x140320210`**: returns init phase from `0x1411AB290`
  (the `f*_init` procs branch on 0/1/3).
- **Unit lookup `FUN_14040f790(short id)`** — THE registry walk:
  ```
  ptr = *(0x140AA8098)            // same global as the wall-actor master list
  arrBase = *(ptr + 8)            // 22 category list heads
  node = *(arrBase + (id>>10)*8)  // then follow +0x150 next-pointers
  node+0x00 = u16 full handle; match → return node
  ```
  Categories valid 1..0x15. **Node position offset: TBD at runtime**
  (candidates: +0x360/+0x368 like the interactable table, or via the
  +0x168/+0x190 model object).

Runtime dump tool: `frida/dump_units.py` (RPM-based, crash-safe) — summary /
per-cat / raw-node modes.

## Verified format stats (all 105 arcs, data_e)

863 FBN files → 5,685 placement records; 237 HBN blobs → 560 trigger boxes +
858 spawn points; 0 parse errors. FBN has two versions: 0x10002 (rec 0x140)
and 0x10001 (rec 0x70) — same fields where it matters (id +0x08, pos +0x50).

## Runtime correlation — VERIFIED 2026-06-12 (live, Shopping District South 8/2)

Snapshot-diff session (Daidara ↔ save point zigzag, 4 snaps) + node dumps:

- **Live registry mirrors the HBN assets verbatim.** Cat-13 nodes carry the
  box record at `node+0x164` (x,y,z + 3 extents); cat-14 at `node+0x160`
  (x,y,z,rot). Node stride in memory observed 0x1C0 (next-ptr at +0x150).
- **h-row i ↔ HBN box i CONFIRMED with two anchors** (in world space):
  Daidara → box 3 → h-row 3 → proc 42 `equip_shop()`; save point → box 8 →
  h-row 8 → proc 51 `save_point()`.
- **Cat 14 = party-follower stand points** (not spawn points). The follower
  controller `FUN_140316e10` (reader of `0x140EC1170`) steers party members
  to them. Useful as nav anchors anyway.
  - `0x140EC0FF8 + n*0xF8(?)` = per-member active point idx (u16, -1 none)
  - `0x140EC1170` = per-member active point node ptr
  - `0x140EC0FE8/0x140EC0FF0` = party member array (member 0 = protagonist,
    node ptrs; player node is the **cat-1 id 0x400** unit).
- **TRUE world-space player position** (THE chain for overworld nav):
  ```
  playerNode = *(0x140EC0FF0)          // cat-1 unit id 0x400
  *(playerNode+0x250) → obj
  *(obj+0x48)        → sub            // NOT the fieldObj sub_obj!
  *(sub+0x18)        → xform
  xform+0x360/+0x364/+0x368 = world X, Y, Z (float)
  ```
  Verified live: (393.87, 2.93, -2103.63) at the save point box (409, -2102).
- **WARNING: the old 2.5D player chain (`fieldObj→+0x48→sub_obj+0xD0/+0xD4`)
  is NOT world space.** It's a local/scroll space (Daidara reads (630, 272)
  there vs world (-346, -3052)-ish). All old recorded positions
  (area_entries.json, old teleport targets) are in THAT space — do not mix.

## Open questions

1. **CHECK dispatch internals**: which structure binds the active CHECK to the
   h-row at press time (suspect `0x1411BC804` = `0x01A8....` family near the
   CHECK flag; low word is not a BF instr/label index — unresolved, but not
   needed for the nav catalog).
2. **Unit-handle ↔ FBN mapping**: flow toggles cat-10 ids; FBN records carry
   cat-3 ids (per-file local). Resolve when we need NPC-level naming beyond
   trigger boxes (street NPCs etc.).
3. Minor-area numbering: fd008_001 covers `f008_001..039` inits + ENV files
   numbered 101..850 — map minors → in-game sub-areas via GET_FIELD_MAJOR/MINOR.
4. Confirm cat-13 `node+0x28` bit 0x2 visibility semantics on a schedule-gated
   trigger (e.g. night-only) — the FLD_FUNCTION_0007 flag was verified on
   cat-10 nodes (mixed Y/n seen live).

## The static catalog — BUILT 2026-06-12

**`database/overworld_catalog.json`** (generator:
`tools/build_overworld_catalog.py` — rerun any time, ~10 s). 105 arcs →
**180 areas, 377 interactables (287 proc-named), 360 follower points,
5,685 placements, 0 warnings**. Per area: `interactables` (box geometry +
proc + friendly name + kind), `followPoints`, `extraPoints` (cat-15/16,
semantics TBD), `zones` (section-5 volumes), `placements` (FBN), `warnings`.

Format details learned while building:
- HBN section 0 boxes with **idx ≥ 1000 (0x37fe/0x37ff) are boundary/system
  triggers** — not h-bound; h-rows bind positionally to the NORMAL boxes only.
- h-rows with **proc 0xFFFF are null** placeholders.
- HBN sections 2/3 = **cat-15/16 point families** (unknown purpose yet);
  section 5 = large zone volumes (ids 0x25400+).
- Friendly-name dictionary lives in the generator (`FRIENDLY`) — extend there.
- `kind` values observed: 0..4 (4 = lmap/exit style, 2 = shop door style,
  1/3 = misc; exact semantics TBD).

## SHIPPED (2026-06-12) — OverworldNav + everything around it

> **The auto-walk ARRIVAL system was rebuilt 2026-06-21/22** — door-snap targeting, per-type confirm rules
> (school / exit / NPC / object), the 0x7F4-gated honest confirm, the genuine tiny-step search, NPC live-chase,
> and the universal learned-spots file (+ how to bundle it in future releases). **Source of truth:
> `database/OVERWORLD_AUTOWALK.md`** — read it before touching the OverworldNav arrival/confirm logic. The
> 2026-06-12 notes below are the original ship.

The plan below was executed the same day. Final state:

### OverworldNav (`Components/Navigation/OverworldNav.cs`) — user-verified
- **`-`/`=`** categories (Places · Exits · NPCs · Other), **`[`/`]`** entries
  nearest-first with live distances, **`\`** distance brief (now also speaks the
  **compass direction** — `WorldDirection`, same wording as the dungeon; note the
  overworld **Z axis is inverted** vs the dungeon, so N/S is flipped there),
  **Backspace** auto-walk (cancel = press again), **`P`** beacon.
- **`P` STEREO POSITIONAL BEACON (rebuilt 2026-06-26).** Replaces the old mono
  `WinBeep` tick. Drives a `BeaconVoice` (smoothed pan + gain + rate + one-pole
  low-pass + inter-aural delay) through `DungeonAudio`. **World-anchored** (the
  sound sits at the interactable's world position, NOT relative to facing — a
  facing-relative version was built and user-rejected as confusing):
  **pan = world east/west** (`PanSign`), **low-pass "muffle" = world north/south**
  (`FrontSign=-1`, bright=north to match the flipped Z), **volume + tick gap =
  distance** (`NearGain/FarGain`, `NearGap/FarGap`, `FarDist=2500`), **ITD**
  (`MaxItd≈28` samples) for the 3D feel. Arrival trill at CHECK; arrival radius
  is the normal `ArriveDist=90` **except** the cramped fridge (`check_reizou`) +
  sofa (`check_sofa_p4p`) → tight 38u (`IsRoomArea()`-gated). Focus-gated
  (mutes + releases `DungeonAudio.SetWant` when alt-tabbed). **KNOWN OPEN:** a
  world-anchored beacon can feel "reversed" when you face away from a target
  (world-vs-body trade-off); intermittent, user couldn't pin it down — deferred
  for player-test data before a deeper hunt. Future: true HRTF for full 360°.
- Auto-walk is self-calibrating (probes movement, learns the camera's
  world angle live, recalibrates each step), with final-approach tap-steps
  until the CHECK fires, 4-direction probe + back-out for corner wedges,
  signed wall-slide sidestep. Walk target = the bound follower stand-point.
- NPCs category reads LIVE cat-3 units; names resolved from the model path
  (`*(node+0x190)→*(+0x8)→*(+0x1C8)` buffer = `\model\npc\n1557_4.AMD`) via
  **`database/npc_model_names.json`** (719 ids; built by
  `tools/build_npc_names.py` from AMD texture-name mining: n1xx Yosuke,
  n2xx Chie, n3xx Yukiko, n4xx Rise, n5xx Kanji, n6xx Naoto, n7xx Teddie,
  n15xx Nanako, n16xx Dojima, npc2: Margaret/Marie/Namatame/Adachi/
  shopkeepers…). Generic models = "Townsperson".
- **Exits**: the `call_*` proc family = transitions; names resolved from
  each proc's `CALL_FIELD(maj,min)` in the decompiled flow ("Exit to
  Shopping District North"); `call_lmap` = DATE-GATED street exits (story
  prompt on plot days, town map otherwise) = "Street exit N".
- **TRUE world player position** (catalog coordinate space):
  `*(*(*(*(0x140EC0FF0)+0x250)+0x48)+0x18)+0x360/+0x364/+0x368`.
  The old `sub_obj+0xD0` chain is a LOCAL space — never mix.
- NavigationAssist (manual record/calibrate teleport) RETIRED.

### Field reader fixes (audit via flow proc names)
- 11 = Okina City (was "Samegawa Riverbank"), 18 = Ski Lodge/Snow Mountain
  (was "Moel Gas Station" — Moel is inside the shopping district map),
  17 = farm area (was shrine; real shrine = 8/9, added), 60-69 = event areas.

### The chronic crash (fixed 2026-06-12)
~40 identical silent crashes since 06-10 (ExecutionEngineException):
crash-dump analysis (`%LOCALAPPDATA%\CrashDumps` + `dotnet-dump analyze`)
showed `ProximityBeacon.Tick → PlayerX2DLive` — the dungeon beacons polled
player position every 50ms in ALL contexts through a chain with NO page
guards (`GetSubObjPtr`). Fixed: IsReadable on every link + beacons
short-circuit unless active-in-dungeon. **Crash-dump forensics is the play
for any silent death — never guess from logs.**

### Shops
Closed the same day — see **`database/SHOP_SYSTEM.md`** (its own doc).

## Remaining overworld-phase queue
Town-map reader (fast travel) → inventory menu (unlocks shop Sell screen) →
date reader check → game menus → FIRST RELEASE BUILD.
