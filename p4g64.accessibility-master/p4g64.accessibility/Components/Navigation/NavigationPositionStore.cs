using System.IO;
using System.Text.Json;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Persistent store of recorded entity world positions.
/// File: {game_dir}/database/navigation_positions.json (same folder as log.txt).
/// Loaded lazily on first access; rewritten on every Save() call.
///
/// The JSON is a flat array so it's easy to edit by hand:
/// [
///   { "major": 8, "minor": 2, "name": "Save Point", "x": 812.4, "z": 451.2 }
/// ]
///
/// Lookup key = (major, minor, name). When NavigationData.GetEntries is called
/// it overlays any recorded position onto the matching NavEntry by name.
/// </summary>
internal static class NavigationPositionStore
{
    private static readonly string FilePath =
        Path.Combine(Environment.CurrentDirectory, "database", "navigation_positions.json");

    // Full records keyed by (major, minor, name). Records with category != null are
    // treated as custom entries (appended to area listings); others are position overlays.
    private static readonly Dictionary<(int Major, int Minor, string Name), PositionRecord> _records
        = new();
    private static readonly object _lock = new();
    private static bool _loaded;

    /// <summary>
    /// One line in navigation_positions.json.
    ///   category: null = overlay only (fills WorldX/Z on an existing NavigationData entry by name).
    ///             "NPC"/"Exit"/"Place"/"Item" = a CUSTOM entry appended to the area's list
    ///             (for things the static database doesn't know about).
    ///   hint: optional spoken description (custom entries only; overlay records ignore it).
    /// </summary>
    internal record PositionRecord(int major, int minor, string name, float x, float z,
                                   string? category = null, string? hint = null,
                                   float? fx = null, float? fz = null,
                                   float? ax = null, float? az = null);

    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    internal static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath))
                {
                    Log($"[NavPositions] No file at {FilePath} — starting empty.");
                    return;
                }
                var text = File.ReadAllText(FilePath);
                var arr = JsonSerializer.Deserialize<PositionRecord[]>(text);
                if (arr == null) return;
                foreach (var r in arr)
                    _records[(r.major, r.minor, r.name)] = r;
                Log($"[NavPositions] Loaded {_records.Count} recorded positions from {FilePath}");
            }
            catch (Exception ex) { Log($"[NavPositions] Load error: {ex.Message}"); }
        }
    }

    internal static bool TryGetPosition(int major, int minor, string name, out float x, out float z)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_records.TryGetValue((major, minor, name), out var r))
            {
                x = r.x; z = r.z; return true;
            }
        }
        x = 0; z = 0; return false;
    }

    /// <summary>Returns recorded facing (fx, fz) for an entry, or (NaN, NaN) if none.</summary>
    internal static bool TryGetFacing(int major, int minor, string name, out float fx, out float fz)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_records.TryGetValue((major, minor, name), out var r) && r.fx.HasValue && r.fz.HasValue)
            {
                fx = r.fx.Value; fz = r.fz.Value; return true;
            }
        }
        fx = float.NaN; fz = float.NaN; return false;
    }

    /// <summary>Returns recorded approach point (ax, az) for an entry, or (NaN, NaN) if none.</summary>
    internal static bool TryGetApproach(int major, int minor, string name, out float ax, out float az)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_records.TryGetValue((major, minor, name), out var r) && r.ax.HasValue && r.az.HasValue)
            {
                ax = r.ax.Value; az = r.az.Value; return true;
            }
        }
        ax = float.NaN; az = float.NaN; return false;
    }

    /// <summary>Merge an approach point onto an existing record (keeps x/z/facing).</summary>
    internal static bool SaveApproach(int major, int minor, string name, float ax, float az)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_records.TryGetValue((major, minor, name), out var existing))
            {
                Log($"[NavPositions] Cannot save approach for {name} ({major},{minor}) — record target position first (Numpad 7).");
                return false;
            }
            var updated = existing with { ax = ax, az = az };
            SaveRecord(updated);
            return true;
        }
    }

    /// <summary>Returns all records (both overlay and custom) for an area.</summary>
    internal static PositionRecord[] GetRecordsFor(int major, int minor)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var list = new List<PositionRecord>();
            foreach (var kv in _records)
                if (kv.Key.Major == major && kv.Key.Minor == minor)
                    list.Add(kv.Value);
            return list.ToArray();
        }
    }

    internal static int Count
    {
        get { EnsureLoaded(); lock (_lock) return _records.Count; }
    }

    /// <summary>Overlay save: just position. Category is null (not a custom entry).</summary>
    internal static void Save(int major, int minor, string name, float x, float z)
        => SaveRecord(new PositionRecord(major, minor, name, x, z));

    /// <summary>Overlay save with facing: position + forward direction vector.</summary>
    internal static void Save(int major, int minor, string name, float x, float z, float fx, float fz)
        => SaveRecord(new PositionRecord(major, minor, name, x, z, fx: fx, fz: fz));

    /// <summary>Full save: write any record (overlay or custom) to the store.</summary>
    internal static void SaveRecord(PositionRecord record)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _records[(record.major, record.minor, record.name)] = record;
            try
            {
                var list = new List<PositionRecord>(_records.Count);
                foreach (var kv in _records) list.Add(kv.Value);

                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, _writeOpts));
                Log($"[NavPositions] Saved {record.name} ({record.major},{record.minor}) X={record.x:F2} Z={record.z:F2} category={record.category ?? "(overlay)"} — total {_records.Count}.");
            }
            catch (Exception ex) { Log($"[NavPositions] Save error: {ex.Message}"); }
        }
    }
}
