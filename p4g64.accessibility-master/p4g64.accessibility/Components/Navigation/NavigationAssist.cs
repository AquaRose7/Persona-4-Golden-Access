using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;
using p4g64.accessibility.Components.Navigation;

namespace p4g64.accessibility.Components;

/// <summary>
/// Navigation Assist — browse the current area by category and teleport to the
/// announced entry via FlowScript.
///
/// HOTKEYS:
///   -  /  =   — cycle categories backward / forward (NPC → Exit → Place → Item)
///   [  /  ]   — cycle entries backward / forward within the current category
///   \         — teleport to the last-announced entry
///
///   Numpad 6  — add a CUSTOM entry at your current position (auto-named Custom N)
///   Numpad 7  — record the last-announced entry's world position (saved to
///               database/navigation_positions.json; overlays NavigationData)
///   Numpad 8  — calibration: cycle through field entries 0..15 of the current
///               area, announcing spawn coord for each. Used to discover new
///               entry points when adding destinations to TeleportTargets.
///   Numpad 9  — record the last-announced entry's approach point (legacy auto-walk)
///
/// TELEPORT (backslash):
///   Looks up the last-announced entry in TeleportTargets. If found:
///   1. C# sets the target's FlagId bit in the FlowScript bitmap at
///      *(byte**)0x1451FF7A0.
///   2. C# synthesises an F keydown+keyup (scan code 0x21).
///   3. Vanilla's `order_party` runs; our softhook (FEmulator/BF/field.flow)
///      matches the flag, clears it, and calls CALL_FIELD_SAFE to reload the
///      right (major, minor) at the right entry.
///   4. C# waits 700ms for the reload, then holds the target's nudge key for
///      the target's nudge duration to walk into the Check zone.
///
///   Physical F press: no flag armed → softhook no-ops → vanilla sub-menu
///   opens normally. To add a destination, see TeleportTargets.cs.
///
/// REMOVED FEATURES (snapshot in legacy/NavigationAssist_Beacon.cs):
///   - `p` audio beacon — distance-scaled pulse to home in on a target.
///   - Quiet 900Hz exploration pulse on CHECK!!.
///   - Auto-walk — earlier auto-pathing, replaced by FlowScript teleport.
///   - "Reached X" arrival announce — only meaningful with the beacon target.
/// </summary>
internal unsafe class NavigationAssist
{
    // Confirmed static global: 0 = nothing nearby, non-zero = CHECK!! visible.
    // Used by CheckWallBump to suppress wall-bump beeps when standing on an
    // interactable (so a chest doesn't sound like a wall).
    private static readonly int* _interactFlag = (int*)0x1411BC7F4L;

    // FlowScript bit-flag bitmap — pointer-to-buffer at this BSS address.
    // BIT_CHK(id) reads bit `id & 31` of the dword at `bitmap + (id >> 5) * 4`.
    // Dereference once, then index. See database/ghidra/ for the RE evidence.
    // (Kept for ABI parity — actual bitmap dispatch now uses _flagBitmapPtrA/B
    // declared near SetFlagBit, since high-numbered bits live in bitmap B.)
    private static readonly byte** _flagBitmapPtr = (byte**)0x1451FF7A0L;

    // Calibration flags — see FEmulator/BF/field.flow for the matching gate.
    private const int CALIBRATION_FLAG_BIT  = 6710;
    private const int CALIBRATION_ENTRY_BIT0 = 6720; // 4 bits of entry index (0..15)

    // Per-category cursor state
    private int _npcIndex   = -1;
    private int _exitIndex  = -1;
    private int _placeIndex = -1;
    private int _itemIndex  = -1;

    // Which category the user is currently browsing. `-` / `=` moves through this.
    private NavCategory _currentCategory = NavCategory.NPC;
    private static readonly NavCategory[] _categoryOrder = new[]
    {
        NavCategory.NPC, NavCategory.Exit, NavCategory.Place, NavCategory.Item
    };

    private NavEntry? _lastAnnounced;

    // Area tracking
    private int _trackedMajor = -1;
    private int _trackedMinor = -1;

    // Key edge-detection
    private bool _minusWas, _plusWas;
    private bool _lbrackWas, _rbrackWas;
    private bool _backslashWas;
    private bool _num6Was, _num7Was, _num8Was, _num9Was;

    // Calibration: which entry we'll teleport to on the next Numpad 8 press.
    private int _calibrationEntry = 0;

