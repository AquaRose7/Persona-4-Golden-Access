# Battle System Accessibility — Status

Source-of-truth status doc for the in-battle screen-reader work (Phase B). All code is in
`p4g64.accessibility-master/p4g64.accessibility/Components/Battle/`. Read this first when
resuming battle work. Companion memory: `memory/persona_panel_affinity.md`,
`memory/battle_result_screen.md`, `memory/battle_structs_wip.md`, `memory/battle_audit_2026-06-04.md`.

Image base `0x140000000`, ASLR off → all `0x140…` / BSS addresses are constant. Every unsafe
read goes through `IsReadable` (AVE is uncatchable in .NET 9). Sub-components are constructed
in `Battle.cs`'s constructor and receive `IReloadedHooks`.

---

## 2026-06-30 — battle-reader info expansion (in progress)

New readers layered on the existing stack. **Stat-node layout note:** the affinity GRID is read via
the game functions `GetAffinity(stat,elem)` / `GetAffinityKnown(nameId,elem)` — NOT the raw nibble
offsets in `battle_structs_wip.md` (those proved unreliable for buffs). The raw offsets below are for
STAT BUFFS only, found via a clean live diff (cast in isolation, watch the stat node).

- **Whose-turn** (`TurnReader.cs`): `Battle.ActingUnit()` = `*(BtlInfo+0xCB8)` (Turn) `→ +0x38` = acting
  party member; announce FIRST name on change. **NULL on enemy turns**; a full BtlInfo+manager pointer
  scan found no acting-enemy field, and P4G has **no turn-order UI** (agility-weighted + random, web-
  researched) → no readable "next turn." Enemies are named at their action by DamageMonitor.
- **Target affinity** (`Battle.TargetWeaknessNote` → appended in `BattleLog.cs` enemy-target read):
  element from `Battle.SelectedSkillId` (published by SkillSelect, persistent) or Physical for Attack;
  `Battle.KnownAffinity(unit,elem)` = `GetAffinityKnown` gate → only DISCOVERED (hit-revealed)
  affinities, matching the game's mark (Analyze does NOT reveal). **`ClassifyAffinity` flag `0x02000000`
  = REPEL** (2026-07-02): Ghidra proved it's a DISTINCT no-damage flag (merge layer treats 0x01/0x02 as
  separate parallel flags; `FUN_1400d2840` zeroes damage for the whole `0x27000000` = 0x01|0x02|0x04|0x20
  null/drain/repel group), and the USER confirmed live that the suspect element bounced damage back →
  repel. Was previously (wrongly) "block" then "null". Fixed in both `ClassifyAffinity` and
  `ClassifyPersonaRaw`. Confirmed flags: `0x08`=weak, `0x10`=resist, `0x01`=null, `0x02`=repel; `0x04`/
  `0x20` (drain/repel) still unverified guesses.
- **Stat buffs** atk/def/agi (`Battle.BuffText(unit)` / `BuffTextFromStat(stat)`): per-stat **sign byte
  at stat+0x1C (atk) / +0x1D (def) / +0x1E (agi)** — 0 neutral, **high bit set = down**, clear = up;
  **turn counter at stat+0x25/+0x26/+0x27** (non-zero = active, ticks 3→2→1, gate on this). Wired into
  the enemy-target read, U-key `EnemyStatus`, and `PartyStatus.DescribeMember` (O / `;` / `'`).
  A **4th buff slot, index 3 = critical rate** (sign +0x1F, turn +0x28), was added 2026-07-02 (Chie's
  all-party crit buff); `BuffTextFromStat` loops `t < 4` with `_buffStat[3]="critical rate"`.
- **Mind Charge (Concentrate)** = **`stat+0x16` bit `0x10`** — VERIFIED live 2026-07-02 (an enemy that
  self-Concentrates: the bit toggled `00→10` on the charge and `10→00` on the discharge across two
  isolated captures; it is NOT in the 0x1C buff array). Announced as "mind charged" in `BuffTextFromStat`.
  Found by widening a throttled per-unit `stat[0x0C-0x8F]` diagnostic dump (now removed) and diffing
  charge vs discharge. NOTE: **normal Charge** (Power Charge, 2.5× next PHYS) not yet observed — likely
  an adjacent bit in the same `0x16` byte; add when a Charge user is available. Dead ends: the buff array
  0x1C-0x28 and the status u32 at 0x0C both stayed empty through a Mind Charge (status only carried
  down `0x100000` / dead `0x80000`).
