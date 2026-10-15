using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the name-entry keyboard render functions to speak whichever key the cursor is on.
///
/// Struct layout (all three renderers):
///   1st param (rcx) = outer struct; *(outer + 0x48) = inner struct
///   inner + 0x20 = column (int, 0–19)
///   inner + 0x24 = row    (int, 0–6)
///
/// 2026-09-07 — LANGUAGE-NEUTRAL: the key under the cursor is read from the GAME's own
/// keyboard grid (<c>m_name_keychar.ctd</c> of the language's <c>dict/nEntry.arc</c>, 20 columns
/// × up to 7 rows of 2-byte glyph codes — see <see cref="Native.Text.GameText.KeyboardChar"/>).
/// Japanese gets kana, French gets its accented letters, English is unchanged. The old
/// hard-coded English 20×6 map (screenshot-verified 2026-06-25) stays as the FALLBACK when the
/// grid can't be read.
///
/// The game has THREE name screens, each with its own per-frame cursor function taking the
/// M_Name_KeyProc task node (work = node+0x48, col +0x20, row +0x24 in all three):
///   0  FUN_1403EF020  task nameEntry       Japanese / English / Korean / Traditional Chinese
///   1  FUN_1403FA570  task nameEntry_ck    Simplified Chinese (+ a second layout)
///   2  FUN_140400F40  task nameEntry_figs  French / German / Italian / Spanish (2026-10-01 —
///      a different prologue, so the shared signature never matched it: silent in FIGS)
/// Each is hooked at its constant address (ASLR is off) after a prologue check; the log names
/// which one fires.
///
/// Space/OK/Delete are bottom BUTTONS (controller-bound), not grid cells.
/// </summary>
internal unsafe class NameEntryKeyboard : IDisposable
{
    private static readonly (nint Va, string Sig)[] Renderers =
    {
        (unchecked((nint)0x1403EF020L), "48 89 5C 24 18 48 89 6C 24 20 56 57 41 56 48 83 EC 50 48 8B D9"),
        (unchecked((nint)0x1403FA570L), "48 89 5C 24 18 48 89 6C 24 20 56 57 41 56 48 83 EC 50 48 8B D9"),
        (unchecked((nint)0x140400F40L), "48 89 5C 24 18 55 56 57 48 83 EC 50 48 8B D9"),
    };
    private const int FigsRenderer = 2;

    // The European screen's work is only 0x2218 bytes (the +0x3100 field below is outside it); its
    // name cursor is a static instead: 0..7 = the top line (last name), 8+ = the bottom line (first
    // name). Live-verified in Spanish 2026-10-01 ("Escribe tu apellido" / "Escribe tu nombre").
    private const long FigsNameCursorVA = 0x1451D28E0L;

    // 7 rows × 20 cols.  null = empty / no key.  (English fallback only.)
    private static readonly string?[,] CharMap =
    {
        // row 0
        {
            "A","B","C","D","E",                                 // cols  0– 4 uppercase
            "a","b","c","d","e",                                 // cols  5– 9 lowercase
            "0","1","2","3","4",                                 // cols 10–14 digits
            "plus","minus","times","divide","equals"             // cols 15–19 symbols
        },
        // row 1
        {
            "F","G","H","I","J",
            "f","g","h","i","j",
            "5","6","7","8","9",
            "period","comma","open parenthesis","close parenthesis","question mark"
        },
        // row 2
        {
            "K","L","M","N","O",
            "k","l","m","n","o",
            null,null,null,null,null,                            // digits panel: nothing below 0–9
            "exclamation mark","hash","dollar","percent","ampersand"
        },
        // row 3
        {
            "P","Q","R","S","T",
            "p","q","r","s","t",
            null,null,null,null,null,
            "asterisk","slash","quote","comma","dot"
        },
        // row 4
        {
            "U","V","W","X","Y",
            "u","v","w","x","y",
            null,null,null,null,null,
            "colon","semicolon",null,null,null
        },
        // row 5
        {
            "Z",null,null,null,null,
            "z",null,null,null,null,
            null,null,null,null,null,
            null,null,null,null,null
        },
        // row 6 (no grid keys)
        {
            null,null,null,null,null,
            null,null,null,null,null,
            null,null,null,null,null,
            null,null,null,null,null
        },
    };