    // Main-keyboard punctuation VK codes
    private const int VK_OEM_MINUS = 0xBD; // -
    private const int VK_OEM_PLUS  = 0xBB; // =
    private const int VK_OEM_4     = 0xDB; // [
    private const int VK_OEM_6     = 0xDD; // ]
    private const int VK_OEM_5     = 0xDC; // \

    private const int VK_NUMPAD6 = 0x66;
    private const int VK_NUMPAD7 = 0x67;
    private const int VK_NUMPAD8 = 0x68;
    private const int VK_NUMPAD9 = 0x69;

    // Movement keys (scan codes — P4G reads raw input, not VK codes).
    // KEYEVENTF_SCANCODE + standard QWERTY positions independent of layout.
    private const ushort SC_W = 0x11;
    private const ushort SC_A = 0x1E;
    private const ushort SC_S = 0x1F;
    private const ushort SC_D = 0x20;
    private const ushort SC_F = 0x21; // scan code for F — simulated on backslash to trigger order_party

    // Calibrated from F9 logs: ~30 internal units per walking step.
    private const float UnitsPerStep = 30f;

    internal NavigationAssist()
    {
        new Thread(Poll) { IsBackground = true, Name = "NavAssist" }.Start();
        Log($"[NavAssist] Loaded. -/= categories, [/] entries, \\ teleport. Numpad 6=add-custom 7=record-pos 8=calibrate-entry 9=record-approach. {TeleportTargets.Count} teleport targets, {NavigationPositionStore.Count} positions preloaded.");
    }

