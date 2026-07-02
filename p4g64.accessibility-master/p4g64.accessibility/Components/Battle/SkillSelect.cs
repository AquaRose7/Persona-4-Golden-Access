using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

public unsafe class SkillSelect
{
    private IHook<DrawSkillListItemDelegate> _drawSkillListItemHook;
    private bool _lastDrawDescription = false;

    private short _lastSkillId = -1;

    internal SkillSelect(IReloadedHooks hooks)
    {
        SigScan("48 8B C4 56 57 48 81 EC C8 00 00 00", "DrawSkillListItem",
            address =>
            {
                _drawSkillListItemHook =
                    hooks.CreateHook<DrawSkillListItemDelegate>(DrawSkillListItem, address).Activate();
            });
    }

    private void DrawSkillListItem(Battle.BtlInfo* btl, int skillSlot, float param_3, float param_4, byte alpha,
        int isSelected, bool drawDescription)
    {
        _drawSkillListItemHook.OriginalFunction(btl, skillSlot, param_3, param_4, alpha, isSelected, drawDescription);

        if (isSelected == 0) return;

        // Speak skills only in the actual SKILL command — this drawer can render
        // in other contexts (it never fires for Tactics; that menu is drawn by
        // 0x1400E7020 → see SkillInfoSelect's tactics path).
        if (Battle.CurrentCommand != 4) return;

        var skillId = btl->ActiveMemberSkills[skillSlot];

        // Publish the highlighted skill's HP cost (refreshed every frame the row
        // is selected). If the player casts it, the caster's HP drop equal to
        // this cost is a SELF-COST — DamageMonitor must not read it as an enemy
        // attack. A non-HP skill highlighted clears the pending cost.
        try
        {
            if (Skill.GetActiveSkillData(skillId)->CostType == Skill.SkillCostType.HP)
            {
                var unit = btl->Turn->Unit;
                Battle.PendingHpCostUnit = (nint)unit;
                Battle.PendingHpCost = PartyMember.GetSkillCost(unit->PartyMemberInfo, skillId);
                Battle.PendingHpCostTick = Environment.TickCount64;
            }
            else Battle.PendingHpCost = 0;

            // Also remember the highlighted skill so the cast-time bubble that
            // merely echoes it isn't spoken twice (MessageBubble consumes this).
            Battle.PendingEchoSkillId = skillId;
            Battle.PendingEchoSkillTick = Environment.TickCount64;
            Battle.SelectedSkillId = skillId;   // persistent — TargetReader reads its element for weakness
        }
        catch { }
        if (skillId == _lastSkillId)
        {
            // Hack to get around the game's stupid hack that calls this twice when the help box is opening
            // The first time it's called with the correct opacity, the second it's called with 255 but drawDescription turned off
            // With this we only check the value when it's opening or closing
            if (alpha == 255)
            {
                // Output just the description if it was just toggled on
                if (!_lastDrawDescription && drawDescription)
                {
                    var description = Skill.GetDescription(skillId);
                    Log($"Outputting skill description \"{description}\"");
                    Speech.Say(description, true);
                }

                _lastDrawDescription = drawDescription;
            }

            return;
        }

        _lastSkillId = skillId;
        _lastDrawDescription = drawDescription;
        var skillName = Skill.GetName(skillId);

        var member = btl->Turn->Unit->PartyMemberInfo;
        var skillCost = PartyMember.GetSkillCost(member, skillId);
        var costType = Skill.GetActiveSkillData(skillId)->CostType;
        var skillType = Skill.GetSkillType(skillId);

        string text;
        if (skillType == Skill.SkillType.Passive)
        {
            text = $"{skillName}: Passive Skill. ";
        }
        else
        {
            var skillElement = Skill.GetSkillElement(skillId);
            var elementText =
                skillElement > Skill.ElementalType.Dark
                    ? "Support"
                    : skillElement.ToString(); // The icon does not differentiate between the non-damaging types
            // Scope (single/all) is already in the skill's description, so it's not repeated here.
            // The AoE affinity breakdown is spoken at the TARGET step (MultiTargetReader).
            text = $"{skillName}: {elementText} skill that costs {skillCost} {costType}. ";
        }

        if (drawDescription)
        {
            text += Skill.GetDescription(skillId);
        }

        Log($"Outputting skill text \"{text}\"");
        Speech.Say(text, true);
    }

    private delegate void DrawSkillListItemDelegate(Battle.BtlInfo* btl, int skillSlot, float param_3, float param_4,
        byte alpha, int isSelected, bool drawDescription);
}