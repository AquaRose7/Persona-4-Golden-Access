using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the config menu and announces the highlighted tab and item.
///
/// Item cursor (all tabs):  0x1401667D2  mov [r14+0x30E], di    bytes: 66 41 89 BE 0E 03 00 00
///   Fires EVERY FRAME on ALL tabs.  di = current item cursor for whatever tab is active.
///   Used to detect cursor movement on any tab; _currentTab determines which item array to use.
///
/// Tab selector:            0x140164261  mov [rbx+rdi*4+30], esi  bytes: 89 74 BB 30
///   rdi=1: fires every frame on all tabs — ignored (filtered in ASM via jl skip_non_audio).
///   rdi=2: fires when Q/E is pressed to switch tabs.
///     esi = new tab index: 0=Audio, 1=Game, 2=Graphics, 3=Display, 4=Keyboard, 5=Controller.
///
/// Detection logic:
///   - Tab switch:  nonAudioFired fires (rdi=2), esi = new tab index → update _currentTab.
///   - Item move:   itemFired fires, *_item = new cursor → announce TabItems[_currentTab][item].
///     The item==_lastItem guard suppresses every-frame fires when cursor is stable.
/// </summary>
internal unsafe class ConfigMenu : IDisposable
{
    private const string CursorWritePattern = "66 41 89 BE 0E 03 00 00";
    private const string TabWritePattern    = "89 74 BB 30";

    // Labels are taken from the game's own config-label table in init_free.bin
    // (stride 64). Tab order matches the struct's type-6 "Confirm" boundaries.
    private static readonly string[][] TabItems =
    {
        // Tab 0: Audio (verified)
        new[] { "Confirm", "BGM", "SE", "Voice", "Voiced Line", "Audio Language", "Sound Output Device" },
        // Tab 1: Game — struct shows 10 rows; the asset list has 11 (one of
        // Network Function / Anime Subtitles / Captions is hidden on PC).
        // di1..7 are certain; di8/di9 best-guess — VERIFY.
        new[] { "Confirm", "Auto Text", "Cursor Position Memory", "Battle Order Memory", "Equipped Persona Memory", "Program Guide Notification", "Inverted Camera", "Camera Speed", "Network Function", "Anime Subtitles" },
        // Tab 2: Graphics
        new[] { "Confirm", "Rendering Scale", "Animation Quality", "Shadow Quality", "Shadow", "Anisotropic Filter", "Anti Aliasing", "Contrast" },
        // Tab 3: Display
        new[] { "Confirm", "Resolution", "Monitor", "Screen Mode", "V Sync", "FPS Limit" },
        // Tab 4: Keyboard — keybind tab (sections). Names TBD (next pass).
        new[] { "Confirm", "Character Movement Forward", "Character Movement Back", "Character Movement Left", "Character Movement Right", "Confirm Action" },
        // Tab 5: Controller — keybind tab (sections). Names TBD (next pass).
        new[] { "Confirm", "Button Display", "Character Movement Forward", "Character Movement Back", "Character Movement Left", "Character Movement Right" },
    };

    private static readonly string[] TabNames =
        { "Audio", "Game", "Graphics", "Display", "Keyboard", "Controller" };

    private IAsmHook? _cursorHook;
    private IAsmHook? _tabHook;

    // Shared block layout (ints, 4 bytes each):
    // [0]   item cursor (di, from item hook — latest value)
    // [4]   item write flag
    // [8]   non-audio esi (esi captured only when rdi>=2)
    // [12]  non-audio rdi (rdi captured only when rdi>=2)
    // [16]  non-audio flag (set only when rdi>=2 fires)
    private byte*  _shared;
    private int*   _item         => (int*)(_shared + 0);
    private int*   _itemFlag     => (int*)(_shared + 4);
    private int*   _nonAudioEsi  => (int*)(_shared + 8);
    private int*   _nonAudioRdi  => (int*)(_shared + 12);
    private int*   _nonAudioFlag => (int*)(_shared + 16);
    private ulong* _r14          => (ulong*)(_shared + 24); // DEBUG: live menu working-struct base

    private int _lastItem   = -1; // last announced row (absolute, any tab)
    private int _lastValue  = -999; // last announced value (raw) for the current row
    private int _currentTab = 0;
    private int _idlePolls  = 0;
    private const int IdleResetThreshold = 40;

    private bool _running = true;
    private readonly Thread _pollThread;

