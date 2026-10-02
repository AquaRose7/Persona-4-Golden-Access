using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// THE WORLD HELPER (v2.1.0, 2026-09-02) — hand-authored map descriptions + walk
/// ZONES for the 2.5D overworld/interior maps, so a blind player gets the "one
/// glance at the room" a sighted player gets for free.
///
/// Data = <c>overworld_zones.json</c> (bundled; keyed "major_minor"): per map a
/// prose DESCRIPTION, a DEFAULT zone name, and named world-space rectangles.
/// Walking across a zone boundary speaks the zone's short name ("Sofa." …
/// "Your room."); <b>Shift+/</b> (pad: LT+RT+Back) speaks the current zone's
/// detail line + the whole map description. The file hot-reloads on change
/// (authoring loop: edit json → walk — no rebuild, no restart).
///
/// Zone-capture authoring loop: <c>database/tools/zone_capture.py</c> (position +
/// screenshot in one shot while the user parks at a boundary).
///
/// Gates: overworld majors only (1..19 — dungeons have their own stack), focus,
/// settings menu, camp menu, dialogue (crossings stay silent during a
/// conversation and re-announce after), area transitions. Zone changes debounce
/// over two polls so boundary jitter can't flap.
/// </summary>
internal sealed class OverworldZones
{
    private const int PollMs = 150;
    private const int VK_SHIFT = 0x10, VK_OEM_2 = 0xBF;   // Shift + / = describe

    // FromOrd: a school-year ordinal (April 16 -> 16); -1 = always available. A zone
    // with a later FromOrd than today stays SILENT (the Velvet Room appears 4/16 —
    // announcing it before it exists would mislead + mildly spoil).
    private sealed record Zone(string Name, string Detail, float X0, float X1, float Z0, float Z1, int FromOrd);
    // Extras: description sentences that appear only from a date on (same ordinal
    // rule as a zone's FromOrd) — so "The Velvet Room…" isn't spoken before 4/16.
    private sealed record Area(string Name, string Description, string Default, List<Zone> Zones,
        List<(int FromOrd, string Text)> Extras);

    private readonly object _lock = new();
    private Dictionary<string, Area> _areas = new();
    private DateTime _fileTime;
    private long _nextFileCheck;
    private string _path = "";

    private string _lastAreaKey = "";
    private string? _currentZone;         // announced zone (null = none yet on this map)
    private string? _pendingZone;         // debounce: must be seen 2 consecutive polls
    private bool _firstOnMap;             // first announce after a map change queues (no interrupt)
    private bool _keyWas;

    internal static OverworldZones? Instance;

    /// <summary>True when the zones file has an entry for this map. Dungeon LOBBIES
    /// (majors 20+, 2.5D like town) are authored the same way (user request 2026-09-03);
    /// the file is the gate, so an unauthored 3D floor never matches.</summary>
    internal static bool IsAuthored(int major, int minor)
    {
        var inst = Instance;
        if (inst == null) return false;
        lock (inst._lock) return inst._areas.ContainsKey($"{major}_{minor}");
    }

    /// <summary>Field major where zones may apply: town (1..19) always; anything else
    /// below the battle band only when the file has it. Callers add InFieldLive.</summary>
    internal static bool IsZoneMap(int major, int minor)
        => major > 0 && major < 220 && (major < 20 || IsAuthored(major, minor));

