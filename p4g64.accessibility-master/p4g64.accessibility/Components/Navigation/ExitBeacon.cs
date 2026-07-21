namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Toggle (<b>/</b>) an audio beacon toward the <b>next-floor stairs</b> (the
/// minimap sprite <c>0x0C</c> staircase — the way forward, not the entrance you
/// came in from). Loops <c>database/sounds/stares.wav</c>, panned toward the
/// stairs and louder as you approach. See <see cref="ProximityBeacon"/>.
///
/// Needs the stairs to be mapped on the minimap first (explore a little). Silent
/// otherwise.
/// </summary>
internal sealed class ExitBeacon : ProximityBeacon
{
    protected override int Vk => 0xBF;                 // / (slash) — exit beacon (rebound from P, 2026-06-11)
    protected override string SoundFile => "stares.wav";  // new short stairs cue (replaced 3s exit.wav 2026-06-18)
    protected override string Label => "Exit beacon";
    protected override float VolumeScale => SoundSettings.StairsVol;   // SettingsMenu knob
    // The clip had 1.2s of dead silence baked in after a ~0.1s ping, so earlier gap
    // tweaks were inaudible — the cadence was dominated by that. The file is now
    // trimmed to ~0.2s, so the gap sets the REAL ping interval: cycle = clip + gap.
    // 0.7s gap → ~0.9s cycle (a clear speed-up from the old ~1.5s). Tune to taste.
    protected override float LoopGapSeconds => 0.7f;

    // Delegate to the auto-walk's stairs finder so the staircase-sprite set lives in ONE place
    // (GridRouter.IsStairSprite — 0x0C Yukiko, 0x0E Steamy Bathhouse, …). Previously this scanned for
    // 0x0C only, so the beacon was silent in the Bathhouse (its stairs are 0x0E). Fixed 2026-06-27.
    protected override bool FindTarget(float px, float pz, out float tx, out float tz)
        => AutoWalk.GridRouter.FindNearestStairs(px, pz, out tx, out tz);
}
