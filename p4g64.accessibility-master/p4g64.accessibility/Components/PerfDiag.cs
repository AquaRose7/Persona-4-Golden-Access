using System.Diagnostics;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// TEMPORARY perf instrumentation for the MENU-HEAVINESS bug (2026-07-27). Every
/// suspect hook wraps its body in Begin/End; a background reporter logs one compact
/// [Perf] line every 3s whenever any bucket did real work — so a single user
/// reproduction pinpoints exactly which hook eats the frame. Near-zero overhead
/// (Stopwatch.GetTimestamp + interlocked adds). STRIP once the bug is closed.
/// </summary>
internal static class PerfDiag
{
    internal enum B
    {
        SkillRepRow,       // SkillReplaceMenu row-drawer hook (fires in battle persona grid too)
        SkillRepText,      // SkillReplaceMenu 450C60 capture (gated on RecentlyActive)
        EnsureNamesLoop,   // full 1024-skill name-map rebuild attempts (should be ~1 EVER)
        ShuffleTextCap,    // ShuffleText 450C60 capture
        ConfigValCap,      // ConfigValueText 450C60 capture (ungated by design)
        QuestCap,          // QuestMenu 450C60 capture
        SLinkCap,          // SocialLinkDetail 450C60 capture
        CompendiumCap,     // CompendiumInfoText 450C60 capture
        GameOverCap,       // GameOverReader 450C60 capture
        TvCap,             // TvListingsReader 450C60 capture
        VelvetDispatch,    // VelvetFusion dispatch hook (whole reader body)
        SpeechSay,         // Speech.Say total (Tolk IPC included)
        Count,
    }

    private static readonly long[] _ticks = new long[(int)B.Count];
    private static readonly int[] _calls = new int[(int)B.Count];
    private static int _started;

    // RELEASE: the whole harness compiles away (v2.0 packaging) — Begin returns 0 and
    // End does nothing, so the per-hook instrumentation costs literally nothing in a
    // shipped build. Debug/dev builds keep the full [Perf] reporter.
    internal static long Begin()
    {
#if DEBUG
        return Stopwatch.GetTimestamp();
#else
        return 0;
#endif
    }

    internal static void End(B b, long t0)
    {
#if DEBUG
        Interlocked.Add(ref _ticks[(int)b], Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref _calls[(int)b]);
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            new Thread(Report) { IsBackground = true, Name = "PerfDiag" }.Start();
#endif
    }

    internal static void Bump(B b)   // count-only bucket (no timing)
    {
#if DEBUG
        Interlocked.Increment(ref _calls[(int)b]);
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            new Thread(Report) { IsBackground = true, Name = "PerfDiag" }.Start();
#endif
    }

    private static void Report()
    {
        var names = Enum.GetNames(typeof(B));
        while (true)
        {
            Thread.Sleep(3000);
            try
            {
                double toMs = 1000.0 / Stopwatch.Frequency;
                var sb = new System.Text.StringBuilder();
                double worst = 0;
                for (int i = 0; i < (int)B.Count; i++)
                {
                    long t = Interlocked.Exchange(ref _ticks[i], 0);
                    int n = Interlocked.Exchange(ref _calls[i], 0);
                    if (n == 0) continue;
                    double ms = t * toMs;
                    if (ms > worst) worst = ms;
                    sb.Append($" {names[i]} n={n} t={ms:F1}ms |");
                }
                // Log only when something did non-trivial work in the window — keeps
                // idle play silent but catches every heavy screen (>=2ms/3s).
                if (worst >= 2.0 && sb.Length > 0)
                    Log($"[Perf] 3s window (major={FieldTracker.CurrentMajor}):{sb}");
            }
            catch { }
        }
    }
}
