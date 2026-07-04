# VELVET_ROOM.md — Velvet Room menu accessibility (source of truth)

Status: **~COMPLETE + user-verified (2026-06-16/17).** Almost the entire Velvet
Room menu system reads. All readers live in
`p4g64.accessibility-master/p4g64.accessibility/Components/VelvetFusion.cs`
(it hooks many velvet facility dispatchers by absolute address). The old
`VelvetMenu.cs` poll reader is **retired** (its construction is commented out in
`Mod.cs`) — the root menu is now read instantly via a hook (fixed the lag).

Deep session history + every dead end: memory `velvet_room_fusion_re.md`.
Persona/skill accessors reused here: memory `persona_panel_affinity.md` + `Battle.cs`.

## Platform notes
- ASLR off, image base `0x140000000` → all `FUN_140xxxxxx` addresses are constant.
- Every velvet facility object is per-session heap; we never hold static pointers to
  them — we hook the per-frame dispatcher and read offsets off its object param.
- All unsafe reads go through `IsReadable` (uncatchable AVE otherwise).

## Facility / dispatcher map (what we hook)

| Screen | Dispatcher (hooked, abs addr) | Flag | Key offsets (object-relative) |
|---|---|---|---|
| **Root / top menu** | `FUN_14021E2A0` @0x21E2A0 | flag@+0xF0 bit 0x01 (menu draw `FUN_14021BBE0`) | count@+0x288, cursor@+0x292, scroll@+0x294, char* label @ `*(obj+0x1F0+abs*0x18)` |
| **Fusion** | `FUN_140236810` @0x236810 (+0x38) AND `FUN_1402369A0` (+0x98) | flag@+0xE8 | see Fusion section |
| **Compendium** | `FUN_140265060` @0x265060 | flag@+0xF0 | see Compendium section |
| **Skill Cards** | `FUN_14028C010` @0x28C010 → menu `FUN_140287E10` | (calls menu each frame) | count@+0xE8, cursor@+0xEA, scroll@+0xEC, char* @ `*(obj+0x98+abs*0x18)` |
| **Rescue Requests** | `FUN_14029F190` @0x29F190 → menu `FUN_140299220` | (calls menu each frame) | count@+0xE0, cursor@+0xE2, scroll@+0xE4, char* @ `*(obj+0x90+abs*0x18)` |
| **Ask (tutorial topics)** | `FUN_140221DF0` @0x221DF0 (`fcl_cmb_talk`) | param_2 = obj | LINKED LIST — see Ask section |

`abs = scroll + cursor` everywhere. "char* label" = read the null-terminated ASCII
string at the pointer (`ReadAscii`). Check on Dwellers reads for free (shares a hooked
facility). `G` key anywhere in velvet speaks the wallet (`0x1451BCD70`).

## Fusion (FUN_140236810 @obj+0x38, FUN_1402369A0 @obj+0x98 — both flag@+0xE8)
Per-frame: hook FUN_140236810; for screens its second dispatcher draws (inheritance),
hook the draw fn directly.
- **L2 menu** (bit 0x400, draw FUN_1402258F0): Fuse / Fusion Forecast / Talk / Back.
  count@+0x188, cursor@+0x18A. **ID-based**: id @ `*(short*)(obj+0xFA+cursor*0x18)`;
  6=Fuse, 56=Fusion Forecast, 61=Talk, 63=Back.
- **L3 method menu** (bit 0x01, draw FUN_140226A20): Search / Normal / Triangle / Back.
  count@+0x1F0, cursor@+0x1F2. **ID-based** (2026-06-17): id (glyph index) @
  `*(short*)(obj+0x1D0+cursor*4)`; **7=Search, 0=Normal, 1=Triangle, 6=Back**.
- **Persona-pick list** (bit 0x02, draw FUN_140229190): count@+0x740, cursor@+0x744.
  Shown in MC STOCK ORDER → row N = Nth non-empty stock slot
  (`*(0x141165900)+0xA34+slot*0x30`, PersonaInfo {id@+0x02, level@+0x04, valid@+0x00!=0}).
- **Result preview**: per left-row entry @ `obj+0x356+row*0x50` {result id@+0, level@+2,
  innate skills@+0x0A, stats@+0x1A}. On the 2nd-persona screen reads "{candidate}. Makes
  {result}".
