using BF.File.Emulator.Interfaces;
using DavyKager;
using p4g64.accessibility.Components;
using p4g64.accessibility.Components.Battle;
using p4g64.accessibility.Components.Navigation;
using p4g64.accessibility.Components.CommandMenu;
using p4g64.accessibility.Configuration;
using p4g64.accessibility.Native;
using p4g64.accessibility.Native.Text;
using p4g64.accessibility.Template;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility;

/// <summary>
/// Your mod logic goes here.
/// </summary>
public class Mod : ModBase // <= Do not Remove.
{
    /// <summary>
    /// Provides access to the Reloaded.Hooks API.
    /// </summary>
    /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
    private readonly IReloadedHooks? _hooks;

    /// <summary>
    /// Provides access to the Reloaded logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// The configuration of the currently executing mod.
    /// </summary>
    private readonly IModConfig _modConfig;

    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private readonly IModLoader _modLoader;

    /// <summary>
    /// Entry point into the mod, instance that created this class.
    /// </summary>
    private readonly IMod _owner;

    private Battle _battle;
    private BattleLog _battleLog;
    private PartyStatus _partyStatus;
    private EnemyStatus _enemyStatus;
    private DamageMonitor _damageMonitor;
    private ProfileNav _profileNav;
    private PersonaNav _personaNav;
    private Components.CommandMenus.PlayerMenu _playerMenu;
    private Components.CommandMenus.QuestMenu _questMenu;
    private ShopMenu _shopMenu;
    private BookstoreMenu _bookstoreMenu;
    private TitleMenu _titleMenu;
    private InternetDialog _internetDialog;
    private SystemMessage _systemMessage;
    private TownMapReader _townMapReader;
    private CalendarReader _calendarReader;
    private BookMenu _bookMenu;
    private SkillReplaceMenu _skillReplaceMenu;
    private VelvetMenu _velvetMenu;
    private VelvetFusion _velvetFusion;
    private DifficultyMenu _difficultyMenu;
    private EarlyMenu _earlyMenu;
    private SubtitleReader _subtitleReader;
    private MovieDescription _movieDescription;
    private Tutorial _tutorial;
    private NameEntryKeyboard _nameEntryKeyboard;
    private FieldTracker _fieldTracker;
    private LoadScreenTracker _loadScreenTracker;
    private ConfigMenu _configMenu;
    private DaidaraCharSelect _daidaraCharSelect;
    private OverworldNav _overworldNav;
    private DungeonCursor? _dungeonCursor;
    private EnemyRadar? _enemyRadar;
    private ExitBeacon? _exitBeacon;
    private ChestBeacon? _chestBeacon;
    private DungeonNav? _dungeonNav;
    private PersonaReleaseMenu? _personaReleaseMenu;
    private ControllerInput? _controllerInput;
    private Components.HistoryKeys? _historyKeys;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    private Config _configuration;

    private Dialogue _dialogue;
    private TitleBar _titleBar;

    // BF (FlowScript bytecode) emulator from Sewer56/FileEmulationFramework.
    // Lets us ship .flow files that get compiled + injected at runtime so the
    // game's own FlowScript interpreter runs our code — the gateway to native
    // functions like CALL_FIELD for teleport-by-area-id.
    private IBfEmulator? _bfEmulator;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        Initialise(_logger, _configuration, _modLoader);

        // Catch managed exceptions that escape background threads (PollKeys, NavAssist,
        // DungeonStepNav, etc.) before the CLR terminates the process. AccessViolationException
        // cannot be caught on .NET 9 — those still kill the process silently; for everything
        // else this at least lands a stack trace in the Reloaded log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log($"[CRASH] UnhandledException (terminating={e.IsTerminating}): {ex?.GetType().Name}: {ex?.Message}");
            if (ex?.StackTrace != null) Log($"[CRASH] {ex.StackTrace}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"[CRASH] UnobservedTaskException: {e.Exception.GetType().Name}: {e.Exception.Message}");
            if (e.Exception.StackTrace != null) Log($"[CRASH] {e.Exception.StackTrace}");
            e.SetObserved();
        };
        Utils.ModDir = _modLoader.GetDirectoryForModId(_modConfig.ModId);
        AtlusEncoding.Initiailse(Utils.ModDir);
        Dialog.Initialise();
        Party.Initialise();
        PartyMember.Initialise(_hooks);
        Skill.Initialise();
        Item.Initialise();
        Persona.Initialise();
        var modDir = _modLoader.GetDirectoryForModId(_modConfig.ModId);

