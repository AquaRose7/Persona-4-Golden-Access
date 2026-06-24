using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Memory;
using Reloaded.Memory.Interfaces;
using System.Diagnostics;
using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// A class that hooks the title bar to remove the fps from it
/// </summary>
internal class TitleBar
{
    private IAsmHook _titleBarUpdateHook;

    internal TitleBar(IReloadedHooks hooks)
    {
        SigScan("0F 8D ?? ?? ?? ?? 33 D2 48 8D 4C 24 ??", "UpdateTitleBar", address =>
        {
            string[] function =
            {
                "use64",
                "cmp rax, rax" // prevent the title bar from updating
            };
            _titleBarUpdateHook = hooks.CreateAsmHook(function, address, AsmHookBehaviour.ExecuteFirst).Activate();
        });
    }
}
