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
    private string _lastHoverSig = "";   // cursor|cardName of the last hover announce
    private int _lastTriesSpoken = -999; // last spoken "you can take N cards" value (for settle re-announce)
    private int _prevTries = -1;         // head[5] from the previous poll (stability gate for the re-announce)
    private int _unresolvedCursor = -1;  // cursor position currently held as unresolvable (change-card grace)
    private int _unresolvedPolls = 0;    // consecutive polls that position has been unresolvable
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
                    // Retry FAST at first so a brief scripted tutorial shuffle is caught as its textures
                    // load, then BACK OFF hard: battle-UI state stays 17 through the REWARD screen too,
                    // where there's no struct to find — a flat fast retry would scan (~600ms) nonstop for
                    // the whole reward screen and waste CPU (seen live: 185 fruitless scans in a row).
                    // _scanAttempts resets on every pick and on leaving state 17, so active shuffling
                    // (including one-mores) always gets the fast window; only a dead state-17 screen slows.
                    long cooldown = _scanAttempts < 8 ? 200 : _scanAttempts < 24 ? 1500 : 6000;
                    _nextScanTick = now + cooldown;
                    FindStruct();
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
                    _lastHoverSig = "";                 // a reshuffle may change cards in place
                    _foundAnnounced = false;            // re-announce the new spread
                    _announceTick = Environment.TickCount64 + AnnounceDebounceMs;
                    if (wasReady) Log($"[Shuffle] spread changed (one-more?) → round {liveRound}, {liveCount} cards: {liveSig}");
                    continue;
                }

                int dealt = (int)head[1];
                int remaining = RemainingCards(_logic, dealt);

                // The game's PANEL slot-map (display order → texture slot) is at a FIXED
                // offset _logic+0xE7C (verified two runs). It COMPACTS as cards are drawn,
                // so map[cursor] is the real texture slot — the texture array itself does
                // NOT keep display order after a draw/change (RE'd live 2026-07-01).
                int[] pmap = PanelSlotMap();
                bool validMap = pmap.Length == remaining && ValidMap(pmap);

                // Remember the original spread while it has no created card yet, so the resolver
                // can later distinguish a CHANGED texture slot from an unchanged one.
                if (validMap)
                {
                    bool anyCreated = false;
                    foreach (int id in pmap) if (id >= dealt) { anyCreated = true; break; }
                    if (!anyCreated) CaptureOrig();
                }

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
                if (cursor >= 0 && cursor < remaining)
                {
                    // Resolve the panel display map (logical card-ids) to physical texture
                    // slots, PER POSITION. Direct ids (< dealt) usually map straight through;
                    // a created/changed card (id >= dealt) sits at slot (id - dealt). A
                    // REPLACEMENT change (e.g. Hierophant→Persona) permutes ids↔slots with no
                    // formula, so ResolveSlots does constraint propagation (the displaced-slot
                    // resolution) and returns a PER-POSITION array: a real texture slot where it
                    // can prove the mapping, or null for a genuinely ambiguous position. Only the
                    // ambiguous position(s) announce without a name — every readable card (incl.
                    // the unchanged ones) still reads, and we never risk a confident wrong name.
                    var resolved = validMap ? ResolveSlots(pmap, dealt) : null;
                    string? cn; string eff;
                    if (resolved != null && cursor < resolved.Length && resolved[cursor] is (string rn, string re))
                        (cn, eff) = (rn, re);                                  // this position resolved
                    else if (validMap)
                        (cn, eff) = ("card changed, name unavailable", "");    // this position ambiguous only
                    else
                        (cn, eff) = ReadCardFull(_logic, cursor);              // no map — old behavior
                    // "N tries left" = the remaining pick budget, at logic header +0x14 (head[5]).
                    // Located via snapshot-diff 2026-07-01: it went 3→2→1 as plain cards were taken,
                    // while cards-remaining went 5→4→3, so it's the tries, not the card count. It's a
                    // FIXED header field the scan already locates (the old docs only searched
                    // 0x20..0x1EC0, so they missed it). Rises when a "+draw" card is picked.
                    int tries = (int)head[5];
                    bool triesSane = tries >= 1 && tries <= 30;
                    string triesStr = triesSane ? $" You can take {tries} card{(tries == 1 ? "" : "s")}." : "";

                    // GRACE for a loading change-card: during a change animation the panel map points at
                    // the created card a poll or two BEFORE its texture is written, so it's briefly
                    // unresolvable and then resolves (live: map=[…,4] read "changed", then map=[…,5] read
                    // "Persona Senri"). Hold the "card changed" announce ~300ms so a loading change-card
                    // doesn't blurt "unavailable" then immediately correct itself. A genuinely
                    // unresolvable position (rare multi-change) still announces after the grace.
                    if (validMap && cn == "card changed, name unavailable")
                    {
                        if (_unresolvedCursor != cursor) { _unresolvedCursor = cursor; _unresolvedPolls = 0; }
                        if (++_unresolvedPolls < 6) { _prevTries = tries; continue; }   // still may resolve → wait
                    }
                    else { _unresolvedCursor = -1; _unresolvedPolls = 0; }

                    string hoverSig = $"{cursor}|{cn ?? "?"}";
                    if (hoverSig != _lastHoverSig)
                    {
                        _lastHoverSig = hoverSig;
                        _lastTriesSpoken = triesSane ? tries : _lastTriesSpoken;
                        _lastCursor = cursor;
                        string name = cn != null ? $", {cn}" : "";
                        string effStr = string.IsNullOrEmpty(eff) ? "" : $". {eff}";
                        Log($"[Shuffle] announce: Card {cursor + 1} of {remaining}{name}{effStr}{triesStr}");
                        Speech.Say($"Card {cursor + 1} of {remaining}{name}{effStr}{triesStr}", true);
                    }
                    else if (triesSane && tries != _lastTriesSpoken && tries == _prevTries)
                    {
                        // Same card, but the counter SETTLED to a new value — a BONUS spread reads 1 for a
                        // moment before the +draw/sweep bonus applies (and a picked "+draw" raises it). The
                        // `tries == _prevTries` gate means we only speak once it's stable across two polls,
                        // so the deal's brief 1→3→4→2 fluctuation doesn't spam. Speak just the corrected
                        // count so the first card no longer stays stuck on the pre-bonus value.
                        _lastTriesSpoken = tries;
                        Log($"[Shuffle] announce: You can take {tries} card{(tries == 1 ? "" : "s")}.");
                        Speech.Say($"You can take {tries} card{(tries == 1 ? "" : "s")}.", true);
                    }
                    _prevTries = tries;
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
        _lastHoverSig = "";
        _cards = Array.Empty<string?>();
        // NOTE: do NOT clear _orig here — DropStruct also fires on a transient struct re-find
        // mid-shuffle (during a take/change animation), and by the time the struct is re-found
        // the spread already carries the created card, so CaptureOrig won't re-run. _orig is
        // keyed by slot and refreshed at every pristine deal, so preserving it is correct; a
        // stale value can never cause a wrong name (Step 1 pins only on a live-texture match).
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
    // Upper bounds RELAXED 2026-07-01 so a big / high-progression spread isn't rejected outright
    // (the "card/…" path read in ScanChunk is the real false-positive gate, not these limits):
    // h[1] dealt ≤ 16 (was 8 — the texture array holds up to MaxCardSlots), h[5] TRIES ≤ 0x3F (was
    // 0xF — h[5] is the pick counter, which can stack high with +draw/sweep bonuses), h[6] ≤ 0xFF
    // (was 0x40 — h[6] grows with the player's Shuffle rank). These caps were causing whole
    // late-game shuffles to go unread.
    private static bool IsLogicHeader(Span<uint> h) =>
        h[0] >= 1 && h[0] <= 0xF && h[1] >= 2 && h[1] <= 16 && h[2] <= 0x20 && h[3] == 0 &&
        h[4] < h[1] && h[5] >= 1 && h[5] <= 0x3F && h[6] <= 0xFF && h[7] == 0;

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
                // Bounds RELAXED to match IsLogicHeader (dealt ≤ 16, tries u[5] ≤ 0x3F, u[6] ≤ 0xFF)
                // so large/high-rank spreads aren't skipped; the card-path read below is the real gate.
                if (u[0] < 1 || u[0] > 0xF || u[5] < 1 || u[5] > 0x3F || u[6] > 0xFF || u[7] != 0) continue;
                if (u[1] < 2 || u[1] > 16 || u[2] > 0x20 || u[3] != 0 || u[4] >= u[1]) continue;
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

    // ── Panel slot-map (display order → texture slot) ───────────────────────────
    // The shuffle PANEL keeps a u16 slot-map in on-screen order at a FIXED offset
    // _logic+0xE7C (verified two runs: panel−logic = 0xE7C both times; the map is right
    // before the 0xE88 remaining field). It is [0,1,…,dealt-1,0xFFFF] at round 1 and
    // COMPACTS as cards are drawn, so map[cursor] is the real texture slot even after a
    // draw/change reorders the texture array. Source: diagnose_shuffle.py,
    // shuffle_time_structs.md; live-confirmed 2026-07-01.
    private const int PanelMapOff = 0xE7C;

    // Remembered card (name + effect) per texture slot from when the spread had no created card
    // (the pristine deal / draw-only states). An original still present in the panel map keeps
    // this identity even if its slot texture was later collaterally overwritten by a reused slot,
    // because a REPLACED card's id LEAVES the map — so id-in-map ⟹ unchanged identity.
    private readonly (string Name, string Effect)?[] _orig = new (string, string)?[MaxCardSlots];

    // Refresh _orig from the current textures — call only when the spread has no created card
    // (all map ids < dealt), so _orig always holds the last-known stable card at each slot.
    private void CaptureOrig()
    {
        for (int s = 0; s < MaxCardSlots; s++)
        {
            var c = ReadCardFull(_logic, s);
            if (c.Name != null) _orig[s] = (c.Name, c.Effect);
        }
    }

    // Resolve the panel display map (logical card-ids, on-screen order) to the CARD (name +
    // effect) at each display position. The game scrambles id→texture-slot on a change with no
    // formula and even overwrites a bystander card's slot, so we never trust slot arithmetic:
    //   1. ORIGINALS (id < dealt still in the map): identity is preserved — a REPLACED card's id
    //      LEAVES the map, so any id < dealt present is still its remembered card _orig[id]. Read
    //      the live texture when it still matches (keeps the current effect); otherwise fall back
    //      to the remembered card (its slot was collaterally overwritten by a reused slot).
    //   2. The CREATED card (id >= dealt): its texture IS the one CHANGED slot (live texture ≠
    //      remembered). When exactly one created position and one changed slot exist, they must
    //      correspond — read that slot live. Anything left (a rarer multi-change) stays null →
    //      "card changed". Every named position is a fact (remembered original, or the single
    //      created↔changed match), so a wrong name is impossible. null = "card changed".
    private (string Name, string Effect)?[] ResolveSlots(int[] map, int dealt)
    {
        int rem = map.Length;
        var res = new (string Name, string Effect)?[rem];
        var live = new (string? Name, string Effect)[MaxCardSlots];
        for (int s = 0; s < MaxCardSlots; s++) live[s] = ReadCardFull(_logic, s);
        Span<bool> usedSlot = stackalloc bool[MaxCardSlots];

        // 1 — originals still in the map keep their remembered identity
        for (int i = 0; i < rem; i++)
        {
            int id = map[i];
            if (id < 0 || id >= dealt || _orig[id] is not (string on, string oe)) continue;
            if (id < MaxCardSlots && live[id].Name == on)   // slot still shows it → live (fresh effect)
            { res[i] = (on, live[id].Effect); usedSlot[id] = true; }
            else res[i] = (on, oe);                          // slot clobbered → remembered card
        }

        // 2 — the single created card ↔ the single changed slot (a slot with a KNOWN card that
        // now differs). Multiple of either → can't match safely → leave null ("card changed").
        var changedSlots = new System.Collections.Generic.List<int>();
        for (int s = 0; s < MaxCardSlots; s++)
            if (live[s].Name != null && !usedSlot[s] && _orig[s] is (string cn, _) && live[s].Name != cn)
                changedSlots.Add(s);
        var createdPos = new System.Collections.Generic.List<int>();
        for (int i = 0; i < rem; i++)
            if (!res[i].HasValue && map[i] >= dealt) createdPos.Add(i);
        if (changedSlots.Count == 1 && createdPos.Count == 1)
        {
            var c = live[changedSlots[0]];
            res[createdPos[0]] = (c.Name!, c.Effect);
        }
        return res;
    }


    // Live display-order slot list (reads u16s until the 0xFFFF terminator).
    private int[] PanelSlotMap()
    {
        if (_logic == 0) return Array.Empty<int>();
        Span<byte> b = stackalloc byte[16 * 2];
        if (!ReadSelf(_logic + PanelMapOff, b)) return Array.Empty<int>();
        var list = new System.Collections.Generic.List<int>(8);
        for (int i = 0; i < 16; i++)
        {
            int v = MemoryMarshal.Read<ushort>(b.Slice(i * 2, 2));
            if (v == 0xFFFF) break;
            if (v > 15) return Array.Empty<int>();   // sanity — bail rather than misread
            list.Add(v);
        }
        return list.ToArray();
    }

    // Map is trustworthy if every entry is a distinct texture slot in [0, 16). A
    // change/one-more can APPEND new cards to slots beyond the original dealt count
    // (seen live: dealt=3 but the map referenced slot 3/4), so bound by the record
    // array capacity, NOT by dealt.
    private const int MaxCardSlots = 16;
    private static bool ValidMap(int[] map)
    {
        if (map.Length < 1) return false;
        int seen = 0;
        foreach (int v in map)
        {
            if (v < 0 || v >= MaxCardSlots || (seen & (1 << v)) != 0) return false;
            seen |= 1 << v;
        }
        return true;
    }

    private (string? Name, string Effect) ReadCardFull(nint logic, int slot)
    {
        Span<byte> buf = stackalloc byte[0x40];
        if (!ReadSelf(logic + CardPathBase + slot * CardStride, buf)) return (null, "");
        int end = buf.IndexOf((byte)0);
        if (end <= 0) return (null, "");
        string path;
        try { path = Encoding.ASCII.GetString(buf[..end]); } catch { return (null, ""); }
        if (!path.StartsWith("card/")) return (null, "");
        return DecodeCard(path);
    }

    private static string DecodeCardPath(string path) => DecodeCard(path).Name;

    // Decode a card texture path into its display NAME and its EFFECT text. The game
    // shows NO per-card description on screen, so the effects are a hand-built table
    // (sourced from the P4G Shuffle Time card list, web-verified 2026-06-30). Effect
    // is "" when unknown (Fool/Magician/Jester arcana never confirmed) — name only.
    private static (string Name, string Effect) DecodeCard(string path)
    {
        string stem = path;
        int s = stem.LastIndexOf('/');
        if (s >= 0) stem = stem[(s + 1)..];
        if (stem.EndsWith(".tmx")) stem = stem[..^4];

        // major arcana: c_cardXX
        if (stem.StartsWith("c_card") && stem.Length >= 8 &&
            int.TryParse(stem.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out int arc))
        {
            string nm = arc < ArcanaNames.Length ? $"{ArcanaNames[arc]} card" : $"Arcana {arc} card";
            string eff = arc >= 0 && arc < ArcanaEffects.Length ? ArcanaEffects[arc] ?? "" : "";
            return (nm, eff);
        }

        // persona card: i_prcXXX (XXX = hex persona id; confirmed i_prc02e -> Pixie)
        if (stem.StartsWith("i_prc") &&
            int.TryParse(stem.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out int pid))
        {
            string? pname = null;
            try { pname = Native.Persona.GetName(pid); } catch { }
            string nm = string.IsNullOrWhiteSpace(pname) ? $"Persona card {pid}" : $"Persona {pname}";
            return (nm, "Adds this Persona to your stock.");
        }

        // suit cards: wand_cNN / sword_cNN / cup_cNN / coin_cNN
        foreach (var (prefix, suit, eff) in SuitNames)
        {
            if (stem.StartsWith(prefix) && stem.Length >= prefix.Length + 2 &&
                int.TryParse(stem.AsSpan(prefix.Length, 2), System.Globalization.NumberStyles.HexNumber, null, out int rank))
                return ($"{suit} rank {rank}", eff);
        }
        return (stem, "");   // unknown shape — speak the stem, the log keeps the full path
    }

    private static readonly (string Prefix, string Suit, string Effect)[] SuitNames =
    {
        // the game's own filenames spell Swords as "sord" (seen live: sord_c01.tmx)
        ("wand_c", "Wands",  "Bonus experience."),
        ("sord_c", "Swords", "Gives a Skill Card."),
        ("sword_c","Swords", "Gives a Skill Card."),
        ("cup_c",  "Cups",   "Restores HP and SP."),
        ("coin_c", "Coins",  "Bonus money."),
    };

    // Major-arcana Shuffle card effects, indexed to ArcanaNames (web-verified P4G list,
    // 2026-06-30). null = effect not confirmed → speak the card name only.
    private static readonly string?[] ArcanaEffects =
    {
        /*0  Fool*/        "Changes all cards; plus one draw.",
        /*1  Magician*/    "Changes your equipped Persona's skill.",
        /*2  Priestess*/   "Plus one draw; changes an undrawn card to a random arcana.",
        /*3  Empress*/     "Plus one draw; removes an undrawn card.",
        /*4  Emperor*/     "Levels up your equipped Persona.",
        /*5  Hierophant*/  "Plus one draw; turns an undrawn card into a random Persona.",
        /*6  Lovers*/      "Plus two draws, but loses obtained items.",
        /*7  Chariot*/     "Raises your equipped Persona's Agility.",
        /*8  Justice*/     "Raises your equipped Persona's Strength.",
        /*9  Hermit*/      "Avoid encounters for a while.",
        /*10 Fortune*/     "Raises your equipped Persona's Luck.",
        /*11 Strength*/    "Raises your equipped Persona's Magic.",
        /*12 Hanged Man*/  "Raises your equipped Persona's Endurance.",
        /*13 Death*/       "Ends Shuffle Time.",
        /*14 Temperance*/  "Gives one Treasure Key.",
        /*15 Devil*/       "Plus three draws, but only 1 experience.",
        /*16 Tower*/       "Plus three draws, but no money from this battle.",
        /*17 Star*/        "Plus one draw; removes one already-drawn card.",
        /*18 Moon*/        "Plus two draws, but half experience.",
        /*19 Sun*/         "Plus two draws, but half money.",
        /*20 Judgement*/   "No effect.",
        /*21 World*/       "No effect.",
        /*22 Jester*/      null,
        /*23 Aeon*/        "Plus four draws.",
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
