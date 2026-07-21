# TV Listings (the bonus-content guide) — reader RE + design

**Status: SHIPPED 2026-07-07 (v1.4.0 work).** Component:
`Components/TvListingsReader.cs`. Catalog: `database/tvlistings_catalog.json`
(also copied flat into the mod folder — edit + relaunch, no rebuild).

## What the screen is

The TV guide reachable from the MAIN MENU ("TV Listings") and in-game (TV
Overlay). A horizontal strip of channels, each a vertical list of program
cards (title + description + New badge; locked = "Coming Soon" card). It is
the browser for the Golden bonus content: Edogawa lectures (`lesson.pack`),
Miracle Quiz (`quiz.arc`), anime rewatch (`animCh.arc`), Soejima art
(`artCh.arc`), Music King playlists (`musicCh.arc`), concerts (`liveCh.arc`),
P4G promos, P4 Arena / P4 Animation trailers. Assets: `title/` in data_e.cpk
(`channelMain.arc` = the guide's own sheets).

## ★ Why the usual menu-reader approach failed (do not re-walk)

- The screen draws **NO font glyphs**: every card is a PRERENDERED TEXTURE
  (`tv_listings02spr.spr`, 15 pages of 8bpp TMX, one bg sheet per channel).
  The shared UI-text fn `FUN_140450C60` never fires (UiTextSpy caught only the
  top-bar "C"). So there is nothing to hook for text — the reader POLLS state
  and speaks from a catalog.
- The Vita-era `user_community` module fns (`0x1403F6290` grid anim etc.) do
  NOT run for the PC main-menu guide — hooking them was a dead end. The PC
  screen is the `title/` module's task (below).
- The module dispatcher is VMProtect-virtualized; fn-pointer tables are only
  referenced from `.arch` — static xref tracing dies there. The TASK REGISTRY
  (below) is the reliable anchor.

## The task registry (general-purpose — reusable for ANY screen)

The game runs UI screens as named tasks. `FindTaskByName = FUN_1404FB910`
(559 callers) walks 3 singly-linked lists:

- **List heads (static): `0x1462486F8`, `0x1462486A8`, `0x146248768`**
- Node: ASCII name at `+0x00` (hash at `+0x18`), **next at `+0x50`**,
  update-fn low32 at `+0x34`(hi of the u64 at 0x30..), draw-fn, and
  **work struct ptr (u64) at `+0x48`**.
- Task names seen live: `CHANNEL_MAIN_PROC`, `CH_READ_PROC`, `titleDrawProc`,
  `gameSequence`, `weatherProcess`, `dateMainProcess`, `loadingProc`,
  `user_community_audience`, `CtrlConnectionCheckMain`, `NetworkDialog`…
  **This is a new universal anchor pattern: any screen with a named task can
  be found without a hook.**

## The guide task: CHANNEL_MAIN_PROC

- update fn `0x14034F2C0`, draw `0x140350D80` (virtualized stub), loader task
  `CH_READ_PROC` update = `0x140350F10` (loads channelMain.arc).
- **work struct** (`*(node+0x48)`), fields used by the reader:
  - `+0x04` (i32) **screen state**: 0-2 = DORMANT (task persists after the
    screen closes — gate on this or the reader talks outside the menu!),
    3/4 = opening anim (cursor fields NOT yet valid — log-proven garbage),
    5-7 = open/settle, **8 = browse idle, 9/10 = cursor-move settle,
    14-16 = zoomed card view (cursor still tracks), 18 = un-zoom,
    33/35 = long channel-jump anim, 0x13+/0x20+/0x22 = watch/confirm
    subscreens.** Reader announces when state in [5, 0x30].
  - `+0x60` (i32) **selected PROGRAM ID** (not an index!). >= 100 = the
    channel-footer info card (id = 100 + channel).
  - `+0x64` (i32) **channel index** 0..7; the main-menu guide navigates
    channels 1..6 only (0 and 7 exist for the in-game 7-strip variant).

## Static data (ASLR off — read directly)

- **Program table `0x140B0E400`**: 8 channels x 13 rows, stride 0x28.
  entry+0x00 = program id (i32), -1 = end of channel; ids >= 100 = footer.
  Rest of entry: sheet x/y floats + 3 callback VAs (low32+flag pairs).
  Channel contents: ch0 = {0} concerts; ch1 = {1..10} lectures; ch2 =
  {11,12,13} quiz; ch3 = {14} personamations; ch4 = {27,28} promos; ch5 =
  {29,30,31} art; ch6 = {32,33,34} music king; ch7 = {35,36,37} hits/arena/
  animation. (Ids 15-26 = the JP-only 12-part cast-interview show — in-game
  overlay table, not in this one.)
- **Unlock byte**: `*(*(0x1451BCCA8)) + 0x80 + id` → 0 = locked ("Coming
  Soon"), 1 = watched, 2 = unwatched (**"New" badge**), 3 = special (watching
  quits the game session). Visibility check fn: `FUN_140340860(ch,row)`.
- **English program titles**: `0x140AA44A0 + id*0x40` (ASCII, stride 0x40;
  one block per language in .xpdata — the reader always reads English).
- **English channel names**: `0x140AA0DA0`, stride 0x40 (a NAME table, not
  channel-ordered). Real channel mapping (screenshot-verified 2026-07-07, in
  the catalog): ch0 YBC · ch1 "2ch Arts & Edutainment" · ch2 "3ch Inaba
  Public Access" · ch3 "4ch Big TV" · ch4 "6ch PBS" · ch5 "7ch Tatsu TV" ·
  ch6 "8ch Yasovision" · ch7 "9ch Jacktoons". The on-screen numbering SKIPS
  5ch (matches the footer-id gap 104).

## Reader design (TvListingsReader.cs)

Poll thread (100 ms, focus-gated): find task → work → (state, id, ch); all
reads IsReadable-guarded. Announce on (id, ch) change; channel name prefixed
only when the channel changes; "TV Listings" greeting + 900 ms settle on
activation. Locked card = "Coming soon." ONLY (spoiler rule — never leak the
title). Titles/descriptions from `tvlistings_catalog.json` (descriptions
transcribed from the decoded sheet textures; titles fall back to the EXE
table read). Every spoken line is logged `[TvList] state=.. ch=.. id=..`.

## Asset notes (for future work)

- `channelMain.arc` format: `[u32 count]` then `[name 32][u32 size][data]`.
  `tv_listings02spr.spr` = SPR0 with 15 embedded TMX0 (8bpp PSMT8, palette
  256x4 with PS2 CLUT swizzle, alpha 0..128 doubled). Decoder in session
  scratch; sheets transcribed 2026-07-07.
- `tv_listings03spr.spr` / `tv_listings06.tmx` = 4bpp PSMT4 TV-static noise
  (no text).
- Channel BMDs with per-episode blurbs for the SUBSCREENS (not the guide):
  `animCh.bmd` (CH_ANIME_DETAILxxx), `artCh.bmd` (CH_ART_DETAILxxx),
  `quiz.arc` msg_quiz*.bmd, `mode_init.arc` (difficulty select — note: the
  5 difficulty help texts DifficultyMenu hardcodes live in
  `mode_init_help.bmd`).

## Open items

- Channel names for ch2..ch6 (numeric placeholders) — verify the real
  on-screen names/order, then edit the catalog json.
- The IN-GAME guide variant (7 strips, `DAT_140FD01E4`, ids 15-26 + 0x41+,
  0x50, 0x90+ from a second table) — untested; reader gates are generic and
  should mostly work, but the id->catalog coverage is main-menu only.
- Subscreen reading (quiz questions, anime episode picker) — separate systems
  (msg_quiz*.bmd / animCh.bmd render paths unknown, likely also baked or
  BMD-driven).

## Channel SUBSCREENS (the players) — 2026-07-07, second half

Each channel's player is its OWN named task, created on open and destroyed on
close (task presence == screen open): **`MUSIC_CHANNEL`** (Music King song
lists), **`ANIME_CHANNEL`** (Daily Personamations episode carousel). Work
struct (from `*(node+0x48)`):

- MUSIC: state u16 @+0x00 (12 idle / 13 moving / 14 blip), cursor u16 @+0x10
  — **ABSOLUTE index over the UNLOCKED songs** (wraps at the end).
- ANIME: state u16 @+0x04 (12/13), cursor u16 @+0x10 (absolute, wraps).

⚠ THE LISTS HIDE LOCKED ENTRIES — any static cursor→name mapping mis-indexes
and LEAKS SPOILERS (shipped once, user caught it). The subscreens draw text
through the shared UI-text fn `FUN_140450C60` (full strings p6==0 for rows/
titles; GLYPH STREAMS p6!=0 for the description panels), so the reader
CAPTURES the game's own draws instead:

- ANIME (capture, user-verified): capture window DELAYED ~400 ms past the
  carousel slide (else the leaving title/old description mix in). The slide
  draws OLD then NEW title (both p2==1) — the LAST p2==1 full string is the
  selection; p2==12 fulls = background preview dialogue (filter);
  description = assembled glyph lines (p6!=0 per-char draws keyed by the
  line object).
- MUSIC — ★ FINAL, user-verified "works perfectly": NO capture. The work
  struct holds THREE per-episode UNLOCKED-track index arrays (u16, strictly
  increasing, values = indices into the episode's static playlist):
  **program 32 Vocal → work+0xB6 · program 33 Event → work+0x16 ·
  program 34 Battle/Dungeon → work+0x66.** The cursor indexes the ARRAY:
  `name = staticPlaylist[arr[cursor]]`, count = arr length — exact,
  scroll/speed/wrap-independent. Static playlists live in the catalog
  (`music_playlists`, order-verified vs screenshots): 32 = 13 tracks,
  33 = 31 tracks (base file 0xa9e240 u32 list), 34 = 21 tracks.
  Spoiler-safe: the array only ever contains unlocked entries.
  DEAD ENDS (do not re-walk): capture-order rotation (capture can start
  mid-frame → rotated rows), window-tracking heuristics (break on fast
  presses/double-steps), the stride-0x54 "row widgets" at work+0xE60
  (static layout objects, ids ≠ tracks).
- `anime_episodes` in the catalog is UNUSED (capture replaced it);
  `music_playlists` IS used (the per-episode name tables).

## (unrelated, filed here for the task-registry pattern) Social Link Rank-Up banner
`SocialLinkRankUp.cs` — same task-registry anchor. `cmmRankUp` task work:
**+0x04 = COMMUNITY ID, +0x08 = new rank** (Yukiko id 5 / Eri id 29 verified).
The full commu-id → character/arcana table was DERIVED FROM THE GAME'S OWN
ART (no external research): `commu/bustup/com_kyaraNN.tmx` portraits in
data.cpk, decoded to PNG (PSMT8, same CLUT-swizzle decoder as the TV sheets)
→ **commu_id = bustup asset number + 1** (Yukiko portrait 0x04→id 5, Eri
portrait 0x1c→id 29 both confirm). `database/social_links.json` holds it;
SCR_COMMU_<suffix> script task is the fallback; ids self-harvest from
`[RankUp] id=..` logs. Portrait ids that lack a confident face are left to
the SCR fallback.
