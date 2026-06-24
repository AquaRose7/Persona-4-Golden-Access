using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Native.Party;
using static p4g64.accessibility.Native.Persona;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.CommandMenus;

/// <summary>
/// Hooks for the Persona menu
/// </summary>
internal unsafe class PersonaMenu
{
    private IHook<DrawContentsDelegate> _drawHook;

    private short _lastSelected = -1;

    internal PersonaMenu(IReloadedHooks hooks)
    {
        SigScan("4C 8B DC 55 53 57 41 56 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC B0 01 00 00", "PersonaMenu::DrawContents",
            address => { _drawHook = hooks.CreateHook<DrawContentsDelegate>(Draw, address).Activate(); });
    }

    private nuint Draw(PersonaMenuInfo* info)
    {
        var res = _drawHook.OriginalFunction(info);

        var selected = info->SelectedPersona;
        if (selected != _lastSelected)
        {
            PersonaInfo persona = (&PartyInfo->ProtagPersonas)[selected];
            var id = persona.PersonaId;
            var name = GetName(id);
            var level = persona.Level;

            var text = $"{name} level {level}";
            Log($"Outputting selected Persona \"{text}\"");
            Speech.Say(text, true);

            _lastSelected = selected;
        }

        return res;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PersonaMenuInfo
    {
        [FieldOffset(0x56)] internal short SelectedPersona;
    }

    private delegate nuint DrawContentsDelegate(PersonaMenuInfo* info);
}