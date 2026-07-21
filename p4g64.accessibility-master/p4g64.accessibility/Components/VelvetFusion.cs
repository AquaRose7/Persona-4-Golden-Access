using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DavyKager;
using p4g64.accessibility.Native;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Velvet Room FUSION sub-screens (the "cmb"/combine facility).
///
/// The whole fusion flow is ONE facility object, driven per-frame by the
/// dispatcher FUN_140236810 (found via snapshot-diff — see
/// memory/velvet_room_fusion_re.md). It swaps sub-panels by a flag word @ +0xE8;
/// each panel keeps its OWN count/cursor pair, so the reader picks the active
/// panel by flag bit and speaks its highlighted row. Offsets are object-relative,
/// decompiled from each panel's draw fn (all confirmed: count then cursor at +2):
///   bit 0x001  FUN_140226A20  count +0x1F0  cursor +0x1F2   (fusion-type list)
///   bit 0x400  FUN_1402258F0  count +0x188  cursor +0x18A   (first fusion menu)
/// (bit 0x02 FUN_140229190 = the persona-pick LIST, a 0x70-stride scroll list —
/// handled separately once its layout is verified.)
///
/// Hooked by ABSOLUTE address: P4G has several near-identical facility dispatchers
/// sharing the `48 89 5C 24 08 57 48 83 EC 20` prologue, so a SigScan binds the
/// wrong one (learned the hard way). Option NAMES are still TODO (each option
/// carries a labelId; for now it speaks "Option N of M"), so the panel struct is
/// dumped for any unmapped menu-ish panel to decode labels + the persona list.
/// </summary>
internal unsafe class VelvetFusion : IDisposable
{
    private const nint DISPATCH_OFF = 0x236810;   // FUN_140236810 (fusion-facility dispatcher)
    private static readonly byte[] DispatchPro =
        { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20 };

    private const int FLAG = 0xE8;   // uint panel-routing bits

    // --- Skill-INHERITANCE screen ("select skills to inherit"). It's panel bit
    // 0x4000 of the fusion facility's SECOND dispatcher (FUN_1402369A0 @obj+0x98),
    // DRAWN by FUN_140234B20 — which is why the FUN_140236810 hook never surfaced
    // it. The skill list is real data, ALL in the facility object (RE'd from
    // FUN_140234B20 + snapshot-verified, facObj=0x32600210: count=4 @+0x1458,
    // rows {39,192,199,213} @+0x12D8, cursor row 2 @+0x33F2E):
    //   count          u16 @ obj+0x1458
    //   scroll-top     u16 @ obj+0x33F2C
    //   highlighted    u16 @ obj+0x33F2E   (visible row; abs = scroll + this)
    //   skill rows     @ obj+0x12D8, stride 4: +0 = skill id (u16), +2 = flags (u16)
    // We hook the draw fn directly so it fires every frame the screen is up.
    private const nint INH_DRAW_OFF = 0x234B20;   // FUN_140234B20 (inheritance draw)
    private static readonly byte[] InhPro =
        { 0x48, 0x8B, 0xC4, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41 };
    private const uint INH_BIT     = 0x4000;
    private const int  INH_COUNT   = 0x1458;
    private const int  INH_SCROLL  = 0x33F2C;
    private const int  INH_CURSOR  = 0x33F2E;
    private const int  INH_ARRAY   = 0x12D8;
    private delegate void InhDrawDelegate(nint param_1);
    private IHook<InhDrawDelegate>? _inhHook;
    private int _inhLast = -1;

    // --- CHECK COMPENDIUM. A SEPARATE velvet facility driven by FUN_140265060
    // (flag word @obj+0xF0, NOT the fusion 0xE8). Two screens used:
    //   bit 0x01 (FUN_14025BC80) = L2 option menu "What will you do?" — text rows.
    //   bit 0x02 (FUN_14025E1D0) = the "All Personas" LIST. Offsets (RE'd):
    //     count @obj+0x3AD8 (int) · highlighted visible row @obj+0x3ADC (short) ·
    //     scroll-top @obj+0x3ADE (short) -> abs = scroll + row; per-row entry @
    //     obj+0x2D8 + abs*0x38 {id @+0x02 (u16), level @+0x04 (byte), flag @+0x34
    //     (bit1 = registered)}; the cost (¥) offset within the entry is TBD (logged).
    private const nint COMP_DISP_OFF = 0x265060;   // FUN_140265060 (compendium dispatcher)
    private const int  COMP_FLAG     = 0xF0;
    private const int  CL_COUNT      = 0x3AD8;
    private const int  CL_CURSOR     = 0x3ADC;
    private const int  CL_SCROLL     = 0x3ADE;
    private const int  CL_ENTRY_BASE = 0x2D8;
    private const int  CL_ENTRY_STRIDE = 0x38;
    private IHook<DispatchDelegate>? _compHook;
    private bool _compFired;
    private uint _compLastFlag = 0xFFFFFFFF;
    private int  _compLast = -1;

    // --- Velvet ROOT/TOP menu (Fuse Personas / Check Compendium / Utilize Skill Cards
    // / Manage Rescue Requests / Check on Dwellers / Leave Velvet Room). Found by
    // accident hunting the Ask menu: its facility dispatcher is FUN_14021E2A0 (flag
    // word @obj+0xF0): bit 0x01 = the text menu (FUN_14021BBE0). This is the SAME root
    // menu VelvetMenu.cs read via a slow ~100ms poll scan (the documented cursor lag) —
    // hooking the dispatcher reads it INSTANTLY, so VelvetMenu's poll is retired.
    // Menu: count @obj+0x288, highlighted visible row @obj+0x292, scroll @obj+0x294
    // (abs = scroll + row); per-row char* label @ *(obj+0x1F0 + abs*0x18).
    private const nint ROOT_DISP_OFF = 0x21E2A0;   // FUN_14021E2A0 (root-menu dispatcher)
    private static readonly byte[] RootPro =
        { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x50 };
    private IHook<DispatchDelegate>? _rootHook;
    private bool _rootFired;
    private uint _rootLastFlag = 0xFFFFFFFF;
    private int  _rootLast = -1;

