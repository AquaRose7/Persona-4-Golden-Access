using System.Runtime.InteropServices;
using DavyKager;
using p4g64.accessibility.Native;
using p4g64.accessibility.Native.Text;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the shop menu to announce item info via screen reader.
///
/// DISCOVERY NOTES:
///   CE scan (cursor value 0–6) → CE "find what writes" → prologue at VA 0x1402827F0
///   SigScan "40 55 53 41 54 41 55 41 57 48 8B EC 48 83 EC 70 48 8B D9" — UNIQUE in .shared.
///
///   Struct layout (confirmed via F10 dump, log.txt 2026-04-02):
///     pShop+0x004 (short)  = ItemCount  (lower 16 bits; item shop item count or category list count)
///     pShop+0x010 (nint)   = ItemListPtr → pointer to consumable item list (stride=0x90)
///     pShop+0x032 (short)  = ActiveCursor → item cursor WITHIN the current weapon/armor/accessory category
///     pShop+0x060 (nint)   = ActiveListPtr → pointer to current category's item list (stride=0x14)
///     pShop+0x09A (short)  = ActiveItemCount → item count in the current category sub-list
///     pShop+0x35C (int)    = Cursor → main/outer cursor (item shop: item index; weapon shop: category 0-2)
///
///   Item entries inside *ItemListPtr (stride=0x90, confirmed for consumable items):
///     base of entries = ItemListPtr + 0x018
///     stride per entry = 0x90 bytes
///     entry+0x04 (int) = category  (0=Weapon, 1=Armor, 2=Item, 3=Accessory)
///     entry+0x08 (int) = HelpBmd item ID
///
///   Active list entries inside *ActiveListPtr (stride=0x14, confirmed for weapon shop):
///     base of entries = ActiveListPtr + 0x000  (no header)
///     stride per entry = 0x14 bytes
///     entry+0x00 (int) = price
///     entry+0x04 lo16  = item ID (in game's item database)
///     entry+0x04 hi16  = HelpBmd index (use directly as Dialog.HelpBmd enum value)
///
///   F10 dump evidence (log.txt 2026-04-04, accessories sub-menu, 2 items):
///     cursor032=0 ↔ cursor032=1 as user moves between the 2 accessories
///     ptr060 entries: [price=3000, lo=634 hi=4] and [price=3000, lo=646 hi=4]
///     ActiveItemCount (pShop+0x09A) = 2 = number of accessories
///
///   HelpBmd.Item[198] = "Soft, sweet, milky candy. Restores 10SP to one ally."
///   HelpBmd.Item[15]  = "Revives an ally, and restores 50% of HP."
///   (Category 2 → HelpBmd.Item confirmed by log evidence)
///
/// TODO: Find item NAME source (separate from description).
///       The MessageDialog.Name field is dumped by F10 — check if it contains display names.
///       If not, need to find an EnglishItemNames native array (like Skill.cs does).
/// TODO: Find mode indicator to silence wrong cursor35C announcements on weapon shop
///       category tabs. Currently pShop+0x068=2 when in item list; need a dump from
///       category-tab navigation to confirm if it differs.
/// </summary>
internal unsafe class ShopMenu
{
    // ── Struct layout ────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Explicit)]
    private struct ShopStruct
    {
        [FieldOffset(0x004)] public short ItemCount;        // item count (consumable list or category count)
        [FieldOffset(0x010)] public nint  ItemListPtr;      // consumable item list (stride=0x90)
        [FieldOffset(0x032)] public short ActiveCursor;     // cursor within current weapon/armor/accessory list
        [FieldOffset(0x060)] public nint  ActiveListPtr;    // current category's item list (stride=0x14)
        [FieldOffset(0x068)] public int   Mode068;          // mode discriminator: top-menu vs item-list (docstring hint: =2 in item list)
        [FieldOffset(0x09A)] public short ActiveItemCount;  // item count in current category sub-list
        [FieldOffset(0x35C)] public int   Cursor;           // outer cursor (item shop item index / weapon shop category)
    }

    // Consumable item list layout (stride=0x90)
    private const int ListFirstEntryOffset = 0x018;
    private const int EntryStride          = 0x90;
    private const int EntryCategory        = 0x04;
    private const int EntryItemId          = 0x08;

    // Active category item list layout (stride=0x14, no header)
    private const int ActiveEntryStride    = 0x14;
    private const int ActiveEntryPacked    = 0x04;  // lo16=itemId, hi16=helpBmdIndex

    // ── Hook state ───────────────────────────────────────────────────────
    private IHook<ShopUpdateDelegate>? _hook;
    private static ShopStruct* _lastPtr;
    private int   _lastCursor    = -1;   // outer cursor (pShop+0x35C)
    private short _lastCursor032 = -1;   // inner cursor (pShop+0x032)
    // Written by DaidaraCharSelect's hook (it announces character names).
    // v2 keeps the field for that component's benefit but no longer gates
    // any announcement on it — the shop state field is the truth.
    internal static volatile bool CharSelectIsActive;
    private bool  _active;
    private bool  _fKeyWas;              // 'F' key (0x46) previous state — focus-gated
    private bool  _fPressedRawWas;       // 'F' key previous state ignoring focus (diagnostic)
    private bool  _escWas;              // Escape key previous state
    private bool  _yButtonWas;           // Gamepad Y button previous state (XInput)
    private bool  _gKeyWas;              // 'G' key previous state (wallet readout)
    private bool  _xInputLogged;         // one-time XInput diagnostic

    internal ShopMenu(IReloadedHooks hooks)
    {
        SigScan(
            "40 55 53 41 54 41 55 41 57 48 8B EC 48 83 EC 70 48 8B D9",
            "ShopMenu::Update",
            address =>
            {
                _hook = hooks.CreateHook<ShopUpdateDelegate>(OnShopUpdate, address).Activate();
                Log($"[ShopMenu] Hook active at 0x{address:X}");
            });

        var t = new System.Threading.Thread(PollKeys) { IsBackground = true };
        t.Start();
    }

    // ════════════════════════════════════════════════════════════════════
    // ShopReader v2 (2026-06-12) — driven by the shop's OWN state machine.
    //
    // Ghidra (ShopUpdate 0x1402827F0 → state setter FUN_14027e4d0):
    //   *(u16*)(pShop+0x04) = CURRENT STATE, *(u16*)(pShop+0x06) = previous.
    //   (The field we documented as "ItemCount" at +0x004 was the state all
    //   along.) State map — ground-truth user tour in Daidara:
    //     0x07 top menu · 0x09 →charselect transition · 0x08 character select
    //     0x0A ITEM LIST · 0x0B DESCRIPTION WINDOW · 0x12 sell · 0x1C talk
    //     0x1E/0x20/0x21/0x22 exit teardown (struct freed after!)
    //   Top-menu option-id array at +0x340, cursor +0x35C, count +0x35A.
    //
    // All announcements key off state transitions + cursor changes gated by
    // state. The legacy heuristics (mode068, confirmedInItemList,
    // CharSelectIsActive gating, the 0x140BEB4EC "panel" global) are GONE.
    // ════════════════════════════════════════════════════════════════════
    private const ushort ST_NONE    = 0xFFFF;
    private const ushort ST_TOP     = 0x07;
    private const ushort ST_SELL    = 0x12;
    private const ushort ST_TALK    = 0x1C;
    private const ushort ST_EXIT0   = 0x1E;

