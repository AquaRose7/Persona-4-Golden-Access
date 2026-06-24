using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the difficulty selection menu render function to speak whichever option the cursor is on.
///
/// Cursor is a short at offset +0x04:
///   0 = Very Easy   1 = Easy   2 = Normal   3 = Hard   4 = Very Hard
///
/// </summary>
internal unsafe class DifficultyMenu : IDisposable
{
    private static readonly string[] Options =
    {
        "Very Easy", "Easy", "Normal", "Hard", "Very Hard"
    };

    private IHook<RenderDelegate>? _hook;
    private short _lastCursor = -1;

    [StructLayout(LayoutKind.Explicit)]
    private struct DifficultyMenuStruct
    {
        [FieldOffset(0x04)] public short Cursor; // 0–4
    }

    private delegate void RenderDelegate(DifficultyMenuStruct* pMenu, nint param2, nint param3, nint param4);

    internal DifficultyMenu(IReloadedHooks hooks)
    {
        SigScan(
            "40 55 53 41 56 48 8D AC 24 F0 FE FF FF 48 81 EC 10 02 00 00",
            "DifficultyMenu::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Difficulty menu render hook active.");
            });

    }

    private void OnRender(DifficultyMenuStruct* pMenu, nint param2, nint param3, nint param4)
    {
        _hook!.OriginalFunction(pMenu, param2, param3, param4);

        if (pMenu == null) return;
        var cursor = pMenu->Cursor;
        if (cursor < 0 || cursor > 4) return;
        if (cursor == _lastCursor) return;

        var wasFirstRender = _lastCursor < 0;
        _lastCursor = cursor;

        // Don't announce on the very first render — the menu may be drawing
        // in the background before it's actually visible.
        if (wasFirstRender) return;

        LogDebug($"Difficulty menu: cursor={cursor} -> {Options[cursor]}");
        Speech.Say(Options[cursor], true);
    }

    public void Dispose() { }
}
