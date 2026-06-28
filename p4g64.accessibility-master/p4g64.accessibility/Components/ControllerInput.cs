using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Controller support for the mod's hotkeys (bug-fix session 2026-06-19, priority #2).
/// Button map authored by the user (database/newControlor.txt). Trigger names are Xbox-style
/// LT/RT (= PlayStation L2/R2).
///
/// The mod actions sit behind the <b>shoulder triggers (LT / RT)</b> as a modifier. We READ the
/// pad via XInput and <b>synthesize the matching keyboard key</b> via <c>keybd_event</c> so every
/// existing <c>GetAsyncKeyState</c> handler fires unchanged — one source of truth per action.
///
/// ## Map (LT/RT = left/right trigger; "both" = LT AND RT held). Source: newControlor.txt
///   BOTH + A        → enemy/shadow radar   (.)
///   BOTH + X        → chest beacon         (,)
///   BOTH + B        → exit/stairs beacon   (/)
///   BOTH + d-pad ↑  → repeat last spoken line       (Shift+P)
///   BOTH + d-pad ←/→ → speech history back / forward (Shift+[ / Shift+])
///   BOTH + d-pad ↓  → toggle dialogue auto-reader    (Shift+M)
///   BOTH + Y        → FREE (reserved for future actions)
///   RT + A          → party HP/SP          (O)
///   RT + X          → money (shop/velvet)  (G)
///   RT + B          → enemy status         (U)
///   RT + Y          → time + date          (M)
///   RT + d-pad      → grid-cursor move I/K/J/L
///   RT + L3         → grid-cursor open/toggle    (H)
///   RT + R3         → cursor walk/look mode (N)   — dungeon walking vs cursor
///   LT + d-pad ←/→  → category prev/next   (- / =)
///   LT + d-pad ↑/↓  → entry prev/next      ([ / ])
///   LT + A          → brief: name + distance (\)
///   LT + B          → overworld nav beacon (P)
///   LT + L3 or R3   → auto-walk            (Backspace)
///   LT + X          → FREE (was auto-walk; moved off X because the game reads X as Open-Menu)
///
/// ## Stopping the GAME from also reacting (the hard part — solved at P4G's own input layer)
/// P4G assembles its button presses into an internal bitfield in <c>FUN_140505D50</c> (found via
/// the snapshot method — see memory/controller_support.md). That layer is DOWNSTREAM of
/// XInput/DirectInput/Steam, so it's the authoritative place to suppress: we hook FUN_140505D50,
/// let it run, then <b>while a trigger-modifier is held</b> we ZERO the game's held/pressed button
/// bitfields (<c>0x15E3FD674</c> held, <c>0x15E3FD678</c> pressed, + the per-context copies) so
/// the game ignores every pad button. The triggers themselves are NOT in this bitfield (the game
/// never reacts to them), which is why they make a clean modifier and why we still read them via
/// XInput. (Earlier attempts to mask XInput/DInput below Steam failed — Steam's overlay hook sits
/// above ours; this layer can't be overridden.)
///
/// Our synthesized keys also reach the game's DInput KEYBOARD read, so we additionally strip
/// exactly our synth scan codes from that buffer (MaskKeyboard) — real typing is untouched.
///
/// Focus-gated (bug #1): nothing fires / no suppression while P4G isn't the foreground window.
/// </summary>
internal class ControllerInput
{
    private const int PollMs = 33;

    // XInput trigger threshold (SDK default 30). Hysteresis stops flutter near the edge.
    private const byte TrigOn  = 45;
    private const byte TrigOff = 20;

