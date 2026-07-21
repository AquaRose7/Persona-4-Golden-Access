namespace p4g64.accessibility;

/// <summary>
/// The SHIPPED defaults for every user-tunable setting — baked 2026-07-19 from the
/// author's own tuned values ("what I have now is what I want for each new player").
/// Used by SoundSettings.Load, the SettingsMenu rows (and its Restore Defaults), the
/// DungeonCursor start-mode reads, and the Mod.cs reader restores — ONE source.
/// </summary>
internal static class Defaults
{
    public const int WallHumVol = 50;
    public const int DoorVol = 100;
    public const int StairsVol = 100;
    public const int ChestVol = 100;
    public const int NavVol = 100;
    public const int RadarVol = 100;
    public const int ChoiceVol = 100;
    public const int CursorBeepVol = 100;
    public const int ChimeVol = 100;
    public const int BumpVol = 100;
    public const int ShadowFreqYou = 140;
    public const int ShadowFreqAway = 300;
    public const int CursorMode = 1;    // 1 = Look
    public const int CursorFrame = 1;   // 1 = Camera
    public const bool DialogueReader = true;
    public const bool SubtitleReader = false;   // game's own subtitles are off by default too
    public const bool MovieDescriptions = true;
}

/// <summary>
/// Live, user-tunable sound values, written by the in-game SettingsMenu and read by the
/// audio components every tick. Persisted in ModSettings as ints (percent / hertz);
/// mirrored here as ready-to-multiply floats so hot paths don't touch the JSON lock.
/// 1.0f = the shipped default loudness for every volume.
/// </summary>
internal static class SoundSettings
{
    public static float WallHumVol = 1f;
    public static float DoorVol = 1f;
    public static float StairsVol = 1f;
    public static float ChestVol = 1f;
    public static float NavVol = 1f;
    public static float RadarVol = 1f;
    public static float ChoiceVol = 1f;
    public static float CursorBeepVol = 1f;
    public static float ChimeVol = 1f;
    public static float BumpVol = 1f;
    // ⚠ 140/300 defaults are EAR-PROVEN (see EnemyRadar's warning comment) — these are
    // the user-adjustable overrides; the menu row descriptions carry the caveat.
    public static float ShadowFreqYou = 140f;
    public static float ShadowFreqAway = 300f;

    internal static void Load()
    {
        WallHumVol    = ModSettings.GetInt("vol_wall_hum", Defaults.WallHumVol) / 100f;
        DoorVol       = ModSettings.GetInt("vol_door", Defaults.DoorVol) / 100f;
        StairsVol     = ModSettings.GetInt("vol_stairs_beacon", Defaults.StairsVol) / 100f;
        ChestVol      = ModSettings.GetInt("vol_chest_beacon", Defaults.ChestVol) / 100f;
        NavVol        = ModSettings.GetInt("vol_nav_beacon", Defaults.NavVol) / 100f;
        RadarVol      = ModSettings.GetInt("vol_shadow_radar", Defaults.RadarVol) / 100f;
        ChoiceVol     = ModSettings.GetInt("vol_choice", Defaults.ChoiceVol) / 100f;
        CursorBeepVol = ModSettings.GetInt("vol_cursor_beeps", Defaults.CursorBeepVol) / 100f;
        ChimeVol      = ModSettings.GetInt("vol_arrival_chime", Defaults.ChimeVol) / 100f;
        BumpVol       = ModSettings.GetInt("vol_wall_bump", Defaults.BumpVol) / 100f;
        ShadowFreqYou = ModSettings.GetInt("shadow_freq_facing_you", Defaults.ShadowFreqYou);
        ShadowFreqAway = ModSettings.GetInt("shadow_freq_facing_away", Defaults.ShadowFreqAway);
        Utils.Log("[SoundSettings] loaded");
    }
}