        // Add the mod's folder to the path so tolk will load screen reader dlls
        Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH") + ";" + modDir,
            EnvironmentVariableTarget.Process);

        Log("Loading tolk");
        Tolk.Load();

        if (!Tolk.IsLoaded())
        {
            LogError("Tolk failed to load, your mod files may be corrupted!");
            return;
        }

        Log($"Tolk loaded. IsLoaded={Tolk.IsLoaded()}, HasSpeech={Tolk.HasSpeech()}, ScreenReader={Tolk.DetectScreenReader() ?? "none"}");
        Speech.Say("Accessibility mod loaded", true);

        _dialogue = new Dialogue(_hooks!);
        _titleBar = new TitleBar(_hooks!);
        // PLAYER MENU phase (2026-06-12): PlayerMenu replaces CommandMenu +
        // PersonaMenu (retired, not constructed). Poll-based reader of the
        // camp menu master struct at *(0x140EC0A40) — see
        // memory/camp_menu_structure.md.
        _playerMenu = new Components.CommandMenus.PlayerMenu();
        _questMenu = new Components.CommandMenus.QuestMenu(_hooks!);
        _battle = new Battle(_hooks!);
        _battleLog = new BattleLog(_hooks!);
        _partyStatus = new PartyStatus();
        _enemyStatus = new EnemyStatus();
        _damageMonitor = new DamageMonitor();
        _profileNav = new ProfileNav();
        _personaNav = new PersonaNav();
        _titleMenu = new TitleMenu(_hooks!);
        _internetDialog = new InternetDialog(_hooks!);
        _systemMessage = new SystemMessage(_hooks!);
        _townMapReader = new TownMapReader(_hooks!);
        _calendarReader = new CalendarReader(_hooks!);
        _bookMenu = new BookMenu(_hooks!);
        _skillReplaceMenu = new SkillReplaceMenu(_hooks!);
        // Velvet Room — fusion facility menus (phase 1: top/root menu).
        // VelvetMenu (root-menu POLL reader) RETIRED 2026-06-16: VelvetFusion now hooks
        // the root-menu dispatcher FUN_14021E2A0 and reads it instantly (no ~100ms poll
        // lag). Construction disabled to stop the double/slow read. Kept for reference.
        // _velvetMenu = new VelvetMenu(_hooks!);
        // Velvet Room — fusion SUB-screens (the cmb facility). Diagnostic-first
        // build: hooks the dispatcher FUN_140265060, proves the detour installs +
        // fires inside fusion (resolves the prior "hook wall"), maps each screen
        // to its obj+0xF0 panel flag, and best-effort speaks the persona-pick
        // list. See memory/velvet_room_fusion_re.md.
        _velvetFusion = new VelvetFusion(_hooks!);
        _difficultyMenu = new DifficultyMenu(_hooks!);
        _earlyMenu = new EarlyMenu(_hooks!);
        _subtitleReader = new SubtitleReader(_hooks!);
        _movieDescription = new MovieDescription();
        _tutorial = new Tutorial();
        _nameEntryKeyboard = new NameEntryKeyboard(_hooks!);
        _fieldTracker = new FieldTracker(_hooks!);
        _loadScreenTracker = new LoadScreenTracker(_hooks!);
        _configMenu = new ConfigMenu(_hooks!);
        _shopMenu = new ShopMenu(_hooks!);
        _bookstoreMenu = new BookstoreMenu(_hooks!);
        _daidaraCharSelect = new DaidaraCharSelect(_hooks!);

        // Overworld navigation browser (OVERWORLD phase, 2026-06-12).
        // -/= category (Places · Exits · Other), [ ] entries nearest→far,
        // \ distance brief, Backspace auto-walk, P accelerating beacon.
        // Data: database/overworld_catalog.json (asset-derived; see
        // database/OVERWORLD.md). RETIRED: NavigationAssist (manual
        // record/calibrate teleport) — class kept for the BIT-teleport
        // reference but no longer constructed.
        _overworldNav = new OverworldNav();

        // Grid cursor: H toggles a virtual cursor at the player; I/K/J/L move it
        // tile-by-tile, announcing wall / door / chest / shadow / floor and
        // stopping at walls. The unified blind-mapping tool.
        _dungeonCursor = new DungeonCursor();

        // Dungeon audio layer (off by default; shares one DungeonAudio output):
        //   . (period) = Shadow radar — positional tones (volume=closeness,
        //                pan=L/R, growl=facing you).
        //   / (slash)  = exit/stairs beacon toward the next-floor stairs.
        //   , (comma)  = chest beacon toward the nearest chest.
        _enemyRadar = new EnemyRadar();
        _exitBeacon = new ExitBeacon();
        _chestBeacon = new ChestBeacon();
        // DoorBeacon (the ';' door-sound beacon) removed 2026-06-23 (user request).

        // Unified dungeon browser + auto-walk. -/= cycle category (Doors ·
        // Chests · Shadows · Exits), [ ] step entries nearest→far, \ briefs /
        // teleports exits (Shift+\), Backspace auto-walks to the selection
        // (shadow hunt for Shadows). Add new floors in DungeonNav._floors AND
        // in FEmulator/BF/field.flow.
        _dungeonNav = new DungeonNav();

        // "Personas full — release one" menu reader (Shuffle Time persona card /
        // fusion when stock is full). Signature-scans for the menu; see
        // memory/persona_release_menu.md.
        _personaReleaseMenu = new PersonaReleaseMenu();

        // Controller support for the mod hotkeys (bug-fix session 2026-06-19).
        // Hold both triggers (L2+R2) for the "mod layer", then buttons/d-pad fire
        // the existing keyboard shortcuts (synthesized as key presses). Focus-gated.
        // Requires Steam Input disabled for P4G. See ControllerInput.cs.
        _controllerInput = new ControllerInput(_hooks!);
        _historyKeys = new Components.HistoryKeys();   // Shift+P/[/]/M speech-history + dialogue toggle

        // FlowScript bridge. Used by the navigation teleport (see NavigationAssist
        // "Teleport trigger"). Requires BOTH:
        //   1. BF Emulator mod ('reloaded.universal.fileemulationframework.bf')
        //   2. customSubMenu mod ('p4g64.customSubMenu') — it ships the field.bf
        //      replacement that hosts our softhook. Without it, our .flow file
        //      becomes the baseFlow and AtlusScriptCompiler's ImportedOnly hook
        //      mode won't match our softhook. See memory/navigation_working.md.
        var bfController = _modLoader.GetController<IBfEmulator>();
        if (bfController != null && bfController.TryGetTarget(out _bfEmulator))
        {
            Log("[BF] IBfEmulator controller acquired — FlowScript path available.");

            // Diagnostic: list imports for field.flow so we can see at startup
            // whether our FEmulator/BF/field.flow was auto-scanned AND customSubMenu
            // is contributing its own base file. Two entries = good to go.
            try
            {
                if (_bfEmulator.TryGetImports("field.flow", out var imports))
                {
                    Log($"[BF] field.flow imports: {imports.Length} entries");
                    foreach (var imp in imports) Log($"[BF]   - {imp}");
                    if (imports.Length < 2)
                        LogError("[BF] Only one field.flow provider detected — teleport requires customSubMenu installed as a baseFlow source. Install 'p4g64.customSubMenu' alongside this mod.");
                }
            }
            catch (Exception ex)
            {
                LogError($"[BF] Import check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            LogError("[BF] Failed to acquire IBfEmulator controller. Install 'BF Emulator' mod (dependency) to enable FlowScript features.");
        }
    }

    #region For Exports, Serialization etc.

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod()
    {
    }
#pragma warning restore CS8618

    #endregion

    #region Standard Overrides

    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }

    #endregion
}