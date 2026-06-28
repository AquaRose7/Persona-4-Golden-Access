using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// In-game mod onboarding for new blind players.
///
///  • <b>Contextual onboarding</b>: short messages fired ONCE at the right moment
///    (drip-fed, not a data dump), tied to progress. First: the welcome lines the
///    first time the player walks in the overworld, shown as real in-game help bubbles.
///
/// The old <b>F1</b> spoken-guide (a README-by-ear topic cycler) was REMOVED 2026-06-28 —
/// the in-game help bubbles cover onboarding now, so F1 is free again.
///
/// "Seen" tips are persisted to a small file so they never repeat — a brand-new
/// player gets them once.
/// </summary>
internal class Tutorial
{
    private const int PollMs = 150;
    private const int Msg2DelayTicks = 7000 / PollMs;   // ~7s gap before the 2nd welcome line

    private readonly HashSet<string> _seen = new();
    private readonly string _stateFile;

    // First-walk movement detection.
    private bool _havePos;
    private float _lx, _lz;
    private int _moveStreak, _tick, _msg2DueTick = -1;

    internal Tutorial()
    {
        _stateFile = ResolveStateFile();
        Load();
        new Thread(Poll) { IsBackground = true, Name = "Tutorial" }.Start();
        Log($"[Tutorial] ready ({_seen.Count} tips already seen)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(PollMs);
            _tick++;
            try
            {
                if (!GameHasFocus()) continue;
                HandleFirstWalk();
            }
            catch (Exception ex) { Log($"[Tutorial] {ex.Message}"); }
        }
    }

    // ── First-walk welcome (your two drip-fed lines) ──────────────────────
    private void HandleFirstWalk()
    {
        // Per-SAVE persistence: a game event-flag (saved with the file, cleared on
        // a new game) marks the welcome as shown — not a global install file.
        if (CheckFlagBit(WelcomeSeenFlag)) return;

        var (x, _, z, ok) = FieldTracker.WorldPlayerPos();
        if (!ok) { _havePos = false; _moveStreak = 0; return; }

        if (_havePos)
        {
            float d = Math.Abs(x - _lx) + Math.Abs(z - _lz);
            _moveStreak = d > 2f ? _moveStreak + 1 : 0;     // sustained real movement
        }
        _lx = x; _lz = z; _havePos = true;

        if (_moveStreak < 3) return;                         // ~450ms of walking → "in the world"

        // One trigger shows BOTH welcome bubbles back-to-back (field.flow 7900).
        ShowPopup(7900,
            "You're free to explore the world around you. You can set a sound beacon on nearby things, and even auto-walk to them. "
          + "Whenever you like, press F to open the travel menu. It has more options to help you on your journey.");
        SetFlagBit(WelcomeSeenFlag, true);   // remember in the save
    }

    // ── Native pop-up via our field.flow softhook ────────────────────────────
    // Set the FlowScript BIT, then synthesise F: the overworld field-menu hook
    // (FEmulator/BF/field.flow) sees the bit and runs OPEN_MSG_WIN/MSG/CLOSE_MSG_WIN
    // → a real message window (auto-read by Dialogue, dismissed with a button).
    // Same proven mechanism as dungeon teleport. Falls back to plain speech if the
    // flow/bitmap isn't ready (e.g. not in the overworld).
    private void ShowPopup(int bitId, string fallback)
    {
        new Thread(() =>
        {
            try
            {
                // The HELP bubble is read by the dialogue reader; only speak it
                // ourselves if the flow/bitmap isn't ready (so it's never lost).
                if (!SetFlagBit(bitId, true)) { Speech.Say(fallback, true); return; }
                Thread.Sleep(120);
                keybd_event(0, SC_F, KEYEVENTF_SCANCODE, UIntPtr.Zero);
                Thread.Sleep(40);
                keybd_event(0, SC_F, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex) { Log($"[Tutorial] popup: {ex.Message}"); }
        }) { IsBackground = true, Name = "TutorialPopup" }.Start();
    }

    private static readonly unsafe byte** _flagBitmapPtr = (byte**)0x1451FF7A0L;
    private const int WelcomeSeenFlag = 7950;   // saved event-flag: welcome shown this playthrough
    private const byte SC_F = 0x21;
    private const uint KEYEVENTF_SCANCODE = 0x0008, KEYEVENTF_KEYUP = 0x0002;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static unsafe bool SetFlagBit(int bitId, bool value)
    {
        if (!IsReadable((nint)_flagBitmapPtr, 8)) return false;
        byte* bitmap = *_flagBitmapPtr;
        if (bitmap == null) return false;
        nint dwordAddr = (nint)(bitmap + (bitId >> 5) * 4);
        if (!IsReadable(dwordAddr, 4)) return false;
        uint mask = 1u << (bitId & 31);
        uint* word = (uint*)dwordAddr;
        if (value) *word |= mask; else *word &= ~mask;
        return true;
    }

    private static unsafe bool CheckFlagBit(int bitId)
    {
        if (!IsReadable((nint)_flagBitmapPtr, 8)) return false;
        byte* bitmap = *_flagBitmapPtr;
        if (bitmap == null) return false;
        nint dwordAddr = (nint)(bitmap + (bitId >> 5) * 4);
        if (!IsReadable(dwordAddr, 4)) return false;
        return (*(uint*)dwordAddr & (1u << (bitId & 31))) != 0;
    }

    [DllImport("kernel32.dll")]
    private static extern unsafe nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static unsafe bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        return (*(uint*)(buf + 36) & (0x01 | 0x100)) == 0;
    }

    // ── persistence ───────────────────────────────────────────────────────
    private void Mark(string id) { if (_seen.Add(id)) Save(); }

    private void Load()
    {
        try
        {
            if (System.IO.File.Exists(_stateFile))
                foreach (var line in System.IO.File.ReadAllLines(_stateFile))
                    if (line.Trim().Length > 0) _seen.Add(line.Trim());
        }
        catch { /* fresh start if unreadable */ }
    }

    private void Save()
    {
        try { System.IO.File.WriteAllLines(_stateFile, _seen); }
        catch (Exception ex) { Log($"[Tutorial] save failed: {ex.Message}"); }
    }

    private static string ResolveStateFile()
    {
        string dir = !string.IsNullOrEmpty(ModDir)
            ? ModDir
            : System.IO.Path.Combine(Environment.CurrentDirectory, "Persona 4 golden", "database");
        return System.IO.Path.Combine(dir, "tutorial_seen.txt");
    }
}
