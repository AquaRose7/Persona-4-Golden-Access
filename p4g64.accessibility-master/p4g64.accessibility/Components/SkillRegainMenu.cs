using System.Text;
using p4g64.accessibility.Native;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the rare "Select the skill you want to regain" menu — a party member
/// relearning a FORGOTTEN skill on a special Social-Link outing. (The follow-up
/// "which current skill to replace" screen is already read by SkillReplaceMenu.)
///
/// RE 2026-07-09 (snapshot method, no hook needed). The screen runs as the named
/// task **cmp_skill_add_ex** in the task registry (heads 0x1462486F8 / 0x1462486A8 /
/// 0x146248768; node name @+0x00, work @+0x48, next @+0x50). Its work struct:
///   +0x28 u16  COUNT of regainable skills
///   +0x2A u16  CURSOR (0..count-1)
///   +0x4C + i*2  u16  skill id for row i  (-> Skill.GetName/GetDescription)
/// We poll the registry and speak the highlighted skill + its description on move.
/// </summary>
internal sealed unsafe class SkillRegainMenu
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] Task = Encoding.ASCII.GetBytes("cmp_skill_add_ex");

    private int _lastCursor = -1;

    internal SkillRegainMenu()
    {
        var t = new Thread(Poll) { IsBackground = true, Name = "SkillRegainMenu" };
        t.Start();
        Log("[SkillRegain] ready");
    }

    internal SkillRegainMenu(Reloaded.Hooks.Definitions.IReloadedHooks _) : this() { }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(100);
            try { if (GameHasFocus()) Tick(); } catch { }
        }
    }

    private void Tick()
    {
        nint node = FindTask(Task);
        if (node == 0) { _lastCursor = -1; return; }
        if (!IsReadable(node + 0x48, 8)) return;
        nint work = *(nint*)(node + 0x48);
        if (work <= 0x10000 || !IsReadable(work + 0x2C, 2)) return;

        int count  = *(ushort*)(work + 0x28);
        int cursor = *(ushort*)(work + 0x2A);
        if (count < 1 || count > 32 || cursor < 0 || cursor >= count) { _lastCursor = -1; return; }
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        if (!IsReadable(work + 0x4C + cursor * 2, 2)) return;
        int sid = *(ushort*)(work + 0x4C + cursor * 2);
        if (sid < 1 || sid > 1024) return;

        string nm; try { nm = Skill.GetName(sid); } catch { nm = null; }
        if (string.IsNullOrEmpty(nm)) nm = $"skill {sid}";
        string desc; try { desc = Skill.GetDescription(sid); } catch { desc = ""; }

        string body = string.IsNullOrEmpty(desc)
            ? $"{nm}. {cursor + 1} of {count}."
            : $"{nm}. {desc}. {cursor + 1} of {count}.";
        Log($"[SkillRegain] cursor={cursor}/{count} id={sid} say: {body}");
        Speech.Say(body, interrupt: true);
    }

    private static nint FindTask(byte[] name)
    {
        foreach (nint head in TaskHeads)
        {
            if (!IsReadable(head, 8)) continue;
            nint node = *(nint*)head;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!IsReadable(node, 0x58)) break;
                bool match = true;
                for (int j = 0; j < name.Length; j++)
                    if (*(byte*)(node + j) != name[j]) { match = false; break; }
                if (match && *(byte*)(node + name.Length) == 0) return node;
                node = *(nint*)(node + 0x50);
            }
        }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint protect = *(uint*)(buf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }
}
