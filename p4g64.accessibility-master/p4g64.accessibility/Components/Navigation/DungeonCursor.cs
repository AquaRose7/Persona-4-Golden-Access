using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Blind dungeon navigation cursor. Press <c>H</c> to toggle it on at your position;
/// <b>I/K/J/L</b> move one grid cell. Two independent axes of behaviour:
/// <b>N = Walk/Look</b> sub-mode (Walk steps the player; Look surveys), and
/// <b>Shift+N = Compass/Camera</b> FRAME. Compass (default): I/K/J/L are absolute
/// North/South/West/East and the map never rotates (stable mental map). Camera:
/// I/K/J/L are Ahead/Behind/Left/Right relative to where you look (gaze-forward,
/// sign-safe), snapped to the nearest cardinal; moves AND open-exit readouts are
/// spoken in relative words (egocentric — the frame turns with you). <b>Y</b>
/// re-announces. On <c>H</c> it announces facing + both modes.
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
    private const int VK_N = 0x4E;             // N = Walk/Look sub-mode; Shift+N = Compass/Camera frame
    private const int VK_SHIFT = 0x10;

    // The cursor moves on the in-game minimap GRID — exactly ONE CELL per press —
    // so the player builds a clean square-by-square mental map (user 2026-06-18:
    // wants it "squareish to build the map in the mind"). One minimap cell is
    // ~1250 world units (the dungeon's own minimap resolution; see MinimapTracker),
    // so a cell-step is both bigger than the old 250u nudge AND grid-aligned.

    // FIXED COMPASS: I/K/J/L are absolute map directions (I=north/up, K=south,
    // J=west, L=east), NOT relative to facing — so the map never rotates under the
    // player and the directions never depend on the camera.
    private enum Dir { North, South, West, East }

    // Two movement FRAMES (Shift+N toggles; orthogonal to Walk/Look):
    //  Compass (default) — I/K/J/L are absolute North/South/West/East; the map
    //    never rotates, so it builds a stable mental map.
    //  Camera — I/K/J/L are Ahead/Behind/Left/Right relative to the LIVE camera
    //    (read fresh each press, snapped to the nearest cardinal); moves AND
    //    open-exit readouts are spoken in relative words. Egocentric: the frame
    //    turns with the camera, like a first-person view.
    private enum Frame { Compass, Camera }
    private volatile Frame _frame = Frame.Compass;

    private enum RelDir { Ahead, Behind, Left, Right }
    // The four move keys in a fixed slot order (I, K, J, L). Each slot means a
    // compass Dir in Compass frame and a RelDir in Camera frame.
    private static readonly int[] MoveKeys = { VK_I, VK_K, VK_J, VK_L };
    private static readonly Dir[] CompassForSlot = { Dir.North, Dir.South, Dir.West, Dir.East };
    private static readonly RelDir[] RelForSlot = { RelDir.Ahead, RelDir.Behind, RelDir.Left, RelDir.Right };

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _hWas;
    private readonly bool[] _moveWas = new bool[4];

    private static DungeonCursor? _instance;   // for cross-component static accessors (DungeonNav Backspace mark-walk)
    private volatile bool _active;             // read/written from DungeonNav's thread too → volatile
    private int _row, _col;          // cursor grid cell
    private int _orow, _ocol;        // origin cell (player's cell at activation)
    private float _cx, _cz;          // cursor cell-center world pos (entity/log use)
    private (int dr, int dc) _northGrid, _eastGrid;  // grid step for north (world +Z) / east (world −X)
    private bool _inDungeonLast;

    // Walk = I/K/J/L STEP the player one cell (the primary movement). Look = move
    // a virtual survey cursor without walking (scout ahead). N toggles; Walk default.
    private enum SubMode { Walk, Look }
    private volatile SubMode _mode = SubMode.Walk;
    private bool _nWas;
    private int _lastRoomId = -1;              // for the "new room" landmark while stepping
    private int _preStepRow, _preStepCol;      // cell before the current step (to detect tile changes)
    private const float StepFraction = 1f;     // WALK step = one full minimap tile (the map unit); doors are auto-threaded so the tile needn't be tiny

    public DungeonCursor()
    {
        _instance = this;
        // Session-start FRAME from the settings menu (Shift+N still toggles during
        // play and stays sticky per session — the toggle does NOT write the setting).
        _frame = ModSettings.GetInt("cursor_default_frame", Defaults.CursorFrame) == 1 ? Frame.Camera : Frame.Compass;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "DungeonCursor" };
        _thread.Start();
        Log("[DungeonCursor] ready (H toggle, I/K/J/L move, N Walk/Look, Shift+N Compass/Camera)");
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

    /// <summary>Controller entry point for Shift+N (Compass/Camera frame toggle) — LT+R3.
    /// Direct call, not a synthesized Shift+N, so it can't race the N edge into a Walk/Look toggle.</summary>
    internal static void ToggleFrameFromController()
    {
        var inst = _instance;
        if (inst != null && inst._active) inst.ToggleFrame();
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
        if (SettingsMenu.IsOpen) return;     // settings menu owns input
        // Don't run the mapping cursor while the camp menu is open (its keys would fire behind it).
        if (CommandMenus.PlayerMenu.IsMenuOpen) return;
        if (FieldTracker.InAreaTransition) return;   // back off during a transition (crash-safety)
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
        if (n && !_nWas && _active) { if (IsKeyDown(VK_SHIFT)) ToggleFrame(); else ToggleMode(); }
        _nWas = n;

        // (The old Y re-orient key was UNBOUND 2026-07-19 on user request.)

        if (_active)
            for (int i = 0; i < MoveKeys.Length; i++)
            {
                bool down = IsKeyDown(MoveKeys[i]);
                if (down && !_moveWas[i]) MovePress(i);
                _moveWas[i] = down;
            }
    }

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220;   // dungeon floors 20-69; battles (220-299) excluded
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
        _mode = ModSettings.GetInt("cursor_default_mode", Defaults.CursorMode) == 1
            ? SubMode.Look : SubMode.Walk;           // user-set default (SettingsMenu)
        MinimapTracker.CellToWorld(_row, _col, out _cx, out _cz);
        _northGrid = ProbeGridDelta(0f, 1f);         // +Z = north
        // Physical EAST = world −X. P4G's +X is physically WEST (verified 2026-07-08:
        // facing north, the player's right-hand side — real-compass east — reads as
        // world −X). So "east" is anchored to −X here, not the assumed +X.
        _eastGrid = ProbeGridDelta(-1f, 0f);         // east = world −X
        _lastRoomId = MinimapTracker.ReadCell(_row, _col, out var startCell) ? startCell.RoomId : -1;

        WinBeep(1400, 30);
        Speech.Say($"Cursor on. You face {FacingWord()}. {ModeWord()}. {FrameWord()}. {ContentHere()}. {OpenDirections()}.", true);
        Log($"[DungeonCursor] ON major={FieldTracker.CurrentMajor} cell=({_row},{_col}) frame={_frame}");
    }

    /// <summary>Compass direction → world unit vector (+Z north, +X east).</summary>
    private static (float dx, float dz) Vector(Dir dir) => dir switch
    {
        Dir.North => (0f, 1f),
        Dir.South => (0f, -1f),
        Dir.East => (-1f, 0f),   // east = world −X (P4G's +X is physically west)
        Dir.West => (1f, 0f),
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

    /// <summary>
    /// A move key was pressed (slot 0..3 = I/K/J/L). Resolve it to a compass
    /// direction per the active frame, then step. In Camera frame the slot's
    /// RelDir is resolved against the LIVE camera cardinal (fresh each press).
    /// </summary>
    private void MovePress(int slot)
    {
        Dir dir;
        if (_frame == Frame.Compass)
            dir = CompassForSlot[slot];
        else
            dir = ResolveRel(CameraCardinal(), RelForSlot[slot]);
        Move(dir);
    }

    /// <summary>
    /// The compass direction the CAMERA is aimed — Camera frame's "ahead". Uses
    /// FieldTracker.CameraForward3D — the SAME "way W moves" basis the auto-walker,
    /// the P beacon, the school steerer and the wall-bump all steer by (all
    /// user-verified). "Ahead" therefore = the direction you'd walk holding
    /// forward, guaranteed consistent with the beacon and auto-walk. NOT the
    /// body/gaze facing (differs when the camera is rotated round the party).
    /// </summary>
    private static Dir CameraCardinal()
    {
        var (fx, fz) = FacingVector();
        if (fx == 0f && fz == 0f) return Dir.North;              // camera unavailable → sane default
        if (MathF.Abs(fx) >= MathF.Abs(fz)) return fx > 0 ? Dir.West : Dir.East;  // +X = west
        return fz > 0 ? Dir.North : Dir.South;                    // +Z = north
    }

    /// <summary>The camera-forward direction XZ (gaze facing as a last resort).</summary>
    private static (float fx, float fz) FacingVector()
    {
        var (fx, fz) = FieldTracker.CameraForward3D();
        if (fx == 0f && fz == 0f) (fx, fz) = FieldTracker.PlayerForwardViaGaze();
        return (fx, fz);
    }

    private static Dir ResolveRel(Dir cardinal, RelDir rel) => rel switch
    {
        RelDir.Ahead => cardinal,
        RelDir.Behind => Opposite(cardinal),
        RelDir.Right => CamRight(cardinal),
        RelDir.Left => CamLeft(cardinal),
        _ => cardinal
    };

    // With east correctly anchored to world −X (see _eastGrid), the map is a
    // standard compass: the camera's right is forward rotated clockwise.
    private static Dir CamRight(Dir fwd) => RotateCW(fwd);
    private static Dir CamLeft(Dir fwd) => RotateCcw(fwd);

    private static Dir Opposite(Dir d) => d switch
    { Dir.North => Dir.South, Dir.South => Dir.North, Dir.East => Dir.West, _ => Dir.East };
    private static Dir RotateCW(Dir d) => d switch
    { Dir.North => Dir.East, Dir.East => Dir.South, Dir.South => Dir.West, _ => Dir.North };
    private static Dir RotateCcw(Dir d) => Opposite(RotateCW(d));

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

    /// <summary>Shift+N: toggle the movement FRAME (Compass ↔ Camera). Re-orients.</summary>
    private void ToggleFrame()
    {
        _frame = _frame == Frame.Compass ? Frame.Camera : Frame.Compass;
        WinBeep(_frame == Frame.Camera ? 1150u : 1450u, 30);
        Speech.Say($"{FrameWord()}. You face {FacingWord()}. {OpenDirections()}.", true);
        Log($"[DungeonCursor] frame → {_frame} cell=({_row},{_col})");
    }

    /// <summary>
    /// Which directions are open walkable floor (flag 1) from the current cell.
    /// Compass frame speaks north/east/south/west (stable map); Camera frame
    /// speaks ahead/right/behind/left relative to the live camera (so the whole
    /// readout matches the movement keys — no compass↔relative translation).
    /// </summary>
    private string OpenDirections()
    {
        var parts = new List<string>(4);
        if (_frame == Frame.Camera)
        {
            Dir cam = CameraCardinal();
            // clockwise from "ahead" so the list reads naturally
            if (OpenInCompass(cam)) parts.Add("ahead");
            if (OpenInCompass(CamRight(cam))) parts.Add("right");
            if (OpenInCompass(Opposite(cam))) parts.Add("behind");
            if (OpenInCompass(CamLeft(cam))) parts.Add("left");
        }
        else
        {
            if (OpenInCompass(Dir.North)) parts.Add("north");
            if (OpenInCompass(Dir.East)) parts.Add("east");
            if (OpenInCompass(Dir.South)) parts.Add("south");
            if (OpenInCompass(Dir.West)) parts.Add("west");
        }
        if (parts.Count == 0) return "No way out";
        if (parts.Count == 1) return $"Only {parts[0]}";   // dead end — sole exit
        return "Open " + NaturalJoin(parts);
    }

    /// <summary>Is the neighbouring cell in a compass direction open walkable floor?</summary>
    private bool OpenInCompass(Dir d)
    {
        var (dr, dc) = d switch
        {
            Dir.North => _northGrid,
            Dir.South => (-_northGrid.dr, -_northGrid.dc),
            Dir.East => _eastGrid,
            Dir.West => (-_eastGrid.dr, -_eastGrid.dc),
            _ => (0, 0)
        };
        return NeighborWalkable(dr, dc);
    }

    private string FrameWord() => _frame == Frame.Compass ? "Compass mode" : "Camera mode";
    private string ModeWord() => _mode == SubMode.Walk ? "Walk mode" : "Look mode";
    private static string DirWord(Dir d) => d switch
    { Dir.North => "north", Dir.South => "south", Dir.East => "east", _ => "west" };

    /// <summary>The player's facing as a compass word ("you face north").</summary>
    private static string FacingWord()
    {
        var (fx, fz) = FacingVector();
        if (fx == 0f && fz == 0f) return "an unknown direction";
        Dir d = MathF.Abs(fx) >= MathF.Abs(fz) ? (fx > 0 ? Dir.West : Dir.East) : (fz > 0 ? Dir.North : Dir.South);
        return DirWord(d);
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

    /// <summary>
    /// Cursor offset from origin in CELLS. Compass frame speaks north/south +
    /// east/west; Camera frame speaks ahead/behind + right/left relative to the
    /// live camera (so it matches the move keys and the open-exits readout — no
    /// compass word leaks into camera mode, which was reading as a contradiction
    /// against "you face north").
    /// </summary>
    private string OffsetFromPlayer()
    {
        int dr = _row - _orow, dc = _col - _ocol;
        if (dr == 0 && dc == 0) return "here";
        int north = dr * _northGrid.dr + dc * _northGrid.dc;   // unit orthogonal axes →
        int east = dr * _eastGrid.dr + dc * _eastGrid.dc;      // exact cell counts

        if (_frame == Frame.Camera)
        {
            Dir cam = CameraCardinal();
            var (ae, an) = CardinalEN(cam);              // "ahead" unit in (east,north)
            var (re, rn) = CardinalEN(CamRight(cam));    // "right" unit
            int ahead = east * ae + north * an;
            int right = east * re + north * rn;
            var rel = new List<string>(2);
            if (ahead != 0) rel.Add($"{Math.Abs(ahead)} {(ahead > 0 ? "ahead" : "behind")}");
            if (right != 0) rel.Add($"{Math.Abs(right)} {(right > 0 ? "right" : "left")}");
            return string.Join(", ", rel);
        }

        var parts = new List<string>(2);
        if (north != 0) parts.Add($"{Math.Abs(north)} {(north > 0 ? "north" : "south")}");
        if (east != 0) parts.Add($"{Math.Abs(east)} {(east > 0 ? "east" : "west")}");
        return string.Join(", ", parts);
    }

    /// <summary>Unit vector of a compass Dir in (east, north) name-space.</summary>
    private static (int e, int n) CardinalEN(Dir d) => d switch
    {
        Dir.North => (0, 1),
        Dir.South => (0, -1),
        Dir.East => (1, 0),
        _ => (-1, 0)   // West
    };

    private static uint ContentBeep(string content) =>
        content.StartsWith("Shadow") ? 700u :
        content == "Chest" ? 1500u :
        content == "Door" ? 1100u :
        content == "Unexplored" ? 500u : 950u;

    private static void WinBeep(uint freq, uint ms)
        => ToneCue.PlayTones(0.45f * SoundSettings.CursorBeepVol, ((float)freq, (int)ms));

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}
