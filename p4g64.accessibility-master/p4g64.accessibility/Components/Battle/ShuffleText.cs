using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Shuffle Time INFO-PANEL text reader (v1.3.5 — user's idea 2026-07-03, log-verified).
/// The game renders the hovered card's identity as TEXT through the shared UI-text fn
/// FUN_140450C60 (the QuestMenu/SocialLinkDetail pattern):
///   • param_6 == 0 full strings — the panel CATEGORY footer (all-caps: "ARCANA", "WAND",
///     "CUP", "PERSONA"…) and the TITLE line(s) ("Strength", "Cup"; persona cards give
///     THREE: "Ara Mitama", "Chariot", "Lv18").
///   • param_6 != 0 glyph runs into three persistent line objects — the DESCRIPTION
///     ("Partial HP/SP / recovery for the / entire party").
/// This is game-truth identity that exists even on a SKIPPED deal (which never writes the
/// texture paths ShuffleReader names cards from) and after any change card. Reconstruction
/// uses ~120ms windows (several frames; the panel redraws every frame, so a window holds
/// the complete de-duplicated set): a window's composed text replaces
/// <see cref="LatestPanel"/> only when it CHANGES, so ShuffleReader can wait for a stable
/// post-hover value and never speak a mid-transition merge.
/// </summary>
internal sealed unsafe class ShuffleText
{
    // FUN_140450C60 — hardcoded VA (ASLR off); prologue not unique, no SigScan.
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60);

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(
        nint param_1, byte param_2, byte param_3, uint param_4, byte param_5, nint param_6);

    /// <summary>Composed "Title[, …]. Description" of the card the game's info panel
    /// currently shows, or null. Written by the hook thread, read by ShuffleReader.</summary>
    internal static volatile string? LatestPanel;
    /// <summary>TickCount64 when LatestPanel last CHANGED.</summary>
    internal static long LatestChangedTick;
    /// <summary>TickCount64 of the last compose (panel rendering at all).</summary>
    internal static long LatestSeenTick;

    // PER-FRAME reconstruction (hook thread only). Each render frame draws the panel in
    // strict order: p6=0 full strings (category footer + title lines) THEN the description
    // glyph lines. A fixed-window collector scrambled that order at window cuts (log
    // 2026-07-03: "Persona level 20, Andra, Moon" flip-flopping, desc lines rotated) — so
    // we rebuild frame-by-frame instead: fulls arriving AFTER glyph lines belong to the
    // NEXT frame; the old frame closes (and composes) when the next frame's first glyph
    // line begins, which is also the moment the old frame's last line completes.
    private nint _lineObj = -1;
    private readonly System.Text.StringBuilder _line = new();
    private System.Collections.Generic.List<string> _fulls = new();
    private System.Collections.Generic.List<string> _pendingFulls = new();
    private readonly System.Collections.Generic.List<string> _glyphs = new();
    private bool _wasActive;

    internal ShuffleText(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnSetText, SetUiTextVA).Activate();
        Log("[ShuffleText] info-panel hook active on FUN_140450C60");
    }

    private nint OnSetText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try { Capture(p1, p6); } catch { /* never let a hook throw */ }
        return ret;
    }

    private void Capture(nint strPtr, nint textObj)
    {
        // FAST GATE — volatile reads only. Gated on the battle-UI STATE, not the struct
        // latch: a SKIPPED deal writes neither texture paths nor the panel map, so the
        // struct may never latch — the panel text must carry the reading alone then.
        if (!ShuffleReader.ShuffleStateActive && !ShuffleReader.ShuffleActive)
        {
            if (_wasActive)
            {
                _wasActive = false;
                _lineObj = -1;
                _line.Clear();
                _fulls.Clear();
                _pendingFulls.Clear();
                _glyphs.Clear();
                LatestPanel = null;
            }
            return;
        }
        _wasActive = true;

        if (textObj == 0)
        {
            string full = ReadCString(strPtr, 96).Trim();
            if (full.Length < 2) return;
            // Fulls arriving after this frame's glyph lines = the NEXT frame's header.
            if (_glyphs.Count > 0 || _line.Length > 0)
            {
                // If the PENDING header itself starts repeating, the glyph frame is
                // long over and a GLYPH-LESS frame follows it (suit → persona hover):
                // close the old frame, then the completed pending one.
                if (_pendingFulls.Contains(full) || (IsAllCaps(full) && _pendingFulls.Exists(IsAllCaps)))
                {
                    Compose();                                   // the finished glyph frame
                    _fulls = _pendingFulls;
                    _pendingFulls = new System.Collections.Generic.List<string>();
                    _glyphs.Clear();
                    _line.Clear();
                    _lineObj = -1;
                    Compose();                                   // the finished glyph-less frame
                    _fulls.Clear();
                }
                _pendingFulls.Add(full);
                return;
            }
            // GLYPH-LESS frames (persona cards draw title lines but NO description
            // glyphs — user 2026-07-04: persona cards silent when spam-skipping) never
            // hit the glyph boundary below, so close on TEXT evidence instead: the
            // same string re-arriving, or a second CATEGORY word (all-caps, drawn
            // first each frame), means a new frame began.
            bool isCat = IsAllCaps(full);
            if (_fulls.Contains(full) || (isCat && _fulls.Exists(IsAllCaps)))
            {
                Compose();
                _fulls.Clear();
            }
            _fulls.Add(full);
            return;
        }

        if (textObj != _lineObj)
        {
            FlushLine();
            // The next frame's first glyph line is starting AND the old frame's last
            // line just completed — close and compose the finished frame.
            if (_pendingFulls.Count > 0)
            {
                Compose();
                _fulls = _pendingFulls;
                _pendingFulls = new System.Collections.Generic.List<string>();
                _glyphs.Clear();
            }
            _lineObj = textObj;
        }
        string g = ReadCString(strPtr, 16);
        _line.Append(g.Length > 0 ? g : " ");
        if (_line.Length > 200) _line.Clear();   // runaway guard
    }

    private void FlushLine()
    {
        if (_line.Length == 0) return;
        string s = _line.ToString().Trim();
        _line.Clear();
        if (s.Length >= 2) _glyphs.Add(s);
    }

    private static bool IsAllCaps(string t)
    {
        bool hasLetter = false;
        foreach (char c in t)
        {
            if (char.IsLetter(c)) { hasLetter = true; if (char.IsLower(c)) return false; }
        }
        return hasLetter;
    }

    private void Compose()
    {
        long now = Environment.TickCount64;
        LatestSeenTick = now;
        if (_fulls.Count == 0 && _glyphs.Count == 0) return;

        string? category = null;
        var titles = new System.Collections.Generic.List<string>();
        foreach (var t in _fulls)
        {
            bool hasLetter = false, allUpper = true;
            foreach (char c in t)
            {
                if (char.IsLetter(c)) { hasLetter = true; if (char.IsLower(c)) allUpper = false; }
            }
            if (hasLetter && allUpper) category ??= t;   // "ARCANA"/"WAND"/"CUP"/"PERSONA" footer
            else if (!titles.Contains(t)) titles.Add(t); // arrival order = true render order
        }
        if (titles.Count == 0 && _glyphs.Count == 0) return;
        // A REAL card panel always carries one of exactly these category footers. The
        // battle-UI state that gates this capture (17) also covers the LEVEL-UP / drop
        // screens, which draw through the same text fn — without this whitelist the
        // no-struct announcer narrated the whole level-up ("Chie, Suzuka Gongen,
        // Bufula…", "Ag has increased by", drop items) in a spam loop (user 2026-07-04).
        if (category is not ("ARCANA" or "WAND" or "SWORD" or "CUP" or "COIN" or "PERSONA"))
            return;

        var sb = new System.Text.StringBuilder();
        if (category == "PERSONA") sb.Append("Persona ");
        for (int i = 0; i < titles.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            // "Lv18" → "level 18" (persona card third title line)
            string t = titles[i];
            if (t.StartsWith("Lv") && t.Length > 2 && char.IsDigit(t[2]))
                t = "level " + t[2..];
            sb.Append(t);
        }
        if (titles.Count > 0 && category != null && category != "PERSONA" &&
            !string.Equals(titles[0], category, StringComparison.OrdinalIgnoreCase))
            sb.Append(" card");        // "Strength card", "Priestess card" (arcana)
        if (_glyphs.Count > 0)
        {
            if (sb.Length > 0) sb.Append(". ");
            sb.Append(string.Join(" ", _glyphs));
        }
        string composed = sb.ToString();
        if (composed.Length == 0 || composed == LatestPanel) return;
        LatestPanel = composed;
        LatestChangedTick = now;
        Log($"[ShuffleText] panel: {composed}");

        // NO-STRUCT fallback announcer (2026-07-04): when the struct never latched (a
        // fully-skipped deal has no paths AND no panel map), the panel text itself is
        // the whole reading — speak each new card as the player moves. When a struct
        // IS latched, ShuffleReader's hover announce owns the speech (adds position,
        // rank, tries) and this stays silent.
        if (!ShuffleReader.ShuffleActive && GameHasFocus())
        {
            Log($"[ShuffleText] announce (no struct): {composed}");
            Speech.Say(composed, interrupt: true);
        }
    }

    // Guarded ASCII read (the QuestMenu helper — glyph strings are 1-2 chars, panel
    // strings longer; non-ASCII glyph bytes are skipped, callers treat "" as a space).
    private static string ReadCString(nint p, int maxLen)
    {
        if (p == 0) return "";
        ulong a = (ulong)p;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return "";
        byte* qbuf = stackalloc byte[48];
        if (VirtualQuery(p, qbuf, 48) == 0) return "";
        if (*(uint*)(qbuf + 32) != 0x1000) return "";
        uint protect = *(uint*)(qbuf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return "";
        nint regionBase = *(nint*)(qbuf + 0);
        nint regionSize = *(nint*)(qbuf + 24);
        ulong end = (ulong)regionBase + (ulong)regionSize;
        int safe = (int)System.Math.Min((ulong)maxLen, end - a);
        var sb = new System.Text.StringBuilder(maxLen);
        for (int i = 0; i < safe; i++)
        {
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);
}
