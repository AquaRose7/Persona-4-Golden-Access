using DavyKager;

namespace p4g64.accessibility;

/// <summary>
/// Central speech output + history (2026-06-20). Every mod announcement goes through
/// <see cref="Say"/> (was <c>Tolk.Output</c> everywhere) so the last N lines are kept in a ring
/// buffer the player can repeat / scroll back through. <see cref="Record"/> stores a line WITHOUT
/// speaking it — used by the dialogue reader when its auto-read is toggled off, so the text is
/// still available to repeat while the game's voice plays.
///
/// Controls (wired in HistoryKeys [keyboard] + ControllerInput [pad]):
///   repeat last line  — Shift+P / LT+RT+D-pad Up
///   history back/fwd  — Shift+[ , Shift+] / LT+RT+D-pad Left/Right
/// </summary>
internal static class Speech
{
    private const int Max = 20;   // keep only the last 20 lines
    private static readonly List<string> _hist = new();
    private static int _navIdx = -1;            // current browse position; -1/Count = "at newest"
    private static readonly object _lock = new();

    // Anti-spam: suppress an IDENTICAL line re-spoken within this window — but ONLY IN BATTLE. The
    // navigator↔reader ping-pong (two readers alternating the same line 30-40×) is a battle-only
    // problem; applying the guard everywhere made fast list-scrolling (re-reading the same item)
    // feel bad by muting a deliberate re-read. So outside battle nothing is ever muted, and in battle
    // the window is short enough to kill only true rapid-fire ping-pong, not an intentional re-read.
    // Manual repeat (RepeatLast/Step) and Record bypass this regardless.
    private const long SpamWindowMs = 300;

    // GRACE RULE (2026-09-04, user: zone names cut by the next line; "took N damage" cut by
    // "X's turn"): a line from one of these sources opens a protected window sized to its
    // length; an INTERRUPTING line arriving inside it is downgraded to a queued one, so the
    // protected line finishes first. Queued lines never cut anything, so nothing is lost —
    // at worst a line arrives a second late.
    private static readonly HashSet<string> ProtectedSources = new(StringComparer.Ordinal)
        { "OverworldZones", "CheckLabel", "DamageMonitor", "EnemyActionHook" };
    private static long _protectedUntilMs;
    private static readonly Dictionary<string, long> _recentSaid = new();

    // FORWARDED GAME TEXT (2026-09-05, user: "…when you're in a pinch,和you to help others…").
    // These readers do not author prompts of their own — they hand the GAME's text straight to
    // the screen reader (dialogue lines, subtitles, system messages, tutorial pages …). Such a
    // line is arbitrary prose, so the translation layer's PATTERN rows have no business touching
    // it: a row whose literal part is short enough ("{0} and {1}") can match an English sentence
    // that merely contains that word and swap the word alone, leaving the mixed-language result
    // the player heard. Lines from these sources are therefore translated by EXACT lookup only
    // (see Localization.Tr's exactOnly) — their fixed short prompts still get translated, their
    // forwarded prose never gets rewritten. Names are the source FILE names without .cs, matching
    // the CallerFilePath tag computed in Say.
    private static readonly HashSet<string> ForwardedGameTextSources = new(StringComparer.Ordinal)
    {
        "Dialogue", "SubtitleReader", "SystemMessage", "MessageBubble", "TelopReader",
        "BacklogReader", "InternetDialog", "Tutorial", "GameOverReader", "SocialLinkDetail",
        // DifficultyMenu joined on the same grounds (2026-09-05, user heard "Normal.第This is the
        // best balance个，共difficulty and enjoyment. Please experience the tension.个"): its option
        // DESCRIPTIONS are the game's own on-screen prose carried inside the component, so
        // "Normal. <long English sentence>." was matched by a list-style "{0}. {1} of {2}." row and
        // poured into the Chinese counter template. Forwarded game text in every way that matters
        // here, even though those characters happen to live in our source file.
        "DifficultyMenu",
    };

    // The game's text separates words with the Japanese IDEOGRAPHIC SPACE (U+3000),
    // not an ASCII space — some screen readers stumble on it (words run together /
    // odd pauses). Swap it for a normal space so EVERY announcement reads cleanly.
    // Applied centrally (all readers route through Say/Record) so nothing is missed.
    // The IndexOf guard avoids allocating when there's no full-width space (the norm).
    // Verified 2026-07-09: "Custom　Sub　Menu" → "Custom Sub Menu".
    private static string Normalize(string s)
        => s.IndexOf('　') >= 0 ? s.Replace('　', ' ') : s;