    // XInput digital button masks.
    private const ushort DPAD_UP = 0x0001, DPAD_DOWN = 0x0002, DPAD_LEFT = 0x0004, DPAD_RIGHT = 0x0008;
    private const ushort L3 = 0x0040, R3 = 0x0080;
    private const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    // Virtual-key codes the existing handlers poll for.
    private const int VK_BACK = 0x08, VK_G = 0x47, VK_H = 0x48, VK_M = 0x4D, VK_N = 0x4E;
    private const int VK_I = 0x49, VK_J = 0x4A, VK_K = 0x4B, VK_L = 0x4C;
    private const int VK_O = 0x4F, VK_P = 0x50, VK_U = 0x55;             // party HP/SP, overworld beacon, enemy status
    private const int VK_OEM_MINUS = 0xBD, VK_OEM_PLUS = 0xBB;          // - / =
    private const int VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD, VK_OEM_5 = 0xDC; // [ / ] / \
    private const int VK_OEM_PERIOD = 0xBE, VK_OEM_2 = 0xBF, VK_OEM_COMMA = 0xBC; // . / / / ,

    // Every VK this poller can synthesize — the universe we diff against each tick.
    private static readonly int[] AllVks =
    {
        VK_OEM_PERIOD, VK_OEM_COMMA, VK_OEM_2, VK_P,
        VK_OEM_MINUS, VK_OEM_PLUS, VK_OEM_4, VK_OEM_6,
        VK_I, VK_J, VK_K, VK_L,
        VK_H, VK_G, VK_M, VK_N, VK_BACK, VK_O, VK_U, VK_OEM_5,
    };

    private readonly HashSet<int> _synthDown = new();
    private bool _rtHeld, _ltHeld;             // hysteretic trigger state (poll thread)
    private bool _bUpWas, _bDownWas, _bLeftWas, _bRightWas;  // LT+RT d-pad edge state (speech history)
    private bool _bR3Was;                                    // LT+RT + R3 edge state (subtitle toggle)
    private bool _bL3Was;                                    // LT+RT + L3 edge state (description toggle)
    private bool _connectedLogged;

    private readonly Thread _thread;
    private volatile bool _stopped;

    public ControllerInput(IReloadedHooks hooks)
    {
        TrySetupInputSuppression(hooks);     // hook P4G's input fn → clear button bitfield
        TrySetupKeyboardSuppression(hooks);  // strip our synth keys from the game's DInput keyboard

        _thread = new Thread(PollLoop) { IsBackground = true, Name = "ControllerInput" };
        _thread.Start();
        Log("[ControllerInput] ready — hold LT/RT for the mod layer (see newControlor.txt map).");
    }

    public void Stop()
    {
        _stopped = true;
        ReleaseAll();
    }

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[ControllerInput] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        var desired = new HashSet<int>();

        // Bug #1 focus gate: only act when the game is focused AND a pad is present.
        if (GameHasFocus() && TryGetState(out XInputState st))
            ComputeDesired(st, desired);
        else
            { _rtHeld = _ltHeld = false; _modHeldShared = false; }

