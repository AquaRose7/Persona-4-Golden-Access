using System.Runtime.InteropServices;
using System.Text.Json;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Overworld navigation browser — the dungeon kit's UX on the asset-driven
/// catalog (database/overworld_catalog.json, built from the game's own
/// HBN/h-registry files; see database/OVERWORLD.md).
///
///   <c>-</c> / <c>=</c>   cycle CATEGORY: Places · Exits · Other
///   <c>[</c> / <c>]</c>   step entries nearest → far (LIVE re-sort each step)
///   <c>\</c>              distance brief for the current selection
///   <c>Backspace</c>      auto-walk to the selection (cancel when running)
///   <c>P</c>              sound beacon on the selection — ticks faster as you
///                         close in, stops at the CHECK prompt
///
/// All coordinates are WORLD SPACE via <see cref="FieldTracker.WorldPlayerPos"/>
/// (NOT the old sub_obj+0xD0 local space). Replaces the retired
/// NavigationAssist record/calibrate flow — targets come from the catalog, and
/// the game itself has already evaluated which are present today.
///
/// Auto-walk v1 is self-calibrating: it holds W briefly, measures the world
/// direction that produced, and from then on picks the WASD combo whose angle
/// best matches the bearing to the target, re-measuring as it goes (the
/// overworld camera direction varies per area, so the world→key mapping is
/// learned live, not assumed). Arrival = the game's CHECK prompt or close
/// proximity; a no-progress watchdog stops cleanly instead of wall-grinding.
/// </summary>
internal class OverworldNav
{
    private const int PollMs = 50;

    private const int VK_OEM_MINUS = 0xBD;   // -
    private const int VK_OEM_PLUS  = 0xBB;   // =
    private const int VK_OEM_4     = 0xDB;   // [
    private const int VK_OEM_6     = 0xDD;   // ]
    private const int VK_OEM_5     = 0xDC;   // \
    private const int VK_BACK      = 0x08;   // Backspace
    private const int VK_P         = 0x50;   // P beacon
    private const int VK_OEM_1     = 0xBA;   // ;  → speak selected NPC's area+id (rename diagnostic)

    // Final-step assist: a bounded position-write nudge for the last ≤130u onto the spot (school only;
    // travel is always stick). PERMANENTLY ON — the O-key toggle was removed 2026-06-22 (user never wants it
    // off, and O is the party HP/SP key). Kept as a const flag so the gating reads clearly.
    internal const bool PosDrive = true;

    // World units per walking step — first calibration from the live session:
    // Daidara → save point ≈ 1214 units for a short walk. Tune after testing.
    private const float UnitsPerStep = 120f;
    private const float ArriveDist   = 90f;

    // Whole-walk position-drive step sizes (world units per ~10 ms write). The old code used a single
    // tiny value (3) for the WHOLE walk → "turtle". These scale by phase: brisk en route, ease in near
    // the target, precise for the exact final spot. Tune from user feedback (raise travel if too slow,
    // lower it if it clips corners).
    private const float PosTravelStep = 16f;   // en route (~1.3k u/s)
    private const float PosEaseStep   = 8f;    // final approach
    private const float PosArriveStep = 3f;    // exact final placement

    private enum Cat { Places, Exits, People, Other }
    private static readonly Cat[] Categories = { Cat.Places, Cat.Exits, Cat.People, Cat.Other };
    private static string CatName(Cat c) => c == Cat.People ? "NPCs" : c.ToString();

    private struct Target
    {
        public string Name;
        public string? Proc;
        // X/Z = the WALK/BEACON target: the area's follower stand-point bound
        // to this trigger when one sits in/near its box (that's the exact spot
        // the game expects you to stand — e.g. the save point's is the
        // verified CHECK position), else the box centre. BX/BZ + ExtX/ExtZ =
        // the raw trigger volume (inside-the-box arrival test).
        public float X, Y, Z;
        public float BX, BZ, ExtX, ExtZ;
        public bool Boundary;
        public int Kind;
        public ushort PersonId;   // live cat-3 unit handle; 0 = static target
    }

    private sealed class Area
    {
        public List<Target> Targets = new();
    }

    // catalog: "008_002" → area
    private readonly Dictionary<string, Area> _catalog = new();
    // NPC model base-id ("n1557") → display name ("Nanako"); mined from the
    // AMD texture names (tools/build_npc_names.py → npc_model_names.json)
    private readonly Dictionary<string, string> _npcNames = new();

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _minusWas, _plusWas, _lbWas, _rbWas, _bsWas, _bkWas, _pWas, _idWas;

    private int _catIndex = -1;
    private bool _activeLast;
    private List<(Target t, float dist)> _entries = new();
    private int _cursor;

    // selection shared with walker/beacon threads
    private readonly object _selLock = new();
    private Target _selTarget;
    private bool _hasSel;

    // ── manual calibration ────────────────────────────────────────────────────
    // For hard targets the catalog/flag math can't nail, the user stands on the exact spot where the
    // Check works, faces it, and presses Shift+Backspace. We record the player_unit's pos + forward
    // (read reliably from DAT_140ec0fe8) keyed to the target, persist it, and the auto-walk reproduces
    // that verified spot+facing next time — ground truth, no catalog guess, no flaky flags.
    private readonly Dictionary<string, float[]> _calib = new();
    private readonly object _calibLock = new();

    // The two tiny interior rooms whose cramped layout makes the 75u town confirm grab a NEIGHBOUR's
    // check. The calib confirm-tighten in WalkLoop engages ONLY in these area IDs — everywhere else the
    // town path is byte-for-byte unchanged. IDs locked from the live probe (2026-06-24):
    // Dojima Living Room = 7_2, protagonist's Your Room = 7_3 (house entrance 7_1 is left alone).
    private static readonly (int major, int minor)[] _roomAreas = { (7, 2), (7, 3) };
    private static bool IsRoomArea()
    {
        int maj = FieldTracker.CurrentMajor, min = FieldTracker.CurrentMinor;
        foreach (var (a, b) in _roomAreas) if (maj == a && min == b) return true;
        return false;
    }

    // Learned wall points per area (Stage 2): when the body wedges, we stamp the obstacle just ahead so
    // A* routes around it — this run and (in-memory) the rest of the session. The optimistic grid assumes
    // unknown = walkable, so these stalls are how we DISCOVER the real walls the mesh never gave us.
    private readonly Dictionary<string, List<float[]>> _obstacles = new();
    private List<float[]> ObstaclesFor(string area)
    { if (!_obstacles.TryGetValue(area, out var l)) { l = new(); _obstacles[area] = l; } return l; }

    // Prebuilt per-area walkable grid (database/overworld_walkgrid.json — offline-decoded field collision
    // h*.AMD "atari" meshes). The COMPLETE accurate layout, so A* routes correctly with no learn-by-stalling.
    private readonly Dictionary<string, (float ox, float oz, int cols, int rows, bool[] walk)> _walkgrid = new();

