using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Config-menu LIVE VALUES from the render stream (2026-07-10 — the "Angle 1" win of
/// memory/next_session_config_values.md). The old approach — reading the value out of
/// the menu working struct — was a proven dead end: the buffer pool-recycles and the
/// field freezes mid-session (memory/config_menu_status.md). This reader instead hooks
/// the shared UI-text renderer FUN_140450C60 (the ShuffleText/CompendiumInfoText/Quest
/// pattern) and parses what the game DRAWS, which is live by construction (it tracks
/// un-confirmed left/right previews, multi-choice included — log-proven).
///
/// Render stream shape (p6==0 full strings; the menu renders DIRTY — a full redraw on
/// open/scroll/change, NOT every frame):
///   "F", "&lt;hovered row's description&gt;", then per visible row LABEL followed by its
///   VALUE ("BGM","5","SE","5","Voiced Line","ON","Audio Language","English",…), then
///   the button legend "C","X","Q","E". Parsing rule: a string that matches a known
///   setting label arms it; the NEXT string is its value — unless it is itself a label
///   (row with no drawn value) or a single-letter legend token. Because rendering is
///   dirty, the map PERSISTS across quiet periods and menu visits (a redraw always
///   precedes any read that matters); every value change is redrawn, so it is caught.
///
/// Publishes <see cref="Lookup"/> for <see cref="ConfigMenu"/> ("name, value" on row
/// change) and PUSHES <see cref="ConfigMenu.OnValueDrawn"/> when a mapped value
/// changes — that push is the ONLY voice for in-place left/right changes, because
/// none of the menu's input hooks fire for them (log-proven 2026-07-10).
/// </summary>
internal sealed unsafe class ConfigValueText
{
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);

    private static readonly object MapLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, string> Values = new();

    // Every setting label across all tabs — a drawn string matching one of these arms
    // the "next string is its value" rule. Built from ConfigMenu.TabItems (which is
    // sourced from the game's own init_free.bin label table, so it matches the drawn
    // text exactly).
    private static readonly System.Collections.Generic.HashSet<string> KnownLabels = BuildLabelSet();

    // Single-letter button-legend glyphs drawn after the rows each frame ("F" leads
    // the frame, "C"/"X"/"Q"/"E" trail it). Never a value.
    private static bool IsLegendToken(string s) =>
        s.Length == 1 && (s == "F" || s == "C" || s == "X" || s == "Q" || s == "E");

    private string? _pendingLabel;
    private long _pendingTick;

    internal ConfigValueText(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnText, SetUiTextVA).Activate();
        Log("[CfgVal] config value reader active on FUN_140450C60");
    }

    /// <summary>Current drawn value for a setting label, or null if not seen.</summary>
    internal static string? Lookup(string label)
    {
        lock (MapLock)
            return Values.TryGetValue(label, out var v) ? v : null;
    }

    private static System.Collections.Generic.HashSet<string> BuildLabelSet()
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var tab in ConfigMenu.TabItems)
            foreach (var name in tab)
                set.Add(name);
        return set;
    }

    private nint OnText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try { Capture(p1, p6); } catch { /* never let a hook throw */ }
        return ret;
    }

    private void Capture(nint strPtr, nint p6)
    {
        // UNGATED by design (2026-07-10): no hook fires while the menu sits idle,
        // so any activity-based gate starves the capture exactly when the user
        // presses left/right after a pause. Contamination from other screens is
        // contained by (a) exact-match arming on config-only label strings,
        // (b) the same-render-burst pairing window, (c) the value length cap,
        // and (d) the map self-correcting on the menu-open full redraw.
        // Do NOT clear the value map on idle: the menu renders dirty (strings
        // draw on change, not per frame) — a cleared map stays empty until the
        // next redraw and first-hover lookups miss (user-hit 2026-07-10).
        if (p6 != 0) return; // glyph-stream bodies (popups etc.) — labels/values are all p6==0

        string s = ReadCStr(strPtr, 96).Trim();
        if (s.Length == 0) return;
        long now = Environment.TickCount64;

        if (KnownLabels.Contains(s))
        {
            // A label directly after an armed label = the previous row drew no value.
            _pendingLabel = s;
            _pendingTick  = now;
            return;
        }

        if (_pendingLabel == null) return;      // descriptions / scratch between frames
        string label = _pendingLabel;
        _pendingLabel = null;
        if (now - _pendingTick > 250) return;   // stale arm from an earlier burst — not this row's value
        if (IsLegendToken(s)) return;           // row was last before the button legend
        if (s.Length > 32) return;              // values are short — long text is scratch/descriptions

        lock (MapLock)
        {
            if (Values.TryGetValue(label, out var old) && old == s) return;
            Values[label] = s;
        }
        Log($"[CfgVal] {label} = \"{s}\"");
        // PUSH: if the cursor is sitting on this row, speak the new value now —
        // in-place left/right changes fire no input hook, this is their only voice.
        ConfigMenu.OnValueDrawn(label, s);
    }

    // Guarded ASCII read (the QuestMenu/UiTextSpy helper).
    private static string ReadCStr(nint p, int maxLen)
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

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);
}
