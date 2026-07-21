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
                HandleRoomTip();
                HandleDungeonTip();
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
        if (ArmCooldown()) return;

        // One trigger shows BOTH welcome bubbles back-to-back (field.flow 7900).
        _moveStreak = 0; _lastArmTick = Environment.TickCount64;
        ShowPopup(7900, WelcomeSeenFlag,
            "You're free to explore the world around you. You can set a sound beacon on nearby things, and even auto-walk to them. "
          + "Whenever you like, press F to open the travel menu. It has more options to help you on your journey.");
        SetFlagBit(WelcomeSeenFlag, true);   // remember in the save
    }

    // ── First time in YOUR ROOM (7_3): Quick Interact + the settings menu ────
    // Fires on the FIRST STEPS in the place (like the welcome), not after an idle
    // wait — the movement itself proves the entry scene is over and the player
    // has control (user request 2026-07-19).
    private readonly MoveDetect _roomMove = new(static () =>
    { var (x, _, z, ok) = FieldTracker.WorldPlayerPos(); return (x, z, ok); });

    private void HandleRoomTip()
    {
        if (CheckFlagBit(RoomSeenFlag)) return;
        if (FieldTracker.CurrentMajor != 7 || FieldTracker.CurrentMinor != 3) { _roomMove.Reset(); return; }
        if (!QuietForTips()) { _roomMove.Reset(); return; }
        if (!_roomMove.SteppedEnough()) return;
        if (ArmCooldown()) return;
        _roomMove.Reset(); _lastArmTick = Environment.TickCount64;   // a retry needs FRESH steps
        ShowPopup(7901, RoomSeenFlag,
            "In certain places, you can press Shift and F, or Left Trigger plus X on a controller, "
          + "to open the Quick Interact menu. From it, choose a spot and you'll be taken straight to it. "
          + "You can also press F1, or hold both triggers and press Start, to open the Accessibility Settings and its Help.");
        SetFlagBit(RoomSeenFlag, true);
    }

    // ── First DUNGEON floor: the sound tools + stairs travel + Help ──────────
    private readonly MoveDetect _dungeonMove = new(static () =>
    { float x = FieldTracker.LivePlayerX, z = FieldTracker.LivePlayerZ; return (x, z, true); });

    private void HandleDungeonTip()
    {
        if (CheckFlagBit(DungeonSeenFlag)) return;
        int major = FieldTracker.CurrentMajor;
        // KNOWN dungeon majors only: the story TV-world visits (major 100/68 on a new
        // game) sit in the 20-219 band too, but their field menu is locked — the armed
        // bubble deferred to the next overworld F press (live 2026-07-19).
        if (!FieldTracker.IsKnownDungeonMajor(major)) { _dungeonMove.Reset(); return; }
        if (FieldTracker.InAreaTransition) { _dungeonMove.Reset(); return; }
        if (!QuietForTips()) { _dungeonMove.Reset(); return; }
        if (!_dungeonMove.SteppedEnough()) return;
        if (ArmCooldown()) return;
        _dungeonMove.Reset(); _lastArmTick = Environment.TickCount64;
        ShowPopup(7902, DungeonSeenFlag,
            "In dungeons, sound is your guide: beacons for the stairs and chests, the shadow radar, and the wall sounds. "
          + "Explore freely until you find the stairs, then open the menu with F, X on a controller, and travel to the next floor. "
          + "Learn more any time in the dungeon tips, under Help in the Accessibility Settings.");
        SetFlagBit(DungeonSeenFlag, true);
    }

    // A silent-retry disarm (ShowPopup's guard) must not re-arm instantly while the
    // player is mid-interaction — require a fresh window between attempts.
    private long _lastArmTick;
    private bool ArmCooldown() => Environment.TickCount64 - _lastArmTick < 10000;

    /// <summary>No dialogue drawing, no menus open — safe to pop a bubble (its synth-F
    /// would otherwise land in a menu or talk over story).</summary>
    private static bool QuietForTips()
        => Environment.TickCount64 - Dialogue.LastDialogTick > 2500
           && !CommandMenus.PlayerMenu.IsMenuOpen
           && !SettingsMenu.IsOpen;

    /// <summary>The welcome's sustained-movement detector, reusable: ~450 ms of real
    /// walking (3 poll ticks) = "the player took their first steps here". Reads the
    /// LivePlayerX/Z dispatcher so it works in 2.5D rooms AND 3D dungeons.</summary>
    /// <summary>⚠ Position source matters: LivePlayerX/Z is FROZEN inside house
    /// rooms (diag-proven 2026-07-19: constant (14.9, 105.0) while walking in 7_3)
    /// — interiors need WorldPlayerPos (the party-unit chain the welcome uses).
    /// Dungeons keep LivePlayerX/Z (proven there; WorldPlayerPos is unverified in 3D).</summary>
    private sealed class MoveDetect
    {
        private readonly Func<(float x, float z, bool ok)> _pos;
        private bool _have;
        private float _lx, _lz;
        private int _streak;

        public MoveDetect(Func<(float x, float z, bool ok)> pos) { _pos = pos; }

        public void Reset() { _have = false; _streak = 0; }

        public bool SteppedEnough()
        {
            var (x, z, ok) = _pos();
            if (!ok || float.IsNaN(x) || float.IsNaN(z)) { Reset(); return false; }
            if (_have)
                _streak = Math.Abs(x - _lx) + Math.Abs(z - _lz) > 2f ? _streak + 1 : 0;
            _lx = x; _lz = z; _have = true;
            return _streak >= 3;
        }
    }

    // ── Native pop-up via our field.flow softhook ────────────────────────────
    // Set the FlowScript BIT, then synthesise F: the overworld field-menu hook
    // (FEmulator/BF/field.flow) sees the bit and runs OPEN_MSG_WIN/MSG/CLOSE_MSG_WIN
    // → a real message window (auto-read by Dialogue, dismissed with a button).
    // Same proven mechanism as dungeon teleport. Falls back to plain speech if the
    // flow/bitmap isn't ready (e.g. not in the overworld).
    private void ShowPopup(int bitId, int seenFlag, string fallback)
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

                // AMBUSH GUARD (2026-07-19, revised 07-20): if the moment was wrong
                // (story segment, the player already in an interaction — e.g. going
                // to bed), the synth-F can't run the flow and the ARMED BIT would
                // linger, deferring the bubbles to the next F press ANYWHERE. If the
                // flow hasn't consumed the bit shortly, disarm it AND un-mark the tip
                // as seen — SILENT retry at the next good moment. (The first version
                // spoke the fallback here; that ghost speech landed over the
                // next-day screen after a quick bedtime — user report.)
                Thread.Sleep(2500);
                if (CheckFlagBit(bitId))
                {
                    SetFlagBit(bitId, false);
                    SetFlagBit(seenFlag, false);
                    Log($"[Tutorial] popup bit {bitId} not consumed — disarmed, will retry later");
                }
            }
            catch (Exception ex) { Log($"[Tutorial] popup: {ex.Message}"); }
        }) { IsBackground = true, Name = "TutorialPopup" }.Start();
    }

    private static readonly unsafe byte** _flagBitmapPtr = (byte**)0x1451FF7A0L;
    private const int WelcomeSeenFlag = 7950;   // saved event-flag: welcome shown this playthrough
    private const int RoomSeenFlag = 7951;      // saved event-flag: first-room tip shown
    private const int DungeonSeenFlag = 7952;   // saved event-flag: first-dungeon tip shown
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
