using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the title menu render function to speak whichever option the cursor is on.
///
/// Cursor is a single value 0-5:
///   0 = New Game   3 = Config
///   1 = Load Game  4 = TV Listing
///   2 = Continue   5 = Exit
///
/// No F1 or scanning needed — works automatically and with gamepad.
/// F6 re-reads the current option.
/// </summary>
internal unsafe class TitleMenu : IDisposable
{
    private static readonly string[] MenuItems =
    {
        "New Game", "Load Game", "Continue",
        "Config", "TV Listing", "Exit"
    };

    private IHook<TitleMenuRenderDelegate>? _hook;

    // Initialised to 0 so the cursor-at-New-Game state during the "Press Start"
    // screen doesn't trigger a false announcement. Resets to -1 when in-game
    // events fire so the option is re-announced when returning to this menu.
    private static short _lastCursor = 0;

    [StructLayout(LayoutKind.Explicit)]
    private struct TitleMenuStruct
    {
        [FieldOffset(0x14)]   public short Cursor; // 0-5
        // State byte the render gates a draw element on (decompiled
        // FUN_1403CAF10: `if ((*(byte*)(p+0x1A28) & 2) == 0)`). Observed live:
        // on the "press to continue" screen the 0x02 bit NEVER sets (only
        // 0x00/0x01); once the menu is navigable it TOGGLES 0x00<->0x02 every
        // frame (an animation/blink). So a single frame can't tell the two
        // apart — we LATCH active the first time the 0x02 bit is seen.
        [FieldOffset(0x1A28)] public byte State;
    }

    // Latched true the first time the menu's 0x02 animation bit is seen.
    // Press-to-continue never sets it; the live menu toggles it every frame.
    private bool _menuActive;
    private bool _promptedContinue;

    private delegate void TitleMenuRenderDelegate(nint param1, TitleMenuStruct* pMenu);

    internal TitleMenu(IReloadedHooks hooks)
    {
        SigScan(
            "48 8B C4 48 89 58 08 48 89 68 18 48 89 70 20 57 41 54 41 55 41 56 41 57 48 81 EC 40 01 00 00",
            "TitleMenu::Render",
            address =>
            {
                _hook = hooks.CreateHook<TitleMenuRenderDelegate>(OnRender, address).Activate();
                Log("Title menu render hook active.");
            });
    }

    /// <summary>
    /// Called by other components when in-game events fire.
    /// Resets the cursor so the option is re-announced when returning to the title menu.
    /// </summary>
    internal static void GameEventFired() => _lastCursor = -1;

    // ── Render hook ───────────────────────────────────────────────────────

    private void OnRender(nint param1, TitleMenuStruct* pMenu)
    {
        _hook!.OriginalFunction(param1, pMenu);

        if (pMenu == null) return;
        var cursor = pMenu->Cursor;
        var state = pMenu->State;

        // Latch active on the first 0x02 frame. On that transition reset the
        // cursor so the highlighted option announces as the menu slides in.
        if ((state & 2) != 0 && !_menuActive) { _menuActive = true; _lastCursor = -1; }

        // Not navigable yet: the options are drawn but input does nothing
        // ("Press to continue"). Announce a prompt, not a misleading option.
        if (!_menuActive)
        {
            if (!_promptedContinue) { _promptedContinue = true; Speech.Say("Press any button to continue", true); }
            return;
        }

        if (cursor < 0 || cursor > 5) return;
        if (cursor == _lastCursor)    return;

        _lastCursor = cursor;
        LogDebug($"Title menu: cursor={cursor} -> {MenuItems[cursor]}");
        Speech.Say(MenuItems[cursor], true);
    }

    public void Dispose() { }
}
