using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native.Text;

/// <summary>
/// Language-neutral access to the game's OWN localized text tables (2026-09-07).
///
/// The game keeps ONE table per language for skill / persona / enemy names, selected by the
/// language id global 0x140FD01E4 (set once at startup from the Steam language string):
///   0 japanese · 1 english · 2 koreana · 3 tchinese · 4 schinese ·
///   5 french · 6 german · 7 italian · 8 spanish
/// Each table is a BSS pointer cell → array of NUL-terminated strings in the Atlus glyph
/// encoding (English is plain ASCII; every other language has 2-byte glyph pairs), with a
/// per-language stride. Strides/cells replicate the game's own accessors
/// (FUN_1400D59B0 skills, FUN_1400D6170 personas, FUN_1400CC0E0 enemies). The old readers
/// sig-scanned the ENGLISH branch only → null table → NullReferenceException inside a
/// game-thread hook the moment a non-English player opened the Skill menu.
///
/// Also here: the config-menu label table (sub-file "cmpConfigItem.ctd" of the loaded
/// init_free pack at *(0x1411658C0)) and the name-entry keyboard grid
/// ("m_name_keychar.ctd" of dict/nEntry.arc, row pointers at 0x1451D2330 / 0x1451D22D0).
/// Every read is RPM-guarded; nothing here ever throws to a caller.
/// </summary>
internal static unsafe class GameText
{
    private const long LanguageIdVA = 0x140FD01E4L;

    private static int _loggedLang = int.MinValue;

    /// <summary>The game's language id (see class doc). -1 if unreadable.</summary>
    internal static int LanguageId
    {
        get
        {
            int v;
            if (!TryReadRaw((nint)LanguageIdVA, &v, 4)) return -1;
            if (v < 0 || v > 8) return -1;
            if (v != _loggedLang)
            {
                _loggedLang = v;
                string expect = TableForLanguageId(v);
                // 0 is also the BSS default before the game's own init has run (an English
                // install logged a false "mismatch" at startup, 2026-09-09) — only judge a
                // non-zero id; a real Japanese install simply logs "japanese" here.
                string note = expect == GameLanguage.ActiveTable
                    ? "matches the mod's glyph table"
                    : v == 0 ? "(startup default — will re-read)"
                    : $"⚠ MISMATCH — mod decodes with {GameLanguage.ActiveTable}, the game runs {expect} (set F1 → Readers → Game text language)";
                Log($"[Language] game language id = {v} ({LanguageName(v)}); {note}");
            }
            return v;
        }
    }

    internal static string LanguageName(int id) => id switch
    {
        0 => "japanese", 1 => "english", 2 => "koreana", 3 => "tchinese", 4 => "schinese",
        5 => "french", 6 => "german", 7 => "italian", 8 => "spanish", _ => "unknown",
    };

    /// <summary>Glyph table the mod should decode with for a game language id.</summary>
    internal static string TableForLanguageId(int id) => id switch
    {
        0 => "P4G_JP.tsv",
        2 => "P4G_Korean.tsv",
        3 => "P4G_CHT.tsv",
        4 => "P4G_CHS.tsv",
        _ => "P4G_EFIGS.tsv",
    };

    // ── Name tables ───────────────────────────────────────────────────────────

    private readonly record struct NameTable(long Cell, int Stride);

    /// <summary>Skill names — FUN_1400D59B0.</summary>
    private static NameTable SkillTable(int lang) => lang switch
    {
        0 => new(0x15E4390D8, 0x13),
        1 => new(0x15E4390C8, 0x17),
        2 or 3 or 4 => new(0x15E439088, 0x15),
        _ => new(0x15E439080, 0x21),
    };

    /// <summary>Persona names — FUN_1400D6170.</summary>
    private static NameTable PersonaTable(int lang) => lang switch
    {
        0 => new(0x15E439078, 0x13),
        1 => new(0x15E4390A8, 0x15),
        2 or 3 or 4 => new(0x15E4390B0, 0x13),
        _ => new(0x15E4390A0, 0x15),
    };

    /// <summary>Enemy (shadow) names — the unit+0xA2==1 arm of FUN_1400CC0E0.</summary>
    private static NameTable EnemyTable(int lang) => lang switch
    {
        0 => new(0x15E4390E8, 0x13),
        1 => new(0x15E4390E0, 0x15),
        2 or 3 or 4 => new(0x15E4390F0, 0x13),
        _ => new(0x15E439070, 0x1D),
    };

