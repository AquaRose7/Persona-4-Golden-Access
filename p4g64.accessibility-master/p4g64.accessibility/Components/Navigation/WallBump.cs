using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Wall-hit audio cue (2026-07-02). Repeating thud while the player pushes into a wall. Robust
/// conditions so it only fires on a real head-on wall: movement INTENT (stick / D-pad / WASD / arrows),
/// pushing the SAME way you were just moving (reversals push opposite → ignored), you moved FRESHLY
/// just before (excludes standstill starts), and position isn't advancing. Off during dialogs/menus and
/// auto-walk. (Currently logging inputs for calibration.)
/// </summary>
internal sealed class WallBump
{
    private const int PollMs = 60;

    private const float MovedUnits = 15f;    // advanced less than this in a poll = not making progress
    private const int   StuckNeeded = 3;     // consecutive stuck polls (~180 ms) before it's a wall
    private const float IntentMag = 0.35f;   // stick magnitude that counts as "trying to move"
    private const long  FreshMoveMs = 500;   // must have moved within this long → excludes standstill starts
    private const long  RepeatMs = 550;      // thud cadence while held against the wall
    // Push-vs-last-move dot. Only REVERSALS (pushing ~opposite, dot ≤ -0.5) are excluded — that kills the
    // "turn around and it thuds" false-positive. A perpendicular turn INTO a wall (dot ≈ 0) is a real hit,
    // so anything above -0.5 fires. (Was 0.2, which wrongly rejected turning a corner into a wall.)
    private const float AlignDot = -0.5f;

    // Arrow-key virtual codes.
    private const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    private readonly Thread _thread;
    private volatile bool _stopped;

    private float _lastX = float.NaN, _lastZ;
    private float _moveDirX, _moveDirZ;   // world unit direction of the last significant move
    private long _lastMoveTick;
    private int _stuckTicks;
    private bool _hitting;                 // currently against a wall (repeat state)
    private float _hitDirX, _hitDirZ;      // intent direction captured at the hit
    private long _lastThudMs;

    public WallBump()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "WallBump" };
        _thread.Start();
        Log("[WallBump] ready (wall-hit thud)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[WallBump] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) { Reset(); return; }
        // Back off during an area transition — reading live position/camera while the scene rebuilds
        // can hit freed memory and hard-crash (the transition-crash fix, 2026-07-02).
        if (FieldTracker.InAreaTransition) { Reset(); return; }
        if (!InDungeon() || AutoWalk.AutoWalker.IsActive || DialogOrMenuActive()
            || CommandMenus.PlayerMenu.IsMenuOpen) { Reset(); return; }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) { Reset(); return; }
        if (float.IsNaN(_lastX)) { _lastX = px; _lastZ = pz; return; }

        float dx = px - _lastX, dz = pz - _lastZ;
        float moved = MathF.Sqrt(dx * dx + dz * dz);
        long now = Environment.TickCount64;
        bool haveIntent = TryIntentWorld(out float ix, out float iz);

        if (moved >= MovedUnits)   // moving freely → remember direction, clear any wall state
        {
            _moveDirX = dx / moved; _moveDirZ = dz / moved; _lastMoveTick = now;
            _lastX = px; _lastZ = pz;
            _stuckTicks = 0; _hitting = false;
            return;
        }
        _lastX = px; _lastZ = pz;

        if (!haveIntent) { _stuckTicks = 0; _hitting = false; return; }

        _stuckTicks++;
        if (_hitting)
        {
            float dot = ix * _hitDirX + iz * _hitDirZ;
            if (dot <= AlignDot) { _hitting = false; _stuckTicks = 0; return; }
            if (now - _lastThudMs >= RepeatMs) { _lastThudMs = now; Thud(); }
            return;
        }

        if (_stuckTicks >= StuckNeeded && now - _lastMoveTick < FreshMoveMs
            && ix * _moveDirX + iz * _moveDirZ > AlignDot)
        {
            _hitting = true; _hitDirX = ix; _hitDirZ = iz; _lastThudMs = now;
            Thud();
        }
    }

    private void Reset() { _lastX = float.NaN; _stuckTicks = 0; _hitting = false; }

    /// <summary>Movement intent as a WORLD unit vector. Input = left stick / D-pad (via ControllerInput)
    /// OR keyboard WASD / arrow keys. Camera-relative (stick up = camera forward), so it's rotated into
    /// world via CameraForward3D. False if no intent or no readable camera.</summary>
    private static bool TryIntentWorld(out float ix, out float iz)
    {
        ix = 0; iz = 0;
        float sx = ControllerInput.LeftStickX, sy = ControllerInput.LeftStickY;
        if (MathF.Sqrt(sx * sx + sy * sy) < IntentMag)
        {
            sx = (IsKeyDown(0x44) || IsKeyDown(VK_RIGHT) ? 1 : 0) - (IsKeyDown(0x41) || IsKeyDown(VK_LEFT) ? 1 : 0);  // D/→ − A/←
            sy = (IsKeyDown(0x57) || IsKeyDown(VK_UP) ? 1 : 0) - (IsKeyDown(0x53) || IsKeyDown(VK_DOWN) ? 1 : 0);      // W/↑ − S/↓
            if (sx == 0f && sy == 0f) return false;
        }
        var (fx, fz) = FieldTracker.CameraForward3D();
        if (fx == 0f && fz == 0f) return false;                          // no camera → skip
        float rx = -fz, rz = fx;                                         // world "right" of camera forward
        ix = sy * fx + sx * rx; iz = sy * fz + sx * rz;                  // stick up = forward, right = right
        float m = MathF.Sqrt(ix * ix + iz * iz);
        if (m < 1e-3f) return false;
        ix /= m; iz /= m;
        return true;
    }

    // A real dialog is on screen (has text / a choice) within the last ~200ms. The camp-pointer check
    // was dropped — that global holds garbage on dungeon floors so it can't gate reliably; the camp menu
    // navigates with the movement keys anyway, so it can't be mistaken for pushing into a wall.
    private static bool DialogOrMenuActive() => Environment.TickCount64 - Dialogue.LastDialogTick < 200;

    private static bool InDungeon()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220;   // dungeon floors 20-69; battles (220-299) excluded
    }

    private static void Thud() { WinBeep(440, 55); WinBeep(300, 85); }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private static void WinBeep(uint freq, uint ms) { try { Beep(freq, ms); } catch { } }
    [DllImport("kernel32.dll")] private static extern bool Beep(uint dwFreq, uint dwDuration);
}
