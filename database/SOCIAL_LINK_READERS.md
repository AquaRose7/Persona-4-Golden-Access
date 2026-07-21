# Social Link reader stack (rank-up banner + establish/max poem) — 2026-07-07

Two overlays, both baked art / special render (no dialogue-text path), both
anchored via the NAMED-TASK REGISTRY (see TV_LISTINGS.md for the registry).

## The commu data table — THE reliable arcana source
`*(0x1411A6B10)` = base of the commu table, **stride 100 (0x64) per commu
index**. Row **+0x10 = arcana, 1-INDEXED** (Fool=1 … so subtract 1 for the
standard tarot number: Devil card "XV"=15 ⇒ table byte 16; Sun=19 ⇒ byte 20 —
both log-verified 2026-07-07). Read: `arcana = *(*(0x1411A6B10) + idx*100 +
0x10) - 1`. The full 100-byte row is logged by SocialLinkRankUp (`row=…`) to
harvest the character-name field later (not yet decoded).

## Rank-up banner — `SocialLinkRankUp.cs`  (task `cmmRankUp`)
Mid-rank "Rank up!!" banner. work u16 **+0x04 = commu index**, **+0x08 = rank**.
Announce: "Rank up! <Arcana> Arcana. <Name if known>. Rank N." Arcana from the
commu table (reliable). NAME is best-effort only (verified id table in
`database/social_links.json`, or a named `SCR_COMMU_<suffix>` task; skip the
generic `SCR_COMMU_NPC_*`). ⚠ NEVER guess a name — a wrong name is worse than
none (the bustup-portrait id guess shipped wrong: id 8≠Naoto etc.).

## Establish / max poem — `SocialLinkBond.cs`  (task `cmmRankUpSequence`)
The "Thou art I… established a new/genuine bond… Personas of the X Arcana…"
overlay (BMD msg MSG_CMM_RANKUP_OPEN / _MAX). Driver fn FUN_1401C14B0, created
by FUN_1401C26D0(obj, commuIdx→work+0x1C, rank→work+0x20, …). Detection: task
`cmmRankUpSequence` present AND `cmmRankUp` ABSENT (the banner up = ordinary
mid-rank, not the poem). Arcana from the commu table via work+0x1C. Poem text
is FIXED (hardcoded open/max variants) — only the arcana varies. Only shows at
first establishment and rank 10. (work+0x20/+0x24 held 0/1 at establishment in
the log, so rank-1 vs rank-10 is currently distinguished by the field==10
check; refine when a rank-10 sample is logged.)

## Dead ends
- Portrait-derived commu-id → name table (commu = bustup+1): WRONG, shipped
  bad names. Removed.
- Capturing the poem via FUN_140450C60 (SocialLinkBond v1): the overlay does
  NOT render through the shared UI-text fn — captured nothing. Task-struct
  read replaced it.

## Commu table decoded (live dump 2026-07-07)
`*(0x1411A6B10)` rows are indexed by the COMMU INDEX = the banner's work+0x04
AND the poem's work+0x1C (same space; idx7=Yosuke/Magician, idx2=Nanako/
Justice, idx31=Adachi/Jester, idx10=Kanji/Emperor — all log-verified).
**2026-07-10 — ROW PAIRS: each romanceable link occupies TWO adjacent rows
(normal/lover).** Log-proven for Priestess: a rank-9 banner reported idx 5,
the rank-10 banner and the Album entry reported idx 6, both arcanaByte=3 with
near-identical row data — mapping only one of the pair leaves the other
nameless. social_links.json now maps BOTH rows of every pair (Lovers 3/4
Rise, Priestess 5/6 Yukiko, Chariot 11/12 Chie, Fortune 14/15 Naoto, Moon
21/22 Ai, Aeon 33/34 Marie) — safe by ARCANA-DERIVED identity (each of these
arcana has exactly one character in P4G; not a name guess). Only Sun (25-28,
Yumi/Ayane) and Strength (16/17, Kou/Daisuke) are genuinely two-character →
left to the SCR_COMMU_ task. ⚠ Fix social_links.json in BOTH database/ AND
the deployed mod folder — DataPath reads the mod copy first. Row fields: **+0x08 = arcana
(0-based)**, **+0x10 = arcana byte (1-based, P4G gaps: Jester=25, Aeon=27)** —
the reader speaks via `SocialLinkRankUp.ArcanaByByte[+0x10]`. Character NAME is
NOT a string in the row (names live in an adjacent blob at idx35+); instead
`database/social_links.json` maps commu index → name for SINGLE-character
arcana (Devil=9 Sayoko, Hermit=13 Fox, Hanged Man=18 Naoki, Death=19 Hisano,
Empress=20 Margaret, Tower=23 Shu, + the party-verified ones). Multi-row
arcana (Sun idx25-28, Strength idx16/17, Chariot 11/12, Fortune 14/15, Moon
21/22, Lovers 3/4, Aeon 33/34) are left to the SCR_COMMU_ task to avoid
ambiguity.

## Poem establishment vs mid-rank (false-fire fix)
cmmRankUpSequence runs for BOTH the establishment poem AND the mid-rank
"Rank up!!" banner (there is a timing window where the sequence task is up
before cmmRankUp). Discriminator: **work+0x20 == 0 at first establishment**,
== the new rank (>=2) mid-rank (log-proven: a rank-3 had +0x20 == 3, both real
establishments had 0). SocialLinkBond fires ONLY on +0x20 == 0. Rank-10 MAX
poem still TODO (no live sample of its +0x20 value yet).
