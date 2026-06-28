using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the phone CALENDAR app (the year/month grid; "View what day's schedule?").
/// Hooks the calendar render FUN_1401550F0(_, struct) and announces the selected
/// day as the cursor moves. The struct is the 2nd argument (rdx → rdi):
///   +0x20 int  displayed month (1-12)
///   +0x24 int  cursor day (the selected day)
///   +0x28 int  today's month
///   +0x2c int  today's day
/// Found 2026-06-28: snapshot-diff (cursor day 20→21→22) located the struct, CE
/// "find what accesses" gave the [rdi+24] day read, and Ghidra confirmed the
/// render + its TODAY/cursor-highlight logic (drawDay==[+0x24] = cursor; month
/// [+0x20]==[+0x28] && drawDay==[+0x2c] = today). No pointer chain needed — the
/// struct arrives as the render argument every frame.
/// </summary>
internal unsafe class CalendarReader : IDisposable
{
    private static readonly string[] Months =
        { "January", "February", "March", "April", "May", "June", "July",
          "August", "September", "October", "November", "December" };

    private static readonly string[] Weekdays =
        { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    // Full per-day weather schedule (community sheet, validated vs the live seg+0x06 read AND the TV
    // forecast). Keyed "month-day" (e.g. "5-19") -> name ("Sunny" / "Rainy, then Cloudy"). The static
    // weather_calendar.json was the WRONG data; this replaces it for arbitrary-day weather.
    private static Dictionary<string, string>? _weather;

    private IHook<RenderDelegate>? _hook;
    private readonly WeekdayDelegate? _weekdayFn;   // game's day-of-week function FUN_14013bc80(month, day)
    private nint _lastStruct;
    private int  _lastDay = -1, _lastMonth = -1;

    private delegate void RenderDelegate(nint p1, nint structPtr);
    private delegate int  WeekdayDelegate(int month, int day);

    internal CalendarReader(IReloadedHooks hooks)
    {
        // The render computes each day's weekday via FUN_14013bc80(month, day); reuse it so the
        // weekday is correct for ANY displayed month/year without us tracking the year.
        try { _weekdayFn = hooks.CreateWrapper<WeekdayDelegate>(unchecked((nint)0x14013BC80L), out _); }
        catch (Exception e) { _weekdayFn = null; Log($"[Calendar] weekday wrapper failed: {e.Message}"); }
        Log($"[Calendar] weekday fn {(_weekdayFn == null ? "NULL" : "ready")}");

        SigScan(
            "40 55 57 48 8D 6C 24 A8 48 81 EC 58 01 00 00 48 8D 0D C8 A9 35 1E 48 8B FA",
            "Calendar::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Calendar reader hook active.");
            });
    }

    private void OnRender(nint p1, nint s)
    {
        _hook!.OriginalFunction(p1, s);
        try { Read(s); } catch { /* never let a hook throw */ }
    }

    private void Read(nint s)
    {
        if (!GameHasFocus()) return;
        if (!IsReadable(s + 0x2c)) return;

        int dispMonth  = *(int*)(s + 0x20);
        int cursorDay  = *(int*)(s + 0x24);
        int todayMonth = *(int*)(s + 0x28);
        int todayDay   = *(int*)(s + 0x2c);
        if (cursorDay < 1 || cursorDay > 31 || dispMonth < 1 || dispMonth > 9999) return;

        // New calendar instance -> re-announce.
        if (s != _lastStruct) { _lastStruct = s; _lastDay = -1; _lastMonth = -1; }
        if (cursorDay == _lastDay && dispMonth == _lastMonth) return;
        bool monthChanged = dispMonth != _lastMonth;
        _lastDay = cursorDay; _lastMonth = dispMonth;

        int mi = ((dispMonth - 1) % 12 + 12) % 12;   // 0-11, robust to an absolute-month encoding
        string msg = $"{Months[mi]} {cursorDay}";
        // Announce the YEAR when the displayed month changes (Q/E) so the year is clear without
        // repeating it every day. P4G runs Apr 2011 – Mar 2012: months 4-12 = 2011, months 1-3 = 2012.
        if (monthChanged) msg += $", {(mi + 1 >= 4 ? 2011 : 2012)}";

        // Weekday via the game's own day-of-week fn (render maps it Sun=0 with (raw%7+5)%7).
        if (_weekdayFn != null)
        {
            try
            {
                int wd = (((_weekdayFn(dispMonth, cursorDay) % 7) + 5) % 7 + 7) % 7;
                msg += $", {Weekdays[wd]}";
            }
            catch (Exception e) { Log($"[Calendar] weekday call failed: {e.Message}"); }
        }

        // Per-day weather from the validated full schedule (any date, with morning/afternoon splits).
        if (_weather == null) LoadWeather();
        if (_weather != null && _weather.TryGetValue($"{mi + 1}-{cursorDay}", out var w) && w.Length > 0)
            msg += $", {w}";

        if (dispMonth == todayMonth && cursorDay == todayDay) msg += ", today";

        // SELECTED day's schedule event NAME(s) — a null-terminated list of char* at struct+0xA0
        // (stride 8). Found live 2026-06-28: *(struct+0xA0) -> "Test Results" on 5/19, +0xA8 null.
        // Announce the real event text (e.g. "Test Results"); fall back to a generic "event" if the
        // per-day icon array (+0x30+day*2 != -1) marks a day but no detail text resolved.
        // ONLY read the event text when the cursor day actually has an event (per the per-day icon
        // array +0x30+day*2 != -1). +0xA0 is STALE on no-event days — the game leaves the last event
        // day's text there and just stops drawing it — so reading it unconditionally announced a
        // nearby day's event (e.g. "School Campout" repeated across empty days).
        var events = new List<string>();
        bool dayHasEvent = IsReadable(s + 0x30 + cursorDay * 2) && *(short*)(s + 0x30 + cursorDay * 2) != -1;
        if (dayHasEvent)
        {
            for (int i = 0; i < 8; i++)
            {
                if (!IsReadable(s + 0xA0 + i * 8)) break;
                nint ep = *(nint*)(s + 0xA0 + i * 8);
                if (ep == 0) break;
                string ev = ReadAscii(ep);
                if (ev.Length > 0) events.Add(ev);
            }
            msg += events.Count > 0 ? ", " + string.Join(", ", events) : ", event";
        }

        Speech.Say(msg, interrupt: true);
    }

    private static void LoadWeather()
    {
        _weather = new Dictionary<string, string>();
        try
        {
            string p = DataPath("weather_schedule.json");
            if (!System.IO.File.Exists(p)) { Log("[Calendar] weather_schedule.json not found"); return; }
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("schedule", out var s))
                foreach (var kv in s.EnumerateObject()) _weather[kv.Name] = kv.Value.GetString() ?? "";
            Log($"[Calendar] weather schedule loaded ({_weather.Count} days)");
        }
        catch (Exception e) { Log($"[Calendar] weather schedule load failed: {e.Message}"); }
    }

    // Read a short null-terminated ASCII string (the calendar resolves event names to plain ASCII).
    private static string ReadAscii(nint p)
    {
        if (!IsReadable(p)) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 64; i++)
        {
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 32 && b < 127) sb.Append((char)b);
            else if (b == '\n' || b == '\r') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nint VQ(nint a, byte* b, nint l);
    private static bool IsReadable(nint a)
    {
        if (a < 0x10000) return false;
        byte* buf = stackalloc byte[48];
        if (VQ(a, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint p = *(uint*)(buf + 36);
        return (p & 0x01) == 0 && (p & 0x100) == 0;
    }

    public void Dispose() { }
}
