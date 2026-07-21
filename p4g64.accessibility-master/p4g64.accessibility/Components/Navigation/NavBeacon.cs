using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Unified dungeon audio beacon (Feature 3, 2026-07-02). Press <b>P</b> to toggle a 3D positional
/// beacon toward the CURRENTLY SELECTED browser entry (Door / All door / Chest / Shadow / Stairs), or
/// the H-cursor's cell when the cursor is active. It live-follows the selection, so browsing with
/// <c>[</c>/<c>]</c> while it plays "scans" items by ear.
///
/// Direction is <b>CAMERA-relative</b> (user call 2026-07-02: simpler + better than body-facing) — the
/// same forward the auto-walker uses (<see cref="FieldTracker.CameraForward3D"/>, = the way W moves):
///   • pan   = target LEFT/RIGHT of the camera forward (with the BeaconVoice inter-aural delay for 3D),
///   • muffle = target AHEAD (bright) vs BEHIND (low-passed) via <see cref="BeaconVoice.Openness"/>,
///   • volume = distance (steep falloff — quiet far, loud near).
/// Shares the one <see cref="DungeonAudio"/> output with the other beacons. Off by default; dungeon-only.
/// </summary>
internal sealed class NavBeacon
{
    private const int PollMs = 50;
    private const int VK_P = 0x50;
    private const int VK_SHIFT = 0x10;   // Shift+P = speech history — never fire the beacon on it

    // Distance → volume + TICK RATE, matching the overworld P beacon (louder + faster ticks as you
    // approach). prox = 1 near … 0 far over FarDist. Sound is the same 760 Hz MakePing().
    private const int FarDist = 2500;
    private static readonly int NearGap = (int)(DungeonAudio.Format.SampleRate * 0.05f);  // ~0.05 s → rapid ticks close
    private static readonly int FarGap  = (int)(DungeonAudio.Format.SampleRate * 0.75f);  // ~0.75 s → lazy ticks far
    private const float NearGain = 0.85f, FarGain = 0.22f;
    private const float PanSign = -1f;   // camera-right = right ear (flipped after user reported reversed)
    private const float SameTargetUnits = 250f;   // P on ~this same spot = toggle off; elsewhere = retarget

    private readonly BeaconVoice _voice;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private volatile bool _active;
    private bool _keyWas;
    private int _grace;

    // The target is SNAPSHOTTED when P is pressed and held FIXED until P again — so browsing the list
    // with [ ] to check distances doesn't move it. A SHADOW target is live-tracked (shadows move).
    private bool _hasTarget;
    private float _targetX, _targetZ;
    private bool _targetIsShadow;
    private float _trackX, _trackZ;      // shadow-follow running position
    private int _beaconFloor = int.MinValue;

    public NavBeacon()
    {
        // Same synthesized ping as the overworld beacon (user asked to match it).
        _voice = new BeaconVoice(DungeonAudio.Format, MakePing(), gapFrames: FarGap);
        DungeonAudio.AddInput(_voice);

        _thread = new Thread(PollLoop) { IsBackground = true, Name = "NavBeacon" };
        _thread.Start();
        Log("[NavBeacon] ready (P beacons the selected item / H-cursor, camera-relative 3D)");
    }

