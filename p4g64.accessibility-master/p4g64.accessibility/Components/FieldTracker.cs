using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DavyKager;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using p4g64.accessibility.Components.Navigation;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Polls game memory every 500ms and announces area/time/date changes via screen reader.
///
/// HOW TIME/DATE READING WORKS:
///   Three SigScans find game-module globals that the engine writes time/date into.
///   _segMiscPtr  — area (major/minor field ID). Confirmed working.
///   _segTimeDayPtr — write site (MOV WORD [RAX+8], BX). Day at +8. Primary time/day source.
///   _segTimeLivePtr — write site (writes +2 and +4). Layout matches zarroboogs: day at +0, period at +2.
///
/// FIELD OBJECT (_fieldObjPtr):
///   Global at 0x1411AB2C8 — pointer to the main "field object" struct for the current area.
///   Found via Ghidra: MOV [0x1411AB2C8], RAX  followed immediately by  MOV RAX, [segMiscPtr].
///   SigScan pattern: 48 89 05 ?? ?? ?? ?? 48 8B 05 84 A5 7D 00
///   The struct contains all field state. Dump it with F4 (near/far NPC) to find the interaction flag.
///
/// HOTKEYS:
///   M = re-read current area, date, time, weather aloud (was F1)
///   F2 = dump raw struct values to log (helps diagnose wrong offsets)
///   F3 = dump segMisc struct (32 ints)
///   F4 = dump field object struct (128 shorts) — use near/far NPC to find interaction flag
/// </summary>
internal unsafe class FieldTracker
{
    private static int**   _segMiscPtr;
    private static byte**  _segTimeDayPtr;   // write site: MOV WORD [RAX+8], BX  → day at +8
    private static short** _segTimeLivePtr;  // write site: MOV WORD [RAX+2] + MOV WORD [RAX+4]
    private static void**  _fieldObjPtr;     // global 0x1411AB2C8 → pointer to field object struct

    // Confirmed via snapshot comparison: 0 = no interactable nearby, 1 = CHECK!! prompt visible
    private static readonly int* _interactFlag = (int*)0x1411BC7F4L;
    private int _lastInteractFlag = -1;

    private int   _lastMajor      = -1;
    private int   _lastMinor      = -1;

    public static int CurrentMajor { get; private set; } = -1;
    public static int CurrentMinor { get; private set; } = -1;

    /// <summary>True when a major is a BATTLE context. Battle majors are PER-DUNGEON
    /// (Yukiko's Castle = 240, Steamy Bathhouse = 241, … one per dungeon) — NOT just 240.
    /// Every battle-only reader must gate on this, and every dungeon reader / the floor-name
    /// announcer must EXCLUDE it. Hardcoding 240 was the "battle reads as a new floor / battle
    /// functions fail outside Yukiko's Castle" bug (fixed 2026-06-27).</summary>
    public static bool IsBattleMajor(int major) => major >= 240 && major < 250;
    public static bool InBattle => IsBattleMajor(CurrentMajor);
    private short _lastTimePeriod = -1;
    private short _lastGameDay    = -1;
    private int   _npcCountdown   = 0;   // counts down poll ticks after area change; fires NPC count on 0

    // ── Public position accessors ─────────────────────────────────────────────
    // NavigationAssist reads these. Returns NaN when position is not yet captured.
    // 2.5D overworld uses the fieldObj chain (sub_obj+0x20); 3D dungeons use the AsmHook.
    public static float LivePlayerX => PlayerX;
    public static float LivePlayerZ => PlayerZ;
    public static unsafe float LivePlayerY
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            if (CurrentMajor < 20) return 0f;
            if (!IsReadable(sub + 0x10, 8)) return float.NaN;
            nint p = *(nint*)(sub + 0x10);
            // Read the 12-byte XYZ block; we want the middle float (Y).
            if (p == 0 || !IsReadable(p + 0x50, 12)) return float.NaN;
            float y = *(float*)(p + 0x54);
            // Sanity-bound — sub_obj+0x10 chain occasionally returns a pointer
            // to stale memory mid-area-transition where Y is a garbage float
            // like 1e15. If we accept that, the wall-Y column shifts far away
            // from real walls and we filter out everything.
            if (!float.IsFinite(y) || MathF.Abs(y) > 100_000f) return float.NaN;
            return y;
        }
    }

    // ── 2.5D player position via fieldObj chain (LIVE) ────────────────────────
    // Path: *_fieldObjPtr → fieldObj → *(fieldObj+0x48) → sub_obj
    //   sub_obj + 0xD0 = live player X (float)
    //   sub_obj + 0xD4 = live player Z (float)
    //   sub_obj + 0x20 = spawn X (STATIC — only set at area load; kept for reference)
    // Confirmed by cross-referencing Frida heap scan (find_position_write_log.txt):
    //   Frida found position floats at heap addr 0x12781308 / 0x1278130c with
    //   ranges (967→635 X, 534→232 Z). Our F9 sub_obj+0xD0/+0xD4 showed
    //   (872→634 X, 527→198 Z) — same ranges, same axis, confirming the offsets.
    private static unsafe (nint sub, bool ok) GetSubObjPtr()
    {
        // CRASH FIX 2026-06-12: this used to deref obj+0x48 and (in callers)
        // sub+0xD0 with only numeric range checks — NO page guards. During
        // area transitions (entering Daidara/shops, quitting) fieldObj/sub_obj
        // get freed while background poll threads (the dungeon beacons tick
        // every 50ms even in overworld) still read LivePlayerX → uncatchable
        // AVE → the chronic ExecutionEngineException crashes logged since
        // 2026-06-10 (crash-dump stack: ProximityBeacon.Tick →
        // get_PlayerX2DLive). Every link is now IsReadable-guarded.
        if (_fieldObjPtr == null) return (0, false);
        if (!IsReadable((nint)_fieldObjPtr, 8)) return (0, false);
        void* obj = *_fieldObjPtr;
        ulong addr = (ulong)obj;
        if (addr < 0x10000UL || addr > 0x0007FFFFFFFFFFFFUL) return (0, false);
        if (!IsReadable((nint)obj + 0x48, 8)) return (0, false);
        nint sub = *(nint*)((byte*)obj + 0x48);
        if ((ulong)sub < 0x10000UL || (ulong)sub > 0x0007FFFFFFFFFFFFUL) return (0, false);
        return (sub, true);
    }

    private static unsafe float PlayerX2DLive
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok || !IsReadable(sub + 0xD0, 8)) return float.NaN;
            float x = *(float*)((byte*)sub + 0xD0);
            // In 3D dungeons, sub+0xD0/+0xD4 are literally 0 — treat as invalid so
            // the dispatch falls through to the 3D-via-sub path below.
            return (float.IsNaN(x) || float.IsInfinity(x) || x == 0f) ? float.NaN : x;
        }
    }

    private static unsafe float PlayerZ2DLive
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok || !IsReadable(sub + 0xD4, 4)) return float.NaN;
            float z = *(float*)((byte*)sub + 0xD4);
            return (float.IsNaN(z) || float.IsInfinity(z) || z == 0f) ? float.NaN : z;
        }
    }

    // ── 3D dungeon player position via sub_obj+0x10 ──────────────────────
    // F4 dump in Yukiko's Castle floor 1 showed sub_obj+0x10 points to a
    // transform struct where X/Y/Z are at +0x50/+0x54/+0x58 (live per-frame).
    // Example values (16423, 192, 12461) — clearly dungeon coords.
    // Used when 2D fields read as 0 (non-overworld mode).
    private static unsafe float PlayerX3DViaSub
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            if (!IsReadable(sub + 0x10, 8)) return float.NaN;
            nint p = *(nint*)(sub + 0x10);
            if (p == 0 || !IsReadable(p + 0x60, 4)) return float.NaN;
            float x = *(float*)(p + 0x50);
            return (float.IsNaN(x) || float.IsInfinity(x) || x == 0f) ? float.NaN : x;
        }
    }

    private static unsafe float PlayerZ3DViaSub
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            if (!IsReadable(sub + 0x10, 8)) return float.NaN;
            nint p = *(nint*)(sub + 0x10);
            if (p == 0 || !IsReadable(p + 0x60, 4)) return float.NaN;
            float z = *(float*)(p + 0x58);
            return (float.IsNaN(z) || float.IsInfinity(z) || z == 0f) ? float.NaN : z;
        }
    }

    // ── 3D dungeon player FACING via *(sub_obj+0x10) transform basis ──────
    // From the movement-context layout in memory/ghidra_movement_system.md:
    //   *(sub_obj+0x10) + 0x40, 0x44, 0x48  =  forward.x, forward.y, forward.z
    //   *(sub_obj+0x10) + 0x20, 0x24, 0x28  =  right.x, right.y, right.z
    //   *(sub_obj+0x10) + 0x50/0x54/0x58    =  position.x, position.y, position.z (already used)
    // The transform is the engine's authoritative basis for the player —
    // unlike velocity-derived facing, it doesn't change between scans when
    // the player stops, and it matches what the engine uses when rotating
    // input keys (W/A/S/D) into world-direction movement. Returns NaN if
    // the chain is unreadable or the vector isn't roughly unit-length.

    public static unsafe float PlayerForward3DX
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            if (!IsReadable(sub + 0x10, 8)) return float.NaN;
            nint p = *(nint*)(sub + 0x10);
            if (p == 0 || !IsReadable(p + 0x40, 12)) return float.NaN;
            float f = *(float*)(p + 0x40);
            return float.IsFinite(f) ? f : float.NaN;
        }
    }

    public static unsafe float PlayerForward3DZ
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            if (!IsReadable(sub + 0x10, 8)) return float.NaN;
            nint p = *(nint*)(sub + 0x10);
            if (p == 0 || !IsReadable(p + 0x40, 12)) return float.NaN;
            float f = *(float*)(p + 0x48);
            return float.IsFinite(f) ? f : float.NaN;
        }
    }

    // ── Player facing in 3D dungeons via gaze-cursor interactable ─────────
    // The interactable master table at 0x146248560 has cat=1 id=0x0001
    // whose world position tracks "20 units in front of the player". That
    // makes its position a function of player position + facing direction:
    //   id1_pos = player_pos + 20 * player_forward
    // ⇒ player_forward = (id1_pos - player_pos).normalized
    // Discovered 2026-05-02 from a DoorRadar log analysis: id=0x0001 was
    // always at d≈20 from the player AND its world direction always
    // matched the player's velocity-derived facing — i.e. it's the engine's
    // own live "where the player is looking" cursor, not a real door.
    // Using this gives us the player's TRUE current facing in 3D dungeons
    // without lag and without depending on velocity history.

    private const long InteractableMasterTable = 0x146248560L;
    private const int  GazeCategory = 1;
    private const short GazeId = 0x0001;

    private static unsafe bool TryReadGazeInteractablePos(out float gx, out float gz)
    {
        gx = gz = 0;
        if (!IsReadable((nint)(InteractableMasterTable + GazeCategory * 8), 8)) return false;
        nint cur = *(nint*)(InteractableMasterTable + GazeCategory * 8);
        // Walk linked list (next at +0xfc0) looking for the id=0x0001 entry.
        int safety = 0;
        while ((ulong)cur > 0x10000UL && safety++ < 32)
        {
            if (!IsReadable(cur, 0x1000)) return false;
            short id = *(short*)(cur + 0x406);
            if (id == GazeId)
            {
                gx = *(float*)(cur + 0x360);
                gz = *(float*)(cur + 0x368);
                return float.IsFinite(gx) && float.IsFinite(gz);
            }
            if (!IsReadable(cur + 0xfc0, 8)) return false;
            cur = *(nint*)(cur + 0xfc0);
        }
        return false;
    }

    /// <summary>
    /// Player's true facing in 3D dungeons (or 2.5D overworld where the gaze
    /// cursor still exists), derived from the engine's gaze-cursor entry in
    /// the interactable master table. Returns (0, 0) if unavailable so
    /// callers can fall back to velocity-derived facing.
    /// </summary>
    public static unsafe (float fx, float fz) PlayerForwardViaGaze()
    {
        if (!TryReadGazeInteractablePos(out float gx, out float gz)) return (0, 0);
        float px = LivePlayerX, pz = LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return (0, 0);
        float dx = gx - px, dz = gz - pz;
        float mag = MathF.Sqrt(dx * dx + dz * dz);
        if (mag < 1f || !float.IsFinite(mag)) return (0, 0);
        return (dx / mag, dz / mag);
    }

    // ── LIVE 3D dungeon camera forward (FOUND 2026-06-11) ────────────────
    // From decompiling the FLD_CAMERA_ROTATE_BEHIND FlowScript handler
    // (memory/camera_struct_found.md): the field camera transform lives at
    //   *(*(0x1462487C0) + 8) → ctx
    //   ctx+0x20 right · ctx+0x30 up · ctx+0x40 forward · ctx+0x50 position
    // Returns the RAW camera basis forward XZ, normalized. SIGN WARNING: the
    // relationship between this vector and the direction W moves flipped
    // between two live measurements on 2026-06-11 (Numpad-6 validation read
    // it opposite the gaze after R; AutoWalker walking with the negated value
    // moved 180° from intended — velErr 172-177° in the log). Consumers must
    // NOT assume a fixed sign — AutoWalker learns it from its first
    // velocity sample and remembers. Returns (0,0) when unreadable.
    private const long CameraCtxGlobal = 0x1462487C0L;

    internal static unsafe (float fx, float fz) CameraForward3D()
    {
        if (!IsReadable((nint)CameraCtxGlobal, 8)) return (0, 0);
        nint root = *(nint*)CameraCtxGlobal;
        if ((ulong)root < 0x10000UL || !IsReadable(root + 8, 8)) return (0, 0);
        nint ctx = *(nint*)(root + 8);
        if ((ulong)ctx < 0x10000UL || !IsReadable(ctx + 0x40, 0x0C)) return (0, 0);
        float fx = *(float*)(ctx + 0x40);
        float fz = *(float*)(ctx + 0x48);
        float m = MathF.Sqrt(fx * fx + fz * fz);
        if (!float.IsFinite(m) || m < 1e-3f) return (0, 0);
        return (fx / m, fz / m);
    }

    /// <summary>
    /// True while the game's CHECK prompt is up (an interactable is in
    /// range). The game's own arrival condition — AutoWalker uses the rising
    /// edge near its target instead of a distance radius.
    /// </summary>
    internal static unsafe bool CheckPromptActive
        => IsReadable((nint)_interactFlag, 4) && *_interactFlag == 1;

    // 0x1411BC720 = "player is INSIDE an interactable's zone" (POSITION only — true even when
    // facing the wrong way). 0x7F4 above is the FACING-AWARE prompt (zone AND facing). Verified live
    // 2026-06-20: in-zone-facing-wrong → 0x720=1, 0x7F4=0. Lets us split arrival into "get in the
    // zone" (0x720) then "turn to face it" (0x7F4).
    private static readonly int* _inZoneFlag = (int*)0x1411BC720L;
    internal static unsafe bool InInteractZone
        => IsReadable((nint)_inZoneFlag, 4) && *_inZoneFlag == 1;

    // 0x140FFDE70 = the FOCUSED interactable's id/index (0 = none). Found via snapshot diff
    // 2026-06-20: save zone=0x15, NPC zone=0x2F, empty=0. Lets us confirm we're locked onto a
    // SPECIFIC interactable (compare the value at the target spot), not just "something is nearby".
    private static readonly int* _focusedInteractable = (int*)0x140FFDE70L;
    internal static unsafe int FocusedInteractable
        => IsReadable((nint)_focusedInteractable, 4) ? *_focusedInteractable : 0;

    // ── EXACT press-selection inputs (from decompiling FUN_1402E7FD0) ────────
    // The press fires on an object when distance(player, object) < 350 (DAT_14096585c) AND the object
    // is within the player's ±75° view cone (DAT_140965480·0.5). Player pos+forward come from the
    // player_unit DAT_140ec0fe8: world pos +0x360/+0x368, forward +0x330+0x20/+0x28 (verified live
    // 2026-06-20 — forward rotates with turns, same coord space as the catalog). Objects come from the
    // scene-actor list the press itself walks: *(0x140AA8098)→+0x08→+0x50, next +0x150, xform +0x168,
    // world XZ +0x360/+0x368.
    internal static unsafe bool TryPlayerPose(out float px, out float pz, out float fx, out float fz)
    {
        px = pz = fx = fz = 0;
        if (!IsReadable((nint)0x140ec0fe8L, 8)) return false;
        nint pu = *(nint*)0x140ec0fe8L;
        if (pu <= 0x10000 || !IsReadable(pu + 0x360, 12) || !IsReadable(pu + 0x330 + 0x20, 12)) return false;
        px = *(float*)(pu + 0x360); pz = *(float*)(pu + 0x368);
        float ffx = *(float*)(pu + 0x330 + 0x20), ffz = *(float*)(pu + 0x330 + 0x28);
        float m = MathF.Sqrt(ffx * ffx + ffz * ffz);
        if (m < 0.01f || !float.IsFinite(px) || !float.IsFinite(pz)) return false;
        fx = ffx / m; fz = ffz / m;
        return true;
    }

    // ── DIRECT position drive (write the player where we want, beating the physics) ──────────────
    // Verified live 2026-06-21: writing the world pos continuously HOLDS (physics doesn't revert it), so
    // we can place the player exactly — no stick, no drift. Writes the player_unit pos + the 3D-chain copy.
    // SAFE: every link IsReadable-guarded, and the caller clamps the step so we can never fling far.
    internal static unsafe void WritePlayerPos(float x, float z)
    {
        if (IsReadable((nint)0x140ec0fe8L, 8))
        {
            nint pu = *(nint*)0x140ec0fe8L;
            if (pu > 0x10000 && IsReadable(pu + 0x360, 12)) { *(float*)(pu + 0x360) = x; *(float*)(pu + 0x368) = z; }
        }
        if (IsReadable((nint)0x1411AB2C8L, 8))
        {
            nint fo = *(nint*)0x1411AB2C8L;
            if (fo > 0x10000 && IsReadable(fo + 0x48, 8))
            {
                nint sub = *(nint*)(fo + 0x48);
                if (sub > 0x10000 && IsReadable(sub + 0x10, 8))
                {
                    nint p3 = *(nint*)(sub + 0x10);
                    if (p3 > 0x10000 && IsReadable(p3 + 0x50, 12)) { *(float*)(p3 + 0x50) = x; *(float*)(p3 + 0x58) = z; }
                }
            }
        }
    }

    internal static unsafe void WritePlayerForward(float fx, float fz)
    {
        if (!IsReadable((nint)0x140ec0fe8L, 8)) return;
        nint pu = *(nint*)0x140ec0fe8L;
        if (pu > 0x10000 && IsReadable(pu + 0x330 + 0x20, 12)) { *(float*)(pu + 0x330 + 0x20) = fx; *(float*)(pu + 0x330 + 0x28) = fz; }
    }

    internal static unsafe bool TryNearestSceneObject(float tx, float tz, out float ox, out float oz)
    {
        ox = oz = 0; float best = float.MaxValue;
        if (!IsReadable((nint)0x140AA8098L, 8)) return false;
        nint root = *(nint*)0x140AA8098L;
        if (root <= 0x10000 || !IsReadable(root + 0x08, 8)) return false;
        nint scene = *(nint*)(root + 0x08);
        if (scene <= 0x10000 || !IsReadable(scene + 0x50, 8)) return false;
        nint cur = *(nint*)(scene + 0x50);
        int n = 0;
        while (cur > 0x10000 && n++ < 256)
        {
            if (!IsReadable(cur + 0x168, 8)) break;
            nint xf = *(nint*)(cur + 0x168);
            if (xf > 0x10000 && IsReadable(xf + 0x360, 12))
            {
                float x = *(float*)(xf + 0x360), z = *(float*)(xf + 0x368);
                if ((x != 0 || z != 0) && float.IsFinite(x) && float.IsFinite(z))
                {
                    float d = (x - tx) * (x - tx) + (z - tz) * (z - tz);
                    if (d < best) { best = d; ox = x; oz = z; }
                }
            }
            if (!IsReadable(cur + 0x150, 8)) break;
            cur = *(nint*)(cur + 0x150);
        }
        return best < float.MaxValue;
    }

    // ── What a PRESS would interact with right now (replicates the press selector FUN_1402E7FD0) ──────
    // Decompiled 2026-06-22: the press walks the scene-actor list (0x140AA8098→+0x08→+0x50, next +0x150),
    // and selects the actor that is ACTIVE (+0x28 & 2), an INTERACTABLE (xform[+0x168] + 0xf88 & 1), within
    // 350u, AND inside the player's ±75° view cone (player_unit DAT_140ec0fe8 forward +0x330 vs the actor's
    // world XZ at xform +0x360/+0x368). Returns the world XZ of the nearest such actor = exactly what a press
    // would hit. This is the reliable "which interactable is the game focused on" signal — the 0x140FFDE70
    // global that looked like it (snapshot) is actually a GPU instance buffer (decompiled FUN_1405979B0).
    internal static unsafe bool TryFacedInteractable(out float ox, out float oz)
    {
        ox = oz = 0;
        if (!TryPlayerPose(out float px, out float pz, out float fx, out float fz)) return false;
        if (!IsReadable((nint)0x140AA8098L, 8)) return false;
        nint root = *(nint*)0x140AA8098L;
        if (root <= 0x10000 || !IsReadable(root + 0x08, 8)) return false;
        nint scene = *(nint*)(root + 0x08);
        if (scene <= 0x10000 || !IsReadable(scene + 0x50, 8)) return false;
        nint cur = *(nint*)(scene + 0x50);
        float best = float.MaxValue; int n = 0;
        while (cur > 0x10000 && n++ < 384)
        {
            if (IsReadable(cur + 0x28, 4) && (*(int*)(cur + 0x28) & 2) != 0 && IsReadable(cur + 0x168, 8))
            {
                nint xf = *(nint*)(cur + 0x168);
                if (xf > 0x10000 && IsReadable(xf + 0xf88, 4) && (*(int*)(xf + 0xf88) & 1) != 0
                    && IsReadable(xf + 0x360, 12))
                {
                    float x = *(float*)(xf + 0x360), z = *(float*)(xf + 0x368);
                    float dx = x - px, dz = z - pz; float d = MathF.Sqrt(dx * dx + dz * dz);
                    // within 350u (DAT_14096585c) AND inside the ±75° view cone (cos75°≈0.26), nearest wins.
                    if (d > 1f && d < 350f && float.IsFinite(x) && float.IsFinite(z)
                        && (fx * dx + fz * dz) / d > 0.26f && d < best)
                    { best = d; ox = x; oz = z; }
                }
            }
            if (!IsReadable(cur + 0x150, 8)) break;
            cur = *(nint*)(cur + 0x150);
        }
        return best < float.MaxValue;
    }

    // ── TRUE world-space player position (overworld) ────────────────────────
    // Found 2026-06-12 (see database/OVERWORLD.md "Runtime correlation"). The
    // 2.5D chain sub_obj+0xD0/+0xD4 is a LOCAL/SCROLL space, NOT the space the
    // assets (FBN/HBN) and unit-registry nodes use. The world position lives on
    // the player's unit transform:
    //   playerNode = *(0x140EC0FF0)            (party member 0, cat-1 unit 0x400)
    //   *(playerNode+0x250) → obj → *(+0x48) → sub → *(+0x18) → xform
    //   xform+0x360/+0x364/+0x368 = world X, Y, Z
    // Verified live: (393.9, 2.9, -2103.6) standing at the save-point trigger
    // box (409, -2102) in Shopping District South.
    private static readonly unsafe nint* _partyArray0 = (nint*)0x140EC0FF0L;

    /// <summary>
    /// World-space player position for overworld navigation (the coordinate
    /// system of overworld_catalog.json / FBN / HBN / unit-registry nodes).
    /// Returns ok=false when any link of the chain is unreadable.
    /// </summary>
    internal static unsafe (float x, float y, float z, bool ok) WorldPlayerPos()
    {
        if (!IsReadable((nint)_partyArray0, 8)) return (0, 0, 0, false);
        nint node = *_partyArray0;
        if (node == 0 || !IsReadable(node + 0x250, 8)) return (0, 0, 0, false);
        nint obj = *(nint*)(node + 0x250);
        if (obj == 0 || !IsReadable(obj + 0x48, 8)) return (0, 0, 0, false);
        nint sub = *(nint*)(obj + 0x48);
        if (sub == 0 || !IsReadable(sub + 0x18, 8)) return (0, 0, 0, false);
        nint xform = *(nint*)(sub + 0x18);
        if (xform == 0 || !IsReadable(xform + 0x360, 12)) return (0, 0, 0, false);
        float x = *(float*)(xform + 0x360);
        float y = *(float*)(xform + 0x364);
        float z = *(float*)(xform + 0x368);
        if (!float.IsFinite(x) || !float.IsFinite(z)) return (0, 0, 0, false);
        return (x, y, z, true);
    }

    // Camera forward vector (NOT player facing): sub_obj+0xE0/+0xE4.
    // Stays roughly fixed per area because the camera is locked to the road.
    internal static unsafe float CameraForwardX
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok || !IsReadable(sub + 0xE0, 8)) return float.NaN;
            float f = *(float*)((byte*)sub + 0xE0);
            return (float.IsNaN(f) || float.IsInfinity(f)) ? float.NaN : f;
        }
    }

    internal static unsafe float CameraForwardZ
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok || !IsReadable(sub + 0xE4, 4)) return float.NaN;
            float f = *(float*)((byte*)sub + 0xE4);
            return (float.IsNaN(f) || float.IsInfinity(f)) ? float.NaN : f;
        }
    }

    // Player facing — real. Reached via *(sub_obj + 0x18).
    //   +0x260 = forward.x        +0x268 = forward.z
    //   +0x280 = right.x          +0x288 = right.z   (right = forward rotated 90° CW)
    // Both pairs are unit vectors. Confirmed by a 180° turn flipping all four signs
    // with the correct perpendicularity between forward and right.
    private static unsafe (nint ptr, bool ok) GetFacingStructPtr()
    {
        var (sub, ok) = GetSubObjPtr();
        if (!ok) return (0, false);
        if (!IsReadable(sub + 0x18, 8)) return (0, false);
        nint p = *(nint*)(sub + 0x18);
        if (p == 0) return (0, false);
        if (!IsReadable(p + 0x260, 0x10)) return (0, false);
        return (p, true);
    }

    /// <summary>
    /// Player facing X. In 3D dungeons (major>=20) facing is NOT stored in any
    /// of the scanned struct fields — verified by F10 diff snapshot, no float
    /// in sub_obj/*(sub_obj+0x10)/*(sub_obj+0x18)/fieldObj changes when the
    /// player turns in place. P4G's avatar always faces its movement direction,
    /// so we derive facing from the velocity (Δposition over a short window).
    /// While stationary we hold the last computed facing.
    /// 2.5D overworld still uses the chain at *(sub_obj+0x18)+0x260 which was
    /// previously verified there.
    /// </summary>
    internal static unsafe float ForwardX
    {
        get
        {
            if (CurrentMajor >= 20) { UpdateVelocityFacing(); return _vfx; }
            var (p, ok) = GetFacingStructPtr();
            if (!ok) return float.NaN;
            float f = *(float*)(p + 0x260);
            return float.IsFinite(f) ? f : float.NaN;
        }
    }

    internal static unsafe float ForwardZ
    {
        get
        {
            if (CurrentMajor >= 20) { UpdateVelocityFacing(); return _vfz; }
            var (p, ok) = GetFacingStructPtr();
            if (!ok) return float.NaN;
            float f = *(float*)(p + 0x268);
            return float.IsFinite(f) ? f : float.NaN;
        }
    }

    // ── Facing for 3D dungeons (velocity + input key) ────────────────────
    // We can't read the player's facing directly in dungeons, so we derive it.
    // Two signals combine:
    //   1) Input keys (W/A/S/D and arrows) — instant, no lag. When the player
    //      starts pressing a direction we already know which way they want to
    //      face, even before the character's turn-before-walk animation runs.
    //      Resolved through the *camera forward* read at sub_obj+0xE0/+0xE4
    //      so screen-relative input maps to world-relative facing correctly.
    //   2) Velocity (Δposition over last ~200 ms) — used when no key is held
    //      (e.g. controller, momentum after key release) and as the canonical
    //      "I'm actually moving this way" signal when keys disagree.
    //
    // Default points along +Z so audio degrades gracefully to world-aligned
    // if neither signal is available.

    private const int VelHistory = 3;          // ~300 ms at 100 ms poll
    private const float MoveEpsilon = 0.5f;
    private static readonly float[] _vhX = new float[VelHistory];
    private static readonly float[] _vhZ = new float[VelHistory];
    private static int _vhIdx = 0;
    private static int _vhCount = 0;
    private static float _vfx = 0f, _vfz = 1f; // last known facing

    private static void UpdateVelocityFacing()
    {
        // ── 1. Try input keys (instant, no lag) ───────────────────────────
        // Convert held W/A/S/D / arrow input into a desired-direction vector
        // in screen space, then rotate by the camera basis to get a
        // world-space facing. Camera forward read from sub_obj+0xE0/+0xE4.
        bool kFwd  = IsKeyDown(0x57) || IsKeyDown(0x26); // W / Up
        bool kBack = IsKeyDown(0x53) || IsKeyDown(0x28); // S / Down
        bool kLeft = IsKeyDown(0x41) || IsKeyDown(0x25); // A / Left
        bool kRight= IsKeyDown(0x44) || IsKeyDown(0x27); // D / Right
        int keysHeld = (kFwd ? 1 : 0) + (kBack ? 1 : 0) + (kLeft ? 1 : 0) + (kRight ? 1 : 0);
        if (keysHeld > 0)
        {
            float cfx = CameraForwardX, cfz = CameraForwardZ;
            float clen2 = cfx * cfx + cfz * cfz;
            if (float.IsFinite(cfx) && float.IsFinite(cfz) && clen2 > 0.81f && clen2 < 1.21f)
            {
                float cn = 1f / MathF.Sqrt(clen2);
                cfx *= cn; cfz *= cn;
                // Camera right = camera forward rotated 90° CW (same convention
                // as InspectCollider's player-frame basis).
                float crx = cfz, crz = -cfx;
                // Sum the held keys' contributions.
                float ix = 0f, iz = 0f;
                if (kFwd)   { ix += cfx; iz += cfz; }
                if (kBack)  { ix -= cfx; iz -= cfz; }
                if (kRight) { ix += crx; iz += crz; }
                if (kLeft)  { ix -= crx; iz -= crz; }
                float ilen2 = ix * ix + iz * iz;
                if (ilen2 > 0.01f)
                {
                    float n = 1f / MathF.Sqrt(ilen2);
                    _vfx = ix * n; _vfz = iz * n;
                    // Still update the ring so velocity stays consistent for
                    // the moment input is released.
                }
            }
        }

        // ── 2. Velocity ring (fallback / canonical) ───────────────────────
        float x = LivePlayerX, z = LivePlayerZ;
        if (float.IsNaN(x) || float.IsNaN(z)) return;

        int prev = (_vhIdx - 1 + VelHistory) % VelHistory;
        if (_vhCount > 0 &&
            MathF.Abs(x - _vhX[prev]) < 0.001f &&
            MathF.Abs(z - _vhZ[prev]) < 0.001f) return;

        _vhX[_vhIdx] = x; _vhZ[_vhIdx] = z;
        _vhIdx = (_vhIdx + 1) % VelHistory;
        if (_vhCount < VelHistory) _vhCount++;
        if (_vhCount < 2) return;
        if (keysHeld > 0) return;          // input already supplied facing this tick

        int oldest = (_vhIdx - _vhCount + VelHistory) % VelHistory;
        float dx = x - _vhX[oldest];
        float dz = z - _vhZ[oldest];
        float d2 = dx * dx + dz * dz;
        if (d2 < MoveEpsilon * MoveEpsilon) return;
        float n2 = 1f / MathF.Sqrt(d2);
        _vfx = dx * n2; _vfz = dz * n2;
    }


    // Legacy spawn-point accessor (sub_obj+0x20/+0x24). Static — does NOT update while walking.
    private static unsafe float PlayerX2DChain
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            float x = *(float*)((byte*)sub + 0x20);
            return (float.IsNaN(x) || float.IsInfinity(x) || x == 0f) ? float.NaN : x;
        }
    }

    private static unsafe float PlayerZ2DChain
    {
        get
        {
            var (sub, ok) = GetSubObjPtr();
            if (!ok) return float.NaN;
            float z = *(float*)((byte*)sub + 0x24);
            return (float.IsNaN(z) || float.IsInfinity(z)) ? float.NaN : z;
        }
    }

    private bool _f1, _f1Was, _f2, _f2Was, _f3, _f3Was, _f4, _f4Was, _f5, _f5Was, _f6, _f6Was, _f7, _f7Was, _f8, _f8Was, _f9, _f9Was, _f10, _f10Was, _f11, _f11Was, _scrollLock, _scrollLockWas, _home, _homeWas, _end, _endWas, _pageUp, _pageUpWas, _pageDown, _pageDownWas, _numpad5, _numpad5Was, _numpad0, _numpad0Was, _numpad2, _numpad2Was, _numpad3, _numpad3Was, _numpad4, _numpad4Was, _numpad7, _numpad7Was, _numpad8, _numpad8Was, _numpad9, _numpad9Was;
    private volatile bool _npcMonitorActive = false;

    // ── Interactable-index capture via AsmHook ──────────────────────────────────
    // Inside FUN_1402EDB70 (Ghidra), at VA 0x1402EE068, the game executes
    //   mov dword ptr [rdx + rcx + 0x10], edi
    // This writes the interactable INDEX into the active-slot struct. Just
    // before that store runs, EDI holds the index and RBX holds a pointer to
    // the registry entry (entryPtr = RBX - 2). A function-level trampoline on
    // this function crashes the game, but an inline AsmHook at this single
    // instruction works because we only replace a handful of bytes and
    // preserve all registers around our capture.
    //
    // We allocate 24 bytes of unmanaged storage laid out as:
    //   +0  uint32  capturedIndex  (from EDI)
    //   +8  uint64  capturedEntry  (from RBX - 2)
    //   +16 uint64  capturedHitCount (incremented every time the hook fires)
    private static IntPtr _interactCaptureStorage = IntPtr.Zero;
    private IAsmHook? _interactIndexAsmHook;

    internal static unsafe uint InteractableIndexCaptured
    {
        get
        {
            if (_interactCaptureStorage == IntPtr.Zero) return 0;
            return *(uint*)_interactCaptureStorage;
        }
    }

    internal static unsafe nint InteractableEntryCaptured
    {
        get
        {
            if (_interactCaptureStorage == IntPtr.Zero) return 0;
            return *(nint*)((byte*)_interactCaptureStorage + 8);
        }
    }

    internal static unsafe ulong InteractableCaptureHits
    {
        get
        {
            if (_interactCaptureStorage == IntPtr.Zero) return 0;
            return *(ulong*)((byte*)_interactCaptureStorage + 16);
        }
    }

    // ── Player world position (captured via AsmHook on entity position write) ──────
    // Write site: MOVSS [rbx+0x70], xmm1 (F3 0F 11 4B 70) — unique in P4G.exe.
    // The function iterates entities indexed by r12 (0, 8, 16 ... per entity pointer).
    // r12==0 → entity index 0 → assumed to be the player character (verify empirically).
    // When r12==0, rbx = ptr to the player transform struct; X is at [rbx+0x70], Z at [rbx+0x78].
    // _playerXformStorage holds the live rbx value so we can read X/Z at any time.
    private static IntPtr _playerXformStorage = IntPtr.Zero;   // Marshal.AllocHGlobal(8)
    private IAsmHook? _playerPosHook;

    // 3D position (dungeon/Midnight Channel): X at [rbx+0x70], Z at [rbx+0x78]
    private static unsafe float PlayerX3D =>
        _playerXformStorage != IntPtr.Zero && *(nint*)_playerXformStorage != 0
            ? *(float*)((byte*)*(nint*)_playerXformStorage + 0x70) : float.NaN;

    private static unsafe float PlayerZ3D =>
        _playerXformStorage != IntPtr.Zero && *(nint*)_playerXformStorage != 0
            ? *(float*)((byte*)*(nint*)_playerXformStorage + 0x78) : float.NaN;

    // ── 2.5D player position (overworld: Shopping District, school, Junes, etc.) ──
    // Write site: MOVUPS [rbx+0x30], xmm1 at VA 0x14050468F (file offset 0x503C8F).
    // Found via Frida heap scan + Cheat Engine "Find what writes" on address 0x12781308.
    // Fires once per frame for the player. rbx = player 2.5D transform ptr.
    // X at [rbx+0x30], Z at [rbx+0x34].
    private static IntPtr _playerXform2DStorage = IntPtr.Zero;
    private IAsmHook? _playerPos2DHook;

    // Per-frame 2D position from the new hook: X at [r14+0x50], Z at [r14+0x54].
    // xmm0 stores two packed 32-bit floats, confirmed by F8 scan finding both X and Z
    // as adjacent floats at session addresses 0x12781308 (X) and 0x1278130C (Z).
    private static unsafe float PlayerX2DAsmHook =>
        _playerXform2DStorage != IntPtr.Zero && *(nint*)_playerXform2DStorage != 0
            ? *(float*)((byte*)*(nint*)_playerXform2DStorage + 0x50) : float.NaN;

    private static unsafe float PlayerZ2DAsmHook =>
        _playerXform2DStorage != IntPtr.Zero && *(nint*)_playerXform2DStorage != 0
            ? *(float*)((byte*)*(nint*)_playerXform2DStorage + 0x54) : float.NaN;

