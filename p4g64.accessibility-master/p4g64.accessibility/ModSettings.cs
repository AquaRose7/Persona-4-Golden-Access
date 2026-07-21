using System.Text.Json;

namespace p4g64.accessibility;

/// <summary>
/// Persisted user settings — <c>mod_settings.json</c> flat in the mod folder (next to the DLL,
/// like the bundled data files). Loaded once at startup (Mod.cs, right after Utils.ModDir is
/// set, BEFORE components construct); every Set writes the file immediately (toggles change
/// rarely). Components read their initial value in Mod.cs and write back inside their Toggle,
/// so a toggle survives relaunch. Missing file / missing key = the built-in default.
/// </summary>
internal static class ModSettings
{
    private static readonly object _lock = new();
    private static Dictionary<string, JsonElement> _values = new();

    private static string FilePath => System.IO.Path.Combine(Utils.ModDir, "mod_settings.json");

    internal static void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(Utils.ModDir) || !System.IO.File.Exists(FilePath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(System.IO.File.ReadAllText(FilePath));
            if (d != null) lock (_lock) _values = d;
            Utils.Log($"[Settings] loaded {d?.Count ?? 0} persisted setting(s)");
        }
        catch (Exception e) { Utils.Log($"[Settings] load failed: {e.Message}"); }
    }

    internal static bool GetBool(string key, bool def)
    {
        lock (_lock)
            if (_values.TryGetValue(key, out var v)
                && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
        return def;
    }

    internal static void SetBool(string key, bool value)
    {
        lock (_lock) _values[key] = JsonSerializer.SerializeToElement(value);
        Save();
    }

    internal static int GetInt(string key, int def)
    {
        lock (_lock)
            if (_values.TryGetValue(key, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
                return i;
        return def;
    }

    internal static void SetInt(string key, int value)
    {
        lock (_lock) _values[key] = JsonSerializer.SerializeToElement(value);
        Save();
    }

    private static bool _saveWarned;

    private static void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(Utils.ModDir)) return;
            Dictionary<string, JsonElement> snap;
            lock (_lock) snap = new(_values);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Utils.Log($"[Settings] save failed: {e.Message}");
            if (!_saveWarned)
            {
                _saveWarned = true;
                try { Speech.Say("Settings could not be saved.", false); } catch { }
            }
        }
    }
}
