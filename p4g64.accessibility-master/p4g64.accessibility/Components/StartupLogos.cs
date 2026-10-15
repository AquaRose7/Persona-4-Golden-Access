using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Describes the two logos the game shows at startup (Haru 2026-10-15): ATLUS, then P-Studio.
/// Both are drawn by ONE task, "logoDrawProc" (~5.5 s, with "logoTMX"; then "movie play" starts the
/// opening). Its work has no logo-index field; work+0x08 (float) is a frame timer that counts up to
/// ~70.5 on the ATLUS logo and RESETS once when the P-Studio logo comes up (live-sampled 2026-10-15).
/// So: the task appearing = ATLUS, the timer dropping = P-Studio. The thread ends once the logos are
/// over (or 3 minutes after launch) — nothing runs afterwards.
/// </summary>
internal sealed unsafe class StartupLogos
{
    private static readonly byte[] LogoTaskName = Encoding.ASCII.GetBytes("logoDrawProc");

    internal const string AtlusText =
        "ATLUS logo: the word ATLUS in bold blue capitals edged in white, its T in red, on black.";
    internal const string PStudioText =
        "P-Studio logo: a black zigzag like a lightning bolt on a tall white card, above the words " +
        "P-Studio, Creative department 2nd Production.";

    internal StartupLogos()
    {
        new Thread(Loop) { IsBackground = true, Name = "StartupLogos" }.Start();
    }

    private static void Loop()
    {
        long giveUp = Environment.TickCount64 + 180_000;
        bool seen = false, second = false;
        float prev = 0;
        while (Environment.TickCount64 < giveUp)
        {
            Thread.Sleep(50);
            try
            {
                nint task = TvListingsReader.FindTaskByName(LogoTaskName);
                if (task == 0)
                {
                    if (seen) break;            // logos over
                    continue;
                }
                if (!ProbeReadable(task + 0x48, 8)) continue;
                nint work = *(nint*)(task + 0x48);
                if (work == 0 || !ProbeReadable(work + 0x08, 4)) continue;
                float t = *(float*)(work + 0x08);
                if (!seen)
                {
                    seen = true;
                    prev = t;
                    Log("[Logos] startup logos: ATLUS");
                    Speech.Say(AtlusText, false);
                    continue;
                }
                if (!second && t < prev - 20f)
                {
                    second = true;
                    Log("[Logos] startup logos: P-Studio");
                    Speech.Say(PStudioText, false);
                }
                prev = t;
            }
            catch (Exception ex) { Log($"[Logos] {ex.Message}"); }
        }
    }
}
