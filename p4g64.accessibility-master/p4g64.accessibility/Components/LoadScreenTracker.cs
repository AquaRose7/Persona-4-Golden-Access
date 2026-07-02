using System.Runtime.InteropServices;
using System.Text;
using DavyKager;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the instruction that writes the save/load screen cursor and announces the slot.
///
/// HOW IT WORKS:
///   Cheat Engine "what writes to cursor address" found one instruction:
///     0x1403E4C90: mov [rbx+00003536], cx      bytes: 66 89 8B 36 35 00 00
///   cx = new cursor value (0-indexed slot number).
///   We inject BEFORE that instruction (ExecuteFirst), capture cx into pinned
///   memory, and a poll thread announces any change.
///
///   If no write fires for 2 seconds we assume the screen is closed and reset
///   so the first slot is re-announced next time the screen opens.
/// </summary>
internal unsafe class LoadScreenTracker : IDisposable
{
    // mov [rbx+00003536], cx
    // Extended from 7 to 14 bytes via Ghidra (game 0x1403E4C90, unique match confirmed).
    // Context: 0F B7 4D F8 | 66 89 8B 36 35 00 00 48 8B CB E8 ...
    // 0F B7 4D ?? = MOVZX ecx,[rbp+x]  (wildcard: stack slot may shift)
    // 66 89 8B 36 35 00 00 = MOV [rbx+3536], cx
    // 48 8B CB = MOV rcx, rbx
    private const string CursorWritePattern = "0F B7 4D ?? 66 89 8B 36 35 00 00 48 8B CB";

    private readonly string? _savePath;
    private IAsmHook? _cursorHook;

    // Pinned unmanaged block: [0..1] = cursor short, [4..7] = write-flag int
    private byte* _shared;
    private short* _cursorSlot => (short*)_shared;
    private int*   _writeFlag  => (int*)(_shared + 4);

    private short _lastAnnounced = -1;
    private int   _idlePolls     = 0;
    private const int IdleResetThreshold = 40; // 40 × 50 ms = 2 s

    private bool _running = true;
    private readonly Thread _pollThread;

    internal LoadScreenTracker(IReloadedHooks hooks)
    {
        _savePath = FindSavePath();
        if (_savePath != null)
            Log($"[LoadScreenTracker] Save folder: {_savePath}");
        else
            Log("[LoadScreenTracker] WARNING: save folder not found");

        _shared = (byte*)Marshal.AllocHGlobal(8);
        *_cursorSlot = -1;
        *_writeFlag  =  0;

        ulong slotAddr = (ulong)_cursorSlot;
        ulong flagAddr = (ulong)_writeFlag;
        Log($"[LoadScreenTracker] Shared block: cursor=0x{slotAddr:X} flag=0x{flagAddr:X}");

        SigScan(CursorWritePattern, "SaveScreen::CursorWrite", address =>
        {
            Log($"[LoadScreenTracker] Cursor-write instruction found at 0x{address:X}");

            // Inject before "mov [rbx+3536], cx":
            //   capture cx (new cursor index) into _cursorSlot
            //   set _writeFlag = 1 so the poll loop knows a write happened
            var asm = new[]
            {
                "use64",
                "push rax",
                "push rbx",
                "movzx eax, cx",
                $"mov rbx, 0x{slotAddr:X16}",
                "mov word [rbx], ax",
                $"mov rbx, 0x{flagAddr:X16}",
                "mov dword [rbx], 1",
                "pop rbx",
                "pop rax"
            };

            // +4 to skip the 0F B7 4D ?? prefix and land on the actual MOV [rbx+3536], cx
            _cursorHook = hooks.CreateAsmHook(asm, address + 4, AsmHookBehaviour.ExecuteFirst)
                               .Activate();
        });

        _pollThread = new Thread(Poll) { IsBackground = true, Name = "LoadScreenTracker" };
        _pollThread.Start();
    }

    // ── Poll loop ─────────────────────────────────────────────────────────

    private void Poll()
    {
        while (_running)
        {
            Thread.Sleep(50);
            try   { CheckCursor(); }
            catch (Exception ex) { Log($"[LoadScreenTracker] Poll error: {ex.Message}"); }
        }
    }

    private void CheckCursor()
    {
        var fired = *_writeFlag;
        *_writeFlag = 0;

        if (fired == 0)
        {
            // No cursor write this tick — count idle time
            if (++_idlePolls >= IdleResetThreshold)
            {
                // Screen has been closed long enough; reset so next open re-announces
                _lastAnnounced = -1;
                _idlePolls     =  0;
            }
            return;
        }

        _idlePolls = 0;

        var cursor = *_cursorSlot;
        if (cursor < 0 || cursor > 15) return;
        if (cursor == _lastAnnounced)  return;

        _lastAnnounced = cursor;
        var slotNum = cursor + 1;   // 0-indexed → 1-indexed
        var desc    = GetSlotDescription(slotNum);
        var msg     = $"Save slot {slotNum}: {desc}";

        Log($"[LoadScreenTracker] {msg}");
        Speech.Say(msg, true);
    }

    // ── Slot description ──────────────────────────────────────────────────

    private string GetSlotDescription(int slotNum)
    {
        if (_savePath == null) return "unknown";
        var path = Path.Combine(_savePath, $"data{slotNum:D4}.binslot");
        if (!File.Exists(path)) return "empty";
        return ParseSlot(path) ?? "unreadable";
    }

