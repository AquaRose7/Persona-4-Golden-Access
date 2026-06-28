using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Blind dungeon navigation cursor with a FIXED COMPASS frame. Press <c>H</c> to
/// toggle it on at your position; <b>I/K/J/L = North/South/West/East</b> on the map
/// (I=north/up, K=south, J=west, L=east) — always the same directions, never tied to
/// which way you face or where the camera points. <b>N</b> switches sub-mode; <b>Y</b>
/// re-announces where you are.
///
/// - <b>Walk mode (default)</b>: each I/K/J/L press <b>physically steps the player
///   ~half a tile</b> in that compass direction (via <see cref="AutoWalk.AutoWalker.Step"/>)
///   and announces the tile's content + which compass directions are open (+ "new
///   room"). The player ends up facing the way they stepped, so no separate "turn"
///   is needed; the step also slides around small wall protrusions.
/// - <b>Look mode</b>: I/K/J/L move a VIRTUAL survey cursor without walking — scout
///   ahead (e.g. check for a shadow) before committing. Backspace then mark-walks to it.
///
/// Each move speaks what's there — <b>floor · door · chest · shadow · unexplored</b>
/// — and refuses a <b>wall</b> / map edge instantly (it never drives into a known wall).
///
/// ### Data sources (both accurate)
/// - <b>Walls / floor</b>: the in-game minimap grid (<see cref="MinimapTracker"/>).
///   Each explored cell is tagged flag 1 = walkable (rooms, corridors, and the
///   1×1 cells that ARE doors), flag 2 = wall/boundary, flag 0 = unexplored.
///   This covers the whole explored floor — no collision-mesh coverage bubble —
///   and crucially doors are walkable cells, so a closed door doesn't read as a
///   wall. The cursor marches the grid from its position toward the target and
///   stops at the first boundary cell.
/// - <b>Doors / Chests / Shadows</b>: exact world positions via
///   <see cref="DungeonNav"/>. These take priority over the bare floor label.
///
/// Compass = world axes (+Z north, +X east), mapped to grid steps once at toggle via
/// <see cref="ProbeGridDelta"/>. Only active in dungeons (major &gt;= 20 excl. 240/250+).
/// </summary>
internal class DungeonCursor
{
    private const int PollMs = 40;
    private const int VK_H = 0x48;             // toggle
    private const int VK_I = 0x49, VK_K = 0x4B, VK_J = 0x4A, VK_L = 0x4C;
    private const int VK_N = 0x4E;             // Walk/Look sub-mode toggle
    private const int VK_Y = 0x59;             // re-orient: re-announce here + open directions

    // The cursor moves on the in-game minimap GRID — exactly ONE CELL per press —
    // so the player builds a clean square-by-square mental map (user 2026-06-18:
    // wants it "squareish to build the map in the mind"). One minimap cell is
    // ~1250 world units (the dungeon's own minimap resolution; see MinimapTracker),
    // so a cell-step is both bigger than the old 250u nudge AND grid-aligned.

    // FIXED COMPASS: I/K/J/L are absolute map directions (I=north/up, K=south,
    // J=west, L=east), NOT relative to facing — so the map never rotates under the
    // player and the directions never depend on the camera.
    private enum Dir { North, South, West, East }
    private static readonly (int vk, Dir dir)[] Moves =
    {
        (VK_I, Dir.North),
        (VK_K, Dir.South),
        (VK_J, Dir.West),
        (VK_L, Dir.East),
    };

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _hWas;
    private readonly bool[] _moveWas = new bool[4];

    private static DungeonCursor? _instance;   // for cross-component static accessors (DungeonNav Backspace mark-walk)
    private volatile bool _active;             // read/written from DungeonNav's thread too → volatile
    private int _row, _col;          // cursor grid cell
    private int _orow, _ocol;        // origin cell (player's cell at activation)
    private float _cx, _cz;          // cursor cell-center world pos (entity/log use)
    private (int dr, int dc) _northGrid, _eastGrid;  // grid step for world +Z (north) / +X (east)
    private bool _inDungeonLast;

