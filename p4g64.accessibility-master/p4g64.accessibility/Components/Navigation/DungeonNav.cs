using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Unified dungeon "search" browser — the overworld NavigationAssist browse
/// model, applied to dungeon contents.
///
///   <c>-</c> / <c>=</c>   cycle CATEGORY: Doors · Chests · Shadows · Exits.
///                         In the LOBBY only (TV-world hub, major == 20) a 5th
///                         category, Interactables, lists the save point + named
///                         NPCs from the master table. It is HIDDEN inside dungeon
///                         floors, where that table is all false readings.
///   <c>[</c> / <c>]</c>   step through that category's entries, nearest → far,
///                         one at a time
///   <c>\</c>              act on the current selection
///
/// The menu is <b>live</b>: every <c>[</c>/<c>]</c> step re-reads the world from
/// the player's CURRENT position, so distances/directions update as you move —
/// no need to re-select the category to refresh.
///
/// <c>\</c> behaviour — REVERTED to the original (user call, 2026-06-11, after
/// two briefing iterations missed the mark):
/// - <b>Exits</b> → instant ±1 floor teleport (story-safe; BIT-flag →
///   field.flow → CALL_DUNGEON).
/// - <b>Doors / Chests / Shadows</b> → re-announce the selection.
///
/// <c>Backspace</c> → auto-walk to the selection
/// (<see cref="AutoWalk.AutoWalker"/>); Backspace again cancels. Shadows are
/// blocked until the stealth approach (P4) ships.
///
/// ### Data sources
/// - <b>Shadows</b>: enemy actor array <c>0x140EC2E90</c> (16 × 0x7C0; actor at
///   <c>+0x58</c>; XZ at <c>actor+0x360/+0x368</c>). Exact positions.
/// - <b>Doors / Chests</b>: the engine scene-actor list (scene root
///   <c>*(0x140AA8098)</c> → <c>+0x08</c> → head <c>+0x50</c>, walk <c>+0x150</c>;
///   ACTIVE nodes via <c>+0x28 &amp; 2</c>; xform <c>+0x168</c> → XZ
///   <c>+0x360/+0x368</c>). A node is split into door vs chest by the float at
///   <c>+0x430</c> (doors ≈ 0.3; see <see cref="ClassifyKind"/> — first
///   calibration, the <c>[ActiveNodes]</c> log carries the real values).
///
/// Active on any dungeon floor (<c>major &gt;= 20</c> excluding battle 240 / 250+).
/// </summary>
internal class DungeonNav
{
    private const int PollMs = 50;

    private const int VK_OEM_MINUS = 0xBD;   // -
    private const int VK_OEM_PLUS  = 0xBB;   // =
    private const int VK_OEM_4     = 0xDB;   // [
    private const int VK_OEM_6     = 0xDD;   // ]
    private const int VK_OEM_5     = 0xDC;   // \
    private const int VK_BACK      = 0x08;   // Backspace = auto-walk

    private enum Cat { Doors, AllDoors, Chests, Shadows, Exits, Places, Events }
    // Places (the interactable master table). In the LOBBY it lists all named NPCs + save point. On
    // dungeon FLOORS the table is mostly garbage EXCEPT the walk-up NPCs (benched party member + the
    // Fox) which are cat=9 — so Places shows on floors too but BuildPlaceEntries filters to cat=9 there
    // (verified 2026-07-02: Marukyu had cat=9 ids 0x120A/0x020A right next to the party+fox).
    private static readonly Cat[] DungeonCategories =
        { Cat.Doors, Cat.AllDoors, Cat.Chests, Cat.Shadows, Cat.Exits, Cat.Places };
    // Entrance/lobby (major 20): NO "All doors" — it's not a real dungeon floor (user 2026-07-02).
    private static readonly Cat[] LobbyCategories =
        { Cat.Doors, Cat.Chests, Cat.Shadows, Cat.Exits, Cat.Places };
    private static bool InLobby() => FieldTracker.CurrentMajor == 20;
    // Location-aware: every call site uses Categories.Length / Categories[i].
    // "Events" is appended ONLY when the current floor has recorded marks — that
    // presence check IS the "scripted floor" gate, so procedural floors are
    // untouched and the category count matches today's everywhere else.
    private static Cat[] Categories
    {
        get
        {
            var baseCats = InLobby() ? LobbyCategories : DungeonCategories;
            if (!HasEventMarks()) return baseCats;
            var l = new List<Cat>(baseCats) { Cat.Events };
            return l.ToArray();
        }
    }
    private static string CatName(Cat c) => c switch
    {
        Cat.Doors => "Doors", Cat.AllDoors => "All doors", Cat.Chests => "Chests",
        Cat.Shadows => "Shadows", Cat.Exits => "Stairs",
        Cat.Places => "Interactables", Cat.Events => "Events", _ => c.ToString()
    };

    // ── Shadow actor array (Ghidra) ──
    private static readonly nint ShadowArray = unchecked((nint)0x140EC2E90L);
    private static readonly nint FloorGatePtr = unchecked((nint)0x1411AB2F0L);
    private const int SLOT_COUNT = 16, SLOT_STRIDE = 0x7C0;
    private const int OFF_ACTIVE1 = 0x48, OFF_ACTOR = 0x58, OFF_ACTIVE2 = 0x60;
    private const int OFF_ACTOR_X = 0x360, OFF_ACTOR_Z = 0x368;
    // Shadow actor forward vector (found 2026-06-03 by velocity correlation):
    // x at +0x350, z at +0x358 — the matrix forward row just before position.
    private const int OFF_ACTOR_FX = 0x350, OFF_ACTOR_FZ = 0x358;
    // 3D dungeon world scale ≈ 250u per walking step (see dungeon_3d_scale_and_shadows).
    private const float WorldPerStep = 250f;

    // ── Chest / treasure array (Ghidra: FLD_FUNCTION_0039's array, flagged
    // "treasure/gimmick" in door_actor_investigation.md) ──
    // 8 entries × 0x190; active = *(int*)(e+0x00) != 0; XZ inline at +0x170/+0x178.
    private static readonly nint ChestArray = unchecked((nint)0x15E4375C0L);
    private const int CHEST_COUNT = 8, CHEST_STRIDE = 0x190;
    private const int OFF_CHEST_ACTIVE = 0x00, OFF_CHEST_STATE = 0x08;
    private const int OFF_CHEST_X = 0x170, OFF_CHEST_Z = 0x178;

    // ── Door / interactable scene-actors (Ghidra, corrected by live probe) ──
    private static readonly nint SceneRootPtr = unchecked((nint)0x140AA8098L);
    private const int OFF_SCENE       = 0x08;
    private const int OFF_LIST_HEAD   = 0x50;
    private const int OFF_NODE_NEXT   = 0x150;
    private const int OFF_NODE_ACTIVE = 0x28;     // byte, &2 = active/near
    private const int OFF_NODE_XFORM  = 0x168;
    private const int OFF_NODE_RADIUS = 0x430;    // float; doors ≈ 0.3 (type discriminator)
    private const float DedupUnits = 150f;        // merge an object's paired nodes
    private const float DoorRadius = 0.3f;
    private const float DoorRadiusTol = 0.08f;

    // ── Interactable master table (Ghidra: thunk_FUN_16ac1a140; see
    // memory/interactable_master_table.md) — the source for the lobby's save
    // point, dungeon ENTRANCES, and NPCs, which the scene-actor door/chest
    // enumeration above does NOT surface. One 8-byte pointer per CATEGORY;
    // each heads a linked list (next at +0xFC0). World XZ at +0x360/+0x368
    // (same convention as wall/scene actors). id at +0x406, flags at +0x408.
    // cat=1 id=0x0001 is the gaze cursor (tracks player facing) — filtered.
    // Categories ≥ 16 carry garbage/system rows — not enumerated. This is the
    // "Places" data source: in the lobby the user browses it to find + walk to
    // the castle entrance / save point. We log the full table on Places-select
    // so we can map cat/id → friendly names afterward.
    private static readonly nint MasterTablePtr = unchecked((nint)0x146248560L);
    private const int MT_MAX_CAT = 16;            // observed real interactables live in 0..15
    private const int MT_MAX_PER_CAT = 64;        // linked-list walk guard
    private const int OFF_MT_X = 0x360, OFF_MT_Z = 0x368;
    private const int OFF_MT_ID = 0x406, OFF_MT_FLAGS = 0x408, OFF_MT_NEXT = 0xFC0;
    private const float MtPosCeiling = 1e6f;      // world fits in ~±20k; bigger = noise
    private const float PlaceDedupUnits = 200f;   // merge co-located place rows
    // Template / room-marker rows (the evenly-spaced grid + (0,0) anchors that
    // flood cat 4/6) all set this flag bit; real interactables — NPCs and the
    // lobby's fixed objects (save point / dungeon entrance, e.g. cat=4 id=0x0066
    // at (-523,525), flags 0x?4091118) — do NOT. Filtering on the flag instead
    // of on the category keeps the standout fixed objects while dropping the
    // grid noise. (Lobby dump 2026-06-17.)
    private const uint MT_MARKER_FLAG = 0x01000000u;
    // Generic "person present here" mirror id — duplicates each NPC + the player
    // at a separate cat (4); the real NPC carries a distinct id in cat 5.
    private const short MT_PERSON_MIRROR_ID = 0x07D0;

