using System.Runtime.InteropServices;
using Reloaded.Hooks.ReloadedII.Interfaces;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

public unsafe class PartyMember
{
    private static GetSkillCostDelegate _getSkillCost;
    internal static GetSkillCostDelegate GetSkillCost => _getSkillCost;

    internal static void Initialise(IReloadedHooks hooks)
    {
        SigScan("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F1 0F B7 DA 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ??",
            "GetSkillCost",
            address => { _getSkillCost = hooks.CreateWrapper<GetSkillCostDelegate>(address, out _); });
    }

    // Will almost certainly need to actually use this later, leaving it empty for now since I'm lazy :)
    [StructLayout(LayoutKind.Explicit)]
    internal struct PartyMemberInfo
    {
    }

    internal delegate int GetSkillCostDelegate(PartyMemberInfo* member, int skillId);
}