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
    // internal: ConfigValueText builds its known-label set from these (the drawn
    // labels match this table exactly, both come from init_free.bin).
    internal static readonly string[][] TabItems =
    {
        // Tab 0: Audio (verified)
        new[] { "Confirm", "BGM", "SE", "Voice", "Voiced Line", "Audio Language", "Sound Output Device" },
        // Tab 1: Game — struct shows 10 rows; the asset list has 11 (one of
        // Network Function / Anime Subtitles / Captions is hidden on PC).
        // di1..7 are certain; di8/di9 best-guess — VERIFY.
        new[] { "Confirm", "Auto Text", "Cursor Position Memory", "Battle Order Memory", "Equipped Persona Memory", "Program Guide Notification", "Inverted Camera", "Camera Speed", "Network Function", "Anime Subtitles" },
        // Tab 2: Graphics — REAL on-screen rows (screenshot-verified 2026-07-10):
        // "Presets" IS shown (it sits at table idx 91, appended after the tab
        // blocks, which is why the table-order rebuild missed it) and the
        // table's "Animation Quality" is hidden on PC (like "Captions").
        new[] { "Confirm", "Presets", "Rendering Scale", "Shadow Quality", "Shadow", "Anisotropic Filter", "Anti Aliasing", "Contrast" },
        // Tab 3: Display
        new[] { "Confirm", "Resolution", "Monitor", "Screen Mode", "V Sync", "FPS Limit" },
        // Tab 4: Keyboard — label table idx 33-61 (2026-07-10 dump), blank
        // section-separator entries excluded on the assumption the cursor
        // skips them — VERIFY by ear; if names misalign, separators count.
        new[] { "Confirm", "Character Movement(Forward)", "Character Movement(Back)", "Character Movement(Left)", "Character Movement(Right)", "Confirm, Action", "Cancel", "2D Display", "Command Menu", "Sub Menu", "Rotate Camera(Left)", "Rotate Camera(Right)", "Center Camera", "Vox Populi/Display Floor Map", "TV Overlay/Rescue", "Quick Save", "Toggle Rush On/Off", "Display detailed list", "Check turn order", "Analyze", "Fast Forward Text", "Skip Event", "Backlog", "Move 2 forward in backlog", "Move 2 back in backlog" },
        // Tab 5: Controller — label table idx 62-90; same layout but
        // "Button Display" replaces "2D Display" and sits at row 1.
        new[] { "Confirm", "Button Display", "Character Movement(Forward)", "Character Movement(Back)", "Character Movement(Left)", "Character Movement(Right)", "Confirm, Action", "Cancel", "Command Menu", "Sub Menu", "Rotate Camera(Left)", "Rotate Camera(Right)", "Center Camera", "Vox Populi/Display Floor Map", "TV Overlay/Rescue", "Quick Save", "Toggle Rush On/Off", "Display detailed list", "Check turn order", "Analyze", "Fast Forward Text", "Skip Event", "Backlog", "Move 2 forward in backlog", "Move 2 back in backlog" },
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
    // ⚠ BOTH hooks fire only during INPUT activity, not every frame (log-proven
    // 2026-07-10 — the old "fires every frame" notes were wrong). There is no
    // per-frame "menu open" signal here; in-place value changes are therefore
    // announced by ConfigValueText PUSHING OnValueDrawn from its render hook.
    private byte*  _shared;
    private int*   _item         => (int*)(_shared + 0);
    private int*   _itemFlag     => (int*)(_shared + 4);
    private int*   _nonAudioEsi  => (int*)(_shared + 8);
    private int*   _nonAudioRdi  => (int*)(_shared + 12);
    private int*   _nonAudioFlag => (int*)(_shared + 16);
    private ulong* _r14          => (ulong*)(_shared + 24); // DEBUG: live menu working-struct base

    // Push anchor for ConfigValueText: the label of the row the cursor is on.
    // When the game redraws that label's value (left/right press — no hook
    // fires), ConfigValueText calls OnValueDrawn and the new value is spoken.
    // Kept across idle (the hooks are silent while the menu just sits open);
    // only the config menu ever draws these exact labels, so a stale anchor is
    // harmless — the next row announce replaces it.
    internal static volatile string? CurrentRowLabel;
    private static volatile string? _lastValue; // last announced value for the current row

    private int _lastItem   = -1; // last announced row (absolute, any tab)
    private long _lastItemTick;   // last time the cursor hook fired (active-use gate)
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

        // Idle reset: both hooks quiet. NOTE this also happens while the menu is
        // OPEN with the cursor still (the hooks only fire on input) — so only
        // _lastItem/_lastValue reset (for re-announce on the next move); the
        // CurrentRowLabel push anchor is deliberately KEPT so an in-place
        // left/right change after a pause still speaks (OnValueDrawn).
        // Do NOT reset _currentTab — P4G remembers the last tab, so preserving it is correct,
        // and it prevents false Audio detection when the cursor is briefly stable mid-session.
        if (nonAudioFired == 0 && itemFired == 0)
        {
            if (++_idlePolls >= IdleResetThreshold)
            {
                _lastItem  = -1;
                _lastValue = null;
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
                _lastValue  = null;
                tabChanged  = true;
            }
        }

        // Item cursor: *_item is the cursor on the current tab (all tabs, every frame).
        // Use _currentTab to pick the right item array.
        if (itemFired != 0)
        {
            _lastItemTick = Environment.TickCount64;

            // The game OWNS the tab field — r14+0x38, slot 2 of the same slot array
            // the scroll lives in (slot 1 = +0x34, proven by the working ReadScroll).
            // The IN-GAME config RESETS it to 0 (Audio) with a write cascade at close
            // (log 2026-07-10: TabSwitch esi 5→4→…→0), so event-tracking alone
            // reopened on a stale tab and misread every row. r14 is fresh here (the
            // cursor hook just re-captured it), so the read is trustworthy.
            int structTab = ReadStructTab();
            if (structTab >= 0 && structTab != _currentTab)
            {
                Log($"[ConfigMenu] struct tab {_currentTab} → {structTab} (adopting)");
                _currentTab = structTab;
                _lastItem   = -1;
                _lastValue  = null;
                tabChanged  = true;
            }

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

            // Live value = what the game DRAWS for this row (ConfigValueText parses
            // the FUN_140450C60 render stream — never stale, tracks un-confirmed
            // left/right previews, multi-choice included). The old menu-struct read
            // (r14+0x71C rec +0x1C) is a documented dead end: the buffer recycles
            // and the field freezes mid-session (memory/config_menu_status.md).
            // Rows with fallback "Item N" names simply miss the map → name-only.
            // ⚠ CONTROLLER tab (5): its bindings draw as BUTTON ICONS (never
            // captured), but its labels are the same strings as the Keyboard
            // tab's — a plain lookup leaks the KEYBOARD key ("Back, S")
            // (log-caught 2026-07-10). Wrong value is worse than none → the
            // tab is name-only except its own "Button Display" text value.
            bool suppressed = _currentTab == 5 && itemName != "Button Display";
            string? valueStr = suppressed ? null : ConfigValueText.Lookup(itemName);
            CurrentRowLabel = suppressed ? null : itemName; // push anchor for in-place value changes

            // Re-announce when EITHER the row OR the value changed (so changing a
            // setting in place — left/right — speaks the new value, not silence).
            bool rowChanged = row != _lastItem;
            bool valChanged = valueStr != null && valueStr != _lastValue;
            if (!rowChanged && !valChanged && !tabChanged) return; // nothing changed
            _lastItem  = row;
            _lastValue = valueStr;

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
            Log($"[ConfigMenu] → {TabNames[_currentTab]} > {itemName}  (di={di} row={row} val='{valueStr}' rowCh={rowChanged} valCh={valChanged})"); // DEBUG
            Speech.Say(announce, true);
        }
        else if (tabChanged)
        {
            // Tab switched but item hook didn't fire this poll — announce just the tab
            // name. NOTE: a "recent activity" gate was tried here (2026-07-10) to mute
            // the close-time tab-write cascade, but it also muted REAL Q/E switches
            // after a pause (the cursor hook doesn't fire on Q/E) — user-hit, reverted.
            // The close cascade briefly flutters tab names; interrupt=true collapses it
            // audibly and the struct-tab read corrects everything on reopen.
            Log($"[ConfigMenu] Tab → {TabNames[_currentTab]}");
            Speech.Say(TabNames[_currentTab], true);
        }
    }

    // PUSH path (called by ConfigValueText from the render hook): the game just
    // redrew `label`'s value and it CHANGED. If that's the row the cursor is on,
    // speak the new value — this is how in-place left/right changes are voiced
    // (no input hook fires for them). "Confirm" is excluded: its "OK" is button
    // art, and the label also appears on other screens.
    internal static void OnValueDrawn(string label, string value)
    {
        if (label == "Confirm") return;
        if (label != CurrentRowLabel) return;
        if (value == _lastValue) return;
        _lastValue = value;
        if (!GameHasFocus()) return;
        Log($"[ConfigMenu] value → {label} = '{value}'");
        Speech.Say(value, true);
    }

    // ── Live value reading ────────────────────────────────────────────────
    // Values come from ConfigValueText (the FUN_140450C60 render stream). The
    // menu working struct's own value field (r14+0x71C rec +0x1C) pool-recycles
    // and freezes mid-session — do NOT go back to it (memory/config_menu_status.md).

    private const int ScrollOff = 0x34;  // top visible row index (list scroll) — slot 1
    private const int TabOff    = 0x38;  // current tab index — slot 2 (game-owned truth)

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

    // The game's own current-tab field. Only meaningful while the menu draws
    // (call right after the cursor hook fired, when r14 is freshly captured).
    private int ReadStructTab()
    {
        ulong r14 = *_r14;
        if (r14 == 0) return -1;
        nint p = (nint)r14 + TabOff;
        if (!IsReadable(p, 4)) return -1;
        int t = *(int*)p;
        return (t >= 0 && t < TabNames.Length) ? t : -1;
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