    internal OverworldZones()
    {
        _path = DataPath("overworld_zones.json");
        LoadFile(initial: true);
        Instance = this;
        new Thread(Poll) { IsBackground = true, Name = "OverworldZones" }.Start();
        Log($"[Zones] world helper ready ({_areas.Count} map(s); Shift+/ describes)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(PollMs);
            try { Tick(); } catch { /* poll must never die */ }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus() || SettingsMenu.IsOpen) { _keyWas = IsKeyDown(VK_OEM_2); return; }

        // Hot-reload the data file when it changed (every ~2s) — the authoring loop.
        // ⚠ MUST run BEFORE the authored-map gate: with the data-driven gate (09-03) an
        // unauthored lobby returned early and the file never reloaded while standing in
        // it — the map only "appeared" after visiting an already-loaded one (log-proven).
        if (Environment.TickCount64 >= _nextFileCheck)
        {
            _nextFileCheck = Environment.TickCount64 + 2000;
            try
            {
                var t = System.IO.File.GetLastWriteTimeUtc(_path);
                if (t != _fileTime) { LoadFile(initial: false); _currentZone = _pendingZone = null; }
            }
            catch { }
        }

        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        // InFieldLive: CurrentMajor goes STALE at the title screen — without it the
        // zone poll and Shift+/ kept "living" in the last room (user caught 09-02).
        bool inField = major > 0 && major < 220 && FieldTracker.InFieldLive;
        bool inOverworld = inField && IsZoneMap(major, minor);

        // Key first — on any field map, so an unauthored lobby/floor answers
        // "No description for this place yet." (authoring aid).
        bool k = inField && IsKeyDown(VK_OEM_2) && IsKeyDown(VK_SHIFT);
        if (k && !_keyWas) DescribeCurrent();
        _keyWas = k;

        if (!inOverworld) { _lastAreaKey = ""; _currentZone = _pendingZone = null; return; }
        if (FieldTracker.InAreaTransition) return;
        if (CommandMenus.PlayerMenu.IsMenuOpen) return;
        if (Environment.TickCount64 - Dialogue.LastDialogTick < 300) return;   // stay quiet in conversations

        string key = $"{major}_{minor}";
        Area? area;
        lock (_lock) _areas.TryGetValue(key, out area);
        if (key != _lastAreaKey)
        {
            _lastAreaKey = key;
            _currentZone = _pendingZone = null;
            _firstOnMap = true;
        }
        if (area == null) return;

        var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) return;

        string zone = MatchZone(area, px, pz);

        // Two-poll debounce so a step exactly on a boundary can't flap.
        if (zone != (_pendingZone ?? _currentZone)) { _pendingZone = zone; return; }
        if (_pendingZone == null || zone == _currentZone) return;