- **Multi-target** (`MultiTargetReader.cs`): skill target scope = **`ActiveSkillData+0x0C`** (0 single /
  1 all — Bufula 0x00 vs Mabufu 0x01; `Skill.GetTargetScope`). Fires on a REAL skill-list confirm only
  (`_listWasOpen` guards against a stale `SelectedSkillId` re-firing on every ring hover of "Skill"):
  speaks "All enemies." + `Battle.AoeAffinitySummary(elem)` = discovered weak/resist/null/drain/repel
  COUNTS across living enemies.
- **Keys:** Shift+O = current acting character (`PartyStatus.AnnounceCurrent`, reads `ActingUnit` stat
  node); `;`/`'` = cycle one ally (battle-gated). **Pad: RT+L3 = current char (in battle) / cursor H
  (dungeon); RT+left/right shoulder = `;`/`'`** (`PartyStatus.Instance` static hooks).
- **Enervation** ailment = status `0x80` (`_ailmentNames`).
- **Anti-spam:** `Speech.Say` mutes an identical line re-spoken within 600ms (`_recentSaid`), killing
  the navigator↔reader ping-pong; `RepeatLast`/`Step`/`Record` bypass it.

## ✅ WORKING (built + confirmed by the user this session)

### 1. Persona panel (in-battle MC Persona menu) — `PersonaNav.cs`
Press **Persona** → submenu (PersonaSelect speaks "Name, level N" per row) → **F** opens the
full panel. In the panel: **I/K** move rows, **J/L** step items:
- Row 0 — name, arcana, level
- Row 1 — Elements (Phys/Fire/Ice/Elec/Wind/Light/Dark)
- Row 2 — Stats (Str/Mag/End/Agi/Luck)
- Row 3 — Skills; J/L speaks each skill **name + full description**

Data from the highlighted **stock PersonaInfo** entry (`Battle.PersonaCursor()` → prefers
`LastPersonaEntry`/`LastPersonaId` published by PersonaSelect, because the live stock cursor
`*(0x141165900)+0xA30` reverts to the EQUIPPED persona in the panel). Affinity table is
`*(0x140EC0988)`, row = persona id, **high-byte flag decode** `ClassifyPersonaRaw`:
`0x10=resist, 0x08=weak, 0x01=null`, hi-byte 0 = normal. `unit+0xA4` = party SLOT index, NOT
persona id (early bug). Arcana table `*(0x140EC0958)`.

### 2. Enemy analyze profile — `ProfileNav.cs`
During the **Analysis** command: name/arcana/level, Max HP/SP, Elements. I/K/J/L like the
persona panel. Auto-reads the header when you move to a new enemy.

### 3. Menu gating (the big fix — leak-free, hover-silent)
- **PersonaNav** active only when `CurrentCommand == 5` (Persona) AND `PersonaEntered` (the
  submenu actually rendered — set by PersonaSelect, cleared when leaving cmd 5). Auto-reads
  the panel only when the submenu is hidden (`LastPersonaTick` stale). → hovering the Persona
  choice in the ring is silent; no leaks into skill targeting/combat.
- **ProfileNav** active only when `CurrentCommand == 0` (Analyze); auto-reads only when the
  analyzed enemy CHANGES and KEEPS `_lastTarget` across the ring → re-hovering Analysis stays
  silent.