    internal static string SkillName(int id) => ReadEntry(SkillTable(LanguageId), id, 0x800);
    internal static string PersonaName(int id) => ReadEntry(PersonaTable(LanguageId), id, 0x400);
    internal static string EnemyName(int id) => ReadEntry(EnemyTable(LanguageId), id, 0x800);

    private static string ReadEntry(NameTable t, int id, int maxId)
    {
        if (id < 0 || id >= maxId) return "";
        nint basePtr;
        if (!TryReadRaw((nint)t.Cell, &basePtr, 8) || basePtr == 0) return "";
        return ReadAtlusStringRpm(basePtr + id * t.Stride, t.Stride);
    }

    // ── Raw Atlus MSG text (function codes + glyph pairs) ─────────────────────

    /// <summary>
    /// Decode a raw Atlus MSG byte run (2026-09-09): bytes &lt; 0x80 are ASCII (0x0A = line
    /// break → space), 0x80..0xEF begin a 2-byte GLYPH pair (decoded with the active glyph
    /// table), and 0xF0..0xFF begin a FUNCTION token whose length is
    /// 2 + ((b0 &amp; 0x0F) − 1) × 2 bytes (AtlusScriptCompiler v1 layout: "F2 05 FF FF" = a
    /// 2-arg function, "F1 41" = none). 0x00 ends the run once text has started (function
    /// arguments may contain 0x00 before it). The old readers skipped EVERY high byte as a
    /// control code, which erased all non-English text.
    /// </summary>
    internal static string DecodeMsg(nint p, int maxLen = 1024)
    {
        if (p == 0 || maxLen <= 0) return "";
        if (maxLen > 4096) maxLen = 4096;
        var buf = new byte[maxLen];
        int total = 0;
        fixed (byte* pb = buf)
        {
            while (total < maxLen)
            {
                int chunk = Math.Min(maxLen - total, 0x1000 - (int)((ulong)(p + total) & 0xFFF));
                if (!TryReadRaw(p + total, pb + total, chunk)) break;
                total += chunk;
            }
        }
        return DecodeMsg(buf, total);
    }