    // ── Event marks (hand-recorded named points on SCRIPTED/fixed floors) ──
    // Keyed by floor "major_minor" (verified 2026-06-24: scripted floors get a
    // distinct key — Castle 2F = 23_3, vs the 40_0 procedural template). Universal
    // (fixed geometry + ASLR off): record once, bundle, works for every player.
    // Loaded ModDir-first via Utils.DataPath.
    private sealed class EventMark
    {
        public float X { get; set; }
        public float Z { get; set; }
        public string Label { get; set; } = "Mark";
    }
    private static readonly Dictionary<string, List<EventMark>> _marks = new();
    private static readonly object _marksLock = new();

    private static string FloorKey() => $"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}";

    private static bool HasEventMarks()
    {
        lock (_marksLock)
            return _marks.TryGetValue(FloorKey(), out var l) && l.Count > 0;
    }

    private static string MarksWritePath()
    {
        var cwd = Environment.CurrentDirectory;
        foreach (var b in new[] { System.IO.Path.Combine(cwd, "Persona 4 golden", "database"),
                                  System.IO.Path.Combine(cwd, "database") })
            if (System.IO.Directory.Exists(b)) return System.IO.Path.Combine(b, "dungeon_waypoints.json");
        return System.IO.Path.Combine(Utils.ModDir ?? "", "dungeon_waypoints.json");
    }

    private static void LoadMarks()
    {
        try
        {
            string p = DataPath("dungeon_waypoints.json");
            if (!System.IO.File.Exists(p)) { Log("[EventMarks] no file (0 marks)"); return; }
            var d = JsonSerializer.Deserialize<Dictionary<string, List<EventMark>>>(System.IO.File.ReadAllText(p));
            int total = 0;
            if (d != null)
                lock (_marksLock)
                {
                    _marks.Clear();
                    foreach (var kv in d) { _marks[kv.Key] = kv.Value ?? new(); total += _marks[kv.Key].Count; }
                }
            Log($"[EventMarks] loaded {total} mark(s) across {_marks.Count} floor(s)");
        }
        catch (Exception e) { Log($"[EventMarks] load failed: {e.Message}"); }
    }

    private static void SaveMarks()
    {
        try
        {
            Dictionary<string, List<EventMark>> snap;
            lock (_marksLock) snap = new(_marks);
            string json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
            // Write to BOTH (a) the mod-folder copy the loader actually READS via DataPath — so marks
            // PERSIST across restart (the old bug: it saved only to database/, which the loader ignores
            // when a mod-folder copy exists) AND (b) the database source (keeps the committed/bundled
            // copy current). Dedupe when they resolve to the same file.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Utils.ModDir)) paths.Add(System.IO.Path.Combine(Utils.ModDir, "dungeon_waypoints.json"));
            paths.Add(MarksWritePath());
            foreach (var p in paths)
            {
                try { System.IO.File.WriteAllText(p, json); Log($"[EventMarks] saved {snap.Count} floor(s) -> {p}"); }
                catch (Exception e) { Log($"[EventMarks] save to {p} failed: {e.Message}"); }
            }
        }
        catch (Exception e) { Log($"[EventMarks] save failed: {e.Message}"); }
    }

    // ── Floor teleport (folded in from DungeonStepNav) ──
    private const ushort SC_F = 0x21;
    private static readonly unsafe byte** _flagBitmapPtr = (byte**)0x1451FF7A0L;
    private static readonly (int major, int minor, string name, int flagId)[] _floors =
    {
        (23, 1, "Yukiko's Castle, Gate", 6707),
        (40, 0, "Yukiko's Castle 1F",    6708),
        (23, 3, "Yukiko's Castle 2F",    6709),
    };

    private enum Kind { Door, Chest }

    private struct Entry
    {
        public string Say;
        public float Dist;
        public int FloorDir;   // 0 = not a floor; -1 / +1 = previous / next floor
        public string Label;   // short noun for the route briefing ("Chest", "Stairs")
        public bool HasPos;    // TX/TZ valid → entry is routable
        public float TX, TZ;   // world target for the route briefing / auto-walk
        public float NX, NZ;   // door normal (walk-through direction); (0,0) = none
    }

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _minusWas, _plusWas, _lbWas, _rbWas, _bsWas, _f7Was, _f6Was;

    private int _catIndex = -1;      // -1 = nothing selected yet this visit
    private bool _inDungeonLast;
    private bool _inLobbyLast;
    private List<Entry> _entries = new();
    private int _cursor;
    private bool _bkWas;

    // ── Door OPEN self-marking (2026-07-02) ──
    // A door is marked OPEN the moment the player (or the auto-walker) actually CROSSES
    // it — detected by a sign-flip of the player's perpendicular offset from the door's
    // plane (facing from TryDoorAxis), so any orientation works and a parallel walk-by
    // or a never-crossed LOCKED door never marks. Marks are per-FLOOR, in-memory, cleared
    // on a floor change (procedural floors regenerate). Consumed by the browser ("Open
    // door") + the auto-walker (skip the open-tap). Validated as a spike 2026-07-01.
    private readonly Dictionary<int, sbyte> _doorSide = new();          // door key → last side (-1/+1)
    private static readonly List<(float x, float z)> _openDoors = new(); // doors crossed = OPEN, this floor
    private int _passFloorKey = int.MinValue;
    private long _lastDoorScanMs;   // throttle the per-tick scene walk (crash-safety near transitions)
    private const float PassNearUnits = 600f;   // only track when within ~2.5 steps of the door
    private const float PassDeadzone  = 50f;    // ignore the ±50u band right on the door plane

    /// <summary>True if a door at (x,z) has been marked open this floor (matched within
    /// <see cref="DedupUnits"/> so the browser's clustered center and the crossing point agree).</summary>
    internal static bool IsDoorOpenMarked(float x, float z)
    {
        foreach (var (ox, oz) in _openDoors)
            if (MathF.Abs(ox - x) < DedupUnits && MathF.Abs(oz - z) < DedupUnits) return true;
        return false;
    }
    private static int DoorKey(float x, float z) => (int)MathF.Round(x / 200f) * 100003 + (int)MathF.Round(z / 200f);

    // ── Current selection target (for NavBeacon, Feature 3) ──
    // The world XZ of the highlighted browser entry, updated whenever the cursor moves.
    // NavBeacon reads it (live-follows the selection) to beacon whatever you're browsing.
    private static volatile bool _selHasPos;
    private static volatile bool _selIsShadow;   // shadows MOVE → NavBeacon live-tracks them
    private static float _selX, _selZ;
    internal static bool TryGetSelectionTarget(out float x, out float z, out bool isShadow)
    {
        x = _selX; z = _selZ; isShadow = _selIsShadow; return _selHasPos;
    }
    private void UpdateSelectionTarget()
    {
        if (_catIndex >= 0 && _cursor >= 0 && _cursor < _entries.Count && _entries[_cursor].HasPos)
        {
            _selX = _entries[_cursor].TX; _selZ = _entries[_cursor].TZ;
            _selIsShadow = _entries[_cursor].Label == "Shadow";
            _selHasPos = true;
        }
        else _selHasPos = false;
    }

