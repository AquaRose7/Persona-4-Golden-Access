# Persona 4 Golden Access

A screen-reader accessibility mod for the Steam version of **Persona 4 Golden**, built as a
[Reloaded II](https://reloaded-project.github.io/Reloaded-II/) mod. It speaks the game through the
user's screen reader (NVDA / SAPI via Tolk) and adds blind-playable navigation, so the game can be
played without sight.

**Latest release: v1.2.0.**

## What it does

- Reads menus, dialogue, and on-screen prompts aloud.
- Full in-battle screen reader (commands, skills, damage, status, personas, results, Shuffle Time).
- Shops, the Velvet Room (fusion / compendium / skill cards), and the system pop-ups.
- **Anime cutscenes** — reads the on-screen subtitles, plus optional hand-authored descriptions of
  the visuals, synced to the movie.
- **Dungeon navigation** — a compass-based browser + auto-walk (to stairs, chests, doors, shadows),
  a mapping cursor with walk/look modes, and positional audio cues.
- **Overworld navigation** — an interactable browser + auto-walk that arrives on the game's real
  interaction prompt, with a self-learning spot system.
- Keyboard **and** controller support (the controller layer sits behind the triggers).

## Repository layout

| Path | What it is |
|---|---|
| `p4g64.accessibility-master/` | The mod source — a .NET (`net9.0-windows`) Reloaded II mod. The active project (entry point `Mod.cs`; features under `Components/`; native data accessors under `Native/`). |
| `database/` | The per-system **source-of-truth docs** (`*.md`), the derived data files the mod loads at runtime (`*.json`, `P4.tsv`, sounds), and the Python data-generators under `tools/`. |

**Where to start:** this README for the overview, then the source-of-truth docs in `database/` —
`BATTLE_SYSTEM.md`, `OVERWORLD.md` + `OVERWORLD_AUTOWALK.md`, `DUNGEON_AUTOWALK.md`, `SHOP_SYSTEM.md`,
`VELVET_ROOM.md`, and `SNAPSHOT_METHOD.md` (the live-memory investigation technique). Each explains how
that part of the game was reverse-engineered and how the corresponding mod code works — read the relevant
one before touching a system.

## Building

It's a standard SDK-style .NET project. From the repo root:

```
dotnet build p4g64.accessibility-master/p4g64.accessibility/p4g64.accessibility.csproj -c Debug
```

The output DLL is `p4g64.accessibility.dll`. To run it, copy that DLL into the `p4g64.accessibility`
folder inside your Reloaded II `Mods/` directory and restart the game — Reloaded II injects it at launch.
There are no automated tests; verify a change by launching the game and exercising the affected feature
(the mod logs to `%APPDATA%\Reloaded-Mod-Loader-II\Logs`).

## Runtime dependencies

The mod loads under Reloaded II and requires these other Reloaded II mods (declared in `ModConfig.json`):
**File Emulation Framework (BF Emulator)** and **`p4g64.customSubMenu`** are load-bearing for navigation;
without them the FlowScript-based features silently no-op. NVDA (or a SAPI voice) provides speech.

## Platform notes

- P4G PC 64-bit, **ASLR disabled** — all static/BSS addresses are constant across runs and hardcodable.
- There are no automated tests; verification is manual (launch the game and exercise the affected feature).
- `AccessViolationException` cannot be caught in .NET, so every unsafe memory read is guarded.

## Credits

- Mod by **Haru**, built on AnimatedSwine37's accessibility mod template.
- Speech via **Tolk** (NVDA / SAPI); audio cues via **NAudio**.
- Runs on **Reloaded II** and the **File Emulation Framework** by Sewer56 and contributors, and
  **Custom Sub Menu** by AnimatedSwine37, Tekka, and ShrineFox.
