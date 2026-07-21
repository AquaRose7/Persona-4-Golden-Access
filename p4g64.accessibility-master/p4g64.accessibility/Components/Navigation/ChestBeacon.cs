namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Toggle (<b>C</b>) an audio beacon toward the <b>nearest chest</b>. Loops
/// <c>database/sounds/chest.wav</c>, panned toward the chest and louder as you
/// approach. Uses the exact chest positions from the treasure array via
/// <see cref="DungeonNav"/>. See <see cref="ProximityBeacon"/>.
/// </summary>
internal sealed class ChestBeacon : ProximityBeacon
{
    protected override int Vk => 0xBC;                  // , (comma) — chest beacon (rebound from C, 2026-06-11)
    protected override string SoundFile => "chest.wav";
    protected override string Label => "Chest beacon";
    protected override float VolumeScale => SoundSettings.ChestVol;   // SettingsMenu knob

    protected override bool FindTarget(float px, float pz, out float tx, out float tz)
    {
        tx = 0; tz = 0;
        float best = float.MaxValue; bool found = false;
        foreach (var (x, z) in DungeonNav.Chests())
        {
            float dx = x - px, dz = z - pz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < best) { best = d; tx = x; tz = z; found = true; }
        }
        return found;
    }
}
