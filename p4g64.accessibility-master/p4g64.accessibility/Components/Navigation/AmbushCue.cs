using p4g64.accessibility.Components.Navigation.AutoWalk;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Sound help for MANUAL Shadow ambushes (2026-10-02; players: "a hard time ambushing"; Haru: focus on the manual
/// hunt, the Backspace hunt must never become a sure thing). Everything here is what a sighted player SEES — which
/// way the Shadow faces and how close you are; the swing and the advantage are the game's.
///
/// The game's numbers (live, the per-type table *(0x140EC0EC0) rows of 0x40, row = type + byte(slot+0x212)*6):
/// a normal Shadow sees 120° wide (+0x0C) up to 1800u (+0x10); strong/gold use the same cone. A swing gives the
/// advantage only when it LANDS on its back: labelled test battles gave the advantage at back -0.88..-0.99 and a
/// NORMAL start at -0.45/-0.57, and hits landed 145..406u away.
///  - SNEAK TICKS: while you are in the rear 120° of the nearest Shadow in sight (back <= -0.5, i.e. outside its
///    view) within 1500u, a soft tick from its direction, faster as you close in.
///  - TURNING: if it turns from that to side-on or worse while you are near, a low two-note warning (once).
///  - SWING NOW: a bright double chime when you are in reach NOW (140..430u), squarely behind it (back <= -0.7)
///    and facing it within 30°. (The first version chimed a reaction early while running — Haru then slowed down
///    and pressed ~2 s later, after the Shadow had turned.)
/// The radar's old "Strike." (behind it within 500u, whatever your facing or reach) is silent now.
/// </summary>
internal sealed class AmbushCue
{
    private const int PollMs = 20;
    private const float ReachNear = 140f, ReachFar = 430f;
    private const float FaceDeg = 30f;
    private const float BackMax = -0.7f;        // squarely behind (within ~45° of its rear axis)
    private const float SneakRange = 1500f;
    private const float SneakBack = -0.5f;      // outside its 120° view cone (cos 60° = 0.5) on the rear side
    private const float TurnBack = -0.2f;       // it has turned to side-on or toward you
    private const int RearmMs = 400;            // not-ready this long before the next chime
    // Running in, the chime comes a little earlier (+150u at ~1000u/s): Haru's 10-02 run-in pressed 0.3 s after a
    // 420u chime at 174u — the bodies touched before the swing landed (normal start). Walking in is unchanged.
    private const float RunLeadMs = 150f;

    private static readonly nint HeldButtons = unchecked((nint)0x15E3FD674L);   // Action = 0x2000
    private const uint ActionBit = 0x2000;

    // Who struck first — the game's own battle-start value (2026-10-02): the advantage effect FUN_1400DE4A0 plays
    // SE 0x4EE8 (blue) when *(short*)(*(0x140EC08F0) + 0x1E) == 1 and SE 0x4EE9 (red) otherwise; the pointer is
    // the live battle context (null in the field). Found by labelled battle-start snapshots: the sound channel
    // 0x15E3FE600+0x2C held 0x4EE8 / 0x4EE9 / 0x283C for player / enemy / normal starts.
    private static readonly nint BattleCtxPtr = unchecked((nint)0x140EC08F0L);
    private bool _wasInBattle;
    private long _battleSinceMs;
    private bool _advSpoken;

    private bool _ready;
    private long _notReadySince;
    private long _nextTickMs;
    private int _sneakSlot = -1;          // list index of the Shadow being sneaked on (-1 = none)
    private float _sneakX, _sneakZ;
    private float _sneakD = -1f;          // last poll's distance to it (closing speed)
    private long _sneakDMs;
    private bool _sneaking;               // that Shadow currently has its back to you
    private float _runLead;               // extra reach for the chime while closing fast (u)
#if DEBUG
    private bool _actionWas;
    private long _chimeMs;
    private (long ms, float d, float face, float back, bool ready)? _press;
#endif

    internal AmbushCue()
    {
        new Thread(Loop) { IsBackground = true, Name = "AmbushCue" }.Start();
        Log("[Ambush] ready (sneak ticks, turning warning, swing-now chime, battle-start advantage)");
    }

    private void Loop()
    {
        while (true)
        {
            Thread.Sleep(PollMs);
            try { Tick(); } catch { Thread.Sleep(500); }
        }
    }

    private static bool InDungeonField()
    {
        int major = FieldTracker.CurrentMajor;
        return major >= 20 && major < 220 && !FieldTracker.InAreaTransition;
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
#if DEBUG
        WatchOutcome(now);
#endif
        AnnounceAdvantage(now);
        if (!GameHasFocus() || !InDungeonField() || AutoWalker.IsActive || SettingsMenu.IsOpen
            || CommandMenus.PlayerMenu.IsMenuOpen || now - Dialogue.LastDialogTick < 300)
        { SetReady(false, now); _sneakSlot = -1; _sneaking = false; return; }

        float px = FieldTracker.LivePlayerX, pz = FieldTracker.LivePlayerZ;
        var (fx, fz) = FieldTracker.PlayerForwardViaGaze();
        if (float.IsNaN(px) || float.IsNaN(pz) || (fx == 0 && fz == 0)) { SetReady(false, now); return; }
        var (camFx, camFz) = FieldTracker.CameraForward3D();

        bool anyReady = false;
        int near = -1;
        float nearD = float.MaxValue, nearBack = 1f, nearFace = 180f, nearPan = 0f;
        var shadows = DungeonNav.ShadowsWithType();
        for (int i = 0; i < shadows.Count; i++)
        {
            var s = shadows[i];
            if (s.fx == 0 && s.fz == 0) continue;
            float dx = s.x - px, dz = s.z - pz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > SneakRange || d < 1f || !ClearPath(px, pz, s.x, s.z)) continue;
            float face = MathF.Abs(RouteSpeech.SignedAngleDeg(fx, fz, dx, dz));
            float back = (s.fx * -dx + s.fz * -dz) / d;   // its facing . direction to you: +1 = looking at you
            float far = ReachFar + (i == _sneakSlot ? _runLead : 0f);
            if (d >= ReachNear && d <= far && back <= BackMax && face <= FaceDeg) anyReady = true;
            if (d < nearD)
            {
                near = i; nearD = d; nearBack = back; nearFace = face;
                // Camera-relative pan, the radar's own math.
                nearPan = camFx != 0 || camFz != 0
                    ? Math.Clamp(-(dx / d * camFz - dz / d * camFx), -1f, 1f) : 0f;
            }
        }

        // Track the nearest Shadow in sight; a list index only counts as the same Shadow while it moved < 400u.
        if (near < 0) { _sneakSlot = -1; _sneaking = false; _runLead = 0f; }
        else
        {
            var t = shadows[near];
            bool same = near == _sneakSlot
                        && (t.x - _sneakX) * (t.x - _sneakX) + (t.z - _sneakZ) * (t.z - _sneakZ) < 400f * 400f;
            if (!same) { _sneakSlot = near; _sneaking = false; _sneakD = -1f; }
            _sneakX = t.x; _sneakZ = t.z;
            // closing speed (u/ms) → how much earlier the chime may come next poll
            float v = _sneakD > 0 && now > _sneakDMs ? (_sneakD - nearD) / (now - _sneakDMs) : 0f;
            _runLead = Math.Clamp(v, 0f, 1f) * RunLeadMs;
            _sneakD = nearD; _sneakDMs = now;

            if (nearBack <= SneakBack)
            {
#if DEBUG
                if (!_sneaking) Log($"[AmbushDiag] sneak start d={nearD:F0} back={nearBack:F2}");
#endif
                _sneaking = true;
            }
            else if (_sneaking && nearBack > TurnBack)
            {
                _sneaking = false;
                ToneCue.PlayCuePanned("ambush_turn.wav", 0.45f * SoundSettings.AmbushTurnVol, nearPan, (520f, 70), (0f, 30), (390f, 110));
#if DEBUG
                Log($"[AmbushDiag] TURNING d={nearD:F0} back={nearBack:F2}");
#endif
            }

            // Sneak ticks: only while it has its back to you and you are not already in reach.
            if (_sneaking && !anyReady && now >= _nextTickMs)
            {
                float k = Math.Clamp((nearD - 300f) / 1200f, 0f, 1f);
                _nextTickMs = now + (long)(140f + 660f * k);
                ToneCue.PlayCuePanned("ambush_tick.wav", 0.22f * SoundSettings.AmbushTickVol, nearPan, (1100f, 14));
            }
        }

        if (anyReady && !_ready && now - _notReadySince >= RearmMs)
        {
            ToneCue.PlayCuePanned("ambush_strike.wav", 0.5f * SoundSettings.AmbushStrikeVol, nearPan, (2093f, 45), (0f, 15), (2637f, 70));
#if DEBUG
            _chimeMs = now;
            Log($"[AmbushDiag] chime d={nearD:F0} face={nearFace:F0} back={nearBack:F2}");
#endif
        }
        SetReady(anyReady, now);
#if DEBUG
        WatchPress(now, nearD, nearFace, nearBack, anyReady);
#endif
    }

    private unsafe void AnnounceAdvantage(long now)
    {
        bool inBattle = FieldTracker.InBattle;
        if (inBattle && !_wasInBattle) { _battleSinceMs = now; _advSpoken = false; }
        _wasInBattle = inBattle;
        if (!inBattle || _advSpoken || now - _battleSinceMs > 4000) return;
        nint ctx = 0;
        short adv = 0;
        if (!TryReadRaw(BattleCtxPtr, &ctx, 8) || ctx == 0 || !TryReadRaw(ctx + 0x1E, &adv, 2)) return;
        _advSpoken = true;
        Log($"[Ambush] battle start: advantage value {adv}");
        if (adv == 1) Speech.Say("You struck first!", false);
        else if (adv == 2) Speech.Say("Ambushed!", false);
    }

    private void SetReady(bool ready, long now)
    {
        if (_ready && !ready) _notReadySince = now;
        _ready = ready;
    }

    // Same wall test as the radar: a minimap wall cell on the straight line = blocked.
    private static bool ClearPath(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1f) return true;
        float nx = dx / len, nz = dz / len;
        for (float t = 120f; t < len; t += 120f)
            if (MinimapTracker.WorldToCell(ax + nx * t, az + nz * t, out int r, out int c)
                && MinimapTracker.ReadCell(r, c, out var cell) && cell.Flag == 2)
                return false;
        return true;
    }

