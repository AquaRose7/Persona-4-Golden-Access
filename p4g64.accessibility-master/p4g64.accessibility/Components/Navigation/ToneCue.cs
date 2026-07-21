using NAudio.Wave;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// One-shot cue player on the shared DungeonAudio mixer — replaces every kernel32
/// Beep and winmm PlaySound in the mod so ALL sounds get a user volume knob
/// (SettingsMenu, 2026-07-19). Cues are pre-rendered to a mono buffer and played
/// once, centered. A small fixed voice pool is added to the mixer at startup
/// (mixer rule: inputs only at startup); SetWant lingers ~3 s after the last cue
/// so rapid cursor blips don't pay the output-start latency each time.
/// </summary>
internal static class ToneCue
{
    private const int PoolSize = 4;
    private const int LingerMs = 3000;
    private static OneShot[]? _pool;
    private static long _lastPlayTick;
    private static System.Threading.Timer? _lingerTimer;
    private static readonly object _wantKey = new();

    /// <summary>Create + register the pool. Call once from Mod.cs before components.</summary>
    internal static void Init()
    {
        if (_pool != null) return;
        _pool = new OneShot[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new OneShot();
            DungeonAudio.AddInput(_pool[i]);
        }
        _lingerTimer = new System.Threading.Timer(_ => LingerCheck(), null, 1000, 1000);
        Log("[ToneCue] ready (one-shot cue pool on the shared mixer)");
    }

    internal static void PlayTones(float gain, params (float freq, int ms)[] notes)
    {
        try
        {
            if (_pool == null || notes.Length == 0 || gain <= 0f) return;
            int sr = DungeonAudio.Format.SampleRate;
            int total = 0;
            foreach (var (_, ms) in notes) total += Math.Max(0, ms) * sr / 1000;
            if (total <= 0) return;
            var buf = new float[total];
            int pos = 0;
            foreach (var (freq, ms) in notes)
            {
                int n = Math.Max(0, ms) * sr / 1000;
                if (freq > 0f)
                {
                    int fade = Math.Max(1, Math.Min(n / 4, sr * 5 / 1000));   // ≤5ms edges, no clicks
                    for (int i = 0; i < n; i++)
                    {
                        float env = i < fade ? i / (float)fade : (i > n - fade ? (n - i) / (float)fade : 1f);
                        buf[pos + i] = MathF.Sin(2f * MathF.PI * freq * i / sr) * gain * env;
                    }
                }
                pos += n;   // freq 0 = silence gap (buffer already zeroed)
            }
            Submit(buf);
        }
        catch { /* a cue must never break its caller */ }
    }

    internal static void PlayWav(float[] mono, float gain, int maxMs = 2000)
    {
        try
        {
            if (_pool == null || mono == null || mono.Length == 0 || gain <= 0f) return;
            int sr = DungeonAudio.Format.SampleRate;
            int n = Math.Min(mono.Length, Math.Max(1, maxMs) * sr / 1000);
            var buf = new float[n];
            int fade = Math.Max(1, Math.Min(n / 4, sr * 10 / 1000));   // 10ms fade-out on a slice
            for (int i = 0; i < n; i++)
            {
                float env = i > n - fade ? (n - i) / (float)fade : 1f;
                buf[i] = mono[i] * gain * env;
            }
            Submit(buf);
        }
        catch { }
    }

    internal static bool TryLoadWav(string fileName, out float[] mono)
        => BeaconVoice.TryLoadMono(fileName, out mono);

    private static void Submit(float[] rendered)
    {
        var pool = _pool!;
        OneShot slot = pool[0];
        foreach (var v in pool) if (v.Idle) { slot = v; break; }   // else steal pool[0]
        _lastPlayTick = Environment.TickCount64;
        DungeonAudio.SetWant(_wantKey, true);
        slot.Start(rendered);
    }

    private static void LingerCheck()
    {
        try
        {
            var pool = _pool;
            if (pool == null) return;
            bool anyBusy = false;
            foreach (var v in pool) if (!v.Idle) { anyBusy = true; break; }
            if (!anyBusy && Environment.TickCount64 - _lastPlayTick > LingerMs)
                DungeonAudio.SetWant(_wantKey, false);
        }
        catch { }
    }

    /// <summary>Plays one pre-rendered mono buffer once, centered. Reusable.</summary>
    private sealed class OneShot : ISampleProvider
    {
        public WaveFormat WaveFormat => DungeonAudio.Format;
        private volatile float[]? _buf;
        private int _pos;
        public bool Idle => _buf == null;

        public void Start(float[] buf) { _pos = 0; _buf = buf; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            var buf = _buf;
            for (int n = 0; n < frames; n++)
            {
                float s = 0f;
                if (buf != null)
                {
                    if (_pos < buf.Length) s = buf[_pos++];
                    else { _buf = null; buf = null; }
                }
                buffer[offset + n * 2] = s * 0.707f;
                buffer[offset + n * 2 + 1] = s * 0.707f;
            }
            return count;
        }
    }
}