    private void LoadWalkgrid()
    {
        try
        {
            string p = DataPath("overworld_walkgrid.json");
            if (!System.IO.File.Exists(p)) { Log("[OverworldNav] overworld_walkgrid.json not found — runtime grid only"); return; }
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(p));
            foreach (var a in doc.RootElement.GetProperty("areas").EnumerateObject())
            {
                var e = a.Value;
                float ox = e.GetProperty("origin")[0].GetSingle(), oz = e.GetProperty("origin")[1].GetSingle();
                int cols = e.GetProperty("cols").GetInt32(), rows = e.GetProperty("rows").GetInt32();
                byte[] bytes = Convert.FromBase64String(e.GetProperty("bits").GetString()!);
                var walk = new bool[cols * rows];
                for (int i = 0; i < walk.Length; i++) walk[i] = (bytes[i >> 3] & (0x80 >> (i & 7))) != 0;   // MSB-first, 1=walkable
                _walkgrid[a.Name] = (ox, oz, cols, rows, walk);
            }
            Log($"[OverworldNav] loaded walkgrid: {_walkgrid.Count} areas");
        }
        catch (Exception ex) { Log($"[OverworldNav] walkgrid load failed: {ex.Message}"); }
    }

    /// <summary>The prebuilt COMPLETE grid for an area, or null (→ fall back to the runtime wall bubble).</summary>
    private MeshGrid? PrebuiltGrid(string area)
    {
        // area is "major_minor" ("6_6"); walkgrid keys are zero-padded like the catalog ("006_006").
        var parts = area.Split('_');
        if (parts.Length == 2 && int.TryParse(parts[0], out int mj) && int.TryParse(parts[1], out int mn))
            area = $"{mj:D3}_{mn:D3}";
        if (!_walkgrid.TryGetValue(area, out var w)) return null;
        var g = MeshGrid.FromWalkable(w.ox, w.oz, w.cols, w.rows, w.walk);
        return g.BlockedCount >= w.cols * w.rows ? null : g;   // all-blocked = useless
    }

    /// <summary>True while an overworld auto-walk is running. Read by ControllerInput so it can
    /// stop clearing the input bitfield (which would otherwise wipe the walk's synthesized
    /// W/A/S/D movement when the user keeps a trigger held).</summary>
    internal static volatile bool IsWalking;
    private bool _walking { get => IsWalking; set => IsWalking = value; }
    private volatile bool _beacon;
    private BeaconVoice _voice;   // stereo positional beacon voice (mixed via DungeonAudio)

    // Singleton handle for cross-component walk requests (RoomActionMenu's quick-interact
    // handoff in big outdoor areas). Set once at startup.
    internal static OverworldNav? Instance;

    public OverworldNav()
    {
        Instance = this;
        LoadCatalog();
        LoadCalib();
        LoadWalkgrid();
        _voice = new BeaconVoice(DungeonAudio.Format, MakePing(), gapFrames: 6000);
        DungeonAudio.AddInput(_voice);   // persistent input; added once at startup
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "OverworldNav" };
        _thread.Start();
        Log($"[OverworldNav] ready ({_catalog.Count} areas; -/= category, [ ] entries, \\ brief, Backspace walk, P beacon)");
    }

    public void Stop() { _stopped = true; _walking = false; _beacon = false; }

    // ── catalog ──────────────────────────────────────────────────────────────

    private void LoadCatalog()
    {
        try
        {
            // RELEASE: bundled in the mod folder. DEV: the game's database folder.
            string path = DataPath("overworld_catalog.json");
            if (!File.Exists(path)) { Log("[OverworldNav] overworld_catalog.json NOT FOUND — browser disabled"); return; }

            // NPC names ride next to the catalog
            string npcPath = DataPath("npc_model_names.json");
            if (File.Exists(npcPath))
            {
                try
                {
                    using var nd = JsonDocument.Parse(File.ReadAllText(npcPath));
                    foreach (var p in nd.RootElement.EnumerateObject())
                        _npcNames[p.Name] = p.Value.GetString() ?? p.Name;
                    _npcNamesStatic = _npcNames;   // share with DungeonNav's lobby Places reader
                    Log($"[OverworldNav] npc names loaded: {_npcNames.Count}");
                }
                catch (Exception ex) { Log($"[OverworldNav] npc names load failed: {ex.Message}"); }
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var areaProp in doc.RootElement.GetProperty("areas").EnumerateObject())
            {
                var area = new Area();
                if (areaProp.Value.TryGetProperty("interactables", out var ints))
                {
                    foreach (var it in ints.EnumerateArray())
                    {
                        string? proc = it.TryGetProperty("proc", out var pr) && pr.ValueKind == JsonValueKind.String
                            ? pr.GetString() : null;
                        string? name = it.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                            ? nm.GetString() : null;
                        bool boundary = name == "(boundary trigger)";
                        int kind = it.TryGetProperty("kind", out var kd) && kd.ValueKind == JsonValueKind.Number
                            ? kd.GetInt32() : -1;
                        float ex = 0, ez = 0;
                        if (it.TryGetProperty("ext", out var extArr) && extArr.ValueKind == JsonValueKind.Array)
                        {
                            int n = 0;
                            foreach (var v in extArr.EnumerateArray())
                            {
                                if (n == 0) ex = v.GetSingle();
                                else if (n == 1) ez = v.GetSingle();
                                n++;
                            }
                        }
                        float bx = it.GetProperty("x").GetSingle();
                        float bz = it.GetProperty("z").GetSingle();
                        area.Targets.Add(new Target
                        {
                            Name = PlaceNames.Translate(name ?? proc ?? "Unknown trigger"),
                            Proc = proc,
                            X = bx + ex / 2f,
                            Y = it.GetProperty("y").GetSingle(),
                            Z = bz + ez / 2f,
                            BX = bx, BZ = bz, ExtX = ex, ExtZ = ez,
                            Boundary = boundary,
                            Kind = kind,
                        });
                    }
                }
                // Bind follower stand-points: a point sitting in/near a
                // trigger box is the precise spot to stand for that
                // interaction (runtime-verified: Daidara point idx2, save
                // point idx5). Use it as the walk/beacon target instead of
                // the geometric box centre, which can be unreachable
                // (the save-point "walks around itself" bug).
                if (areaProp.Value.TryGetProperty("followPoints", out var fps)
                    && fps.ValueKind == JsonValueKind.Array)
                {
                    var pts = new List<(float x, float z)>();
                    foreach (var fp in fps.EnumerateArray())
                        pts.Add((fp.GetProperty("x").GetSingle(), fp.GetProperty("z").GetSingle()));
                    for (int ti = 0; ti < area.Targets.Count; ti++)
                    {
                        var t = area.Targets[ti];
                        if (t.ExtX == 0 && t.ExtZ == 0) continue;
                        float bestD = float.MaxValue; int best = -1;
                        for (int pi = 0; pi < pts.Count; pi++)
                        {
                            if (!InsideBox(in t, pts[pi].x, pts[pi].z, 48f)) continue;
                            float dx = pts[pi].x - t.X, dz = pts[pi].z - t.Z;
                            float d = dx * dx + dz * dz;
                            if (d < bestD) { bestD = d; best = pi; }
                        }
                        if (best >= 0)
                        {
                            t.X = pts[best].x;
                            t.Z = pts[best].z;
                            area.Targets[ti] = t;
                        }
                    }
                }
                _catalog[areaProp.Name] = area;
            }
        }
        catch (Exception ex)
        {
            Log($"[OverworldNav] catalog load failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private Area? CurrentArea()
    {
        int maj = FieldTracker.CurrentMajor, min = FieldTracker.CurrentMinor;
        if (maj <= 0 || min < 0) return null;
        return _catalog.TryGetValue($"{maj:000}_{min:000}", out var a) ? a : null;
    }

    // ── gate ─────────────────────────────────────────────────────────────────

    private bool ActiveHere()
    {
        int major = FieldTracker.CurrentMajor;
        // Yield dungeon (20-69) AND battle (220-299) majors to DungeonNav / the battle readers.
        if (major >= 20 && major < 300) return false;
        return CurrentArea() != null;
    }

    // ── poll loop ────────────────────────────────────────────────────────────

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[OverworldNav] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    // ── passive check-spot LEARNER ──────────────────────────────────────────────
    // We have the game's check signal (0x7F4) but kept THROWING AWAY where it fires. Instead: the moment
    // a real check appears, record the player's EXACT pos+facing keyed to the nearest interactable. After
    // you've checked a thing once (by hand or auto-walk), auto-walk navigates to that LEARNED spot — the
    // check fires because it's literally where it fired before. Auto-calibration, no guessing.
    private float _learnLastX = -9e9f, _learnLastZ;
    private int _stationaryMs;
    private bool _learnedThisStop;
    private void LearnCheckSpot()
    {
        // 0x7F4 fires for EVERY interactable you walk past, so learning on it directly = garbage. Only
        // learn from a DELIBERATE check: player STOPPED ~0.6s with the check up, and NOT auto-walking
        // (so walk-bys + the auto-walk's own bad stops can't pollute the learned spots). Once per stop.
        if (!FieldTracker.TryPlayerPose(out float px, out float pz, out float fx, out float fz)) return;
        float moved = _learnLastX < -8e9f ? 0
            : MathF.Sqrt((px - _learnLastX) * (px - _learnLastX) + (pz - _learnLastZ) * (pz - _learnLastZ));
        _learnLastX = px; _learnLastZ = pz;
        if (moved > 15f) { _stationaryMs = 0; _learnedThisStop = false; }
        else _stationaryMs += PollMs;

        if (_walking || _learnedThisStop || _stationaryMs < 600 || !FieldTracker.CheckPromptActive) return;
        var area = CurrentArea(); if (area == null) return;
        Target best = default; float bestD = float.MaxValue; bool found = false;
        foreach (var tt in area.Targets)
        {
            float d = MathF.Min((tt.BX - px) * (tt.BX - px) + (tt.BZ - pz) * (tt.BZ - pz),
                                (tt.X - px) * (tt.X - px) + (tt.Z - pz) * (tt.Z - pz));
            if (d < bestD) { bestD = d; best = tt; found = true; }
        }
        if (found && bestD < 250f * 250f)
        {
            // Don't DRIFT an already-good learned spot: an auto-walk ends stopped ON the check, so re-learning
            // every time would creep the spot a few u toward the approach each visit (seen in the log: Drama
            // -1868→-1935→-1880). Keep the existing spot if it's within 70u; only (re)learn when there's none
            // yet, or the player deliberately stood somewhere clearly different.
            if (TryGetCalib(best, out float ex, out float ez, out _, out _)
                && (ex - px) * (ex - px) + (ez - pz) * (ez - pz) < 70f * 70f)
            { _learnedThisStop = true; return; }
            lock (_calibLock) _calib[CalibKey(best)] = new[] { px, pz, fx, fz };
            SaveCalib(); _learnedThisStop = true;
            Log($"[OverworldNav] LEARNED check spot for {best.Name} at ({px:F0},{pz:F0}) fwd=({fx:F2},{fz:F2})");
        }
    }

    private void Tick()
    {
        bool active = ActiveHere();
        if (active != _activeLast)
        {
            _catIndex = -1; _entries = new(); _cursor = 0;
            lock (_selLock) _hasSel = false;
            _walking = false;
            if (_beacon) { _beacon = false; }
            _activeLast = active;
        }
        if (!active) return;
        LearnCheckSpot();
        if (!Utils.GameHasFocus()) return;   // don't process hotkeys while alt-tabbed

        // Shift is the "speech history" modifier (handled by HistoryKeys); when it's
        // held our base [ ] / P shortcuts must NOT fire (Shift+[ = history, not entries).
        bool shift = IsKeyDown(0x10);

        bool minus = IsKeyDown(VK_OEM_MINUS);
        if (minus && !_minusWas) CycleCategory(-1);
        _minusWas = minus;

        bool plus = IsKeyDown(VK_OEM_PLUS);
        if (plus && !_plusWas) CycleCategory(+1);
        _plusWas = plus;

        bool lb = IsKeyDown(VK_OEM_4);
        if (lb && !_lbWas && !shift) StepEntry(-1);
        _lbWas = lb;

        bool rb = IsKeyDown(VK_OEM_6);
        if (rb && !_rbWas && !shift) StepEntry(+1);
        _rbWas = rb;

        bool bs = IsKeyDown(VK_OEM_5);
        if (bs && !_bsWas) Brief();
        _bsWas = bs;

        bool bk = IsKeyDown(VK_BACK);
        if (bk && !_bkWas)
        {
            if (shift) RecordSpot();                                   // Shift+Backspace = record this spot
            else if (_walking) { _walking = false; Speech.Say("Walk cancelled.", true); }
            else StartWalk();
        }
        _bkWas = bk;

        bool p = IsKeyDown(VK_P);
        if (p && !_pWas && !shift) ToggleBeacon();   // Shift+P = repeat last (HistoryKeys)
        _pWas = p;

        bool idk = IsKeyDown(VK_OEM_1);
        if (idk && !_idWas) AnnounceSelectedId();    // ;  → speak the selected NPC's area + id (rename diagnostic)
        _idWas = idk;
        // (Removed 2026-06-22: the O-key PosDrive toggle. The final-step assist stays permanently ON — the
        // user never wants it off — and O is the party HP/SP key, so the binding was a conflict.)
    }

    // ── browser ──────────────────────────────────────────────────────────────

    // The call_* proc family = AREA TRANSITIONS (call_street_02 →
    // CALL_FIELD(8,1) etc. — verified in the decompiled flow) plus call_lmap
    // (the "Leave the street?" town-map prompt). The catalog resolves their
    // names to "Exit to <area>".
    private static bool IsExit(Target t)
        => t.Proc?.StartsWith("call_") ?? false;
    private static bool IsStreet(Target t) => false;

    private List<(Target, float)> BuildCategory(Cat cat)
    {
        var list = new List<(Target, float)>();
        var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) return list;

        if (cat == Cat.People)
        {
            foreach (var t in EnumerateLivePeople(px, pz))
            {
                float dx = t.X - px, dz = t.Z - pz;
                list.Add((t, MathF.Sqrt(dx * dx + dz * dz)));
            }
            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return list;
        }

        var area = CurrentArea();
        if (area == null) return list;
        foreach (var t in area.Targets)
        {
            bool match = cat switch
            {
                Cat.Exits => IsExit(t),
                Cat.Places => !IsExit(t) && !IsStreet(t) && !t.Boundary && t.Proc != null,
                Cat.Other => !IsExit(t) && (IsStreet(t) || t.Boundary || t.Proc == null),
                _ => false,
            };
            if (!match) continue;
            float dx = t.X - px, dz = t.Z - pz;
            list.Add((t, MathF.Sqrt(dx * dx + dz * dz)));
        }
        list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return list;
    }

    // ── live people (unit registry, category 3 = placed character models) ───
    // Positions probed from per-node candidates (the registry node layout
    // differs per category; cat-3's exact offset is being verified live —
    // candidates: node+0x160 inline, or the model sub-object at node+0x168 /
    // +0x190 with the standard transform +0x360/+0x368). Heavy sanity filters
    // + a one-shot dump log so a single test run pins the real offset.
    private static readonly nint UnitMaster = unchecked((nint)0x140AA8098L);
    private const int OFF_NODE_NEXT = 0x150;


    private unsafe List<Target> EnumerateLivePeople(float px, float pz)
    {
        var outp = new List<Target>();
        if (!IsReadable(UnitMaster, 8)) return outp;
        nint root = *(nint*)UnitMaster;
        if (root == 0 || !IsReadable(root + 8, 8)) return outp;
        nint arr = *(nint*)(root + 8);
        if (arr == 0 || !IsReadable(arr + 3 * 8, 8)) return outp;

        string areaKey = $"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}";
        nint cur = *(nint*)(arr + 3 * 8);
        int guard = 0, person = 0;
        while (cur != 0 && IsReadable(cur, 0x170) && guard++ < 256)
        {
            ushort id = *(ushort*)cur;
            // candidate position sources, first sane one wins
            float i160x = *(float*)(cur + 0x160), i160z = *(float*)(cur + 0x168);
            SubPos(cur, 0x168, out float s168x, out float s168z);
            SubPos(cur, 0x190, out float s190x, out float s190z);
            var cands = new (string src, float x, float z)[]
            {
                ("inline160", i160x, i160z),
                ("sub168", s168x, s168z),
                ("sub190", s190x, s190z),
            };
            string used = "none";
            float bx = float.NaN, bz = float.NaN;
            foreach (var c in cands)
            {
                if (!float.IsFinite(c.x) || !float.IsFinite(c.z)) continue;
                if (c.x == 0 && c.z == 0) continue;
                if (MathF.Abs(c.x) > 40000 || MathF.Abs(c.z) > 40000) continue;
                float dx = c.x - px, dz = c.z - pz;
                if (MathF.Sqrt(dx * dx + dz * dz) > 25000) continue;
                used = c.src; bx = c.x; bz = c.z;
                break;
            }
            if (used != "none")
            {
                person++;
                string name = TryGetNpcModelName(cur) ?? $"NPC {person}";
                name = OverrideNpcName(areaKey, id, name);
                outp.Add(new Target { Name = name, X = bx, Z = bz, PersonId = id });
            }
            if (!IsReadable(cur + OFF_NODE_NEXT, 8)) break;
            cur = *(nint*)(cur + OFF_NODE_NEXT);
        }
        // de-duplicate display names ("Student" ×3 → "Student 1..3")
        var counts = new Dictionary<string, int>();
        foreach (var t in outp)
            counts[t.Name] = counts.GetValueOrDefault(t.Name) + 1;
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < outp.Count; i++)
        {
            var t = outp[i];
            if (counts[t.Name] > 1)
            {
                seen[t.Name] = seen.GetValueOrDefault(t.Name) + 1;
                t.Name = $"{t.Name} {seen[t.Name]}";
                outp[i] = t;
            }
        }
        return outp;
    }

    /// <summary>
    /// Per-INSTANCE NPC name overrides, keyed on area + the unit's 16-bit handle id (unique per
    /// scene). Used for story characters that appear in the field with a SHARED model — either a
    /// generic "Townsperson" OR a named-but-shared model like "Drama club member". Because the key
    /// is the exact area+id, renaming one instance never touches the others (the shared model name,
    /// used for navigating every other instance, stays intact). Add a line per character; find the
    /// id with the `;` diagnostic (AnnounceSelectedId). For TV-world LOBBY NPCs use
    /// DungeonNav.PlaceNpcNames instead.
    /// </summary>
    private static string OverrideNpcName(string areaKey, ushort id, string baseName)
    {
        if (areaKey == "8_2" && id == 0x0C1C) return "Dojima";       // Central Shopping District
        if (areaKey == "8_2" && id == 0x0C28) return "King Moron";
        if (areaKey == "6_2" && id == 0x0C35) return "Yumi";         // was "Drama club member"
        if (areaKey == "9_4" && id == 0x0C34) return "Adachi";       // was "Townsperson"
        // From database/fixed characters IDs.txt (user-recorded via the `;` diagnostic, 2026-07-02):
        if (areaKey == "6_2" && id == 0x0C38) return "Daisuke";              // was "Townsperson 6" (Daisuke Nagase)
        if (areaKey == "6_2" && id == 0x0C37) return "Kou";                  // was "Townsperson 5" (Kou Ichijo)
        if (areaKey == "8_2" && id == 0x0C3A) return "Adachi";              // was "Townsperson 3"
        if (areaKey == "8_2" && id == 0x0C3E) return "Yumi";               // was "Drama club member"
        if (areaKey == "6_1" && id == 0x0C32) return "Naoki Konishi";       // was "Boy"
        if (areaKey == "10_1" && id == 0x0C06) return "Konishi Liquor Store manager"; // was "Townsperson 4"
        // From database/fixed characters IDs.txt (user-recorded via the `;` diagnostic, 2026-07-03):
        if (areaKey == "8_1" && id == 0x0C36) return "Kou";                  // was "Townsperson 2"
        if (areaKey == "6_6" && id == 0x0C02) return "Timid female student"; // was "Townsperson 2"
        if (areaKey == "6_2" && id == 0x0C04) return "Shady student";        // was "Townsperson 4"
        if (areaKey == "6_4" && id == 0x0C02) return "Male student";         // was "Townsperson 2"
        return baseName;
    }

    /// <summary>
    /// Resolve a live NPC's display name from its model file path. Chain
    /// (verified live on Nanako, 2026-06-12): *(node+0x190) = model object,
    /// *(model+0x8) = resource, *(res+0x1C8) = name buffer with the ASCII
    /// path "\model\npc\n1557_4" at +0x8 → base id n1557 → name DB.
    /// </summary>
    // Shared, read-only copy of the loaded model→name DB so other components
    // (DungeonNav's lobby Places reader) can resolve NPC names off a live node
    // without re-loading the json. Populated once when the catalog loads.
    private static Dictionary<string, string>? _npcNamesStatic;

    /// <summary>
    /// Resolve a live NPC's display name from a node via the model-path chain
    /// (*(node+0x190)→+0x8→+0x1C8→+8 ASCII "\model\npc\nNNNN_N" → name DB).
    /// Static façade over <see cref="TryGetNpcModelName"/> for reuse outside the
    /// overworld (e.g. DungeonNav's lobby NPCs). Returns null if the chain
    /// doesn't resolve to a valid NPC model — so non-NPC nodes fall through.
    /// </summary>
    internal static unsafe string? ResolveNpcModelName(nint node)
    {
        var db = _npcNamesStatic;
        if (db == null || db.Count == 0) return null;
        if (!IsReadable(node + 0x190, 8)) return null;
        nint mdl = *(nint*)(node + 0x190);
        if (mdl == 0 || !IsReadable(mdl + 0x8, 8)) return null;
        nint res = *(nint*)(mdl + 0x8);
        if (res == 0 || !IsReadable(res + 0x1C8, 8)) return null;
        nint buf = *(nint*)(res + 0x1C8);
        if (buf == 0 || !IsReadable(buf + 8, 64)) return null;
        Span<byte> bytes = stackalloc byte[64];
        for (int i = 0; i < 64; i++) bytes[i] = *(byte*)(buf + 8 + i);
        int len = bytes.IndexOf((byte)0);
        if (len <= 0) return null;
        string pathStr = System.Text.Encoding.ASCII.GetString(bytes[..len]);
        var m = System.Text.RegularExpressions.Regex.Match(pathStr, @"\\(n\d+)_\d+$");
        if (!m.Success) return null;
        return db.TryGetValue(m.Groups[1].Value, out var nm) ? nm : "Townsperson";
    }

    private unsafe string? TryGetNpcModelName(nint node)
    {
        if (_npcNames.Count == 0) return null;
        if (!IsReadable(node + 0x190, 8)) return null;
        nint mdl = *(nint*)(node + 0x190);
        if (mdl == 0 || !IsReadable(mdl + 0x8, 8)) return null;
        nint res = *(nint*)(mdl + 0x8);
        if (res == 0 || !IsReadable(res + 0x1C8, 8)) return null;
        nint buf = *(nint*)(res + 0x1C8);
        if (buf == 0 || !IsReadable(buf + 8, 64)) return null;
        Span<byte> bytes = stackalloc byte[64];
        for (int i = 0; i < 64; i++) bytes[i] = *(byte*)(buf + 8 + i);
        int len = bytes.IndexOf((byte)0);
        if (len <= 0) return null;
        string pathStr = System.Text.Encoding.ASCII.GetString(bytes[..len]);
        var m = System.Text.RegularExpressions.Regex.Match(pathStr, @"\\(n\d+)_\d+$");
        if (!m.Success) return null;
        // Known model → real name; unknown but valid NPC model → the model is
        // one of the anonymous shared-texture townsfolk.
        return _npcNames.TryGetValue(m.Groups[1].Value, out var nm) ? nm : "Townsperson";
    }

    private static unsafe bool SubPos(nint node, int subOff, out float x, out float z)
    {
        x = float.NaN; z = float.NaN;
        if (!IsReadable(node + subOff, 8)) return false;
        nint sub = *(nint*)(node + subOff);
        if (sub == 0 || !IsReadable(sub + 0x360, 12)) return false;
        x = *(float*)(sub + 0x360);
        z = *(float*)(sub + 0x368);
        return true;
    }

    /// <summary>Re-resolve a live person's current position by unit handle
    /// (people walk around — a captured position goes stale mid-walk).</summary>
    private unsafe bool TryGetPersonPos(ushort personId, out float x, out float z)
    {
        x = 0; z = 0;
        if (!IsReadable(UnitMaster, 8)) return false;
        nint root = *(nint*)UnitMaster;
        if (root == 0 || !IsReadable(root + 8, 8)) return false;
        nint arr = *(nint*)(root + 8);
        if (arr == 0 || !IsReadable(arr + 3 * 8, 8)) return false;
        nint cur = *(nint*)(arr + 3 * 8);
        int guard = 0;
        while (cur != 0 && IsReadable(cur, 0x170) && guard++ < 256)
        {
            if (*(ushort*)cur == personId)
                return SubPos(cur, 0x190, out x, out z) && float.IsFinite(x) && float.IsFinite(z)
                       && (x != 0 || z != 0);
            if (!IsReadable(cur + OFF_NODE_NEXT, 8)) break;
            cur = *(nint*)(cur + OFF_NODE_NEXT);
        }
        return false;
    }

    private string Say((Target t, float dist) e)
    {
        int steps = Math.Max(1, (int)MathF.Round(e.dist / UnitsPerStep));
        return $"{e.t.Name}, {steps} step{(steps == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Inside the target's trigger box, with margin. The HBN extent sign
    /// convention is ambiguous (live evidence: Daidara's stand-point fits
    /// [anchor, anchor+ext] while the north exit's CHECK area fits
    /// [anchor−ext, anchor]) — accept EITHER rectangle until the exact
    /// convention (possibly a rotated rect) is decoded.
    /// </summary>
    private static bool InsideBox(in Target t, float px, float pz, float margin)
    {
        if (t.ExtX == 0 && t.ExtZ == 0)
        {
            float dx = px - t.X, dz = pz - t.Z;
            return MathF.Sqrt(dx * dx + dz * dz) < ArriveDist + margin;
        }
        if (InRect(t.BX, t.BZ, t.BX + t.ExtX, t.BZ + t.ExtZ, px, pz, margin)) return true;
        return InRect(t.BX - t.ExtX, t.BZ - t.ExtZ, t.BX, t.BZ, px, pz, margin);
    }

    private static bool InRect(float ax, float az, float bx, float bz, float px, float pz, float m)
    {
        float x0 = MathF.Min(ax, bx) - m, x1 = MathF.Max(ax, bx) + m;
        float z0 = MathF.Min(az, bz) - m, z1 = MathF.Max(az, bz) + m;
        return px >= x0 && px <= x1 && pz >= z0 && pz <= z1;
    }

    private void CycleCategory(int dir)
    {
        int n = Categories.Length;
        _catIndex = _catIndex < 0 ? (dir > 0 ? 0 : n - 1) : (_catIndex + dir + n) % n;
        _cursor = 0;
        Cat cat = Categories[_catIndex];
        _entries = BuildCategory(cat);
        Log($"[OverworldNav] category → {cat} entries={_entries.Count}");
        if (_entries.Count == 0)
        {
            Speech.Say($"{CatName(cat)}: none here.", true);
            return;
        }
        SetSelection(_entries[0].t);
        Speech.Say($"{CatName(cat)}: {_entries.Count}. {Say(_entries[0])}.", true);
        WinBeep(1100, 30);
    }

    private void StepEntry(int dir)
    {
        if (_catIndex < 0) { Speech.Say("Pick a category with minus or equals first.", true); return; }
        Cat cat = Categories[_catIndex];
        _entries = BuildCategory(cat);
        if (_entries.Count == 0) { Speech.Say($"{CatName(cat)}: none here.", true); return; }

        int next = _cursor + dir;
        if (next < 0) { Speech.Say("First. ", false); next = 0; }
        else if (next >= _entries.Count) { Speech.Say("Last. ", false); next = _entries.Count - 1; }
        _cursor = next;

        SetSelection(_entries[_cursor].t);
        Speech.Say($"{Say(_entries[_cursor])}, {_cursor + 1} of {_entries.Count}.", true);
        WinBeep(1000, 25);
    }

    private void Brief()
    {
        if (!TryGetSelection(out var t)) { Speech.Say("Nothing selected.", true); return; }
        var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) { Speech.Say("Position unavailable.", true); return; }
        float dx = t.X - px, dz = t.Z - pz;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(1, (int)MathF.Round(dist / UnitsPerStep));
        string dir = WorldDirection(dx, dz);
        // No keybind hint — it names keyboard keys (noise for controller players, messy with both schemes).
        Speech.Say($"{t.Name}: {dir}, {steps} step{(steps == 1 ? "" : "s")}.", true);
    }

    // Fixed-compass bearing to a target offset (dx = +X / east, dz = +Z / north),
    // worded the same as the dungeon browser so directions feel consistent.
    private static string WorldDirection(float dx, float dz)
    {
        float adx = MathF.Abs(dx), adz = MathF.Abs(dz);
        if (adx < 1e-4f && adz < 1e-4f) return "here";
        string ns = dz > 0 ? "south" : "north";   // overworld Z is inverted vs the dungeon convention
        string ew = dx > 0 ? "east" : "west";
        if (adz > adx * 2f) return ns;          // mostly north/south
        if (adx > adz * 2f) return ew;          // mostly east/west
        return ns + ew;                         // diagonal: northeast / southwest / …
    }

    private void SetSelection(Target t)
    {
        lock (_selLock) { _selTarget = t; _hasSel = true; }
    }

    private bool TryGetSelection(out Target t)
    {
        lock (_selLock) { t = _selTarget; return _hasSel; }
    }

    /// <summary>
    /// Rename diagnostic (the <c>;</c> key). Speaks the currently-selected NPC's area key and
    /// 16-bit handle id — exactly the two values <see cref="OverrideNpcName"/> needs to add a
    /// per-instance rename. Navigate to a "Townsperson" with <c>[</c>/<c>]</c>, press <c>;</c>,
    /// read back the id, and we add one line. Only People targets carry an id (PersonId != 0).
    /// (For TV-world LOBBY NPCs the id table is DungeonNav.PlaceNpcNames; those already speak
    /// "Person &lt;id&gt;" in DECIMAL via the dungeon browser.)
    /// </summary>
    private void AnnounceSelectedId()
    {
        if (!TryGetSelection(out var t)) { Speech.Say("Nothing selected.", true); return; }
        if (t.PersonId == 0)
        {
            Speech.Say($"{t.Name} is not a person, no id to rename.", true);
            return;
        }
        int maj = FieldTracker.CurrentMajor, min = FieldTracker.CurrentMinor;
        string hex = t.PersonId.ToString("X4");
        string spoken = string.Join(" ", hex.ToCharArray());   // digit-by-digit so the reader doesn't mangle it
        Speech.Say($"{t.Name}. Area {maj} {min}. Id {spoken}.", true);
        Log($"[OverworldNav] ID DIAG: area {maj}_{min} id 0x{hex} name '{t.Name}'");
    }

    private static string CalibKey(Target t) => $"{(int)MathF.Round(t.BX)}_{(int)MathF.Round(t.BZ)}_{t.Name}";

    private static string CalibWritePath()
    {
        var cwd = Environment.CurrentDirectory;
        foreach (var b in new[] { System.IO.Path.Combine(cwd, "Persona 4 golden", "database"),
                                  System.IO.Path.Combine(cwd, "database") })
            if (System.IO.Directory.Exists(b)) return System.IO.Path.Combine(b, "overworld_calibration.json");
        return System.IO.Path.Combine(Utils.ModDir ?? "", "overworld_calibration.json");
    }

    private void LoadCalib()
    {
        try
        {
            string p = DataPath("overworld_calibration.json");
            if (!System.IO.File.Exists(p)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, float[]>>(System.IO.File.ReadAllText(p));
            if (d != null) lock (_calibLock) { _calib.Clear(); foreach (var kv in d) _calib[kv.Key] = kv.Value; }
            Log($"[OverworldNav] loaded {_calib.Count} recorded spots");
        }
        catch { }
    }

    private void SaveCalib()
    {
        try
        {
            Dictionary<string, float[]> snap;
            lock (_calibLock) snap = new Dictionary<string, float[]>(_calib);
            string json = JsonSerializer.Serialize(snap);
            // Write to BOTH (a) the mod-folder copy the loader actually READS via DataPath — so
            // learned spots PERSIST across restart (the old bug: saved only to database/, which
            // LoadCalib ignores when a mod-folder copy exists — same as the dungeon-marks bug)
            // AND (b) the database source (keeps the committed/bundled copy current).
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Utils.ModDir)) paths.Add(System.IO.Path.Combine(Utils.ModDir, "overworld_calibration.json"));
            paths.Add(CalibWritePath());
            foreach (var p in paths)
            {
                try { System.IO.File.WriteAllText(p, json); }
                catch (Exception e) { Log($"[OverworldNav] calib save to {p} failed: {e.Message}"); }
            }
        }
        catch (Exception e) { Log($"[OverworldNav] calib save failed: {e.Message}"); }
    }

    private void RecordSpot()
    {
        if (!TryGetSelection(out var t)) { Speech.Say("Select a target first.", true); return; }
        if (!FieldTracker.TryPlayerPose(out float px, out float pz, out float fx, out float fz))
        { Speech.Say("Position unavailable.", true); return; }
        lock (_calibLock) _calib[CalibKey(t)] = new[] { px, pz, fx, fz };
        SaveCalib();
        Speech.Say($"Recorded the spot for {t.Name}. Auto walk will use it next time.", true);
        Log($"[OverworldNav] calib RECORD {t.Name} pos=({px:F0},{pz:F0}) fwd=({fx:F2},{fz:F2})");
    }

    private bool TryGetCalib(Target t, out float x, out float z, out float fx, out float fz)
    {
        x = z = fx = fz = 0;
        lock (_calibLock)
            if (_calib.TryGetValue(CalibKey(t), out var c) && c.Length >= 4)
            { x = c[0]; z = c[1]; fx = c[2]; fz = c[3]; return true; }
        return false;
    }

    // ── beacon (P) ───────────────────────────────────────────────────────────

    private void ToggleBeacon()
    {
        if (_beacon) { _beacon = false; Speech.Say("Beacon off.", true); return; }
        if (!TryGetSelection(out var t)) { Speech.Say("Select a target first.", true); return; }
        _beacon = true;
        Speech.Say($"Beacon on: {t.Name}.", true);
        new Thread(BeaconLoop) { IsBackground = true, Name = "OverworldBeacon" }.Start();
    }

    // Beacon tuning: the tick gap (in mixer frames) at the target and far away,
    // and how far "far" is. Larger gap = slower ticks.
    private const int FarDist = 2500;
    private const int NearGap = (int)(44100 * 0.05f);   // ~0.05 s gap when close → rapid ticks
    private const int FarGap  = (int)(44100 * 0.75f);   // ~0.75 s gap far away → lazy ticks
    private const float NearGain = 0.85f, FarGain = 0.22f;  // louder near, quieter far (realism)
    private const float PanSign = 1f;                   // world east = right (flip to -1 if reversed)
    private const float FrontSign = -1f;                // north = bright (matches flipped overworld Z + compass)

    /// <summary>
    /// STEREO positional beacon: the sound sits at the TARGET's location relative
    /// to the CAMERA (which is locked per area, so it doesn't swing as the player
    /// turns — far less confusing than facing-relative). Pan = target left/right on
    /// screen; pitch = up/down (into the view = higher, toward the camera = lower);
    /// ticks faster as you approach. Trill on arrival / CHECK prompt.
    /// </summary>
    private void BeaconLoop()
    {
        try
        {
            _voice.Playing = true;
            DungeonAudio.SetWant(this, true);

            while (_beacon && !_stopped && ActiveHere())
            {
                if (!Utils.GameHasFocus()) { _voice.Playing = false; DungeonAudio.SetWant(this, false); Thread.Sleep(150); continue; }
                DungeonAudio.SetWant(this, true);   // re-acquire the output when focus returns
                _voice.Playing = true;
                if (!TryGetSelection(out var t)) break;
                var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
                if (!ok) { Thread.Sleep(120); continue; }
                if (t.PersonId != 0 && TryGetPersonPos(t.PersonId, out float lx, out float lz))
                {
                    t.X = lx; t.Z = lz;
                }

                float dx = t.X - px, dz = t.Z - pz;
                float dist = MathF.Sqrt(dx * dx + dz * dz);

                // Arrival = CHECK prompt inside THIS target's box, or on the centre.
                // Only the cramped fridge + sofa (which sit right next to other checks)
                // use a tight radius; every other place keeps the normal tolerance.
                bool tight = IsRoomArea() && (t.Name == "check_reizou" || t.Name == "check_sofa_p4p");
                float arrive = tight ? 38f : ArriveDist;
                bool atCheck = FieldTracker.CheckPromptActive &&
                               (tight ? dist < arrive : InsideBox(in t, px, pz, 60f));
                if (atCheck)
                {
                    Arrive($"{t.Name}. Check available.");
                    return;
                }
                if (dist < arrive)
                {
                    Arrive(FieldTracker.CheckPromptActive
                        ? $"{t.Name}. Check available."
                        : $"At {t.Name}, no check prompt here right now.");
                    return;
                }

                // ── direction ──
                // TOWN (2.5D, fixed camera): FIXED WORLD frame — the sound sits at the
                // interactable's world position (east/west = pan, north/south = muffle),
                // matching the spoken compass cues. (Camera-relative was tried and
                // user-rejected for the town.)
                // SCHOOL (major 6, REAL 3D — the camera rotates): the world frame is wrong
                // relative to the player's view ("hear it left but need to go right",
                // user 2026-07-03) → use the dungeon NavBeacon's CAMERA-relative math
                // (same CameraForward3D the school auto-walker steers by, same
                // user-verified pan sign).
                float ux = dist > 1e-3f ? dx / dist : 0f;
                float uz = dist > 1e-3f ? dz / dist : 0f;
                float pan, openness;
                var (cfx, cfz) = FieldTracker.CurrentMajor == 6
                    ? FieldTracker.CameraForward3D() : (0f, 0f);
                if ((cfx != 0f || cfz != 0f) && dist > 1f)
                {
                    float fwd = ux * cfx + uz * cfz;              // ahead(+1) … behind(-1)
                    float rgt = ux * cfz + uz * (-cfx);           // right = (fz,-fx)
                    pan = Math.Clamp(rgt * -1f, -1f, 1f);         // NavBeacon's verified sign
                    openness = Math.Clamp((fwd + 1f) * 0.5f, 0f, 1f);
                }
                else
                {
                    pan = Math.Clamp(ux * PanSign, -1f, 1f);
                    openness = Math.Clamp((uz * FrontSign + 1f) * 0.5f, 0f, 1f);
                }
                float prox = 1f - Math.Clamp(dist, 0f, FarDist) / FarDist; // 1 near … 0 far
                float gain = FarGain + (NearGain - FarGain) * prox;        // louder as you approach
                int gap = NearGap + (int)((1f - prox) * (FarGap - NearGap));

                _voice.Set(gain, pan, 1f);     // constant pitch (pitch cue removed)
                _voice.Openness = openness;
                _voice.GapFrames = gap;
                Thread.Sleep(55);
            }
        }
        catch (Exception ex) { Log($"[OverworldNav] beacon error: {ex.Message}"); }
        finally { _beacon = false; _voice.Playing = false; DungeonAudio.SetWant(this, false); }
    }

    private void Arrive(string say)
    {
        _voice.Playing = false;
        DungeonAudio.SetWant(this, false);
        WinBeep(900, 60); WinBeep(1200, 60); WinBeep(1500, 90);
        Speech.Say(say, true);
        _beacon = false;
    }

    // Short sine ping for the beacon tick (the gap between loops sets the rhythm).
    private static float[] MakePing()
    {
        int rate = DungeonAudio.Format.SampleRate;
        int n = (int)(rate * 0.05f);
        var m = new float[n];
        int fade = Math.Max(1, n / 6);
        for (int i = 0; i < n; i++)
        {
            float env = i < fade ? i / (float)fade : (i > n - fade ? (n - i) / (float)fade : 1f);
            m[i] = MathF.Sin(2f * MathF.PI * 760f * i / rate) * 0.7f * env;
        }
        return m;
    }

    // ── auto-walk (Backspace) ────────────────────────────────────────────────

    private void StartWalk()
    {
        if (!TryGetSelection(out var t)) { Speech.Say("Select a target first.", true); return; }
        var (_, _, _, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) { Speech.Say("Position unavailable.", true); return; }
        _walking = true;
        Speech.Say($"Walking to {t.Name}.", true);
        new Thread(() => WalkLoop(t)) { IsBackground = true, Name = "OverworldWalk" }.Start();
    }

    // ── programmatic walk (RoomActionMenu quick-interact handoff) ────────────
    // True once the poll has SEEN the field as active (its state-reset on the active flip has
    // already run) — starting a walk before that would get it killed by the reset.
    internal bool ReadyForWalk => ActiveHere() && _activeLast;

    /// <summary>
    /// Start the Backspace auto-walk to the current area's catalog target with the given proc
    /// name (e.g. "tyuuka" = Aiya). Returns false if not in a catalogued area, the proc isn't
    /// here, the player position isn't readable yet, or a walk is already running.
    /// announce=false skips the "Walking to X" line (quick-interact: the player just PICKED
    /// the place from a menu — repeating it is noise; the arrival announce still happens).
    /// </summary>
    internal bool WalkToProcTarget(string proc, bool announce = true)
    {
        if (_walking) return false;
        var area = CurrentArea();
        if (area == null) return false;
        // Same proc can back several boxes (e.g. the two check_farmtools yard spots) — prefer
        // the one with a LEARNED spot (ground truth), else the first match.
        Target target = default; bool found = false, foundCalib = false;
        foreach (var t in area.Targets)
        {
            if (t.Proc != proc) continue;
            bool hasCalib = TryGetCalib(t, out _, out _, out _, out _);
            if (!found || (hasCalib && !foundCalib)) { target = t; found = true; foundCalib = hasCalib; }
        }
        if (!found) return false;
        var (_, _, _, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) return false;
        _walking = true;
        if (announce) Speech.Say($"Walking to {target.Name}.", true);
        new Thread(() => WalkLoop(target)) { IsBackground = true, Name = "OverworldWalk" }.Start();
        return true;
    }

    // 8 walk directions: angle (degrees, 0 = the direction W moves) → scancodes.
    private static readonly (float ang, ushort[] keys)[] KeyDirs =
    {
        (0f,   new ushort[] { SC_W }),
        (45f,  new ushort[] { SC_W, SC_D }),
        (90f,  new ushort[] { SC_D }),
        (135f, new ushort[] { SC_S, SC_D }),
        (180f, new ushort[] { SC_S }),
        (225f, new ushort[] { SC_S, SC_A }),
        (270f, new ushort[] { SC_A }),
        (315f, new ushort[] { SC_W, SC_A }),
    };

    private const ushort SC_W = 0x11, SC_A = 0x1E, SC_S = 0x1F, SC_D = 0x20;

    // Final-approach search sweep: world-space offsets around the target. Driving toward each both
    // nudges the player into a possibly-OFFSET trigger volume AND turns facing that way, so the
    // game's facing-aware prompt (0x7F4) eventually lights even when the volume isn't at the catalog point.
    private static readonly (float x, float z)[] SweepOffsets =
    {
        (0, 0), (70, 0), (-70, 0), (0, 70), (0, -70),
        (55, 55), (55, -55), (-55, 55), (-55, -55),
    };

    // ARRIVE local search: an EXPANDING ring of probe offsets around the destination. Driving toward each
    // tests a position AND a facing (facing follows movement); the flags+nearest arrival stops us the
    // instant the target's own check fires. Fine (30) → wide (75) so it creeps closer, not one coarse loop.
    private static readonly (float x, float z)[] ArriveProbe =
    {
        (0, 0),
        (30, 0), (-30, 0), (0, 30), (0, -30),
        (45, 45), (45, -45), (-45, 45), (-45, -45),
        (60, 0), (-60, 0), (0, 60), (0, -60),
        (75, 30), (75, -30), (-75, 30), (-75, -30), (30, 75), (-30, 75), (30, -75), (-30, -75),
    };

    /// <summary>
    /// Self-calibrating greedy walker.
    /// 1. Probe: hold W ~200 ms, measure the world-space direction it produced
    ///    (the overworld camera mapping is unknown per area).
    /// 2. Steer: every ~250 ms recompute the bearing to the target, pick the
    ///    key combo whose angle (relative to W's world direction) matches, and
    ///    update the calibration from the motion actually measured.
    /// Stops on: CHECK prompt (arrival), proximity, cancel (Backspace),
    /// no-progress watchdog, area change, or 45 s timeout.
    /// </summary>
    private void WalkLoop(Target t)
    {
        var held = new List<ushort>();
        string areaAtStart = $"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}";
        // DISABLED 2026-06-21 (session 2): whole-walk position-drive drove the player OFF THE MAP and
        // broke the game in area 6_4. The prebuilt walkgrid there routed the path NORTH (rasterization
        // false-walls), and position-write BYPASSES the game's own collision — so nothing stopped the
        // player leaving the world (Z climbed +1400u into the void). Hard-off → the walk is FULLY stick
        // again (the game's collision is the safety net). Do NOT re-enable without (1) a fixed/validated
        // grid and (2) a hard in-bounds clamp on every write. See pathfinder_research_2026-06-21.md.
        bool wholePosDrive = false;
        // If the user recorded a verified spot for this target, drive to THAT (not the catalog point)
        // and arrive on the recorded pos+facing — ground truth instead of the unreliable flags.
        bool calib = TryGetCalib(t, out float calX, out float calZ, out float calFx, out float calFz);
        if (calib) { t.X = calX; t.Z = calZ; }
        // REMOVED 2026-06-22: the old "adopt nearest scene object" step. TryNearestSceneObject is unreliable
        // in the school (it returns walls/props, not the door) and it DRAGGED targets onto the wrong thing —
        // it pulled "Sports Clubs" (undoubu box centre -5253,69) over to (-5200,206), right onto the Passage,
        // so the walk grabbed the passage's check. Use the catalog target (box centre/follow-point) directly;
        // the learned-spot system handles any per-target offset (e.g. the save's butterfly) far better.
        var areaObs = ObstaclesFor(areaAtStart);   // learned walls for this area (Stage 2)
        var collGrid = PrebuiltGrid(areaAtStart);   // for position-drive's own wall collision

        // REACHABLE-DOOR TARGET (town, 2026-06-22): a shop's catalog point is often the trigger-box CENTRE,
        // which sits INSIDE the building (non-walkable) → the stick rams the wall ~90u short and the arrival
        // search never runs (Yomenaido/Marukyu failed this way; Daidara/Shiroku worked only because a bound
        // follow-point happened to sit out at the door). Snap the target to the nearest WALKABLE cell = the
        // street-side door. No-op when the target is already walkable (a shop with a good stand-point) or out
        // of grid bounds. School + calib (learned spot) + NPCs (PersonId!=0, they move + aren't buildings)
        // untouched (door-snapping an NPC dragged Townsperson 1 to a random "door").
        if (!calib && t.PersonId == 0 && FieldTracker.CurrentMajor != 6 && collGrid != null)
        {
            collGrid.WorldToCell(t.X, t.Z, out int tr, out int tc);
            if (collGrid.InBounds(tr, tc) && collGrid.Blocked(tr, tc)
                && collGrid.NearestFree(tr, tc, out int fr, out int fc))
            {
                collGrid.CellToWorld(fr, fc, out float ndx, out float ndz);
                Log($"[OverworldNav] town target → reachable door ({ndx:F0},{ndz:F0}) was box-centre ({t.X:F0},{t.Z:F0})");
                t.X = ndx; t.Z = ndz;
            }
        }
        try
        {
            // ── probe: find ANY direction that moves (a player wedged in a
            // corner can be unable to move W but free along other axes —
            // the "(-475,-4414) can't walk" wedge from live testing) ──
            var (p0x, p0y, p0z, ok0) = FieldTracker.WorldPlayerPos();
            if (!ok0) { Speech.Say("Position unavailable.", true); return; }
            Log($"[OverworldNav] walk START → {t.Name} target=({t.X:F0},{t.Z:F0}) from=({p0x:F0},{p0z:F0}) area={areaAtStart}");
            // Probe with the STICK (we drive with the stick now): push it in a direction, measure the
            // world motion → the world angle of "stick forward". (Keyboard-W ≠ stick-forward, which was
            // sending the walker off in a fixed direction.) Stick frame: deg 0 = forward (Y low).
            // Stick mode needs the world-angle of "stick forward" (the camera mapping varies per area).
            // Position-drive writes WORLD pos directly, so it needs no probe — skip it (faster start, and
            // no false "can't walk from here" when the body is momentarily wedged).
            float wAngle = 0f;
            if (!wholePosDrive)
            {
                wAngle = float.NaN;
                var stickProbes = new (float deg, int x, int y)[]
                { (0f, 0x80, 0x10), (180f, 0x80, 0xF0), (90f, 0xF0, 0x80), (270f, 0x10, 0x80) };
                foreach (var sp in stickProbes)
                {
                    ControllerInput.DriveStickXY(sp.x, sp.y);
                    Thread.Sleep(220);
                    var (p1x, _, p1z, ok1) = FieldTracker.WorldPlayerPos();
                    if (!ok1) break;
                    float wx = p1x - p0x, wz = p1z - p0z;
                    float wlen = MathF.Sqrt(wx * wx + wz * wz);
                    if (wlen >= 3f)
                    {
                        wAngle = MathF.Atan2(wz, wx) - sp.deg * MathF.PI / 180f;
                        Log($"[OverworldNav] stick calibration: {sp.deg}°-stick moved ({wx:F1},{wz:F1}) → forward angle={wAngle * 180 / MathF.PI:F0}°");
                        break;
                    }
                    p0x = p1x; p0z = p1z;
                    Log($"[OverworldNav] stick probe {sp.deg}° moved {wlen:F1}u — trying next");
                }
                ControllerInput.ReleaseDriveStick();
                if (float.IsNaN(wAngle))
                {
                    Speech.Say("Can't walk from here, blocked.", true);
                    Log("[OverworldNav] stick probe failed in all four directions — abort");
                    return;
                }
            }

            float lastX = p0x, lastZ = p0z;
            float lastDist = float.MaxValue;
            int stuckMs = 0, totalMs = 0;
            int deviated = 0, sidestepUntil = 0, sidestepSign = +1;
            bool wallFollow = false; float wfHitDist = 0; int wfSign = +1, wfStartMs = 0;   // (legacy Bug2 — dormant)
            int stalls = 0;   // total stalls this walk — learn+detour each, give up after too many
            int lastLogMs = -1000;   // throttle the per-move diagnostic log
            int arriveStartMs = -1;  // when we entered ARRIVE mode (at the destination)
            bool finalApproach = false, recovered = false;
            int finalMs = 0;

            // 3D-overworld areas (the school = major 6) use a camera that ROTATES as you move, so the
            // once-learned W direction goes stale → the walker drives into walls. Steer off the LIVE
            // camera instead (anchor the camera→W offset to the probe, re-read each step). AND, because
            // the school is a maze, PLAN a route with A* over a grid rasterized from the wall mesh, then
            // follow it (Path A). Greedy-to-target is kept only as the fallback when no route is found.
            bool cam3D = false; float camOffset = 0f;
            List<(float x, float z)>? path = null; int carrot = 0; int replans = 0; bool partial = false;
            int lastReplanMs = -3000;
            float faceX = 0, faceZ = 0;   // last world-space movement dir = the player's facing
            int sweepIdx = 0, sweepTickMs = 0;   // final search sweep to enter offset volumes + face them
            int readyPolls = 0;                  // debounce: both flags must hold, not flicker
            int tFocus = 0; float tFocusDist = 9e9f;   // the target's focused-id, learned at closest approach

            // Re-plan the optimistic route from (fx,fz): rebuild the grid spanning to the target, stamp
            // known + learned walls, force endpoints walkable, A* + string-pull. Used after a wall-follow.
            string gridKind = "none";
            float snapTx = t.X, snapTz = t.Z;   // the A* target SNAPPED to a reachable cell (door/target)
            void Replan(float fx, float fz)
            {
                // Try the COMPLETE prebuilt grid first; if A* can't connect (rasterization false-walls →
                // PARTIAL), fall back to the OPTIMISTIC runtime grid (unknown=walkable, always connects).
                foreach (bool prebuilt in new[] { true, false })
                {
                    MeshGrid? g;
                    if (prebuilt)
                    {
                        g = PrebuiltGrid(areaAtStart); if (g == null) continue;
                        // GUARD (2026-06-21): if the PLAYER is outside this grid, the grid is in the wrong
                        // frame for this area (006_004's Z is off ~1500u → the player sits entirely north of
                        // it). Using it snaps the route to the grid EDGE = garbage path. Skip it → fall back
                        // to the runtime mesh. Only ever trust a prebuilt grid we're actually standing on.
                        g.WorldToCell(fx, fz, out int gr, out int gc);
                        if (!g.InBounds(gr, gc))
                        { Log($"[OverworldNav] prebuilt grid SKIP — player ({fx:F0},{fz:F0}) is out of its bounds (mis-framed grid)"); continue; }
                    }
                    else
                    {
                        // Runtime scene-mesh fallback is SCHOOL-ONLY (the wall-bubble enumerator is tuned for
                        // major 6). For other areas, no prebuilt grid → leave path null → straight-line stick
                        // (the prior 2.5D behaviour), rather than route on an untrusted partial mesh.
                        if (FieldTracker.CurrentMajor != 6) { path = null; partial = false; gridKind = "none"; break; }
                        MeshGrid.AccumulateScene(areaAtStart); g = MeshGrid.BuildAccumulated(p0y, 0, fx, fz, t.X, t.Z);
                    }
                    if (g == null) { path = null; partial = false; gridKind = "none"; continue; }
                    StampObstacles(g);
                    foreach (var o in areaObs) g.StampObstacle(o[0], o[1], 1);
                    g.ClearAround(fx, fz, 1);   // player is standing here = walkable
                    if (calib) g.ClearAround(calX, calZ, 1);
                    // SNAP the A* target to the nearest WALKABLE cell. Catalog stand-points routinely sit
                    // ~100u beyond the floor (at a wall/door/trigger-box), which A* can never reach → PARTIAL.
                    // Route to the reachable cell next to it; the follower + 350u press-range cover the rest.
                    float ptx = t.X, ptz = t.Z;
                    g.WorldToCell(t.X, t.Z, out int trr, out int tcc);
                    if (g.NearestFree(trr, tcc, out int tfr, out int tfc)) g.CellToWorld(tfr, tfc, out ptx, out ptz);
                    snapTx = ptx; snapTz = ptz;   // remember the reachable cell for the final-step approach
                    path = g.PlanWorld(fx, fz, ptx, ptz); carrot = 0;
                    partial = path != null && !g.LastReachedTarget;
                    gridKind = prebuilt ? "PREBUILT" : "runtime";
                    // snapTx = where the route ACTUALLY ENDS. For a town shop in a DISCONNECTED walkable island
                    // the route ends at the reachable cell nearest the shop = the STREET-SIDE DOOR — head there
                    // and confirm on the door check, instead of straight-lining into the shop wall.
                    if (path != null && path.Count > 0) { snapTx = path[^1].x; snapTz = path[^1].z; }
                    if (path != null && g.LastReachedTarget) return;   // reached → use it
                    // prebuilt couldn't reach the target → loop once more to the runtime fallback
                }
            }

            // Step 4: ALL interactables in the area (static catalog + live NPCs, every category) are
            // used two ways: (1) stamped as obstacle "bumps" so A* routes AROUND a blocker (e.g. an
            // NPC standing in front of the target), and (2) at arrival we require the TARGET to be the
            // NEAREST interactable — otherwise the game's single Check prompt belongs to the blocker,
            // not our target (the "checked the girl instead of the save point" bug).
            var interactables = new List<(float x, float z)>();
            { var ar0 = CurrentArea(); if (ar0 != null) foreach (var ot in ar0.Targets) interactables.Add((ot.X, ot.Z)); }
            try { foreach (var pp in EnumerateLivePeople(p0x, p0z)) interactables.Add((pp.X, pp.Z)); } catch { }
            var obstacles = new List<(float x, float z)>();
            foreach (var (ix, iz) in interactables)
            { float ddx = ix - t.X, ddz = iz - t.Z; if (ddx * ddx + ddz * ddz > 70f * 70f) obstacles.Add((ix, iz)); }
            void StampObstacles(MeshGrid? g) { if (g == null) return; foreach (var o in obstacles) g.StampObstacle(o.x, o.z, 1); }
            // True when the interactable nearest the player IS (≈) our target — i.e. the Check prompt
            // showing is for the target, not a closer blocker.
            bool TargetIsNearest(float px2, float pz2)
            {
                float best = float.MaxValue, bx = 0, bz = 0;
                foreach (var (ix, iz) in interactables)
                { float d = (ix - px2) * (ix - px2) + (iz - pz2) * (iz - pz2); if (d < best) { best = d; bx = ix; bz = iz; } }
                if (best == float.MaxValue) return true;   // no data → don't block arrival
                float dt = (bx - t.X) * (bx - t.X) + (bz - t.Z) * (bz - t.Z);
                return dt < 70f * 70f;
            }
            // True when the player is FACING the target (last movement dir ≈ direction to target). The
            // game selects the in-view-cone interactable at press-time, so facing it makes the press
            // pick OUR target, not a side neighbour. Uses measured world movement (frame-safe).
            bool FacingTarget(float px2, float pz2)
            {
                if (faceX == 0 && faceZ == 0) return true;          // no movement yet → don't block
                float dxx = t.X - px2, dzz = t.Z - pz2; float m = MathF.Sqrt(dxx * dxx + dzz * dzz);
                if (m < 80f) return true;                            // basically on it
                return (faceX * dxx + faceZ * dzz) / m >= 0.35f;     // ~70° cone toward the target
            }
            // A* maze-routing is SCHOOL-ONLY (major 6). The 2.5D town uses the simple, proven v1 walker:
            // straight-line self-calibrating stick toward the target + IN-THE-BOX arrival (below). Every
            // attempt to layer A*/ring-search/nudges onto the town made it WORSE than v1, even with the
            // corrected map (user feedback) — so town stays simple. Camera-refresh stays school-only.
            if (FieldTracker.CurrentMajor == 6)
            {
                Replan(p0x, p0z);
                var (cfx0, cfz0) = FieldTracker.CameraForward3D();
                if (MathF.Sqrt(cfx0 * cfx0 + cfz0 * cfz0) > 0.1f)
                {
                    camOffset = wAngle - MathF.Atan2(cfz0, cfx0);
                    cam3D = true;   // refresh wAngle off the live rotating camera each step
                }
                else Log("[OverworldNav] major 6 but CameraForward3D unreadable");
                Log($"[OverworldNav] plan ({gridKind}): {(path == null ? "A* NULL" : (partial ? "PARTIAL " : "") + path.Count + "pts")} → {t.Name}");
            }

            while (_walking && !_stopped && totalMs < 45000)
            {
                var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
                if (!ok) break;
                // live target refresh: townspeople walk around (keeps the chase on the moving ones)
                if (t.PersonId != 0 && TryGetPersonPos(t.PersonId, out float lx, out float lz))
                {
                    t.X = lx; t.Z = lz;
                }
                float dx = t.X - px, dz = t.Z - pz;
                float dist = MathF.Sqrt(dx * dx + dz * dz);

                // Bug2 LEAVE: stop wall-following once we're strictly CLOSER to the goal than where we hit
                // the wall (monotonic progress → can't orbit). Resume normal path-follow + replan.
                if (wallFollow && dist < wfHitDist - 40f)
                {
                    wallFollow = false;
                    Log($"[OverworldNav] wall-follow leave (closer: {dist:F0}<{wfHitDist:F0}) — replan");
                    Replan(px, pz);   // route from here around the learned walls
                }

                float distSnap = MathF.Sqrt((snapTx - px) * (snapTx - px) + (snapTz - pz) * (snapTz - pz));

                // ARRIVAL aim = the spot we finish on: the LEARNED spot if the user taught one (ground truth
                // — they physically stood there and the check worked), else the catalog target. (Dropped
                // TryNearestSceneObject: in the school it returns walls/props 230-250u away, NOT the door, so
                // its "distObj" made arrival miss the Music Room/Library entirely.)
                float aimTx = calib ? calX : t.X, aimTz = calib ? calZ : t.Z;
                float distArr = MathF.Sqrt((aimTx - px) * (aimTx - px) + (aimTz - pz) * (aimTz - pz));

                bool arriving = (path != null && distSnap < 90f) || distArr < 130f;
                arriveStartMs = arriving ? (arriveStartMs < 0 ? totalMs : arriveStartMs) : -1;

                // CONFIRM. SCHOOL (major 6, packed interactables): TIGHT — the nudge lands the player on the
                // spot, so confirm on the real prompt 0x7F4 within 45u (calib: 0x7F4 within 40u, or essentially
                // ON the spot ≤12u). TOWN (2.5D): catalog targets sit ~80-110u INSIDE shops and the door-check
                // 0x7F4 fires at the building edge, so confirm on the game's REAL prompt 0x7F4 within a GENEROUS
                // 150u (tight distArr made every shop time out even with 0x7F4 ON — Save read 0x7F4=True at 109).
                bool school = FieldTracker.CurrentMajor == 6;
                bool ready;
                if (school)
                    // SCHOOL: the position-write nudge places the player + holds the facing, so the real check
                    // fires — calib can trust the spot (≤12u) or 0x7F4; non-calib needs 0x7F4 within 45u.
                    ready = calib ? ((FieldTracker.CheckPromptActive && distArr < 40f) || distArr < 12f)
                                  : (FieldTracker.CheckPromptActive && distArr < 45f);
                else if (IsExit(t))
                    // TOWN EXIT (call_* proc) = a WALK-INTO transition, NOT a press-check (it never sets 0x7F4).
                    // Arrive when we're IN the exit volume (0x720) near the target — the player walks forward to leave.
                    ready = FieldTracker.InInteractZone && distArr < 75f;
                else if (t.PersonId != 0)
                    // NPC: confirm the INSTANT the talk prompt (0x7F4) is on. distArr is to the person's CENTER,
                    // and their BODY stops us ~100-123u short of it (log: every NPC bumped + got 0x7F4 at
                    // distArr 103-123 but my old <90 was UNREACHABLE → it fell through to the 5s timeout = the
                    // long wait + NO arrival beep). 140u covers the bump so the proper ready path fires (with the
                    // sound). The chase faces the target, so 0x7F4 within 140u of it = THIS person's talk.
                    ready = FieldTracker.CheckPromptActive && distArr < 140f;
                else
                {
                    // TOWN shop/object: the REAL check flag 0x7F4 within 75u of the (door-snapped) target. TIGHT
                    // enough to exclude a NEIGHBOUR's check — InsideBox grabbed neighbours via Marukyu's HUGE box
                    // (it confirmed at distArr=320; the poster at 175). The tiny-step search faces OUR object, so
                    // when 0x7F4 lights within 75u it's OURS. Never confirm on distance alone (that was the false
                    // "Check available"); the genuine pose makes 0x7F4 real + persistent.
                    // Per-object tighten (2026-06-24): the fridge (check_reizou, Living Room 7_2) and the sofa
                    // (check_sofa_p4p, Your Room 7_3) confirm at 50u so they stop on their OWN check; every other
                    // object — here and everywhere else — stays at the stock 75u.
                    bool tight50 = IsRoomArea() && (t.Name == "check_reizou" || t.Name == "check_sofa_p4p");
                    float win = tight50 ? 50f : 75f;
                    ready = FieldTracker.CheckPromptActive && distArr < win;
                }

                // arrival diagnostic — logs the ACTUAL flags so the log shows exactly why it does/doesn't latch
                if (arriving && totalMs - lastLogMs >= 400)
                {
                    lastLogMs = totalMs;
                    Log($"[OverworldNav] arrive: 0x7F4={FieldTracker.CheckPromptActive} 0x720={FieldTracker.InInteractZone} " +
                        $"focus={FieldTracker.FocusedInteractable} distArr={distArr:F0} distSnap={distSnap:F0} dist={dist:F0} calib={calib} aim=({aimTx:F0},{aimTz:F0})");
                }
                if (ready)
                {
                    ControllerInput.ReleaseDriveStick(); ReleaseKeys(held);
                    if (++readyPolls >= 2)
                    {
                        WinBeep(900, 60); WinBeep(1200, 60); WinBeep(1500, 90);
                        string doneKind;
                        if (IsExit(t))
                        {
                            // EXIT = a walk-into transition; we're standing in its volume → tell the player to
                            // walk forward (or press) to leave. No "check" — exits don't have one.
                            Speech.Say($"At {t.Name}. Walk forward to leave.", true);
                            doneKind = "exit";
                        }
                        else
                        {
                            // "Check available" only when the real check is actually lit (town ready requires
                            // 0x7F4; school ready too; calib = a known-good spot). This block only runs when
                            // ready, so for town that means 0x7F4 is on → an honest, true "Check available".
                            bool realCheck = calib || school || FieldTracker.CheckPromptActive;
                            bool npc = t.PersonId != 0;   // a living person → "talk", not "check"
                            string word = npc ? "Talk" : "Check";
                            Speech.Say(realCheck ? $"{t.Name}. {word} available."
                                                 : $"You're at {t.Name}. Turn to find the {(npc ? "person" : "check")}.", true);
                            doneKind = realCheck ? "check" : "at-place";
                        }
                        Log($"[OverworldNav] walk DONE ({doneKind}) at ({px:F0},{pz:F0}) target {t.Name} 0x7F4={FieldTracker.CheckPromptActive} distArr={distArr:F0}");
                        return;
                    }
                    Thread.Sleep(60); totalMs += 60; continue;
                }
                readyPolls = 0;

                // ARRIVE give-up: we're at the object but the game never offered a check (door not
                // interactable now / story-gated / blocked). Say so HONESTLY — do NOT claim "press" when
                // there's no real check (that was the false positive at Drama/Library).
                if (arriving && totalMs - arriveStartMs > 5000)
                {
                    ReleaseKeys(held); ControllerInput.ReleaseDriveStick();
                    // A LEARNED target's check IS here (the user did it before) — trust that over a flaky
                    // 0x7F4, but only if we're actually NEAR the learned spot (else it'd announce 80u short).
                    if (FieldTracker.CheckPromptActive && distArr < 55f)
                    { WinBeep(900, 60); WinBeep(1200, 60); WinBeep(1500, 90); Speech.Say($"{t.Name}. Check available.", true); }
                    else
                        // Reached the place but couldn't fire the REAL check (facing/geometry) → honest +
                        // actionable, NEVER a false "check available" (require 0x7F4, not just in-zone/learned).
                        Speech.Say($"You're at {t.Name}. Turn to face it to check.", true);
                    Log($"[OverworldNav] walk DONE (arrive timeout) at ({px:F0},{pz:F0}) target {t.Name} 0x7F4={FieldTracker.CheckPromptActive} 0x720={FieldTracker.InInteractZone} calib={calib} distArr={distArr:F0}");
                    return;
                }
                if (dist < ArriveDist)
                {
                    // FINAL APPROACH: most "missed it" reports were walks
                    // stopping 30-65u short — inside ArriveDist but outside
                    // the game's own CHECK radius. Keep inching toward the
                    // exact point until CHECK fires or we're basically on it.
                    if (!finalApproach)
                    {
                        finalApproach = true;
                        finalMs = 0;
                        Log($"[OverworldNav] walk final approach from dist={dist:F0}");
                    }
                    // Don't stop early on a distance radius (you wanted the actual Check prompt so
                    // packed interactables can be targeted). Keep inching until CHECK fires (handled
                    // by the Check-in-box arrival above); only after a long try, give up gracefully.
                    if (finalMs > 8000)   // allow the final nudge to finish before giving up
                    {
                        ReleaseKeys(held); ControllerInput.ReleaseDriveStick();
                        if (calib && FieldTracker.InInteractZone && distArr < 55f)
                        { WinBeep(900, 60); WinBeep(1200, 60); WinBeep(1500, 90); Speech.Say($"{t.Name}. Check available.", true); }
                        else
                            Speech.Say($"At {t.Name}, but there's no check here right now.", true);
                        Log($"[OverworldNav] walk DONE (no-check timeout) at ({px:F0},{pz:F0}) target {t.Name} 0x7F4={FieldTracker.CheckPromptActive} 0x720={FieldTracker.InInteractZone} calib={calib} distArr={distArr:F0}");
                        return;
                    }
                }
                if ($"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}" != areaAtStart)
                {
                    ReleaseKeys(held);
                    Speech.Say("Area changed, walk ended.", true);
                    Log($"[OverworldNav] walk END (area change) now={FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}");
                    return;
                }

                // 3D area: refresh W's world direction from the LIVE camera so steering tracks the
                // rotation (no lag, no mistaking a camera swing for a wall).
                if (cam3D)
                {
                    var (cfx, cfz) = FieldTracker.CameraForward3D();
                    if (MathF.Sqrt(cfx * cfx + cfz * cfz) > 0.1f)
                        wAngle = MathF.Atan2(cfz, cfx) + camOffset;
                }

                // Grow wall coverage while walking (the scene cache only shows nearby walls). If the
                // current route is only PARTIAL, re-plan every ~2s with the richer coverage so it
                // extends toward the target as more of the building is revealed.
                if (cam3D)
                {
                    MeshGrid.AccumulateScene(areaAtStart);
                    if (partial && totalMs - lastReplanMs >= 2000)
                    {
                        lastReplanMs = totalMs;
                        var gg = MeshGrid.BuildAccumulated(p0y, 0);
                        StampObstacles(gg);
                        var np = gg?.PlanWorld(px, pz, t.X, t.Z);
                        if (np != null && np.Count > 1) { path = np; carrot = 0; partial = !gg!.LastReachedTarget; }
                    }
                }

                // STEERING aim: in the school follow the A* carrot (routes around walls); otherwise
                // (or if planning failed) head straight at the target.
                bool atFrontier = false;
                float adx = dx, adz = dz;
                if (path != null && path.Count > 0)
                {
                    while (carrot < path.Count - 1)
                    {
                        float wcx = path[carrot].x - px, wcz = path[carrot].z - pz;
                        if (MathF.Sqrt(wcx * wcx + wcz * wcz) < MeshGrid.Cell * 0.9f) carrot++; else break;
                    }
                    float cwx = path[carrot].x - px, cwz = path[carrot].z - pz;
                    // Consumed a PARTIAL route (its end is a frontier next to us, not the real target).
                    // Steer at the actual target for the rest, but GENTLY (probe, see pulse below): if
                    // it's a passable door we ease through; if it's a real wall we barely move and the
                    // stuck-watchdog replans/stops — instead of ramming it like a bullet.
                    atFrontier = partial && carrot >= path.Count - 1 && MathF.Sqrt(cwx * cwx + cwz * cwz) < MeshGrid.Cell;
                    if (!atFrontier) { adx = cwx; adz = cwz; }
                }
                // FINAL STRETCH: head at the EXACT object so facing lines up as we walk in. If the
                // game's facing-aware prompt still isn't lit (volume offset, or facing off), SWEEP
                // small offsets around the target — each nudge enters a possibly-offset volume AND
                // turns facing — until 0x7F4 lights (caught by the arrival check at the loop top).
                // Final stretch. With a PATH: head firmly at the SNAPPED reachable cell (door / target
                // cell) to complete the last step — NOT the catalog point (which may be inside a room
                // behind a wall). Without a path (open 2.5D): head at the target / recorded spot.
                if (dist < ArriveDist || arriving)
                {
                    if (calib && school)
                    {
                        // SCHOOL learned spot: drive STRAIGHT to it — the nudge places it + holds the facing.
                        // (Town calib falls through to the tiny-step search below, around the learned spot,
                        // because the town facing can't be WRITTEN — it must be set by genuine movement.)
                        adx = calX - px; adz = calZ - pz;
                    }
                    else if (arriving)
                    {
                        if (wholePosDrive)
                        {
                            // position-drive writes facing directly; aim at the snapped spot.
                            adx = snapTx - px; adz = snapTz - pz;
                        }
                        else if (school)
                        {
                            // SCHOOL: face the catalog point + a ±60° wiggle to slip into an OFFSET trigger
                            // volume (the nudge above does the precise placement; this just keeps it squared up).
                            float wig = ((sweepIdx % 7) - 3) * (20f * MathF.PI / 180f);
                            float baseAng = MathF.Atan2(t.Z - pz, t.X - px);
                            adx = MathF.Cos(baseAng + wig); adz = MathF.Sin(baseAng + wig);
                            if (totalMs - sweepTickMs > 300) { sweepTickMs = totalMs; sweepIdx++; }
                        }
                        else
                        {
                            // TOWN: approaching → head straight at the place. AT a static place (≤60u) → take
                            // GENUINE tiny steps around the spot (8 dirs, ~30u) so REAL movement sets a PERSISTENT
                            // facing and the player physically lands ON the check spot ("move right but land wrong"
                            // was a tiny step short). NEVER search an NPC (PersonId!=0): they're easy to reach, and
                            // the wander walked the player 13 steps AWAY from a talk prompt that was already there.
                            // Just chase the live position straight; walking toward them already faces them.
                            if (distArr < 60f && t.PersonId == 0)
                            {
                                float a = (sweepIdx % 8) * (45f * MathF.PI / 180f);
                                adx = (aimTx + MathF.Cos(a) * 30f) - px; adz = (aimTz + MathF.Sin(a) * 30f) - pz;
                                if (totalMs - sweepTickMs > 450) { sweepTickMs = totalMs; sweepIdx++; }
                            }
                            else { adx = dx; adz = dz; }
                        }
                    }
                    else if (path != null) { adx = snapTx - px; adz = snapTz - pz; }
                    else { adx = dx; adz = dz; }
                }

                // bearing to aim relative to W's world direction (continuous, not snapped to 8)
                float bearing = MathF.Atan2(adz, adx) - wAngle;
                float deg = bearing * 180f / MathF.PI;
                bool following = cam3D && path != null;
                if (!following && totalMs < sidestepUntil) deg += sidestepSign * 45f;   // wall-slide dodge
                // Bug2 WALL-FOLLOW: drive ~tangent to the wall (90° off the goal heading + a small lean
                // into it so the game's collision slides us along) until the leave-check fires.
                if (wallFollow) deg += wfSign * 100f;
                while (deg < 0) deg += 360f;
                while (deg >= 360f) deg -= 360f;

                // nearest 8-combo — kept only for the recalibration intent + the backing-out reverse
                int best = 0; float bestD = 999f;
                for (int i = 0; i < KeyDirs.Length; i++)
                {
                    float d = MathF.Abs(deg - KeyDirs[i].ang);
                    if (d > 180f) d = 360f - d;
                    if (d < bestD) { bestD = d; best = i; }
                }

                // ANALOG STICK DRIVE (replaces the 8-key pulses): continuous direction + a speed that
                // EASES as we close in, so it glides onto the spot and stops — no 8-dir zigzag, no
                // min-pulse overshoot/bounce. The game's own physics/collision/Check run normally; we're
                // just feeding it a perfect joystick. 0x80 = centre, <0x30 up/left, >0xD0 down/right.
                // Arrival speed is GENTLE so we catch the check's rising edge instead of blowing past it
                // (the "it passed it" report). School 42 (the nudge places it); town 55 (straight-walk in).
                float speed = arriving ? (school ? 42f : 55f)
                            : finalApproach ? Math.Clamp(dist * 0.6f, 35f, 110f)     // ease in near the target
                                            : Math.Clamp(dist * 1.0f, 110f, 127f);   // full tilt en route
                // NPC: walk straight up and EASE TO A NEAR-STOP. The old floor-70 chase BLEW PAST them — then
                // the point-blank aim vector (tiny dx/dz) is noisy, so the player spun in place ("went far,
                // moved around a while till it noticed"). dist*0.85 with a low floor brakes us onto the spot so
                // the talk prompt registers and the confirm latches immediately. Live refresh still re-chases a
                // walker if it strolls off (distArr climbs → speed climbs again).
                if (t.PersonId != 0) speed = Math.Clamp(dist * 0.85f, 8f, 110f);
                float rad = deg * (MathF.PI / 180f);
                bool nearTarget = distArr < 130f;   // the stick WEDGES on the last ~100u (log: stuck=4920, moved=0)
                // Finishing facing: the LEARNED facing if taught, else straight at the aim spot.
                float finFx, finFz;
                if (calib && (calFx * calFx + calFz * calFz) > 0.1f) { finFx = calFx; finFz = calFz; }
                else { float al0 = MathF.Sqrt((aimTx - px) * (aimTx - px) + (aimTz - pz) * (aimTz - pz)); finFx = al0 > 1f ? (aimTx - px) / al0 : faceX; finFz = al0 > 1f ? (aimTz - pz) / al0 : faceZ; }
                if (PosDrive && nearTarget && FieldTracker.CurrentMajor == 6)
                {
                    // BOUNDED FINAL NUDGE — SCHOOL-ONLY (position-write verified in major 6; the 2.5D town
                    // uses its proven stick arrival below). Place the player on the exact spot the stick can't
                    // reach (it wedges 80-110u short on desks/corners). SAFE: only the last ≤130u, every step is
                    // VALIDATED against the now-CORRECT walkgrid (off-map = blocked → skipped) and capped to
                    // 8u, toward a LEARNED (verified-walkable) spot when we have one. This is the arrival that
                    // worked before; the off-map disaster was the WHOLE-WALK drive on the BROKEN map (both gone).
                    ControllerInput.ReleaseDriveStick();
                    float aimx = aimTx - px, aimz = aimTz - pz; float al = MathF.Sqrt(aimx * aimx + aimz * aimz);
                    for (int s = 0; s < 4; s++)
                    {
                        var (cpx, _, cpz, cok) = FieldTracker.WorldPlayerPos(); if (!cok) break;
                        float rem = MathF.Sqrt((aimTx - cpx) * (aimTx - cpx) + (aimTz - cpz) * (aimTz - cpz));
                        if (rem > 6f && al > 1f)
                        {
                            float step = MathF.Min(8f, rem);
                            float nx2 = cpx + aimx / al * step, nz2 = cpz + aimz / al * step;
                            bool blocked = false;
                            if (collGrid != null) { collGrid.WorldToCell(nx2, nz2, out int cr, out int cc); blocked = collGrid.Blocked(cr, cc); }
                            // For a LEARNED spot, always drive onto it: it's a point the user physically stood
                            // on (verified-walkable) and we're already within 130u, so the short move is safe
                            // even past a furniture cell. For an unlearned target, respect the walkgrid.
                            if (!blocked || calib) FieldTracker.WritePlayerPos(nx2, nz2);
                        }
                        FieldTracker.WritePlayerForward(finFx, finFz);
                        Thread.Sleep(9);
                    }
                    totalMs += 40;
                    if (finalApproach || atFrontier) finalMs += 40;
                }
                else if (nearTarget && FieldTracker.CurrentMajor == 6)
                {
                    // SCHOOL pure-stick fallback (write FACING only — a rotation, can't move the player — so the
                    // game's check can fire). NOTE: now unreachable since PosDrive is permanently true (the O
                    // toggle was removed 2026-06-22); kept harmlessly in case the nudge is ever gated off again.
                    ControllerInput.ReleaseDriveStick();
                    FieldTracker.WritePlayerForward(finFx, finFz);
                    Thread.Sleep(40); totalMs += 40;
                    if (finalApproach || atFrontier) finalMs += 40;
                }
                else if (wholePosDrive)
                {
                    // ── WHOLE-WALK DIRECT POSITION-DRIVE (2026-06-21): write the player along the route
                    // instead of steering a stick. No drift, no overshoot, and it can pass a spot a body
                    // can't reach (an NPC parked on the save point). The game still reads our MOVING
                    // position each frame, so the CHECK prompt + triggers fire normally. Phase-scaled step
                    // size is the fix for the old "turtle": brisk en route, precise only for the final spot.
                    ControllerInput.ReleaseDriveStick();   // we place directly; hand the stick back
                    float dl = MathF.Sqrt(adx * adx + adz * adz);
                    float ux = dl > 1f ? adx / dl : 0f, uz = dl > 1f ? adz / dl : 0f;
                    // Facing: AT the destination, face the OBJECT so the press selects it (learned facing,
                    // else the real scene object). EN ROUTE, face the way we move (natural + view cone ahead).
                    float ffx = ux, ffz = uz;
                    if (arriving)
                    {
                        if (calib && MathF.Sqrt(calFx * calFx + calFz * calFz) > 0.1f) { ffx = calFx; ffz = calFz; }
                        else if (FieldTracker.TryNearestSceneObject(t.X, t.Z, out float aox, out float aoz))
                        { float dox = aox - px, doz = aoz - pz; float dm = MathF.Sqrt(dox * dox + doz * doz); if (dm > 1f) { ffx = dox / dm; ffz = doz / dm; } }
                    }
                    float stepU = arriving ? PosArriveStep : (finalApproach ? PosEaseStep : PosTravelStep);
                    int writes = arriving ? 4 : 3;
                    int nap = arriving ? 9 : 11;        // <16ms keeps writes >60 Hz so physics can't drift us
                    for (int s = 0; s < writes; s++)
                    {
                        var (cpx, _, cpz, cok) = FieldTracker.WorldPlayerPos();
                        if (!cok) break;
                        if (dl > 1f)
                        {
                            float nx2 = cpx + ux * stepU, nz2 = cpz + uz * stepU;
                            // Our OWN walkgrid IS the collision: never drive into a cell the prebuilt grid
                            // calls a wall (we CAN clip through anything, so we choose not to). dist<50 lets
                            // the final inch finish even if the target cell reads blocked (catalog points
                            // routinely sit a hair into a wall / trigger box).
                            bool blocked = false;
                            if (collGrid != null) { collGrid.WorldToCell(nx2, nz2, out int cr, out int cc); blocked = collGrid.Blocked(cr, cc); }
                            if (!blocked || dist < 50f) FieldTracker.WritePlayerPos(nx2, nz2);
                            // blocked: skip the write (wall ahead in our grid) — the stall watchdog learns it + replans.
                        }
                        if (ffx != 0f || ffz != 0f) FieldTracker.WritePlayerForward(ffx, ffz);
                        Thread.Sleep(nap);
                    }
                    int spent = writes * nap + 4;
                    totalMs += spent;
                    if (finalApproach || atFrontier) finalMs += spent;
                }
                else
                {
                    int sx = 0x80 + (int)MathF.Round(MathF.Sin(rad) * speed);   // +sin = right (D)
                    int sy = 0x80 - (int)MathF.Round(MathF.Cos(rad) * speed);   // forward (W) = LOW Y
                    ControllerInput.DriveStickXY(sx, sy);
                    Thread.Sleep(40);
                    totalMs += 40;
                    if (finalApproach || atFrontier) finalMs += 40;
                }

                // progress + live recalibration from actual motion
                var (nx, _, nz, ok2) = FieldTracker.WorldPlayerPos();
                if (ok2)
                {
                    float mx = nx - lastX, mz = nz - lastZ;
                    float mlen = MathF.Sqrt(mx * mx + mz * mz);
                    if (mlen > 8f)
                    {
                        faceX = mx / mlen; faceZ = mz / mlen;   // player faces their movement direction
                        float moveAng = MathF.Atan2(mz, mx);
                        float comboOff = deg * MathF.PI / 180f;   // continuous stick bearing (not the snapped combo)
                        // Only recalibrate when motion roughly matches intent.
                        // A wall slide produces motion in the WALL's direction —
                        // feeding that back poisons the calibration and makes
                        // the walker orbit (the save-point circling bug).
                        float intended = wAngle + comboOff;
                        float devSigned = NormDeg((moveAng - intended) * 180f / MathF.PI);
                        float dev = MathF.Abs(devSigned);
                        if (dev < 50f)
                        {
                            if (!cam3D) wAngle = moveAng - comboOff;   // 3D: the live camera is authoritative
                            deviated = 0;
                        }
                        else if (!following && !arriving && ++deviated >= 3 && totalMs >= sidestepUntil)
                        {
                            // wall pushed our motion off-axis; sidestep on the
                            // side the wall deflected us TOWARD (go with the
                            // slide, not back into the wall). NOT while ARRIVING: at the
                            // destination the thing deflecting us is usually the TARGET
                            // itself (an NPC we bumped) — sidestepping it = "walking
                            // around the person a while" instead of just talking.
                            sidestepSign = devSigned > 0 ? +1 : -1;
                            sidestepUntil = totalMs + 750;
                            deviated = 0;
                            Log($"[OverworldNav] walk wall-slide detected (dev={devSigned:F0}°) — sidestep {(sidestepSign > 0 ? "+" : "-")}45°");
                        }
                        stuckMs = 0;
                    }
                    else stuckMs += 40;   // loop is now ~40ms/iteration (stick drive), not 250
                    lastX = nx; lastZ = nz;

                    if (totalMs - lastLogMs >= 400)   // movement diagnostic (~every 0.4s)
                    {
                        lastLogMs = totalMs;
                        Log($"[OverworldNav] move ({nx:F0},{nz:F0}) dist={dist:F0} deg={deg:F0} {(wholePosDrive ? "POSdrv" : "stick")} " +
                            $"spd={speed:F0} moved={mlen:F0} stuck={stuckMs} stalls={stalls} arr={arriving} " +
                            $"{(path == null ? "nopath" : "p" + path.Count + "/c" + carrot)} wf={wallFollow}");
                    }

                    if (stuckMs >= 500 && !arriving)   // CONFIRMED stall en route (NOT at the destination — there we just face+check)
                    {
                        // LEARN: stamp the wall just AHEAD of where we wedged so A* routes around it (this
                        // run + the session). Only on a real stall (not a slide, which has motion).
                        float adl = MathF.Sqrt(adx * adx + adz * adz);
                        if (adl > 1f)
                        {
                            float ahx = nx + adx / adl * MeshGrid.Cell, ahz = nz + adz / adl * MeshGrid.Cell;
                            areaObs.Add(new[] { ahx, ahz });
                            Log($"[OverworldNav] learned wall at ({ahx:F0},{ahz:F0}) — {areaObs.Count} total");
                        }
                        stuckMs = 0;
                        if (++stalls > 14)   // too many = genuinely unreachable
                        {
                            ReleaseKeys(held); ControllerInput.ReleaseDriveStick();
                            int steps = Math.Max(1, (int)MathF.Round(dist / UnitsPerStep));
                            Speech.Say($"Couldn't reach {t.Name}. It's {steps} steps away.", true);
                            Log($"[OverworldNav] gave up (too many stalls) at ({nx:F0},{nz:F0}) dist={dist:F0}");
                            return;
                        }
                        // Back off the wall a touch (so we're free to take the detour), then REPLAN around
                        // the learned walls — optimistic A* finds the way around the stamped obstacle.
                        if (wholePosDrive)
                        {
                            // position-drive back-off: write the player a touch back opposite the aim.
                            float bl = MathF.Sqrt(adx * adx + adz * adz);
                            if (bl > 1f)
                                for (int s = 0; s < 6; s++)
                                {
                                    var (cbx, _, cbz, cok) = FieldTracker.WorldPlayerPos();
                                    if (!cok) break;
                                    FieldTracker.WritePlayerPos(cbx - adx / bl * 8f, cbz - adz / bl * 8f);
                                    Thread.Sleep(10);
                                }
                            totalMs += 70;
                        }
                        else
                        {
                            float backRad = (deg + 180f) * (MathF.PI / 180f);
                            ControllerInput.DriveStickXY(0x80 + (int)MathF.Round(MathF.Sin(backRad) * 80),
                                                         0x80 - (int)MathF.Round(MathF.Cos(backRad) * 80));
                            Thread.Sleep(250); totalMs += 250;
                        }
                        Replan(nx, nz);
                        Log($"[OverworldNav] stall #{stalls} → detour {(path == null ? "no-path" : path.Count + "pts")}");
                        continue;
                    }
                }
                lastDist = dist;
            }
            ReleaseKeys(held);
            if (totalMs >= 45000) { Speech.Say("Walk timed out.", true); Log("[OverworldNav] walk END (timeout)"); }
            else if (!_walking) Log("[OverworldNav] walk END (cancelled)");
        }
        catch (Exception ex)
        {
            Log($"[OverworldNav] walk error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ControllerInput.ReleaseDriveStick();   // hand the stick back to the player on ANY exit
            ReleaseKeys(held);
            _walking = false;
        }
    }

    // ── wall-aware steering (3D field areas: the school) ─────────────────────────
    // The collision mesh is in worldPos space (verified live), so we ray-cast from worldPos.
    private static readonly List<float> _segBuf = new();   // flat XZ segments: ax,az,bx,bz …

    /// <summary>Refine the greedy combo: among the 8 directions, pick the one with the most
    /// clearance to a wall that still heads toward the target. Keeps it in corridors / steers it
    /// around walls instead of grinding. Returns the combo index; <paramref name="clearance"/> = its
    /// ray distance to the nearest wall.</summary>
    private int PickClearCombo(int greedy, float bearingDeg, float px, float pz, float wAngle, out float clearance)
    {
        const float Safe = 280f;    // want ~2 steps of room ahead
        const float MaxR = 700f;
        GatherWallSegs(_segBuf);
        if (_segBuf.Count == 0) { clearance = MaxR; return greedy; }   // no walls read → trust greedy

        int bestI = greedy; float bestScore = float.NegativeInfinity; clearance = 0f;
        for (int i = 0; i < KeyDirs.Length; i++)
        {
            float a = wAngle + KeyDirs[i].ang * MathF.PI / 180f;
            float clr = CastRay(_segBuf, px, pz, MathF.Cos(a), MathF.Sin(a), MaxR);
            float ad = MathF.Abs(bearingDeg - KeyDirs[i].ang); if (ad > 180f) ad = 360f - ad;
            // Clear directions always beat blocked ones; among clear, the most target-aligned wins.
            float clearScore = clr >= Safe ? 1f : clr / Safe;
            float score = clearScore * 1000f - ad;
            if (score > bestScore) { bestScore = score; bestI = i; clearance = clr; }
        }
        return bestI;
    }

    private static void GatherWallSegs(List<float> outSegs)
    {
        outSegs.Clear();
        FieldTracker.VisitWallTrianglesInScene(t =>
        {
            if (t.Length < 9) return;
            outSegs.Add(t[0]); outSegs.Add(t[2]); outSegs.Add(t[3]); outSegs.Add(t[5]);
            outSegs.Add(t[3]); outSegs.Add(t[5]); outSegs.Add(t[6]); outSegs.Add(t[8]);
            outSegs.Add(t[6]); outSegs.Add(t[8]); outSegs.Add(t[0]); outSegs.Add(t[2]);
        });
    }

    /// <summary>Nearest wall-hit distance from (ox,oz) along unit (dx,dz), capped at maxDist.</summary>
    private static float CastRay(List<float> segs, float ox, float oz, float dx, float dz, float maxDist)
    {
        float best = maxDist;
        for (int i = 0; i + 3 < segs.Count; i += 4)
        {
            float ax = segs[i], az = segs[i + 1];
            float ex = segs[i + 2] - ax, ez = segs[i + 3] - az;   // segment direction
            float denom = dx * ez - dz * ex;
            if (MathF.Abs(denom) < 1e-6f) continue;               // parallel
            float aox = ax - ox, aoz = az - oz;
            float tt = (aox * ez - aoz * ex) / denom;             // distance along ray
            float u = (aox * dz - aoz * dx) / denom;              // param along segment
            if (tt >= 0f && tt < best && u >= 0f && u <= 1f) best = tt;
        }
        return best;
    }

    private static void HoldKeys(List<ushort> held, ushort[] want)
    {
        foreach (var k in held.ToArray())
            if (Array.IndexOf(want, k) < 0)
            {
                keybd_event(0, (byte)k, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
                held.Remove(k);
            }
        foreach (var k in want)
            if (!held.Contains(k))
            {
                keybd_event(0, (byte)k, KEYEVENTF_SCANCODE, UIntPtr.Zero);
                held.Add(k);
            }
    }

    private static void ReleaseKeys(List<ushort> held)
    {
        foreach (var k in held)
            keybd_event(0, (byte)k, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
        held.Clear();
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>Normalise an angle in degrees to [-180, 180].</summary>
    private static float NormDeg(float d)
    {
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    private static void WinBeep(uint freq, uint ms) { try { Beep(freq, ms); } catch { } }
    [DllImport("kernel32.dll")] private static extern bool Beep(uint dwFreq, uint dwDuration);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // AV-safe page check (.NET 9 cannot catch AVE). UNLIKE the older copies,
    // this validates the WHOLE [addr, addr+size) range — the single-region
    // versions silently approve reads that cross into an unreadable page,
    // which is the suspected AVE class behind the 2026-06-12 session crashes.
    [DllImport("kernel32.dll")]
    private static extern unsafe nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static unsafe bool IsReadable(nint addr, int size)
    {
        if (addr == 0 || size <= 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a + (ulong)size > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48;
        const int OFF_BASE = 0;          // BaseAddress
        const int OFF_REGIONSIZE = 24;   // RegionSize
        const int OFF_STATE = 32;
        const int OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        ulong end = a + (ulong)size;
        ulong cursor = a;
        int guard = 0;
        while (cursor < end && guard++ < 8)
        {
            if (VirtualQuery((nint)cursor, buf, MBI_SIZE) == 0) return false;
            uint state = *(uint*)(buf + OFF_STATE);
            uint protect = *(uint*)(buf + OFF_PROTECT);
            if (state != MEM_COMMIT) return false;
            if ((protect & PAGE_NOACCESS) != 0) return false;
            if ((protect & PAGE_GUARD) != 0) return false;
            ulong regBase = *(ulong*)(buf + OFF_BASE);
            ulong regSize = *(ulong*)(buf + OFF_REGIONSIZE);
            cursor = regBase + regSize;
        }
        return cursor >= end;
    }
}
