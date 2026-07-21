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
    private static readonly Dictionary<string, long> _recentSaid = new();

    // The game's text separates words with the Japanese IDEOGRAPHIC SPACE (U+3000),
    // not an ASCII space — some screen readers stumble on it (words run together /
    // odd pauses). Swap it for a normal space so EVERY announcement reads cleanly.
    // Applied centrally (all readers route through Say/Record) so nothing is missed.
    // The IndexOf guard avoids allocating when there's no full-width space (the norm).
    // Verified 2026-07-09: "Custom　Sub　Menu" → "Custom Sub Menu".
    private static string Normalize(string s)
        => s.IndexOf('　') >= 0 ? s.Replace('　', ' ') : s;

    /// <summary>Speak a line AND record it to history. Drop-in for the old <c>Tolk.Output</c>.</summary>
    internal static void Say(string text, bool interrupt = true)
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
