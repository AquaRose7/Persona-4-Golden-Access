using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// ALBUM menu reader — the sofa activity that replays a maxed Social Link's memory
/// at a chosen rank. RE 2026-07-09 (snapshot method + Ghidra).
///
/// Handler = FUN_14014f110(param1); menu struct = *(param1 + 0x48):
///   +0x70  int    STATE  (2 = "Whose album" character list, 3 = "Which memory" rank screen)
///   +0x708 short  entry COUNT
///   +0x70A short  cursor      +0x70C short  scroll   → char index = cursor + scroll
///   +0x70E short  RANK        (rank screen; 0-based, display = +1)
///   +0x78 + i*0x46 = entry i:  +0x02 COMMU index, +0x04 max rank (11→clamp 10)
/// The COMMU index resolves to a name via social_links.json (shared with the SL
/// rank-up reader); party members not in that file fall back to "{Arcana} Social
/// Link" via the commu table (0x1411A6B10, row +0x10 = arcana byte) — never wrong.
/// </summary>
internal unsafe class AlbumMenu
{
    private static readonly nint HandlerVA     = unchecked((nint)0x14014F110L);
    private static readonly nint CommuTablePtr = unchecked((nint)0x1411A6B10L);

    private delegate nint UpdateFn(nint p1);
    private IHook<UpdateFn>? _hook;

    private readonly Dictionary<int, string> _names = new();   // commu index -> character name
    private int _lastState = -1, _lastIdx = -1, _lastRank = -1;

    internal AlbumMenu(IReloadedHooks hooks)
    {
        LoadNames();
        _hook = hooks.CreateHook<UpdateFn>(OnUpdate, HandlerVA).Activate();
        Log($"[Album] reader hook active @0x14014f110 ({_names.Count} names)");
    }

    private void LoadNames()
    {
        try
        {
            string path = DataPath("social_links.json");
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("commu_ids", out var ids)) return;
            foreach (var p in ids.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0
                    && int.TryParse(p.Name, out int cid))
                    _names[cid] = p.Value[0].GetString() ?? "";
        }
        catch (Exception ex) { Log($"[Album] names load failed: {ex.Message}"); }
    }

    private nint OnUpdate(nint p1)
    {
        nint ret = _hook!.OriginalFunction(p1);
        try { Read(p1); } catch { /* never let a hook throw */ }
        return ret;
    }

    private void Read(nint p1)
    {
        if (!IsReadable(p1 + 0x48, 8)) return;
        nint s = *(nint*)(p1 + 0x48);
        if (s <= 0x10000 || !IsReadable(s + 0x710, 2)) return;

        int state = *(int*)(s + 0x70);
        // Only the two interactive screens speak; init/close (1/5) reset.
        if (state != 2 && state != 3) { _lastState = state; _lastIdx = -1; _lastRank = -1; return; }

        int count  = *(short*)(s + 0x708);
        int cursor = *(short*)(s + 0x70A);
        int scroll = *(short*)(s + 0x70C);
        int rank   = *(short*)(s + 0x70E);
        int idx = cursor + scroll;
        if (count < 1 || count > 64 || idx < 0 || idx >= count) return;

        // Announce on entering a screen, moving the cursor, or changing the rank.
        bool changed = state != _lastState || idx != _lastIdx || (state == 3 && rank != _lastRank);
        if (!changed) return;
        _lastState = state; _lastIdx = idx; _lastRank = rank;

        nint entry = s + 0x78 + idx * 0x46;
        if (!IsReadable(entry, 6)) return;
        int commu = *(short*)(entry + 2);
        int rawMax = *(short*)(entry + 4);         // entry's rank count: 10, or 11 when romanced
        if (rawMax < 1 || rawMax > 20) rawMax = 10;
        // The game caps the rank NUMBER at 10 and shows the extra (11th) entry of a
        // ROMANCED link as "RANK 10, Lover" (the romance variant of the final rank),
        // not "Rank 11" — verified on-screen 2026-07-09. Mirror that.
        int rankMax = rawMax > 10 ? 10 : rawMax;
        bool lover = rank + 1 > rankMax;           // beyond the 10 normal ranks = the Lover memory

        string name = ResolveName(commu);
        string rankText = lover ? $"Rank {rankMax}, Lover" : $"Rank {rank + 1} of {rankMax}";
        string text = state == 3 ? $"{name}. {rankText}." : $"{name}. {idx + 1} of {count}.";
        Speech.Say(text, interrupt: true);
        Log($"[Album] state={state} idx={idx}/{count} commu={commu} rank={rank + 1}/{rankMax} lover={lover} say: {text}");
    }

    private string ResolveName(int commu)
    {
        if (_names.TryGetValue(commu, out var n) && !string.IsNullOrEmpty(n)) return n;
        // Fallback: arcana from the commu table — authoritative, never a wrong name.
        int arc = ReadArcanaByte(commu);
        if (arc > 0 && SocialLinkRankUp.ArcanaByByte.TryGetValue(arc, out var a))
            return $"{a} Social Link";
        return commu > 0 ? $"Social Link {commu}" : "Social Link";
    }

    private static int ReadArcanaByte(int commu)
    {
        if (commu < 0 || commu > 255 || !IsReadable(CommuTablePtr, 8)) return -1;
        nint table = *(nint*)CommuTablePtr;
        nint row = table + commu * 100 + 0x10;
        return (table != 0 && IsReadable(row, 1)) ? *(byte*)row : -1;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint protect = *(uint*)(buf + 36);
        if ((protect & 0x01) != 0 || (protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }
}
