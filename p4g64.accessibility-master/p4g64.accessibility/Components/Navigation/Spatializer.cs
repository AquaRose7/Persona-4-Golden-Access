using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// One sound through the head (<see cref="HrtfSet"/>; ported 2026-10-18 from P5R Access): fed one mono sample at a time on
/// the audio thread, it gives the two ears. Pure and allocation-free after construction (P5R's modtests check the same
/// code offline).
///   AIM      glides to the asked direction once per <see cref="Block"/> samples, the short way round, at most
///            <see cref="MaxStepDeg"/> a block (~2,200 degrees a second: a jump is a quick glide, never a click).
///   FILTERS  re-blended from the grid each block; 128 taps per ear, one SIMD dot product per ear per sample over a
///            double-written history ring.
///   DELAY    the far ear's output is delayed by the inter-aural delay, a fractional delay that glides sample by sample.
///   MUFFLE   Haru's pick (2026-10-17, "let's use the muffled version"): a tiny one-pole low-pass on the input,
///            a = <see cref="MuffleFloor"/> behind .. 1 in front (front / back blended across the sides).
///   SIDES    P4G only (Haru 2026-10-18, "when it becomes on my left or right it feels like center"): the far ear is
///            lowered by up to <see cref="SideBoostDb"/> more, by how far to the side the sound is (|sin azimuth|). P4G's
///            sounds are low pure tones (walls 145–890 Hz, the P beacon 760 Hz), where the head alone gives only 3–6 dB
///            between the ears at 90 degrees (≈30 dB above 6 kHz, measured on the KU100 set) — the pan mode gives the far
///            ear nothing. Glided across each block like the delay, so it never clicks.
/// </summary>
internal sealed class Spatializer
{
    internal const int Block = 32;
    internal const float MuffleFloor = 0.45f;
    internal const float SideBoostDb = 15f;
    private const float MaxStepDeg = 1.6f;
    private const int DelayRing = 64, RingMask = DelayRing - 1;   // the head's delay is at most ~35 samples at 44.1 kHz

    private readonly HrtfSet _set;
    private readonly int _taps;
    private readonly bool _muffle;
    private readonly float[] _hl, _hr, _hist, _dl = new float[DelayRing], _dr = new float[DelayRing];
    private int _pos, _dpos, _inBlock = Block, _quiet;
    private float _az, _el, _tgtAz, _tgtEl, _itd, _itdFrom, _a = 1f, _lp;
    private float _gl = 1f, _gr = 1f, _glFrom = 1f, _grFrom = 1f;   // SIDES: the far ear's extra lowering
    private bool _aimed;

    internal Spatializer(HrtfSet set, bool muffle = true)
    {
        _set = set; _taps = set.Taps; _muffle = muffle;
        _hl = new float[_taps]; _hr = new float[_taps]; _hist = new float[2 * _taps];
    }

    /// <summary>Where the sound is: azimuth CLOCKWISE from straight ahead, elevation up, degrees. The first aim is taken at
    /// once; later ones are glided to.</summary>
    internal void Aim(float azimuthDeg, float elevationDeg)
    {
        if (!float.IsFinite(azimuthDeg) || !float.IsFinite(elevationDeg)) return;   // keep the last good direction (the lookup must never see NaN)
        _tgtAz = azimuthDeg; _tgtEl = elevationDeg;
        if (!_aimed) { _az = azimuthDeg; _el = elevationDeg; _aimed = true; _inBlock = Block; Refilter(first: true); _inBlock = 0; }
    }

    /// <summary>Silence everything kept between samples (a voice that starts over).</summary>
    internal void Reset()
    {
        Array.Clear(_hist); Array.Clear(_dl); Array.Clear(_dr);
        _lp = 0; _pos = _dpos = 0; _quiet = _taps + DelayRing;
    }

