using System.Runtime.InteropServices;
using System.Text;
using p4g64.accessibility.Native.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

public unsafe class Skill
{
    private static ActiveSkillData** _activeSkillData;
    private static SkillElements** _skillElements;

    internal static void Initialise()
    {
        // Skill NAMES: per-language tables selected by the game's language id — see
        // GameText.SkillName (2026-09-07). The old "EnglishSkillNamesPtr" sig-scan bound the
        // ENGLISH branch only, whose cell is null in every other language (crash in SkillSelect).

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

    /// <summary>Target scope from ActiveSkillData+0x0C: 0 = single target, 1 = all targets
    /// (confirmed 2026-06-30 — Bufula 0x00 vs Mabufu 0x01). Side (enemies vs allies) is inferred
    /// from the element by the caller.</summary>
    internal static byte GetTargetScope(int skillId)
    {
        return ((byte*)GetActiveSkillData(skillId))[0x0C];
    }

    /// <summary>
    /// Gets the name of a skill in the GAME's language (the game's own per-language table,
    /// decoded with the active glyph table). Never throws; "" when the table isn't loaded.
    /// </summary>
    internal static string GetName(int skillId) => GameText.SkillName(skillId);

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
        return GameText.DecodeMsg((nint)page.Text, page.TextSize);   // 2026-09-09: function-code aware
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