    // Walk = I/K/J/L STEP the player one cell (the primary movement). Look = move
    // a virtual survey cursor without walking (scout ahead). N toggles; Walk default.
    private enum SubMode { Walk, Look }
    private volatile SubMode _mode = SubMode.Walk;
    private bool _nWas, _yWas;
    private int _lastRoomId = -1;              // for the "new room" landmark while stepping
    private int _preStepRow, _preStepCol;      // cell before the current step (to detect tile changes)
    private const float StepFraction = 1f;     // WALK step = one full minimap tile (the map unit); doors are auto-threaded so the tile needn't be tiny

    public DungeonCursor()
    {
        _instance = this;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "DungeonCursor" };
        _thread.Start();
        Log("[DungeonCursor] ready (H toggle, I/K/J/L move)");
    }

    public void Stop() => _stopped = true;

    /// <summary>True while the cursor is active (used by DungeonNav so Backspace walks to the cursor cell).</summary>
    internal static bool IsActive => _instance?._active == true;

    /// <summary>
    /// World XZ of the cursor's current cell, for "mark &amp; walk" (DungeonNav Backspace).
    /// <paramref name="unexplored"/> = the cell is mapped-but-unexplored (flag 0) → the walker
    /// will straight-line toward it rather than route. Returns false if the cursor is off or the
    /// cell can't be resolved.
    /// </summary>
    internal static bool TryGetMarkTarget(out float wx, out float wz, out bool unexplored)
    {
        wx = wz = 0f; unexplored = false;
        var inst = _instance;
        // Only Look mode marks a cell for Backspace; in Walk mode the look-cursor
        // IS the player, so Backspace falls through to the browser auto-walk.
        if (inst == null || !inst._active || inst._mode != SubMode.Look) return false;
        if (!MinimapTracker.CellToWorld(inst._row, inst._col, out wx, out wz)) return false;
        unexplored = MinimapTracker.ReadCell(inst._row, inst._col, out var cell) && cell.Flag == 0;
        return true;
    }

    /// <summary>Turn the cursor off (DungeonNav calls this once a mark-walk starts).</summary>
    internal static void Deactivate()
    {
        if (_instance != null) _instance._active = false;
    }

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[DungeonCursor] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!Utils.GameHasFocus()) return;   // don't process hotkeys while alt-tabbed
        bool inDungeon = InDungeon();
        if (inDungeon != _inDungeonLast)
        {
            if (!inDungeon) _active = false;
            _inDungeonLast = inDungeon;
        }

        bool h = IsKeyDown(VK_H);
        if (h && !_hWas) Toggle();
        _hWas = h;

        bool n = IsKeyDown(VK_N);
        if (n && !_nWas && _active) ToggleMode();
        _nWas = n;

        bool y = IsKeyDown(VK_Y);
        if (y && !_yWas && _active) SpeakOrient();
        _yWas = y;

        if (_active)
            for (int i = 0; i < Moves.Length; i++)
            {
                bool down = IsKeyDown(Moves[i].vk);
                if (down && !_moveWas[i]) Move(Moves[i].dir);
                _moveWas[i] = down;
            }
    }

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 240;   // dungeons 20-239; battles (240-249) excluded
    }

    private void Toggle()
    {
        if (!InDungeon()) { Speech.Say("Cursor only works in dungeons.", true); return; }
        if (_active)
        {
            _active = false;
            if (AutoWalk.AutoWalker.IsActive) AutoWalk.AutoWalker.Cancel();   // stop any step in flight
            Speech.Say("Cursor off.", true);
            return;
        }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) { Speech.Say("Position unavailable.", true); return; }

        // The cursor starts on the player's grid cell. If the player isn't on a
        // mapped cell the grid model can't anchor — bail cleanly.
        if (!MinimapTracker.WorldToCell(px, pz, out _row, out _col))
        { Speech.Say("Your position isn't on the map yet.", true); return; }
        _orow = _row; _ocol = _col;

        // FIXED COMPASS frame: map world +Z (north) and +X (east) to grid steps via
        // the live minimap transform (handedness/sign handled in ProbeGridDelta). No
        // gaze/facing involved — the directions are absolute and never rotate.
        _active = true;
        _mode = SubMode.Walk;                        // default to stepping (the primary movement)
        MinimapTracker.CellToWorld(_row, _col, out _cx, out _cz);
        _northGrid = ProbeGridDelta(0f, 1f);         // +Z = north
        _eastGrid = ProbeGridDelta(1f, 0f);          // +X = east
        _lastRoomId = MinimapTracker.ReadCell(_row, _col, out var startCell) ? startCell.RoomId : -1;

        WinBeep(1400, 30);
        Speech.Say($"Cursor on. Walk mode. {ContentHere()}. {OpenDirections()}.", true);
        Log($"[DungeonCursor] ON major={FieldTracker.CurrentMajor} cell=({_row},{_col}) north={_northGrid} east={_eastGrid}");
    }

    /// <summary>Compass direction → world unit vector (+Z north, +X east).</summary>
    private static (float dx, float dz) Vector(Dir dir) => dir switch
    {
        Dir.North => (0f, 1f),
        Dir.South => (0f, -1f),
        Dir.East => (1f, 0f),
        Dir.West => (-1f, 0f),
        _ => (0f, 0f)
    };

    /// <summary>
    /// Convert a world direction into a single grid step (Δrow, Δcol) by walking the
    /// live minimap transform until the cell changes. Robust to the transform's sign
    /// (we never assume world +X = +col). For a cardinal world vector exactly one of
    /// Δrow/Δcol is non-zero.
    /// </summary>
    private (int dr, int dc) ProbeGridDelta(float wdx, float wdz)
    {
        if (!MinimapTracker.CellToWorld(_row, _col, out float bx, out float bz)) return (0, 0);
        for (float t = 300f; t <= 3000f; t += 150f)
            if (MinimapTracker.WorldToCell(bx + wdx * t, bz + wdz * t, out int r, out int c)
                && (r != _row || c != _col))
                return (Math.Sign(r - _row), Math.Sign(c - _col));
        return (0, 0);
    }

    /// <summary>Move the cursor / step the player exactly ONE grid cell in a compass direction.</summary>
    private void Move(Dir dir)
    {
        var (dr, dc) = dir switch
        {
            Dir.North => _northGrid,
            Dir.South => (-_northGrid.dr, -_northGrid.dc),
            Dir.East => _eastGrid,
            Dir.West => (-_eastGrid.dr, -_eastGrid.dc),
            _ => (0, 0)
        };
        if (dr == 0 && dc == 0) { Speech.Say("Direction unavailable.", true); return; }

        int nr = _row + dr, nc = _col + dc;
        if (nr < 0 || nr >= MinimapTracker.ROWS || nc < 0 || nc >= MinimapTracker.COLS)
        {
            WinBeep(360, 70);
            Speech.Say("Edge of map.", true);
            Log($"[DungeonCursor] {dir} BLOCKED edge cell=({_row},{_col})");
            return;
        }
        // Boundary cells (flag 2) are walls — the cursor can't enter them.
        if (MinimapTracker.ReadCell(nr, nc, out var cell) && cell.Flag == 2)
        {
            WinBeep(360, 70);
            Speech.Say("Wall.", true);
            Log($"[DungeonCursor] {dir} BLOCKED wall cell=({nr},{nc})");
            return;
        }

        if (_mode == SubMode.Look) MoveLookCursor(nr, nc, dir);
        else StepPlayer(nr, nc, dir);
    }

    /// <summary>LOOK mode: move the virtual survey cursor one cell (the player stays put).</summary>
    private void MoveLookCursor(int nr, int nc, Dir dir)
    {
        _row = nr; _col = nc;
        MinimapTracker.CellToWorld(_row, _col, out _cx, out _cz);
        string content = ContentCell(_row, _col);
        string off = OffsetFromPlayer();
        WinBeep(ContentBeep(content), 25);
        Speech.Say(string.IsNullOrEmpty(off) ? $"{content}." : $"{content}, {off}.", true);
        Log($"[DungeonCursor] LOOK {dir} → cell=({_row},{_col}) content={content}");
    }

    /// <summary>WALK mode: physically step the player ~half a tile in this compass direction, then announce.</summary>
    private void StepPlayer(int nr, int nc, Dir dir)
    {
        if (AutoWalk.AutoWalker.IsActive) { Speech.Say("Busy.", true); return; }
        // Drive the absolute compass world direction (straight along the grid axis),
        // not toward a point — that's what keeps the step from curving.
        var (dirX, dirZ) = Vector(dir);
        if (MathF.Abs(dirX) < 1e-3f && MathF.Abs(dirZ) < 1e-3f)
        { Speech.Say("Direction unavailable.", true); return; }

        _preStepRow = _row; _preStepCol = _col;
        WinBeep(1250, 16);   // a soft "stepping" tick before the move
        Log($"[DungeonCursor] STEP {dir} from=({_row},{_col}) dir=({dirX:F2},{dirZ:F2}) toward=({nr},{nc})");
        AutoWalk.AutoWalker.Step(dirX, dirZ, StepFraction, OnStepDone);
    }

    /// <summary>Step finished (runs on the walker thread): re-sync to the REAL cell, then announce.</summary>
    private void OnStepDone(AutoWalk.AutoWalker.StepResult result, int finalRow, int finalCol)
    {
        // Re-sync the cursor to the player's ACTUAL cell (truth, not intent) so the
        // map stays honest even if the step was deflected or fell short.
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (!float.IsNaN(px) && !float.IsNaN(pz) && MinimapTracker.WorldToCell(px, pz, out int rr, out int rc))
        { _row = rr; _col = rc; }
        else if (finalRow >= 0) { _row = finalRow; _col = finalCol; }
        MinimapTracker.CellToWorld(_row, _col, out _cx, out _cz);

        bool tileChanged = _row != _preStepRow || _col != _preStepCol;
        switch (result)
        {
            // A half-tile step often stays in the same tile: a soft tick confirms the
            // move; full content + open directions are spoken only when the tile changes.
            case AutoWalk.AutoWalker.StepResult.Moved:
                if (tileChanged) SpeakHere(false); else WinBeep(980, 16);
                break;
            case AutoWalk.AutoWalker.StepResult.Blocked: SpeakHere(true); break;
            case AutoWalk.AutoWalker.StepResult.Refused: Speech.Say("Can't step there.", true); break;
            case AutoWalk.AutoWalker.StepResult.Cancelled: break;   // the walker already logged why
        }
        Log($"[DungeonCursor] step done {result} cell=({_row},{_col}) tileChanged={tileChanged}");
    }

    /// <summary>Speak the current cell: content + which directions are open.</summary>
    private void SpeakHere(bool blocked)
    {
        string content = ContentCell(_row, _col);
        string open = OpenDirections();
        // "New room." landmark removed per user 2026-06-24 (misleading on the coarse minimap).
        WinBeep(blocked ? 300u : ContentBeep(content), blocked ? 70u : 25u);
        Speech.Say($"{(blocked ? "Blocked. " : "")}{content}. {open}.", true);
    }

    /// <summary>Y key: re-announce the current cell on demand — content + open directions.</summary>
    private void SpeakOrient()
    {
        Speech.Say($"{ContentCell(_row, _col)}. {OpenDirections()}.", true);
        Log($"[DungeonCursor] orient cell=({_row},{_col})");
    }

    /// <summary>Toggle between Walk (step the player) and Look (survey cursor) sub-modes.</summary>
    private void ToggleMode()
    {
        if (AutoWalk.AutoWalker.IsActive) { Speech.Say("Busy.", true); return; }
        _mode = _mode == SubMode.Walk ? SubMode.Look : SubMode.Walk;
        // Re-anchor to the player's REAL cell (in Walk the look-cursor may have
        // wandered; in Look the survey should start where the player stands).
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (!float.IsNaN(px) && !float.IsNaN(pz) && MinimapTracker.WorldToCell(px, pz, out int rr, out int rc))
        { _row = rr; _col = rc; MinimapTracker.CellToWorld(_row, _col, out _cx, out _cz); }
        _orow = _row; _ocol = _col;
        if (_mode == SubMode.Walk)
        {
            WinBeep(1300, 30);
            Speech.Say($"Walk mode. {ContentHere()}. {OpenDirections()}.", true);
        }
        else
        {
            WinBeep(900, 30);
            Speech.Say($"Look mode. {ContentHere()}.", true);
        }
        Log($"[DungeonCursor] mode → {_mode} cell=({_row},{_col})");
    }

    /// <summary>Which compass directions are open walkable floor (flag 1) from the current cell.</summary>
    private string OpenDirections()
    {
        bool north = NeighborWalkable(_northGrid.dr, _northGrid.dc);
        bool south = NeighborWalkable(-_northGrid.dr, -_northGrid.dc);
        bool east = NeighborWalkable(_eastGrid.dr, _eastGrid.dc);
        bool west = NeighborWalkable(-_eastGrid.dr, -_eastGrid.dc);
        var parts = new List<string>(4);
        if (north) parts.Add("north");
        if (east) parts.Add("east");
        if (south) parts.Add("south");
        if (west) parts.Add("west");
        if (parts.Count == 0) return "No way out";
        if (parts.Count == 1) return $"Only {parts[0]}";   // dead end — sole exit
        return "Open " + NaturalJoin(parts);
    }

    private bool NeighborWalkable(int dr, int dc)
    {
        if (dr == 0 && dc == 0) return false;
        int r = _row + dr, c = _col + dc;
        if (r < 0 || r >= MinimapTracker.ROWS || c < 0 || c >= MinimapTracker.COLS) return false;
        return MinimapTracker.ReadCell(r, c, out var cell) && cell.Flag == 1;   // 1 = walkable floor/door
    }

    private static string NaturalJoin(List<string> parts) =>
        parts.Count == 1 ? parts[0]
        : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[^1];

    /// <summary>What's on the cursor's current cell.</summary>
    private string ContentHere() => ContentCell(_row, _col);

    /// <summary>
    /// What occupies a grid cell: an entity whose world position falls in that same
    /// square (squareish — same cell = present), else the cell's mapped state.
    /// </summary>
    private string ContentCell(int row, int col)
    {
        // Shadow first; carry its facing relative to the player ("facing you" = it
        // can ambush, "facing away" = you can hit it from behind).
        foreach (var (x, z, fx, fz) in DungeonNav.ShadowsWithFacing())
            if (InCell(x, z, row, col))
            {
                string f = "";
                if (fx != 0 || fz != 0)
                {
                    float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
                    if (!float.IsNaN(px) && !float.IsNaN(pz))
                        f = (fx * (px - x) + fz * (pz - z)) > 0 ? ", facing you" : ", facing away";
                }
                return $"Shadow{f}";
            }
        foreach (var (x, z) in DungeonNav.Chests()) if (InCell(x, z, row, col)) return "Chest";
        foreach (var (x, z) in DungeonNav.Doors()) if (InCell(x, z, row, col)) return "Door";
        if (MinimapTracker.ReadCell(row, col, out var cell)) return cell.Flag == 0 ? "Unexplored" : "Floor";
        return "Floor";
    }

    private static bool InCell(float x, float z, int row, int col)
        => MinimapTracker.WorldToCell(x, z, out int r, out int c) && r == row && c == col;

    /// <summary>Cursor offset from origin in CELLS, as compass (north/south, east/west).</summary>
    private string OffsetFromPlayer()
    {
        int dr = _row - _orow, dc = _col - _ocol;
        if (dr == 0 && dc == 0) return "here";
        int north = dr * _northGrid.dr + dc * _northGrid.dc;   // unit orthogonal axes →
        int east = dr * _eastGrid.dr + dc * _eastGrid.dc;      // exact cell counts
        var parts = new List<string>(2);
        if (north != 0) parts.Add($"{Math.Abs(north)} {(north > 0 ? "north" : "south")}");
        if (east != 0) parts.Add($"{Math.Abs(east)} {(east > 0 ? "east" : "west")}");
        return string.Join(", ", parts);
    }

    private static uint ContentBeep(string content) =>
        content.StartsWith("Shadow") ? 700u :
        content == "Chest" ? 1500u :
        content == "Door" ? 1100u :
        content == "Unexplored" ? 500u : 950u;

    private static void WinBeep(uint freq, uint ms) { try { Beep(freq, ms); } catch { } }
    [DllImport("kernel32.dll")] private static extern bool Beep(uint dwFreq, uint dwDuration);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}