    internal static string DecodeMsg(byte[] b, int len)
    {
        var sb = new StringBuilder(Math.Min(len, 256));
        var enc = AtlusEncoding.P4;
        var pair = new byte[2];
        bool started = false;
        int i = 0;
        while (i < len)
        {
            byte c = b[i];
            if (c == 0) { if (started) break; i++; continue; }
            if (c >= 0xF0)
            {
                int args = ((c & 0x0F) - 1) * 2;
                i += 2 + Math.Max(0, args);
                continue;
            }
            if (c >= 0x80)
            {
                if (i + 1 >= len) break;
                pair[0] = c; pair[1] = b[i + 1];
                if (enc != null)
                {
                    try { sb.Append(enc.GetString(pair)); started = true; } catch { }
                }
                i += 2;
                continue;
            }
            if (c == 0x0A) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); i++; continue; }
            if (c >= 0x20) { sb.Append((char)c); started = true; i++; continue; }
            i++;   // other control byte
        }
        return sb.ToString().Replace('\0', ' ').Trim();
    }

    // ── Battle command / bubble names ─────────────────────────────────────────

    /// <summary>
    /// Per-language pointer table of the battle COMMAND names the game draws in the top
    /// bubble (static .sdata, 2026-09-09): <c>*(0x14090B310 + lang*8)</c> → char*[] with
    /// 0 Attack · 1 Skill · 2 Item · 3 Tactics · 4 Change Personas · 5 Escape · 6 Guard ·
    /// 7/8 Stand By · 9 Summon · 10/11 Analyze (EN "Attack" @0x140918C48, FR "Attaque"
    /// @0x140918E48). Lets MessageBubble recognise its own echo in any language.
    /// </summary>
    private const long BattleCommandTablesVA = 0x14090B310L;
    internal const int BattleCommandCount = 12;
    internal const int BattleCommandChangePersonas = 4;

    private static int _cmdLang = int.MinValue;
    private static string[]? _cmdNames;

    /// <summary>Localized battle command names (index per the table above); empty on failure.</summary>
    internal static string[] BattleCommandNames()
    {
        int lang = LanguageId;
        if (lang < 0) return Array.Empty<string>();
        var cached = _cmdNames;
        if (cached != null && lang == _cmdLang) return cached;
        nint tbl;
        if (!TryReadRaw((nint)(BattleCommandTablesVA + lang * 8), &tbl, 8) || tbl == 0) return Array.Empty<string>();
        var names = new string[BattleCommandCount];
        for (int i = 0; i < BattleCommandCount; i++)
        {
            nint p;
            names[i] = TryReadRaw(tbl + i * 8, &p, 8) && p != 0 ? ReadAtlusStringRpm(p, 48).Trim() : "";
        }
        _cmdNames = names; _cmdLang = lang;
        Log($"[Language] battle command names: {string.Join(" / ", names)}");
        return names;
    }

    /// <summary>Index of a drawn bubble/command string in the localized table, or -1.</summary>
    internal static int BattleCommandIndex(string text)
    {
        var names = BattleCommandNames();
        for (int i = 0; i < names.Length; i++)
            if (names[i].Length > 0 && string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── Shuffle Time info-panel category footers ─────────────────────────────

    /// <summary>
    /// The six category footers the Shuffle Time info panel draws, per language (static
    /// .sdata pointer array at 0x14090BD50, 6 pointers per language): 0 PERSONA · 1 SWORD ·
    /// 2 COIN · 3 WAND · 4 CUP · 5 ARCANA. JP/KR/CH keep the English words; FR/DE/IT/ES are
    /// localized ("Épées", "Schwert", "ARKANA", …). Found 2026-09-09.
    /// </summary>
    private const long ShuffleCategoryTablesVA = 0x14090BD50L;
    internal const int ShuffleCategoryCount = 6;
    private static int _sflLang = int.MinValue;
    private static string[]? _sflNames;

    internal static string[] ShuffleCategoryNames()
    {
        int lang = LanguageId;
        if (lang < 0) return Array.Empty<string>();
        var cached = _sflNames;
        if (cached != null && lang == _sflLang) return cached;
        var names = new string[ShuffleCategoryCount];
        nint tbl = (nint)(ShuffleCategoryTablesVA + lang * 0x30);
        for (int i = 0; i < ShuffleCategoryCount; i++)
        {
            nint p;
            names[i] = TryReadRaw(tbl + i * 8, &p, 8) && p != 0 ? ReadAtlusStringRpm(p, 32).Trim() : "";
        }
        _sflNames = names; _sflLang = lang;
        Log($"[Language] shuffle category footers: {string.Join(" / ", names)}");
        return names;
    }

    /// <summary>Index (0..5) of a drawn string in the localized footer table, or -1.</summary>
    internal static int ShuffleCategoryIndex(string text)
    {
        var names = ShuffleCategoryNames();
        for (int i = 0; i < names.Length; i++)
            if (names[i].Length > 0 && string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── Config-menu labels (cmpConfigItem.ctd) ───────────────────────────────

    private const long InitFreePackVA = 0x1411658C0L;
    private static nint _cfgPackSeen;
    private static int _cfgLangSeen = int.MinValue;
    private static nint _cfgRows;          // first row
    private static int _cfgStride;
    private static int _cfgCount;
    private static string[]? _cfgTexts;
    private static HashSet<string>? _cfgSet;
    private static readonly object CfgLock = new();

    /// <summary>Row stride of the label table — FUN_140181C70: 0x40 for EN/KR/CHT/CHS, 0x80 otherwise.</summary>
    private static int ConfigStride(int lang) => (lang >= 1 && lang <= 4) ? 0x40 : 0x80;

    /// <summary>
    /// Refresh the cached label table if the pack pointer changed. Cheap when unchanged.
    /// Returns false when the table is not available (title screen, table not loaded).
    /// </summary>
    private static bool EnsureConfigTable()
    {
        nint pack;
        if (!TryReadRaw((nint)InitFreePackVA, &pack, 8) || pack == 0) return false;
        int langNow = LanguageId;
        lock (CfgLock)
        {
            // Cache key = pack pointer AND language id: the stride depends on the language,
            // and the id can still read 0 (BSS default) if this runs before the game's init.
            if (pack == _cfgPackSeen && langNow == _cfgLangSeen) return _cfgTexts != null;
            _cfgPackSeen = pack; _cfgLangSeen = langNow;
            _cfgTexts = null; _cfgSet = null;
            nint entry = FindPackEntry(pack, "cmpConfigItem.ctd", 96 * 1024 * 1024, out int size);
            if (entry == 0) { Log("[Language] cmpConfigItem.ctd not found in the init_free pack"); return false; }
            // payload: u32 dataLen, u32 rowCount, 8 zero bytes, then the rows
            int count; if (!TryReadRaw(entry + 0x24 + 4, &count, 4)) return false;
            if (count <= 0 || count > 512) { Log($"[Language] cmpConfigItem.ctd row count {count} rejected"); return false; }
            _cfgStride = ConfigStride(LanguageId);
            _cfgRows = entry + 0x34;
            _cfgCount = count;
            var texts = new string[count];
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                texts[i] = ReadAtlusStringRpm(_cfgRows + i * _cfgStride, _cfgStride).Trim();
                if (texts[i].Length > 0) set.Add(texts[i]);
            }
            _cfgTexts = texts; _cfgSet = set;
            Log($"[Language] config labels: {count} rows, stride 0x{_cfgStride:X}, [0]=\"{texts[0]}\" [1]=\"{(count > 1 ? texts[1] : "")}\" [91]=\"{(count > 91 ? texts[91] : "")}\"");
            return true;
        }
    }

    /// <summary>Localized label text for a table index, or null if unavailable.</summary>
    internal static string? ConfigLabel(int idx)
    {
        if (!EnsureConfigTable()) return null;
        var t = _cfgTexts;
        if (t == null || idx < 0 || idx >= t.Length) return null;
        return t[idx].Length > 0 ? t[idx] : null;
    }

    /// <summary>True when a drawn string is exactly one of the localized config labels.</summary>
    internal static bool IsConfigLabel(string s)
    {
        if (!EnsureConfigTable()) return false;
        var set = _cfgSet;
        return set != null && set.Contains(s);
    }

    /// <summary>
    /// Walk a loaded pack (u32 count, then inline entries: name[32], u32 size, payload) for a
    /// named entry; falls back to a bounded byte search for the name if the walk fails (the
    /// in-memory layout was only ever seen through decompiles). Returns the ENTRY address
    /// (name at +0, size at +0x20, payload at +0x24) or 0.
    /// </summary>
    private static nint FindPackEntry(nint pack, string name, int scanLimit, out int size)
    {
        size = 0;
        var nameBytes = Encoding.ASCII.GetBytes(name);
        byte* hdr = stackalloc byte[0x24];
        foreach (nint start in new[] { pack + 4, pack })
        {
            nint e = start;
            for (int i = 0; i < 4096; i++)
            {
                if (!TryReadRaw(e, hdr, 0x24)) break;
                if (hdr[0] < 0x20 || hdr[0] > 0x7E) break;          // not a name → wrong layout
                int sz = *(int*)(hdr + 0x20);
                if (sz < 0 || sz > 0x10000000) break;
                if (MatchName(hdr, nameBytes)) { size = sz; return e; }
                e += 0x24 + sz;
            }
        }
        // Fallback: bounded linear search for the 32-byte name field.
        const int chunk = 1 << 20;
        var buf = new byte[chunk + 64];
        for (long off = 0; off < scanLimit; off += chunk)
        {
            fixed (byte* p = buf)
            {
                if (!TryReadRaw(pack + (nint)off, p, chunk)) break;
                int idx = IndexOf(buf, chunk, nameBytes);
                if (idx >= 0)
                {
                    nint e = pack + (nint)off + idx;
                    int sz;
                    if (TryReadRaw(e + 0x20, &sz, 4))
                    {
                        size = sz;
                        Log($"[Language] pack entry \"{name}\" found by scan at +0x{off + idx:X}");
                        return e;
                    }
                }
            }
        }
        return 0;
    }

    private static bool MatchName(byte* hdr, byte[] name)
    {
        if (name.Length >= 32) return false;
        for (int i = 0; i < name.Length; i++) if (hdr[i] != name[i]) return false;
        return hdr[name.Length] == 0;
    }

    private static int IndexOf(byte[] hay, int len, byte[] needle)
    {
        int last = len - needle.Length;
        for (int i = 0; i <= last; i += 4)      // entries are 4-aligned
        {
            if (hay[i] != needle[0]) continue;
            int j = 1;
            for (; j < needle.Length; j++) if (hay[i + j] != needle[j]) break;
            if (j == needle.Length && hay[i + j] == 0) return i;
        }
        return -1;
    }

    // ── Name-entry keyboard grid (m_name_keychar.ctd) ─────────────────────────

    private const long NEntryArcVA = 0x1451D2280L;
    private const long KeyRowsEnVA = 0x1451D2330L;     // 6 row pointers (english)
    private const long KeyRowsOtherVA = 0x1451D22D0L;  // 7 row pointers (every other language)
    private static nint _keyArcSeen, _keyGrid;

    /// <summary>Address of keyboard row <paramref name="row"/>, or 0 when the grid isn't loaded.</summary>
    private static nint KeyboardRow(int row)
    {
        nint rowPtr = 0;
        int lang = LanguageId;
        long rowsVA = lang == 1 ? KeyRowsEnVA : KeyRowsOtherVA;
        int maxRows = lang == 1 ? 6 : 7;
        if (row < maxRows) TryReadRaw((nint)(rowsVA + row * 8), &rowPtr, 8);
        if (rowPtr != 0 && ProbeReadable(rowPtr, 0x28)) return rowPtr;

        // Fallback: locate the grid inside the loaded nEntry arc.
        nint arc;
        if (!TryReadRaw((nint)NEntryArcVA, &arc, 8) || arc == 0) return 0;
        if (arc != _keyArcSeen)
        {
            _keyArcSeen = arc; _keyGrid = 0;
            nint e = FindPackEntry(arc, "m_name_keychar.ctd", 16 * 1024 * 1024, out _);
            if (e != 0) _keyGrid = e + 0x24;
            Log($"[NameEntry] keychar grid via arc scan: 0x{_keyGrid:X}");
        }
        if (_keyGrid == 0) return 0;
        rowPtr = _keyGrid + row * 0x40;
        return ProbeReadable(rowPtr, 0x28) ? rowPtr : 0;
    }

    /// <summary>True when the game's keyboard grid is readable right now.</summary>
    internal static bool KeyboardGridLoaded() => KeyboardRow(0) != 0;

    /// <summary>
    /// The character drawn at (row, col) of the name-entry keyboard, as the game's own
    /// glyph table decodes it. Returns null for an empty cell or when the grid is not loaded.
    /// Cells are 2-byte big-endian glyph codes (= AtlusEncoding CodePoint form), 20 per row,
    /// row stride 0x40.
    /// </summary>
    internal static string? KeyboardChar(int row, int col)
    {
        if (row < 0 || row > 7 || col < 0 || col > 19) return null;
        nint rowPtr = KeyboardRow(row);
        if (rowPtr == 0) return null;
        byte* cell = stackalloc byte[2];
        if (!TryReadRaw(rowPtr + col * 2, cell, 2)) return null;
        if (cell[0] == 0 && cell[1] == 0) return null;
        string s = DecodeKeyCell(cell[0], cell[1]);
        s = NormalizeFullwidth(s).Trim();
        return s.Length == 0 ? null : s;
    }

    private static Encoding? _sjis;
    private static bool _sjisTried;

    /// <summary>
    /// Keyboard cells are SHIFT-JIS (live-verified 2026-09-08: あ = 82A0, Ａ = 8260 — the glyph
    /// tables don't even contain fullwidth letters, so the glyph-code reading spoke 〔〈《 for
    /// kana). The one exception: the European keyboards put ACCENTED letters at Shift-JIS
    /// KANJI positions (FR é = 9CE3), where the game's glyph table (P4G_EFIGS: é at code 9CE3)
    /// is the right decoder. So: Shift-JIS first; a CJK ideograph or an undecodable pair falls
    /// back to the glyph table.
    /// </summary>
    private static string DecodeKeyCell(byte hi, byte lo)
    {
        if (!_sjisTried)
        {
            _sjisTried = true;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _sjis = Encoding.GetEncoding(932);
            }
            catch (Exception e) { Log($"[NameEntry] Shift-JIS codepage unavailable: {e.Message}"); }
        }
        byte[] pair = { hi, lo };
        string s = "";
        if (_sjis != null)
        {
            try { s = _sjis.GetString(pair).Trim('\0'); } catch { s = ""; }
        }
        bool bad = s.Length == 0 || s.Contains('�') || IsCjkIdeograph(s);
        if (bad && AtlusEncoding.P4 != null)
        {
            try { s = AtlusEncoding.P4.GetString(pair); } catch { }
        }
        return s;
    }

    private static bool IsCjkIdeograph(string s)
    {
        foreach (char c in s)
            if ((c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿')) return true;
        return false;
    }

    /// <summary>Fullwidth ASCII (U+FF01..U+FF5E) → ASCII; ideographic space → space.</summary>
    internal static string NormalizeFullwidth(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= '！' && c <= '～') sb.Append((char)(c - 0xFEE0));
            else if (c == '　') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
