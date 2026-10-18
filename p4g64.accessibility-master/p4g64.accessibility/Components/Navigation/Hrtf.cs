using System.IO;
using System.Runtime.InteropServices;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// A measured HEAD for 3D sound (ported 2026-10-18 from P5R Access, where Haru picked it): per direction, the filter each
/// ear puts on a sound, and how much later the far ear hears it. The shipped set is the Neumann KU100 dummy head of the
/// SADIE II database (Apache 2.0, hrtf\SADIE-II-LICENSE.txt) — Haru's pick of five blind renders, on DT 990 headphones.
///
/// File (made by the P5R project's database\tools\hrtf_prep.py, the same bytes ship here): "P5RH", 8 × int32 — version 1,
/// sample rate, azimuth count, azimuth step, elevation count, lowest elevation, elevation step, taps — then per elevation
/// row (lowest first) and azimuth column (0 = straight ahead, CLOCKWISE): the inter-aural delay in samples (+ = the right
/// ear hears it later), the left filter, the right filter. The filters are minimum-phase (the delay is kept apart, so
/// blending neighbours does not smear it), and straight ahead each ear carries half the power — the loudness of the
/// centre pan.
/// Pure: no game, no logging (the audio thread calls <see cref="Filter"/>).
/// </summary>
internal sealed class HrtfSet
{
    internal int Taps { get; }
    internal int SampleRate { get; }

    private readonly int _azN, _elN;
    private readonly float _azStep, _elMin, _elStep;
    private readonly float[] _itd;        // [row * azN + column]
    private readonly float[] _filters;    // [((row * azN + column) * 2 + ear) * taps + k]

    private HrtfSet(int fs, int azN, int azStep, int elN, int elMin, int elStep, int taps, float[] itd, float[] filters)
    {
        SampleRate = fs; _azN = azN; _azStep = azStep; _elN = elN; _elMin = elMin; _elStep = elStep; Taps = taps;
        _itd = itd; _filters = filters;
    }

    /// <summary>The set in <paramref name="path"/>, or null when it is missing or not a whole P5RH v1 file.</summary>
    internal static HrtfSet? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 36 || b[0] != 'P' || b[1] != '5' || b[2] != 'R' || b[3] != 'H') return null;
            var h = MemoryMarshal.Cast<byte, int>(b.AsSpan(4, 32));
            int version = h[0], fs = h[1], azN = h[2], azStep = h[3], elN = h[4], elMin = h[5], elStep = h[6], taps = h[7];
            if (version != 1 || fs <= 0 || azN <= 0 || azN * azStep != 360 || elN < 2 || elStep <= 0 || taps is < 1 or > 1024) return null;
            int records = azN * elN, record = 1 + 2 * taps;
            if (b.Length != 36 + (long)records * record * 4) return null;
            var all = MemoryMarshal.Cast<byte, float>(b.AsSpan(36));
            var itd = new float[records];
            var filters = new float[records * 2 * taps];
            for (int i = 0; i < records; i++)
            {
                itd[i] = all[i * record];
                all.Slice(i * record + 1, 2 * taps).CopyTo(filters.AsSpan(i * 2 * taps));
            }
            return new HrtfSet(fs, azN, azStep, elN, elMin, elStep, taps, itd, filters);
        }
        catch { return null; }
    }

    /// <summary>The filters and the delay for a direction: azimuth in degrees CLOCKWISE from straight ahead (any value, it
    /// wraps), elevation in degrees (clamped to the grid) — the four neighbouring grid points blended.</summary>
    internal void Filter(float azimuthDeg, float elevationDeg, Span<float> left, Span<float> right, out float itdSamples)
    {
        float top = _elMin + (_elN - 1) * _elStep;
        float fe = (Math.Clamp(elevationDeg, _elMin, top) - _elMin) / _elStep;
        int e0 = Math.Min((int)fe, _elN - 2);
        float te = fe - e0;
        float fa = ((azimuthDeg % 360f) + 360f) % 360f / _azStep;
        int a0 = (int)fa % _azN, a1 = (a0 + 1) % _azN;
        float ta = fa - MathF.Floor(fa);

        int r00 = e0 * _azN + a0, r01 = e0 * _azN + a1, r10 = (e0 + 1) * _azN + a0, r11 = (e0 + 1) * _azN + a1;
        float w00 = (1 - te) * (1 - ta), w01 = (1 - te) * ta, w10 = te * (1 - ta), w11 = te * ta;
        itdSamples = w00 * _itd[r00] + w01 * _itd[r01] + w10 * _itd[r10] + w11 * _itd[r11];
        Blend(left, 0, r00, r01, r10, r11, w00, w01, w10, w11);
        Blend(right, 1, r00, r01, r10, r11, w00, w01, w10, w11);
    }

    private void Blend(Span<float> into, int ear, int r00, int r01, int r10, int r11, float w00, float w01, float w10, float w11)
    {
        var f = _filters.AsSpan();
        var p00 = f.Slice((r00 * 2 + ear) * Taps, Taps); var p01 = f.Slice((r01 * 2 + ear) * Taps, Taps);
        var p10 = f.Slice((r10 * 2 + ear) * Taps, Taps); var p11 = f.Slice((r11 * 2 + ear) * Taps, Taps);
        for (int k = 0; k < Taps; k++) into[k] = w00 * p00[k] + w01 * p01[k] + w10 * p10[k] + w11 * p11[k];
    }
}
