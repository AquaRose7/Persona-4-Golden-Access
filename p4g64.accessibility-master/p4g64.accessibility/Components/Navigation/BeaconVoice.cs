using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// A looping mono sample for the proximity beacons. While <see cref="Playing"/>,
/// it loops the whole sample at its natural rate (no chopping/pulsing), with
/// smoothed gain + stereo pan (no clicks when it starts, stops, or pans). Shared
/// by <see cref="ExitBeacon"/> and <see cref="ChestBeacon"/>, mixed through
/// <see cref="DungeonAudio"/>.
/// 3D sound (2026-10-18, ported from P5R Access): with <see cref="Hrtf3D"/> on (the settings menu's General → "3D sound
/// (HRTF)", off by default) the voice goes through the measured head (<see cref="Spatializer"/>) from the direction its
/// owner gives with <see cref="Aim"/>; off, the pan / inter-aural delay / <see cref="Openness"/> muffle below, unchanged.
/// </summary>
internal sealed class BeaconVoice : ISampleProvider
{
    /// <summary>3D sound on (the setting, when the head file loaded): every voice goes through the head.</summary>
    public static volatile bool Hrtf3D;

    private readonly Spatializer? _spatial;
    private volatile float _tgtAz, _tgtEl;
    private bool _wasHrtf;

    public WaveFormat WaveFormat { get; }
    public volatile bool Playing;

    private readonly float[] _mono;
    private volatile int _gapFrames;     // silent frames inserted between loops (runtime-settable = tick rate)
    private int _gapLeft;

    /// <summary>Silent frames between loops = the TICK interval. Larger = slower
    /// ticks (used by the overworld beacon to encode distance).</summary>
    public int GapFrames { set => _gapFrames = Math.Max(0, value); }
    private volatile float _tgtGain, _tgtPan, _tgtRate = 1f, _tgtOpen = 1f;
    private float _gain, _pan, _rate = 1f, _open = 1f;
    private float _posF;     // fractional read position (for pitch/rate resampling)
    private float _lpState;  // one-pole low-pass state (front/back "muffle")
    private readonly float[] _hist = new float[64];   // short history for the inter-aural delay
    private int _histPos;
    private const float MaxItd = 28f;                 // max inter-aural delay in samples (~0.6 ms)

    /// <summary>Tone openness 0..1: 1 = bright (target ahead), 0 = muffled (behind).
    /// A cheap one-pole low-pass; left at 1 it's transparent (dungeon beacons).</summary>
    public float Openness { set => _tgtOpen = Math.Clamp(value, 0f, 1f); }

    public BeaconVoice(WaveFormat fmt, float[] mono, int gapFrames = 0)
    {
        WaveFormat = fmt;
        // No sample supplied → synthesize a short sine "ping" so a beacon can still
        // sound (used by the door beacon, which has no WAV). Pitch is set via the
        // rate knob; a brief fade in/out avoids clicks at the loop seam.
        if (mono == null || mono.Length == 0)
        {
            int n = (int)(fmt.SampleRate * 0.15f);
            var m = new float[n];
            int fade = Math.Max(1, n / 16);
            for (int i = 0; i < n; i++)
            {
                float env = i < fade ? i / (float)fade : (i > n - fade ? (n - i) / (float)fade : 1f);
                m[i] = MathF.Sin(2f * MathF.PI * 520f * i / fmt.SampleRate) * 0.7f * env;
            }
            mono = m;
        }
        _mono = mono;
        _gapFrames = Math.Max(0, gapFrames);
        if (DungeonAudio.Hrtf is { } head) _spatial = new Spatializer(head);
    }

    /// <summary><paramref name="rate"/> = playback speed = PITCH (1 = natural; &gt;1 higher, &lt;1 lower).
    /// A non-finite value is taken as silence (gain), centre (pan) or natural pitch: one NaN would poison the voice for
    /// good — gain, filter and history all NaN (P5R review, 2026-10-18).</summary>
    public void Set(float gain, float pan, float rate = 1f)
    {
        _tgtGain = float.IsFinite(gain) ? gain : 0f;
        _tgtPan = float.IsFinite(pan) ? pan : 0f;
        _tgtRate = float.IsFinite(rate) ? Math.Clamp(rate, 0.4f, 2.2f) : 1f;
    }

    /// <summary>The 3D DIRECTION: azimuth in degrees CLOCKWISE from the camera's forward (90 = right), elevation up.
    /// Heard only with <see cref="Hrtf3D"/> on; the pan mode keeps using <see cref="Set"/>'s pan.</summary>
    public void Aim(float azimuthDeg, float elevationDeg = 0f)
    {
        if (!float.IsFinite(azimuthDeg) || !float.IsFinite(elevationDeg)) return;   // keep the last good direction
        _tgtAz = azimuthDeg; _tgtEl = elevationDeg;
    }

