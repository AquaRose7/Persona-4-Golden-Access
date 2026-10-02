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
    public const int GoldVol = 100;
    public const int ChoiceVol = 100;
    public const int CheckVol = 100;            // the Check-prompt blip (2026-09-04)
    public const bool CheckSoundOn = true;
    public const int CursorBeepVol = 100;
    public const int ChimeVol = 100;
    public const int BumpVol = 100;
    // 2026-10-02: the minigame + ambush cues get their own volumes (they borrowed the arrival chime's / the
    // P beacon's before). 100 = exactly the loudness they had.
    public const int FishBiteVol = 100;
    public const int FishReelVol = 100;
    public const int BugChimeVol = 100;
    public const int AmbushTickVol = 100;
    public const int AmbushTurnVol = 100;
    public const int AmbushStrikeVol = 100;
    public const int ShadowFreqYou = 140;
    public const int ShadowFreqAway = 300;
    public const int CursorMode = 1;    // 1 = Look
    public const int CursorFrame = 1;   // 1 = Camera
    public const int TextLanguage = 0;  // 0 = Auto (Steam's per-game language)
    public const bool RadarDefaultOn = true;   // Shadow radar starts ON in dungeons (2026-08-22)
    public const int NavSort = 0;       // 0 = by distance, 1 = alphabetical (2026-08-31)
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
    public static float GoldVol = 1f;
    public static float ChoiceVol = 1f;
    public static float CheckVol = 1f;
    public static bool CheckSoundOn = true;
    public static float CursorBeepVol = 1f;
    public static float ChimeVol = 1f;
    public static float BumpVol = 1f;
    public static float FishBiteVol = 1f;
    public static float FishReelVol = 1f;
    public static float BugChimeVol = 1f;
    public static float AmbushTickVol = 1f;
    public static float AmbushTurnVol = 1f;
    public static float AmbushStrikeVol = 1f;
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
        GoldVol       = ModSettings.GetInt("vol_gold_hand", Defaults.GoldVol) / 100f;
        ChoiceVol     = ModSettings.GetInt("vol_choice", Defaults.ChoiceVol) / 100f;
        CheckVol      = ModSettings.GetInt("vol_check", Defaults.CheckVol) / 100f;
        CheckSoundOn  = ModSettings.GetInt("check_sound_on", Defaults.CheckSoundOn ? 1 : 0) == 1;
        CursorBeepVol = ModSettings.GetInt("vol_cursor_beeps", Defaults.CursorBeepVol) / 100f;
        ChimeVol      = ModSettings.GetInt("vol_arrival_chime", Defaults.ChimeVol) / 100f;
        BumpVol       = ModSettings.GetInt("vol_wall_bump", Defaults.BumpVol) / 100f;
        FishBiteVol     = ModSettings.GetInt("vol_fish_bite", Defaults.FishBiteVol) / 100f;
        FishReelVol     = ModSettings.GetInt("vol_fish_reel", Defaults.FishReelVol) / 100f;
        BugChimeVol     = ModSettings.GetInt("vol_bug_chime", Defaults.BugChimeVol) / 100f;
        AmbushTickVol   = ModSettings.GetInt("vol_ambush_tick", Defaults.AmbushTickVol) / 100f;
        AmbushTurnVol   = ModSettings.GetInt("vol_ambush_turn", Defaults.AmbushTurnVol) / 100f;
        AmbushStrikeVol = ModSettings.GetInt("vol_ambush_strike", Defaults.AmbushStrikeVol) / 100f;
        ShadowFreqYou = ModSettings.GetInt("shadow_freq_facing_you", Defaults.ShadowFreqYou);
        ShadowFreqAway = ModSettings.GetInt("shadow_freq_facing_away", Defaults.ShadowFreqAway);
        Utils.Log("[SoundSettings] loaded");
    }
}
