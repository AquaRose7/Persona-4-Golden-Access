using System.Runtime.InteropServices;
using p4g64.accessibility.Components.Navigation;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// The in-game ACCESSIBILITY SETTINGS MENU (2026-07-19 design, spec in
/// docs/superpowers/specs). F1 or LT+RT+Start toggles it anywhere the game has
/// focus. Up/Down rows (wrap), Left/Right adjust, Enter enters a category /
/// confirms Restore defaults, Escape backs out / closes. Every row speaks
/// name, value, description, position; every change saves to mod_settings.json
/// instantly and previews by ear. While open, ControllerInput masks the menu
/// keys from the game (keyboard DInput + pad bitfields) and the other mod
/// hotkey handlers early-return on IsOpen. Auto-closes when a battle starts.
/// </summary>
internal sealed class SettingsMenu
{
    private const int PollMs = 50;
    private const int VK_F1 = 0x70, VK_UP = 0x26, VK_DOWN = 0x28, VK_LEFT = 0x25,
                      VK_RIGHT = 0x27, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;

    internal static volatile bool IsOpen;
    internal static volatile bool OpenRequest;   // LT+RT+Start (ControllerInput) — toggle
    /// <summary>True while the suppression hooks must keep masking the menu keys:
    /// the whole time the menu is open PLUS until every menu key (and menu pad
    /// button) is released after it closes — else the tail of the closing Escape/B
    /// press leaks to the game and closes ITS menu too (user report 2026-07-19).</summary>
    internal static volatile bool CaptureKeys;

    private enum Kind { Percent, Hertz, Toggle, Choice, Action }

    private sealed class Row
    {
        public string Name = "", Desc = "", Key = "";
        public Kind Kind;
        public int Def, Min, Max, Step;
        public string[]? Options;                 // Toggle/Choice labels, index = value
        public Func<int> Get = () => 0;
        public Action<int> Set = _ => { };        // apply live + persist
        public Action<int>? Preview;              // play the sound at value (volumes/pitches)
    }

    private sealed class Category { public string Name = ""; public string Desc = ""; public Row[] Rows = Array.Empty<Row>(); }

    private readonly Category[] _cats;
    private readonly Row _restoreRow;
    private int _catIndex, _rowIndex;
    private bool _inCategory;
    private long _confirmUntil;                   // restore-defaults double-Enter window

    // ── HELP section (content = help_content.json, editable without a rebuild) ──
    private sealed class HelpItem { public string Name { get; set; } = ""; public string Text { get; set; } = ""; }
    private sealed class HelpNode
    {
        public string Name { get; set; } = "";
        public string Desc { get; set; } = "";
        public HelpNode[] Children { get; set; } = Array.Empty<HelpNode>();
        public HelpItem[] Items { get; set; } = Array.Empty<HelpItem>();
        public int Count => Children.Length > 0 ? Children.Length : Items.Length;
    }
    private sealed class HelpFile { public HelpNode[] Help { get; set; } = Array.Empty<HelpNode>(); }
    private HelpNode? _helpRoot;
    private readonly List<(HelpNode Node, int Idx)> _helpStack = new();   // menu-thread only

    private readonly bool[] _was = new bool[7];   // F1,Up,Down,Left,Right,Enter,Escape edges
    private static readonly int[] SlotVks = { VK_F1, VK_UP, VK_DOWN, VK_LEFT, VK_RIGHT, VK_RETURN, VK_ESCAPE };

    // Preview WAVs (loaded once; empty array = preview silently skipped).
    private readonly float[] _wavWall, _wavDoor, _wavStairs, _wavChest, _wavChoice;

    public SettingsMenu()
    {
        ToneCue.TryLoadWav("wallNorth.wav", out _wavWall);
        ToneCue.TryLoadWav("wallDoor.wav", out _wavDoor);
        ToneCue.TryLoadWav("stares.wav", out _wavStairs);
        ToneCue.TryLoadWav("chest.wav", out _wavChest);
        ToneCue.TryLoadWav("choice.wav", out _wavChoice);
        _cats = BuildCategories();
        _restoreRow = new Row { Name = "Restore defaults", Kind = Kind.Action,
            Desc = "Resets every setting in this menu to its default value." };
        LoadHelp();
        new Thread(Poll) { IsBackground = true, Name = "SettingsMenu" }.Start();
        Log("[SettingsMenu] ready (F1 / LT+RT+Start)");
    }

    // ── the settings tree ────────────────────────────────────────────────────