    // Symbols spoken as words (after fullwidth → ASCII normalization).
    private static readonly Dictionary<string, string> SymbolWords = new()
    {
        ["+"] = "plus", ["-"] = "minus", ["−"] = "minus", ["×"] = "times", ["÷"] = "divide", ["="] = "equals",
        ["."] = "period", [","] = "comma", ["("] = "open parenthesis", [")"] = "close parenthesis",
        ["?"] = "question mark", ["!"] = "exclamation mark", ["#"] = "hash", ["$"] = "dollar",
        ["%"] = "percent", ["&"] = "ampersand", ["*"] = "asterisk", ["/"] = "slash",
        ["\""] = "quote", ["”"] = "quote", ["'"] = "apostrophe", ["’"] = "apostrophe",
        ["·"] = "dot", ["・"] = "dot", [":"] = "colon", [";"] = "semicolon",
        ["ー"] = "long vowel mark", ["々"] = "repeat mark", ["、"] = "comma", ["。"] = "period",
    };

    private readonly IHook<RenderDelegate>?[] _hooks = new IHook<RenderDelegate>?[Renderers.Length];
    private int _lastCol = -1;
    private int _lastRow = -1;
    private bool _sourceLogged;

    [StructLayout(LayoutKind.Explicit)]
    private struct KbdOuter
    {
        [FieldOffset(0x48)] public KbdInner* Inner;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct KbdInner
    {
        [FieldOffset(0x20)]   public int Col;
        [FieldOffset(0x24)]   public int Row;
        // Active name field: 2 = Last name (top row), 1 = First name (bottom) — see FieldName.
        // Found via live diagnostic — flips exactly when the name-cell cursor
        // (Q/E) crosses between the two rows; unchanged on within-row moves.
        // Japanese/English screen only: the European work is smaller (AnnounceField).
        [FieldOffset(0x3100)] public int Field;
    }

    private delegate void RenderDelegate(KbdOuter* pOuter, nint p2, nint p3, nint p4);

    internal NameEntryKeyboard(IReloadedHooks hooks)
    {
        // Sanity: the first bytes of each constant address must still match its signature
        // (guards against a different game build). Hook every renderer that does.
        int hooked = 0;
        byte* probe = stackalloc byte[32];
        for (int i = 0; i < Renderers.Length; i++)
        {
            var (va, sig) = Renderers[i];
            var sigBytes = sig.Split(' ').Select(h => Convert.ToByte(h, 16)).ToArray();
            if (!TryReadRaw(va, probe, sigBytes.Length)) { Log($"[NameEntry] renderer {i} @0x{va:X} unreadable — skipped"); continue; }
            bool ok = true;
            for (int b = 0; b < sigBytes.Length; b++) if (probe[b] != sigBytes[b]) { ok = false; break; }
            if (!ok) { Log($"[NameEntry] renderer {i} @0x{va:X} does not match the signature — skipped"); continue; }
            int which = i;
            _hooks[i] = hooks.CreateHook<RenderDelegate>((o, a, b, c) => OnRender(which, o, a, b, c), va).Activate();
            hooked++;
        }
        Log($"Name entry keyboard render hook active ({hooked} renderer(s)).");
        StartNameReader(hooks);
    }

    private void OnRender(int which, KbdOuter* pOuter, nint p2, nint p3, nint p4)
    {
        _hooks[which]!.OriginalFunction(pOuter, p2, p3, p4);
        try { Handle(which, pOuter); } catch (Exception e) { Log($"[NameEntry] {e.Message}"); }
    }

    private void Handle(int which, KbdOuter* pOuter)
    {
        if (pOuter == null) return;
        var inner = pOuter->Inner;
        if (inner == null || !Utils.ProbeReadable((nint)inner, 0x28)) return;
        var col = inner->Col;
        var row = inner->Row;
        if (col < 0 || col > 19 || row < 0 || row > 6) return;

        // The prompt, the typed name and the review keys run on the poll thread (below); the render
        // only marks the screen alive and hands over which screen it is.
        _renderer = which;
        if (which != FigsRenderer && IsReadable((long)inner + 0x3100)) _fieldFromInner = inner->Field;
        Interlocked.Exchange(ref _lastRenderTick, Environment.TickCount64);

        if (col == _lastCol && row == _lastRow) return;
        _lastCol = col;
        _lastRow = row;

        bool fromGame = Native.Text.GameText.KeyboardGridLoaded();
        string? ch = fromGame
            ? Native.Text.GameText.KeyboardChar(row, col)
            : CharMap[row, col];   // grid not readable → English fallback (only meaningful in English)
        if (!_sourceLogged)
        {
            _sourceLogged = true;
            Log($"[NameEntry] renderer {which}, language {Native.Text.GameText.LanguageId}, key source = {(fromGame ? "game grid" : "English fallback")}");
        }
        if (ch == null) return;

        string spoken = Spoken(ch);
        LogDebug($"Keyboard: row={row} col={col} -> {spoken}");
        Speech.Say(spoken, true);
    }

