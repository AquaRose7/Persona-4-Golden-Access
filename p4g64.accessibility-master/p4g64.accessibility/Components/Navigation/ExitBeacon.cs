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
    private const byte StairSprite = 0x0C;   // next-floor staircase

    protected override int Vk => 0xBF;                 // / (slash) — exit beacon (rebound from P, 2026-06-11)
    protected override string SoundFile => "stares.wav";  // new short stairs cue (replaced 3s exit.wav 2026-06-18)
    protected override string Label => "Exit beacon";
    // The clip had 1.2s of dead silence baked in after a ~0.1s ping, so earlier gap
    // tweaks were inaudible — the cadence was dominated by that. The file is now
    // trimmed to ~0.2s, so the gap sets the REAL ping interval: cycle = clip + gap.
    // 0.7s gap → ~0.9s cycle (a clear speed-up from the old ~1.5s). Tune to taste.
    protected override float LoopGapSeconds => 0.7f;

    protected override bool FindTarget(float px, float pz, out float tx, out float tz)
    {
        tx = 0; tz = 0;
        float best = float.MaxValue; bool found = false;
        for (int r = 0; r < MinimapTracker.ROWS; r++)
            for (int c = 0; c < MinimapTracker.COLS; c++)
            {
                if (!MinimapTracker.ReadCell(r, c, out var cell)) continue;
                if (cell.Flag != 1 || cell.Sprite != StairSprite) continue;
                if (!MinimapTracker.CellToWorld(r, c, out float wx, out float wz)) continue;
                float dx = wx - px, dz = wz - pz;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < best) { best = d; tx = wx; tz = wz; found = true; }
            }
        return found;
    }
}
