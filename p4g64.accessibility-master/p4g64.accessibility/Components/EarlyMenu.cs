using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the early-game RESTRICTED pause menu. Before the Persona awakening the
/// full camp menu isn't set up (pCamp == *(0x140EC0A40) is 0), and pressing the
/// menu button opens only the System options (Config / Change Difficulty / …).
/// PlayerMenu owns the camp System menu (pCamp != 0), so this acts ONLY when
/// pCamp == 0 and can never conflict with it.
///
/// The menu object has no static anchor, so we hook the generic widget field
/// setter FUN_14019D040(obj, index, value) — it writes field[index] at
/// obj+0x2C+index*2. When the cursor field changes we read the highlighted
/// option id and speak it.
///
/// DIAGNOSTIC BUILD: while pCamp == 0, every small write logs the object + a
/// field dump so one visit to the menu reveals the real cursor offset and the
/// option-id array. Once confirmed, this is replaced by a targeted reader.
/// </summary>
internal unsafe class EarlyMenu : IDisposable
{
    private const ulong PCampPtr = 0x140EC0A40;
    // Generic widget field-setter FUN_14019D040 (ASLR off → fixed VA). The
    // prologue isn't unique, so hook the exact address rather than SigScan.
    private const long SetterVA = 0x14019D040;

    private delegate ulong SetterDelegate(nint obj, int index, int value);
    private IHook<SetterDelegate>? _hook;

    internal EarlyMenu(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetterDelegate>(OnSet, SetterVA).Activate();
        Log($"[EarlyMenu] widget-setter hook @0x{SetterVA:X}");
    }

    // Confirmed layout (2026-06-25, identical to the camp System submenu):
    //   cursor = field[0] @ obj+0x2C   scroll @ obj+0x32   count @ obj+0x4E
    //   option ids (s16[]) @ obj+0x3E. Early list = [1,2,5,4,6,7] (no Suspend).
    private const int CursorField = 0;   // index passed to the setter
    private const int OffScroll = 0x32;
    private const int OffIds = 0x3E;
    private const int OffCount = 0x4E;

    private nint _lastObj;
    private int _lastRow = -1;

    private ulong OnSet(nint obj, int index, int value)
    {
        var ret = _hook!.OriginalFunction(obj, index, value);
        try { Handle(obj, index, value & 0xFFFF); } catch { }
        return ret;
    }

    private void Handle(nint obj, int cursor, int newCursor)
    {
        // Only the early menu: camp menu (pCamp) must be absent, else PlayerMenu
        // owns the System list and we'd double-announce.
        if (*(nint*)PCampPtr != 0) return;
        if (cursor != CursorField) return;               // only cursor-field writes
        if (obj == 0 || !IsReadable(obj + 0x2C, 0x40)) return;

        int count = *(short*)(obj + OffCount);
        if (count < 1 || count > 8) return;              // System list is short
        if (*(short*)(obj + OffIds) != 1) return;        // must start with Config(1)

        int row = *(short*)(obj + OffScroll) + newCursor;
        if (row < 0 || row >= count) return;
        int id = *(short*)(obj + OffIds + row * 2);
        if (id < 1 || id > 7) return;

        if (obj == _lastObj && row == _lastRow) return;
        _lastObj = obj; _lastRow = row;
        Log($"[EarlyMenu] System row={row} id={id} -> {SystemName(id)}");
        Speech.Say(SystemName(id), true);
    }

    // Same id→label table the camp System menu uses (PlayerMenu.SystemOptionName).
    private static string SystemName(int id) => id switch
    {
        1 => "Config",
        2 => "Change Difficulty",
        3 => "Suspend",
        4 => "Delete Data",
        5 => "Load Data",
        6 => "Return to Title",
        7 => "End Game",
        _ => $"Option {id}"
    };

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
        if (*(uint*)(buf + OFF_STATE) != MEM_COMMIT) return false;
        if ((*(uint*)(buf + OFF_PROTECT) & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;
        return true;
    }

    public void Dispose() => _hook?.Disable();
}
