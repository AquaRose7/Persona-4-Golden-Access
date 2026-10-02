using System.Runtime.InteropServices;
using DavyKager;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Toggle an audible Shadow radar with <b>M</b> (off by default). While on:
///
/// - Each Shadow is a continuous, localizable tone: <b>volume = closeness</b>,
///   <b>stereo pan = left/right</b>, <b>low throbbing growl = facing you</b>
///   (danger), <b>soft higher tone = facing away</b> (back-attack chance).
/// - <b>Strike cue</b> when a Shadow is within striking range: a loud rising
///   tone + speech "Strike" if it faces away (swing now for the advantage), or
///   a falling tone + "It sees you" if it faces you (ambush risk).
/// - <b>Chase events</b> (speech): "Shadow chasing you" when one starts coming
///   at you, "Shadow stopped" when a chaser gives up / loses you, "Dodge" on a
///   sudden lunge.
///
/// All audio goes through ONE <see cref="WaveOutEvent"/> + one
/// <see cref="MixingSampleProvider"/> with a FIXED voice set (no inputs added/
/// removed during playback) — the rule learned from the retired wall audio.
/// The loud cue tones play through this same mixer (the old console Beep was too
/// quiet to hear). Only runs in dungeons; auto-stops on exit.
/// </summary>
internal class EnemyRadar
{
    // Spoken Dodge/Strike rate limit (2026-09-29: "Dodge." x7 in a row during an auto-walk).
    private const long CueRepeatMs = 4000;
    private static long _lastDodgeMs;

    private const int PollMs = 40;
    private const int UpdateMs = 80;
    private const int VK_M = 0xBE;   // . (period) — Shadow radar toggle (rebound from M, 2026-06-11)

    private const int Voices = 16;
    private const float MaxRange = 4000f;
    private const float DistanceScale = 700f;
    private const float MasterGain = 0.10f;
    // ⚠ 140/300 is EAR-PROVEN — do not widen. The 2026-07-19 try at 100/450 made
    // shadows INAUDIBLE under the wall hum ("can't hear them at all", reverted
    // same day): 100 Hz sank below the audible mix and 450 blended into it. If
    // shadows-vs-walls needs more separation, duck the HUM while a shadow is in
    // radar range instead of moving these pitches.
    // Since 2026-07-19 the SettingsMenu can override these via SoundSettings
    // (defaults stay 140/300); these constants remain the shipped defaults.
    private const float FreqFacingYou = 140f;   // low growl (sine + tremolo)
    private const float FreqFacingAway = 300f;  // soft higher steady sine
    private const float StrikeRange = 500f;      // back-attack / ambush window
    // ── Sound map v2 (player feedback 2026-08-22): ONE sound for every Shadow (type/facing
    // pitch split parked until the positional cue is proven), FRONT/BACK in the pitch
    // (ahead higher, behind lower — the old pan-only cue made a Shadow straight ahead and
    // one straight behind identical, which is how players walked into them), and a
    // NEAR-FIELD swell + pulse so closing distance is unmistakable.
    private const float BaseFreq = 220f;          // between the ear-proven 140/300 (vs the wall hum)
    private const float AheadRate = 1.15f, BehindRate = 0.80f;   // pitch factor at dead ahead / dead behind
    private const float BehindGain = 0.75f;       // behind is also a little quieter
    private const float NearRange = 320f;         // swell + pulse start inside this
    private const float NearBoost = 1.6f;         // up to ×2.6 volume at contact
    private const float PulseMaxHz = 9f;          // pulse rate at contact (0 = steady beyond NearRange)
    // ── The SHIPPED shadow sound (user sound design, adopted 2026-08-27): shadowsound-001.wav (mod folder /
    // database/sounds in dev), looped per shadow; pitch = front/back factor,
    // RAISED when the Shadow sees you. Delete the file to fall back to the synth.
    private const string ShadowWav = "shadowsound-001.wav";
    private const string GoldWav = "goldhand-001.wav";   // golden hands (spawn type 3) get their own sound (2026-08-27)
    private const float SeesYouRate = 1.35f;      // pitch-up factor while it sees / chases you
    private const float WavBoost = 2.5f;          // wav-mode gain boost (the synth's amplitude IS vol; a wav's content is far below full scale)
    // View-cone detection (replicates FUN_14031DCF0): cos of the angle between a
    // Shadow's forward and the direction to you. >ConeSeeCos ⇒ you're in its
    // ~63° frontal cone (it sees you); <ConeBackCos ⇒ you're clearly behind it.
    private const float ConeSeeCos = 0.45f;
    private const float ConeBackCos = -0.45f;

