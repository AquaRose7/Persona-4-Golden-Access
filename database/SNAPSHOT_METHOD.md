# The Snapshot Method — finding ANY game value with the player in the loop

Proven 2026-06-10: cracked Shuffle Time (cursor, card count, card identities) and the
victory-panel reward struct in one evening, after weeks of pointer-chain hunting and
Ghidra static RE had failed (Arxan hides xrefs; the data lives in transient heap arenas
with no stable pointers). **This is the default tool now for "where does X live?"**

## The idea

The player is the experiment driver; external read-only snapshots are the instrument.
No game restarts, no rebuilds, no injection, no crash risk (plain `OpenProcess(VM_READ)`
+ `VirtualQueryEx` + `ReadProcessMemory` — far safer than Frida, which has crashed P4G).

The loop: **"you click, I scan."**
1. Player navigates to the screen of interest and HOLDS STILL → Claude snapshots all
   committed private memory (~0.5 GB, ~10s).
2. Player performs ONE discrete input (move cursor right, advance one step) → snapshot.
3. Repeat 3-4 times → diff the snapshots for the field matching the expected sequence
   (0,1,2,3 = a cursor; known EXP value = reward field; etc).
4. Player reports what the game SAYS (result text is ground truth — e.g. "slot 2 was
   Temperance → Chest Key" anchored the whole card-identity decode).

## The toolkit (`database/frida/`)

| Script | Role |
|---|---|
| `cursor_hunt.py snap <file>` | Snapshot all committed PRIVATE memory to disk |
| `cursor_hunt.py find <s0> <s1> ...` | Diff snapshots: u8 fields reading 0,1,2,... across them |
| `peek.py <addr> [span...]` | Live hex/u16 dump around addresses |
| `snap_window.py <va> <span> [u16\|ascii] <snap>` | Print a window from a saved snapshot |
| `find_ptrs_to.py <lo> <hi>` | LIVE: find qword pointers into a range (flags EXE-static hits) |
| `snap_find_ptrs.py <snap> <lo> <hi>` | Same, offline inside a snapshot |
| `arena_compare.py <snapA> <snapB>` | Cross-battle diff at struct-relative offsets — separates per-event data from constant config |
| `find_reward.py <snap> <exp> <money> <id> <cnt>` | Find a struct by its KNOWN displayed values |
| `scan_text.py <word>...` | ASCII keyword scan of live private memory |
| `diagnose_shuffle.py`, `check_state.py` | Worked examples: relaxed-signature diagnosis, battle-UI state read |

All need the game running (numpy for speed). Snapshots land in `C:\p4g_re\shots\`.

## Key lessons baked in

- **One diff is not enough.** A value that fits the pattern in one battle can be constant
  config (the `[1,9,5,0]` decoy). Confirm across TWO events (`arena_compare.py`) —
  per-event data is the tiny diff; everything else is config.
- **Struct-relative offsets are stable; absolute/arena offsets are not.** Anchor
  everything to a signature-findable struct, then offsets hold across battles/sessions.
- **Strings beat numbers.** The card identities were texture PATHS (`c_card0e.tmx`) —
  always scan changed regions as ASCII before squinting at integers.
- **Result/dialog text is free ground truth** — the game announces what happened; pair
  it with the player's input position to label unknown values.
- **In-mod implementation:** heap structs with no stable pointer → signature scan
  gated on the right UI state (see `ShuffleReader.cs`); reads via ReadProcessMemory-on-
  self so freed regions fail gracefully (AVE is uncatchable in .NET 9).
- 64KB page-aligned reads are a trap — allocations don't always start at page
  boundaries. Validate via small reads at struct-relative offsets instead.

## When to reach for it

Any "the screen shows X but we don't know where X lives" problem: menu cursors, panel
contents, transient overlays, anything Ghidra can't reach because Arxan hides the xrefs.
Cost: a few minutes of player time per hunt. It has replaced Cheat Engine for this
project — same power, scriptable, and the blind player can drive it solo.
