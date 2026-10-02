using System.Runtime.InteropServices;
using System.Text.Json;
using DavyKager;
using p4g64.accessibility.Native.Text;
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
        public string? ModelKey;  // live NPC model base-id ("n1851"); null = unknown / not a person
        // The game's OWN check-prompt label for this trigger (2026-09-29): h-row label id baked into
        // the catalog; text resolved live in the game's language (GameText.FieldLabel). 0 = none.
        public int LabelId;
        // The catalog name before any label rename - the stable key for learned spots (CalibKey).
        public string? BaseName;
        // The trigger's GATE (2026-09-29): a FlowScript BIT the game requires ON for this trigger to exist
        // (h-row i32 @+0 — Ema/Fortune Hanger 3263, Trophy 3237, fld_debug_menu 3403…). -1 = always live.
        public int[]? GateBits;   // FlowScript BITs that must ALL be ON for the trigger to exist (null = always)
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
    // Per-INSTANCE renames (npc_instance_names.json, 2026-09-04): "area:idhex" -> entries.
    // An entry with a Model applies only when the live model matches - the same slot id
    // holds DIFFERENT people on different days / story states (Dojima vs an old woman;
    // Yumi vs Ayane by club choice - user report). Model "" = legacy any-model entry.
    private readonly Dictionary<string, List<(string Model, string Base, string Name)>> _npcInstanceNames = new();
    // Once-per-area people dump so the log accumulates (slot, model) pairs across days.
    private string _dumpAreaKey = "";
    private string _dumpSig = "";
    private long _dumpNextMs;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _minusWas, _plusWas, _lbWas, _rbWas, _bsWas, _bkWas, _pWas;

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

    // NO-ROUTING areas (2026-07-06, the big town test): grid routing is DISABLED here —
    // these walk with the plain straight-line walker. "8_9" = Tatsuhime Shrine, one of the
    // 5 KNOWN mis-framed walkgrids (translation-offset bug — the player can be in-bounds so
    // the out-of-grid guard can't catch it). "11_1" = Okina City: MULTI-LEVEL (station
    // stairs/escalators) — a flat walkgrid cannot represent level boundaries, so its routes
    // are fiction (live: every waypoint "unreachable", 14 stalls marching the walk BACKWARDS).
    // The runtime rule self-adds any area that proves bad live (2 dropped paths in one
    // walk) — but only for 15 MINUTES, and never persisted: a TRANSIENT glitch (NPC
    // parked on the route, a load hiccup) must not degrade a player's area for good
    // (user concern 2026-07-06). A genuinely bad map just re-earns its entry cheaply;
    // blacklisted = the proven v1 straight-line walker, never "broken".
    private static readonly HashSet<string> _noRouteAreas = new();   // 8_9 + 11_1 routed again 2026-09-29: both grids were mis-framed (collision bone translation ignored) — rebuilt   // 8_9 routed again 2026-09-29: its grid was mis-framed (collision bone translation ignored) — fixed
    private static readonly Dictionary<string, long> _noRouteUntil = new();
    private const long NoRouteMs = 15 * 60_000;

    // Learned wall points per area (Stage 2): when the body wedges, we stamp the obstacle just ahead so
    // A* routes around it. PERSISTED to overworld_obstacles.json since 2026-07-06 (town item B) — the
    // overworld's geometry never changes, so every wedge ever hit keeps teaching future walks, across
    // sessions and (bundled) across players — the dungeon pathfinder's learn-by-grinding, made permanent.
    private readonly Dictionary<string, List<float[]>> _obstacles = new();
    private bool _obsLoaded;
    private string _obsPath = "";
    private const int MaxObsPerArea = 200;

    private List<float[]> ObstaclesFor(string area)
    {
        if (!_obsLoaded) LoadObstacles();
        if (!_obstacles.TryGetValue(area, out var l)) { l = new(); _obstacles[area] = l; }
        return l;
    }

    private void LoadObstacles()
    {
        _obsLoaded = true;
        try
        {
            _obsPath = DataPath("overworld_obstacles.json");
            if (!System.IO.File.Exists(_obsPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(_obsPath));
            int pts = 0;
            foreach (var area in doc.RootElement.EnumerateObject())
            {
                var l = new List<float[]>();
                foreach (var pt in area.Value.EnumerateArray())
                    l.Add(new[] { pt[0].GetSingle(), pt[1].GetSingle() });
                _obstacles[area.Name] = l;
                pts += l.Count;
            }
            Log($"[OverworldNav] learned obstacles loaded: {pts} points in {_obstacles.Count} areas");
        }
        catch (Exception e) { Log($"[OverworldNav] obstacles load failed: {e.Message}"); }
    }

    private void SaveObstacles()
    {
        try
        {
            if (string.IsNullOrEmpty(_obsPath)) _obsPath = DataPath("overworld_obstacles.json");
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var kv in _obstacles)
            {
                if (kv.Value.Count == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"\n  \"{kv.Key}\": [");
                for (int i = 0; i < kv.Value.Count; i++)
                    sb.Append($"{(i > 0 ? "," : "")}[{kv.Value[i][0]:F0},{kv.Value[i][1]:F0}]");
                sb.Append(']');
            }
            sb.Append("\n}");
            System.IO.File.WriteAllText(_obsPath, sb.ToString());
        }
        catch (Exception e) { Log($"[OverworldNav] obstacles save failed: {e.Message}"); }
    }

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
            LoadInstanceNames();

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
                        int labelId = it.TryGetProperty("labelId", out var li) && li.ValueKind == JsonValueKind.Number
                            ? li.GetInt32() : 0;
                        string baseName = PlaceNames.Translate(name ?? proc ?? "Unknown trigger");
                        // gate [bit,-1,-1] = live only while the bit is ON. 3-value rows = ALL listed bits ON
                        // (2026-09-30, slot 11: Cushion [3254,12,1621] with 3254 off draws no prompt; its script
                        // clears 1621 after giving the item, so 1621 must be part of the gate for it to vanish).
                        int[]? gateBits = null;
                        if (it.TryGetProperty("gate", out var ga) && ga.ValueKind == JsonValueKind.Array)
                        {
                            var gl = new List<int>();
                            foreach (var gv in ga.EnumerateArray()) { int b = gv.GetInt32(); if (b >= 0) gl.Add(b); }
                            if (gl.Count > 0) gateBits = gl.ToArray();
                        }
                        area.Targets.Add(new Target
                        {
                            Name = baseName,
                            BaseName = baseName,
                            LabelId = labelId,
                            GateBits = gateBits,
                            Proc = proc,
                            // The HBN box position is the trigger CENTRE (|ext| = full size) —
                            // proven 2026-09-29 against the 100 learned spots: 40 fit only the
                            // centre reading, 8 only the old "corner + ext/2" one (38 both). The
                            // old reading aimed every box target half a box off (Street exit 1:
                            // 560u east of the real "Leave the shopping district?" spot).
                            X = bx,
                            Y = it.GetProperty("y").GetSingle(),
                            Z = bz,
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

    /// <summary>An area's catalog triggers as (proc, box X, box Z) — for DungeonNav's LOBBY exits
    /// (enter_* / return_* / save_point / velvet_room). The box position is the trigger CENTRE
    /// (live-verified 2026-09-29: the game's prompt spots sit inside the box around it).</summary>
    internal static List<(string proc, float x, float z)> CatalogTriggers(int major, int minor)
    {
        var list = new List<(string, float, float)>();
        var inst = Instance;
        if (inst == null || !inst._catalog.TryGetValue($"{major:000}_{minor:000}", out var a)) return list;
        foreach (var t in a.Targets)
            if (t.Proc != null) list.Add((t.Proc, t.BX, t.BZ));
        return list;
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
        // IDENTITY (2026-09-29): credit the object whose game label the prompt DRAWS. The old
        // "nearest trigger" rule taught wrong spots (Fishing Equipment learned a spot where the
        // TV's prompt lights). No label drawn → only a label-less target (exits) may be credited.
        string? drawn = CheckLabel.FreshLabel();
        string drawnNorm = drawn != null ? NormLabel(drawn) : "";
        Target best = default; float bestD = float.MaxValue; bool found = false;
        foreach (var tt in area.Targets)
        {
            string? own = GameText.FieldLabel(tt.LabelId);
            if (own != null ? !LabelIs(tt.LabelId, drawnNorm) : drawnNorm.Length > 0 && LabelBelongsToOther(in tt, drawnNorm)) continue;
            if (own != null && drawnNorm.Length == 0) continue;
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

    // ── NPC NAMES FROM THE TALK WINDOW (2026-09-30, nav pt2 item 6) ──────────────────────
    // Field NPCs are named from their MODEL ("Townsperson", "Student") — the game names them in the
    // talk window ("Lazy student:"). When a conversation starts within 2 s of a lit CHECK prompt and
    // exactly ONE person stands within 220u (the next one ≥100u farther), the speaker label is that
    // person's name: pinned to area + slot id + model key (the exact occupant), saved to
    // npc_learned_names.json. Only generic model names are replaced — hand renames
    // (npc_instance_names.json) and named models (Chie, Dojima) always win. Never guessed.
    private long _promptSeenMs;
    private long _learnedSpeakerTick = -1;
    private readonly Dictionary<string, string> _npcLearned = new();
    private bool _npcLearnedLoaded;
    // Story characters speak under their own name already — never relabel their models.
    private static readonly HashSet<string> StoryNpcNames = new(StringComparer.OrdinalIgnoreCase)
    { "Chie", "Yosuke", "Yukiko", "Rise", "Nanako", "Teddie", "Naoto", "Kanji", "Marie", "Namatame", "Dojima",
      "Mitsuo", "Fox", "Fox cub", "Margaret", "Protagonist", "Adachi", "Igor", "Elizabeth", "Yuta", "Nakajima" };

    private string LearnedPath() => System.IO.Path.Combine(
        string.IsNullOrEmpty(Utils.ModDir) ? AppContext.BaseDirectory : Utils.ModDir, "npc_learned_names.json");

    private void EnsureLearnedLoaded()
    {
        if (_npcLearnedLoaded) return;
        _npcLearnedLoaded = true;
        try
        {
            string p = LearnedPath();
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            foreach (var kv in doc.RootElement.EnumerateObject())
                if (kv.Value.GetString() is string v && v.Length > 0) _npcLearned[kv.Name] = v.Replace('\u3000', ' ').Trim();
            Log($"[NpcLearn] {_npcLearned.Count} learned NPC name(s) loaded");
        }
        catch (Exception ex) { Log($"[NpcLearn] load failed: {ex.Message}"); }
    }

    private string LearnedNpcName(string areaKey, ushort id, string modelKey, string current, string modelName)
    {
        EnsureLearnedLoaded();
        if (current != modelName || StoryNpcNames.Contains(current)) return current;   // hand rename / story name wins
        lock (_npcLearned)
            return _npcLearned.TryGetValue($"{areaKey}:{id:X4}:{modelKey}", out var n) ? n : current;
    }

    private void LearnNpcNameFromTalk()
    {
        long now = Environment.TickCount64;
        if (FieldTracker.CheckPromptActive) _promptSeenMs = now;
        string? spk = Dialogue.LastSpeakerName;
        if (spk == null || spk.Length == 0 || spk.Length > 40 || now - Dialogue.LastSpeakerTick > 1500) return;
        // Each spoken speaker LINE is judged once (by its draw tick, not by the name: a name-based "seen"
        // lost the "Refreshed old man" for the whole session — his first talk began while the walk was still
        // running and his second talk was skipped as already seen; a second "Old man" elsewhere would be too).
        if (Dialogue.LastSpeakerTick == _learnedSpeakerTick) return;
        _learnedSpeakerTick = Dialogue.LastSpeakerTick;
        if (now - _promptSeenMs > 2500) return;              // not a conversation the player started
        if (StoryNpcNames.Contains(spk)) return;             // a story voice never names a generic model
        var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) return;
        Target best = default; float d1 = float.MaxValue, d2 = float.MaxValue; bool any = false;
        foreach (var t in EnumerateLivePeople(px, pz))
        {
            float d = MathF.Sqrt((t.X - px) * (t.X - px) + (t.Z - pz) * (t.Z - pz));
            if (d < d1) { d2 = d1; d1 = d; best = t; any = true; }
            else if (d < d2) d2 = d;
        }
        if (!any || d1 > 220f || d2 < d1 + 100f || string.IsNullOrEmpty(best.ModelKey)) return;
        string baseName = ModelNameOf(best.ModelKey);
        if (StoryNpcNames.Contains(baseName) || spk.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return;
        string areaKey = $"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}";
        string key = $"{areaKey}:{best.PersonId:X4}:{best.ModelKey}";
        EnsureLearnedLoaded();
        lock (_npcLearned)
        {
            if (_npcLearned.TryGetValue(key, out var old) && old == spk) return;
            _npcLearned[key] = spk;
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(LearnedPath(), JsonSerializer.Serialize(_npcLearned, opts));
            }
            catch (Exception ex) { Log($"[NpcLearn] save failed: {ex.Message}"); }
        }
        Log($"[NpcLearn] {key} ({baseName}, {d1:F0}u away) = \"{spk}\"");
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
        try { LearnNpcNameFromTalk(); } catch (Exception ex) { Log($"[NpcLearn] error: {ex.Message}"); }
        if (!Utils.GameHasFocus()) return;   // don't process hotkeys while alt-tabbed
        if (SettingsMenu.IsOpen) return;     // settings menu owns input

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
#if DEBUG
        bk |= IsKeyDown(0x77) && !IsKeyDown(0x11);   // TEST HARNESS ONLY: F8 = Backspace (see DungeonNav)
#endif
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

        // (Removed 2026-06-22: the O-key PosDrive toggle. The final-step assist stays permanently ON — the
        // user never wants it off — and O is the party HP/SP key, so the binding was a conflict.)
    }

    // ── browser ──────────────────────────────────────────────────────────────

    // The call_* proc family = AREA TRANSITIONS (call_street_02 →
    // CALL_FIELD(8,1) etc. — verified in the decompiled flow) plus call_lmap
    // (the "Leave the street?" town-map prompt). The catalog resolves their
    // names to "Exit to <area>".
    /// <summary>Is the lit prompt (its drawn <paramref name="label"/>) for target <paramref name="t"/>?
    /// +1 when the label names t (at least as well as any other target of this area), or when it
    /// names NO target here and we stand close (the catalog's "Down to riverbank" draws as "Stairs").
    /// A label that names ANOTHER target here is never ours (the Pharmacy passed on the way).</summary>
    private int PromptIdentity(in Target t, string label, float px, float pz, float distArr)
    {
        // EXACT IDENTITY (2026-09-29): the trigger's own game label vs the drawn one. The fuzzy
        // name score below let "Calendar" confirm "Examine shelf" / "Examine my room TV" 200-330u
        // away (a tiny room: everything is within the old 380u fallback).
        string drawn = NormLabel(label);
        if (GameText.FieldLabel(t.LabelId) is not null)
            return LabelIs(t.LabelId, drawn) ? (distArr < 900f ? 1 : 0) : -1;
        if (LabelBelongsToOther(in t, drawn)) return -1;
        int mine = LabelScore(label, t.Name), bestOther = 0;
        var area = CurrentArea();
        if (area != null)
            foreach (var o in area.Targets)
            {
                if (o.Name == t.Name && MathF.Abs(o.BX - t.BX) < 1f && MathF.Abs(o.BZ - t.BZ) < 1f) continue;
                bestOther = Math.Max(bestOther, LabelScore(label, o.Name));
            }
        if (mine > 0 && mine >= bestOther) return distArr < 600f ? 1 : 0;
        if (bestOther > 0) return -1;
        // Names nothing here: ours when close OR standing inside our own trigger box (the Samegawa stairs
        // lit "Stairs" at 430u from the embankment box's centre — inside the 970u-wide box, 2026-09-29).
        return distArr < 380f || ((t.ExtX != 0 || t.ExtZ != 0) && InsideBox(in t, px, pz, 60f)) ? 1 : 0;
    }

    private static string NormLabel(string s)
        => string.Join(' ', (s ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // RUNTIME LABEL SWAPS (2026-09-30): the game redraws a few triggers under another label of the same
    // table — "Shiroku Store" (55) is "Shiroku Pub" (122) in the evening (slot 5, Apr 17: every walk ended
    // "Turn to face it" with the Pub prompt lit), "Open Land" (120) becomes "Vegetable Patch" (123) once
    // the garden is planted. No h-row carries 122/123, so the swap is in code; the drawn alias counts.
    private static readonly Dictionary<int, int[]> LabelAliases = new() { [55] = new[] { 122 }, [120] = new[] { 123 } };

    /// <summary>Does the drawn (normalised) label name this label id — itself or its runtime alias?</summary>
    private static bool LabelIs(int labelId, string drawnNorm)
    {
        if (drawnNorm.Length == 0) return false;
        if (GameText.FieldLabel(labelId) is string own && NormLabel(own) == drawnNorm) return true;
        if (LabelAliases.TryGetValue(labelId, out var al))
            foreach (int a in al)
                if (GameText.FieldLabel(a) is string alt && NormLabel(alt) == drawnNorm) return true;
        return false;
    }

    /// <summary>Is the player inside the box of the (other) trigger that owns this drawn label (+30u)?</summary>
    private bool LabelOwnerContains(in Target t, string drawnNorm, float px, float pz)
    {
        var area = CurrentArea();
        if (area == null || drawnNorm.Length == 0) return false;
        foreach (var o in area.Targets)
        {
            if (MathF.Abs(o.BX - t.BX) < 1f && MathF.Abs(o.BZ - t.BZ) < 1f) continue;
            if (LabelIs(o.LabelId, drawnNorm) && InsideBox(in o, px, pz, 30f)) return true;
        }
        return false;
    }

    /// <summary>Does a drawn label name ANOTHER labelled trigger of this area?</summary>
    private bool LabelBelongsToOther(in Target t, string drawnNorm)
    {
        var area = CurrentArea();
        if (area == null || drawnNorm.Length == 0) return false;
        foreach (var o in area.Targets)
        {
            if (MathF.Abs(o.BX - t.BX) < 1f && MathF.Abs(o.BZ - t.BZ) < 1f) continue;
            if (LabelIs(o.LabelId, drawnNorm)) return true;
        }
        return false;
    }

    /// <summary>Distance to the nearest live person (NPC) in this area, +∞ when none.</summary>
    private float NearestPersonDist(float px, float pz)
    {
        float best = float.PositiveInfinity;
        try
        {
            foreach (var p in EnumerateLivePeople(px, pz))
            {
                float d = MathF.Sqrt((p.X - px) * (p.X - px) + (p.Z - pz) * (p.Z - pz));
                if (d < best) best = d;
            }
        }
        catch { }
        return best;
    }

    /// <summary>How well a drawn prompt label names a catalog target: 3 same, 2 one contains the
    /// other, 1 a shared word (4+ letters), 0 unrelated. "Examine"/"Exit to" and commas ignored.</summary>
    private static int LabelScore(string label, string name)
    {
        static string Norm(string x)
        {
            x = x.ToLowerInvariant().Replace(",", " ").Replace("'", "");
            foreach (var pre in new[] { "examine ", "exit to ", "to " })
                if (x.StartsWith(pre)) x = x.Substring(pre.Length);
            return string.Join(' ', x.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        string a = Norm(label), b = Norm(name);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 3;
        if (a.Contains(b) || b.Contains(a)) return 2;
        var bw = b.Split(' ');
        foreach (var w in a.Split(' '))
            if (w.Length >= 4 && Array.IndexOf(bw, w) >= 0) return 1;
        return 0;
    }

    /// <summary>Developer leftovers in the catalog ("Fld Debug Menu" was listed as a Shrine Place).</summary>
    private static bool IsDevLeftover(Target t)
        => (t.Proc?.Contains("debug", StringComparison.OrdinalIgnoreCase) ?? false)
           || t.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase);

    private const int WideRingAfterMs = 3600;   // small sweep first, then the wide ring (2026-09-29)
    private const int RingSlotMs = 1500;        // per stand point: walk there, then step in toward the box

    /// <summary>WALKING PEOPLE (2026-09-29, "it said press to talk but there was no one"): a townsperson
    /// strolls out of talk range while "Talk available" is still being spoken. If they are moving,
    /// keep close behind them (≤8 s) so the talk prompt stays up until the player presses — stops the
    /// moment a dialogue starts, the walk is cancelled, or a standing person needs no follow.</summary>
    private void FollowUntilTalk(ushort personId, bool camLive, float wAngle)
    {
        long start = Environment.TickCount64, dlg0 = Dialogue.LastDialogTick;
        float lastNx = float.NaN, lastNz = 0f; bool moving = false, logged = false;
        try
        {
            while (Environment.TickCount64 - start < 8000 && _walking && Utils.GameHasFocus())
            {
                Thread.Sleep(80);
                if (Dialogue.LastDialogTick != dlg0) break;                 // the talk started
                if (!TryGetPersonPos(personId, out float nx, out float nz)) break;
                var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
                if (!ok) break;
                if (!float.IsNaN(lastNx) && (nx - lastNx) * (nx - lastNx) + (nz - lastNz) * (nz - lastNz) > 16f) moving = true;
                lastNx = nx; lastNz = nz;
                if (!moving) { if (Environment.TickCount64 - start > 1200) break; continue; }
                if (!logged) { logged = true; Log("[OverworldNav] person is walking — following until the talk starts"); }
                if (camLive)
                {
                    var (cfx, cfz) = FieldTracker.CameraForward3D();
                    if (MathF.Sqrt(cfx * cfx + cfz * cfz) > 0.1f) wAngle = MathF.Atan2(cfz, cfx);
                }
                float dx = nx - px, dz = nz - pz, d = MathF.Sqrt(dx * dx + dz * dz);
                if (d > 105f)
                {
                    float rad = MathF.Atan2(dz, dx) - wAngle, spd = Math.Clamp(d * 0.85f, 40f, 110f);
                    ControllerInput.DriveStickXY(0x80 + (int)MathF.Round(MathF.Sin(rad) * spd),
                                                 0x80 - (int)MathF.Round(MathF.Cos(rad) * spd));
                }
                else ControllerInput.ReleaseDriveStick();
            }
        }
        finally { ControllerInput.ReleaseDriveStick(); }
    }

    /// <summary>Up to 8 WALKABLE stand points around a trigger box (walkgrid, one cell of clearance),
    /// nearest to the player first; duplicates within 90u merged.</summary>
    private static List<(float x, float z)> RingStandPoints(in Target t, MeshGrid? g, float px, float pz)
    {
        var pts = new List<(float x, float z)>();
        float reach = MathF.Max(MathF.Abs(t.ExtX), MathF.Abs(t.ExtZ)) * 0.5f + 360f;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f, ux = MathF.Cos(a), uz = MathF.Sin(a);
            for (float d = 60f; d <= reach; d += 30f)
            {
                float x = t.BX + ux * d, z = t.BZ + uz * d;
                if (g == null) { if (d >= reach - 30f) pts.Add((x, z)); continue; }
                g.WorldToCell(x, z, out int r, out int c);
                // walkable is enough: stand points hug furniture and walls (Fishing Equipment's is a
                // 1-cell strip along the south wall); a clearance test rejected them all.
                if (!g.Blocked(r, c)) { pts.Add((x, z)); break; }
            }
        }
        var merged = new List<(float x, float z)>();
        foreach (var p in pts)
            if (!merged.Exists(m => (m.x - p.x) * (m.x - p.x) + (m.z - p.z) * (m.z - p.z) < 90f * 90f)) merged.Add(p);
        float qx = px, qz = pz;
        merged.Sort((a, b) => ((a.x - qx) * (a.x - qx) + (a.z - qz) * (a.z - qz)).CompareTo((b.x - qx) * (b.x - qx) + (b.z - qz) * (b.z - qz)));
        Log($"[OverworldNav] ring: {merged.Count} walkable stand points around {t.Name}");
        return merged;
    }

    private static bool IsExit(Target t)
        => t.Proc?.StartsWith("call_") ?? false;

    /// <summary>A walkable cell INSIDE the target's trigger box (+30u), nearest the box centre. False when the
    /// box centre itself is walkable (nothing to fix), the target has no box, or no such cell exists.
    /// BOX ACROSS A FENCE (2026-09-30, 7_1 "Open Land"): with the player position, unreachable cells never
    /// win, and when the nearest-the-centre cell is >1000u further to WALK than the best cell in the box
    /// (the field half behind the fence, reachable only out past the street's west end — the walk took that
    /// detour into a pole), the pick becomes walking distance + 2 × centre distance. Ordinary boxes keep the
    /// nearest-the-centre cell (a plain path score moved "Home entrance" off its doorstep).</summary>
    private static bool NearestFreeInBox(MeshGrid g, in Target t, out float x, out float z, float px = float.NaN, float pz = float.NaN)
    {
        x = t.X; z = t.Z;
        if (t.ExtX == 0 && t.ExtZ == 0 || t.PersonId != 0) return false;
        g.WorldToCell(t.BX, t.BZ, out int br, out int bc);
        bool centreFree = g.InBounds(br, bc) && !g.Blocked(br, bc);
        int[]? pd = float.IsNaN(px) ? null : g.PathDistances(px, pz);
        // A WALKABLE centre is fine unless it is far round a fence (7_1 "Home entrance": the centre inside
        // the house is grid-walkable only via the street's east end — the walk went 1500u round and gave up).
        if (centreFree && pd == null) return false;
        int walkCentre = centreFree ? pd![br * g.Cols + bc] : -1;
        float hx = MathF.Abs(t.ExtX) * 0.5f + 30f, hz = MathF.Abs(t.ExtZ) * 0.5f + 30f;
        g.WorldToCell(t.BX - hx, t.BZ - hz, out int r0, out int c0);
        g.WorldToCell(t.BX + hx, t.BZ + hz, out int r1, out int c1);
        float bestC = float.MaxValue, bestS = float.MaxValue; int walkC = 0, walkMin = int.MaxValue;
        float sx = 0, sz = 0;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                if (!g.InBounds(r, c) || g.Blocked(r, c)) continue;
                g.CellToWorld(r, c, out float cx, out float cz);
                if (MathF.Abs(cx - t.BX) > hx || MathF.Abs(cz - t.BZ) > hz) continue;
                int w = pd != null ? pd[r * g.Cols + c] : 0;
                if (w < 0) continue;   // unreachable from here
                float d = MathF.Sqrt((cx - t.BX) * (cx - t.BX) + (cz - t.BZ) * (cz - t.BZ));
                if (d < bestC) { bestC = d; x = cx; z = cz; walkC = w; }
                walkMin = Math.Min(walkMin, w);
                float score = w * MeshGrid.Cell + 2f * d;
                if (score < bestS) { bestS = score; sx = cx; sz = cz; }
            }
        if (bestC == float.MaxValue) return false;
        if (centreFree)
        {
            if (walkCentre >= 0 && (walkCentre - walkMin) * MeshGrid.Cell <= 1000f) { x = t.X; z = t.Z; return false; }
            Log($"[OverworldNav] box across a wall ({t.Name}): aim ({sx:F0},{sz:F0}) — the walkable box centre is {(walkCentre < 0 ? "unreachable" : $"{(walkCentre - walkMin) * MeshGrid.Cell:F0}u further to walk")}");
            x = sx; z = sz;
            return true;
        }
        if (pd != null && (walkC - walkMin) * MeshGrid.Cell > 1000f)
        {
            Log($"[OverworldNav] box across a wall ({t.Name}): aim ({sx:F0},{sz:F0}) — the cell nearest the centre ({x:F0},{z:F0}) is {(walkC - walkMin) * MeshGrid.Cell:F0}u further to walk");
            x = sx; z = sz;
        }
        return true;
    }

    private const long FlagBitmapPtr = 0x1451FF7A0L;   // *(byte**) → the FlowScript BIT bitmap

    // What a place is FOR, when the game's own label doesn't say (Haru 2026-09-29: the fortune is drawn at
    // "Yashiro", not at the "Fortune Hanger"). List-only suffix; the arrival still matches the game label.
    private static readonly Dictionary<string, string> PlaceHints = new()
    {
        ["jinjya_yashiro"] = "draw a fortune",
    };

    /// <summary>Is this trigger switched ON by the game right now? (its h-row gate BIT; unreadable → live)</summary>
    private static unsafe bool TriggerLive(in Target t)
    {
        if (t.GateBits == null) return true;
        nint bm;
        if (!Utils.TryReadRaw((nint)FlagBitmapPtr, &bm, 8) || bm == 0) return true;
        foreach (int bit in t.GateBits)
        {
            uint word;
            if (!Utils.TryReadRaw(bm + (bit >> 5) * 4, &word, 4)) return true;
            if ((word & (1u << (bit & 31))) == 0) return false;
        }
        return true;
    }

    /// <summary>UNREACHABLE EXITS (2026-09-29): some areas carry leftover exit triggers OUTSIDE the game's own
    /// wall outline — Okina's Street exits 2/3 sit past both closed street ends (driven: the body stops at
    /// x≈-1414). The prebuilt walkgrid is reach-filtered (cells a body can get to from where players really
    /// stood), so an exit with no walkable cell within 150u of its box can never be walked into — don't
    /// offer it. No grid for the area → keep it (unknown ≠ unreachable).</summary>
    private bool ExitReachable(in Target t)
    {
        string key = $"{FieldTracker.CurrentMajor:000}_{FieldTracker.CurrentMinor:000}";
        if (!_walkgrid.TryGetValue(key, out var w)) return true;
        float hx = MathF.Abs(t.ExtX) * 0.5f + 150f, hz = MathF.Abs(t.ExtZ) * 0.5f + 150f;
        const float cell = 60f;
        int c0 = Math.Max(0, (int)MathF.Floor((t.BX - hx - w.ox) / cell)), c1 = Math.Min(w.cols - 1, (int)MathF.Floor((t.BX + hx - w.ox) / cell));
        int r0 = Math.Max(0, (int)MathF.Floor((t.BZ - hz - w.oz) / cell)), r1 = Math.Min(w.rows - 1, (int)MathF.Floor((t.BZ + hz - w.oz) / cell));
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (w.walk[r * w.cols + c]) return true;
        return false;
    }
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
            FinishSort(list);
            return list;
        }

        var area = CurrentArea();
        if (area == null) return list;
        // GAME LABELS AS NAMES (2026-09-29): an object is named by the label the game draws on its
        // CHECK prompt ("Calendar", not our proc-derived "Examine diary") - what the player hears
        // on arrival is what they picked. Repeats get a stable number (catalog order).
        var labelTotal = new Dictionary<string, int>();
        foreach (var t in area.Targets)
            if (!IsExit(t) && TriggerLive(in t) && GameText.FieldLabel(t.LabelId) is string gl0)
                labelTotal[gl0] = labelTotal.GetValueOrDefault(gl0) + 1;
        var labelSeen = new Dictionary<string, int>();
        foreach (var t0 in area.Targets)
        {
            var t = t0;
            // GATED TRIGGERS (2026-09-29): the game switches some triggers off by a story BIT — the list
            // offered the Shrine's hangers, the Trophy, Fishing Equipment and dead street exits that have
            // no check at this point in the story (walks "arrived" on a neighbour or stopped short).
            if (!TriggerLive(in t)) continue;
            if (!IsExit(t) && GameText.FieldLabel(t.LabelId) is string gl)
            {
                labelSeen[gl] = labelSeen.GetValueOrDefault(gl) + 1;
                t.Name = labelTotal[gl] > 1 ? $"{gl} {labelSeen[gl]}" : gl;
            }
            if (t.Proc != null && PlaceHints.TryGetValue(t.Proc, out var hint)) t.Name = $"{t.Name}, {hint}";
            bool match = cat switch
            {
                Cat.Exits => IsExit(t) && ExitReachable(in t),
                Cat.Places => !IsExit(t) && !IsStreet(t) && !t.Boundary && t.Proc != null && !IsDevLeftover(t),
                Cat.Other => !IsExit(t) && (IsStreet(t) || t.Boundary || t.Proc == null),
                _ => false,
            };
            if (!match) continue;
            float dx = t.X - px, dz = t.Z - pz;
            list.Add((t, MathF.Sqrt(dx * dx + dz * dz)));
        }
        FinishSort(list);
        return list;
    }

    /// <summary>Distance order by default; alphabetical (numeric-aware) when the
    /// "Navigation sorting" setting says so (2026-08-31 - overworld always honors it).</summary>
    private static void FinishSort(List<(Target t, float dist)> list)
    {
        if (ModSettings.GetInt("nav_sort", Defaults.NavSort) == 1)
            list.Sort((a, b) => DungeonNav.NaturalCompare(a.t.Name ?? "", b.t.Name ?? ""));
        else
            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
    }

    // CHECK-prompt NAMING was built and REMOVED 2026-07-09. It named the field
    // interactable under the "Check" prompt by geometry (trigger-box containment +
    // facing over the catalog + live NPCs). It reached ~90% but the misses were
    // confidently-WRONG names (sofa→"small desk", unlabelled save point), which
    // misdirect a blind player worse than the honest plain "Check". The game never
    // exposes the selected field object to read exactly (overworld slot arrays null,
    // scanner FUN_1402EDB70 hook crashes, no on-screen interactable name — the banner
    // is the ZONE name; deep-research confirmed no community solution exists). Do not
    // re-add without a real selected-object source. EnumerateLivePeople below stays —
    // the nav browser uses it.

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
                string mkey = TryGetNpcModelKey(cur) ?? "";
                string name = mkey.Length > 0 ? ModelNameOf(mkey) : $"NPC {person}";
                string modelName = name;
                name = OverrideNpcName(areaKey, id, mkey, name);
                if (mkey.Length > 0) name = LearnedNpcName(areaKey, id, mkey, name, modelName);
                outp.Add(new Target { Name = name, X = bx, Z = bz, PersonId = id, ModelKey = mkey });
            }
            if (!IsReadable(cur + OFF_NODE_NEXT, 8)) break;
            cur = *(nint*)(cur + OFF_NODE_NEXT);
        }