- **Result PERSONA PANEL** (profile, bit 0x40): navigable I/K (section) · J/L (item) —
  overview / 7 resistances / 5 stats / skills+descriptions / NEXT EXP + next-skill. Reads
  the result for the CURRENTLY highlighted 2nd persona (`obj+0x356+cursor(+0x744)*0x50`).
- **SKILL-INHERITANCE** ("select skills to inherit", bit 0x4000, draw `FUN_140234B20`,
  hooked directly @0x234B20): count@+0x1458, scroll@+0x33F2C, cursor@+0x33F2E (abs=sum);
  skill rows @ `obj+0x12D8 + abs*4` {id@+0 (u16), flags@+2}. Reads skill name + **SP/HP
  cost** + description. Cost from skill data (`Skill.GetActiveSkillData`): type@+0x06
  (1=HP,2=SP), values @+0x08/+0x0A; SP = +8 + +0xA (member-independent); HP = flat +8+0xA.
- **OWNED-persona detail** (bit 0x20 WITHOUT 0x40, requires list bit 0x02): viewing one of
  YOUR personas from the select list. Resolves the stock PersonaInfo for the cursor row
  and builds the panel (real stats via `Battle.PersonaTotalStats`, skills @entry+0x0C).
- **SEARCH** (guided fusion, bit 0x2000, draw `FUN_14022CFB0`): result list count@+0x33C14,
  cursor@+0x33C16, scroll@+0x33C18; each result is a 0x278 entry @ `obj+0x25C4+abs*0x278`;
  the result persona is sub-entry 0 and MATERIALS are sub-entries at +0x30/+0x60/+0x90
  (each 0x30 sub-entry: id@+0x02, level@+0x04, via shared drawer FUN_140257B20). Reads
  "{result}, {arcana}, level N. From {mat1} level a, …". The result's persona PANEL
  (flag 0x40+0x2000) is built BY ID (species base stats @ `*(0x140EC0958)+id*0xE+4`,
  default skills via `Battle.PersonaLearnedSkills`).

## Compendium (FUN_140265060 @0x265060, flag@+0xF0)
- **L2 "What will you do?"** (bit 0x01, draw FUN_14025BC80): View Compendium / Register
  Personas / Ask / Cancel. count@+0x160, cursor@+0x162, scroll@+0x164, char* @
  `*(obj+0x110+abs*0x18)`.
