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

    // The game's text separates words with the Japanese IDEOGRAPHIC SPACE (U+3000),
    // not an ASCII space — some screen readers stumble on it (words run together /
    // odd pauses). Swap it for a normal space so EVERY announcement reads cleanly.
    // Applied centrally (all readers route through Say/Record) so nothing is missed.
    // The IndexOf guard avoids allocating when there's no full-width space (the norm).
    // Verified 2026-07-09: "Custom　Sub　Menu" → "Custom Sub Menu".
    private static string Normalize(string s)
    {
        if (s.IndexOf('　') >= 0) s = s.Replace('　', ' ');
        return s.IndexOfAny(KanjiChars) >= 0 ? SpeakKanji(s) : s;
    }

    // The European scripts show exactly three kanji, drawn as pictures: 応天門 (the Outen Gate) in
    // the lecture "missed the dot on top of the 応 character" (event E471) and as the three choices
    // of the finals question "Even Kobo made mistakes in writing" (E474, player report 2026-10-02).
    // An English voice can't pronounce a lone kanji, so they are spoken as what a sighted player
    // sees. Japanese / Chinese / Korean games keep them as normal text.
    private static readonly char[] KanjiChars = { '応', '天', '門' };
    private static readonly string[] KanjiWords = { "kanji O", "kanji Ten", "kanji Mon" };

    private static string SpeakKanji(string s)
    {
        int lang = Native.Text.GameText.LanguageId;
        if (lang != 1 && (lang < 5 || lang > 8)) return s;
        for (int i = 0; i < KanjiChars.Length; i++)
            s = s.Replace(KanjiChars[i].ToString(), KanjiWords[i]);
        return s;
    }

    /// <summary>Speak a line AND record it to history. Drop-in for the old <c>Tolk.Output</c>.</summary>
    internal static void Say(string text, bool interrupt = true,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
    {
        // Speech-source tag (2026-08-31, the "random numbers" hunt): every spoken line is
        // logged with the component that spoke it, so a player's log identifies the culprit
        // of any speech spam. CallerFilePath is a COMPILE-TIME constant — zero runtime cost;
        // the substring below runs once per spoken sentence (a few per second at most).
        if (!string.IsNullOrWhiteSpace(text))
        {
            int cut = callerFile.LastIndexOfAny(new[] { '\\', '/' });
            string src = cut >= 0 ? callerFile.Substring(cut + 1) : callerFile;
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
        try { SayCore(text, interrupt); }
        finally { Components.PerfDiag.End(Components.PerfDiag.B.SpeechSay, t0); }
    }

    private static void SayCore(string text, bool interrupt = true)
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
        Record(text);
        Tolk.Output(text, interrupt);
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
            if (_hist.Count == 0) { Tolk.Output("No history.", true); return; }
            _navIdx = _hist.Count;
            Tolk.Output(_hist[^1], true);
        }
    }

    /// <summary>Browse history. dir = -1 older, +1 newer. Speaks the landed-on line.</summary>
    internal static void Step(int dir)
    {
        lock (_lock)
        {
            if (_hist.Count == 0) { Tolk.Output("No history.", true); return; }
            if (_navIdx < 0 || _navIdx > _hist.Count - 1) _navIdx = _hist.Count;  // start from newest
            int next = _navIdx + dir;
            if (next < 0) { _navIdx = 0; Tolk.Output("Start of history. " + _hist[0], true); return; }
            if (next > _hist.Count - 1) { _navIdx = _hist.Count - 1; Tolk.Output("Newest. " + _hist[^1], true); return; }
            _navIdx = next;
            Tolk.Output(_hist[_navIdx], true);
        }
    }
}