#if DEBUG
        DumpPeopleOnce(areaKey, outp);   // [NpcDump] authoring log (dev builds only, 2026-10-02)
#endif
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
    private string OverrideNpcName(string areaKey, ushort id, string modelKey, string baseName)
    {
        if (!_npcInstanceNames.TryGetValue($"{areaKey}:{id:X4}", out var list)) return baseName;
        string byBase = "", generic = "";
        foreach (var (model, bas, name) in list)
        {
            if (model.Length > 0) { if (model == modelKey) return name; continue; }   // model-pinned: exact occupant
            if (bas.Length > 0) { if (bas == baseName && byBase.Length == 0) byBase = name; continue; }
            if (generic.Length == 0) generic = name;
        }
        // Base-pinned: applies only while the slot reads as the generic label the rename was
        // written against ("Townsperson", "Drama club member"…) — a NAMED occupant (Naoto n609
        // in Adachi's 9_4 slot, user-caught 2026-09-04; Teddie; "Old woman") keeps its own name.
        if (byBase.Length > 0) return byBase;
        return generic.Length > 0 ? generic : baseName;   // legacy any-occupant entry
    }

    /// <summary>npc_instance_names.json: {"entries":[{"area":"8_2","id":"0C1C","model":"n1620","name":"Dojima"}, ...]}.
    /// "model" (exact occupant) and "base" (the table label the slot must currently read as) are optional pins. Editable,
    /// no rebuild (restart the game).</summary>
    private void LoadInstanceNames()
    {
        string ip = DataPath("npc_instance_names.json");
        if (!File.Exists(ip)) { Log("[OverworldNav] npc_instance_names.json not found - no per-instance renames"); return; }
        try
        {
            using var idoc = JsonDocument.Parse(File.ReadAllText(ip));
            int n = 0;
            foreach (var e in idoc.RootElement.GetProperty("entries").EnumerateArray())
            {
                string area = e.GetProperty("area").GetString() ?? "";
                string idh = e.GetProperty("id").GetString() ?? "";
                if (idh.StartsWith("0x") || idh.StartsWith("0X")) idh = idh.Substring(2);
                if (!int.TryParse(idh, System.Globalization.NumberStyles.HexNumber, null, out int idv)) continue;
                string model = e.TryGetProperty("model", out var mv) ? (mv.GetString() ?? "") : "";
                string bas = e.TryGetProperty("base", out var bv) ? (bv.GetString() ?? "") : "";
                string name = e.GetProperty("name").GetString() ?? "";
                if (area.Length == 0 || name.Length == 0) continue;
                string key = $"{area}:{idv:X4}";
                if (!_npcInstanceNames.TryGetValue(key, out var list)) _npcInstanceNames[key] = list = new();
                list.Add((model, bas, name));
                n++;
            }
            Log($"[OverworldNav] npc instance names loaded: {n}");
        }
        catch (Exception ex) { Log($"[OverworldNav] npc instance names load failed: {ex.Message}"); }
    }

    /// <summary>One log line per area whenever the (slot, model) set changes - people stream
    /// in over a few frames and change by date, so the log accumulates every occupant a slot
    /// ever had. Rate-limited to one line per 5s.</summary>
    private void DumpPeopleOnce(string areaKey, List<Target> people)
    {
        if (people.Count == 0) return;
        long now = Environment.TickCount64;
        if (now < _dumpNextMs) return;
        var sb = new System.Text.StringBuilder();
        foreach (var t in people)
            sb.Append(t.PersonId.ToString("X4")).Append('=').Append(string.IsNullOrEmpty(t.ModelKey) ? "?" : t.ModelKey)
              .Append('(').Append(t.Name).Append(") ");
        string sig = sb.ToString();
        if (areaKey == _dumpAreaKey && sig == _dumpSig) return;
        _dumpAreaKey = areaKey; _dumpSig = sig; _dumpNextMs = now + 5000;
        var (m, d) = FieldTracker.GameDate();
        Log($"[NpcDump] area {areaKey} date {m}/{d}: {sig.TrimEnd()}");
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

    private string ModelNameOf(string key) => _npcNames.TryGetValue(key, out var nm) ? nm : "Townsperson";

    private unsafe string? TryGetNpcModelName(nint node)
    {
        var k = TryGetNpcModelKey(node);
        return k == null ? null : ModelNameOf(k);
    }

    /// <summary>The live NPC's model base-id ("n1851") via the model-path chain, or null.</summary>
    private unsafe string? TryGetNpcModelKey(nint node)
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
        // Known model -> real name (ModelNameOf); unknown but valid NPC model -> one of
        // the anonymous shared-texture townsfolk ("Townsperson").
        return m.Groups[1].Value;
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
        // (BX,BZ) is the box CENTRE and |Ext| its full size (2026-09-29, proven on 100 learned spots).
        // The old "either corner" test covered TWICE the box: it bound the Samegawa stairs trigger
        // (down_embankment, centre -977) to a follow point 670u away, so the walk stopped far short.
        float hx = MathF.Abs(t.ExtX) * 0.5f, hz = MathF.Abs(t.ExtZ) * 0.5f;
        return InRect(t.BX - hx, t.BZ - hz, t.BX + hx, t.BZ + hz, px, pz, margin);
    }

    /// <summary>BOX IDENTITY (2026-09-29): +1 = standing in OUR trigger box (+30u) and in no other live box;
    /// -1 = inside another live trigger's box and clearly outside ours (+60u); 0 = can't tell (overlaps,
    /// no boxes). The second opinion for prompts that draw NO label (Save Point, exits) or a STALE one
    /// (the Bus Stop's label kept at South Street exit 1 next to it — touching zones never drop the prompt).</summary>
    private int BoxIdent(in Target t, float px, float pz)
    {
        var area = CurrentArea();
        if (area == null || (t.ExtX == 0 && t.ExtZ == 0)) return 0;
        bool inOther = false;
        foreach (var o in area.Targets)
        {
            if (MathF.Abs(o.BX - t.BX) < 1f && MathF.Abs(o.BZ - t.BZ) < 1f) continue;
            if ((o.ExtX == 0 && o.ExtZ == 0) || !TriggerLive(in o)) continue;
            if (InsideBox(in o, px, pz, 0f)) { inOther = true; break; }
        }
        if (!inOther && InsideBox(in t, px, pz, 30f)) return 1;
        if (inOther && !InsideBox(in t, px, pz, 60f)) return -1;
        return 0;
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
        // Skip EMPTY categories (2026-09-29, the P5R rule — "Other: none here" in almost every area).
        Cat cat = Categories[0];
        for (int tries = 0; tries < n; tries++)
        {
            _catIndex = _catIndex < 0 ? (dir > 0 ? 0 : n - 1) : (_catIndex + dir + n) % n;
            _cursor = 0;
            cat = Categories[_catIndex];
            _entries = BuildCategory(cat);
            if (_entries.Count > 0) break;
        }
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

    // -- STICKY SELECTION (2026-08-31): see DungeonNav - the live rebuild moved the
    // index off the entry the user was chasing. Relocate by name + nearest position.
    private string? _stickName; private float _stickX, _stickZ; private bool _stickValid;

    private void RememberStick()
    {
        if (_cursor >= 0 && _cursor < _entries.Count)
        { var t = _entries[_cursor].t; _stickName = t.Name; _stickX = t.X; _stickZ = t.Z; _stickValid = true; }
    }

    private void RelocateStick()
    {
        if (_cursor >= _entries.Count) _cursor = Math.Max(0, _entries.Count - 1);
        if (!_stickValid || _stickName == null) return;
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < _entries.Count; i++)
        {
            var t = _entries[i].t;
            if (t.Name != _stickName) continue;
            float d = (t.X - _stickX) * (t.X - _stickX) + (t.Z - _stickZ) * (t.Z - _stickZ);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) _cursor = best;
    }

    private void StepEntry(int dir)
    {
        if (_catIndex < 0) { Speech.Say("Pick a category with minus or equals first.", true); return; }
        Cat cat = Categories[_catIndex];
        _entries = BuildCategory(cat);
        if (_entries.Count == 0) { Speech.Say($"{CatName(cat)}: none here.", true); return; }
        RelocateStick();

        int next = _cursor + dir;
        if (next < 0) { Speech.Say("First. ", false); next = 0; }
        else if (next >= _entries.Count) { Speech.Say("Last. ", false); next = _entries.Count - 1; }
        _cursor = next;
        RememberStick();

        SetSelection(_entries[_cursor].t);
        Speech.Say($"{Say(_entries[_cursor])}. {_cursor + 1} of {_entries.Count}.", true);   // same format as the dungeon browser
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

    private static string CalibKey(Target t) => $"{(int)MathF.Round(t.BX)}_{(int)MathF.Round(t.BZ)}_{t.BaseName ?? t.Name}";

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
        {
            var (cfx, cfz) = FieldTracker.CameraForward3D();
            var (bpx, _, bpz, _) = FieldTracker.WorldPlayerPos();
            Log($"[OverworldNav] beacon on: {t.Name} at ({t.X:F0},{t.Z:F0}) player ({bpx:F0},{bpz:F0}) "
                + $"frame=screen camera=({cfx:F2},{cfz:F2})");
        }
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
                bool tight = IsRoomArea() && (t.Proc == "check_reizou" || t.Proc == "check_sofa_p4p");
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
                // TOWN SCREEN MODE (2026-09-30, nav pt2 item 16 — Haru: "the beacon in 2.5D maps is not
                // working really good"): the fixed world frame ignores which way each fixed camera looks
                // (a south-looking camera turns it into a mirror). The LIVE camera (CameraForward3D — the
                // same vector every town walk steers by since round 3; the old CameraForwardX/Z that was
                // rejected in June read (0,0) indoors) gives the screen frame: W = up the screen, D =
                // (−fz,fx) = right. No setting (Haru 09-30: "the Compass one is wrong anyway"); the fixed
                // world frame below is only the fallback while the camera is unreadable.
                float pan, openness;
                var (cfx, cfz) = FieldTracker.CameraForward3D();
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
                float gain = (FarGain + (NearGain - FarGain) * prox) * SoundSettings.NavVol; // louder as you approach
                int gap = NearGap + (int)((1f - prox) * (FarGap - NearGap));

                // Pitch pairs with the muffle axis (user 2026-07-06, matching the dungeon
                // NavBeacon): fully "closed" (school: behind the camera; town: the muffled
                // compass side) eases ~3 semitones down, so the two states are unmistakable.
                float rate = 1f - 0.18f * (1f - openness);
                _voice.Set(gain, pan, rate);
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
        ArrivalChime();
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
        // DIALOGUE GUARD (2026-09-30, nav pt2 item 11): a walk started while a message window is up
        // pushed the stick into the conversation for ~13 s ("grinds in place"). The DrawDialog hook
        // stamps LastDialogTick every frame a window with text/choices is visible.
        if (Environment.TickCount64 - Dialogue.LastDialogTick < 600)
        {
            Speech.Say("Finish the conversation first.", true);
            Log("[Nav] walk refused: a dialogue is on screen");
            return;
        }
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
        // EXITS aim at their trigger box, never a learned spot (2026-09-29): "Exit to Shopping District
        // North" had learned a spot 900u OUTSIDE its box facing west → every walk stopped short with
        // "push that way". An exit is a volume you walk INTO; the box is the truth.
        if (calib && IsExit(t) && (t.ExtX != 0 || t.ExtZ != 0)) calib = false;
        // A learned spot far OUTSIDE its trigger box is a mislearned one (the pre-label learner credited
        // the NEAREST trigger: Marutake Hobby Shop's spot sat 250u outside its box beside a wall → 13
        // stalls). Ignore it; the ring search + label-verified learning replace it.
        if (calib && (t.ExtX != 0 || t.ExtZ != 0) && FieldTracker.CurrentMajor != 6)
        {
            float hx = MathF.Abs(t.ExtX) * 0.5f, hz = MathF.Abs(t.ExtZ) * 0.5f;
            float ox = MathF.Max(0f, MathF.Abs(calX - t.BX) - hx), oz = MathF.Max(0f, MathF.Abs(calZ - t.BZ) - hz);
            if (ox * ox + oz * oz > 400f * 400f)
            {
                Log($"[OverworldNav] learned spot for {t.Name} is {MathF.Sqrt(ox * ox + oz * oz):F0}u outside its box — ignoring it");
                calib = false;
            }
        }
        long calibWrongSinceMs = -1;
        long promptOnMs = -1;   // when the current CHECK prompt lit (its label lands a moment later)
        if (calib) { t.X = calX; t.Z = calZ; }
        // REMOVED 2026-06-22: the old "adopt nearest scene object" step. TryNearestSceneObject is unreliable
        // in the school (it returns walls/props, not the door) and it DRAGGED targets onto the wrong thing —
        // it pulled "Sports Clubs" (undoubu box centre -5253,69) over to (-5200,206), right onto the Passage,
        // so the walk grabbed the passage's check. Use the catalog target (box centre/follow-point) directly;
        // the learned-spot system handles any per-target offset (e.g. the save's butterfly) far better.
        // Learned walls are PER-WALK only since 2026-09-29: persisted ones piled up in the MIDDLE of open
        // streets (NPC / pole bumps — 180 in the Shrine, most against its mis-framed grid) and bent every
        // later route. The grids are now built right (bone translations), so a stall teaches THIS walk only.
        var areaObs = new List<float[]>();
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
            // TOWN STEERING FROM THE LIVE CAMERA (2026-09-29): stick-forward = the camera's forward (XZ),
            // stick-right 90° counter-clockwise from it — the same camera the school/dungeon walkers
            // use. The per-walk stick PROBE it replaces measured ~6u of motion next to furniture and
            // mis-aimed the whole walk (Entrance: wedged on the door frame 3 s at (367,-93) while a
            // camera-steered probe walked straight onto the prompt), and its 0.2-0.9 s cost every walk.
            bool camLive = false;
            if (!wholePosDrive && FieldTracker.CurrentMajor != 6)
            {
                var (cf0x, cf0z) = FieldTracker.CameraForward3D();
                if (MathF.Sqrt(cf0x * cf0x + cf0z * cf0z) > 0.1f)
                {
                    wAngle = MathF.Atan2(cf0z, cf0x); camLive = true;
                    Log($"[OverworldNav] stick mapping from the live camera: forward angle={wAngle * 180 / MathF.PI:F0}°");
                }
            }
            if (!wholePosDrive && !camLive)
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
            float bestProgDist = float.MaxValue; int lastProgMs = 0;   // oscillating-wedge detector (2026-07-06)
            bool wallFollow = false; float wfHitDist = 0; int wfSign = +1, wfStartMs = 0;   // (legacy Bug2 — dormant)
            int stalls = 0;   // total stalls this walk — learn+detour each, give up after too many
            int lastLogMs = -1000;   // throttle the per-move diagnostic log
            int arriveStartMs = -1;  // when we entered ARRIVE mode (at the destination)
            int exitZoneMs = -1;     // when we first stood in an exit's volume without its prompt (2026-09-29)
            int objZoneMs = -1; bool faceSweepDone = false;   // in OUR object's zone, prompt dark → face-sweep once
            int burstUntilMs = -1, lastBurstMs = -10000;      // full-stick push when the arrival is wedged (a step)
            int exitDiagMs = -10000;   // [ExitDiag] throttle (DEBUG)
            int exitNudgeUntilMs = -1, lastExitNudgeMs = -10000;   // exit shows a NEIGHBOUR's prompt → push into the exit box
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
            int wdCarrot = -1; float wdBest = float.MaxValue; int wdMs = 0;   // waypoint orbit watchdog (2026-07-06)
            int pathDrops = 0;   // 2 dropped paths in one walk = this area's grid is bad → session no-route
            float faceX = 0, faceZ = 0;   // last world-space movement dir = the player's facing
            int sweepIdx = 0, sweepTickMs = 0;   // final search sweep to enter offset volumes + face them
            List<(float x, float z)>? ringPts = null;   // walkable stand points around the trigger box
            int ringIdx = 0, ringPhaseMs = 0; bool ringStepIn = false, ringDone = false;
            List<(float x, float z)>? arrivePath = null; int arrivePathMs = 0;   // short route around a corner at arrival
            int readyPolls = 0;                  // debounce: both flags must hold, not flicker
            int tFocus = 0; float tFocusDist = 9e9f;   // the target's focused-id, learned at closest approach

            // Re-plan the optimistic route from (fx,fz): rebuild the grid spanning to the target, stamp
            // known + learned walls, force endpoints walkable, A* + string-pull. Used after a wall-follow.
            string gridKind = "none";
            float snapTx = t.X, snapTz = t.Z;   // the A* target SNAPPED to a reachable cell (door/target)
            void Replan(float fx, float fz)
            {
                // No-routing areas: mis-framed or multi-level grids — routing there is
                // fiction; the straight-line walker is the sane behavior (2026-07-06).
                if (_noRouteAreas.Contains(areaAtStart)
                    || (_noRouteUntil.TryGetValue(areaAtStart, out long nrUntil)
                        && nrUntil > Environment.TickCount64))
                { path = null; partial = false; gridKind = "blacklist"; return; }
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
                        // (2026-09-29) KEEP the prebuilt PARTIAL route: it ends at the reachable cell nearest the
                        // target — exactly what the arrival logic wants. Nulling it here sent every town walk to
                        // a target just outside the reachable grid ("Stairs to my room", Street exits) straight-line
                        // into a wall ("nopath" from the start, then "Couldn't reach").
                        if (FieldTracker.CurrentMajor != 6) { if (path == null) { partial = false; gridKind = "none"; } break; }
                        MeshGrid.AccumulateScene(areaAtStart); g = MeshGrid.BuildAccumulated(p0y, 0, fx, fz, t.X, t.Z);
                    }
                    if (g == null) { path = null; partial = false; gridKind = "none"; continue; }
                    StampObstacles(g);
                    foreach (var o in areaObs) g.StampObstacle(o[0], o[1], 1);
                    // Only the OWN cell on the prebuilt (un-inflated) mask: a 1-cell radius around the learned
                    // fridge spot opened the entryway's north wall (7_2, 2026-09-30 — the route ran through it
                    // from the Entrance and ground 8 stalls: "Couldn't reach Fridge, 1 step away").
                    g.ClearAround(fx, fz, prebuilt ? 0 : 1);   // player is standing here = walkable
                    if (calib) g.ClearAround(calX, calZ, prebuilt ? 0 : 1);
                    // SNAP the A* target to the nearest WALKABLE cell. Catalog stand-points routinely sit
                    // ~100u beyond the floor (at a wall/door/trigger-box), which A* can never reach → PARTIAL.
                    // Route to the reachable cell next to it; the follower + 350u press-range cover the rest.
                    float ptx = t.X, ptz = t.Z;
                    g.WorldToCell(t.X, t.Z, out int trr, out int tcc);
                    // INSIDE THE BOX FIRST (2026-09-29, the door of your room): the nearest walkable cell to a
                    // non-walkable box centre can lie OUTSIDE the box (the door snapped to the sofa's spot);
                    // the prompt lights only standing IN the box facing the object. Prefer a walkable cell
                    // inside the box (+30u), nearest its centre; else the plain nearest walkable cell.
                    if (!NearestFreeInBox(g, in t, out ptx, out ptz, calib ? float.NaN : fx, fz)   // a learned spot stays the aim
                        && g.NearestFree(trr, tcc, out int tfr, out int tfc)) g.CellToWorld(tfr, tfc, out ptx, out ptz);
                    snapTx = ptx; snapTz = ptz;   // remember the reachable cell for the final-step approach
                    path = g.PlanWorld(fx, fz, ptx, ptz); carrot = 0;
                    partial = path != null && !g.LastReachedTarget;
                    if (prebuilt && (path == null || partial)
                        && TryPlanWithoutStatic(fx, fz, ptx, ptz, out var p2, out var g2))
                    { path = p2; partial = false; g = g2; carrot = 0; }
#if DEBUG
                    if (path == null && prebuilt)
                    {
                        // [TownPlanDiag] (TEMP 2026-09-29): why did A* find nothing? Dump the grid.
                        g.WorldToCell(fx, fz, out int dpr, out int dpc); g.WorldToCell(ptx, ptz, out int dtr, out int dtc);
                        Log($"[TownPlanDiag] NO PATH {areaAtStart} from ({fx:F0},{fz:F0}) cell ({dpr},{dpc}) to ({ptx:F0},{ptz:F0}) cell ({dtr},{dtc}) (grid follows)");
                        foreach (var gl in g.Ascii(dpr, dpc, dtr, dtc).Split('\n')) Log("[TownPlanDiag] " + gl);
                    }
#endif
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
            // OBSTACLES = live PEOPLE only (2026-09-29). Static catalog triggers were stamped too (a
            // 180u square each) from the pre-walkgrid days; in the 11-object living room those stamps
            // sealed the room → "PARTIAL" plans → the fridge walk pushed into the kotatsu for 10 s. The
            // walkgrid is the real collision now; trigger boxes sit ON walls/furniture it already has.
            var obstacles = new List<(float x, float z)>();
            try
            {
                foreach (var pp in EnumerateLivePeople(p0x, p0z))
                { float ddx = pp.X - t.X, ddz = pp.Z - t.Z; if (ddx * ddx + ddz * ddz > 70f * 70f) obstacles.Add((pp.X, pp.Z)); }
            }
            catch { }
            // Static triggers stay as a FIRST-TRY obstacle set: outdoors they stand in for props the
            // walkgrid lacks (the Shrine approach: without them the route ran into the gate). The plan
            // retries WITHOUT them when they seal the target off (the 11-object living room).
            var staticObs = new List<(float x, float z)>();
            { var ar1 = CurrentArea(); if (ar1 != null) foreach (var ot in ar1.Targets)
                { float ddx = ot.X - t.X, ddz = ot.Z - t.Z; if (ddx * ddx + ddz * ddz > 70f * 70f) staticObs.Add((ot.X, ot.Z)); } }
            bool useStatic = true;
            // the static stamps sealed the target off → replan with people (+ learned walls) only
            // RELAXATION LADDER: when our stamps seal the target off, drop them in order — first the
            // static trigger stamps (the 11-object living room), then this walk's LEARNED walls (the
            // Shrine: a stall at the 2-cell gate gap stamped a 3×3 wall INTO the only gap → every
            // replan led into the dead-end corridor). People always stay.
            bool TryPlanWithoutStatic(float fx2, float fz2, float ptx2, float ptz2,
                                      out List<(float x, float z)>? p2, out MeshGrid? g2)
            {
                p2 = null; g2 = null;
                for (int level = 0; level < 2; level++)
                {
                    if (level == 0 && (!useStatic || staticObs.Count == 0)) continue;
                    if (level == 1 && areaObs.Count == 0) continue;
                    g2 = PrebuiltGrid(areaAtStart);
                    if (g2 == null) return false;
                    foreach (var o in obstacles) g2.StampObstacle(o.x, o.z, 1);
                    if (level == 0) foreach (var o in areaObs) g2.StampObstacle(o[0], o[1], 1);
                    g2.ClearAround(fx2, fz2, 0);
                    if (calib) g2.ClearAround(calX, calZ, 0);
                    p2 = g2.PlanWorld(fx2, fz2, ptx2, ptz2);
                    if (p2 == null || !g2.LastReachedTarget) continue;
                    useStatic = false;
                    if (level == 1)
                    {
                        Log($"[OverworldNav] learned walls sealed the route ({areaObs.Count}) — cleared them");
                        areaObs.Clear();
                    }
                    else Log("[OverworldNav] static-object stamps sealed the route — replanned without them");
                    return true;
                }
                return false;
            }

            void StampObstacles(MeshGrid? g)
            {
                if (g == null) return;
                foreach (var o in obstacles) g.StampObstacle(o.x, o.z, 1);
                if (useStatic) foreach (var o in staticObs) g.StampObstacle(o.x, o.z, 1);
            }
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
            else if (t.PersonId == 0 || MathF.Sqrt((t.X - p0x) * (t.X - p0x) + (t.Z - p0z) * (t.Z - p0z)) > 700f)
            {
                // (2026-09-29) PEOPLE ROUTE TOO — when FAR (>700u); a close person keeps the proven direct
                // chase (routing Nanako across the small living room stalled 2 steps short). the straight live-chase failed every NPC walk that
                // started on the Samegawa stairs (a wall in every straight line). Route to where the
                // person stands now; the loop drops the route for the direct chase once close.
                // TOWN ROUTES AT WALK START (2026-07-06 pivot): the prebuilt walkgrid is
                // COMPLETE for town — there is nothing to "learn by grinding" (that loop
                // burned 90s and gave up on a small map, user verdict: impractical). The
                // June A*-to-door ROUTING was verified good ("reaches every shop"); what
                // broke it was arrival meddling (untouched now) + the waypoint ORBIT
                // (killed by the waypoint watchdog below). A PARTIAL route ends at the
                // reachable cell nearest the target = the street-side door — the proven
                // arrival takes it from there. NPCs keep the straight live-chase.
                Replan(p0x, p0z);
                Log(path != null
                    ? $"[OverworldNav] town plan ({gridKind}): {(partial ? "PARTIAL " : "")}{path.Count}pts → {t.Name}"
                    : $"[OverworldNav] town plan: NONE ({gridKind}) → {t.Name} — straight line");
#if DEBUG
                if (path != null && path.Count > 0)
                    Log("[TownPlanDiag] route: " + string.Join(" ", path.Take(12).Select(pp => $"({pp.x:F0},{pp.z:F0})")) + (path.Count > 12 ? " ..." : ""));
#endif
            }

            long walkT0 = Environment.TickCount64;
            while (_walking && !_stopped && totalMs < 45000)
            {
                // A CONVERSATION STARTED (2026-09-30, Haru's log): "Townsperson 1. Check available." was spoken,
                // Haru pressed and talked — but the walk ran on for 6 s under the dialogue and ended "Turn to face
                // them to talk". A message window that appears after the walk began = the player is talking to
                // (or being talked to by) someone: stop quietly, the dialogue reader has the floor.
                if (Dialogue.LastDialogTick > walkT0 + 300 && Environment.TickCount64 - Dialogue.LastDialogTick < 400)
                {
                    ReleaseKeys(held); ControllerInput.ReleaseDriveStick();
                    Log($"[OverworldNav] walk END (a conversation started) target {t.Name}");
                    return;
                }
                var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
                if (!ok) break;
                // live target refresh: townspeople walk around (keeps the chase on the moving ones)
                if (t.PersonId != 0 && TryGetPersonPos(t.PersonId, out float lx, out float lz))
                {
                    t.X = lx; t.Z = lz;
                }
                float dx = t.X - px, dz = t.Z - pz;
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                // A person's route only brings us NEAR them — from there the live chase (they move).
                // …but only when nothing solid stands between us (2026-09-30, school 6_6): Chie sits INSIDE
                // a 65u-tall desk block 31u from the right aisle, 439u from the door — the chase dropped
                // the aisle route at once and drove into the desk corner 15 times. Chase straight only when
                // the line to a point 110u short of the person is clear on the walk grid, or they are close.
                // After a stall the detour wins too: the static grid can't see PEOPLE (Yosuke standing in the
                // classroom's back corridor ground the "Timid female student" walk 15 times the same way).
                if (t.PersonId != 0 && path != null && dist < 450f)
                {
                    float sx = t.X - dx / dist * 110f, sz = t.Z - dz / dist * 110f;
                    if (dist < 150f || (stalls == 0 && (collGrid == null || collGrid.LineOfSight(px, pz, sx, sz))))
                    { path = null; partial = false; }
                }

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

                bool arriving = (path != null && distSnap < 90f) || distArr < 130f
                                || (ringPts != null && arriveStartMs >= 0);   // the ring search keeps its clock
                arriveStartMs = arriving ? (arriveStartMs < 0 ? totalMs : arriveStartMs) : -1;

                // CONFIRM. SCHOOL (major 6, packed interactables): TIGHT — the nudge lands the player on the
                // spot, so confirm on the real prompt 0x7F4 within 45u (calib: 0x7F4 within 40u, or essentially
                // ON the spot ≤12u). TOWN (2.5D): catalog targets sit ~80-110u INSIDE shops and the door-check
                // 0x7F4 fires at the building edge, so confirm on the game's REAL prompt 0x7F4 within a GENEROUS
                // 150u (tight distArr made every shop time out even with 0x7F4 ON — Save read 0x7F4=True at 109).
                bool school = FieldTracker.CurrentMajor == 6;
                bool npcT = t.PersonId != 0;
                // IDENTITY (2026-09-29 playtest): the game DRAWS the prompt's label ("Check: Bus Stop",
                // "Check: Stairs") — the arrival now asks WHOSE prompt is lit, not only how far our
                // aim point is. It only ADDS arrivals (every old rule still stands): the false
                // failures were all "prompt lit, aim point unreachable" — Bus Stop (learned spot
                // behind a pole, 119u), the Samegawa stairs (292u, spoken "Check: Stairs" 6x).
                string? plabel = FieldTracker.CheckPromptActive ? CheckLabel.FreshLabel() : null;
                if (string.IsNullOrWhiteSpace(plabel)) plabel = null;
                promptOnMs = FieldTracker.CheckPromptActive ? (promptOnMs < 0 ? totalMs : promptOnMs) : -1;
                int ident = plabel != null && !npcT ? PromptIdentity(in t, plabel, px, pz, distArr) : 0;   // +1 ours, -1 another target's
                int bid = !npcT && FieldTracker.CheckPromptActive ? BoxIdent(in t, px, pz) : 0;
                if (!npcT && FieldTracker.CheckPromptActive)
                {
                    // no label drawn, but we stand in another trigger's box → that trigger's prompt (the Bus
                    // Stop's learned spot sits in Street exit 1's box — "arrived" at the exit 7 times)
                    if (ident == 0 && plabel == null && bid < 0) ident = -1;
                    // our own label, but we stand in another trigger's box and well outside ours = a STALE label
                    // (the Bus Stop walk "arrived" in Street exit 1's box on the leftover "Bus Stop" label)
                    else if (ident > 0 && bid < 0) ident = -1;
                    // an EXIT whose box we stand in, the label naming an object whose box we are NOT in = stale
                    else if (ident < 0 && IsExit(t) && bid > 0 && !LabelOwnerContains(in t, NormLabel(plabel!), px, pz)) ident = 0;
                }
                bool npcPromptIsObject = npcT && plabel != null && LabelBelongsToOther(in t, NormLabel(plabel));
                // SELF-HEALING learned spots (2026-09-29): standing on the learned spot but the lit prompt
                // names ANOTHER object for ~1 s → the spot was mis-learned (the old nearest-trigger
                // learner). Forget it and aim at the catalog trigger instead.
                if (calib && ident < 0 && distArr < 160f)
                {
                    if (calibWrongSinceMs < 0) calibWrongSinceMs = totalMs;
                    else if (totalMs - calibWrongSinceMs > 1000)
                    {
                        lock (_calibLock) _calib.Remove(CalibKey(t));
                        SaveCalib();
                        calib = false;
                        Log($"[OverworldNav] learned spot for {t.Name} lights '{plabel}' — dropped it, aiming at the trigger");
                        arriveStartMs = -1;
                        continue;
                    }
                }
                else calibWrongSinceMs = -1;
                bool ours = ident > 0;
                bool ready;
                if (npcT)
                    // NPC: confirm the INSTANT the talk prompt (0x7F4) is on. distArr is to the person's CENTER,
                    // and their BODY stops us ~100-123u short of it (log: every NPC bumped + got 0x7F4 at
                    // distArr 103-123 but my old <90 was UNREACHABLE → it fell through to the 5s timeout = the
                    // long wait + NO arrival beep). 140u covers the bump so the proper ready path fires (with the
                    // sound). The chase faces the target, so 0x7F4 within 140u of it = THIS person's talk.
                    // ⚠ Checked BEFORE the school rule (2026-09-29): the school's 45u window failed Chie at 81u
                    // with her talk prompt lit.
                    ready = FieldTracker.CheckPromptActive && distArr < 140f && !npcPromptIsObject;
                else if (school)
                    // SCHOOL: the position-write nudge places the player + holds the facing, so the real check
                    // fires — calib can trust the spot (≤12u) or 0x7F4; non-calib needs 0x7F4 within 45u.
                    ready = ours || (calib ? ((FieldTracker.CheckPromptActive && distArr < 40f) || distArr < 12f)
                                           : (FieldTracker.CheckPromptActive && distArr < 45f));
                else if (IsExit(t))
                    // TOWN EXIT (call_* proc). Most are CHECK prompts you FACE and press ("Check: Shopping
                    // District, North", the Shrine torii, "Leave the shopping district?") — live 2026-09-29;
                    // a few are walk-into volumes (0x720).
                    // An UNNAMED lit prompt near the exit counts only when no person stands at us (a talk
                    // prompt draws the area banner = no label) — "Street exit 1. Press to go." fired beside
                    // the Bus Stop's check (2026-09-29).
                {
                    bool inZone = FieldTracker.InInteractZone && distArr < 250f;   // the zone flag IS the signal (87u live miss, 2026-09-29)
                    // In the volume but no prompt yet: keep pushing toward the exit ~1.5 s — most exits
                    // raise their CHECK prompt a step further in ("Exit to Shopping District North" stopped
                    // at 66u saying "Walk forward to leave", 2026-09-29); a walk-into exit changes the area.
                    if (inZone && !FieldTracker.CheckPromptActive) { if (exitZoneMs < 0) exitZoneMs = totalMs; }
                    else exitZoneMs = -1;
                    // LABEL GRACE (2026-09-29): the label draws a moment AFTER the prompt lights — Okina's
                    // unreachable Street exit 3 took Cafe Chagall's prompt in that gap ("Press to go" at the
                    // cafe). An unlabelled prompt counts only after 800 ms with still no label drawn.
                    // A NEIGHBOUR'S PROMPT AT THE EXIT (2026-09-29, [ExitDiag]): South Street exit 1 sits beside
                    // the Bus Stop — arriving facing the pole, the lit prompt drew "Bus Stop" for 4 s and the walk
                    // timed out. Which one lights depends on FACING: push into the exit's own box for 0.6 s.
                    if (FieldTracker.CheckPromptActive && ident < 0 && totalMs - lastExitNudgeMs > 1500)
                    { lastExitNudgeMs = totalMs; exitNudgeUntilMs = totalMs + 600; Log($"[OverworldNav] exit shows '{plabel}' — pushing into {t.Name}"); }
                    ready = ours
                            // bid > 0: standing in OUR box (and no other) — the distance to a box centre far past
                            // the path's end doesn't matter (Samegawa's exits: prompt lit at 393u, timed out)
                            || (ident == 0 && FieldTracker.CheckPromptActive && (distArr < 350f || bid > 0)
                                && NearestPersonDist(px, pz) > 180f
                                && (plabel != null || (promptOnMs >= 0 && totalMs - promptOnMs > 800)))
                            || (inZone && exitZoneMs >= 0 && totalMs - exitZoneMs > 2500);
#if DEBUG
                    if (arriving && totalMs - exitDiagMs >= 1000)
                    {
                        exitDiagMs = totalMs;
                        Log($"[ExitDiag] {t.Name}: prompt={FieldTracker.CheckPromptActive} zone={FieldTracker.InInteractZone} focus={FieldTracker.FocusedInteractable} " +
                            $"plabel='{plabel ?? "-"}' ident={ident} promptAge={(promptOnMs >= 0 ? totalMs - promptOnMs : -1)} person={NearestPersonDist(px, pz):F0} distArr={distArr:F0} ready={ready}");
                    }
#endif
                }
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
                    bool tight50 = IsRoomArea() && (t.Proc == "check_reizou" || t.Proc == "check_sofa_p4p");
                    float win = tight50 ? 50f : 75f;
                    // FACE SWEEP (2026-09-29): standing INSIDE the object's box, the game's zone flag (0x720) on,
                    // yet no prompt — the prompt needs the body FACING the object (the Shrine's Fortune Hanger:
                    // "no-check timeout" at 25u from the box centre). Once per walk, after 1.2 s like that, turn
                    // through 8 short pushes and stop the moment the prompt lights; the label check below then
                    // decides whose prompt it is.
                    if (!faceSweepDone && FieldTracker.InInteractZone && !FieldTracker.CheckPromptActive
                        && (t.ExtX != 0 || t.ExtZ != 0) && InsideBox(in t, px, pz, 40f))
                    {
                        if (objZoneMs < 0) objZoneMs = totalMs;
                        else if (totalMs - objZoneMs > 1200)
                        {
                            faceSweepDone = true;
                            ReleaseKeys(held);
                            for (int k = 0; k < 8 && !FieldTracker.CheckPromptActive && _walking; k++)
                            {
                                double sa = k * Math.PI / 4;
                                ControllerInput.DriveStickXY(0x80 + (int)Math.Round(Math.Sin(sa) * 60), 0x80 - (int)Math.Round(Math.Cos(sa) * 60));
                                Thread.Sleep(180);
                                ControllerInput.ReleaseDriveStick();
                                Thread.Sleep(220);
                            }
                            ControllerInput.ReleaseDriveStick();
                            Log($"[OverworldNav] face sweep at {t.Name}: prompt={FieldTracker.CheckPromptActive}");
                            totalMs += 3200;
                            if (arriveStartMs >= 0) arriveStartMs += 3200;   // the sweep must not eat the arrival budget
                            continue;
                        }
                    }
                    else if (!FieldTracker.InInteractZone) objZoneMs = -1;
                    // ident -1 = the lit prompt draws ANOTHER object's label - never ours, however close.
                    ready = ours || (ident == 0 && FieldTracker.CheckPromptActive && distArr < win);
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
                        // VERIFIED LEARNING (2026-09-29): the prompt drew THIS object's own game label, so
                        // where we stand is a proven check spot — remember it (replacing a mislearned one).
                        if (ours && !npcT && !IsExit(t) && GameText.FieldLabel(t.LabelId) != null
                            && FieldTracker.TryPlayerPose(out float lpx, out float lpz, out float lfx, out float lfz)
                            && (!TryGetCalib(t, out float ocx, out float ocz, out _, out _)
                                || (ocx - lpx) * (ocx - lpx) + (ocz - lpz) * (ocz - lpz) > 70f * 70f))
                        {
                            lock (_calibLock) _calib[CalibKey(t)] = new[] { lpx, lpz, lfx, lfz };
                            SaveCalib();
                            Log($"[OverworldNav] LEARNED (label-verified) check spot for {t.Name} at ({lpx:F0},{lpz:F0})");
                        }
                        ArrivalChime();
                        string doneKind;
                        if (IsExit(t) && !FieldTracker.CheckPromptActive)
                        {
                            // STICK SWEEP (2026-09-29): in the exit volume but its prompt is dark — the game
                            // raises it only when FACING the street end. Try 8 short pushes (what raised
                            // "Check: Shopping District, North" by hand) and stop the moment it lights or the
                            // area changes (a walk-into exit).
                            // INTO THE BOX FIRST (2026-09-29, the Shrine torii): the walk stops at the zone's edge
                            // (0x720 on, 50u outside the box) — walking on toward the box centre raises the prompt
                            // or leaves the map. Up to 1.5 s at a steady pace, camera-steered like the walk.
                            {
                                long t0 = Environment.TickCount64;
                                while (!float.IsNaN(wAngle) && Environment.TickCount64 - t0 < 1500 && !FieldTracker.CheckPromptActive && _walking
                                       && $"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}" == areaAtStart)
                                {
                                    var (qx, _, qz, qok) = FieldTracker.WorldPlayerPos();
                                    if (!qok) break;
                                    float ang = MathF.Atan2(t.BZ - qz, t.BX - qx) - wAngle;
                                    ControllerInput.DriveStickXY(0x80 + (int)Math.Round(MathF.Sin(ang) * 90), 0x80 - (int)Math.Round(MathF.Cos(ang) * 90));
                                    Thread.Sleep(60);
                                }
                                ControllerInput.ReleaseDriveStick();
                                if ($"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}" != areaAtStart)
                                { Log("[OverworldNav] walk END (walked into the exit)"); return; }
                            }
                            for (int k = 0; k < 8 && !FieldTracker.CheckPromptActive; k++)
                            {
                                double sa = k * Math.PI / 4;
                                ControllerInput.DriveStickXY(0x80 + (int)Math.Round(Math.Sin(sa) * 70), 0x80 - (int)Math.Round(Math.Cos(sa) * 70));
                                Thread.Sleep(260);
                                ControllerInput.ReleaseDriveStick();
                                Thread.Sleep(170);
                                if ($"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}" != areaAtStart) break;
                            }
                            ControllerInput.ReleaseDriveStick();
                            if ($"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}" != areaAtStart)
                            { Log("[OverworldNav] walk END (exit sweep walked through)"); return; }
                        }
                        if (IsExit(t))
                        {
                            // EXIT: a lit prompt = press to go (most town exits are checks); otherwise it is a
                            // walk-into volume → walk forward.
                            // No prompt after the sweep: say WHICH WAY the exit lies (the old "walk forward" gave no
                            // direction — a sighted tester needed 4 tries, 2026-09-29). Compass words match the list.
                            Speech.Say(FieldTracker.CheckPromptActive ? $"{t.Name}. Press to go."
                                : $"At {t.Name}. It is {WorldDirection(t.BX - px, t.BZ - pz)} — push that way, then press.", true);
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
                        Log($"[OverworldNav] walk DONE ({doneKind}) at ({px:F0},{pz:F0}) target {t.Name} label='{CheckLabel.FreshLabel() ?? "-"}' 0x7F4={FieldTracker.CheckPromptActive} distArr={distArr:F0}");
                        if (t.PersonId != 0 && doneKind == "check") FollowUntilTalk(t.PersonId, camLive, wAngle);
                        return;
                    }
                    Thread.Sleep(60); totalMs += 60; continue;
                }
                readyPolls = 0;

                // ARRIVE give-up: we're at the object but the game never offered a check (door not
                // interactable now / story-gated / blocked). Say so HONESTLY — do NOT claim "press" when
                // there's no real check (that was the false positive at Drama/Library).
                bool wideRing = (t.ExtX != 0 || t.ExtZ != 0) && t.PersonId == 0 && !school;
                if (arriving && (wideRing ? (ringDone || totalMs - arriveStartMs > WideRingAfterMs + 30000)
                                          : totalMs - arriveStartMs > 5000))
                {
                    ReleaseKeys(held); ControllerInput.ReleaseDriveStick();
                    // A LEARNED target's check IS here (the user did it before) — trust that over a flaky
                    // 0x7F4, but only if we're actually NEAR the learned spot (else it'd announce 80u short).
                    if (FieldTracker.CheckPromptActive && distArr < 55f)
                    { ArrivalChime(); Speech.Say($"{t.Name}. Check available.", true); }
                    else if (distArr > 160f)
                    {
                        // NOT at it — the old line said "You're at" from 319u (school desks, 2026-09-29).
                        int shortSteps = Math.Max(1, (int)MathF.Round(distArr / 250f));
                        Speech.Say($"Stopped short of {t.Name}, {shortSteps} step{(shortSteps == 1 ? "" : "s")} away.", true);
                    }
                    else
                        // Reached the place but couldn't fire the REAL check (facing/geometry) → honest +
                        // actionable, NEVER a false "check available" (require 0x7F4, not just in-zone/learned).
                        Speech.Say(t.PersonId != 0 ? $"You're next to {t.Name}. Turn to face them to talk."
                                                   : $"You're at {t.Name}. Turn to face it to check.", true);
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
                        { ArrivalChime(); Speech.Say($"{t.Name}. Check available.", true); }
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
                if (cam3D || camLive)
                {
                    var (cfx, cfz) = FieldTracker.CameraForward3D();
                    if (MathF.Sqrt(cfx * cfx + cfz * cfz) > 0.1f)
                        wAngle = MathF.Atan2(cfz, cfx) + (cam3D ? camOffset : 0f);
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
                bool ringTravel = false;   // moving between ring stand points (normal speed, not the arrival creep)
                float adx = dx, adz = dz;
                if (path != null && path.Count > 0)
                {
                    while (carrot < path.Count - 1)
                    {
                        float wcx = path[carrot].x - px, wcz = path[carrot].z - pz;
                        // Town consumes waypoints from FARTHER out (the 2.5D walk covers ~60u
                        // per tick at full speed — the school's 54u radius gets overshot and
                        // the waypoint never consumes → part of the 07-06 orbit).
                        // A waypoint beside a wall (a gate gap, a doorway) must be reached TIGHTLY — consuming it
                        // 96u early cut the corner into the Shrine gate pillar (2026-09-29).
                        float consume = MeshGrid.Cell * (cam3D ? 0.9f : 1.6f);
                        if (!cam3D && collGrid != null && collGrid.NearWall(path[carrot].x, path[carrot].z)) consume = MeshGrid.Cell * 0.8f;
                        if (MathF.Sqrt(wcx * wcx + wcz * wcz) < consume) carrot++; else break;
                    }
                    float cwx = path[carrot].x - px, cwz = path[carrot].z - pz;
                    // WAYPOINT ORBIT WATCHDOG (2026-07-06): a waypoint the body can't
                    // actually touch (grid pocket / corner) trapped the walk CIRCLING it
                    // at full speed until cancel. No progress toward the CURRENT waypoint
                    // for 1.2s → SKIP it; if it was the last one, drop the path — the walk
                    // degrades gracefully toward the plain straight-line walker.
                    {
                        float cd = MathF.Sqrt(cwx * cwx + cwz * cwz);
                        if (carrot != wdCarrot) { wdCarrot = carrot; wdBest = cd; wdMs = totalMs; }
                        else if (cd < wdBest - 20f) { wdBest = cd; wdMs = totalMs; }
                        else if (totalMs - wdMs > 1200 && !arriving)
                        {
                            if (carrot < path.Count - 1)
                            {
                                carrot++;
                                Log($"[OverworldNav] waypoint {carrot - 1} unreachable ({totalMs - wdMs}ms no progress) — skipping");
                            }
                            else
                            {
                                Log($"[OverworldNav] final waypoint unreachable — dropping path, straight-line");
                                path = null; partial = false;
                                // (2026-09-29) No AREA blacklist any more: ONE unreachable target (the Shrine
                                // main hall through its torii) dropped its path twice and made EVERY other
                                // Shrine walk go straight-line for 15 min. The grids are right now; the drop
                                // stays for this walk only.
                                ++pathDrops;
                            }
                            wdCarrot = -1;
                            if (path != null) { cwx = path[carrot].x - px; cwz = path[carrot].z - pz; }
                        }
                    }
                    if (path != null)
                    {
                        // Consumed a PARTIAL route (its end is a frontier next to us, not the real target).
                        // Steer at the actual target for the rest, but GENTLY (probe, see pulse below): if
                        // it's a passable door we ease through; if it's a real wall we barely move and the
                        // stuck-watchdog replans/stops — instead of ramming it like a bullet.
                        atFrontier = partial && carrot >= path.Count - 1 && MathF.Sqrt(cwx * cwx + cwz * cwz) < MeshGrid.Cell;
                        if (!atFrontier) { adx = cwx; adz = cwz; }
                    }
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
                        if (IsExit(t) && exitZoneMs >= 0 && !school)
                        {
                            // EXIT PUSH (2026-09-29): in the exit's volume without its prompt → walk INTO the
                            // trigger box (its centre). The small step search here faced away; one step toward
                            // the box raised "Check: Shopping District, North" live.
                            adx = t.BX - px; adz = t.BZ - pz;
                        }
                        else if (wholePosDrive)
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
                            // ARRIVAL-ZONE WEDGE (2026-07-06, "Examine bike"): the walk can jam
                            // dead on a blocker 60-115u short — inside the arriving zone where
                            // the stall escalation is deliberately off, but OUTSIDE the search's
                            // 60u trigger — and grind for the whole timeout. When arrival has
                            // physically stalled (~0.6s no motion), engage the SAME tiny-step
                            // search from farther out with a wider ring so the genuine-motion
                            // sweep slides around the blocker's corner.
                            // 200u (2026-09-29): the stairs to your room wedged at 119u on the stair's side
                            // corner and ground straight into it for 4 s — just outside the old 115u.
                            bool arrStuck = stuckMs > 600 && distArr < 200f;
                            // WIDE RING (2026-09-29): the small sweep circles the SNAPPED walkable aim point,
                            // but some prompts fire only from one side of the trigger BOX (the Samegawa stairs
                            // light on the steps below the aim, the Shrine plaques/fortune from the front).
                            // After ~3.6 s of small sweeps with no prompt, circle the BOX centre at a radius
                            // sized to the box, stepping in from each of 8 sides.
                            bool hasBox = t.ExtX != 0 || t.ExtZ != 0;
                            if (hasBox && t.PersonId == 0 && arriveStartMs >= 0 && totalMs - arriveStartMs > WideRingAfterMs)
                            {
                                // STAND POINTS FROM THE COLLISION (2026-09-29): along each of 8 directions from the
                                // box centre, the first walkable cell (with a cell of clearance) = where a body can
                                // stand facing the object. Blind ring points sat behind walls (Fishing Equipment:
                                // half the ring outside the room) and the stick ground into them.
                                if (ringPts == null) { ringPts = RingStandPoints(in t, collGrid, px, pz); ringPhaseMs = totalMs; }
                                if (ringIdx >= ringPts.Count) { ringDone = true; adx = t.BX - px; adz = t.BZ - pz; }
                                else
                                {
                                    // Per stand point: WALK there (≤3 s), then STEP IN toward the box (0.6 s) —
                                    // the time follows the distance instead of a fixed slot that ended before
                                    // a far point was even reached (the North exit, 850u away).
                                    var (sxp, szp) = ringPts[ringIdx];
                                    float rdx = sxp - px, rdz = szp - pz, rd = MathF.Sqrt(rdx * rdx + rdz * rdz);
                                    if (!ringStepIn)
                                    {
                                        if (rd < 60f || totalMs - ringPhaseMs > 3000) { ringStepIn = true; ringPhaseMs = totalMs; }
                                        adx = rdx; adz = rdz; ringTravel = rd > 60f;
                                    }
                                    else
                                    {
                                        adx = t.BX - px; adz = t.BZ - pz;
                                        if (totalMs - ringPhaseMs > 600) { ringStepIn = false; ringIdx++; ringPhaseMs = totalMs; }
                                    }
                                }
                            }
                            else if ((distArr < 60f || arrStuck) && t.PersonId == 0)
                            {
                                float ring = arrStuck && distArr >= 60f ? 55f : 30f;
                                float a = (sweepIdx % 8) * (45f * MathF.PI / 180f);
                                adx = (aimTx + MathF.Cos(a) * ring) - px; adz = (aimTz + MathF.Sin(a) * ring) - pz;
                                if (totalMs - sweepTickMs > 450) { sweepTickMs = totalMs; sweepIdx++; }
                            }
                            else { adx = dx; adz = dz; }
                            // WALL BETWEEN US AND THE SPOT (2026-09-29, the stairs to your room): arriving
                            // switches to "head straight at it", and that straight line crossed the wall
                            // corner at z -257 — pinned 3 s, then the ring. While the grid says the line
                            // is blocked and the route still has waypoints, keep following the route.
                            if (collGrid != null && !collGrid.LineOfSight(px, pz, px + adx, pz + adz))
                            {
                                if (arrivePath == null || totalMs - arrivePathMs > 800)
                                {
                                    arrivePath = collGrid.PlanWorld(px, pz, px + adx, pz + adz);
                                    arrivePathMs = totalMs;
                                }
                                if (arrivePath != null && arrivePath.Count > 1)
                                { adx = arrivePath[1].x - px; adz = arrivePath[1].z - pz; }
                            }
                            else arrivePath = null;
                            // STEP BURST (2026-09-29, the stairs to your room): the gentle arrival WALK cannot
                            // climb a stair's first step — it stops at its foot (z -272 there, live: stick 0.75
                            // stops, 1.0 lights the prompt at -302). Wedged close with no prompt → a 0.55 s
                            // FULL-stick push straight at the spot, at most every 1.5 s.
                            if (arrStuck && t.PersonId == 0 && !FieldTracker.CheckPromptActive && totalMs - lastBurstMs > 1500)
                            { lastBurstMs = totalMs; burstUntilMs = totalMs + 550; Log($"[OverworldNav] step burst toward {t.Name} at ({px:F0},{pz:F0})"); }
                            if (totalMs < burstUntilMs) { adx = aimTx - px; adz = aimTz - pz; }
                            if (totalMs < exitNudgeUntilMs) { adx = t.BX - px; adz = t.BZ - pz; }
                        }
                    }
                    else if (path != null) { adx = snapTx - px; adz = snapTz - pz; }
                    else { adx = dx; adz = dz; }
                }

                // bearing to aim relative to W's world direction (continuous, not snapped to 8)
                float bearing = MathF.Atan2(adz, adx) - wAngle;
                float deg = bearing * 180f / MathF.PI;
                // "Following" = ANY area with a live A* path (2026-07-06, town escalation):
                // the sidestep dodge must not corrupt carrot-following — a +45° dodge off a
                // planned route walks it into the walls the route exists to avoid. (Was
                // cam3D-gated, which left town paths exposed to the dodge.)
                bool following = path != null;
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
                float speed = ringTravel ? 100f
                            : arriving ? (school ? 42f : 55f)
                            : finalApproach ? Math.Clamp(dist * 0.6f, 35f, 110f)     // ease in near the target
                                            : Math.Clamp(dist * 1.0f, 110f, 127f);   // full tilt en route
                // NPC: walk straight up and EASE TO A NEAR-STOP. The old floor-70 chase BLEW PAST them — then
                // the point-blank aim vector (tiny dx/dz) is noisy, so the player spun in place ("went far,
                // moved around a while till it noticed"). dist*0.85 with a low floor brakes us onto the spot so
                // the talk prompt registers and the confirm latches immediately. Live refresh still re-chases a
                // walker if it strolls off (distArr climbs → speed climbs again).
                // …but only CLOSE: far away a person is walked to at full tilt like any object (2026-09-30,
                // Samegawa: a 1400u walk to a townsperson started on the stair slope crawled at stick 110 —
                // a partial stick can't climb stairs — and ground 15 stalls into "Couldn't reach").
                if (t.PersonId != 0) speed = dist > 450f ? Math.Clamp(dist, 110f, 127f) : Math.Clamp(dist * 0.85f, 8f, 110f);
                if (totalMs < burstUntilMs) speed = 127f;   // step burst (see above)
                if (totalMs < exitNudgeUntilMs) speed = 90f; // exit nudge (see the exit confirm)
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
                            // Town recalibration ONLY while straight-lining. Re-deriving wAngle
                            // from our own movement while FOLLOWING A PATH is a feedback loop —
                            // aim rotates, player follows, calibration chases itself → the
                            // full-speed ORBIT that made June's town A* "really bad" (finally
                            // caught on camera 2026-07-06: 45s triangle ping-pong, carrot never
                            // consumed). The town camera is FIXED, so the walk-start calibration
                            // stays valid for the whole walk. (3D: the live camera is authoritative.)
                            if (!cam3D && !camLive && path == null) wAngle = moveAng - comboOff;
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

                    // OSCILLATING WEDGE (2026-07-06, town item B): the classic town failure
                    // slides close-far-close against a slope/corner — that's MOTION, so the
                    // 500ms no-motion stall never fires and the sidestep dodges forever
                    // ("plays a game"). Real progress = getting CLOSER to the target; none
                    // for 2.5s while straight-lining (no path) = a wedge → escalate through
                    // the SAME stall path: learn the wall, back off, A* on the prebuilt grid
                    // (the OVERWORLD_AUTOWALK §8.1 design: A* only when a straight shot
                    // stalls — never for clean walks). Path-following walks legitimately
                    // move AWAY from the target around corners, so this only arms when
                    // path == null.
                    if (dist < bestProgDist - 25f) { bestProgDist = dist; lastProgMs = totalMs; }
                    else if (!arriving && path == null && dist > 200f && totalMs - lastProgMs > 2500)
                    {
                        Log($"[OverworldNav] oscillating wedge (no progress {totalMs - lastProgMs}ms, dist={dist:F0}) — escalate");
                        stuckMs = 500;                    // reuse the confirmed-stall machinery below
                        lastProgMs = totalMs; bestProgDist = dist;
                    }
                    // SAFETY VALVE (2026-07-06): a town DETOUR that makes no progress for 6s is
                    // hurting, not helping — drop the path and resume the proven straight-line
                    // walk (= worst case is exactly the pre-escalation behavior, never worse).
                    else if (!arriving && path != null && !cam3D && totalMs - lastProgMs > 6000)
                    {
                        Log($"[OverworldNav] detour unproductive ({totalMs - lastProgMs}ms) — dropping path, back to straight-line");
                        path = null; partial = false;
                        lastProgMs = totalMs; bestProgDist = dist;
                        ++pathDrops;   // this walk only — no area blacklist (see above, 2026-09-29)
                    }

                    // STALL BURST (2026-09-30): before a stall counts, one 0.55 s FULL-stick push the same way —
                    // steps and stair slopes stop a partial stick but not a full one (the arrival STEP BURST rule).
                    if (stuckMs >= 500 && !arriving && totalMs - lastBurstMs > 2000)
                    {
                        lastBurstMs = totalMs; burstUntilMs = totalMs + 550; stuckMs = 0;
                        Log($"[OverworldNav] stall burst (full stick) at ({nx:F0},{nz:F0}) toward {t.Name}");
                    }
                    if (stuckMs >= 500 && !arriving)   // CONFIRMED stall en route (NOT at the destination — there we just face+check)
                    {
                        // LEARN: stamp the wall just AHEAD of where we wedged so A* routes around it (this
                        // run + the session). Only on a real stall (not a slide, which has motion).
                        float adl = MathF.Sqrt(adx * adx + adz * adz);
                        if (adl > 1f && areaObs.Count < MaxObsPerArea)
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
                            Speech.Say($"Couldn't reach {t.Name}. It's {steps} step{(steps == 1 ? "" : "s")} away.", true);
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
                        // A LEARNED spot we keep stalling on the way to (3 stalls) is suspect (mislearned by the
                        // pre-label learner, or behind a wall) — aim at the trigger box itself for this walk.
                        if (calib && stalls >= 3 && (t.ExtX != 0 || t.ExtZ != 0) && FieldTracker.CurrentMajor != 6)
                        {
                            calib = false; t.X = t.BX; t.Z = t.BZ;
                            Log($"[OverworldNav] learned spot for {t.Name} unreachable ({stalls} stalls) — aiming at its trigger box");
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

    private static void WinBeep(uint freq, uint ms)
        => ToneCue.PlayTones(0.45f * SoundSettings.CursorBeepVol, ((float)freq, (int)ms));

    /// <summary>The rising three-note arrival chime (was three blocking Beeps —
    /// ToneCue sequences the notes itself, non-blocking).</summary>
    private static void ArrivalChime()
        => ToneCue.PlayTones(0.5f * SoundSettings.ChimeVol, (900f, 60), (1200f, 60), (1500f, 90));

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

    private static bool IsReadable(nint addr, int size)
        => Utils.ProbeReadable(addr, size);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable
}