    internal ConfigMenu(IReloadedHooks hooks)
    {
        _shared = (byte*)Marshal.AllocHGlobal(32);
        for (int i = 0; i < 32; i++) _shared[i] = 0;

        ulong b = (ulong)_shared;
        Log($"[ConfigMenu] Shared base: 0x{b:X}");

        // Hook 1: item cursor write (fires every frame on ALL tabs)
        SigScan(CursorWritePattern, "ConfigMenu::CursorWrite", address =>
        {
            Log($"[ConfigMenu] Item hook at 0x{address:X}");
            var asm = new[]
            {
                "use64",
                "push rax",
                "push rbx",
                "movzx eax, di",
                $"mov rbx, 0x{b+0:X16}",
                "mov dword [rbx], eax",
                $"mov rbx, 0x{b+4:X16}",
                "mov dword [rbx], 1",
                // DEBUG: capture r14 (the menu working-struct base) for value-offset hunting
                $"mov rbx, 0x{b+24:X16}",
                "mov [rbx], r14",
                "pop rbx",
                "pop rax"
            };
            _cursorHook = hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
        });

        // Hook 2: tab/item-save write (mov [rbx+rdi*4+30], esi)
        // Only capture when rdi>=2 (non-Audio tab cursor changes).
        // rdi=1 fires every frame on all tabs and is ignored via the conditional jump.
        SigScan(TabWritePattern, "ConfigMenu::TabWrite", address =>
        {
            Log($"[ConfigMenu] Tab hook at 0x{address:X}");
            var asm = new[]
            {
                "use64",
                "push rax",
                "push rbx",
                // Skip capture entirely if rdi < 2 (Audio background write)
                "cmp edi, 2",
                "jl skip_non_audio",
                // Capture esi (item cursor for this non-Audio tab)
                "mov eax, esi",
                $"mov rbx, 0x{b+8:X16}",
                "mov dword [rbx], eax",
                // Capture edi (tab slot: 2=Game, 3=Graphics…)
                "mov eax, edi",
                $"mov rbx, 0x{b+12:X16}",
                "mov dword [rbx], eax",
                // Set non-audio flag
                $"mov rbx, 0x{b+16:X16}",
                "mov dword [rbx], 1",
                "skip_non_audio:",
                "pop rbx",
                "pop rax"
            };
            _tabHook = hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
        });

        _pollThread = new Thread(Poll) { IsBackground = true, Name = "ConfigMenu" };
        _pollThread.Start();
    }

    private void Poll()
    {
        while (_running)
        {
            Thread.Sleep(50);
            try   { CheckCursor(); }
            catch (Exception ex) { Log($"[ConfigMenu] Poll error: {ex.Message}"); }
        }
    }

    private void CheckCursor()
    {
        var nonAudioFired = *_nonAudioFlag; *_nonAudioFlag = 0;
        var itemFired     = *_itemFlag;     *_itemFlag     = 0;

        // Idle reset: both hooks quiet → config menu is closed (or cursor stable on non-Audio tab).
        // Reset _lastItem so the cursor position is re-announced when the menu reopens.
        // Do NOT reset _currentTab — P4G remembers the last tab, so preserving it is correct,
        // and it prevents false Audio detection when the cursor is briefly stable mid-session.
        if (nonAudioFired == 0 && itemFired == 0)
        {
            if (++_idlePolls >= IdleResetThreshold)
            {
                _lastItem  = -1;
                _lastValue = -999;
                _idlePolls = 0;
            }
            return;
        }
        _idlePolls = 0;

        // Tab switch: rdi=2 fires on Q/E with esi = new tab index
        //   esi=0→Audio, 1→Game, 2→Graphics, 3→Display, 4→Keyboard, 5→Controller
        bool tabChanged = false;
        if (nonAudioFired != 0)
        {
            var esi = *_nonAudioEsi;
            var rdi = *_nonAudioRdi;
            Log($"[ConfigMenu] TabSwitch: rdi={rdi} esi={esi} (currentTab={_currentTab})");

            if (esi >= 0 && esi < TabNames.Length && esi != _currentTab)
            {
                _currentTab = esi;
                _lastItem   = -1;
                _lastValue  = -999;
                tabChanged  = true;
            }
        }

        // Item cursor: *_item is the cursor on the current tab (all tabs, every frame).
        // Use _currentTab to pick the right item array.
        if (itemFired != 0)
        {
            var di = *_item;
            if (di < 0 || di > 15) return;

            // The cursor `di` is the row WITHIN the visible window. Long tabs
            // (e.g. Game = 10 rows) scroll, so the true row = di + scroll, where
            // scroll (top visible row index) lives at r14+0x34. Dedupe + name +
            // value must all use the absolute row, or scrolled rows go silent /
            // read the wrong setting.
            int row = di + ReadScroll();
            if (row < 0 || row > 31) return;

            var items    = TabItems[_currentTab];
            var itemName = row < items.Length ? items[row] : $"Item {row + 1}";

            // Read the live value for this row from the descriptor/value array
            // at r14+0x71C (stride 0x20): type @+0x10, current value @+0x1C (low16).
            string valueStr = ReadValueString(_currentTab, row, out int rec, out int type, out int raw);

            // Re-announce when EITHER the row OR the value changed (so changing a
            // setting in place — left/right — speaks the new value, not silence).
            bool rowChanged = row != _lastItem;
            bool valChanged = valueStr != null && raw != _lastValue;
            if (!rowChanged && !valChanged && !tabChanged) return; // nothing changed
            _lastItem  = row;
            _lastValue = raw;

            string announce;
            if (rowChanged || tabChanged)
            {
                var label = valueStr != null ? $"{itemName}, {valueStr}" : itemName;
                announce = tabChanged ? $"{TabNames[_currentTab]}, {label}" : label;
            }
            else
            {
                announce = valueStr; // value-only change → just speak the new value
            }
            Log($"[ConfigMenu] → {TabNames[_currentTab]} > {itemName}  (r14=0x{*_r14:X} di={di} row={row} rec={rec} type={type} raw={raw} val='{valueStr}' rowCh={rowChanged} valCh={valChanged})"); // DEBUG
            Speech.Say(announce, true);
        }
        else if (tabChanged)
        {
            // Tab switched but item hook didn't fire this poll — announce just the tab name
            Log($"[ConfigMenu] Tab → {TabNames[_currentTab]}");
            Speech.Say(TabNames[_currentTab], true);
        }
    }

