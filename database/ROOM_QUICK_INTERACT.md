# Room "Quick Interact" menu — source of truth (2026-06-29; expanded 2026-07-02)

Lets a blind player reliably reach and interact with **tricky in-room objects** (bed/futon,
desk, TV, fridge, save point, …) that the overworld auto-walk handles poorly in cramped rooms.
**Shift+F** in a set-up room opens a spoken **"Quick interact"** list; picking an object
**teleports** to the room's nearest entry point, **nudges** a short fixed direction into the
game's interact prompt, and announces **"Press to interact"** — the player presses to interact
**normally** (the mod never triggers the event itself → no save risk). User-verified working
(your room + living room).

## Triggers

- **Keyboard:** `Shift+F`.
- **Controller:** **LT + X** (left TRIGGER + X). `ControllerInput` EdgeFires it →
  `RoomActionMenu.ControllerOpenRequest` (a static flag the poll consumes → `OpenMenu`). The game's
  X (= Open-Menu) is suppressed while LT is held, so no game menu sneaks in.
  ⚠ **UX gotcha:** while a trigger is held the mod mutes the pad buttons, so after **LT+X** the user
  must **release the trigger** to navigate the ADV_SEL with the d-pad/A. (Holding LT = menu opens but
  feels frozen.) Verified via the X-press XInput diag (left trigger → leftTrig=255 ltHeld=True; the
  left bumper is LB=0x0100 and does NOT set ltHeld).

## Components & data flow

1. **`Shift+F`** (`Components/RoomActionMenu.cs`, poll thread) / **LT+X** (controller): in a set-up
   room, arms FlowScript **BIT 7905** and synthesises a **clean F** (after the physical keys release).
2. **`ControllerInput.MaskKeyboard`** suppresses the game's own read of **physical F while Shift
   is held** (DInput keyboard hook; `SuppressShiftF=true`) — so Shift+F does NOT also open the
   normal game menu (the same double-fire fix as the controller buttons). Plain F is untouched.
3. **`field.flow` `order_party_softhook`** (END of the BIT checks): `if (BIT_CHK(7905))` →
   branch by `GET_FIELD_MINOR()` → `ADV_SEL(<Room>_Text, <Room>, 0)`. Each object pick does
   `BIT_ON(<79xx>)` + `CALL_FIELD_SAFE(major, minor, entry, 0)` and returns. (Cancel just returns
   → the normal menu opens once — accepted quirk, like the tutorial bubbles.)
4. **`RoomActionMenu.CheckObjectBits`**: after the reload, sees the object BIT, **waits for the
   player to load** (valid `WorldPlayerPos`, back in the room; ~700ms + guard), then holds the
   object's **nudge step(s)** (W/A/S/D via `keybd_event` scan codes) and announces the prompt
   (`FieldTracker.CheckPromptActive`).

The teleport can ONLY land at the game's **fixed entry points** (we can't teleport to an arbitrary
spot — position-write doesn't hold in the 2.5D overworld). So each object = nearest entry + a short
nudge.

## Where each piece lives (must stay aligned by menu index / bit)

| Piece | File | Holds |
|---|---|---|
| Labels | `FEmulator/BF/rooms.msg` | `[msg <Room>_Text]` header + `[sel <Room>]` option list (last = Cancel) |
| Entry  | `FEmulator/BF/field.flow` (7905 branch) | per-pick `BIT_ON(79xx)` + `CALL_FIELD_SAFE(major,minor,ENTRY,0)` |
| Nudge  | `Components/RoomActionMenu.cs` `_rooms` dict | `"major_minor" → (bit, label, (dir,ms)[] steps)`; empty steps = no nudge |

BIT ranges: room menu trigger **7905**; object bits **79xx** (your room 7_3 = 7910–7915, living
room 7_2 = 7920–7922, Shopping District North 8_1 = 7930–7931, Shopping District South 8_2 = 7940,
Okina City 11_1 = **7960–7962**). The `field.flow` 7905 block branches on **GET_FIELD_MAJOR() AND
GET_FIELD_MINOR()** (7_x, 8_x, 11_x).

⚠ **RESERVED — do NOT use for object bits: 7900 (tutorial trigger) and 7950 (tutorial
WelcomeSeenFlag, `Tutorial.cs`).** 2026-07-06 a new Okina object got 7950 → the tutorial's flag
and the object bit were the SAME switch: walking fired the tutorial (spam) AND `CheckObjectBits`
read 7950 as "object selected" → nudged everywhere; the menu also flipped the tutorial flag.
Okina was moved to 7960+. When adding a room, SKIP 7900 and 7950.