    private static Row Vol(string name, string key, string desc, int def, Func<float> get, Action<float> set, Action<int>? preview) => new()
    {
        Name = name, Key = key, Desc = desc, Kind = Kind.Percent,
        Def = def, Min = 0, Max = 200, Step = 10,
        Get = () => (int)MathF.Round(get() * 100f),
        Set = v => { set(v / 100f); ModSettings.SetInt(key, v); },
        Preview = preview,
    };

    private Category[] BuildCategories() => new[]
    {
        new Category
        {
            Name = "Sound volumes",
            Desc = "A volume for every mod sound, and the shadow radar pitches.",
            Rows = new[]
            {
                Vol("Wall hum volume", "vol_wall_hum",
                    "The four directional wall sounds while walking a dungeon.", Defaults.WallHumVol,
                    () => SoundSettings.WallHumVol, v => SoundSettings.WallHumVol = v,
                    v => ToneCue.PlayWav(_wavWall, 0.55f * v / 100f, 1000)),
                Vol("Door cue volume", "vol_door",
                    "The distinct door sound inside the wall hum.", Defaults.DoorVol,
                    () => SoundSettings.DoorVol, v => SoundSettings.DoorVol = v,
                    v => ToneCue.PlayWav(_wavDoor, 0.55f * v / 100f, 1000)),
                Vol("Stairs beacon volume", "vol_stairs_beacon",
                    "The looping beacon toward the stairs.", Defaults.StairsVol,
                    () => SoundSettings.StairsVol, v => SoundSettings.StairsVol = v,
                    v => ToneCue.PlayWav(_wavStairs, 0.6f * v / 100f, 1200)),
                Vol("Chest beacon volume", "vol_chest_beacon",
                    "The looping beacon toward the nearest chest.", Defaults.ChestVol,
                    () => SoundSettings.ChestVol, v => SoundSettings.ChestVol = v,
                    v => ToneCue.PlayWav(_wavChest, 0.6f * v / 100f, 1200)),
                Vol("Navigation beacon volume", "vol_nav_beacon",
                    "The ping toward your selected target, in dungeons and town.", Defaults.NavVol,
                    () => SoundSettings.NavVol, v => SoundSettings.NavVol = v,
                    v => ToneCue.PlayTones(0.85f * v / 100f, (760f, 60), (0f, 150), (760f, 60))),
                Vol("Shadow radar volume", "vol_shadow_radar",
                    "The shadow tones and danger cues while the radar is on.", Defaults.RadarVol,
                    () => SoundSettings.RadarVol, v => SoundSettings.RadarVol = v,
                    v => ToneCue.PlayTones(0.10f * v / 100f, (SoundSettings.ShadowFreqAway, 700))),
                Vol("Choice sound volume", "vol_choice",
                    "The chime when a dialogue choice appears.", Defaults.ChoiceVol,
                    () => SoundSettings.ChoiceVol, v => SoundSettings.ChoiceVol = v,
                    v => ToneCue.PlayWav(_wavChoice, 0.9f * v / 100f)),
                Vol("Cursor beeps volume", "vol_cursor_beeps",
                    "The short ticks from the mapping cursor and the navigation browsers.", Defaults.CursorBeepVol,
                    () => SoundSettings.CursorBeepVol, v => SoundSettings.CursorBeepVol = v,
                    v => ToneCue.PlayTones(0.45f * v / 100f, (1250f, 16), (0f, 60), (980f, 16))),
                Vol("Arrival chime volume", "vol_arrival_chime",
                    "The rising three-note chime when auto-walk arrives.", Defaults.ChimeVol,
                    () => SoundSettings.ChimeVol, v => SoundSettings.ChimeVol = v,
                    v => ToneCue.PlayTones(0.5f * v / 100f, (900f, 60), (1200f, 60), (1500f, 90))),
                Vol("Wall bump volume", "vol_wall_bump",
                    "The thunk when you push into a wall.", Defaults.BumpVol,
                    () => SoundSettings.BumpVol, v => SoundSettings.BumpVol = v,
                    v => ToneCue.PlayTones(0.5f * v / 100f, (440f, 55), (300f, 85))),
                new Row
                {
                    Name = "Shadow pitch, facing you", Key = "shadow_freq_facing_you",
                    Desc = "Pitch of the growl when a shadow is looking at you. "
                         + "Keep it clearly apart from the facing-away tone — that contrast is what makes a shadow's attention audible.",
                    Kind = Kind.Hertz, Def = Defaults.ShadowFreqYou, Min = 60, Max = 600, Step = 10,
                    Get = () => (int)MathF.Round(SoundSettings.ShadowFreqYou),
                    Set = v => { SoundSettings.ShadowFreqYou = v; ModSettings.SetInt("shadow_freq_facing_you", v); },
                    Preview = v => ToneCue.PlayTones(0.10f * SoundSettings.RadarVol, (v, 700)),
                },
                new Row
                {
                    Name = "Shadow pitch, facing away", Key = "shadow_freq_facing_away",
                    Desc = "Pitch of the soft steady tone when a shadow has not seen you.",
                    Kind = Kind.Hertz, Def = Defaults.ShadowFreqAway, Min = 60, Max = 600, Step = 10,
                    Get = () => (int)MathF.Round(SoundSettings.ShadowFreqAway),
                    Set = v => { SoundSettings.ShadowFreqAway = v; ModSettings.SetInt("shadow_freq_facing_away", v); },
                    Preview = v => ToneCue.PlayTones(0.10f * SoundSettings.RadarVol, (v, 700)),
                },
            },
        },
        new Category
        {
            Name = "Cursor",
            Desc = "How the H mapping cursor starts.",
            Rows = new[]
            {
                new Row
                {
                    Name = "Cursor default mode", Key = "cursor_default_mode",
                    Desc = "What the mapping cursor starts in each time you open it. Walk steps the player; Look surveys without moving.",
                    Kind = Kind.Choice, Options = new[] { "Walk", "Look" }, Def = Defaults.CursorMode, Min = 0, Max = 1, Step = 1,
                    Get = () => ModSettings.GetInt("cursor_default_mode", Defaults.CursorMode),
                    Set = v => ModSettings.SetInt("cursor_default_mode", v),
                },
                new Row
                {
                    Name = "Cursor default directions", Key = "cursor_default_frame",
                    Desc = "The direction frame at game start. Compass is fixed north south west east; Camera is ahead behind left right.",
                    Kind = Kind.Choice, Options = new[] { "Compass", "Camera" }, Def = Defaults.CursorFrame, Min = 0, Max = 1, Step = 1,
                    Get = () => ModSettings.GetInt("cursor_default_frame", Defaults.CursorFrame),
                    Set = v => ModSettings.SetInt("cursor_default_frame", v),
                },
            },
        },
        new Category
        {
            Name = "Readers",
            Desc = "The spoken reader toggles. Their shortcuts keep working and stay in sync.",
            Rows = new[]
            {
                new Row
                {
                    Name = "Dialogue reader", Key = "dialogue_reader",
                    Desc = "Automatic reading of dialogue text.",
                    Kind = Kind.Toggle, Options = new[] { "Off", "On" }, Def = Defaults.DialogueReader ? 1 : 0, Min = 0, Max = 1, Step = 1,
                    Get = () => Dialogue.ReaderEnabled ? 1 : 0,
                    Set = v => { Dialogue.ReaderEnabled = v == 1; ModSettings.SetBool("dialogue_reader", v == 1); },
                },
                new Row
                {
                    Name = "Cutscene subtitles", Key = "subtitle_reader",
                    Desc = "Reading of anime cutscene subtitles. Note: subtitles must also be turned on in the game's own settings for this to read.",
                    Kind = Kind.Toggle, Options = new[] { "Off", "On" }, Def = Defaults.SubtitleReader ? 1 : 0, Min = 0, Max = 1, Step = 1,
                    Get = () => SubtitleReader.ReaderEnabled ? 1 : 0,
                    Set = v => { SubtitleReader.ReaderEnabled = v == 1; ModSettings.SetBool("subtitle_reader", v == 1); },
                },
                new Row
                {
                    Name = "Cutscene descriptions", Key = "movie_descriptions",
                    Desc = "Hand written descriptions of cutscene visuals.",
                    Kind = Kind.Toggle, Options = new[] { "Off", "On" }, Def = Defaults.MovieDescriptions ? 1 : 0, Min = 0, Max = 1, Step = 1,
                    Get = () => MovieDescription.Enabled ? 1 : 0,
                    Set = v => { MovieDescription.Enabled = v == 1; ModSettings.SetBool("movie_descriptions", v == 1); },
                },
            },
        },
    };