- **BattleLog** defers to ProfileNav only when `cmd==0 && AnalyzeSelectedEnemy()!=0`.
- Dead-end signals (don't reuse): `FUN_1400edf80` container mode (flickers 0/1/2 all battle),
  `mgr+0x208` overlay pointer (generic, flickers, fires at turn-start), `S+0x52F2` (=3 during
  all targeting). The reliable lever was **`Battle.CurrentCommand`** (enum: Analysis=0,
  Attack=3, Skill=4, Persona=5).

### 4. Victory / Result screen — `ResultReader.cs`
Announces **"Gained N EXP, M yen, <items>"**. Hooks the spoils-apply
**`FUN_140101880(expCtx, spoils)`** (found via CE "what writes the wallet" → write at
`0x140101994`):
- EXP = `*(int*)(expCtx+4)`
- Money = wallet **before/after delta** (`*(0x1451BCD70)`, exact incl. ×2 bonus)
- Items = count at `*(int*)(spoils+0x30)`, id/count u16 pairs from `spoils+0x24` (stride 4).
  CONFIRMED ids: **1297 = Big Incisor, 1318 = Idea Paper**; names via `Item.GetName(id)`.
- `_announced` re-arms when battle-UI state is 1..11 (active battle).

### 5. (Earlier, pre-session) combat feed, target narration, party/enemy HP-SP, damage,
item/skill sub-menus — already shipped; see `project_status.md` / `battle_audit_2026-06-04.md`.

---

## ✅ THE REWARD PROBLEM — SOLVED (2026-06-10)

The reward is now announced **while the victory panel is showing**. The old "no pointer
from mgr" finding was wrong by one level of indirection — found via the snapshot tooling
(snapshot during the panel + search for the known exp/money/item values):

**`mgr+0x14E0` → result UI node → `+0x48` → spoils-8.** So the live spoils struct =
`*(*(mgr+0x14E0)+0x48)+8`: money `+0x08`, item id/cnt u16 pairs `+0x24` (count `+0x30`),
EXP `+0x58` — same layout the apply hook receives.

**State-17 reality (revised after play-testing):** state 17 hosts the ENTIRE post-battle
flow — shuffle cards, reveal, AND the reward panel, in every battle (not just scripted;
a no-shuffle battle's reward panel is also 17). The reliable "panel is actually built"
signal is **`spoils+0x58` (EXP) becoming non-zero** — money/items are written early
(during cards; reading then spoke "Gained 0 EXP" over the shuffle), EXP only when the
panel populates. `TryEarlyAnnounce` therefore requires: state 17/18,
`ShuffleReader.ShuffleActive == false`, **exp ≥ 1**, sane ranges, two identical reads.
Zero-EXP battles fall back to the on-advance hook announce. The `FUN_140101880` apply
hook remains as fallback announcer + **exact-money corrector** (early money `spoils+0x08`
may exclude a ×2 bonus — the hook's wallet delta is exact and speaks "Correction, N yen"
if they differ; log line `[Result] money correction`).

**ShuffleReader timing (same session):** the logic struct's count field GROWS during the
deal (latches at 2, ends at 4) — the find announce is debounced until the count sits
still for 800ms, with in-place relatch (re-read card paths) on count change. After a pick
(header invalidates) `_holdUntilExit` stops rescans until the UI state leaves 17/18 —
the struct and lookalikes linger through reveal/reward. Suit filenames: the game spells
Swords as **`sord_cNN`**.

Level-up announcement: NOT needed — the game itself voices level-ups (user confirmed
2026-06-10).

**Scripted/tutorial Shuffle Time fixed the same day:** the game's first (scripted) shuffle
uses header `[1, count, 4, 0, ...]` — `+0x08` is **4** instead of 0 (and only 2 cards).
Signature relaxed to `h[2] <= 8`; all arena anchors (panel map, const blob, card paths at
`+0x1EC0/+0x138`) sit at the same struct-relative offsets in the tutorial variant.

---

## ✅ Persona panel session 2 wins (2026-06-10)

- **Ally persona id** = party record `stat+0x56` (mirror `+0x64`); Yosuke→192 "Jiraiya".
  `PersonaNav.AllyTick` reads the ally panel (I/K rows: info · Elements · Skills, J/L
  items with skill descriptions). Keyed-only — no ally panel-open signal exists
  (PersonaMenuMode stays 1, FPanelOpen false with the panel up).
- **Native Skill Info (F) cursor — found, then RETIRED (user call, 2026-06-10).**
  The snapshot hunt DID locate it: heap widget, sig
  `FF FF FF FF 00 00 00 00 02 00 [count] 00 00 00 [cursor]` + screen-coord floats,
  panel data block at sig-0x25C (skill ids u16 at +0x06+slot*0xC, decoded affinity
  u32 row before it). But the widget is RECREATED per panel open, a persistent
  count=2 lookalike kept stealing the latch, the early build starved ShuffleReader,
  and reliability never stabilized — the user chose to drop it rather than fight it.
  `SkillInfoReader.cs` deleted; hunt artifacts kept at C:\p4g_re\shots\sk*.bin if
  ever revisited. The SUPPORTED path is the panel's Skills row: I/K to Skills,
  J/L per skill — now speaks "Name: Element skill that costs N HP/SP. Description"
  (RichSkillDetail in PersonaNav, MC + ally).
- MessageBubble: content-keyed dedupe (pointer reuse ate lines) + player-echo
  suppression (command names always; just-picked skill/item one-shot via
  Battle.PendingEcho*).
- **Session 3 (same day): stats + EXP + learnset all SOLVED via Ghidra.**
  Ally persona = EMBEDDED PersonaInfo at party record +0x54 (id +0x02, level +0x04,
  exp +0x08, skills +0x0C, stat arrays +0x1C/+0x21/+0x26 — same layout as MC stock).
  Persona next-level exp: thunk 0x1400D8050 (FUN_15f139d20(entry, level), pure).
  Char exp curve int[100] @ 0x14090DBF0; char exp at record+0x40, level +0x06.
  Learnsets: party personas *(0x140EC0980)+(id-0xC0)*0x26E+4 (32×4B, absolute lvls);
  stock *(0x140EC0978)+id*0x46+6 (16×4B, lvls relative to species base
  *(0x140EC0958)+id*0xE+3). Entry [lvl u8, flag u8, skill u16], flag 0 = end.
  Displayed stats = SUM of the three stat arrays (the 99-cap sum in FUN_1400D7A90).
  Both panels now have 5 rows: info · Elements · Stats · Skills · Experience.
- **Status ailments (incremental map, started 2026-06-10):** status u32 at
  statNode+0x0C. Confirmed: 0x80000 dead/removed, 0x100000 down, **0x4 = Fear**
  (Evil Touch → banner "afraid" → [Status] 0x0→0x4; the feared enemy then FLED:
  0x4→0x80000 at full HP — DamageMonitor announces "fled" on dead-bit-with-HP),
  **0x1 = Dizzy** (2026-06-11: HARU knocked down, banner "dizzy", [Status]
  0x0→0x100001 = Down + the new bit).
  Unmapped bits speak a generic "status effect" (U/O keys + targeting lines);
  DamageMonitor logs every [Status] change next to the naming banner — add each
  new ailment to Battle._ailmentNames as it's first encountered in play.
  Party record +0x00 flag word: bit 0x1 = in-party (TEST THE BIT — `!= 1` broke
  the O key when 0x200 appeared during a guard), 0x200 = guard stance (probable,
  unconfirmed — log-only).
- **Tactics menu (2026-06-10):** the drawer at 0x1400E7020 (long mislabeled
  "persona skill info") is the TACTICS OPTIONS list drawer; the panel renderer
  is FUN_1400EC280 (mislabeled "MC persona detail" — PersonaPanelHook's mc-hook
  label is wrong, harmless). SkillInfoSelect speaks the options (canonical
  ORDER_SEL order: Act Freely · Full Assault · Conserve SP · Heal/Support ·
  Direct Commands · Don't change tactics) and, on entering, WHO they're for +
  their current tactic. Member current-tactic byte = party record+0x10
  (value 0..4 = same order); chosen member = BtlInfo+0xD10 when +0xD18 != 0.
  The MEMBER screen has NO readable cursor — EXHAUSTIVELY verified 2026-06-10:
  widget words static, BtlInfo D00-D30 static, full private-heap zigzag
  snapshot diff (6 snaps, byte + two-state dword) found only render-side row
  COLORS flipping (0x2D2D2DFF dim / 0xFFF000FF selected) in transient text
  objects, and a 497MB writable static+image snapshot diff found ZERO
  two-state values. The cursor exists only as render state. DO NOT hunt this
  again. The screen stays silent by design; row order is fixed ("All Members",
  then allies in party order) and the options-entry header ("Yosuke, now Act
  Freely") confirms every pick. Skill names gated to cmd==4 (SkillSelect).
- Still open: ambush attacker naming (needs live action-struct snapshot hunt);
  the [TacticsProbe] diagnostic in PersonaMenuHook can be removed once the
  tactics flow is confirmed good.

## 🔧 Attack attribution — what works and what's dead (2026-06-10, same-day session 2)

**Working now (DamageMonitor):** enemy attacks on the party speak as
"\<enemy\> attacks. \<ally\> took N damage" via a fallback chain: enemy whose
`unit+0x48` target pointer aims at the victim → only-living-enemy → recent
info-window enemy banner actor (`Battle.LastEnemyBannerActor`, set by BattleLog
on every enemy banner event even when deduped). Skill names for casts come from
the game's own top bubble (MessageBubble); DamageMonitor lines now speak with
`interrupt: false` so they QUEUE behind the bubble instead of cutting it off.
HP-cost skills (Bash/Cleave): SkillSelect publishes `Battle.PendingHpCost{Unit,Tick}`
per highlighted row; an exact-delta HP drop on the caster speaks "spent N HP"
and is never attributed (it used to read as an enemy attack).

**Dead ends verified live this session (do not retry):**
- `BtlInfo->Turn` (`+0xCB8`) is **NULL during enemy turns** — it only tracks the
  player command phase, and it's ALSO null by the time a player skill's HP cost
  lands (menu closed). Useless for attribution either way.
- Move resolver **`FUN_14004D0A0` hook never fires** in live encounters (hooked
  cleanly, zero hits across multiple battles. battle_damage_hunt.md Item 3 rated
  it MEDIUM; the real executor is in the protected region).
- **`unit+0xAE` (skill) / `unit+0x48` (target) never transition during combat**
  (40ms poll, zero transitions) — stale/setup-time copies. `+0x48` is still
  usable as a fuzzy attribution hint (see chain above).
- **`S+0x52F2 == 3` is NOT a targeting signal** (read false during all targeting;
  the old dead-end note was wrong in the other direction).
- Enemy plain attacks emit **no info-window banner and no bubble** — there is no
  text to intercept; only the HP delta is observable from clean memory.

**Open gap:** battle that OPENS with an enemy ambush turn — no targeting/banner
history exists yet, so with 2+ enemies the attacker can't be named (single enemy
is covered by only-living-enemy). Full fix = snapshot-method hunt for the LIVE
action struct the protected executor uses (actor + skill id), like the Shuffle
Time arena hunt. Candidate next session.

---

## ✅ Shuffle Time card narration — CLOSED 2026-07-01 (both issues solved, user-verified)

Everything in this section below is the original 2026-06-10 build; the two hard problems were solved
2026-07-01. All logic is in `Components/Battle/ShuffleReader.cs`.

**Model (why change spreads were hard):** the game keeps a **panel map** (`logic+0xE7C`, u16[] of a
logical card-id per on-screen slot in display order, `0xFFFF`-term, compacts as drawn) + a **texture
array** (`logic+0x1EC0 + slot*0x138`). On a **change** card it gives the new card a **brand-new id ≥
dealt**, drops the replaced card's id out of the map, and may overwrite a *bystander* slot's texture.
So `id-dealt` arithmetic and the raw texture array are BOTH unreliable after a change (every formula
gave wrong names; a One-More even renumbers survivors).

**Issue 1 — change-card names (SOLVED for normal + single-change).** `ResolveSlots()` returns
(name,effect) per position by two always-true facts: (1) **an original still in the map keeps its
identity** — a replaced card's id LEAVES the map, so any `id < dealt` present is still the remembered
card `_orig[id]` even if its slot texture got clobbered; (2) **the one created card (id ≥ dealt) = the
one changed slot** (texture ≠ `_orig`). Else → "card changed" (never a wrong name). `_orig[]` is
captured on any no-created-card frame and **must survive a mid-shuffle struct re-find** (do NOT clear it
on drop). A ~300ms grace lets a still-loading change-card resolve before saying "card changed". OPEN
(safe-degrade): double-change (2 created) and Fool (all-change) — full naming needs the render node
(`*(logic+0x20)` = head of a draw-task list; the shuffle node is **`battle_shuffle`**, its card sprites
are children — a tree-walk, deferred).

**Issue 2 — "you can take X cards" (SOLVED).** Tries = **`head[5]` = `*(int*)(logic+0x14)`** — a HEADER
field (the old "not in struct" only scanned 0x20..0x1EC0). Went 3→2→1 as cards were taken while
remaining (`+0xE88`) went 5→4→3; rises on a "+draw" pick. Found via snapshot-diff (`cursor_hunt.py snap`
at 3/2/1 + `find_seq.py 3,2,1` + a scratch struct-locator) — no CE table needed.

**Detection hardened + made light 2026-07-01:** signature caps relaxed (dealt ≤ 16, tries h5 ≤ 0x3F, h6
≤ 0xFF — the "card/" path read is the real gate) so big/high-rank spreads read; and a **scan backoff**
(fast 200ms → 6s) because battle-UI state stays 17 on the reward screen with no struct (was scanning
~600ms nonstop). Fast start also catches brief tutorial shuffles.

**Full offset map + every dead end:** `memory/shuffle_time_structs.md`,
`memory/next_session_shuffle_time.md` (read before any shuffle work).

---

### Original build notes (2026-06-10, kept for history)

**The shuffle struct is FOUND** (external RPM snapshot diff, `database/frida/cursor_hunt.py`
— CE-style 0→1→2→3 hunt over all private memory; scripts in `database/frida/`):

- The shuffle state lives in a **heap arena** (64KB-page allocator, low 4GB; that battle it
  was `0x2E720000`). NOT reachable from `mgr`/`BtlInfo` — no stable pointer found; the
  mod must **signature-scan** private memory during the shuffle.
- **Battle UI state for the card screen is 17, NOT 18** (verified live: `mgr+0x458` reads
  17 while the cards are up; 18 is the Result/spoils screen after — the cards are freed by
  then). The old "post-battle = 18" note below lumped both. ShuffleReader scans on 17.
- **Logic struct** (that run `0x2E722B68`): `+0x00`=1, `+0x04`=**card count** (4),
  `+0x10`=**cursor** (u32 0..N-1, confirmed by live 3→2 on ← press), `+0x14`=1,
  `+0x18`=0x14 (state?), `+0x20`=ptr to "battle_shuffle" render node, `+0x28`=0x3C.
  Scan signature: u32 pattern `[1, N(2..8), 0, 0, cur<N, 1, 0x14, 0]`.
- **Panel struct** (`+0x3AA4` in the same arena): u16 cursor; preceded at `-0xC0` by the
  distinctive u16 slot map `00 00 01 00 02 00 03 00 FF FF FF FF 04 00 00 00`; u16 count
  at cursor-4. A second u32 cursor copy also lives in a UI widget obj (screen-coord floats).
- ~~Card-type array~~ **DEBUNKED (2026-06-10 play test):** the u32 `[1,9,5,0]`+count+`-1`
  blob near the struct is **identical in every battle** — it's a constant config table
  (likely category definitions), NOT the spread. It still works as arena *evidence* for
  validating the struct (ShuffleReader uses it + the panel slot-map sig to reject a
  permanent lookalike object at `0xACFFB10`/`0xAD5FB10` that matches the 32-byte header).
  The REAL per-card records are still undecoded. Ground truth available for offline work:
  snapshots `C:\p4g_re\shots\s0..s3.bin` hold a spread whose slot 2 was "Temperance
  Arcana → Chest Key" — candidate regions: u16 `13` at arena+0x2E5E area, the small-int
  record block at arena+0x2BB8..+0x2E80, and the (item-id,count) tables at +0x3054/+0x3374.
- **Result messages are ALREADY read** — the post-pick text ("The card of the X Arcana...
  Obtained Y!") goes through the normal BMD dialog path and the Dialogue hook speaks it.
  Only pre-pick hover narration is missing.
- EXE debug strings: `shufflecard.c`/`shufflepanel.c`/`shuffleseq.c` + `ShuffleMsg.bmd`/
  `ShuffleHelp.bmd`/`ShuffleCardHelp.bmd` exist, but Arxan hides all static AND runtime
  string xrefs — code-side RE is a dead end; memory-side (above) is the way.
- Snapshots + analysis scripts: `C:\p4g_re\shots\s0..s3.bin` (4 full heap snaps, cursor
  0-3 on a known spread), `database/frida/{cursor_hunt,analyze_cards,snap_window,peek,
  find_ptrs_to,scan_shuffle_*}.py`.

**SIGNATURE FIX (2026-06-11):** the header u32 at +0x18 is NOT a constant 0x14 —
it changed when the user's Shuffle RANK went up, making every scan miss (15
full-memory attempts, zero candidates; tactics work was wrongly suspected).
Header fields +0x08 and +0x18 are now small-value sanity checks only
(<=0x20 / <=0x40); the card-path read remains the real gate. The scan also
falls back to the full address space after the low-4GB pass (the arena can
land above 4GB late in long sessions), and logs every 5th failed attempt.
Tactics member screen: virtual key-tracking cursor was built, then REMOVED at
user request same day — the supported flow is options-only + entry header.

**SHIPPED (2026-06-10):** `ShuffleReader` — state-**17** gated signature scan announces
"Shuffle Time, N cards" + "Card k of N" on cursor move; logs spread + picked slot.
Confirmed working over a multi-battle session (finds in ~350-500ms, lookalike rejected).

**CARD IDENTITIES DECODED (2026-06-10, two-battle arena diff + live pick test):**
Each slot's card stores its TEXTURE PATH at **`struct + 0x1EC0 + slot*0x138`**
(null-terminated ASCII, offsets verified identical across two battles):
- `card/arcana/c_cardXX.tmx` — major arcana, XX = hex arcana id **0-based**
  (0x0E = Temperance, confirmed by result text; P4 order Fool..Judgement).
- `card/sarcana/wand_cNN.tmx` — suit card, NN = hex rank ("Rank 1, Wand card" =
  `wand_c01`, confirmed live; Wands = EXP-up). `coin_cNN`/`cup_cNN` seen in logs too.
- `card/persona/i_prcXXX.tmx` — persona card, XXX = hex persona id (confirmed:
  `i_prc02e` → result text "Persona Pixie", so 0x2E = Pixie). Spoken as
  "Persona <name>" via `Native.Persona.GetName(id)`.
ShuffleReader reads these on find and speaks "Card k of N, <name>" on hover; unknown
path shapes are spoken as the file stem and logged with the full path for later decode
(persona-attached cards / penalty cards / blanks may have other shapes — collect from logs).

**Remaining polish:**
1. After the pick resolves ShuffleReader keeps rescanning every 700ms while state
   stays 17 (post-pick animation) — add a "done until state leaves 17/18" latch.
2. The deal animation runs ~6s of failed scans before validation (arena tables are
   written late) — fine in practice; the announce lands as the cards settle.
3. Suit→bonus wording (Wands=EXP up, etc.) once each suit is confirmed in play.

## (previous) Shuffle Time notes

Shuffle Time = **4 face-up cards** (random post-battle). Screenshots `database/S1.jpeg`,
`S2.jpeg`. Each card has a label (e.g. **Money UP · Avoid Encounters · Get Skill Card ·
Persona Lu UP**) and a top-right panel showing the highlighted card's name + description +
category (COIN / ARCANA / PERSONA). Controls: `←→ Select Card`, `Space OK`, `C Cancel`,
`F View Status`. **Nothing is read** currently — no existing hook catches the labels or panel.

Need: the **cursor index** (0..3) + the **card-type array**, then map types → names (hardcode
from screenshots) and narrate on cursor move. Runs in **state 18** (post-battle), same as the
Result screen — Shuffle Time comes BEFORE the Result screen.

**Probe v1 result (2026-06-10): mgr/BtlInfo RULED OUT.** Three G-dumps at cursor 0/1/2 were
captured; diff (`database/tools/diff_shufdump.py`) showed only ONE change across all
0x6000+0x4000 bytes — a float pair at `mgr+0x460` (timer). The Shuffle Time object is heap,
like the spoils struct.

**Leads found the same day:**
- The state-18 ENTER handler `FUN_1400b7710` (state vtable `0x140a303f0`, entry 18 = h0
  `0x1400b7710`, h1 `0x1400b7730`) walks **four task linked lists at
  `mgr+0x210/0x220/0x230/0x240`** (next ptr `+0xD8`, flag byte `+0x87`). The Shuffle Time
  overlay is most likely a node on one of those lists.
- EXE debug strings (found via `database/tools/find_shuffle_strings.py`): `shuffle.c`,
  `shufflecard.c`, `shufflepanel.c`, `shuffleseq.c`, `shuffleseqshuffle1..5.c`,
  `shuffle_arcana_card`, and BMD resources **`ShuffleMsg.bmd` / `ShuffleHelp.bmd` /
  `ShuffleCardHelp.bmd`** — the help-panel text is BMD-driven, so once we know which card
  is highlighted we can pull name/description from the BMD instead of hardcoding.
  No static xrefs to these strings (Arxan-protected binary) — runtime hunting only.

**Current step — probe v2 (deployed, needs a game run):** `ShuffleDump` now walks the four
task lists and dumps every node (0x300 bytes, tag `L<list>N<node>`) plus `*(mgr+0x208)` as
`OVL`. Same drill: during Shuffle Time press **G** at cursor positions 0, 1, 2, then run
`python database/tools/diff_shufdump.py` (clear/rotate `database/log.txt` first if it still
holds the v1 dumps). The cursor is the field reading 0→1→2; card-type array sits nearby.
(Remove the G-probe + `DumpU16`/`ShuffleDump` from ResultReader once found.)

Fallback if v2 misses: CE value-scan for the cursor (move `←→`, narrow 0→1→2→3), or CE
string-scan for a card label ("Avoid Encounters") to find the panel text buffer.

---

## 📋 TODO / NOT DONE (smaller, deferred — see also `PersonaNav.cs` header)
- **Native "Skill Info" (F) cursor** inside the persona panel — the game's own skill-slot
  cursor isn't read (J/L Row 3 already covers skills). Cursor is heap at object+0x2B8 (CE found
  `0x041CADD8` one session); stable pointer chain unfound (Frida deep-scan crashed the game).
- **Ally persona profile** — allies have no stock submenu, so their persona id needs a
  different source than `PersonaCursor`.
- **Persona panel extras** — NEXT EXP (PersonaInfo+0x08) and the "NEXT LV n: skill"
  learn-on-levelup entry (learnset source not yet RE'd).
- **Result repel/drain/block** affinity flag values for personas are best-guesses (0x02/0x04/
  0x20) — confirm when one shows on screen.

---

## Key addresses / RE crib
- Battle manager: `mgr = *(0x140EC08F0)`. UI state machine: `*(mgr+0x458)` (post-battle = 18).
  State-handler vtable `0x140a303f0`, stride 0x18, two ptrs/entry (dumped via
  `C:\p4g_re\scripts\DumpVtable.py`).
- `Battle.CurrentCommand` = `commands->SelectedCommand` (enum Analysis=0/Attack=3/Skill=4/Persona=5).
- Wallet `0x1451BCD70`. AddMoney `FUN_14019eac0(amount)`. Spoils-apply `FUN_140101880`.
- Persona stock base `*(0x141165900)`, entries `+0xA34` stride 0x30; cursor `+0xA30`.
- Affinity `*(0x140EC0988)`, arcana `*(0x140EC0958)`. Player facing via gaze cursor (dungeon).
- Ghidra: project `C:\p4g_re\project` (P4G), decompile via `analyzeHeadless ... -postScript
  DecompileFunction.py` with target VA in `C:\p4g_re\scripts\decompile_target.txt`. Jython
  scripts need `# @runtime Jython`. `FindXrefs.py` lists callers.

## Files (Components/Battle/)
`Battle.cs` (owner + persona/analyze helpers), `PersonaNav.cs`, `ProfileNav.cs`,
`PersonaSelect.cs`, `PersonaPanelHook.cs`, `PersonaMenuHook.cs`, `SkillInfoSelect.cs`,
`SkillSelect.cs`, `ItemSelect.cs`, `BattleLog.cs`, `MessageBubble.cs`, `DamageMonitor.cs`,
`PartyStatus.cs`, `EnemyStatus.cs`, `ResultReader.cs` (Victory + Shuffle probe).
