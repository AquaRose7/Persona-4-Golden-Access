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

    /// <summary>True while the battle-UI state machine is on the Shuffle/reward screen
    /// (state 17), updated every poll. ShuffleText gates on THIS (not ShuffleActive):
    /// a SKIPPED deal writes neither texture paths NOR the panel map, so the struct may
    /// never latch at all — but the game's info panel still renders, and the panel text
    /// alone can carry the reading (2026-07-04).</summary>
    internal static volatile bool ShuffleStateActive;

    // Located struct for the current shuffle (0 = not found).
    private nint _logic;
    private int _count;
    private int _lastCursor = -1;
    private string _lastHoverSig = "";   // cursor|cardName of the last hover announce
    private int _lastTriesSpoken = -999; // last spoken "you can take N cards" value (for settle re-announce)
    private int _prevTries = -1;         // head[5] from the previous poll (stability gate for the re-announce)
    private int _unresolvedCursor = -1;  // cursor position currently held as unresolvable (change-card grace)
    private int _unresolvedPolls = 0;    // consecutive polls that position has been unresolvable
    private int _invalidPolls;           // consecutive polls the latched header failed validation
    private string _pendHoverSig = "";   // hover awaiting the info panel to settle (ShuffleText)
    private long _pendHoverTick;
    // Created-card (change) tracking for the current spread:
    //   _createdResolved: created id → (texture slot, name, effect) once proven — lets a LATER
    //   change resolve too (incremental promotion; closes the old double-change gap).
    //   _announcedCreated / _pendingCreated: which created ids have been announced / how long
    //   an unresolved one has been waiting for its texture.
    private readonly System.Collections.Generic.Dictionary<int, (int Slot, string Name, string Effect)> _createdResolved = new();
    private readonly System.Collections.Generic.HashSet<int> _announcedCreated = new();
    private readonly System.Collections.Generic.Dictionary<int, int> _pendingCreated = new();
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

    // THE SCAN GATE (2026-07-10 — the "result menu is heavy" fix, v2). State 17 spans
    // the whole post-battle flow, so on a battle with NO Shuffle Time the reader used
    // to grind full-heap scans (~800ms over ~1500 regions, RPM of gigabytes) through
    // the entire RESULT screen — and a 15s give-up window (v1 of this fix) still let
    // the burst land exactly on the result screen. The REAL gate came from a live
    // NAMED-TASK REGISTRY dump (2026-07-10): the game runs battle_shuffle /
    // battle_shuffle_bg / _eff / _panel / _result tasks for EXACTLY the Shuffle Time
    // window (skipped deals and one-mores included) and never spawns them on a
    // shuffle-less battle. So: scan ONLY while a battle_shuffle* task is alive —
    // a microsecond registry walk per poll, zero scans on result-only screens.
    // (If the check ever broke, the failure mode is mild: ShuffleText's no-struct
    // panel announcer still reads every card by name.)
    private bool _taskWasAlive;

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
                ShuffleStateActive = state == StateShuffle;
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
                    // Reset the scan pacing even when nothing was ever latched — DropStruct
                    // early-returns when _logic==0, so a fruitless shuffle used to leave
                    // _scanAttempts high and the NEXT shuffle started pre-starved in the
                    // 1.5s/6s backoff tiers (live log 2026-07-03: attempts 1→5 spanning two
                    // separate shuffles).
                    _scanAttempts = 0;
                    _nextScanTick = 0;
                    _noPathHits.Clear();
                    _taskWasAlive = false;   // silent reset — no transition log across battles
                    continue;
                }

                // KEEP scanning after a pick. A "One More!" deals a fresh spread in
                // the SAME UI state (17); the just-picked spread is remembered as
                // _pickedSig so its lingering copy isn't re-latched (ScanChunk skips it).
                if (_logic == 0)
                {
                    long now = Environment.TickCount64;

                    // THE GATE: the game's own shuffle sequence runs as battle_shuffle*
                    // named tasks — scan only while one is alive. A shuffle-less
                    // battle's result screen gets ZERO full-heap scans.
                    bool shuffleTask = IsShuffleTaskAlive();
                    if (shuffleTask != _taskWasAlive)
                    {
                        _taskWasAlive = shuffleTask;
                        Log(shuffleTask
                            ? "[Shuffle] battle_shuffle task alive — scanning enabled"
                            : "[Shuffle] battle_shuffle task gone — scans off");
                        if (shuffleTask) { _scanAttempts = 0; _nextScanTick = 0; } // fresh fast window
                    }
                    if (!shuffleTask) continue;

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
                // resolves, and the arena page can be reused for anything. Validation is
                // the RELAXED header (a skipped deal never regains the strict shape) plus
                // the card paths themselves — the paths are the struct's identity, so a
                // reused page can't impersonate it even with a stale-valid header.
                Span<uint> head = stackalloc uint[8];
                bool headOk = ReadSelf(_logic, head) && RelaxedHeader(head);
                int liveCount = headOk ? (int)head[1] : _count;
                var liveCards = ReadCardNames(_logic, liveCount, quiet: true);
                bool anyLive = false;
                foreach (var c in liveCards) if (c != null) { anyLive = true; break; }
                if (anyLive && _pathless)
                {
                    // The paths arrived after all (very late deal) — upgrade to named mode.
                    Log("[Shuffle] paths appeared on a path-less spread — names available now");
                    _pathless = false;
                }
                if (!headOk || (!anyLive && !_pathless))
                {
                    // TRANSIENT vs REAL invalidation (v1.3.5 — the "skip breaks the whole
                    // shuffle" fix). The header flickers invalid for a few frames during the
                    // deal-SKIP / take / change animations while the struct stays alive at
                    // this address. The old immediate drop marked the LIVE spread as "just
                    // picked" (_pickedSig), so the rescan refused to re-latch it while the
                    // round hadn't advanced — the rest of the shuffle read NOTHING. Tolerate
                    // a short invalid window; only a persistent invalidation (a real pick
                    // frees the struct) drops.
                    if (++_invalidPolls < 10) continue;   // ~500ms
                    Log($"[Shuffle] struct invalid {_invalidPolls} polls (headOk={headOk} paths={anyLive}) — dropping (pick). " +
                        $"h=[{head[0]:X},{head[1]:X},{head[2]:X},{head[3]:X},{head[4]:X},{head[5]:X},{head[6]:X},{head[7]:X}]");
                    _pickedSig = _lastSig;                     // pick done — skip its ghost
                    _pickedRound = _lastRound;                 // …only while the round hasn't advanced
                    DropStruct(announcePick: true);
                    continue;
                }
                if (_invalidPolls > 0)
                    Log($"[Shuffle] struct recovered after {_invalidPolls} invalid polls (transient — kept the spread)");
                _invalidPolls = 0;

                // Compare the card CONTENT, not just the count: the deal grows the count
                // as cards land (2→4), AND a "One More!" RESHUFFLES the cards in place —
                // usually with the SAME count, so a count-only check reads the new spread
                // as "the same cards" (user bug 2026-06-17: one-more not recognised).
                int liveRound = (int)head[0];
                string liveSig = SpreadSig(liveCards, liveCount);
                // A "One More!" advances the ROUND (h[0] 1→3) and reshuffles in place
                // — sometimes to the SAME cards, so also trigger on a round change.
                if (liveCount != _count || liveSig != _lastSig || liveRound != _lastRound)
                {
                    // How many card slots actually differ? A REAL one-more re-DEALS the
                    // spread (most slots change / count changes); the post-pick REVEAL
                    // merely bumps the round counter and touches ≤1-2 slots — announcing
                    // "One more" on a round bump alone was the false-one-more bug
                    // (user 2026-07-03: took a normal card, heard "One more").
                    int diffs = 0;
                    for (int i = 0; i < liveCards.Length; i++)
                        if (i >= _cards.Length || liveCards[i] != _cards[i]) diffs++;
                    bool redealt = liveCount != _count || diffs > 2;
                    if (liveRound != _lastRound && !redealt)
                        Log($"[Shuffle] round {_lastRound} → {liveRound}, spread ~unchanged ({diffs} diffs) — reveal, not one-more");
                    _lastRound = liveRound;

                    if (!_foundAnnounced || redealt)
                    {
                        // Deal still settling, or a genuine re-deal → (re)arm the spread announce.
                        bool wasReady = _foundAnnounced;
                        _count = liveCount;
                        _cards = liveCards;
                        _lastSig = liveSig;
                        _lastCursor = -1;
                        _lastHoverSig = "";                 // a reshuffle may change cards in place
                        _foundAnnounced = false;            // re-announce the new spread
                        _announceTick = Environment.TickCount64 + AnnounceDebounceMs;
                        if (redealt) { _createdResolved.Clear(); _announcedCreated.Clear(); _pendingCreated.Clear(); }
                        if (wasReady) Log($"[Shuffle] spread re-dealt (one-more) → round {liveRound}, {liveCount} cards: {liveSig}");
                        continue;
                    }
                    // Small in-place mutation after the spread was announced: a picked
                    // change card rewrote a texture (or a reveal cleared a slot). Track
                    // silently; the created-card announcement below names real changes.
                    _count = liveCount;
                    _cards = liveCards;
                    _lastSig = liveSig;
                    Log($"[Shuffle] spread mutated in place ({diffs} slots): {liveSig}");
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

                // Resolve the panel display map (logical card-ids) to the card at each
                // display position. Direct ids (< dealt) keep their remembered identity;
                // a created/changed card resolves by the single-created↔single-changed-slot
                // match, or by an EARLIER resolution remembered in _createdResolved
                // (incremental promotion). null = genuinely ambiguous → "card changed";
                // a confident wrong name is impossible.
                var resolved = validMap ? ResolveSlots(pmap, dealt) : null;

                // PROACTIVE change announcements (v1.3.5 — "the mod isn't good at pointing
                // out the changed card"). When a picked change card creates a new id in the
                // panel map, name it as soon as its texture resolves: "Card N changed to X."
                // The user no longer has to re-browse the spread to discover the change.
                if (validMap && _foundAnnounced && resolved != null && !_pathless)
                {
                    for (int i = 0; i < pmap.Length && i < resolved.Length; i++)
                    {
                        int cid = pmap[i];
                        if (cid < dealt || _announcedCreated.Contains(cid)) continue;
                        if (resolved[i] is (string chName, string chEff))
                        {
                            _announcedCreated.Add(cid);
                            _pendingCreated.Remove(cid);
                            // If the cursor is sitting on this position, sync the hover sig so
                            // the hover block doesn't instantly re-announce over this message.
                            if (i == (int)head[4]) _lastHoverSig = $"{i}|{chName}";
                            string msg = $"Card {i + 1} changed to {chName}." +
                                         (string.IsNullOrEmpty(chEff) ? "" : $" {chEff}");
                            Log($"[Shuffle] change announce: {msg}");
                            Speech.Say(msg, true);
                        }
                        else
                        {
                            // Texture not written yet (loading) — or a mass-change (Fool).
                            // Give it ~700ms to resolve, then announce position-only.
                            _pendingCreated.TryGetValue(cid, out int polls);
                            _pendingCreated[cid] = ++polls;
                            if (polls >= 14)
                            {
                                _announcedCreated.Add(cid);
                                _pendingCreated.Remove(cid);
                                Log($"[Shuffle] change announce (unresolved): card {i + 1} id={cid}");
                                Speech.Say($"Card {i + 1} changed.", true);
                            }
                        }
                    }
                }

                // The cursor can range over MORE positions than `remaining`: already-taken
                // cards stay on screen and hoverable (live 2026-07-03 — positions 4/5 of a
                // 3-remaining spread were unreadable). The info panel names whatever is
                // hovered, so announce for any sane cursor; the display total shows at
                // least the hovered position.
                int cursor = (int)head[4];
                if (cursor >= 0 && cursor < 16)
                {
                    int totalDisp = Math.Max(remaining, cursor + 1);
                    string? cn; string eff;
                    if (_pathless)
                        (cn, eff) = (null, "");                                // skipped deal — no names exist
                    else if (resolved != null && cursor < resolved.Length && resolved[cursor] is (string rn, string re))
                        (cn, eff) = (rn, re);                                  // this position resolved
                    else if (validMap && cursor >= pmap.Length)
                        (cn, eff) = (null, "");                                // beyond the live map (taken card) — panel names it
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
                        // GAME-TRUTH NAMES (v1.3.5, user's idea): the info panel renders the
                        // hovered card's title + description as text (ShuffleText). Prefer it —
                        // it exists on SKIPPED deals (no texture paths) and after any change.
                        // Wait briefly for the panel to re-render for THIS hover and settle one
                        // window (so a mid-transition merge is never spoken); fall back to the
                        // struct-derived name on timeout.
                        long nowH = Environment.TickCount64;
                        if (_pendHoverSig != hoverSig) { _pendHoverSig = hoverSig; _pendHoverTick = nowH; }
                        string? panel = ShuffleText.LatestPanel;
                        // Per-frame reconstruction is stable frame-to-frame, so 50ms of
                        // stillness suffices (was 130ms of window churn — "feels slow").
                        bool panelReady = panel != null
                                          && ShuffleText.LatestChangedTick >= _pendHoverTick - 60
                                          && nowH - ShuffleText.LatestChangedTick >= 50;
                        if (!panelReady && nowH - _pendHoverTick < 300) { _prevTries = tries; continue; }
                        if (!panelReady && panel != null && nowH - ShuffleText.LatestSeenTick < 300)
                            panelReady = true;   // panel live but text identical (duplicate card) — trust it
                        _lastHoverSig = hoverSig;
                        _lastTriesSpoken = triesSane ? tries : _lastTriesSpoken;
                        _lastCursor = cursor;
                        string name, effStr;
                        if (panelReady)
                        {
                            string p2 = panel!;
                            // The game's panel omits the SUIT RANK (bigger rank = bigger
                            // bonus — user 2026-07-04). Inject it from the struct-decoded
                            // name when available; skipped deals have no paths → no rank.
                            if (cn != null)
                            {
                                var mr = System.Text.RegularExpressions.Regex.Match(cn, @" rank (\d+)$");
                                if (mr.Success && !p2.Contains("rank", StringComparison.OrdinalIgnoreCase))
                                {
                                    int dot = p2.IndexOf('.');
                                    string ins = $", rank {mr.Groups[1].Value}";
                                    p2 = dot > 0 ? p2.Insert(dot, ins) : p2 + ins;
                                }
                            }
                            name = $", {p2}"; effStr = "";
                        }
                        else
                        {
                            name = cn != null ? $", {cn}" : "";
                            effStr = string.IsNullOrEmpty(eff) ? "" : $". {eff}";
                        }
                        Log($"[Shuffle] announce: Card {cursor + 1} of {totalDisp}{name}{effStr}{triesStr}");
                        Speech.Say($"Card {cursor + 1} of {totalDisp}{name}{effStr}{triesStr}", true);
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
        _invalidPolls = 0;
        _pathless = false;
        _pendHoverSig = "";
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

    // RELAXED header (v1.3.5 — the "skip leaves the header out of strict shape" fix; live log
    // 2026-07-03: a skipped deal never matched IsLogicHeader across 15+ scans, so the whole
    // shuffle went unread). Only the structurally-load-bearing fields: dealt count, two zero
    // words, and a sane cursor. The card-path check is the real identity proof; the strict
    // header remains the PREFERRED match so normal deals behave exactly as before.
    private static bool RelaxedHeader(Span<uint> h) =>
        h[1] >= 2 && h[1] <= 16 && h[3] == 0 && h[4] < 16 && h[7] == 0;

    // ---- the shuffle-task gate --------------------------------------------------
    // The NAMED-TASK REGISTRY (the TvListings anchor: heads 0x1462486F8/0x1462486A8/
    // 0x146248768, name @node+0x00, next @node+0x50). A live [ShufTask] dump
    // (2026-07-10) showed the shuffle sequence runs as battle_shuffle +
    // battle_shuffle_bg/_eff/_panel (+_result at pick time), present for EXACTLY the
    // Shuffle Time window and never on a shuffle-less battle — the perfect scan gate.
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] ShuffleTaskPrefix =
        System.Text.Encoding.ASCII.GetBytes("battle_shuffle");

    /// <summary>True while any battle_shuffle* task is in the registry — i.e. the
    /// game's own Shuffle Time sequence is running. ~45 RPM node reads, microseconds.</summary>
    private bool IsShuffleTaskAlive()
    {
        try
        {
            Span<byte> headBuf = stackalloc byte[8];
            Span<byte> node = stackalloc byte[0x58];
            foreach (nint head in TaskHeads)
            {
                if (!ReadSelf(head, headBuf)) continue;
                nint n = (nint)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(headBuf);
                for (int i = 0; i < 256 && n != 0; i++)
                {
                    if (!ReadSelf(n, node)) break;
                    bool match = true;
                    for (int k = 0; k < ShuffleTaskPrefix.Length; k++)
                        if (node[k] != ShuffleTaskPrefix[k]) { match = false; break; }
                    if (match) return true;
                    n = (nint)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(node.Slice(0x50));
                }
            }
        }
        catch { /* fail closed: no scans; ShuffleText's panel announcer still reads cards */ }
        return false;
    }

    // ---- the scan -------------------------------------------------------------

    private int _scanAttempts;

    // First relaxed-only candidate seen during the current walk (strict header failed but the
    // card paths verify). Consumed only when the whole walk finds no strict match.
    private nint _relaxCand;
    private int _relaxCount;
    private string _relaxLog = "";

    private void FindStruct()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int regions = 0;
        _relaxCand = 0;

        // ★ ANCHOR FIRST (2026-07-19): the spread struct lives INSIDE the
        // battle_shuffle task's WORK allocation at +0x1BB08 — proven by two
        // independent latches with the exact same delta (the anchors log).
        // Registry walk + one 0x30 read replaces the ~900ms full-heap sweep
        // whose VirtualQuery walk contended the game's allocation lock — THE
        // result-screen lag. The sweep below survives only as a fallback in
        // case the offset ever shifts.
        nint anchorCand = ShuffleWorkAnchor();
        if (anchorCand == 0) return;   // task not up yet — the struct cannot exist
        byte[] abuf = new byte[0x30];
        if (ReadSelf(anchorCand, abuf.AsSpan(0, 0x30)) && ScanChunk(anchorCand, abuf, 0x30))
        { LogFound(sw, regions); return; }
        if (_relaxCand != 0)
        {
            LatchStruct(_relaxCand, _relaxCount);
            Log($"[Shuffle] RELAXED latch (anchor) @ 0x{_relaxCand:X}: {_relaxLog}");
            LogFound(sw, regions);
            return;
        }

        // ★ HEAP SWEEPS DELETED (2026-07-19). The spread ONLY ever lives at the
        // anchor — it's inside the battle_shuffle task's work allocation, and
        // One-More re-deals mutate IN PLACE at the same address ("spread mutated"
        // logs). The old post-pick sweep loop ran 20 × ~830ms through the reveal/
        // reward/release flow hunting a re-deal that can only appear HERE — that
        // was THE reward-screen lag (VirtualQuery contends the game's allocation
        // lock). Candidate not valid yet → the next poll retries, ~free. If the
        // anchor ever misses entirely, ShuffleText's panel announcer still reads
        // every card (proven carrying whole sessions alone).
        _scanAttempts++;
        if (!_announcedEntry || _scanAttempts % 20 == 0)
        {
            Log($"[Shuffle] anchor cand 0x{anchorCand:X} not valid yet (attempt {_scanAttempts})");
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
                // RELAXED pre-filter — only the structurally required fields (count, two
                // zero words, sane cursor). count >= 2: count=1 lookalikes flooded the
                // scan (2026-06-11) and no real shuffle deals a single card.
                if (u[1] < 2 || u[1] > 16 || u[3] != 0 || u[4] >= 16 || u[7] != 0) continue;
                // +0x20 must hold a plausible low-4GB heap pointer.
                ulong ptr = *(ulong*)(b + i + 0x20);
                if (ptr < 0x10000 || ptr >= 0x1_0000_0000UL) continue;
                // STRICT extras — the preferred match, i.e. a normally-dealt spread. A
                // SKIPPED deal leaves the header out of this shape (live log 2026-07-03:
                // 15+ scans never matched), so a strict miss can still latch via the
                // relaxed fallback below — its card paths are the real identity proof.
                // h[6] can read 0xFFFFFFFF at higher progression (live 2026-07-03 — a NORMAL
                // deal only latched via the relaxed fallback because of it); accept both shapes.
                bool strict = u[0] >= 1 && u[0] <= 0xF && u[2] <= 0x20 && u[4] < u[1] &&
                              u[5] >= 1 && u[5] <= 0x3F && (u[6] <= 0xFF || u[6] == 0xFFFFFFFF);
                if (!strict && _relaxCand != 0) continue;   // already holding a fallback
                if (!ReadSelf((nint)ptr, probe)) continue;

                // The header alone false-positives on unrelated objects (seen live: a
                // permanent count=2 match at 0xAD5FB10). The definitive check: the real
                // struct has its card texture paths at +0x1EC0 (stride 0x138) — an ASCII
                // "card/" prefix there can't happen by accident. During the deal animation
                // the paths aren't written yet — we keep rescanning until they appear,
                // which also means the announce lands right as the cards settle.
                nint cand = baseVa + i;
                int count = (int)u[1];
                var cards = ReadCardNames(cand, count, quiet: true);
                bool any = false;
                foreach (var c in cards) if (c != null) { any = true; break; }
                if (!any)
                {
                    if (!strict) continue;
                    // (No DumpDiag here — garbage candidates cycling through fresh addresses,
                    // e.g. our own LOG TEXT in memory, spammed dumps in a feedback loop,
                    // live 2026-07-03. Dumps only fire on a real latch now.)
                    if (cand != _lastRejected)
                    {
                        _lastRejected = cand;
                        Log($"[Shuffle] candidate @ 0x{cand:X} count={count} has no card paths yet — waiting");
                    }
                    // PATH-LESS LATCH (v1.3.5 — the real skip effect, log-proven 2026-07-03:
                    // a SKIPPED deal's struct keeps a strict-valid header but the deal
                    // animation never writes the card texture paths, so the path wait
                    // starved forever and the shuffle read NOTHING). A normal deal gains
                    // its paths within a scan or two; when a strict candidate stays
                    // path-less across a couple of scans AND its panel map validates, it
                    // IS the live skipped spread — latch it (names come from ShuffleText's
                    // info-panel capture). Hits are counted PER ADDRESS — see field note.
                    int hits = _noPathHits.TryGetValue(cand, out int h) ? h + 1 : 1;
                    if (_noPathHits.Count < 64) _noPathHits[cand] = hits;
                    if (hits >= 2 && PanelMapValidAt(cand, count))
                    {
                        Log($"[Shuffle] PATH-LESS latch @ 0x{cand:X} count={count} (skipped deal) " +
                            $"h=[{u[0]:X},{u[1]:X},{u[2]:X},{u[3]:X},{u[4]:X},{u[5]:X},{u[6]:X},{u[7]:X}]");
                        LatchStruct(cand, count, cards, SpreadSig(cards, count), (int)u[0], pathless: true);
                        return true;
                    }
                    continue;
                }
                cards = ReadCardNames(cand, count);   // verbose re-read for the log
                string sig = SpreadSig(cards, count);

                // Skip the spread we just picked from (its struct lingers through the
                // reveal/reward phase). BUT a "One More!" reshuffles it in place with
                // the SAME cards and an ADVANCED round (h[0]) — so only skip while the
                // round hasn't advanced past the one we picked (else the one-more is
                // never re-latched, user 2026-06-17). A relaxed candidate's h[0] may be
                // junk, so it skips on the signature alone.
                if (_pickedSig != null && sig == _pickedSig && (!strict || (int)u[0] <= _pickedRound))
                {
                    if (cand != _lastRejected)
                    {
                        _lastRejected = cand;
                        Log($"[Shuffle] @ 0x{cand:X} is the just-picked spread (round {u[0]}) — skipping");
                    }
                    continue;
                }

                if (!strict)
                {
                    // Remember as the walk's fallback; keep going in case a strict match
                    // exists elsewhere. Consumed by FindStruct after the full walk.
                    _relaxCand = cand;
                    _relaxCount = count;
                    _relaxLog = $"h=[{u[0]:X},{u[1]:X},{u[2]:X},{u[3]:X},{u[4]:X},{u[5]:X},{u[6]:X},{u[7]:X}] " +
                                $"cards=[{string.Join(" | ", cards)}]";
                    continue;
                }

                LatchStruct(cand, count, cards, sig, (int)u[0]);
                return true;
            }
        }
        return false;
    }

    private void LatchStruct(nint cand, int count, string?[]? cards = null, string? sig = null,
                             int round = -1, bool pathless = false)
    {
        cards ??= ReadCardNames(cand, count);
        if (round < 0)
        {
            Span<uint> h = stackalloc uint[8];
            round = ReadSelf(cand, h) ? (int)h[0] : 1;
        }
        _pickedSig = null;
        _logic = cand;
        _count = count;
        _lastCursor = -1;            // force the first announcement
        _cards = cards;
        _lastSig = sig ?? SpreadSig(cards, count);
        _lastRound = round;
        _foundAnnounced = false;
        _announceTick = Environment.TickCount64 + AnnounceDebounceMs;
        _invalidPolls = 0;
        _pathless = pathless;
        _noPathHits.Clear();
        ShuffleActive = true;
    }

    // Path-less (skipped-deal) tracking: PER-ADDRESS bare-scan counts. ⚠ A single
    // last-candidate slot ping-ponged between a permanent path-less header LOOKALIKE
    // (lower address, scanned first) and the real struct, so the count never reached
    // the threshold and skipped shuffles stayed silent (log-proven 2026-07-04).
    // _pathless = the latched spread has no texture paths (names come from the panel).
    private readonly System.Collections.Generic.Dictionary<nint, int> _noPathHits = new();
    private bool _pathless;

    /// <summary>The candidate's panel slot-map at +0xE7C validates (distinct u16 slots
    /// &lt; 16, 0xFFFF-terminated) and its remaining count at +0xE88 is sane — the identity
    /// gate for a path-less latch (a header lookalike has neither).</summary>
    private static bool PanelMapValidAt(nint cand, int count)
    {
        Span<byte> b = stackalloc byte[16 * 2];
        if (!ReadSelf(cand + PanelMapOff, b)) return false;
        int n = 0, seen = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = MemoryMarshal.Read<ushort>(b.Slice(i * 2, 2));
            if (v == 0xFFFF) break;
            if (v > 15 || (seen & (1 << v)) != 0) return false;
            seen |= 1 << v;
            n++;
        }
        if (n < 1 || n > count + 8) return false;
        Span<uint> rb = stackalloc uint[1];
        if (!ReadSelf(cand + RemainingOff, rb)) return false;
        return rb[0] >= 1 && rb[0] <= 16;
    }

    private void LogFound(System.Diagnostics.Stopwatch sw, int regions)
    {
        // spoken announce is deferred until the card count sits still (Poll)
        Log($"[Shuffle] struct @ 0x{_logic:X} count={_count} cards=[{string.Join(" | ", _cards)}] " +
            $"({regions} regions, {sw.ElapsedMilliseconds}ms)");
        // TEMP ANCHOR HUNT (2026-07-19): the 941ms full-heap sweep at result-screen
        // open is THE reward lag (VirtualQuery contends the game's allocation lock).
        // If this struct is reachable from a battle_shuffle* task's work struct, the
        // scan dies. Logs each task's work + delta + any work[0..0x400] u64 == _logic.
        DumpAnchorHunt(_logic);
    }

    private static readonly byte[] _shufflePrefixBytes = System.Text.Encoding.ASCII.GetBytes("battle_shuffle");

    /// <summary>The spread-struct ANCHOR: the task named EXACTLY "battle_shuffle"
    /// (NUL after the prefix — battle_shuffle_bg/panel/eff/result must not match),
    /// its work pointer + 0x1BB08. Returns 0 when the task isn't running.</summary>
    private nint ShuffleWorkAnchor()
    {
        try
        {
            Span<byte> nm = stackalloc byte[16];
            Span<byte> q = stackalloc byte[8];
            foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
            {
                if (!ReadSelf((nint)head, q)) continue;
                nint node = (nint)BitConverter.ToInt64(q);
                for (int i = 0; i < 300 && node != 0; i++)
                {
                    if (!ReadSelf(node, nm)) break;
                    // Exact task = prefix + a NON-PRINTABLE byte (not necessarily
                    // NUL — the ==0 check silently missed every anchor, 2026-07-19;
                    // _bg/_panel/_eff/_result continue with printable '_').
                    if (nm.Slice(0, 14).SequenceEqual(_shufflePrefixBytes)
                        && (nm[14] < 0x20 || nm[14] >= 0x7F))
                    {
                        if (!ReadSelf(node + 0x48, q)) return 0;
                        nint work = (nint)BitConverter.ToInt64(q);
                        return work > 0x10000 ? work + 0x1BB08 : 0;
                    }
                    if (!ReadSelf(node + 0x50, q)) break;
                    node = (nint)BitConverter.ToInt64(q);
                }
            }
        }
        catch { }
        return 0;
    }

    private void DumpAnchorHunt(nint target)
    {
        try
        {
            var sb = new System.Text.StringBuilder("[Shuffle] anchors:");
            Span<byte> nm = stackalloc byte[24];
            Span<byte> q = stackalloc byte[8];
            foreach (long head in new[] { 0x1462486F8L, 0x1462486A8L, 0x146248768L })
            {
                if (!ReadSelf((nint)head, q)) continue;
                nint node = (nint)BitConverter.ToInt64(q);
                for (int i = 0; i < 300 && node != 0; i++)
                {
                    if (!ReadSelf(node, nm)) break;
                    if (nm[0] == (byte)'b' && nm.Slice(0, 14).SequenceEqual(_shufflePrefixBytes))
                    {
                        int len = 0;
                        while (len < 24 && nm[len] >= 0x20 && nm[len] < 0x7F) len++;
                        string name = System.Text.Encoding.ASCII.GetString(nm.Slice(0, len));
                        nint work = ReadSelf(node + 0x48, q) ? (nint)BitConverter.ToInt64(q) : 0;
                        sb.Append($" {name}: work=0x{work:X} d={(long)target - (long)work:X}");
                        for (int off = 0; off < 0x400; off += 8)
                            if (ReadSelf(work + off, q) && (nint)BitConverter.ToInt64(q) == target)
                                sb.Append($" [work+0x{off:X}=STRUCT]");
                    }
                    if (!ReadSelf(node + 0x50, q)) break;
                    node = (nint)BitConverter.ToInt64(q);
                }
            }
            Log(sb.ToString());
        }
        catch { }
    }
    // (The ShufDiag identity dumps are GONE — the hunt ended 2026-07-03 when the card
    // records proved to be texture bookkeeping only; names come from the info-panel
    // text now (ShuffleText). Do not re-add record/type-array scans.)

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
        // A pristine spread has no created ids, so the change-tracking state is stale
        // by definition (created ids are small ints and get reused across spreads).
        _createdResolved.Clear();
        _announcedCreated.Clear();
        _pendingCreated.Clear();
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

        // 1.5 — created cards resolved EARLIER in this spread keep their identity too
        // (incremental promotion). Without this, the SECOND change of a spread saw two
        // created ids and every one degraded to "card changed" (the old double-change gap).
        for (int i = 0; i < rem; i++)
        {
            int id = map[i];
            if (res[i].HasValue || id < dealt) continue;
            if (_createdResolved.TryGetValue(id, out var cr))
            {
                if (cr.Slot >= 0 && cr.Slot < MaxCardSlots && live[cr.Slot].Name == cr.Name)
                { res[i] = (cr.Name, live[cr.Slot].Effect); usedSlot[cr.Slot] = true; }
                else res[i] = (cr.Name, cr.Effect);
            }
        }

        // 2 — the single UNRESOLVED created card ↔ the single changed slot. "Changed" =
        // a slot whose live card differs from the remembered one, OR a slot that gained
        // its FIRST texture (the game sometimes appends the new card to a slot beyond the
        // deal instead of clobbering a bystander — map referencing slot 3/4 with dealt=3,
        // seen live). A drawn card's lingering texture equals _orig, so it can't be
        // mistaken for a change. Multiple of either → can't match safely → null.
        var changedSlots = new System.Collections.Generic.List<int>();
        for (int s = 0; s < MaxCardSlots; s++)
            if (live[s].Name != null && !usedSlot[s] &&
                (_orig[s] is not (string cn, _) || live[s].Name != cn))
                changedSlots.Add(s);
        var createdPos = new System.Collections.Generic.List<int>();
        for (int i = 0; i < rem; i++)
            if (!res[i].HasValue && map[i] >= dealt) createdPos.Add(i);
        if (changedSlots.Count == 1 && createdPos.Count == 1)
        {
            var c = live[changedSlots[0]];
            res[createdPos[0]] = (c.Name!, c.Effect);
            // PROMOTE: remember this created id ↔ slot ↔ card, and fold it into _orig so
            // the NEXT change sees this slot as a known quantity. Sequential changes now
            // resolve one at a time; only a simultaneous mass-change (Fool) still degrades.
            int cid = map[createdPos[0]];
            if (!_createdResolved.ContainsKey(cid))
                Log($"[Shuffle] resolved created id {cid} → slot {changedSlots[0]} ({c.Name})");
            _createdResolved[cid] = (changedSlots[0], c.Name!, c.Effect);
            _orig[changedSlots[0]] = (c.Name!, c.Effect);
        }
        else if (createdPos.Count > 1 && createdPos.Count != _lastMultiLogged)
        {
            _lastMultiLogged = createdPos.Count;
            Log($"[Shuffle] multi-change: {createdPos.Count} unresolved created ids, " +
                $"{changedSlots.Count} changed slots — degrading to \"card changed\" (Fool?)");
        }
        return res;
    }
    private int _lastMultiLogged;


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
            string nm;
            if (string.IsNullOrWhiteSpace(pname)) nm = $"Persona card {pid}";
            else
            {
                nm = $"Persona {pname}";
                // Enrich with arcana + species base level (user 2026-07-04) — the
                // species table *(0x140EC0958), stride 0xE: arcana @+2, level @+3.
                try
                {
                    string parc = Battle.PersonaArcanaName(pid);
                    if (!string.IsNullOrEmpty(parc) && !parc.StartsWith("arcana ")) nm += $", {parc}";
                }
                catch { }
                Span<byte> pb = stackalloc byte[8];
                if (ReadSelf(unchecked((nint)0x140EC0958L), pb))
                {
                    nint t = (nint)BitConverter.ToInt64(pb);
                    Span<byte> lb = stackalloc byte[1];
                    if (t != 0 && ReadSelf(t + pid * 0xE + 3, lb) && lb[0] > 0 && lb[0] < 100)
                        nm += $", level {lb[0]}";
                }
            }
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