    // The BUY FLOW (character select / item list / description window) does
    // NOT use stable state numbers across game sessions: the mapping tour
    // saw charsel=0x08, list=0x0A, desc=0x0B; the next session ran the whole
    // flow under 0x09 (counts populated while the state never moved).
    // Top menu (0x07), Sell (0x12), Talk (0x1C) and the exit chain (0x1E+)
    // ARE stable. So the gate is a BAND: anything in 0x08..0x11 = buy flow,
    // and within it announcements key off the ITEM CURSOR (which only moves
    // in lists) + the list-count edge (which fires when a list opens).
    private static bool InBuyFlow(ushort s) => s >= 0x08 && s <= 0x11;

    // exposed for DaidaraCharSelect (it must only announce on the real
    // char-select screen, state 0x08)
    internal static volatile ushort ShopState = ST_NONE;
    private ushort _shopState { get => ShopState; set => ShopState = value; }
    private int _announceListIn;     // frame countdown: announce after list fields populate
    private short _lastListCount;    // ActiveItemCount edge → list just opened
    private nint  _lastListPtr;      // ActiveListPtr change → different list
    // Inner-cursor neighborhood (+0x28..+0x46 as shorts). Different buy tabs
    // keep their row cursor in different slots; we track all and remember
    // which one moved last (that's the live cursor for AnnounceCurrentItem).
    private short _lastRow = -2;    // pShop+0x9E mirror (-2 = uninitialised)
    private short _lastWho = -1;    // pShop+0x4E0 mirror (selected character)
    private static readonly string[] CharNames =
        { "You", "Yosuke", "Chie", "Yukiko", "Kanji", "Rise", "Naoto", "Teddie" };

    private static ushort ReadShopState(ShopStruct* pShop)
        => IsReadable((nint)pShop + 4, 2) ? *(ushort*)((nint)pShop + 4) : ST_NONE;

    private void OnShopUpdate(ShopStruct* pShop, nint p2, nint p3, nint p4)
    {
        _hook!.OriginalFunction(pShop, p2, p3, p4);
        if (pShop == null) return;

        // Reset all state when shop struct changes (new shop opened)
        if (pShop != _lastPtr)
        {
            _lastCursor     = -1;
            _lastCursor032  = -1;
            _shopState      = ST_NONE;
            _announceListIn = 0;
            _lastListCount  = 0;
            _lastListPtr    = 0;
            _lastRow = -2;
            _lastWho = -1;
        }
        _lastPtr = pShop;
        _active  = true;

        ushort state = ReadShopState(pShop);
        if (state > 0x23) return;   // teardown garbage — struct on its way out

        // v3: ALL state logic lives on the poll thread (Tick50). This hook is
        // the TOP-LEVEL shop update and STARVES while the user is inside an
        // item list — F10 proved the +0x04 state keeps advancing (0x0A list,
        // 0x0B desc — the ORIGINAL tour map was right; "session-variable
        // states" were this hook's frozen view). Here we only capture the
        // pointer (done above).
    }

    private void OnStateChanged(ShopStruct* pShop, ushort prev, ushort state)
    {
        if (state == ST_TOP)
        {
            // returning from any sub-screen → re-orient with the menu label
            if (prev != ST_NONE) AnnounceOuterCursor(pShop, pShop->Cursor);
        }
        else if (state == 0x08)
        {
            // the REAL character-select state (Daidara equip flow). Shiroku
            // goes 0x09->0x0A directly and must not hear this.
            Speech.Say("Character select.", true);
        }
        else if (state == 0x0C)
        {
            // amount window (quantity at +0x4E2, F10-diff verified): orient
            // with the starting quantity + unit price
            short qty = *(short*)((nint)pShop + 0x4E2);
            int unit = RowPrice(pShop);
            Speech.Say(unit > 0 ? $"How many? {qty}. {unit} yen each."
                                 : $"How many? {qty}.", true);
        }
        else if (state == 0x0B)
        {
            // description window opened → read the full form. interrupt:true
            // also cancels the short-form announce when the window re-opens
            // right after a row move, so the user hears the full read once.
            _announceListIn = 1;
        }
        else if (state == ST_SELL)
        {
            // The sell list is now read live in the poll (AnnounceSellCursor off
            // pShop+0x60/cursor+0x32). Arm a fresh announce; an EMPTY sell list
            // shows "Nothing to sell" through the dialogue UI (its own reader).
            _lastSellRow = -1;
        }
        // ST_TALK: dialogue reader owns it.
    }

    // ── Sell screen (state 0x12) — RE'd 2026-06-18 (builder FUN_140280f60) ──
    // The sell list is built into the SAME field as the buy list: list pointer at
    // *(pShop+0x60) (ActiveListPtr), count at *(int*)(pShop+0x68); entries stride
    // 0x14 = {int price, u16 itemId @+0x04, u16 qty @+0x06}. The highlighted row is
    // the cursor at pShop+0x32 (confirmed live — c32 stepped 0..N in lockstep with
    // the on-screen move). The old reader read the wrong list (player inventory at
    // +0x10) — gone. Announces "name, qty. price yen. description".
    private int _lastSellRow = -1;
    private bool AnnounceSellCursor(ShopStruct* pShop, int index)
    {
        nint p = (nint)pShop;
        if (!IsReadable(p + 0x60, 8)) return false;
        nint list = *(nint*)(p + 0x60);
        int count = IsReadable(p + 0x68, 4) ? *(int*)(p + 0x68) : 0;
        if (list == 0 || index < 0 || index > 255) return false;
        nint e = list + index * 0x14;
        if (!IsReadable(e, 0x14)) return false;
        int price = *(int*)e;
        ushort id = *(ushort*)(e + 4);
        ushort qty = *(ushort*)(e + 6);
        if (price < 0 || price > 9_999_999) return false;

        // Each category's list ends with a "Sell all" bulk option whose id is the
        // category BLOCK-BASE (e.g. 1280 for materials, "Blank" → GetName empty)
        // and whose price is the TOTAL. Label it instead of "Item 1280".
        string name = Item.GetName(id);
        if (string.IsNullOrEmpty(name))
        {
            if (price <= 0) return false;   // genuinely empty entry, not Sell all
            Log($"[ShopMenu] Sell[{index}/{count}] SELL ALL id={id} price={price}");
            Speech.Say($"Sell all. {price} yen", true);
            return true;
        }
        if (id > 0x3000) return false;
        string msg = qty > 1 ? $"{name}, {qty}" : name;
        if (price > 0) msg += $". {price} yen";
        string desc = Item.GetDescription(id);
        if (!string.IsNullOrEmpty(desc)) msg += ". " + desc;
        Log($"[ShopMenu] Sell[{index}/{count}] id={id} qty={qty} price={price} \"{name}\"");
        Speech.Say(msg, true);
        return true;
    }