    private void LoadHelp()
    {
        try
        {
            string path = DataPath("help_content.json");
            if (!System.IO.File.Exists(path)) { Log("[SettingsMenu] help_content.json not found — Help section empty"); return; }
            var hf = System.Text.Json.JsonSerializer.Deserialize<HelpFile>(
                System.IO.File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (hf != null && hf.Help.Length > 0)
                _helpRoot = new HelpNode { Name = "Help", Desc = "Controls and game tips.", Children = hf.Help };
            Log($"[SettingsMenu] help content: {(_helpRoot != null ? $"{_helpRoot.Children.Length} section(s)" : "empty")}");
        }
        catch (Exception ex) { Log($"[SettingsMenu] help load error: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── poll loop ────────────────────────────────────────────────────────────

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(PollMs);
            try { Tick(); }
            catch (Exception ex) { IsOpen = false; Log($"[SettingsMenu] poll error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) return;

        // Masking linger: after a close, keep the game blind to the menu keys until
        // they're ALL released (keyboard here; pad via ControllerInput.MenuPadHeld).
        if (!IsOpen && CaptureKeys)
        {
            bool anyDown = ControllerInput.MenuPadHeld;
            for (int i = 1; !anyDown && i < SlotVks.Length; i++)
                anyDown = (GetAsyncKeyState(SlotVks[i]) & 0x8000) != 0;
            if (!anyDown) CaptureKeys = false;
        }

        if (IsOpen && FieldTracker.InBattle) { Close(silent: true, "battle"); return; }   // battle takes over

        bool f1 = Edge(0, VK_F1);
        bool fromReq = false;
        if (OpenRequest) { OpenRequest = false; fromReq = true; }
        if (f1 || fromReq)
        {
            if (IsOpen) Close(silent: false, $"toggle f1={f1} pad={fromReq}");
            else Open();
            return;
        }
        if (!IsOpen)
        {
            for (int i = 1; i < _was.Length; i++) _was[i] = false;   // stale edges
            return;
        }

        if (Edge(1, VK_UP)) Move(-1);
        if (Edge(2, VK_DOWN)) Move(+1);
        if (Edge(3, VK_LEFT)) Adjust(-1);
        if (Edge(4, VK_RIGHT)) Adjust(+1);
        if (Edge(5, VK_RETURN)) EnterPress();
        if (Edge(6, VK_ESCAPE)) EscapePress();
    }

    private bool Edge(int slot, int vk)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool fired = down && !_was[slot];
        _was[slot] = down;
        return fired;
    }

    // ── navigation ───────────────────────────────────────────────────────────

    private int TopCount => _cats.Length + 2;   // + Help + Restore defaults
    private int HelpIndex => _cats.Length;
    private int RestoreIndex => _cats.Length + 1;

    private void Open()
    {
        IsOpen = true; CaptureKeys = true;
        _inCategory = false; _catIndex = 0; _confirmUntil = 0;
        _helpStack.Clear();
        // Seed the edge trackers with the CURRENT key states so a key already held
        // (or stuck down by a stray injected event) can't fire on the first tick —
        // it must be released and pressed again to count.
        for (int i = 1; i < _was.Length; i++)
            _was[i] = (GetAsyncKeyState(SlotVks[i]) & 0x8000) != 0;
        Speech.Say($"Accessibility settings. {TopEntryText()}", true);
        Log("[SettingsMenu] OPEN");
    }

    private void Close(bool silent, string reason)
    {
        IsOpen = false; _confirmUntil = 0;
        _helpStack.Clear(); _inCategory = false;
        if (!silent) Speech.Say("Settings closed.", true);
        Log($"[SettingsMenu] CLOSED ({reason})");
    }

    private void Move(int dir)
    {
        _confirmUntil = 0;
        if (_helpStack.Count > 0)
        {
            var (node, idx) = _helpStack[^1];
            int n = node.Count;
            if (n == 0) return;
            idx = ((idx + dir) % n + n) % n;
            _helpStack[^1] = (node, idx);
            Speech.Say(HelpEntryText(node, idx), true);
            return;
        }
        if (!_inCategory)
        {
            _catIndex = ((_catIndex + dir) % TopCount + TopCount) % TopCount;
            Speech.Say(TopEntryText(), true);
        }
        else
        {
            var rows = _cats[_catIndex].Rows;
            _rowIndex = ((_rowIndex + dir) % rows.Length + rows.Length) % rows.Length;
            Speech.Say(RowFocusText(rows[_rowIndex], _rowIndex, rows.Length), true);
        }
    }

    private void Adjust(int dir)
    {
        _confirmUntil = 0;
        if (_helpStack.Count > 0) return;          // help entries have no value
        if (!_inCategory) return;                  // top level: Enter opens, arrows move
        var row = _cats[_catIndex].Rows[_rowIndex];
        if (row.Kind == Kind.Action) return;
        int v = row.Get();
        v = row.Kind is Kind.Toggle or Kind.Choice
            ? ((v + dir) % (row.Max + 1) + row.Max + 1) % (row.Max + 1)
            : Math.Clamp(v + dir * row.Step, row.Min, row.Max);
        row.Set(v);
        Speech.Say($"{row.Name}, {ValueText(row, v)}", true);
        row.Preview?.Invoke(v);
    }

    private void EnterPress()
    {
        if (_helpStack.Count > 0)
        {
            var (node, idx) = _helpStack[^1];
            if (node.Count == 0) return;
            if (node.Children.Length > 0)
            {
                var child = node.Children[idx];
                if (child.Count == 0) { Speech.Say("Empty.", true); return; }
                _helpStack.Add((child, 0));
                Speech.Say($"{child.Name}. {HelpEntryText(child, 0)}", true);
            }
            else Speech.Say(HelpEntryText(node, idx), true);   // re-read the tip
            return;
        }
        if (!_inCategory)
        {
            if (_catIndex == HelpIndex)
            {
                if (_helpRoot == null || _helpRoot.Count == 0) { Speech.Say("Help content is missing.", true); return; }
                _confirmUntil = 0;
                _helpStack.Add((_helpRoot, 0));
                Speech.Say($"Help. {HelpEntryText(_helpRoot, 0)}", true);
                return;
            }
            if (_catIndex == RestoreIndex)   // Restore defaults action row
            {
                long now = Environment.TickCount64;
                if (now < _confirmUntil)
                {
                    _confirmUntil = 0;
                    foreach (var cat in _cats)
                        foreach (var row in cat.Rows)
                            if (row.Kind != Kind.Action) row.Set(row.Def);
                    Speech.Say("All settings restored to defaults.", true);
                    Log("[SettingsMenu] defaults restored");
                }
                else
                {
                    _confirmUntil = now + 5000;
                    Speech.Say("Press Enter again to restore all settings to defaults.", true);
                }
                return;
            }
            _confirmUntil = 0;
            _inCategory = true; _rowIndex = 0;
            var rows = _cats[_catIndex].Rows;
            Speech.Say($"{_cats[_catIndex].Name}. {RowFocusText(rows[0], 0, rows.Length)}", true);
        }
        else _confirmUntil = 0;   // Enter on a row: nothing (left/right adjusts)
    }

    private void EscapePress()
    {
        _confirmUntil = 0;
        if (_helpStack.Count > 0)
        {
            _helpStack.RemoveAt(_helpStack.Count - 1);
            if (_helpStack.Count > 0)
            {
                var (node, idx) = _helpStack[^1];
                Speech.Say(HelpEntryText(node, idx), true);
            }
            else Speech.Say(TopEntryText(), true);
            return;
        }
        if (_inCategory)
        {
            _inCategory = false;
            Speech.Say(TopEntryText(), true);
        }
        else Close(silent: false, "escape");
    }

    // ── speech text ──────────────────────────────────────────────────────────

    private string TopEntryText()
    {
        if (_catIndex == HelpIndex)
            return $"Help. Controls and game tips. Item {_catIndex + 1} of {TopCount}.";
        if (_catIndex == RestoreIndex)
            return $"Restore defaults. {_restoreRow.Desc} Item {TopCount} of {TopCount}.";
        var c = _cats[_catIndex];
        return $"{c.Name} category. {c.Desc} Item {_catIndex + 1} of {TopCount}.";
    }

    private static string HelpEntryText(HelpNode node, int idx)
    {
        if (node.Children.Length > 0)
        {
            var c = node.Children[idx];
            return $"{c.Name}. {c.Desc} Item {idx + 1} of {node.Children.Length}.";
        }
        var it = node.Items[idx];
        return $"{it.Name}. {it.Text} Item {idx + 1} of {node.Items.Length}.";
    }

    private static string RowFocusText(Row row, int index, int count)
        => $"{row.Name}, {ValueText(row, row.Get())}. {row.Desc} Row {index + 1} of {count}.";

    private static string ValueText(Row row, int v) => row.Kind switch
    {
        Kind.Percent => $"{v} percent",
        Kind.Hertz => $"{v} hertz",
        _ => row.Options != null && v >= 0 && v < row.Options.Length ? row.Options[v] : v.ToString(),
    };

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
