using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// The single shared audio output for every dungeon sound feature (Shadow radar,
/// exit beacon, …). The retired wall audio proved that opening MULTIPLE
/// WaveOut/Wasapi outputs in the game's process breaks the game's own sound — so
/// everything feeds ONE <see cref="MixingSampleProvider"/> driven by ONE
/// <see cref="WaveOutEvent"/> here.
///
/// Each feature adds its persistent inputs once via <see cref="AddInput"/> (at
/// construction, before any output exists — so no input is added to a live
/// mixer, which NAudio mixing isn't thread-safe for) and calls
/// <see cref="SetWant"/> to declare whether it currently needs audio. The shared
/// output runs only while at least one feature wants it, so nothing is allocated
/// on the main menu / overworld.
/// </summary>
internal static class DungeonAudio
{
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    /// <summary>The measured head for 3D sound (hrtf\ku100.p5rh, ported from P5R Access 2026-10-18), loaded at startup by
    /// <see cref="LoadHrtf"/> BEFORE any voice is built (each <see cref="BeaconVoice"/> makes its own
    /// <see cref="Spatializer"/> from it); null = the file is missing: pan only.</summary>
    internal static HrtfSet? Hrtf;

    /// <summary>The settings menu's "3D sound (HRTF)" (General; saved, default <c>Defaults.Hrtf3D</c> = off).</summary>
    internal const string Hrtf3DKey = "hrtf_3d";

    internal static void LoadHrtf()
    {
        string path = Utils.DataPath(System.IO.Path.Combine("hrtf", "ku100.p5rh"));
        Hrtf = HrtfSet.Load(path);
        // a head measured at another rate would place every sound wrong (its delays and filters are in samples): pan only
        if (Hrtf != null && Hrtf.SampleRate != Format.SampleRate)
        {
            Utils.Log($"[3D] head NOT used ({path}): measured at {Hrtf.SampleRate} Hz, the mixer runs at {Format.SampleRate} Hz — panning only");
            Hrtf = null;
            return;
        }
        Utils.Log(Hrtf != null ? $"[3D] head loaded: {path} ({Hrtf.Taps} taps)" : $"[3D] head NOT loaded ({path}): 3D sound unavailable, panning only");
    }

    private static readonly object _lock = new();
    private static MixingSampleProvider? _mixer;
    private static WaveOutEvent? _out;
    private static readonly HashSet<object> _wanters = new();

    private static MixingSampleProvider Mixer()
    {
        _mixer ??= new MixingSampleProvider(Format) { ReadFully = true };
        return _mixer;
    }

    /// <summary>Add a persistent input. Call once at startup, before any SetWant.</summary>
    public static void AddInput(ISampleProvider provider)
    {
        lock (_lock) { Mixer().AddMixerInput(provider); }
    }

    /// <summary>Declare whether <paramref name="who"/> currently needs audio.</summary>
    public static void SetWant(object who, bool want)
    {
        lock (_lock)
        {
            bool changed = want ? _wanters.Add(who) : _wanters.Remove(who);
            if (!changed) return;
            bool run = _wanters.Count > 0;
            if (run && _out == null) Open();
            else if (!run && _out != null) Close();
        }
    }

    // Short-delay mode (2026-10-02, fishing): the reel gauge needs the ear to follow a needle that
    // crosses its target zone in ~0.2 s, so while a feature asks for it the output is re-opened
    // with ~60 ms of buffering instead of 160. Everything else keeps the stutter-safe default.
    private static readonly HashSet<object> _lowLatency = new();

    /// <summary>Ask for (or release) the short-delay output while <paramref name="who"/> needs it.</summary>
    public static void SetLowLatency(object who, bool on)
    {
        lock (_lock)
        {
            bool changed = on ? _lowLatency.Add(who) : _lowLatency.Remove(who);
            if (!changed || _out == null) return;
            Close();
            Open();
        }
    }

    private static void Open()
    {
        // Default: 4 × 40 ms buffers instead of NAudio's 2 × 60 ms — when one buffer finishes, 120 ms stay
        // queued, so a busy game frame can't starve the continuous wall/door loops into an audible gap
        // (2026-09-29 stutter fix).
        bool low = _lowLatency.Count > 0;
        _out = new WaveOutEvent { DesiredLatency = low ? 60 : 160, NumberOfBuffers = low ? 3 : 4 };
        _out.Init(Mixer());
        _out.Play();
    }

    private static void Close()
    {
        _out!.Stop();
        _out.Dispose();
        _out = null;
    }
}