    /// <summary>
    /// How a key is spoken: uppercase LETTERS get a "cap" prefix (so they're distinguishable by
    /// ear from their lowercase twins), symbols become words, everything else (digits, kana,
    /// accented letters, CJK) is spoken as-is.
    /// </summary>
    private static string Spoken(string ch)
    {
        if (SymbolWords.TryGetValue(ch, out var word)) return word;
        if (ch.Length == 1 && char.IsLetter(ch[0]) && char.IsUpper(ch[0])) return $"cap {ch}";
        return ch;
    }

    // ── The prompt, the typed name, and the I/K/J/L review (Haru 2026-10-15) ──────────────────────
    // The typed name lives in STATICS on the Japanese/English screen (live-found 2026-10-15): last name =
    // 8 Shift-JIS cells at 0x1451D2310, first name = 8 cells at 0x1451D2238 (fullwidth space 81 40 = an
    // empty slot), name cursor = int at 0x1451D228C (0..7 last name, 8..15 first name). Delete first
    // steps back onto a letter, then erases it — the reader just compares the cells. The European screen
    // has its own cursor static (FigsNameCursorVA); its name cells (and the Chinese screen's) are not
    // mapped yet, so there the prompt and rows are read but the echo stays off.
    // The PROMPT is the game's own text in the game's language: the screen draws both lines ("Enter your
    // last name", then "Enter your first name") through the shared text renderer every frame — captured
    // in that order while the screen is up; the cursor's row picks which one is meant.
    private const long LastNameVA = 0x1451D2310L, FirstNameVA = 0x1451D2238L, NameCursorVA = 0x1451D228CL;
    private static readonly string[] FallbackPrompts = { "Enter your last name", "Enter your first name" };
    private static readonly string[] RowNames = { "Last name", "First name" };

    private volatile int _renderer = -1;
    private volatile int _fieldFromInner;            // Chinese screen: inner+0x3100, 2 = last name, 1 = first name
    private long _lastRenderTick;
    private volatile bool _screenActive;
    private readonly object _promptLock = new();
    private readonly List<string> _prompts = new();

    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);
    private IHook<SetTextDelegate>? _textHook;

    private void StartNameReader(IReloadedHooks hooks)
    {
        _textHook = hooks.CreateHook<SetTextDelegate>(OnText, SetUiTextVA).Activate();
        new Thread(Poll) { IsBackground = true, Name = "NameEntryName" }.Start();
    }