    // Daidara Metalworks (Tatsumi Metalworks) top-menu labels in cursor order.
    // Confirmed by user 2026-04-19; character-select appears after picking one of
    // the first three (Weapon/Armor/Accessory) but lives in a separate UI that
    // doesn't write to pShop, so we only cover the top menu + item list here.
    private static readonly string[] DaidaraMainMenu =
        { "Weapon", "Armor", "Accessory", "Sell", "Talk", "Exit" };

    private static bool IsInDaidara() =>
        FieldTracker.CurrentMajor == 8 &&
        (FieldTracker.CurrentMinor == 4 || FieldTracker.CurrentMinor == 5);

    // ── Announce the outer cursor ────────────────────────────────────────
    // The ShopStruct is shared across all shop sub-menus. When ActiveItemCount>0
    // we're in an item list and can read the item directly. When it's 0 we're
    // either in the top menu or in character-select (which doesn't touch pShop,
    // so its cursor just sits on whatever the main menu was last set to). For
    // known shops we speak the top-menu label; otherwise silent.
    // Top-menu option IDs (pShop+0x340 array, count at +0x35A) are
    // SHOP-AGNOSTIC: the ShopUpdate dispatch maps 8->organize/sell screen,
    // 9->talk, 10->exit etc., and the equip-shop builder writes 0..3 for the
    // buy categories. Labels per id (extend as other shops reveal theirs).
    private static readonly Dictionary<int, string> OptionLabels = new()
    {
        [0] = "Weapon", [1] = "Armor", [2] = "Accessory", [3] = "Buy",
        [4] = "Buy", [5] = "Buy", [6] = "Buy", [7] = "Buy",
        [8] = "Sell", [9] = "Talk", [10] = "Exit", [11] = "Talk", [12] = "Special",
    };

    private void AnnounceOuterCursor(ShopStruct* pShop, int cursor35C)
    {
        // generic path: read the option id under the cursor
        short optCount = *(short*)((nint)pShop + 0x35A);
        if (cursor35C >= 0 && cursor35C < optCount && optCount <= 16)
        {
            short optId = *(short*)((nint)pShop + 0x340 + cursor35C * 2);
            string label = OptionLabels.TryGetValue(optId, out var l) ? l : $"Option {optId}";
            // Daidara's confirmed labels win where applicable
            if (IsInDaidara() && cursor35C < DaidaraMainMenu.Length)
                label = DaidaraMainMenu[cursor35C];
            Log($"[ShopMenu] Outer {cursor35C}: optId={optId} label \"{label}\"");
            Speech.Say(label, true);
            return;
        }
        if (IsInDaidara() && cursor35C >= 0 && cursor35C < DaidaraMainMenu.Length)
        {
            Speech.Say(DaidaraMainMenu[cursor35C], true);
            return;
        }
        Log($"[ShopMenu] Outer {cursor35C}: silent (optCount={optCount})");
    }

    // ── Read and announce one active-list entry ──────────────────────────
    // The active list (pShop+0x060, stride=0x14) holds the current sub-menu's
    // items. HOWEVER — after the user briefly enters a sub-menu and cancels
    // back to the top menu, the pointer lingers and points at STALE data from
    // that previous visit. To block stale reads, we verify that the entry's
    // HelpBmd tag (hi16 of packed) matches the category implied by cursor35C
    // in Daidara: Weapon=0, Armor=1, Accessory=2. Mismatch → silent.
    // Returns true if something was spoken.
    private bool AnnounceFromActiveList(ShopStruct* pShop, int index, string source)
    {
        nint activePtr   = pShop->ActiveListPtr;
        int  cursor35C   = pShop->Cursor;

        // v2.2: do NOT gate on ActiveItemCount — it lies (reads 1 with two
        // accessories on screen, then drifts to 2; the refusal made cursor
        // moves silent). Bound generously and validate the ENTRY CONTENT
        // below instead (plausible id + non-empty name = real row).
        if (activePtr == 0 || index < 0 || index >= 64) return false;
        int needBytes = (index + 1) * ActiveEntryStride;
        if (!IsReadable(activePtr, needBytes)) return false;

        var entryPtr   = (byte*)activePtr + index * ActiveEntryStride;
        int price      = *(int*)(entryPtr + 0x00);
        int packed     = *(int*)(entryPtr + ActiveEntryPacked);
        int gameItemId = packed & 0xFFFF;                 // lo16 → GLOBAL item id
        int hi16       = (packed >> 16) & 0xFFFF;         // hi16 → unknown tag (shop slot / char code)

        // content plausibility — replaces the lying ActiveItemCount gate
        if (gameItemId <= 0 || gameItemId >= 0x3000 || price < 0 || price > 9_999_999)
        {
            Log($"[ShopMenu] {source}[{index}]: implausible entry (id={gameItemId} price={price}) — silent");
            return false;
        }

        // Daidara: cursor35C picks the HelpBmd (0=Weapon, 1=Armor, 2=Accessory).
        // BMDs are indexed RELATIVE to each category (256 entries each), while
        // the packed field carries a GLOBAL id with weapons 0-255, armor 256-511,
        // accessories 512-767. So dialogIdx = globalId - 256*cursor35C.
        // We read both NAME and DESCRIPTION from the same BMD MessageDialog
        // (ReadMessageDialogName returns the 24-byte name field; ReadDescription
        // returns the page text). The native-array probe returned skill names,
        // not item names — item names are in the BMDs.
        // Shop-agnostic: the GLOBAL item id encodes the category — derive the
        // description BMD from it so Shiroku/other shops (and the Daidara SELL
        // screen, where MATERIAL drops 1280-1535 show up) get descriptions too.
        // Block map mirrors Item.GetDescription / PlayerMenu.DescribeItem so every
        // category reads, not just equipment+consumables. (Old map stopped at 1024,
        // so shadow material drops were name-only on the sell screen — 2026-06-18.)
        Dialog.HelpBmd? helpBmd = null;
        int dialogIdx = gameItemId;
        if (gameItemId < 256)        { helpBmd = Dialog.HelpBmd.Weapon;    dialogIdx = gameItemId; }
        else if (gameItemId < 512)   { helpBmd = Dialog.HelpBmd.Armor;     dialogIdx = gameItemId - 256; }
        else if (gameItemId < 768)   { helpBmd = Dialog.HelpBmd.Accessory; dialogIdx = gameItemId - 512; }
        else if (gameItemId < 1024)  { helpBmd = Dialog.HelpBmd.Item;      dialogIdx = gameItemId - 768; }
        else if (gameItemId < 1281)  { helpBmd = Dialog.HelpBmd.Event;     dialogIdx = gameItemId - 1024; }
        else if (gameItemId < 1536)  { helpBmd = Dialog.HelpBmd.Material;  dialogIdx = gameItemId - 1280; }
        else if (gameItemId < 1792)  { helpBmd = Dialog.HelpBmd.SkillCard; dialogIdx = gameItemId - 1536; }
        else if (gameItemId < 2048)  { helpBmd = Dialog.HelpBmd.Dress;     dialogIdx = gameItemId - 1792; }
        else if (gameItemId < 2304)  { helpBmd = Dialog.HelpBmd.Item2;     dialogIdx = gameItemId - 2048; }
        else if (gameItemId < 2560)  { helpBmd = Dialog.HelpBmd.Weapon2;   dialogIdx = gameItemId - 2304; }

        string name = Item.GetName(gameItemId), desc = "", effect = "";
        bool full = _shopState == 0x0B;   // description window open → long form
        if (full && helpBmd is { } hb)
        {
            desc = ReadDescription(hb, dialogIdx);
            // AddEffect only applies to equipment (weapon/armor/accessory); reading
            // it for consumables/materials just returns garbage we never speak.
            if (gameItemId < 768)
                effect = ReadDescription(Dialog.HelpBmd.AddEffect, dialogIdx);
        }
        Log($"[ShopMenu] {source}[{index}]: packed=0x{packed:X8} lo16={gameItemId} hi16={hi16} helpBmd={(helpBmd?.ToString() ?? "none")} dialogIdx={dialogIdx} price={price} full={full} name=\"{name}\" desc=\"{desc}\"");

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(desc))
        {
            Log($"[ShopMenu] {source}[{index}]: no name/desc — silent (price={price})");
            return false;
        }