    // --- Two more velvet TEXT menus (user-verified): FacA = Utilize SKILL CARDS
    // (Buy / Give Skill Cards / Ask / Cancel), FacB = Manage RESCUE REQUESTS (Create
    // Message / Check Rescue Log / Ask / Cancel). Each dispatcher calls its menu draw
    // every frame; we read the highlighted char* label (validated + deduped):
    //   FacA (Skill Cards): dispatcher FUN_14028C010 -> menu FUN_140287E10
    //         count @obj+0xE8, cursor @obj+0xEA, scroll @obj+0xEC, label @ *(obj+0x98+abs*0x18)
    //   FacB (Rescue):      dispatcher FUN_14029F190 -> menu FUN_140299220
    //         count @obj+0xE0, cursor @obj+0xE2, scroll @obj+0xE4, label @ *(obj+0x90+abs*0x18)
    private const nint FACA_OFF = 0x28C010;
    private static readonly byte[] FacAPro = { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x18, 0x56, 0x48, 0x83 };
    private const nint FACB_OFF = 0x29F190;
    private static readonly byte[] FacBPro = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20 };
    private IHook<DispatchDelegate>? _facAHook, _facBHook;
    private bool _facAFired, _facBFired;
    private int  _facALast = -1, _facBLast = -1;
    private int  _facASubRow = -1;       // Buy/Give skill-card list dedupe

    // --- The "ASK" tutorial-topic menu (fcl_cmb_talk facility). Per-frame handler
    // FUN_140221DF0 (param_2 = facility obj). The menu is a LINKED LIST: menu-data ptr
    // @obj+0x90, count @*(int*)(menuData+0xC), cursor @obj+0x98 (short), scroll @obj+0x9A;
    // entries are a linked list from *(menuData+0x10) walked via +0x18; each entry's
    // label char* = *(entry+0x20) + 8. (RE'd from FUN_140220F80; snapshot-verified
    // obj=0x2E960A50, cursor obj+0x98.)
    private const nint ASKM_OFF = 0x221DF0;   // FUN_140221DF0 (ask-menu handler)
    private static readonly byte[] AskMPro =
        { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x50 };
    private IHook<DispatchDelegate>? _askMenuHook;
    private bool _askMenuFired;
    private int  _askMenuLast = -1;

    // Active-input panels, in focus priority (first match wins when several bits
    // are set during a transition). Names are bound by cursor position from the
    // user's screenshots (database/player menu/velvetroomL2|L3.jpeg); used only
    // when the option count matches, so a reduced menu falls back to positional
    // rather than mislabelling.
    private readonly struct Panel
    {
        public readonly uint Bit; public readonly int Count, Cursor, IdOff, Stride; public readonly string Tag;
        public readonly int[] Ids; public readonly string[] Names;
        public Panel(uint bit, int count, int cursor, int idOff, int stride, string tag, int[] ids, string[] names)
        { Bit = bit; Count = count; Cursor = cursor; IdOff = idOff; Stride = stride; Tag = tag; Ids = ids; Names = names; }
    }
    // The packed option's stable id sits at id[cursor] = *(short*)(obj + IdOff +
    // cursor*Stride). When a panel has Ids, names bind by ID (robust to options being
    // added/removed/reordered — a new id just speaks a number until mapped). When
    // Ids is null, names bind by cursor POSITION, guarded on the count matching.
    private static readonly Panel[] Panels =
    {
        // L3 fusion-method menu. Option id (a glyph index, stable per option) @
        // obj+0x1D0 + cursor*4 (RE'd from FUN_140226A20). Ids confirmed live:
        // 7=Search, 0=Normal, 1=Triangle, 6=Back. Cross/Pentagon/Hexagon (the
        // spread fusions, unlocked 2026-07-03) = 2/3/4 by the glyph-id sequence —
        // an UNKNOWN id logs itself so a wrong guess is caught in one visit.
        new(0x001, 0x1F0, 0x1F2, 0x1D0, 4, "method-menu", new[] { 7, 0, 1, 2, 3, 4, 6 },
            new[] { "Search", "Normal", "Triangle", "Cross", "Pentagon", "Hexagon", "Back" }),
        // L2 fusion facility menu, id-based (ids confirmed live 6/56/61/63 @ +0xFA).
        new(0x400, 0x188, 0x18A, 0x0FA, 0x18, "fuse-menu", new[] { 6, 56, 61, 63 },
            new[] { "Fuse", "Fusion Forecast", "Talk", "Back" }),
    };

    private delegate void DispatchDelegate(nint param_1, nint param_2);
    private IHook<DispatchDelegate>? _hook;

    private bool _fired;
    private uint _lastFlag = 0xFFFFFFFF;

    // Fusion inherit capacity (+0x145A on the facility object): max skills the result
    // persona can inherit, or 0 if not on a fusion-result/inherit screen. FIXED per
    // fusion. Used in the result persona panel's "Inheritance" section.
    private static unsafe int InheritCap(nint obj)
    {
        if (!IsReadable(obj + 0x145A)) return 0;
        int c = *(ushort*)(obj + 0x145A);
        return (c >= 1 && c <= 8) ? c : 0;
    }
    private static string InheritCapText(int cap) => $"Can inherit {cap} skill{(cap == 1 ? "" : "s")}";
    private uint _lastPanelBit;
    private int  _lastCursor = -1;
    private int  _dumps;
    private int  _idLogs;

    internal VelvetFusion(IReloadedHooks hooks)
    {
        nint addr = BaseAddress + DISPATCH_OFF;
        var got = new StringBuilder();
        bool ok = true;
        for (int i = 0; i < DispatchPro.Length; i++)
        {
            byte b = ((byte*)addr)[i];
            got.Append(b.ToString("X2")).Append(' ');
            if (b != DispatchPro[i]) ok = false;
        }
        if (!ok) { LogError($"[VelvetFusion] dispatcher prologue MISMATCH @ 0x{(ulong)addr:X} got {got}- not hooking."); return; }

        _hook = hooks.CreateHook<DispatchDelegate>(OnDispatch, addr).Activate();
        Log($"[VelvetFusion] hooked fusion dispatcher FUN_140236810 @ 0x{(ulong)addr:X}.");

        // Inheritance/skill-select draw — separate hook so it fires every frame
        // the screen is shown (its dispatcher FUN_1402369A0 is not the one above).
        nint ia = BaseAddress + INH_DRAW_OFF;
        bool iok = true;
        for (int i = 0; i < InhPro.Length; i++) if (((byte*)ia)[i] != InhPro[i]) iok = false;
        if (iok)
        {
            _inhHook = hooks.CreateHook<InhDrawDelegate>(OnInhDraw, ia).Activate();
            Log($"[VelvetFusion] hooked inheritance draw FUN_140234B20 @ 0x{(ulong)ia:X}.");
        }
        else LogError($"[VelvetFusion] inheritance-draw prologue MISMATCH @ 0x{(ulong)ia:X} — not hooking.");

        // Compendium dispatcher (separate velvet facility, flag @obj+0xF0). Same
        // shared prologue as the fusion dispatcher; hooked by absolute address.
        nint ca = BaseAddress + COMP_DISP_OFF;
        bool cok = true;
        for (int i = 0; i < DispatchPro.Length; i++) if (((byte*)ca)[i] != DispatchPro[i]) cok = false;
        if (cok)
        {
            _compHook = hooks.CreateHook<DispatchDelegate>(OnCompDispatch, ca).Activate();
            Log($"[VelvetFusion] hooked compendium dispatcher FUN_140265060 @ 0x{(ulong)ca:X}.");
        }
        else LogError($"[VelvetFusion] compendium-dispatcher prologue MISMATCH @ 0x{(ulong)ca:X} — not hooking.");

        // Velvet ROOT/TOP-menu dispatcher (FUN_14021E2A0) — reads the root menu fast,
        // replacing VelvetMenu.cs's laggy poll.
        nint aa = BaseAddress + ROOT_DISP_OFF;
        bool aok = true;
        for (int i = 0; i < RootPro.Length; i++) if (((byte*)aa)[i] != RootPro[i]) aok = false;
        if (aok)
        {
            _rootHook = hooks.CreateHook<DispatchDelegate>(OnRootDispatch, aa).Activate();
            Log($"[VelvetFusion] hooked velvet root-menu dispatcher FUN_14021E2A0 @ 0x{(ulong)aa:X}.");
        }
        else LogError($"[VelvetFusion] root-menu-dispatcher prologue MISMATCH @ 0x{(ulong)aa:X} — not hooking.");

        // Two more velvet text-menu facilities (identity TBD).
        nint fa = BaseAddress + FACA_OFF;
        bool faok = true;
        for (int i = 0; i < FacAPro.Length; i++) if (((byte*)fa)[i] != FacAPro[i]) faok = false;
        if (faok) { _facAHook = hooks.CreateHook<DispatchDelegate>(OnFacA, fa).Activate(); Log($"[VelvetFusion] hooked velvet menu facility A FUN_14028C010 @ 0x{(ulong)fa:X}."); }
        else LogError($"[VelvetFusion] facility-A prologue MISMATCH @ 0x{(ulong)fa:X} — not hooking.");

        nint fb = BaseAddress + FACB_OFF;
        bool fbok = true;
        for (int i = 0; i < FacBPro.Length; i++) if (((byte*)fb)[i] != FacBPro[i]) fbok = false;
        if (fbok) { _facBHook = hooks.CreateHook<DispatchDelegate>(OnFacB, fb).Activate(); Log($"[VelvetFusion] hooked velvet menu facility B FUN_14029F190 @ 0x{(ulong)fb:X}."); }
        else LogError($"[VelvetFusion] facility-B prologue MISMATCH @ 0x{(ulong)fb:X} — not hooking.");

        // The "Ask" tutorial-topic menu (fcl_cmb_talk).
        nint ma = BaseAddress + ASKM_OFF;
        bool maok = true;
        for (int i = 0; i < AskMPro.Length; i++) if (((byte*)ma)[i] != AskMPro[i]) maok = false;
        if (maok) { _askMenuHook = hooks.CreateHook<DispatchDelegate>(OnAskMenu, ma).Activate(); Log($"[VelvetFusion] hooked Ask-menu handler FUN_140221DF0 @ 0x{(ulong)ma:X}."); }
        else LogError($"[VelvetFusion] ask-menu prologue MISMATCH @ 0x{(ulong)ma:X} — not hooking.");
    }

    private void OnAskMenu(nint param_1, nint obj)
    {
        if (!_askMenuFired) { _askMenuFired = true; Log($"[VelvetFusion] Ask-menu handler FIRED (obj=0x{(ulong)obj:X})."); }
        _askMenuHook!.OriginalFunction(param_1, obj);
        try { ReadAskMenu(obj); } catch { }
        try { PollMoney(); } catch { }
    }

    /// <summary>Ask tutorial-topic menu — walks the entry linked list to the highlighted
    /// row and speaks its label.</summary>
    private void ReadAskMenu(nint obj)
    {
        if (obj == 0 || !IsReadable(obj + 0x98) || !IsReadable(obj + 0x90)) return;
        nint menuData = *(nint*)(obj + 0x90);
        if (menuData <= 0x10000 || !IsReadable(menuData + 0x10)) { _askMenuLast = -1; return; }
        int count = IsReadable(menuData + 0xC) ? *(int*)(menuData + 0xC) : 0;
        if (count < 1 || count > 64) { _askMenuLast = -1; return; }
        int abs = *(short*)(obj + 0x9A) + *(short*)(obj + 0x98);
        if (abs < 0 || abs >= count) return;

        nint node = *(nint*)(menuData + 0x10);
        for (int i = 0; i < abs; i++)
        {
            if (node <= 0x10000 || !IsReadable(node + 0x18)) return;
            node = *(nint*)(node + 0x18);
        }
        if (node <= 0x10000 || !IsReadable(node + 0x20)) return;
        nint ls = *(nint*)(node + 0x20);
        if (ls <= 0x10000) return;
        string label = ReadAscii(ls + 8, 64);
        if (string.IsNullOrEmpty(label)) return;
        if (abs == _askMenuLast) return;
        _askMenuLast = abs;
        Speech.Say(label, interrupt: true);
    }

    private void OnFacA(nint param_1, nint obj)
    {
        if (!_facAFired) { _facAFired = true; Log($"[VelvetFusion] velvet menu facility A FIRED (obj=0x{(ulong)obj:X})."); }
        _facAHook!.OriginalFunction(param_1, obj);
        // When a Buy/Give skill-card LIST sub-screen is open, read it; otherwise the
        // top menu (Buy/Give/Ask/Cancel).
        try { if (!ReadSkillCardList(obj)) ReadTextMenu(obj, 0xE8, 0xEA, 0xEC, 0x98, ref _facALast); } catch { }
        try { PollMoney(); } catch { }
    }

    /// <summary>
    /// Buy / Give Skill Cards LIST sub-screen (RE'd 2026-06-18 from the FacA draws
    /// FUN_140288470 / FUN_14028ADE0). A panel is showing when its visibility bit
    /// (bit 2) is set: GIVE @ obj+0x265, the other (Buy/detail) @ obj+0x26e. The
    /// shared list: cursor @ obj+0x2a4, scroll @ obj+0x2a6 (abs = sum); entry @
    /// obj+0x6fc + abs*0x0C; card id = u16 @ entry+0 (0x600 = block-base terminator).
    /// Card id → Item.GetName/GetDescription (skill-card block 1536-1791). Returns
    /// true if a list panel is open (so OnFacA suppresses the top-menu read).
    /// </summary>
    private bool ReadSkillCardList(nint obj)
    {
        // The card LIST is showing when its panel-visible bit (bit 2) is set:
        // GIVE @ obj+0x1340, BUY @ obj+0x1328 (both live-confirmed; the top-menu gate
        // 0x1310 drops to 0 then). List cursor @ obj+0x2a4, scroll @ obj+0x2a6 (abs =
        // sum); entry @ obj+0x6fc + abs*0x0C; card id = u16 @ entry+0. The GIVE list's
        // entry 0 (id 1536) is the "Register all" header; the BUY list has none.
        if (obj == 0 || !IsReadable(obj + 0x1328) || !IsReadable(obj + 0x1340)) return false;
        bool give = (*(byte*)(obj + 0x1340) & 4) != 0;
        bool buy  = (*(byte*)(obj + 0x1328) & 4) != 0;
        if (!give && !buy) { _facASubRow = -1; return false; }
        if (!IsReadable(obj + 0x2a4) || !IsReadable(obj + 0x2a6)) return true;
        int abs = *(short*)(obj + 0x2a4) + *(short*)(obj + 0x2a6);
        if (abs < 0 || abs > 255) return true;
        nint e = obj + 0x6fc + (nint)abs * 0xc;
        if (!IsReadable(e)) return true;
        ushort id = *(ushort*)e;
        if (id == 0) { _facASubRow = -1; return true; }

        int key = (id << 8) | (abs & 0xFF);
        if (key == _facASubRow) return true;
        _facASubRow = key;

        // Entry id 1536 (skill-card block base) is the bulk header — the "Register all"
        // row at the top of the list (the sell-screen "Sell all" analogue). GetName is
        // empty for it, so label it explicitly.
        string name = id == 1536 ? "Register all" : Item.GetName(id);
        if (string.IsNullOrEmpty(name)) return true;   // any other blank row — silent
        string msg = name;
        if (id != 1536)
        {
            // BUY shows each card's cost (entry+8, yen — live-verified Sonic Punch 10000 /
            // Rakunda 5000). GIVE (registering) is free, so no price there.
            if (buy) { uint price = *(uint*)(e + 8); if (price > 0) msg += $". {price} yen"; }
            string desc = Item.GetDescription(id);
            if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
        }
        Log($"[VelvetFusion] SkillCard sub[{abs}] {(buy ? "buy" : "give")} id={id} \"{name}\"");
        Speech.Say(msg, interrupt: true);
        return true;
    }

    private void OnFacB(nint param_1, nint obj)
    {
        if (!_facBFired) { _facBFired = true; Log($"[VelvetFusion] velvet menu facility B FIRED (obj=0x{(ulong)obj:X})."); }
        _facBHook!.OriginalFunction(param_1, obj);
        try { ReadTextMenu(obj, 0xE0, 0xE2, 0xE4, 0x90, ref _facBLast); } catch { }
        try { PollMoney(); } catch { }
    }

    /// <summary>Generic velvet text-menu reader: speaks the highlighted char* label
    /// (selected abs index = scroll + cursor) on change. Logs the option once per
    /// row so the menu can be identified.</summary>
    private void ReadTextMenu(nint obj, int countOff, int curOff, int scrOff, int labelBase, ref int last)
    {
        if (obj == 0 || !IsReadable(obj + countOff) || !IsReadable(obj + curOff) || !IsReadable(obj + scrOff)) return;
        int count = *(short*)(obj + countOff);
        if (count < 1 || count > 32) { last = -1; return; }
        int abs = *(short*)(obj + scrOff) + *(short*)(obj + curOff);
        if (abs < 0 || abs >= count) return;
        nint pe = obj + labelBase + (nint)abs * 0x18;
        if (!IsReadable(pe)) return;
        string label = ReadAscii(*(nint*)pe, 64);
        if (string.IsNullOrEmpty(label)) return;
        if (abs == last) return;
        last = abs;
        Speech.Say(label, interrupt: true);
    }

    private void OnRootDispatch(nint param_1, nint obj)
    {
        if (!_rootFired) { _rootFired = true; Log($"[VelvetFusion] velvet root-menu dispatcher FIRED (obj=0x{(ulong)obj:X})."); }
        _rootHook!.OriginalFunction(param_1, obj);
        try { ReadRootMenu(obj); } catch { }
        try { PollMoney(); } catch { }
    }

    /// <summary>Velvet ROOT/TOP menu (bit 0x01): reads the highlighted text option.</summary>
    private void ReadRootMenu(nint obj)
    {
        if (obj == 0 || !IsReadable(obj + 0xF0)) return;
        uint flag = *(ushort*)(obj + 0xF0);
        if (flag != _rootLastFlag) { _rootLastFlag = flag; }
        if ((flag & 0x01) == 0) { _rootLast = -1; return; }

        if (!IsReadable(obj + 0x288) || !IsReadable(obj + 0x292) || !IsReadable(obj + 0x294)) return;
        int count = *(short*)(obj + 0x288);
        if (count < 1 || count > 32) { _rootLast = -1; return; }
        int abs = *(short*)(obj + 0x294) + *(short*)(obj + 0x292);
        if (abs < 0 || abs >= count) return;
        nint pe = obj + 0x1F0 + (nint)abs * 0x18;
        if (!IsReadable(pe)) return;
        string label = ReadAscii(*(nint*)pe, 64);
        if (string.IsNullOrEmpty(label)) return;
        if (abs == _rootLast) return;
        _rootLast = abs;
        Speech.Say(label, interrupt: true);
    }

    private void OnCompDispatch(nint param_1, nint obj)
    {
        if (!_compFired) { _compFired = true; Log($"[VelvetFusion] compendium dispatcher FIRED (obj=0x{(ulong)obj:X})."); }
        _compHook!.OriginalFunction(param_1, obj);
        try { ReadCompendium(obj); } catch { }
        try { PollMoney(); } catch { }
    }

    // G key — announce the player's money (wallet @0x1451BCD70, same global the shop
    // reader uses), so you can check it against a persona's compendium summon cost.
    // In the Velvet Room it ALSO speaks the PROTAGONIST LEVEL (user request 2026-07-20):
    // the ceiling on which persona you can fuse, handy when you've forgotten it. MC
    // level = byte at the party-member array +0x06 (PlayerMenu's StatusLevel offset).
    private const int VK_G = 0x47;
    private bool _gWas;
    private static readonly nint WALLET = unchecked((nint)0x1451BCD70L);
    private static readonly nint MC_LEVEL = unchecked((nint)(0x1451BD9E4L + 0x06));
    /// <summary>TickCount64 stamped every frame the Velvet Room UI updates (PollMoney
    /// runs from all its render hooks). PlayerMenu's camp-G defers to us while this is
    /// fresh, so G isn't spoken twice in the Velvet Room (money+level here vs money there).</summary>
    internal static long LastVelvetTick;

    private void PollMoney()
    {
        LastVelvetTick = Environment.TickCount64;
        if (!Utils.GameHasFocus()) return;   // ignore G while alt-tabbed
        bool g = (GetAsyncKeyState(VK_G) & 0x8000) != 0;
        if (g && !_gWas)
        {
            _gWas = true;
            uint w = IsReadable(WALLET) ? *(uint*)WALLET : 0;
            int lv = IsReadable(MC_LEVEL) ? *(byte*)MC_LEVEL : 0;
            string money = $"You have {w} yen";
            Speech.Say(lv > 0 ? $"{money}. You are level {lv}" : money, interrupt: true);
        }
        else if (!g) _gWas = false;
    }

    /// <summary>TickCount64 of the last frame the compendium persona PROFILE (Info/
    /// Status tabs) was open — the gate for CompendiumInfoText's flavor-text capture.</summary>
    internal static long CompendiumProfileTick;

    private void ReadCompendium(nint obj)
    {
        if (obj == 0 || !IsReadable(obj + COMP_FLAG)) return;
        uint flag = *(uint*)(obj + COMP_FLAG);
        _compLastFlag = flag;   // (per-change flag log removed in the v1.3.5 cleanup)

        // REGISTER PERSONAS (bit 0x04, FUN_1402628F0) — your personas to register.
        // Checked first: its flags (0x04/0x05/0x2C) are distinct from View Compendium.
        // With the detail bit 0x08 also set (flag 0x2C) it's the persona PANEL.
        if ((flag & 0x04) != 0)
        {
            if ((flag & 0x08) != 0) ReadCompendiumRegisterPanel(obj);
            else ReadCompendiumRegister(obj);
            return;
        }
        _regLast = -1; _regPanelId = -1;

        // Persona PROFILE in the compendium (bit 0x08 added over the list) — read the
        // selected persona's full panel. Checked before the list (0x1A has both).
        if ((flag & 0x08) != 0)
        {
            CompendiumProfileTick = Environment.TickCount64;   // gate for the Info-text capture
            ReadCompendiumProfile(obj);
            return;
        }
        _compProfId = -1;

        // "All Personas" persona LIST (bit 0x02, FUN_14025E1D0) — the deeper screen.
        if ((flag & 0x02) != 0) { ReadCompendiumList(obj); return; }
        _compLast = -1;

        // L2 option menu "What will you do?" (bit 0x01, FUN_14025BC80): text rows
        // (char* @ *(obj+0x110+abs*0x18)), count @+0x160, cursor @+0x162, scroll @+0x164.
        if ((flag & 0x01) != 0) { ReadCompendiumL2(obj); return; }
        _compL2Last = -1;
    }

    // REGISTER PERSONAS list (bit 0x04, FUN_1402628F0): count @obj+0x3D20 (int),
    // highlighted visible row @obj+0x3D28 (short), scroll @obj+0x3D2A (short) ->
    // abs = scroll + row; per-row persona record ptr @ *(obj+0x3CB8 + abs*8)
    // {id @+0x02, level @+0x04}. Shows the player's own personas.
    private const int REG_COUNT   = 0x3D20;
    private const int REG_CURSOR  = 0x3D28;
    private const int REG_SCROLL  = 0x3D2A;
    private const int REG_ENTRIES = 0x3CB8;
    private int _regLast = -1;
    private void ReadCompendiumRegister(nint obj)
    {
        if (!IsReadable(obj + REG_COUNT) || !IsReadable(obj + REG_CURSOR) || !IsReadable(obj + REG_SCROLL)) return;
        int count = *(int*)(obj + REG_COUNT);
        if (count < 1 || count > 8192) { _regLast = -1; return; }
        int abs = *(short*)(obj + REG_SCROLL) + *(short*)(obj + REG_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint pe = obj + REG_ENTRIES + (nint)abs * 8;
        if (!IsReadable(pe)) return;
        nint entry = *(nint*)pe;
        if (entry <= 0x10000 || !IsReadable(entry + 4)) return;
        int id = *(ushort*)(entry + 2);
        if (id < 1 || id > 400) return;
        int key = (abs << 16) | id;
        if (key == _regLast) return;
        _regLast = key;
        int lvl = *(byte*)(entry + 4);
        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arc = PanelArcana(id);
        Speech.Say(string.IsNullOrEmpty(arc) ? $"{name}, level {lvl}" : $"{name}, {arc}, level {lvl}", interrupt: true);
    }

    // Register-list persona PANEL (bit 0x04 + 0x08). The entry ptr is the persona's
    // stock PersonaInfo record, so the owned-persona panel builder applies directly
    // (real stats via PersonaTotalStats, current skills @record+0x0C).
    private int _regPanelId = -1;
    private void ReadCompendiumRegisterPanel(nint obj)
    {
        if (!IsReadable(obj + REG_COUNT) || !IsReadable(obj + REG_CURSOR) || !IsReadable(obj + REG_SCROLL)) return;
        int count = *(int*)(obj + REG_COUNT);
        if (count < 1 || count > 8192) { _regPanelId = -1; return; }
        int abs = *(short*)(obj + REG_SCROLL) + *(short*)(obj + REG_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint pe = obj + REG_ENTRIES + (nint)abs * 8;
        if (!IsReadable(pe)) return;
        nint entry = *(nint*)pe;
        if (entry <= 0x10000 || !IsReadable(entry + 4)) return;
        int id = *(ushort*)(entry + 2);
        if (id < 1 || id > 400) return;
        int lvl = *(byte*)(entry + 4);

        if (id != _regPanelId)
        {
            _regPanelId = id;
            _panelSecs = BuildOwnedSections(entry, id, lvl);
            _panelSec = 0; _panelItem = 0;
            AnnounceSection();
        }
        PanelInput();
    }

    private int _compL2Last = -1;
    private void ReadCompendiumL2(nint obj)
    {
        if (!IsReadable(obj + 0x160) || !IsReadable(obj + 0x162) || !IsReadable(obj + 0x164)) return;
        int count = *(short*)(obj + 0x160);
        if (count < 1 || count > 32) { _compL2Last = -1; return; }
        int abs = *(short*)(obj + 0x164) + *(short*)(obj + 0x162);
        if (abs < 0 || abs >= count) return;
        nint pe = obj + 0x110 + (nint)abs * 0x18;
        if (!IsReadable(pe)) return;
        string label = ReadAscii(*(nint*)pe, 48);
        if (string.IsNullOrEmpty(label)) return;
        if (abs == _compL2Last) return;
        _compL2Last = abs;
        Speech.Say(label, interrupt: true);
    }

    private static string ReadAscii(nint p, int max)
    {
        if (p <= 0x10000 || !IsReadable(p)) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < max; i++)
        {
            if (!IsReadable(p + i)) break;
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b); else break;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>"All Personas" compendium list (bit 0x02, FUN_14025E1D0). Entry is
    /// inline at obj+0x2D8 + abs*0x38: id @+0x02, level @+0x04, cost(¥) @+0x30 (uint),
    /// registered flag @+0x34 bit0 — set means UNREGISTERED (drawn as "??"), so
    /// registered = bit clear. Registered rows read name/arcana/level/cost; unregistered
    /// rows read arcana/level + "not registered" (the game hides their name).</summary>
    private void ReadCompendiumList(nint obj)
    {
        if (!IsReadable(obj + CL_COUNT) || !IsReadable(obj + CL_CURSOR) || !IsReadable(obj + CL_SCROLL)) return;
        int count = *(int*)(obj + CL_COUNT);
        if (count < 1 || count > 8192) { _compLast = -1; return; }
        int abs = *(short*)(obj + CL_SCROLL) + *(short*)(obj + CL_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint e = obj + CL_ENTRY_BASE + (nint)abs * CL_ENTRY_STRIDE;
        if (!IsReadable(e + 0x34)) return;
        int id = *(ushort*)(e + 2);
        if (id < 1 || id > 400) return;

        int key = (abs << 16) | id;
        if (key == _compLast) return;
        _compLast = key;

        int lvl = *(byte*)(e + 4);
        bool registered = (*(byte*)(e + 0x34) & 1) == 0;
        string arc = PanelArcana(id);
        string spoken;
        if (registered)
        {
            string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
            uint cost = *(uint*)(e + 0x30);   // summon cost (¥)
            var sb = new StringBuilder(string.IsNullOrEmpty(arc) ? $"{name}, level {lvl}" : $"{name}, {arc}, level {lvl}");
            if (cost > 0)
            {
                uint wallet = IsReadable(WALLET) ? *(uint*)WALLET : 0;
                sb.Append($", {cost} yen, you have {wallet} yen");
            }
            spoken = sb.ToString();
        }
        else
        {
            spoken = string.IsNullOrEmpty(arc) ? $"level {lvl}, not registered" : $"{arc}, level {lvl}, not registered";
        }
        Speech.Say(spoken, interrupt: true);
    }

    // Persona PROFILE in the compendium (bit 0x08): the selected list persona's full
    // panel, navigable I/K/J/L. Stats are the registered instance's actual values
    // (list entry +0x1C, 5 bytes); skills via the species learnset; resistances /
    // arcana / next-learn by id; plus the summon cost (entry +0x30).
    private int _compProfId = -1;
    private void ReadCompendiumProfile(nint obj)
    {
        if (!IsReadable(obj + CL_COUNT) || !IsReadable(obj + CL_CURSOR) || !IsReadable(obj + CL_SCROLL)) return;
        int count = *(int*)(obj + CL_COUNT);
        if (count < 1 || count > 8192) { _compProfId = -1; return; }
        int abs = *(short*)(obj + CL_SCROLL) + *(short*)(obj + CL_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint e = obj + CL_ENTRY_BASE + (nint)abs * CL_ENTRY_STRIDE;
        if (!IsReadable(e + 0x34)) return;
        int id = *(ushort*)(e + 2);
        if (id < 1 || id > 400) return;
        int lvl = *(byte*)(e + 4);

        CompendiumProfileId = id;   // keys the Info-tab flavor-text capture
        if (id != _compProfId)
        {
            _compProfId = id;
            _panelSecs = BuildCompendiumProfile(e, id, lvl);
            _panelSec = 0; _panelItem = 0;
            AnnounceSection();
        }
        // (The Info tab's flavor text is spoken by CompendiumInfoText itself, on every
        // render of the tab — the user preferred that over an I/K "Description" section.)
        PanelInput();
    }

    /// <summary>Persona id currently shown in the compendium profile (0 = none) —
    /// keys CompendiumInfoText's flavor-text capture.</summary>
    internal static volatile int CompendiumProfileId;

    private List<(string, string, List<string>)> BuildCompendiumProfile(nint e, int id, int lvl)
    {
        var secs = new List<(string, string, List<string>)>();
        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arcana = PanelArcana(id);
        var overItems = new List<string> { name };
        if (!string.IsNullOrEmpty(arcana)) overItems.Add(arcana);
        overItems.Add($"level {lvl}");
        uint cost = IsReadable(e + 0x30) ? *(uint*)(e + 0x30) : 0;
        if (cost > 0)
        {
            uint wallet = IsReadable(WALLET) ? *(uint*)WALLET : 0;
            overItems.Add($"{cost} yen");
            overItems.Add($"you have {wallet} yen");
        }
        secs.Add((null, string.Join(", ", overItems), overItems));

        var res = new List<string>();
        foreach (var (eid, enm) in Battle.Battle.ProfileElements)
            try { res.Add(Battle.Battle.PersonaElementAffinityText(id, eid, enm)); } catch { }
        if (res.Count > 0) secs.Add(("Resistances", string.Join(", ", res), res));

        // Stats: the registered instance's actual values (entry +0x1C, 5 bytes).
        if (IsReadable(e + 0x20))
        {
            byte* b = (byte*)(e + 0x1C);
            var stats = new List<string>
            { $"Strength {b[0]}", $"Magic {b[1]}", $"Endurance {b[2]}", $"Agility {b[3]}", $"Luck {b[4]}" };
            secs.Add(("Stats", string.Join(", ", stats), stats));
        }

        var skNames = new List<string>();
        var skDetail = new List<string>();
        // The REGISTERED instance's REAL skills: u16[8] @ entry+0x0C (decoded 2026-07-04
        // from the CompDiag dump vs the Hua Po screenshot — [2,222,512,4,40,42,195,97] =
        // Agilao, Rakukaja, Dodge Ice, Maragi, Zionga, Mazio, Media, Makajam, the exact
        // on-screen order; exp u32 @+0x08). The old species-LEARNSET derivation spoke the
        // wrong skills whenever the registered set differed (user report).
        if (IsReadable(e + 0x1C))
        {
            for (int s = 0; s < 8; s++)
            {
                int sid = *(ushort*)(e + 0x0C + s * 2);
                if (sid < 1 || sid > 2000) continue;
                string n2; try { n2 = Skill.GetName(sid); } catch { n2 = null; }
                if (string.IsNullOrEmpty(n2)) continue;
                skNames.Add(n2);
                skDetail.Add(SkillLine(sid));
            }
        }
        if (skNames.Count == 0)
        {
            // fallback (unreadable entry): the species learnset — better than silence
            List<int> learned; try { learned = Battle.Battle.PersonaLearnedSkills(id, lvl); } catch { learned = new List<int>(); }
            foreach (int sid in learned)
            {
                if (sid < 1 || sid > 900) continue;
                string n2; try { n2 = Skill.GetName(sid); } catch { n2 = null; }
                if (string.IsNullOrEmpty(n2)) continue;
                skNames.Add(n2);
                skDetail.Add(SkillLine(sid));
            }
        }
        if (skNames.Count > 0) secs.Add(("Skills", string.Join(", ", skNames), skDetail));

        try
        {
            if (Battle.Battle.PersonaNextLearn(id, lvl, out int lvlLearn, out int sid) && sid > 0)
            {
                string nm; try { nm = Skill.GetName(sid); } catch { nm = $"skill {sid}"; }
                var nextItems = new List<string> { $"next skill at level {lvlLearn}, {SkillLine(sid)}" };
                secs.Add((null, $"next skill at level {lvlLearn}, {nm}", nextItems));
            }
        }
        catch { }

        return secs;
    }

    private void OnDispatch(nint param_1, nint obj)
    {
        if (!_fired) { _fired = true; Log($"[VelvetFusion] fusion dispatcher FIRED (obj=0x{(ulong)obj:X})."); }
        _hook!.OriginalFunction(param_1, obj);
        try { Read(obj); } catch { }
        try { PollMoney(); } catch { }
    }

    // FUN_140234B20(facObj) draws the skill-inheritance screen every frame it's up.
    private void OnInhDraw(nint obj)
    {
        _inhHook!.OriginalFunction(obj);
        try { ReadInheritance(obj); } catch { }
    }

    /// <summary>"Select skills to inherit" list. Reads the highlighted skill id from
    /// the facility object's skill-row array and speaks name + description on move.</summary>
    private void ReadInheritance(nint obj)
    {
        if (obj == 0 || !IsReadable(obj + INH_COUNT) || !IsReadable(obj + INH_CURSOR)) return;
        int count  = *(ushort*)(obj + INH_COUNT);
        if (count < 1 || count > 64) { _inhLast = -1; return; }
        int scroll = *(ushort*)(obj + INH_SCROLL);
        int abs    = scroll + *(ushort*)(obj + INH_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint e = obj + INH_ARRAY + (nint)abs * 4;
        if (!IsReadable(e)) return;
        int sid = *(ushort*)e;
        if (sid < 1 || sid > 900) return;

        // Remaining inherit slots = capacity (+0x145A) - SELECTED. A skill row is
        // selected when bit 0x0001 of its flags (+0x12D8 + i*4 + 2) is set — live-
        // verified 2026-07-09 (the flag toggles per pick; +0x1464/+0x1466 are static
        // and were wrong). Include `rem` in the dedupe key so PICKING a skill (cursor
        // may not move) re-announces with the updated count.
        int cap = InheritCap(obj);
        int selected = 0;
        for (int i = 0; i < count; i++)
        { nint fe = obj + INH_ARRAY + (nint)i * 4; if (IsReadable(fe + 2) && (*(ushort*)(fe + 2) & 1) != 0) selected++; }
        int rem = cap > 0 ? System.Math.Max(0, cap - selected) : -1;

        int key = ((abs & 0xFF) << 20) | (((rem < 0 ? 0xF : rem) & 0xF) << 16) | (sid & 0xFFFF);
        if (key == _inhLast) return;
        _inhLast = key;

        string name; try { name = Skill.GetName(sid); } catch { name = $"skill {sid}"; }
        if (string.IsNullOrEmpty(name)) name = $"skill {sid}";
        string cost = CostText(sid);
        string desc = null; try { desc = Skill.GetDescription(sid); } catch { }
        var sb = new StringBuilder(name);
        if (!string.IsNullOrEmpty(cost)) sb.Append(", ").Append(cost);
        if (!string.IsNullOrEmpty(desc)) sb.Append(". ").Append(desc);
        if (rem >= 0) sb.Append($". You can inherit {rem} more skill{(rem == 1 ? "" : "s")}");
        Speech.Say(sb.ToString(), interrupt: true);
    }

    // --- FUSION SEARCH (guided fusion). Panel bit 0x2000, drawn by FUN_14022CFB0.
    // Result list (left): count @obj+0x33C14, highlighted visible row @+0x33C16,
    // scroll-top @+0x33C18 (abs = scroll + row). Each result is a 0x278-byte entry
    // at obj+0x25C4 + abs*0x278; the RESULT persona is sub-entry 0 and the MATERIALS
    // are sub-entries at +0x30/+0x60/+0x90… Each 0x30 sub-entry: id @+0x02 (u16),
    // level @+0x04 (byte); arcana from the species table via PanelArcana(id). (All
    // RE'd from FUN_14022CFB0 + the shared row drawer FUN_140257B20.)
    private const uint SEARCH_BIT    = 0x2000;
    private const uint SPREAD_BIT    = 0x10;    // Cross/Pentagon/Hexagon summon screens (2026-07-03)
    private const int  SR_COUNT      = 0x33C14;
    private const int  SR_CURSOR     = 0x33C16;
    private const int  SR_SCROLL     = 0x33C18;
    private const int  SR_ENTRY_BASE = 0x25C4;
    private const int  SR_ENTRY_STRIDE = 0x278;
    private const int  SR_SUB        = 0x30;
    private int _searchLast = -1;

    // Cross/Pentagon/Hexagon spread summon list — layout SNAPSHOT-HUNTED live 2026-07-03
    // (0/1/0/1/2 cursor zigzag across Cross+Pentagon; entries verified against the
    // on-screen lists: Cross = Neko Shogun 32 / Tam Lin 53, Pentagon = Yoshitsune 75 /
    // Black Frost 38 / Futsunushi 80 / Yatsufusa 49 with 5-material recipes):
    //   count  @obj+0x10B0 (u16), cursor @obj+0x10B2 (u16)  [mirror at +0x10E2/+0x10E6]
    //   result entry k @obj+0x8E2 + k*0x30: persona id u16 @+0, level u16 @+2,
    //     (u32 @+6 = cost?, 5 stat bytes @+0x1A)
    //   recipe ptr   k @obj+0xB28 + k*0x70 → block: result id u16 @+0 (validated
    //     against the entry — a stale block can never misread), material persona ids
    //     u16[] @+0x0C, zero-terminated (4/5/6 for Cross/Pentagon/Hexagon).
    private const int SP_COUNT = 0x10B0, SP_CURSOR = 0x10B2;
    private const int SP_ENTRY = 0x8E2, SP_ENTRY_STRIDE = 0x30;
    private const int SP_PTR = 0xB28, SP_PTR_STRIDE = 0x70, SP_MATS = 0x0C;
    private int _spreadLast = -1;

    private void ReadSpread(nint obj)
    {
        if (!IsReadable(obj + SP_COUNT)) return;
        int count = *(ushort*)(obj + SP_COUNT);
        int cursor = IsReadable(obj + SP_CURSOR) ? *(ushort*)(obj + SP_CURSOR) : -1;
        if (count < 1 || count > 64 || cursor < 0 || cursor >= count) { _spreadLast = -1; return; }
        nint e = obj + SP_ENTRY + (nint)cursor * SP_ENTRY_STRIDE;
        if (!IsReadable(e + 4)) return;
        int id = *(ushort*)(e + 0);
        int lvl = *(ushort*)(e + 2);
        if (id < 1 || id > 400 || lvl < 1 || lvl > 99) return;
        _spreadPanelId = -1;   // back on the list — reopening F re-reads the panel
        int key = (cursor << 16) | id;
        if (key == _spreadLast) return;
        _spreadLast = key;

        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arc = PanelArcana(id);
        var sb = new StringBuilder(name);
        if (!string.IsNullOrEmpty(arc)) sb.Append(", ").Append(arc);
        sb.Append($", level {lvl}.");

        nint slot = obj + SP_PTR + (nint)cursor * SP_PTR_STRIDE;
        if (IsReadable(slot))
        {
            nint blk = *(nint*)slot;
            if (blk != 0 && IsReadable(blk + 0x20) && *(ushort*)blk == id)
            {
                var mats = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    int mid = *(ushort*)(blk + SP_MATS + i * 2);
                    if (mid < 1 || mid > 400) break;
                    string mn; try { mn = Persona.GetName(mid); } catch { mn = $"persona {mid}"; }
                    if (!string.IsNullOrEmpty(mn)) mats.Add(mn);
                }
                if (mats.Count > 0) sb.Append(" From ").Append(string.Join(", ", mats)).Append('.');
            }
        }
        // Position LAST (user pref 2026-07-03: name/arcana/level, then materials, then "k of M").
        sb.Append($" {cursor + 1} of {count}.");
        Log($"[VelvetFusion] spread: {sb}");
        Speech.Say(sb.ToString(), interrupt: true);
    }

    private void ReadSearch(nint obj)
    {
        if (!IsReadable(obj + SR_COUNT) || !IsReadable(obj + SR_CURSOR)) return;
        int count = *(short*)(obj + SR_COUNT);
        if (count < 1 || count > 4096) { _searchLast = -1; return; }
        int scroll = IsReadable(obj + SR_SCROLL) ? *(short*)(obj + SR_SCROLL) : 0;
        int abs = scroll + *(short*)(obj + SR_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint e = obj + SR_ENTRY_BASE + (nint)abs * SR_ENTRY_STRIDE;
        if (!IsReadable(e + 4)) return;
        int id = *(ushort*)(e + 2);
        if (id < 1 || id > 400) return;

        int key = (abs << 16) | id;
        if (key == _searchLast) return;
        _searchLast = key;

        int lvl = *(byte*)(e + 4);
        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arc = PanelArcana(id);
        var sb = new StringBuilder(string.IsNullOrEmpty(arc)
            ? $"{name}, level {lvl}" : $"{name}, {arc}, level {lvl}");

        // Materials (recipe): sub-entries at +0x30 stride; stop at the first invalid id.
        var mats = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            nint sub = e + (nint)(i + 1) * SR_SUB;
            if (!IsReadable(sub + 4)) break;
            int mid = *(ushort*)(sub + 2);
            if (mid < 1 || mid > 400) break;
            int mlvl = *(byte*)(sub + 4);
            string mn; try { mn = Persona.GetName(mid); } catch { mn = $"persona {mid}"; }
            if (string.IsNullOrEmpty(mn)) break;
            mats.Add($"{mn} level {mlvl}");
        }
        if (mats.Count > 0) sb.Append(". From ").Append(string.Join(", ", mats));
        Speech.Say(sb.ToString(), interrupt: true);
    }

    // --- SEARCH result persona PANEL (open a result's profile from the Search list:
    // flag 0x40 + 0x2000). The shown persona is the highlighted Search result; build
    // the panel by persona id (a potential result, not in stock). Stats = species base
    // (species table 0x140EC0958 +0x04); default skills come from the species learnset
    // (Battle.PersonaLearnedSkills) since the +0x12D8 panel array is stale here.
    // Resistances / arcana / next-learn are all by id.
    private int _searchPanelId = -1;

    // ── FUSION FORECAST (2026-07-03). The screen's VALUES are baked sprites (unreadable
    // as text/data — RE'd: FUN_140233570 draws only fixed label sprites; the per-day
    // content is pre-rendered textures at obj+0x34120..48 with no enum left behind).
    // But the forecast is DATE-DETERMINISTIC, so we read the community-validated
    // fusion_forecast.json by the in-game date instead — the weather_schedule.json
    // precedent exactly. Golden table (GameFAQs FAQ 81937) incl. Jan/Feb epilogue days;
    // a date absent from the sheet = no forecast. Day tab (Today=0 / Tomorrow=1)
    // @obj+0x1748; FieldTracker.GameDate(tab) gives the calendar date.
    private int _fcstLastTab = -1;
    private static System.Collections.Generic.Dictionary<string, (string Trigger, string Effect)>? _fcst;
    private static readonly string[] _monthNames =
        { "", "January", "February", "March", "April", "May", "June", "July",
          "August", "September", "October", "November", "December" };

    private static void LoadForecast()
    {
        _fcst = new();
        try
        {
            string p = DataPath("fusion_forecast.json");
            if (!System.IO.File.Exists(p)) { Log("[VelvetFusion] fusion_forecast.json not found"); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("schedule", out var s))
                foreach (var kv in s.EnumerateObject())
                    _fcst[kv.Name] = (kv.Value.GetProperty("trigger").GetString() ?? "None",
                                      kv.Value.GetProperty("effect").GetString() ?? "");
            Log($"[VelvetFusion] fusion forecast loaded ({_fcst.Count} days)");
        }
        catch (Exception e) { Log($"[VelvetFusion] fusion forecast load failed: {e.Message}"); }
    }

    private void ReadForecast(nint obj)
    {
        if (!IsReadable(obj + 0x1748)) return;
        int tab = *(int*)(obj + 0x1748);
        if (tab != 0 && tab != 1) return;
        if (tab == _fcstLastTab) return;
        _fcstLastTab = tab;
        if (_fcst == null) LoadForecast();

        var (m, d) = FieldTracker.GameDate(tab);
        string dayWord = tab == 0 ? "Today" : "Tomorrow";
        string line;
        if (m == 0)
            line = $"{dayWord}: date not known yet.";
        else if (_fcst != null && _fcst.TryGetValue($"{m}-{d}", out var f))
            line = f.Trigger == "None"
                ? $"{dayWord}, {_monthNames[m]} {d}: {f.Effect}."
                : $"{dayWord}, {_monthNames[m]} {d}: {f.Trigger}: {f.Effect}.";
        else
            line = $"{dayWord}, {_monthNames[m]} {d}: no fusion forecast.";
        Log($"[VelvetFusion] forecast: {line}");
        Speech.Say(line, interrupt: true);
    }

    /// <summary>Spread result's F "Persona Status" panel — same navigable sections as
    /// the Search result panel (I/K sections, J/L items), built from the SPREAD entry's
    /// id + level (flags 0x34/0x134 carry no 0x40 bit, so the shared profile path never
    /// sees them).</summary>
    private int _spreadPanelId = -1;

    private void ReadSpreadPersonaPanel(nint obj)
    {
        if (!IsReadable(obj + SP_COUNT)) return;
        int count = *(ushort*)(obj + SP_COUNT);
        int cursor = IsReadable(obj + SP_CURSOR) ? *(ushort*)(obj + SP_CURSOR) : -1;
        if (count < 1 || count > 64 || cursor < 0 || cursor >= count) { _spreadPanelId = -1; return; }
        nint e = obj + SP_ENTRY + (nint)cursor * SP_ENTRY_STRIDE;
        if (!IsReadable(e + 4)) return;
        int id = *(ushort*)(e + 0);
        int lvl = *(ushort*)(e + 2);
        if (id < 1 || id > 400 || lvl < 1 || lvl > 99) return;
        if (id != _spreadPanelId)
        {
            _spreadPanelId = id;
            _panelSecs = BuildSearchSections(obj, id, lvl);
            _panelSec = 0; _panelItem = 0;
            AnnounceSection();
        }
        PanelInput();
    }

    private void ReadSearchPersonaPanel(nint obj)
    {
        if (!IsReadable(obj + SR_COUNT) || !IsReadable(obj + SR_CURSOR)) return;
        int count = *(short*)(obj + SR_COUNT);
        if (count < 1 || count > 4096) { _searchPanelId = -1; return; }
        int scroll = IsReadable(obj + SR_SCROLL) ? *(short*)(obj + SR_SCROLL) : 0;
        int abs = scroll + *(short*)(obj + SR_CURSOR);
        if (abs < 0 || abs >= count) return;
        nint e = obj + SR_ENTRY_BASE + (nint)abs * SR_ENTRY_STRIDE;
        if (!IsReadable(e + 4)) return;
        int id = *(ushort*)(e + 2);
        if (id < 1 || id > 400) return;
        int lvl = *(byte*)(e + 4);

        if (id != _searchPanelId)
        {
            _searchPanelId = id;
            _panelSecs = BuildSearchSections(obj, id, lvl);
            _panelSec = 0; _panelItem = 0;
            AnnounceSection();
        }
        PanelInput();
    }

    private List<(string, string, List<string>)> BuildSearchSections(nint obj, int id, int lvl)
    {
        var secs = new List<(string, string, List<string>)>();
        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arcana = PanelArcana(id);
        var overItems = new List<string> { name };
        if (!string.IsNullOrEmpty(arcana)) overItems.Add(arcana);
        overItems.Add($"level {lvl}");
        secs.Add((null, string.Join(", ", overItems), overItems));

        var res = new List<string>();
        foreach (var (eid, enm) in Battle.Battle.ProfileElements)
            try { res.Add(Battle.Battle.PersonaElementAffinityText(id, eid, enm)); } catch { }
        if (res.Count > 0) secs.Add(("Resistances", string.Join(", ", res), res));

        // Stats: species base (St, Ma, En, Ag, Lu) — table 0x140EC0958, stride 0xE, +0x04.
        nint sp = unchecked((nint)0x140EC0958L);
        if (IsReadable(sp))
        {
            nint t = *(nint*)sp;
            if (IsReadable(t + id * 0xE + 8))
            {
                byte* b = (byte*)(t + id * 0xE + 4);
                var stats = new List<string>
                { $"Strength {b[0]}", $"Magic {b[1]}", $"Endurance {b[2]}", $"Agility {b[3]}", $"Luck {b[4]}" };
                secs.Add(("Stats", string.Join(", ", stats), stats));
            }
        }

        // Default skills: the species' learnset skills known by this level (the
        // +0x12D8 panel array is stale in the Search context, so derive by id).
        var skNames = new List<string>();
        var skDetail = new List<string>();
        List<int> learned; try { learned = Battle.Battle.PersonaLearnedSkills(id, lvl); } catch { learned = new List<int>(); }
        foreach (int sid in learned)
        {
            if (sid < 1 || sid > 900) continue;
            string n2; try { n2 = Skill.GetName(sid); } catch { n2 = null; }
            if (string.IsNullOrEmpty(n2)) continue;
            skNames.Add(n2);
            skDetail.Add(SkillLine(sid));
        }
        if (skNames.Count > 0) secs.Add(("Skills", string.Join(", ", skNames), skDetail));

        // Inheritance capacity — live fusion result only (field is 0 otherwise).
        int cap = InheritCap(obj);
        if (cap > 0) { string t = InheritCapText(cap); secs.Add(("Inheritance", t, new List<string> { t })); }

        // Next-learned skill (by id + level).
        try
        {
            if (Battle.Battle.PersonaNextLearn(id, lvl, out int lvlLearn, out int sid) && sid > 0)
            {
                string nm; try { nm = Skill.GetName(sid); } catch { nm = $"skill {sid}"; }
                var nextItems = new List<string> { $"next skill at level {lvlLearn}, {SkillLine(sid)}" };
                secs.Add((null, $"next skill at level {lvlLearn}, {nm}", nextItems));
            }
        }
        catch { }

        return secs;
    }

    /// <summary>Base SP/HP cost of a skill, read straight from the skill-data entry
    /// (no battle member needed). From GetSkillCost @0x1400d3f40: entry = base+id*0x2c,
    /// +0x06 = cost type (1=HP, 2=SP), +0x08/+0x0A = cost words, +0x00 bit 0x10 = the
    /// "percent of max" variant. SP base cost is member-independent (+8 + +0xA); HP
    /// cost is a percentage of max HP (+8), so we report the percent.</summary>
    private static string CostText(int sid)
    {
        byte* a;
        try { a = (byte*)Skill.GetActiveSkillData(sid); } catch { return null; }
        if (a == null || !IsReadable((nint)a + 0x0B)) return null;
        int type = a[6];
        int c8 = *(ushort*)(a + 8), cA = *(ushort*)(a + 0x0A);
        bool pct = (a[0] & 0x10) != 0;
        if (type == 2) return pct ? $"{c8} percent SP" : $"{c8 + cA} SP";          // SP
        if (type == 1)                                                              // HP (shown as flat HP in-game)
        {
            int hp = c8 + cA;
            if (hp > 0) return $"{hp} HP";
        }
        return null;   // passive / no cost
    }

    // Persona-pick LIST (bit 0x02 / FUN_140229190). Live-confirmed: count @ +0x740
    // (=2 personas), cursor @ +0x742. Personas are arcana-grouped in a global table
    // (DAT_1411A78B0, via arcana index @ obj+0xF0 -> DAT_14090E690), so the actual
    // persona NAME still needs a decode pass — for now we read count/cursor and dump.
    private const uint PLIST_BIT    = 0x002;
    private const int  PLIST_COUNT  = 0x740;  // ushort entry count (= non-empty stock)
    private const int  PLIST_SEL    = 0x744;  // ushort highlighted row (0..count-1)
    // The list is shown in STOCK ORDER, so row N = the Nth non-empty stock slot.
    // MC persona stock: *(0x141165900)+0xA34 + slot*0x30; PersonaInfo {id@+0x02,
    // level@+0x04}, valid when +0x00 != 0. (Confirmed live: slots 0..4 = Nata
    // Taishi/Ukobach/Apsaras/Izanagi/Omoikane, matching the on-screen rows.)
    private static readonly nint STOCK_BASE_PTR = unchecked((nint)0x141165900L);
    private const int  STOCK_ARRAY    = 0xA34;
    private const int  STOCK_STRIDE   = 0x30;

    private void Read(nint obj)
    {
        if (obj == 0 || !IsReadable(obj + FLAG)) return;
        uint flag = *(uint*)(obj + FLAG);
        _lastFlag = flag;   // (the per-change flag log was removed in the v1.3.5 cleanup)

        // Fusion INHERIT CAPACITY (+0x145A, u16) — how many skills you can give the
        // result persona. FIXED per fusion (NOT +0x1464/+0x1466, which are live
        // remaining-slot counters; live-verified 2026-07-09: +0x145A read 4 and the
        // game capped selection at 4). It is NOT announced standalone (that stomped
        // the persona panel) — it's folded into the result persona panel (BuildSections
        // "Inheritance" section) and the skill-select screen (ReadInheritance). See
        // InheritCap().

        // Skill-INHERITANCE screen (bit 0x4000) is drawn by FUN_140234B20 and read
        // by its own hook (OnInhDraw) — skip here so the profile reader below doesn't
        // talk over it (the inheritance flag also carries the profile bit 0x40).
        if ((flag & INH_BIT) != 0) return;
        _inhLast = -1;   // left the inheritance screen -> re-announce on re-entry

        // SPREAD-fusion RESULT panel (Cross/Pentagon/Hexagon — press to MAKE the persona):
        // low word 0x0074 = the spread persona panel (0x0034) + the profile bit (0x40).
        // It carries 0x40, so the generic profile branch below would hand it to the
        // NORMAL-fusion ReadPersonaPanel with stale data (silent — user 2026-07-09,
        // live flag 0x80040074). Route it to the spread reader first; the spread cursor
        // points at the result persona being confirmed.
        if ((flag & 0xFFFF) == 0x0074) { ReadSpreadPersonaPanel(obj); return; }

        // Persona PROFILE (F / Skill Info) opens over the list — flag bit 0x40.
        // In the Search context (0x2000 also set) the shown persona is the highlighted
        // SEARCH result, not the fusion-result entry — read it from the Search entry.
        if ((flag & 0x40) != 0)
        {
            if ((flag & SEARCH_BIT) != 0) ReadSearchPersonaPanel(obj);
            else ReadPersonaPanel(obj);
            return;
        }
        _panelResultId = -1;   // profile closed -> re-opening (even same persona) re-reads
        _searchPanelId = -1;

        // OWNED-persona detail — viewing one of YOUR personas from the select list.
        // Requires bit 0x20 (detail open) AND the list bit 0x02 (we're in the select
        // list, so +0x744 is a valid row). Result/transition screens also set 0x20 but
        // WITHOUT 0x02, where +0x744 is stale — gating on 0x02 stops the wrong-persona
        // read on the result screen. Checked before the list so it isn't talked over.
        if ((flag & OWNED_BIT) != 0 && (flag & PLIST_BIT) != 0) { ReadOwnedPersonaPanel(obj); return; }
        _ownedPid = -1;        // detail closed -> re-opening re-reads

        // FUSION SEARCH (guided fusion) — bit 0x2000, drawn by FUN_14022CFB0.
        if ((flag & SEARCH_BIT) != 0) { ReadSearch(obj); return; }
        // SPREAD summon lists (Cross/Pentagon/Hexagon, 2026-07-03) — EXACT low-word
        // match on the two observed screen states (0x80040010 / 0x80040014) only.
        // ⚠ A loose "bit 0x10 set" test STOLE other fusion states that carry 0x10
        // alongside their own bits (persona-pick, transitions) and broke
        // Normal/Triangle reading entirely (regression, user 2026-07-03) — keep this
        // exact until the flag semantics are decoded.
        if ((flag & 0xFFFF) is 0x0010 or 0x0014) { ReadSpread(obj); return; }
        // Spread result's F "Persona Status" panel (log-observed low words 0x34/0x134;
        // no 0x40 bit here, unlike every other velvet profile — exact match again).
        if ((flag & 0xFFFF) is 0x0034 or 0x0134) { ReadSpreadPersonaPanel(obj); return; }
        // FUSION FORECAST screen (low word 0x8200; the 0x8600/0x8400 transitions carry
        // the L2 bit 0x400 and stay with the menu panel).
        if ((flag & 0xFFFF) == 0x8200) { ReadForecast(obj); return; }
        _searchLast = -1;
        _spreadLast = -1;
        _spreadPanelId = -1;
        _fcstLastTab = -1;

        // Persona list takes priority when active (it's the deepest/focused panel).
        if ((flag & PLIST_BIT) != 0 && ReadPersonaList(obj)) return;

        // Pick the active input panel by flag bit (focus priority order).
        foreach (var p in Panels)
        {
            if ((flag & p.Bit) == 0) continue;
            if (!IsReadable(obj + p.Count)) return;
            short count  = *(short*)(obj + p.Count);
            short cursor = *(short*)(obj + p.Cursor);
            if (count < 1 || count > 64 || cursor < 0 || cursor >= count) return;

            if (p.Bit != _lastPanelBit) { _lastPanelBit = p.Bit; _lastCursor = -1; }
            if (cursor == _lastCursor) return;
            _lastCursor = cursor;

            string spoken;
            if (p.Ids != null && IsReadable(obj + p.IdOff + cursor * p.Stride))
            {
                // ID-based: robust to options being added/removed/reordered.
                int id = *(short*)(obj + p.IdOff + (nint)cursor * p.Stride);
                int idx = System.Array.IndexOf(p.Ids, id);
                spoken = idx >= 0 ? p.Names[idx] : $"Option {cursor + 1} of {count}";
                if (idx < 0 && _idLogs < 24)
                {
                    _idLogs++;
                    Log($"[VelvetFusion] {p.Tag} UNKNOWN id {id} at cursor {cursor}/{count} — name it");
                }
            }
            else
            {
                // Position-based, guarded on the full menu being present.
                spoken = (count == p.Names.Length && cursor < p.Names.Length)
                    ? p.Names[cursor]
                    : $"Option {cursor + 1} of {count}";
                // Log the option id so this panel can be made id-based.
                if (_idLogs < 24 && IsReadable(obj + p.IdOff + cursor * p.Stride))
                {
                    _idLogs++;
                    Log($"[VelvetFusion] {p.Tag} cursor={cursor}/{count} " +
                        $"id@+0x{p.IdOff:X}+{cursor}*0x{p.Stride:X} = {*(short*)(obj + p.IdOff + (nint)cursor * p.Stride)}");
                }
            }
            Speech.Say(spoken, interrupt: true);
            return;
        }

        // Unmapped panel state: log the flag word once (no hex dumps — the v1.3.5
        // cleanup removed them; re-add the +0x0F0/+0x740/+0x10A0 HexAscii dump here
        // if a NEW velvet screen ever needs decoding — that's how every screen so
        // far was solved).
        if (_dumps < 10 && flag != _lastDumpedFlag)
        {
            _lastDumpedFlag = flag;
            _dumps++;
            Log($"[VelvetFusion] unmapped panel flag=0x{flag:X}");
        }
    }

    private uint _lastDumpedFlag = 0xFFFFFFFF;
    private int  _plLastSel = -1;

    /// <summary>"Select Nth Persona" fusion list (vertical, shown in stock order;
    /// shared by Normal/Triangle fusion). Reads the highlighted row at +0x744 and
    /// speaks that stock persona's name/arcana/level.</summary>
    private bool ReadPersonaList(nint obj)
    {
        if (!IsReadable(obj + PLIST_COUNT) || !IsReadable(obj + PLIST_SEL)) return false;
        int count = *(ushort*)(obj + PLIST_COUNT);
        int sel   = *(ushort*)(obj + PLIST_SEL);
        if (count < 1 || count > 1024 || sel < 0 || sel >= count) return false;

        if (PLIST_BIT != _lastPanelBit) { _lastPanelBit = PLIST_BIT; _plLastSel = -1; }
        // De-dupe on BOTH the cursor row AND the result id, so changing the fusion
        // (result changes without the cursor moving) still re-announces.
        int resId = IsReadable(obj + RESULT_BASE + sel * RESULT_STRIDE) ? *(ushort*)(obj + RESULT_BASE + sel * RESULT_STRIDE) : 0;
        int key = (sel & 0xFFFF) | (resId << 16);
        if (key == _plLastSel) return true;
        _plLastSel = key;
        Log($"[VelvetFusion] plist sel={sel} resId={resId}");

        string candidate = StockPersonaForRow(sel);
        string result = ResultForRow(obj, sel);
        string spoken =
            result != null && candidate != null ? $"{candidate}. Makes {result}"
            : candidate ?? $"Persona {sel + 1} of {count}";
        Speech.Say(spoken, interrupt: true);
        return true;
    }

    // Fusion RESULT preview (2nd-persona screen): one entry per left-list row at
    // obj + 0x356 + row*0x50; {result persona id @+0x00, result level @+0x02}.
    // Confirmed live vs screenshot (row1 Ukobach -> Andra Moon L20, etc.).
    private const int RESULT_BASE = 0x356, RESULT_STRIDE = 0x50;

    /// <summary>The fusion result for the row-th candidate ("{name}, {arcana}, level
    /// N"), or null on the 1st-persona screen / the chosen persona's own row.</summary>
    private string ResultForRow(nint obj, int row)
    {
        nint e = obj + RESULT_BASE + row * RESULT_STRIDE;
        if (!IsReadable(e)) return null;
        int id = *(ushort*)e;
        if (id < 1 || id > 320) return null;
        string name; try { name = Persona.GetName(id); } catch { return null; }
        if (string.IsNullOrEmpty(name)) return null;
        int lvl = *(ushort*)(e + 2);
        string arcana = PanelArcana(id);
        return string.IsNullOrEmpty(arcana) ? $"{name}, level {lvl}" : $"{name}, {arcana}, level {lvl}";
    }

    /// <summary>Name + arcana + level of the row-th non-empty MC stock persona.</summary>
    private string StockPersonaForRow(int row)
    {
        if (!IsReadable(STOCK_BASE_PTR)) return null;
        nint baseObj = *(nint*)STOCK_BASE_PTR;
        if (baseObj == 0) return null;
        int n = 0;
        for (int slot = 0; slot < 12; slot++)
        {
            nint e = baseObj + STOCK_ARRAY + slot * STOCK_STRIDE;
            if (!IsReadable(e) || *(byte*)e == 0) continue;
            if (n++ != row) continue;
            int id = *(short*)(e + 2), lvl = *(byte*)(e + 4);
            if (id < 1 || id > 320) return null;
            string name; try { name = Persona.GetName(id); } catch { return null; }
            if (string.IsNullOrEmpty(name)) return null;
            string arcana = PanelArcana(id);
            return string.IsNullOrEmpty(arcana) ? $"{name}, level {lvl}" : $"{name}, {arcana}, level {lvl}";
        }
        return null;
    }

    // ---- Persona PROFILE panel (F), navigable like the battle panel. The shown
    // result follows the highlighted 2nd persona (cursor +0x744) -> RESULT ENTRY
    // +0x356 + cursor*0x50 {id @+0, level @+2}. I/K step the data lines (overview,
    // the 7 resistances, the next-learned skill); each is reliable by persona id.
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_I = 0x49, VK_J = 0x4A, VK_K = 0x4B, VK_L = 0x4C;
    private bool _wasI, _wasJ, _wasK, _wasL;
    private int  _panelResultId = -1, _panelSec, _panelItem;
    // Sections like the battle panel: I/K reads the section SUMMARY (short — e.g.
    // skill names only); J/L steps to an item for the full detail (name + desc).
    private List<(string header, string summary, List<string> items)> _panelSecs = new();

    private void ReadPersonaPanel(nint obj)
    {
        if (!IsReadable(obj + PLIST_SEL)) return;
        int cursor = *(ushort*)(obj + PLIST_SEL);
        if (cursor < 0 || cursor > 32) return;
        nint e = obj + RESULT_BASE + cursor * RESULT_STRIDE;
        if (!IsReadable(e)) return;
        int id = *(ushort*)e;
        if (id < 1 || id > 320) return;
        int lvl = *(ushort*)(e + 2);

        if (id != _panelResultId)
        {
            _panelResultId = id;
            _panelSecs = BuildSections(obj, e, id, lvl);
            _panelSec = 0; _panelItem = 0;
            AnnounceSection();
        }

        PanelInput();
    }

    /// <summary>I/K step the section, J/L step items within it. Shared by the result
    /// panel and the owned-persona panel (only one is active at a time).</summary>
    private void PanelInput()
    {
        if (!Utils.GameHasFocus()) return;   // ignore I/K/J/L while alt-tabbed
        bool i = (GetAsyncKeyState(VK_I) & 0x8000) != 0, k = (GetAsyncKeyState(VK_K) & 0x8000) != 0;
        bool j = (GetAsyncKeyState(VK_J) & 0x8000) != 0, l = (GetAsyncKeyState(VK_L) & 0x8000) != 0;
        bool secPrev = i && !_wasI, secNext = k && !_wasK, itPrev = j && !_wasJ, itNext = l && !_wasL;
        _wasI = i; _wasJ = j; _wasK = k; _wasL = l;
        if (_panelSecs.Count == 0) return;
        if (secPrev || secNext)
        {
            _panelSec = System.Math.Clamp(_panelSec + (secNext ? 1 : -1), 0, _panelSecs.Count - 1);
            _panelItem = 0; AnnounceSection();
        }
        else if (itPrev || itNext)
        {
            var items = _panelSecs[_panelSec].items;
            if (items.Count > 0) { _panelItem = System.Math.Clamp(_panelItem + (itNext ? 1 : -1), 0, items.Count - 1); Speech.Say(items[_panelItem], interrupt: true); }
        }
    }

    // ---- Owned-persona DETAIL panel (viewing one of YOUR personas from the fusion
    // select list). It's the +0xE8 flag bit 0x20 WITHOUT the result-profile bit 0x40
    // (live flag 0x80040032 vs the result's 0x80040072). The shown persona is the
    // highlighted select-list row (+0x744); we resolve it to its MC stock PersonaInfo
    // entry and read name/arcana/level, resistances, stats, skills, next-exp/skill —
    // reusing the battle owned-persona accessors (stats via PersonaTotalStats, skills
    // @entry+0x0C, exp@entry+0x08). Navigable with I/K/J/L like the result panel.
    private const uint OWNED_BIT = 0x20;
    private int _ownedPid = -1;

    private void ReadOwnedPersonaPanel(nint obj)
    {
        if (!IsReadable(obj + PLIST_SEL)) return;
        int row = *(ushort*)(obj + PLIST_SEL);
        if (row < 0 || row > 32) return;
        nint e = StockEntryForRow(row, out int pid, out int lvl);
        if (e == 0 || pid < 1 || pid > 320) { _ownedPid = -1; return; }

        if (pid != _ownedPid)
        {
            _ownedPid = pid;
            _panelSecs = BuildOwnedSections(e, pid, lvl);
            _panelSec = 0; _panelItem = 0;
            AnnounceSection();
        }
        PanelInput();
    }

    /// <summary>MC stock PersonaInfo entry for the row-th non-empty slot (matches the
    /// fusion list's stock-order indexing). Returns 0 if not found.</summary>
    private nint StockEntryForRow(int row, out int pid, out int lvl)
    {
        pid = -1; lvl = 0;
        if (!IsReadable(STOCK_BASE_PTR)) return 0;
        nint baseObj = *(nint*)STOCK_BASE_PTR;
        if (baseObj == 0) return 0;
        int n = 0;
        for (int slot = 0; slot < 12; slot++)
        {
            nint e = baseObj + STOCK_ARRAY + slot * STOCK_STRIDE;
            if (!IsReadable(e) || *(byte*)e == 0) continue;
            if (n++ != row) continue;
            pid = *(ushort*)(e + 2);
            lvl = *(byte*)(e + 4);
            return e;
        }
        return 0;
    }

    /// <summary>Owned-persona panel sections — mirrors BuildSections but sources stats
    /// and current skills from the stock PersonaInfo entry (not a fusion result entry).</summary>
    private List<(string, string, List<string>)> BuildOwnedSections(nint e, int id, int lvl)
    {
        var secs = new List<(string, string, List<string>)>();
        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arcana = PanelArcana(id);
        var overItems = new List<string> { name };
        if (!string.IsNullOrEmpty(arcana)) overItems.Add(arcana);
        overItems.Add($"level {lvl}");
        secs.Add((null, string.Join(", ", overItems), overItems));

        var res = new List<string>();
        foreach (var (eid, enm) in Battle.Battle.ProfileElements)
            try { res.Add(Battle.Battle.PersonaElementAffinityText(id, eid, enm)); } catch { }
        if (res.Count > 0) secs.Add(("Resistances", string.Join(", ", res), res));

        // Stats: SUM of base+growth+bonus for an owned persona (PersonaTotalStats).
        int[] t = null; try { t = Battle.Battle.PersonaTotalStats(e); } catch { }
        if (t != null && t.Length >= 5)
        {
            var stats = new List<string>
            { $"Strength {t[0]}", $"Magic {t[1]}", $"Endurance {t[2]}", $"Agility {t[3]}", $"Luck {t[4]}" };
            secs.Add(("Stats", string.Join(", ", stats), stats));
        }

        // Current skills: PersonaInfo.Skills[8] @ entry+0x0C (short stride 2).
        var skNames = new List<string>();
        var skDetail = new List<string>();
        for (int s = 0; s < 8; s++)
        {
            int sid = IsReadable(e + 0x0C + s * 2) ? *(short*)(e + 0x0C + s * 2) : 0;
            if (sid < 1 || sid > 900) continue;
            string n2; try { n2 = Skill.GetName(sid); } catch { n2 = $"skill {sid}"; }
            if (string.IsNullOrEmpty(n2)) continue;
            skNames.Add(n2);
            skDetail.Add(SkillLine(sid));
        }
        if (skNames.Count > 0) secs.Add(("Skills", string.Join(", ", skNames), skDetail));

        // Next experience (actual remaining, from entry+0x08) + next-learned skill.
        try
        {
            int gap = Battle.Battle.PersonaNextExp(e);
            var parts = new List<string>();
            var nextItems = new List<string>();
            if (gap >= 0) { parts.Add($"next experience {gap}"); nextItems.Add($"next experience {gap}"); }
            if (Battle.Battle.PersonaNextLearn(id, lvl, out int lvlLearn, out int sid) && sid > 0)
            {
                string nm; try { nm = Skill.GetName(sid); } catch { nm = $"skill {sid}"; }
                parts.Add($"next skill at level {lvlLearn}, {nm}");
                nextItems.Add($"next skill at level {lvlLearn}, {SkillLine(sid)}");
            }
            if (nextItems.Count > 0) secs.Add((null, string.Join(", ", parts), nextItems));
        }
        catch { }

        return secs;
    }

    /// <summary>Announce a whole section: "Header: item, item, ..." (J/L then re-steps
    /// individual items). Header-less sections (overview, next-skill) just read.</summary>
    private void AnnounceSection()
    {
        var (header, summary, _) = _panelSecs[_panelSec];
        Speech.Say(string.IsNullOrEmpty(header) ? summary : $"{header}: {summary}", interrupt: true);
    }

    private List<(string, string, List<string>)> BuildSections(nint obj, nint e, int id, int lvl)
    {
        var secs = new List<(string, string, List<string>)>();
        string name; try { name = Persona.GetName(id); } catch { name = $"persona {id}"; }
        string arcana = PanelArcana(id);
        // Overview: I/K reads the whole row; J/L steps name / arcana / level.
        var overItems = new List<string> { name };
        if (!string.IsNullOrEmpty(arcana)) overItems.Add(arcana);
        overItems.Add($"level {lvl}");
        secs.Add((null, string.Join(", ", overItems), overItems));

        var res = new List<string>();
        foreach (var (eid, enm) in Battle.Battle.ProfileElements)
            try { res.Add(Battle.Battle.PersonaElementAffinityText(id, eid, enm)); } catch { }
        if (res.Count > 0) secs.Add(("Resistances", string.Join(", ", res), res));

        // Stats: result entry +0x1A, 5 bytes = St, Ma, En, Ag, Lu.
        if (IsReadable(e + 0x1A))
        {
            byte* st = (byte*)(e + 0x1A);
            var stats = new List<string>
            { $"Strength {st[0]}", $"Magic {st[1]}", $"Endurance {st[2]}", $"Agility {st[3]}", $"Luck {st[4]}" };
            secs.Add(("Stats", string.Join(", ", stats), stats));
        }

        // Skills: result entry +0x0A, u16 stride 2 (innate skills). Summary = names
        // only (for I/K); J/L steps to the name + full description.
        var skNames = new List<string>();
        var skDetail = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            int sid2 = IsReadable(e + 0x0A + i * 2) ? *(ushort*)(e + 0x0A + i * 2) : 0;
            if (sid2 < 1 || sid2 > 900) continue;
            string n2; try { n2 = Skill.GetName(sid2); } catch { n2 = $"skill {sid2}"; }
            if (string.IsNullOrEmpty(n2)) continue;
            skNames.Add(n2);
            skDetail.Add(SkillLine(sid2));
        }
        if (skNames.Count > 0) secs.Add(("Skills", string.Join(", ", skNames), skDetail));

        // Inheritance capacity — only when this is a live fusion result (the field is
        // 0 outside fusion), so it never shows on a plain compendium/owned view.
        int cap = InheritCap(obj);
        if (cap > 0) { string t = InheritCapText(cap); secs.Add(("Inheritance", t, new List<string> { t })); }

        // Next experience + next-learned skill — one row (matches the screen's
        // "NEXT EXP" and "Next LV" boxes). I/K reads both; J/L steps through them
        // separately (exp, then the skill with its full description).
        try
        {
            int gap = Battle.Battle.PersonaExpGapForLevel(id, lvl);
            var parts = new List<string>();
            var nextItems = new List<string>();
            if (gap >= 0) { parts.Add($"next experience {gap}"); nextItems.Add($"next experience {gap}"); }
            if (Battle.Battle.PersonaNextLearn(id, lvl, out int lvlLearn, out int sid) && sid > 0)
            {
                string nm; try { nm = Skill.GetName(sid); } catch { nm = $"skill {sid}"; }
                parts.Add($"next skill at level {lvlLearn}, {nm}");
                nextItems.Add($"next skill at level {lvlLearn}, {SkillLine(sid)}");
            }
            if (nextItems.Count > 0)
                secs.Add((null, string.Join(", ", parts), nextItems));
        }
        catch { }

        return secs;
    }

    /// <summary>Arcana name, fixed for Golden/high-arcana personas the base table
    /// mislabels. PersonaArcanaName covers vanilla (byte 1..22); for an unmapped byte
    /// we read the species arcana byte and map the known Golden arcana (27 = Aeon).
    /// Unknown bytes are logged + return null (omit) rather than read "arcana N" junk.</summary>
    private static string PanelArcana(int id)
    {
        string a = Battle.Battle.PersonaArcanaName(id);
        if (!string.IsNullOrEmpty(a) && !a.StartsWith("arcana ")) return a;
        nint sp = unchecked((nint)0x140EC0958L);
        if (!IsReadable(sp)) return null;
        nint t = *(nint*)sp;
        if (!IsReadable(t + id * 0xE + 2)) return null;
        int b = *(byte*)(t + id * 0xE + 2);
        string g = b switch { 27 => "Aeon", _ => null };
        if (g == null) Log($"[VelvetFusion] UNMAPPED arcana: persona id={id} speciesByte={b}");
        return g;
    }

    private static string SkillLine(int sid)
    {
        string nm; try { nm = Skill.GetName(sid); } catch { return $"skill {sid}"; }
        if (string.IsNullOrEmpty(nm)) return $"skill {sid}";
        string desc = null; try { desc = Skill.GetDescription(sid); } catch { }
        return string.IsNullOrEmpty(desc) ? nm : $"{nm}. {desc}";
    }

    private static string HexAscii(nint addr, int len)
    {
        if (!IsReadable(addr)) return "<unreadable>";
        var hex = new StringBuilder();
        var asc = new StringBuilder();
        for (int i = 0; i < len; i++)
        {
            if (!IsReadable(addr + i)) { hex.Append(".. "); asc.Append('.'); continue; }
            byte b = ((byte*)addr)[i];
            hex.Append(b.ToString("X2")).Append(' ');
            asc.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        return hex.Append('|').Append(asc).ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nint VQ(nint a, byte* b, nint l);
    private static bool IsReadable(nint a)
    {
        if (a < 0x10000) return false;
        byte* buf = stackalloc byte[48];
        if (VQ(a, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;   // MEM_COMMIT
        uint p = *(uint*)(buf + 36);                       // Protect
        return (p & 0x01) == 0 && (p & 0x100) == 0;        // not NOACCESS / not GUARD
    }

    public void Dispose() { }
}