    private nint OnText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _textHook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        if (!_screenActive || p6 != 0) return ret;          // cheap gate: only while the name screen is up
        try
        {
            lock (_promptLock)
            {
                if (_prompts.Count >= 2) return ret;
                string s = ReadCStringRpm(p1, 96).Replace('　', ' ').Trim();
                if (s.Length >= 4 && !_prompts.Contains(s)) _prompts.Add(s);
            }
        }
        catch { }
        return ret;
    }

    private string Prompt(int row)
    {
        lock (_promptLock)
            return _prompts.Count >= 2 ? _prompts[row] : FallbackPrompts[row];
    }

    private int ReadCursor(int renderer)
    {
        int c;
        long va = renderer == 0 ? NameCursorVA : renderer == FigsRenderer ? FigsNameCursorVA : 0;
        if (va == 0 || !TryReadRaw((nint)va, &c, 4) || c < 0 || c > 16) return -1;
        return c;
    }

    // 0 = last name, 1 = first name, -1 = unknown.
    private int ReadRow(int renderer, int cursor)
    {
        if (cursor >= 0) return cursor < 8 ? 0 : 1;
        int f = _fieldFromInner;
        return f == 2 ? 0 : f == 1 ? 1 : -1;
    }

    private static string[]? ReadName(int renderer, long va)
    {
        if (renderer != 0) return null;
        byte* b = stackalloc byte[16];
        if (!TryReadRaw((nint)va, b, 16)) return null;
        var cells = new string[8];
        for (int i = 0; i < 8; i++) cells[i] = Native.Text.GameText.NameCellChar(b[i * 2], b[i * 2 + 1]);
        return cells;
    }

    private static string Joined(string[] cells)
        => string.Concat(cells.Select(c => c.Length > 0 ? c : " ")).Trim();

    // Speak a change in one row: the typed letter, then the name so far ("a, Ha"); a deletion names
    // what went and what is left. Returns true when something was spoken.
    private static bool Echo(string[] before, string[] now)
    {
        for (int i = 0; i < 8; i++)
        {
            if (before[i] == now[i]) continue;
            string name = Joined(now);
            string say;
            if (now[i].Length > 0)
                say = name == now[i] ? Spoken(now[i]) : $"{Spoken(now[i])}, {name}";
            else
                say = name.Length > 0 ? $"{Spoken(before[i])} deleted, {name}" : $"{Spoken(before[i])} deleted, empty";
            Speech.Say(say, true);
            return true;
        }
        return false;
    }

    private const int VK_I = 0x49, VK_J = 0x4A, VK_K = 0x4B, VK_L = 0x4C;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Poll()
    {
        bool active = false, introDone = false;
        long since = 0;
        string[]? prevLast = null, prevFirst = null;
        int prevCursor = -1, prevRow = -1;
        int reviewRow = -1, reviewPos = -1;
        bool iWas = false, jWas = false, kWas = false, lWas = false;
        while (true)
        {
            Thread.Sleep(50);
            try
            {
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastRenderTick) > 400)
                {
                    if (active) { active = false; _screenActive = false; }
                    continue;
                }
                int renderer = _renderer;
                if (!active)
                {
                    active = true; introDone = false; since = now;
                    lock (_promptLock) _prompts.Clear();
                    _screenActive = true;
                    prevLast = prevFirst = null; prevCursor = -1; prevRow = -1; reviewRow = -1; reviewPos = -1;
                    iWas = KeyDown(VK_I); jWas = KeyDown(VK_J); kWas = KeyDown(VK_K); lWas = KeyDown(VK_L);   // ghost-key guard
                }

                int cursor = ReadCursor(renderer);
                int row = ReadRow(renderer, cursor);
                var last = ReadName(renderer, LastNameVA);
                var first = ReadName(renderer, FirstNameVA);

                if (!introDone)
                {
                    if (now - since < 400) continue;   // let the prompts draw once
                    introDone = true;
                    Speech.Say($"{Prompt(row < 0 ? 0 : row)}. Press X when finished. I and K read the names, J and L the letters.", false);
                    Log($"[NameEntry] prompts: {string.Join(" / ", new[] { Prompt(0), Prompt(1) })}; name cells {(last != null ? "read" : "not mapped on this screen")}");
                    prevLast = last; prevFirst = first; prevCursor = cursor; prevRow = row;
                    continue;
                }

                bool spoke = false;
                if (last != null && prevLast != null) spoke = Echo(prevLast, last);
                if (!spoke && first != null && prevFirst != null) spoke = Echo(prevFirst, first);
                if (row >= 0 && row != prevRow)
                    Speech.Say(Prompt(row), !spoke);                 // after a typed 8th letter: queued
                else if (!spoke && cursor >= 0 && cursor != prevCursor && last != null && first != null && cursor < 16)
                {
                    var cells = cursor < 8 ? last : first;
                    string ch = cells[cursor % 8];
                    Speech.Say(ch.Length > 0 ? Spoken(ch) : "blank", true);   // Q / E moved the name cursor
                }
                prevLast = last; prevFirst = first; prevCursor = cursor; prevRow = row;

                // I / K: read the last / first name. J / L: step through the letters of that name.
                if (!GameHasFocus()) continue;
                bool i = KeyDown(VK_I), j = KeyDown(VK_J), k = KeyDown(VK_K), l = KeyDown(VK_L);
                bool iE = i && !iWas, jE = j && !jWas, kE = k && !kWas, lE = l && !lWas;
                iWas = i; jWas = j; kWas = k; lWas = l;
                if (iE || kE)
                {
                    reviewRow = iE ? 0 : 1;
                    reviewPos = -1;
                    var cells = reviewRow == 0 ? last : first;
                    string name = cells != null ? Joined(cells) : "";
                    Speech.Say(cells == null ? RowNames[reviewRow] : $"{RowNames[reviewRow]}, {(name.Length > 0 ? name : "empty")}", true);
                }
                else if (jE || lE)
                {
                    if (reviewRow < 0) reviewRow = row < 0 ? 0 : row;
                    var cells = reviewRow == 0 ? last : first;
                    if (cells == null) { Speech.Say(RowNames[reviewRow], true); continue; }
                    reviewPos = Math.Clamp(reviewPos < 0 ? 0 : reviewPos + (lE ? 1 : -1), 0, 7);
                    if (jE && reviewPos == 0 && cells[0].Length == 0 && Joined(cells).Length == 0) { Speech.Say("empty", true); continue; }
                    string ch = cells[reviewPos];
                    Speech.Say(ch.Length > 0 ? Spoken(ch) : "blank", true);
                }
            }
            catch (Exception ex) { Log($"[NameEntry] {ex.Message}"); }
        }
    }

    private static bool IsReadable(long addr)
        => Utils.ProbeReadable((nint)addr, 8);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable

    public void Dispose()
    {
        foreach (var h in _hooks) h?.Disable();
    }
}
