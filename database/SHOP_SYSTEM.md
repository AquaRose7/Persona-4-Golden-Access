# SHOP SYSTEM — source of truth

**SECTION CLOSED 2026-06-12 with user sign-off ("this is PERFECT!!!!").**
The shop reader (`Components/ShopMenu.cs` + `DaidaraCharSelect.cs`) is fully
state-machine driven and works in Daidara and Shiroku (designed shop-generic).
This doc records the real structures — and the dead ends, because this system
burned months on misread fields. **Read this before touching ANY shop code.**

## The state machine (pShop+0x04) — THE foundation

`ShopUpdate` (hooked, SigScan `40 55 53 41 54 41 55 41 57 48 8B EC 48 83 EC
70 48 8B D9` = 0x1402827F0) receives `pShop`. The u16 at **pShop+0x04** is the
CURRENT STATE (set by `FUN_14027e4d0(pShop, newState)`, previous state at
+0x06). Complete map (live-verified through real purchases):

| State | Screen |
|---|---|
| 0x07 | top menu |
| 0x08 | character select (Daidara equip flow) |
| 0x09 | transition into buy flow (Shiroku goes 0x09→0x0A, NO charselect) |
| 0x0A | **item list** (list has focus) |
| 0x0B | **description window open** |
| 0x0C | **amount/quantity window** |
| 0x0D | purchase confirm (game dialog reads itself) |
| 0x0E | purchase complete |
| 0x0F | post-buy prompt (equip?, seen after gear purchase) |
| 0x12 | Sell screen (DONE 2026-06-18 — see "Sell screen" section below) |
| 0x1C | Talk |
| 0x1E→0x20→0x21→0x22 | exit teardown — **struct freed after; guard all reads** |

## The cursor fields (all F10-diff verified live)

| Field | Meaning |
|---|---|
| `pShop+0x9E` (s16) | **row cursor while the list has focus (0x0A)**; −1 when unfocused |
| `pShop+0x32` (s16) | **row cursor while the description window is open (0x0B)** — the "April cursor" was the window-mode cursor all along |
| `pShop+0x4E0` (s16) | selected character slot (0=protagonist, 1=Yosuke, …) |
| `pShop+0x4E2` (s16) | buy quantity (amount window) |
| `pShop+0x35C` (i32) | outer cursor: top-menu row / buy-tab index |
| `pShop+0x340` (s16[]) | top-menu OPTION IDS (count at +0x35A) — shop-agnostic: 0-2 equip categories, 3 Buy, 8 Sell, 9/11 Talk, 10 Exit |
| `pShop+0x60` (ptr) | active list; entries stride 0x14: +0x00 price (i32), +0x04 packed (lo16 = GLOBAL item id, hi16 = tag) |
| `pShop+0x08` (s16) | SHOP TYPE (0..5; 3 = materials shop — Shiroku sell side) |
| `pShop+0x2A` (s16) | screen-builder sub-phase |

Item-id blocks (global ids, confirmed): 0-255 weapons, 256-511 armor,
512-767 accessories, **768-1023 consumables** (Medicine=770; desc =
`HelpBmd.Item[id-768]` — Revival Bead 783→Item[15] matches April evidence).
1024+ = materials/key items, no desc BMD mapped yet.

## Other globals

| Address | Meaning |
|---|---|
| `0x1451BCD70` | wallet (u32 yen) — shared with battle ResultReader |
| `0x1451BCD8E` | **protagonist FIRST name** — 16-byte field, FULL-WIDTH Shift-JIS ("ＨＡＲＵ" = 82 67 82 60 82 71 82 74); last name in the preceding field. Plain-ASCII scans find NOTHING. Decoder: `ShopMenu.ProtagonistName()` |

## Architecture of the reader (v3 family)

- The hooked ShopUpdate **STARVES inside item lists** (it's the top-level
  menu update; the game stops calling it when a sub-screen has focus).
  → ALL logic runs on the 50ms poll thread reading the struct live; the hook
  only captures `pShop`. **Never poll anything in the hook.**
- Announce gating by exact state: rows/items only in 0x0A/0x0B; "Character
  select." only on 0x08; character-switch names only while the list is
  focused (DaidaraCharSelect's hook owns the charselect screen itself and is
  gated on `ShopMenu.ShopState == 0x08`).
- Browse announce = short form (name, price, "You have N yen"); description
  window (0x0B) = full form (name + description + price), auto-read on open.
- Amount window: "How many? 1. 850 yen each." on open; "{n}. Total {n×unit}
  yen." per change. Unit price via the window-row (+0x32) list entry.
- Keys: **F** = re-read current item (full in 0x0B) · **G** = wallet ·
  **F10** = diff-dumper (snapshots pShop 0x700 + UI statics 0x140BEA800+
  0x1800, prints changed shorts vs previous press — the tool that cracked
  every field above; KEEP IT).
- Entry/rebuild announces via a deferred countdown (`_announceListIn`),
  fired on list-opened edges (ActiveListPtr/count populate) and tab/character
  switches.

## DEAD ENDS — do not re-walk

1. **`Mode068` (+0x68)** — "mode discriminator" — STALE, never resets. Years
   of heuristics built on it; all wrong.
2. **`ActiveItemCount` (+0x9A)** — LIES transiently (read 1 with 2 items on
   screen, drifts). Never gate announces on it; validate row CONTENT instead
   (plausible id 1..0x2FFF + price 0..9,999,999 + non-empty name).