## Current rooms (verified)

- **Your room `7_3`** (`MyRoom`): Futon (entry 4, S 600) · Desk (1, none) · TV (2, D 600) ·
  Small desk (3, S 600 then A 1200) · Sofa (0, A 600) · Calendar (1, W 300).
- **Living room `7_2`** (`LivingRoom`): Save point (entry 6, none) · Fridge (0, D 600 then W 300) ·
  **Farm** (0, **S 1500 then D 600** — user-timed 2026-07-02; the yard's farm-tools check).
- **Shopping District North `8_1`** (`ShopNorth`, added 2026-07-02): **Aiya Chinese Diner**
  (entry 7) · **Bulletin board** (entry 1) — both use the **auto-walk HANDOFF** (below), not a nudge.

## Big-outdoor-area variant: auto-walk handoff (2026-07-02)

In a huge outdoor area a fixed blind nudge can't cover the hundreds of units from the entry to
the object, so 8_1's objects teleport to the nearest entry then hand off to the **overworld
auto-walk**: `RoomActionMenu._walkProc` (bit → catalog proc name, e.g. `tyuuka` = Aiya) →
`OverworldNav.WalkToProcTarget(proc, announce:false)` (no "Walking to X" — the player just picked
it; the arrival announce stays). The handoff waits for `OverworldNav.ReadyForWalk` first (the
poll must see the post-teleport field active again or its state reset kills the walk). A
**residual nudge** safety net exists: if the walk ends WITHOUT the check and the object has
steps, they run afterwards (currently unused — both 8_1 targets arrive on the check).
`WalkToProcTarget` prefers the duplicate-proc target that has a LEARNED spot.

⚠ **Do NOT use the handoff for cramped interiors** — tried for the Farm 2026-07-02 and REVERTED
(the walker wandered around the house). Rooms stay deterministic teleport+nudge; when tuning a
nudge with the user, talk in **literal keys W/A/S/D** (the room cameras make key↔compass
mapping non-obvious — the Farm's "south" turned out to be the S key after W was tried).

Related fix shipped the same day: **`OverworldNav.SaveCalib` now dual-writes** the learned-spots
file (mod folder + database) — it used to save only to `database/` while loading the mod-folder
copy, so every learned spot silently died on restart (same split-brain as the old dungeon-marks
bug). The bulletin board's exact spot is self-learned data now persisted.

## How to ADD a new room / object

1. **Find the entries:** re-enable the **Ctrl+F9 scout** (uncomment the block in
   `RoomActionMenu.Poll` + re-add `private bool _scoutWas;`). In the room, Ctrl+F9 cycles teleport
   to entry 0..7 and speaks the index; feel where each lands.
2. **Pick, per object:** the nearest entry + a nudge (direction + ms, or none) — tune by testing.
3. **`rooms.msg`:** add `[msg <Room>_Text]` (header "Quick interact") + `[sel <Room>]` with the
   labels, ending in `Cancel`.
4. **`field.flow`:** add `if (GET_FIELD_MINOR() == N)` inside the 7905 block →
   `ADV_SEL(<Room>_Text, <Room>, 0)` + one `if (pick==i) { BIT_ON(79xx); CALL_FIELD_SAFE(major,N,entry,0); return; }`
   per object.
5. **`RoomActionMenu._rooms`:** add `["major_minor"] = new (int,string,(char,int)[])[] { (bit,label,steps), … }`.
   `InRoom()` keys off this dict, so Shift+F auto-enables there.
6. Re-disable the scout, build, **and hand-copy `field.flow` + `rooms.msg` to `<mod>/FEmulator/BF/`**
   (they do NOT deploy with the DLL).

## Notes / dead ends

- Recording a custom walk was tried and dropped — it's not a coordinates problem; one teleport +
  a short fixed nudge is enough and far simpler. (The record/replay code was removed.)
- Position-write teleport to an arbitrary spot: rejected — doesn't hold in the 2.5D overworld and
  the prompt won't fire from static presence.
- Shipping builds: the Ctrl+F9 scout is UNBOUND (dev-only). `ScoutNextEntry()` kept as dead code.
- Release bundle: the feature is the DLL + `field.flow` + `rooms.msg` (both already under
  `FEmulator/BF/`) — no separate data file.