        // Diff desired vs currently-synthesized; press/release the delta.
        foreach (int vk in AllVks)
        {
            bool want = desired.Contains(vk);
            bool have = _synthDown.Contains(vk);
            if (want && !have) { KeyDown(vk); _synthDown.Add(vk); }
            else if (!want && have) { KeyUp(vk); _synthDown.Remove(vk); }
        }
    }

    private void ComputeDesired(in XInputState st, HashSet<int> desired)
    {
        byte lt = st.Gamepad.LeftTrigger, rt = st.Gamepad.RightTrigger;
        _rtHeld = _rtHeld ? rt >= TrigOff : rt >= TrigOn;
        _ltHeld = _ltHeld ? lt >= TrigOff : lt >= TrigOn;
        _modHeldShared = _rtHeld || _ltHeld;   // published for the per-frame OnInputFn (no per-frame XInput)
        bool both = _rtHeld && _ltHeld;
        if (!both) _bUpWas = _bDownWas = _bLeftWas = _bRightWas = _bR3Was = _bL3Was = false;  // reset edges off-combo

        ushort b = st.Gamepad.Buttons;

        if (both)
        {
            // BOTH triggers = dungeon audio beacons (face buttons) + speech history (d-pad).
            // both + Y is left FREE on purpose.
            if ((b & A) != 0) desired.Add(VK_OEM_PERIOD);  // enemy / shadow radar  (.)
            if ((b & X) != 0) desired.Add(VK_OEM_COMMA);   // chest beacon          (,)
            if ((b & B) != 0) desired.Add(VK_OEM_2);       // exit / stairs beacon  (/)

            // Speech history — direct calls on the d-pad EDGE (not synth); the bitfield-suppress
            // hook already hides the d-pad from the game while triggers are held.
            EdgeFire(b, DPAD_UP,    ref _bUpWas,    static () => Speech.RepeatLast());    // repeat last line
            EdgeFire(b, DPAD_LEFT,  ref _bLeftWas,  static () => Speech.Step(-1));        // history back
            EdgeFire(b, DPAD_RIGHT, ref _bRightWas, static () => Speech.Step(+1));        // history forward
            EdgeFire(b, DPAD_DOWN,  ref _bDownWas,  static () => Dialogue.ToggleReader()); // dialogue auto-read on/off
            EdgeFire(b, R3,         ref _bR3Was,    static () => SubtitleReader.ToggleReader()); // movie subtitle on/off
            EdgeFire(b, L3,         ref _bL3Was,    static () => MovieDescription.Toggle());     // cutscene description on/off
        }
        else if (_rtHeld)
        {
            // RT alone = status/info on the face buttons; d-pad = grid-cursor moves;
            // stick-click = grid-cursor toggle.
            if ((b & A) != 0) desired.Add(VK_O);           // party HP/SP           (O)
            if ((b & X) != 0) desired.Add(VK_G);           // money (shop/velvet)   (G)
            if ((b & B) != 0) desired.Add(VK_U);           // enemy status          (U)
            if ((b & Y) != 0) desired.Add(VK_M);           // time + date           (M)

            if ((b & DPAD_UP)    != 0) desired.Add(VK_I);  // cursor ahead
            if ((b & DPAD_DOWN)  != 0) desired.Add(VK_K);  // cursor behind
            if ((b & DPAD_LEFT)  != 0) desired.Add(VK_J);  // cursor left
            if ((b & DPAD_RIGHT) != 0) desired.Add(VK_L);  // cursor right

            if ((b & L3) != 0) desired.Add(VK_H); // grid-cursor open/toggle      (H)
            if ((b & R3) != 0) desired.Add(VK_N); // cursor walk/look mode toggle (N)
        }
        else if (_ltHeld)
        {
            // LT alone = navigation on the d-pad + actions on the face buttons; stick-click =
            // auto-walk (moved off X, which the game reads as Open-Menu and leaked once the walk
            // started — a stick-click is harmless in-game). LT+X is now FREE.
            if ((b & DPAD_LEFT)  != 0) desired.Add(VK_OEM_MINUS); // category prev (-)
            if ((b & DPAD_RIGHT) != 0) desired.Add(VK_OEM_PLUS);  // category next (=)
            if ((b & DPAD_UP)    != 0) desired.Add(VK_OEM_4);     // entry prev    ([)
            if ((b & DPAD_DOWN)  != 0) desired.Add(VK_OEM_6);     // entry next    (])

            if ((b & A) != 0) desired.Add(VK_OEM_5);       // brief: name + distance (\)
            if ((b & B) != 0) desired.Add(VK_P);           // overworld nav beacon   (P)

            if ((b & L3) != 0 || (b & R3) != 0) desired.Add(VK_BACK); // auto-walk (Backspace)
        }
    }

    private static void EdgeFire(ushort buttons, ushort mask, ref bool was, Action act)
    {
        bool down = (buttons & mask) != 0;
        if (down && !was) act();
        was = down;
    }

    private void ReleaseAll()
    {
        foreach (int vk in _synthDown) KeyUp(vk);
        _synthDown.Clear();
        _rtHeld = _ltHeld = false;
    }

    private int _slot = -1;          // last connected XInput slot (-1 = none known)
    private int _rescanWait;         // poll cycles to wait before re-enumerating empty slots

    /// <summary>Read the connected controller. CRITICAL PERF: <c>XInputGetState</c> on an EMPTY
    /// slot does synchronous device enumeration (~ms) that can stutter the whole game, so we poll
    /// ONLY the known slot each cycle and re-scan all four only every ~2s when none is connected.</summary>
    private bool TryGetState(out XInputState st)
    {
        if (_slot >= 0)
        {
            if (XInputGetState((uint)_slot, out st) == 0) return true;
            _slot = -1;   // it disconnected — fall through to a throttled re-scan
        }
        if (_rescanWait > 0) { _rescanWait--; st = default; return false; }
        _rescanWait = 60;   // ~2s at 33ms between full enumerations (empty-slot polls are costly)
        for (uint ci = 0; ci < 4; ci++)
        {
            if (XInputGetState(ci, out st) == 0)
            {
                _slot = (int)ci;
                if (!_connectedLogged) { Log($"[ControllerInput] controller {ci} connected."); _connectedLogged = true; }
                return true;
            }
        }
        st = default;
        return false;
    }

    // ── GAME INPUT-BITFIELD SUPPRESSION (the authoritative fix) ─────────────────
    // FUN_140505D50 = P4G's input function; it assembles the held/pressed button bitfields.
    // ASLR is off so the VA is constant. We hook it, let it run, then zero the bitfields while a
    // trigger-modifier is held. (snapshot RE: memory/controller_support.md)
    private const long InputFnAddr = 0x140505D50;

    // Every "current input" copy that tracked real button presses in the live snapshot — held,
    // pressed, and the per-context duplicates. Zeroing them all guarantees no consumer sees a press.
    private static readonly long[] InputBitfields =
    {
        // primary block — RAW (held/pressed/repeat) AND assembled (held/pressed/prev).
        // FUN_140505D50 derives pressed-edges INSIDE itself, so the raw pressed (0x664) and
        // repeat (0x668) must be cleared too — menus/confirm read those, not just 0x674/0x678.
        // NB: do NOT clear 0x15E3FD670 — its LOW word is the analog STICK (center 0x8080);
        // zeroing it = full deflection = the player walks. Its HIGH dword IS 0x674 (buttons),
        // which we clear separately, leaving the stick bytes untouched.
        0x15E3FD660, 0x15E3FD664, 0x15E3FD668, 0x15E3FD66C,
        0x15E3FD674, 0x15E3FD678, 0x15E3FD67C, 0x15E3FD680,
        // per-context copies (held/pressed/repeat per pad context) found in the live snapshot.
        0x15E3FD74C, 0x15E3FD754, 0x15E3FD75C,
        0x15E3FD8F4, 0x15E3FD8FC,
        0x15E3FD9CC, 0x15E3FD9D4, 0x15E3FD9DC,
        // MENU-context button copies (2026-06-20): in menus the d-pad lands HERE, not in the
        // 0x674 field block — held/pressed at block+0x10/+0x18 of the byte-stick contexts
        // (0x740 and 0x9C0 blocks; sticks at +0x04/+0x08 are left untouched). Missing these made
        // the controller double-navigate menus while a trigger was held. (peek_region live diff.)
        0x15E3FD750, 0x15E3FD758,
        0x15E3FD9D0, 0x15E3FD9D8,
    };

    private delegate void InputFnDelegate();
    private IHook<InputFnDelegate>? _inputHook;
    private long[]? _validBits;                    // InputBitfields validated ONCE (static BSS) — no per-frame VirtualQuery
    private static volatile bool _modHeldShared;   // trigger-held, published by the poll thread; read per-frame here

    private void TrySetupInputSuppression(IReloadedHooks hooks)
    {
        try
        {
            // The 0x15E3FDxxx block is constant committed BSS (ASLR off) — validate readability
            // once here so OnInputFn (per frame) can write straight through with no VirtualQuery.
            var valid = new List<long>();
            foreach (long a in InputBitfields) if (IsReadable((nint)a, 4)) valid.Add(a);
            _validBits = valid.ToArray();

            _inputHook = hooks.CreateHook<InputFnDelegate>(OnInputFn, InputFnAddr).Activate();
            Log($"[ControllerInput] input-suppression hook installed (FUN_140505D50 → clears {_validBits.Length} pad-button copies while a trigger is held).");
        }
        catch (Exception ex) { Log($"[ControllerInput] input hook failed: {ex.GetType().Name}: {ex.Message} — buttons will double-fire."); }
    }

    // ── analog-stick DRIVE (precise auto-walk) ─────────────────────────────────
    // FUN_140505D50 rewrites the stick from the live pad every frame, so we override it HERE (post-
    // original): 0x15E3FD670 byte0 = X, byte1 = Y, 8-bit, centre 0x80 (<0x30 = up/left, >0xD0 = down/
    // right — from the decompile). Continuous 256-level analog → precise movement, the game's own
    // physics/collision/Check all run normally. -1 = off (hands the stick back to the player).
    internal static volatile int DriveStick = -1;   // -1 = off; else (X & 0xFF) | ((Y & 0xFF) << 8)
    internal static void DriveStickXY(int x, int y)
        => DriveStick = (Math.Clamp(x, 0, 255) & 0xFF) | ((Math.Clamp(y, 0, 255) & 0xFF) << 8);
    internal static void ReleaseDriveStick() => DriveStick = -1;

    private unsafe void OnInputFn()
    {
        _inputHook!.OriginalFunction();   // let the game assemble its bitfields first
        int ds = DriveStick;              // auto-walker analog override (runs regardless of trigger)
        if (ds >= 0)
        {
            *(byte*)(nint)0x15E3FD670L = (byte)ds;
            *(byte*)(nint)0x15E3FD671L = (byte)(ds >> 8);
        }
        if (!_modHeldShared) return;      // cheap volatile read; the poll thread does the XInput work
        // While an auto-walk is running, DON'T clear — the walker drives the player with
        // synthesized W/A/S/D, which feeds this same unified bitfield. Clearing it (because the
        // user is still holding the trigger that started the walk) would wipe that movement and
        // the walk would stall ("moved 0.0u"). No buttons need suppressing mid-walk anyway.
        if (Navigation.AutoWalk.AutoWalker.IsActive || Navigation.OverworldNav.IsWalking) return;
        var bits = _validBits;
        if (bits == null) return;
        for (int i = 0; i < bits.Length; i++) *(uint*)(nint)bits[i] = 0;
    }

    // ── synth-key suppression from the game's DInput keyboard ───────────────────
    // P4G reads the keyboard via dinput8 GetDeviceState (cb=256). It also sees the keys WE inject.
    // Strip exactly our synthesized scan codes from that buffer so the game ignores them.
    private static readonly Guid IID_IDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    private static readonly Guid IID_IDirectInput8A = new("BF798030-483A-4DA2-AA99-5D64ED369700");
    private static readonly Guid GUID_SysKeyboard   = new("6F1D2B61-D5A0-11CF-BFC7-444553540000");
    private const uint DIRECTINPUT_VERSION = 0x0800;

    private delegate int GetDeviceStateDelegate(nint self, uint cbData, nint lpvData);
    private IHook<GetDeviceStateDelegate>? _gds0, _gds1;

    private unsafe void TrySetupKeyboardSuppression(IReloadedHooks hooks)
    {
        try
        {
            nint dll = GetModuleHandle("dinput8.dll");
            if (dll == 0) dll = LoadLibrary("dinput8.dll");
            if (dll == 0) return;

            nint hinst = GetModuleHandle(null);
            var addrs = new HashSet<nint>();
            // A throwaway DInput keyboard device (each char-width variant) gives us the SHARED
            // GetDeviceState vtable slot — timing-independent, no need to race the game.
            foreach (Guid iid in new[] { IID_IDirectInput8W, IID_IDirectInput8A })
            {
                Guid riid = iid;
                if (DirectInput8Create(hinst, DIRECTINPUT_VERSION, in riid, out nint obj, 0) != 0 || obj == 0) continue;
                try
                {
                    nint objVtbl = *(nint*)obj;
                    var createDevice = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, nint, int>)(*(nint*)(objVtbl + 3 * 8));
                    var releaseObj   = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint*)(objVtbl + 2 * 8));
                    Guid kbd = GUID_SysKeyboard;
                    nint dev;
                    if (createDevice(obj, &kbd, &dev, 0) == 0 && dev != 0)
                    {
                        nint devVtbl = *(nint*)dev;
                        addrs.Add(*(nint*)(devVtbl + 9 * 8));   // vtable[9] = GetDeviceState
                        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint*)(devVtbl + 2 * 8)))(dev);
                    }
                    releaseObj(obj);
                }
                catch (Exception ex) { Log($"[ControllerInput] DInput probe error: {ex.Message}"); }
            }

            int i = 0;
            foreach (nint a in addrs)
            {
                if (i == 0)      _gds0 = hooks.CreateHook<GetDeviceStateDelegate>(OnGetDeviceState0, a).Activate();
                else if (i == 1) _gds1 = hooks.CreateHook<GetDeviceStateDelegate>(OnGetDeviceState1, a).Activate();
                i++;
            }
            Log($"[ControllerInput] keyboard-suppression hooks: {addrs.Count} (hides our synth keys from the game).");
        }
        catch (Exception ex) { Log($"[ControllerInput] keyboard hook setup failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private int OnGetDeviceState0(nint self, uint cb, nint data) { int hr = _gds0!.OriginalFunction(self, cb, data); MaskKeyboard(hr, cb, data); return hr; }
    private int OnGetDeviceState1(nint self, uint cb, nint data) { int hr = _gds1!.OriginalFunction(self, cb, data); MaskKeyboard(hr, cb, data); return hr; }

    private static unsafe void MaskKeyboard(int hr, uint cb, nint data)
    {
        if (hr != 0 || data == 0 || cb != 256) return;   // 256 = DInput keyboard state (DIK-indexed, 0x80=down)
        if (_synthScanCount <= 0) return;
        if (!IsReadable(data, 256)) return;
        for (int sc = 1; sc < 256; sc++)
            if (_synthScan[sc] != 0 && *(byte*)(data + sc) != 0) *(byte*)(data + sc) = 0;
    }

    // ── key synthesis (VK form: updates GetAsyncKeyState, which every handler polls) ──
    // Injected scan codes are tracked in _synthScan so MaskKeyboard can hide them from the game.
    private static readonly byte[] _synthScan = new byte[256];
    private static int _synthScanCount;

    private static void KeyDown(int vk)
    {
        byte sc = (byte)MapVirtualKey((uint)vk, 0);
        keybd_event((byte)vk, sc, 0, UIntPtr.Zero);
        if (sc != 0 && _synthScan[sc] == 0) { _synthScan[sc] = 1; System.Threading.Interlocked.Increment(ref _synthScanCount); }
    }
    private static void KeyUp(int vk)
    {
        byte sc = (byte)MapVirtualKey((uint)vk, 0);
        keybd_event((byte)vk, sc, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (sc != 0 && _synthScan[sc] != 0) { _synthScan[sc] = 0; System.Threading.Interlocked.Decrement(ref _synthScanCount); }
    }

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("dinput8.dll")]
    private static extern int DirectInput8Create(nint hinst, uint dwVersion, in Guid riidltf, out nint ppvOut, nint punkOuter);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll")]
    private static extern unsafe nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static unsafe bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000 || a > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48, OFF_STATE = 32, OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state = *(uint*)(buf + OFF_STATE), protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD) != 0) return false;
        return true;
    }

    // ── XInput (read-only — for the modifier + action buttons) ─────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte   LeftTrigger;
        public byte   RightTrigger;
        public short  ThumbLX;
        public short  ThumbLY;
        public short  ThumbRX;
        public short  ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint          PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);
}