3. **The "panel-state global" 0x140BEB4EC** — a MIRAGE. It tracks the
   top-menu highlight (≈ cursor35C+1 with pulse flicker). An early 4-snap
   zigzag mapped it to panels by pure coincidence of cursor positions.
4. **"Session-variable state numbers"** — FALSE conclusion caused by the
   starving hook freezing its last-seen state value. The state map above is
   stable; trust pShop+0x04 read live.
5. **`+0x2E + tab*2` per-tab cursor slots** — wrong guess; weapon +0x2E held
   garbage (row 3 → id 65534). The truth is +0x9E / +0x32 by FOCUS MODE,
   not by tab.
6. The 0x1409A2200 region = packed u32 RVA-pair UI widget records (handler
   cluster 0x140281xxx-0x140282xxx: FUN_140282580 screen builder,
   FUN_1402820d0 menu-option builder, FUN_140280210 screen-object factory
   alloc 0x3C00, FUN_14027f170 = list SORT comparator). Interesting but not
   needed — the pShop fields above suffice.

## Sell screen (0x12) — DONE 2026-06-18 (user-verified "works beautifully")

RE'd from the builder **`FUN_140280f60(pShop+0x18, shopType)`** (called at the
0x07→0x12 transition in ShopUpdate; note `param_1` is `uint*` so `+0x18` =
**byte +0x60**). The sell list reuses the SAME field as the buy list:
- **list ptr = `*(pShop+0x60)`** (ActiveListPtr), **count = `*(int*)(pShop+0x68)`**
- **entries stride 0x14: `+0x00` price (int) · `+0x04` itemId (u16) · `+0x06` qty (u16)**
- **highlighted row = cursor at `pShop+0x32`** (live-confirmed: stepped 0..N in
  lockstep; `+0x9E` mirrors it). The poll announces on `+0x32` change.
- Each category's list ends with a **"Sell all"** bulk row whose id is the
  category BLOCK-BASE (1280 for materials → `GetName` empty) and whose price is
  the TOTAL → labelled "Sell all. N yen". Builder fills items via
  `FUN_14019f2f0(cat,i)`, keeping only owned (inventory qty byte `+DAT_141165930`)
  + sellable (`FUN_140246980==0`). Reader: `ShopMenu.AnnounceSellCursor`.
  The old v2 attempt read the WRONG list (player inventory at +0x10) — deleted.

## Shiroku Pub TRADE shop — DONE 2026-06-18 (user-verified)

Goes through the normal shop hook (state 0x0A list / 0x0B desc), list at `*(pShop+0x60)`,
stride 0x14. Two fixes made it fully accessible:
- **Trade cost (no yen):** each entry carries up to two required materials inline —
  **`+0x0C` matId / `+0x0E` count** and **`+0x10` matId / `+0x12` count**. Owned count =
  `*(byte*)(*(nint*)0x141165930 + matId)`. Announce appends "Needs 5 Iolite, have 1"
  (gated on `price==0`; `ShopMenu.TradeRequirement`). Iolite=2128, materials live in the
  Golden Item2 block (2048+).
- **SCROLL (the big one):** the trade list has 16 items but only ~6 are visible, so
  `+0x9E`/`+0x32` is the WINDOW cursor (0..5) and **`+0x34` is the scroll** (first-visible
  absolute index). Real row = **cursor + `+0x34`** (live-verified: Spring Boots = 5+10 =
  15). `EffectiveRow` now adds it, guarded by total count `+0x68` so non-scrolling shops
  (Daidara) are a no-op. This ALSO fixed the "silent on scroll" symptom (window cursor
  didn't change on scroll → dedupe stayed silent). NOTE: total count is **`+0x68`**;
  `ActiveItemCount`/`+0x9A` (=6) is the VISIBLE window size, not the total.

## Yomenaido BOOKSTORE — DONE 2026-06-18 (user-verified "perfectly")

A CUSTOM menu that **reuses the shop menu-object layout** but does NOT go through
ShopUpdate, so the shop hook never saw it. Cracked via Cheat Engine "find what accesses"
the cursor (a 2-byte short) → render fn **`FUN_140213190`**. `BookstoreMenu.cs` hooks it;
the menu object = **`*(*param_1 + 0x48)`**, with the same shop-shaped fields:
- `+0x04` state — **0x0A** list, **0x0B** Show-Info window (read both); 0x03/0x06/0x09 …
  0x1E-0x22 are just open/close transitions (skip).
- `+0x32` window cursor + `+0x34` scroll → **abs row = cursor + scroll**.
- `+0x60` list ptr, `+0x68` count; entries stride **0x14**: `+0x00` price (yen),
  `+0x04` item id (u16).

Books are key-item ids (1024-1280, `HelpBmd.Event`) — e.g. Expert Study Methods 1136,
Beginner Fishing 1146, The Lovely Man 1259. Announces name + price + "you have N yen"
(wallet `0x1451BCD70`) + description on cursor change. **Gated on
`ShopMenu.ShopState == 0xFFFF`** so it never fights the real shop reader. (Its open/close
states show it's a full shop state machine, just behind a different renderer.)

## Parked (future phases)
- Materials/key-item description BMDs (ids 1024+).
- Same-name-different-desc armor verification once shops stock more.
- Buy-confirm (0x0D) content reading — the game dialog already reads it.
