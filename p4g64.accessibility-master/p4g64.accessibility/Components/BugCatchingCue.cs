using System.Diagnostics;
using System.Text;
using p4g64.accessibility.Components.Navigation;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Bug catching at the Tatsuhime Shrine tree (2026-10-02 — Ghidra + live timing on Haru's save; source of
/// truth memory/bug_catching.md). One timed Action press per day: task <c>insectCapture</c> (update
/// FUN_1403D0CD0) counts a round timer at work +0x1C (+0.5 per frame step = 30 units/s, the round loops at
/// 200 if nobody presses) and grades the press by int(t − 145): PERFECT only while t is in (143, 149) — a
/// 0.2 s window. The game's own sound at t=144 sits at the window's START, so pressing in reaction to it
/// lands late (Haru: "off"), and the "!" sound at t=150 is already past it.
///
/// This plays ONE short chime ahead of the window (Haru 10-02: "a small cue that gives at least 0.1 s"),
/// placed so a press made in reaction to it lands in the middle of perfect. It only informs — the press,
/// the window and the grade are the game's own. Work +0x10 = state (1 running, 2 pressed; the timer
/// freezes at the press).
/// </summary>
internal sealed unsafe class BugCatchingCue
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] CaptureTask = Encoding.ASCII.GetBytes("insectCapture");

    private const float TimerPerSecond = 30f;
    // 0.2 s before the game's own sound. Haru's first press came 8 units (~0.27 s) after a t=140 chime = t 148,
    // one unit inside the late edge — so the chime moved 2 earlier: a reaction like his now lands ≈146, mid-window.
    private const float CueHeardAt = 138f;
    private const float OutputDelayMs = 50f; // the short-delay mixer output (60 ms buffering)
    private const float CueFireAt = CueHeardAt - OutputDelayMs * TimerPerSecond / 1000f;

    private readonly object _audioKey = new();
    private bool _audioOn;
    private nint _pressedNode;   // the task lingers after the press — don't re-run that round
    private static bool _tipSpoken;

    internal BugCatchingCue()
    {
        new Thread(Poll) { IsBackground = true, Name = "BugCatchingCue" }.Start();
        Log("[BugCatch] ready (press cue)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(50);
            try { Round(); } catch { Thread.Sleep(500); }
            finally { Audio(false); }
        }
    }

    /// <summary>Runs one insectCapture round from the moment the task appears until it ends.</summary>
    private void Round()
    {
        if (!GameHasFocus()) return;
        nint node = TaskNode(CaptureTask), work;
        if (node == 0) { _pressedNode = 0; return; }
        if (node == _pressedNode || !TryReadRaw(node + 0x48, &work, 8) || work < 0x10000) return;

        Audio(true);   // open the short-delay output now, so the cue doesn't wait on it
        if (!_tipSpoken)
        {
            _tipSpoken = true;
            Speech.Say("Press Action on the chime.", false);
        }

        bool fired = false;
        float last = 0f;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 60_000)
        {
            int state;
            float t;
            if (!TaskAlive(node) || !TryReadRaw(work + 0x10, &state, 4) || !TryReadRaw(work + 0x1C, &t, 4)) return;
            if (state == 2)
            {
                _pressedNode = node;
#if DEBUG
                Log($"[BugCatch] pressed at t={t:F1} ({t / TimerPerSecond:F2} s; perfect = 143..149, cue heard ~{CueHeardAt})");
#endif
                return;
            }
            if (state == 1 && !float.IsNaN(t))
            {
                if (t < last - 50f) fired = false;   // nobody pressed: the round looped back to 0
                last = t;
                if (!fired && t >= CueFireAt && t < CueHeardAt + 6f)
                {
                    fired = true;
                    ToneCue.PlayCue("bug_chime.wav", 0.6f * SoundSettings.BugChimeVol, (1760f, 70));
                }
            }
            Thread.Sleep(1);
        }
    }

    private void Audio(bool on)
    {
        if (on == _audioOn) return;
        _audioOn = on;
        DungeonAudio.SetLowLatency(_audioKey, on);
        DungeonAudio.SetWant(_audioKey, on);
    }

    private static bool TaskAlive(nint node)
    {
        byte* buf = stackalloc byte[16];
        if (!TryReadRaw(node, buf, CaptureTask.Length + 1)) return false;
        for (int j = 0; j < CaptureTask.Length; j++) if (buf[j] != CaptureTask[j]) return false;
        byte term = buf[CaptureTask.Length];
        return term < 0x20 || term >= 0x7F;
    }

    // ── named-task registry (heads above; node name @+0x00, work @+0x48, next @+0x50) ─────────
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
                // "insectCapture" never matches "insectCaptureSE".
                byte term = buf[name.Length];
                if (match && (term < 0x20 || term >= 0x7F)) return node;
                if (!TryReadRaw(node + 0x50, &node, 8)) break;
            }
        }
        return 0;
    }
}
