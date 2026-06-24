namespace p4g64.accessibility.Components.Navigation.AutoWalk;

/// <summary>
/// The single-`\` briefing (auto-walk plan P1, REWORKED after 2026-06-11 user
/// feedback): one short sat-nav instruction, not the whole route. The full
/// turn list on a winding floor was 13 legs of zigzag noise and its total
/// contradicted the browser's straight-line step count.
///
/// Now: "Chest: ahead left, 18 steps." — the direction (8-way, relative to
/// the player's facing) and distance of the NEXT leg only, where a leg is the
/// longest stretch of the routed path that stays within ~25° of one bearing
/// (so an A*-staircase diagonal corridor reads as one diagonal leg). Press `\`
/// again after walking it to get the next instruction. Units are walking
/// steps (250u), same as the browser entries.
/// </summary>
internal static class RouteSpeech
{
    private const float WorldPerStep = 250f;
    private const float LegBendDeg = 25f;     // bearing spread that ends a leg

    /// <param name="label">Spoken noun ("Chest", "Stairs").</param>
    /// <param name="pathPts">Routed path as world points, player's cell
    /// excluded, EXACT target included as the final point.</param>
    internal static string NextLeg(string label, List<(float x, float z)> pathPts,
                                   float px, float pz, float gazeX, float gazeZ)
    {
        if (pathPts.Count == 0) return $"{label}: right here.";

        // Skip path points the player is practically standing on — bearing to
        // a cell center half a step away is noise ("behind, 4 steps" pointing
        // at a cell center you already passed; live bug 2026-06-11).
        float pitch = GridRouter.CellPitch();
        if (pitch <= 0) pitch = 1250f;
        float skip2 = pitch * 0.45f * pitch * 0.45f;
        int start = 0;
        while (start < pathPts.Count - 1)
        {
            float sx = pathPts[start].x - px, sz = pathPts[start].z - pz;
            if (sx * sx + sz * sz > skip2) break;
            start++;
        }

        // Extend the leg while the bearing to each successive point stays
        // within LegBendDeg of the bearing to the leg's first point.
        float b0x = pathPts[start].x - px, b0z = pathPts[start].z - pz;
        int end = start;
        for (int j = start + 1; j < pathPts.Count; j++)
        {
            float bx = pathPts[j].x - px, bz = pathPts[j].z - pz;
            if (MathF.Abs(SignedAngleDeg(b0x, b0z, bx, bz)) > LegBendDeg) break;
            end = j;
        }

        float lx = pathPts[end].x - px, lz = pathPts[end].z - pz;
        int steps = Math.Max(1, StepsFromUnits(MathF.Sqrt(lx * lx + lz * lz)));
        if (end == pathPts.Count - 1 && steps <= 1) return $"{label}: right here.";

        string dir = (gazeX == 0 && gazeZ == 0) ? "" : RelativeDir8(gazeX, gazeZ, lx, lz) + ", ";
        return $"{label}: {dir}{steps} step{(steps == 1 ? "" : "s")}.";
    }

    internal static int StepsFromUnits(float units)
        => (int)MathF.Round(MathF.Abs(units) / WorldPerStep);

    /// <summary>
    /// 8-way direction word relative to a heading, in sat-nav language:
    /// ahead / slight left / left / sharp left / behind. Positive angle =
    /// right (cross convention verified live; SteerProbe round 2 re-confirmed).
    /// </summary>
    internal static string RelativeDir8(float hx, float hz, float dx, float dz)
    {
        float ang = SignedAngleDeg(hx, hz, dx, dz);
        float a = MathF.Abs(ang);
        string side = ang >= 0 ? "right" : "left";
        if (a < 22.5f) return "ahead";
        if (a < 67.5f) return $"slight {side}";
        if (a < 112.5f) return side;
        if (a < 157.5f) return $"sharp {side}";
        return "behind";
    }

    internal static float SignedAngleDeg(float ax, float az, float bx, float bz)
    {
        float dot = ax * bx + az * bz;
        float cross = ax * bz - az * bx;
        return MathF.Atan2(cross, dot) * (180f / MathF.PI);
    }
}
