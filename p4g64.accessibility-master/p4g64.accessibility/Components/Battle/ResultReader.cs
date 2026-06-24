using System.Runtime.InteropServices;
using System.Text;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Victory/Result screen reader. Hooks the battle-spoils apply FUN_140101880(expCtx,
/// spoils) — found via CE "what writes the wallet" → the once-fired write at 0x140101994.
/// It applies EXP (expCtx+4), money (spoils+8, ×2 if a bonus flag is set), and item drops
/// (count at spoils+0x30, id/count pairs from spoils+0x24). We read money as the wallet
/// before/after delta (exact, including the bonus), EXP from expCtx+4, items from the
/// drop list, and speak them when the spoils land (= the Result screen). Diagnostic logs
/// the raw fields so the offsets/item-stride can be confirmed against the screen.
/// </summary>
internal sealed unsafe class ResultReader
{
    private const int PollMs = 50;
    private static readonly nint MgrG    = unchecked((nint)0x140EC08F0L);
    private static readonly nint WalletA = unchecked((nint)0x1451BCD70L);

    private IHook<SpoilsDelegate> _hook;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _announced;

    public ResultReader(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SpoilsDelegate>(Spoils, unchecked((nint)0x140101880L)).Activate();
        Log("[ResultReader] hooked spoils-apply @ 0x140101880");
        _thread = new Thread(Poll) { IsBackground = true, Name = "ResultReader" };
        _thread.Start();
    }

    public void Stop() => _stopped = true;

    private void Spoils(nint expCtx, nint spoils)
    {
        uint before = IsReadable(WalletA, 4) ? *(uint*)WalletA : 0;
        _hook.OriginalFunction(expCtx, spoils);
        try
        {
            uint after = IsReadable(WalletA, 4) ? *(uint*)WalletA : before;
            long money = (long)after - before;
            int exp = IsReadable(expCtx + 4, 4) ? *(int*)((byte*)expCtx + 4) : 0;
            string items = ItemList(spoils);

            if (_announced)
            {
                // Early announce already happened off the display struct. The wallet
                // delta here is exact (incl. any ×2 bonus) — speak a correction if the
                // displayed money we read early was wrong.
                if (_earlyMoney >= 0 && money != _earlyMoney)
                {
                    Log($"[Result] money correction: early {_earlyMoney}, actual {money}");
                    Speech.Say($"Correction, {money} yen", true);
                }
                return;
            }
            _announced = true;
            string msg = $"Gained {exp} EXP, {money} yen{items}";
            Log($"[Result] {msg}");
            Speech.Say(msg, true);
        }
        catch { }
    }

    private static string ItemList(nint spoils)
    {
        var spoken = new StringBuilder();
        int n = IsReadable(spoils + 0x30, 4) ? *(int*)((byte*)spoils + 0x30) : 0;
        for (int i = 0; i < n && i < 8 && IsReadable(spoils + 0x24 + i * 4, 4); i++)
        {
            int id = *(short*)((byte*)spoils + 0x24 + i * 4);
            int cnt = *(short*)((byte*)spoils + 0x26 + i * 4);
            string name = null;
            try { name = Item.GetName(id); } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = $"item {id}";
            spoken.Append(cnt > 1 ? $", {name} times {cnt}" : $", {name}");
        }
        return spoken.ToString();
    }

    /// <summary>
    /// EARLY reward announce — speaks while the victory panel is SHOWING instead of
    /// on advance (session 2026-06-10). The spoils struct is alive during the panel
    /// and reachable via mgr+0x14E0 → result node → +0x48 → struct-8:
    /// money +0x08, item id/cnt pairs +0x24 (count +0x30), EXP +0x58. Announce is
    /// gated on: shuffle screen gone (ShuffleReader.ShuffleActive false), values sane,
    /// and two consecutive identical reads (the struct fills over a few frames).
    /// The Spoils hook stays as fallback + exact-money corrector.
    /// </summary>
    private long _earlyMoney = -1;
    private (int exp, long money, int n) _lastEarly = (-1, -1, -1);

    private void TryEarlyAnnounce()
    {
        nint mgr = IsReadable(MgrG, 8) ? *(nint*)MgrG : 0;
        if (mgr == 0 || !IsReadable(mgr + 0x14E0, 8)) return;
        nint node = *(nint*)(mgr + 0x14E0);
        if (node == 0 || !IsReadable(node + 0x48, 8)) return;
        nint spoils = *(nint*)(node + 0x48) + 8;
        if (!IsReadable(spoils, 0x80)) return;

        int exp = *(int*)((byte*)spoils + 0x58);
        long money = *(int*)((byte*)spoils + 0x08);
        int n = *(int*)((byte*)spoils + 0x30);
        // exp == 0 means the reward panel hasn't been built yet (it stays 0 through
        // the shuffle cards and reveal) — wait for it.
        if (exp < 1 || exp > 1_000_000 || money < 0 || money > 10_000_000 || n < 0 || n > 8) return;
        for (int i = 0; i < n; i++)
        {
            int id = *(short*)((byte*)spoils + 0x24 + i * 4);
            if (id <= 0 || id > 4096) return;
        }

        // require two consecutive identical reads before speaking
        if (_lastEarly != (exp, money, n)) { _lastEarly = (exp, money, n); return; }

        _announced = true;
        _earlyMoney = money;
        string msg = $"Gained {exp} EXP, {money} yen{ItemList(spoils)}";
        Log($"[Result] early: {msg} (spoils @ 0x{spoils:X})");
        Speech.Say(msg, true);
    }

    private void Poll()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try
            {
                int state = CurrentState();
                if (state >= 1 && state < 12)
                {
                    _announced = false;                  // fresh battle → re-arm
                    _earlyMoney = -1;
                    _lastEarly = (-1, -1, -1);
                }
                else if (!_announced && (state == 17 || state == 18) && !ShuffleReader.ShuffleActive)
                {
                    // State 17 hosts the WHOLE post-battle flow (cards, reveal, reward
                    // panel — even in battles without a shuffle). The reliable "panel
                    // is actually showing" signal is the spoils EXP field: it stays 0
                    // through the cards/reveal and is written when the panel builds
                    // (seen live: "Gained 0 EXP" spoken over the cards before this
                    // gate existed). TryEarlyAnnounce requires exp >= 1; zero-EXP
                    // battles fall back to the on-advance hook announce.
                    TryEarlyAnnounce();
                }
            }
            catch { }
        }
    }

    private static int CurrentState()
    {
        if (!IsReadable(MgrG, 8)) return -1;
        nint mgr = *(nint*)MgrG;
        return IsReadable(mgr + 0x460, 4) ? *(int*)(mgr + 0x458) : -1;
    }

    private delegate void SpoilsDelegate(nint expCtx, nint spoils);

    [DllImport("kernel32.dll")]
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
