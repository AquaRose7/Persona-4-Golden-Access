using DavyKager;
using Reloaded.Hooks.Definitions;
using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the cursor-update function used by Daidara's "buy for character" select screen.
///
/// DISCOVERY:
///   CE scan (cursor value 0/1) → "Find what writes" → mov [rdx+0xAC], ax at VA 0x1402AD5B9
///   Function prologue: VA 0x1402AD4E0
///   SigScan: "40 53 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ??
///             48 8B 03 44 8B 43 08 48 8B 50 48 48 8B 02 4C 8B 48 48 41 0F BF 41 04
///             83 C0 F9 83 F8 0D 0F 87"
///   Unique in .shared (1 match).
///
/// STRUCT / POINTER CHAIN (confirmed at runtime):
///   arg0          → first qword = ptr A
///   ptr A + 0x48  → rdx  (character-select menu struct)
///   rdx  + 0xAC   → short  cursor  (0 = first member, 1 = second, …)
/// </summary>
internal unsafe class DaidaraCharSelect
{
    private IHook<CursorUpdateDelegate> _hook = null!;
    private short _lastCursor = -1;

    // Party member names in join order. Slot 0 is the protagonist whose
    // CUSTOM name lives in a game global we haven't located yet — "You" is
    // always true meanwhile (user request 2026-06-12: was reading "Yu"
    // instead of their custom name).
    private static readonly string[] MemberNames =
        { "You", "Yosuke", "Chie", "Yukiko", "Kanji", "Rise", "Naoto", "Teddie" };

    internal DaidaraCharSelect(IReloadedHooks hooks)
    {
        SigScan(
            "40 53 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? " +
            "48 8B 03 44 8B 43 08 48 8B 50 48 48 8B 02 4C 8B 48 48 41 0F BF 41 04 " +
            "83 C0 F9 83 F8 0D 0F 87",
            "DaidaraCharSelectUpdate",
            address =>
            {
                Log($"[DaidaraCharSelect] Hook at 0x{address:X}");
                _hook = hooks.CreateHook<CursorUpdateDelegate>(OnCursorUpdate, address).Activate();
            });
    }

    private void OnCursorUpdate(void* arg0)
    {
        _hook.OriginalFunction(arg0);

        if (!IsInDaidara()) return;
        // only speak while the char-select screen is actually up (shop state
        // 0x08) — this hook also fires once at shop open / during teardown
        if (ShopMenu.ShopState != 0x08) return;

        // Follow pointer chain confirmed by CE:  *arg0 → [+0x48] → cursor at +0xAC
        nint ptrA = *(nint*)arg0;
        if (ptrA == 0) return;
        nint rdx = *(nint*)(ptrA + 0x48);
        if (rdx == 0) return;

        short cursor = *(short*)(rdx + 0xAC);
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        string name = cursor == 0
            ? ShopMenu.ProtagonistName()
            : cursor > 0 && cursor < MemberNames.Length
                ? MemberNames[cursor]
                : $"Member {cursor}";

        Log($"[DaidaraCharSelect] cursor={cursor} → {name}");
        Speech.Say(name, true);
        // Signal ShopMenu that the char-select screen is active (not the item list).
        // This prevents stale item announces when the user backs out through here.
        ShopMenu.CharSelectIsActive = true;
    }

    private static bool IsInDaidara() =>
        FieldTracker.CurrentMajor == 8 &&
        (FieldTracker.CurrentMinor == 4 || FieldTracker.CurrentMinor == 5);

    private delegate void CursorUpdateDelegate(void* arg0);
}
