using System.Text;
using System.Text.RegularExpressions;

namespace p4g64.accessibility;

/// <summary>
/// Translation layer for the mod's OWN announcements (2026-09-05).
///
/// The mod speaks English prompts from ~hundreds of call sites ("Dialogue reader on.",
/// "Took 42 damage.", …). Instead of touching every one of them, EVERY spoken line is
/// funnelled through <c>Speech.SayCore</c>, which asks this class for a replacement
/// right before <c>Tolk.Output</c>. Miss = the original English is spoken, so a partial or
/// missing table degrades gracefully rather than muting anything.
///
/// DATA FILE — <c>ui_strings.tsv</c>, flat in the mod folder (resolved through
/// <see cref="Utils.DataPath"/>, so the dev tree's database/ folder works too). TAB separated,
/// UTF-8, first line is the header. Columns are found BY HEADER NAME, so their order is free and
/// any column this class does not know about is silently ignored — the extraction pipeline may
/// add bookkeeping columns without touching the loader. What it produces today:
///
///     key  file  line  en  zh-TW  zh-CN  raw
///
///   • key / file / line / raw — bookkeeping for the extraction pipeline (<c>raw</c> keeps the
///             original C# interpolated string the row was lifted from, for review); ignored here.
///   • en    — the EXACT English string as passed to Speech.Say, optionally with {0} {1} …
///             placeholders standing in for the runtime-interpolated parts. Loaded through the
///             same U+3000 → ASCII space clean-up Speech.SayCore applies before asking us, so a
///             cell pasted from the game's full-width text still matches.
///   • zh-TW / zh-CN — the translation, reusing the same {n} placeholders (they may be
///             re-ordered; {2} may come before {0}). An EMPTY cell means "not translated
///             yet" and the row is skipped for that language.
///
/// Example rows (→ = a TAB, trailing bookkeeping columns elided):
///
///     key                → file       → line → en                        → zh-TW      → zh-CN
///     speech.no_history  → Speech.cs  → 125  → No history.               → 沒有歷史紀錄。 → 没有历史记录。
///     battle.damage      → Battle.cs  → 88   → {0} took {1} damage.      → {0} 受到 {1} 點傷害。 → {0} 受到 {1} 点伤害。
///
/// <c>ui_strings.sample.tsv</c> next to the sources is documentation only — it is NEVER loaded.
/// The one file that takes effect is <c>ui_strings.tsv</c>.
///
/// Escapes: <c>\n</c>, <c>\r</c>, <c>\t</c> and <c>\\</c> are decoded in the en/translation
/// cells (a TSV cell cannot hold a real tab or line break).
///
/// LANGUAGE — driven by <see cref="Native.Text.GameLanguage.ActiveTable"/>, the table already
/// chosen for the game's text: P4G_CHT.tsv → zh-TW column, P4G_CHS.tsv → zh-CN column,
/// anything else → the layer stays DISABLED and <see cref="TryTranslate"/> is a single bool
/// test (zero cost for English/Japanese/Korean players).
///
/// MATCHING — exact lookup first (a plain <see cref="Dictionary{TKey,TValue}"/> on the ordinal
/// string), then the placeholder rows in file order, each pre-compiled ONCE at startup into an
/// anchored regex where every {n} became a capture group (<c>(.+?)</c>, or <c>(.+)</c> for a
/// group at the very end of the template). The captured runtime values are then dropped into
/// the {n} slots of the target-language template. First match wins.
///
/// EXACT-ONLY MODE — a handful of components do not author prompts at all, they forward the GAME's
/// own text (Dialogue, SubtitleReader, SystemMessage, MessageBubble, TelopReader, BacklogReader,
/// InternetDialog, Tutorial, GameOverReader, SocialLinkDetail, DifficultyMenu — the list lives in
/// <c>Speech.ForwardedGameTextSources</c>). Speech.SayCore passes <c>exactOnly: true</c> for those,
/// so the pattern rows are skipped and only the exact table applies. Without it a loose row could
/// match a whole English game sentence and replace just the word it recognised, which is exactly
/// how "…when you're in a pinch,和you to help others in turn" reached a player (2026-09-05).
/// Their own fixed prompts are unaffected — an exact row still translates them.
///
/// CAPTURED-VALUE GUARD — the exact-only list is a NAMED set, so it can only protect the channels
/// we already know about; a component nobody classified yet forwards prose through the full table
/// and gets rewritten. So a second, source-agnostic rule runs on every pattern hit, right before
/// the values are poured into the target template: if any CAPTURED value looks like English prose
/// — 2 or more spaces AND 4 or more ASCII letters — the whole match is discarded and the line is
/// returned untranslated, i.e. spoken as the original English. A mod prompt never interpolates a
/// sentence; it interpolates numbers, names and single words ("5", "April", "Vox Populi"), all of
/// which stay below that bar, and translated game text has no ASCII letters at all. What trips it
/// is exactly the failure shape seen twice now: a list-style row such as "{0}. {1} of {2}."
/// swallowing "Normal. This is the best balance of difficulty and enjoyment. …" and reading back
/// as "Normal.第This is the best balance个，共difficulty and enjoyment…个" (2026-09-05,
/// DifficultyMenu — which is ALSO on the exact-only list now; this rule is the net under it for
/// the components we have not found yet). Cost is one character scan of the captures, only after a
/// row has already matched.
///
/// Each captured value is itself looked up ONCE in the exact table on the way in (never in the
/// patterns — that would recurse), because some captures are English words the C# side generated:
/// month names, weekdays, directions. A single-word row like <c>date.month.april → 四月</c> in the
/// data file therefore fixes "預報，April15日" everywhere at once. No hit = kept verbatim.
///
/// PLACEHOLDER ROW LIMITS — a row whose <c>en</c> breaks either rule is dropped at load time
/// (logged, and that prompt simply stays English):
///
///   • The literal text left after removing every {n} must contain at least one letter or digit.
///     "{0} {1}" or "{0}: {1}" would otherwise compile into a regex matching nearly any sentence
///     — including the game's own dialogue, which is spoken through the same Speech.Say path.
///   • That literal text must not be a lone "and" / "or" sitting BETWEEN placeholders, i.e. with
///     no letter or digit at either end of the template. "{0} and {1}" passes the rule above yet
///     still matches any sentence containing that word, and can only ever fix one prompt.
///     Deliberately narrow: an edge anchor ("{0} HP", "Row {0}", "{0} on.") or a word that carries
///     meaning ("{0} of {1}.", "{0}, now {1}") keeps the row — those only see sentences the mod
///     assembled itself, and the exact-only mode above already shields the game's own text.
///   • The same {n} may appear only ONCE in <c>en</c>. "{0} beats {0}" would need a regex
///     back-reference; two independent groups would match too much and the second capture would
///     be discarded. Give the two parts distinct numbers ("{0} beats {1}") instead. The
///     TRANSLATION side has no such limit — it may repeat or omit any {n}.
///
/// SAFETY — a missing / malformed file, a missing column, a bad row or a runaway regex can
/// only disable this layer (or skip that row): nothing here is allowed to throw into the
/// speech path. Everything is logged through <see cref="Utils.Log"/> with the [i18n] tag.
/// </summary>
internal static class Localization
{
    /// <summary>File name looked up in the mod folder. Absent = layer off (not an error).</summary>
    private const string TableFile = "ui_strings.tsv";