#if DEBUG
    // [AmbushDiag] (temporary, DEBUG only): every Action press in a dungeon field with the nearest Shadow's
    // geometry, then whether a battle started within 1.5 s (= the swing connected) and the field's encounter
    // words at that moment — the data to tune the band/lead and to find where the game keeps the advantage.
    private unsafe void WatchPress(long now, float d, float face, float back, bool ready)
    {
        uint held = 0;
        TryReadRaw(HeldButtons, &held, 4);
        bool action = (held & ActionBit) != 0;
        if (action && !_actionWas && d < SneakRange)
        {
            _press = (now, d, face, back, ready);
            Log($"[AmbushDiag] press d={d:F0} face={face:F0} back={back:F2} ready={ready} sinceChime={(_chimeMs == 0 ? -1 : now - _chimeMs)}ms");
        }
        _actionWas = action;
    }

    private unsafe void WatchOutcome(long now)
    {
        if (_press is not { } p) return;
        if (FieldTracker.InBattle)
        {
            Log($"[AmbushDiag] → BATTLE {now - p.ms}ms after the press (d={p.d:F0} back={p.back:F2} ready={p.ready})");
            _press = null;
        }
        else if (now - p.ms > 1500)
        {
            Log($"[AmbushDiag] → no battle (miss) d={p.d:F0} face={p.face:F0} back={p.back:F2} ready={p.ready}");
            _press = null;
        }
    }
#endif
}
