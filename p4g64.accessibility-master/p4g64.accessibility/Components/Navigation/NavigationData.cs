namespace p4g64.accessibility.Components.Navigation;

internal enum NavCategory { NPC, Exit, Place, Item }

/// <summary>
/// Position is in game-world units (same coordinate system as PlayerX/Z from FieldTracker).
/// Set null until the player visits the location and records coordinates with F9.
/// Once recorded, NavigationAssist can give distance and direction.
/// </summary>
internal record NavEntry(string Name, NavCategory Category, string? Hint = null,
    float? WorldX = null, float? WorldZ = null,
    float? TargetForwardX = null, float? TargetForwardZ = null,
    float? ApproachX = null, float? ApproachZ = null)
{
    internal bool HasPosition => WorldX.HasValue && WorldZ.HasValue;
    internal bool HasFacing   => TargetForwardX.HasValue && TargetForwardZ.HasValue;
    // Approach point: if set, auto-walk does a two-leg walk — first to the approach,
    // then bee-lines THROUGH the target, keeping keys held, letting CHECK latch as
    // the player crosses the trigger zone instead of hoping it latches at rest.
    internal bool HasApproach => ApproachX.HasValue && ApproachZ.HasValue;
}

/// <summary>
/// Static database of known entities per area (major, minor).
/// NPC = people you can talk to.
/// Exit = transitions to other areas.
/// Place = interactable locations (shops, Velvet Room, save points, signs, objects).
/// Item = item pickup spots on the ground.
/// </summary>
internal static class NavigationData
{
    private static readonly Dictionary<(int Major, int Minor), NavEntry[]> _db = new()
    {
        // ── Shopping District North (8,1) ────────────────────────────────────────
        // Confirmed area: the top half of the Shopping District.
        // Contains the Velvet Room limo, Aiya Chinese Diner, Shiroku Store,
        // and the road to school.
        [(8, 1)] = new[]
        {
            // People
            new NavEntry("Shiroku Store Lady",           NavCategory.NPC,   "runs the Shiroku Store"),
            new NavEntry("Aiya Staff",                   NavCategory.NPC,   "works at the Chinese diner"),
            new NavEntry("Townsperson by Notice Board",  NavCategory.NPC),

            // Key places
            new NavEntry("Velvet Room",                  NavCategory.Place, "Igor's blue limousine, manage your Personas"),
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("Notice Board",                 NavCategory.Place, "community announcements, check for quests"),
            new NavEntry("Aiya Chinese Diner",           NavCategory.Place, "eat here to raise multiple stats at once"),
            new NavEntry("Shiroku Store",                NavCategory.Place, "convenience store, sells healing items"),

            // Exits
            new NavEntry("Shopping District South",      NavCategory.Exit),
            new NavEntry("School Road",                  NavCategory.Exit,  "leads to Yasogami High School"),
        },

        // ── Shopping District South (8,2) ────────────────────────────────────────
        // The bottom half of the Shopping District.
        // Contains Daidara Metalworks, the pharmacy, bookstore, clothing store,
        // and Souzai Daigaku food stall.
        [(8, 2)] = new[]
        {
            // People
            new NavEntry("Pharmacy Owner",               NavCategory.NPC,   "woman who runs the pharmacy"),
            new NavEntry("Bookstore Clerk",              NavCategory.NPC,   "works at Yomenaido Bookstore"),
            new NavEntry("Townsperson",                  NavCategory.NPC,   "local resident you can talk to"),

            // Key places
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("Souzai Daigaku",               NavCategory.Place, "food stall, buy Chinese food for quests"),
            new NavEntry("Pharmacy",                     NavCategory.Place, "buy healing and support items"),
            new NavEntry("Yomenaido Bookstore",          NavCategory.Place, "buy books that permanently raise stats"),
            new NavEntry("Croco Fur Clothing",           NavCategory.Place, "buy accessories and outfits"),

            // Exits
            new NavEntry("Daidara Metalworks",           NavCategory.Exit,  "weapon and armor shop, upgrade equipment"),
            new NavEntry("Shopping District North",      NavCategory.Exit),
            new NavEntry("Samegawa Flood Plain Road",    NavCategory.Exit,  "path to the river"),
            new NavEntry("Junes Road",                   NavCategory.Exit,  "road to the right leads to Junes"),
        },

        // ── Souzai Daigaku interior (8,3) ────────────────────────────────────────
        [(8, 3)] = new[]
        {
            new NavEntry("Souzai Daigaku Owner",         NavCategory.NPC,   "sells Chinese food"),
            new NavEntry("Food Stall Counter",           NavCategory.Place, "order food here"),
            new NavEntry("Shopping District South",      NavCategory.Exit),
        },

        // ── Daidara Metalworks Outside (8,4) ─────────────────────────────────────
        [(8, 4)] = new[]
        {
            new NavEntry("Daidara Metalworks Door",      NavCategory.Exit,  "enter the weapon shop"),
            new NavEntry("Shopping District South",      NavCategory.Exit),
        },

        // ── Daidara Metalworks Inside (8,5) ──────────────────────────────────────
        [(8, 5)] = new[]
        {
            new NavEntry("Daidara",                      NavCategory.NPC,   "the blacksmith, buy and upgrade weapons"),
            new NavEntry("Weapon Display",               NavCategory.Place, "browse weapons"),
            new NavEntry("Armor Display",                NavCategory.Place, "browse armor and accessories"),
            new NavEntry("Exit to Shopping District",    NavCategory.Exit),
        },

        // ── Shopping District Side Alley (8,6) ───────────────────────────────────
        [(8, 6)] = new[]
        {
            new NavEntry("Shopping District South",      NavCategory.Exit),
        },

        // ── Junes Food Court (9,1) ───────────────────────────────────────────────
        [(9, 1)] = new[]
        {
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("Junes TV",                     NavCategory.Place, "the large department store TV, leads to the Midnight Channel"),
            new NavEntry("Electronics Department",       NavCategory.Exit),
            new NavEntry("Junes Entrance",               NavCategory.Exit),
        },

        // ── Junes Entrance (9,2) ─────────────────────────────────────────────────
        [(9, 2)] = new[]
        {
            new NavEntry("Junes Staff",                  NavCategory.NPC,   "store employee"),
            new NavEntry("Food Court",                   NavCategory.Exit),
            new NavEntry("Electronics Department",       NavCategory.Exit),
            new NavEntry("Junes West Side",              NavCategory.Exit),
            new NavEntry("Exit to Shopping District",    NavCategory.Exit),
        },

        // ── Junes Electronics Department (9,3) ───────────────────────────────────
        [(9, 3)] = new[]
        {
            new NavEntry("Junes Staff",                  NavCategory.NPC,   "store employee"),
            new NavEntry("Large TV",                     NavCategory.Place, "the TV that leads to the Midnight Channel"),
            new NavEntry("Electronics Counter",          NavCategory.Place),
            new NavEntry("Food Court",                   NavCategory.Exit),
            new NavEntry("Junes Entrance",               NavCategory.Exit),
        },

        // ── Junes West Side (9,4) ────────────────────────────────────────────────
        [(9, 4)] = new[]
        {
            new NavEntry("Junes Entrance",               NavCategory.Exit),
        },

        // ── Samegawa Flood Plain (10,1) ──────────────────────────────────────────
        [(10, 1)] = new[]
        {
            new NavEntry("Old Fisherman",                NavCategory.NPC,   "teaches you to fish"),
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("Fishing Spot",                 NavCategory.Place, "fish here with a fishing rod"),
            new NavEntry("Samegawa Riverbank",           NavCategory.Exit),
            new NavEntry("Shopping District South",      NavCategory.Exit),
        },

        // ── Samegawa Riverbank (10,2) ─────────────────────────────────────────────
        [(10, 2)] = new[]
        {
            new NavEntry("Flood Plain",                  NavCategory.Exit),
            new NavEntry("Path to Shrine",               NavCategory.Exit,  "leads to Tatsuhime Shrine"),
        },

        // ── Dojima Residence Front (7,1) ─────────────────────────────────────────
        [(7, 1)] = new[]
        {
            new NavEntry("Front Door",                   NavCategory.Exit,  "enter the house"),
            new NavEntry("Shopping District Road",       NavCategory.Exit),
        },

        // ── Dojima Residence Living Room (7,2) ───────────────────────────────────
        [(7, 2)] = new[]
        {
            new NavEntry("Nanako",                       NavCategory.NPC,   "your cousin, social link: Justice"),
            new NavEntry("Dojima",                       NavCategory.NPC,   "your uncle, social link: Hierophant"),
            new NavEntry("Living Room TV",               NavCategory.Place, "watch TV for news, weather, and special events"),
            new NavEntry("Dinner Table",                 NavCategory.Place),
            new NavEntry("Your Room",                    NavCategory.Exit),
            new NavEntry("Outside",                      NavCategory.Exit),
        },

        // ── Your Room (7,3) ──────────────────────────────────────────────────────
        [(7, 3)] = new[]
        {
            new NavEntry("Desk",                         NavCategory.Place, "study at night to raise Academics"),
            new NavEntry("Bookshelf",                    NavCategory.Place, "read books you have bought"),
            new NavEntry("Bed",                          NavCategory.Place, "sleep to end the day and advance to tomorrow"),
            new NavEntry("Dresser",                      NavCategory.Place, "change your outfit"),
            new NavEntry("Living Room",                  NavCategory.Exit),
        },

        // ── Dojima Residence Hallway (7,4) ───────────────────────────────────────
        [(7, 4)] = new[]
        {
            new NavEntry("Living Room",                  NavCategory.Exit),
            new NavEntry("Your Room",                    NavCategory.Exit),
            new NavEntry("Dojima's Room",                NavCategory.Exit),
        },

        // ── School Gate (6,15) ───────────────────────────────────────────────────
        // The gate outside the school — the transition point from the road.
        [(6, 15)] = new[]
        {
            new NavEntry("School Front Entrance",        NavCategory.Exit,  "enter the school building"),
            new NavEntry("Shopping District Road",       NavCategory.Exit,  "back toward the Shopping District"),
        },

        // ── School Front Entrance (6,1) ──────────────────────────────────────────
        [(6, 1)] = new[]
        {
            new NavEntry("Shoe Lockers",                 NavCategory.Place, "change into school shoes"),
            new NavEntry("School Gate",                  NavCategory.Exit,  "exit back to the road"),
            new NavEntry("First Floor Hallway",          NavCategory.Exit,  "into the main building"),
        },

        // ── School Hallway (6,3) ─────────────────────────────────────────────────
        [(6, 3)] = new[]
        {
            new NavEntry("Classroom 2-2",                NavCategory.Exit,  "your homeroom class"),
            new NavEntry("Front Entrance",               NavCategory.Exit,  "back to the entrance"),
            new NavEntry("Second Floor",                 NavCategory.Exit),
            new NavEntry("Courtyard",                    NavCategory.Exit),
            new NavEntry("Nurse's Office",               NavCategory.Exit),
        },

        // ── School Hallway Second Floor (6,4) ────────────────────────────────────
        [(6, 4)] = new[]
        {
            new NavEntry("First Floor Hallway",          NavCategory.Exit),
            new NavEntry("Library",                      NavCategory.Exit),
            new NavEntry("Rooftop",                      NavCategory.Exit),
        },

        // ── School Courtyard (6,5) ───────────────────────────────────────────────
        [(6, 5)] = new[]
        {
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("First Floor Hallway",          NavCategory.Exit),
            new NavEntry("Practice Building",            NavCategory.Exit),
            new NavEntry("Gym",                          NavCategory.Exit),
        },

        // ── Classroom 2-2 (6,6) ──────────────────────────────────────────────────
        [(6, 6)] = new[]
        {
            new NavEntry("Chie",                         NavCategory.NPC,   "your friend, social link: Chariot"),
            new NavEntry("Yukiko",                       NavCategory.NPC,   "your friend, social link: Priestess"),
            new NavEntry("Yosuke",                       NavCategory.NPC,   "your friend, social link: Magician"),
            new NavEntry("Your Desk",                    NavCategory.Place, "sit here during class"),
            new NavEntry("First Floor Hallway",          NavCategory.Exit),
        },

        // ── School Library (6,10) ────────────────────────────────────────────────
        [(6, 10)] = new[]
        {
            new NavEntry("Librarian",                    NavCategory.NPC,   "social link: Fortune"),
            new NavEntry("Book Stacks",                  NavCategory.Place, "study here to raise Knowledge"),
            new NavEntry("Second Floor Hallway",         NavCategory.Exit),
        },

        // ── School Gym (6,11) ────────────────────────────────────────────────────
        [(6, 11)] = new[]
        {
            new NavEntry("Coach Nagase",                 NavCategory.NPC,   "PE teacher"),
            new NavEntry("Practice Area",                NavCategory.Place, "practice here to raise Diligence"),
            new NavEntry("Courtyard",                    NavCategory.Exit),
        },

        // ── Home Economics Room (6,12) ───────────────────────────────────────────
        [(6, 12)] = new[]
        {
            new NavEntry("Cooking Station",              NavCategory.Place, "make lunch to raise Expression"),
            new NavEntry("First Floor Hallway",          NavCategory.Exit),
        },

        // ── School Rooftop (6,14) ────────────────────────────────────────────────
        [(6, 14)] = new[]
        {
            new NavEntry("Rooftop Bench",                NavCategory.Place, "hang out here"),
            new NavEntry("Second Floor Hallway",         NavCategory.Exit),
        },

        // ── Tatsuhime Shrine (17,0) ──────────────────────────────────────────────
        [(17, 0)] = new[]
        {
            new NavEntry("Shrine Keeper",                NavCategory.NPC,   "the old man who tends the shrine"),
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("Shrine Offering Box",          NavCategory.Place, "pray here to raise Diligence"),
            new NavEntry("Fortune Telling Box",          NavCategory.Place, "get a fortune told"),
            new NavEntry("Path to Samegawa",             NavCategory.Exit,  "back to the Samegawa Riverbank"),
        },

        // ── Hanamura Residence (15,0) ────────────────────────────────────────────
        [(15, 0)] = new[]
        {
            new NavEntry("Yosuke's House",               NavCategory.Place),
            new NavEntry("Back to Road",                 NavCategory.Exit),
        },

        // ── Okina City (14,0) ────────────────────────────────────────────────────
        [(14, 0)] = new[]
        {
            new NavEntry("Music Shop Staff",             NavCategory.NPC,   "sells music CDs"),
            new NavEntry("Save Point",                   NavCategory.Place, "save your game here"),
            new NavEntry("Junes Okina Branch",           NavCategory.Place, "department store"),
            new NavEntry("Movie Theater",                NavCategory.Place, "watch movies to raise Expression"),
            new NavEntry("Music Shop",                   NavCategory.Place, "buy music CDs"),
            new NavEntry("Cafe Chagall",                 NavCategory.Place, "cafe, study here to raise Academics"),
            new NavEntry("Train Station",                NavCategory.Exit,  "train back to Inaba"),
        },

        // ── Moel Gas Station (18,0) ──────────────────────────────────────────────
        [(18, 0)] = new[]
        {
            new NavEntry("Strange Man",                  NavCategory.NPC,   "mysterious person, appears during prologue"),
            new NavEntry("Gas Station",                  NavCategory.Place),
        },

        // ── Shiroku Store (12,0) ─────────────────────────────────────────────────
        [(12, 0)] = new[]
        {
            new NavEntry("Shiroku Store Lady",           NavCategory.NPC,   "sells items, special stock at night"),
            new NavEntry("Item Counter",                 NavCategory.Place, "buy healing and support items"),
            new NavEntry("Shopping District North",      NavCategory.Exit),
        },
    };