        _pendingZone = null;
        _currentZone = zone;
        // First zone after entering the map queues after the game's own area
        // announcement; crossings while walking interrupt (short names, fast feed).
        // QUIET WHILE AUTO-WALKING (2026-09-29 playtest): every zone the walk passes through was
        // spoken ("Shopping district. Save point. Shopping district. Bus stop.") on top of the walk's
        // own arrival line. The zone is still tracked; manual walking speaks as before.
        if (!OverworldNav.IsWalking && !AutoWalk.AutoWalker.IsActive)
            Speech.Say($"{zone}.", interrupt: !_firstOnMap);
        _firstOnMap = false;
    }

    private static string MatchZone(Area a, float x, float z)
    {
        int today = CurrentOrdinal();
        foreach (var zn in a.Zones)
        {
            // Date-gated zone not reached yet (or date unknown) → treat as absent.
            if (zn.FromOrd >= 0 && (today < 0 || today < zn.FromOrd)) continue;
            if (x >= zn.X0 && x <= zn.X1 && z >= zn.Z0 && z <= zn.Z1)
                return zn.Name;
        }
        return a.Default;
    }

    /// <summary>School-year ordinal so date-gates survive the Apr→Mar calendar wrap:
    /// April is 0, ... March is 11, times 31 plus the day. April 16 → 16, January 5 →
    /// 284 — monotonic across the story year, so a "from" gate never re-hides later.</summary>
    private static int Ordinal(int month, int day) => ((month - 4 + 12) % 12) * 31 + day;

    private static int CurrentOrdinal()
    {
        var (m, d) = FieldTracker.GameDate();
        return m == 0 ? -1 : Ordinal(m, d);
    }

    /// <summary>Shift+/ and the pad combo: current zone's detail + the map description.</summary>
    internal void DescribeCurrent()
    {
        int major = FieldTracker.CurrentMajor, minor = FieldTracker.CurrentMinor;
        if (major <= 0 || major >= 220 || !FieldTracker.InFieldLive) return;

        Area? area;
        lock (_lock) _areas.TryGetValue($"{major}_{minor}", out area);
        if (area == null) { Speech.Say("No description for this place yet.", true); return; }

        var (px, _, pz, ok) = FieldTracker.WorldPlayerPos();
        string desc = BuildDescription(area);
        string text;
        if (ok)
        {
            string zoneName = MatchZone(area, px, pz);
            var zn = area.Zones.Find(z => z.Name == zoneName);
            string detail = zn != null ? zn.Detail : "";
            text = $"{zoneName}. {detail} {desc}";
        }
        else
            text = desc;
        Speech.Say(text, true);
    }

    /// <summary>Base description plus any date-gated extra sentence whose date has
    /// arrived (Velvet Room mention only from 4/16). Unknown date → extras omitted.</summary>
    private static string BuildDescription(Area a)
    {
        int today = CurrentOrdinal();
        var sb = new System.Text.StringBuilder(a.Description);
        foreach (var (fromOrd, txt) in a.Extras)
            if (fromOrd < 0 || (today >= 0 && today >= fromOrd))
                sb.Append(' ').Append(txt);
        return sb.ToString();
    }

    internal static void DescribeFromController() => Instance?.DescribeCurrent();

    private void LoadFile(bool initial)
    {
        try
        {
            if (!System.IO.File.Exists(_path))
            {
                if (initial) Log($"[Zones] overworld_zones.json not found at {_path} — world helper idle");
                return;
            }
            _fileTime = System.IO.File.GetLastWriteTimeUtc(_path);
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(_path));
            var areas = new Dictionary<string, Area>();
            foreach (var prop in doc.RootElement.GetProperty("areas").EnumerateObject())
            {
                var el = prop.Value;
                var zones = new List<Zone>();
                if (el.TryGetProperty("zones", out var zs))
                    foreach (var z in zs.EnumerateArray())
                    {
                        var xr = z.GetProperty("x");
                        var zr = z.GetProperty("z");
                        int fromOrd = -1;
                        if (z.TryGetProperty("from", out var fr) && fr.GetString() is string fs)
                        {
                            var parts = fs.Split('-');
                            if (parts.Length == 2 && int.TryParse(parts[0], out int fm) && int.TryParse(parts[1], out int fd))
                                fromOrd = Ordinal(fm, fd);
                        }
                        zones.Add(new Zone(
                            z.GetProperty("name").GetString() ?? "",
                            z.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "",
                            xr[0].GetSingle(), xr[1].GetSingle(),
                            zr[0].GetSingle(), zr[1].GetSingle(),
                            fromOrd));
                    }
                var extras = new List<(int, string)>();
                if (el.TryGetProperty("description_extra", out var exArr))
                    foreach (var ex in exArr.EnumerateArray())
                    {
                        int exOrd = -1;
                        if (ex.TryGetProperty("from", out var exf) && exf.GetString() is string exs)
                        {
                            var p2 = exs.Split('-');
                            if (p2.Length == 2 && int.TryParse(p2[0], out int em) && int.TryParse(p2[1], out int ed))
                                exOrd = Ordinal(em, ed);
                        }
                        extras.Add((exOrd, ex.TryGetProperty("text", out var et) ? et.GetString() ?? "" : ""));
                    }
                areas[prop.Name] = new Area(
                    el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    el.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "",
                    el.TryGetProperty("default", out var df) ? df.GetString() ?? "" : "",
                    zones,
                    extras);
            }
            lock (_lock) _areas = areas;
            if (!initial) Log($"[Zones] overworld_zones.json reloaded ({areas.Count} map(s))");
        }
        catch (Exception ex)
        {
            Log($"[Zones] load failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