    // ── Live value reading ────────────────────────────────────────────────
    // The menu working struct (r14) holds a flat descriptor/value array at
    // r14+0x71C, stride 0x20, covering EVERY setting across all tabs in order.
    // Each record:  +0x10 int = type (6 button, 3 slider, 1/2 toggle, 4 multi)
    //               +0x1C low16 = current value (verified by snapshot-diff).
    // Each tab begins with a type-6 "Confirm" button, so the Nth type-6 record
    // is the base record of tab N. The cursor `di` is the row within the tab
    // (di0 = Confirm), so rec = tabBase + di.
    private const int RecordArray = 0x71C;
    private const int RecordStride = 0x20;
    private const int TypeOff  = 0x10;
    private const int ValueOff = 0x1C;
    private const int ScrollOff = 0x34;  // top visible row index (list scroll)

    // Scroll offset of the current tab's list: true row = di + scroll.
    private int ReadScroll()
    {
        ulong r14 = *_r14;
        if (r14 == 0) return 0;
        nint p = (nint)r14 + ScrollOff;
        if (!IsReadable(p, 4)) return 0;
        int s = *(int*)p;
        return (s >= 0 && s < 32) ? s : 0;
    }

    private string ReadValueString(int tab, int row, out int rec, out int type, out int raw)
    {
        rec = -1; type = -1; raw = -1;
        ulong r14 = *_r14;
        if (r14 == 0) return null;
        nint arr = (nint)r14 + RecordArray;
        if (!IsReadable(arr, RecordStride * 64)) return null;

        // Find the base record of each tab = positions of type-6 (button) records.
        int tabBase = -1, seen = 0;
        for (int k = 0; k < 64; k++)
        {
            int t = *(int*)(arr + k * RecordStride + TypeOff);
            if (t == 6)
            {
                if (seen == tab) { tabBase = k; break; }
                seen++;
            }
        }
        if (tabBase < 0) return null;

        rec  = tabBase + row;
        if (rec < 0 || rec >= 64) return null;
        nint r = arr + rec * RecordStride;
        type = *(int*)(r + TypeOff);
        raw  = *(short*)(r + ValueOff);   // low 16 bits = current value
        return FormatValue(type, raw);
    }

    private static string FormatValue(int type, int raw)
    {
        // VALUES DISABLED 2026-06-25. The menu's value buffer (+0x1C) is recycled:
        // it reads correctly just after the menu opens, then goes STALE — toggles
        // freeze at an old On/Off and sliders stop tracking. Same recycled-buffer
        // root cause that blocked multi-choice (see memory/config_menu_status.md).
        // A stale/wrong value is worse than none, so announce NAME ONLY for every
        // setting. (raw/type still logged for debugging; re-enable here only if a
        // reliably-live value field is ever found.)
        return null;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        const int MBI_SIZE = 48, OFF_STATE = 32, OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;
        return true;
    }

    public void Dispose()
    {
        _running = false;
        _cursorHook?.Disable();
        _tabHook?.Disable();
        if (_shared != null)
        {
            Marshal.FreeHGlobal((IntPtr)_shared);
            _shared = null;
        }
    }
}
