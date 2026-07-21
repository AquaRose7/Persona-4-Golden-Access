# The In-Game Accessibility Settings Menu — source of truth

Built + user-verified 2026-07-19. Design spec: `docs/superpowers/specs/2026-07-19-settings-menu-design.md`,
plan: `docs/superpowers/plans/2026-07-19-settings-menu.md`.

## What it is

A fully mod-drawn spoken menu holding EVERY mod setting: a volume for every mod sound,
the two shadow-radar pitches, the H-cursor start modes, the three reader toggles, a
HELP section (all controls + game tips), and Restore Defaults. Everything persists
instantly to `mod_settings.json`; volume/pitch rows preview their sound on change.

## Keys

- **F1** (keyboard) / **LT+RT+Start** (pad) — toggle, anywhere the game has focus.
- **Up/Down** rows (wrap) · **Left/Right** adjust · **Enter** enter/confirm · **Escape** back/close.
- Pad inside the menu: d-pad = arrows, A = Enter, B = Escape (synthesized keys).
- Auto-closes silently when a battle starts (`FieldTracker.InBattle`).

## Components (all in `p4g64.accessibility`)

| File | Role |
|---|---|
| `Components/SettingsMenu.cs` | The menu: categories/rows, help tree, speech, previews, `IsOpen`/`CaptureKeys`/`OpenRequest` statics |
| `SoundSettings.cs` | `Defaults` (shipped defaults, ONE source) + live float multipliers the audio components read per tick |
| `ModSettings.cs` | persistence (`GetInt/SetInt` added; instant save; one-time spoken save-failure warning) |
| `Components/Navigation/ToneCue.cs` | one-shot tone/WAV player on the shared mixer (pool of 4, 3 s SetWant linger) |

## Menu tree

Top: **Sound volumes · Cursor · Readers · Help · Restore defaults**.

- **Sound volumes** (12 rows): wall hum, door cue, stairs beacon, chest beacon, navigation
  beacon (one knob for dungeon `NavBeacon` + `OverworldNav`), shadow radar, choice sound,
  cursor beeps, arrival chime, wall bump — percent 0–200 step 10 — plus the two pitch rows
  **Shadow pitch facing you / facing away** (hertz, 60–600 step 10).
- **Cursor**: default mode Walk/Look (applied every H activation), default directions
  Compass/Camera (applied at session start; Shift+N stays session-sticky, doesn't write).
- **Readers**: dialogue / subtitles / descriptions — same keys the shortcuts write, both stay
  in sync (menu sets the static bool + `ModSettings.SetBool`, no Toggle() → no double speech).
  Subtitle row's description carries the note that the GAME's own subtitles must be on too.
- **Help**: content from **`help_content.json`** (ships flat in the mod folder — editable, no
  rebuild; `DataPath` resolves mod folder first, dev fallback `database/`). Tree: Controls →
  Keyboard / Controller (one row per shortcut), Game tips (name-entry guide first, then
  moving, shadows, wall sound, beacons, cursor, info panels, battles, town). Navigated via a
  stack; Enter re-reads a tip; rows are read-only.
- **Restore defaults**: double-Enter (5 s window) → every row back to `Defaults`.

## Shipped defaults (`Defaults` class — baked 2026-07-19 from Haru's own tuning)

Wall hum **50**, everything else 100; pitches 140/300; cursor **Look + Camera**;
dialogue ON, subtitles **OFF** (game default is off too), descriptions ON. The Mod.cs
startup announcements compare against `Defaults` (not literal true) so defaults launch silent.

## Volume plumbing model

Effective gain = hardcoded tuned constant × `SoundSettings.X` (1.0 = default). Call sites:
`WallHum` (per-direction maxes + door), `ProximityBeacon.VolumeScale` virtual (Exit/Chest
override), `NavBeacon`, `OverworldNav` beacon, `EnemyRadar` (MasterGain + strike/danger cues;
frequencies come from `SoundSettings.ShadowFreqYou/Away` — the 140/300 ear-proven warning
comment stays as the DEFAULT rationale).

**Win32 sound migration (2026-07-19):** every kernel32 `Beep` and the choice.wav `PlaySound`
now render through `ToneCue` on the shared mixer so they have volume knobs: DungeonCursor +
DungeonNav + OverworldNav `WinBeep` bodies, OverworldNav `ArrivalChime()`, WallBump `Thud()`,
Dialogue `PlayChoiceCue` (winmm fallback kept if the WAV fails to load). Base gains: beeps
0.45, chime/thud 0.5, choice 0.9.

## Input capture (the hard-won parts)

- While open, other mod hotkeys yield: whole-tick `SettingsMenu.IsOpen` early-returns in
  DungeonNav/OverworldNav/DungeonCursor/HistoryKeys; key-read-only guards in
  WallHum/EnemyRadar/NavBeacon/ProximityBeacon/FieldTracker-M (their AUDIO keeps running →
  live volume changes are audible).
- Game is kept blind via the two EXISTING ControllerInput hooks: `MaskKeyboard` zeroes the
  `MenuScan` DIK codes (arrows, Enter, numpad Enter, Escape, F1), `OnInputFn` clears the pad
  bitfields — both gated on **`SettingsMenu.CaptureKeys`**, which stays true until every menu
  key AND menu pad button (`ControllerInput.MenuPadHeld`) is RELEASED after close. ⚠ Gating
  on `IsOpen` alone leaked the TAIL of the closing Escape/B press to the game and closed the
  game's pause menu underneath (user-reported).
- ⚠ **Synthesized arrows MUST carry `KEYEVENTF_EXTENDEDKEY`** (`IsExtendedVk` in
  ControllerInput). Without it Windows delivers VK_UP with the numpad-8 scan code and NVDA's
  desktop layout SWALLOWS it as a numpad review command ("top"/"bottom" speech, key never
  reaches GetAsyncKeyState) — the d-pad-dead-in-menu bug.
- Open() seeds the edge trackers with current key states, so a key already held (or stuck
  down by a stray injected event — seen once in the wild closing the menu instantly) can't
  fire on the first tick.

## Key removals (2026-07-19, user request)

- **B** (spoken wall readout) UNBOUND — `FieldTracker.RequestCardinalProbe` remains unused.
- **Y** (cursor re-orient) UNBOUND — `SpeakOrient` deleted; open/move announcements cover it.
- ⚠ The v1.4.2 README still documents both + lacks the settings menu — fix at next packaging.

## Editing help content

Edit `database/help_content.json`, copy to the live mod folder, restart the game (it's read
once at startup). Shape: `{ "help": [ { name, desc, children: [...] | items: [{name,text}] } ] }`.
Keep names short (they're the spoken row labels); text = full sentences for NVDA.

## Release packaging additions

- **Bundle `help_content.json`** with the data files (NEW since v1.4.2).
- `mod_settings.json` stays EXCLUDED from zips (user-local).
- README: remove B readout + Y, add the settings menu (F1 / LT+RT+Start) + "full controls and
  tips are in-game under Help".
