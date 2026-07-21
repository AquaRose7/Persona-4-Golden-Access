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
    private static readonly byte[] AnimeTaskName = Encoding.ASCII.GetBytes("ANIME_CHANNEL");

    private bool _active;
    private int _lastId = -1, _lastCh = -1;
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
    private readonly List<(string s, byte p2, byte p3, uint p4, bool glyph)> _captured = new();

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
        try
        {
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
                    if (p6 != _glyphObj) { FlushGlyphLine(); _glyphObj = p6; _glyphP2 = p2; }
                    string g = ReadCStrSafe(p1, 8);
                    _glyphLine.Append(g.Length > 0 ? g : " ");
                    if (_glyphLine.Length > 200) FlushGlyphLine();
                }
            }
        }
        catch { /* never throw from a hook */ }
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
            foreach (var c in _captured)
                if (c.s == s) return;                // dedupe across frames
            _captured.Add((s, p2, p3, p4, glyph));
        }
    }

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
            Thread.Sleep(100);
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

        if (id == _lastId && ch == _lastCh) return;
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
            if (_animeActive) { _animeActive = false; _lastEpisode = -1; }
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
            _announceAt = Environment.TickCount64 + 680;
            return true;
        }
        if (cursor != _lastEpisode)
        {
            _lastEpisode = cursor;
            StartCapture(400, 200);   // wait out the carousel slide + panel repaint
            _pendingKind = 'a';
            _announceAt = Environment.TickCount64 + 680;
        }
        FlushPending(astate, cursor);
        return true;
    }

    private char _pendingKind;
    private long _announceAt;

    /// <summary>Speak the captured frame once the capture window closed.</summary>
    private void FlushPending(int state, int cursor)
    {
        if (_pendingKind == 0 || Environment.TickCount64 < _announceAt) return;
        char kind = _pendingKind;
        _pendingKind = (char)0;
        lock (_capLock) FlushGlyphLine();

        (string s, byte p2, byte p3, uint p4, bool glyph)[] cap;
        lock (_capLock) cap = _captured.ToArray();
        if (cap.Length == 0) return;

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
            var sb = new StringBuilder(title ?? "");
            foreach (var c in cap)
            {
                if (!c.glyph) continue;
                if (IsButtonLabel(c.s)) continue;
                sb.Append(sb.Length > 0 ? " " : "").Append(c.s);
                char last = c.s[c.s.Length - 1];
                if (last != '.' && last != '!' && last != '?') sb.Append('.');
            }
            text = sb.ToString().Trim();
        }
        else
        {
            return; // music no longer uses capture (direct array read)
        }
        if (text.Length == 0) return;
        Log($"[TvList] {kind} state={state} cursor={cursor} say: {text}");
        Speech.Say(text, true);
    }

    private static bool IsButtonLabel(string s)
    {
        switch (s)
        {
            case "Zoom": case "Back": case "Music": case "Movie":
            case "Back to movie": case "OK":
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

    private static nint FindTaskByName(byte[] name)
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

    /// <summary>ASCII c-string read with full page validation (hook-safe).</summary>
    private static string ReadCStrSafe(nint p, int maxLen)
    {
        if (p == 0 || !IsReadable(p, 1)) return "";
        var sb = new StringBuilder(maxLen);
        for (int i = 0; i < maxLen; i++)
        {
            if ((i & 0xF) == 0 && !IsReadable(p + i, 16)) break;
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString();
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
