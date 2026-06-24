namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// One row in the teleport routing table. A target maps to:
///   - FlagId: which BIT in the game's flag bitmap to set so the field.flow
///     softhook routes to the right CALL_FIELD_SAFE. Must match a gate in
///     FEmulator/BF/field.flow.
///   - NudgeKeys + NudgeMs: the post-teleport walk that moves the player from
///     the field entry's spawn into the target's Check trigger zone. An array
///     so diagonals work — [SC_W, SC_A] holds both keys for NW movement.
///     null/empty/0 if no nudge is needed.
/// </summary>
internal record TeleportTarget(
    int FlagId,
    string DisplayName,
    ushort[]? NudgeKeys = null,
    int NudgeMs = 0);

/// <summary>
/// Static routing: (major, minor, NavEntry.Name) → TeleportTarget.
///
/// Flag IDs come from the 6700-6799 free range (see memory/flowscript_bit_flags.md).
/// Every FlagId used here MUST have a matching `if (BIT_CHK(N))` gate in
/// FEmulator/BF/field.flow — otherwise setting the bit does nothing.
///
/// ADDING A NEW DESTINATION:
///   1. Walk to the target in-game and record position with Numpad 7.
///   2. Use Numpad 8 calibration to cycle entries 0-15 of the current area;
///      each press announces the spawn coord for that entry. Pick the entry
///      closest to the target.
///   3. From (entry spawn, target coord) compute the nudge:
///      - Direction: which QWERTY key moves toward the target
///        (W = north = -Z, S = south = +Z, A = west = -X, D = east = +X).
///      - Duration: ~60ms per unit of distance (calibrate with short tests).
///   4. Pick the next free flag ID (look at _table below + field.flow).
///   5. Add a row here, add a matching gate to field.flow, rebuild, restart.
/// </summary>
internal static class TeleportTargets
{
    // Scan codes duplicated from NavigationAssist so the table reads self-contained.
    private const ushort SC_W = 0x11;
    private const ushort SC_A = 0x1E;
    private const ushort SC_S = 0x1F;
    private const ushort SC_D = 0x20;

    // P4G coordinate system: +X = east, -X = west, +Z = south, -Z = north.
    // So SC_W = north (-Z), SC_S = south (+Z), SC_A = west (-X), SC_D = east (+X).
    // Walking speed ≈ 60ms per world unit (calibrated from Daidara: 350ms ≈ 6u).
    private static readonly Dictionary<(int Major, int Minor, string Name), TeleportTarget> _table = new()
    {
        // ── Shopping District South (8, 2) ──
        // Entry 2 (628.8, 277.6) → Daidara zone at (630.3, 272.5): ~6u north. W 350ms.
        [(8, 2, "Daidara Metalworks")] = new TeleportTarget(
            6700, "Daidara", new[] { SC_W }, 350),

        // Entry 5 (615.0, 338.8) → Save Point zone just east. Short D tap.
        [(8, 2, "Save Point")] = new TeleportTarget(
            6701, "Save Point", new[] { SC_D }, 150),

        // Entry 0 (1063.9, 538.1) lands right at Junes Road — Check zone extends
        // east. Longer D hold needed to cross it.
        [(8, 2, "Junes Road")] = new TeleportTarget(
            6702, "Junes Road", new[] { SC_D }, 450),

        // Entry 1 (633.9, 198.2) → exit trigger is NE. Longer W+D hold so Check
        // fires after the spawn animation releases.
        [(8, 2, "Shopping District North")] = new TeleportTarget(
            6703, "Shopping North", new[] { SC_W, SC_D }, 450),

        // Entry 0 (1063.9, 538.1) lands in Samegawa's Check zone but spawn motion
        // alone isn't enough — a tiny A tap provides the trigger-fire movement.
        [(8, 2, "Samegawa Flood Plain Road")] = new TeleportTarget(
            6704, "Samegawa Road", new[] { SC_A }, 100),

        // Entry 3 (634.4, 203.7) → Pharmacy (634.3, 202.3): 1.4u north. W 150ms.
        [(8, 2, "Pharmacy")] = new TeleportTarget(
            6705, "Pharmacy", new[] { SC_W }, 150),

        // Entry 7 (614.9, 383.7) → Bookstore (613.9, 370.3): 13.4u north, 1u west.
        // W 750ms.
        [(8, 2, "Yomenaido Bookstore")] = new TeleportTarget(
            6706, "Bookstore", new[] { SC_W }, 750),
    };

    internal static bool TryGet(int major, int minor, string name, out TeleportTarget target)
        => _table.TryGetValue((major, minor, name), out target!);

    internal static int Count => _table.Count;
}
