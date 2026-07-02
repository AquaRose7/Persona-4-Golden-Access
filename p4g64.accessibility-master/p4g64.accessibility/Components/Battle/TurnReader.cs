using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Announces whose turn it is in battle. During the PLAYER command phase,
/// <see cref="Battle.ActingUnit"/> (BtlInfo+0xCB8 → Turn → +0x38) is the party member whose
/// command ring is open — we announce "X's turn" when it changes to a new member.
///
/// The Turn pointer is NULL on enemy turns and the game tracks no "acting enemy" pointer
/// (verified by a full BtlInfo + manager scan 2026-06-29), and P4G never displays a turn order
/// (agility-weighted + random) — so there is no readable "next turn"; enemies are named at their
/// ACTION via DamageMonitor ("X attacks"). See database/BATTLE_SYSTEM.md.
/// </summary>
internal sealed class TurnReader
{
    private nint _lastActing = -1;
    private bool _wasInBattle;

    internal TurnReader()
    {
        var t = new Thread(Poll) { IsBackground = true, Name = "TurnReader" };
        t.Start();
        Log("[TurnReader] ready");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(50);
            try { Tick(); }
            catch (Exception ex) { Log($"[TurnReader] {ex.Message}"); Thread.Sleep(500); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) return;

        bool inBattle = Battle.ActiveBtlInfo != 0;
        if (!inBattle)
        {
            if (_wasInBattle) { _wasInBattle = false; _lastActing = -1; }
            return;
        }
        _wasInBattle = true;

        nint au = Battle.ActingUnit();
        if (au == _lastActing) return;
        _lastActing = au;

        if (au != 0)
        {
            string name = FirstName(Battle.UnitName(au));
            if (!string.IsNullOrEmpty(name)) Speech.Say($"{name}'s turn.");
            Log($"[TurnReader] acting → {name} (0x{au:X})");
        }
    }

    // English party/enemy names are "Given Family" — the user wants just the given name.
    internal static string FirstName(string full)
    {
        if (string.IsNullOrEmpty(full)) return full;
        int sp = full.IndexOf(' ');
        return sp > 0 ? full[..sp] : full;
    }
}
