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
            if (run && _out == null)
            {
                _out = new WaveOutEvent { DesiredLatency = 120 };
                _out.Init(Mixer());
                _out.Play();
            }
            else if (!run && _out != null)
            {
                _out.Stop();
                _out.Dispose();
                _out = null;
            }
        }
    }
}
