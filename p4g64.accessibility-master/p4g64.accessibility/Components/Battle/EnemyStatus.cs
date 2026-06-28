using System.Runtime.InteropServices;
using System.Text;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Press <c>U</c> in battle to hear every living enemy, its current HP, and
/// whether it's knocked down:
///
///   <c>"3 enemies. Lying Hablerie 25 HP, down. Lying Hablerie 73 HP. Lying Hablerie 73 HP."</c>
///
/// Reads the combatant list via <see cref="Battle.EnumerateUnits"/> (manager
/// global 0x140EC08F0). Enemies are side==1. Per enemy: current HP at
/// statNode+0x08, knocked-down when statNode+0x0C &amp; 0x100000 (both confirmed
/// from the F12 probe — HP 73→25 + status 0→0x100000 on a weakness hit). Names
/// via <see cref="Battle.UnitName"/> (GetUnitName).
///
/// Max HP is logged (from the enemy table) but not yet spoken — pending one more
/// runtime confirmation of the table value; once confirmed this says "X of Y HP".
/// </summary>
internal sealed unsafe class EnemyStatus
{
    private const int PollMs = 50;
    private const int VK_U = 0x55;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _keyWas;

    public EnemyStatus()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "EnemyStatus" };
        _thread.Start();
        Log("[EnemyStatus] ready (U = speak enemy HP / down state)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try
            {
                if (!Utils.GameHasFocus()) continue;   // ignore U while alt-tabbed
                bool down = IsKeyDown(VK_U);
                if (down && !_keyWas) Announce();
                _keyWas = down;
            }
            catch (Exception ex)
            {
                Log($"[EnemyStatus] poll error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Announce()
    {
        if (!FieldTracker.InBattle)   // per-dungeon battle major (240, 241, …)
        {
            Speech.Say("Enemy status only works in battle.", true);
            return;
        }

        var units = Battle.EnumerateUnits();
        var speech = new StringBuilder();
        var log = new StringBuilder();
        int living = 0;

        foreach (var (unit, side, stat) in units)
        {
            if (side != 1) continue; // enemies only
            if (!IsReadable(stat, 0x10)) continue;

            ushort hp = *(ushort*)((byte*)stat + 0x08);
            uint status = *(uint*)((byte*)stat + 0x0C);
            bool down = (status & 0x100000) != 0;
            bool dead = hp == 0;

            string name = Battle.UnitName(unit) ?? "Enemy";
            Battle.TryEnemyMax(stat, out int maxHp, out int maxSp);
            log.Append($" [{name} hp={hp} max={maxHp} status=0x{status:X} down={down} dead={dead}]");

            if (dead) continue; // skip defeated enemies in the spoken summary
            living++;

            speech.Append($"{name} {hp} HP");
            if (down) speech.Append(", down");
            string ail = Battle.AilmentText(status);
            if (ail != null) speech.Append($", {ail}");
            speech.Append(". ");
        }

        string msg = living > 0
            ? $"{living} {(living == 1 ? "enemy" : "enemies")}. {speech.ToString().TrimEnd()}"
            : "No enemies.";
        Speech.Say(msg, true);
        Log($"[EnemyStatus] U ->{log} | {msg}");
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        uint state = *(uint*)(buf + 32);
        uint protect = *(uint*)(buf + 36);
        if (state != 0x1000) return false;
        if ((protect & 0x01) != 0) return false;
        if ((protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }
}
