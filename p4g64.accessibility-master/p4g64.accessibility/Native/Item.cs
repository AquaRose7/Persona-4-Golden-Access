using System.Runtime.InteropServices;
using System.Text;
using p4g64.accessibility.Native.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

/// <summary>
/// Reads item display names from the game's native English name arrays.
///
/// Ghidra-style analysis (2026-04-19) of the inline switch dispatcher at
/// 0x1400E5D43..0x1400E5D8A (inside a larger formatter function) identified
/// FOUR distinct (BSS cell, stride) pairs, selected by a global at
/// 0x140FD01E4:
///
///   type=0       : stride=0x13 (19)  BSS=0x15E4390D8
///   type=1       : stride=0x17 (23)  BSS=0x15E4390C8  (Skill names per Skill.cs)
///   type=2..4    : stride=0x15 (21)  BSS=0x15E439088
///   default      : stride=0x21 (33)  BSS=0x15E439080
///
/// Each is reachable by a unique 11-byte pattern
///   48 6B CE <stride>  48 03 0D ?? ?? ?? ??
/// (IMUL rcx, rsi, stride ; ADD rcx, [rip+disp32]).
///
/// We SigScan all four and probe each at lookup time, returning the first
/// array whose entry looks like a plausible ASCII name. The log records
/// every candidate so we can map categories (weapon/armor/accessory/item)
/// to arrays empirically from one play session.
/// </summary>
public unsafe class Item
{
    private struct NameArray
    {
        public byte** BssCell;   // pointer to the global that holds the array base
        public int    Stride;
        public string Label;
        public nint   BssVa;     // diagnostic
    }

    private static readonly NameArray[] _arrays = new NameArray[4];

    // Weapon/armor/accessory/item display names — flat ASCII array, stride=0x18.
    // ASLR is disabled in P4G so this address is constant. Confirmed empirically:
    // gameItemId=4 ("Imitation Katana") is at 0x1411901C0, stride=0x18 verified
    // by scanning adjacent entries in-game (DiagnoseNameStride, 2026-04-20).
    // Base = 0x1411901C0 - 4*0x18 = 0x141190160.
    private const long NATIVE_NAME_BASE   = 0x141190160L;
    private const int  NATIVE_NAME_STRIDE = 0x18;

    internal static void Initialise()
    {
        RegisterArray(0, stride: 0x13, label: "s19",
            pattern: "48 6B CE 13 48 03 0D ?? ?? ?? ??");
        RegisterArray(1, stride: 0x17, label: "s23",
            pattern: "48 6B CE 17 48 03 0D ?? ?? ?? ??");
        RegisterArray(2, stride: 0x15, label: "s21",
            pattern: "48 6B CE 15 48 03 0D ?? ?? ?? ??");
        RegisterArray(3, stride: 0x21, label: "s33",
            pattern: "48 6B CE 21 48 03 0D ?? ?? ?? ??");
    }

    private static void RegisterArray(int idx, int stride, string label, string pattern)
    {
        SigScan(pattern, $"ItemNames_{label}", addr =>
        {
            // Pattern is 11 bytes: [48 6B CE SS] [48 03 0D d32].
            // disp32 begins at addr+7; GetGlobalAddress returns RIP+disp.
            var bss = GetGlobalAddress(addr + 7);
            _arrays[idx] = new NameArray
            {
                BssCell = (byte**)bss,
                Stride  = stride,
                Label   = label,
                BssVa   = (nint)bss,
            };
            Log($"[Item] {label} (stride={stride}): sigscan=0x{addr:X} BSS=0x{bss:X}");
        });
    }

    /// <summary>
    /// Diagnostic: reads strings at 0x1411901C0 + candidate_stride*n to find
    /// the stride of the weapon/item name array empirically. Logs up to 8 entries
    /// for each candidate stride. Call once from the shop when peeking at a weapon.
    /// </summary>
    internal static void DiagnoseNameStride(int gameItemId)
    {
        int[] strides = { 0x10, 0x12, 0x14, 0x16, 0x18, 0x1A, 0x1C, 0x1E, 0x20, 0x22, 0x24, 0x28, 0x30, 0x38 };
        const long KNOWN_VA = 0x1411901C0L;

        var sb = new StringBuilder();
        sb.Append($"[Item.DiagnoseStride] gameItemId={gameItemId} knownVA=0x{KNOWN_VA:X}");

        foreach (int stride in strides)
        {
            sb.Append($"\n  stride=0x{stride:X2}:");
            for (int delta = -2; delta <= 5; delta++)
            {
                byte* ptr = (byte*)(KNOWN_VA + (long)delta * stride);
                if (!IsReadable((nint)ptr, stride)) { sb.Append($" [{delta}]=unreadable"); continue; }
                int len = 0; bool bad = false;
                for (; len < stride; len++) { byte b = ptr[len]; if (b == 0) break; if (b < 0x20 || b > 0x7E) { bad = true; break; } }
                if (bad || len < 2) { sb.Append($" [{delta}]=bad"); continue; }
                var s = Encoding.UTF8.GetString(ptr, len);
                sb.Append($" [{delta}]=\"{s}\"");
            }
        }
        Log(sb.ToString());
    }

