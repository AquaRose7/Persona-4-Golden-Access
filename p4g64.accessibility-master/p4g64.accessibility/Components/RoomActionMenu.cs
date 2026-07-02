using System.Collections.Generic;
using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// "What's here?" room-action menu (design: docs/superpowers/specs/2026-06-29-room-action-menu-design.md).
///
/// Opens our object menu in the player's room WITHOUT touching the normal F menu. Trigger =
/// Shift+F: in the room we set FlowScript BIT 7905 + synthesise a clean F (after the physical
/// keys release; ControllerInput suppresses the game's own F while Shift is held), and field.flow
/// shows the MyRoom list. Each object pick arms a per-object BIT (7910..) and CALL_FIELD_SAFE-
/// teleports to the room entry nearest that object.
///
/// After the reload, this component sees the object BIT, waits for the player to load, then holds
/// ONE direction for a short fixed time — just enough to step into the game's interact prompt.
/// It then announces "press to interact"; the player presses to interact normally (we never
/// trigger the event → no save risk). The teleport already lands close to each object, so this is
/// a single nudge, not a path — no recording, no coordinates.
/// </summary>
internal sealed unsafe class RoomActionMenu : IDisposable
{
    private const int RoomMenuBit = 7905;   // field.flow: BIT_CHK(7905) → show MyRoom menu
    private const int VK_SHIFT = 0x10, VK_F = 0x46;
    private const byte SC_F = 0x21;
    private const uint KEYEVENTF_SCANCODE = 0x0008, KEYEVENTF_KEYUP = 0x0002;

    private static readonly byte** _flagBitmapPtr = (byte**)0x1451FF7A0L;
    private static readonly Dictionary<char, byte> _scan = new() { ['W'] = 0x11, ['A'] = 0x1E, ['S'] = 0x1F, ['D'] = 0x20 };

    // Per-object: arming BIT, spoken label, and a sequence of nudge steps (dir, hold-ms) from the
    // teleport spot into the interact prompt. Empty steps = no nudge (teleport lands on it).
    // Keyed by room "major_minor"; bits are unique across rooms. Menu order here matches the
    // [sel] order in rooms.msg and the pick cases in field.flow.
    private static readonly Dictionary<string, (int bit, string label, (char dir, int ms)[] steps)[]> _rooms = new()
    {
        ["7_3"] = new (int, string, (char, int)[])[]   // Your room
        {
            (7910, "Futon",      new[] { ('S', 600) }),
            (7911, "Desk",       System.Array.Empty<(char, int)>()),
            (7912, "TV",         new[] { ('D', 600) }),
            (7913, "Small desk", new[] { ('S', 600), ('A', 1200) }),
            (7914, "Sofa",       new[] { ('A', 600) }),
            (7915, "Calendar",   new[] { ('W', 300) }),
        },
        ["7_2"] = new (int, string, (char, int)[])[]   // Living room
        {
            (7920, "Save point", System.Array.Empty<(char, int)>()),
            (7921, "Fridge",     new[] { ('D', 600), ('W', 300) }),
            (7922, "Farm",       new[] { ('S', 1500), ('D', 600) }),   // yard from entry 0: S 1.5s, D 0.6s (user-timed)
        },
        ["8_1"] = new (int, string, (char, int)[])[]   // Shopping District North (auto-walk handoff)
        {
            (7930, "Aiya Chinese Diner", System.Array.Empty<(char, int)>()),
            (7931, "Bulletin board",     System.Array.Empty<(char, int)>()),
        },
    };

    // Big OUTDOOR areas: a blind fixed nudge can't cover the hundreds of units from the entry to
    // the object, so these objects hand off to the overworld auto-walk (self-calibrating, arrival
    // = the real check prompt) after the teleport. Keyed by object bit → catalog proc name.
    private static readonly Dictionary<int, string> _walkProc = new()
    {
        // (Farm was tried here 2026-07-02 and REVERTED — in the cramped house the walker
        // wandered; rooms stay deterministic teleport+nudge. Handoff is for BIG outdoor areas.)
        [7930] = "tyuuka",            // Aiya Chinese Diner
        [7931] = "keijiban",          // Bulletin board
    };

    private volatile bool _busy;

    // Set by ControllerInput on LT+X — opens the Quick interact menu from the controller
    // (same as Shift+F). Polled + consumed below.
    internal static volatile bool ControllerOpenRequest;