    // Chase detection (per-update, UpdateMs cadence).
    private const float ChaseMatchRadius = 800f; // match a Shadow to its prior track
    private const float ApproachPerUpdate = 6f;  // dist must shrink by this to count as "approaching"
    private const float RecedePerUpdate = 6f;    // dist must GROW by this to count as receding
    private const float StationaryMove = 8f;     // moved less than this = stood still (gave up)
    private const int   ApproachStreakOn = 3;    // ~240 ms of approach → "chasing"
    private const int   RecedeStreakOff  = 8;    // ~640 ms of recede/stationary → "stopped"
    private const float ChaseAwareRange  = 2800f;// only announce chase within this
    private const float LungePerUpdate   = 110f; // sudden move this big = a lunge
    private const float MaxMatchStep     = 600f; // moved more than this between updates = a track mismatch, ignore

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _mWas;
    private int _pauseGrace;

    private volatile bool _active;
    private readonly ShadowVoice[] _voices = new ShadowVoice[Voices];
    private readonly BeaconVoice[] _wavVoices = new BeaconVoice[Voices];
    private readonly BeaconVoice[] _goldVoices = new BeaconVoice[Voices];
    private readonly bool _wavMode, _goldMode;
    private float[] _goldMono = Array.Empty<float>();

    /// <summary>Normalize a user-supplied buffer: target RMS 0.5 like the synth sine, peaks clamped to full scale.</summary>
    private static void Normalize(float[] mono, string name)
    {
        double sum = 0; float peak = 0;
        foreach (var v in mono) { sum += v * v; if (MathF.Abs(v) > peak) peak = MathF.Abs(v); }
        float rms = (float)Math.Sqrt(sum / Math.Max(1, mono.Length));
        if (rms > 1e-5f && peak > 1e-5f)
        {
            float scale = MathF.Min(0.5f / rms, 1.0f / peak);
            for (int j = 0; j < mono.Length; j++) mono[j] *= scale;
            Log($"[EnemyRadar] {name} normalized: rms {rms:F3} -> {rms * scale:F3}, peak {peak:F3} -> {peak * scale:F3} (x{scale:F2})");
        }
    }
    private readonly CueVoice _cue;
    private int _updateAccum;

    private bool _oppArmed, _dangerArmed;
    private readonly List<Track> _tracks = new();