    // ── Main poll thread ──────────────────────────────────────────────────

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(50);
            try
            {
                TrackAreaChange();
                CheckKeys();
            }
            catch (Exception ex) { Log($"[NavAssist] Poll error: {ex.Message}"); }
        }
    }

    // Dungeon wall-bump feedback RETIRED 2026-06-03: it guessed "stuck at a
    // wall" from velocity (movement key held but world position not changing),
    // which misfired constantly in normal corridors because P4G movement is
    // camera-relative (you move diagonally in world axes while it thinks you're
    // stuck). The DungeonCursor (H + I/K/J/L) now gives accurate wall info from
    // the minimap grid on demand. No continuous wall cue.

    // ── Area change ───────────────────────────────────────────────────────

    private void TrackAreaChange()
    {
        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major == _trackedMajor && minor == _trackedMinor) return;

        _trackedMajor = major;
        _trackedMinor = minor;

        _npcIndex = _exitIndex = _placeIndex = _itemIndex = -1;
        _lastAnnounced = null;
        ReleaseAllMovementKeys();

        if (major <= 0) return;
        AnnounceSummary(major, minor);
    }

    private static void AnnounceSummary(int major, int minor)
    {
        var all = NavigationData.GetEntries(major, minor);
        if (all.Length == 0)
        {
            string note = major >= 20 ? "Dungeon area, no navigation data." : "Area not in database yet.";
            Log($"[NavAssist] {note} (major={major} minor={minor})");
            Speech.Say(note, false);
            return;
        }

        int npcs   = Array.FindAll(all, e => e.Category == NavCategory.NPC).Length;
        int exits  = Array.FindAll(all, e => e.Category == NavCategory.Exit).Length;
        int places = Array.FindAll(all, e => e.Category == NavCategory.Place).Length;
        int items  = Array.FindAll(all, e => e.Category == NavCategory.Item).Length;

        var parts = new System.Collections.Generic.List<string>();
        if (npcs   > 0) parts.Add(NavigationData.CategorySpokenLabel(NavCategory.NPC,   npcs));
        if (exits  > 0) parts.Add(NavigationData.CategorySpokenLabel(NavCategory.Exit,  exits));
        if (places > 0) parts.Add(NavigationData.CategorySpokenLabel(NavCategory.Place, places));
        if (items  > 0) parts.Add(NavigationData.CategorySpokenLabel(NavCategory.Item,  items));

        string summary = string.Join(", ", parts) + ".";
        Log($"[NavAssist] Summary: {summary}");
        Speech.Say(summary, false);
    }

    // ── Key handling ──────────────────────────────────────────────────────

    private void CheckKeys()
    {
        // In dungeons -=[]\\ are owned by DungeonTravel.cs (floor teleport).
        // Skip everything here so the two components don't both fire on the
        // same press. Battle (240) is also a dungeon major but you can't
        // teleport floors during a battle anyway.
        int curMajor = FieldTracker.CurrentMajor;
        if (curMajor >= 20 && curMajor < 300) return;   // dungeon 20-69 + battle 220-299

        bool minus = IsKeyDown(VK_OEM_MINUS);
        if (minus && !_minusWas) CycleCategory(-1);
        _minusWas = minus;

        bool plus = IsKeyDown(VK_OEM_PLUS);
        if (plus && !_plusWas) CycleCategory(+1);
        _plusWas = plus;

        bool lbr = IsKeyDown(VK_OEM_4);
        if (lbr && !_lbrackWas) CycleEntry(-1);
        _lbrackWas = lbr;

        bool rbr = IsKeyDown(VK_OEM_6);
        if (rbr && !_rbrackWas) CycleEntry(+1);
        _rbrackWas = rbr;

        bool bs = IsKeyDown(VK_OEM_5);
        if (bs && !_backslashWas) TriggerTeleport();
        _backslashWas = bs;

        bool n6 = IsKeyDown(VK_NUMPAD6);
        if (n6 && !_num6Was) AddCustomEntryHere();
        _num6Was = n6;

        bool n7 = IsKeyDown(VK_NUMPAD7);
        if (n7 && !_num7Was) RecordLastAnnouncedPosition();
        _num7Was = n7;

        bool n8 = IsKeyDown(VK_NUMPAD8);
        if (n8 && !_num8Was) TriggerCalibration();
        _num8Was = n8;

        bool n9 = IsKeyDown(VK_NUMPAD9);
        if (n9 && !_num9Was) RecordApproachForLastAnnounced();
        _num9Was = n9;
    }

    // ── Category / entry browsing ────────────────────────────────────────

    /// <summary>
    /// Moves the current category cursor by `dir` (wrap-around). Announces just
    /// the category plural name + count, e.g. "NPCs: 4". User then uses [ / ]
    /// to step through individual entries.
    /// </summary>
    private void CycleCategory(int dir)
    {
        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major <= 0) { Speech.Say("Not in a field area.", true); return; }

        int idx = Array.IndexOf(_categoryOrder, _currentCategory);
        idx = (idx + dir + _categoryOrder.Length) % _categoryOrder.Length;
        _currentCategory = _categoryOrder[idx];

        var all = NavigationData.GetEntries(major, minor);
        var entries = Array.FindAll(all, e => e.Category == _currentCategory);
        string plural = NavigationData.CategoryPluralLabel(_currentCategory);

        string msg = entries.Length == 0
            ? $"No {plural.ToLower()} here."
            : $"{plural}: {entries.Length}";
        Speech.Say(msg, true);
        Log($"[NavAssist] Category → {plural} ({entries.Length}).");
    }

    /// <summary>
    /// Moves the entry cursor within the current category by `dir` (wrap-around).
    /// </summary>
    private void CycleEntry(int dir)
    {
        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major <= 0) { Speech.Say("Not in a field area.", true); return; }

        var all = NavigationData.GetEntries(major, minor);
        var entries = Array.FindAll(all, e => e.Category == _currentCategory);
        string label = NavigationData.CategorySingularLabel(_currentCategory);

        if (entries.Length == 0)
        {
            Speech.Say($"No {label.ToLower()}s here.", true);
            return;
        }

        ref int cursor = ref GetCursor(_currentCategory);
        if (cursor < 0) cursor = dir > 0 ? 0 : entries.Length - 1;
        else cursor = (cursor + dir + entries.Length) % entries.Length;

        AnnounceEntry(entries[cursor], cursor, entries.Length, label);
    }

    private void AnnounceEntry(NavEntry entry, int index, int total, string label)
    {
        string hint = entry.Hint != null ? $". {entry.Hint}" : "";
        string pos  = total > 1 ? $"{label} {index + 1} of {total}" : label;
        string dist = BuildDistanceString(entry);
        string text = dist.Length > 0 ? $"{pos}: {entry.Name}. {dist}{hint}"
                                      : $"{pos}: {entry.Name}{hint}";

        Speech.Say(text, true);
        _lastAnnounced = entry;
        Log($"[NavAssist] {text}");
    }

    private ref int GetCursor(NavCategory cat)
    {
        switch (cat)
        {
            case NavCategory.NPC:   return ref _npcIndex;
            case NavCategory.Exit:  return ref _exitIndex;
            case NavCategory.Place: return ref _placeIndex;
            case NavCategory.Item:  return ref _itemIndex;
            default: throw new ArgumentException($"Unknown category {cat}");
        }
    }

    // ── Teleport trigger (backslash) ─────────────────────────────────────
    //
    //   1. User presses `\`. C# sends a synthesised F keydown+keyup so the
    //      game's input layer sees F pressed — same as a physical press.
    //   2. Vanilla's FlowScript `order_party` procedure fires. Our injected
    //      softhook (FEmulator/BF/field.flow) calls CALL_FIELD_SAFE(8, 2, 2, 0)
    //      which reloads Shopping District South at entry 2 (~628.8, 277.6).
    //   3. 700ms later (reload settled), C# holds W for 350ms. Player walks
    //      ~6u north into Daidara's Check zone at (630.3, 272.5).
    //   4. Check!! fires — movement-through, not position-in, is what fires it.
    //   5. Player presses Enter → vanilla transition into the shop.
    //
    // Requires: customSubMenu installed (provides field.flow baseFlow so our
    // import-side softhook gets matched by AtlusScriptCompiler). See
    // memory/navigation_working.md for the full chain.
    private void TriggerTeleport()
    {
        if (_lastAnnounced == null)
        {
            Speech.Say("Browse an entry first with the minus, equals, or bracket keys, then press backslash to teleport.", true);
            return;
        }

        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        string name = _lastAnnounced.Name;

        // 1. Manual override — hand-tuned entry/nudge for targets that need it.
        if (TeleportTargets.TryGet(major, minor, name, out var manual))
        {
            Log($"[NavAssist] Teleport (manual): {name} via flag {manual.FlagId}.");
            new Thread(() => TeleportSequence(manual, name)) { IsBackground = true, Name = "NavAssistTeleport" }.Start();
            return;
        }

        // 2. Auto-compute: pick closest calibrated entry + walk the gap.
        if (!_lastAnnounced.HasPosition)
        {
            Speech.Say($"No recorded position for {name}. Walk there and press Numpad 7.", true);
            return;
        }
        var entries = AreaEntries.GetEntries(major, minor);
        if (entries.Count == 0)
        {
            Speech.Say($"No entry data for this area. Press Numpad 8 to cycle entries, then try again.", true);
            return;
        }

        float tx = _lastAnnounced.WorldX!.Value;
        float tz = _lastAnnounced.WorldZ!.Value;

        int bestEntry = -1;
        (float X, float Z) bestSpawn = default;
        float bestDist = float.MaxValue;
        foreach (var kv in entries)
        {
            float dx = tx - kv.Value.X;
            float dz = tz - kv.Value.Z;
            float d  = MathF.Sqrt(dx * dx + dz * dz);
            if (d < bestDist) { bestDist = d; bestEntry = kv.Key; bestSpawn = kv.Value; }
        }

        // Compute nudge from best entry spawn toward target. If target is essentially
        // on top of the entry (<2u), use the recorded facing vector instead so we still
        // get movement — Check needs motion, not just presence.
        float ndx = tx - bestSpawn.X;
        float ndz = tz - bestSpawn.Z;
        var keys = new List<ushort>(2);
        if (MathF.Abs(ndx) < 0.5f && MathF.Abs(ndz) < 0.5f && _lastAnnounced.HasFacing)
        {
            ndx = _lastAnnounced.TargetForwardX!.Value * 3f;
            ndz = _lastAnnounced.TargetForwardZ!.Value * 3f;
        }
        if (ndx >  0.5f) keys.Add(SC_D);
        if (ndx < -0.5f) keys.Add(SC_A);
        if (ndz >  0.5f) keys.Add(SC_S);
        if (ndz < -0.5f) keys.Add(SC_W);
        if (keys.Count == 0) keys.Add(SC_D); // last-resort fallback

        // ~60ms per world unit + 80ms buffer, clamped.
        int durationMs = (int)Math.Clamp(bestDist * 60f + 80f, 120f, 1000f);

        Log($"[NavAssist] Teleport (auto): {name} → entry {bestEntry} spawn ({bestSpawn.X:F1}, {bestSpawn.Z:F1}), target ({tx:F1}, {tz:F1}) dist={bestDist:F1}, nudge {DescribeScanCodes(keys.ToArray())} for {durationMs}ms.");
        new Thread(() => TeleportAutoSequence(bestEntry, keys.ToArray(), durationMs, name))
            { IsBackground = true, Name = "NavAssistTeleportAuto" }.Start();
    }

    /// <summary>
    /// Auto-teleport: uses the calibration gate (flag 6710 + entry index in bits 6720-3)
    /// so CALL_FIELD_SAFE runs for the CURRENT area at the chosen entry index. Then
    /// applies the computed nudge.
    /// </summary>
    private void TeleportAutoSequence(int entry, ushort[] nudgeKeys, int nudgeMs, string name)
    {
        try
        {
            for (int bit = 0; bit < 4; bit++)
            {
                bool v = (entry & (1 << bit)) != 0;
                SetFlagBit(CALIBRATION_ENTRY_BIT0 + bit, v);
            }
            if (!SetFlagBit(CALIBRATION_FLAG_BIT, true))
            {
                Speech.Say("Teleport not ready, try again.", true);
                return;
            }

            SendKey(SC_F, up: false);
            Thread.Sleep(40);
            SendKey(SC_F, up: true);

            // Wait for CALL_FIELD_SAFE reload + arrival auto-walk animation.
            Thread.Sleep(700);

            if (nudgeKeys.Length > 0 && nudgeMs > 0)
            {
                SetHeldKeys(nudgeKeys);
                Thread.Sleep(nudgeMs);
                SetHeldKeys();
                Thread.Sleep(100);
            }

            float x = FieldTracker.LivePlayerX;
            float z = FieldTracker.LivePlayerZ;
            Log($"[NavAssist] Teleport to {name} (auto): settled ({x:F1}, {z:F1}).");
        }
        catch (Exception ex) { Log($"[NavAssist] Teleport-auto error: {ex.Message}"); }
    }

    private void TeleportSequence(TeleportTarget target, string name)
    {
        try
        {
            // Pre-clear the order-party softhook diagnostic, then write the
            // destination flag, then read it back. This tells us:
            //   1. Did C# actually write the flag we think we wrote?
            //   2. Did our softhook run at all?  (BIT 6770 set after F)
            //   3. Did our softhook consume the dest flag?  (set then cleared)
            // Used to figure out why dungeon teleport isn't matching overworld.
            SetFlagBit(6770, false);

            if (!SetFlagBit(target.FlagId, true))
            {
                Log("[NavAssist] Teleport aborted: flag bitmap pointer not yet initialised.");
                Speech.Say("Teleport not ready, try again in a moment.", true);
                return;
            }
            bool destAfterWrite = ReadFlagBitForDiag(target.FlagId);
            Log($"[NavAssist] DIAG dest-flag {target.FlagId} after C# write = {destAfterWrite}");

            // Tap F so vanilla's order_party runs, firing our softhook.
            SendKey(SC_F, up: false);
            Thread.Sleep(40);
            SendKey(SC_F, up: true);

            // Read diagnostics 200 ms after F: did our softhook run? Did it
            // consume the dest flag? This runs BEFORE the long sleep so the
            // reads happen before any area transition resets process state.
            Thread.Sleep(200);
            bool hookRan      = ReadFlagBitForDiag(6770);
            bool destStillSet = ReadFlagBitForDiag(target.FlagId);
            Log($"[NavAssist] DIAG order_party_softhook fired = {hookRan}, dest-flag {target.FlagId} after F = {destStillSet}");

            // Wait for CALL_FIELD_SAFE reload AND the spawn-in animation to finish.
            // 700ms covered just the reload; 1500ms also covers the game's arrival
            // auto-walk (which eats nudge keys that don't align with it).
            Thread.Sleep(500);

            if (target.NudgeKeys != null && target.NudgeKeys.Length > 0 && target.NudgeMs > 0)
            {
                float x0 = FieldTracker.LivePlayerX;
                float z0 = FieldTracker.LivePlayerZ;
                Log($"[NavAssist] Teleport nudge: spawn ({x0:F1}, {z0:F1}) — holding {DescribeScanCodes(target.NudgeKeys)} for {target.NudgeMs}ms.");

                SetHeldKeys(target.NudgeKeys);
                Thread.Sleep(target.NudgeMs);
                SetHeldKeys();
                Thread.Sleep(100);
            }

            float x1 = FieldTracker.LivePlayerX;
            float z1 = FieldTracker.LivePlayerZ;
            Log($"[NavAssist] Teleport to {name} settled at ({x1:F1}, {z1:F1}).");
        }
        catch (Exception ex) { Log($"[NavAssist] Teleport sequence error: {ex.Message}"); }
    }

    // ── Calibration (Numpad 8) ───────────────────────────────────────────
    //
    // Cycles through entry indices 0..15 of the current area, one per press.
    // After each teleport, announces the spawn coord via Tolk so the user can
    // note which entry lands closest to each target. Use the results to fill
    // in new rows in TeleportTargets.cs (flag, entry, nudgeKey, nudgeMs).
    //
    // Mechanism: arms calibration flag 6710 + encodes entry index in 4 bits
    // 6720-6723. field.flow's softhook reads them and calls CALL_FIELD_SAFE
    // with the current (major, minor) and the decoded entry.

    private void TriggerCalibration()
    {
        int entry = _calibrationEntry;
        _calibrationEntry = (_calibrationEntry + 1) % 16;

        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major <= 0) { Speech.Say("Not in a field area.", true); return; }

        Speech.Say($"Testing entry {entry}.", true);
        Log($"[NavAssist] Calibration: entry {entry} of ({major},{minor}).");
        new Thread(() => CalibrationSequence(entry)) { IsBackground = true, Name = "NavAssistCalibration" }.Start();
    }

    private void CalibrationSequence(int entry)
    {
        try
        {
            int major = FieldTracker.CurrentMajor;
            int minor = FieldTracker.CurrentMinor;

            for (int bit = 0; bit < 4; bit++)
            {
                bool value = (entry & (1 << bit)) != 0;
                SetFlagBit(CALIBRATION_ENTRY_BIT0 + bit, value);
            }
            if (!SetFlagBit(CALIBRATION_FLAG_BIT, true))
            {
                Speech.Say("Calibration not ready, try again.", true);
                return;
            }

            SendKey(SC_F, up: false);
            Thread.Sleep(40);
            SendKey(SC_F, up: true);

            Thread.Sleep(800);

            float x = FieldTracker.LivePlayerX;
            float z = FieldTracker.LivePlayerZ;
            string msg = float.IsNaN(x)
                ? $"Entry {entry}: no spawn — may not exist in this area."
                : $"Entry {entry}: X {x:F0}, Z {z:F0}.";
            Speech.Say(msg, true);
            Log($"[NavAssist] Calibration entry {entry}: ({x:F1}, {z:F1})");

            // Persist the spawn so future teleports in this area can auto-compute.
            if (!float.IsNaN(x) && major > 0)
                AreaEntries.Record(major, minor, entry, x, z);
        }
        catch (Exception ex) { Log($"[NavAssist] Calibration error: {ex.Message}"); }
    }

    private static string DescribeScanCodes(ushort[] keys)
    {
        if (keys.Length == 0) return "(none)";
        return string.Join("+", Array.ConvertAll(keys, sc => sc switch
        {
            SC_W => "W",
            SC_A => "A",
            SC_S => "S",
            SC_D => "D",
            _    => $"0x{sc:X}",
        }));
    }

    /// <summary>
    /// Sets or clears a FlowScript event-flag bit in the game's bitmap at
    /// `*(byte**)0x1451FF7A0`. Returns false if the bitmap pointer isn't
    /// allocated yet (happens before first field load).
    /// </summary>
    // P4G has TWO parallel FlowScript flag bitmaps, each 832 bytes / 6656 bits:
    //   *(0x1451FF7A0)  — bitmap A: bits 0..6655
    //   *(0x1451FF7B0)  — bitmap B: bits 6656..13311
    // Found via Ghidra (FUN_1403D4300 initialises both as a pair, FUN_14046B170
    // is the runtime setter for A's pointer). The bit-count 0x1a00 = 6656
    // written alongside is the per-bitmap capacity.
    //
    // Why we have to dispatch by bit ID instead of just adding offset to
    // bitmap A's pointer: in overworld the two bitmaps are in contiguous
    // static memory, so writing past bitmap A's end accidentally lands in
    // bitmap B and FlowScript saw it. In dungeon the bitmaps are heap-
    // allocated separately, so that coincidence breaks. The correct logic
    // is "low bits go to A, high bits go to B" — same as FlowScript itself.
    private const int BITMAP_BITS = 6656;   // 0x1a00
    private static readonly unsafe byte** _flagBitmapPtrA = (byte**)0x1451FF7A0L;
    private static readonly unsafe byte** _flagBitmapPtrB = (byte**)0x1451FF7B0L;

    private static unsafe bool SetFlagBit(int bitId, bool value)
    {
        byte** ptrSource;
        int relBit;
        if (bitId < BITMAP_BITS) { ptrSource = _flagBitmapPtrA; relBit = bitId; }
        else                     { ptrSource = _flagBitmapPtrB; relBit = bitId - BITMAP_BITS; }

        if (!IsReadable((nint)ptrSource, 8)) return false;
        byte* bitmap = *ptrSource;
        if (bitmap == null) return false;

        nint dwordAddr = (nint)(bitmap + (relBit >> 5) * 4);
        if (!IsReadable(dwordAddr, 4)) return false;

        uint mask = 1u << (relBit & 31);
        uint* word = (uint*)dwordAddr;
        if (value) *word |= mask;
        else       *word &= ~mask;
        return true;
    }

    /// <summary>Diagnostic-only read of a flag bit; mirrors SetFlagBit.</summary>
    private static unsafe bool ReadFlagBitForDiag(int bitId)
    {
        byte** ptrSource;
        int relBit;
        if (bitId < BITMAP_BITS) { ptrSource = _flagBitmapPtrA; relBit = bitId; }
        else                     { ptrSource = _flagBitmapPtrB; relBit = bitId - BITMAP_BITS; }

        if (!IsReadable((nint)ptrSource, 8)) return false;
        byte* bitmap = *ptrSource;
        if (bitmap == null) return false;
        nint dwordAddr = (nint)(bitmap + (relBit >> 5) * 4);
        if (!IsReadable(dwordAddr, 4)) return false;
        uint mask = 1u << (relBit & 31);
        return (*(uint*)dwordAddr & mask) != 0;
    }

    // ── Position recording (Numpad 7) ─────────────────────────────────────

    /// <summary>
    /// Saves the last-announced entry's name + the player's current X/Z to
    /// database/navigation_positions.json. Cycle to an entry first with the
    /// browse keys, walk to the spot, then press Numpad 7.
    /// </summary>
    private void RecordLastAnnouncedPosition()
    {
        if (_lastAnnounced == null)
        {
            Speech.Say("Cycle to an entry first with minus, equals, or bracket keys, then press Numpad 7.", true);
            return;
        }

        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major <= 0) { Speech.Say("Not in a field area.", true); return; }

        float px = FieldTracker.LivePlayerX;
        float pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px))
        {
            Speech.Say("Player position not available yet. Try again in a moment.", true);
            return;
        }

        float fx = FieldTracker.ForwardX;
        float fz = FieldTracker.ForwardZ;
        bool haveFacing = !float.IsNaN(fx) && !float.IsNaN(fz);

        if (haveFacing)
        {
            NavigationPositionStore.Save(major, minor, _lastAnnounced.Name, px, pz, fx, fz);
            _lastAnnounced = _lastAnnounced with
            {
                WorldX = px, WorldZ = pz,
                TargetForwardX = fx, TargetForwardZ = fz
            };
            Speech.Say($"Recorded {_lastAnnounced.Name} at X {px:F0}, Z {pz:F0}. Facing X {fx:F2}, Z {fz:F2}.", true);
        }
        else
        {
            NavigationPositionStore.Save(major, minor, _lastAnnounced.Name, px, pz);
            _lastAnnounced = _lastAnnounced with { WorldX = px, WorldZ = pz };
            Speech.Say($"Recorded {_lastAnnounced.Name} at X {px:F0}, Z {pz:F0}. Facing unavailable.", true);
        }
    }

    // ── Approach-point recording (Numpad 9) ───────────────────────────────

    /// <summary>
    /// Saves an APPROACH POINT for the last-announced entry — a position from
    /// which the legacy auto-walk would bee-line THROUGH the target to trigger
    /// CHECK!!. Auto-walk is retired, but recording approach points still
    /// updates the JSON for future reference or if we revive the path.
    /// </summary>
    private void RecordApproachForLastAnnounced()
    {
        if (_lastAnnounced == null)
        {
            Speech.Say("Cycle to an entry first, then press Numpad 9 to record your position as the approach point.", true);
            return;
        }
        if (!_lastAnnounced.HasPosition)
        {
            Speech.Say($"{_lastAnnounced.Name} has no target position yet. Record the target first with Numpad 7.", true);
            return;
        }

        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major <= 0) { Speech.Say("Not in a field area.", true); return; }

        float px = FieldTracker.LivePlayerX;
        float pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px))
        {
            Speech.Say("Player position not available yet. Try again in a moment.", true);
            return;
        }

        if (NavigationPositionStore.SaveApproach(major, minor, _lastAnnounced.Name, px, pz))
        {
            _lastAnnounced = _lastAnnounced with { ApproachX = px, ApproachZ = pz };
            float dx = _lastAnnounced.WorldX!.Value - px;
            float dz = _lastAnnounced.WorldZ!.Value - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            Speech.Say($"Approach point for {_lastAnnounced.Name} set at X {px:F0}, Z {pz:F0}. {dist:F1} units from target.", true);
        }
        else
        {
            Speech.Say("Could not save approach — record the target's position first with Numpad 7.", true);
        }
    }

    /// <summary>
    /// Numpad 6: record a brand-new CUSTOM entry at the player's current position.
    /// Auto-names it "Custom N". User can rename in the JSON afterwards.
    /// </summary>
    private void AddCustomEntryHere()
    {
        int major = FieldTracker.CurrentMajor;
        int minor = FieldTracker.CurrentMinor;
        if (major <= 0) { Speech.Say("Not in a field area.", true); return; }

        float px = FieldTracker.LivePlayerX;
        float pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px))
        {
            Speech.Say("Player position not available yet. Try again in a moment.", true);
            return;
        }

        var existing = NavigationData.GetEntries(major, minor);
        int n = 1;
        while (Array.Exists(existing, e => e.Name == $"Custom {n}")) n++;
        string name = $"Custom {n}";

        NavigationPositionStore.SaveRecord(new NavigationPositionStore.PositionRecord(
            major, minor, name, px, pz, category: "Place", hint: "custom marker — rename in JSON"));

        Speech.Say($"Added {name} at X {px:F0}, Z {pz:F0}. Edit navigation positions JSON to rename.", true);
    }

    // ── Distance and direction ────────────────────────────────────────────

    private static string BuildDistanceString(NavEntry entry)
    {
        if (!entry.HasPosition) return "";

        float px = FieldTracker.LivePlayerX;
        float pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px)) return "";

        float dx = entry.WorldX!.Value - px;
        float dz = entry.WorldZ!.Value - pz;
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        string dir = CompassDirection(dx, dz);
        if (dist < 3f)  return "right here";
        if (dist < 10f) return $"very close {dir}";
        if (dist < 30f) return $"nearby {dir}";
        int steps = (int)MathF.Round(dist / UnitsPerStep);
        if (steps < 1) steps = 1;
        return $"{steps} step{(steps == 1 ? "" : "s")} {dir}";
    }

    /// <summary>
    /// Converts a delta vector to an 8-point compass direction.
    /// P4G 2.5D: +X = east, +Z = south.
    /// </summary>
    private static string CompassDirection(float dx, float dz)
    {
        float angle = MathF.Atan2(dz, dx) * 180f / MathF.PI;
        float bearing = (angle + 90f + 360f) % 360f;

        return bearing switch
        {
            < 22.5f  => "north",
            < 67.5f  => "north-east",
            < 112.5f => "east",
            < 157.5f => "south-east",
            < 202.5f => "south",
            < 247.5f => "south-west",
            < 292.5f => "west",
            < 337.5f => "north-west",
            _        => "north",
        };
    }


    // ── Input injection (SendInput with scan codes) ──────────────────────
    // KEYEVENTF_SCANCODE sends raw scan codes past Windows' VK translation,
    // which is what DirectInput / raw-input games (P4G) actually read.
    //
    // _heldScanCodes tracks sticky holds so SetHeldKeys only emits transitions.
    // SendKey is a one-shot call — used for the F tap in TeleportSequence.

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public long padding; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

    private const uint INPUT_KEYBOARD     = 1;
    private const uint KEYEVENTF_KEYUP    = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private readonly HashSet<ushort> _heldScanCodes = new();

    /// <summary>
    /// Transitions the held-key set to exactly `desired`. Presses new keys,
    /// releases keys no longer in the set. Call with no args to release all.
    /// </summary>
    private int SetHeldKeys(params ushort[] desired)
    {
        int sent = 0;
        var snapshot = new ushort[_heldScanCodes.Count];
        _heldScanCodes.CopyTo(snapshot);
        foreach (var sc in snapshot)
        {
            if (Array.IndexOf(desired, sc) < 0)
            {
                sent += (int)SendKey(sc, up: true);
                _heldScanCodes.Remove(sc);
            }
        }
        foreach (var sc in desired)
        {
            if (!_heldScanCodes.Contains(sc))
            {
                sent += (int)SendKey(sc, up: false);
                _heldScanCodes.Add(sc);
            }
        }
        return sent;
    }

    private static uint SendKey(ushort scanCode, bool up)
    {
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki   = new KEYBDINPUT { wVk = 0, wScan = scanCode, dwFlags = flags },
        };
        return SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private void ReleaseAllMovementKeys() => SetHeldKeys();

    // ── Win32 helpers ─────────────────────────────────────────────────────

    [DllImport("kernel32.dll")] private static extern bool Beep(uint freq, uint duration);
    private static void WinBeep(uint freq, uint ms) { try { Beep(freq, ms); } catch { } }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    // Guards unsafe memory reads — AccessViolationException cannot be caught on
    // .NET 9 and terminates the process. Mirrors the copy in FieldTracker/ShopMenu/Item.
    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48;
        const int OFF_STATE = 32;
        const int OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD) != 0) return false;
        return true;
    }
}