    public DungeonNav()
    {
        LoadMarks();
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "DungeonNav" };
        _thread.Start();
        Log("[DungeonNav] ready (-/= category Doors/Chests/Shadows/Exits [+Interactables in lobby only], [ ] step entries live, \\ act, Backspace walk)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[DungeonNav] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!Utils.GameHasFocus()) return;   // don't process hotkeys while alt-tabbed
        if (CommandMenus.PlayerMenu.IsMenuOpen) return;   // don't fire nav keys behind the camp menu
        bool inDungeon = InDungeon();
        if (inDungeon != _inDungeonLast)
        {
            _catIndex = -1;
            _entries = new();
            _cursor = 0;
            _inDungeonLast = inDungeon;
        }
        if (!inDungeon) return;

        // Places (Interactables) is lobby-only; crossing the lobby↔floor boundary
        // changes the active category set's size, so reset the selection to avoid a
        // stale out-of-range index.
        bool lobby = InLobby();
        if (lobby != _inLobbyLast) { _catIndex = -1; _entries = new(); _cursor = 0; _inLobbyLast = lobby; }

        DetectDoorPassThrough();   // SPIKE A: announce when the player crosses a door

        bool minus = IsKeyDown(VK_OEM_MINUS);
        if (minus && !_minusWas) CycleCategory(-1);
        _minusWas = minus;

        bool plus = IsKeyDown(VK_OEM_PLUS);
        if (plus && !_plusWas) CycleCategory(+1);
        _plusWas = plus;

        bool shift = IsKeyDown(0x10);   // Shift = speech-history modifier (HistoryKeys); don't step entries

        bool lb = IsKeyDown(VK_OEM_4);
        if (lb && !_lbWas && !shift) StepEntry(-1);
        _lbWas = lb;

        bool rb = IsKeyDown(VK_OEM_6);
        if (rb && !_rbWas && !shift) StepEntry(+1);
        _rbWas = rb;

        bool bs = IsKeyDown(VK_OEM_5);
        if (bs && !_bsWas) Act();
        _bsWas = bs;

        // Backspace: cancel a running walk; else if the H cursor is on, walk to
        // the cursor cell (mark & walk — lets the player go anywhere they've
        // mapped, not just a door/chest); else auto-walk the browser selection.
        bool bk = IsKeyDown(VK_BACK);
        if (bk && !_bkWas)
        {
            if (AutoWalk.AutoWalker.IsActive) AutoWalk.AutoWalker.Cancel();
            else if (DungeonCursor.IsActive
                     && DungeonCursor.TryGetMarkTarget(out float mwx, out float mwz, out bool mUnexplored))
            {
                AutoWalk.AutoWalker.Start(mUnexplored ? "Unmapped marker" : "Marker", mwx, mwz);
                DungeonCursor.Deactivate();   // cursor's job is done; re-press H to remap at the new spot
            }
            else StartWalk();
        }
        _bkWas = bk;

        // Event-mark AUTHORING keys — RE-BOUND 2026-07-02 (user request: record marks while
        // playing through the 3rd dungeon). Ctrl-guarded so normal play can't trip them.
        //   Ctrl+F7        = record a "Mark" at the player's spot on this floor
        //   Ctrl+Shift+F7  = undo the most recent mark on this floor
        //   Ctrl+F6        = promote the selected browser entry to a named Event mark
        // (Marks save to BOTH the mod folder and database/ — SaveMarks dual-write.)
        // NOTE: comment these out before building a public Release (dev-key policy — v1.2.0+
        // ship without them; the user keeps them in the dev build to author marks while playing).
        bool ctrl = IsKeyDown(0x11);
        bool f7 = ctrl && IsKeyDown(0x76);
        if (f7 && !_f7Was) { if (IsKeyDown(0x10)) UndoMark(); else RecordMark(); }
        _f7Was = f7;

        bool f6 = ctrl && IsKeyDown(0x75);
        if (f6 && !_f6Was) PromoteSelectionToEvents();
        _f6Was = f6;
    }

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220;   // dungeon floors 20-69; battles (220-299) excluded
    }

    private void CycleCategory(int dir)
    {
        int n = Categories.Length;
        _catIndex = _catIndex < 0 ? (dir > 0 ? 0 : n - 1) : (_catIndex + dir + n) % n;
        _cursor = 0;
        Cat cat = Categories[_catIndex];
        _entries = BuildCategory(cat);
        Log($"[DungeonNav] category → {cat} entries={_entries.Count}");
        if (cat == Cat.Chests) LogChestArray();
        if (cat == Cat.Places) LogMasterTable();
        // STAIRS EMPTY diagnostic: a new dungeon's stairs sprite id is unknown until
        // dumped. When Stairs comes up empty on a gridded floor, log the minimap
        // sprites so the value can be added to GridRouter.IsStairSprite (stand on the
        // stairs first). See memory/bathhouse_dungeon.md.
        if (cat == Cat.Exits && _entries.Count == 0) LogStairSpriteDump();

        string name = CatName(cat);
        if (_entries.Count == 0)
        {
            Speech.Say(cat switch
            {
                Cat.Doors => "Doors: none nearby.",
                Cat.AllDoors => "All doors: none on this floor.",
                Cat.Chests => "Chests: none nearby.",
                Cat.Shadows => "Shadows: none nearby.",
                Cat.Exits => "Stairs: none found on this floor.",
                Cat.Places => "Interactables: none nearby.",
                Cat.Events => "Events: none on this floor.",
                _ => $"{name}: none."
            }, true);
            return;
        }
        UpdateSelectionTarget();
        Speech.Say($"{name}: {_entries.Count}. {_entries[0].Say}.", true);
        WinBeep(1100, 30);
    }

    private void StepEntry(int dir)
    {
        if (_catIndex < 0)
        {
            Speech.Say("Pick a category with minus or equals first.", true);
            return;
        }
        // LIVE: rebuild from the current player position every step.
        Cat cat = Categories[_catIndex];
        _entries = BuildCategory(cat);

        if (_entries.Count == 0)
        {
            Speech.Say($"{CatName(cat)}: none nearby.", true);
            return;
        }

        int next = _cursor + dir;
        if (next < 0) { Speech.Say("First. ", false); next = 0; }
        else if (next >= _entries.Count) { Speech.Say("Last. ", false); next = _entries.Count - 1; }
        _cursor = next;

        UpdateSelectionTarget();
        // Interactables: NAME first (better readability, user 2026-07-02); other categories keep "N of M: …".
        Speech.Say(cat == Cat.Places
            ? $"{_entries[_cursor].Say}. {_cursor + 1} of {_entries.Count}."
            : $"{_cursor + 1} of {_entries.Count}: {_entries[_cursor].Say}.", true);
        WinBeep(1000, 25);
    }

    // Reverted to pre-auto-walk behaviour (user call 2026-06-11): \ teleports
    // on Exits and re-announces everything else. Walking moved to Backspace.
    private void Act()
    {
        if (_catIndex < 0) { Speech.Say("Pick a category first.", true); return; }
        Cat cat = Categories[_catIndex];
        // Refresh so we act on a current selection.
        _entries = BuildCategory(cat);
        if (_entries.Count == 0) { Speech.Say("Nothing selected.", true); return; }
        if (_cursor >= _entries.Count) _cursor = _entries.Count - 1;

        var e = _entries[_cursor];
        // \ just re-announces the selection — no keybind hint (it names keyboard keys, which is noise
        // for controller players and messy once both input schemes are covered; user 2026-07-02).
        Speech.Say($"{e.Say}.", true);
    }

    private void StartWalk()
    {
        if (_catIndex < 0) { Speech.Say("Pick a category first.", true); return; }
        Cat cat = Categories[_catIndex];
        _entries = BuildCategory(cat);
        if (_entries.Count == 0) { Speech.Say("Nothing selected.", true); return; }
        if (_cursor >= _entries.Count) _cursor = _entries.Count - 1;

        var e = _entries[_cursor];
        if (cat == Cat.Shadows)
        {
            // Original position-gated hunt (user call 2026-06-23 to restore, same as chests):
            // the player walks close themselves; Backspace strikes when already behind it (the
            // browser announces each shadow's facing). The explore/travel version is retired.
            if (!e.HasPos) { Speech.Say("No shadow position.", true); return; }
            AutoWalk.AutoWalker.StartHunt(e.TX, e.TZ);
            return;
        }
        if (cat == Cat.Exits)   // Stairs: full travel (explore + route + open doors)
        {
            AutoWalk.AutoWalker.TravelToStairs();
            return;
        }
        if (cat == Cat.Events && IsFloorJumpLabel(e.Label, out int jdir))   // placed "Next/Previous floor" event
        {
            TeleportRelativeFloor(jdir);
            return;
        }
        if (cat == Cat.Events)  // Event marks: full travel too — the one-shot Walk can't reach a
        {                       // tricky scripted-floor mark (e.g. a winding-path door). Chain marks
            if (!e.HasPos) { Speech.Say("No position to walk to.", true); return; }   // for a winding route.
            AutoWalk.AutoWalker.TravelTo(e.Label, e.TX, e.TZ);
            return;
        }
        if (!e.HasPos)
        {
            Speech.Say("No position to walk to.", true);
            return;
        }

        // Chests use the original one-shot walker (the explore/travel version hugged walls
        // on the coarse minimap — user call 2026-06-23 to restore the old chest hunter).

        // Doors: target the door's RAW XZ. The minimap cell is ~5 steps wide
        // but a doorway is narrow, so snapping to a walkable cell centre lands
        // BESIDE the door on the wall (live 2026-06-11: 13900,12000 snapped to
        // 14067,11667 — the next cell over). AutoWalker's door-push handles
        // the narrow final approach (walk straight in + tap open, no
        // wall-blocked abort).
        AutoWalk.AutoWalker.Start(e.Label, e.TX, e.TZ);
    }

    // ── Event marks (record / undo) ──

    // F7: drop a generic ("Mark") event point at the player's current spot on this
    // floor. The label is named later by hand (Claude edits dungeon_waypoints.json).
    private void RecordMark()
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) { Speech.Say("Position unavailable.", true); return; }
        string key = FloorKey();
        lock (_marksLock)
        {
            if (!_marks.TryGetValue(key, out var l)) { l = new(); _marks[key] = l; }
            l.Add(new EventMark { X = px, Z = pz, Label = "Mark" });
        }
        SaveMarks();
        Speech.Say("Recorded a mark on this floor.", true);
        Log($"[EventMarks] RECORD floor={key} pos=({px:F0},{pz:F0})");
    }

    // F6: copy the CURRENTLY-SELECTED browser entry (a detected door / chest / stairs)
    // into Events as a permanent named mark for this floor. Lets us capture an existing
    // door's exact coords (e.g. Castle 5F's event door) without standing on it. Label is
    // the entry's generic noun ("Door") until renamed by hand.
    private void PromoteSelectionToEvents()
    {
        if (_catIndex < 0) { Speech.Say("Pick a category and an entry first.", true); return; }
        Cat cat = Categories[_catIndex];
        if (cat == Cat.Events) { Speech.Say("That is already an event mark.", true); return; }
        _entries = BuildCategory(cat);
        if (_entries.Count == 0) { Speech.Say("Nothing selected.", true); return; }
        if (_cursor >= _entries.Count) _cursor = _entries.Count - 1;
        var e = _entries[_cursor];
        if (!e.HasPos) { Speech.Say("That entry has no position to save.", true); return; }
        string key = FloorKey();
        lock (_marksLock)
        {
            if (!_marks.TryGetValue(key, out var l)) { l = new(); _marks[key] = l; }
            l.Add(new EventMark { X = e.TX, Z = e.TZ, Label = e.Label });
        }
        SaveMarks();
        Speech.Say($"Added {e.Label} to events on this floor.", true);
        Log($"[EventMarks] PROMOTE floor={key} label={e.Label} pos=({e.TX:F0},{e.TZ:F0})");
    }

    // Shift+F7: remove the most-recently recorded mark on this floor (recording by ear).
    private void UndoMark()
    {
        string key = FloorKey();
        bool removed = false;
        lock (_marksLock)
            if (_marks.TryGetValue(key, out var l) && l.Count > 0)
            {
                l.RemoveAt(l.Count - 1);
                if (l.Count == 0) _marks.Remove(key);
                removed = true;
            }
        if (removed) { SaveMarks(); Speech.Say("Removed the last mark.", true); Log($"[EventMarks] UNDO floor={key}"); }
        else Speech.Say("No marks to remove.", true);
    }

    // ── Category builders ──

    private List<Entry> BuildCategory(Cat cat) => cat switch
    {
        Cat.Doors => BuildInteractableEntries(Kind.Door, "Door"),
        Cat.AllDoors => BuildInteractableEntries(Kind.Door, "Door", activeOnly: false),
        // Chests from the dedicated treasure array (not the noisy scene singles).
        Cat.Chests => BuildChestEntries(),
        Cat.Shadows => BuildShadowEntries(),
        Cat.Exits => BuildExitEntries(),
        Cat.Places => BuildPlaceEntries(),
        Cat.Events => BuildEventEntries(),
        _ => new()
    };

    // Event marks for the CURRENT floor, nearest-first, as routable entries. The
    // label is whatever was authored ("Stairs", "Event door"); Backspace walks
    // there via the generic AutoWalker.Start path in StartWalk (HasPos = true).
    private List<Entry> BuildEventEntries()
    {
        var list = new List<Entry>();
        List<EventMark> marks;
        lock (_marksLock) marks = _marks.TryGetValue(FloorKey(), out var l) ? new List<EventMark>(l) : new();
        if (marks.Count == 0) return list;

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        bool havePos = !float.IsNaN(px) && !float.IsNaN(pz);
        if (havePos)
            marks.Sort((a, b) =>
            {
                float da = (a.X - px) * (a.X - px) + (a.Z - pz) * (a.Z - pz);
                float db = (b.X - px) * (b.X - px) + (b.Z - pz) * (b.Z - pz);
                return da.CompareTo(db);
            });

        foreach (var m in marks)
        {
            // Floor-jump marks ("Next floor"/"Previous floor") are teleports, not walk targets — no
            // distance/steps; Backspace teleports (see the Events dispatch in StartWalk).
            if (IsFloorJumpLabel(m.Label, out _))
            {
                list.Add(new Entry { Say = m.Label, Dist = 1e9f, FloorDir = 0, Label = m.Label, HasPos = true, TX = m.X, TZ = m.Z });
                continue;
            }
            float dist = havePos ? MathF.Sqrt((m.X - px) * (m.X - px) + (m.Z - pz) * (m.Z - pz)) : 0;
            int steps = AutoWalk.RouteSpeech.StepsFromUnits(dist);
            list.Add(new Entry
            {
                Say = $"{m.Label}, {steps} step{(steps == 1 ? "" : "s")}",
                Dist = dist, FloorDir = 0, Label = m.Label,
                HasPos = true, TX = m.X, TZ = m.Z
            });
        }
        return list;
    }

    // STAIRS category (rebuilt 2026-06-17 — replaces the old teleport-based
    // "Exits"). Every entry is an actual stairs POSITION you auto-walk to with
    // Backspace; the floor transitions on contact (no teleport). Stair source:
    //   • mapped floors  → the minimap stairs sprite (GridRouter).
    //   • scripted / no-grid floors (e.g. Yukiko 2F) → the scene-actor pairs
    //     (same source as Doors — these floors have no real doors, only the
    //     forward/back stairs). The floor-teleport machinery is kept but
    //     UNBOUND (see TeleportFloor + database/DUNGEON_TELEPORT.md).
    private List<Entry> BuildExitEntries()
    {
        var list = new List<Entry>();
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        bool havePos = !float.IsNaN(px) && !float.IsNaN(pz);

        var pts = new List<(float x, float z)>();
        if (AutoWalk.GridRouter.HasGrid())
        {
            if (havePos && AutoWalk.GridRouter.FindNearestStairs(px, pz, out float sx, out float sz))
                pts.Add((sx, sz));
        }
        else
        {
            // No minimap grid: the stairs are the scene-actor pairs.
            pts.AddRange(Doors());
        }

        // De-dupe co-located hits, then sort nearest-first.
        var uniq = new List<(float x, float z)>();
        foreach (var p in pts)
        {
            bool dup = false;
            foreach (var u in uniq)
                if (MathF.Abs(u.x - p.x) < DedupUnits && MathF.Abs(u.z - p.z) < DedupUnits) { dup = true; break; }
            if (!dup) uniq.Add(p);
        }
        if (havePos)
            uniq.Sort((a, b) =>
            {
                float da = (a.x - px) * (a.x - px) + (a.z - pz) * (a.z - pz);
                float db = (b.x - px) * (b.x - px) + (b.z - pz) * (b.z - pz);
                return da.CompareTo(db);
            });

        for (int i = 0; i < uniq.Count; i++)
        {
            var (x, z) = uniq[i];
            float dist = havePos ? MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)) : 0;
            int steps = AutoWalk.RouteSpeech.StepsFromUnits(dist);
            string say = uniq.Count > 1 ? $"Stairs {i + 1}, {steps} steps" : $"Stairs, {steps} steps";
            list.Add(new Entry { Say = say, Dist = dist, FloorDir = 0,
                                 Label = "Stairs", HasPos = true, TX = x, TZ = z });
        }

        // (Floor-jump teleport is NOT a global Exits option — teleporting skips a floor's STORY
        //  scripts, so it's only offered as a PLACED "Next floor"/"Previous floor" Event mark on the
        //  specific floors where we want it (e.g. Bath #3). See BuildEventEntries / the Events dispatch.)
        return list;
    }

    private List<Entry> BuildChestEntries()
    {
        var list = new List<Entry>();
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return list;

        foreach (var (x, z) in EnumerateChests())
        {
            float dx = x - px, dz = z - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            int steps = Math.Max(1, (int)MathF.Round(dist / WorldPerStep));
            string dir = WorldDirection(dx, dz);   // fixed compass (same as the H cursor)
            list.Add(new Entry { Say = $"Chest {dir}, {steps} step{(steps == 1 ? "" : "s")}", Dist = dist,
                                 Label = "Chest", HasPos = true, TX = x, TZ = z });
        }
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return list;
    }

    private List<Entry> BuildShadowEntries()
    {
        var list = new List<Entry>();
        if (!ReadFloorGate()) return list;
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return list;

        foreach (var (x, z, sfx, sfz) in EnumerateShadows())
        {
            float dx = x - px, dz = z - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            int steps = Math.Max(1, (int)MathF.Round(dist / WorldPerStep));
            string dir = WorldDirection(dx, dz);   // fixed compass (same as the H cursor)
            // Shadow forward · (player − shadow): >0 faces you (danger), <0 faces
            // away (hit it from behind for the advantage). (px−x, pz−z) = dx,dz.
            string face = "";
            if (sfx != 0 || sfz != 0)
            {
                // Shadow forward · (shadow → player). >0 ⇒ it's looking at you
                // (danger / face-to-face); <0 ⇒ facing away (hit from behind for
                // the advantage). dx,dz = shadow − player, so → player = −dx,−dz.
                float fdot = sfx * (-dx) + sfz * (-dz);
                face = fdot > 0 ? ", facing you" : ", facing away";
            }
            list.Add(new Entry { Say = $"Shadow {dir}, {steps} step{(steps == 1 ? "" : "s")}{face}", Dist = dist,
                                 Label = "Shadow", HasPos = true, TX = x, TZ = z });
        }
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return list;
    }

    private List<Entry> BuildInteractableEntries(Kind want, string noun, bool activeOnly = true)
    {
        var list = new List<Entry>();
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return list;

        foreach (var (x, z, kind, count, nx, nz) in EnumerateInteractables(activeOnly))
        {
            if (kind != want) continue;
            float dx = x - px, dz = z - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            int steps = Math.Max(1, (int)MathF.Round(dist / WorldPerStep));
            string dir = WorldDirection(dx, dz);   // fixed compass (same as the H cursor)
            // A door the player/auto-walker has walked THROUGH reads "Open door".
            string label = (want == Kind.Door && IsDoorOpenMarked(x, z)) ? "Open door" : noun;
            list.Add(new Entry { Say = $"{label} {dir}, {steps} step{(steps == 1 ? "" : "s")}", Dist = dist,
                                 Label = label, HasPos = true, TX = x, TZ = z, NX = nx, NZ = nz });
        }
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return list;
    }

    // (Door open/closed label REMOVED 2026-06-27 — the minimap-explore heuristic wasn't reliable
    //  enough in play. The minimap does NOT encode door state on the door's own cell; the only direct
    //  signal is the door actor animation pose at node+0x300. See memory/bathhouse_dungeon.md.)

    /// <summary>
    /// Places = the interactable master table (save point, dungeon ENTRANCES,
    /// NPCs in the lobby) — the data the scene-actor door/chest pass misses.
    /// Generic for now ("Object N steps DIR"); the diagnostic log on selection
    /// captures cat/id so we can assign friendly names (Yukiko's Castle, Save
    /// Point, NPC names) next. Every entry is auto-walkable via Backspace.
    /// </summary>
    private List<Entry> BuildPlaceEntries()
    {
        var list = new List<Entry>();
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return list;

        // On a dungeon FLOOR (not the lobby) the master table is mostly garbage; only cat=9 (the
        // walk-up NPCs — benched party member + Fox) is real, so restrict to it there. The lobby keeps
        // the full list (named NPCs + save point).
        bool floorOnly = !InLobby();

        foreach (var (x, z, cat, id, node) in EnumeratePlaces())
        {
            if (floorOnly && cat != 9) continue;   // dungeon floors: only the cat=9 walk-up NPCs
            float dx = x - px, dz = z - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            // Skip the player's own node (it sits exactly at the player — e.g.
            // the lobby's cat=9 id=0x0102 read at d=0). Not a walk target.
            if (dist < 120f) continue;
            int steps = Math.Max(1, (int)MathF.Round(dist / WorldPerStep));
            string dir = WorldDirection(dx, dz);   // fixed compass (same as the H cursor)
            string label = PlaceLabel(cat, id, node);
            list.Add(new Entry { Say = $"{label} {dir}, {steps} step{(steps == 1 ? "" : "s")}", Dist = dist,
                                 Label = label, HasPos = true, TX = x, TZ = z });
        }
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return list;
    }

    // cat=5 master-table id → display name for lobby NPCs. The live model-path
    // resolver returns nothing on these nodes (verified 2026-06-17), so names
    // are bound by id. Filled from the user's id→name report.
    private static readonly Dictionary<int, string> PlaceNpcNames = new()
    {
        // Yukiko's Castle lobby NPCs, user-mapped 2026-06-17. Teddie = the
        // dungeon entrance (talk to him to go in).
        [0x00C8] = "Yosuke",   // 200
        [0x0320] = "Teddie",   // 800
        [0x012C] = "Chie",     // 300
        [0x0190] = "Yukiko",   // 400 — added when she joined (user-mapped)
        [0x0258] = "Kanji",    // 600 — TV-world entrance (user-mapped 2026-07-02, "Person 600")
        [0x0384] = "Fox",      // 900 — TV-world entrance (SP heal NPC)
    };

    /// <summary>
    /// Friendly label for a Places entry. Try the live NPC model-path resolver
    /// first; it doesn't resolve on master-table nodes, so for people
    /// (cat=5) fall back to the id→name bind table — and, until that's filled,
    /// SPEAK the id ("Person 300") so the user can map id→name by walking to
    /// each. cat=4 (surviving the marker filter) = the save point
    /// (user-confirmed 2026-06-17; talking to Teddie here enters the dungeon).
    /// </summary>
    // cat=9 walk-up NPCs on DUNGEON FLOORS. VERIFIED 2026-07-02: the id's HIGH byte = the party char id
    // (Yosuke 0x020A → 0x02 = char 2 = Yosuke), so benched members name themselves via CharNames. The Fox
    // (0x120A, high byte 0x12) isn't a party id → bound explicitly here.
    private static readonly Dictionary<int, string> DungeonNpcNames = new()
    {
        [0x120A] = "Fox",   // SP-heal fox (user-confirmed)
    };
    // Party char id (id high byte) → name; index = char id (1=protagonist … 8=Teddie).
    private static readonly string[] DungeonCharNames =
        { "?", "You", "Yosuke", "Chie", "Yukiko", "Rise", "Kanji", "Naoto", "Teddie" };

    private static string PlaceLabel(int cat, short id, nint node)
    {
        string? npc = OverworldNav.ResolveNpcModelName(node);
        if (!string.IsNullOrEmpty(npc)) return npc;
        if (cat == 5)
            return PlaceNpcNames.TryGetValue(id, out var nm) ? nm : $"Person {(ushort)id}";
        if (cat == 9)   // dungeon-floor walk-up NPC (benched member / Fox)
        {
            if (DungeonNpcNames.TryGetValue((ushort)id, out var nm9)) return nm9;
            int charId = ((ushort)id >> 8) & 0xFF;   // high byte = party char id (benched member)
            if (charId >= 1 && charId < DungeonCharNames.Length) return DungeonCharNames[charId];
            return $"Person {(ushort)id}";
        }
        if (cat == 4) return "Save point";
        return "Interactable";
    }

    // ── Raw enumerations ──

    // Shared with DungeonCursor so the grid cursor reads the same accurate data.
    internal static List<(float x, float z)> Doors()
    {
        var outp = new List<(float, float)>();
        foreach (var (x, z, kind, _, _, _) in EnumerateInteractables())
            if (kind == Kind.Door) outp.Add((x, z));
        return outp;
    }

    /// <summary>Mark a door OPEN when the player walks THROUGH it. A door has a facing
    /// (its transform normal via <see cref="TryDoorAxis"/>), which splits space into
    /// in-front / behind; a genuine crossing flips the player's side while close. Works
    /// for any door orientation; a parallel walk-by or a never-crossed (locked) door does
    /// NOT fire. First crossing of a door → add to <see cref="_openDoors"/> + a one-time
    /// "Door open" confirmation; later crossings are silent.</summary>
    private void DetectDoorPassThrough()
    {
        // CRASH-SAFETY: the hub (major 20) has no walk-through doors, so never walk its scene; and for
        // ~1.5s after ANY area change the scene is still being (re)built — walking it then can hit a
        // freed page and hard-crash (the Entrance-load + Velvet-Room crashes). Back off in both cases.
        int major = FieldTracker.CurrentMajor;
        if (major <= 20) return;
        if (Environment.TickCount64 - FieldTracker.LastAreaChangeMs < 1500) return;

        int floorKey = major * 1000 + FieldTracker.CurrentMinor;
        if (floorKey != _passFloorKey)   // new floor → forget stale sides + marks
        {
            _doorSide.Clear(); _openDoors.Clear(); _passFloorKey = floorKey;
        }

        // Throttle the SCENE WALK to ~130ms (not every 50ms tick): a door crossing lasts several
        // hundred ms so it's still caught, and it cuts how often we walk the scene list.
        long now = Environment.TickCount64;
        if (now - _lastDoorScanMs < 130) return;
        _lastDoorScanMs = now;

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return;   // NaN = mid-transition → don't walk the scene

        foreach (var (dx, dz) in Doors())
        {
            float ddx = px - dx, ddz = pz - dz;
            float dist2 = ddx * ddx + ddz * ddz;
            int key = DoorKey(dx, dz);

            if (dist2 > PassNearUnits * PassNearUnits) { _doorSide.Remove(key); continue; }
            if (!TryDoorAxis(dx, dz, out float nx, out float nz)) continue;
            float nlen = MathF.Sqrt(nx * nx + nz * nz);
            if (nlen < 1e-3f) continue;

            // signed perpendicular offset of the player from the door plane
            float perp = (ddx * nx + ddz * nz) / nlen;
            sbyte side = perp > PassDeadzone ? (sbyte)1 : perp < -PassDeadzone ? (sbyte)-1 : (sbyte)0;
            if (side == 0) continue;   // on the door line — wait for a definite side

            if (_doorSide.TryGetValue(key, out sbyte prev) && prev != 0 && prev != side
                && !IsDoorOpenMarked(dx, dz))
            {
                _openDoors.Add((dx, dz));   // silent: the browser reflects it as "Open door"
                Log($"[DoorMark] open door @({dx:F0},{dz:F0}) normal=({nx:F2},{nz:F2}) side {prev}->{side}");
            }
            _doorSide[key] = side;
        }
    }

    /// <summary>Auto-walk hook: mark a door open by position (used when the walker
    /// successfully threads THROUGH one — same signal as a manual crossing).</summary>
    internal static void MarkDoorOpen(float x, float z)
    {
        if (!IsDoorOpenMarked(x, z)) _openDoors.Add((x, z));
    }

    /// <summary>
    /// The PASSAGE AXIS (door normal, XZ) for the door nearest (x,z) — the way you walk THROUGH it —
    /// read from its scene-actor TRANSFORM (a 4×4 at xf+0x330; row 2 @+0x350 = the forward/Z-basis =
    /// the door normal). This is the exact orientation, far more reliable than guessing the axis from
    /// the approach direction (which fails on diagonal approaches — the narrow Bathhouse doors).
    /// Verified 2026-06-27 on Bathhouse door @(9000,13200): forward=(-1,0,0) ⇒ pass along ±X, matching
    /// the player's side. Returns false if no active node sits within ~250u of (x,z).
    /// </summary>
    internal static unsafe bool TryDoorAxis(float x, float z, out float ax, out float az)
    {
        ax = az = 0;
        if (!IsReadable(SceneRootPtr, 8)) return false;
        nint sceneRoot = *(nint*)SceneRootPtr;
        if (sceneRoot == 0 || !IsReadable(sceneRoot + OFF_SCENE, 8)) return false;
        nint scene = *(nint*)(sceneRoot + OFF_SCENE);
        if (scene == 0 || !IsReadable(scene + OFF_LIST_HEAD, 8)) return false;
        nint node = *(nint*)(scene + OFF_LIST_HEAD);
        int guard = 0; float bestD = float.MaxValue, bfx = 0, bfz = 0; bool found = false;
        while (node != 0 && guard++ < 4096)
        {
            if (!IsReadable(node + OFF_NODE_ACTIVE, 1)) break;
            if ((*(byte*)(node + OFF_NODE_ACTIVE) & 2) != 0 && IsReadable(node + OFF_NODE_XFORM, 8))
            {
                nint xf = *(nint*)(node + OFF_NODE_XFORM);
                if (xf != 0 && IsReadable(xf + 0x358, 4))
                {
                    float dx = *(float*)(xf + OFF_ACTOR_X) - x, dz = *(float*)(xf + OFF_ACTOR_Z) - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        bfx = *(float*)(xf + 0x350);   // forward/Z-basis .x  (door normal)
                        bfz = *(float*)(xf + 0x358);   // forward/Z-basis .z
                        found = true;
                    }
                }
            }
            if (!IsReadable(node + OFF_NODE_NEXT, 8)) break;
            node = *(nint*)(node + OFF_NODE_NEXT);
        }
        if (!found || bestD > 250f * 250f) return false;
        float m = MathF.Sqrt(bfx * bfx + bfz * bfz);
        if (!float.IsFinite(m) || m < 0.1f) return false;
        ax = bfx / m; az = bfz / m;
        return true;
    }
    internal static List<(float x, float z)> Chests() => EnumerateChests();
    internal static List<(float x, float z)> Shadows()
    {
        var outp = new List<(float x, float z)>();
        if (!ReadFloorGate()) return outp;
        foreach (var (x, z, _, _) in EnumerateShadows()) outp.Add((x, z));
        return outp;
    }
    /// <summary>Shadows with their forward vector (fx,fz) for facing checks.</summary>
    internal static List<(float x, float z, float fx, float fz)> ShadowsWithFacing()
        => ReadFloorGate() ? EnumerateShadows() : new();

    private static unsafe List<(float x, float z)> EnumerateChests()
    {
        var outp = new List<(float, float)>();
        for (int i = 0; i < CHEST_COUNT; i++)
        {
            nint e = ChestArray + i * CHEST_STRIDE;
            if (!IsReadable(e + OFF_CHEST_Z, 4)) continue;
            if (*(int*)(e + OFF_CHEST_ACTIVE) == 0) continue;
            // +0x08 state: 0 = unopened. It flips when the chest is opened, so
            // skip those — an opened chest must not keep showing in nav/beacon.
            if (*(int*)(e + OFF_CHEST_STATE) == 1) continue;
            float x = *(float*)(e + OFF_CHEST_X), z = *(float*)(e + OFF_CHEST_Z);
            if (float.IsFinite(x) && float.IsFinite(z) && (x != 0 || z != 0)) outp.Add((x, z));
        }
        return outp;
    }

    private static unsafe void LogChestArray()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < CHEST_COUNT; i++)
        {
            nint e = ChestArray + i * CHEST_STRIDE;
            if (!IsReadable(e + OFF_CHEST_Z, 4)) { sb.Append($" [{i} unreadable]"); continue; }
            int act = *(int*)(e + OFF_CHEST_ACTIVE);
            int st = *(int*)(e + OFF_CHEST_STATE);
            int b04 = *(int*)(e + 0x04), b0c = *(int*)(e + 0x0C), b10 = *(int*)(e + 0x10);
            float x = *(float*)(e + OFF_CHEST_X), z = *(float*)(e + OFF_CHEST_Z);
            sb.Append($" [{i} act={act} st={st} +4={b04} +C={b0c} +10={b10} @({x:F0},{z:F0})]");
        }
        Log($"[ChestArray]{sb}");
    }

    private static unsafe List<(float x, float z, float fx, float fz)> EnumerateShadows()
    {
        var outp = new List<(float, float, float, float)>();
        for (int i = 0; i < SLOT_COUNT; i++)
        {
            nint e = ShadowArray + i * SLOT_STRIDE;
            if (!IsReadable(e + OFF_ACTIVE2, 8)) continue;
            if (*(nint*)(e + OFF_ACTIVE1) == 0 || *(nint*)(e + OFF_ACTIVE2) == 0) continue;
            nint actor = *(nint*)(e + OFF_ACTOR);
            if (actor == 0 || !IsReadable(actor + OFF_ACTOR_Z, 4)) continue;
            float x = *(float*)(actor + OFF_ACTOR_X), z = *(float*)(actor + OFF_ACTOR_Z);
            if (!float.IsFinite(x) || !float.IsFinite(z)) continue;
            float fx = 0f, fz = 0f;
            if (IsReadable(actor + OFF_ACTOR_FZ, 4))
            {
                fx = *(float*)(actor + OFF_ACTOR_FX);
                fz = *(float*)(actor + OFF_ACTOR_FZ);
                if (!float.IsFinite(fx) || !float.IsFinite(fz)) { fx = 0f; fz = 0f; }
            }
            outp.Add((x, z, fx, fz));
        }
        return outp;
    }

    /// <summary>
    /// Walk the scene-actor list, cluster ACTIVE interactable nodes by position,
    /// and classify each cluster: <b>a door is two co-located actors</b> (its
    /// leaves/frame), <b>a chest is one</b>. Verified: confirmed doors always
    /// appear as pairs; chests as singles. Returns one entry per cluster at its
    /// centroid.
    /// </summary>
    /// <param name="activeOnly">true (default) = only the `+0x28 &amp; 2` active/near nodes (the
    /// "Doors" category). false = EVERY node on the loaded floor regardless of the active bit (the
    /// "All doors" category). Inactive nodes may carry stale transforms, so (0,0)/non-finite positions
    /// are skipped either way.</param>
    private static unsafe List<(float x, float z, Kind kind, int count, float nx, float nz)> EnumerateInteractables(bool activeOnly = true)
    {
        // 1. Collect every (active) node's position (no dedup yet).
        var raw = new List<(float x, float z)>();
        if (IsReadable(SceneRootPtr, 8))
        {
            nint sceneRoot = *(nint*)SceneRootPtr;
            if (sceneRoot != 0 && IsReadable(sceneRoot + OFF_SCENE, 8))
            {
                nint scene = *(nint*)(sceneRoot + OFF_SCENE);
                if (scene != 0 && IsReadable(scene + OFF_LIST_HEAD, 8))
                {
                    nint node = *(nint*)(scene + OFF_LIST_HEAD);
                    int guard = 0;
                    while (node != 0 && guard++ < 4096)
                    {
                        if (!IsReadable(node + OFF_NODE_ACTIVE, 1)) break;
                        bool near = (*(byte*)(node + OFF_NODE_ACTIVE) & 2) != 0;
                        if ((near || !activeOnly) && IsReadable(node + OFF_NODE_XFORM, 8))
                        {
                            nint xf = *(nint*)(node + OFF_NODE_XFORM);
                            if (xf != 0 && IsReadable(xf + OFF_ACTOR_Z, 4))
                            {
                                float x = *(float*)(xf + OFF_ACTOR_X), z = *(float*)(xf + OFF_ACTOR_Z);
                                if (float.IsFinite(x) && float.IsFinite(z) && (x != 0f || z != 0f)) raw.Add((x, z));
                            }
                        }
                        if (!IsReadable(node + OFF_NODE_NEXT, 8)) break;
                        node = *(nint*)(node + OFF_NODE_NEXT);
                    }
                }
            }
        }

        // 2. Cluster co-located nodes and count members. Keep the first two
        // member positions per cluster — a door's pair spans its frame, so
        // (m2 − m1) is the door's in-wall AXIS and its perpendicular is the
        // door NORMAL (the direction you walk through it). Auto-walk stages
        // its approach in front of the door along that normal.
        var sumX = new List<float>(); var sumZ = new List<float>(); var cnt = new List<int>();
        var m1x = new List<float>(); var m1z = new List<float>();
        var m2x = new List<float>(); var m2z = new List<float>();
        foreach (var (x, z) in raw)
        {
            int idx = -1;
            for (int i = 0; i < cnt.Count; i++)
                if (MathF.Abs(sumX[i] / cnt[i] - x) < DedupUnits && MathF.Abs(sumZ[i] / cnt[i] - z) < DedupUnits)
                { idx = i; break; }
            if (idx < 0)
            {
                sumX.Add(x); sumZ.Add(z); cnt.Add(1);
                m1x.Add(x); m1z.Add(z); m2x.Add(float.NaN); m2z.Add(float.NaN);
            }
            else
            {
                sumX[idx] += x; sumZ[idx] += z; cnt[idx]++;
                if (float.IsNaN(m2x[idx])) { m2x[idx] = x; m2z[idx] = z; }
            }
        }

        // 3. Classify: 2+ co-located actors = door, 1 = chest. Door normal
        // from the pair axis (zero when degenerate). BUT a "door" cluster sitting on a treasure-array
        // chest IS that chest (some chests are modelled as a co-located node PAIR — Marukyu chest
        // @(16800,26400), verified 2026-07-02) — reclassify it to Chest so it doesn't show as a door.
        var chestPos = ActiveChestPositions();
        var outp = new List<(float, float, Kind, int, float, float)>();
        for (int i = 0; i < cnt.Count; i++)
        {
            float cx = sumX[i] / cnt[i], cz = sumZ[i] / cnt[i];
            Kind kind = cnt[i] >= 2 ? Kind.Door : Kind.Chest;
            if (kind == Kind.Door)
                foreach (var (hx, hz) in chestPos)
                    if (MathF.Abs(hx - cx) < DedupUnits && MathF.Abs(hz - cz) < DedupUnits) { kind = Kind.Chest; break; }

            float nx = 0, nz = 0;
            if (kind == Kind.Door && !float.IsNaN(m2x[i]))
            {
                float ax = m2x[i] - m1x[i], az = m2z[i] - m1z[i];
                float m = MathF.Sqrt(ax * ax + az * az);
                if (m > 1f) { nx = -az / m; nz = ax / m; }
            }
            outp.Add((cx, cz, kind, cnt[i], nx, nz));
        }
        return outp;
    }

    /// <summary>World XZ of every ACTIVE treasure-array chest (opened OR not) — used to keep chests out
    /// of the door list (a chest modelled as a co-located node pair would otherwise read as a door).</summary>
    private static unsafe List<(float x, float z)> ActiveChestPositions()
    {
        var outp = new List<(float, float)>();
        for (int i = 0; i < CHEST_COUNT; i++)
        {
            nint e = ChestArray + i * CHEST_STRIDE;
            if (!IsReadable(e + OFF_CHEST_Z, 4)) continue;
            if (*(int*)(e + OFF_CHEST_ACTIVE) == 0) continue;
            float x = *(float*)(e + OFF_CHEST_X), z = *(float*)(e + OFF_CHEST_Z);
            if (float.IsFinite(x) && float.IsFinite(z) && (x != 0f || z != 0f)) outp.Add((x, z));
        }
        return outp;
    }

    /// <summary>
    /// Walk the interactable master table (0x146248560) and return every real
    /// interactable's world XZ + (cat,id). Each category is a linked list; we
    /// walk cats 0..15 (≥16 = garbage). Filters: the cat=1 id=0x0001 gaze
    /// cursor, the non-actionable marker cats (<see cref="PlaceSkipCats"/>),
    /// zero / non-finite / out-of-range positions, and co-located duplicates.
    /// Read-only, bounded, every dereference IsReadable-guarded — the same
    /// crash-safe pattern as <see cref="EnumerateInteractables"/> and
    /// FieldTracker's master-table dump.
    /// </summary>
    private static unsafe List<(float x, float z, int cat, short id, nint node)> EnumeratePlaces()
    {
        var outp = new List<(float, float, int, short, nint)>();
        if (!IsReadable(MasterTablePtr, MT_MAX_CAT * 8)) return outp;

        for (int cat = 0; cat < MT_MAX_CAT; cat++)
        {
            nint head = *(nint*)(MasterTablePtr + cat * 8);
            if ((ulong)head <= 0x10000UL || !IsReadable(head, 0x1000)) continue;

            nint cur = head;
            int guard = 0;
            while (cur != 0 && guard++ < MT_MAX_PER_CAT && IsReadable(cur, 0x1000))
            {
                short id = *(short*)(cur + OFF_MT_ID);
                uint flags = *(uint*)(cur + OFF_MT_FLAGS);
                float x = *(float*)(cur + OFF_MT_X), z = *(float*)(cur + OFF_MT_Z);

                bool isGaze = cat == 1 && id == 0x0001;          // player-facing cursor, not a place
                bool isMarker = (flags & MT_MARKER_FLAG) != 0;   // template / room-grid row
                // id 0x07D0 (2000) is a generic "person present here" mirror —
                // the SAME id appears at every NPC's spot AND the player. It's
                // enumerated before the real NPC (distinct-id cat=5 row) and,
                // co-located, deduped it out — so every NPC collapsed to its
                // cat=4 label "Save point" (live 2026-06-17). Skip the mirror;
                // the distinct-id row is the real, name-bearing NPC.
                bool isMirror = id == MT_PERSON_MIRROR_ID;
                bool posOk = float.IsFinite(x) && float.IsFinite(z)
                    && (x != 0f || z != 0f)
                    && MathF.Abs(x) < MtPosCeiling && MathF.Abs(z) < MtPosCeiling;
                if (!isGaze && !isMarker && !isMirror && posOk)
                {
                    bool dup = false;
                    foreach (var (ex, ez, _, _, _) in outp)
                        if (MathF.Abs(ex - x) < PlaceDedupUnits && MathF.Abs(ez - z) < PlaceDedupUnits) { dup = true; break; }
                    if (!dup) outp.Add((x, z, cat, id, cur));
                }

                if (!IsReadable(cur + OFF_MT_NEXT, 8)) break;
                cur = *(nint*)(cur + OFF_MT_NEXT);
            }
        }
        return outp;
    }

    /// <summary>
    /// Diagnostic: dump every master-table category/id/flags/position relative
    /// to the player. Logged once per Places-select so we can map the lobby's
    /// rows to friendly names (entrance, save point, NPCs) in a follow-up.
    /// </summary>
    private static unsafe void LogMasterTable()
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        Log($"[Places] === master-table dump  major={FieldTracker.CurrentMajor}/{FieldTracker.CurrentMinor}  player=({px:F0},{pz:F0}) ===");
        if (!IsReadable(MasterTablePtr, MT_MAX_CAT * 8)) { Log("[Places]   table unreadable"); return; }

        int total = 0;
        for (int cat = 0; cat < MT_MAX_CAT; cat++)
        {
            nint head = *(nint*)(MasterTablePtr + cat * 8);
            if ((ulong)head <= 0x10000UL || !IsReadable(head, 0x1000)) continue;
            nint cur = head;
            int guard = 0;
            while (cur != 0 && guard++ < MT_MAX_PER_CAT && IsReadable(cur, 0x1000))
            {
                short id = *(short*)(cur + OFF_MT_ID);
                uint flags = *(uint*)(cur + OFF_MT_FLAGS);
                float x = *(float*)(cur + OFF_MT_X), z = *(float*)(cur + OFF_MT_Z);
                float d = (!float.IsNaN(px) && !float.IsNaN(pz)) ? MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)) : float.NaN;
                bool skipped = (flags & MT_MARKER_FLAG) != 0 || (cat == 1 && id == 0x0001);
                string npc = skipped ? "" : (OverworldNav.ResolveNpcModelName(cur) ?? "");
                Log($"[Places]   cat={cat,2} id=0x{(ushort)id:X4} flags=0x{flags:X8} pos=({x:F0},{z:F0}) d={d:F0}{(skipped ? " [skipped]" : "")}{(npc.Length > 0 ? $" npc={npc}" : "")}");
                total++;
                if (!IsReadable(cur + OFF_MT_NEXT, 8)) break;
                cur = *(nint*)(cur + OFF_MT_NEXT);
            }
        }
        Log($"[Places]   total rows dumped: {total}");
    }

    /// <summary>
    /// Diagnostic (STAIRS EMPTY): the minimap stairs sprite id differs per dungeon
    /// (<see cref="AutoWalk.GridRouter"/> IsStairSprite currently knows 0x0C Yukiko /
    /// 0x0E Bathhouse). When a new dungeon's Stairs category comes up empty, stand ON
    /// the staircase and cycle to Stairs — this logs the player's own cell + 3×3
    /// neighbourhood and every distinct non-zero sprite on the explored map. The
    /// stairs are a ~9-cell (3×3) block of ONE sprite clustered at the player; add that
    /// value to GridRouter.IsStairSprite. See memory/bathhouse_dungeon.md.
    /// </summary>
    private static void LogStairSpriteDump()
    {
        if (!AutoWalk.GridRouter.HasGrid()) return;   // scripted/no-grid floors use scene-actor stairs
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        Log($"[StairsEmpty] === minimap sprite dump  major={FieldTracker.CurrentMajor}/{FieldTracker.CurrentMinor}  player=({px:F0},{pz:F0}) ===");

        // Player's own cell + its 3×3 neighbourhood = the staircase block you stand on.
        if (!float.IsNaN(px) && !float.IsNaN(pz) && MinimapTracker.WorldToCell(px, pz, out int pr, out int pc))
        {
            Log($"[StairsEmpty] player cell = row {pr}, col {pc} (flag/sprite of the 3x3 around it):");
            for (int dr = -1; dr <= 1; dr++)
            {
                var row = new System.Text.StringBuilder();
                for (int dc = -1; dc <= 1; dc++)
                    row.Append(MinimapTracker.ReadCell(pr + dr, pc + dc, out var cc)
                        ? $"[f{cc.Flag} s0x{cc.Sprite:X2}] " : "[--------] ");
                Log($"[StairsEmpty]   {row.ToString().TrimEnd()}");
            }
        }
        else Log("[StairsEmpty] player cell unknown (no position).");

        // Every distinct non-zero sprite across the explored map, with count + first cell.
        var seen = new Dictionary<byte, (int count, int r, int c)>();
        for (int r = 0; r < MinimapTracker.ROWS; r++)
        for (int c = 0; c < MinimapTracker.COLS; c++)
        {
            if (!MinimapTracker.ReadCell(r, c, out var cell)) continue;
            if (cell.Flag == 0 || cell.Sprite == 0) continue;
            if (seen.TryGetValue(cell.Sprite, out var v)) seen[cell.Sprite] = (v.count + 1, v.r, v.c);
            else seen[cell.Sprite] = (1, r, c);
        }
        if (seen.Count == 0) { Log("[StairsEmpty] no non-zero sprites on the explored map."); return; }
        foreach (var kv in seen)
        {
            byte spr = kv.Key;
            var (count, r, c) = kv.Value;
            bool known = spr == 0x0C || spr == 0x0D || spr == 0x0E;  // Yukiko / entrance-up / Bathhouse
            Log($"[StairsEmpty]   sprite 0x{spr:X2}  x{count}  first@row{r},col{c}{(known ? "  (known stair/entrance sprite)" : "")}");
        }
        Log("[StairsEmpty] === end dump — the progress stairs are a ~9-cell block near the player; add its sprite id to GridRouter.IsStairSprite ===");
    }

    private static unsafe bool ReadFloorGate()
        => IsReadable(FloorGatePtr, 8) && *(nint*)FloorGatePtr != 0;

    // ── Floor teleport ──

    private static int CurrentFloorIndex()
    {
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        for (int i = 0; i < _floors.Length; i++)
            if (_floors[i].major == major && _floors[i].minor == minor) return i;
        return -1;
    }

    private void TeleportFloor(int dir)
    {
        int cur = CurrentFloorIndex();
        int t = cur + dir;
        if (cur < 0 || t < 0 || t >= _floors.Length)
        {
            Speech.Say(cur < 0 ? "Floor jump not available here."
                                : $"No {(dir > 0 ? "next" : "previous")} floor.", true);
            return;
        }
        var target = _floors[t];
        Log($"[DungeonNav] teleport → {target.name} (flag {target.flagId})");
        Speech.Say($"Teleporting to {target.name}.", true);
        new Thread(() => TeleportSequence(target)) { IsBackground = true, Name = "DungeonNavTeleport" }.Start();
    }

    // A PLACED Event mark whose Label is a floor-jump command (vs a walk-to point) — hand-set in
    // dungeon_waypoints.json on the specific floors where a teleport makes sense (teleporting skips
    // a floor's story scripts, so it must NOT be global). Returns +1 next / -1 previous.
    private static bool IsFloorJumpLabel(string label, out int dir)
    {
        dir = 0;
        if (string.IsNullOrEmpty(label)) return false;
        string l = label.Trim().ToLowerInvariant();
        if (l == "next floor")     { dir = +1; return true; }
        if (l == "previous floor" || l == "prev floor") { dir = -1; return true; }
        return false;
    }

    // GENERIC floor jump — works in ANY dungeon (Bathhouse included), because the field.flow branch
    // uses CALL_DUNGEON(GET_FLOOR_ID() ± 1, 0) (floor IDs are one sequential int; mechanism found in
    // customSubMenu's Floor Select). dir = +1 next, -1 previous. Flags 6711/6712 (armed here, consumed
    // by our field.flow). The way past a floor whose stairs the auto-walk can't route to.
    private void TeleportRelativeFloor(int dir)
    {
        int flag = dir > 0 ? 6711 : 6712;
        Speech.Say(dir > 0 ? "Teleporting to the next floor." : "Teleporting to the previous floor.", true);
        new Thread(() => TriggerFloorFlag(flag)) { IsBackground = true, Name = "DungeonNavFloorJump" }.Start();
    }

    private unsafe void TriggerFloorFlag(int flagId)
    {
        try
        {
            if (!SetFlagBit(flagId, true))
            {
                Speech.Say("Floor jump not ready, try again.", true);
                Log("[DungeonNav] floor-jump flag bitmap not initialised; aborted.");
                return;
            }
            Thread.Sleep(180);   // let the trigger key (Backspace) release before synth-F
            keybd_event(0, (byte)SC_F, KEYEVENTF_SCANCODE, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(0, (byte)SC_F, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(3500);
            Log($"[DungeonNav] floor-jump flag {flagId} -> now {FieldTracker.CurrentMajor}/{FieldTracker.CurrentMinor}");
        }
        catch (Exception ex) { Log($"[DungeonNav] floor-jump error: {ex.Message}"); }
    }

    private void TeleportSequence((int major, int minor, string name, int flagId) target)
    {
        try
        {
            if (!SetFlagBit(target.flagId, true))
            {
                Speech.Say("Teleport not ready, try again.", true);
                Log("[DungeonNav] flag bitmap pointer not initialised; aborted.");
                return;
            }
            int waited = 0;
            while (IsKeyDown(VK_OEM_5) && waited < 600) { Thread.Sleep(20); waited += 20; }
            Thread.Sleep(120);

            keybd_event(0, (byte)SC_F, KEYEVENTF_SCANCODE, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(0, (byte)SC_F, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);

            Thread.Sleep(3500);
            Log($"[DungeonNav] arrived on {FieldTracker.CurrentMajor}/{FieldTracker.CurrentMinor} for {target.name}.");
        }
        catch (Exception ex) { Log($"[DungeonNav] teleport error: {ex.Message}"); }
    }

    private static unsafe bool SetFlagBit(int bitId, bool value)
    {
        if (!IsReadable((nint)_flagBitmapPtr, 8)) return false;
        byte* bitmap = *_flagBitmapPtr;
        if (bitmap == null) return false;
        nint dwordAddr = (nint)(bitmap + (bitId >> 5) * 4);
        if (!IsReadable(dwordAddr, 4)) return false;
        uint mask = 1u << (bitId & 31);
        uint* word = (uint*)dwordAddr;
        if (value) *word |= mask; else *word &= ~mask;
        return true;
    }

    // ── direction helpers ──

    // 8-way compass direction to a target offset (dx = east+, dz = north+). Matches
    // the H-cursor's fixed-compass frame (+Z north, +X east) so the browser and the
    // cursor speak the SAME directions — no more relative "ahead/left/right".
    private static string WorldDirection(float dx, float dz)
    {
        float adx = MathF.Abs(dx), adz = MathF.Abs(dz);
        if (adx < 1e-4f && adz < 1e-4f) return "here";
        string ns = dz > 0 ? "north" : "south";
        string ew = dx > 0 ? "east" : "west";
        if (adz > adx * 2f) return ns;        // mostly north/south
        if (adx > adz * 2f) return ew;        // mostly east/west
        return ns + ew;                       // diagonal: northeast / southwest / …
    }

    // ── plumbing ──

    private static void WinBeep(uint freq, uint ms) { try { Beep(freq, ms); } catch { } }
    [DllImport("kernel32.dll")] private static extern bool Beep(uint dwFreq, uint dwDuration);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern unsafe nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static unsafe bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48;
        const int OFF_STATE = 32;
        const int OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD) != 0) return false;
        return true;
    }
}