    /// <summary>Speak a line AND record it to history. Drop-in for the old <c>Tolk.Output</c>.</summary>
    internal static void Say(string text, bool interrupt = true,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
    {
        // Speech-source tag (2026-08-31, the "random numbers" hunt): every spoken line is
        // logged with the component that spoke it, so a player's log identifies the culprit
        // of any speech spam. CallerFilePath is a COMPILE-TIME constant — zero runtime cost;
        // the substring below runs once per spoken sentence (a few per second at most).
        // The tag is also handed to SayCore, which uses it to decide how the translation layer
        // may touch this line (see ForwardedGameTextSources). Empty for a blank line — SayCore
        // drops those before it reaches anything that reads the tag.
        string src = "";
        if (!string.IsNullOrWhiteSpace(text))
        {
            int cut = callerFile.LastIndexOfAny(new[] { '\\', '/' });
            src = cut >= 0 ? callerFile.Substring(cut + 1) : callerFile;
            if (src.EndsWith(".cs")) src = src.Substring(0, src.Length - 3);
            long now = Environment.TickCount64;
            bool downgraded = false;
            lock (_lock)
            {
                if (interrupt && now < _protectedUntilMs) { interrupt = false; downgraded = true; }
                if (ProtectedSources.Contains(src))
                    _protectedUntilMs = now + Math.Clamp(400 + text.Length * 55L, 1200L, 3500L);
            }
            Utils.Log($"[Speech] {src}: {text}{(downgraded ? "  (queued: grace)" : interrupt ? "" : "  (queued)")}");
        }
        // TEMP perf shim (heaviness diag 2026-07-27): measures the full cost incl. Tolk IPC.
        long t0 = Components.PerfDiag.Begin();
        try { SayCore(text, interrupt, src); }
        finally { Components.PerfDiag.End(Components.PerfDiag.B.SpeechSay, t0); }
    }

    /// <param name="src">Speaking component (file name without .cs) as tagged by <see cref="Say"/>;
    /// "" when unknown, which simply means the full translation table applies.</param>
    private static void SayCore(string text, bool interrupt = true, string src = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = Normalize(text);
        if (Components.FieldTracker.InBattle)   // spam guard is battle-only (see note above)
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                if (_recentSaid.TryGetValue(text, out long t) && now - t < SpamWindowMs)
                {
                    _recentSaid[text] = now;   // keep refreshing so sustained spam stays muted
                    return;
                }
                _recentSaid[text] = now;
                if (_recentSaid.Count > 48)    // opportunistic prune of stale entries
                {
                    var stale = new List<string>();
                    foreach (var kv in _recentSaid) if (now - kv.Value > SpamWindowMs * 4) stale.Add(kv.Key);
                    foreach (var k in stale) _recentSaid.Remove(k);
                }
            }
        }
        // TRANSLATION LAYER (2026-09-05): the mod's own prompts are authored in English at
        // hundreds of call sites; instead of touching them all, the swap happens HERE — the one
        // point every announcement converges on — using the ui_strings.tsv table for the game's
        // language (see Localization). Deliberate split: the spam guard above keys on the
        // ENGLISH text (stable regardless of language, and it is what the callers actually
        // repeat), while history + Tolk get the TRANSLATION so repeat/browse read back exactly
        // what was spoken. No table, or a language with no translations = Localization is
        // disabled and Tr is a single bool test. Lines forwarded verbatim from the game get the
        // exact-only mode (no pattern rewriting) — see ForwardedGameTextSources above.
        string spoken = Localization.Tr(text, ForwardedGameTextSources.Contains(src));
        Record(spoken);
        Tolk.Output(spoken, interrupt);
    }

    /// <summary>Record a line to history WITHOUT speaking it (e.g. dialogue while auto-read is off).</summary>
    internal static void Record(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = Normalize(text);   // history/repeat should read the same clean text as Say
        lock (_lock)
        {
            if (_hist.Count > 0 && _hist[^1] == text) { _navIdx = _hist.Count; return; }  // skip consecutive dupes
            _hist.Add(text);
            if (_hist.Count > Max) _hist.RemoveAt(0);
            _navIdx = _hist.Count;   // a new line resets browsing to the newest
        }
    }

    /// <summary>Re-speak the most recent line.</summary>
    internal static void RepeatLast()
    {
        lock (_lock)
        {
            // The history browser is the only speech that bypasses SayCore (it must NOT re-record
            // what it reads back), so its own fixed prompts are translated here; the history lines
            // themselves were already translated on the way in.
            if (_hist.Count == 0) { Tolk.Output(Localization.Tr("No history."), true); return; }
            _navIdx = _hist.Count;
            Tolk.Output(_hist[^1], true);
        }
    }

    /// <summary>Browse history. dir = -1 older, +1 newer. Speaks the landed-on line.</summary>
    internal static void Step(int dir)
    {
        lock (_lock)
        {
            if (_hist.Count == 0) { Tolk.Output(Localization.Tr("No history."), true); return; }
            if (_navIdx < 0 || _navIdx > _hist.Count - 1) _navIdx = _hist.Count;  // start from newest
            int next = _navIdx + dir;
            if (next < 0) { _navIdx = 0; Tolk.Output(Localization.Tr("Start of history.") + " " + _hist[0], true); return; }
            if (next > _hist.Count - 1) { _navIdx = _hist.Count - 1; Tolk.Output(Localization.Tr("Newest.") + " " + _hist[^1], true); return; }
            _navIdx = next;
            Tolk.Output(_hist[_navIdx], true);
        }
    }
}
