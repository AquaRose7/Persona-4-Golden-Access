using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Components.CommandMenus;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.CommandMenu;

/// <summary>
/// A class that hooks the command menu and its sub menus, adding screen reader support and stuff
/// </summary>
internal unsafe class CommandMenu
{
    private static readonly Dictionary<SubCommandMenu, string> _subMenuNames = new()
    {
        { SubCommandMenu.Skill, "Skill Menu" },
        { SubCommandMenu.Item, "Item Menu" },
        { SubCommandMenu.Equip, "Equipment Menu" },
        { SubCommandMenu.Persona, "Persona Menu" },
        { SubCommandMenu.Status, "Status Menu" },
        { SubCommandMenu.SocialLink, "Social Link Menu" },
        { SubCommandMenu.Quest, "Quest Menu" },
        { SubCommandMenu.System, "System Menu" }
    };

    private IHook<DrawMainMenuDelegate> _drawMainMenuHook;

    private SubCommandMenu _lastSubMenu = SubCommandMenu.None;

    private PersonaMenu _personaMenu;
    private IHook<StartDelegate> _startHook;

    internal CommandMenu(IReloadedHooks hooks)
    {
        _personaMenu = new PersonaMenu(hooks);

        SigScan("40 53 57 48 81 EC 28 01 00 00", "CommandMenu::DrawMainMenu",
            address =>
            {
                _drawMainMenuHook = hooks.CreateHook<DrawMainMenuDelegate>(DrawMainMenu, address).Activate();
            });

        SigScan("40 53 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B 0D ?? ?? ?? ?? 45 33 DB",
            "CommandMenu::Start",
            address => { _startHook = hooks.CreateHook<StartDelegate>(Start, address).Activate(); });
    }

    private nuint Start(nuint task)
    {
        _lastSubMenu = SubCommandMenu.None;
        return _startHook.OriginalFunction(task);
    }

    private nuint DrawMainMenu(CommandMenuStruct* info)
    {
        var res = _drawMainMenuHook.OriginalFunction(info);

        var subMenu = info->SelectedMenu;
        if (subMenu != _lastSubMenu)
        {
            var menuName = _subMenuNames[subMenu];
            LogDebug($"Outputting selected command menu \"{menuName}\"");
            TitleMenu.GameEventFired();
            Speech.Say(menuName, true);
            _lastSubMenu = subMenu;
        }

        return res;
    }

    private delegate nuint StartDelegate(nuint task);

    private delegate nuint DrawMainMenuDelegate(CommandMenuStruct* info);

    [StructLayout(LayoutKind.Explicit)]
    private struct CommandMenuStruct
    {
        [FieldOffset(0x14)] internal SubCommandMenu SelectedMenu;
    }

    private enum SubCommandMenu : int
    {
        None = -1,
        Skill = 0,
        Item = 1,
        Equip = 2,
        Persona = 3,
        Status = 4,
        SocialLink = 5,
        Quest = 6,
        System = 7
    }
}