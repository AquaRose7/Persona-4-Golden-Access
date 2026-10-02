using System.Text;
using NAudio.Wave;
using p4g64.accessibility.Components.Navigation;
using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Fishing (2026-10-02 — live-RE'd on Haru's save + Ghidra; source of truth
/// memory/fishing_accessibility.md). The minigame is entirely visual; everything here is read
/// from the game's own task work structs through the named-task registry (no hooks), and it only
/// INFORMS — the game plays exactly as for a sighted player (Haru: no assist, "I don't want to cheat").
///
///  1. BAIT MENU — task PROC_FISHBAIT_SETTING alive = the menu is up. Task cmmFishingBaitSelect
///     (update FUN_1401D1CD0) work: +0x3C cursor, +0x38 count, +0x08 = the game's own bait list
///     (+0x10 first node, node+0x18 next, node+0x20 → u16 item id; built from the bait items 908-912
///     at the river / 913-915 at sea, owned ones only). The help line is the item's own description.
///  2. BITE — task cmmFishing (update FUN_1401CE120) work +0x00 flag 0x40 = the bobber sinks; the
///     player has ~0.75 s to press Action, so it gets an instant chime (speech is too slow).
///  3. REEL GAUGE — cmmFishing work +0x04 state 6/7 = reeling (FUN_1401CCC10). The gauge task
///     cmmFishingHELP work: +0xBC = the needle's SEGMENT 0..6 left→right (3 = BLUE = the only place
///     to hold Action), +0xC0 = needle − fish offset; the bar is drawn at x = 481 + offset over
///     189..773 (FUN_1401CBC50 constants), so (offset + 292) / 584 = the needle's exact place on the
///     bar. The needle moves ~300 units/s with momentum and crosses blue in ~0.2 s, so the tone
///     follows the EXACT position (pitch rises smoothly away from the middle, pan = side) and blue
///     gets its own steady "hold" sound + an entry tick; the output runs short-delay while reeling.
///     Holding Action outside blue fills a strain meter; each fill = a STRIKE (cmmFishing +0x88),
///     more than 4 = the line snaps → "Strain N" is spoken on each strike.
/// </summary>
internal sealed unsafe class FishingReader
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] BaitSettingTask = Encoding.ASCII.GetBytes("PROC_FISHBAIT_SETTING");
    private static readonly byte[] BaitSelectTask  = Encoding.ASCII.GetBytes("cmmFishingBaitSelect");
    private static readonly byte[] FishingTask     = Encoding.ASCII.GetBytes("cmmFishing");
    private static readonly byte[] GaugeTask       = Encoding.ASCII.GetBytes("cmmFishingHELP");
    private static readonly nint InvCountBasePtr   = unchecked((nint)0x141165930L);   // byte count per item id

    private const int BiteFlag = 0x40;
    private const float BarHalf = 292f;   // half the 584-px bar (FUN_1401CBC50: x = 189 + 292 + offset)

    private readonly GaugeVoice _voice = new();
    private int _lastBaitCursor = -1, _lastBaitId = -1;
    private bool _biteHeard;
    private bool _gaugeOn;
    private int _lastStrikes;
    private static bool _tipSpoken;

    internal FishingReader()
    {
        DungeonAudio.AddInput(_voice);   // mixer rule: inputs only at startup
        new Thread(Poll) { IsBackground = true, Name = "FishingReader" }.Start();
        Log("[Fishing] ready (bait menu, bite cue, reel gauge tone)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(_gaugeOn ? 10 : 25);   // the needle crosses blue in ~0.2 s — poll fast while reeling
            try { Tick(); } catch { Thread.Sleep(500); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) { StopGauge(); return; }

        ReadBaitMenu();

        nint fish = TaskWork(FishingTask);
        if (fish == 0) { StopGauge(); _biteHeard = false; return; }

        int flags, state;
        if (!TryReadRaw(fish, &flags, 4) || !TryReadRaw(fish + 4, &state, 4)) { StopGauge(); return; }

        // Bite: the flag rises once per bite and stays set through the reel.
        bool bite = (flags & BiteFlag) != 0;
        if (bite && !_biteHeard)
        {
            ToneCue.PlayCue("fish_bite.wav", 0.55f * SoundSettings.FishBiteVol, (1320f, 70), (0f, 25), (1760f, 70), (0f, 25), (1320f, 90));
            Log($"[Fishing] bite (flags 0x{flags:X})");
        }
        _biteHeard = bite;

        if (state == 6 || state == 7) ReadGauge(fish);
        else StopGauge();
    }

    private void ReadBaitMenu()
    {
        if (TaskNode(BaitSettingTask) == 0) { _lastBaitCursor = _lastBaitId = -1; return; }
        nint work = TaskWork(BaitSelectTask);
        if (work == 0) return;
        int cursor, count;
        nint list, node, data;
        if (!TryReadRaw(work + 0x3C, &cursor, 4) || !TryReadRaw(work + 0x38, &count, 4)) return;
        if (count < 1 || count > 8 || cursor < 0 || cursor >= count) return;
        // Walk the game's own list to the cursor's row. The "selected node" pointer (+0x10) is
        // updated a frame AFTER the cursor, so reading it named the previous bait (Haru 10-02:
        // "3 of 4" said Yaso Locust again).
        if (!TryReadRaw(work + 0x08, &list, 8) || list == 0) return;
        if (!TryReadRaw(list + 0x10, &node, 8)) return;
        for (int i = 0; i < cursor && node != 0; i++)
            if (!TryReadRaw(node + 0x18, &node, 8)) return;
        if (node == 0 || !TryReadRaw(node + 0x20, &data, 8) || data == 0) return;
        ushort id;
        if (!TryReadRaw(data, &id, 2) || id < 768 || id > 1023) return;
        if (cursor == _lastBaitCursor && id == _lastBaitId) return;
        _lastBaitCursor = cursor;
        _lastBaitId = id;

        string name = Item.GetName(id);
        if (string.IsNullOrEmpty(name)) name = $"Bait {id}";
        int owned = OwnedCount(id);
        string desc = Item.GetDescription(id);
        var sb = new StringBuilder(name);
        if (owned > 0) sb.Append(", ").Append(owned);
        sb.Append('.');
        if (!string.IsNullOrEmpty(desc)) sb.Append(' ').Append(desc.TrimEnd('.')).Append('.');
        sb.Append(' ').Append(cursor + 1).Append(" of ").Append(count).Append('.');
        Speech.Say(sb.ToString(), true);
    }

    private void ReadGauge(nint fish)
    {
        nint gauge = TaskWork(GaugeTask);
        int seg, strikes;
        float offset;
        if (gauge == 0 || !TryReadRaw(gauge + 0xBC, &seg, 4) || seg < 0 || seg > 6
            || !TryReadRaw(gauge + 0xC0, &offset, 4) || float.IsNaN(offset)) { StopGauge(); return; }
        if (!TryReadRaw(fish + 0x88, &strikes, 4)) strikes = 0;

        if (!_gaugeOn)
        {
            _gaugeOn = true;
            _lastStrikes = strikes;
            DungeonAudio.SetLowLatency(_voice, true);
            DungeonAudio.SetWant(_voice, true);
            if (!_tipSpoken)
            {
                _tipSpoken = true;
                // Haru 10-02: the game balances the analog stick against the fish's pull; keys now drive a
                // virtual stick while reeling (OnInputFrame), so the same advice fits both.
                Speech.Say("Reeling. Steer against the drift with left and right: a short press pushes gently, holding pushes harder. " +
                           "The pitch tells you which way it moves. Hold Action only on the steady low sound.", false);
            }
        }

        ReelActive = true;
        float pos = Math.Clamp((offset + BarHalf) / (2f * BarHalf), 0f, 1f);
        _voice.Set(pos, seg == 3);

        if (strikes > _lastStrikes && strikes <= 5) Speech.Say($"Strain {strikes}.", true);
        _lastStrikes = strikes;

#if DEBUG
        DiagGauge(fish, seg, offset, strikes);
#endif
    }

    // ── keyboard → virtual analog stick while reeling (Haru 10-02: "most blind players use keyboard") ──
    // The reel code (FUN_1401CCC10) reads the d-pad bits in the pad byte 0x15E3FD8E0 (0x80 left, 0x20
    // right) as a fixed full-strength kick every frame, but balances the analog stick X byte
    // 0x15E3FD8F0 (rest 127) against the fish's pull — that's why a controller can hold the bobber
    // still and keys can't. While reeling, a held left/right is turned into a stick push that starts
    // gentle and grows to full over ~0.75 s; releasing recenters it, so the next tap is gentle again.
    // The game still plays its own rules — the keys just get the input the gauge was designed for.
    internal static volatile bool ReelActive;
    private static readonly nint PadByte = unchecked((nint)0x15E3FD8E0L);
    private static readonly nint StickXByte = unchecked((nint)0x15E3FD8F0L);
    private static bool _inputOk, _inputChecked;
    private static int _holdFrames, _holdDir;

    /// <summary>Called every frame from ControllerInput's hook right after the game's input function.</summary>
    internal static unsafe void OnInputFrame()
    {
        if (!ReelActive) { _holdFrames = 0; _holdDir = 0; return; }
        if (!_inputChecked)
        {
            _inputChecked = true;
            _inputOk = ProbeReadable(PadByte, 1) && ProbeReadable(StickXByte, 1);   // constant BSS, checked once
        }
        if (!_inputOk) return;

        byte pad = *(byte*)PadByte;
        int dir = (pad & 0x80) != 0 ? -1 : (pad & 0x20) != 0 ? 1 : 0;
        if (dir == 0) { _holdFrames = 0; _holdDir = 0; return; }   // a real stick (no d-pad bits) passes untouched
        if (dir != _holdDir) { _holdDir = dir; _holdFrames = 0; }
        _holdFrames++;

        float push = Math.Min(1f, 0.1f + _holdFrames / 45f);          // 10% → full over ~45 frames
        *(byte*)PadByte = (byte)(pad & ~0xA0);                         // the game takes the stick path, not the kick
        *(byte*)StickXByte = (byte)Math.Clamp(127 + (int)MathF.Round(dir * push * 127f), 0, 255);
    }

    private void StopGauge()
    {
        ReelActive = false;
        if (!_gaugeOn) return;
        _gaugeOn = false;
        _voice.Off();
        DungeonAudio.SetWant(_voice, false);
        DungeonAudio.SetLowLatency(_voice, false);
    }

#if DEBUG
    // [FishDiag] (2026-10-02, temporary): 4 lines/s while reeling — segment, the gauge offset (+0xC0),
    // needle +0x6C / fish +0x40 / velocity +0x70 / red stress +0x38 / strikes +0x88 (cmmFishing work)
    // and the pad byte the reel code reads (0x80 left, 0x20 right).
    private long _diagMs;
    private void DiagGauge(nint f, int seg, float offset, int strikes)
    {
        long now = Environment.TickCount64;
        if (now - _diagMs < 250) return;
        _diagMs = now;
        float needle, fishPos, vel, stress;
        byte pad, stickX;
        if (!TryReadRaw(f + 0x6C, &needle, 4) || !TryReadRaw(f + 0x40, &fishPos, 4)
            || !TryReadRaw(f + 0x70, &vel, 4) || !TryReadRaw(f + 0x38, &stress, 4)) return;
        TryReadRaw(PadByte, &pad, 1);
        TryReadRaw(StickXByte, &stickX, 1);
        Log($"[FishDiag] seg={seg} off={offset:F1} needle={needle:F1} fish={fishPos:F1} vel={vel:F2} stress={stress:F1} strikes={strikes} pad=0x{pad:X2} stickX={stickX} hold={_holdFrames}");
    }
#endif

    private static int OwnedCount(int id)
    {
        nint b;
        byte c;
        if (!TryReadRaw(InvCountBasePtr, &b, 8) || b == 0) return 0;
        return TryReadRaw(b + id, &c, 1) ? c : 0;
    }

    // ── named-task registry (heads above; node name @+0x00, work @+0x48, next @+0x50) ─────────
    private static nint TaskWork(byte[] name)
    {
        nint node = TaskNode(name), work;
        if (node == 0 || !TryReadRaw(node + 0x48, &work, 8)) return 0;
        return work > 0x10000 ? work : 0;
    }

    private static nint TaskNode(byte[] name)
    {
        byte* buf = stackalloc byte[32];
        foreach (nint head in TaskHeads)
        {
            nint node;
            if (!TryReadRaw(head, &node, 8)) continue;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!TryReadRaw(node, buf, name.Length + 1)) break;
                bool match = true;
                for (int j = 0; j < name.Length; j++) if (buf[j] != name[j]) { match = false; break; }
                // Exact match = the name + a NON-PRINTABLE byte (not necessarily NUL) — so
                // "cmmFishing" never matches "cmmFishingHELP".
                byte term = buf[name.Length];
                if (match && (term < 0x20 || term >= 0x7F)) return node;
                if (!TryReadRaw(node + 0x50, &node, 8)) break;
            }
        }
        return 0;
    }

    /// <summary>
    /// The reel-gauge sound. Position tone: pan = the needle's place on the bar, pitch rises smoothly
    /// with the distance from the middle, pulsing faster the farther out it is. In the BLUE lane the
    /// pulse stops and a low octave joins it — the steady "hold Action now" sound — with a short tick
    /// on entering.
    /// </summary>
    private sealed class GaugeVoice : ISampleProvider
    {
        public WaveFormat WaveFormat => DungeonAudio.Format;
        private volatile bool _on, _blue;
        private volatile float _pos = 0.5f;
        private bool _wasBlue;
        private float _phase, _subPhase, _pulsePhase, _tickPhase;
        private float _gain, _sub, _freq = 300f, _panL = 0.707f, _panR = 0.707f;
        private int _tickLeft;

        public void Set(float pos, bool blue) { _pos = pos; _blue = blue; _on = true; }
        public void Off() => _on = false;

        public int Read(float[] buffer, int offset, int count)
        {
            int sr = WaveFormat.SampleRate;
            bool blue = _blue && _on;
            if (blue && !_wasBlue) _tickLeft = sr * 25 / 1000;   // entry tick
            _wasBlue = blue;

            float pos = _pos;
            float dist = Math.Abs(pos - 0.5f) * 2f;              // 0 middle … 1 bar end
            float targetFreq = 260f + 900f * dist;
            float pulse = blue ? 0f : 5f + 13f * dist;
            float level = (blue ? 0.17f : 0.12f + 0.12f * dist) * SoundSettings.FishReelVol;
            float target = _on ? level : 0f;
            float subTarget = blue && _on ? 0.6f : 0f;
            float angle = pos * MathF.PI / 2f;                   // 0 = full left … 1 = full right
            float tl = MathF.Cos(angle), tr = MathF.Sin(angle);

            for (int n = 0; n < count / 2; n++)
            {
                // per-sample smoothing: no clicks on zone / pan / on-off changes, pitch glides
                _gain += (target - _gain) * 0.002f;
                _sub += (subTarget - _sub) * 0.004f;
                _freq += (targetFreq - _freq) * 0.004f;
                _panL += (tl - _panL) * 0.003f;
                _panR += (tr - _panR) * 0.003f;

                _phase += _freq / sr;
                if (_phase >= 1f) _phase -= 1f;
                _subPhase += _freq * 0.5f / sr;
                if (_subPhase >= 1f) _subPhase -= 1f;
                float s = MathF.Sin(2f * MathF.PI * _phase) + _sub * MathF.Sin(2f * MathF.PI * _subPhase);

                float env = 1f;
                if (pulse > 0f)
                {
                    _pulsePhase += pulse / sr;
                    if (_pulsePhase >= 1f) _pulsePhase -= 1f;
                    env = 0.15f + 0.85f * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * _pulsePhase));   // smooth, click-free
                }
                float v = s * _gain * env;

                if (_tickLeft > 0)
                {
                    _tickPhase += 1500f / sr;
                    if (_tickPhase >= 1f) _tickPhase -= 1f;
                    v += MathF.Sin(2f * MathF.PI * _tickPhase) * 0.25f * SoundSettings.FishReelVol * (_tickLeft / (float)(sr * 25 / 1000));
                    _tickLeft--;
                }

                buffer[offset + n * 2] = v * _panL;
                buffer[offset + n * 2 + 1] = v * _panR;
            }
            return count;
        }
    }
}
