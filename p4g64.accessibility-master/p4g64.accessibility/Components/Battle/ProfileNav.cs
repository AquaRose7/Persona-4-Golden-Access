using System.Runtime.InteropServices;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Navigable enemy-analyze profile, matching the in-game Analyze panel. Active
/// while you're Analyzing an enemy (battle, command = Analysis, an enemy
/// targeted). Mirrors the screen:
///
///   Row 0 — name, arcana, level      ("Lying Hablerie, Magician, level 5")
///   Row 1 — max HP, max SP           ("Max HP 73, Max SP 51")
///   Row 2 — element affinities       ("Elements. Physical normal, Fire unknown,
///                                       Ice unknown, Electric weak, Wind normal,
///                                       Light unknown, Dark unknown")
///
/// Controls (same I/J/K/L cursor keys as dungeon mode; free in battle):
///   I / K = up / down a row (reads the whole row)
///   J / L = left / right through the items in the current row (one at a time)
///
/// On targeting a (new) enemy under Analysis it auto-reads Row 0; from there you
/// choose how much to hear. Element states read "unknown" until the game reveals
/// them — exactly like the on-screen "?".
/// </summary>
internal sealed unsafe class ProfileNav
{
    private const int PollMs = 40;
    private const int VK_I = 0x49, VK_K = 0x4B, VK_J = 0x4A, VK_L = 0x4C;
    private const int Rows = 3;

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _iW, _kW, _jW, _lW;
    private int _row, _item = -1;
    private nint _lastTarget;
    private bool _panelWas;

    public ProfileNav()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "ProfileNav" };
        _thread.Start();
        Log("[ProfileNav] ready (Analyze an enemy, then I/K rows, J/L items)");
    }

    public void Stop() => _stopped = true;

    private void PollLoop()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { Log($"[ProfileNav] error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!Utils.GameHasFocus()) return;   // ignore I/K/J/L while alt-tabbed
        // Active inside the ANALYSIS screen: command 0 with an enemy resolved. We do NOT
        // gate on the overlay pointer (it flickers and fires at turn-start). Instead the
        // hover auto-read is avoided by only auto-reading when the analyzed enemy CHANGES
        // — _lastTarget is kept across the command ring, so re-stopping on Analysis with
        // the same lingering enemy stays silent. I/K/J/L still navigates the panel.
        nint target = Battle.AnalyzeSelectedEnemy();
        bool active = Battle.CurrentCommand == 0 && target != 0 && Battle.UnitSide(target) == 1;
        if (!active)
        {
            _row = 0; _item = -1;   // keep _lastTarget so re-hover same enemy is silent
            _iW = IsKeyDown(VK_I); _kW = IsKeyDown(VK_K); _jW = IsKeyDown(VK_J); _lW = IsKeyDown(VK_L);
            return;
        }

        // New analyzed enemy → auto-read its header. Re-entering with the same enemy does
        // not (so hovering Analysis in the ring after a scan is silent).
        if (target != _lastTarget)
        {
            _lastTarget = target; _row = 0; _item = -1;
            Announce(target);
        }

        bool i = IsKeyDown(VK_I), k = IsKeyDown(VK_K), j = IsKeyDown(VK_J), l = IsKeyDown(VK_L);
        if (i && !_iW) { _row = Math.Max(0, _row - 1); _item = -1; Announce(target); }
        if (k && !_kW) { _row = Math.Min(Rows - 1, _row + 1); _item = -1; Announce(target); }
        if (j && !_jW) StepItem(target, -1);
        if (l && !_lW) StepItem(target, +1);
        _iW = i; _kW = k; _jW = j; _lW = l;
    }

    private void StepItem(nint target, int dir)
    {
        var (_, items) = BuildRow(target, _row);
        if (items.Length == 0) return;
        _item = _item < 0 ? (dir > 0 ? 0 : items.Length - 1)
                          : Math.Clamp(_item + dir, 0, items.Length - 1);
        Speech.Say(items[_item], true);
        Log($"[ProfileNav] row {_row} item {_item}: {items[_item]}");
    }

    private void Announce(nint target)
    {
        var (label, items) = BuildRow(target, _row);
        string body = items.Length > 0 ? string.Join(", ", items) : "nothing";
        string msg = label != null ? $"{label}. {body}" : body;
        Speech.Say(msg, true);
        Log($"[ProfileNav] row {_row}: {msg}");
    }

    private (string label, string[] items) BuildRow(nint unit, int row)
    {
        switch (row)
        {
            case 0:
            {
                string name = Battle.UnitName(unit) ?? "Enemy";
                string arcana = Battle.EnemyArcanaName(unit);
                int lvl = Battle.EnemyLevel(unit);
                var items = new List<string> { name };
                if (!string.IsNullOrEmpty(arcana)) items.Add(arcana);
                items.Add(lvl >= 0 ? $"level {lvl}" : "level unknown");
                return (null, items.ToArray());
            }
            case 1:
            {
                if (Battle.EnemyMaxHpSp(unit, out int mhp, out int msp))
                    return (null, new[] { $"Max HP {mhp}", $"Max SP {msp}" });
                return (null, new[] { "Max HP unknown", "Max SP unknown" });
            }
            case 2:
            {
                var items = new List<string>();
                foreach (var (id, nm) in Battle.ProfileElements)
                    items.Add(Battle.ElementAffinityText(unit, id, nm));
                return ("Elements", items.ToArray());
            }
        }
        return (null, Array.Empty<string>());
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
