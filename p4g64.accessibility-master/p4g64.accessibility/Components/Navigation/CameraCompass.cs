using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// The CAMERA COMPASS (v2.2.1, Haru: "in 3d maps like school and dungeons when you move the camera the screen reader say
/// where are you pointing, south east north etc like fire emblem"; the model is FE3H Access's Compass). While YOU turn the
/// camera, each time its ground direction enters a new eighth of the compass that eighth's word is spoken ("Northeast"),
/// interrupting the last one. An eighth is left only <see cref="MarginDeg"/> past its edge, so a camera resting on an edge
/// never flips. A turn starts silently from the eighth the camera faced when the input began, so the camera's OWN turns are
/// never spoken: the school camera swinging round corners behind you, the auto-walk, and Camera north (B), which says
/// "Camera north." itself. Works on any field map whose camera turns (the school, the dungeons); the town's fixed cameras
/// never move, so nothing is said there. F1 → Readers → "Camera directions".
///
/// The camera inputs come from the game's field camera controller FUN_1402EC0B0 (decompiled 2026-10-08): pad word
/// 0x15E3FD8E0 bits 0x400/0x800 (held: turn left/right — PS-style L1/R1; the keyboard's turn keys land there too), the
/// right stick X byte 0x15E3FD9C4 (rest 128; the game turns past ±48) and 0x15E3FD8E4 bits 0x100/0x200/0x4000 (one-frame
/// presses: the eased snap turn and the recentre behind the player). They are sampled every frame on the game thread
/// (<see cref="OnInputFrame"/>, from ControllerInput's input hook) so a one-frame press is never missed.
/// North = world +Z, east = −X (memory world_x_axis_is_west).
/// </summary>
internal sealed class CameraCompass
{
    private const int PollMs = 40;
    private const long TailMs = 400;        // the camera eases on a moment after a held turn is let go
    private const long SnapTailMs = 800;    // a snap / recentre press: the game eases that turn over several frames
    private const float MarginDeg = 5f;
    private static readonly string[] Words = { "North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest" };

    private static readonly nint PadHeld = unchecked((nint)0x15E3FD8E0L);
    private static readonly nint PadPress = unchecked((nint)0x15E3FD8E4L);
    private static readonly nint RightStickX = unchecked((nint)0x15E3FD9C4L);
    private static bool _inputChecked, _inputOk;

    // Written on the game thread, read by the poll thread.
    private static long _turnUntilMs;
    private static int _turnSeq;
    private static float _startBearing;

    /// <summary>The F1 setting (Readers → "Camera directions").</summary>
    internal static volatile bool Enabled = true;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private int _seenSeq;
    private int _sector = -1;

    public CameraCompass()
    {
        Enabled = ModSettings.GetBool("camera_compass", Defaults.CameraCompass);
        _thread = new Thread(Poll) { IsBackground = true, Name = "CameraCompass" };
        _thread.Start();
        Log("[CameraCompass] ready (turning the camera speaks the direction it faces)");
    }

    public void Stop() => _stopped = true;

    /// <summary>Degrees clockwise from north, 0..360, of a ground direction (north = +Z, east = −X).</summary>
    private static float Bearing(float fx, float fz) => (MathF.Atan2(-fx, fz) * 180f / MathF.PI + 360f) % 360f;

    private static int Sector(float bearing) => (int)MathF.Round(bearing / 45f) % 8;

    /// <summary>Every frame from ControllerInput's hook, right after the game's input function (game thread): notes a
    /// camera turn input and, when it starts a new turn, the direction the camera faced before it moved.</summary>
    internal static unsafe void OnInputFrame()
    {
        if (!Enabled) return;
        if (!_inputChecked)
        {
            _inputChecked = true;
            _inputOk = ProbeReadable(PadHeld, 8) && ProbeReadable(RightStickX, 1);   // constant BSS, checked once
        }
        if (!_inputOk) return;

        ushort held = *(ushort*)PadHeld, press = *(ushort*)PadPress;
        int rx = *(byte*)RightStickX - 128;
        long tail = (press & 0x4300) != 0 ? SnapTailMs
                  : (held & 0x0C00) != 0 || rx > 48 || rx < -48 ? TailMs : 0;
        if (tail == 0) return;

        long now = Environment.TickCount64;
        if (now > _turnUntilMs)
        {
            // A new turn: the camera hasn't moved yet this frame (the camera controller runs later in the frame).
            var (fx, fz) = FieldTracker.CameraForward3D();
            if (fx == 0f && fz == 0f) return;
            _startBearing = Bearing(fx, fz);
            Interlocked.Increment(ref _turnSeq);
        }
        long until = now + tail;
        if (until > _turnUntilMs) _turnUntilMs = until;
    }

    private void Poll()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[CameraCompass] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        int seq = Volatile.Read(ref _turnSeq);
        if (seq != _seenSeq)
        {
            _seenSeq = seq;
            _sector = Sector(_startBearing);   // the turn starts from here, silently
        }
        if (_sector < 0 || Environment.TickCount64 > Interlocked.Read(ref _turnUntilMs)) return;
        if (!Enabled || !GameHasFocus() || SettingsMenu.IsOpen) return;
        if (!FieldTracker.InFieldLive || FieldTracker.InBattle || FieldTracker.InAreaTransition) return;
        int major = FieldTracker.CurrentMajor;
        if (major <= 0 || major >= 220) return;

        var (fx, fz) = FieldTracker.CameraForward3D();
        if (fx == 0f && fz == 0f) return;
        float bearing = Bearing(fx, fz);
        float off = MathF.Abs((bearing - _sector * 45f + 540f) % 360f - 180f);
        if (off <= 22.5f + MarginDeg) return;
        _sector = Sector(bearing);
        Speech.Say(Words[_sector], true);
    }
}