    /// <summary>The azimuth (degrees clockwise from ahead) of a direction given as its right (+) / left (−) part and its
    /// ahead (+) / behind (−) part — the pan and the (2 × openness − 1) every beacon already computes.</summary>
    public static float Azimuth(float right, float ahead) => MathF.Atan2(right, ahead) * (180f / MathF.PI);

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        const float smooth = 0.0009f;
        bool hrtf = Hrtf3D && _spatial != null;
        if (hrtf != _wasHrtf) { _wasHrtf = hrtf; _spatial?.Reset(); _lpState = 0f; }
        if (hrtf) _spatial!.Aim(_tgtAz, _tgtEl);
        for (int n = 0; n < frames; n++)
        {
            float target = Playing ? _tgtGain : 0f;
            _gain += (target - _gain) * smooth;
            // A fade to silence must END: the wall and door voices stay Playing at gain 0, and a gain that only tends to
            // 0 kept them computing forever — in 3D never skipped as silent, and sinking into denormals (P5R, +11 % of a
            // core live, 2026-10-17)
            if (target == 0f && _gain < 1e-5f) _gain = 0f;
            _pan += (_tgtPan - _pan) * smooth;
            _rate += (_tgtRate - _rate) * smooth;
            _open += (_tgtOpen - _open) * smooth;

            float s = 0f;
            if (_mono.Length > 0 && (Playing || _gain > 1e-4f))
            {
                if (_gapLeft > 0) { _gapLeft--; s = 0f; }   // silent gap between loops
                else
                {
                    int i0 = (int)_posF; if (i0 >= _mono.Length) i0 = 0;
                    int i1 = i0 + 1 < _mono.Length ? i0 + 1 : 0;
                    float frac = _posF - (int)_posF;
                    s = (_mono[i0] + (_mono[i1] - _mono[i0]) * frac) * _gain;   // pitch = resample by _rate
                    _posF += _rate > 0f ? _rate : 1f;
                    if (_posF >= _mono.Length) { _posF -= _mono.Length; _gapLeft = _gapFrames; }   // loop + start gap
                }
            }
            else { _posF = 0; _gapLeft = 0; }

            if (hrtf)
            {
                // the head places it (with its own tiny back muffle); the Openness muffle is the pan mode's only
                _spatial!.Process(s, out buffer[offset + n * 2], out buffer[offset + n * 2 + 1]);
                continue;
            }

            // One-pole low-pass: a≈1 passes everything (bright), small a muffles.
            float a = 0.08f + 0.92f * _open;
            _lpState += a * (s - _lpState);
            if (MathF.Abs(_lpState) < 1e-15f) _lpState = 0f;   // a dying tail must not turn into denormals
            s = _lpState;

            // Inter-aural time delay (ITD): the ear FARTHER from the source hears it
            // a fraction of a ms later — the cue that makes direction feel 3D, not
            // just "panned". Delay the contralateral ear by |pan| × MaxItd samples.
            int size = _hist.Length;
            _hist[_histPos] = s;
            float d = MathF.Abs(_pan) * MaxItd;
            int di = (int)d; float df = d - di;
            int j0 = ((_histPos - di) % size + size) % size;
            int j1 = ((j0 - 1) % size + size) % size;
            float delayed = _hist[j0] + (_hist[j1] - _hist[j0]) * df;
            _histPos = (_histPos + 1) % size;

            float angle = (_pan + 1f) * 0.25f * MathF.PI;
            float lSig = _pan > 0f ? delayed : s;   // source on the right → left ear lags
            float rSig = _pan < 0f ? delayed : s;   // source on the left  → right ear lags
            buffer[offset + n * 2]     = lSig * MathF.Cos(angle);
            buffer[offset + n * 2 + 1] = rSig * MathF.Sin(angle);
        }
        return count;
    }

    /// <summary>
    /// Make a LOOPED sample seamless: crossfade its last <paramref name="fadeFrames"/> into its
    /// first ones (equal-power) and drop that tail, so the end flows into the start with no jump.
    /// The wall WAVs are 2.2 s loops whose last sample ≠ first (wallSouth −0.73 → −0.16): the raw
    /// loop CLICKED and dipped every 2.2 s — measured by loopback recording, 2026-09-29 (Haru's
    /// "wall/door stutter"). Returns the input unchanged when it is too short to fade.
    /// </summary>
    public static float[] MakeSeamless(float[] mono, int fadeFrames)
    {
        int n = mono.Length;
        if (fadeFrames <= 0 || n < fadeFrames * 3) return mono;
        int len = n - fadeFrames;
        var r = new float[len];
        Array.Copy(mono, fadeFrames, r, fadeFrames, len - fadeFrames);
        for (int i = 0; i < fadeFrames; i++)
        {
            float t = (i + 0.5f) / fadeFrames;
            float gin = MathF.Sin(t * MathF.PI * 0.5f), gout = MathF.Cos(t * MathF.PI * 0.5f);
            r[i] = mono[i] * gin + mono[len + i] * gout;
        }
        return r;
    }

    /// <summary>
    /// Load a WAV from database/sounds, downmix to mono at the mixer rate.
    /// Handles the path quirk (database is in the nested "Persona 4 golden"
    /// subfolder, not under the game-exe CurrentDirectory).
    /// </summary>
    public static bool TryLoadMono(string fileName, out float[] mono)
    {
        mono = Array.Empty<float>();
        try
        {
            // RELEASE: bundled flat in the mod folder. DEV: database/sounds.
            string path = DataPath(fileName, "sounds");
            if (!File.Exists(path)) { Log($"[Beacon] {fileName} not found (tried: {path})"); return false; }

            using var reader = new AudioFileReader(path);
            ISampleProvider sp = reader;
            if (sp.WaveFormat.SampleRate != DungeonAudio.Format.SampleRate)
                sp = new WdlResamplingSampleProvider(sp, DungeonAudio.Format.SampleRate);
            int ch = sp.WaveFormat.Channels;

            var all = new List<float>();
            var buf = new float[8192];
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++) all.Add(buf[i]);

            int frames = all.Count / ch;
            var m = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float a = 0; for (int c = 0; c < ch; c++) a += all[i * ch + c];
                m[i] = a / ch;
            }
            mono = m;
            Log($"[Beacon] loaded {fileName} ({frames} frames, {frames / (float)DungeonAudio.Format.SampleRate:F2}s)");
            return m.Length > 0;
        }
        catch (Exception ex) { Log($"[Beacon] load error {fileName}: {ex.GetType().Name}: {ex.Message}"); return false; }
    }
}