    /// <summary>
    /// Returns all entries for the given area.
    /// Overlay: any base entry whose name has a recorded position in NavigationPositionStore
    /// is returned with WorldX/WorldZ filled in.
    /// Custom: any PositionRecord whose name is NOT in the base list and has a non-null
    /// category is appended as a new NavEntry (for user-added notice boards, signs, etc).
    /// </summary>
    internal static NavEntry[] GetEntries(int major, int minor)
    {
        _db.TryGetValue((major, minor), out var baseList);
        baseList ??= Array.Empty<NavEntry>();

        // Start with base entries, overlay recorded positions (and facing, if any) by name
        var withOverlay = new NavEntry[baseList.Length];
        var baseNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < baseList.Length; i++)
        {
            var e = baseList[i];
            baseNames.Add(e.Name);
            if (NavigationPositionStore.TryGetPosition(major, minor, e.Name, out var x, out var z))
            {
                e = e with { WorldX = x, WorldZ = z };
                if (NavigationPositionStore.TryGetFacing(major, minor, e.Name, out var fx, out var fz))
                    e = e with { TargetForwardX = fx, TargetForwardZ = fz };
                if (NavigationPositionStore.TryGetApproach(major, minor, e.Name, out var ax, out var az))
                    e = e with { ApproachX = ax, ApproachZ = az };
            }
            withOverlay[i] = e;
        }

