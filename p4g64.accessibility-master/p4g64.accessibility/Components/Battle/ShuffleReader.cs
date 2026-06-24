using System.Runtime.InteropServices;
using System.Text;
using DavyKager;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Shuffle Time card narration (session 2026-06-10, see database/BATTLE_SYSTEM.md and
/// memory/shuffle_time_structs.md). The shuffle state lives in a heap arena in the low
/// 4GB with NO stable pointer from mgr/BtlInfo — found via external snapshot diffing.
/// When the battle UI state machine hits 18 (post-battle), we signature-scan private
/// committed memory for the logic struct:
///
///   u32 [1, count(2..8), 0, 0, cursor(&lt;count), 1, 0x14, 0] ... +0x20 heap ptr
///
/// then announce "Card k of N" as the cursor moves. The card-TYPE array lives in the
/// same arena (u32 type[count], count, 0xFFFFFFFF); its enum is not decoded yet, so we
/// log the spread + the picked slot every shuffle — the post-pick result text is already
/// spoken by the Dialogue hook, and the log correlation will hand us the type→name table
/// after a few play sessions. All scanning reads go through ReadProcessMemory-on-self so
/// a region freed mid-scan fails gracefully instead of raising an uncatchable AVE.
/// </summary>
internal sealed unsafe class ShuffleReader
{
    private const int PollMs = 50;
    // Battle UI state machine (mgr+0x458): Shuffle Time card screen = 17 (verified
    // live 2026-06-10); the Result/spoils screen after it = 18. The doc's old
    // "post-battle = 18" lumped both — the cards are gone by 18.
    private const int StateShuffle = 17;
    private static readonly nint MgrG = unchecked((nint)0x140EC08F0L);

    private readonly Thread _thread;
    private volatile bool _stopped;

    // Card resource paths live at struct + CardPathBase + slot * CardStride —
    // e.g. "card/arcana/c_card0e.tmx" (major arcana 0x0E = Temperance, 0-based)
    // or "card/sarcana/wand_c01.tmx" (Wands rank 1). Verified across two battles
    // 2026-06-10; slot order matches the on-screen order and the live pick test.
    private const int CardPathBase = 0x1EC0;
    private const int CardStride = 0x138;
    // REMAINING selectable cards (u32). Drops as you take cards in a one-more
    // (4→3→2), unlike head[1] (total dealt, stable) and head[5] (deal-animation
    // counter that fluctuates). Located via snapshot-diff while parked at each
    // settled pick state, 2026-06-18 — see memory/shuffle_time_structs.md.
    private const int RemainingOff = 0xE88;

    private static readonly string[] ArcanaNames =
    {
        "Fool", "Magician", "Priestess", "Empress", "Emperor", "Hierophant",
        "Lovers", "Chariot", "Justice", "Hermit", "Fortune", "Strength",
        "Hanged Man", "Death", "Temperance", "Devil", "Tower", "Star",
        "Moon", "Sun", "Judgement", "World", "Jester", "Aeon",
    };

    /// <summary>True while a live shuffle struct is latched. ResultReader uses this
    /// to hold its early reward announce until the card screen is gone (the scripted
    /// flow keeps battle-UI state at 17 for both screens).</summary>
    internal static volatile bool ShuffleActive;

    // Located struct for the current shuffle (0 = not found).
    private nint _logic;
    private int _count;
    private int _lastCursor = -1;
    private const int AnnounceDebounceMs = 800;   // count must sit still this long

    private string?[] _cards = Array.Empty<string?>();
    private nint _lastRejected;          // last no-card-paths candidate (log dedupe)
    private long _nextScanTick;          // rescan cooldown while in state 17
    private long _announceTick;          // when the count-stable announce may fire
    private bool _foundAnnounced;        // "Shuffle Time, N cards" spoken for THIS spread
    private bool _announcedEntry;
    private string _lastSig = "";        // signature of the latched spread's cards
    private string? _pickedSig;          // spread just picked from — skip its lingering copy
    private int _lastRound;              // h[0] of the latched spread (1 deal, 3 one-more…)
    private int _pickedRound;            // round we picked from — re-latch when it ADVANCES
    private bool _everAnnounced;         // any spread announced this Shuffle Time (→ "One more")

    public ShuffleReader()
    {
        _thread = new Thread(Poll) { IsBackground = true, Name = "ShuffleReader" };
        _thread.Start();
        Log("[Shuffle] reader started (state-17 signature scan)");
    }

    public void Stop() => _stopped = true;

    private void Poll()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try
            {
                int state = CurrentState();
                if (state != StateShuffle)
                {
                    // RESCUE (2026-06-11): the deferred "Shuffle Time" announce
                    // waits AnnounceDebounceMs for the card count to settle, but
                    // the TUTORIAL Shuffle leaves state 17 before that elapses —
                    // so it logged the cards yet never spoke them. If a struct
                    // was found but never announced, speak it now, with the card
                    // names (navigation may be scripted in the tutorial).
                    if (_logic != 0 && !_foundAnnounced && _count > 0)
                    {
                        _foundAnnounced = true;
                        string list = "";
                        if (_cards.Length > 0)
                        {
                            var parts = new System.Collections.Generic.List<string>();
                            foreach (var c in _cards) parts.Add(c ?? "unknown");
                            list = ": " + string.Join(", ", parts);
                        }
                        Log($"[Shuffle] rescue announce: Shuffle Time, {_count} cards{list}");
                        Speech.Say($"Shuffle Time, {_count} cards{list}", true);
                    }
                    if (state != 18) { _pickedSig = null; _pickedRound = 0; _everAnnounced = false; }
                    DropStruct(announcePick: true);
                    continue;
                }

                // KEEP scanning after a pick. A "One More!" deals a fresh spread in
                // the SAME UI state (17); the just-picked spread is remembered as
                // _pickedSig so its lingering copy isn't re-latched (ScanChunk skips it).
                if (_logic == 0)
                {
                    long now = Environment.TickCount64;
                    if (now < _nextScanTick) continue;
                    _nextScanTick = now + 700;
                    FindStruct();
                    if (_logic == 0) DiagCardScan();   // TEMP: locate the one-more spread
                    continue;
                }

                // Re-validate every poll — the struct is freed the instant the pick
                // resolves, and the arena page can be reused for anything.
                Span<uint> head = stackalloc uint[8];
                if (!ReadSelf(_logic, head) || !IsLogicHeader(head))
                {
                    _pickedSig = _lastSig;                     // pick done — skip its ghost
                    _pickedRound = _lastRound;                 // …only while the round hasn't advanced
                    DropStruct(announcePick: true);
                    continue;
                }

                // Re-read the spread every poll and compare the card CONTENT, not just
                // the count: the deal grows the count as cards land (2→4), AND a
                // "One More!" RESHUFFLES the cards in place — usually with the SAME
                // count, so a count-only check reads the new spread as "the same cards"
                // (user bug 2026-06-17: one-more not recognised).
                int liveCount = (int)head[1];
                int liveRound = (int)head[0];
                var liveCards = ReadCardNames(_logic, liveCount, quiet: true);
                string liveSig = SpreadSig(liveCards, liveCount);
                // A "One More!" advances the ROUND (h[0] 1→3) and reshuffles in place
                // — sometimes to the SAME cards, so also trigger on a round change.
                if (liveCount != _count || liveSig != _lastSig || liveRound != _lastRound)
                {
                    bool wasReady = _foundAnnounced;
                    _count = liveCount;
                    _cards = liveCards;
                    _lastSig = liveSig;
                    _lastRound = liveRound;
                    _lastCursor = -1;
                    _foundAnnounced = false;            // re-announce the new spread
                    _announceTick = Environment.TickCount64 + AnnounceDebounceMs;
                    if (wasReady) Log($"[Shuffle] spread changed (one-more?) → round {liveRound}, {liveCount} cards: {liveSig}");
                    continue;
                }

                // REMAINING selectable cards — drops as cards are taken in a one-more.
                int remaining = RemainingCards(_logic, (int)head[1]);

                if (!_foundAnnounced)
                {
                    if (Environment.TickCount64 < _announceTick) continue;
                    _foundAnnounced = true;
                    string lead = _everAnnounced ? "One more. " : "Shuffle Time, ";
                    _everAnnounced = true;
                    Log($"[Shuffle] announce: {lead}{remaining} cards");
                    Speech.Say($"{lead}{remaining} cards", true);
                }

                int cursor = (int)head[4];
                if (cursor != _lastCursor && cursor >= 0 && cursor < remaining)
                {
                    _lastCursor = cursor;
                    // Read the card FRESH at the live cursor (not from the cached
                    // array, which can lag a one-more reshuffle → wrong card / off
                    // by one, user 2026-06-17).
                    string? cn = ReadCardAt(_logic, cursor);
                    string name = cn != null ? $", {cn}" : "";
                    Log($"[Shuffle] announce: Card {cursor + 1} of {remaining}{name}");
                    Speech.Say($"Card {cursor + 1} of {remaining}{name}", true);
                }
            }
            catch { }
        }
    }

    private void DropStruct(bool announcePick)
    {
        if (_logic == 0) return;
        if (announcePick && _lastCursor >= 0)
        {
            string card = _lastCursor < _cards.Length ? _cards[_lastCursor] ?? "?" : "?";
            Log($"[Shuffle] ended on slot {_lastCursor} ({card})");
        }
        _logic = 0;
        _count = 0;
        _lastCursor = -1;
        _cards = Array.Empty<string?>();
        _lastSig = "";
        _lastRound = 0;
        _announcedEntry = false;
        _lastRejected = 0;
        _foundAnnounced = false;
        _scanAttempts = 0;
        ShuffleActive = false;
        // _pickedSig / _everAnnounced persist across a pick (cleared on state exit).
    }

    // Stable signature of a spread's cards — used to tell a genuinely new deal
    // (one-more) from the just-picked spread lingering in memory.
    private static string SpreadSig(string?[] cards, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
            sb.Append(i < cards.Length ? (cards[i] ?? "?") : "?").Append('|');
        return sb.ToString();
    }

    // h[2] is 0 in normal battles but 4 in the SCRIPTED tutorial shuffle; h[6]
    // read 0x14 in every early-game capture but went DIFFERENT once the user's
    // Shuffle RANK increased (2026-06-11: 15 full-memory scans, zero candidates
    // — the only header field that could have moved). Both are now just
    // small-value sanity checks; the card-path read in ScanChunk is the real
    // false-positive gate.
    // h[0] is a SHUFFLE-ROUND counter: 1 on the first deal, 3 after a "One More!"
    // (live-verified 2026-06-17 — the one-more reshuffles the SAME struct in place
    // with h[0]=3, which the old `h[0]==1` check rejected → one-more never read).
    // Accept the small round range; the card-path read is the real false-positive gate.
    private static bool IsLogicHeader(Span<uint> h) =>
        h[0] >= 1 && h[0] <= 0xF && h[1] >= 2 && h[1] <= 8 && h[2] <= 0x20 && h[3] == 0 &&
        h[4] < h[1] && h[5] >= 1 && h[5] <= 0xF && h[6] <= 0x40 && h[7] == 0;

    // ---- the scan -------------------------------------------------------------

    private int _scanAttempts;

    private void FindStruct()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int regions = 0;
        // Low 4GB first (where every observed arena lived), then the rest of
        // user space as a fallback — a whole shuffle went unfound 2026-06-11,
        // consistent with the arena landing above 4GB late in a long session.
        if (FindStructInRange(0x10000, unchecked((nint)0x1_0000_0000L), ref regions)) { LogFound(sw, regions); return; }
        if (FindStructInRange(unchecked((nint)0x1_0000_0000L), unchecked((nint)0x7FFF_FFF00000L), ref regions)) { LogFound(sw, regions); return; }

        _scanAttempts++;
        if (!_announcedEntry || _scanAttempts % 5 == 0)
        {
            Log($"[Shuffle] scan: no struct yet (attempt {_scanAttempts}, {regions} regions, {sw.ElapsedMilliseconds}ms)");
            _announcedEntry = true;
        }
    }

    private bool FindStructInRange(nint lo, nint hi, ref int regions)
    {
        byte[] buf = _scanBuf ??= new byte[0x100000];
        nint addr = lo;
        while ((ulong)addr < (ulong)hi)
        {
            if (VirtualQuery(addr, out var mbi, (nint)sizeof(MemoryBasicInformation)) == 0) break;
            nint regionEnd = (nint)((long)mbi.BaseAddress + (long)mbi.RegionSize);
            bool scannable = mbi.State == 0x1000 /*COMMIT*/ &&
                             mbi.Type == 0x20000 /*PRIVATE*/ &&
                             (mbi.Protect & 0xCC) != 0 /*RW/WC/EXEC_RW*/ &&
                             (mbi.Protect & 0x100) == 0 /*GUARD*/;
            if (scannable)
            {
                regions++;
                for (nint p = (nint)mbi.BaseAddress; p < regionEnd; p += buf.Length - 0x40)
                {
                    int len = (int)Math.Min(buf.Length, regionEnd - p);
                    if (!ReadSelf(p, buf.AsSpan(0, len))) break;
                    if (ScanChunk(p, buf, len)) return true;
                }
            }
            addr = regionEnd;
        }
        return false;
    }

    private bool ScanChunk(nint baseVa, byte[] buf, int len)
    {
        Span<byte> probe = stackalloc byte[4];
        fixed (byte* b = buf)
        {
            for (int i = 0; i + 0x30 <= len; i += 4)
            {
                uint* u = (uint*)(b + i);
                // count >= 2: count=1 lookalikes flooded the scan (2026-06-11)
                // and no real shuffle deals a single card. u[0] AND u[5] are
                // round/state counters (1 on a normal deal; u[0]=3 one-more,
                // u[5]=3 during an UPGRADE, live-verified 2026-06-17) — accept the
                // small range, the card-path read is the real false-positive gate.
                if (u[0] < 1 || u[0] > 0xF || u[5] < 1 || u[5] > 0xF || u[6] > 0x40 || u[7] != 0) continue;
                if (u[1] < 2 || u[1] > 8 || u[2] > 0x20 || u[3] != 0 || u[4] >= u[1]) continue;
                // +0x20 must hold a plausible low-4GB heap pointer.
                ulong ptr = *(ulong*)(b + i + 0x20);
                if (ptr < 0x10000 || ptr >= 0x1_0000_0000UL) continue;
                if (!ReadSelf((nint)ptr, probe)) continue;

                // The 32-byte header alone false-positives on unrelated objects (seen
                // live: a permanent count=2 match at 0xAD5FB10). The definitive check:
                // the real struct has its card texture paths at +0x1EC0 (stride 0x138)
                // — an ASCII "card/" prefix there can't happen by accident. (The old
                // 64KB-page "arena evidence" read failed whenever the allocation didn't
                // start at the page boundary, which silently broke whole shuffles.)
                // During the deal animation the paths aren't written yet — we just
                // keep rescanning until they appear, which also means the announce
                // lands right as the cards settle.
                nint cand = baseVa + i;
                int count = (int)u[1];
                var cards = ReadCardNames(cand, count, quiet: true);
                bool any = false;
                foreach (var c in cards) if (c != null) { any = true; break; }
                if (!any)
                {
                    if (cand != _lastRejected)
                    {
                        _lastRejected = cand;
                        Log($"[Shuffle] candidate @ 0x{cand:X} count={count} has no card paths yet — waiting");
                    }
                    continue;
                }
                cards = ReadCardNames(cand, count);   // verbose re-read for the log
                string sig = SpreadSig(cards, count);

                // Skip the spread we just picked from (its struct lingers through the
                // reveal/reward phase). BUT a "One More!" reshuffles it in place with
                // the SAME cards and an ADVANCED round (h[0]) — so only skip while the
                // round hasn't advanced past the one we picked (else the one-more is
                // never re-latched, user 2026-06-17).
                if (_pickedSig != null && sig == _pickedSig && (int)u[0] <= _pickedRound)
                {
                    if (cand != _lastRejected)
                    {
                        _lastRejected = cand;
                        Log($"[Shuffle] @ 0x{cand:X} is the just-picked spread (round {u[0]}) — skipping");
                    }
                    continue;
                }

                _pickedSig = null;
                _logic = cand;
                _count = count;
                _lastCursor = -1;            // force the first announcement
                _cards = cards;
                _lastSig = sig;
                _lastRound = (int)u[0];
                _foundAnnounced = false;
                _announceTick = Environment.TickCount64 + AnnounceDebounceMs;
                ShuffleActive = true;
                return true;
            }
        }
        return false;
    }

    private void LogFound(System.Diagnostics.Stopwatch sw, int regions)
    {
        // spoken announce is deferred until the card count sits still (Poll)
        Log($"[Shuffle] struct @ 0x{_logic:X} count={_count} cards=[{string.Join(" | ", _cards)}] " +
            $"({regions} regions, {sw.ElapsedMilliseconds}ms)");
    }

    // TEMP DIAGNOSTIC (one-more hunt 2026-06-17): the post-pick scan never finds
    // the one-more spread. Locate card structs by their card-PATH content instead
    // of the header signature, and log each candidate struct's header so we can see
    // why IsLogicHeader rejected the one-more deal. Throttled; dedupes on content.
    private long _diagTick;
    private string _diagLast = "";
    private void DiagCardScan()
    {
        long now = Environment.TickCount64;
        if (now < _diagTick) return;
        _diagTick = now + 1200;
        byte[] buf = _scanBuf ??= new byte[0x100000];
        byte[] needle = { (byte)'c', (byte)'a', (byte)'r', (byte)'d', (byte)'/' };
        var found = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<long>();
        nint addr = 0x10000, hi = unchecked((nint)0x1_0000_0000L);
        var mbi = new MemoryBasicInformation();
        while ((ulong)addr < (ulong)hi && found.Count < 30)
        {
            if (VirtualQuery(addr, out mbi, (nint)sizeof(MemoryBasicInformation)) == 0) break;
            nint regionEnd = (nint)((long)mbi.BaseAddress + (long)mbi.RegionSize);
            if (mbi.State == 0x1000 && mbi.Type == 0x20000 && (mbi.Protect & 0xCC) != 0 && (mbi.Protect & 0x100) == 0)
            {
                for (nint p = (nint)mbi.BaseAddress; p < regionEnd; p += buf.Length - 0x40)
                {
                    int len = (int)Math.Min(buf.Length, regionEnd - p);
                    if (!ReadSelf(p, buf.AsSpan(0, len))) break;
                    var span = buf.AsSpan(0, len);
                    int off = 0;
                    while (found.Count < 30)
                    {
                        int idx = span[off..].IndexOf(needle);
                        if (idx < 0) break;
                        int i = off + idx; off = i + 5;
                        int e = i; while (e < len && buf[e] != 0) e++;
                        if (e >= len || e - i >= 0x30) continue;
                        string s = Encoding.ASCII.GetString(buf, i, e - i);
                        if (!s.EndsWith(".tmx")) continue;
                        nint strAddr = p + i;
                        if (!seen.Add((long)strAddr)) continue;
                        // Log EVERY card string with its address + the u32 header at
                        // string−0x1EC0 (so if it IS a real slot-0 we can see why the
                        // scan rejected it; if not, the addresses reveal the layout).
                        nint structAddr = strAddr - CardPathBase;
                        Span<uint> h = stackalloc uint[2];
                        string hd = ReadSelf(structAddr, h) ? $"hdr@-0x1EC0=[{h[0]},{h[1]}]" : "hdr?";
                        found.Add($"0x{strAddr:X}({hd})={s}");
                    }
                }
            }
            addr = regionEnd;
        }
        // Log every scan (no dedupe) to capture the upgrade phase, where a new
        // count=N struct appears but its cards may load late / at another layout.
        Log($"[ShuffleDiag] {found.Count} card strings: {string.Join("  ", found)}");
    }

    /// <summary>Read and decode each slot's card texture path. Known shapes:
    /// "card/arcana/c_cardXX.tmx" (XX = hex arcana id, 0-based) and
    /// "card/sarcana/&lt;suit&gt;_cNN.tmx" (wand/sword/cup/coin, NN = hex rank).
    /// Unknown shapes are spoken as the raw file stem and logged for decoding.</summary>
    private string?[] ReadCardNames(nint logic, int count, bool quiet = false)
    {
        var names = new string?[count];
        Span<byte> buf = stackalloc byte[0x40];
        for (int slot = 0; slot < count; slot++)
        {
            if (!ReadSelf(logic + CardPathBase + slot * CardStride, buf)) continue;
            int end = buf.IndexOf((byte)0);
            if (end <= 0) continue;
            string path;
            try { path = Encoding.ASCII.GetString(buf[..end]); }
            catch { continue; }
            // quiet = candidate validation during the scan — lookalikes hold
            // garbage here; only a latched struct's paths are worth logging.
            if (!path.StartsWith("card/")) { if (!quiet) Log($"[Shuffle] slot {slot}: odd path \"{path}\""); continue; }
            names[slot] = DecodeCardPath(path);
            if (!quiet) Log($"[Shuffle] slot {slot}: {path} -> {names[slot]}");
        }
        return names;
    }

    /// <summary>Read+decode a single card at the live cursor slot (fresh, no cache).</summary>
    /// <summary>Remaining selectable cards (struct+0xE88); falls back to the total
    /// dealt if the field is out of range (deal still settling).</summary>
    private static int RemainingCards(nint logic, int total)
    {
        Span<uint> rb = stackalloc uint[1];
        if (ReadSelf(logic + RemainingOff, rb))
        {
            int r = (int)rb[0];
            if (r >= 1 && r <= total) return r;
        }
        return total;
    }

    private string? ReadCardAt(nint logic, int slot)
    {
        Span<byte> buf = stackalloc byte[0x40];
        if (!ReadSelf(logic + CardPathBase + slot * CardStride, buf)) return null;
        int end = buf.IndexOf((byte)0);
        if (end <= 0) return null;
        string path;
        try { path = Encoding.ASCII.GetString(buf[..end]); } catch { return null; }
        return path.StartsWith("card/") ? DecodeCardPath(path) : null;
    }

    private static string DecodeCardPath(string path)
    {
        string stem = path;
        int s = stem.LastIndexOf('/');
        if (s >= 0) stem = stem[(s + 1)..];
        if (stem.EndsWith(".tmx")) stem = stem[..^4];

        // major arcana: c_cardXX
        if (stem.StartsWith("c_card") && stem.Length >= 8 &&
            int.TryParse(stem.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out int arc))
            return arc < ArcanaNames.Length ? $"{ArcanaNames[arc]} card" : $"Arcana {arc} card";

        // persona card: i_prcXXX (XXX = hex persona id; confirmed i_prc02e -> Pixie)
        if (stem.StartsWith("i_prc") &&
            int.TryParse(stem.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out int pid))
        {
            string? pname = null;
            try { pname = Native.Persona.GetName(pid); } catch { }
            return string.IsNullOrWhiteSpace(pname) ? $"Persona card {pid}" : $"Persona {pname}";
        }

        // suit cards: wand_cNN / sword_cNN / cup_cNN / coin_cNN
        foreach (var (prefix, suit) in SuitNames)
        {
            if (stem.StartsWith(prefix) && stem.Length >= prefix.Length + 2 &&
                int.TryParse(stem.AsSpan(prefix.Length, 2), System.Globalization.NumberStyles.HexNumber, null, out int rank))
                return $"{suit} rank {rank}";
        }
        return stem;   // unknown shape — speak the stem, the log keeps the full path
    }

    private static readonly (string Prefix, string Suit)[] SuitNames =
    {
        // the game's own filenames spell Swords as "sord" (seen live: sord_c01.tmx)
        ("wand_c", "Wands"), ("sord_c", "Swords"), ("sword_c", "Swords"),
        ("cup_c", "Cups"), ("coin_c", "Coins"),
    };

    // ---- plumbing ---------------------------------------------------------------

    private byte[]? _scanBuf;

    private static int CurrentState()
    {
        Span<byte> q = stackalloc byte[8];
        if (!ReadSelf(MgrG, q)) return -1;
        nint mgr = (nint)MemoryMarshal.Read<long>(q);
        if (mgr == 0) return -1;
        Span<byte> s = stackalloc byte[4];
        return ReadSelf(mgr + 0x458, s) ? MemoryMarshal.Read<int>(s) : -1;
    }

    private static bool ReadSelf(nint addr, Span<byte> dst)
    {
        fixed (byte* d = dst)
            return ReadProcessMemory(GetCurrentProcess(), addr, d, (nint)dst.Length, out nint got)
                   && got == dst.Length;
    }

    private static bool ReadSelf(nint addr, Span<uint> dst)
        => ReadSelf(addr, MemoryMarshal.AsBytes(dst));

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, out MemoryBasicInformation lpBuffer, nint dwLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte* lpBuffer,
        nint nSize, out nint lpNumberOfBytesRead);
}
