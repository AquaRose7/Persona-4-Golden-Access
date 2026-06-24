using System.Linq;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// Translates the overworld catalog's internal/romaji place labels into readable English
/// (2026-06-20). Many catalog "name" fields are the game's raw proc identifiers — romaji school
/// rooms (<c>jyosi_toire</c>, <c>tosyositu</c>), classroom codes (<c>iti_iti</c> = class 1-1),
/// dungeon triggers (<c>upstair_2f_passage</c>), etc. <see cref="Translate"/> maps the known ones
/// and prettifies the rest (underscores → spaces, title-cased) so nothing reads as raw romaji.
/// Genuinely-English labels (have a space/capital, no underscore) pass through untouched.
/// </summary>
internal static class PlaceNames
{
    private static readonly Dictionary<string, string> Map = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // ── Yasogami High: special rooms (oku = back, temae = front) ──
        ["bijyutu_oku"] = "Art Room, back",          ["bijyutu_temae"] = "Art Room, front",
        ["eigo_oku"] = "English Room, back",         ["eigo_temae"] = "English Room, front",
        ["gijyutu_oku"] = "Crafts Room, back",       ["gijyutu_temae"] = "Crafts Room, front",
        ["hihuku_oku"] = "Sewing Room, back",        ["hihuku_temae"] = "Sewing Room, front",
        ["housou_oku"] = "Broadcasting Room, back",  ["housou_temae"] = "Broadcasting Room, front",
        ["kagaku_oku"] = "Chemistry Room, back",     ["kagaku_temae"] = "Chemistry Room, front",
        ["konpyu_oku"] = "Computer Room, back",      ["konpyu_temae"] = "Computer Room, front",
        ["seibutu_oku"] = "Biology Room, back",      ["seibutu_temae"] = "Biology Room, front",
        ["syakai_oku"] = "Social Studies Room, back",["syakai_temae"] = "Social Studies Room, front",
        ["syodou_oku"] = "Calligraphy Room, back",   ["syodou_temae"] = "Calligraphy Room, front",
        ["sityoukaku_oku"] = "AV Room, back",        ["sityoukaku_temae"] = "AV Room, front",
        ["tyouri_oku"] = "Cooking Room, back",       ["tyouri_temae"] = "Cooking Room, front",
        ["seitokai_oku"] = "Student Council Room, back", ["seitokai_temae"] = "Student Council Room, front",
        ["seitosidou_oku"] = "Guidance Office, back",["seitosidou_temae"] = "Guidance Office, front",
        ["tosyositu_oku"] = "Library, back",         ["tosyositu_temae"] = "Library, front",
        ["syokuin_l_side"] = "Faculty Office, left", ["syokuin_r_side"] = "Faculty Office, right",
        ["jyosi_toire"] = "Girls' Restroom",
        ["dansi_toire_6_1"] = "Boys' Restroom", ["dansi_toire_6_2"] = "Boys' Restroom",
        ["dansi_toire_6_3"] = "Boys' Restroom", ["dansi_toire_6_4"] = "Boys' Restroom",
        ["dansi_toire_6_5"] = "Boys' Restroom",
        ["getabako"] = "Shoe Lockers", ["hoken"] = "Nurse's Office", ["keiji"] = "Bulletin Board",
        ["kaigisitu"] = "Meeting Room", ["undoubu"] = "Sports Clubs", ["enngeki"] = "Drama Club",
        ["ongaku_mes"] = "Music Room", ["ongakujyunbi"] = "Music Prep Room",

        // ── Yasogami classrooms (iti=1, ni=2, san=3 → grade-class) ──
        ["iti_iti_oku"] = "Class 1-1, back", ["iti_iti_temae"] = "Class 1-1, front",
        ["iti_ni_oku"] = "Class 1-2, back",  ["iti_ni_temae"] = "Class 1-2, front",
        ["iti_san_oku"] = "Class 1-3, back", ["iti_san_temae"] = "Class 1-3, front",
        ["ni_iti_oku"] = "Class 2-1, back",  ["ni_iti_temae"] = "Class 2-1, front",
        ["ni_san_oku"] = "Class 2-3, back",  ["ni_san_temae"] = "Class 2-3, front",
        ["san_iti_oku"] = "Class 3-1, back", ["san_iti_temae"] = "Class 3-1, front",
        ["san_ni_oku"] = "Class 3-2, back",  ["san_ni_temae"] = "Class 3-2, front",
        ["san_san_oku"] = "Class 3-3, back", ["san_san_temae"] = "Class 3-3, front",
        ["into_classroom_left"] = "Into classroom, left", ["into_classroom_right"] = "Into classroom, right",
        ["out_classroom_over"] = "Out of classroom, upper", ["out_classroom_under"] = "Out of classroom, lower",