        // Append custom entries (records with a category and a name not in the base list)
        var records = NavigationPositionStore.GetRecordsFor(major, minor);
        var customList = new List<NavEntry>();
        foreach (var r in records)
        {
            if (r.category == null || baseNames.Contains(r.name)) continue;
            if (!TryParseCategory(r.category, out var cat)) continue;
            customList.Add(new NavEntry(r.name, cat, r.hint, r.x, r.z,
                                        TargetForwardX: r.fx, TargetForwardZ: r.fz,
                                        ApproachX: r.ax, ApproachZ: r.az));
        }

        if (customList.Count == 0) return withOverlay;
        var combined = new NavEntry[withOverlay.Length + customList.Count];
        Array.Copy(withOverlay, combined, withOverlay.Length);
        for (int i = 0; i < customList.Count; i++) combined[withOverlay.Length + i] = customList[i];
        return combined;
    }

    private static bool TryParseCategory(string s, out NavCategory cat)
    {
        switch (s)
        {
            case "NPC":   cat = NavCategory.NPC;   return true;
            case "Exit":  cat = NavCategory.Exit;  return true;
            case "Place": cat = NavCategory.Place; return true;
            case "Item":  cat = NavCategory.Item;  return true;
            default:      cat = NavCategory.Place; return false;
        }
    }

    internal static string CategorySpokenLabel(NavCategory cat, int count) => cat switch
    {
        NavCategory.NPC   => count == 1 ? "1 person"  : $"{count} people",
        NavCategory.Exit  => count == 1 ? "1 exit"    : $"{count} exits",
        NavCategory.Place => count == 1 ? "1 place"   : $"{count} places",
        NavCategory.Item  => count == 1 ? "1 item"    : $"{count} items",
        _                 => $"{count} {cat}",
    };

    internal static string CategorySingularLabel(NavCategory cat) => cat switch
    {
        NavCategory.NPC   => "Person",
        NavCategory.Exit  => "Exit",
        NavCategory.Place => "Place",
        NavCategory.Item  => "Item",
        _                 => cat.ToString(),
    };

    internal static string CategoryPluralLabel(NavCategory cat) => cat switch
    {
        NavCategory.NPC   => "NPCs",
        NavCategory.Exit  => "Exits",
        NavCategory.Place => "Places",
        NavCategory.Item  => "Items",
        _                 => cat.ToString(),
    };
}