    /// <summary>A pathological pattern must not stall the game thread — bail after this.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>Above this many placeholder rows we stop paying RegexOptions.Compiled at startup.</summary>
    private const int CompileLimit = 400;

    // Written ONCE in Init, before any component (and therefore any speech) exists; _enabled is
    // written LAST and is volatile, so a reader that sees true also sees the finished tables.
    // Nothing mutates after Init, so the hot path needs no lock.
    private static volatile bool _enabled;
    private static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private static Pattern[] _patterns = Array.Empty<Pattern>();

    /// <summary>True when a table for the current game language was loaded.</summary>
    internal static bool Enabled => _enabled;

    /// <summary>{0}, {12}, … in a template.</summary>
    private static readonly Regex PlaceholderRe =
        new(@"\{(\d+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One placeholder row: the compiled English matcher + the pre-split target template.</summary>
    private sealed class Pattern
    {
        internal Regex Re = null!;
        /// <summary>Capture group i+1 of <see cref="Re"/> holds placeholder number SourceOrder[i].</summary>
        internal int[] SourceOrder = Array.Empty<int>();
        /// <summary>Target template split at its {n}s: Literals.Length == Slots.Length + 1.</summary>
        internal string[] TargetLiterals = Array.Empty<string>();
        internal int[] TargetSlots = Array.Empty<int>();
    }

    /// <summary>
    /// Load the table for the language <see cref="Native.Text.GameLanguage.ResolveTable"/> just
    /// picked. Call from Mod.cs AFTER the language is decided and BEFORE components construct.
    /// </summary>
    internal static void Init()
    {
        try
        {
            string[] wanted = ColumnAliases(Native.Text.GameLanguage.ActiveTable);
            if (wanted.Length == 0)
            {
                Utils.Log($"[i18n] no translations for text table {Native.Text.GameLanguage.ActiveTable} — mod prompts stay English");
                return;
            }

            string path = Utils.DataPath(TableFile);
            if (!System.IO.File.Exists(path))
            {
                Utils.Log($"[i18n] {TableFile} not found ({path}) — mod prompts stay English");
                return;
            }

            string[] lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) { Utils.Log($"[i18n] {TableFile} has no rows — mod prompts stay English"); return; }

            string[] header = lines[0].Split('\t');
            int enIdx = FindColumn(header, new[] { "en" });
            int trIdx = FindColumn(header, wanted);
            if (enIdx < 0 || trIdx < 0)
            {
                Utils.Log($"[i18n] {TableFile} header is missing the \"en\" or \"{wanted[0]}\" column — mod prompts stay English");
                return;
            }

            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var patternRows = new List<KeyValuePair<string, string>>();   // en → translation, file order
            int skipped = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                string[] cells = lines[i].Split('\t');
                // The translation is usually one of the LAST columns, so an untranslated row often
                // ends right after "en" with its trailing tabs stripped by the editor. That is a
                // normal "not translated yet" row, not a broken one — only a row that does not even
                // reach the "en" column is counted as unusable.
                if (cells.Length <= enIdx) { skipped++; continue; }
                if (cells.Length <= trIdx) continue;              // no translation cell → speak English

                string en = NormalizeSource(Unescape(cells[enIdx]));
                string tr = Unescape(cells[trIdx]);
                if (en.Length == 0 || tr.Length == 0) continue;   // untranslated row → speak English

                if (!PlaceholderRe.IsMatch(en))
                {
                    if (!exact.ContainsKey(en)) exact[en] = tr;   // first row wins on a duplicate key
                    continue;
                }
                patternRows.Add(new KeyValuePair<string, string>(en, tr));
            }

            // RegexOptions.Compiled emits IL at construction — great for the per-line matching,
            // but it costs real milliseconds EACH, and this runs in the game's load path. Below
            // the limit we pay it once for the faster matching; a table that somehow ships
            // thousands of patterns falls back to the interpreted engine rather than freezing
            // startup (matching stays correct either way).
            var opts = RegexOptions.CultureInvariant | RegexOptions.Singleline;
            if (patternRows.Count <= CompileLimit) opts |= RegexOptions.Compiled;

            var patterns = new List<Pattern>(patternRows.Count);
            foreach (var row in patternRows)
            {
                var p = BuildPattern(row.Key, row.Value, opts);
                if (p != null) patterns.Add(p); else skipped++;
            }

            _exact = exact;
            _patterns = patterns.ToArray();
            _enabled = exact.Count > 0 || patterns.Count > 0;     // publish LAST (volatile release)

            // The table is LIVE from here on. Reporting it must never be able to undo that, so the
            // log gets its own guard instead of falling into the catch below (which would flip a
            // perfectly good table back to disabled over a logging hiccup).
            try
            {
                Utils.Log($"[i18n] {wanted[0]}: {exact.Count} exact + {patterns.Count} pattern string(s) from {TableFile}"
                          + (skipped > 0 ? $" ({skipped} unusable row(s) skipped)" : "")
                          + ((opts & RegexOptions.Compiled) == 0 ? " (patterns interpreted, too many to compile)" : ""));
            }
            catch { }
        }
        catch (Exception e)
        {
            _enabled = false;
            Utils.Log($"[i18n] load failed, mod prompts stay English: {e.Message}");
        }
    }

    /// <summary>
    /// Swap an English mod prompt for its translation. False (and <paramref name="translated"/>
    /// left empty) whenever the layer is off or nothing matches — the caller then speaks the
    /// original. Never throws.
    ///
    /// <paramref name="exactOnly"/> = the line is NOT a mod prompt but text forwarded verbatim
    /// from the game (dialogue, subtitles, tutorial pages …), so only the exact table may apply:
    /// its fixed short prompts still translate, while the placeholder patterns — which are
    /// written for the mod's own sentences and can match a stray English clause — are skipped.
    /// See <c>Speech.ForwardedGameTextSources</c> for who gets this mode and why.
    /// </summary>
    internal static bool TryTranslate(string en, out string translated, bool exactOnly = false)
    {
        translated = "";
        if (!_enabled || string.IsNullOrEmpty(en)) return false;
        try
        {
            if (_exact.TryGetValue(en, out string? hit)) { translated = hit; return true; }
            if (exactOnly) return false;   // forwarded game text — patterns must not rewrite it

            var patterns = _patterns;
            for (int i = 0; i < patterns.Length; i++)
            {
                Match m;
                try { m = patterns[i].Re.Match(en); }
                catch (RegexMatchTimeoutException) { continue; }   // pathological row → ignore it
                if (!m.Success) continue;
                // CAPTURED-VALUE GUARD (see the class notes): a match that swallowed an English
                // SENTENCE into one of its {n}s is a false positive, whatever row produced it.
                // Give up on translating this line entirely rather than trying the next row —
                // the same prose would just be offered to an equally loose pattern.
                if (HasProseCapture(patterns[i], m)) return false;
                translated = Fill(patterns[i], m);
                return true;
            }
        }
        catch (Exception e)
        {
            LogOnce($"[i18n] translate failed: {e.Message}");
        }
        return false;
    }

    /// <summary>Convenience wrapper: the translation, or the input unchanged.</summary>
    internal static string Tr(string en, bool exactOnly = false)
        => TryTranslate(en, out string t, exactOnly) ? t : en;

    // ── loading helpers ─────────────────────────────────────────────────────────

    /// <summary>Accepted header spellings per glyph table; empty = language not translated.</summary>
    private static string[] ColumnAliases(string activeTable) => activeTable switch
    {
        "P4G_CHT.tsv" => new[] { "zh-TW", "zh_TW", "zh-Hant", "cht" },
        "P4G_CHS.tsv" => new[] { "zh-CN", "zh_CN", "zh-Hans", "chs" },
        _ => Array.Empty<string>(),
    };

    private static int FindColumn(string[] header, string[] names)
    {
        for (int i = 0; i < header.Length; i++)
        {
            // A UTF-8 BOM is normally eaten by the reader; strip it anyway so column 0 still matches.
            string h = StripBom(header[i].Trim());
            foreach (var n in names)
                if (string.Equals(h, n, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Drop a leading U+FEFF (byte-order mark) the reader may have left on cell 0.</summary>
    private static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;

    /// <summary>
    /// Apply the SAME clean-up <c>Speech.SayCore</c> does before it asks us to translate: the
    /// ideographic space U+3000 becomes an ASCII space. Without this an <c>en</c> cell copied
    /// straight out of the game's text (which uses U+3000 between words) could never match,
    /// because by the time the line reaches <see cref="TryTranslate"/> it has already been
    /// converted. Applied to the en side only — translations are spoken as authored.
    /// </summary>
    private static string NormalizeSource(string s) => s.IndexOf('　') >= 0 ? s.Replace('　', ' ') : s;

    /// <summary>Decode the escapes a TSV cell needs (\n \r \t \\). Anything else is left alone.</summary>
    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(n); break;   // not an escape we know — keep both chars
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Compile one "{n}" row: the English template becomes an anchored regex whose capture
    /// groups replace the placeholders, and the translation is pre-split at its own {n}s.
    /// Returns null (with a log line) for a row we refuse: a bad regex, an English side whose
    /// literal text holds no letter or digit to anchor on, one that is nothing but a conjunction
    /// between two placeholders, or a repeated {n}. See the PLACEHOLDER ROW LIMITS section on the
    /// class for why.
    /// </summary>
    private static Pattern? BuildPattern(string en, string tr, RegexOptions opts)
    {
        try
        {
            // \A…\z (not ^…$): the template must match the WHOLE spoken line, including any
            // trailing newline — ^…$ would let a line end early and silently drop text.
            var sb = new StringBuilder(@"\A");
            var order = new List<int>();
            int pos = 0;
            bool anchored = false;   // does the literal text carry a letter or digit?
            // Every literal chunk, joined by a space so two chunks can never fuse into one word,
            // plus whether either EDGE of the template (before the first {n} / after the last)
            // carries an anchor. Both only feed the conjunction check below.
            var literalText = new StringBuilder();
            bool edgeAnchored = false;
            bool firstChunk = true;
            foreach (Match m in PlaceholderRe.Matches(en))
            {
                string lit = en.Substring(pos, m.Index - pos);
                anchored |= HasLetterOrDigit(lit);
                if (firstChunk) { edgeAnchored |= HasLetterOrDigit(lit); firstChunk = false; }
                literalText.Append(lit).Append(' ');
                sb.Append(Regex.Escape(lit));
                if (!int.TryParse(m.Groups[1].Value, out int slot)) return null;
                if (order.Contains(slot))
                {
                    // "{0} beats {0}" would need a back-reference to be right; two independent
                    // groups match far too loosely and the second capture is thrown away anyway.
                    // Refuse the row rather than half-supporting it.
                    Utils.Log($"[i18n] skipped row \"{en}\": placeholder {{{slot}}} appears more than once (not supported — use distinct numbers)");
                    return null;
                }
                order.Add(slot);
                // Greedy for a trailing group (it owns the rest of the line), lazy elsewhere so
                // the literal text after it still gets a chance to match.
                sb.Append(m.Index + m.Length >= en.Length ? "(.+)" : "(.+?)");
                pos = m.Index + m.Length;
            }
            string tail = en.Substring(pos);
            anchored |= HasLetterOrDigit(tail);
            edgeAnchored |= HasLetterOrDigit(tail);
            literalText.Append(tail);
            sb.Append(Regex.Escape(tail)).Append(@"\z");
            // Literal text that is only spaces and punctuation is no anchor at all: "{0} {1}"
            // compiles to \A(.+?)\ (.+)\z, which matches nearly ANY sentence — including the
            // game's own dialogue, spoken through the same Speech.Say funnel. Such a row would
            // silently rewrite unrelated lines, so demand at least one letter or digit.
            if (!anchored)
            {
                Utils.Log($"[i18n] skipped row \"{en}\": nothing but placeholders and punctuation to match on (would rewrite unrelated lines)");
                return null;
            }
            // Second belt (2026-09-05, the "…in a pinch,和you to help others…" report): "{0} and {1}"
            // clears the anchor test above, yet it matches ANY sentence containing " and " and
            // rewrites just that word. The exact-only rule in TryTranslate already shields the
            // game-text channels, so this only has to catch the shape that stays dangerous
            // everywhere else — a bare conjunction BETWEEN two placeholders, with no letter or
            // digit at either end of the template to pin the match down. Deliberately narrow:
            // rows with an edge anchor ("{0} HP", "Row {0}", "{0} on.") or a more meaningful
            // inner word ("{0} of {1}.", "{0}, now {1}") only ever see sentences the mod itself
            // assembled, and are kept.
            string? conj = edgeAnchored ? null : LoneConjunction(literalText.ToString());
            if (conj != null)
            {
                Utils.Log($"[i18n] skipped row \"{en}\": \"{conj}\" between placeholders is the only thing to match on (too generic — would rewrite unrelated lines)");
                return null;
            }

            var p = new Pattern
            {
                Re = new Regex(sb.ToString(), opts, MatchTimeout),
                SourceOrder = order.ToArray(),
            };

            var literals = new List<string>();
            var slots = new List<int>();
            pos = 0;
            foreach (Match m in PlaceholderRe.Matches(tr))
            {
                if (!int.TryParse(m.Groups[1].Value, out int slot)) return null;
                literals.Add(tr.Substring(pos, m.Index - pos));
                slots.Add(slot);
                pos = m.Index + m.Length;
            }
            literals.Add(tr.Substring(pos));
            p.TargetLiterals = literals.ToArray();
            p.TargetSlots = slots.ToArray();

            // A slot captured from English but absent from the translation means that runtime
            // value (a name, a number) is dropped from what the player hears. Sometimes deliberate
            // — Chinese may not need it — so the row stays usable and this is only a warning.
            string missing = "";
            foreach (int slot in order)
                if (Array.IndexOf(p.TargetSlots, slot) < 0)
                    missing += (missing.Length > 0 ? ", " : "") + "{" + slot + "}";
            if (missing.Length > 0)
                Utils.Log($"[i18n] row \"{en}\": {missing} captured from English but missing from the translation — that value will be dropped");

            return p;
        }
        catch (Exception e)
        {
            Utils.Log($"[i18n] bad pattern row \"{en}\": {e.Message}");
            return null;
        }
    }

    /// <summary>Does this literal chunk carry anything a pattern can actually anchor on?</summary>
    private static bool HasLetterOrDigit(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (char.IsLetterOrDigit(s[i])) return true;
        return false;
    }

    /// <summary>Words carrying no meaning of their own — a template made of one of these is a trap.</summary>
    private static readonly string[] BareConjunctions = { "and", "or" };

    /// <summary>
    /// The conjunction a template's literal text boils down to when it is NOTHING but one bare
    /// conjunction; null for every other row (no word, two words, or any other word). The CALLER
    /// additionally requires that neither edge of the template is anchored, which — with a single
    /// word in play — is what makes that word an INNER one, sitting between two placeholders.
    /// </summary>
    private static string? LoneConjunction(string literal)
    {
        string? only = null;
        int i = 0;
        while (i < literal.Length)
        {
            if (!char.IsLetterOrDigit(literal[i])) { i++; continue; }
            int start = i;
            while (i < literal.Length && char.IsLetterOrDigit(literal[i])) i++;
            if (only != null) return null;   // a second word → specific enough, keep the row
            only = literal.Substring(start, i - start);
        }
        if (only == null) return null;       // no word at all — the anchor check already refused it

        foreach (var c in BareConjunctions)
            if (string.Equals(only, c, StringComparison.OrdinalIgnoreCase)) return only;
        return null;
    }

    /// <summary>
    /// Did this match pour an English SENTENCE into one of its placeholders? Checks the capture
    /// groups only (group 0 is the whole line, which legitimately contains the row's own words).
    /// </summary>
    private static bool HasProseCapture(Pattern p, Match m)
    {
        for (int g = 1; g <= p.SourceOrder.Length; g++)
            if (LooksLikeProse(m.Groups[g].Value)) return true;
        return false;
    }

    /// <summary>
    /// "Looks like English prose": at least 2 spaces AND at least 4 ASCII letters. Chosen to clear
    /// every value the mod actually interpolates — numbers ("5"), single words ("April", "North"),
    /// and short proper nouns ("Vox Populi", one space) all pass through untouched — while a
    /// forwarded sentence ("This is the best balance") is caught. Translated game text carries no
    /// ASCII letters at all, so it is never affected either. One allocation-free character scan
    /// over a value that is at most a line long; it runs only when a pattern already matched.
    /// </summary>
    private static bool LooksLikeProse(string s)
    {
        int spaces = 0, letters = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ' ') spaces++;
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letters++;
        }
        return spaces >= 2 && letters >= 4;
    }

    /// <summary>
    /// Stitch the target template back together with this match's captured values, each passed
    /// through <see cref="TranslateCapture"/> first.
    /// </summary>
    private static string Fill(Pattern p, Match m)
    {
        var sb = new StringBuilder(p.TargetLiterals[0], 64);
        for (int i = 0; i < p.TargetSlots.Length; i++)
        {
            int g = Array.IndexOf(p.SourceOrder, p.TargetSlots[i]);
            // A {n} the English side never captured stays literal — visible in-game, so a
            // mistranslated row is obvious instead of silently losing text.
            sb.Append(g >= 0 ? TranslateCapture(m.Groups[g + 1].Value) : "{" + p.TargetSlots[i] + "}");
            sb.Append(p.TargetLiterals[i + 1]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A captured value is not always a number or a proper noun: plenty of them are English words
    /// the C# side built itself (month and weekday names out of _monthNames, direction words …).
    /// Dropped in raw they produce "預報，April15日". So each capture gets ONE lookup in the exact
    /// table — the data file already carries word rows like <c>date.month.april → 四月</c>, which
    /// start working the moment they are translated. Miss = the value is kept verbatim.
    ///
    /// Deliberately the EXACT table only, never the patterns: a pattern could match the capture
    /// and fill from it again, i.e. unbounded recursion, and it would re-scan every pattern row
    /// for every placeholder of every spoken line. One dictionary probe is the whole cost.
    /// Reads nothing but the post-Init, never-mutated <see cref="_exact"/>, so the hot path stays
    /// lock-free and thread-safe exactly as before.
    /// </summary>
    private static string TranslateCapture(string value)
        => value.Length > 0 && _exact.TryGetValue(value, out string? hit) ? hit : value;

    private static bool _runtimeWarned;

    /// <summary>Runtime faults repeat once per spoken line — log the first and stay quiet.</summary>
    private static void LogOnce(string message)
    {
        if (_runtimeWarned) return;
        _runtimeWarned = true;
        try { Utils.Log(message); } catch { }
    }
}
