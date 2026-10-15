using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the "work of fiction" disclaimer shown just before a new game starts (Haru 2026-10-15). It is a
/// PICTURE (no text in the message files or the exe), drawn by the new-game task "mode_init" in its state
/// 12, between "Well, relax and have fun with the game." (state 11) and the fade-out (19) — states
/// live-logged 2026-10-15 on a New Game Plus. Spoken once per showing; English (the picture has no
/// readable text to localize from).
/// </summary>
internal sealed unsafe class NewGameDisclaimer
{
    private static readonly byte[] ModeInitTask = Encoding.ASCII.GetBytes("mode_init");

    internal const string Text =
        "This game is a work of fiction. No persons, groups, incidents, or terms appearing in this work " +
        "are intended to represent any entities in reality.";

    internal NewGameDisclaimer()
    {
        new Thread(Loop) { IsBackground = true, Name = "NewGameDisclaimer" }.Start();
    }

    private static void Loop()
    {
        bool spoken = false;
        while (true)
        {
            Thread.Sleep(150);
            try
            {
                nint task = TvListingsReader.FindTaskByName(ModeInitTask);
                nint work;
                int state;
                if (task == 0 || !TryReadRaw(task + 0x48, &work, 8) || work == 0 || !TryReadRaw(work, &state, 4))
                {
                    spoken = false;
                    continue;
                }
                if (state == 12 && !spoken)
                {
                    spoken = true;
                    Log("[Disclaimer] new-game disclaimer");
                    Speech.Say(Text, true);
                }
                else if (state < 12) spoken = false;
            }
            catch (Exception ex) { Log($"[Disclaimer] {ex.Message}"); }
        }
    }
}
