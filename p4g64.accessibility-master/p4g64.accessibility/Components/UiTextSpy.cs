using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Dev recon tool (Debug builds only): F9 arms a ~20s capture window that logs
/// EVERYTHING the shared UI-text renderer FUN_140450C60 draws, WITH its p4 COLOR
/// (2026-07-11 upgrade for the battle hunts: the tactics member cursor exists only
/// as a render-state color flip — dim 0x2D2D2DFF vs selected 0xFFF000FF — so the
/// color per string is the payload). Dedupe = (color|text) pair, so a highlight
/// move re-logs the same text in its new color. On arm it also dumps the
/// NAMED-TASK REGISTRY once (does the Q quick-analyze / tactics screen run as a
/// task?). F9 is gated on Battle.InBattle so it can't collide with the room-scout
/// Ctrl+F9. Log prefix [TextSpy].
/// </summary>
internal sealed unsafe class UiTextSpy
{
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);
    private const int VK_F9 = 0x78;

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);

    private long _until;
    private bool _keyWas;
    private bool _raw;
    private long _nextTaskTick;
    private static string _lastTaskDump = "";
    private nint _lineObj = -1;
    private uint _lineP4;
    private readonly System.Text.StringBuilder _line = new();
    private readonly System.Collections.Generic.HashSet<string> _seen = new();

    internal UiTextSpy(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnText, SetUiTextVA).Activate();
        var t = new Thread(Poll) { IsBackground = true, Name = "UiTextSpy" };
        t.Start();
        Log("[TextSpy] ready (F9 in battle = 20s color capture of all UI text)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(80);
            try
            {
                bool k = (GetAsyncKeyState(VK_F9) & 0x8000) != 0;
                if (k && !_keyWas && GameHasFocus() && FieldTracker.InBattle)
                {
                    // Shift+F9 = RAW mode (8s, NO dedupe): logs every draw so the
                    // REDRAW PATTERN shows — if a cursor move redraws only the
                    // highlighted row, the last-drawn text IS the cursor.
                    _raw = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    _until = Environment.TickCount64 + (_raw ? 8000 : 40000);
                    _seen.Clear();
                    _lastTaskDump = "";
                    Speech.Say(_raw ? "Raw text spy on." : "Text spy on.", true);
                    Log($"[TextSpy] ── capture START{(_raw ? " (RAW)" : "")} ──");
                    if (!_raw) DumpTasks();
                }
                _keyWas = k;

                // While armed (normal mode), re-dump the task registry every ~8s ON
                // CHANGE — screens that appear mid-window (e.g. the GAME OVER flow)
                // show up as new task names between dumps.
                if (!_raw && Environment.TickCount64 < _until
                          && Environment.TickCount64 >= _nextTaskTick)
                {
                    _nextTaskTick = Environment.TickCount64 + 8000;
                    DumpTasks();
                }
            }
            catch { }
        }
    }

    private nint OnText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try
        {
            if (Environment.TickCount64 >= _until) return ret;
            if (p6 == 0)
            {
                string s = ReadCStr(p1, 96).Trim();
                // dedupe on (color|text): a cursor move re-draws the same text
                // in its highlight color — that re-log IS the finding. RAW mode
                // logs every draw (redraw-pattern hunting).
                if (s.Length > 0 && (_raw || _seen.Add($"{p4:X8}|{s}")))
                    Log($"[TextSpy] full p4={p4:X8} \"{s}\"");
            }
            else
            {
                if (p6 != _lineObj) { FlushLine(); _lineObj = p6; _lineP4 = p4; }
                string g = ReadCStr(p1, 16);
                _line.Append(g.Length > 0 ? g : " ");
                if (_line.Length > 160) FlushLine();
            }
        }
        catch { /* never let a hook throw */ }
        return ret;
    }

    private void FlushLine()
    {
        if (_line.Length == 0) return;
        string s = _line.ToString().Trim();
        _line.Clear();
        if (s.Length > 1 && _seen.Add($"L{_lineP4:X8}|{s}"))
            Log($"[TextSpy] line p4={_lineP4:X8} obj=0x{_lineObj:X} \"{s}\"");
    }

    // One-shot named-task registry dump on arm (the TvListings anchor: heads
    // 0x1462486F8/0x1462486A8/0x146248768, name @+0x00, next @+0x50).
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };

    private static void DumpTasks()
    {
        try
        {
            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (nint head in TaskHeads)
            {
                if (!IsReadableStatic(head, 8)) continue;
                nint n = *(nint*)head;
                for (int i = 0; i < 256 && n != 0; i++)
                {
                    if (!IsReadableStatic(n, 0x58)) break;
                    int len = 0;
                    while (len < 0x40 && *(byte*)(n + len) >= 0x20 && *(byte*)(n + len) < 0x7F) len++;
                    if (len >= 2 && len < 0x40 && *(byte*)(n + len) == 0)
                        names.Add(System.Text.Encoding.ASCII.GetString((byte*)n, len));
                    n = *(nint*)(n + 0x50);
                }
            }
            string dump = string.Join(", ", names);
            if (dump.Length > 700) dump = dump[..700] + "…";
            if (dump == _lastTaskDump) return;   // periodic re-dumps log on CHANGE only
            _lastTaskDump = dump;
            Log($"[TextSpy] tasks({names.Count}): {dump}");
        }
        catch { }
    }

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

    private static bool IsReadableStatic(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint protect = *(uint*)(buf + 36);
        return (protect & 0x101) == 0;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);
}
