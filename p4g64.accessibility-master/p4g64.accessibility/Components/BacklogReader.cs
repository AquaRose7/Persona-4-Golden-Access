using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Hooks.Definitions;
using p4g64.accessibility.Native.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// The DIALOGUE BACKLOG (X during a dialogue — scrollable history; Space replays a
/// voiced line, handled by the game). Reads the SELECTED line on each scroll.
/// Source of truth: database/BACKLOG_READER.md. RE 2026-07-20 (live RPM + Ghidra).
///
///  - Screen = the PERSISTENT task "itfBacklogDraw" (draw closure FUN_140459cf0, row
///    drawer FUN_140459780, run layout FUN_1681ed230). work (node+0x48):
///      +0x0C (u32) STATE — bit 0x4 set + count>0 = the live dialogue backlog is up.
///                  Other uses of the task (Social Link level-up 0x80000000, closing
///                  screens 0x10/0x80000010) DON'T have bit 0x4 — gate on it (no leak).
///      +0x1C (i32) animated scroll pos; +0x20 (i32) SELECTION TARGET (read this).
///  - count = *(i32)(*(obj+0x08)+0x0C); entries = linked list from *(cont+0x10), next
///    *(node+0x18), RECORD *(node+0x20). Record: +0x00 slot, +0x04 sub (message index,
///    goes past 100), +0x08 mask, +0x1A6 u16 voice id (0 = unvoiced).
///  - TEXT: msgObj = *(0x1451FE688 + slot*0x40); block = *(msgObj+8); line hdr =
///    *(block+0x38 + sub*0x10). Line hdr: RUN COUNT = u16 +0x18 for DIALOGUE (each run
///    is its own record, mask marks earlier runs revealed → read the one focused run =
///    lowest CLEAR mask bit); +0x18==0 means CHOICES with the option count in u16 +0x1A
///    (read all non-masked runs). Run ptr array at +0x20 stride 8. Decode = Atlus MSG:
///    skip 0x80+ 2-byte tokens (functions/F5 voice code eat their param), keep ASCII,
///    newline 0x0A -> space, 0x00 ends after text starts.
///  - SPEAKER NAME is NOT wired: it lives in a per-speaker-group "name-holder" at the
///    block entry +0x08 (shared, layout varies) — a heuristic mis-read it (Rise->Kou),
///    so it's disabled (ResolveSpeaker kept, unused). Needs the name-draw fn decompiled.
/// </summary>
internal sealed unsafe class BacklogReader
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] TaskName = Encoding.ASCII.GetBytes("itfBacklogDraw");
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60L);

    internal static volatile bool IsOpen;

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6);

    private bool _wasOpen;
    private int _pendingCursor = int.MinValue, _pendingSince;

    internal BacklogReader(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnText, SetUiTextVA).Activate();
        new Thread(Poll) { IsBackground = true, Name = "BacklogReader" }.Start();
        Log("[Backlog] reader ready (task itfBacklogDraw)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(100);
            try { Tick(); }
            catch (Exception ex) { IsOpen = false; Log($"[Backlog] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) return;

        nint node = FindTaskByName(TaskName);
        nint work = node != 0 && IsReadable(node + 0x48, 8) ? *(nint*)(node + 0x48) : 0;
        if (work == 0 || !IsReadable(work, 0x30))
        {
            IsOpen = false; _wasOpen = false;
            return;
        }

        uint state = *(uint*)(work + 0x0C);
        int count = ReadCount(work);
        // The backlog often opens EMPTY and populates a frame later (log: OPEN
        // count=0 then count=3). Treat "open" as state set AND entries present, so we
        // don't lock in an empty read and then miss the content (the "unreadable"
        // bug — cursor never changed, so the old code never re-read). 2026-07-20.
        // The dialogue backlog's live/interactive states (0x4/0xD/0x1C/0x1D) all have
        // bit 0x4 set; the OTHER uses of itfBacklogDraw that leaked text — a Social
        // Link level-up (0x80000000), and the closing/secondary screens (0x10,
        // 0x80000010) — do NOT. Gate on that bit (leak fix 2026-07-20).
        bool open = (state & 0x4) != 0 && count > 0;
        IsOpen = open;

        // work+0x20 = the SELECTION TARGET (settle destination); work+0x1C is the
        // animated scroll position that steps toward it. Use the target so we read
        // the line the user is actually landing on, not the animation pass-through.
        int cursor = *(int*)(work + 0x20);

        if (open != _wasOpen)
        {
            Log($"[Backlog] {(open ? "OPEN" : "closed")} state=0x{state:X} target={cursor} count={count}");
            _wasOpen = open;
            _lastReadKey = int.MinValue;
            _pendingCursor = int.MinValue;
            _wasFirstRead = false;
            if (!open) return;
        }

        if (!open) return;

        // Read each line ONCE when the selection settles on it. Key on the cursor
        // alone — NOT the count: the entry count jitters frame-to-frame while the
        // backlog is up, and folding it into the key made the same line re-read over
        // and over (the "leak / reads again" bug). The count>0 open-gate already
        // covers the populate-after-open race, so count isn't needed here. 2026-07-20.
        if (cursor != _lastReadKey)
        {
            if (cursor != _pendingCursor) { _pendingCursor = cursor; _pendingSince = 0; }
            else _pendingSince += 100;   // poll interval
            if (_pendingSince >= 150)
            {
                _lastReadKey = cursor;
                bool spoke = Speak(work, cursor, count, prefix: _wasFirstRead ? null : "Backlog. ");
                if (spoke) _wasFirstRead = true;   // keep the "Backlog." lead until a line is actually read
            }
        }
    }

    private int _lastReadKey = int.MinValue;

    private bool _wasFirstRead;

    /// <summary>count = *(i32)( *(obj+8) + 0xC ) — guarded.</summary>
    private static int ReadCount(nint work)
    {
        if (!IsReadable(work, 8)) return -1;
        nint obj = *(nint*)work;
        if (obj == 0 || !IsReadable(obj + 8, 8)) return -1;
        nint cont = *(nint*)(obj + 8);
        if (cont == 0 || !IsReadable(cont + 0xC, 4)) return -1;
        return *(int*)(cont + 0xC);
    }


    // The 450C60 hook stays installed but idle — the backlog draws GLYPH-BY-GLYPH
    // through it (capture-proven 2026-07-20: one char per call, interleaved across
    // rows), useless as a text source. Text comes from the record chain instead.
    private nint OnText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
        => _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);

    /// <summary>Speak entry <paramref name="index"/>: its decoded line text plus a
    /// "Voiced" tag (Space replays it — the game's own legend). Chain from the row
    /// drawer FUN_140459780: msgObj = *(0x1451FE688 + rec[0]*0x40); block = *(msgObj+8);
    /// lineHdr = *(block + 0x38 + rec[1]*0x10); line COUNT i16 @+0x1A; a QWORD text
    /// pointer per line @+0x20 (the record's mask bit i set = the game skips line i).</summary>
    private bool Speak(nint work, int index, int count, string? prefix)
    {
        nint rec = FindRecord(work, index);
        if (rec == 0 || !IsReadable(rec, 0x1B0)) return false;
        int sub = *(int*)(rec + 4);
        bool voiced = *(ushort*)(rec + 0x1A6) != 0;

        string text = ReadEntryText(rec);
        if (text.Length == 0) return false;   // empty render (e.g. a fully-masked row)

        // Speaker name resolution (ResolveSpeaker) is DISABLED: the name-holder layout
        // varies and the heuristic grabbed the wrong name (Rise read as "Kou") — worse
        // than none. Needs the name-draw fn decompiled. Text-only until then.
        string suffix = voiced ? " Voiced." : "";
        Speech.Say($"{prefix}{text}.{suffix}", true);
        return true;
    }

    /// <summary>The exact on-screen text of one backlog record. RE 2026-07-20 (live
    /// hunt): a message's line struct (block + 0x38 + sub*0x10) holds an ARRAY of text
    /// RUN pointers at +0x20 stride 8; the RUN COUNT is a u16 at line+0x18 (NOT +0x1A —
    /// that reads 1 even for a 2-run message and gave the wrong sentence). The record's
    /// mask (rec+0x08) is a skip-bitmask: the game renders run i only when bit i is
    /// CLEAR (from the layout builder FUN_1681ed230: "if (mask &amp; 1)==0 render; mask
    /// &gt;&gt;= 1"). So the shown text = the non-skipped runs, in order.</summary>
    private string ReadEntryText(nint rec)
    {
        int slot = *(int*)rec;
        int sub = *(int*)(rec + 4);
        uint mask = *(uint*)(rec + 8);
        // sub = the message index; it goes well past 100 in a long conversation
        // (91/92/105 seen). Only slot indexes the fixed 64-entry buffer table.
        if (slot < 0 || slot >= 64 || sub < 0 || sub >= 8192) return "";
        nint slotAddr = unchecked((nint)0x1451FE688L) + slot * 0x40;
        if (!IsReadable(slotAddr, 8)) return "";
        nint msgObj = *(nint*)slotAddr;
        if (msgObj == 0 || !IsReadable(msgObj + 8, 8)) return "";
        nint block = *(nint*)(msgObj + 8);
        nint lineAddr = block + 0x38 + sub * 0x10;
        if (block == 0 || !IsReadable(lineAddr, 8)) return "";
        nint line = *(nint*)(lineAddr);
        if (line == 0 || !IsReadable(line + 0x1C, 2)) return "";
        // Run-count fields (verified across many scenes 2026-07-20):
        //   DIALOGUE: run count in u16 +0x18 (+0x1A is a small unrelated value);
        //             each run is its OWN record, the mask marking earlier runs
        //             already-revealed, so we read the ONE run this record focuses.
        //   CHOICES:  +0x18 == 0 and the option count is in u16 +0x1A; one record
        //             holds all options, so we read them all.
        int n18 = *(ushort*)(line + 0x18);
        int n1a = *(ushort*)(line + 0x1A);
        bool choices = n18 == 0;
        int count = choices ? n1a : n18;
        if (count <= 0 || count >= 16 || !IsReadable(line + 0x20, count * 8)) return "";

        var sb = new StringBuilder();
        if (choices)
        {
            for (int i = 0; i < count; i++)
            {
                if ((mask & (1u << i)) != 0) continue;
                string s = DecodeAtlusRun(*(nint*)(line + 0x20 + i * 8));
                if (s.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(s);
            }
        }
        else
        {
            // The one focused line = the lowest CLEAR mask bit (mask0 -> run0,
            // mask0x1 -> run1, ...). Its sibling records cover the other runs.
            int run = -1;
            for (int i = 0; i < count; i++) if ((mask & (1u << i)) == 0) { run = i; break; }
            if (run >= 0) sb.Append(DecodeAtlusRun(*(nint*)(line + 0x20 + run * 8)));
        }
        return sb.ToString().Trim();
    }

    /// <summary>Decode one backlog line's Atlus-MSG run. Format (RE 2026-07-20):
    /// each line = control prefix (F2 05 FF FF · F1 41), optional F5 name/voice
    /// function codes with args, then the LITERAL ASCII text, then 0x00.
    ///   - a byte >= 0x80 begins a 2-byte token (function/glyph) — skip BOTH bytes
    ///     (this correctly eats a function's inline param, e.g. F1's 0x41).
    ///   - a byte in 0x20..0x7E is literal English text — emit it.
    ///   - a byte < 0x20 is a control/formatting byte or a function arg — skip.
    ///   - 0x00 ends the line, but ONLY after text has started: the 0x00 bytes
    ///     buried in the F5 args come BEFORE the text and must not terminate it.
    /// English-only (the mod's scope) means the 0x80+ tokens are all function codes,
    /// never Japanese glyphs, so dropping them loses no text.</summary>
    private static string DecodeAtlusRun(nint p)
    {
        if (p == 0 || !IsReadable(p, 2)) return "";
        var sb = new StringBuilder(160);
        bool started = false;
        int i = 0;
        while (i < 600)
        {
            if (!IsReadable(p + i, 1)) break;
            byte b = *(byte*)(p + i);
            if (b >= 0x80) { i += 2; continue; }         // 2-byte token — skip it + its param
            if (b >= 0x20 && b < 0x7F) { sb.Append((char)b); started = true; i += 1; continue; }
            if (b == 0 && started) break;                // line terminator (after the text)
            // Newline (0x0A) inside a run is a soft wrap or a two-sentence box — render
            // it as a SPACE so words don't fuse ("because\nI" -> "because I", not
            // "becauseI"). Other control bytes are dropped.
            if (b == 0x0A && started && (sb.Length == 0 || sb[sb.Length - 1] != ' ')) sb.Append(' ');
            i += 1;                                       // control byte / pre-text 0x00 / F5 arg
        }
        return sb.ToString().Trim();
    }

    /// <summary>Speaker name for a message. RE 2026-07-20 (live hunt): the message
    /// block's entry table (block + 0x38, stride 0x10) holds [+0x00 line, +0x08
    /// name-holder]. The name-holder is set on ONE message of a speaker's group (and
    /// shared by the rest, which have +0x08 == 0), and contains a run of pointers then
    /// the ASCII name inline. So: from this sub, scan the entries for the nearest
    /// name-holder and pull the first clean ASCII run out of it. Empty if none.</summary>
    private static string ResolveSpeaker(int slot, int sub)
    {
        if (slot < 0 || slot >= 64 || sub < 0 || sub >= 8192) return "";
        nint slotAddr = unchecked((nint)0x1451FE688L) + slot * 0x40;
        if (!IsReadable(slotAddr, 8)) return "";
        nint msgObj = *(nint*)slotAddr;
        if (msgObj == 0 || !IsReadable(msgObj + 8, 8)) return "";
        nint block = *(nint*)(msgObj + 8);
        if (block == 0) return "";
        // This message's own holder first; then look FORWARD (name-holder tends to sit
        // on the last message of a turn), then a short way BACK as a fallback.
        for (int s = sub; s <= sub + 15; s++)
        {
            string nm = HolderName(block, s);
            if (nm.Length > 0) return nm;
        }
        for (int s = sub - 1; s >= 0 && s >= sub - 15; s--)
        {
            string nm = HolderName(block, s);
            if (nm.Length > 0) return nm;
        }
        return "";
    }

    private static string HolderName(nint block, int sub)
    {
        nint entry = block + 0x38 + sub * 0x10;
        if (!IsReadable(entry, 0x10)) return "";
        nint holder = *(nint*)(entry + 8);
        if (holder == 0 || !IsReadable(holder, 0x60)) return "";
        // Skip the leading pointer array, then take the first ASCII run of >= 3 chars.
        var sb = new StringBuilder();
        for (int i = 0; i < 0x60; i++)
        {
            if (!IsReadable(holder + i, 1)) break;
            byte b = *(byte*)(holder + i);
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else { if (sb.Length >= 3) break; sb.Clear(); }
        }
        return sb.Length >= 3 ? sb.ToString().Trim() : "";
    }

    /// <summary>Entry record by index (the FUN_140459710 walk).</summary>
    private static nint FindRecord(nint work, int index)
    {
        if (index < 0 || !IsReadable(work, 8)) return 0;
        nint obj = *(nint*)work;
        if (obj == 0 || !IsReadable(obj + 8, 8)) return 0;
        nint cont = *(nint*)(obj + 8);
        if (cont == 0 || !IsReadable(cont + 0x10, 8)) return 0;
        nint n = *(nint*)(cont + 0x10);
        for (int i = 0; i < index && n != 0; i++)
        {
            if (!IsReadable(n + 0x18, 8)) return 0;
            n = *(nint*)(n + 0x18);
        }
        if (n == 0 || !IsReadable(n + 0x20, 8)) return 0;
        return *(nint*)(n + 0x20);
    }

    private static nint FindTaskByName(byte[] name)
    {
        foreach (nint head in TaskHeads)
        {
            if (!IsReadable(head, 8)) continue;
            nint node = *(nint*)head;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!IsReadable(node, 0x58)) break;
                if (NameMatches(node, name)) return node;
                node = *(nint*)(node + 0x50);
            }
        }
        return 0;
    }

    // Exact name match = the name + a NON-PRINTABLE byte (not necessarily NUL —
    // the ==0 check silently missed anchors, memory 2026-07-19).
    private static bool NameMatches(nint node, byte[] name)
    {
        for (int i = 0; i < name.Length; i++)
            if (*(byte*)(node + i) != name[i]) return false;
        byte term = *(byte*)(node + name.Length);
        return term < 0x20 || term >= 0x7F;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;          // MEM_COMMIT
        return (*(uint*)(buf + 36) & (0x01 | 0x100)) == 0;       // !NOACCESS, !GUARD
    }

    // Guarded ASCII read (the ConfigValueText/UiTextSpy helper).
    private static string ReadCStr(nint p, int maxLen)
    {
        if (p == 0) return "";
        ulong a = (ulong)p;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return "";
        if (!IsReadable(p, 1)) return "";
        var sb = new StringBuilder(maxLen);
        for (int i = 0; i < maxLen; i++)
        {
            if (!IsReadable(p + i, 1)) break;
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '?');
        }
        return sb.ToString();
    }
}