    // The audio thread's hot path: compiled fully optimised from the first call (tiered compilation would run the slow first
    // version for a while — the 16-voice cost test measured 132 ms a second of audio before this).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Process(float mono, out float left, out float right)
    {
        // SILENT: once a voice has fed zeros for a whole filter plus the delay ring, every sample it keeps is zero — skip the
        // work (a silent wall or door voice costs nothing). Coming back, it starts at its direction instead of gliding there.
        if (mono == 0f && _lp == 0f)
        {
            if (_quiet >= _taps + DelayRing) { left = right = 0f; return; }
            _quiet++;
        }
        else if (_quiet >= _taps + DelayRing) { _quiet = 0; _az = _tgtAz; _el = _tgtEl; Refilter(first: true); _inBlock = 0; }
        else _quiet = 0;

        if (_inBlock >= Block) { Step(); _inBlock = 0; }
        float t = (_inBlock + 1) / (float)Block;
        _inBlock++;

        if (_muffle)
        {
            _lp += _a * (mono - _lp);
            if (MathF.Abs(_lp) < 1e-15f) _lp = 0f;          // a fading tail must not sink into denormals (100x slower)
            mono = _lp;
        }

        if (--_pos < 0) _pos = _taps - 1;                  // newest sample first: window[k] = x[n - k]
        _hist[_pos] = mono; _hist[_pos + _taps] = mono;
        var window = new ReadOnlySpan<float>(_hist, _pos, _taps);
        float yl = Dot(_hl, window), yr = Dot(_hr, window);

        _dpos = (_dpos + 1) & RingMask;
        _dl[_dpos] = yl; _dr[_dpos] = yr;
        float itd = _itdFrom + (_itd - _itdFrom) * t;      // + = the right ear hears it later
        left = Delayed(_dl, itd < 0 ? -itd : 0f) * (_glFrom + (_gl - _glFrom) * t);
        right = Delayed(_dr, itd > 0 ? itd : 0f) * (_grFrom + (_gr - _grFrom) * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Delayed(float[] ring, float d)
    {
        int i = (int)d; float f = d - i;
        float a = ring[(_dpos - i) & RingMask], b = ring[(_dpos - i - 1) & RingMask];
        return a + (b - a) * f;
    }

    private void Step()
    {
        float daz = ((_tgtAz - _az) % 360f + 540f) % 360f - 180f;
        _az = ((_az + Math.Clamp(daz, -MaxStepDeg, MaxStepDeg)) % 360f + 360f) % 360f;
        _el += Math.Clamp(_tgtEl - _el, -MaxStepDeg, MaxStepDeg);
        Refilter(first: false);
    }

    private void Refilter(bool first)
    {
        _itdFrom = first ? float.NaN : _itd;
        _set.Filter(_az, _el, _hl, _hr, out _itd);
        if (first) _itdFrom = _itd;
        float front = Math.Clamp((MathF.Cos(_az * MathF.PI / 180f) + 0.15f) / 0.3f, 0f, 1f);
        _a = MuffleFloor + (1f - MuffleFloor) * front;

        // SIDES: azimuth 0..180 = the right side (clockwise), so the LEFT ear is the far one there
        _glFrom = _gl; _grFrom = _gr;
        float side = MathF.Sin(_az * MathF.PI / 180f);
        float far = MathF.Pow(10f, -SideBoostDb * MathF.Abs(side) / 20f);
        _gl = side > 0f ? far : 1f;
        _gr = side < 0f ? far : 1f;
        if (first) { _glFrom = _gl; _grFrom = _gr; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(float[] h, ReadOnlySpan<float> x)
    {
        var vh = MemoryMarshal.Cast<float, Vector<float>>(h.AsSpan());
        var vx = MemoryMarshal.Cast<float, Vector<float>>(x);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < vh.Length; i++) acc += vh[i] * vx[i];
        float s = Vector.Sum(acc);
        for (int i = vh.Length * Vector<float>.Count; i < h.Length; i++) s += h[i] * x[i];
        return s;
    }
}