    public EnemyRadar()
    {
        var fmt = DungeonAudio.Format;
        _wavMode = BeaconVoice.TryLoadMono(ShadowWav, out var shadowMono);
        if (_wavMode) Normalize(shadowMono, ShadowWav);
        _goldMode = _wavMode && BeaconVoice.TryLoadMono(GoldWav, out _goldMono);
        if (_goldMode) Normalize(_goldMono, GoldWav);
        for (int i = 0; i < Voices; i++)
        {
            _voices[i] = new ShadowVoice(fmt);
            DungeonAudio.AddInput(_voices[i]);
            if (_wavMode)
            {
                _wavVoices[i] = new BeaconVoice(fmt, shadowMono);
                DungeonAudio.AddInput(_wavVoices[i]);
                if (_goldMode)
                {
                    _goldVoices[i] = new BeaconVoice(fmt, _goldMono);
                    DungeonAudio.AddInput(_goldVoices[i]);
                }
            }
        }
        if (_wavMode) Log($"[EnemyRadar] WAV mode: {ShadowWav} drives the shadow voices");
        _cue = new CueVoice(fmt);
        DungeonAudio.AddInput(_cue);

        _inst = this;
        // Restore last session's state; the old "shadow_radar_default_on" (08-22) seeds it once.
        _active = ModSettings.GetBool("shadow_radar_on", ModSettings.GetBool("shadow_radar_default_on", Defaults.RadarDefaultOn));
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "EnemyRadar" };
        _thread.Start();
        Log("[EnemyRadar] ready (M to toggle Shadow audio radar)");
    }

    public void Stop() { _stopped = true; _active = false; DungeonAudio.SetWant(this, false); }

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[EnemyRadar] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        // Alt-tabbed away: don't read the toggle key and silence any active radar
        // audio (the intent in _active is preserved, so it resumes on refocus).
        if (!Utils.GameHasFocus())
        {
            foreach (var v in _voices) v.TargetVolume = 0f;
            DungeonAudio.SetWant(this, false);
            return;
        }
        // Shift-up so Shift+. (the radar's period key) is reserved for the
        // movie-description toggle and never fires the radar.
        bool m = !SettingsMenu.IsOpen && IsKeyDown(VK_M) && !IsKeyDown(0x10 /*VK_SHIFT*/);
        if (m && !_mWas) Toggle();
        _mWas = m;

        // M is a persistent intent. Audio plays only while a dungeon floor is
        // loaded (InDungeon excludes battle 240) — so a battle PAUSES the radar
        // and it auto-resumes after, instead of toggling the intent off.
        if (_active && InDungeon() && !FieldTracker.InAreaTransition)   // back off during a transition (crash-safety)
        {
            _pauseGrace = 0;
            DungeonAudio.SetWant(this, true);
            _updateAccum += PollMs;
            if (_updateAccum >= UpdateMs) { _updateAccum = 0; Update(); }
        }
        else
        {
            foreach (var v in _voices) v.TargetVolume = 0f;
            if (_active) { _pauseGrace += PollMs; if (_pauseGrace > 400) DungeonAudio.SetWant(this, false); }
            else DungeonAudio.SetWant(this, false);
        }
    }

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220;   // dungeon floors 20-69; battles (220-299) excluded
    }

    private static EnemyRadar? _inst;
    /// <summary>Live on/off for the F1 menu (persists; silent).</summary>
    internal static bool ActiveLive
    {
        get => _inst?._active ?? false;
        set { var i = _inst; if (i == null) return; i._active = value; if (!value) { i.SilenceAll(); DungeonAudio.SetWant(i, false); } ModSettings.SetBool("shadow_radar_on", value); }
    }

    private void SilenceAll()
    {
        foreach (var v in _voices) v.TargetVolume = 0f;
        if (_wavMode) foreach (var v in _wavVoices) v.Set(0f, 0f);
        if (_goldMode) foreach (var v in _goldVoices) v.Set(0f, 0f);
    }

    private void Toggle()
    {
        if (_active)
        {
            _active = false;
            ModSettings.SetBool("shadow_radar_on", false);
            SilenceAll();
            DungeonAudio.SetWant(this, false);
            Speech.Say("Shadow radar off.", true);
            Log("[EnemyRadar] OFF");
            return;
        }
        // Allow turning on anywhere on the dungeon side (incl. right after a
        // fight); reject only from town/overworld. Intent then persists.
        if (FieldTracker.CurrentMajor < 20)
        {
            Speech.Say("Shadow radar only works in dungeons.", true);
            return;
        }
        _active = true;
        ModSettings.SetBool("shadow_radar_on", true);
        _tracks.Clear(); _oppArmed = false; _dangerArmed = false;
        Speech.Say("Shadow radar on.", true);
        Log("[EnemyRadar] ON");
    }

    private void Update()
    {
        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;

        var shadows = float.IsNaN(px) || float.IsNaN(pz)
            ? new List<(float x, float z, float fx, float fz, int type)>()
            : DungeonNav.ShadowsWithType();

        // ── continuous tones + nearest-for-strike ──
        // CAMERA-relative pan (2026-07-11, user request — one model for all dungeon
        // beacons; the old world-axis pan was also mirrored vs the fixed spoken
        // compass, +X = WEST). Same math + sign as NavBeacon. The PITCH channel is
        // unchanged: it carries the facing-you (growl) / facing-away (soft) danger
        // info, not direction. Falls back to the old cardinal pan if no camera.
        var (camFx, camFz) = FieldTracker.CameraForward3D();
        bool hasCam = camFx != 0f || camFz != 0f;

        float nearest = float.MaxValue; float nearX = 0, nearZ = 0, nearCos = -1f; bool nearHasFwd = false;
        for (int i = 0; i < Voices; i++)
        {
            if (i >= shadows.Count) { _voices[i].TargetVolume = 0f; if (_wavMode) _wavVoices[i].Set(0f, 0f); if (_goldMode) _goldVoices[i].Set(0f, 0f); continue; }
            var s = shadows[i];
            float dx = s.x - px, dz = s.z - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist > MaxRange) { _voices[i].TargetVolume = 0f; if (_wavMode) _wavVoices[i].Set(0f, 0f); if (_goldMode) _goldVoices[i].Set(0f, 0f); continue; }

            float vol = MasterGain * SoundSettings.RadarVol / (1f + dist / DistanceScale);
            float pan = 0f, openness = 1f;   // openness: 1 = dead ahead, 0 = dead behind
            if (dist > 1f)
            {
                float ux = dx / dist, uz = dz / dist;
                if (hasCam)
                {
                    pan = Math.Clamp(-(ux * camFz + uz * (-camFx)), -1f, 1f);
                    openness = Math.Clamp((ux * camFx + uz * camFz + 1f) * 0.5f, 0f, 1f);
                }
                else pan = Math.Clamp(ux, -1f, 1f);
            }
            // near-field: swell + pulse (pulse rate rises as the Shadow closes in)
            float near = dist < NearRange ? 1f - dist / NearRange : 0f;
            vol *= 1f + NearBoost * near;
            vol *= BehindGain + (1f - BehindGain) * openness;
            float rate = BehindRate + (AheadRate - BehindRate) * openness;
            float pulseHz = near > 0f ? PulseMaxHz * (0.25f + 0.75f * near) : 0f;

            // cos angle between the Shadow's forward and the direction to you
            // (view-cone detection, FUN_14031DCF0). >ConeSeeCos = it sees you.
            bool hasFwd = s.fx != 0 || s.fz != 0;
            float cos = (hasFwd && dist > 1f) ? (s.fx * (-dx) + s.fz * (-dz)) / dist : -1f;
            bool seesYou = hasFwd && cos > ConeSeeCos;
            if (dist < nearest) { nearest = dist; nearX = s.x; nearZ = s.z; nearCos = cos; nearHasFwd = hasFwd; }
            if (_wavMode)
            {
                bool gold = _goldMode && s.type == 3;
                var use = gold ? _goldVoices[i] : _wavVoices[i];
                var other = gold ? _wavVoices[i] : (_goldMode ? _goldVoices[i] : null);
                use.Playing = true;
                float g = vol * WavBoost;
                if (gold && SoundSettings.RadarVol > 0.01f) g = g / SoundSettings.RadarVol * SoundSettings.GoldVol;
                use.Set(g, pan, rate * (seesYou ? SeesYouRate : 1f));
                if (!gold && _goldMode) _goldVoices[i].Set(0f, 0f);
                else if (gold) _wavVoices[i].Set(0f, 0f);
            }
            else _voices[i].Set(vol, pan, BaseFreq * rate, pulseHz);
        }

        // ── strike one-shots: view-cone + range + line-of-sight ──
        // LOS gate: a wall between you and the Shadow means it can't see/reach
        // you (and you can't strike it). Cone: it must actually be looking at
        // you (danger) or you clearly behind it (opportunity), not just "front
        // hemisphere".
        bool clear = nearest <= StrikeRange && ClearPath(px, pz, nearX, nearZ);
        bool danger = clear && nearHasFwd && nearCos > ConeSeeCos;
        bool opp = clear && nearHasFwd && nearCos < ConeBackCos;
        if (opp && !_oppArmed) { _oppArmed = true; FireCue(true); }
        if (!opp) _oppArmed = false;
        if (danger && !_dangerArmed) { _dangerArmed = true; FireCue(false); }
        if (!danger) _dangerArmed = false;

        // ── chase events ──
        if (!float.IsNaN(px) && !float.IsNaN(pz)) UpdateChase(shadows, px, pz);
    }

    private void FireCue(bool opportunity)
    {
        Log($"[EnemyRadar] strike cue: {(opportunity ? "OPPORTUNITY (behind it — strike)" : "DANGER (it faces you)")}");
        // 2026-10-02: the "behind it" opportunity no longer sounds — "Strike." fired at 500u whatever your
        // facing or reach, so players swung and whiffed. AmbushCue chimes only when a swing would land.
        if (opportunity) { }
        else { _cue.Trigger(700f, 340f, 0.45f * SoundSettings.RadarVol, 240f); }   // ("It sees you." speech removed per user 2026-06-24; danger tone kept)
    }

    /// <summary>
    /// Track each Shadow across updates (matched by nearest prior position) and
    /// fire speech when one starts chasing, stops/loses you, or lunges.
    /// </summary>
    private void UpdateChase(List<(float x, float z, float fx, float fz, int type)> shadows, float px, float pz)
    {
        var used = new bool[_tracks.Count];
        var next = new List<Track>(shadows.Count);

        foreach (var s in shadows)
        {
            float dist = MathF.Sqrt((s.x - px) * (s.x - px) + (s.z - pz) * (s.z - pz));

            int idx = -1; float bestD = ChaseMatchRadius;
            for (int t = 0; t < _tracks.Count; t++)
            {
                if (used[t]) continue;
                float d = MathF.Sqrt((s.x - _tracks[t].X) * (s.x - _tracks[t].X) + (s.z - _tracks[t].Z) * (s.z - _tracks[t].Z));
                if (d < bestD) { bestD = d; idx = t; }
            }

            if (idx < 0)
            {
                next.Add(new Track { X = s.x, Z = s.z, Dist = dist });
                continue;
            }

            used[idx] = true;
            var tr = _tracks[idx];
            float moved = bestD;
            float ddist = dist - tr.Dist;   // <0 = getting closer
            bool reliable = moved < MaxMatchStep;   // else it's a track mismatch, ignore this frame

            // Chasers turn to FACE you; wanderers that randomly drift closer do
            // not. Requiring the Shadow to be looking at you filters out the
            // false "chasing" that produced spurious "Shadow stopped".
            float cos = (s.fx != 0 || s.fz != 0) && dist > 1f
                ? (s.fx * (px - s.x) + s.fz * (pz - s.z)) / dist : -1f;
            bool facingPlayer = cos > ConeSeeCos;

            if (reliable && moved > LungePerUpdate && ddist < -ApproachPerUpdate && facingPlayer && dist < ChaseAwareRange
                && Environment.TickCount64 - _lastDodgeMs > CueRepeatMs)
            {
                _lastDodgeMs = Environment.TickCount64;
                Speech.Say("Dodge.", true);
            }

            // Constant-distance pursuit (circling/keeping pace) is NOT "stopped";
            // only an actual recede or going stationary is.
            if (reliable)
            {
                if (ddist < -ApproachPerUpdate) { tr.ApproachStreak++; tr.RecedeStreak = 0; }
                else if (ddist > RecedePerUpdate || moved < StationaryMove) { tr.RecedeStreak++; tr.ApproachStreak = 0; }
                // else: moving but constant distance — leave both streaks unchanged.
            }

            // Chasing tracked silently (the game has its own chase sound); we only
            // announce the STOP. Must be approaching AND facing you to count.
            if (!tr.Chasing && tr.ApproachStreak >= ApproachStreakOn && dist < ChaseAwareRange && facingPlayer)
                tr.Chasing = true;
            else if (tr.Chasing && tr.RecedeStreak >= RecedeStreakOff)
                tr.Chasing = false;   // (was: "Shadow stopped." — removed per user, 2026-06-24)

            tr.X = s.x; tr.Z = s.z; tr.Dist = dist;
            next.Add(tr);
        }

        _tracks.Clear();
        _tracks.AddRange(next);
    }

    private sealed class Track
    {
        public float X, Z, Dist;
        public bool Chasing;
        public int ApproachStreak, RecedeStreak;
    }

    /// <summary>
    /// True if the straight line from (ax,az) to (bx,bz) crosses no minimap wall
    /// cell (flag 2). Doors are walkable cells (flag 1) so a clear doorway counts
    /// as clear; a wall in between blocks it. Partial fix — the minimap can't
    /// tell a closed door from an open one, so the Ghidra aggro-state read is the
    /// authoritative follow-up.
    /// </summary>
    private static bool ClearPath(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1f) return true;
        float nx = dx / len, nz = dz / len;
        const float step = 120f;
        for (float t = step; t < len; t += step)
        {
            if (MinimapTracker.WorldToCell(ax + nx * t, az + nz * t, out int r, out int c)
                && MinimapTracker.ReadCell(r, c, out var cell) && cell.Flag == 2)
                return false;
        }
        return true;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>Continuous Shadow voice (volume/pan/pitch + facing-you tremolo).</summary>
    private sealed class ShadowVoice : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }
        public volatile float TargetVolume;
        private volatile float _targetPan;
        private volatile float _targetFreq = 220f;
        private volatile float _pulseHz;   // 0 = steady tone; >0 = amplitude pulse at this rate

        private float _vol, _pan, _freq = 220f;
        private double _phase, _tremPhase;
        private readonly float _sr;

        public ShadowVoice(WaveFormat fmt) { WaveFormat = fmt; _sr = fmt.SampleRate; }

        public void Set(float vol, float pan, float freq, float pulseHz)
        { TargetVolume = vol; _targetPan = pan; _targetFreq = freq; _pulseHz = pulseHz; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            const float smooth = 0.0008f;
            for (int n = 0; n < frames; n++)
            {
                _vol += (TargetVolume - _vol) * smooth;
                _pan += (_targetPan - _pan) * smooth;
                _freq += (_targetFreq - _freq) * smooth;

                float s;
                if (_vol < 1e-4f) { s = 0f; _phase = 0; _tremPhase = 0; }
                else
                {
                    _phase += _freq / _sr;
                    if (_phase >= 1.0) _phase -= 1.0;
                    float amp = _vol;
                    float ph = _pulseHz;
                    if (ph > 0f)
                    {
                        _tremPhase += ph / _sr;
                        if (_tremPhase >= 1.0) _tremPhase -= 1.0;
                        amp *= 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin((float)_tremPhase * MathF.Tau));
                    }
                    s = MathF.Sin((float)_phase * MathF.Tau) * amp;
                }

                float angle = (_pan + 1f) * 0.25f * MathF.PI;
                buffer[offset + n * 2] = s * MathF.Cos(angle);
                buffer[offset + n * 2 + 1] = s * MathF.Sin(angle);
            }
            return count;
        }
    }

    /// <summary>One-shot loud cue: a short pitch sweep (f0→f1) with a smooth
    /// envelope, centred. Triggered for the strike / danger cues.</summary>
    private sealed class CueVoice : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }
        private readonly float _sr;
        private readonly object _lock = new();
        private int _remaining, _total;
        private float _f0, _f1, _gain;
        private double _phase;

        public CueVoice(WaveFormat fmt) { WaveFormat = fmt; _sr = fmt.SampleRate; }

        public void Trigger(float f0, float f1, float gain, float durMs)
        {
            lock (_lock)
            {
                _total = _remaining = (int)(_sr * durMs / 1000f);
                _f0 = f0; _f1 = f1; _gain = gain; _phase = 0;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            for (int n = 0; n < frames; n++)
            {
                float s = 0f;
                lock (_lock)
                {
                    if (_remaining > 0 && _total > 0)
                    {
                        float t = 1f - _remaining / (float)_total;   // 0..1
                        float f = _f0 + (_f1 - _f0) * t;
                        _phase += f / _sr;
                        if (_phase >= 1.0) _phase -= 1.0;
                        float env = MathF.Sin(t * MathF.PI);          // smooth attack+decay
                        s = MathF.Sin((float)_phase * MathF.Tau) * env * _gain;
                        _remaining--;
                    }
                }
                buffer[offset + n * 2] = s;
                buffer[offset + n * 2 + 1] = s;
            }
            return count;
        }
    }
}