    public void Stop() { _stopped = true; _active = false; DungeonAudio.SetWant(this, false); }

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[NavBeacon] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!Utils.GameHasFocus()) { _voice.Playing = false; DungeonAudio.SetWant(this, false); return; }
        // Back off during an area transition (reading live camera/position mid-scene-rebuild can crash).
        if (FieldTracker.InAreaTransition) { _voice.Playing = false; DungeonAudio.SetWant(this, false); return; }

        bool k = !SettingsMenu.IsOpen && IsKeyDown(VK_P) && !IsKeyDown(VK_SHIFT) && !CommandMenus.PlayerMenu.IsMenuOpen;
        if (k && !_keyWas) Toggle();
        _keyWas = k;

        if (!_active || !_hasTarget) { _voice.Playing = false; DungeonAudio.SetWant(this, false); return; }

        // The fixed target belongs to ONE floor: stop if we leave the dungeon or change floors.
        if (!InDungeon() || FloorKey() != _beaconFloor)
        {
            _active = false; _hasTarget = false;
            _voice.Playing = false; DungeonAudio.SetWant(this, false);
            return;
        }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz))
        {
            _voice.Playing = false;
            _grace += PollMs; if (_grace > 500) DungeonAudio.SetWant(this, false);
            return;
        }
        _grace = 0;

        // Fixed snapshot target — except a SHADOW, which we follow live (snap to the nearest live
        // shadow to where we last tracked it; per-tick moves are small so it stays the same one).
        float tx = _targetX, tz = _targetZ;
        if (_targetIsShadow)
        {
            float best = 1500f * 1500f; float nx = _trackX, nz = _trackZ; bool found = false;
            foreach (var (sx, sz) in DungeonNav.Shadows())
            {
                float d = (sx - _trackX) * (sx - _trackX) + (sz - _trackZ) * (sz - _trackZ);
                if (d < best) { best = d; nx = sx; nz = sz; found = true; }
            }
            if (found) { _trackX = nx; _trackZ = nz; }
            tx = _trackX; tz = _trackZ;
        }

        float dx = tx - px, dz = tz - pz;
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        // CAMERA-relative: forward = the way the camera faces (= the way W moves).
        var (fx, fz) = FieldTracker.CameraForward3D();
        float pan = 0f, openness = 1f;
        if ((fx != 0f || fz != 0f) && dist > 1f)
        {
            float ux = dx / dist, uz = dz / dist;         // unit vector to target
            float fwd = ux * fx + uz * fz;                // ahead(+1) … behind(-1)
            float rgt = ux * fz + uz * (-fx);             // right(+1) … left(-1)  (right = (fz,-fx))
            pan = Math.Clamp(rgt * PanSign, -1f, 1f);
            openness = Math.Clamp((fwd + 1f) * 0.5f, 0f, 1f);   // ahead → bright, behind → muffled
        }

        // Volume + tick rate from distance (overworld model): louder + faster ticks as you close in.
        float prox = 1f - Math.Clamp(dist, 0f, FarDist) / FarDist;   // 1 near … 0 far
        float gain = (FarGain + (NearGain - FarGain) * prox) * SoundSettings.NavVol;
        int gap = NearGap + (int)((1f - prox) * (FarGap - NearGap));

        DungeonAudio.SetWant(this, true);
        _voice.Openness = openness;
        _voice.GapFrames = gap;
        // BEHIND also lowers the PITCH a little (user 2026-07-06): the muffle
        // alone was ambiguous — pitch 1.0 dead ahead easing to ~0.82 (≈3
        // semitones down) directly behind makes front/back unmistakable.
        float rate = 1f - 0.18f * (1f - openness);
        _voice.Set(gain, pan, rate);
        _voice.Playing = true;
    }

    /// <summary>What P would target RIGHT NOW: the H-cursor cell if the cursor is in Look mode (a
    /// deliberately marked place), else the current browser selection. Doesn't store anything.</summary>
    private static bool TryComputeCandidate(out float x, out float z, out bool isShadow)
    {
        x = 0; z = 0; isShadow = false;
        if (DungeonCursor.IsActive && DungeonCursor.TryGetMarkTarget(out float cx, out float cz, out _))
        { x = cx; z = cz; return true; }
        return DungeonNav.TryGetSelectionTarget(out x, out z, out isShadow);
    }

    private void SetTarget(float x, float z, bool isShadow)
    {
        _targetX = x; _targetZ = z; _targetIsShadow = isShadow; _trackX = x; _trackZ = z;
        _hasTarget = true; _beaconFloor = FloorKey(); _grace = 0;
    }

    private void StopBeacon()
    {
        _active = false; _hasTarget = false;
        _voice.Playing = false; DungeonAudio.SetWant(this, false);
        Speech.Say("Beacon off.", true); Log("[NavBeacon] OFF");
    }

    /// <summary>P behaviour: OFF → lock the candidate + turn on. ON → same spot toggles OFF, a
    /// DIFFERENT spot RETARGETS (no off-then-on). While the H-cursor is closed, the candidate is the
    /// pathfinder selection, so P moves a cursor-mark beacon onto what you're browsing.</summary>
    private void Toggle()
    {
        // Silent when not on a dungeon floor — the overworld has its OWN P beacon (OverworldNav), so
        // announcing an error here would talk over it. Just do nothing.
        if (!InDungeon()) return;

        if (!TryComputeCandidate(out float cx, out float cz, out bool isShadow))
        {
            if (_active) StopBeacon();
            else Speech.Say("Select something to beacon first.", true);
            return;
        }

        if (_active)
        {
            float ddx = cx - _trackX, ddz = cz - _trackZ;
            if (ddx * ddx + ddz * ddz < SameTargetUnits * SameTargetUnits) { StopBeacon(); return; }  // same → off
            SetTarget(cx, cz, isShadow);                                    // different → retarget
            Speech.Say("Beacon moved.", true);
            Log("[NavBeacon] retarget");
            return;
        }

        SetTarget(cx, cz, isShadow);
        _active = true;
        Speech.Say("Beacon on.", true);
        Log("[NavBeacon] ON");
    }

    private static int FloorKey() => FieldTracker.CurrentMajor * 1000 + FieldTracker.CurrentMinor;

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220;   // dungeon floors 20-69; battles (220-299) excluded
    }

    /// <summary>The overworld beacon's 760 Hz ping (0.05 s, fade in/out) — same sound, as requested.</summary>
    private static float[] MakePing()
    {
        int rate = DungeonAudio.Format.SampleRate;
        int n = (int)(rate * 0.05f);
        var m = new float[n];
        int fade = Math.Max(1, n / 6);
        for (int i = 0; i < n; i++)
        {
            float env = i < fade ? i / (float)fade : (i > n - fade ? (n - i) / (float)fade : 1f);
            m[i] = MathF.Sin(2f * MathF.PI * 760f * i / rate) * 0.7f * env;
        }
        return m;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}