    internal RoomActionMenu()
    {
        var t = new Thread(Poll) { IsBackground = true, Name = "RoomActionMenu" };
        t.Start();
        Log("[RoomAction] ready (Shift+F in your room)");
    }

    private void Poll()
    {
        bool comboWas = false;
        while (true)
        {
            Thread.Sleep(40);
            try
            {
                if (!GameHasFocus()) { comboWas = false; continue; }
                bool combo = Down(VK_SHIFT) && Down(VK_F);
                if (combo && !comboWas && !_busy && InRoom()) OpenMenu();
                comboWas = combo;

                // LT+X from the controller (ControllerInput sets the flag).
                if (ControllerOpenRequest) { ControllerOpenRequest = false; if (!_busy && InRoom()) OpenMenu(); }

                // DEV scout key is UNBOUND for shipping builds. To set up a NEW room's entries,
                // re-enable this (Ctrl+F9 cycles teleport to entry 0..7 of the current area):
                //   bool scout = Down(0x11) && Down(0x78);
                //   if (scout && !_scoutWas && !_busy && (FieldTracker.CurrentMajor == 7 || FieldTracker.CurrentMajor == 8)) ScoutNextEntry();
                //   _scoutWas = scout;

                CheckObjectBits();
            }
            catch (Exception ex) { Log($"[RoomAction] {ex.Message}"); Thread.Sleep(500); }
        }
    }

    private static bool InRoom() => _rooms.ContainsKey($"{FieldTracker.CurrentMajor}_{FieldTracker.CurrentMinor}");

