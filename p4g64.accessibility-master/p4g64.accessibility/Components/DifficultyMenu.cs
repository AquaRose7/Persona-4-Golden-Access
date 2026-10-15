using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the difficulty selection menu render function to speak whichever option the cursor is on.
///
/// Cursor is a short at offset +0x04:
///   0 = Very Easy   1 = Easy   2 = Normal   3 = Hard   4 = Very Hard
///
/// </summary>
internal unsafe class DifficultyMenu : IDisposable
{
    private static readonly string[] Options =
    {
        "Very Easy", "Easy", "Normal", "Hard", "Very Hard"
    };

    // The on-screen description for each difficulty (transcribed from the game —
    // the text is baked into an encoded blob, not extractable as plain text).
    private static readonly string[] Descriptions =
    {
        "This mode is easier than easy. Relax and enjoy the story. If your party is wiped in battle, you can instantly retry.",
        "For those who are less than confident in their skills. The battles are easier than Normal mode, so don't get too worked up.",
        "This is the best balance of difficulty and enjoyment. Please experience the tension.",
        "This is for the confident. Battles are more challenging, and you'll experience severe gameplay.",
        "Do you hate yourself, or are you supremely self-confident? This difficulty requires a strength of heart that never gives in.",
    };

    private IHook<RenderDelegate>? _hook;
    private short _lastCursor = -1;

    [StructLayout(LayoutKind.Explicit)]
    private struct DifficultyMenuStruct
    {
        [FieldOffset(0x00)] public int State;    // 6 = the difficulty list is up (= task mode_init's work)
        [FieldOffset(0x04)] public short Cursor; // 0–4
    }

    private delegate void RenderDelegate(DifficultyMenuStruct* pMenu, nint param2, nint param3, nint param4);

    internal DifficultyMenu(IReloadedHooks hooks)
    {
        SigScan(
            "40 55 53 41 56 48 8D AC 24 F0 FE FF FF 48 81 EC 10 02 00 00",
            "DifficultyMenu::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Difficulty menu render hook active.");
            });

    }

    private void OnRender(DifficultyMenuStruct* pMenu, nint param2, nint param3, nint param4)
    {
        _hook!.OriginalFunction(pMenu, param2, param3, param4);

        if (pMenu == null) return;
        // The list also RENDERS behind the earlier boxes (state 13/14 = the New Game Plus clear-data
        // questions) — speak only while it is really up (state 6, live-read 2026-10-15).
        if (pMenu->State != 6) return;
        var cursor = pMenu->Cursor;
        if (cursor < 0 || cursor > 4) return;
        if (cursor == _lastCursor) return;

        // The first render announces too (2026-10-15: the highlighted NORMAL was never read on open),
        // queued so the game's "choose one that you will be comfortable with" line finishes first.
        var wasFirstRender = _lastCursor < 0;
        _lastCursor = cursor;
        EnsureGameTexts();
        string name = _names != null && cursor < _names.Length && _names[cursor].Length > 0 ? _names[cursor] : Options[cursor];
        string desc = _descs != null && _descs[cursor].Length > 0 ? _descs[cursor] : Descriptions[cursor];
        LogDebug($"Difficulty menu: cursor={cursor} -> {name}");
        Speech.Say($"{name}. {desc}", !wasFirstRender);
    }

    // ── The game's own texts, in the game's language (2026-10-15) ──
    // The task mode_init (this screen) points at its two LOADED message files: work+0x100 =
    // mode_init.bmd (selection MODE_SEL = the five names) and work+0x110 = mode_init_help.bmd
    // (SAFETY/EASY/NORMAL/HARD/RISKY_HELP = the descriptions). Read once per opening; the English
    // literals above stay as the fallback.
    private static readonly byte[] ModeInitTask = System.Text.Encoding.ASCII.GetBytes("mode_init");
    private static readonly string[] HelpNames = { "SAFETY_HELP", "EASY_HELP", "NORMAL_HELP", "HARD_HELP", "RISKY_HELP" };
    private string[]? _names, _descs;

    private void EnsureGameTexts()
    {
        if (_names != null && _descs != null) return;
        try
        {
            nint task = TvListingsReader.FindTaskByName(ModeInitTask);
            nint work;
            if (task == 0 || !TryReadRaw(task + 0x48, &work, 8) || work == 0) return;
            foreach (int off in new[] { 0x100, 0x108, 0x110 })
            {
                nint bmd;
                if (!TryReadRaw(work + off, &bmd, 8)) continue;
                var img = Native.Text.BmdImage.Copy(bmd);
                if (img == null) continue;
                if (_names == null && Native.Text.BmdImage.Options(img, "MODE_SEL") is { Length: 5 } opts)
                    _names = opts.Select(Tidy).ToArray();
                if (_descs == null)
                {
                    var d = HelpNames.Select(n => Native.Text.BmdImage.Message(img, n) ?? "").ToArray();
                    if (d.All(x => x.Length > 0)) _descs = d;
                }
            }
            Log($"[Difficulty] game texts: names={(_names != null ? string.Join(" / ", _names) : "none")} descriptions={(_descs != null ? "yes" : "none")}");
        }
        catch (Exception ex) { Log($"[Difficulty] {ex.Message}"); }
    }

    // "ＶＥＲＹ ＥＡＳＹ" (full-width capitals) → "Very Easy"; other scripts unchanged.
    private static string Tidy(string s)
    {
        s = s.Normalize(System.Text.NormalizationForm.FormKC).Trim();
        if (s.Length > 0 && s.Any(char.IsLetter) && s.Where(char.IsLetter).All(c => c < 0x250 && char.IsUpper(c)))
            s = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
        return s;
    }

    public void Dispose() { }
}