        // ── Dojima residence ──
        ["check_bedclothes"] = "Examine futon", ["check_bike"] = "Examine bike",
        ["check_desk"] = "Examine desk", ["check_diary"] = "Examine diary",
        ["check_farmfield"] = "Examine field", ["check_farmtools"] = "Examine farm tools",
        ["check_fishingtools"] = "Examine fishing gear", ["check_kitchen"] = "Examine kitchen",
        ["check_livingroom_tv"] = "Examine living room TV", ["check_mini_desk"] = "Examine small desk",
        ["check_myroom_tv"] = "Examine my room TV", ["check_reizou"] = "Examine fridge",
        ["check_roomrack"] = "Examine shelf", ["check_sofa_p4p"] = "Examine sofa",
        ["check_trophy"] = "Examine trophy",
        ["myhouse_entrance"] = "Home entrance", ["upstair_myroom"] = "Stairs to my room",
        ["door_living_room"] = "Living room door", ["door_entrance"] = "Entrance door",

        // ── Okina City ──
        ["city_board"] = "Okina notice board", ["city_bookstore"] = "Okina bookstore",
        ["city_cafe"] = "Okina cafe", ["city_clothshop"] = "Okina clothing store",
        ["city_police"] = "Okina police box", ["city_station"] = "Okina Station",
        ["city_theater"] = "Okina theater", ["go_foodcourt"] = "To food court",
        ["into_shop"] = "Enter shop", ["out_shop"] = "Exit shop",

        // ── Outdoors ──
        ["down_embankment"] = "Down to riverbank", ["up_embankment"] = "Up from riverbank",
        ["farmshop"] = "Farm shop", ["sigemi"] = "Bushes", ["sigemi2"] = "Bushes",
        ["tanabata"] = "Tanabata display", ["zabuton"] = "Cushion", ["ufo_catcher"] = "Crane game",

        // ── Dungeons: stairs / passages / doors / warps ──
        ["bossroom_door"] = "Boss room door", ["bossroom_downstair"] = "Boss room, stairs down",
        ["bossroom_return"] = "Boss room exit", ["door_bossfloor"] = "Boss floor door",
        ["upstair_1f_entrance"] = "Stairs up, 1F entrance", ["upstair_1f_passage"] = "Stairs up, 1F passage",
        ["upstair_2f_entrance"] = "Stairs up, 2F entrance", ["upstair_2f_passage"] = "Stairs up, 2F passage",
        ["upstair_2f_b_l"] = "Stairs up, 2F left", ["upstair_2f_b_r"] = "Stairs up, 2F right",
        ["upstair_3f_4f"] = "Stairs up, 3F to 4F",
        ["downstair_1f_b_l"] = "Stairs down, 1F left", ["downstair_1f_b_r"] = "Stairs down, 1F right",
        ["downstair_2f_entrance"] = "Stairs down, 2F entrance", ["downstair_2f_passage"] = "Stairs down, 2F passage",
        ["downstair_3f_entrance"] = "Stairs down, 3F entrance", ["downstair_3f_passage"] = "Stairs down, 3F passage",
        ["downstair_4f_3f"] = "Stairs down, 4F to 3F", ["downstair_bossfloor"] = "Stairs down, boss floor",
        ["passage_1f"] = "Passage, 1F", ["passage_1f_b"] = "Passage, 1F",
        ["passage_2f"] = "Passage, 2F", ["passage_2f_b"] = "Passage, 2F",
        ["return_entrance"] = "Entrance", ["return_point"] = "Return point", ["falling_hole"] = "Pit",
        ["dressroom_door_1"] = "Dressing room door", ["dressroom_go"] = "Enter dressing room",
        ["dressroom_return"] = "Exit dressing room", ["entrance_lodge"] = "Lodge entrance",

        // ── Dungeon entrances / items / objects ──
        ["enter_base"] = "Enter base", ["enter_castle"] = "Enter castle", ["enter_gameworld"] = "Enter game world",
        ["enter_heaven"] = "Enter Heaven", ["enter_last"] = "Enter final area", ["enter_overthecity"] = "Enter over the city",
        ["enter_playhouse"] = "Enter playhouse", ["enter_sauna"] = "Enter sauna", ["enter_tomb"] = "Enter tomb",
        ["base_item"] = "Treasure", ["castle_item"] = "Treasure", ["heaven_item"] = "Treasure",
        ["liquor_item"] = "Treasure", ["playhouse_item"] = "Treasure", ["sauna_item"] = "Treasure",
        ["quest_item"] = "Quest item",
        ["bone_object"] = "Bones", ["boot_margaret"] = "Margaret", ["temptation_1"] = "Temptation",
        ["temptation_2"] = "Temptation", ["musi_ap_shrine"] = "Shrine",
    };

    public static string Translate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
        if (Map.TryGetValue(raw, out var v)) return v;
        // Already English (a space or a capital, and not an underscore identifier) → leave it.
        if (!raw.Contains('_') && (raw.Contains(' ') || raw.Any(char.IsUpper))) return raw;
        return Pretty(raw);   // unmapped internal id → at least make it readable
    }

    private static string Pretty(string s)
    {
        var parts = s.Replace('_', ' ').Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }
}
