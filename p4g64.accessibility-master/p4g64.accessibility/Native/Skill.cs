using System.Runtime.InteropServices;
using System.Text;
using p4g64.accessibility.Native.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

public unsafe class Skill
{
    private static EnglishSkillName** _englishNames;
    private static ActiveSkillData** _activeSkillData;
    private static SkillElements** _skillElements;

    internal static void Initialise()
    {
        SigScan("48 03 0D ?? ?? ?? ?? EB ?? 48 6B CE 13", "EnglishSkillNamesPtr",
            address => { _englishNames = (EnglishSkillName**)GetGlobalAddress(address + 3); });

        SigScan("48 8B 05 ?? ?? ?? ?? 0F B6 7C ?? ??", "ActiveSkillDataPtr",
            address => { _activeSkillData = (ActiveSkillData**)GetGlobalAddress(address + 3); });

        SigScan("48 8B 05 ?? ?? ?? ?? 0F B7 CF 0F BE 0C ??", "SkillElementsPtr",
            address => { _skillElements = (SkillElements**)GetGlobalAddress(address + 3); });
    }

    internal static ActiveSkillData* GetActiveSkillData(int skillId)
    {
        return &(*_activeSkillData)[skillId];
    }

    internal static ElementalType GetSkillElement(int skillId)
    {
        return (*_skillElements)[skillId].Element;
    }

    internal static SkillType GetSkillType(int skillId)
    {
        return (*_skillElements)[skillId].Type;
    }

    // TODO Support other languages

    /// <summary>
    /// Gets the name of a skill
    /// </summary>
    /// <param name="skillId">The ID of the skill</param>
    /// <returns>The name of the skill in English</returns>
    internal static string GetName(int skillId)
    {
        var namePtr = (*_englishNames)[skillId].Name;
        return Encoding.UTF8.GetString(namePtr, GetStringLength(namePtr, 0x15));
    }

    /// <summary>
    /// Gets the description of a skill
    /// </summary>
    /// <param name="skillId">The ID of the skill</param>
    /// <returns>The name of the skill in English</returns>
    internal static string GetDescription(int skillId)
    {
        var skillHelpDialog = Dialog.GetExecution(Dialog.HelpBmd.Skill);
        var bmd = skillHelpDialog->Info->Bmd;
        if (skillId > bmd->Header.DialogCount)
        {
            LogError(
                $"Unable to get the description for skill {skillId} as the bmd only has {bmd->Header.DialogCount} messages!");
            return "";
        }

        var messageDialog = (&bmd->DialogHeaders)[skillId].MessageDialog;
        if (messageDialog->PageCount < 1)
        {
            LogError($"Unable to get the description for skill {skillId} as the message has no pages!");
            return "";
        }

        var page = messageDialog->Pages; // First (and only) page
        return AtlusEncoding.P4.GetString(page.Text, page.TextSize).Replace('\n', ' ').Replace('\0', ' ').Trim();
    }

    internal struct EnglishSkillName
    {
        internal fixed byte Name[0x17];
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x2c)]
    internal struct ActiveSkillData
    {
        [FieldOffset(0)] internal uint CasterEffect;

        [FieldOffset(6)] internal SkillCostType CostType;
    }

    internal enum SkillCostType : byte
    {
        None = 0,
        HP = 1,
        SP = 2
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct SkillElements
    {
        [FieldOffset(0)] internal ElementalType Element;

        [FieldOffset(1)] internal SkillType Type;
    }

    internal enum ElementalType : byte
    {
        Physical = 0,
        Fire = 1,
        Ice = 2,
        Electric = 3,
        Wind = 4,
        Almighty = 5,
        Light = 6,
        Dark = 7,
        Panic = 8,
        Poison = 9,
        Feear = 10,
        Rage = 11,
        Unkown = 12,
        Exhaustion = 13,
        Enervation = 14,
        Silence = 15,
        Healing = 16,
        Support = 17,
        Special = 18
    }

    internal enum SkillType : byte
    {
        Active = 0,
        Passive = 1,
        Support = 2
    }
}