    private static string? ParseSlot(string path)
    {
        try
        {
            var text    = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            var dateIdx = text.IndexOf("Date:", StringComparison.Ordinal);
            if (dateIdx < 0) return null;

            var section = text.Substring(dateIdx);
            var nullIdx = section.IndexOf('\0');
            if (nullIdx >= 0) section = section.Substring(0, nullIdx);

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in section.Split('\n'))
            {
                var sep = line.IndexOf(':');
                if (sep < 0) continue;
                var key   = line.Substring(0, sep).Trim();
                var value = line.Substring(sep + 1).Trim();
                if (key.Length > 0) fields[key] = value;
            }

            var dateStr = ""; var timeStr = "";
            if (fields.TryGetValue("Date", out var dateRaw))
            {
                var p = dateRaw.Split(':');
                dateStr = p.Length > 0 ? FormatDate(p[0]) : "";
                timeStr = p.Length > 1 ? p[1] : "";
            }

            var nameStr = ""; var levelStr = "";
            if (fields.TryGetValue("Name", out var nameRaw))
            {
                var ascii = FullwidthToAscii(nameRaw);
                var lv    = ascii.IndexOf("Lv:", StringComparison.OrdinalIgnoreCase);
                if (lv >= 0) { nameStr = ascii.Substring(0, lv).Trim(); levelStr = "Level " + ascii.Substring(lv + 3).Trim(); }
                else           { nameStr = ascii.Trim(); }
            }

            var difficulty = fields.TryGetValue("Difficulty", out var diff) ? TitleCase(diff.Trim()) : "";
            var location = fields.TryGetValue("Location", out var loc) ? loc : "";
            var playTime = fields.TryGetValue("Play Time", out var pt)  ? FormatPlayTime(pt) : "";

            var parts = new List<string>();
            if (dateStr.Length    > 0) parts.Add(dateStr);
            if (timeStr.Length    > 0) parts.Add(timeStr);
            if (nameStr.Length    > 0) parts.Add(nameStr);
            if (levelStr.Length   > 0) parts.Add(levelStr);
            if (difficulty.Length > 0) parts.Add(difficulty);
            if (location.Length   > 0) parts.Add(location);
            if (playTime.Length   > 0) parts.Add(playTime);

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch (Exception ex)
        {
            Log($"[LoadScreenTracker] Parse error: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // "NORMAL" → "Normal", "VERY HARD" → "Very Hard" (the save file stores the
    // difficulty preset name in upper case).
    private static string TitleCase(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1).ToLowerInvariant();
        return string.Join(" ", words);
    }

    private static string FullwidthToAscii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c);
        return sb.ToString();
    }

    private static string FormatDate(string mmdd)
    {
        var p = mmdd.Split('/');
        if (p.Length != 2 || !int.TryParse(p[0], out var m) || !int.TryParse(p[1], out var d) || m < 1 || m > 12)
            return mmdd;
        string[] months = { "January","February","March","April","May","June","July","August","September","October","November","December" };
        return $"{months[m - 1]} {d}";
    }

    private static string FormatPlayTime(string raw)
    {
        var p = raw.Split(':');
        if (p.Length < 3 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)) return raw;
        if (h > 0 && m > 0) return $"{h} hour{(h != 1 ? "s" : "")} {m} minute{(m != 1 ? "s" : "")}";
        if (h > 0)           return $"{h} hour{(h != 1 ? "s" : "")}";
        if (m > 0)           return $"{m} minute{(m != 1 ? "s" : "")}";
        return "less than a minute";
    }

    // ── Save path ─────────────────────────────────────────────────────────

    private static string? FindSavePath()
    {
        // Steam's INSTALL dir (userdata lives there, not in the game library). The old
        // hardcoded default broke every non-default install ("Save slot 1: unknown"), so:
        // registry first (covers any drive/path), default path as last resort.
        var roots = new List<string>();
        try
        {
            if (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                    ?.GetValue("SteamPath") is string sp && sp.Length > 0)
                roots.Add(sp.Replace('/', '\\'));
        }
        catch { }
        try
        {
            if (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    ?.GetValue("InstallPath") is string ip && ip.Length > 0)
                roots.Add(ip);
        }
        catch { }
        roots.Add(@"C:\Program Files (x86)\Steam");

        foreach (var root in roots)
        {
            string userdata;
            try { userdata = Path.Combine(root, "userdata"); if (!Directory.Exists(userdata)) continue; }
            catch { continue; }
            // Several Steam accounts can have a P4G folder — pick the most recently written.
            string? best = null; DateTime bestTime = DateTime.MinValue;
            foreach (var dir in Directory.GetDirectories(userdata))
            {
                var candidate = Path.Combine(dir, "1113000", "remote");
                if (!Directory.Exists(candidate)) continue;
                var t = Directory.GetLastWriteTimeUtc(candidate);
                if (best == null || t > bestTime) { best = candidate; bestTime = t; }
            }
            if (best != null) return best;
        }
        Log("[LoadScreenTracker] save folder NOT FOUND (no Steam userdata/1113000) — slots will read 'unknown'");
        return null;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _running = false;
        _cursorHook?.Disable();
        if (_shared != null)
        {
            Marshal.FreeHGlobal((IntPtr)_shared);
            _shared = null;
        }
    }
}
