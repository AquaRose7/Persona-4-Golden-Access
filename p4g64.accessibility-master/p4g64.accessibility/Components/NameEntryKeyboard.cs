using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the name-entry keyboard render functions to speak whichever key the cursor is on.
///
/// Struct layout (both renderers):
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
/// Two renderers share the signature "48 89 5C 24 18 48 89 6C 24 20 56 57 41 56 48 83 EC 50
/// 48 8B D9": FUN_1403EF020 (4 per-language cursor tables) and FUN_1403FA570 (2 tables). The
/// sig-scan only ever bound the FIRST, which is what English uses; non-English players got
/// silence. Both are hooked now (ASLR is off; the addresses are constant) and the log names
/// which one fires.
///
/// Space/OK/Delete are bottom BUTTONS (controller-bound), not grid cells.
/// </summary>
internal unsafe class NameEntryKeyboard : IDisposable
{
    private static readonly nint[] RenderVAs = { unchecked((nint)0x1403EF020L), unchecked((nint)0x1403FA570L) };
    private const string RenderSig = "48 89 5C 24 18 48 89 6C 24 20 56 57 41 56 48 83 EC 50 48 8B D9";

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

    private readonly List<IHook<RenderDelegate>> _hooks = new();
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
        // Active name field: 1 = Last name (top row), 2 = First name (bottom).
        // Found via live diagnostic — flips exactly when the name-cell cursor
        // (Q/E) crosses between the two rows; unchanged on within-row moves.
        [FieldOffset(0x3100)] public int Field;
    }

    private delegate void RenderDelegate(KbdOuter* pOuter, nint p2, nint p3, nint p4);

    internal NameEntryKeyboard(IReloadedHooks hooks)
    {
        // Sanity: the first bytes of each constant address must still match the signature
        // (guards against a different game build). Hook every renderer that does.
        var sigBytes = RenderSig.Split(' ').Select(h => Convert.ToByte(h, 16)).ToArray();
        byte* probe = stackalloc byte[sigBytes.Length];
        for (int i = 0; i < RenderVAs.Length; i++)
        {
            nint va = RenderVAs[i];
            if (!TryReadRaw(va, probe, sigBytes.Length)) { Log($"[NameEntry] renderer {i} @0x{va:X} unreadable — skipped"); continue; }
            bool ok = true;
            for (int b = 0; b < sigBytes.Length; b++) if (probe[b] != sigBytes[b]) { ok = false; break; }
            if (!ok) { Log($"[NameEntry] renderer {i} @0x{va:X} does not match the signature — skipped"); continue; }
            int which = i;
            var h = hooks.CreateHook<RenderDelegate>((o, a, b, c) => OnRender(which, o, a, b, c), va).Activate();
            _hooks.Add(h);
        }
        Log($"Name entry keyboard render hook active ({_hooks.Count} renderer(s)).");
    }

    private void OnRender(int which, KbdOuter* pOuter, nint p2, nint p3, nint p4)
    {
        _hooks[which].OriginalFunction(pOuter, p2, p3, p4);
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

        // Speak the instruction once when the screen opens. The field value
        // isn't valid yet at this point, so the prompt states the order
        // (last name first) instead of reading the live field.
        if (!_introDone)
        {
            _introDone = true;
            Speech.Say("Enter your name. Last name first. Press X when finished.", true);
        }

        AnnounceField(inner); // "Last name" / "First name" when the row changes

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

    private int  _lastField = -1;
    private bool _introDone;

    private static string FieldName(int f) => f == 2 ? "Last name" : "First name";

    // Announce the field name whenever the active field changes (the field at
    // +0x3100 flips 1<->2 as the cell cursor crosses between the two rows).
    private void AnnounceField(KbdInner* inner)
    {
        if (!IsReadable((long)inner + 0x3100)) return;
        int f = inner->Field;
        if (f != 1 && f != 2) return;
        if (f == _lastField) return;
        _lastField = f;
        Speech.Say(FieldName(f), true);
    }

    private static bool IsReadable(long addr)
        => Utils.ProbeReadable((nint)addr, 8);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable

    public void Dispose()
    {
        foreach (var h in _hooks) h.Disable();
    }
}