    /// <summary>
    /// Probe all four name arrays for the given ID. Returns the first
    /// plausible ASCII name, or "" if none qualify.
    /// </summary>
    internal static string GetName(int gameItemId)
    {
        if (gameItemId < 0 || gameItemId > 4096) return "";

        // Try the flat native name array first (stride=0x18, ASLR-off fixed base).
        byte* nativePtr = (byte*)NATIVE_NAME_BASE + gameItemId * NATIVE_NAME_STRIDE;
        if (IsReadable((nint)nativePtr, NATIVE_NAME_STRIDE))
        {
            int nlen = 0; bool nbad = false;
            for (; nlen < NATIVE_NAME_STRIDE; nlen++)
            {
                byte b = nativePtr[nlen];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) { nbad = true; break; }
            }
            if (!nbad && nlen >= 2)
            {
                var nname = Encoding.UTF8.GetString(nativePtr, nlen);
                if (nname != "Blank" && nname != "????" && !nname.StartsWith("item_") && !nname.StartsWith("weapon_") && !nname.StartsWith("armor_"))
                {
                    Log($"[Item] GetName({gameItemId}) -> native \"{nname}\"");
                    return nname;
                }
            }
        }

        string chosen = "";
        var sb = new StringBuilder();
        sb.Append($"[Item] GetName({gameItemId}) native miss, probing BMD arrays:");

        for (int i = 0; i < _arrays.Length; i++)
        {
            var arr = _arrays[i];
            if (arr.Stride == 0x17) { sb.Append($" {arr.Label}=skipped(skills)"); continue; } // s23 = skill names
            if (arr.BssCell == null) { sb.Append($" {arr.Label ?? "?"}=unset"); continue; }
            byte* baseAddr = *arr.BssCell;
            if (baseAddr == null) { sb.Append($" {arr.Label}=null"); continue; }

            byte* namePtr = baseAddr + gameItemId * arr.Stride;
            if (!IsReadable((nint)namePtr, arr.Stride))
            {
                sb.Append($" {arr.Label}=unreadable");
                continue;
            }

            // Validate: plausible name = all printable ASCII until a null, ≥ 2 chars.
            int len = 0;
            bool bad = false;
            for (; len < arr.Stride; len++)
            {
                byte b = namePtr[len];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) { bad = true; break; }
            }
            if (bad || len < 2)
            {
                sb.Append($" {arr.Label}=invalid(len={len})");
                continue;
            }

            var s = Encoding.UTF8.GetString(namePtr, len);
            sb.Append($" {arr.Label}=\"{s}\"");
            if (string.IsNullOrEmpty(chosen)) chosen = s;
        }

        Log(sb.ToString());
        return chosen;
    }

    /// <summary>
    /// Item description text from the Item help BMD (like <c>Skill.GetDescription</c>).
    /// Consumable game-item ids start at 0x300 (768), so the help dialog index is
    /// <c>gameItemId - 768</c>. Fully guarded; returns "" if anything's off (the
    /// caller speaks nothing rather than garbage).
    /// </summary>
    internal static string GetDescription(int gameItemId)
    {
        // Item-id → (help BMD, index) by block — matches PlayerMenu.DescribeItem so
        // the in-battle item menu reads descriptions for every block, incl. the
        // GOLDEN consumables (Item2, id 2048+; e.g. Tiny Soul Tomato 2097 was silent
        // because the old code assumed a single 768-base, user 2026-06-17).
        Dialog.HelpBmd bmdId;
        int idx;
        if (gameItemId < 256) { bmdId = Dialog.HelpBmd.Weapon; idx = gameItemId; }
        else if (gameItemId < 512) { bmdId = Dialog.HelpBmd.Armor; idx = gameItemId - 256; }
        else if (gameItemId < 768) { bmdId = Dialog.HelpBmd.Accessory; idx = gameItemId - 512; }
        else if (gameItemId < 1024) { bmdId = Dialog.HelpBmd.Item; idx = gameItemId - 768; }
        else if (gameItemId < 1281) { bmdId = Dialog.HelpBmd.Event; idx = gameItemId - 1024; }
        else if (gameItemId < 1536) { bmdId = Dialog.HelpBmd.Material; idx = gameItemId - 1280; }
        else if (gameItemId < 1792) { bmdId = Dialog.HelpBmd.SkillCard; idx = gameItemId - 1536; }
        else if (gameItemId < 2048) { bmdId = Dialog.HelpBmd.Dress; idx = gameItemId - 1792; }
        else if (gameItemId < 2304) { bmdId = Dialog.HelpBmd.Item2; idx = gameItemId - 2048; }
        else if (gameItemId < 2560) { bmdId = Dialog.HelpBmd.Weapon2; idx = gameItemId - 2304; }
        else return "";
        if (idx < 0) return "";

        var exec = Dialog.GetExecution(bmdId);
        if (!IsReadable((nint)exec, 0x10)) return "";
        var info = exec->Info;
        if (!IsReadable((nint)info, 0x10)) return "";
        var bmd = info->Bmd;
        if (!IsReadable((nint)bmd, 0x30)) return "";
        if (idx > bmd->Header.DialogCount) return "";

        var header = (&bmd->DialogHeaders) + idx;
        if (!IsReadable((nint)header, 0x10)) return "";
        var md = header->MessageDialog;
        if (!IsReadable((nint)md, 0x28) || md->PageCount < 1) return "";

        var page = md->Pages;
        int size = page.TextSize;
        if (size < 1 || size > 1024 || !IsReadable((nint)page.Text, size)) return "";

        return AtlusEncoding.P4.GetString(page.Text, size).Replace('\n', ' ').Replace('\0', ' ').Trim();
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000 || a > 0x00007FFFFFFFFFFFUL) return false;
        const int  MBI_SIZE = 48, OFF_STATE = 32, OFF_PROTECT = 36;
        const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;
        uint state   = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD)    != 0) return false;
        return true;
    }
}
