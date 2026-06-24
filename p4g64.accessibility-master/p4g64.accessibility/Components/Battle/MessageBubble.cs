using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Native.Text.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// A class for hooking the message bubble that shows up at the top of the screen
/// in battle when using skills and analysing enemies
/// </summary>
internal unsafe class MessageBubble
{
    private IHook<DrawMessageBubbleDelegate> _drawBubbleHook;

    // De-dup on decoded CONTENT, not the TextStruct pointer: the game reuses the
    // same text buffer for successive messages, so a pointer compare silently ate
    // new lines (live 2026-06-10: an enemy's Dia bubble never spoke because the
    // pointer hadn't changed). _lastRenderTick re-arms after the bubble hides so
    // the same skill cast again later re-announces.
    private string _lastStr;
    private long _lastRenderTick;

    internal MessageBubble(IReloadedHooks hooks)
    {
        SigScan("40 55 56 41 54 41 55 41 56 48 81 EC 20 01 00 00", "Btl::UI::DrawMessageBubble",
            address =>
            {
                _drawBubbleHook = hooks.CreateHook<DrawMessageBubbleDelegate>(DrawMessageBubble, address).Activate();
            });
    }

    private nuint DrawMessageBubble(nuint param_1, BtlMessageBubble* bubble, nuint param_3)
    {
        var res = _drawBubbleHook.OriginalFunction(param_1, bubble, param_3);

        var text = bubble->Text;
        if (text != null)
        {
            long now = Environment.TickCount64;
            if (now - _lastRenderTick > 500) _lastStr = null;   // bubble was hidden — re-arm
            _lastRenderTick = now;

            var textStr = text->ToString();
            if (!string.IsNullOrWhiteSpace(textStr) && textStr != _lastStr)
            {
                _lastStr = textStr;

                // The target-cursor / info window also shows the enemy name, and
                // BattleLog speaks that with HP% + ordinal + down. Suppress the
                // bare duplicate here so the name isn't read twice. Also suppress
                // bubbles that only echo the player's own choice (command names,
                // the just-picked skill/item) — enemy skill names still speak.
                if (Battle.IsCombatantName(textStr))
                {
                    Log($"[MessageBubble] suppressed duplicate name \"{textStr}\"");
                }
                else if (IsPlayerEcho(textStr))
                {
                    Log($"[MessageBubble] suppressed player echo \"{textStr}\"");
                }
                else
                {
                    Log($"Outputting battle bubble text \"{textStr}\"");
                    Speech.Say(textStr, true);
                }
            }
        }

        return res;
    }

    /// <summary>True if this bubble text just echoes the player's own action:
    /// a battle command name (Attack/Guard/... — the enemy's basic-attack bubble
    /// is also "Attack", but the damage attribution line already names it), or
    /// the skill/item highlighted in a menu moments ago. The skill/item match is
    /// consumed one-shot so an enemy casting the same skill still speaks.</summary>
    private static bool IsPlayerEcho(string t)
    {
        string s = t.Trim();
        if (Battle.IsCommandName(s)) return true;

        long now = Environment.TickCount64;
        if (Battle.PendingEchoSkillId > 0 && now - Battle.PendingEchoSkillTick < 10000)
        {
            string n = null;
            try { n = p4g64.accessibility.Native.Skill.GetName(Battle.PendingEchoSkillId); } catch { }
            if (!string.IsNullOrEmpty(n) && string.Equals(s, n.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Battle.PendingEchoSkillId = -1;
                return true;
            }
        }
        if (Battle.PendingEchoItemId > 0 && now - Battle.PendingEchoItemTick < 10000)
        {
            string n = null;
            try { n = p4g64.accessibility.Native.Item.GetName(Battle.PendingEchoItemId); } catch { }
            if (!string.IsNullOrEmpty(n) && string.Equals(s, n.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Battle.PendingEchoItemId = -1;
                return true;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct BtlMessageBubble
    {
        [FieldOffset(0)] internal TextStruct* Text;
    }

    private delegate nuint DrawMessageBubbleDelegate(nuint param_1, BtlMessageBubble* bubble, nuint param_3);
}