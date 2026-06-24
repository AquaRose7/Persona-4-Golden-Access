using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the name-entry keyboard render function to speak whichever key the cursor is on.
///
/// Struct layout:
///   1st param (rcx) = outer struct; *(outer + 0x48) = inner struct
///   inner + 0x20 = column (int, 0–19)
///   inner + 0x24 = row    (int, 0–6)
///
/// The keyboard is 20 columns × 7 rows:
///   Cols  0– 4 : uppercase A–Z (rows 0–5)
///   Cols  5– 9 : lowercase a–z (rows 0–5)
///   Cols 10–14 : digits 0–9 (rows 0–1) + Atlus-encoded symbols (rows 2–5)
///   Cols 15–19 : ASCII symbols &amp; through @, and more
/// </summary>
internal unsafe class NameEntryKeyboard : IDisposable
{
    // 7 rows × 20 cols.  null = empty / no key.
    private static readonly string?[,] CharMap =
    {
        // row 0
        {
            "A","B","C","D","E",          // cols  0– 4  uppercase
            "a","b","c","d","e",          // cols  5– 9  lowercase
            "0","1","2","3","4",          // cols 10–14  digits (Atlus 0x0005-0x0009)
            "&","'","(",")", "*"          // cols 15–19  symbols
        },
        // row 1
        {
            "F","G","H","I","J",
            "f","g","h","i","j",
            "5","6","7","8","9",          // Atlus 0x000A-0x000E
            "+", null, null, null, ","
        },
        // row 2
        {
            "K","L","M","N","O",
            "k","l","m","n","o",
            null,null,null,null,null,     // Atlus 0x000F-0x0013  (unknown symbols)
            "-",".","/" ,"0","1"
        },
        // row 3
        {
            "P","Q","R","S","T",
            "p","q","r","s","t",
            null,null,null,null,null,     // Atlus 0x0014-0x0018
            "2","3","4","5","6"
        },
        // row 4
        {
            "U","V","W","X","Y",
            "u","v","w","x","y",
            null,null,null,null,null,     // Atlus 0x0019-0x001D
            "7","8","9",":",";"
        },
        // row 5
        {
            "Z","[","\\","]","^",
            "z","{","|","}","~",
            null,null,"Space","!","\"",   // Atlus 0x001E-0x001F, then ASCII space/!/\"
            "<","=",">","?","@"
        },
        // row 6
        {
            "_","`", null, null, null,    // cols 0–4
            "Delete",null,null,null,null, // cols 5–9  (0x007F = DEL)
            null,null,null,null,null,     // cols 10–14
            null,null,null,null,null      // cols 15–19
        },
    };

    private IHook<RenderDelegate>? _hook;
    private int _lastCol = -1;
    private int _lastRow = -1;

    [StructLayout(LayoutKind.Explicit)]
    private struct KbdOuter
    {
        [FieldOffset(0x48)] public KbdInner* Inner;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct KbdInner
    {
        [FieldOffset(0x20)]   public int Col;
        [FieldOffset(0x24)]   public int Row;
        // Active name field: 1 = Last name (top row), 2 = First name (bottom).
        // Found via live diagnostic — flips exactly when the name-cell cursor
        // (Q/E) crosses between the two rows; unchanged on within-row moves.
        [FieldOffset(0x3100)] public int Field;
    }

    private delegate void RenderDelegate(KbdOuter* pOuter, nint p2, nint p3, nint p4);

    internal NameEntryKeyboard(IReloadedHooks hooks)
    {
        SigScan(
            "48 89 5C 24 18 48 89 6C 24 20 56 57 41 56 48 83 EC 50 48 8B D9",
            "NameEntryKeyboard::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Name entry keyboard render hook active.");
            });
    }

    private void OnRender(KbdOuter* pOuter, nint p2, nint p3, nint p4)
    {
        _hook!.OriginalFunction(pOuter, p2, p3, p4);

        if (pOuter == null) return;
        var inner = pOuter->Inner;
        if (inner == null) return;

        var col = inner->Col;
        var row = inner->Row;
        if (col < 0 || col > 19 || row < 0 || row > 6) return;

        // Speak the instruction once when the screen opens. The field value
        // isn't valid yet at this point, so the prompt states the order
        // (last name first) instead of reading the live field.
        if (!_introDone)
        {
            _introDone = true;
            Speech.Say("Enter your name. Last name first. Press X when finished.", true);
        }

        AnnounceField(inner); // "Last name" / "First name" when the row changes

        if (col == _lastCol && row == _lastRow) return;

        _lastCol = col;
        _lastRow = row;

        var ch = CharMap[row, col];
        if (ch == null) return;

        LogDebug($"Keyboard: row={row} col={col} -> {ch}");
        Speech.Say(ch, true);
    }

    private int  _lastField = -1;
    private bool _introDone;

    private static string FieldName(int f) => f == 2 ? "Last name" : "First name";

    // Announce the field name whenever the active field changes (the field at
    // +0x3100 flips 1<->2 as the cell cursor crosses between the two rows).
    private void AnnounceField(KbdInner* inner)
    {
        if (!IsReadable((long)inner + 0x3100)) return;
        int f = inner->Field;
        if (f != 1 && f != 2) return;
        if (f == _lastField) return;
        _lastField = f;
        Speech.Say(FieldName(f), true);
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nint VirtualQuery(nint a, byte* b, nint l);
    private static bool IsReadable(long addr)
    {
        byte* buf = stackalloc byte[48];
        if (VirtualQuery((nint)addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;     // MEM_COMMIT
        uint p = *(uint*)(buf + 36);
        return (p & 0x01) == 0 && (p & 0x100) == 0;          // not NOACCESS / GUARD
    }

    public void Dispose() { }
}
