using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Base for the toggle-able audio beacons (exit stairs, chests). Loops a sound
/// toward the nearest target of a kind: <b>panned</b> left/right toward it and
/// <b>louder as you approach</b> (steep falloff so far targets are quiet). Off
/// by default; shares the one <see cref="DungeonAudio"/> output. Subclasses
/// supply the key, sound file, spoken label, and how to find the target.
///
/// Only runs in dungeons (major &gt;= 20 excl. 240 / 250+).
/// </summary>
internal abstract class ProximityBeacon
{
    private const int PollMs = 50;
    protected const float Gain = 0.6f;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _keyWas;
    private volatile bool _active;
    private float _grace;
    private readonly BeaconVoice _voice;
    private readonly bool _loaded;

    protected abstract int Vk { get; }
    protected abstract string SoundFile { get; }
    protected abstract string Label { get; }
    /// <summary>Silent pause inserted between loop repeats (0 = seamless). Lets a
    /// short clip repeat as a spaced beacon ping instead of a continuous drone.</summary>
    protected virtual float LoopGapSeconds => 0f;
    /// <summary>If true, a missing sound file is OK — BeaconVoice synthesizes a tone instead.</summary>
    protected virtual bool AllowSynth => false;
    /// <summary>Find the nearest target's world XZ; false if none.</summary>
    protected abstract bool FindTarget(float px, float pz, out float tx, out float tz);

    protected ProximityBeacon()
    {
        bool wav = BeaconVoice.TryLoadMono(SoundFile, out var mono);
        _loaded = wav || AllowSynth;   // a synth-tone beacon (no WAV) is still usable
        int gapFrames = (int)(LoopGapSeconds * DungeonAudio.Format.SampleRate);
        _voice = new BeaconVoice(DungeonAudio.Format, mono, gapFrames);
        DungeonAudio.AddInput(_voice);

        _thread = new Thread(PollLoop) { IsBackground = true, Name = GetType().Name };
        _thread.Start();
        Log($"[{GetType().Name}] ready ({(char)Vk} to toggle){(_loaded ? "" : " — sound missing, disabled")}");
    }

    public void Stop() { _stopped = true; _active = false; DungeonAudio.SetWant(this, false); }

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[{GetType().Name}] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        // Alt-tabbed away: don't read the toggle key and silence the beacon (the
        // _active intent is preserved, so it resumes on refocus).
        if (!Utils.GameHasFocus())
        {
            _voice.Playing = false;
            DungeonAudio.SetWant(this, false);
            return;
        }

        // Shift-up so Shift+, (the chest beacon's comma) is reserved for the
        // movie-subtitle toggle and never fires the beacon.
        bool k = IsKeyDown(Vk) && !IsKeyDown(0x10 /*VK_SHIFT*/);
        if (k && !_keyWas) { Log($"[{GetType().Name}] key {(char)Vk} edge"); Toggle(); }
        _keyWas = k;

        // CRASH FIX 2026-06-12: short-circuit BEFORE touching player position.
        // This Tick used to read LivePlayerX/Z every 50ms in EVERY context
        // (overworld included, beacon off) — hammering the fieldObj chain
        // through area transitions, which is where the chronic AVE crashes
        // came from (crash-dump stack: ProximityBeacon.Tick → PlayerX2DLive).
        if (!_active || !InDungeon() || FieldTracker.InAreaTransition)   // back off during a transition
        {
            _voice.Playing = false;
            DungeonAudio.SetWant(this, false);
            return;
        }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        float tx = 0, tz = 0;
        bool want = !float.IsNaN(px) && !float.IsNaN(pz)
                    && FindTarget(px, pz, out tx, out tz);

        if (want)
        {
            _grace = 0;
            float dx = tx - px, dz = tz - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            // PURE CARDINAL (world axes, not relative to facing): pan = east/west
            // (+X east → right), pitch = north/south (+Z north → higher). So a target
            // due north is a centred HIGHER tone, due east a right-panned natural tone,
            // due south a centred LOWER tone, due west a left-panned tone.
            float pan = dist > 1f ? Math.Clamp(dx / dist, -1f, 1f) : 0f;
            float ns = dist > 1f ? Math.Clamp(dz / dist, -1f, 1f) : 0f;
            float rate = 1f + ns * 0.4f;               // north → higher pitch, south → lower

            float near = dist / 1000f;
            float gain = Gain / (1f + near * near);    // steep: quiet far, loud near

            DungeonAudio.SetWant(this, true);
            _voice.Set(gain, pan, rate);
            _voice.Playing = true;
        }
        else
        {
            // Ride out a brief target/position read failure before stopping.
            _voice.Playing = false;
            if (_active) { _grace += PollMs; if (_grace > 500) DungeonAudio.SetWant(this, false); }
            else DungeonAudio.SetWant(this, false);
        }
    }

    private void Toggle()
    {
        if (!_loaded) { Speech.Say($"{Label} unavailable, sound file missing.", true); return; }
        if (_active)
        {
            _active = false;
            _voice.Playing = false;
            DungeonAudio.SetWant(this, false);
            Speech.Say($"{Label} off.", true);
            Log($"[{GetType().Name}] OFF");
            return;
        }
        if (FieldTracker.CurrentMajor < 20) { Speech.Say($"{Label} only works in dungeons.", true); return; }
        _active = true;
        _grace = 0;
        Speech.Say($"{Label} on.", true);
        Log($"[{GetType().Name}] ON");
    }

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220;   // dungeon floors 20-69; battles (220-299) excluded
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}
