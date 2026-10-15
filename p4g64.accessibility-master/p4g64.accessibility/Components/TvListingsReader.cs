using System.Text;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// TV LISTINGS reader (2026-07-07). The main-menu / in-game TV guide (bonus
/// content browser). This screen renders from PRERENDERED SPRITE SHEETS
/// (title/channelMain.arc), never through the shared UI-text fn — so this
/// reader POLLS the game's own menu state instead of hooking a text renderer.
///
/// RE map (session 2026-07-07):
///  - The screen is the task "CHANNEL_MAIN_PROC" (update FUN_14034F2C0). The
///    task registry is 3 singly-linked lists with STATIC heads 0x1462486F8 /
///    0x1462486A8 / 0x146248768; node name = ASCII at node+0x00, next at
///    node+0x50, task work struct = *(node+0x48). (Layout proven from
///    FindTaskByName FUN_1404FB910.)
///  - work+0x04 (i32) = screen state (3..8 open/browse, 9/10 move anim,
///    14-16 zoomed view, 0x13+ subscreens). work+0x60 (i32) = SELECTED
///    PROGRAM ID (>=100 = channel footer card, 100+channel). work+0x64
///    (i32) = channel index (main menu shows 1..6; 0 and 7 exist for the
///    in-game 7-strip variant).
///  - Program table (static): 0x140B0E400, 8 channels x 13 rows, stride 0x28,
///    entry+0x00 = program id, -1 = end of channel.
///  - Per-program unlock byte: *(*(0x1451BCCA8)) + 0x80 + id:
///    0 = locked ("Coming Soon" card), 1 = available/watched, 2 = NEW badge,
///    3 = special (program quits the game session).
///  - English program titles (static, ASCII, stride 0x40): 0x140AA44A0 +
///    id*0x40 (the per-language telop tables in .xpdata).
///  - Names/descriptions overlay: database/tvlistings_catalog.json (editable
///    without rebuild; descriptions transcribed from the baked sheets).
/// SPOILER RULE: a locked card reads "Coming soon" only — never its title.
/// </summary>
internal sealed unsafe class TvListingsReader
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly nint SaveBlkPtrVA = unchecked((nint)0x1451BCCA8L);
    private static readonly nint TitleTableVA = unchecked((nint)0x140AA44A0L);
    private static readonly byte[] TaskName = Encoding.ASCII.GetBytes("CHANNEL_MAIN_PROC");
    // Channel SUBSCREEN tasks (each channel player is its own named task,
    // created on open, destroyed on close — task presence == screen open).
    private static readonly byte[] MusicTaskName = Encoding.ASCII.GetBytes("MUSIC_CHANNEL");
    internal static readonly byte[] AnimeTaskName = Encoding.ASCII.GetBytes("ANIME_CHANNEL");

    /// <summary>True while the episode list (Daily Personamations) shows its PREVIEW movie: the
    /// highlighted episode plays behind the menu, and backing out of a cutscene keeps it running as the
    /// preview. ANIME_CHANNEL work+0x04 (u16) = 7 while an episode really plays; 12 browsing, 11/14/15/19/21
    /// transitions (live-probed 2026-10-15). Cutscene descriptions stay silent unless it is 7.</summary>
    internal static bool AnimePreviewShowing()
    {
        nint task = FindTaskByName(AnimeTaskName);
        if (task == 0 || !IsReadable(task + 0x48, 8)) return false;
        nint work = *(nint*)(task + 0x48);
        if (work == 0 || !IsReadable(work + 0x04, 2)) return false;
        return *(ushort*)(work + 0x04) != 7;
    }

    private bool _active;
    private int _lastId = -1, _lastCh = -1;
    private int _pendId = -1, _pendCh = -1;
    private long _settleUntil;

    // music player (song list): work+0x00 u16 state (12 idle / 13 moving),
    // work+0x10 u16 cursor. Playlist names come from the catalog
    // ("music_playlists" keyed by the guide program id 32/33/34).
    private bool _musicActive;
    private int _lastSong = -1;

    // anime episode carousel: work+0x04 u16 state (12 idle / 13 moving),
    // work+0x10 u16 cursor. Episode names from catalog "anime_episodes".
    private bool _animeActive;
    private int _lastEpisode = -1;
    // Episode text by cursor row, kept while the list is open: the first read waits out the carousel
    // slide (~0.7 s), a revisit speaks at once (Haru 2026-10-15: "TV Listings feels heavy").
    private readonly Dictionary<int, string> _episodeText = new();
    private int _animeRetries;

    private readonly Dictionary<int, string> _channelNames = new();
    private readonly Dictionary<int, string> _titles = new();
    private readonly Dictionary<int, string> _descriptions = new();
    private readonly Dictionary<int, List<string>> _musicPlaylists = new();
    private readonly List<string> _animeEpisodes = new();
    private readonly List<string> _animeEpisodeDescs = new();

    // ── Subscreen TEXT CAPTURE ──────────────────────────────────────────
    // Unlike the guide (baked textures), the channel PLAYERS render their
    // rows through the shared UI-text fn FUN_140450C60. The lists hide
    // LOCKED entries, so any static cursor→name mapping mis-indexes and
    // leaks spoilers (user-reported 2026-07-07). Instead we capture what
    // the game itself draws for one frame after each cursor move and speak
    // the game's own strings.
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);
    private IHook<SetTextDelegate>? _textHook;
    private long _captureFrom, _captureUntil;      // TickCount64 window
    private readonly object _capLock = new();
    private readonly List<(string s, byte p2, byte p3, uint p4, bool glyph, long t)> _captured = new();

    internal TvListingsReader(IReloadedHooks hooks)
    {
        LoadCatalog();
        _textHook = hooks.CreateHook<SetTextDelegate>(OnText, SetUiTextVA).Activate();
        var t = new Thread(Poll) { IsBackground = true, Name = "TvListingsReader" };
        t.Start();
        Log($"[TvList] ready (catalog: {_channelNames.Count} channels, {_descriptions.Count} descriptions)");
    }

    private nint _glyphObj = -1;
    private byte _glyphP2;
    private readonly StringBuilder _glyphLine = new();

    private nint OnText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _textHook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        long t0 = PerfDiag.Begin();
        try
        {
            // The art gallery has its own capture machine (interleave-safe, repaint-aware).
            if (_artActive) { ArtCapture(p1, p2, p6); return ret; }
            long now = Environment.TickCount64;
            if (now < _captureFrom || now >= _captureUntil) return ret;
            if (p6 == 0)
            {
                string s = ReadCStrSafe(p1, 96).Trim();
                if (s.Length < 2) return ret;        // skip single glyphs/buttons
                AddCaptured(s, p2, p3, p4, glyph: false);
            }
            else
            {
                // glyph-stream draw (one call per character, keyed by the
                // line object in p6) — the description panels render this way.
                lock (_capLock)
                {
                    _lastCapActivity = Environment.TickCount64;
                    if (p6 != _glyphObj) { FlushGlyphLine(); _glyphObj = p6; _glyphP2 = p2; }
                    string g = ReadCStrSafe(p1, 8);
                    _glyphLine.Append(g.Length > 0 ? g : " ");
                    if (_glyphLine.Length > 200) FlushGlyphLine();
                }
            }
        }
        catch { /* never throw from a hook */ }
        finally { PerfDiag.End(PerfDiag.B.TvCap, t0); }
        return ret;
    }

    private void FlushGlyphLine()
    {
        if (_glyphLine.Length == 0) return;
        string s = _glyphLine.ToString().Trim();
        _glyphLine.Clear();
        if (s.Length >= 5) AddCaptured(s, _glyphP2, 0xFF, 0, glyph: true);
    }

    private void AddCaptured(string s, byte p2, byte p3, uint p4, bool glyph)
    {
        lock (_capLock)
        {
            _lastCapActivity = Environment.TickCount64;
            foreach (var c in _captured)
                if (c.s == s) return;                // dedupe across frames
            _captured.Add((s, p2, p3, p4, glyph, Environment.TickCount64));
        }
    }

    // Last moment ANY capture activity happened — including single GLYPHS still
    // streaming into the pending line (the art settle-detector must not flush a
    // half-painted caption: "Initial" once split as "Initi"+"al", user 2026-07-31).
    private long _lastCapActivity;

    /// <summary>Capture draws inside [now+delay, now+delay+len) — the delay
    /// skips the scroll/slide animation so only the SETTLED frame is read.</summary>
    private void StartCapture(int delayMs, int lenMs)
    {
        lock (_capLock) { _captured.Clear(); _glyphLine.Clear(); _glyphObj = -1; }
        long now = Environment.TickCount64;
        _captureFrom = now + delayMs;
        _captureUntil = now + delayMs + lenMs;
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(50);   // 50 ms: the guide waits one extra poll for a settled program/channel pair
            try
            {
                if (!GameHasFocus()) continue;
                Tick();
            }
            catch { /* poll must never die */ }
        }
    }

    private void Tick()
    {
        if (TickMusic()) return;   // song list open — it owns the announcements
        if (TickAnime()) return;   // episode carousel open
        if (TickArt()) return;     // Soejima art gallery open

        nint task = FindTask();
        if (task == 0)
        {
            _active = false;
            _lastId = -1; _lastCh = -1;
            return;
        }
        if (!IsReadable(task + 0x48, 8)) return;
        nint work = *(nint*)(task + 0x48);
        if (work == 0 || !IsReadable(work, 0x70)) return;

        int state = *(int*)(work + 0x04);
        int id = *(int*)(work + 0x60);
        int ch = *(int*)(work + 0x64);

        // The task PERSISTS after the screen closes — it idles in state 0..2
        // (case 1/2 in FUN_14034F2C0 are no-ops). States 3/4 are opening
        // animation with UNINITIALIZED cursor fields (log-proven: state 3 held
        // a stale id/ch pair). Only state >= 5 has real selection data.
        if (state < 5 || state > 0x30 || ch < 0 || ch > 7 || id < 0 || id > 200)
        {
            _active = false;
            _lastId = -1; _lastCh = -1;
            return;
        }

        if (!_active)
        {
            _active = true;
            _lastId = -1; _lastCh = -1;
            Speech.Say("TV Listings. Left and right for channels, up and down for programs.", true);
            // let the opening announcement land before the first card readout
            _settleUntil = Environment.TickCount64 + 900;
            return;
        }
        if (Environment.TickCount64 < _settleUntil) return;

        if (id == _lastId && ch == _lastCh) { _pendId = -1; return; }
        // A channel change updates the program one frame before the channel: speak only a pair seen
        // on two polls in a row, or the program is read twice, once without its channel (2026-10-15).
        if (id != _pendId || ch != _pendCh) { _pendId = id; _pendCh = ch; return; }
        _pendId = -1;
        bool chChanged = ch != _lastCh;
        _lastId = id; _lastCh = ch;

        string text = Compose(id, ch, chChanged);
        Log($"[TvList] state={state} ch={ch} id={id} say: {text}");
        Speech.Say(text, true);
    }

    private string Compose(int id, int ch, bool withChannel)
    {
        string prefix = withChannel ? ChannelName(ch) + ". " : "";
        if (id >= 100)
            return prefix + "Channel information card.";

        byte st = UnlockState(id);
        if (st == 0)
            return prefix + "Coming soon.";

        string title = _titles.TryGetValue(id, out var t) ? t : ReadTitleFromExe(id);
        if (string.IsNullOrEmpty(title)) title = $"Program {id}";

        var sb = new StringBuilder(prefix);
        sb.Append(title).Append('.');
        if (st == 2) sb.Append(" New.");
        if (st == 3) sb.Append(" Watching this program closes the game session.");
        if (_descriptions.TryGetValue(id, out var d) && d.Length > 0)
            sb.Append(' ').Append(d);
        return sb.ToString();
    }

    /// <summary>
    /// The MUSIC_CHANNEL player (song list inside a Music King program).
    /// The list HIDES locked songs, so names come from the game's own text
    /// draws (capture), never from a static playlist. Returns true while
    /// the player is open (guide announcements pause).
    /// </summary>
    private bool TickMusic()
    {
        nint task = FindTaskByName(MusicTaskName);
        if (task == 0)
        {
            if (_musicActive) { _musicActive = false; _lastSong = -1; }
            return false;
        }
        if (!IsReadable(task + 0x48, 8)) return false;
        nint work = *(nint*)(task + 0x48);
        if (work == 0 || !IsReadable(work, 0x20)) return false;

        int mstate = *(ushort*)(work + 0x00);
        int cursor = *(ushort*)(work + 0x10);
        if (mstate > 0x20 || cursor > 63) return true; // opening/teardown — hold

        if (!_musicActive)
        {
            _musicActive = true;
            _lastSong = -1;
            Speech.Say("Song list.", true);
            _settleUntil = Environment.TickCount64 + 700;
            return true;
        }
        if (Environment.TickCount64 < _settleUntil) return true;
        if (cursor == _lastSong) return true;
        _lastSong = cursor;
        Speech.Say(MusicText(work, cursor), true);
        return true;
    }

    // Per-episode UNLOCKED-track index arrays inside the music work struct
    // (u16, strictly increasing, indexes into the episode's static playlist;
    // the cursor indexes the ARRAY — exact, scroll-independent):
    //   program 32 (Vocal Collection)          -> work+0xB6
    //   program 33 (Event Collection)          -> work+0x16
    //   program 34 (Battle/Dungeon Collection) -> work+0x66
    private static int MusicArrOffset(int programId) => programId switch
    {
        32 => 0xB6, 33 => 0x16, 34 => 0x66, _ => -1
    };

    private string MusicText(nint work, int cursor)
    {
        _musicPlaylists.TryGetValue(_lastId, out var reference);
        int off = MusicArrOffset(_lastId);
        var arr = new List<int>();
        if (off > 0 && IsReadable(work + off, 0x60))
        {
            int prev = -1;
            for (int k = 0; k < 48; k++)
            {
                int v = *(ushort*)(work + off + k * 2);
                if (v <= prev || v >= 200) break;
                arr.Add(v);
                prev = v;
            }
        }
        string name = $"Track {cursor + 1}";
        if (reference != null && cursor >= 0 && cursor < arr.Count && arr[cursor] < reference.Count)
            name = reference[arr[cursor]];
        string text = arr.Count > 0 ? $"{name}. {cursor + 1} of {arr.Count}." : name + ".";
        Log($"[TvList] m prog={_lastId} cursor={cursor} arrLen={arr.Count} say: {text}");
        return text;
    }

    /// <summary>The ANIME_CHANNEL episode carousel (Daily Personamations).
    /// Same capture approach — only the selected episode's title/description
    /// are drawn, so we speak exactly what a sighted player sees.</summary>
    private bool TickAnime()
    {
        nint task = FindTaskByName(AnimeTaskName);
        if (task == 0)
        {
            if (_animeActive) { _animeActive = false; _lastEpisode = -1; _episodeText.Clear(); }
            return false;
        }
        if (!IsReadable(task + 0x48, 8)) return false;
        nint work = *(nint*)(task + 0x48);
        if (work == 0 || !IsReadable(work, 0x20)) return false;

        int astate = *(ushort*)(work + 0x04);
        int cursor = *(ushort*)(work + 0x10);
        if (astate > 0x20 || cursor > 63) return true;

        if (!_animeActive)
        {
            _animeActive = true;
            _lastEpisode = cursor;
            Speech.Say("Episode list.", true);
            StartCapture(400, 200);
            _pendingKind = 'a';
            _animeRetries = 0;
            _announceAt = Environment.TickCount64 + 680;
            return true;
        }
        if (cursor != _lastEpisode)
        {
            _lastEpisode = cursor;
            if (_episodeText.TryGetValue(cursor, out var known))
            {
                _pendingKind = (char)0;
                Speech.Say(known, true);
                return true;
            }
            StartCapture(400, 200);   // wait out the carousel slide + panel repaint
            _pendingKind = 'a';
            _animeRetries = 0;
            _announceAt = Environment.TickCount64 + 680;
        }
        FlushPending(astate, cursor);
        return true;
    }

    // ── The Soejima ART GALLERY ("Giants of P", task ART_CHANNEL) — 2026-07-31 ──
    // Its own work struct is ALL ZEROS and the guide work doesn't move with the
    // picture (both live-probed), so there is no readable cursor. But the CAPTION
    // ("Chie Satonaka / Initial design draft") is DRAWN TEXT — so the reader
    // triggers a capture window on the browse keys (Up/Down = "Pic") and speaks
    // the caption the game itself draws after the carousel settles. Ghost-key
    // guard on entry (the confirm press that opened the gallery is still down).
    private static readonly byte[] ArtTaskName = Encoding.ASCII.GetBytes("ART_CHANNEL");
    private volatile bool _artActive;   // read by the RENDER hook, written by the poll thread
    private bool _artUpWas, _artDownWas;
    private const int VK_UP = 0x26, VK_DOWN = 0x28;

    private bool TickArt()
    {
        nint task = FindTaskByName(ArtTaskName);
        if (task == 0)
        {
            if (_artActive) _artActive = false;
            return false;
        }
        if (!_artActive)
        {
            _artActive = true;
            _artEntryRead = false;
            _artUpWas = IsKeyDown(VK_UP);
            _artDownWas = IsKeyDown(VK_DOWN);
            Speech.Say("Art gallery. Up and down to browse the pictures.", true);
            BeginArtCapture();
            return true;
        }
        bool up = IsKeyDown(VK_UP), down = IsKeyDown(VK_DOWN);
        bool upEdge = up && !_artUpWas, downEdge = down && !_artDownWas;
        _artUpWas = up; _artDownWas = down;
        // Presses during the OPENING ANIMATION are ignored by the game — ignore them
        // too (user 2026-07-31: an early press started a bogus round that misread,
        // then re-read). Browsing arms once the entry read has landed.
        if ((upEdge || downEdge) && _artEntryRead) BeginArtCapture();
        // (Dead reckoning / learned position maps are DEAD ENDS here: key REPEAT
        // auto-scrolls several pictures per key EDGE, so any self-tracked position
        // drifts — correction-trail-proven 2026-07-31. The vote IS the announcer.)
        ArtFlush();
        return true;
    }

    private bool _artEntryRead;

    // Stability model (v2 — the fixed window was BOTH slow and lossy, user 2026-07-31):
    // the capture net stays open for seconds (a late caption can't be missed) and the
    // announce fires as soon as the paint SETTLES (~230ms without a new string) — as
    // fast as the game, never slower than it. The slide redraws the OLD caption first,
    // so only the LAST paint burst (per-entry timestamps, >280ms gaps split bursts)
    // is spoken.
    // ── The art capture machine (v4, 2026-07-31) — built for THIS renderer's truths:
    // the caption REPAINTS EVERY FRAME (settle-detection on silence never fires), its
    // glyph lines INTERLEAVE character-by-character across row objects (flushing on
    // object switch shreds words — "Protagonist's|initial"), and the OLD caption keeps
    // redrawing during the slide (content-based exclusion, not time windows).
    //  - per line-OBJECT accumulator; an append after a >100ms gap = a NEW repaint pass
    //    → the previous pass's text is stashed as that line's COMPLETE text.
    //  - composed caption = complete lines in first-seen order + recent full strings,
    //    minus legend labels, minus the PREVIOUS caption's lines.
    //  - spoken when the composition is nonempty and identical on two consecutive polls.
    // The caption's TWO lines each repaint WHOLE every frame, alternating objects
    // (ArtDiag-proven: ~900 chars/s of repeats, 0-16ms gaps — no timing can split
    // passes). So: flush ON THE OBJECT SWITCH (always yields a complete line),
    // DEDUPE the 60/s repeats, and speak once no NEW distinct line has appeared
    // for a beat. The one impurity — a partial line caught mid-frame at the
    // keypress clear — is dropped at compose time (it's a suffix of a full line).
    private readonly List<string> _artSeen = new();               // distinct lines, draw order
    private readonly HashSet<string> _artSeenSet = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artPrevPieces = new(StringComparer.Ordinal);
    private readonly StringBuilder _artCurSb = new();
    private nint _artCurObj = -1;
    private long _artLastNew;
    private bool _artSpoken;
    private long _artKeyTick;

    private void BeginArtCapture()
    {
        lock (_capLock)
        {
            // FINALIZE the round being abandoned (fast browsing, 2026-07-31): if its
            // caption was identified but never spoken, adopt it as "where we were" —
            // otherwise that caption redraws into the NEW round and speaks one picture
            // LATE ("read Yosuke while standing on the Protagonist").
            if (!_artSpoken && _artSeen.Count > 0)
            {
                var (rec, score, _) = ArtVote(_artSeen.ToArray(), _artPrevRecord);
                if (rec >= 0 && score >= 1) _artPrevRecord = rec;
            }
            _artSeen.Clear();
            _artSeenSet.Clear();
            _artCurSb.Clear();
            _artCurObj = -1;
        }
        _artSpoken = false;
        _artKeyTick = _artLastNew = Environment.TickCount64;
    }

    /// <summary>The table vote: (bestRecord, score, latestDrawPos); prevRecord excluded.</summary>
    private (int rec, int score, int pos) ArtVote(string[] seen, int exclude)
    {
        int Pos(string s)
        {
            for (int i = seen.Length - 1; i >= 0; i--)
                if (seen[i] == s || seen[i].EndsWith(s) || s.EndsWith(seen[i])) return i;
            return -1;
        }
        int best = -1, bestScore = 0, bestPos = -1;
        for (int r = 0; r < _artCaps.Count; r++)
        {
            if (r == exclude) continue;
            int p1 = Pos(_artCaps[r].l1), p2 = _artCaps[r].l2.Length > 0 ? Pos(_artCaps[r].l2) : -1;
            int score = (p1 >= 0 ? 1 : 0) + (p2 >= 0 ? 1 : 0);
            int latest = Math.Max(p1, p2);
            if (score > bestScore || (score == bestScore && score > 0 && latest > bestPos))
            { best = r; bestScore = score; bestPos = latest; }
        }
        return (best, bestScore, bestPos);
    }

    /// <summary>OnText routing while the gallery is open (instead of AddCaptured).</summary>
    private void ArtCapture(nint strPtr, byte p2, nint p6)
    {
        if (p6 == 0) return;                       // fulls here = the legend labels only
        string g = ReadCStrSafe(strPtr, 8);
        lock (_capLock)
        {
            if (p6 != _artCurObj)
            {
                if (_artCurSb.Length > 0)
                {
                    string s = _artCurSb.ToString().Trim();
                    _artCurSb.Clear();
                    if (s.Length >= 2 && _artSeenSet.Add(s))
                    {
                        _artSeen.Add(s);
                        _artLastNew = Environment.TickCount64;
                    }
                }
                _artCurObj = p6;
            }
            _artCurSb.Append(g.Length > 0 ? g : " ");
        }
    }

    // The offline caption TABLE (artCh.arc CH_ART_DETAILNNN records → catalog
    // "art_captions", strip order): captured fragments only need to IDENTIFY the
    // record — the announcement is the table's FULL caption + position. Immune to
    // shared lines, partial captures, and the old caption's redraws.
    private readonly List<(string l1, string l2, string full)> _artCaps = new();
    private int _artPrevRecord = -1;

    private void ArtFlush()
    {
        long now = Environment.TickCount64;
        string[] seen;
        long lastNew;
        lock (_capLock) { seen = _artSeen.ToArray(); lastNew = _artLastNew; }

        if (_artSpoken) return;

        if (seen.Length == 0)
        {
            if (now - _artKeyTick > 1500) { _artSpoken = true; _artEntryRead = true; Speech.Say("No new caption.", true); }
            return;
        }

        var (best, bestScore, _) = ArtVote(seen, _artPrevRecord);

        // A FULL two-line match (with the old picture excluded) can only be the
        // destination — speak IMMEDIATELY, no settle wait (browsing-pace latency fix).
        // Weaker matches wait out the settle window for more lines to arrive.
        if (bestScore < 2 && now - lastNew < 220) return;

        if (best < 0)
        {
            // No table match (unknown line / no table loaded): raw fallback.
            var raw = new List<string>();
            foreach (var s in seen) if (!IsButtonLabel(s)) raw.Add(s);
            if (raw.Count == 0)
            {
                if (now - _artKeyTick > 1500) { _artSpoken = true; _artEntryRead = true; Speech.Say("No new caption.", true); }
                return;
            }
            _artSpoken = true; _artEntryRead = true;
            string composedRaw = string.Join(", ", raw);
            Log($"[TvList] art say (raw): {composedRaw}");
            Speech.Say(composedRaw + ".", true);
            return;
        }

        _artSpoken = true; _artEntryRead = true;
        _artPrevRecord = best;
        string composed = $"{_artCaps[best].full}. {best + 1} of {_artCaps.Count}";
        Log($"[TvList] art say: {composed}");
        Speech.Say(composed + ".", true);
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private char _pendingKind;
    private long _announceAt;

    /// <summary>Speak the captured frame once the capture window closed.</summary>
    private void FlushPending(int state, int cursor)
    {
        if (_pendingKind == 0 || Environment.TickCount64 < _announceAt) return;
        char kind = _pendingKind;
        _pendingKind = (char)0;
        lock (_capLock) FlushGlyphLine();

        (string s, byte p2, byte p3, uint p4, bool glyph, long t)[] cap;
        lock (_capLock) cap = _captured.ToArray();
        if (cap.Length == 0)
        {
            // The list's first frame draws only after the opening slide: an empty capture right after
            // "Episode list." left the first episode unread — capture again (2026-10-15).
            if (kind == 'a' && _animeRetries++ < 3)
            {
                StartCapture(250, 300);
                _pendingKind = 'a';
                _announceAt = Environment.TickCount64 + 600;
            }
            return;
        }

#if DEBUG
        foreach (var c in cap)
            Log($"[TvCap] {kind}{(c.glyph ? 'g' : ' ')} p2={c.p2} p4={c.p4:X8} \"{c.s}\"");
#endif

        string text;
        if (kind == 'a')
        {
            // anime: the slide anim draws OLD then NEW title (both p2==1) —
            // the LAST title-style full string is the current selection.
            // Background preview dialogue draws with other p2 values; the
            // description panel arrives as glyph lines.
            string? title = null;
            foreach (var c in cap)
                if (!c.glyph && c.p2 == 1) title = c.s;
            // The description is ONE paragraph wrapped over several glyph lines: join them with
            // spaces (a stop after every line read "An invitation to the. mysterious Velvet Room.").
            var desc = new StringBuilder();
            foreach (var c in cap)
            {
                if (!c.glyph || IsButtonLabel(c.s)) continue;
                desc.Append(desc.Length > 0 ? " " : "").Append(c.s.Trim());
            }
            string d = desc.ToString().Trim();
            if (d.Length > 0 && !EndsSentence(d)) d += ".";
            string t = (title ?? "").Trim();
            if (t.Length > 0 && d.Length > 0 && !EndsSentence(t)) t += ".";
            text = (t + " " + d).Trim();
            if (text.Length > 0 && cursor == _lastEpisode) _episodeText[cursor] = text;
        }
        else
        {
            return; // music = direct array read; the art gallery has its own ArtFlush
        }
        if (text.Length == 0) return;
        Log($"[TvList] {kind} state={state} cursor={cursor} say: {text}");
        Speech.Say(text, true);
    }

    private static bool EndsSentence(string s)
    {
        char last = s[s.Length - 1];
        return last == '.' || last == '!' || last == '?' || last == '。' || last == '！' || last == '？';
    }

    private static bool IsButtonLabel(string s)
    {
        switch (s)
        {
            case "Zoom": case "Back": case "Music": case "Movie":
            case "Back to movie": case "OK": case "Pic":
                return true;
        }
        return false;
    }

    private string ChannelName(int ch)
        => _channelNames.TryGetValue(ch, out var n) ? n : $"Channel {ch}";

    private static byte UnlockState(int id)
    {
        if (id < 0 || id > 0xFF) return 0;
        if (!IsReadable(SaveBlkPtrVA, 8)) return 0;
        nint blk = *(nint*)SaveBlkPtrVA;
        if (blk == 0 || !IsReadable(blk + 0x80 + id, 1)) return 0;
        return *(byte*)(blk + 0x80 + id);
    }

    private static string ReadTitleFromExe(int id)
    {
        nint p = TitleTableVA + id * 0x40;
        if (!IsReadable(p, 0x40)) return "";
        var sb = new StringBuilder(0x40);
        for (int i = 0; i < 0x40; i++)
        {
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }

    private nint FindTask() => FindTaskByName(TaskName);

    internal static nint FindTaskByName(byte[] name)
    {
        foreach (nint head in TaskHeads)
        {
            if (!IsReadable(head, 8)) continue;
            nint node = *(nint*)head;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!IsReadable(node, 0x58)) break;
                if (NameMatches(node, name)) return node;
                node = *(nint*)(node + 0x50);
            }
        }
        return 0;
    }

    private static bool NameMatches(nint node, byte[] name)
    {
        for (int i = 0; i < name.Length; i++)
            if (*(byte*)(node + i) != name[i]) return false;
        return *(byte*)(node + name.Length) == 0;
    }

    private void LoadCatalog()
    {
        try
        {
            string path = DataPath("tvlistings_catalog.json");
            if (path == null || !File.Exists(path)) { Log("[TvList] no catalog json"); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("channels", out var chans))
                foreach (var p in chans.EnumerateObject())
                    _channelNames[int.Parse(p.Name)] = p.Value.GetString() ?? "";
            if (root.TryGetProperty("programs", out var progs))
                foreach (var p in progs.EnumerateObject())
                {
                    int id = int.Parse(p.Name);
                    if (p.Value.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } ts)
                        _titles[id] = ts;
                    if (p.Value.TryGetProperty("desc", out var d) && d.GetString() is { Length: > 0 } ds)
                        _descriptions[id] = ds;
                }
            if (root.TryGetProperty("music_playlists", out var pls))
                foreach (var p in pls.EnumerateObject())
                {
                    var list = new List<string>();
                    foreach (var s in p.Value.EnumerateArray())
                        list.Add(s.GetString() ?? "");
                    _musicPlaylists[int.Parse(p.Name)] = list;
                }
            if (root.TryGetProperty("art_captions", out var arts))
                foreach (var a in arts.EnumerateArray())
                {
                    string full = a.GetString() ?? "";
                    int comma = full.IndexOf(", ", StringComparison.Ordinal);
                    _artCaps.Add(comma > 0
                        ? (full.Substring(0, comma), full.Substring(comma + 2), full)
                        : (full, "", full));
                }
            if (root.TryGetProperty("anime_episodes", out var eps))
                foreach (var e in eps.EnumerateArray())
                {
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        string?[] pair = { null, null };
                        int i = 0;
                        foreach (var x in e.EnumerateArray()) { if (i < 2) pair[i] = x.GetString(); i++; }
                        _animeEpisodes.Add(pair[0] ?? "");
                        _animeEpisodeDescs.Add(pair[1] ?? "");
                    }
                    else
                    {
                        _animeEpisodes.Add(e.GetString() ?? "");
                        _animeEpisodeDescs.Add("");
                    }
                }
        }
        catch (Exception ex) { Log($"[TvList] catalog load failed: {ex.Message}"); }
    }

    // The glyph decoder returns the game's IDEOGRAPHIC space (U+3000) between words since v2.1.0: the art
    // vote compared "Yosuke<U+3000>Hanamura" against the caption table and never matched (every read fell
    // back to the raw text and stuck on one caption, found 2026-10-15) — normalize at the capture.
    /// <summary>ASCII c-string read with full page validation (hook-safe).</summary>
    private static string ReadCStrSafe(nint p, int maxLen) => ReadCStringRpm(p, maxLen).Replace('　', ' ');  // RPM since 2026-07-27 (menu-heaviness fix: VirtualQuery stalls under allocator contention)

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
        => Utils.ProbeReadable(addr, size);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable
}