// Live 2D position: sub_obj+0xD0/+0xD4 (confirmed via Frida heap scan).
    // Falls back to AsmHook, then the spawn-point chain if the primary read fails.
    private static float PlayerX2D
    {
        get
        {
            float x = PlayerX2DLive;
            if (!float.IsNaN(x)) return x;
            if (!float.IsNaN(PlayerX2DAsmHook)) return PlayerX2DAsmHook;
            return PlayerX2DChain;
        }
    }

    private static float PlayerZ2D
    {
        get
        {
            float z = PlayerZ2DLive;
            if (!float.IsNaN(z)) return z;
            if (!float.IsNaN(PlayerZ2DAsmHook)) return PlayerZ2DAsmHook;
            return PlayerZ2DChain;
        }
    }

    // Best available position. Mode-dispatch by area: in dungeons the 2D AsmHook
    // fires for NPCs (not the player) with a 3D-struct layout where +0x54 is Y
    // instead of Z — reading through it gives garbage. So for major >= 20 we go
    // straight to the sub_obj+0x10 path which IS the player.
    private static float PlayerX
    {
        get
        {
            if (CurrentMajor >= 20)
            {
                float x = PlayerX3DViaSub; if (!float.IsNaN(x)) return x;
                x = PlayerX3D;             if (!float.IsNaN(x)) return x;
                return float.NaN;
            }
            float y = PlayerX2D; if (!float.IsNaN(y)) return y;
            y = PlayerX3DViaSub; if (!float.IsNaN(y)) return y;
            return PlayerX3D;
        }
    }

    private static float PlayerZ
    {
        get
        {
            if (CurrentMajor >= 20)
            {
                float z = PlayerZ3DViaSub; if (!float.IsNaN(z)) return z;
                z = PlayerZ3D;             if (!float.IsNaN(z)) return z;
                return float.NaN;
            }
            float y = PlayerZ2D; if (!float.IsNaN(y)) return y;
            y = PlayerZ3DViaSub; if (!float.IsNaN(y)) return y;
            return PlayerZ3D;
        }
    }

    // F7 snapshot comparison state (static globals, flags/enums only)
    private int[]? _snapA = null;
    private int[]? _snapB = null;

    // F8 heap position scanner state
    private int[]? _heapSnapA = null;
    private long   _heapSnapBase = 0;

    // Scan regions: (base address, count of ints)
    private static readonly (long Base, int Count)[] _scanRegions =
    {
        (0x140AA0000L, 32768),  // near segMiscPtr (128 KB of game state globals)
        (0x1411A0000L, 32768),  // near NPC/field obj area (128 KB)
    };

    internal unsafe FieldTracker(IReloadedHooks hooks)
    {
        // ── Player position hook ───────────────────────────────────────────────────────
        // Binary-analysis result: unique pattern "F3 0F 11 4B 70 F3 0F 58 D3 0F 57 C9"
        // (MOVSS [rbx+0x70],xmm1 + ADDSS xmm2,xmm3 + XORPS xmm1,xmm1) — 1 match in exe.
        // The outer loop uses r12 as an entity array byte-index (0, 8, 16 ...).
        // r12==0 is assumed to be the player (entity 0).  See F9 log to verify.
        _playerXformStorage = Marshal.AllocHGlobal(8);
        *(nint*)_playerXformStorage = 0;
        var storAddr = (nuint)(void*)_playerXformStorage;

        SigScan("F3 0F 11 4B 70 F3 0F 58 D3 0F 57 C9", "EntityPositionWrite", address =>
        {
            _playerPosHook = hooks.CreateAsmHook(new[]
            {
                "use64",
                "pushfq",
                "cmp r12, 0",
                "jne _epw_skip",
                $"mov qword [{storAddr}], rbx",
                "_epw_skip:",
                "popfq",
            }, address, AsmHookBehaviour.ExecuteFirst).Activate();
            Log($"[FieldTracker] Player position hook (3D) activated at 0x{address:X}");
        });

        // ── 2.5D player position hook (per-frame) ─────────────────────────────────────
        // Target: MOVSD [r14+0x50], xmm0  at VA 0x1402D4070 (file offset 0x2D3670)
        // Found via: Cheat Engine "find what writes" on heap address 0x12781308 — 1125 hits/session (~60fps).
        // xmm0 holds two packed 32-bit floats: low 4 bytes = X, high 4 bytes = Z.
        // r14 is the player 2.5D transform base; X = [r14+0x50], Z = [r14+0x54].
        //
        // SigScan pattern: the 8 bytes BEFORE the target are unique in the entire exe.
        // Pattern: 41 0F 10 45 00 49 8B CE  (starts at VA 0x1402D4068)
        // Hook goes at address+8 to land exactly on the MOVSD instruction.
        _playerXform2DStorage = Marshal.AllocHGlobal(8);
        *(nint*)_playerXform2DStorage = 0;
        var storAddr2D = (nuint)(void*)_playerXform2DStorage;

        SigScan("41 0F 10 45 00 49 8B CE F2 41 0F 11 46 50", "PlayerPos2DWrite", address =>
        {
            _playerPos2DHook = hooks.CreateAsmHook(new[]
            {
                "use64",
                "pushfq",
                $"mov qword [{storAddr2D}], r14",
                "popfq",
            }, address + 8, AsmHookBehaviour.ExecuteFirst).Activate();
            Log($"[FieldTracker] Player position hook (2.5D per-frame) activated at 0x{address + 8:X}");
        });

        // Area: MOV RAX,[RIP+x]; MOV ECX,[RAX]; CMP ECX,0x17; JNZ
        SigScan("48 8B 05 ?? ?? ?? ?? 8B 08 83 F9 17 75", "SegMiscPtr",
            address =>
            {
                _segMiscPtr = (int**)GetGlobalAddress(address + 3);
                Log($"[FieldTracker] segMiscPtr found at 0x{(nint)_segMiscPtr:X}");
            });

        // Time/day write site A: MOV RAX,[RIP+x]; MOV WORD [RAX+8], BX  → day confirmed at +8
        SigScan("48 8B 05 ?? ?? ?? ?? 66 89 58 08", "SegTimeDayWrite",
            address =>
            {
                _segTimeDayPtr = (byte**)GetGlobalAddress(address + 3);
                Log($"[FieldTracker] segTimeDayPtr (write site A) found at 0x{(nint)_segTimeDayPtr:X}");
            });

        // Time/day write site B: cluster that writes +2 and +4 from same global
        // Layout (zarroboogs _segTime): +0=Day, +2=DayTime, +8=NextDay, +0xA=DayTimeNext
        SigScan("48 8B 05 ?? ?? ?? ?? 66 89 48 02 B9 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 66 89 48 04",
            "SegTimeLiveCluster",
            address =>
            {
                _segTimeLivePtr = (short**)GetGlobalAddress(address + 3);
                Log($"[FieldTracker] segTimeLivePtr (write site B) found at 0x{(nint)_segTimeLivePtr:X}");
            });

        // Field object pointer (global 0x1411AB2C8): found via Ghidra
        // Instruction: MOV [0x1411AB2C8], RAX  immediately followed by  MOV RAX, [segMiscPtr]
        // The struct it points to likely contains the NPC-nearby / interaction flag.
        SigScan("48 89 05 ?? ?? ?? ?? 48 8B 05 84 A5 7D 00", "FieldObjPtr",
            address =>
            {
                _fieldObjPtr = (void**)GetGlobalAddress(address + 3);
                Log($"[FieldTracker] fieldObjPtr found at 0x{(nint)_fieldObjPtr:X}");
            });

        // NOTE: AsmHook at 0x1402EE068 also crashed the game — the target
        // instruction sits inside a hot loop and patching it corrupts state.
        // Disabled. See memory notes for details and next experiments.

        new Thread(Poll)       { IsBackground = true, Name = "FieldTracker"     }.Start();
        new Thread(KeyPoll)    { IsBackground = true, Name = "FieldTrackerKeys" }.Start();
        new Thread(NpcMonitor) { IsBackground = true, Name = "NpcMonitor"       }.Start();
    }

    // ── Polling ───────────────────────────────────────────────────────────

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(500);
            try   { CheckChanges(); }
            catch (Exception ex) { Log($"[FieldTracker] Poll error: {ex.Message}"); }
        }
    }

    private void KeyPoll()
    {
        while (true)
        {
            Thread.Sleep(50);

            if (!Utils.GameHasFocus()) continue;   // ignore hotkeys while alt-tabbed

            // M key (rebound from F1 2026-06-12 — closer to the other mod keys).
            // Shift+M is the dialogue-reader toggle (HistoryKeys), so plain M only when Shift is up.
            _f1 = IsKeyDown(0x4D) && !IsKeyDown(0x10);
            if (_f1 && !_f1Was)
            {
                Log($"[FieldTracker] F1 | major={_lastMajor} minor={_lastMinor} " +
                    $"period={_lastTimePeriod} day={_lastGameDay}");
                AnnounceStatus();
            }
            _f1Was = _f1;
            // Developer diagnostic hotkeys (F2-F11, Home, Insert, Page Up/Down,
            // Scroll Lock, Numpad) REMOVED 2026-06-11 so the player can't trip a
            // memory dump. F1 (area/time/date) kept — it's player-facing. The
            // dump methods remain in the file as unused helpers (harmless).
        }
    }

    private static unsafe void ToggleHardwareWatchpoint()
    {
        var (sub, ok) = GetSubObjPtr();
        if (!ok) { Speech.Say("Sub_obj unavailable.", true); return; }
        if (!IsReadable(sub + 0x10, 8)) { Speech.Say("Xform pointer unreadable.", true); return; }
        nint xform = *(nint*)(sub + 0x10);
        if ((ulong)xform < 0x10000UL) { Speech.Say("Xform null.", true); return; }
        long watchAddr = (long)(xform + 0x50);
        HardwareWatchpoint.Toggle(watchAddr);
    }

    // Last interactable snapshot, per cat → list of (id, world XZ) for every
    // instance. Stored as a list (not dict-by-id) because cat=4 has many
    // entries sharing the same id (e.g. id=0x07D0 appears 9× at different
    // positions); a dict would silently collapse them and the diff would
    // miss real instance-level changes. Diff matches by id + position
    // tolerance to identify which instance is the same entity across presses.
    private static Dictionary<int, List<(short id, float wx, float wz)>>? _lastInteractableSnapshot;

    /// <summary>
    /// Numpad 0: walk the per-frame interactable master table at
    /// 0x146248560 and dump each loaded interactable's id + candidate
    /// position fields. The table is one 8-byte pointer per category;
    /// each pointer heads a linked list (next at +0xfc0). Found via
    /// thunk_FUN_16ac1a140 (called from FUN_1402EDB70 — the per-frame
    /// interactable scanner) — see memory/ghidra_interactable_findings.md
    /// for context.
    ///
    /// Position candidates (+0x360, sub10+0x50, sub-fc8+0x50) are kept in
    /// the per-entry log line for diagnostic continuity, but only +0x360
    /// is treated as authoritative for the summary/diff/speech (matches
    /// DoorRadar ground truth for cat=1).
    /// </summary>
    private static unsafe void DumpInteractableMasterTable()
    {
        const long TableBase = 0x146248560L;
        const int  MaxCategories = 64;       // generous; observed up to ~28
        const int  MaxEntriesPerCategory = 64;
        const float StepUnits = 30f;          // DoorRadar convention
        const float NearRadius = 1500f;       // 50 steps — diff/speech cutoff

        Log("[FieldTracker] === Numpad0 INTERACTABLE MASTER TABLE DUMP ===");
        float px = LivePlayerX, pz = LivePlayerZ;
        Log($"[FieldTracker]   player pos=({px:F0},{pz:F0})  major={CurrentMajor} minor={CurrentMinor}");

        if (!IsReadable((nint)TableBase, MaxCategories * 8))
        {
            Log("[FieldTracker]   master table address unreadable");
            return;
        }

        var snapshot = new Dictionary<int, List<(short id, float wx, float wz)>>();
        int totalEntries = 0;
        for (int cat = 0; cat < MaxCategories; cat++)
        {
            nint head = *(nint*)((nint)TableBase + cat * 8);
            if ((ulong)head <= 0x10000UL) continue;
            if (!IsReadable(head, 0x1000)) continue;

            int countInCat = 0;
            nint cur = head;
            while (cur != 0 && countInCat < MaxEntriesPerCategory)
            {
                if (!IsReadable(cur, 0x1000)) break;

                short id = *(short*)(cur + 0x406);
                uint flags = *(uint*)(cur + 0x408);

                float p360x = 0, p360z = 0;
                if (IsReadable(cur + 0x360, 12)) { p360x = *(float*)(cur + 0x360); p360z = *(float*)(cur + 0x368); }

                float pSubX = float.NaN, pSubZ = float.NaN;
                if (IsReadable(cur + 0x10, 8))
                {
                    nint sub = *(nint*)(cur + 0x10);
                    if ((ulong)sub > 0x10000UL && IsReadable(sub + 0x50, 12))
                    {
                        pSubX = *(float*)(sub + 0x50);
                        pSubZ = *(float*)(sub + 0x58);
                    }
                }

                float pFcX = float.NaN, pFcZ = float.NaN;
                if (IsReadable(cur + 0xfc8, 8))
                {
                    nint fc = *(nint*)(cur + 0xfc8);
                    if ((ulong)fc > 0x10000UL && IsReadable(fc + 0x50, 12))
                    {
                        pFcX = *(float*)(fc + 0x50);
                        pFcZ = *(float*)(fc + 0x58);
                    }
                }

                static string F(float v) => float.IsNaN(v) ? "NaN" : v.ToString("F0");
                float dx360 = p360x - px, dz360 = p360z - pz;
                float dist360 = MathF.Sqrt(dx360 * dx360 + dz360 * dz360);

                Log($"[FieldTracker]   cat={cat,2} id=0x{(ushort)id:X4} flags=0x{flags:X8} obj=0x{(ulong)cur:X12}  " +
                    $"+360=({F(p360x)},{F(p360z)})|d={dist360:F0}  " +
                    $"sub10=({F(pSubX)},{F(pSubZ)})  " +
                    $"sub-fc8=({F(pFcX)},{F(pFcZ)})");

                // Skip gaze cursor (cat=1 id=0x0001 tracks player facing, not a real entity).
                // Filter (0,0) and out-of-range positions (cat=20+ system rows have garbage
                // at +0x360 — values like 3.49e18 are finite but obvious noise; the game
                // map fits in roughly ±20 000 units, so 1e6 is a safe ceiling).
                bool isGaze = cat == 1 && id == 0x0001;
                bool posOk = float.IsFinite(p360x) && float.IsFinite(p360z)
                    && (p360x != 0f || p360z != 0f)
                    && MathF.Abs(p360x) < 1e6f && MathF.Abs(p360z) < 1e6f;
                if (!isGaze && posOk)
                {
                    if (!snapshot.TryGetValue(cat, out var list))
                        snapshot[cat] = list = new List<(short, float, float)>();
                    list.Add((id, p360x, p360z));
                }

                if (!IsReadable(cur + 0xfc0, 8)) break;
                cur = *(nint*)(cur + 0xfc0);
                countInCat++;
                totalEntries++;
            }
        }

        Log($"[FieldTracker]   total entries dumped: {totalEntries}");

        var perCat = new List<(int cat, short nearestId, float dx, float dz, float dist, int count)>();
        foreach (var kv in snapshot)
        {
            short bestId = 0;
            float bestDist = float.PositiveInfinity, bestDx = 0, bestDz = 0;
            foreach (var ent in kv.Value)
            {
                float dx = ent.wx - px, dz = ent.wz - pz;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < bestDist) { bestDist = d; bestDx = dx; bestDz = dz; bestId = ent.id; }
            }
            perCat.Add((kv.Key, bestId, bestDx, bestDz, bestDist, kv.Value.Count));
        }
        perCat.Sort((a, b) => a.dist.CompareTo(b.dist));

        Log("[FieldTracker]   per-category nearest (gaze + zero/out-of-range pos filtered):");
        foreach (var c in perCat)
        {
            int steps = Math.Max(1, (int)MathF.Round(c.dist / StepUnits));
            Log($"[FieldTracker]     cat={c.cat,2} count={c.count,2} nearest id=0x{(ushort)c.nearestId:X4} d={c.dist:F0} (~{steps} steps) rel=({c.dx:F0},{c.dz:F0})");
        }

        if (_lastInteractableSnapshot != null)
        {
            // Per-instance diff: greedy 1:1 match by id and proximity (≤MatchTol).
            // An entry that moves ≤MatchTol = same instance. Unmatched old = LOST,
            // unmatched new = NEW. Without per-instance matching, repeated ids
            // (cat=4 0x07D0 ×9) would mask real changes.
            const float MatchTol = 50f;
            Log($"[FieldTracker]   diff vs last press (within {(int)(NearRadius / StepUnits)} steps, instance match tol {MatchTol}u):");
            int diffCount = 0;
            var allCats = new HashSet<int>(snapshot.Keys);
            allCats.UnionWith(_lastInteractableSnapshot.Keys);
            foreach (var cat in allCats.OrderBy(c => c))
            {
                snapshot.TryGetValue(cat, out var nowList);
                _lastInteractableSnapshot.TryGetValue(cat, out var prevList);
                nowList ??= new List<(short, float, float)>();
                prevList ??= new List<(short, float, float)>();

                var nowMatched = new bool[nowList.Count];
                foreach (var prev in prevList)
                {
                    int bestIdx = -1; float bestD = MatchTol;
                    for (int i = 0; i < nowList.Count; i++)
                    {
                        if (nowMatched[i] || nowList[i].id != prev.id) continue;
                        float dx = nowList[i].wx - prev.wx, dz = nowList[i].wz - prev.wz;
                        float d = MathF.Sqrt(dx * dx + dz * dz);
                        if (d < bestD) { bestD = d; bestIdx = i; }
                    }
                    if (bestIdx >= 0) { nowMatched[bestIdx] = true; continue; }

                    float pdx = prev.wx - px, pdz = prev.wz - pz;
                    float pdist = MathF.Sqrt(pdx * pdx + pdz * pdz);
                    if (pdist > NearRadius) continue;
                    int steps = Math.Max(1, (int)MathF.Round(pdist / StepUnits));
                    Log($"[FieldTracker]     cat={cat,2} LOST id=0x{(ushort)prev.id:X4} was at ({prev.wx:F0},{prev.wz:F0}) d={pdist:F0} (~{steps} steps)");
                    diffCount++;
                }
                for (int i = 0; i < nowList.Count; i++)
                {
                    if (nowMatched[i]) continue;
                    var n = nowList[i];
                    float ndx = n.wx - px, ndz = n.wz - pz;
                    float ndist = MathF.Sqrt(ndx * ndx + ndz * ndz);
                    if (ndist > NearRadius) continue;
                    int steps = Math.Max(1, (int)MathF.Round(ndist / StepUnits));
                    Log($"[FieldTracker]     cat={cat,2} NEW  id=0x{(ushort)n.id:X4} at  ({n.wx:F0},{n.wz:F0}) d={ndist:F0} (~{steps} steps)");
                    diffCount++;
                }
            }
            if (diffCount == 0) Log("[FieldTracker]     (no near-player changes)");
        }
        _lastInteractableSnapshot = snapshot;

        var (gfx, gfz) = PlayerForwardViaGaze();
        string? facing = (gfx == 0 && gfz == 0) ? null
            : (MathF.Abs(gfz) > MathF.Abs(gfx)
                ? (gfz > 0 ? "north" : "south")
                : (gfx > 0 ? "east"  : "west"));

        var sb = new System.Text.StringBuilder();
        if (facing != null) sb.Append($"You face {facing}. ");
        int spoken = 0;
        foreach (var c in perCat)
        {
            if (c.dist > NearRadius) continue;
            int steps = Math.Max(1, (int)MathF.Round(c.dist / StepUnits));
            string dir = MathF.Abs(c.dz) > MathF.Abs(c.dx)
                ? (c.dz > 0 ? "north" : "south")
                : (c.dx > 0 ? "east"  : "west");
            sb.Append($"Cat {c.cat}: {steps} step{(steps == 1 ? "" : "s")} {dir}, id {(ushort)c.nearestId}. ");
            if (++spoken >= 4) break;
        }
        if (spoken == 0) sb.Append($"No interactables within {(int)(NearRadius / StepUnits)} steps.");

        Speech.Say(sb.ToString().TrimEnd(), true);
    }

    // Last treasure-array snapshot, keyed by entry's (X,Z) position rounded to
    // ints (chests don't move, so position is a stable identity across presses).
    // Each value: (entry_bytes, sub_struct_bytes) used for byte-level diff to
    // surface the "opened" indicator. The first 0x60 of the master entry
    // didn't change when a chest was opened (test 2026-05-10) — the open
    // flag is most likely in the per-chest struct at +0x18.
    private static Dictionary<(int x, int z), (byte[] entry, byte[] sub)>? _lastTreasureSnapshot;

    /// <summary>
    /// Numpad 2: probe the per-floor treasure array at <c>0x15E437730</c>.
    /// Layout discovered 2026-05-10: stride 0x190; first 8 floats are
    /// world position (X at +0x00, Y at +0x04, Z at +0x08) and rotation/
    /// scale/active flag/per-chest ptr after that. Dumps the first
    /// DumpBytes of each non-empty entry, computes distance from the
    /// player, and diffs against the previous snapshot to highlight which
    /// bytes change when a chest is opened.
    /// </summary>
    private static unsafe void DumpSuspectedTreasureArray()
    {
        const long Base = 0x15E437730L;
        const int  Stride = 0x190;
        const int  Count = 16;
        const int  HexBytes = 0x60;        // bytes printed inline for readability
        const int  SnapBytes = 0x190;      // full entry tracked in snapshot for diff
        const int  SubHexBytes = 0x40;     // bytes of sub-struct printed inline
        const int  SubSnapBytes = 0x100;   // wider sub-struct tracked for diff
        const float MapRange = 50000f;
        const float StepUnits = 30f;

        Log("[FieldTracker] === Numpad2 TREASURE ARRAY DUMP ===");
        float px = LivePlayerX, pz = LivePlayerZ;
        Log($"[FieldTracker]   player pos=({px:F0},{pz:F0})  major={CurrentMajor} minor={CurrentMinor}");

        if (!IsReadable((nint)Base, Count * Stride))
        {
            Log("[FieldTracker]   array address unreadable");
            Speech.Say("Treasure array unreadable.", true);
            return;
        }

        var snapshot = new Dictionary<(int x, int z), (byte[] entry, byte[] sub)>();
        int populated = 0;
        (int idx, float d, float dx, float dz)? nearest = null;

        for (int i = 0; i < Count; i++)
        {
            nint entry = (nint)(Base + i * Stride);
            float fx = *(float*)(entry + 0x00);
            float fy = *(float*)(entry + 0x04);
            float fz = *(float*)(entry + 0x08);

            // Empty/garbage filter. Real chests: X/Z are world coords (thousands),
            // Y is floor height (~3.0). Heap-noise entries past the array end
            // showed denormal X/Z (~1e-19) and Y=0 — would slip past `== 0f`.
            bool empty =
                !float.IsFinite(fx) || !float.IsFinite(fy) || !float.IsFinite(fz)
                || (MathF.Abs(fx) < 1f && MathF.Abs(fz) < 1f)
                || MathF.Abs(fx) > MapRange || MathF.Abs(fz) > MapRange
                || MathF.Abs(fy) > 1000f;
            if (empty) continue;
            populated++;

            var entryBytes = new byte[SnapBytes];
            for (int b = 0; b < SnapBytes; b++) entryBytes[b] = *(byte*)(entry + b);

            byte[] subBytes = Array.Empty<byte>();
            nint subPtr = *(nint*)(entry + 0x18);
            bool subOk = (ulong)subPtr > 0x10000UL && IsReadable(subPtr, SubSnapBytes);
            if (subOk)
            {
                subBytes = new byte[SubSnapBytes];
                for (int b = 0; b < SubSnapBytes; b++) subBytes[b] = *(byte*)(subPtr + b);
            }

            var key = ((int)MathF.Round(fx), (int)MathF.Round(fz));
            snapshot[key] = (entryBytes, subBytes);

            float dx = fx - px, dz = fz - pz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            int steps = Math.Max(1, (int)MathF.Round(d / StepUnits));
            if (nearest == null || d < nearest.Value.d) nearest = (i, d, dx, dz);

            // e+0x4C was previously thought to be open/closed (1.0=unopened, 0=opened).
            // Disproved 2026-05-10: chest [3] showed e+0x4C=0 across all 3 scans
            // INCLUDING the scan right before the user opened it. The flag is a
            // type marker (full-mesh chest vs sparkly point pickup), not state.
            // Keeping the field in the log line as an observability hint.
            uint type4C = *(uint*)(entry + 0x4C);

            Log($"[FieldTracker]   [{i}] base=0x{(ulong)entry:X12} pos=({fx:F0},{fy:F0},{fz:F0}) d={d:F0} (~{steps} steps) sub=0x{(ulong)subPtr:X12} type4C=0x{type4C:X8}");
            for (int row = 0; row < HexBytes; row += 16)
            {
                var hex = new System.Text.StringBuilder();
                for (int col = 0; col < 16; col += 4)
                    hex.Append($"{*(uint*)(entry + row + col):X8} ");
                Log($"[FieldTracker]        e+0x{row:X2}: {hex.ToString().TrimEnd()}");
            }
            if (subOk)
            {
                for (int row = 0; row < SubHexBytes; row += 16)
                {
                    var hex = new System.Text.StringBuilder();
                    for (int col = 0; col < 16; col += 4)
                        hex.Append($"{*(uint*)(subPtr + row + col):X8} ");
                    Log($"[FieldTracker]        s+0x{row:X2}: {hex.ToString().TrimEnd()}");
                }
            }
            else
            {
                Log($"[FieldTracker]        (sub-struct unreadable)");
            }
        }

        Log($"[FieldTracker]   populated entries: {populated}/{Count}  (snapshot tracks {SnapBytes}B entry + {SubSnapBytes}B sub for diff)");

        if (_lastTreasureSnapshot != null)
        {
            Log("[FieldTracker]   diff vs last press (byte-level, matched by position):");
            int diffEntries = 0;
            foreach (var kv in snapshot)
            {
                if (!_lastTreasureSnapshot.TryGetValue(kv.Key, out var prev)) continue;
                var changes = new System.Text.StringBuilder();
                int changeCount = 0;
                int en = Math.Min(prev.entry.Length, kv.Value.entry.Length);
                for (int b = 0; b < en; b++)
                    if (kv.Value.entry[b] != prev.entry[b])
                    {
                        changes.Append($"e+0x{b:X2}: {prev.entry[b]:X2}→{kv.Value.entry[b]:X2}  ");
                        changeCount++;
                    }
                int sn = Math.Min(prev.sub.Length, kv.Value.sub.Length);
                for (int b = 0; b < sn; b++)
                    if (kv.Value.sub[b] != prev.sub[b])
                    {
                        changes.Append($"s+0x{b:X2}: {prev.sub[b]:X2}→{kv.Value.sub[b]:X2}  ");
                        changeCount++;
                    }
                if (changeCount == 0) continue;
                diffEntries++;
                Log($"[FieldTracker]     pos=({kv.Key.x},{kv.Key.z}) {changes.ToString().TrimEnd()}");
            }
            foreach (var prev in _lastTreasureSnapshot)
                if (!snapshot.ContainsKey(prev.Key))
                {
                    Log($"[FieldTracker]     pos=({prev.Key.x},{prev.Key.z}) DISAPPEARED");
                    diffEntries++;
                }
            foreach (var now in snapshot)
                if (!_lastTreasureSnapshot.ContainsKey(now.Key))
                {
                    Log($"[FieldTracker]     pos=({now.Key.x},{now.Key.z}) NEW");
                    diffEntries++;
                }
            if (diffEntries == 0) Log("[FieldTracker]     (no entries changed)");
        }
        _lastTreasureSnapshot = snapshot;

        if (nearest is { } n)
        {
            int steps = Math.Max(1, (int)MathF.Round(n.d / StepUnits));
            string dir = MathF.Abs(n.dz) > MathF.Abs(n.dx)
                ? (n.dz > 0 ? "north" : "south")
                : (n.dx > 0 ? "east"  : "west");
            Speech.Say($"{populated} treasure{(populated == 1 ? "" : "s")} on floor. Nearest {steps} step{(steps == 1 ? "" : "s")} {dir}.", true);
        }
        else
        {
            Speech.Say("No treasures on this floor.", true);
        }
    }

    /// <summary>
    /// Numpad 3: teleport-to-nearest-treasure prototype + canonical-position
    /// hunt. Writes the chest's XYZ to <c>*(sub_obj+0x10)+0x50/+0x54/+0x58</c>,
    /// waits 200 ms (~12 frames), then re-reads. Test 2026-05-10 confirmed
    /// the write LANDS in memory but the engine reverts it on the next physics
    /// tick — meaning a separate canonical/source position field is upstream.
    /// To find it: snapshot the xform struct + the sub_obj struct before AND
    /// after the write, then list every (X, Y, Z) triple that matched the
    /// player's old position in both snapshots. Those offsets are candidates
    /// for the canonical source. We then try writing to each candidate to
    /// find the one the engine actually reads from.
    /// </summary>
    private static unsafe void TeleportToNearestTreasure()
    {
        const long Base = 0x15E437730L;
        const int  Stride = 0x190;
        const int  Count = 16;
        const float MapRange = 50000f;
        const float MatchTol = 1.5f;
        const int  XformScan = 0x200;
        const int  SubScan = 0x1000;
        const int  Sub18Scan = 0x400;

        Log("[FieldTracker] === Numpad3 TELEPORT-TO-TREASURE TEST ===");

        if (CurrentMajor < 20 || CurrentMajor >= 240)
        {
            Speech.Say("Treasure teleport only works in dungeons.", true);
            return;
        }
        if (!IsReadable((nint)Base, Count * Stride))
        {
            Speech.Say("Treasure array unreadable.", true);
            return;
        }

        float px = LivePlayerX, pz = LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz))
        {
            Speech.Say("Player position unavailable.", true);
            return;
        }

        float bestDist = float.PositiveInfinity;
        float tx = 0, ty = 0, tz = 0;
        bool found = false;
        for (int i = 0; i < Count; i++)
        {
            nint entry = (nint)(Base + i * Stride);
            float fx = *(float*)(entry + 0x00);
            float fy = *(float*)(entry + 0x04);
            float fz = *(float*)(entry + 0x08);
            bool empty =
                !float.IsFinite(fx) || !float.IsFinite(fy) || !float.IsFinite(fz)
                || (MathF.Abs(fx) < 1f && MathF.Abs(fz) < 1f)
                || MathF.Abs(fx) > MapRange || MathF.Abs(fz) > MapRange
                || MathF.Abs(fy) > 1000f;
            if (empty) continue;
            float dx = fx - px, dz = fz - pz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < bestDist) { bestDist = d; tx = fx; ty = fy; tz = fz; found = true; }
        }
        if (!found)
        {
            Speech.Say("No treasures on this floor.", true);
            return;
        }

        var (sub, ok) = GetSubObjPtr();
        if (!ok)
        {
            Speech.Say("Could not resolve sub_obj pointer.", true);
            return;
        }
        nint xform = *(nint*)(sub + 0x10);
        if ((ulong)xform < 0x10000UL || !IsReadable(xform + 0x50, 12))
        {
            Speech.Say("Xform struct invalid.", true);
            return;
        }

        float pox = *(float*)(xform + 0x50);
        float poy = *(float*)(xform + 0x54);
        float poz = *(float*)(xform + 0x58);
        Log($"[FieldTracker]   target=({tx:F2},{ty:F2},{tz:F2})  before=({pox:F2},{poy:F2},{poz:F2})  dist={bestDist:F0}  xform=0x{(ulong)xform:X12}  sub_obj=0x{(ulong)sub:X12}");

        // Walk every 8-byte slot in the first 0x200 bytes of sub_obj. Any value
        // that looks like a pointer to readable memory is a candidate "child
        // struct" of sub_obj (xform at +0x10 and facing at +0x18 are two known
        // examples — there may be more). For each, snapshot 0x300 bytes; we'll
        // re-snapshot after the write to find the canonical position source.
        var children = new List<(string label, nint addr, byte[] before, byte[] after)>();
        const int ChildScan = 0x300;
        for (int subOff = 0; subOff <= 0x200; subOff += 8)
        {
            if (!IsReadable(sub + subOff, 8)) continue;
            nint p = *(nint*)(sub + subOff);
            if ((ulong)p < 0x10000UL || (ulong)p > 0x7FFFFFFFFFFFUL) continue;
            if (!IsReadable(p, ChildScan)) continue;
            children.Add(($"sub+0x{subOff:X2}", p, ReadBytes(p, ChildScan), Array.Empty<byte>()));
        }
        Log($"[FieldTracker]   discovered {children.Count} sub_obj child structs to scan");

        var xform1 = ReadBytes(xform, XformScan);
        var sub1   = ReadBytes(sub, SubScan);

        // Write only to xform+0x50/+0x58 (rendered position). Skip +0x90 since
        // last test showed engine writes it too — it's downstream, not source.
        // Keep Y (chest Y=3 was a per-entry constant, not a world height).
        *(float*)(xform + 0x50) = tx;
        *(float*)(xform + 0x58) = tz;
        Log($"[FieldTracker]   wrote xform+0x50,+0x58 only (kept Y={poy:F2}), sleeping 200 ms");

        Thread.Sleep(200);

        var xform2 = ReadBytes(xform, XformScan);
        var sub2   = ReadBytes(sub, SubScan);
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            children[i] = (c.label, c.addr, c.before, ReadBytes(c.addr, ChildScan));
        }

        float npx = *(float*)(xform + 0x50);
        float npy = *(float*)(xform + 0x54);
        float npz = *(float*)(xform + 0x58);
        bool reverted = MathF.Abs(npx - tx) > 5f || MathF.Abs(npz - tz) > 5f;
        Log($"[FieldTracker]   after_settle=({npx:F2},{npy:F2},{npz:F2})  write {(reverted ? "REVERTED" : "STUCK")}");

        int candidates = 0;
        ScanForOldPosition("xform", xform1, xform2, XformScan, pox, poy, poz, MatchTol, 0x50, ref candidates);
        ScanForOldPosition("sub", sub1, sub2, SubScan, pox, poy, poz, MatchTol, -1, ref candidates);
        foreach (var c in children)
            ScanForOldPosition($"{c.label}->", c.before, c.after, ChildScan, pox, poy, poz, MatchTol, -1, ref candidates);
        Log($"[FieldTracker]   total source candidates: {candidates}");

        int steps = Math.Max(1, (int)MathF.Round(bestDist / 30f));
        if (!reverted)
            Speech.Say($"Teleported to treasure {steps} steps away.", true);
        else
            Speech.Say($"Teleport reverted. {candidates} source candidates logged.", true);
    }

    private static unsafe byte[] ReadBytes(nint addr, int len)
    {
        var buf = new byte[len];
        if (!IsReadable(addr, len)) return buf;
        for (int b = 0; b < len; b++) buf[b] = *(byte*)(addr + b);
        return buf;
    }

    private static void ScanForOldPosition(string regionName, byte[] before, byte[] after, int len, float ox, float oy, float oz, float tol, int skipOff, ref int candidates)
    {
        for (int off = 0; off + 12 <= len; off += 4)
        {
            if (off == skipOff) continue;
            float bx = BitConverter.ToSingle(before, off);
            float by = BitConverter.ToSingle(before, off + 4);
            float bz = BitConverter.ToSingle(before, off + 8);
            if (!float.IsFinite(bx) || !float.IsFinite(by) || !float.IsFinite(bz)) continue;
            if (MathF.Abs(bx - ox) > tol || MathF.Abs(bz - oz) > tol) continue;
            if (MathF.Abs(by - oy) > 5f) continue;
            float ax = BitConverter.ToSingle(after, off);
            float az = BitConverter.ToSingle(after, off + 8);
            if (MathF.Abs(ax - ox) > tol || MathF.Abs(az - oz) > tol) continue;
            Log($"[FieldTracker]   cand {regionName}+0x{off:X4}: ({bx:F2},{by:F2},{bz:F2}) unchanged across write+settle");
            candidates++;
        }
    }

    // setup_movement = FUN_1405143B0 (Ghidra-discovered 2026-05-10)
    // Microsoft x64 fastcall — RCX=ctx, RDX=currentPos[3], R8=targetPos[3], R9=alignVec[3]|null
    // Writes currentPos to *(ctx+8)+0x50/+0x54/+0x58.
    private const long SetupMovementVA = 0x1405143B0L;
    private const long ActiveMovementCtxGlobal = 0x1462487C0L;

    // cancel_movement = FUN_14050ACF0 (Ghidra v3 / v5)
    // Microsoft x64 fastcall — RCX=xform_state (= sub_obj)
    // Unlinks the active walk node from per-actor list at [xform_state+0x100].
    // CRITICAL: without this, our writes get reverted within ~16 ms by the
    // mode-3/4 AI path-walker re-running setup_movement from xform_state+0x114.
    private const long CancelMovementVA = 0x14050ACF0L;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void SetupMovementDelegate(nint ctx, float* current, float* target, float* align);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CancelMovementDelegate(nint xformState);

    private static SetupMovementDelegate? _setupMovementCached;
    private static CancelMovementDelegate? _cancelMovementCached;

    private static SetupMovementDelegate GetSetupMovement()
    {
        if (_setupMovementCached == null)
            _setupMovementCached = Marshal.GetDelegateForFunctionPointer<SetupMovementDelegate>(new IntPtr(SetupMovementVA));
        return _setupMovementCached;
    }

    private static CancelMovementDelegate GetCancelMovement()
    {
        if (_cancelMovementCached == null)
            _cancelMovementCached = Marshal.GetDelegateForFunctionPointer<CancelMovementDelegate>(new IntPtr(CancelMovementVA));
        return _cancelMovementCached;
    }

    /// <summary>
    /// Numpad 4: comprehensive bypass teleport (Approach D, completed).
    /// Ghidra v5 (2026-05-11) concluded xform+0x50 IS the canonical position.
    /// The "writes get reverted" symptom is caused by the per-frame mode-3/4
    /// AI path-walker (FUN_1402D3790) re-running setup_movement each tick
    /// using xform_state+0x114..+0x11C (scripted path buffer) as currentPos.
    /// The fix:
    ///   1. Call FUN_14050ACF0(sub_obj) to cancel the in-flight movement queue
    ///   2. Apply BOTH dispatcher gates (sub+0x298=0, sub+4|=1) so the
    ///      dispatcher exits at its prologue every frame.
    ///   3. Call setup_movement(player_ctx, target, ...) — writes target to
    ///      xform+0x50/+0x54/+0x58.
    ///   4. Hold gates + re-cancel queue across 30 frames.
    ///   5. Restore gates and original anchor.
    /// </summary>
    private static unsafe void TestSetupMovementTeleport()
    {
        const long Base = 0x15E437730L;
        const int  Stride = 0x190;
        const int  Count = 16;
        const float MapRange = 50000f;
        const int  CtxSpeedActive = 0x98;
        const int  CtxSpeedMax = 0x9C;
        const float BoostSpeed = 100000f;
        const int  HoldFrames = 180;  // ~3 sec at 60 fps — enough time for the user to interact with the chest before the engine walks back

        Log("[FieldTracker] === Numpad4 SPEED-BOOST TELEPORT TEST ===");

        if (CurrentMajor < 20 || CurrentMajor >= 240)
        {
            Speech.Say("Teleport only works in dungeons.", true);
            return;
        }

        if (!IsReadable((nint)Base, Count * Stride))
        {
            Speech.Say("Treasure array unreadable.", true);
            return;
        }

        float ppx = LivePlayerX, ppz = LivePlayerZ;
        float ppy = LivePlayerY;
        if (float.IsNaN(ppx) || float.IsNaN(ppz))
        {
            Speech.Say("Player position unavailable.", true);
            return;
        }

        float bestDist = float.PositiveInfinity;
        float tx = 0, tz = 0;
        bool found = false;
        for (int i = 0; i < Count; i++)
        {
            nint entry = (nint)(Base + i * Stride);
            float fx = *(float*)(entry + 0x00);
            float fy = *(float*)(entry + 0x04);
            float fz = *(float*)(entry + 0x08);
            bool empty =
                !float.IsFinite(fx) || !float.IsFinite(fy) || !float.IsFinite(fz)
                || (MathF.Abs(fx) < 1f && MathF.Abs(fz) < 1f)
                || MathF.Abs(fx) > MapRange || MathF.Abs(fz) > MapRange
                || MathF.Abs(fy) > 1000f;
            if (empty) continue;
            float dx = fx - ppx, dz = fz - ppz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < bestDist) { bestDist = d; tx = fx; tz = fz; found = true; }
        }
        if (!found)
        {
            Speech.Say("No treasures on this floor.", true);
            return;
        }

        float ty = float.IsNaN(ppy) ? 192f : ppy;

        var (sub, ok) = GetSubObjPtr();
        if (!ok) { Speech.Say("Sub_obj unavailable.", true); return; }
        if (!IsReadable(sub + 0x10, 8)) { Speech.Say("Sub_obj+0x10 unreadable.", true); return; }
        nint liveXform = *(nint*)(sub + 0x10);
        if ((ulong)liveXform < 0x10000UL) { Speech.Say("Xform null.", true); return; }

        // The global ActiveMovementCtxGlobal often points to a non-player ctx
        // (whichever actor's movement was last processed). Find the actual
        // player's ctx by scanning ±64 KB around the live xform for any slot
        // holding a pointer == liveXform; that slot-minus-8 IS the player's
        // movement context (matches setup_movement's expected layout).
        nint ctx = 0;
        if (IsReadable((nint)ActiveMovementCtxGlobal, 8))
        {
            nint maybeMatch = *(nint*)ActiveMovementCtxGlobal;
            if ((ulong)maybeMatch > 0x10000UL && IsReadable(maybeMatch + 8, 8)
                && *(nint*)(maybeMatch + 8) == liveXform)
                ctx = maybeMatch;
        }
        if (ctx == 0)
        {
            const long Window = 0x10000;
            nint searchStart = (nint)((ulong)((long)liveXform - Window) & ~7UL);
            nint searchEnd = (nint)((long)liveXform + Window);
            for (nint a = searchStart; a < searchEnd; a += 8)
            {
                if (!IsReadable(a, 8)) continue;
                if (*(nint*)a == liveXform) { ctx = a - 8; break; }
            }
        }
        if (ctx == 0)
        {
            Speech.Say("Could not find player movement ctx.", true);
            return;
        }

        // Save state for restore
        if (!IsReadable(sub + 0x298, 8) || !IsReadable(sub + 4, 1))
        {
            Speech.Say("Gate fields unreadable.", true);
            return;
        }
        long oldAnchor = *(long*)(sub + 0x298);
        byte oldGateByte = *(byte*)(sub + 4);
        Log($"[FieldTracker]   target=({tx:F2},{ty:F2},{tz:F2})  before=({ppx:F2},{ppy:F2},{ppz:F2})  dist={bestDist:F0}");
        Log($"[FieldTracker]   playerCtx=0x{(ulong)ctx:X12}  liveXform=0x{(ulong)liveXform:X12}  oldAnchor=0x{oldAnchor:X16}  oldGateByte=0x{oldGateByte:X2}");

        SetupMovementDelegate setup;
        try { setup = GetSetupMovement(); }
        catch (Exception ex)
        {
            Log($"[FieldTracker]   delegate bind failed: {ex.Message}");
            Speech.Say("Could not bind setup_movement.", true);
            return;
        }

        // Snapshot the path buffer (first 12 bytes at sub+0x114) for restore.
        // CancelMovement (FUN_14050ACF0) crashed with AVE — skip the function
        // call and use memory writes only. Per agent v5, the AI path-walker
        // re-runs setup_movement each tick with currentPos sourced from
        // xform_state+0x114..+0x11C. Writing OUR target into that slot makes
        // the re-run keep writing target to xform+0x50 instead of an old
        // waypoint.
        if (!IsReadable(sub + 0x114, 12))
        {
            Speech.Say("Path buffer unreadable.", true);
            return;
        }
        float oldPathX = *(float*)(sub + 0x114);
        float oldPathY = *(float*)(sub + 0x118);
        float oldPathZ = *(float*)(sub + 0x11C);
        Log($"[FieldTracker]   oldPath=({oldPathX:F2},{oldPathY:F2},{oldPathZ:F2})");

        // Step 1: apply both dispatcher gates so the per-frame mover exits at
        // its prologue every frame.
        *(long*)(sub + 0x298) = 0;
        *(byte*)(sub + 4) = (byte)(oldGateByte | 1);

        // Step 2: pre-fill the path buffer with our target so any re-engage
        // also targets the chest.
        *(float*)(sub + 0x114) = tx;
        *(float*)(sub + 0x118) = ty;
        *(float*)(sub + 0x11C) = tz;

        // Step 3: kick off the move (writes target to xform+0x50/+0x54/+0x58).
        float* curr = stackalloc float[3] { tx, ty, tz };
        float* tgt  = stackalloc float[3] { tx + 0.001f, ty, tz };
        try { setup(ctx, curr, tgt, null); Log("[FieldTracker]   setup_movement(player_ctx, target, ...) called"); }
        catch (Exception ex) { Log($"[FieldTracker]   setup threw: {ex.Message}"); }

        // Announce immediately so the user can press Enter during the hold.
        int announceSteps = Math.Max(1, (int)MathF.Round(bestDist / 30f));
        Speech.Say($"At chest, {announceSteps} steps away. Press Enter now.", true);

        // Step 4: hold gates + path buffer across the engine's frame loop.
        for (int i = 0; i < HoldFrames; i++)
        {
            Thread.Sleep(16);
            *(long*)(sub + 0x298) = 0;
            *(byte*)(sub + 4) = (byte)(*(byte*)(sub + 4) | 1);
            *(float*)(sub + 0x114) = tx;
            *(float*)(sub + 0x118) = ty;
            *(float*)(sub + 0x11C) = tz;
        }

        float npx = LivePlayerX, npy = LivePlayerY, npz = LivePlayerZ;
        Log($"[FieldTracker]   after_hold=({npx:F2},{npy:F2},{npz:F2})  delta=({npx - ppx:F2},{npz - ppz:F2})");

        // Step 5: restore gate state + path buffer so normal gameplay resumes.
        *(long*)(sub + 0x298) = oldAnchor;
        *(byte*)(sub + 4) = oldGateByte;
        *(float*)(sub + 0x114) = oldPathX;
        *(float*)(sub + 0x118) = oldPathY;
        *(float*)(sub + 0x11C) = oldPathZ;
        Log($"[FieldTracker]   restored anchor=0x{oldAnchor:X16} gateByte=0x{oldGateByte:X2} path=({oldPathX:F0},{oldPathY:F0},{oldPathZ:F0})");


        // Already announced before the hold; no further speech needed.
        // Log a final summary for diagnostics.
        bool atTarget = MathF.Abs(npx - tx) < 50f && MathF.Abs(npz - tz) < 50f;
        Log($"[FieldTracker]   final: atTarget={atTarget}");
    }

    /// <summary>
    /// Numpad 7: diagnostic. Read the player's current (X, Z) from xform+0x50,
    /// then scan wide memory regions for float pairs matching it. The matches
    /// are candidates for "where the canonical player position lives". We've
    /// confirmed xform+0x50 is downstream — the actual canonical source is
    /// somewhere we haven't searched yet. This finds it.
    /// </summary>
    private static unsafe void ScanForPlayerPositionFields()
    {
        const float Tol = 5f;
        const float MinPos = 50f;
        const int  MainScan = 0x40000;  // 256 KB forward from each base
        const int  ChildScan = 0x2000;  // 8 KB per sub_obj pointer-child

        Log("[FieldTracker] === Numpad7 PLAYER POSITION FIELD SCAN ===");
        float px = LivePlayerX, pz = LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz))
        {
            Speech.Say("Player position unavailable.", true);
            return;
        }
        if (MathF.Abs(px) < MinPos || MathF.Abs(pz) < MinPos)
        {
            Speech.Say("Player too close to origin — move away first.", true);
            return;
        }
        Log($"[FieldTracker]   target (X,Z)=({px:F2},{pz:F2})  tol={Tol}");

        var (sub, ok) = GetSubObjPtr();
        if (!ok) { Speech.Say("Sub_obj unavailable.", true); return; }
        if (_fieldObjPtr == null) { Speech.Say("fieldObj global unavailable.", true); return; }
        nint fobj = (nint)(*_fieldObjPtr);
        nint xform = IsReadable(sub + 0x10, 8) ? *(nint*)(sub + 0x10) : 0;

        Log($"[FieldTracker]   regions: fieldObj=0x{(ulong)fobj:X12}, sub_obj=0x{(ulong)sub:X12}, xform=0x{(ulong)xform:X12}");

        int total = 0;
        // Forward sweeps (same as before)
        ScanRegion("fieldObj→",  fobj,  MainScan, px, pz, Tol, ref total);
        ScanRegion("sub_obj→",   sub,   MainScan, px, pz, Tol, ref total);
        if (xform != 0) ScanRegion("xform→", xform, MainScan, px, pz, Tol, ref total);

        // Backward sweeps — scan the memory BEFORE each base. The earlier
        // scan found the canonical isn't forward from any of these. The
        // allocation pool likely extends backward too. Use SafeScanBackward
        // which walks page-by-page to avoid AVE on unmapped boundaries.
        ScanRegionBackward("←fieldObj", fobj,  MainScan, px, pz, Tol, ref total);
        ScanRegionBackward("←sub_obj",  sub,   MainScan, px, pz, Tol, ref total);
        if (xform != 0) ScanRegionBackward("←xform", xform, MainScan, px, pz, Tol, ref total);

        // Sub_obj direct pointer-children
        for (int off = 0; off <= 0x100; off += 8)
        {
            if (!IsReadable(sub + off, 8)) continue;
            nint p = *(nint*)(sub + off);
            if ((ulong)p < 0x10000UL) continue;
            if (!IsRangeReadable(p, ChildScan)) continue;
            ScanRegion($"sub+0x{off:X2}->", p, ChildScan, px, pz, Tol, ref total);
        }

        Log($"[FieldTracker]   total candidate hits: {total}");
        Speech.Say($"Position scan: {total} candidate hits logged.", true);
    }

    // IsReadable only checks the page containing addr — a multi-byte read that
    // spans a page boundary into unmapped memory will AVE despite IsReadable
    // saying true. This checks both ends AND every page boundary in between.
    private static bool IsRangeReadable(nint addr, int size)
    {
        if (size <= 0) return true;
        if (!IsReadable(addr, 1)) return false;
        nint end = addr + size - 1;
        if (!IsReadable(end, 1)) return false;
        nint pageMask = ~(nint)0xFFF;
        nint firstPage = addr & pageMask;
        nint lastPage = end & pageMask;
        for (nint p = firstPage + 0x1000; p <= lastPage; p += 0x1000)
            if (!IsReadable(p, 1)) return false;
        return true;
    }

    // Scan a region going BACKWARD from baseAddr (down to baseAddr - len).
    // Walks pages backward, skipping unreadable pages without aborting.
    private static unsafe void ScanRegionBackward(string name, nint baseAddr, int len, float px, float pz, float tol, ref int totalHits)
    {
        if ((ulong)baseAddr < 0x10000UL) return;
        int hits = 0;
        long start = (long)baseAddr;
        long end = (long)baseAddr - len;
        if (end < 0x10000) end = 0x10000;
        for (long addr = start - 12; addr >= end; addr -= 4)
        {
            nint a = (nint)addr;
            if (!IsRangeReadable(a, 12))
            {
                long pageBase = addr & ~0xFFFL;
                addr = pageBase - 4;     // jump to bottom of prev page; loop's -=4 brings us into it
                continue;
            }
            float fx = *(float*)a;
            float fz = *(float*)(a + 8);
            if (!float.IsFinite(fx) || !float.IsFinite(fz)) continue;
            if (MathF.Abs(fx - px) > tol) continue;
            if (MathF.Abs(fz - pz) > tol) continue;
            float fy = *(float*)(a + 4);
            Log($"[FieldTracker]   hit {name}-0x{(start - addr):X4}: ({fx:F2},{fy:F2},{fz:F2})  abs=0x{(ulong)a:X12}");
            hits++;
            totalHits++;
            if (hits >= 100) { Log($"[FieldTracker]   ({name} hit cap reached, stopping)"); break; }
        }
    }

    private static unsafe void ScanRegion(string name, nint baseAddr, int len, float px, float pz, float tol, ref int totalHits)
    {
        if ((ulong)baseAddr < 0x10000UL) return;
        int hits = 0;
        // Step 4 bytes, but verify the 12-byte window is fully readable each
        // time (the spam of IsRangeReadable is cheap and avoids AVE crashes
        // when the scan crosses an unmapped page).
        for (int off = 0; off + 12 <= len; off += 4)
        {
            if (!IsRangeReadable(baseAddr + off, 12))
            {
                // Skip to next page boundary
                long stepUp = 0x1000 - ((long)(baseAddr + off) & 0xFFF);
                off += (int)stepUp - 4;     // -4 because the for-loop adds 4
                continue;
            }
            float fx = *(float*)(baseAddr + off);
            float fz = *(float*)(baseAddr + off + 8);
            if (!float.IsFinite(fx) || !float.IsFinite(fz)) continue;
            if (MathF.Abs(fx - px) > tol) continue;
            if (MathF.Abs(fz - pz) > tol) continue;
            float fy = *(float*)(baseAddr + off + 4);
            Log($"[FieldTracker]   hit {name}+0x{off:X4}: ({fx:F2},{fy:F2},{fz:F2})  abs=0x{(ulong)(baseAddr + off):X12}");
            hits++;
            totalHits++;
            if (hits >= 100) { Log($"[FieldTracker]   ({name} hit cap reached, stopping)"); break; }
        }
    }

    /// <summary>
    /// Numpad 8: resolve the floor entry-point table (Ghidra v6 2026-05-14) and
    /// read entry 0's XYZ. User compares the announced coords to the visual
    /// spawn point of the floor. If they match, the chain is correct and we
    /// can next try writing chest XYZ to entry 0 + triggering CALL_DUNGEON.
    /// Chain:
    ///   *(0x140AA8098) → +0x08 → walk(+0x150) until node with [+0x1C0]!=0
    ///                  → +0x1C0 → +0x28 → +0xF80 → +0x08 + 0x10 + N*0x70
    ///                  → +0x30 (X), +0x34 (Y), +0x38 (Z)
    /// </summary>
    private const long SceneRootGlobal = 0x140AA8098L;

    // A value is "plausibly a pointer" if it falls in the heap-mappable range
    // AND is 4-byte aligned (some Atlus structs are 4-aligned, not 8-aligned).
    // Filters out small flag values and tag/ID strings.
    private static bool LooksLikePointer(nint p)
    {
        ulong u = (ulong)p;
        if (u < 0x10000UL) return false;
        if (u > 0x00007FFFFFFFFFFFUL) return false;
        if ((u & 3UL) != 0) return false;
        return true;
    }

    // Probe: given a candidate per-area struct base, check whether
    // base+0xF80 → +0x08 → +0x10 leads to a struct whose +0x30 is a finite
    // float (entry 0's X). Returns (arrayBase, header) on success or (0, 0).
    private static unsafe (nint baseAddr, nint header) ProbeAreaStruct(nint candidate)
    {
        if (!LooksLikePointer(candidate) || !IsReadable(candidate + 0xF80, 8)) return (0, 0);
        nint hdr = *(nint*)(candidate + 0xF80);
        if (!LooksLikePointer(hdr) || !IsReadable(hdr + 0x08, 8)) return (0, 0);
        nint ab = *(nint*)(hdr + 0x08);
        if (!LooksLikePointer(ab) || !IsReadable(ab + 0x10 + 0x40, 4)) return (0, 0);
        float testX = *(float*)(ab + 0x10 + 0x30);
        float testY = *(float*)(ab + 0x10 + 0x34);
        float testZ = *(float*)(ab + 0x10 + 0x38);
        if (!float.IsFinite(testX) || !float.IsFinite(testY) || !float.IsFinite(testZ)) return (0, 0);
        // entry 0 should be sane world coords — not all zero, not absurdly large
        if (testX == 0f && testY == 0f && testZ == 0f) return (0, 0);
        if (MathF.Abs(testX) > 50000f || MathF.Abs(testZ) > 50000f) return (0, 0);
        return (ab + 0x10, hdr);
    }

    private static unsafe (nint baseAddr, nint headerAddr) ResolveEntryArrayBase()
    {
        // Step 1: probe the three obvious candidates directly.
        nint fobj = (_fieldObjPtr != null) ? (nint)(*_fieldObjPtr) : (nint)0;
        nint sceneRoot = IsReadable((nint)SceneRootGlobal, 8) ? *(nint*)SceneRootGlobal : (nint)0;
        nint firstNode = (LooksLikePointer(sceneRoot) && IsReadable(sceneRoot + 0x08, 8)) ? *(nint*)(sceneRoot + 0x08) : (nint)0;
        Log($"[FieldTracker]   candidates: fieldObj=0x{(ulong)fobj:X12} sceneRoot=0x{(ulong)sceneRoot:X12} firstNode=0x{(ulong)firstNode:X12}");

        foreach (var (label, cand) in new[] { ("fieldObj", fobj), ("sceneRoot", sceneRoot), ("firstNode", firstNode) })
        {
            var (ab, hdr) = ProbeAreaStruct(cand);
            if (ab != 0)
            {
                Log($"[FieldTracker]   PROBE HIT via {label}: arrayBase=0x{(ulong)ab:X12} header=0x{(ulong)hdr:X12}");
                return (ab, hdr);
            }
        }

        // Step 2: scan every pointer field in firstNode (first 0x200 bytes,
        // 8-byte stride) and probe each as a candidate area struct.
        if (LooksLikePointer(firstNode) && IsReadable(firstNode, 0x200))
        {
            for (int off = 0; off < 0x200; off += 8)
            {
                if (!IsReadable(firstNode + off, 8)) continue;
                nint cand = *(nint*)(firstNode + off);
                if (!LooksLikePointer(cand)) continue;
                var (ab, hdr) = ProbeAreaStruct(cand);
                if (ab != 0)
                {
                    Log($"[FieldTracker]   PROBE HIT via firstNode+0x{off:X3} -> 0x{(ulong)cand:X12}: arrayBase=0x{(ulong)ab:X12} header=0x{(ulong)hdr:X12}");
                    return (ab, hdr);
                }
            }
        }

        // Step 3: scan every pointer field in fieldObj (first 0x200 bytes) too.
        if (LooksLikePointer(fobj) && IsReadable(fobj, 0x200))
        {
            for (int off = 0; off < 0x200; off += 8)
            {
                if (!IsReadable(fobj + off, 8)) continue;
                nint cand = *(nint*)(fobj + off);
                if (!LooksLikePointer(cand)) continue;
                var (ab, hdr) = ProbeAreaStruct(cand);
                if (ab != 0)
                {
                    Log($"[FieldTracker]   PROBE HIT via fieldObj+0x{off:X3} -> 0x{(ulong)cand:X12}: arrayBase=0x{(ulong)ab:X12} header=0x{(ulong)hdr:X12}");
                    return (ab, hdr);
                }
            }
        }

        Log("[FieldTracker]   PROBE: no candidate has +0xF80 leading to a valid entry table. Dumping firstNode for offline analysis.");
        if (LooksLikePointer(firstNode) && IsReadable(firstNode, 0x200))
        {
            for (int off = 0; off < 0x200; off += 0x40)
            {
                var sb = new System.Text.StringBuilder($"[FieldTracker]     +0x{off:X3}: ");
                for (int q = 0; q < 8; q++)
                {
                    nint v = *(nint*)(firstNode + off + q * 8);
                    string mark = LooksLikePointer(v) ? "P" : ".";
                    sb.Append($"{(ulong)v:X12}{mark} ");
                }
                Log(sb.ToString());
            }
        }

        return (0, 0);
    }

    private static unsafe void ReadFloorEntryTable()
    {
        Log("[FieldTracker] === Numpad8 FLOOR ENTRY TABLE READ ===");
        var (firstEntry, header) = ResolveEntryArrayBase();
        if (firstEntry == 0)
        {
            Speech.Say("Entry table chain resolution failed.", true);
            Log("[FieldTracker]   chain resolution returned (0, 0)");
            return;
        }

        Log($"[FieldTracker]   entryArrayFirst=0x{(ulong)firstEntry:X12}  header=0x{(ulong)header:X12}");

        // Read first 8 entries (more is unlikely; floors usually have 1-4 entries)
        int validCount = 0;
        for (int idx = 0; idx < 8; idx++)
        {
            nint entryAddr = firstEntry + idx * 0x70;
            if (!IsReadable(entryAddr + 0x30, 12)) break;
            float ex = *(float*)(entryAddr + 0x30);
            float ey = *(float*)(entryAddr + 0x34);
            float ez = *(float*)(entryAddr + 0x38);
            if (!float.IsFinite(ex) || !float.IsFinite(ez)) break;
            // Stop at all-zero entry (likely past end)
            if (idx > 0 && ex == 0f && ey == 0f && ez == 0f) break;
            Log($"[FieldTracker]   entry[{idx}] @0x{(ulong)entryAddr:X12} pos=({ex:F2},{ey:F2},{ez:F2})");
            validCount++;
        }

        if (validCount == 0)
        {
            Speech.Say("No valid entries read.", true);
            return;
        }

        // Speak entry 0 specifically for the user to verify
        float e0x = *(float*)(firstEntry + 0x30);
        float e0y = *(float*)(firstEntry + 0x34);
        float e0z = *(float*)(firstEntry + 0x38);
        float ppx = LivePlayerX, ppz = LivePlayerZ;
        Log($"[FieldTracker]   player_now=({ppx:F0},?,{ppz:F0})  entry0=({e0x:F0},{e0y:F0},{e0z:F0})");
        Speech.Say($"Entry 0: X {(int)e0x}, Z {(int)e0z}. {validCount} entries total.", true);
    }

    /// <summary>
    /// F11: probe the 4 bytes immediately after the CHECK flag (0x1411BC7F8).
    /// Hypothesis: that slot holds a pointer to the currently-active interactable,
    /// which the game uses to dispatch the interact button. If so, following that
    /// pointer gives us per-interactable data (position, type, name) that the
    /// global CHECK flag alone can't tell us.
    /// Press with "Check!!" visible at a known interactable (Daidara, Save Point,
    /// an NPC). Compare the dumps across multiple interactables.
    /// </summary>
    private static unsafe void ProbeInteractablePointer()
    {
        // Addresses confirmed via Ghidra decompile of FUN_1402EDB70:
        //   0x1411BC720 = real CHECK flag (set to 1 when a matching interactable
        //                 is within range — the 0x1411BC7F4 we used before is a
        //                 separate/mirror value)
        //   0x1411BC724 = active-slot count
        //   0x1411BC728 = array of 8-byte interactable pointers
        //   0x1411BC7B0 = array of 0x38-byte slot info structs. Each slot has:
        //                   +0x00 active flag
        //                   +0x08 pointer to registry entry
        //                   +0x10 interactable INDEX  (<-- unique per-interactable ID)
        const long RealCheckAddr = 0x1411BC720L;
        const long ActiveCountAddr = 0x1411BC724L;
        const long PointerArrayBase = 0x1411BC728L;
        const long SlotInfoBaseAddr = 0x1411BC7B0L;

        int realFlag = IsReadable((nint)RealCheckAddr, 4) ? *(int*)RealCheckAddr : -1;
        int slotCount = IsReadable((nint)ActiveCountAddr, 4) ? *(int*)ActiveCountAddr : -1;
        nint ptrArray = IsReadable((nint)PointerArrayBase, 8) ? *(nint*)PointerArrayBase : 0;
        nint slotInfo = IsReadable((nint)SlotInfoBaseAddr, 8) ? *(nint*)SlotInfoBaseAddr : 0;

        uint capIdx = InteractableIndexCaptured;
        nint capEntry = InteractableEntryCaptured;
        ulong capHits = InteractableCaptureHits;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[FieldTracker] F11 | RealCHECK@0x{RealCheckAddr:X}={realFlag}  slotCount@0x{ActiveCountAddr:X}={slotCount}");
        sb.AppendLine($"  ptrArrayBase=0x{ptrArray:X}  slotInfoBase=0x{slotInfo:X}");
        sb.AppendLine($"  AsmHook captured: index={capIdx}  entry=0x{capEntry:X}  hits={capHits}");

        float px = PlayerX2DLive, pz = PlayerZ2DLive;
        float fx = ForwardX, fz = ForwardZ;
        sb.AppendLine($"  player=({px:F2},{pz:F2}) forward=({fx:F3},{fz:F3})");

        // Iterate slot info array: up to slotCount entries, each 0x38 bytes.
        if (slotInfo != 0 && slotCount > 0 && slotCount < 64)
        {
            for (int i = 0; i < slotCount; i++)
            {
                nint slotAddr = slotInfo + i * 0x38;
                if (!IsReadable(slotAddr, 0x38)) { sb.AppendLine($"  slot {i}: unreadable"); continue; }
                int active = *(int*)slotAddr;
                nint entryPtr = *(nint*)(slotAddr + 8);
                uint interactableIndex = *(uint*)(slotAddr + 0x10);
                sb.AppendLine($"  slot {i}: active={active}  entryPtr=0x{entryPtr:X}  INDEX={interactableIndex}");

                // Peek at the registry entry — 0x20 bytes per entry
                if (entryPtr != 0 && IsReadable(entryPtr, 0x20))
                {
                    sb.Append($"    registry bytes:");
                    for (int k = 0; k < 0x20; k += 2)
                        sb.Append($" {(*(ushort*)(entryPtr + k)):X4}");
                    sb.AppendLine();
                }
            }
        }

        // Also keep the old dump for comparison — the legacy window-based probe.
        const long WindowBase = 0x1411BC6F4L;

        int flag = IsReadable((nint)(WindowBase + 0x100), 4) ? *(int*)(WindowBase + 0x100) : -1;

        // Read likely identifier/category fields
        int v_C4  = IsReadable((nint)(WindowBase + 0xC4),  4) ? *(int*)(WindowBase + 0xC4)  : 0;
        int v_CC  = IsReadable((nint)(WindowBase + 0xCC),  4) ? *(int*)(WindowBase + 0xCC)  : 0;
        uint id110 = IsReadable((nint)(WindowBase + 0x110), 4) ? *(uint*)(WindowBase + 0x110) : 0;

        // The three per-interactable pointer candidates observed at +0xD4/+0xDC/+0xE4.
        // In the captured data they are 32-bit heap addresses (high bits zero).
        uint p_D4 = IsReadable((nint)(WindowBase + 0xD4), 4) ? *(uint*)(WindowBase + 0xD4) : 0;
        uint p_DC = IsReadable((nint)(WindowBase + 0xDC), 4) ? *(uint*)(WindowBase + 0xDC) : 0;
        uint p_E4 = IsReadable((nint)(WindowBase + 0xE4), 4) ? *(uint*)(WindowBase + 0xE4) : 0;

        sb.AppendLine($"  legacy window: CHECK(mirror)@0x{WindowBase+0x100:X}={flag} +0xC4={v_C4} +0xCC={v_CC} +0x110=0x{id110:X8}");
        sb.AppendLine($"  legacy ptrs  : +0xD4=0x{p_D4:X8} +0xDC=0x{p_DC:X8} +0xE4=0x{p_E4:X8}");

        void DumpTarget(string label, uint ptr32)
        {
            if (ptr32 == 0) { sb.AppendLine($"  {label}: (null)"); return; }
            nint ptr = (nint)ptr32;
            if (!IsReadable(ptr, 0x100)) { sb.AppendLine($"  {label} @ 0x{ptr32:X}: not readable"); return; }
            sb.AppendLine($"  {label} @ 0x{ptr32:X}:");
            for (int row = 0; row < 0x100; row += 0x20)
            {
                sb.Append($"    +0x{row:X3}:");
                for (int col = 0; col < 0x20; col += 4)
                    sb.Append($" {(*(uint*)(ptr + row + col)):X8}");
                sb.AppendLine();
            }
            // Any plausible floats in the first 0x80 (positions tend to live in there)
            sb.Append($"    floats +0x00..+0x80:");
            int hits = 0;
            for (int i = 0; i < 0x80; i += 4)
            {
                float f = *(float*)(ptr + i);
                if (!float.IsNaN(f) && !float.IsInfinity(f) && MathF.Abs(f) > 0.01f && MathF.Abs(f) < 1e5f)
                {
                    sb.Append($" +{i:X2}={f:F2}");
                    if (++hits >= 16) break;
                }
            }
            sb.AppendLine();
        }

        DumpTarget("follow +0xD4", p_D4);
        DumpTarget("follow +0xDC", p_DC);
        DumpTarget("follow +0xE4", p_E4);

        Log(sb.ToString().TrimEnd());

        string spoken = $"CHECK {realFlag}, captured index {capIdx}, hits {capHits}.";
        Speech.Say(spoken, true);
    }

    // Scroll Lock: dereference 0x1411ACCE0 (a short** slot) and hex-dump the table it points at.
    // First dump confirmed 0x1411ACCE0 itself holds a pointer, not the registry.
    // Ghidra (FUN_1402EDB70): entries stride 0x20 bytes (0x10 shorts); cells with
    // first short == -1 are group separators. Pure read — no patch, cannot crash.
    private static unsafe void DumpInteractableRegistry()
    {
        const long RegistrySlot = 0x1411ACCE0L;
        const int  EntrySize    = 0x20;
        const int  MaxEntries   = 2048;
        const int  StopAfterSeparators = 4;  // fewer than last time — groups should be short

        Log($"[FieldTracker] ScrollLock | reading registry pointer from slot @0x{RegistrySlot:X}");

        var sb = new System.Text.StringBuilder();
        if (!IsReadable((nint)RegistrySlot, 8))
        {
            sb.AppendLine("  slot itself unreadable, aborting.");
            Log(sb.ToString().TrimEnd());
            return;
        }

        nint regPtr = *(nint*)RegistrySlot;
        sb.AppendLine($"  registry pointer = 0x{regPtr:X}  (current area: major={CurrentMajor} minor={CurrentMinor})");

        // Also dump 64 bytes of the slot itself to see neighbouring globals
        sb.AppendLine($"  slot+neighbourhood @0x{RegistrySlot:X}:");
        for (int row = 0; row < 4; row++)
        {
            nint ra = (nint)RegistrySlot + row * 16;
            if (!IsReadable(ra, 16)) { sb.AppendLine($"    +0x{row*16:X2}: unreadable"); continue; }
            sb.Append($"    +0x{row*16:X2}:");
            for (int k = 0; k < 16; k += 2)
                sb.Append($" {(*(ushort*)(ra + k)):X4}");
            sb.AppendLine();
        }

        if (regPtr == 0 || !IsReadable(regPtr, EntrySize))
        {
            sb.AppendLine("  registry pointer is null or unreadable, aborting walk.");
            Log(sb.ToString().TrimEnd());
            return;
        }

        int consecutiveSeps = 0;
        int entriesPrinted = 0;
        int i = 0;
        for (; i < MaxEntries; i++)
        {
            nint addr = regPtr + i * EntrySize;
            if (!IsReadable(addr, EntrySize))
            {
                sb.AppendLine($"  #{i,4} @0x{addr:X}: UNREADABLE — stopping walk");
                break;
            }

            short s0 = *(short*)addr;
            if (s0 == -1)
            {
                consecutiveSeps++;
                sb.AppendLine($"  #{i,4} @0x{addr:X}: ---- group separator ----");
                if (consecutiveSeps >= StopAfterSeparators) break;
                continue;
            }
            consecutiveSeps = 0;
            entriesPrinted++;

            sb.Append($"  #{i,4} @0x{addr:X}:");
            for (int k = 0; k < EntrySize; k += 2)
                sb.Append($" {(*(ushort*)(addr + k)):X4}");

            ushort s1    = *(ushort*)(addr + 0x02);
            uint   u0E   = *(uint*)  (addr + 0x0E);
            short  s16   = *(short*) (addr + 0x16);
            short  s1E   = *(short*) (addr + 0x1E);
            sb.Append($"   | s0={s0} s2={s1} u0E=0x{u0E:X8} s16={s16} s1E={s1E}");
            sb.AppendLine();

            if (entriesPrinted >= 256) { sb.AppendLine("  … cap 256 entries reached, stopping"); break; }
        }

        sb.AppendLine($"  walked {i} cells, {entriesPrinted} non-separator entries printed.");
        Log(sb.ToString().TrimEnd());
        Speech.Say($"Registry dumped, {entriesPrinted} entries.", true);
    }

    /// <summary>
    /// F10: find the player's facing/yaw by diffing several structs between two poses.
    /// Structs scanned: sub_obj itself, *(sub_obj+0x10), *(sub_obj+0x18), and fieldObj.
    /// First press  = snapshot A (e.g. after walking north).
    /// Second press = snapshot B (e.g. after walking south). Reports the top candidate
    ///                per struct: floats that changed and sit in a plausible range
    ///                (unit-vector component, radians, or degrees).
    /// Third press  = resets and takes a fresh snapshot A.
    /// </summary>
    private const int SubObjEnd = 0x3C0;
    private const int LinkedEnd = 0x400; // size of scan for *(sub_obj+0x10) and *(sub_obj+0x18)
    private const int FieldObjEnd = 0x200;

    private sealed class FacingSnapshot
    {
        public (float x, float z) Pos;
        public float[]? SubObj;
        public float[]? SubLink10;
        public float[]? SubLink18;
        public float[]? FieldObjBuf;
    }
    private static FacingSnapshot? _facingA;

    private static unsafe float[]? SnapshotFloats(nint basePtr, int start, int end)
    {
        if (basePtr == 0) return null;
        if (!IsReadable(basePtr + start, end - start)) return null;
        int n = (end - start) / 4;
        var arr = new float[n];
        for (int i = 0; i < n; i++) arr[i] = *(float*)(basePtr + start + i * 4);
        return arr;
    }

    /// <summary>Scores and logs the top changed float offsets in a struct.</summary>
    private static (int bestOff, float bestA, float bestB) ScoreStruct(
        System.Text.StringBuilder sb, string label, float[]? prev, float[]? cur, int start,
        params int[] skipOffsets)
    {
        sb.AppendLine($"  [{label}]");
        if (prev == null || cur == null) { sb.AppendLine("    (unavailable)"); return (-1, 0, 0); }
        if (prev.Length != cur.Length) { sb.AppendLine("    (size mismatch)"); return (-1, 0, 0); }

        int bestOff = -1; float bestScore = 0f, bestA = 0f, bestB = 0f;
        int printed = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            int off = start + i * 4;
            bool skip = false; foreach (var s in skipOffsets) if (off == s) { skip = true; break; }
            if (skip) continue;

            float a = prev[i], b = cur[i];
            if (float.IsNaN(a) || float.IsNaN(b) || float.IsInfinity(a) || float.IsInfinity(b)) continue;
            float aMag = MathF.Abs(a), bMag = MathF.Abs(b);
            if (aMag > 1e10f || bMag > 1e10f) continue;
            if ((aMag > 0 && aMag < 1e-20f) || (bMag > 0 && bMag < 1e-20f)) continue;
            float delta = b - a;
            if (MathF.Abs(delta) < 1e-4f) continue;

            bool inUnitRange = aMag <= 1.01f && bMag <= 1.01f;
            bool inYawRange  = aMag < 6.3f && bMag < 6.3f;
            bool inDegRange  = aMag < 361f && bMag < 361f;
            string note =
                inUnitRange ? "dir vec? (unit)"
                : inYawRange ? "yaw? (radians)"
                : inDegRange ? "yaw? (degrees?)"
                : "";

            if (printed++ < 24) // cap per struct to keep the log readable
                sb.AppendLine($"    +0x{off:X3}  {a,12:F4}  {b,12:F4}  {delta,12:F4}  {note}");

            float score =
                (inUnitRange ? 4f : inYawRange ? 2f : inDegRange ? 1f : 0f) * MathF.Abs(delta);
            if (score > bestScore) { bestScore = score; bestOff = off; bestA = a; bestB = b; }
        }
        if (printed == 0) sb.AppendLine("    (no plausible float changed)");
        if (bestOff >= 0) sb.AppendLine($"    TOP: +0x{bestOff:X3}  A={bestA:F4}  B={bestB:F4}");
        return (bestOff, bestA, bestB);
    }

    private static unsafe void ProbePlayerTransform()
    {
        var (sub, ok) = GetSubObjPtr();
        if (!ok) { Log("[FieldTracker] F10 | sub_obj not available"); return; }

        // Pointer field-objects reachable from sub_obj
        nint sub10 = 0, sub18 = 0;
        if (IsReadable(sub + 0x10, 8)) sub10 = *(nint*)(sub + 0x10);
        if (IsReadable(sub + 0x18, 8)) sub18 = *(nint*)(sub + 0x18);

        nint fobj = 0;
        if (_fieldObjPtr != null && IsReadable((nint)_fieldObjPtr, 8))
        {
            void* obj = *_fieldObjPtr;
            fobj = (nint)obj;
        }

        var snap = new FacingSnapshot
        {
            Pos = (LivePlayerX, LivePlayerZ),
            SubObj     = SnapshotFloats(sub,   0x00, SubObjEnd),
            SubLink10  = SnapshotFloats(sub10, 0x00, LinkedEnd),
            SubLink18  = SnapshotFloats(sub18, 0x00, LinkedEnd),
            FieldObjBuf = SnapshotFloats(fobj, 0x00, FieldObjEnd),
        };

        if (_facingA == null)
        {
            _facingA = snap;
            string l10 = snap.SubLink10 == null ? "null" : $"ok (0x{sub10:X})";
            string l18 = snap.SubLink18 == null ? "null" : $"ok (0x{sub18:X})";
            string lFO = snap.FieldObjBuf == null ? "null" : $"ok (0x{fobj:X})";
            Log($"[FieldTracker] F10 | Snapshot A at X={snap.Pos.x:F2} Z={snap.Pos.z:F2}. " +
                $"sub_obj=0x{sub:X}  sub+0x10={l10}  sub+0x18={l18}  fieldObj={lFO}. " +
                $"Now turn/walk a DIFFERENT direction, then press F10 again.");
            Speech.Say("Snapshot A. Change facing, press F10.", true);
            return;
        }

        var A = _facingA; _facingA = null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[FieldTracker] F10 | Diff A→B. A=({A.Pos.x:F2},{A.Pos.z:F2}) B=({snap.Pos.x:F2},{snap.Pos.z:F2})");

        var r1 = ScoreStruct(sb, "sub_obj",        A.SubObj,      snap.SubObj,      0x00, 0xD0, 0xD4);
        var r2 = ScoreStruct(sb, "*(sub_obj+0x10)", A.SubLink10,  snap.SubLink10,   0x00);
        var r3 = ScoreStruct(sb, "*(sub_obj+0x18)", A.SubLink18,  snap.SubLink18,   0x00);
        var r4 = ScoreStruct(sb, "fieldObj",       A.FieldObjBuf, snap.FieldObjBuf, 0x00);

        // Announce the strongest single candidate across all structs
        (string who, int off, float av, float bv) best = ("none", -1, 0, 0);
        float bestScore = 0f;
        void Consider(string name, (int off, float a, float b) r)
        {
            if (r.off < 0) return;
            float aMag = MathF.Abs(r.a), bMag = MathF.Abs(r.b);
            bool unit = aMag <= 1.01f && bMag <= 1.01f;
            bool yaw  = aMag < 6.3f && bMag < 6.3f;
            float score = (unit ? 4f : yaw ? 2f : 1f) * MathF.Abs(r.b - r.a);
            if (score > bestScore) { bestScore = score; best = (name, r.off, r.a, r.b); }
        }
        Consider("sub_obj",        r1);
        Consider("*(sub+0x10)",    r2);
        Consider("*(sub+0x18)",    r3);
        Consider("fieldObj",       r4);

        if (best.off >= 0)
        {
            sb.AppendLine($"OVERALL BEST: {best.who} +0x{best.off:X3}  A={best.av:F4}  B={best.bv:F4}");
            Speech.Say($"Best candidate in {best.who} at offset {best.off:X}, A {best.av:F2}, B {best.bv:F2}.", true);
        }
        else
        {
            Speech.Say("No candidates. Turn further and retry.", true);
        }
        Log(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// F9: announces live player position and logs it.
    /// Live source: sub_obj+0xD0 (X) and sub_obj+0xD4 (Z), confirmed by Frida heap scan.
    /// Press repeatedly while walking — values should change continuously.
    /// </summary>
    private static unsafe void LogPlayerPosition()
    {
        // Go through the dispatched PlayerX/PlayerZ so 3D dungeons route through
        // the sub_obj+0x10 path instead of the 2D-only sub+0xD0.
        float xLive = PlayerX, zLive = PlayerZ;
        float xSpawn = PlayerX2DChain, zSpawn = PlayerZ2DChain;
        float fx = ForwardX, fz = ForwardZ;

        if (!float.IsNaN(xLive))
        {
            Log($"[FieldTracker] F9 | LIVE X={xLive:F2} Z={zLive:F2} forward=(fx={fx:F3}, fz={fz:F3}) (spawn X={xSpawn:F2} Z={zSpawn:F2})");
            Speech.Say($"Position X {xLive:F0}, Z {zLive:F0}. Forward X {fx:F2}, Z {fz:F2}.", true);
        }
        else
        {
            Log("[FieldTracker] F9 | Live position not available");
            Speech.Say("Position not available.", true);
        }
    }

    /// <summary>
    /// F7: snapshot comparison.
    /// First press = take snapshot A (be far from any interactable).
    /// Second press = take snapshot B (be near CHECK!! prompt), compare and log differences.
    /// Third press = new A, and so on.
    /// Only reports changes where old or new value is a small integer (0-255),
    /// filtering out floats and pointers.
    /// </summary>
    private void TakeSnapshot()
    {
        int[] snap = SnapshotRegions();

        if (_snapA == null)
        {
            _snapA = snap;
            _snapB = null;
            Log("[Snapshot] A taken. Now walk near CHECK!! prompt and press F7 again.");
            Speech.Say("Snapshot A taken.", true);
            return;
        }

        if (_snapB == null)
        {
            _snapB = snap;
            Log("[Snapshot] B taken. Comparing...");
            Speech.Say("Snapshot B taken, comparing.", true);
            CompareSnapshots(_snapA, _snapB);
            // Reset so next F7 starts a new A
            _snapA = null;
            _snapB = null;
            return;
        }
    }

    /// <summary>
    /// F8: heap position scanner.
    /// First press (while standing still) = snapshot 4 MB of heap memory around the game objects.
    /// Second press (after walking around) = compare snapshots, log float values that changed
    /// by a walking-plausible amount (0.1 – 5000 units). The changing floats are the X/Z position.
    ///
    /// Scan base = slot[0] pointer – 1 MB, scans forward 4 MB (1 M int reads).
    /// Reads are done page-by-page (4 KB at a time) with exception handling so bad pages are skipped.
    /// </summary>
    private unsafe void HeapPositionScan()
    {
        const int TotalInts  = 1048576; // 4 MB / 4 bytes
        const int PageInts   = 1024;    // 4 KB / 4 bytes — skip whole page on access fault

        long slot0 = *(long*)0x1411AB330L;
        if (slot0 < 0x10000L || (ulong)slot0 > 0x0007FFFFFFFFFFFFUL)
        {
            Speech.Say("Slot pointer not ready.", true);
            return;
        }
        long scanBase = (slot0 - 0x100000L) & ~0xFFL; // 1 MB before slot[0], 256-byte aligned
        if (scanBase < 0x10000L) scanBase = 0x10000L;

        if (_heapSnapA == null)
        {
            _heapSnapA   = new int[TotalInts];
            _heapSnapBase = scanBase;
            int* p = (int*)scanBase;
            for (int page = 0; page < TotalInts / PageInts; page++)
            {
                int off = page * PageInts;
                try   { for (int j = 0; j < PageInts; j++) _heapSnapA[off + j] = p[off + j]; }
                catch { /* guard / unmapped page — leave as 0 */ }
            }
            Log($"[HeapScan] Snapshot A taken. Base=0x{scanBase:X}, size=4 MB. Walk around, then press F8 again.");
            Speech.Say("Heap snapshot A taken. Walk around, then press F8.", true);
            return;
        }

        // Second press: take B and compare
        long base2 = _heapSnapBase;
        int* p2 = (int*)base2;
        int[] snapA = _heapSnapA;
        _heapSnapA = null; // reset so next F8 starts fresh

        Log("[HeapScan] Comparing B vs A for moving float coordinates...");
        int found = 0;
        for (int page = 0; page < TotalInts / PageInts; page++)
        {
            int off = page * PageInts;
            int[] pageB = new int[PageInts];
            try   { for (int j = 0; j < PageInts; j++) pageB[j] = p2[off + j]; }
            catch { continue; }

            for (int j = 0; j < PageInts; j++)
            {
                int iA = snapA[off + j];
                int iB = pageB[j];
                if (iA == iB) continue;

                float fA = BitConverter.Int32BitsToSingle(iA);
                float fB = BitConverter.Int32BitsToSingle(iB);
                if (!LooksLikeCoord(fA) || !LooksLikeCoord(fB)) continue;
                float delta = MathF.Abs(fB - fA);
                if (delta < 0.1f || delta > 5000f) continue;

                long addr = base2 + ((long)(off + j)) * 4;
                Log($"[HeapScan] 0x{addr:X}  {fA:F3} → {fB:F3}  (Δ{delta:F3})");
                found++;
            }
        }
        Log($"[HeapScan] Done. {found} float position candidate(s) found.");
        Speech.Say($"Heap scan done. {found} candidates.", true);
    }

    /// <summary>
    /// Counts active NPC slots in the global slot array at 0x1411AB330.
    /// Each slot is an 8-byte pointer; a non-null, plausible pointer means an NPC is present.
    /// Returns the number of occupied slots (0–11).
    /// </summary>
    private static unsafe int CountNpcSlots()
    {
        const long NPC_SLOT_ARRAY = 0x1411AB330L;
        const int  SLOT_COUNT     = 11;
        var arr = (nint*)NPC_SLOT_ARRAY;
        int count = 0;
        for (int i = 0; i < SLOT_COUNT; i++)
        {
            nint ptr = arr[i];
            if (ptr > 0x10000 && (ulong)ptr < 0x0007FFFFFFFFFFFFUL)
                count++;
        }
        return count;
    }

    private int[] SnapshotRegions()
    {
        int total = 0;
        foreach (var r in _scanRegions) total += r.Count;
        int[] buf = new int[total];
        int pos = 0;
        foreach (var r in _scanRegions)
        {
            int* p = (int*)r.Base;
            for (int i = 0; i < r.Count; i++)
                buf[pos++] = p[i];
        }
        return buf;
    }

    private void CompareSnapshots(int[] a, int[] b)
    {
        int pos = 0;
        int found = 0;
        foreach (var r in _scanRegions)
        {
            for (int i = 0; i < r.Count; i++, pos++)
            {
                if (a[pos] == b[pos]) continue;
                uint uA = (uint)a[pos];
                uint uB = (uint)b[pos];
                // Only report if old or new looks like a flag/enum (not a float/pointer)
                if (uA > 255 && uB > 255) continue;
                long addr = r.Base + (long)i * 4;
                Log($"[Snapshot] 0x{addr:X}  {a[pos]} → {b[pos]}");
                found++;
            }
        }
        Log($"[Snapshot] Done. {found} candidate(s) found.");
        Speech.Say($"Snapshot done. {found} candidates.", true);
    }

    // ── Core logic ────────────────────────────────────────────────────────

    private void CheckChanges()
    {
        bool inField = false;

        // --- Area ---
        if (_segMiscPtr != null && *_segMiscPtr != null)
        {
            var seg   = *_segMiscPtr;
            var major = seg[0];   // +0 (int)
            var minor = seg[1];   // +4 (int)

            inField = major > 0;

            if (major > 0 && (major != _lastMajor || minor != _lastMinor))
            {
                _lastMajor = major;
                _lastMinor = minor;
                CurrentMajor = major;
                CurrentMinor = minor;

                // Dungeon FLOORS (major ≥ 21): the (major,minor)→name table is
                // unreliable (deep floors all share one major/minor), so read the
                // game's OWN floor banner from memory instead. Done on a background
                // thread — the heap scan takes ~1 s and the banner cross-fade needs
                // a moment to settle (see DungeonFloorName). major 20 = the
                // TV-world HUB ("Entrance"), which has no floor banner — scanning
                // there found a STALE dungeon banner ("Yukiko's Castle…"), so it
                // uses the table like other non-floor areas.
                if (major >= 21 && !IsBattleMajor(major))
                {
                    string? prev = _lastDungeonFloorName;
                    Log($"[FieldTracker] Area -> major={major} minor={minor}: resolving floor name…");
                    new Thread(() => ResolveAndAnnounceFloor(major, minor, prev))
                        { IsBackground = true, Name = "FloorNameScan" }.Start();
                }
                else
                {
                    // Keep the dungeon name across BATTLES (any battle major, not just 240) so
                    // returning to the floor still says the dungeon; clear it only on a real
                    // non-dungeon area (overworld / TV-world hub).
                    if (!IsBattleMajor(major)) _lastDungeonFloorName = null;
                    var name = GetAreaName(major, minor);
                    Log($"[FieldTracker] Area -> major={major} minor={minor}: {name}");
                    Speech.Say(name, true);
                }
                _npcCountdown = 4;  // announce NPC count after ~2s (4 × 500ms poll ticks)

                // Any area transition invalidates the wall-actor cache. The
                // game reallocates collision heaps on battle exit / floor
                // change, so cached actor pointers become stale even when the
                // major/minor return to a previously-scanned floor.
                if (_wallActors != null)
                {
                    Log($"[FieldTracker] Area changed — invalidating wall actor cache");
                    _wallActors = null;
                    _wallActorsMajor = -1;
                    _wallActorsMinor = -1;
                    _lastScanAttemptUtc = DateTime.MinValue;   // let the next ensure trigger immediately
                }
            }

            // Deferred NPC count: disabled — NPC slot array is a fixed pool (always 11),
            // not area-specific. Needs fieldObj struct analysis to get the real count.
            // TODO: read interactable count from *fieldObjPtr once offsets are mapped.
            if (_npcCountdown > 0) _npcCountdown = 0;
        }

        // --- Time and day ---
        // Try write site A first: day at +8, period at +6
        // (same layout as the now-removed _segTimePtr, but found at a WRITE site so it is live)
        ReadTimePrimary(out short curPeriod, out short curGameDay);

        // Day-change read-out (the date screen the sighted see on a new day): announce
        // date + weekday (FormatDate) + live weather + period. ONLY advance _lastGameDay when
        // inField, so if the day flips during the transition (inField=false) the announce isn't
        // lost — it fires the moment the field resumes. The period is folded in here and the
        // separate period announce below is suppressed so it can't clobber this on the same tick.
        if (curGameDay > 0 && curGameDay != _lastGameDay && inField)
        {
            _lastGameDay = curGameDay;
            string msg = FormatDate(curGameDay);
            string weather = ReadWeatherName();
            if (!string.IsNullOrEmpty(weather)) msg += $", {weather}";
            if (curPeriod >= 0)
            {
                _lastTimePeriod = curPeriod;   // fold period in; don't let the block below re-announce it
                string p = GetTimeName(curPeriod);
                if (!string.IsNullOrEmpty(p)) msg += $", {p}";
            }
            Log($"[FieldTracker] Day -> {curGameDay}: {msg}");
            Speech.Say(msg, true);
        }

        if (curPeriod >= 0 && curPeriod != _lastTimePeriod)
        {
            _lastTimePeriod = curPeriod;
            if (inField)
            {
                var name = GetTimeName(curPeriod);
                Log($"[FieldTracker] Time -> period={curPeriod}: {name}");
                Speech.Say(name, true);
            }
        }

        // --- Interactable nearby ---
        if (inField)
        {
            var flag = *_interactFlag;
            if (flag != _lastInteractFlag)
            {
                _lastInteractFlag = flag;
                if (flag != 0)
                {
                    float px = PlayerX;
                    float pz = PlayerZ;
                    string posStr = float.IsNaN(px) ? "(position not captured)" : $"player X={px:F1} Z={pz:F1}";
                    Log($"[FieldTracker] Interactable nearby (flag={flag}) | {posStr}");
                    // DumpInteractableFingerprint() REMOVED 2026-06-12: it ran
                    // wide multi-hundred-byte reads behind single-page guards on
                    // EVERY check rise — prime AVE suspect for the overworld
                    // session crashes (it was the final log entry before the
                    // first one). The binding question it probed is solved
                    // (database/OVERWORLD.md).
                    Speech.Say("Check", true);
                }
            }
        }
    }

    // On every CHECK rising edge, dump bytes around the global flag and key sub_obj
    // fields so we can compare fingerprints across neighbouring interactables.
    // Goal: find a byte/pointer that identifies WHICH interactable is currently
    // selected (Daidara vs. next-door NPC), not just "something is selected".
    //
    // Output is intentionally wide and POINTER-FOCUSED so two rises at different
    // interactables can be diffed quickly: any qword that differs across dumps in
    // the heap-pointer range is a candidate for "currently-active interactable".
    private static unsafe void DumpInteractableFingerprint()
    {
        var sb = new System.Text.StringBuilder(4096);
        float px = LivePlayerX, pz = LivePlayerZ;
        sb.AppendLine($"[FieldTracker] [RISE] player=({px:F1},{pz:F1}) area={CurrentMajor}/{CurrentMinor}");

        // --- 1. Slot area, read UNCONDITIONALLY (ignoring slotCount which reads 0) ---
        const long PtrArrayBase  = 0x1411BC728L;
        const long SlotInfoBase  = 0x1411BC7B0L;
        nint ptrArray = IsReadable((nint)PtrArrayBase, 8) ? *(nint*)PtrArrayBase : 0;
        nint slotInfo = IsReadable((nint)SlotInfoBase, 8) ? *(nint*)SlotInfoBase : 0;
        sb.AppendLine($"  ptrArrayBase=0x{ptrArray:X}  slotInfoBase=0x{slotInfo:X}");
        if (slotInfo != 0)
        {
            for (int i = 0; i < 16; i++)
            {
                nint slotAddr = slotInfo + i * 0x38;
                if (!IsReadable(slotAddr, 0x38)) break;
                int active    = *(int*)slotAddr;
                nint entryPtr = *(nint*)(slotAddr + 8);
                uint idx      = *(uint*)(slotAddr + 0x10);
                // Stop once we run out of plausibly-valid slots
                if (active == 0 && entryPtr == 0 && idx == 0 && i > 0) break;
                sb.AppendLine($"  slot {i,2}: active={active,3} entryPtr=0x{entryPtr:X}  INDEX={idx}");
            }
        }

        // --- 2. Pointer-only view of sub_obj +0x000..+0x3F8 ---
        var (sub, ok) = GetSubObjPtr();
        if (ok && IsReadable(sub, 0x400))
        {
            sb.Append($"  sub_obj=0x{(long)sub:X} ptrs: ");
            byte* p = (byte*)sub;
            for (int i = 0; i < 0x400; i += 8)
            {
                ulong q = *(ulong*)(p + i);
                // plausible heap pointer: above 0x10_00000 and below 0x7FFF_FFFF_FFFF
                if (q >= 0x10_000000UL && q < 0x7FFF_FFFF_FFFFUL)
                    sb.Append($"+{i:X3}=0x{q:X} ");
            }
            sb.AppendLine();
        }

        // --- 3. Pointer-only view of fieldObj +0x000..+0x3F8 ---
        if (_fieldObjPtr != null)
        {
            void* obj = *_fieldObjPtr;
            ulong addr = (ulong)obj;
            if (addr >= 0x10000UL && addr <= 0x7FFFFFFFFFFFUL && IsReadable((nint)obj, 0x400))
            {
                sb.Append($"  fieldObj=0x{addr:X} ptrs: ");
                byte* p = (byte*)obj;
                for (int i = 0; i < 0x400; i += 8)
                {
                    ulong q = *(ulong*)(p + i);
                    if (q >= 0x10_000000UL && q < 0x7FFF_FFFF_FFFFUL)
                        sb.Append($"+{i:X3}=0x{q:X} ");
                }
                sb.AppendLine();
            }
        }

        // --- 4. Wide window around CHECK flag (expanded to 1024 bytes) ---
        // NEW: pointer-tagged so we can spot heap refs among the noise.
        nint flagBase = unchecked((nint)0x1411BC6F4L);
        if (IsReadable(flagBase, 0x400))
        {
            sb.AppendLine($"  flag-window @0x{(long)flagBase:X} (flag at +0x100) qword view:");
            byte* p = (byte*)flagBase;
            for (int row = 0; row < 0x400; row += 0x20)
            {
                sb.Append($"    +0x{row:X3}:");
                for (int i = 0; i < 0x20; i += 8)
                {
                    ulong q = *(ulong*)(p + row + i);
                    sb.Append($" {q:X16}");
                }
                sb.AppendLine();
            }
        }

        Log(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Tries to read the live time period and game day from write-site pointers.
    /// Returns -1 for any value that cannot be read or is out of range.
    ///
    /// Strategy A: _segTimeDayPtr — matches zarroboogs _segTime layout:
    ///   day at +0 (CurrentDay), period at +2 (DayTime)
    ///   The SigScan finds the write to [RAX+8] (NextDay), so the struct base
    ///   is the same global — we just read +0 and +2 for the live current values.
    ///
    /// Strategy B: _segTimeLivePtr — fallback, same layout
    /// </summary>
    private void ReadTimePrimary(out short period, out short day)
    {
        period = -1;
        day    = -1;

        // Strategy A — confirmed offsets (F2 dump April 16 Evening save):
        //   +00 = CurrentDay  (15 = April 16; epoch April 1, 2011: AddDays(15) = April 16 ✓)
        //   +02 = DayTime     (5  = Evening ✓)
        //   +08 = NextDay     (16 = April 17 — DO NOT use, that is tomorrow)
        //   +10 = NextPeriod  (0  = Early Morning — DO NOT use)
        if (_segTimeDayPtr != null && *_segTimeDayPtr != null)
        {
            var seg = *_segTimeDayPtr;
            var dA  = *(short*)(seg + 0);   // +00 = CurrentDay
            var pA  = *(short*)(seg + 2);   // +02 = DayTime (current period)

            if (dA > 0 && dA < 500 && pA >= 0 && pA <= 7)
            {
                day    = dA;
                period = pA;
                return;
            }

            Log($"[FieldTracker] WriteA out of range: day={dA} period={pA} — trying B");
        }

        // Strategy B (fallback — values seem small/zero in testing, kept for safety)
        if (_segTimeLivePtr != null && *_segTimeLivePtr != null)
        {
            var seg = *_segTimeLivePtr;
            var dB  = seg[0];   // +0
            var pB  = seg[1];   // +2

            if (dB > 0 && dB < 500 && pB >= 0 && pB <= 7)
            {
                day    = dB;
                period = pB;
                return;
            }

            Log($"[FieldTracker] WriteB out of range: day={dB} period={pB}");
        }
    }

    private void AnnounceStatus()
    {
        if (_lastMajor <= 0)
        {
            Speech.Say("Not in a field area.", true);
            return;
        }

        // Area: in a dungeon, use the dungeon name we resolved from the game's
        // banner (the (major,minor) table is wrong for deep floors); else the table.
        var area = _lastDungeonFloorName ?? GetAreaName(_lastMajor, _lastMinor);

        // Date/time/weather only mean something when the field clock is live.
        // Inside dungeons / the TV-world hub those fields read 0 / out-of-range,
        // which made M speak a stale date+time+weather ("weird reading",
        // user 2026-06-17) — so announce just the area there.
        if (_lastTimePeriod >= 0 && _lastGameDay > 0)
        {
            var time = GetTimeName(_lastTimePeriod);
            var date = FormatDate(_lastGameDay);
            string weather = ReadWeatherName();
            var msg = date.Length > 0 ? $"{date}, {time}" : time;
            if (weather.Length > 0) msg = $"{msg}, {weather}";
            msg = $"{msg}, {area}";
            Speech.Say(msg, true);
        }
        else
        {
            Speech.Say(area, true);
        }
    }

    /// <summary>
    /// Weather from the seg-time struct (candidates +4 and +6 — same struct
    /// as day/period; field being pinned via live F1 logs). Script-library
    /// doc: 0=Clear, 1=Rain, 2=Cloudy; flow scripts also branch on 3 and 7
    /// (7 behaves like heavy rain — umbrellas out). Unverified values are
    /// logged but not spoken.
    /// </summary>
    // ── Weather: the game's calendar is FIXED — the per-day table was found
    // in init.bin @0x6A8E1 (2026-06-12) and ships as
    // database/weather_calendar.json (index = game-day field = days since
    // 2011-04-01; verified: Apr15=2 rainy, Apr17=0 clear vs user reports).
    // Codes: 0 Clear · 1 Rainy · 2 Pouring rain · 3-8 post-rain specials
    // (announced "Foggy" — fog follows rain in P4; winter ones may be snow,
    // labels refined from play reports).
    private static int[]? _weatherCalendar;

    private static void LoadWeatherCalendar()
    {
        try
        {
            // RELEASE: bundled in the mod folder. DEV: the game's database folder.
            string p = DataPath("weather_calendar.json");
            if (File.Exists(p))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                var arr = doc.RootElement.GetProperty("codes");
                _weatherCalendar = new int[arr.GetArrayLength()];
                int i = 0;
                foreach (var v in arr.EnumerateArray()) _weatherCalendar[i++] = v.GetInt32();
                Log($"[FieldTracker] weather calendar loaded ({_weatherCalendar.Length} days)");
                return;
            }
            Log("[FieldTracker] weather_calendar.json not found");
        }
        catch (Exception ex) { Log($"[FieldTracker] weather calendar load failed: {ex.Message}"); }
    }

    private string ReadWeatherName()
    {
        // Weather from the validated full schedule by the CURRENT date — the SAME source the calendar
        // uses, so the M-key now matches it exactly. The earlier "live" read (seg+0x04/+0x06) was WRONG:
        // those fields are constant (always 1/0), not the weather, so the M-key was stuck on "Sunny".
        // The real live-weather global is elsewhere and not worth chasing — the sheet is accurate.
        return WeatherFromSheet(_lastGameDay);
    }

    // Validated per-day weather schedule (community sheet) — "month-day" -> name. Used as the M-key
    // fallback for unmapped live codes; the calendar uses the same file directly. See weather_schedule.json.
    private static System.Collections.Generic.Dictionary<string, string>? _weatherSched;
    private static void LoadWeatherSched()
    {
        _weatherSched = new();
        try
        {
            string p = DataPath("weather_schedule.json");
            if (!System.IO.File.Exists(p)) { Log("[FieldTracker] weather_schedule.json not found"); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("schedule", out var s))
                foreach (var kv in s.EnumerateObject()) _weatherSched[kv.Name] = kv.Value.GetString() ?? "";
            Log($"[FieldTracker] weather schedule loaded ({_weatherSched.Count} days)");
        }
        catch (Exception e) { Log($"[FieldTracker] weather schedule load failed: {e.Message}"); }
    }
    private static string WeatherFromSheet(int gameDay)
    {
        if (_weatherSched == null) LoadWeatherSched();
        if (_weatherSched == null || gameDay <= 0) return "";
        var date = _epoch.AddDays(gameDay);
        return _weatherSched.TryGetValue($"{date.Month}-{date.Day}", out var w) ? w : "";
    }

    /// <summary>
    /// F2: dumps raw values from both write-site structs to the log.
    /// Use this to figure out which offsets contain day/period if the defaults are wrong.
    /// </summary>
    private void DumpStructs()
    {
        Log("[FieldTracker] === F2 STRUCT DUMP ===");

        if (_segTimeDayPtr != null && *_segTimeDayPtr != null)
        {
            var seg = *_segTimeDayPtr;
            var sb  = new System.Text.StringBuilder("[FieldTracker] Write-A (segTimeDayPtr) shorts:\n");
            for (int i = 0; i < 16; i++)
                sb.AppendLine($"  +{i * 2:D2} (index {i}): {*(short*)(seg + i * 2)}");
            Log(sb.ToString());
        }
        else Log("[FieldTracker] Write-A: not found or null");

        if (_segTimeLivePtr != null && *_segTimeLivePtr != null)
        {
            var seg = *_segTimeLivePtr;
            var sb  = new System.Text.StringBuilder("[FieldTracker] Write-B (segTimeLivePtr) shorts:\n");
            for (int i = 0; i < 16; i++)
                sb.AppendLine($"  +{i * 2:D2} (index {i}): {seg[i]}");
            Log(sb.ToString());
        }
        else Log("[FieldTracker] Write-B: not found or null");

        Log("[FieldTracker] === END DUMP ===");
        Speech.Say("Struct dump written to log.", true);
    }

    /// <summary>
    /// F3: dumps 128 bytes (32 ints) from the segMisc struct.
    /// Press away from NPC, then near NPC — compare logs to find which offset changes.
    /// </summary>
    private void DumpSegMisc()
    {
        Log("[FieldTracker] === F3 SEGMISC DUMP ===");
        if (_segMiscPtr == null || *_segMiscPtr == null)
        {
            Log("[FieldTracker] segMiscPtr is null — not in field?");
            Speech.Say("Seg misc not available.", true);
            return;
        }

        var seg = *_segMiscPtr;
        var sb  = new System.Text.StringBuilder("[FieldTracker] segMisc ints (128 * 4 = 512 bytes):\n");
        for (int i = 0; i < 128; i++)
            sb.AppendLine($"  +{i * 4:D3} (index {i:D3}): {seg[i]}");

        Log(sb.ToString());
        Log("[FieldTracker] === END F3 DUMP ===");
        Speech.Say("Seg misc dump written to log.", true);
    }

    /// <summary>
    /// Insert: collision mesh / scene dump. Based on Ghidra's FUN_14032B810
    /// decompile: sub_obj+0x10e0 is an inline "scene" struct with 6 groups,
    /// each holding up to 1 primary actor + 7 extra actors. Each actor may
    /// have a collision mesh at actor+0xf60. This dump walks every collider
    /// in the scene and follows the mesh chain for each.
    /// </summary>
    private unsafe void DumpCollisionMesh()
    {
        var (sub, ok) = GetSubObjPtr();
        if (!ok) { Log("[FieldTracker] COLMESH | sub_obj not available"); return; }

        float px = LivePlayerX, pz = LivePlayerZ;
        Log($"[FieldTracker] === COLMESH DUMP === player=({px:F1},{pz:F1}) major={CurrentMajor} minor={CurrentMinor} sub_obj=0x{(ulong)sub:X}");

        nint scene = sub + 0x10e0;
        Log($"  scene @ 0x{(ulong)scene:X}");

        // Accumulate min wall distance across ALL colliders in the scene.
        // Also track the hit location per direction so we can eyeball which
        // wall is responsible for each number.
        float dN = float.PositiveInfinity, dS = float.PositiveInfinity;
        float dE = float.PositiveInfinity, dW = float.PositiveInfinity;
        float hNx = 0, hNz = 0, hSx = 0, hSz = 0, hEx = 0, hEz = 0, hWx = 0, hWz = 0;
        int totalColliders = 0, totalTris = 0;

        for (int group = 0; group < 6; group++)
        {
            nint groupBase = scene + group * 0x38;
            if (!IsReadable(groupBase, 0x38)) continue;

            nint primaryPtr = *(nint*)groupBase;
            byte count1 = *(byte*)(scene + 0x32 + group * 0x38);
            byte count2 = *(byte*)(scene + 0x33 + group * 0x38);

            if (count1 > 0 && (ulong)primaryPtr > 0x10000UL && IsReadable(primaryPtr, 0x1000))
            {
                Log($"  group {group}: primary=0x{(ulong)primaryPtr:X} count1={count1} count2={count2}");
                int tris = InspectCollider($"group{group}.primary", primaryPtr, px, pz,
                    ref dN, ref dS, ref dE, ref dW,
                    ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
                totalColliders++;
                totalTris += tris;
            }

            for (int slot = 0; slot < count2 && slot < 7; slot++)
            {
                nint extraSlot = scene + 8 + (group * 7 + slot) * 8;
                if (!IsReadable(extraSlot, 8)) break;
                nint extraPtr = *(nint*)extraSlot;
                if ((ulong)extraPtr <= 0x10000UL || !IsReadable(extraPtr, 0x1000)) continue;
                int tris = InspectCollider($"group{group}.extra[{slot}]", extraPtr, px, pz,
                    ref dN, ref dS, ref dE, ref dW,
                    ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
                totalColliders++;
                totalTris += tris;
            }
        }

        Log($"  TOTAL: {totalColliders} colliders, {totalTris} wall triangles");
        Log($"  FINAL WALL DISTANCES: N={F(dN)}  S={F(dS)}  E={F(dE)}  W={F(dW)}");
        if (!float.IsPositiveInfinity(dN)) Log($"    N hit @ ({hNx:F0},{hNz:F0})");
        if (!float.IsPositiveInfinity(dS)) Log($"    S hit @ ({hSx:F0},{hSz:F0})");
        if (!float.IsPositiveInfinity(dE)) Log($"    E hit @ ({hEx:F0},{hEz:F0})");
        if (!float.IsPositiveInfinity(dW)) Log($"    W hit @ ({hWx:F0},{hWz:F0})");
        Log("[FieldTracker] === END COLMESH DUMP ===");
        Speech.Say(
            $"N {FSpoken(dN)}, S {FSpoken(dS)}, E {FSpoken(dE)}, W {FSpoken(dW)}", true);
    }

    private static string FSpoken(float v) => float.IsPositiveInfinity(v) ? "none" : ((int)v).ToString();

    /// <summary>
    /// Given a collider actor, log its key transform fields and follow +0xf60 to the mesh.
    /// Then iterate triangles in ALL 6 face groups (stride 0x38 from meshData+0x20),
    /// convert to world space, and accumulate minimum wall distances per cardinal
    /// direction (N/S/E/W) relative to the player. The 4 distances are the direct
    /// input to the wall-audio system.
    /// </summary>
    /// <remarks>
    /// Frida probe (database/frida/probe_facegroups.js, run 2026-04-25) showed that
    /// face groups 1-5 contain real wall geometry — typically lower walls, railings,
    /// half-height balustrades — that the previous group-0-only walker missed.
    /// One actor (0x39e51cd0) had FIVE populated groups for the same room.
    /// Earlier "groups 1-5 are garbage" assumption was based on too small a sample.
    /// </remarks>
    /// <summary>
    /// `dN/dS/dE/dW` are now misnomers — they're the four player-relative cardinal
    /// distances (front / back / right / left) computed by rotating wall vertices
    /// into the player's facing frame before the ray test. In player frame:
    ///   forward (= +Z′)   → dN   ("front")
    ///   back    (= -Z′)   → dS   ("back")
    ///   right   (= +X′)   → dE   ("right")
    ///   left    (= -X′)   → dW   ("left")
    /// Audio system loads wall_{front,back,left,right}.wav into the same indices.
    /// If the forward vector reads bogus we fall back to world-axis-aligned
    /// (north/south/east/west) — same behavior as before this change.
    /// </summary>
    private static unsafe int InspectCollider(string label, nint actor, float playerX, float playerZ,
        ref float dN, ref float dS, ref float dE, ref float dW,
        ref float hNx, ref float hNz, ref float hSx, ref float hSz,
        ref float hEx, ref float hEz, ref float hWx, ref float hWz)
    {
        // Build player-frame basis. forward = (fx, fz) should be unit-length;
        // right is forward rotated 90° CW = (fz, -fx). Any non-finite value or
        // length deviating noticeably from 1.0 means we're reading something
        // that isn't a facing vector — fall back to world identity so audio
        // degrades to the old world-aligned behavior instead of producing
        // garbage rotations.
        float fx = ForwardX, fz = ForwardZ;
        float fLen2 = fx * fx + fz * fz;
        bool useFacing = float.IsFinite(fx) && float.IsFinite(fz) && fLen2 > 0.81f && fLen2 < 1.21f;
        if (useFacing)
        {
            float n = 1f / MathF.Sqrt(fLen2);
            fx *= n; fz *= n;
        }
        else { fx = 0f; fz = 1f; }
        float rx = fz, rz = -fx;   // right vector

        float offX = 0, offZ = 0, sX = 1, sY = 1, sZ = 1;
        if (IsReadable(actor + 0x360, 0x10))
        {
            offX = *(float*)(actor + 0x360);
            offZ = *(float*)(actor + 0x368);
        }
        if (IsReadable(actor + 0x3b0, 0x10))
        {
            sX = *(float*)(actor + 0x3b0);
            sY = *(float*)(actor + 0x3b4);
            sZ = *(float*)(actor + 0x3b8);
        }

        if (!IsReadable(actor + 0xf60, 8)) return 0;
        nint meshRoot = *(nint*)(actor + 0xf60);
        if ((ulong)meshRoot <= 0x10000UL) return 0;
        if (!IsReadable(meshRoot, 0x10)) return 0;

        nint meshData = *(nint*)(meshRoot + 8);
        if ((ulong)meshData <= 0x10000UL) return 0;
        // 6 groups × 0x38 stride starting at +0x20 = 0x150 bytes covers all groups.
        if (!IsReadable(meshData, 0x170)) return 0;

        int wallTris = 0;
        for (int group = 0; group < 6; group++)
        {
            nint groupBase = meshData + 0x20 + group * 0x38;
            nint vBuf = *(nint*)groupBase;
            short fCnt = *(short*)(groupBase + 8);
            nint iBuf = *(nint*)(groupBase + 0x10);

            // Same per-group filter as the heap scan classifier: drop empty,
            // 1-tri noise, and oversized character-mesh face groups.
            if ((ulong)vBuf <= 0x10000UL || (ulong)iBuf <= 0x10000UL) continue;
            if (fCnt < 2 || fCnt > 4096) continue;
            if (!IsReadable(vBuf, 0x18) || !IsReadable(iBuf, 8)) continue;

            if (label.Length > 0)
                Log($"     {label} g{group}: offset=({offX:F0},{offZ:F0}) scale=({sX:F2},{sY:F2},{sZ:F2}) tris={fCnt}");

        for (int tri = 0; tri < fCnt; tri++)
        {
            if (!IsReadable(iBuf + tri * 8, 8)) break;
            ushort i0 = *(ushort*)(iBuf + tri * 8);
            ushort i1 = *(ushort*)(iBuf + tri * 8 + 2);
            ushort i2 = *(ushort*)(iBuf + tri * 8 + 4);

            if (!TryReadVert(vBuf, i0, sX, sY, sZ, offX, offZ, out float v0x, out float v0y, out float v0z)) continue;
            if (!TryReadVert(vBuf, i1, sX, sY, sZ, offX, offZ, out float v1x, out float v1y, out float v1z)) continue;
            if (!TryReadVert(vBuf, i2, sX, sY, sZ, offX, offZ, out float v2x, out float v2y, out float v2z)) continue;

            // Keep only wall triangles (skip floor/ceiling by XZ normal magnitude).
            float e1x = v1x - v0x, e1y = v1y - v0y, e1z = v1z - v0z;
            float e2x = v2x - v0x, e2y = v2y - v0y, e2z = v2z - v0z;
            float nx = e1y * e2z - e1z * e2y;
            float nz = e1x * e2y - e1y * e2x;
            float nLenXZ = MathF.Sqrt(nx * nx + nz * nz);
            if (nLenXZ < 0.01f) continue;
            wallTris++;

            // Rotate triangle vertices into player frame:
            //   x' = (wx - px) * right.x + (wz - pz) * right.z   (rightward)
            //   z' = (wx - px) * forward.x + (wz - pz) * forward.z (forward)
            // Then the existing axis-aligned ray test with origin at (0,0)
            // produces dN=front, dS=back, dE=right, dW=left in player frame.
            float v0xp = (v0x - playerX) * rx + (v0z - playerZ) * rz;
            float v0zp = (v0x - playerX) * fx + (v0z - playerZ) * fz;
            float v1xp = (v1x - playerX) * rx + (v1z - playerZ) * rz;
            float v1zp = (v1x - playerX) * fx + (v1z - playerZ) * fz;
            float v2xp = (v2x - playerX) * rx + (v2z - playerZ) * rz;
            float v2zp = (v2x - playerX) * fx + (v2z - playerZ) * fz;

            RayTestEdge(v0xp, v0zp, v1xp, v1zp, 0f, 0f,
                ref dN, ref dS, ref dE, ref dW,
                ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
            RayTestEdge(v1xp, v1zp, v2xp, v2zp, 0f, 0f,
                ref dN, ref dS, ref dE, ref dW,
                ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
            RayTestEdge(v2xp, v2zp, v0xp, v0zp, 0f, 0f,
                ref dN, ref dS, ref dE, ref dW,
                ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
        }
        }   // end face-group loop
        return wallTris;
    }

    /// <summary>
    /// Cast 4 axis-aligned rays from (px,pz) and check intersection with the
    /// 2D line segment (Ax,Az)-(Bx,Bz). Update the 4 direction distances with
    /// the smallest positive t found, and record the hit point for diagnostics.
    /// Convention: +X = east (E), -X = west (W), +Z = NORTH (N), -Z = SOUTH (S).
    /// Verified empirically 2026-04-24: walking south decreases Z.
    /// </summary>
    private static void RayTestEdge(
        float Ax, float Az, float Bx, float Bz,
        float px, float pz,
        ref float dN, ref float dS, ref float dE, ref float dW,
        ref float hNx, ref float hNz, ref float hSx, ref float hSz,
        ref float hEx, ref float hEz, ref float hWx, ref float hWz)
    {
        // East/West rays: segment must span the ray's Z.
        float zDiff = Bz - Az;
        if (MathF.Abs(zDiff) > 1e-4f)
        {
            float u = (pz - Az) / zDiff;
            if (u >= 0f && u <= 1f)
            {
                float hitX = Ax + u * (Bx - Ax);
                float tE = hitX - px;
                if (tE > 0f && tE < dE) { dE = tE; hEx = hitX; hEz = pz; }
                float tW = px - hitX;
                if (tW > 0f && tW < dW) { dW = tW; hWx = hitX; hWz = pz; }
            }
        }

        // North/South rays: segment must span the ray's X.
        float xDiff = Bx - Ax;
        if (MathF.Abs(xDiff) > 1e-4f)
        {
            float u = (px - Ax) / xDiff;
            if (u >= 0f && u <= 1f)
            {
                float hitZ = Az + u * (Bz - Az);
                float tN = hitZ - pz;   // N: +Z direction
                if (tN > 0f && tN < dN) { dN = tN; hNx = px; hNz = hitZ; }
                float tS = pz - hitZ;   // S: -Z direction
                if (tS > 0f && tS < dS) { dS = tS; hSx = px; hSz = hitZ; }
            }
        }
    }

    /// <summary>
    /// Detect door positions by finding gaps in the wall mesh. Walks every
    /// wall triangle in sub_obj+0x10e0, projects each non-degenerate edge to
    /// XZ, dedupes coincident edges, then identifies "dangling" endpoints
    /// (touched by only one edge) — those are wall ends adjacent to doorways.
    /// Pairs nearest dangling endpoints into door midpoints.
    ///
    /// Each result is (midX, midZ, gapWidth) so callers can filter by gap
    /// width — real P4G doors are ~540 units; very small or very large gaps
    /// are algorithm artifacts (incomplete mesh, room-corner singletons, etc.).
    /// </summary>
    /// <summary>
    /// Real P4G dungeon doors are 540 units wide (verified empirically). Other
    /// widths are wall-mesh artifacts where the greedy endpoint-pairing got it
    /// wrong (incomplete mesh, lone corner verts, etc.). Filter accepts a
    /// band around 540 so slight grid jitter still passes.
    /// </summary>
    private const float DoorWidthMin = 400f;
    private const float DoorWidthMax = 700f;

    public static unsafe List<(float x, float z, float width)>? DetectDoorsInScene()
    {
        var triangles = new List<float[]>();
        bool ok = VisitWallTrianglesInScene(t => triangles.Add(t));
        if (!ok || triangles.Count == 0) return null;
        var raw = DetectDoorsFromTriangles(triangles);
        return raw.Where(d => d.width >= DoorWidthMin && d.width <= DoorWidthMax).ToList();
    }

    private static List<(float x, float z, float width)> DetectDoorsFromTriangles(List<float[]> triangles)
    {
        const float Eps = 1.0f;
        // Round to integer grid (1 unit precision) to dedupe coincident endpoints.
        static (int, int) RoundPt(float x, float z)
            => ((int)MathF.Round(x / Eps), (int)MathF.Round(z / Eps));

        var edgeSet = new HashSet<((int, int), (int, int))>();
        foreach (var t in triangles)
        {
            var pts = new[] { (t[0], t[2]), (t[3], t[5]), (t[6], t[8]) };
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                float ax = pts[i].Item1, az = pts[i].Item2;
                float bx = pts[j].Item1, bz = pts[j].Item2;
                float dx = bx - ax, dz = bz - az;
                if (dx * dx + dz * dz < 0.5f) continue;          // degenerate (vertical wall side edge)
                var a = RoundPt(ax, az); var b = RoundPt(bx, bz);
                // Sort endpoints to canonicalize the edge for set dedup.
                if (a.Item1 > b.Item1 || (a.Item1 == b.Item1 && a.Item2 > b.Item2))
                    (a, b) = (b, a);
                edgeSet.Add((a, b));
            }
        }

        var degree = new Dictionary<(int, int), int>();
        foreach (var (a, b) in edgeSet)
        {
            degree[a] = degree.TryGetValue(a, out int da) ? da + 1 : 1;
            degree[b] = degree.TryGetValue(b, out int db) ? db + 1 : 1;
        }

        var dangling = new List<(int, int)>();
        foreach (var kv in degree) if (kv.Value == 1) dangling.Add(kv.Key);

        var result = new List<(float, float, float)>();
        while (dangling.Count >= 2)
        {
            var a = dangling[0]; dangling.RemoveAt(0);
            int bestJ = -1; double bestD = double.MaxValue;
            for (int j = 0; j < dangling.Count; j++)
            {
                double dx = a.Item1 - dangling[j].Item1;
                double dz = a.Item2 - dangling[j].Item2;
                double d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; bestJ = j; }
            }
            if (bestJ < 0) break;
            var b = dangling[bestJ]; dangling.RemoveAt(bestJ);
            float mx = (a.Item1 + b.Item1) / 2f * Eps;
            float mz = (a.Item2 + b.Item2) / 2f * Eps;
            float width = (float)Math.Sqrt(bestD) * Eps;
            result.Add((mx, mz, width));
        }
        return result;
    }

    private static void DumpDoorCandidates()
    {
        var doors = DetectDoorsInScene();
        if (doors == null) { Log("[Doors] no scene / no triangles"); return; }
        float px = LivePlayerX, pz = LivePlayerZ;
        Log($"[Doors] player at ({px:F1},{pz:F1}); {doors.Count} candidate(s):");
        var sorted = doors.OrderBy(d => MathF.Sqrt((d.x - px) * (d.x - px) + (d.z - pz) * (d.z - pz))).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            float dx = sorted[i].x - px, dz = sorted[i].z - pz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            Log($"  [{i}] ({sorted[i].x:F1},{sorted[i].z:F1})  width={sorted[i].width:F1}  d={dist:F1}");
        }
        Speech.Say($"{doors.Count} door candidate{(doors.Count == 1 ? "" : "s")} detected.", true);
    }

    private static string F(float v) => float.IsPositiveInfinity(v) ? "∞" : v.ToString("F0");

    /// <summary>
    /// Public helper used by the audio system. Walks sub_obj+0x10e0 scene,
    /// fires 4 cardinal rays, returns the nearest wall distance per direction
    /// (or PositiveInfinity if no wall). Silent — no logging. Safe to call on
    /// a background thread at a few Hz.
    /// </summary>
    public static unsafe bool TryComputeWallDistances(
        out float dN, out float dS, out float dE, out float dW)
    {
        dN = dS = dE = dW = float.PositiveInfinity;
        var (sub, ok) = GetSubObjPtr();
        if (!ok) return false;
        float px = LivePlayerX, pz = LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return false;

        nint scene = sub + 0x10e0;
        // Unused hit coords — pass throwaway refs.
        float hNx = 0, hNz = 0, hSx = 0, hSz = 0, hEx = 0, hEz = 0, hWx = 0, hWz = 0;

        for (int group = 0; group < 6; group++)
        {
            nint groupBase = scene + group * 0x38;
            if (!IsReadable(groupBase, 0x38)) continue;

            nint primaryPtr = *(nint*)groupBase;
            byte count1 = *(byte*)(scene + 0x32 + group * 0x38);
            byte count2 = *(byte*)(scene + 0x33 + group * 0x38);

            if (count1 > 0 && (ulong)primaryPtr > 0x10000UL && IsReadable(primaryPtr, 0x1000))
                InspectCollider("", primaryPtr, px, pz,
                    ref dN, ref dS, ref dE, ref dW,
                    ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);

            for (int slot = 0; slot < count2 && slot < 7; slot++)
            {
                nint extraSlot = scene + 8 + (group * 7 + slot) * 8;
                if (!IsReadable(extraSlot, 8)) break;
                nint extraPtr = *(nint*)extraSlot;
                if ((ulong)extraPtr <= 0x10000UL || !IsReadable(extraPtr, 0x1000)) continue;
                InspectCollider("", extraPtr, px, pz,
                    ref dN, ref dS, ref dE, ref dW,
                    ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
            }
        }
        return true;
    }

    /// <summary>
    /// Walk every wall triangle in the current scene and invoke the callback
    /// with world-space vertex coords (9 floats per triangle: v0xyz v1xyz v2xyz).
    /// Used by the dungeon-mapper to snapshot the geometry. Returns true on
    /// success, false if sub_obj / scene is unreadable.
    /// </summary>
    public static unsafe bool VisitWallTrianglesInScene(Action<float[]> callback)
    {
        var (sub, ok) = GetSubObjPtr();
        if (!ok) return false;
        nint scene = sub + 0x10e0;

        for (int group = 0; group < 6; group++)
        {
            nint groupBase = scene + group * 0x38;
            if (!IsReadable(groupBase, 0x38)) continue;

            nint primaryPtr = *(nint*)groupBase;
            byte count1 = *(byte*)(scene + 0x32 + group * 0x38);
            byte count2 = *(byte*)(scene + 0x33 + group * 0x38);

            if (count1 > 0 && (ulong)primaryPtr > 0x10000UL && IsReadable(primaryPtr, 0x1000))
                VisitActorMeshTriangles(primaryPtr, callback);

            for (int slot = 0; slot < count2 && slot < 7; slot++)
            {
                nint extraSlot = scene + 8 + (group * 7 + slot) * 8;
                if (!IsReadable(extraSlot, 8)) break;
                nint extraPtr = *(nint*)extraSlot;
                if ((ulong)extraPtr <= 0x10000UL || !IsReadable(extraPtr, 0x1000)) continue;
                VisitActorMeshTriangles(extraPtr, callback);
            }
        }
        return true;
    }

    // ── Un-culled wall-actor list (Frida-discovered) ──────────────────────────
    //
    // The scene cache at sub_obj+0x10e0 only ever holds ~6 actors (the rooms
    // immediately around the player). Frida memory scans confirmed that the
    // rest of the floor's wall actors are still resident in the same heap
    // region, just not in the cache. We rebuild a full list once per floor by
    // walking that region and matching the actor signature:
    //   *(actor+0xf60)  -> meshRoot
    //   *(meshRoot+0x8) -> meshData
    //   *(meshData+0x20) -> vertex buffer
    //   *(meshData+0x28) -> u16 face count (2..4096 — drops 1-tri noise + char meshes)
    //   *(meshData+0x30) -> index buffer
    //
    // Anchor for the region scan = first non-empty actor in the scene cache.
    // VirtualQuery on the anchor returns the containing committed region which
    // bounds the scan. Build runs on a Task so the WallSounds poll thread
    // doesn't block on the ~1-3 s scan; queries fall back to the scene cache
    // until the new list is ready.

    private static volatile List<nint>? _wallActors;
    private static volatile int _wallActorsMajor = -1;
    private static volatile int _wallActorsMinor = -1;
    private static volatile bool _wallActorsBuilding;
    private static readonly object _wallActorsLock = new();
    private static DateTime _lastScanAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// Kick off (or skip) a background rebuild of the un-culled wall-actor list
    /// for the current floor. Designed to be called from a hotkey for now —
    /// auto-build inside WallSounds is disabled until we trust the scan path.
    /// </summary>
    /// <remarks>
    /// Rate-limited to one attempt per 2 seconds to prevent retry storms when
    /// the scene cache is empty (transitions, loading screens). The scan runs
    /// on the thread pool so it doesn't block the caller; while building, the
    /// cached list is null and queries fall back to the scene walker.
    /// </remarks>
    public static void EnsureWallActorsBuilt()
    {
        int major = CurrentMajor, minor = CurrentMinor;
        if (major < 20)
        {
            if (_wallActors != null) { _wallActors = null; _wallActorsMajor = _wallActorsMinor = -1; }
            return;
        }
        if (major == _wallActorsMajor && minor == _wallActorsMinor) return;
        if (_wallActorsBuilding) return;
        if ((DateTime.UtcNow - _lastScanAttemptUtc).TotalSeconds < 2) return;

        lock (_wallActorsLock)
        {
            if (major == _wallActorsMajor && minor == _wallActorsMinor) return;
            if (_wallActorsBuilding) return;
            if ((DateTime.UtcNow - _lastScanAttemptUtc).TotalSeconds < 2) return;
            _wallActorsBuilding = true;
            _lastScanAttemptUtc = DateTime.UtcNow;
            _wallActors = null;
            _wallActorsMajor = -1;
            _wallActorsMinor = -1;
        }

        Log($"[FieldTracker] Wall actor scan: starting for {major}/{minor}...");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var list = BuildWallActorsList();
                sw.Stop();
                if (list == null)
                {
                    Log($"[FieldTracker] Wall actor scan: no anchor (scene empty) for {major}/{minor}");
                    return;
                }
                _wallActors = list;
                _wallActorsMajor = major;
                _wallActorsMinor = minor;
                Log($"[FieldTracker] Wall actors built: {list.Count} for {major}/{minor} in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log($"[FieldTracker] Wall actor scan failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally { _wallActorsBuilding = false; }
        });
    }

    /// <summary>
    /// Walk the heap region around the scene's first actor and collect every
    /// actor-shaped structure. Returns null if no scene anchor is available.
    /// Verbose logging at each step — if the scan ever AVs, the last log line
    /// pinpoints how far it got.
    /// </summary>
    private static unsafe List<nint>? BuildWallActorsList()
    {
        Log("[FieldTracker]   step1: get sub_obj");
        var (sub, ok) = GetSubObjPtr();
        if (!ok) return null;
        nint scene = sub + 0x10e0;

        Log("[FieldTracker]   step2: find scene anchor");
        nint anchor = 0;
        for (int group = 0; group < 6 && anchor == 0; group++)
        {
            nint groupBase = scene + group * 0x38;
            if (!IsReadable(groupBase, 0x38)) continue;
            byte c1 = *(byte*)(scene + 0x32 + group * 0x38);
            byte c2 = *(byte*)(scene + 0x33 + group * 0x38);
            if (c1 > 0)
            {
                nint primary = *(nint*)groupBase;
                if ((ulong)primary > 0x10000UL && IsReadable(primary, 0x1000)) { anchor = primary; break; }
            }
            for (int slot = 0; slot < c2 && slot < 7 && anchor == 0; slot++)
            {
                nint extraSlot = scene + 8 + (group * 7 + slot) * 8;
                if (!IsReadable(extraSlot, 8)) break;
                nint extra = *(nint*)extraSlot;
                if ((ulong)extra > 0x10000UL && IsReadable(extra, 0x1000)) { anchor = extra; break; }
            }
        }
        if (anchor == 0) return null;
        Log($"[FieldTracker]   step2: anchor = 0x{(ulong)anchor:X}");

        // VirtualQuery returns the size of the contiguous run of pages with the
        // SAME state and protection. The game heap is split across many such
        // runs (different alloc blocks, slightly different attrs). The earlier
        // single-region scan only saw 388 KB while Frida's enumerateRanges('rw-')
        // covered 3.6 MB on the same heap. We now walk forward and backward
        // from the anchor's region, hopping through adjacent committed RW pages,
        // capped at a budget of 16 MB total / 64 regions.

        Log("[FieldTracker]   step3: walking contiguous RW heap regions around anchor");
        const int MBI_SIZE = 48;
        const int OFF_BASE = 0;
        const int OFF_REGION_SIZE = 24;
        const int OFF_STATE = 32;
        const int OFF_PROTECT = 36;
        const int OFF_TYPE = 40;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        const uint PAGE_READWRITE = 0x04;
        const uint MEM_PRIVATE = 0x20000;
        const long MaxBudget = 8L * 1024 * 1024;     // 8 MB (Frida scan covered ~3.6 MB)
        const int MaxRegions = 32;

        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(anchor, buf, MBI_SIZE) == 0) { Log("[FieldTracker]   step3: VirtualQuery returned 0"); return null; }
        if (*(uint*)(buf + OFF_STATE) != MEM_COMMIT) { Log("[FieldTracker]   step3: anchor region not MEM_COMMIT"); return null; }
        if (*(uint*)(buf + OFF_TYPE) != MEM_PRIVATE) { Log("[FieldTracker]   step3: anchor region not MEM_PRIVATE"); return null; }
        nint anchorRegionBase = *(nint*)(buf + OFF_BASE);
        long anchorRegionSize = *(long*)(buf + OFF_REGION_SIZE);
        Log($"[FieldTracker]   step3: anchor region base=0x{(ulong)anchorRegionBase:X} size=0x{anchorRegionSize:X}");

        // Stricter region filter: heap pages only. Excludes stacks (have guard
        // pages — we can't reliably detect from a forward walk), mapped files
        // (MEM_MAPPED), and DLL data (MEM_IMAGE). PAGE_READWRITE only, no
        // executable pages — the heap shouldn't have them and this is another
        // safety filter.
        bool IsHeapPage(uint state, uint protect, uint type)
        {
            if (state != MEM_COMMIT) return false;
            if (type != MEM_PRIVATE) return false;
            if ((protect & PAGE_NOACCESS) != 0) return false;
            if ((protect & PAGE_GUARD) != 0) return false;
            return (protect & 0xFF) == PAGE_READWRITE;
        }

        var regions = new List<(nint Base, long Size)>(16);
        regions.Add((anchorRegionBase, anchorRegionSize));
        long totalBytes = anchorRegionSize;

        // Walk forward from the anchor's end.
        nint cursor = anchorRegionBase + (nint)anchorRegionSize;
        while (regions.Count < MaxRegions && totalBytes < MaxBudget)
        {
            if (VirtualQuery(cursor, buf, MBI_SIZE) == 0) break;
            uint state = *(uint*)(buf + OFF_STATE);
            uint protect = *(uint*)(buf + OFF_PROTECT);
            uint type = *(uint*)(buf + OFF_TYPE);
            long size = *(long*)(buf + OFF_REGION_SIZE);
            if (!IsHeapPage(state, protect, type)) break;
            regions.Add((cursor, size));
            totalBytes += size;
            cursor += (nint)size;
        }

        // Walk backward from the anchor's base.
        cursor = anchorRegionBase - 1;
        while (regions.Count < MaxRegions && totalBytes < MaxBudget)
        {
            if (VirtualQuery(cursor, buf, MBI_SIZE) == 0) break;
            uint state = *(uint*)(buf + OFF_STATE);
            uint protect = *(uint*)(buf + OFF_PROTECT);
            uint type = *(uint*)(buf + OFF_TYPE);
            nint rb = *(nint*)(buf + OFF_BASE);
            long rs = *(long*)(buf + OFF_REGION_SIZE);
            if (!IsHeapPage(state, protect, type)) break;
            regions.Insert(0, (rb, rs));
            totalBytes += rs;
            cursor = rb - 1;
        }

        Log($"[FieldTracker]   step3: collected {regions.Count} private heap regions, total 0x{totalBytes:X} bytes");

        Log("[FieldTracker]   step4: scan loop start");
        var seenMesh = new HashSet<nint>();
        var actors = new List<nint>(128);
        const long Stride = 0x10;
        long iterations = 0;
        long lastProgress = 0;
        long offGlobal = 0;

        foreach (var (rBase, rSize) in regions)
        {
            for (long off = 0; off < rSize; off += Stride)
            {
                iterations++;
                offGlobal += Stride;
                if (offGlobal - lastProgress >= 0x100000) { Log($"[FieldTracker]   step4: progress 0x{offGlobal:X} found={actors.Count}"); lastProgress = offGlobal; }
                nint p = rBase + (nint)off;
                if (!ClassifyWallActor(p, out nint meshData, out short fcount)) continue;
                if (fcount < 2 || fcount > 4096) continue;
                if (!seenMesh.Add(meshData)) continue;
                actors.Add(p);
            }
        }
        Log($"[FieldTracker]   step5: scan complete — {iterations} positions, {actors.Count} actors");

        return actors;
    }

    /// <summary>
    /// Strict actor signature check used by the heap scan. AV-safe: every read
    /// goes through ReadProcessMemory so a faulting page cannot terminate the
    /// process. Accepts the actor if any of the 6 face groups (stride 0x38
    /// from meshData+0x20) has a valid pointer triple + plausible face count
    /// + finite vertex 0. `meshData` is returned for dedup purposes.
    /// </summary>
    private static unsafe bool ClassifyWallActor(nint actor, out nint meshData, out short fcount)
    {
        meshData = 0; fcount = 0;
        if (!SafeReadPtr(actor + 0xf60, out nint root)) return false;
        if ((ulong)root <= 0x10000UL) return false;
        if (!SafeReadPtr(root + 8, out meshData)) return false;
        if ((ulong)meshData <= 0x10000UL) return false;

        // Try each face group — accept the actor as soon as one has valid mesh data.
        for (int group = 0; group < 6; group++)
        {
            nint gbase = meshData + 0x20 + group * 0x38;
            if (!SafeReadPtr(gbase, out nint vbuf)) return false;
            if (!SafeReadPtr(gbase + 0x10, out nint ibuf)) return false;
            if ((ulong)vbuf <= 0x10000UL || (ulong)ibuf <= 0x10000UL) continue;
            if (!SafeReadShort(gbase + 0x08, out short fc)) return false;
            if (fc < 2 || fc > 4096) continue;
            if (!SafeReadFloat(vbuf + 0xC, out float vx)) continue;
            if (!SafeReadFloat(vbuf + 0x10, out float vy)) continue;
            if (!SafeReadFloat(vbuf + 0x14, out float vz)) continue;
            if (float.IsNaN(vx) || float.IsNaN(vy) || float.IsNaN(vz)) continue;
            if (MathF.Abs(vx) > 1e7f || MathF.Abs(vy) > 1e7f || MathF.Abs(vz) > 1e7f) continue;
            // Found a valid group — actor accepted.
            fcount = fc;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cardinal wall distances using the cached un-culled actor list. Falls
    /// back to the scene-only walker if the list isn't ready yet.
    /// </summary>
    public static unsafe bool TryComputeWallDistancesAll(
        out float dN, out float dS, out float dE, out float dW)
    {
        var actors = _wallActors;
        if (actors == null || actors.Count == 0)
            return TryComputeWallDistances(out dN, out dS, out dE, out dW);

        dN = dS = dE = dW = float.PositiveInfinity;
        float px = LivePlayerX, pz = LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz)) return false;

        float hNx = 0, hNz = 0, hSx = 0, hSz = 0, hEx = 0, hEz = 0, hWx = 0, hWz = 0;
        // Snapshot count to avoid re-reading volatile in the loop.
        int n = actors.Count;
        for (int i = 0; i < n; i++)
        {
            nint actor = actors[i];
            if (!IsReadable(actor, 0x1000)) continue;
            InspectCollider("", actor, px, pz,
                ref dN, ref dS, ref dE, ref dW,
                ref hNx, ref hNz, ref hSx, ref hSz, ref hEx, ref hEz, ref hWx, ref hWz);
        }
        return true;
    }

    /// <summary>
    /// Visit every triangle in the cached un-culled actor list. Falls back to
    /// the scene walker if the list isn't ready.
    /// </summary>
    public static unsafe bool VisitWallTrianglesAll(Action<float[]> callback)
    {
        var actors = _wallActors;
        if (actors == null || actors.Count == 0)
            return VisitWallTrianglesInScene(callback);

        for (int i = 0; i < actors.Count; i++)
        {
            nint actor = actors[i];
            if (!IsReadable(actor, 0x1000)) continue;
            VisitActorMeshTriangles(actor, callback);
        }
        return true;
    }

    /// <summary>How many actors are currently cached for the live floor (0 if not yet built).</summary>
    public static int CachedWallActorCount => _wallActors?.Count ?? 0;

    /// <summary>
    /// Public access to the player's sub_obj pointer so other components can
    /// identify whether a given actor pointer IS the player (and exclude it
    /// from collision queries).
    /// </summary>
    public static nint GetSubObjOrZero()
    {
        var (sub, ok) = GetSubObjPtr();
        return ok ? sub : 0;
    }

    /// <summary>
    /// Snapshot of the un-culled wall-actor list for diagnostic walks. Returns
    /// an empty array if the list hasn't been built yet for the current floor.
    /// </summary>
    public static IReadOnlyList<nint> GetWallActorsSnapshot()
    {
        var list = _wallActors;
        return list == null ? Array.Empty<nint>() : list.ToArray();
    }

    /// <summary>
    /// Walk one actor's mesh and emit each wall triangle (9 floats: v0xyz v1xyz v2xyz)
    /// in world space. Public wrapper around the internal mesh walker so a
    /// diagnostic component can attribute hits per-actor.
    /// </summary>
    public static unsafe void VisitOneActorMeshTriangles(nint actor, Action<float[]> callback)
    {
        if (!IsReadable(actor, 0x1000)) return;
        VisitActorMeshTriangles(actor, callback);
    }

    /// <summary>
    /// AV-safe variant of <see cref="VisitOneActorMeshTriangles"/> — every
    /// dereference goes through ReadProcessMemory so a stale actor pointer
    /// or freed mesh page returns a clean false instead of terminating the
    /// process. Slower than the raw walker (one syscall per read) but safe
    /// to use against the un-culled actor list which contains many false
    /// positives that may briefly hold valid pointers and then get freed.
    /// </summary>
    public static unsafe void VisitOneActorMeshTrianglesSafe(nint actor, Action<float[]> callback)
    {
        // Actor transform — read with SafeRead so a stale actor pointer
        // doesn't AVE on the offset/scale fetch.
        float offX = 0, offZ = 0, sX = 1, sY = 1, sZ = 1;
        if (SafeReadFloat(actor + 0x360, out float oxv)) offX = oxv;
        if (SafeReadFloat(actor + 0x368, out float ozv)) offZ = ozv;
        if (SafeReadFloat(actor + 0x3b0, out float sxv)) sX = sxv;
        if (SafeReadFloat(actor + 0x3b4, out float syv)) sY = syv;
        if (SafeReadFloat(actor + 0x3b8, out float szv)) sZ = szv;

        // Sanity-bound the transform. Three classes of actor get rejected:
        //   1. Corrupt headers (scale = 1e40, offset = NaN) — phantom walls
        //      at random world positions after the scale multiply.
        //   2. Boundary / cutscene volumes with scale ~25 (verified in
        //      diagnostic 2026-05-01) — they have legitimate meshes but
        //      represent skyboxes / triggers / area limits, not walls,
        //      and produce 700+ step phantom hits like "north=21812".
        //   3. Sub-unit decorative props with scale ~0.7 — typically tris=0
        //      anyway, but the bound keeps anything pathological out.
        // Real wall actors are always scale=(1,1,1); filter accordingly.
        if (!float.IsFinite(offX) || !float.IsFinite(offZ)) return;
        if (!float.IsFinite(sX) || !float.IsFinite(sY) || !float.IsFinite(sZ)) return;
        if (MathF.Abs(offX) > 1e6f || MathF.Abs(offZ) > 1e6f) return;
        if (MathF.Abs(sX) > 5f || MathF.Abs(sY) > 5f || MathF.Abs(sZ) > 5f) return;
        if (MathF.Abs(sX) < 0.5f || MathF.Abs(sY) < 0.5f || MathF.Abs(sZ) < 0.5f) return;

        if (!SafeReadPtr(actor + 0xf60, out nint meshRoot)) return;
        if ((ulong)meshRoot <= 0x10000UL) return;
        if (!SafeReadPtr(meshRoot + 8, out nint meshData)) return;
        if ((ulong)meshData <= 0x10000UL) return;

        for (int group = 0; group < 6; group++)
        {
            nint groupBase = meshData + 0x20 + group * 0x38;
            if (!SafeReadPtr(groupBase, out nint vBuf)) return;
            if (!SafeReadPtr(groupBase + 0x10, out nint iBuf)) return;
            if (!SafeReadShort(groupBase + 8, out short fCnt)) return;
            if ((ulong)vBuf <= 0x10000UL || (ulong)iBuf <= 0x10000UL) continue;
            if (fCnt < 2 || fCnt > 4096) continue;

            for (int tri = 0; tri < fCnt; tri++)
            {
                if (!SafeReadShort(iBuf + tri * 8, out short i0Raw)) break;
                if (!SafeReadShort(iBuf + tri * 8 + 2, out short i1Raw)) break;
                if (!SafeReadShort(iBuf + tri * 8 + 4, out short i2Raw)) break;
                ushort i0 = (ushort)i0Raw, i1 = (ushort)i1Raw, i2 = (ushort)i2Raw;

                if (!TryReadVertSafe(vBuf, i0, sX, sY, sZ, offX, offZ, out float v0x, out float v0y, out float v0z)) continue;
                if (!TryReadVertSafe(vBuf, i1, sX, sY, sZ, offX, offZ, out float v1x, out float v1y, out float v1z)) continue;
                if (!TryReadVertSafe(vBuf, i2, sX, sY, sZ, offX, offZ, out float v2x, out float v2y, out float v2z)) continue;

                // Same near-horizontal filter as the raw walker so callers
                // see exactly the wall triangles they'd see otherwise.
                float e1x = v1x - v0x, e1y = v1y - v0y, e1z = v1z - v0z;
                float e2x = v2x - v0x, e2y = v2y - v0y, e2z = v2z - v0z;
                float nx = e1y * e2z - e1z * e2y;
                float nz = e1x * e2y - e1y * e2x;
                if (MathF.Sqrt(nx * nx + nz * nz) < 0.01f) continue;

                callback(new[] { v0x, v0y, v0z, v1x, v1y, v1z, v2x, v2y, v2z });
            }
        }
    }

    private static unsafe bool TryReadVertSafe(
        nint vBuf, ushort idx, float sX, float sY, float sZ,
        float offX, float offZ, out float wx, out float wy, out float wz)
    {
        wx = wy = wz = 0;
        nint va = vBuf + 0xC + idx * 0x18;
        if (!SafeReadFloat(va, out float lx)) return false;
        if (!SafeReadFloat(va + 4, out float ly)) return false;
        if (!SafeReadFloat(va + 8, out float lz)) return false;
        if (!float.IsFinite(lx) || !float.IsFinite(ly) || !float.IsFinite(lz)) return false;
        if (MathF.Abs(lx) > 1e6f || MathF.Abs(ly) > 1e6f || MathF.Abs(lz) > 1e6f) return false;
        wx = lx * sX + offX;
        wy = ly * sY;
        wz = lz * sZ + offZ;
        return true;
    }

    /// <summary>
    /// AV-safe equivalent of <see cref="VisitWallTrianglesAll"/>. Use this
    /// when iterating the un-culled actor list, where many entries are
    /// false positives that can fault the process if dereferenced raw.
    /// Falls back to <see cref="VisitWallTrianglesInScene"/> if the list
    /// hasn't been built yet.
    /// </summary>
    public static unsafe bool VisitWallTrianglesAllSafe(Action<float[]> callback)
    {
        var actors = _wallActors;
        if (actors == null || actors.Count == 0)
            return VisitWallTrianglesInScene(callback);

        for (int i = 0; i < actors.Count; i++)
        {
            VisitOneActorMeshTrianglesSafe(actors[i], callback);
        }
        return true;
    }

    // ── Master wall-actor list (game-authoritative source) ─────────────────
    //
    // Reverse-engineered 2026-05-01 from FUN_1402D90B0 (the per-frame
    // populator of the live scene cache at sub_obj+0x10E0). The engine itself
    // ties wall actors to minimap room IDs through a static singleton at
    // 0x140AA8098 → +0x8 → two linked lists at +0x60 and +0x50.
    //
    // Walking these lists gives us the *full floor*'s wall actors with no
    // phantoms — these are the pointers the engine actively dereferences for
    // collision every frame. Replaces the heap-scanned _wallActors list,
    // which had to be filtered against AVE crashes and still produced fixed-
    // pattern phantom hits (99/111/139). Full layout in
    // memory/ghidra_master_wall_actor_list.md.

    private static readonly nint MasterRootPtrAddr = unchecked((nint)0x140AA8098L);
    private const int MasterRootMgrOff = 0x8;
    private const int MasterListAHeadOff = 0x60;       // primary wall-room list
    private const int MasterListBHeadOff = 0x50;       // secondary actor list (props with collision)
    private const int ListAEntryNextOff = 0xA8;
    private const int ListAEntrySubStructOff = 0xE0;   // → +0x40 actor or +0x38 fallback
    private const int ListBEntryNextOff = 0x150;
    private const int ListBEntryActorOff = 0x168;

    /// <summary>
    /// Enumerate every distinct wall/collision actor pointer the game's
    /// engine knows about for the current floor. Returns an empty list if
    /// the master singleton hasn't been initialised (e.g. main menu, mid-
    /// load). All reads are AV-safe — bad pointers degrade to "skip", they
    /// don't terminate the process.
    /// </summary>
    public static unsafe IReadOnlyList<nint> GetMasterWallActors()
    {
        var actors = new List<nint>();
        if (!SafeReadPtr(MasterRootPtrAddr, out nint root) || (ulong)root <= 0x10000UL)
            return actors;
        if (!SafeReadPtr(root + MasterRootMgrOff, out nint mgr) || (ulong)mgr <= 0x10000UL)
            return actors;

        var seen = new HashSet<nint>();
        const int SafeIter = 4096;        // hard upper bound on list length

        // List A — room walls (entries hold a sub-struct with two actor ptrs).
        // The diagnostic showed paired actors at same offsets where one has
        // mesh and the other is empty — +0x40 and +0x38 are an LOD/variant
        // pair. Take BOTH (deduped) so we don't miss the half holding
        // collision geometry.
        if (SafeReadPtr(mgr + MasterListAHeadOff, out nint entry))
        {
            int n = 0;
            while ((ulong)entry > 0x10000UL && n++ < SafeIter)
            {
                if (SafeReadPtr(entry + ListAEntrySubStructOff, out nint subStruct) &&
                    (ulong)subStruct > 0x10000UL)
                {
                    if (SafeReadPtr(subStruct + 0x40, out nint a40) &&
                        (ulong)a40 > 0x10000UL && seen.Add(a40))
                        actors.Add(a40);
                    if (SafeReadPtr(subStruct + 0x38, out nint a38) &&
                        (ulong)a38 > 0x10000UL && seen.Add(a38))
                        actors.Add(a38);
                }
                if (!SafeReadPtr(entry + ListAEntryNextOff, out entry)) break;
            }
        }

        // List B — secondary actors. Engine picks +0x168 by default but
        // switches to +0x198 when actor.flags-at-+0xf88 has bit-21 set —
        // both are real actor pointers, take whichever exists.
        if (SafeReadPtr(mgr + MasterListBHeadOff, out entry))
        {
            int n = 0;
            while ((ulong)entry > 0x10000UL && n++ < SafeIter)
            {
                if (SafeReadPtr(entry + ListBEntryActorOff, out nint actor168) &&
                    (ulong)actor168 > 0x10000UL && seen.Add(actor168))
                    actors.Add(actor168);
                if (SafeReadPtr(entry + 0x198, out nint actor198) &&
                    (ulong)actor198 > 0x10000UL && seen.Add(actor198))
                    actors.Add(actor198);
                if (!SafeReadPtr(entry + ListBEntryNextOff, out entry)) break;
            }
        }

        return actors;
    }

    /// <summary>
    /// Walk every wall triangle reachable through the master actor list.
    /// Uses <see cref="VisitOneActorMeshTrianglesSafe"/> per actor so a stale
    /// mesh page can't fault the process. Returns true if at least one
    /// triangle was visited; false if the master list was empty/unreadable
    /// (caller should fall back to <see cref="VisitWallTrianglesInScene"/>).
    /// </summary>
    public static unsafe bool VisitMasterWallTriangles(Action<float[]> callback)
    {
        var actors = GetMasterWallActors();
        if (actors.Count == 0) return false;
        for (int i = 0; i < actors.Count; i++)
            VisitOneActorMeshTrianglesSafe(actors[i], callback);
        return true;
    }

    /// <summary>
    /// Read an actor's instance offset (X, Z) and scale (X, Y, Z) from the
    /// transform fields the mesh walker uses. Returns false if any required
    /// memory page is unreadable, in which case all out values are 0.
    /// </summary>
    public static unsafe bool TryReadActorTransform(nint actor,
        out float offX, out float offZ,
        out float sX, out float sY, out float sZ)
    {
        offX = offZ = 0f; sX = sY = sZ = 1f;
        if (!IsReadable(actor + 0x360, 0x10)) return false;
        offX = *(float*)(actor + 0x360);
        offZ = *(float*)(actor + 0x368);
        if (IsReadable(actor + 0x3b0, 0x10))
        {
            sX = *(float*)(actor + 0x3b0);
            sY = *(float*)(actor + 0x3b4);
            sZ = *(float*)(actor + 0x3b8);
        }
        return true;
    }

    private static unsafe void VisitActorMeshTriangles(nint actor, Action<float[]> callback)
    {
        float offX = 0, offZ = 0, sX = 1, sY = 1, sZ = 1;
        if (IsReadable(actor + 0x360, 0x10))
        {
            offX = *(float*)(actor + 0x360);
            offZ = *(float*)(actor + 0x368);
        }
        if (IsReadable(actor + 0x3b0, 0x10))
        {
            sX = *(float*)(actor + 0x3b0);
            sY = *(float*)(actor + 0x3b4);
            sZ = *(float*)(actor + 0x3b8);
        }

        if (!IsReadable(actor + 0xf60, 8)) return;
        nint meshRoot = *(nint*)(actor + 0xf60);
        if ((ulong)meshRoot <= 0x10000UL || !IsReadable(meshRoot, 0x10)) return;
        nint meshData = *(nint*)(meshRoot + 8);
        // Need 0x170 to cover all 6 face groups (0x20 + 6*0x38 = 0x170).
        if ((ulong)meshData <= 0x10000UL || !IsReadable(meshData, 0x170)) return;

        for (int group = 0; group < 6; group++)
        {
            nint groupBase = meshData + 0x20 + group * 0x38;
            nint vBuf = *(nint*)groupBase;
            short fCnt = *(short*)(groupBase + 8);
            nint iBuf = *(nint*)(groupBase + 0x10);
            if ((ulong)vBuf <= 0x10000UL || (ulong)iBuf <= 0x10000UL) continue;
            if (fCnt < 2 || fCnt > 4096) continue;
            if (!IsReadable(vBuf, 0x18) || !IsReadable(iBuf, 8)) continue;

            for (int tri = 0; tri < fCnt; tri++)
            {
                if (!IsReadable(iBuf + tri * 8, 8)) break;
                ushort i0 = *(ushort*)(iBuf + tri * 8);
                ushort i1 = *(ushort*)(iBuf + tri * 8 + 2);
                ushort i2 = *(ushort*)(iBuf + tri * 8 + 4);

                if (!TryReadVert(vBuf, i0, sX, sY, sZ, offX, offZ, out float v0x, out float v0y, out float v0z)) continue;
                if (!TryReadVert(vBuf, i1, sX, sY, sZ, offX, offZ, out float v1x, out float v1y, out float v1z)) continue;
                if (!TryReadVert(vBuf, i2, sX, sY, sZ, offX, offZ, out float v2x, out float v2y, out float v2z)) continue;

                // Skip near-horizontal triangles (floor / ceiling).
                float e1x = v1x - v0x, e1y = v1y - v0y, e1z = v1z - v0z;
                float e2x = v2x - v0x, e2y = v2y - v0y, e2z = v2z - v0z;
                float nx = e1y * e2z - e1z * e2y;
                float nz = e1x * e2y - e1y * e2x;
                if (MathF.Sqrt(nx * nx + nz * nz) < 0.01f) continue;

                callback(new[] { v0x, v0y, v0z, v1x, v1y, v1z, v2x, v2y, v2z });
            }
        }
    }

    private static unsafe bool TryReadVert(
        nint vBuf, ushort idx, float sX, float sY, float sZ,
        float offX, float offZ, out float wx, out float wy, out float wz)
    {
        wx = wy = wz = 0;
        nint va = vBuf + 0xC + idx * 0x18;
        if (!IsReadable(va, 12)) return false;
        float lx = *(float*)va;
        float ly = *(float*)(va + 4);
        float lz = *(float*)(va + 8);
        // Some accepted actors have garbage face groups whose triangles
        // dereference indices into random heap memory. Vertex coords there
        // can be on the order of 1e12 or NaN. Sanity-bound to plausible
        // dungeon coordinates so a single bad triangle doesn't poison the
        // min-distance accumulator.
        if (!float.IsFinite(lx) || !float.IsFinite(ly) || !float.IsFinite(lz)) return false;
        if (MathF.Abs(lx) > 1e6f || MathF.Abs(ly) > 1e6f || MathF.Abs(lz) > 1e6f) return false;
        wx = lx * sX + offX;
        wy = ly * sY;
        wz = lz * sZ + offZ;
        return true;
    }

    /// <summary>
    /// Decode the 6-group face-array layout at (meshData + 0x20..+0x30 stride 0x38).
    /// For each group, report vertex/face counts and a sample of the first triangle.
    /// </summary>
    private static unsafe void DumpMeshFaceGroups(nint meshData)
    {
        for (int g = 0; g < 6; g++)
        {
            int stride = g * 0x38;
            nint vBufAddr = meshData + 0x20 + stride;
            nint fCntAddr = meshData + 0x28 + stride;
            nint iBufAddr = meshData + 0x30 + stride;

            if (!IsReadable(vBufAddr, 8) || !IsReadable(fCntAddr, 2) || !IsReadable(iBufAddr, 8))
            {
                Log($"  face-group {g}: not fully readable — stopping");
                break;
            }

            nint vBuf = *(nint*)vBufAddr;
            short fCnt = *(short*)fCntAddr;
            nint iBuf = *(nint*)iBufAddr;

            Log($"  face-group {g}: vBuf=0x{(ulong)vBuf:X}  fCnt={fCnt}  iBuf=0x{(ulong)iBuf:X}");

            if (fCnt <= 0 || fCnt > 100000) continue;
            if ((ulong)vBuf < 0x10000UL || (ulong)iBuf < 0x10000UL) continue;
            if (!IsReadable(vBuf, 0x40) || !IsReadable(iBuf, 0x20)) continue;

            // First face = 8 bytes = 4 u16 indices (?). Read first 3 as triangle verts.
            ushort i0 = *(ushort*)iBuf;
            ushort i1 = *(ushort*)(iBuf + 2);
            ushort i2 = *(ushort*)(iBuf + 4);
            ushort i3 = *(ushort*)(iBuf + 6);
            Log($"    face[0] indices = ({i0}, {i1}, {i2}, {i3})");

            // Vertex stride 0x18, xyz floats at +0xC.
            for (int k = 0; k < 3; k++)
            {
                ushort idx = (ushort)(k == 0 ? i0 : (k == 1 ? i1 : i2));
                nint vAddr = vBuf + 0xC + idx * 0x18;
                if (!IsReadable(vAddr, 12))
                {
                    Log($"    vert[{idx}] unreadable");
                    continue;
                }
                float vx = *(float*)vAddr;
                float vy = *(float*)(vAddr + 4);
                float vz = *(float*)(vAddr + 8);
                Log($"    vert[{idx}] = ({vx:F2}, {vy:F2}, {vz:F2})");
            }
        }
    }

    /// <summary>
    /// Dump `size` bytes as qwords with offsets. Skips all-zero rows for brevity.
    /// </summary>
    private static unsafe void DumpBytesLabeled(string label, nint ptr, int size)
    {
        if (!IsReadable(ptr, 16))
        {
            Log($"  {label} @0x{(ulong)ptr:X}: NOT READABLE");
            return;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  {label} @0x{(ulong)ptr:X}:");
        for (int off = 0; off < size; off += 16)
        {
            if (!IsReadable(ptr + off, 16)) break;
            ulong q0 = *(ulong*)(ptr + off);
            ulong q1 = *(ulong*)(ptr + off + 8);
            if (q0 == 0 && q1 == 0) continue;
            sb.AppendLine($"    +{off:X3}: {q0:X16} {q1:X16}");
        }
        Log(sb.ToString());
    }

    /// <summary>
    /// Home: environment dump. Hunt for room-AABB data in structures near sub_obj
    /// and fieldObj. Prints all plausible-coord floats from multiple regions and
    /// highlights any 4-tuple that brackets the player's live X/Z — those are
    /// candidate (xMin, xMax, zMin, zMax) room bounds we can wire into wall audio.
    /// Press in the middle of a dungeon room. Compare across rooms to narrow down.
    /// </summary>
    private unsafe void DumpEnvironment()
    {
        var (sub, ok) = GetSubObjPtr();
        if (!ok) { Log("[FieldTracker] ENV | sub_obj not available"); return; }

        float px = LivePlayerX, pz = LivePlayerZ;
        if (float.IsNaN(px) || float.IsNaN(pz))
        {
            Log("[FieldTracker] ENV | player position is NaN - aborting"); return;
        }

        Log($"[FieldTracker] === ENV DUMP === player=({px:F1},{pz:F1}) major={CurrentMajor} minor={CurrentMinor} sub_obj=0x{(ulong)sub:X}");

        // 1) sub_obj itself — 0x200 bytes
        DumpRegionForBounds("sub_obj", sub, 0x200, px, pz);

        // 2) *(sub_obj + 0x10) — entities / position struct
        if (IsReadable(sub + 0x10, 8))
        {
            nint p = *(nint*)(sub + 0x10);
            if ((ulong)p > 0x10000UL && IsReadable(p, 0x200))
                DumpRegionForBounds("*(sub+0x10)", p, 0x200, px, pz);
        }

        // 3) *(sub_obj + 0x18) — facing / orientation struct
        if (IsReadable(sub + 0x18, 8))
        {
            nint p = *(nint*)(sub + 0x18);
            if ((ulong)p > 0x10000UL && IsReadable(p, 0x200))
                DumpRegionForBounds("*(sub+0x18)", p, 0x200, px, pz);
        }

        // 4) fieldObj itself — 0x400 bytes (larger, contains many child pointers)
        if (_fieldObjPtr != null)
        {
            void* obj = *_fieldObjPtr;
            nint fobj = (nint)obj;
            if ((ulong)fobj > 0x10000UL && IsReadable(fobj, 0x400))
            {
                DumpRegionForBounds("fieldObj", fobj, 0x400, px, pz);

                // Follow fieldObj pointers at likely offsets. Collision/room data is
                // often stashed at a child ptr rather than inline.
                int[] childOffsets = { 0x28, 0x80, 0xB0, 0xC0, 0xD8, 0x108, 0x158, 0x168,
                                       0x1E8, 0x1F8, 0x258, 0x2E8, 0x378, 0x3A8, 0x3A0 };
                foreach (int off in childOffsets)
                {
                    if (!IsReadable(fobj + off, 8)) continue;
                    nint child = *(nint*)(fobj + off);
                    if ((ulong)child < 0x10000UL || (ulong)child > 0x00007FFFFFFFFFFFUL) continue;
                    if (!IsReadable(child, 0x100)) continue;
                    DumpRegionForBounds($"*(fieldObj+0x{off:X2})", child, 0x200, px, pz);
                }
            }
        }

        Log("[FieldTracker] === END ENV DUMP ===");
        Speech.Say("Environment dump written to log.", true);
    }

    /// <summary>
    /// Dump every "plausible coord" float in [base, base+size), then hunt for
    /// candidate bound tuples that bracket (px, pz) within a small sliding window.
    /// A bound candidate = any pair of floats with (a &lt;= px &lt;= b) AND another
    /// pair with (c &lt;= pz &lt;= d) within the same window.
    /// </summary>
    private static unsafe void DumpRegionForBounds(string label, nint basePtr, int size, float px, float pz)
    {
        int nFloats = size / 4;
        float[] vals = new float[nFloats];
        bool[] valid = new bool[nFloats];
        int readFloats = 0;

        for (int i = 0; i < nFloats; i++)
        {
            // Re-check each 0x400-byte boundary in case the region isn't fully mapped.
            if ((i & 0x3FF) == 0 && !IsReadable(basePtr + i * 4, 4)) break;
            float fval = *(float*)(basePtr + i * 4);
            vals[i] = fval;
            // Accept floats in a coord-ish range. Dungeon coords go up to ~30000.
            valid[i] = !float.IsNaN(fval) && !float.IsInfinity(fval) &&
                       MathF.Abs(fval) > 0.5f && MathF.Abs(fval) < 100000f &&
                       fval != 1.0f && fval != -1.0f;
            readFloats++;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[ENV] {label} (base=0x{(ulong)basePtr:X}, {readFloats} floats scanned) plausible floats:");
        int shown = 0;
        for (int i = 0; i < readFloats && shown < 100; i++)
        {
            if (!valid[i]) continue;
            // Mark floats close to player coords or that look like bounds candidates.
            string tag = "";
            if (MathF.Abs(vals[i] - px) < 1.0f) tag = " <-- matches player X";
            else if (MathF.Abs(vals[i] - pz) < 1.0f) tag = " <-- matches player Z";
            sb.AppendLine($"    +{i * 4:X3}  {vals[i]:F2}{tag}");
            shown++;
        }

        // Bracket hunt: sliding window of 16 floats (64 bytes).
        // Within each window, look for a pair that brackets px AND a pair that brackets pz.
        const int window = 16;
        int candFound = 0;
        var candSb = new System.Text.StringBuilder($"[ENV] {label} BOUND CANDIDATES (window=0x40):\n");
        for (int start = 0; start + window <= readFloats && candFound < 12; start++)
        {
            int xMinIdx = -1, xMaxIdx = -1, zMinIdx = -1, zMaxIdx = -1;
            // Find one float below px and one above px in the window.
            for (int j = start; j < start + window; j++)
            {
                if (!valid[j]) continue;
                if (vals[j] < px && vals[j] > px - 20000f)
                {
                    if (xMinIdx == -1 || vals[j] > vals[xMinIdx]) xMinIdx = j;
                }
                if (vals[j] > px && vals[j] < px + 20000f)
                {
                    if (xMaxIdx == -1 || vals[j] < vals[xMaxIdx]) xMaxIdx = j;
                }
                if (vals[j] < pz && vals[j] > pz - 20000f)
                {
                    if (zMinIdx == -1 || vals[j] > vals[zMinIdx]) zMinIdx = j;
                }
                if (vals[j] > pz && vals[j] < pz + 20000f)
                {
                    if (zMaxIdx == -1 || vals[j] < vals[zMaxIdx]) zMaxIdx = j;
                }
            }

            if (xMinIdx != -1 && xMaxIdx != -1 && zMinIdx != -1 && zMaxIdx != -1)
            {
                // Skip if the four indices overlap (we need 4 distinct floats).
                var indices = new[] { xMinIdx, xMaxIdx, zMinIdx, zMaxIdx };
                bool distinct = indices.Length == indices.Distinct().Count();
                if (!distinct) continue;

                candSb.AppendLine(
                    $"    window@+{start * 4:X3}: " +
                    $"xMin=+{xMinIdx * 4:X3}({vals[xMinIdx]:F1}) " +
                    $"xMax=+{xMaxIdx * 4:X3}({vals[xMaxIdx]:F1}) " +
                    $"zMin=+{zMinIdx * 4:X3}({vals[zMinIdx]:F1}) " +
                    $"zMax=+{zMaxIdx * 4:X3}({vals[zMaxIdx]:F1})");
                candFound++;
                // Skip forward to avoid dumping overlapping windows.
                start += window / 2;
            }
        }

        if (candFound > 0) sb.Append(candSb);
        else sb.AppendLine($"[ENV] {label}: no bound candidates found");

        Log(sb.ToString());
    }

    /// <summary>
    /// F4: dumps 128 shorts (256 bytes) from the field object struct pointed to by _fieldObjPtr.
    /// Press once FAR from any NPC, then press again NEAR an NPC.
    /// Compare the two log dumps to find which offset(s) changed — that offset likely holds
    /// the "near interactable" flag or NPC proximity state.
    /// SAFE: checks pointer range before dereferencing.
    /// </summary>
    private unsafe void DumpFieldObject()
    {
        Log("[FieldTracker] === F4 FIELD OBJECT DUMP ===");

        if (_fieldObjPtr == null)
        {
            Log("[FieldTracker] _fieldObjPtr: SigScan not found yet");
            Speech.Say("Field object not found.", true);
            return;
        }

        void* obj = *_fieldObjPtr;
        ulong addr = (ulong)obj;

        if (addr < 0x10000UL || addr > 0x0007FFFFFFFFFFFFUL)
        {
            Log($"[FieldTracker] _fieldObjPtr derefs to 0x{addr:X} — null or invalid");
            Speech.Say("Field object null.", true);
            return;
        }

        Log($"[FieldTracker] fieldObj = 0x{addr:X}");

        // Dump fieldObj as 64 ints (256 bytes) — only non-zero entries
        var sb = new System.Text.StringBuilder($"[FieldTracker] fieldObj ints (256 bytes, non-zero only):\n");
        for (int i = 0; i < 64; i++)
        {
            int val = *(int*)((byte*)obj + i * 4);
            if (val != 0)
                sb.AppendLine($"  +{i * 4:D3}: {val,12}  0x{(uint)val:X8}");
        }
        Log(sb.ToString());

        // Follow sub-pointer at fieldObj+0x48
        nint subPtr = *(nint*)((byte*)obj + 0x48);
        Log($"[FieldTracker] fieldObj+0x48 (sub-ptr) = 0x{(ulong)subPtr:X}");

        if ((ulong)subPtr >= 0x10000UL && (ulong)subPtr <= 0x0007FFFFFFFFFFFFUL)
        {
            var sb2 = new System.Text.StringBuilder($"[FieldTracker] sub-object ints (256 bytes, non-zero only):\n");
            for (int i = 0; i < 64; i++)
            {
                int val = *(int*)((byte*)subPtr + i * 4);
                if (val != 0)
                    sb2.AppendLine($"  +{i * 4:D3}: {val,12}  0x{(uint)val:X8}");
            }
            Log(sb2.ToString());

            // Also show +0x08 specifically (the index/count candidate)
            int sub8 = *(int*)((byte*)subPtr + 0x08);
            Log($"[FieldTracker] sub+0x08 = {sub8}  (candidate: selected interactable index or count)");

            // Follow every pointer-looking field in the first 0x100 of sub_obj
            // and dump 2 KB (512 ints) at each target. Goal: find the real
            // interactable array. Previous findings expected sub+0xA8 to hold
            // this pointer, but in live dumps it was null — sub+0x10 and
            // sub+0x18 hold plausible heap pointers, and sub+0x08=3 matches
            // "3 people" for the test area, so the array is likely at +0x10.
            int[] ptrCandidates = { 0x10, 0x18, 0x20, 0x28, 0x60, 0x68, 0x70, 0x80, 0xA8 };
            foreach (int off in ptrCandidates)
            {
                nint cand = *(nint*)((byte*)subPtr + off);
                ulong cu = (ulong)cand;
                if (cu < 0x10000UL || cu > 0x0007FFFFFFFFFFFFUL)
                {
                    Log($"[FieldTracker] sub+0x{off:X2} -> 0x{cu:X}  (skip: null/invalid range)");
                    continue;
                }
                // CRITICAL: slot may hold a float bit-pattern (e.g. sub+0x20 = 0x432324ED = 163.14f)
                // that passes the range check but is not a real pointer. Dereferencing crashes with
                // AccessViolationException which cannot be caught on .NET 9 — the process dies.
                // VirtualQuery is the only safe way to verify the address is mapped committed memory.
                if (!IsReadable(cand, 4))
                {
                    Log($"[FieldTracker] sub+0x{off:X2} -> 0x{cu:X}  (skip: not mapped — likely float/int data, not a ptr)");
                    continue;
                }
                Log($"[FieldTracker] sub+0x{off:X2} -> 0x{cu:X}  (following, 2 KB)");

                const int kInts = 512;  // 2 KB
                int[] vals = new int[kInts];
                byte* lp = (byte*)cand;
                int readCount = 0;
                // Walk page-by-page — re-check each 4 KB boundary. This covers the case where
                // the first page is mapped but the struct spans into an unmapped region.
                for (int i = 0; i < kInts; i++)
                {
                    if ((i & 0x3FF) == 0 && !IsReadable(cand + i * 4, 4)) break;
                    vals[i] = *(int*)(lp + i * 4);
                    readCount++;
                }

                int nonZero = 0;
                var sbC = new System.Text.StringBuilder($"[FieldTracker] sub+0x{off:X2} non-zero + coord-float entries:\n");
                for (int i = 0; i < readCount; i++)
                {
                    int ival   = vals[i];
                    if (ival == 0) continue;
                    float fval = BitConverter.Int32BitsToSingle(ival);
                    string floatStr = "";
                    if (LooksLikeCoord(fval))
                    {
                        floatStr = $"  << float: {fval:F3}";
                        if (i + 1 < readCount)
                        {
                            float fn = BitConverter.Int32BitsToSingle(vals[i + 1]);
                            if (LooksLikeCoord(fn))
                                floatStr += $"   (pair +{(i+1)*4:D4}: {fn:F3})";
                        }
                    }
                    string enumStr = (ival >= 0 && ival <= 20) ? "  [small-int]" : "";
                    sbC.AppendLine($"    +{i*4:D4} (idx {i:D3}): {ival,12}  0x{(uint)ival:X8}{enumStr}{floatStr}");
                    nonZero++;
                    if (nonZero >= 120) { sbC.AppendLine("    (...truncated at 120 non-zero)"); break; }
                }
                Log(sbC.ToString());

                // Stride probe at this candidate: does +0 repeat as a small int at regular strides?
                int[] strides = { 0x10, 0x20, 0x40, 0x80, 0x100, 0x200 };
                var sbS = new System.Text.StringBuilder($"[FieldTracker] sub+0x{off:X2} stride probe:\n");
                foreach (int stride in strides)
                {
                    int si = stride / 4;
                    if (si * 3 >= readCount) continue;
                    int t0 = vals[0], t1 = vals[si], t2 = vals[si * 2], t3 = vals[si * 3];
                    bool allSmall = t0 >= 0 && t0 <= 20 && t1 >= 0 && t1 <= 20
                                 && t2 >= 0 && t2 <= 20 && t3 >= 0 && t3 <= 20;
                    sbS.AppendLine($"    stride 0x{stride:X3} -> +0={t0} +{stride:X}={t1} +{stride*2:X}={t2} +{stride*3:X}={t3} {(allSmall ? "<<< type-like" : "")}");
                }
                Log(sbS.ToString());
            }

            // sub+0x08 context: earlier thought to be selected index; if it is the
            // entry count for sub+0x10 (area had 3 NPCs → sub+0x08=3 in last dump),
            // log both together so the relationship is obvious.
            int selCount = *(int*)((byte*)subPtr + 0x08);
            Log($"[FieldTracker] NOTE: sub+0x08={selCount} (if this matches 'people count' for the area, sub+0x10 is likely the NPC array base)");
        }

        Log("[FieldTracker] === END F4 DUMP ===");
        Speech.Say("Field object dump written to log.", true);
    }

    /// <summary>
    /// F5: dumps the NPC object array and first NPC struct bytes.
    /// Press ONCE far from any NPC (no CHECK!!), then ONCE near an NPC (CHECK!! visible).
    /// Compare — the field that changes inside the NPC struct is the "player nearby" flag.
    ///
    /// 0x1411AB330 holds 11 pointer-slots to NPC objects (heap, each 0x90 bytes).
    /// We dump slots + the first valid struct's 144 bytes (as 36 ints).
    /// Also: F3 dumps the full segMisc struct (512 bytes) for the same comparison.
    /// </summary>
    private static void DumpInteractionCandidates()
    {
        Log("[FieldTracker] === F5 INTERACTION CANDIDATES DUMP ===");

        // ── Entity chain (from Ghidra analysis of fieldObjPtr write site) ─────────
        // Code at 0x1402CDB0D does:
        //   rax = *_segMiscPtr        (segMisc struct)
        //   rbp = *(rax + 8)          (pointer at segMisc[+8])
        //   rbp = *(rbp + 0xa0)       (entity/actor struct pointer)
        // Reads from entity: +0x164 float, +0x170 sub-ptr, +0x178 8-bytes, +0x184 speed float
        // One of +0x10..+0x160 likely holds the X, Y, Z world position.
        if (_segMiscPtr != null && *_segMiscPtr != null)
        {
            var segMiscBase = (byte*)(*_segMiscPtr);
            long objAVal = *(long*)(segMiscBase + 8);
            ulong objA = (ulong)objAVal;
            Log($"[FieldTracker] segMisc+8 (objA ptr) = 0x{objA:X}");

            if (objA >= 0x10000UL && objA <= 0x0007FFFFFFFFFFFFUL)
            {
                // Dump objA struct itself (1024 bytes) — this is the PARENT object.
                // Looking for player X/Z position offsets that change when walking.
                {
                    var sb = new System.Text.StringBuilder();
                    byte* op = (byte*)objA;
                    sb.AppendLine($"[FieldTracker] objA struct at 0x{objA:X} (1024 bytes, int + float):");
                    for (int i = 0; i < 256; i++)
                    {
                        int   ival = *(int*)  (op + i * 4);
                        float fval = *(float*)(op + i * 4);
                        string floatStr = LooksLikeCoord(fval) ? $"  << float: {fval:F3}" : "";
                        if (ival != 0)
                            sb.AppendLine($"  +{i * 4:D4} (idx {i:D3}): {ival,12}  0x{(uint)ival:X8}{floatStr}");
                    }
                    Log(sb.ToString());
                }

                long entityVal = *(long*)((byte*)objA + 0xa0);
                ulong entity = (ulong)entityVal;
                Log($"[FieldTracker] objA+0xa0 (entity/map-desc ptr) = 0x{entity:X}");
            }
            else Log($"[FieldTracker] objA ptr invalid: 0x{objA:X}");
        }
        else Log("[FieldTracker] _segMiscPtr not ready");

        // ── Global range around NPC array ─────────────────────────────────────────
        Log("[FieldTracker] Global range 0x1411AB200..0x1411AB600 (128 ints):");
        {
            var sb = new System.Text.StringBuilder();
            int* g = (int*)0x1411AB200L;
            for (int i = 0; i < 128; i++)
            {
                int val = g[i];
                if (val != 0)
                    sb.AppendLine($"  +{i * 4:D3} [0x{0x1411AB200 + i * 4:X}]: {val}  0x{(uint)val:X8}");
            }
            Log(sb.Length > 0 ? sb.ToString() : "  (all zero)");
        }

        Log("[FieldTracker] === END F5 DUMP ===");
        Speech.Say("Interaction dump written to log.", true);
    }

    private static unsafe void ReadAndLog(string label, int* addr)
    {
        // All these addresses are in P4G's data section (ASLR disabled), safe to read directly
        Log($"[FieldTracker] {label} = {*addr}");
    }

    /// <summary>
    /// F6: toggles live NPC struct monitor. While active, polls all 11 NPC slots every 100ms
    /// and logs immediately when any int value changes. Walk near/far NPCs to see what flips.
    /// Also monitors the segMisc struct for changes.
    /// </summary>
    private void NpcMonitor()
    {
        // Previous snapshot: slot index → array of int values
        int[]?[] prevSlots = new int[]?[11];
        int[]? prevMisc = null;
        int[]? prevChar = null;
        int[]? prevFieldObj = null;
        int[]? prevEntity = null;
        bool wasActive = false;

        while (true)
        {
            Thread.Sleep(100);

            if (!_npcMonitorActive)
            {
                if (wasActive)
                {
                    // Reset snapshots when deactivated so next run starts fresh
                    for (int i = 0; i < prevSlots.Length; i++) prevSlots[i] = null;
                    prevMisc = null;
                    prevChar = null;
                    prevFieldObj = null;
                    prevEntity = null;
                    wasActive = false;
                }
                continue;
            }
            wasActive = true;

            try
            {
                // Monitor NPC slots (64 ints = 256 bytes each)
                long* arr = (long*)0x1411AB330L;
                for (int slot = 0; slot < 11; slot++)
                {
                    ulong npcAddr = (ulong)arr[slot];
                    if (npcAddr < 0x10000UL || npcAddr > 0x0007FFFFFFFFFFFFUL) continue;

                    int[] cur = new int[64]; // 64 ints = 256 bytes
                    byte* p = (byte*)npcAddr;
                    for (int i = 0; i < 64; i++) cur[i] = *(int*)(p + i * 4);

                    int[]? prev = prevSlots[slot];
                    if (prev != null)
                    {
                        for (int i = 0; i < 64; i++)
                        {
                            if (cur[i] != prev[i])
                            {
                                float fNew = BitConverter.Int32BitsToSingle(cur[i]);
                                string floatStr = LooksLikeCoord(fNew) ? $"  << float: {fNew:F3}" : "";
                                Log($"[NpcMon] slot[{slot}] +{i*4:D3} (idx {i:D2}) CHANGED: {prev[i]} → {cur[i]}  (0x{(uint)prev[i]:X8} → 0x{(uint)cur[i]:X8}){floatStr}");
                            }
                        }
                    }
                    prevSlots[slot] = cur;
                }

                // Monitor the object at *(0x1411AB2C0) — possible player/character struct (64 ints = 256 bytes)
                ulong charAddr = (ulong)*(long*)0x1411AB2C0L;
                if (charAddr >= 0x10000UL && charAddr <= 0x0007FFFFFFFFFFFFUL)
                {
                    int[] curChar = new int[64];
                    byte* pc = (byte*)charAddr;
                    for (int i = 0; i < 64; i++) curChar[i] = *(int*)(pc + i * 4);
                    if (prevChar != null)
                    {
                        for (int i = 0; i < 64; i++)
                        {
                            if (curChar[i] != prevChar[i])
                            {
                                float fNew = BitConverter.Int32BitsToSingle(curChar[i]);
                                string floatStr = LooksLikeCoord(fNew) ? $"  << float: {fNew:F3}" : "";
                                Log($"[NpcMon] charObj +{i*4:D3} (idx {i:D2}) CHANGED: {prevChar[i]} → {curChar[i]}  (0x{(uint)prevChar[i]:X8} → 0x{(uint)curChar[i]:X8}){floatStr}");
                            }
                        }
                    }
                    prevChar = curChar;
                }

                // Monitor segMisc (first 32 ints = 128 bytes of known non-zero area)
                if (_segMiscPtr != null && *_segMiscPtr != null)
                {
                    var seg = *_segMiscPtr;
                    int[] curMisc = new int[32];
                    for (int i = 0; i < 32; i++) curMisc[i] = seg[i];
                    if (prevMisc != null)
                    {
                        for (int i = 0; i < 32; i++)
                        {
                            if (curMisc[i] != prevMisc[i])
                                Log($"[NpcMon] segMisc +{i*4:D3} (idx {i:D2}) CHANGED: {prevMisc[i]} → {curMisc[i]}");
                        }
                    }
                    prevMisc = curMisc;
                }

                // Monitor field object struct (128 ints = 512 bytes)
                // This is the most likely place for the "near interactable" flag.
                if (_fieldObjPtr != null)
                {
                    void* obj = *_fieldObjPtr;
                    ulong foAddr = (ulong)obj;
                    if (foAddr >= 0x10000UL && foAddr <= 0x0007FFFFFFFFFFFFUL)
                    {
                        int[] curFO = new int[128];
                        byte* pf = (byte*)obj;
                        for (int i = 0; i < 128; i++) curFO[i] = *(int*)(pf + i * 4);
                        if (prevFieldObj != null)
                        {
                            for (int i = 0; i < 128; i++)
                            {
                                if (curFO[i] != prevFieldObj[i])
                                    Log($"[NpcMon] fieldObj +{i*4:D3} (idx {i:D3}) CHANGED: {prevFieldObj[i]} → {curFO[i]}  (0x{(uint)prevFieldObj[i]:X8} → 0x{(uint)curFO[i]:X8})");
                            }
                        }
                        prevFieldObj = curFO;
                    }
                }

                // Monitor objA struct via: *_segMiscPtr + 8 → objA (256 ints = 1024 bytes).
                // The entity at objA+0xa0 is a static map-descriptor; objA itself may contain
                // the player's transform/position fields that change when walking.
                if (_segMiscPtr != null && *_segMiscPtr != null)
                {
                    var segBase = (byte*)(*_segMiscPtr);
                    long objAVal = *(long*)(segBase + 8);
                    ulong objA = (ulong)objAVal;
                    if (objA >= 0x10000UL && objA <= 0x0007FFFFFFFFFFFFUL)
                    {
                        int[] curEnt = new int[256];
                        byte* pe = (byte*)objA;
                        for (int i = 0; i < 256; i++) curEnt[i] = *(int*)(pe + i * 4);
                        if (prevEntity != null)
                        {
                            for (int i = 0; i < 256; i++)
                            {
                                if (curEnt[i] != prevEntity[i])
                                {
                                    float fNew = BitConverter.Int32BitsToSingle(curEnt[i]);
                                    string floatStr = LooksLikeCoord(fNew) ? $"  << float: {fNew:F3}" : "";
                                    Log($"[NpcMon] objA +{i*4:D4} (idx {i:D3}) CHANGED: {prevEntity[i]} → {curEnt[i]}  (0x{(uint)prevEntity[i]:X8} → 0x{(uint)curEnt[i]:X8}){floatStr}");
                                }
                            }
                        }
                        prevEntity = curEnt;
                    }
                }
            }
            catch (Exception ex) { Log($"[NpcMon] error: {ex.Message}"); }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the float looks like a plausible world-space coordinate
    /// (not NaN, not infinity, not zero, and within a reasonable game-world range).
    /// Used to annotate memory dumps so position offsets stand out.
    /// </summary>
    private static bool LooksLikeCoord(float f) =>
        !float.IsNaN(f) && !float.IsInfinity(f) && MathF.Abs(f) > 0.01f && MathF.Abs(f) < 100_000f;

    // ── Win32 ─────────────────────────────────────────────────────────────

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        nint hProcess, nint lpBaseAddress, void* lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    private static readonly nint _selfHandle = GetCurrentProcess();

    /// <summary>
    /// AV-safe read of `size` bytes from `addr` into `buf`. Returns true only
    /// if every byte was read. Slower than direct dereferencing (it's a
    /// syscall) but the OS catches access violations inside the kernel and
    /// returns false instead of terminating the process — which is what we
    /// need when scanning random memory offsets that may straddle stack guard
    /// pages or other mapped sections that VirtualQuery reports as RW but
    /// which still fault on direct read.
    /// </summary>
    internal static unsafe bool SafeRead(nint addr, void* buf, int size)
    {
        if (addr == 0 || size <= 0) return false;
        return ReadProcessMemory(_selfHandle, addr, buf, (nuint)size, out nuint read)
            && read == (nuint)size;
    }

    internal static unsafe bool SafeReadPtr(nint addr, out nint value)
    {
        nint v;
        bool ok = SafeRead(addr, &v, sizeof(nint));
        value = ok ? v : 0;
        return ok;
    }

    internal static unsafe bool SafeReadShort(nint addr, out short value)
    {
        short v;
        bool ok = SafeRead(addr, &v, sizeof(short));
        value = ok ? v : (short)0;
        return ok;
    }

    internal static unsafe bool SafeReadFloat(nint addr, out float value)
    {
        float v;
        bool ok = SafeRead(addr, &v, sizeof(float));
        value = ok ? v : 0f;
        return ok;
    }

    // AccessViolationException cannot be caught on .NET 9 — it terminates the process.
    // Every unsafe memory read on a value that might not be a real pointer must go through
    // this guard first. Mirrors the implementation in ShopMenu/Item.
    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48;
        const int OFF_STATE = 32;
        const int OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD) != 0) return false;
        return true;
    }

    // ── Name tables ───────────────────────────────────────────────────────

    private static string GetAreaName(int major, int minor) => major switch
    {
        6 => minor switch
        {
            1  => "School, Front Entrance",
            3  => "School Hallway",
            4  => "School Hallway, Second Floor",
            5  => "School Courtyard",
            6  => "Classroom 2-2",
            7  => "Classroom 2-1",
            8  => "Classroom 2-3",
            10 => "School Library",
            11 => "School Gym",
            12 => "Home Economics Room",
            13 => "Practice Building",
            14 => "School Rooftop",
            15 => "School Gate",
            _  => $"Yasogami High, Area {minor}",
        },
        7 => minor switch
        {
            1 => "Dojima Residence",
            2 => "Dojima Residence, Living Room",
            3 => "Your Room",
            4 => "Dojima Residence, Hallway",
            _ => $"Dojima Residence, Area {minor}",
        },
        8 => minor switch
        {
            1 => "Shopping District, North",
            2 => "Shopping District, South",
            3 => "Velvet Room", // user-confirmed 2026-06-14 (was mislabeled "Souzai Daigaku")
            4 => "Daidara Metalworks, Outside",
            5 => "Daidara Metalworks, Inside",
            6 => "Shopping District, Side Alley",
            // confirmed via call_street2shrine → CALL_FIELD(8, 9) in f008.bf
            9 => "Tatsuhime Shrine",
            _ => $"Shopping District, Area {minor}",
        },
        9 => minor switch
        {
            1 => "Junes, Food Court",
            2 => "Junes, Entrance",
            3 => "Junes, Electronics Department",
            4 => "Junes, West Side",
            _ => $"Junes, Area {minor}",
        },
        10 => minor switch
        {
            1 => "Samegawa Flood Plain",
            2 => "Samegawa Riverbank",
            _ => $"Samegawa Area {minor}",
        },
        // AUDIT 2026-06-12 (overworld phase): majors 11/17/18 were WRONG —
        // identified from each major's decompiled field-script proc names
        // (fd0XX flows): 11 has city_station/city_theater/city_cafe = OKINA
        // CITY (was "Samegawa Riverbank"); 18 has entrance_lodge/
        // snowmountain_* = the ski-trip area (was "Moel Gas Station" — Moel
        // is inside the shopping district map, not its own major); 17 has
        // farmshop (was "Tatsuhime Shrine" — the shrine is field 8/9).
        11 => "Okina City",
        12 => "Shiroku Store",
        13 => "Daidara Metalworks",
        15 => "Hanamura Residence",
        17 => $"Farm area {minor}",
        18 => minor == 1 ? "Ski Lodge" : "Snow Mountain",
        // Dungeon 2 = Steamy Bathhouse. Entrance/changing area = (24,1); the in-binary names are
        // "Steamy Bathhouse, Bath #N" + "Bathhouse, Changing Area" (latter would strip to just
        // "Bathhouse"), so a major fallback keeps every floor consistent as "Steamy Bathhouse" when
        // the live banner scan can't read one (e.g. the entrance, where banner="" → was "???").
        // Dungeon 2 = Steamy Bathhouse spans THREE majors (same split as Yukiko gate=23/maze=40/
        // scripted=60): 24 = entrance/changing area, 41 = procedural maze floors, 61 = SCRIPTED floors
        // (61_2 = Bath #3, 61_3 = next scripted/boss — both can have an empty banner → were "???").
        // 240/241 = battle majors, handled before this switch.
        24 or 41 or 61 => "Steamy Bathhouse",
        >= 60 and <= 69 => "???",   // scripted/event floors show "???" on screen when they have no banner
        _ => GetDungeonAreaName(major, minor),
    };

    /// <summary>
    /// Dungeon-area name resolver. The game's field MAJOR numbers don't
    /// match customSubMenu's CALL_DUNGEON "floor IDs" (5/20/40/etc.);
    /// those are a separate numbering system. Same major can map to
    /// multiple floors via different minor — confirmed in-game by walking
    /// from Yukiko entrance to 2F:
    ///   (23, 1) = "Yukiko's Castle, Gate"   (entrance lobby)
    ///   (23, 3) = "Yukiko's Castle 2F"      (different minor, same major)
    ///   (40, 0) = "Yukiko's Castle 1F"
    ///
    /// Pattern guess (NOT confirmed): major=23 holds the dungeon shell
    /// with even minors stepping up the floors (1=Gate, 3=2F, 5=3F,
    /// 7=4F, ...), while major=40 is a separate first-floor sub-area.
    /// Don't trust the guess — only the (major, minor) pairs explicitly
    /// listed in the dictionary below are verified.
    ///
    /// Names come from the in-binary string table at 0x140931940+ — the
    /// game indexes into it for the on-screen banner.
    /// </summary>
    private static readonly Dictionary<(int major, int minor), string> _confirmedAreaNames = new()
    {
        [(23, 1)] = "Yukiko's Castle, Gate",
        [(23, 3)] = "Yukiko's Castle 2F",
        [(40, 0)] = "Yukiko's Castle 1F",
        [(20, 1)] = "Entrance", // TV-world hub entrance (user-confirmed 2026-06-14)
    };

    private static string GetDungeonAreaName(int major, int minor)
    {
        if (_confirmedAreaNames.TryGetValue((major, minor), out var name))
            return name;

        // battle majors (per-dungeon, 240-249) = battle; callers normally handle it but include for clarity
        if (IsBattleMajor(major)) return $"Battle (floor {minor})";

        // Unknown dungeon floor: the game itself shows "???" on screen for these
        // (hidden/mystery floors), so match it instead of speaking a major/minor
        // pair or a stale dungeon name. Non-dungeon areas keep the raw pair.
        return major >= 20 ? "???" : $"Area {major}-{minor}";
    }

    // Last dungeon NAME announced (used by the M-key re-read). We announce the
    // dungeon name on every floor (user pref 2026-06-17) — NOT the floor number,
    // which the game stores in a hard-to-disambiguate transient banner. Since all
    // floors of a dungeon share the name, ANY active banner gives the right answer,
    // so no per-floor disambiguation is needed.
    private static volatile string? _lastDungeonFloorName;
    private static volatile int _lastDungeonFloorMajor = -1;   // dungeon the cached name belongs to

    // SCRIPTED floors the user wants announced by their DISTINCT name (not the stripped dungeon
    // name) — so a recognisable fixed-layout floor is identifiable by ear. Keyed (major,minor).
    private static readonly Dictionary<(int, int), string> _floorNameOverrides = new()
    {
        [(61, 2)] = "Steamy Bathhouse, Bath number 3",   // scripted Bathhouse floor (events recorded)
        [(61, 3)] = "Steamy Bathhouse, Bath number 7",   // next scripted floor (user-confirmed 2026-06-27)
    };

    /// <summary>
    /// Background: read the game's banner string for any active floor of the
    /// current dungeon and announce the DUNGEON name (floor part stripped).
    /// Retries because the banner spawns slightly after the area-id flips.
    /// </summary>
    private void ResolveAndAnnounceFloor(int major, int minor, string? prev)
    {
        try
        {
            // Verbatim name for a recognised scripted floor wins over the banner/dungeon name.
            if (_floorNameOverrides.TryGetValue((major, minor), out var fixedName))
            {
                _lastDungeonFloorName = fixedName; _lastDungeonFloorMajor = major;
                Log($"[FieldTracker] Area resolved -> major={major} minor={minor}: override \"{fixedName}\"");
                Speech.Say(fixedName, true);
                return;
            }
            string? banner = null;
            for (int attempt = 0; attempt < 4 && banner == null; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 600 : 400);
                if (CurrentMajor != major || CurrentMinor != minor) return;   // moved on
                var active = DungeonFloorName.ScanActiveBanners();
                if (active.Count > 0) banner = active[0].name;   // any floor of this dungeon → same name
            }
            // Empty scan: keep the cached name ONLY if it belongs to THIS dungeon
            // (e.g. returning from a battle to the same floor — the banner doesn't
            // re-show). A DIFFERENT dungeon whose banner also fails must NOT inherit
            // the previous one's name (the old bug: 68_1 spoke "100 dash 3" /
            // "Yukiko's Castle"); it falls back to the table → "???".
            string? resolved = banner != null ? DungeonNameOf(banner) : null;
            // Known STALE banner: a leftover "Yukiko's Castle, Gate" lingers in memory
            // and gets scanned on floors that have no banner of their own. Reject it
            // when we're not actually in a Yukiko major (23/40) → falls through to "???".
            if (resolved == "Yukiko's Castle" && major != 23 && major != 40) resolved = null;
            string name = resolved
                ?? (_lastDungeonFloorName != null && _lastDungeonFloorMajor == major
                    ? _lastDungeonFloorName
                    : DungeonNameOf(GetAreaName(major, minor)));
            if (CurrentMajor != major || CurrentMinor != minor) return;       // moved on
            _lastDungeonFloorName = name;
            _lastDungeonFloorMajor = major;
            Log($"[FieldTracker] Area resolved -> major={major} minor={minor}: banner=\"{banner}\" -> \"{name}\"");
            Speech.Say(name, true);
        }
        catch (System.Exception ex) { Log($"[FieldTracker] floor scan error: {ex.Message}"); }
    }

    /// <summary>Strip the floor/sub-area suffix off a banner to get the DUNGEON
    /// name: "Yukiko's Castle 3F" → "Yukiko's Castle"; "Heaven, Paradise #2" →
    /// "Heaven"; "Void Quest Chapter 1" → "Void Quest". Unique area names with no
    /// floor suffix (e.g. "Magatsu Inaba") are returned unchanged.</summary>
    private static string DungeonNameOf(string banner)
    {
        int comma = banner.IndexOf(',');
        if (comma > 0) return banner[..comma];                 // ", Gate" / ", Bath #N" / ", Paradise #N"
        var p = banner.Split(' ');
        int n = p.Length;
        // trailing floor token: 1F / 10F / B2F
        if (n >= 2 && IsFloorTok(p[n - 1])) return string.Join(' ', p, 0, n - 1);
        // trailing "<Word> <number>": Chapter 1 / World 1 / Path 2 / Floor 3
        if (n >= 3 && p[n - 1].Length > 0 && p[n - 1].All(char.IsDigit) &&
            (p[n - 2] is "Chapter" or "World" or "Path" or "Floor"))
            return string.Join(' ', p, 0, n - 2);
        return banner;
    }

    private static bool IsFloorTok(string t)
    {
        int i = 0;
        if (i < t.Length && t[i] == 'B') i++;
        int d = 0;
        while (i < t.Length && char.IsDigit(t[i])) { i++; d++; }
        return d > 0 && i == t.Length - 1 && t[i] == 'F';
    }


    /// <summary>
    /// Time period names confirmed by F2 struct dump (April 16 Evening save):
    ///   struct +02 = 5 when game shows "Evening" → period 5 = Evening ✓
    ///   struct +02 = 0 when game shows "Early Morning" → period 0 = Early Morning ✓
    /// Others are best guesses — press F2 and compare struct +02 value against
    /// whatever the game's top-right header shows to verify each one.
    /// </summary>
    private static string GetTimeName(short period) => period switch
    {
        0 => "Early Morning",
        1 => "Morning",
        2 => "Lunchtime",
        3 => "Daytime",     // USER-CONFIRMED in-game label 2026-06-12 (not "Afternoon")
        4 => "After School",
        5 => "Evening",
        6 => "Night",
        7 => "Late Night",
        _ => $"Time period {period}",
    };

    // The game stores current day at struct +00 (CurrentDay).
    // Confirmed: day=15 on April 16 Evening save (F2 dump ✓).
    // Epoch April 1, 2011: AddDays(15) = April 16 ✓
    private static readonly DateTime _epoch = new DateTime(2011, 4, 1);

    private static string FormatDate(short gameDay)
    {
        if (gameDay <= 0) return "";
        var date = _epoch.AddDays(gameDay);
        return $"{date:MMMM d}, {date.DayOfWeek}";
    }
}
