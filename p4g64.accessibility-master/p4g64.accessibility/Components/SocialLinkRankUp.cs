using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Social Link RANK UP banner reader (2026-07-07). The "Rank up!!" overlay
/// (arcana card + "Rank N" + "Arcana • Character") is baked art + sprites —
/// nothing goes through a hookable text path. But the banner runs as named
/// TASKS in the task registry (the TV-listings anchor pattern):
///   cmmRankUp          — the banner animation; work fields (u16):
///                        +0x04 = OLD rank, +0x08 = NEW rank
///                        (Yukiko rank-up 5→6 live-verified 2026-07-07;
///                        "540_rank_up" and cmmRankUpSequence carry the same
///                        pair)
///   SCR_COMMU_&lt;NAME&gt;   — the running social-link event script; the suffix
///                        identifies the character (YUKIKO, KUMA, DOUJIMA…)
/// On cmmRankUp appearing we announce "Rank up! &lt;Arcana&gt;, &lt;Name&gt;, rank N."
/// once (re-armed when the task disappears). Non-interrupting so it queues
/// after any dialogue speech in progress.
/// </summary>
internal sealed unsafe class SocialLinkRankUp
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] RankUpTask = Encoding.ASCII.GetBytes("cmmRankUp");
    private static readonly byte[] CommuPrefix = Encoding.ASCII.GetBytes("SCR_COMMU_");
    // commu data table (stride 100): row +0x10 = arcana number (standard
    // tarot). Same table the bond-poem overlay uses (SocialLinkBond) — gives
    // a RELIABLE arcana for the banner regardless of the character-name path.
    private static readonly nint CommuTablePtrVA = unchecked((nint)0x1411A6B10L);
    // commu table row +0x10 = the game's arcana byte (1-based, with P4G gaps).
    // Verified from a full table dump 2026-07-07 (Jester=25, Aeon=27).
    internal static readonly Dictionary<int, string> ArcanaByByte = new()
    {
        [1] = "Fool", [2] = "Magician", [3] = "Priestess", [4] = "Empress",
        [5] = "Emperor", [6] = "Hierophant", [7] = "Lovers", [8] = "Chariot",
        [9] = "Justice", [10] = "Hermit", [11] = "Fortune", [12] = "Strength",
        [13] = "Hanged Man", [14] = "Death", [15] = "Temperance", [16] = "Devil",
        [17] = "Tower", [18] = "Star", [19] = "Moon", [20] = "Sun",
        [21] = "Judgement", [25] = "Jester", [27] = "Aeon",
    };

    // script-task suffix → (display name, arcana). Fixed game data (the
    // romaji suffixes come from the commu .bf procedure names).
    private static readonly Dictionary<string, (string Name, string Arcana)> Links = new()
    {
        ["YOUSUKE"] = ("Yosuke Hanamura", "Magician"),
        ["CHIE"] = ("Chie Satonaka", "Chariot"),
        ["YUKIKO"] = ("Yukiko Amagi", "Priestess"),
        ["KANJI"] = ("Kanji Tatsumi", "Emperor"),
        ["RISE"] = ("Rise Kujikawa", "Lovers"),
        ["NAOTO"] = ("Naoto Shirogane", "Fortune"),
        ["KUMA"] = ("Teddie", "Star"),
        ["DOUJIMA"] = ("Ryotaro Dojima", "Hierophant"),
        ["DOUJIMA_RSV"] = ("Ryotaro Dojima", "Hierophant"),
        ["NANAKO"] = ("Nanako Dojima", "Justice"),
        ["ADACHI"] = ("Tohru Adachi", "Jester"),
        ["MARIE"] = ("Marie", "Aeon"),
        ["KONISHI"] = ("Naoki Konishi", "Hanged Man"),
        ["ROUHUJIN"] = ("Hisano Kuroda", "Death"),
        ["MANAGER"] = ("Ai Ebihara", "Moon"),
        ["BASKET_ITIJO"] = ("Kou Ichijo", "Strength"),
        ["BASKET_NAGA"] = ("Daisuke Nagase", "Strength"),
        ["SOCCER_NAGA"] = ("Daisuke Nagase", "Strength"),
        ["ENGEKI"] = ("Yumi Ozawa", "Sun"),
        ["SUISOU"] = ("Ayane Matsunaga", "Sun"),
        ["KITUNE"] = ("The Fox", "Hermit"),
    };

    private bool _bannerWasUp;

    // community id (cmmRankUp work+0x04) → (name, arcana). Loaded from
    // database/social_links.json — extendable without a rebuild; the ids are
    // harvested from the [RankUp] log lines as links rank up during play.
    private readonly Dictionary<int, (string Name, string Arcana)> _commuIds = new();

    internal SocialLinkRankUp()
    {
        LoadIds();
        var t = new Thread(Poll) { IsBackground = true, Name = "SocialLinkRankUp" };
        t.Start();
        Log($"[RankUp] ready ({_commuIds.Count} commu ids mapped)");
    }

    private void LoadIds()
    {
        try
        {
            string path = DataPath("social_links.json");
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("commu_ids", out var ids)) return;
            foreach (var p in ids.EnumerateObject())
            {
                string? name = null, arcana = null;
                int i = 0;
                foreach (var x in p.Value.EnumerateArray())
                {
                    if (i == 0) name = x.GetString();
                    else if (i == 1) arcana = x.GetString();
                    i++;
                }
                if (name != null && arcana != null)
                    _commuIds[int.Parse(p.Name)] = (name, arcana);
            }
        }
        catch (Exception ex) { Log($"[RankUp] social_links.json load failed: {ex.Message}"); }
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(200);
            try
            {
                if (!GameHasFocus()) continue;
                Tick();
            }
            catch { }
        }
    }

    private void Tick()
    {
        nint task = FindTask(RankUpTask, exact: true, out _);
        if (task == 0) { _bannerWasUp = false; return; }
        if (_bannerWasUp) return;
        _bannerWasUp = true;

        // work+0x08 = the new rank. +0x04 is a per-character id but it does
        // NOT follow any derivable numbering (portrait-offset guess was wrong)
        // — used ONLY as a verified-id fallback below.
        int commuId = 0, rank = 0;
        if (IsReadable(task + 0x48, 8))
        {
            nint work = *(nint*)(task + 0x48);
            if (work != 0 && IsReadable(work, 0x10))
            {
                commuId = *(ushort*)(work + 0x04);
                rank = *(ushort*)(work + 0x08);
            }
        }

        // ARCANA — RELIABLE, from the game's commu table (work+0x04 = commu
        // index). This is the primary identity (it's printed on the banner and
        // is spoiler-safe). arcanaByte read + row logged for name-harvesting.
        int arcanaByte = ReadArcana(commuId, out byte[] row);
        ArcanaByByte.TryGetValue(arcanaByte, out string? arc);
        arc ??= "";

        // CHARACTER NAME — best effort, appended only when confidently known
        // (verified id table, or a named SCR_COMMU_ task). Never guessed, so a
        // link with no confident name simply reads arcana + rank.
        string name = "";
        if (_commuIds.TryGetValue(commuId, out var byId)) name = byId.Name;
        else
        {
            string picked = FindNamedCommu();
            if (picked.Length > 0 && Links.TryGetValue(picked, out var link)) name = link.Name;
        }

        var sb = new StringBuilder("Rank up! ");
        if (arc.Length > 0) sb.Append(arc).Append(" Arcana. ");
        if (name.Length > 0) sb.Append(name).Append(". ");
        if (rank is >= 1 and <= 10) sb.Append("Rank ").Append(rank).Append('.');
        string text = sb.ToString().Trim();
        Log($"[RankUp] id={commuId} rank={rank} arcanaByte={arcanaByte} row={BytesHex(row)} say: {text}");
        Speech.Say(text, false);   // queue after any dialogue being spoken
    }

    /// <summary>Arcana number for a commu index, via the game's commu table.
    /// Also returns the raw 100-byte row (logged, for name-harvesting).</summary>
    private static int ReadArcana(int commuIdx, out byte[] row)
    {
        row = System.Array.Empty<byte>();
        if (commuIdx < 0 || commuIdx > 255) return -1;
        if (!IsReadable(CommuTablePtrVA, 8)) return -1;
        nint table = *(nint*)CommuTablePtrVA;
        if (table == 0) return -1;
        nint rowp = table + commuIdx * 100;
        if (!IsReadable(rowp, 100)) return -1;
        row = new byte[100];
        for (int i = 0; i < 100; i++) row[i] = *(byte*)(rowp + i);
        return row[0x10];   // raw arcana byte (looked up in ArcanaByByte)
    }

    private static string BytesHex(byte[] b)
    {
        if (b.Length == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2"));
        return sb.ToString();
    }

    /// <summary>Is the exactly-named task present in the registry?</summary>
    private static nint FindTask(byte[] name, bool exact, out string fullName)
    {
        fullName = "";
        foreach (nint head in TaskHeads)
        {
            if (!IsReadable(head, 8)) continue;
            nint node = *(nint*)head;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!IsReadable(node, 0x58)) break;
                if (NameOf(node) is { } nm && MatchesBytes(node, name, exact))
                { fullName = nm; return node; }
                node = *(nint*)(node + 0x50);
            }
        }
        return 0;
    }

    /// <summary>
    /// Walk the registry for a SCR_COMMU_&lt;SUFFIX&gt; task whose suffix (after
    /// stripping a trailing _RANKn) is a KNOWN link — skips the generic
    /// SCR_COMMU_NPC_* scripts that run alongside a rank-up. Returns the
    /// matched Links key, or "" if none.
    /// </summary>
    private static string FindNamedCommu()
    {
        foreach (nint head in TaskHeads)
        {
            if (!IsReadable(head, 8)) continue;
            nint node = *(nint*)head;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!IsReadable(node, 0x58)) break;
                string nm = NameOf(node) ?? "";
                if (nm.Length > CommuPrefix.Length && MatchesBytes(node, CommuPrefix, false))
                {
                    string suffix = nm.Substring(CommuPrefix.Length);
                    int ri = suffix.IndexOf("_RANK", StringComparison.Ordinal);
                    if (ri > 0) suffix = suffix[..ri];
                    if (Links.ContainsKey(suffix)) return suffix;   // named link wins
                }
                node = *(nint*)(node + 0x50);
            }
        }
        return "";
    }

    private static bool MatchesBytes(nint node, byte[] name, bool exact)
    {
        for (int i = 0; i < name.Length; i++)
            if (*(byte*)(node + i) != name[i]) return false;
        return !exact || *(byte*)(node + name.Length) == 0;
    }

    private static string? NameOf(nint node)
    {
        var sb = new StringBuilder(0x18);
        for (int i = 0; i < 0x18; i++)
        {
            byte b = *(byte*)(node + i);
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
    {
        if (addr == 0) return false;
        ulong a = (ulong)addr;
        if (a < 0x10000UL || a > 0x00007FFFFFFFFFFFUL) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery(addr, buf, 48) == 0) return false;
        uint state = *(uint*)(buf + 32);
        uint protect = *(uint*)(buf + 36);
        if (state != 0x1000) return false;
        if ((protect & 0x01) != 0) return false;
        if ((protect & 0x100) != 0) return false;
        nint regionBase = *(nint*)(buf + 0);
        nint regionSize = *(nint*)(buf + 24);
        return a + (ulong)size <= (ulong)regionBase + (ulong)regionSize;
    }
}
