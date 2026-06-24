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

    private static readonly string[][] TabItems =
    {
        // Tab 0: Audio
        new[] { "Confirm", "BGM", "SE", "Voice", "Voiced Line", "Audio Language", "Sound Output Device" },
        // Tab 1: Game
        new[] { "Confirm", "Auto Text", "Cursor Position Memory", "Battle Order Memory", "Equipped Persona Memory", "Program Guide Notification", "Inverted Camera" },
        // Tab 2: Graphics
        new[] { "Confirm", "Presets", "Rendering Scale", "Shadow Quality", "Shadow", "Anisotropic Filter", "Anti Aliasing" },
        // Tab 3: Display
        new[] { "Confirm", "Resolution", "Monitor", "Screen Mode", "V Sync", "FPS Limit" },
        // Tab 4: Keyboard — Common section visible; more sections below (Field/Dungeon, Battle, Event)
        new[] { "Confirm", "Character Movement Forward", "Character Movement Back", "Character Movement Left", "Character Movement Right", "Confirm Action" },
        // Tab 5: Controller — Common section visible; more sections below
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
    private byte* _shared;
    private int*  _item         => (int*)(_shared + 0);
    private int*  _itemFlag     => (int*)(_shared + 4);
    private int*  _nonAudioEsi  => (int*)(_shared + 8);
    private int*  _nonAudioRdi  => (int*)(_shared + 12);
    private int*  _nonAudioFlag => (int*)(_shared + 16);

    private int _lastItem   = -1; // last announced item index (any tab)
    private int _currentTab = 0;
    private int _idlePolls  = 0;
    private const int IdleResetThreshold = 40;

    private bool _running = true;
    private readonly Thread _pollThread;

    internal ConfigMenu(IReloadedHooks hooks)
    {
        _shared = (byte*)Marshal.AllocHGlobal(20);
        for (int i = 0; i < 20; i++) _shared[i] = 0;

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
                tabChanged  = true;
            }
        }

        // Item cursor: *_item is the cursor on the current tab (all tabs, every frame).
        // Use _currentTab to pick the right item array.
        if (itemFired != 0)
        {
            var item = *_item;
            if (item < 0 || item > 15) return;
            if (item == _lastItem && !tabChanged) return; // nothing changed
            _lastItem = item;

            var items    = TabItems[_currentTab];
            var itemName = item < items.Length ? items[item] : $"Item {item + 1}";
            var announce = tabChanged ? $"{TabNames[_currentTab]}, {itemName}" : itemName;
            Log($"[ConfigMenu] → {TabNames[_currentTab]} > {itemName}");
            Speech.Say(announce, true);
        }
        else if (tabChanged)
        {
            // Tab switched but item hook didn't fire this poll — announce just the tab name
            Log($"[ConfigMenu] Tab → {TabNames[_currentTab]}");
            Speech.Say(TabNames[_currentTab], true);
        }
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
