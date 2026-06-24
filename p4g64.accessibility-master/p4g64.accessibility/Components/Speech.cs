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

    /// <summary>Speak a line AND record it to history. Drop-in for the old <c>Tolk.Output</c>.</summary>
    internal static void Say(string text, bool interrupt = true)
    {
        Record(text);
        Tolk.Output(text, interrupt);
    }

    /// <summary>Record a line to history WITHOUT speaking it (e.g. dialogue while auto-read is off).</summary>
    internal static void Record(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
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