        // TRADE shop (Shiroku Pub "Trade Items"): there's no yen price — the cost is
        // MATERIALS, stored inline in the entry as up to two {u16 matId, u16 needed}
        // pairs at +0x0C and +0x10. Owned count = *(byte*)(*(0x141165930) + matId).
        // (RE'd live 2026-06-18: Spiked Bat 2311 needs Iolite 2128 ×5, owned 1.)
        string trade = price == 0 ? TradeRequirement(entryPtr) : "";

        // Browsing (no description window): name, price, your money — short
        // and scannable (user spec 2026-06-12). Description window open:
        // name + description + price.
        string text;
        if (full)
        {
            text = string.IsNullOrEmpty(desc) ? name : $"{name}. {desc}";
            if (price > 0) text = $"{text}. {price} yen";
        }
        else
        {
            text = name;
            if (price > 0) text = $"{text}. {price} yen";
            // Wallet is irrelevant on a trade screen — only show it for yen shops.
            if (string.IsNullOrEmpty(trade))
            {
                uint wallet = ReadWallet();
                if (wallet > 0) text = $"{text}. You have {wallet} yen";
            }
        }
        if (!string.IsNullOrEmpty(trade)) text = $"{text}. {trade}";
        Speech.Say(text, true);
        return true;
    }

    // ── Trade-shop requirement (Shiroku Pub) ────────────────────────────
    // Entry holds up to two {u16 matId @+0x0C/+0x10, u16 needed @+0x0E/+0x12} pairs.
    // Speaks "Needs Iolite times 5, you have 1" (+ a second material if present).
    private static readonly nint InvCountBasePtr = unchecked((nint)0x141165930L);
    private static int OwnedCount(int id)
    {
        if (id <= 0 || !IsReadable(InvCountBasePtr, 8)) return 0;
        nint b = *(nint*)InvCountBasePtr;
        return (b != 0 && IsReadable(b + id, 1)) ? *(byte*)(b + id) : 0;
    }

    private static string TradeRequirement(byte* entryPtr)
    {
        var parts = new List<string>(2);
        for (int m = 0; m < 2; m++)
        {
            ushort matId = *(ushort*)(entryPtr + 0x0C + m * 4);
            ushort need  = *(ushort*)(entryPtr + 0x0E + m * 4);
            if (matId == 0 || matId > 0x3000 || need == 0 || need > 999) continue;
            string mn = Item.GetName(matId);
            if (string.IsNullOrEmpty(mn)) mn = $"item {matId}";
            // Concise so it doesn't get cut off during fast list navigation:
            // "5 Iolite, have 1".
            parts.Add($"{need} {mn}, have {OwnedCount(matId)}");
        }
        return parts.Count == 0 ? "" : "Needs " + string.Join("; ", parts);
    }

    // ── wallet (same global the battle ResultReader uses) ───────────────
    private static readonly nint WalletAddr = unchecked((nint)0x1451BCD70L);
    private static uint ReadWallet()
        => IsReadable(WalletAddr, 4) ? *(uint*)WalletAddr : 0;

    // ── protagonist's custom name ────────────────────────────────────────
    // Found 2026-06-12 by full-memory scan: the save block near the wallet
    // holds the names as FULL-WIDTH Shift-JIS — first name at 0x1451BCD8E
    // (16-byte field; "ＨＡＲＵ" = 82 67 82 60 82 71 82 74), last name in
    // the field before it. Decoded to ASCII for speech; "You" if unreadable.
    private static readonly nint FirstNameAddr = unchecked((nint)0x1451BCD8EL);

    internal static string ProtagonistName()
    {
        if (!IsReadable(FirstNameAddr, 16)) return "You";
        var sb = new System.Text.StringBuilder(8);
        for (int i = 0; i + 1 < 16; i += 2)
        {
            byte hi = *(byte*)(FirstNameAddr + i);
            byte lo = *(byte*)(FirstNameAddr + i + 1);
            if (hi == 0) break;
            char c;
            if (hi == 0x82 && lo >= 0x60 && lo <= 0x79) c = (char)('A' + (lo - 0x60));
            else if (hi == 0x82 && lo >= 0x81 && lo <= 0x9A) c = (char)('a' + (lo - 0x81));
            else if (hi == 0x82 && lo >= 0x4F && lo <= 0x58) c = (char)('0' + (lo - 0x4F));
            else if (hi == 0x81 && lo == 0x40) c = ' ';
            else break;   // non-Latin glyph — bail to whatever we decoded
            sb.Append(c);
        }
        if (sb.Length == 0) return "You";
        // ＨＡＲＵ → Haru (title case reads better than all-caps)
        string s = sb.ToString();
        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    // ── Announce current item (called on F keypress and inner cursor change) ──
    // Reads only from the active list (stride=0x14). The stride-0x90 ItemListPtr
    // fallback was removed on 2026-04-19: that list is the player's consumable
    // inventory, not the shop's sale items, and reading from it caused random
    // "Soft, sweet, milky candy" announcements in Daidara's main menu.
    private short _lastQty = -1;   // pShop+0x4E2 mirror (amount window)

    /// <summary>Unit price of the currently selected row (window-mode row
    /// field +0x32 persists through the amount window).</summary>
    private static int RowPrice(ShopStruct* p)
    {
        nint list = p->ActiveListPtr;
        short row = *(short*)((nint)p + 0x32);
        // On a SCROLLING shop list (e.g. Shiroku Store), +0x32 is the WINDOW position, not the
        // absolute row, so add the scroll +0x34 (guarded like EffectiveRow). Without this the amount
        // window read the wrong item's price — live 2026-06-28: Dokudami Tea (row 4, 450 yen) was
        // priced as Revival Bead (row 2, 1950), so "Total" was 5850 instead of 1350 for qty 3.
        short scroll = *(short*)((nint)p + 0x34);
        short total  = *(short*)((nint)p + 0x68);
        if (scroll > 0 && scroll < 512 && total > 0 && row + scroll < total)
            row = (short)(row + scroll);
        if (list == 0 || row < 0 || row >= 64) return 0;
        nint entry = list + row * ActiveEntryStride;
        if (!IsReadable(entry, 4)) return 0;
        int price = *(int*)entry;
        return price > 0 && price < 10_000_000 ? price : 0;
    }

    private static short EffectiveRow(ShopStruct* p)
    {
        // List focused (0x0A): window cursor @ +0x9E. Description window open
        // (0x0B): +0x9E goes -1 and the cursor is at +0x32 (F10-diff verified
        // 2026-06-12). LONG lists SCROLL (e.g. the Shiroku Pub trade shop — 16
        // items, ~6 visible): the cursor above is the WINDOW position and +0x34
        // is the scroll (first-visible absolute index), so the real row = cursor
        // + scroll (live-verified 2026-06-18: Spring Boots = +0x9E 5 + +0x34 10 =
        // 15). Short non-scrolling shops keep +0x34 = 0, so this is a no-op there;
        // the count guard (+0x68) ignores a bogus scroll.
        short cur = ShopState == 0x0B ? *(short*)((nint)p + 0x32) : *(short*)((nint)p + 0x9E);
        if (cur < 0) return cur;                       // -1 = list unfocused
        short scroll = *(short*)((nint)p + 0x34);
        short total  = *(short*)((nint)p + 0x68);
        if (scroll > 0 && scroll < 512 && total > 0 && cur + scroll < total)
            return (short)(cur + scroll);
        return cur;
    }

    private void AnnounceCurrentItem()
    {
        if (_lastPtr == null) return;
        short row = EffectiveRow(_lastPtr);
        if (row < 0 || row >= 64) row = 0;
        AnnounceFromActiveList(_lastPtr, row, "F");
    }

    // ── Combine name + description into a single announcement string ─────
    // Filters out internal game name codes like "item_3C6" or "weapon_004" as a safety net.
    private static string CombineNameDesc(string name, string desc)
    {
        bool goodName = !string.IsNullOrWhiteSpace(name)
                        && !name.StartsWith("item_", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("armor_", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("accessory_", StringComparison.OrdinalIgnoreCase)
                        && name != "blank"
                        && name != "????";
        bool goodDesc = !string.IsNullOrWhiteSpace(desc)
                        && desc != "blank"
                        && desc != "????";

        if (goodName && goodDesc) return $"{name}. {desc}";
        if (goodName) return name;
        if (goodDesc) return desc;
        return "";
    }

    // ── Memory safety: check that [addr, addr+size) is committed and readable ──
    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000 || a > 0x00007FFFFFFFFFFFUL) return false;

        const int  MBI_SIZE    = 48;
        const int  OFF_STATE   = 32;
        const int  OFF_PROTECT = 36;
        const uint MEM_COMMIT    = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD    = 0x100;

        byte* buf = stackalloc byte[MBI_SIZE];
        if (VirtualQuery(addr, buf, MBI_SIZE) == 0) return false;

        uint state   = *(uint*)(buf + OFF_STATE);
        uint protect = *(uint*)(buf + OFF_PROTECT);
        if (state   != MEM_COMMIT)    return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        if ((protect & PAGE_GUARD)    != 0) return false;
        return true;
    }

    // ── Map shop category code (stride=0x90 format) to HelpBmd ──────────
    private static Dialog.HelpBmd CategoryToHelpBmd(int category) => category switch
    {
        0 => Dialog.HelpBmd.Weapon,
        1 => Dialog.HelpBmd.Armor,
        2 => Dialog.HelpBmd.Item,
        3 => Dialog.HelpBmd.Accessory,
        _ => Dialog.HelpBmd.Item
    };

    // ── Read description text from a HelpBmd ─────────────────────────────
    // Every pointer is validated via IsReadable before being dereferenced.
    // .NET 9 does not let us catch AccessViolationException (it terminates
    // the process), so we can't rely on the try/catch — prevention is the
    // only option. Garbage input is quietly turned into an empty string.
    private static string ReadDescription(Dialog.HelpBmd helpBmd, int itemId)
    {
        try
        {
            if (!Enum.IsDefined(typeof(Dialog.HelpBmd), helpBmd)) return "";

            var exec = Dialog.GetExecution(helpBmd);
            if (exec == null || !IsReadable((nint)exec, 0x20)) return "";
            if (exec->Info == null || !IsReadable((nint)exec->Info, 0x20)) return "";
            if (exec->Info->Bmd == null || !IsReadable((nint)exec->Info->Bmd, 0x40)) return "";

            var bmd = exec->Info->Bmd;
            int dialogCount = bmd->Header.DialogCount;
            if (dialogCount <= 0 || dialogCount > 100000) return "";
            if (itemId < 0 || itemId >= dialogCount) return "";

            var hdr = (&bmd->DialogHeaders)[itemId];
            if (hdr.Kind != Dialog.DialogKind.Message || hdr.MessageDialog == null) return "";
            if (!IsReadable((nint)hdr.MessageDialog, 0x40)) return "";

            var msg = hdr.MessageDialog;
            if (msg->PageCount < 1) return "";

            var page = msg->Pages;
            if (page.Text == null || page.TextSize <= 0 || page.TextSize > 10000) return "";
            if (!IsReadable((nint)page.Text, page.TextSize)) return "";

            return AtlusEncoding.P4.GetString(page.Text, page.TextSize)
                .Replace('\0', ' ').Replace('\n', ' ').Trim();
        }
        catch (Exception ex)
        {
            Log($"[ShopMenu] ReadDescription error: {ex.Message}");
            return "";
        }
    }

    // ── Read the 24-byte Name field from a HelpBmd MessageDialog ─────────
    // Same validation pattern as ReadDescription — prevent AV on garbage input.
    private static string ReadMessageDialogName(Dialog.HelpBmd helpBmd, int itemId)
    {
        try
        {
            if (!Enum.IsDefined(typeof(Dialog.HelpBmd), helpBmd)) return "";

            var exec = Dialog.GetExecution(helpBmd);
            if (exec == null || !IsReadable((nint)exec, 0x20)) return "";
            if (exec->Info == null || !IsReadable((nint)exec->Info, 0x20)) return "";
            if (exec->Info->Bmd == null || !IsReadable((nint)exec->Info->Bmd, 0x40)) return "";

            var bmd = exec->Info->Bmd;
            int dialogCount = bmd->Header.DialogCount;
            if (dialogCount <= 0 || dialogCount > 100000) return "";
            if (itemId < 0 || itemId >= dialogCount) return "";

            var hdr = (&bmd->DialogHeaders)[itemId];
            if (hdr.Kind != Dialog.DialogKind.Message || hdr.MessageDialog == null) return "";
            if (!IsReadable((nint)hdr.MessageDialog, 0x40)) return "";

            var msg = hdr.MessageDialog;
            var nb = msg->Name;
            if (!IsReadable((nint)nb, 24)) return "";
            int nl = 0;
            for (int i = 0; i < 24 && nb[i] != 0; i++) nl++;
            return System.Text.Encoding.UTF8.GetString(nb, nl);
        }
        catch { return ""; }
    }

    // ── Background thread: F key description window tracking ──
    // F key toggles the in-game description window:
    //   open  → announce current item; then auto-read whenever cursor moves
    //   close → stop reading
    // (The F10 dev "diff-dumper" key was UNBOUND for release 2026-06-28; DumpDiff/
    //  DumpShopStruct are kept below as dead code for future field-hunting.)
    private void PollKeys()
    {
        while (true)
        {
            System.Threading.Thread.Sleep(50);

            // Ignore keys entirely when the game is not the foreground window —
            // otherwise F/Esc fire while the user is Alt-Tabbed away.
            bool focused = IsGameFocused();

            bool fPressedRaw = (GetAsyncKeyState(0x46) & 0x8000) != 0;
            _fPressedRawWas = fPressedRaw;

            // Read key states regardless of shop active (prevents stuck toggles)
            bool fNow   = focused && fPressedRaw;                               // 'F'
            bool escNow = focused && (GetAsyncKeyState(0x1B) & 0x8000) != 0;    // Escape
            bool gNow   = focused && (GetAsyncKeyState(0x47) & 0x8000) != 0;    // 'G' = wallet

            // Gamepad Y button via XInput (all four possible controllers).
            // Log controller state once at startup to diagnose connectivity.
            bool yNow = false;
            for (uint ci = 0; ci < 4; ci++)
            {
                XInputState xs;
                uint xr = XInputGetState(ci, out xs);
                if (!_xInputLogged)
                    Log($"[ShopMenu] XInput[{ci}]: result={xr} buttons=0x{xs.Gamepad.Buttons:X4}");
                if (xr == 0 && focused && (xs.Gamepad.Buttons & XInputGamepad.X) != 0)
                    yNow = true;
            }
            _xInputLogged = true;

            // Snapshot _lastPtr + validate before dereferencing. When the shop
            // closes, the game frees the struct but our hook doesn't fire again,
            // so _lastPtr still points at what is now unmapped memory. Reading
            // through it AVEs, which .NET 9 CANNOT catch — it terminates the
            // process. IsReadable uses VirtualQuery and is safe.
            var ptr = _lastPtr;
            if (ptr != null && _active && IsReadable((nint)ptr, sizeof(ShopStruct)))
            {
                // ── v3: the WHOLE state machine, live off the struct ──
                // The +0x04 state advances regardless of the starving hook:
                // 0x07 top · 0x08/0x09 charselect · 0x0A LIST · 0x0B DESC ·
                // 0x12 sell · 0x1C talk · 0x1E+ teardown.
                ushort state = ReadShopState(ptr);
                if (state <= 0x23 && state != _shopState)
                {
                    ushort prev = _shopState;
                    _shopState = state;
                    Log($"[ShopMenu] STATE 0x{prev:X2} -> 0x{state:X2} (cursor35C={ptr->Cursor} count={ptr->ActiveItemCount})");
                    OnStateChanged(ptr, prev, state);
                }

                // SELL screen (0x12): announce the highlighted item as the cursor
                // (pShop+0x32) moves through the sell list (*(pShop+0x60)).
                if (_shopState == ST_SELL)
                {
                    int sellRow = *(short*)((nint)ptr + 0x32);
                    // Latch only once the announce actually succeeds — the list can
                    // be null for a frame or two while the screen builds.
                    if (sellRow != _lastSellRow && AnnounceSellCursor(ptr, sellRow))
                        _lastSellRow = sellRow;
                }
                else _lastSellRow = -1;

                bool inBuy = InBuyFlow(_shopState);
                bool safeInList = _shopState is 0x0A or 0x0B
                                  || (inBuy && ptr->ActiveListPtr != 0);

                // top-menu / tab cursor (cursor35C)
                var cursor35C = ptr->Cursor;
                if (cursor35C >= 0 && cursor35C <= 99 && cursor35C != _lastCursor)
                {
                    _lastCursor = cursor35C;
                    if (_shopState == ST_TOP)
                        AnnounceOuterCursor(ptr, cursor35C);
                    else if (safeInList && IsInDaidara() && cursor35C >= 0 && cursor35C <= 2)
                    {
                        // tab switch inside the buy screen
                        Speech.Say(DaidaraMainMenu[cursor35C], true);
                        _announceListIn = 2;
                    }
                }

                // list-opened edge (fields populate after charselect confirm)
                short listCount = ptr->ActiveItemCount;
                nint  listPtr   = ptr->ActiveListPtr;
                if (inBuy && listPtr != 0 && listCount > 0
                    && (_lastListCount <= 0 || listPtr != _lastListPtr))
                {
                    Log($"[ShopMenu] list opened (count={listCount} ptr=0x{(ulong)listPtr:X})");
                    _announceListIn = 2;
                }
                _lastListCount = inBuy ? listCount : (short)0;
                _lastListPtr   = inBuy ? listPtr : 0;

                // ── THE REAL FIELDS (F10-diff session 2026-06-12) ──
                //   pShop+0x09E (short) = row cursor (-1 = no list focused)
                //   pShop+0x4E0 (short) = selected character slot (0=You...)
                // Item announces gate on the EXACT list/desc states (0x0A/
                // 0x0B) — announcing in 0x08/0x09 caused the "weapon keeps
                // reading on character select" repeat (user 2026-06-12).
                bool inListExact = _shopState is 0x0A or 0x0B;
                if (inListExact)
                {
                    // +0x9E = row while the list has focus; with the
                    // description window open (0x0B) it can drop to -1 and
                    // +0x9C carries the row instead — watch the COMBINED
                    // effective row so moves announce in both modes.
                    short row = EffectiveRow(ptr);
                    if (row != _lastRow)
                    {
                        Log($"[ShopMenu] row {_lastRow} -> {row} (9E={*(short*)((nint)ptr + 0x9E)} 9C={*(short*)((nint)ptr + 0x9C)} state=0x{_shopState:X2})");
                        bool wasNone = _lastRow < 0;
                        _lastRow = row;
                        if (row >= 0 && !wasNone)
                            AnnounceCurrentItem();
                    }
                }
                else _lastRow = -2;

                // character switch. ONLY announced while the LIST is focused
                // (in-list L/R switching) — on the character-select screen
                // DaidaraCharSelect's hook speaks the names, and announcing
                // here too caused the name repeat (user 2026-06-12).
                if (inBuy)
                {
                    short who = *(short*)((nint)ptr + 0x4E0);
                    if (who != _lastWho)
                    {
                        Log($"[ShopMenu] character {_lastWho} -> {who} (state=0x{_shopState:X2})");
                        bool wasNone = _lastWho < 0;
                        _lastWho = who;
                        if (who >= 0 && who < CharNames.Length && !wasNone && inListExact)
                        {
                            Speech.Say(who == 0 ? ProtagonistName() : CharNames[who], true);
                            _announceListIn = 3;   // their list rebuilds
                        }
                    }
                }
                else _lastWho = -1;

                // amount window: announce quantity changes with running total
                if (_shopState == 0x0C)
                {
                    short qty = *(short*)((nint)ptr + 0x4E2);
                    if (qty != _lastQty)
                    {
                        bool wasNone = _lastQty < 0;
                        _lastQty = qty;
                        if (qty > 0 && !wasNone)
                        {
                            int unit = RowPrice(ptr);
                            Speech.Say(unit > 0 ? $"{qty}. Total {qty * unit} yen."
                                                 : qty.ToString(), true);
                        }
                    }
                }
                else _lastQty = -1;

                // deferred announce after entry/tab/character switch — only
                // when the LIST is actually in front of the user
                if (_announceListIn > 0 && --_announceListIn == 0 && inListExact)
                    AnnounceCurrentItem();

                if (fNow && !_fKeyWas)
                {
                    if (inListExact) AnnounceCurrentItem();
                    else Log($"[ShopMenu] F: ignored — state=0x{_shopState:X2} not a list");
                }

                // G = wallet readout (works on any shop screen)
                if (gNow && !_gKeyWas)
                {
                    uint w = ReadWallet();
                    Speech.Say($"You have {w} yen.", true);
                }

                // Gamepad Y/X button: re-read the current item.
                if (yNow && !_yButtonWas)
                {
                    Log($"[ShopMenu] Y/X button edge (state=0x{_shopState:X2})");
                    if (inListExact) AnnounceCurrentItem();
                }
            }
            else if (ptr != null)
            {
                // Struct pointer went unreadable — the shop closed and the
                // game freed it. Clear so no stale flags leak into the next
                // shop visit.
                _lastPtr        = null;
                _active         = false;
                _shopState      = ST_NONE;
                _announceListIn = 0;
                Log("[ShopMenu] Shop struct freed — poll state cleared.");
            }

            _fKeyWas    = fNow;
            _escWas     = escNow;
            _yButtonWas = yNow;
            _gKeyWas    = gNow;
        }
    }

    // ── F10 v3: cursor-hunt differ ───────────────────────────────────────
    // Snapshots pShop (0x700) + the shop UI widget statics (0x140BEA800..
    // 0x140BEC000) and prints WHAT CHANGED since the previous F10. Usage:
    // sit in a list → F10 → scroll ONE row → F10 → the diff lines contain
    // the row cursor, wherever the widget system keeps it.
    private byte[]? _diffShop;
    private byte[]? _diffStatics;
    private const long StaticsBase = 0x140BEA800L;
    private const int  StaticsLen  = 0x1800;

    private void DumpDiff()
    {
        var ptr = _lastPtr;
        if (ptr == null || !IsReadable((nint)ptr, 0x700))
        {
            Log("[ShopMenu] F10diff: no shop struct");
            return;
        }
        var shop = new byte[0x700];
        for (int i = 0; i < shop.Length; i++) shop[i] = *((byte*)ptr + i);
        var stat = new byte[StaticsLen];
        if (IsReadable((nint)StaticsBase, StaticsLen))
            for (int i = 0; i < StaticsLen; i++) stat[i] = *(byte*)(StaticsBase + i);

        if (_diffShop != null && _diffStatics != null)
        {
            var sb = new System.Text.StringBuilder("[ShopMenu] F10diff vs previous:\n");
            int n = 0;
            for (int off = 0; off < shop.Length - 1 && n < 60; off += 2)
            {
                short a = (short)(_diffShop[off] | (_diffShop[off + 1] << 8));
                short b = (short)(shop[off] | (shop[off + 1] << 8));
                if (a != b && Math.Abs(b) < 1000 && Math.Abs(a) < 1000)
                { sb.AppendLine($"  pShop+0x{off:X3}: {a} -> {b}"); n++; }
            }
            for (int off = 0; off < StaticsLen - 1 && n < 120; off += 2)
            {
                short a = (short)(_diffStatics[off] | (_diffStatics[off + 1] << 8));
                short b = (short)(stat[off] | (stat[off + 1] << 8));
                if (a != b && Math.Abs(b) < 1000 && Math.Abs(a) < 1000)
                { sb.AppendLine($"  static 0x{StaticsBase + off:X}: {a} -> {b}"); n++; }
            }
            Log(sb.ToString().TrimEnd());
            Speech.Say($"Diff logged, {n} changes.", true);
        }
        else
        {
            Log("[ShopMenu] F10diff: baseline captured");
            Speech.Say("Baseline captured. Scroll one row, press F10 again.", true);
        }
        _diffShop = shop;
        _diffStatics = stat;
    }

    private void DumpShopStruct()
    {
        if (_lastPtr == null || !_active)
        {
            Log("[ShopMenu] F10: no shop struct captured yet (open the shop first)");
            return;
        }

        var p           = (byte*)_lastPtr;
        int cursor35C   = _lastPtr->Cursor;
        int itemCount   = _lastPtr->ItemCount;
        nint listPtr    = _lastPtr->ItemListPtr;
        nint ptr060     = _lastPtr->ActiveListPtr;
        short cursor032 = _lastPtr->ActiveCursor;
        short active032 = _lastPtr->ActiveItemCount;

        Log($"[ShopMenu] F10 — base=0x{(nuint)p:X}  cursor35C={cursor35C}  cursor032={cursor032}  itemCount={itemCount}  active032Count={active032}");
        Log($"[ShopMenu]   +0x010 (ItemListPtr)  = 0x{(ulong)listPtr:X}");
        Log($"[ShopMenu]   +0x060 (ActiveListPtr) = 0x{(ulong)ptr060:X}");

        // Dump non-zero ints in first 0x400 bytes
        Log("[ShopMenu] pShop non-zero ints [0x000-0x400]:");
        for (int i = 0; i < 0x400 / 4; i++)
        {
            int v = *(int*)(p + i * 4);
            if (v != 0) Log($"  +0x{i * 4:X3} = {v}  (0x{(uint)v:X8})");
        }

        // Dump consumable list (stride=0x90)
        DumpItemList090("+0x010", listPtr, itemCount);

        // Dump active category list (stride=0x14)
        DumpItemList014("+0x060", ptr060, active032);

        // Stride diagnostic: read from known weapon name address to find array layout
        int gameItemId = 0;
        if (ptr060 != 0 && active032 > 0 && cursor032 >= 0 && cursor032 < active032)
        {
            var entryPtr = (byte*)ptr060 + cursor032 * ActiveEntryStride;
            if (IsReadable((nint)entryPtr, ActiveEntryStride))
            {
                int packed = *(int*)(entryPtr + ActiveEntryPacked);
                gameItemId = packed & 0xFFFF;
            }
        }
        Item.DiagnoseNameStride(gameItemId);
    }

    // ── Dump item list with stride=0x90 (consumable shop format) ─────────
    private void DumpItemList090(string label, nint listPtr, int itemCount)
    {
        if (!IsReadable(listPtr, ListFirstEntryOffset + EntryStride))
        {
            Log($"[ShopMenu] {label}: not readable (0x{(ulong)listPtr:X})");
            return;
        }

        int slotCount = Math.Min(itemCount > 0 ? itemCount : 20, 30);
        Log($"[ShopMenu] {label} entries (stride=0x90, count={slotCount}):");
        for (int slot = 0; slot < slotCount; slot++)
        {
            var e   = (byte*)listPtr + ListFirstEntryOffset + slot * EntryStride;
            if (!IsReadable((nint)e, 16)) { Log($"  slot={slot} — unreadable"); break; }
            int v0  = *(int*)(e + 0x00);
            int cat = *(int*)(e + EntryCategory);
            int id  = *(int*)(e + EntryItemId);
            int v3  = *(int*)(e + 0x0C);
            var hb  = CategoryToHelpBmd(cat);
            var nm  = ReadMessageDialogName(hb, id);
            var ds  = ReadDescription(hb, id);
            Log($"  slot={slot}  v0={v0}  cat={cat}  id={id}  v3={v3}  | helpBmd={hb}  name=\"{nm}\"  desc=\"{ds}\"");
        }

        // Raw dump
        Log($"[ShopMenu] {label} raw non-zero ints [0x000-0x200]:");
        var sp = (byte*)listPtr;
        for (int i = 0; i < 0x200 / 4; i++)
        {
            if (!IsReadable((nint)(sp + i * 4), 4)) break;
            int v = *(int*)(sp + i * 4);
            if (v != 0) Log($"  +0x{i * 4:X3} = {v}  (0x{(uint)v:X8})  lo={(ushort)v}  hi={(ushort)((uint)v >> 16)}");
        }
    }

    // ── Dump item list with stride=0x14 (weapon/armor/accessory shop format) ──
    private void DumpItemList014(string label, nint listPtr, short count)
    {
        if (!IsReadable(listPtr, ActiveEntryStride))
        {
            Log($"[ShopMenu] {label}: not readable (0x{(ulong)listPtr:X})");
            return;
        }

        // ── HelpBmd availability diagnostic ──────────────────────────────
        Log("[ShopMenu] HelpBmd availability (in current shop context):");
        foreach (var bmd in Enum.GetValues<Dialog.HelpBmd>())
        {
            try
            {
                var exec = Dialog.GetExecution(bmd);
                if (exec == null || exec->Info == null || exec->Info->Bmd == null)
                {
                    Log($"  {bmd}({(int)bmd}): not loaded");
                    continue;
                }
                int dc = exec->Info->Bmd->Header.DialogCount;
                // Sample indices 0 and 1 to see what's actually in there
                var s0 = ReadDescription(bmd, 0);
                var s1 = ReadDescription(bmd, 1);
                Log($"  {bmd}({(int)bmd}): DialogCount={dc}  [0]=\"{s0}\"  [1]=\"{s1}\"");
            }
            catch (Exception ex) { Log($"  {bmd}({(int)bmd}): exception {ex.Message}"); }
        }

        int slotCount = Math.Min(count > 0 ? count : 8, 20);
        Log($"[ShopMenu] {label} entries (stride=0x14, count={slotCount}):");
        for (int slot = 0; slot < slotCount; slot++)
        {
            var e = (byte*)listPtr + slot * ActiveEntryStride;
            if (!IsReadable((nint)e, ActiveEntryStride)) { Log($"  slot={slot} — unreadable"); break; }
            int price      = *(int*)(e + 0x00);
            int packed     = *(int*)(e + ActiveEntryPacked);
            int flag       = *(int*)(e + 0x08);
            int itemId     = packed & 0xFFFF;
            int helpBmdIdx = (packed >> 16) & 0xFFFF;

            // Try direct mapping + offset-based guesses for each HelpBmd
            var descs = new System.Text.StringBuilder();
            foreach (var bmd in Enum.GetValues<Dialog.HelpBmd>())
            {
                try
                {
                    // Try direct id
                    var d = ReadDescription(bmd, itemId);
                    if (!string.IsNullOrWhiteSpace(d))
                        descs.Append($"  [{bmd}({(int)bmd}),id={itemId}]=\"{d}\"");
                    // Try id with common base offsets (equipment IDs often offset from consumables)
                    foreach (int baseOff in new[] { 300, 400, 500, 600, 200, 100 })
                    {
                        int mappedId = itemId - baseOff;
                        if (mappedId < 0) continue;
                        d = ReadDescription(bmd, mappedId);
                        if (!string.IsNullOrWhiteSpace(d))
                            descs.Append($"  [{bmd}({(int)bmd}),id={itemId}-{baseOff}={mappedId}]=\"{d}\"");
                    }
                }
                catch { }
            }
            Log($"  slot={slot}  price={price}  id={itemId}  helpBmdIdx={helpBmdIdx}  flag=0x{flag:X8}{descs}");
        }

        // Raw dump
        Log($"[ShopMenu] {label} raw non-zero ints [0x000-0x200]:");
        var sp = (byte*)listPtr;
        for (int i = 0; i < 0x200 / 4; i++)
        {
            if (!IsReadable((nint)(sp + i * 4), 4)) break;
            int v = *(int*)(sp + i * 4);
            if (v != 0) Log($"  +0x{i * 4:X3} = {v}  (0x{(uint)v:X8})  lo={(ushort)v}  hi={(ushort)((uint)v >> 16)}");
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Only accept hotkeys when P4G itself owns the foreground window. GetAsyncKeyState
    // is global, so without this we'd toggle the description window (and speak) while
    // the user was Alt-Tabbed away from a paused game. Delegates to the shared helper.
    private static bool IsGameFocused() => Utils.GameHasFocus();

    // ── XInput gamepad support ────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte   LeftTrigger;
        public byte   RightTrigger;
        public short  ThumbLX;
        public short  ThumbLY;
        public short  ThumbRX;
        public short  ThumbRY;

        public const ushort X = 0x4000;  // Square on PS / X on Xbox — the "item info" button in P4G
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint        PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    private delegate void ShopUpdateDelegate(ShopStruct* pShop, nint p2, nint p3, nint p4);
}
