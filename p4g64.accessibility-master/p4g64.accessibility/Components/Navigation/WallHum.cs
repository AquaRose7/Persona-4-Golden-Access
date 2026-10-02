using NAudio.Wave;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// ★ WALL HUM v3 (2026-07-16): while walking a dungeon, a soft continuous PAD
/// says where the walls are — four directions, each its own pitch (Left C4 panned
/// left, Right F4 panned right, Ahead C5 high/centred, Behind G3 low/centred), so
/// they stay separable in a chord. Volume falls with distance and goes SILENT
/// toward open ways. The pad ("H_dream", prototyped in database/sound design and
/// chosen by ear) is detuned pure-sine ensembles in a soft reverb — smooth enough
/// to hear for hours, and timbrally UNIQUE vs the Shadow radar's plain sine beeps.
///
/// Sensor: the game's OWN swept-sphere collision, scanned on the GAME THREAD by
/// FieldTracker's move-tick pump (there is no scene lock — a background-thread call
/// races the game and AV-crashes; see memory/collision_query_sweep_re.md). The pump
/// publishes the 4 camera-relative distances; this component only reads them
/// (<see cref="FieldTracker.WallSense"/>) and drives the pad.
///
/// Lessons from the RETIRED v1 wall audio (2026-05-08), all honored here: ONE
/// shared mixer output (<see cref="DungeonAudio"/>), lazy start, inputs added once
/// at startup. Toggle: N (dungeon floors only). Runs during auto-walk too.
/// </summary>
internal sealed class WallHum
{
    private const int PollMs = 100;
    private const float RangeU = 600f;      // walls farther than this are silent (matches the scan range)
    private const float SideMax = 0.55f;    // gain caps for the WAV samples (tune to taste)
    private const float AheadMax = 0.45f;
    private const float BehindMax = 0.50f;
    private const float DoorRange = 1400f;  // a door is sounded within this range (700 → 1400 on player feedback 2026-08-22)
    private const float OpenDoorRate = 1.3f; // a door you have PASSED (= open) plays higher-pitched
    private const float DoorMax = 0.55f;
    private const float SeamFadeSec = 0.25f;  // loop-seam crossfade (2026-09-29 stutter fix)
    // Door/wall hand-off HYSTERESIS (2026-09-29): a door ENTERS a direction at alignment 0.80
    // but only LEAVES below 0.62 (or past DoorRange + 200u) — the old single 0.80 threshold
    // flipped wall↔door every 100 ms tick while the camera swayed around the cone edge.
    private const float DoorEnterDot = 0.80f, DoorKeepDot = 0.62f, DoorKeepExtra = 200f;
    private readonly (float x, float z)?[] _heldDoor = new (float x, float z)?[4];
#if DEBUG
    private readonly int[] _swaps = new int[4];
    private readonly bool[] _wasDoor = new bool[4];
    private readonly int[] _drops = new int[4];
    private readonly float[] _lastWall = { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
    private long _diagNextMs;
#endif
    private const int VK_N = 0x4E;
    private const int VK_SHIFT = 0x10;

    // The player's four wall sounds (database/sound design → mod folder). Mapped
    // CAMERA-relative: North = ahead, South = behind, West = left, East = right.
    private readonly BeaconVoice? _vAhead, _vBehind, _vLeft, _vRight;
    // A door sound per direction (shared wallDoor.wav) — a door in a direction plays
    // this instead of the wall sound there.
    private readonly BeaconVoice? _vDAhead, _vDBehind, _vDLeft, _vDRight;
    private readonly bool _loaded, _doorLoaded;
    private volatile bool _enabled;
    private bool _nWasDown;

    // Controller LT+RT+Y toggles the hum via a DIRECT call (not synth-N): the
    // combo has no other meaning, so unlike the shared keyboard N it works even
    // while the H cursor owns N (user, 2026-07-18).
    private static WallHum? _inst;

    /// <summary>LT+RT+Y: toggle the wall hum regardless of the H cursor.</summary>
    internal static void ToggleFromController()
    {
        var i = _inst;
        if (i == null || !i._loaded) return;
        int major = FieldTracker.CurrentMajor;
        if (major < 20 || major >= 220) return;              // dungeon floors only
        if (CommandMenus.PlayerMenu.IsMenuOpen) return;
        i._enabled = !i._enabled;
        ModSettings.SetBool("wall_hum_on", i._enabled);
        Speech.Say(i._enabled ? "Wall sound on." : "Wall sound off.", true);
    }

    /// <summary>Live on/off for the F1 menu (persists; silent).</summary>
    internal static bool EnabledLive
    {
        get => _inst?._enabled ?? false;
        set { var i = _inst; if (i == null) return; i._enabled = value; ModSettings.SetBool("wall_hum_on", value); }
    }

    public WallHum()
    {
        _inst = this;
        _enabled = ModSettings.GetBool("wall_hum_on", false);   // restore last session's state (2026-08-27)
        var fmt = DungeonAudio.Format;
        if (BeaconVoice.TryLoadMono("wallNorth.wav", out var nMono)
            & BeaconVoice.TryLoadMono("wallSouth.wav", out var sMono)
            & BeaconVoice.TryLoadMono("wallWest.wav", out var wMono)
            & BeaconVoice.TryLoadMono("wallEast.wav", out var eMono)
            && nMono.Length > 0 && sMono.Length > 0 && wMono.Length > 0 && eMono.Length > 0)
        {
            // Seamless loops (2026-09-29): the raw 2.2 s WAVs clicked + dipped at every loop seam.
            int fade = (int)(fmt.SampleRate * SeamFadeSec);
            nMono = BeaconVoice.MakeSeamless(nMono, fade); sMono = BeaconVoice.MakeSeamless(sMono, fade);
            wMono = BeaconVoice.MakeSeamless(wMono, fade); eMono = BeaconVoice.MakeSeamless(eMono, fade);
            _vAhead = new BeaconVoice(fmt, nMono);   // wallNorth
            _vBehind = new BeaconVoice(fmt, sMono);  // wallSouth
            _vLeft = new BeaconVoice(fmt, wMono);    // wallWest
            _vRight = new BeaconVoice(fmt, eMono);   // wallEast
            DungeonAudio.AddInput(_vAhead); DungeonAudio.AddInput(_vBehind);
            DungeonAudio.AddInput(_vLeft); DungeonAudio.AddInput(_vRight);
            _loaded = true;
            Log("[WallHum] loaded 4 wall sounds (N=ahead S=behind W=left E=right)");
        }
        else Log("[WallHum] wall sound WAVs missing — wall hum disabled");

        // Door sound: one shared buffer, a voice per direction so a door plays in its
        // direction (panned like the walls). Optional — off if wallDoor.wav is absent.
        if (_loaded && BeaconVoice.TryLoadMono("wallDoor.wav", out var dMono) && dMono.Length > 0)
        {
            dMono = BeaconVoice.MakeSeamless(dMono, (int)(fmt.SampleRate * SeamFadeSec));
            _vDAhead = new BeaconVoice(fmt, dMono); _vDBehind = new BeaconVoice(fmt, dMono);
            _vDLeft = new BeaconVoice(fmt, dMono); _vDRight = new BeaconVoice(fmt, dMono);
            DungeonAudio.AddInput(_vDAhead); DungeonAudio.AddInput(_vDBehind);
            DungeonAudio.AddInput(_vDLeft); DungeonAudio.AddInput(_vDRight);
            _doorLoaded = true;
            Log("[WallHum] loaded door sound (wallDoor.wav)");
        }
        else Log("[WallHum] wallDoor.wav absent — door sound off");

        new Thread(Poll) { IsBackground = true, Name = "WallHum" }.Start();
        Log("[WallHum] ready (N toggles the wall hum on dungeon floors)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch { StopVoices(); }
        }
    }

    private void StopVoices()
    {
        if (!_loaded) return;
        _vAhead!.Playing = _vBehind!.Playing = _vLeft!.Playing = _vRight!.Playing = false;
        if (_doorLoaded) _vDAhead!.Playing = _vDBehind!.Playing = _vDLeft!.Playing = _vDRight!.Playing = false;
    }

    private void Tick()
    {
        bool inDungeon = FieldTracker.CurrentMajor >= 20 && FieldTracker.CurrentMajor < 220;
        bool focused = GameHasFocus();

        // N = wall hum toggle — but while the H mapping cursor is ACTIVE, N belongs
        // to IT (walk/look mode toggle); the hum yields entirely (user, 2026-07-18:
        // one key must never fire two meanings at once).
        bool nDown = (GetAsyncKeyState(VK_N) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        if (nDown && !_nWasDown && !shift && focused && inDungeon
            && !SettingsMenu.IsOpen
            && !DungeonCursor.IsActive
            && !CommandMenus.PlayerMenu.IsMenuOpen)
        {
            _enabled = !_enabled;
            ModSettings.SetBool("wall_hum_on", _enabled);
            Speech.Say(_enabled ? "Wall sound on." : "Wall sound off.", true);
        }
        _nWasDown = nDown;

        // (The old B spoken wall readout was UNBOUND 2026-07-19 on user request —
        // the hum itself covers it. FieldTracker.RequestCardinalProbe stays for
        // any future on-demand use.)

        bool active = _enabled && _loaded && focused && inDungeon
                      && !FieldTracker.InAreaTransition
                      && !CommandMenus.PlayerMenu.IsMenuOpen;
        // Turn the GAME-THREAD native wall scan on/off with the hum. We can't call
        // the collision from THIS background thread (it races the game and crashes)
        // — the move-tick pump scans and publishes the distances; we just read them.
        FieldTracker.SetWallSense(active);
        DungeonAudio.SetWant(this, active);
        if (!active) { StopVoices(); return; }

        // Each wall sound loops continuously; volume = closeness, pan = side.
        // North/South are centred (the distinct sound says front vs back); West/East
        // pan to the ear. Volume = the LOUDER of the fine 3D-collision wall (tight
        // RangeU, corridors) and the coarse minimap boundary (longer GridRangeU, so
        // Heaven's open edges are heard from farther). Collision dominates when a
        // real wall is close; the grid fills the open-area gap collision can't see.
        var (dF, dR, dL, dB) = FieldTracker.WallSense();
        var (gF, gR, gL, gB) = FieldTracker.WallSenseGrid();

        // Door detection: from the mod's real door positions (not guessed from
        // collision). Nearest door aligned with each camera direction, if any. A
        // door in a direction plays the DOOR sound there instead of the wall sound.
        var (cfx, cfz) = FieldTracker.CameraForward3D();
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        var doors = DungeonNav.DoorSnapshot();
        (float dist, bool open) DoorDir(int slot, float ux, float uz)
        {
            if (!_doorLoaded || doors.Length == 0 || float.IsNaN(px) || float.IsNaN(pz) || (cfx == 0 && cfz == 0))
            { _heldDoor[slot] = null; return (float.PositiveInfinity, false); }
            // Keep the door this direction already plays while it stays roughly this way.
            if (_heldDoor[slot] is (float hx, float hz))
            {
                foreach (var (xx, zz) in doors)
                {
                    if (MathF.Abs(xx - hx) > 1f || MathF.Abs(zz - hz) > 1f) continue;
                    float vx = xx - px, vz = zz - pz;
                    float dist = MathF.Sqrt(vx * vx + vz * vz);
                    if (dist >= 30f && dist <= DoorRange + DoorKeepExtra && (vx * ux + vz * uz) / dist > DoorKeepDot)
                        return (dist, DungeonNav.IsDoorOpenMarked(xx, zz));
                    break;
                }
                _heldDoor[slot] = null;
            }
            float best = float.PositiveInfinity; float bx = 0, bz = 0;
            foreach (var (xx, zz) in doors)
            {
                float vx = xx - px, vz = zz - pz;
                float dist = MathF.Sqrt(vx * vx + vz * vz);
                if (dist < 30f || dist > DoorRange) continue;
                if ((vx * ux + vz * uz) / dist > DoorEnterDot && dist < best) { best = dist; bx = xx; bz = zz; }   // aligned this way
            }
            if (!float.IsFinite(best)) return (best, false);
            _heldDoor[slot] = (bx, bz);
            return (best, DungeonNav.IsDoorOpenMarked(bx, bz));
        }
        var doorA = DoorDir(0, cfx, cfz); var doorB = DoorDir(1, -cfx, -cfz); var doorL = DoorDir(2, cfz, -cfx); var doorR = DoorDir(3, -cfz, cfx);

        _vAhead!.Playing = _vBehind!.Playing = _vLeft!.Playing = _vRight!.Playing = true;
        if (_doorLoaded) _vDAhead!.Playing = _vDBehind!.Playing = _vDLeft!.Playing = _vDRight!.Playing = true;
        SetDir(_vAhead, _vDAhead, MathF.Max(Gain(dF), GridGain(gF)) * AheadMax * SoundSettings.WallHumVol, doorA.dist, doorA.open, 0f);
        SetDir(_vBehind, _vDBehind, MathF.Max(Gain(dB), GridGain(gB)) * BehindMax * SoundSettings.WallHumVol, doorB.dist, doorB.open, 0f);
        SetDir(_vLeft, _vDLeft, MathF.Max(Gain(dL), GridGain(gL)) * SideMax * SoundSettings.WallHumVol, doorL.dist, doorL.open, -0.9f);
        SetDir(_vRight, _vDRight, MathF.Max(Gain(dR), GridGain(gR)) * SideMax * SoundSettings.WallHumVol, doorR.dist, doorR.open, +0.9f);
#if DEBUG
        // [WallHumDiag] (TEMP, 2026-09-29 stutter hunt): per direction, wall↔door swaps and
        // "sensor dropouts" (a close wall reading → no wall → back) per 5 s. Remove after sign-off.
        bool[] isDoor = { float.IsFinite(doorA.dist), float.IsFinite(doorB.dist), float.IsFinite(doorL.dist), float.IsFinite(doorR.dist) };
        float[] wall = { dF, dB, dL, dR };
        for (int i = 0; i < 4; i++)
        {
            if (isDoor[i] != _wasDoor[i]) _swaps[i]++;
            _wasDoor[i] = isDoor[i];
            if (float.IsFinite(_lastWall[i]) && _lastWall[i] < RangeU && !(wall[i] < RangeU)) _drops[i]++;
            _lastWall[i] = wall[i];
        }
        long nowMs = Environment.TickCount64;
        if (nowMs >= _diagNextMs)
        {
            if (_diagNextMs != 0 && (_swaps[0] + _swaps[1] + _swaps[2] + _swaps[3] + _drops[0] + _drops[1] + _drops[2] + _drops[3]) > 0)
                Log($"[WallHumDiag] 5s: door<->wall swaps A{_swaps[0]} B{_swaps[1]} L{_swaps[2]} R{_swaps[3]} | wall dropouts A{_drops[0]} B{_drops[1]} L{_drops[2]} R{_drops[3]}");
            Array.Clear(_swaps); Array.Clear(_drops);
            _diagNextMs = nowMs + 5000;
        }
#endif
    }

    /// <summary>Drive one direction: if a door is there, play the door voice (volume
    /// by door distance) and mute the wall; otherwise play the wall at its gain.</summary>
    private void SetDir(BeaconVoice wall, BeaconVoice? door, float wallGain, float doorDist, bool doorOpen, float pan)
    {
        if (_doorLoaded && door != null && float.IsFinite(doorDist))
        {
            door.Set(DoorGain(doorDist) * DoorMax * SoundSettings.DoorVol, pan, doorOpen ? OpenDoorRate : 1f);
            wall.Set(0f, pan);
        }
        else
        {
            wall.Set(wallGain, pan);
            door?.Set(0f, pan);
        }
    }

    /// <summary>0 beyond range → 1 at contact, squared so the last steps swell.</summary>
    private static float Gain(float d)
    {
        if (!float.IsFinite(d) || d >= RangeU) return 0f;
        float t = 1f - MathF.Max(d, 50f) / RangeU;
        return t * t;
    }

    /// <summary>Gain for the COARSE grid boundary — a longer range (open-area edges
    /// like Heaven's rim should be heard from farther), same soft square falloff.</summary>
    private const float GridRangeU = 2000f;
    private static float GridGain(float d)
    {
        if (!float.IsFinite(d) || d >= GridRangeU) return 0f;
        float t = 1f - MathF.Max(d, 50f) / GridRangeU;
        return t * t;
    }

    /// <summary>Gain for a door by distance (its own range).</summary>
    private static float DoorGain(float d)
    {
        if (!float.IsFinite(d) || d >= DoorRange) return 0f;
        float t = 1f - MathF.Max(d, 50f) / DoorRange;
        return t * t;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

}