- **All Personas list** (bit 0x02, draw FUN_14025E1D0): count@+0x3AD8, cursor@+0x3ADC,
  scroll@+0x3ADE; entry inline @ `obj+0x2D8 + abs*0x38` {id@+0x02, level@+0x04, cost(¥)@
  +0x30 (uint), stats@+0x1C (5 bytes), **registered = +0x34 bit0 CLEAR** (set = "??")}.
  Registered: name/arcana/level/cost + "you have N yen". Unregistered: arcana/level +
  "not registered" (name hidden, matching the screen's "??").
- **Persona profile** (bit 0x08 added): navigable panel for the selected list persona,
  stats from entry+0x1C.
- **Register Personas list** (bit 0x04, draw FUN_1402628F0): count@+0x3D20, cursor@+0x3D28,
  scroll@+0x3D2A; per-row persona record ptr @ `*(obj+0x3CB8+abs*8)` {id@+0x02, level@+0x04}.
- **Register persona panel** (bit 0x04 + 0x08): reuses the owned-persona panel builder on
  the record ptr (it's a stock PersonaInfo).

## Ask tutorial menu (`fcl_cmb_talk`, handler FUN_140221DF0 @0x221DF0)
Reads in ALL Ask contexts (Compendium / Skill Cards / Rescue all have an "Ask" option).
LINKED-LIST menu on the facility obj (param_2): menu-data ptr @ `*(obj+0x90)`, count @
`*(int*)(menuData+0xC)`, cursor @+0x98 (short), scroll @+0x9A; entries are a linked list
from `*(menuData+0x10)` walked via `+0x18`; each entry's label char* = `*(entry+0x20)+8`.
(Found via snapshot-diff of a 4+-option Ask menu — a 2-option one was too noisy. The
tutorial DIALOG that follows reads via the normal `Dialogue.cs`.)

## How to find a velvet facility (the method that worked)
- Dispatchers are referenced as DATA from a fn-ptr table at `0x17785Dxxx` (unreadable in
  the `-noanalysis` Ghidra DB; relocs only).
- `FindCallersOf` the shared **text-row drawer `FUN_140258EF0`** lists all char*-label
  menu panels; the **persona-row drawer is `FUN_140257B20`** (entry id@+2, level@+4). A
  panel's dispatcher = its caller (`FindXrefsTo`).
- Snapshot the live facility object for a static code pointer (`0x140xxxxxxx`) → that's the
  handler (how Ask/`fcl_cmb_talk` was nailed). Velvet facility objects otherwise have NO
  static pointers, so hook the dispatcher and read offsets off its param.
- The velvet facility dispatchers share the prologue `48 89 5C 24 08 57 48 83 EC 20` — too
  common to SigScan; **hook by absolute address** (ASLR off).

## Golden arcana fix
The species/arcana table `*(0x140EC0958)` (stride 0xE, arcana byte @+0x02) is valid even at
high/Golden ids. `Battle._arcana` (Fool=1..Hunger=25) mislabels Golden bytes, so velvet
reads route arcana through **`VelvetFusion.PanelArcana(id)`** (byte 27 = Aeon; extend the
switch as more Golden arcana surface).

## DONE since (resolved)
- **Buy / Give Skill Cards sub-screens — DONE 2026-06-18 (user-verified).** Was blocked
  until the player owned skill cards; finished once there was content. `VelvetFusion.ReadSkillCardList`
  hooks the skill-cards menu `FUN_14028C010`. Gates: **Give = `obj+0x1340`**, **Buy = `obj+0x1328`
  bit 2** (decompile gotcha — `param_1+0x265` on a `longlong*` is a byte offset ×8 = `0x1328`).
  Cursor `+0x2A4` + scroll `+0x2A6`; entry at `obj+0x6FC + abs*0x0C`, id @ `+0`; "Register all"
  sentinel id = 1536; Buy price = `entry+0x08`.

## SPREAD FUSIONS — Cross / Pentagon / Hexagon (unlocked + wired 2026-07-03, pending user test)

The three multi-persona spread fusions appeared at the user's rank. Findings (from the UNMAPPED-
flag dumps while the user browsed):
- **Method menu (L3)**: the new options are ID-based like the rest — mapped 2=Cross, 3=Pentagon,
  4=Hexagon by the glyph-id sequence (7=Search, 0=Normal, 1=Triangle, 6=Back verified); an UNKNOWN
  id now logs `method-menu UNKNOWN id` so a wrong guess is caught in one hover.
- **Summon screens** ("What will you summon?", result column + numbered MATERIAL column, F =
  Persona Status, Registered marks + Forecast Bonus footer): routed by **EXACT low-word flag match
  `0x0010` / `0x0014`** (observed `0x80040010`/`0x80040014`). ⚠ REGRESSION LESSON (2026-07-03): a
  loose "bit 0x10 set" test STOLE other fusion states that carry 0x10 alongside their own bits and
  broke Normal/Triangle reading — velvet flag WORDS are state combos, route by exact match unless a
  bit is proven exclusive.
  **LAYOUT (snapshot-hunted live 2026-07-03, 0/1/0/1/2 cursor zigzag over Cross+Pentagon; NOT the
  Search offsets — those read 0 here):**
  - count `obj+0x10B0` (u16), cursor `obj+0x10B2` (u16); a mirror pair sits at `+0x10E2/+0x10E6`
    (if long Hexagon lists ever cap the cursor, try `+0x10E6` as the absolute row).
  - result entry k @ `obj+0x8E2 + k*0x30`: persona id u16 @+0, level u16 @+2, u32 @+6 (cost?),
    5 stat bytes @+0x1A (St/Ma/En/Ag/Lu of the result).
  - recipe ptr k @ `obj+0xB28 + k*0x70` → heap block: result id u16 @+0 (**validate against the
    entry id** — blocks go stale across screen switches, the check makes misreads impossible),
    material persona ids u16[] @+0x0C zero-terminated (4/5/6 = Cross/Pentagon/Hexagon).
  Verified against screenshots: Cross = Neko Shogun(23) lv32 ← [53,13,73,102], Tam Lin(87) lv53;
  Pentagon = Yoshitsune(82) lv75 ← 5 mats, Black Frost(119), Futsunushi(38), Yatsufusa(20).
  `ReadSpread` speaks "Name, Arcana, level N. k of M. From mat1, mat2, …".
  **F "Persona Status" on a spread result** = flag low words **0x34 / 0x134** (⚠ carries NO 0x40
  profile bit, unlike every other velvet profile) → `ReadSpreadPersonaPanel` builds the same
  navigable I/K/J/L panel as the Search result (`BuildSearchSections`) from the spread entry's
  id+level. `_spreadPanelId` resets when the list re-focuses so reopening F re-reads.
- Also seen in the dumps: `obj+0xF0` (u16) = the fusion MODE id on these screens (2/3/4 as the
  user entered Cross/Pentagon/Hexagon).

## FUSION FORECAST — ✅ SOLVED 2026-07-03 (data-file route, pending user test)

The sprite-enum RE was a dead end by design: `FUN_140233570` draws only FIXED label sprites
(headers, Today/Tomorrow = `0x5D+day` via FUN_1402332E0, "Trigger/Effect" boxes); the per-day
VALUES are pre-rendered textures at `obj+0x34120/34128/34148` with no readable enum behind them.
**The forecast is DATE-DETERMINISTIC, so the reader uses `database/fusion_forecast.json`**
(the weather_schedule.json precedent): the GOLDEN daily table from GameFAQs FAQ 81937 (141
forecast days incl. the Jan/Feb epilogue; cross-checked against the vanilla wikidot table —
overlaps agree modulo wording; Golden's changed/added days like 5/25 Aeon are real changes).
A date absent from the sheet = "no fusion forecast." Reader: flag low word **0x8200** (0x8600/
0x8400 = transitions carrying the L2 bit) → `ReadForecast`: day tab @`obj+0x1748` (0=Today,
1=Tomorrow), date = `FieldTracker.GameDate(tab)` (new static mirror of the game-day counter),
speaks "Tomorrow, July 4: Skill change." **Bundle `fusion_forecast.json` flat in the mod folder
every release** (already copied to the live mod dir).

## COMPENDIUM PROFILE — real skills + Description (2026-07-04, pending user test)

- **Registered entry layout DECODED** (CompDiag dump vs the Hua Po screenshot): the 0x38-stride
  list entry = `+0x00 u16 flags?(1)` · `+0x02 u16 persona id` · `+0x04 u16 level` · `+0x08 u32 exp`
  · **`+0x0C u16 skills[8]`** (real REGISTERED skills, on-screen order; passives are ids ≥ 512) ·
  `+0x1C byte stats[5]` · `+0x30 u32 summon cost` · `+0x34 flags (bit0 = NOT registered)`.
  `BuildCompendiumProfile` now reads the real skills (learnset only as unreadable-entry fallback) —
  the old learnset derivation spoke wrong skills whenever the registered set differed.
- **Description (flavor text)**: the Info tab's text renders as glyph runs through FUN_140450C60
  (every p6==0 full on this screen is list scratch). `CompendiumInfoText.cs` (rooted in Mod.cs)
  captures the ordered body lines, keyed by `VelvetFusion.CompendiumProfileId`, gated on
  `CompendiumProfileTick` freshness. **It SPEAKS the settled text itself (appended, no interrupt)
  on EVERY render of the Info tab** — the tab redraws the text each time it's (re)entered, and
  "glyph activity after >400ms idle" = a fresh render pass → recollect + re-announce (user pref
  2026-07-04; the I/K "Description" section variant was removed). Two hard-won details: the body
  draws ONCE (not per frame) so the LAST line needs a ~100ms staleness flush (heartbeat = the
  list side's constant p6==0 draws), and announcing waits for a 250ms-settled capture so the
  tail is guaranteed in.

## STILL OPEN (next session)
1. Spread-fusion extras: the material "Registered" mark + compendium level (Phoenix "22" in
   the screenshot), and the greyed not-owned state.
2. Buy/Give Skill Cards sub-screens (see the skill-cards section).
