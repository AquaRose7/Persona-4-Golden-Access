using System.IO;
using System.Text.Json;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Persistent store of field entry-point spawn positions, one row per (major, minor, entryIndex).
/// Populated by the Numpad 8 calibration flow; consumed by auto-teleport to pick the closest
/// entry to any recorded target position in the current area.
///
/// File: database/area_entries.json (sibling to navigation_positions.json).
/// Pre-seeded with Shopping District South (8, 2) entries since we already calibrated those.
/// Adding entries for a new area = walk in, press Numpad 8 through entries 0..10ish —
/// each teleport saves its spawn here automatically.
/// </summary>
internal static class AreaEntries
{
    private static readonly string FilePath =
        Path.Combine(Environment.CurrentDirectory, "database", "area_entries.json");

    internal record EntryRecord(int major, int minor, int entry, float x, float z);

    // (major, minor) -> entry index -> (x, z)
    private static readonly Dictionary<(int Major, int Minor), Dictionary<int, (float X, float Z)>> _byArea
        = new();
    private static readonly object _lock = new();
    private static bool _loaded;

    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            SeedDefaults();
            if (!File.Exists(FilePath))
            {
                WriteToDisk();
                return;
            }
            try
            {
                var json = File.ReadAllText(FilePath);
                var rows = JsonSerializer.Deserialize<EntryRecord[]>(json) ?? Array.Empty<EntryRecord>();
                foreach (var r in rows)
                {
                    if (!_byArea.TryGetValue((r.major, r.minor), out var dict))
                    {
                        dict = new Dictionary<int, (float, float)>();
                        _byArea[(r.major, r.minor)] = dict;
                    }
                    dict[r.entry] = (r.x, r.z);
                }
                Log($"[AreaEntries] Loaded {rows.Length} entries from JSON for {_byArea.Count} areas.");
            }
            catch (Exception ex)
            {
                Log($"[AreaEntries] Failed to load {FilePath}: {ex.Message}. Using seed defaults.");
            }
        }
    }

    /// <summary>
    /// Pre-seed the entries we already discovered by calibration, so a fresh install has
    /// Shopping District South teleports working out of the box. Calibration data from
    /// session 2026-04-23.
    /// </summary>
    private static void SeedDefaults()
    {
        _byArea[(8, 2)] = new()
        {
            [0] = (1063.9f, 538.1f), // Junes/Samegawa corner (east side)
            [1] = (633.9f, 198.2f),  // North (Shopping District North exit area)
            [2] = (628.8f, 277.6f),  // Near Daidara
            [3] = (634.4f, 203.7f),  // North (Pharmacy area)
            [4] = (619.8f, 309.5f),  // Mid street
            [5] = (615.0f, 338.8f),  // Save Point
            [6] = (1005.4f, 535.8f), // East (variant of 0)
            [7] = (614.9f, 383.7f),  // South (Bookstore area)
            [8] = (710.1f, 496.7f),  // East-central
            [9] = (634.1f, 199.8f),  // North (variant of 1)
            [10] = (820.6f, 521.5f), // Invalid/default fallback — also entries 11-15
        };
    }

    private static void WriteToDisk()
    {
        var rows = new List<EntryRecord>();
        foreach (var ((major, minor), dict) in _byArea)
        {
            foreach (var (entry, (x, z)) in dict)
                rows.Add(new EntryRecord(major, minor, entry, x, z));
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(rows, _writeOpts));
        }
        catch (Exception ex) { Log($"[AreaEntries] Failed to write {FilePath}: {ex.Message}"); }
    }

    internal static void Record(int major, int minor, int entry, float x, float z)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byArea.TryGetValue((major, minor), out var dict))
            {
                dict = new Dictionary<int, (float, float)>();
                _byArea[(major, minor)] = dict;
            }
            dict[entry] = (x, z);
            WriteToDisk();
        }
    }

    /// <summary>
    /// Returns the known entries for an area, or empty if none calibrated yet.
    /// </summary>
    internal static IReadOnlyDictionary<int, (float X, float Z)> GetEntries(int major, int minor)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byArea.TryGetValue((major, minor), out var dict)
                ? new Dictionary<int, (float, float)>(dict)
                : new Dictionary<int, (float, float)>();
        }
    }
}