    // ── open the menu (Shift+F → arm 7905 + synth clean F) ─────────────────────────
    // ── DEV scout: teleport to entry N of the room via the existing field.flow 6710
    //    calibration branch (6720..6723 = 4-bit entry index). Cycles 0..4. ──
    // (Re-add `private bool _scoutWas;` when re-enabling the Ctrl+F9 scout block in Poll.)
    private int _scoutEntry = -1;
    private void ScoutNextEntry()
    {
        _busy = true;
        new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 100 && (Down(0x11) || Down(0x78)); i++) Thread.Sleep(20);
                _scoutEntry = (_scoutEntry + 1) % 8;
                int e = _scoutEntry;
                // clear then encode the entry index into 6720..6723, arm 6710
                SetFlagBit(6720, false); SetFlagBit(6721, false); SetFlagBit(6722, false); SetFlagBit(6723, false);
                if ((e & 1) != 0) SetFlagBit(6720, true);
                if ((e & 2) != 0) SetFlagBit(6721, true);
                if ((e & 4) != 0) SetFlagBit(6722, true);
                if ((e & 8) != 0) SetFlagBit(6723, true);
                if (!SetFlagBit(6710, true)) { Log("[RoomAction] scout: bitmap not ready"); return; }
                Speech.Say($"Entry {e}.");
                Thread.Sleep(80);
                keybd_event(0, SC_F, KEYEVENTF_SCANCODE, UIntPtr.Zero);
                Thread.Sleep(40);
                keybd_event(0, SC_F, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
                Log($"[RoomAction] scout teleport to entry {e}");
            }
            catch (Exception ex) { Log($"[RoomAction] scout: {ex.Message}"); }
            finally { Thread.Sleep(400); _busy = false; }
        }) { IsBackground = true, Name = "RoomActionScout" }.Start();
    }

    private void OpenMenu()
    {
        _busy = true;
        new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 100 && (Down(VK_F) || Down(VK_SHIFT)); i++) Thread.Sleep(20);
                if (!SetFlagBit(RoomMenuBit, true)) { Log("[RoomAction] bitmap not ready"); return; }
                Thread.Sleep(80);
                keybd_event(0, SC_F, KEYEVENTF_SCANCODE, UIntPtr.Zero);
                Thread.Sleep(40);
                keybd_event(0, SC_F, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
                Log("[RoomAction] opened room menu");
            }
            catch (Exception ex) { Log($"[RoomAction] open: {ex.Message}"); }
            finally { Thread.Sleep(300); _busy = false; }
        }) { IsBackground = true, Name = "RoomActionOpen" }.Start();
    }

    // ── object BIT fired → wait for load → one directional nudge → announce ────────
    private (int bit, string label, (char dir, int ms)[] steps)? _pending;
    private int _wait, _guard;

    private void CheckObjectBits()
    {
        if (_pending != null)
        {
            if (--_wait > 0) return;
            var (_, _, _, ok) = FieldTracker.WorldPlayerPos();
            if ((!ok || !InRoom()) && _guard++ < 60) { _wait = 5; return; }   // still loading; keep waiting (~12s cap)
            var o = _pending.Value; _pending = null; _guard = 0;
            Nudge(o);
            return;
        }
        foreach (var objs in _rooms.Values)
            foreach (var o in objs)
            {
                if (!BitSet(o.bit)) continue;
                SetFlagBit(o.bit, false);
                _pending = o; _wait = 18; _guard = 0;   // ~700ms for the reload before the load-check kicks in
            }
    }

    private void Nudge((int bit, string label, (char dir, int ms)[] steps) o)
    {
        _busy = true;
        new Thread(() =>
        {
            try
            {
                // Outdoor-area object → hand off to the overworld auto-walk (it announces its
                // own arrival), instead of a blind fixed nudge. No "Walking to X" — the player
                // just picked the place from the menu.
                if (_walkProc.TryGetValue(o.bit, out string? proc))
                {
                    var nav = Navigation.OverworldNav.Instance;
                    if (nav == null) { Speech.Say("Navigation not available."); return; }
                    // Let OverworldNav's poll see the post-teleport field as active again,
                    // otherwise its state reset would kill the walk we just started.
                    for (int i = 0; i < 100 && !nav.ReadyForWalk; i++) Thread.Sleep(50);
                    Thread.Sleep(250);
                    if (!nav.WalkToProcTarget(proc, announce: false))
                    { Speech.Say($"{o.label}: can't start the walk here."); return; }
                    Log($"[RoomAction] {o.label}: auto-walk handoff (proc {proc})");
                    // RESIDUAL nudge: if the walk ends WITHOUT the check (wedged short of the
                    // spot — e.g. the Farm's yard planter), the object's steps push the last
                    // stretch. A walk that arrived on the check needs (and gets) nothing.
                    if (o.steps.Length > 0)
                    {
                        for (int i = 0; i < 1200 && Navigation.OverworldNav.IsWalking; i++) Thread.Sleep(50);
                        Thread.Sleep(100);
                        if (!FieldTracker.CheckPromptActive)
                        {
                            RunSteps(o.steps);
                            Thread.Sleep(60);
                            bool p2 = FieldTracker.CheckPromptActive;
                            Speech.Say(p2 ? $"{o.label}. Press to interact." : $"{o.label}. Press to interact if you're near it.");
                            Log($"[RoomAction] {o.label}: residual nudge {o.steps.Length} steps, prompt={p2}");
                        }
                    }
                    return;
                }

                RunSteps(o.steps);
                Thread.Sleep(60);
                bool prompt = FieldTracker.CheckPromptActive;
                Speech.Say(prompt ? $"{o.label}. Press to interact." : $"{o.label}. Press to interact if you're near it.");
                var (nx, _, nz, nok) = FieldTracker.WorldPlayerPos();
                Log($"[RoomAction] {o.label}: nudged {o.steps.Length} steps, prompt={prompt}, end=({(nok ? nx : float.NaN):F0},{(nok ? nz : float.NaN):F0})");
            }
            catch (Exception ex) { Log($"[RoomAction] nudge: {ex.Message}"); }
            finally { _busy = false; }
        }) { IsBackground = true, Name = "RoomActionNudge" }.Start();
    }

    private static void RunSteps((char dir, int ms)[] steps)
    {
        foreach (var (dir, ms) in steps)
        {
            if (ms <= 0 || !_scan.TryGetValue(dir, out byte sc)) continue;
            keybd_event(0, sc, KEYEVENTF_SCANCODE, UIntPtr.Zero);
            Thread.Sleep(ms);
            keybd_event(0, sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(40);
        }
    }

    // ── primitives ──────────────────────────────────────────────────────────────────
    private static bool BitSet(int bitId)
    {
        if (!IsReadable((nint)_flagBitmapPtr, 8)) return false;
        byte* bitmap = *_flagBitmapPtr;
        if (bitmap == null) return false;
        nint dwordAddr = (nint)(bitmap + (bitId >> 5) * 4);
        if (!IsReadable(dwordAddr, 4)) return false;
        return (*(uint*)dwordAddr & (1u << (bitId & 31))) != 0;
    }
    private static bool SetFlagBit(int bitId, bool value)
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

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);
    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint protect = *(uint*)(buf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }

    public void Dispose() { }
}
