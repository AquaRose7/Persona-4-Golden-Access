namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// The P beacon's ROUTE (v2.2.1, Haru: "the normal one we use but other than pointing at the thing it routes you now"; the
/// model is the FE3H Access / P5R RouteGuide beacon Haru likes). The beacon keeps its own sound; only WHERE the sound sits
/// changes: a "carrot" <see cref="Lookahead"/> units along the auto-walk's planned route, ahead of how far along it the player
/// has come. It slides around corners instead of jumping between them, so the beacon never flips about. Progress only looks
/// at a window of the line around the last progress (a route that doubles back can't jump to its far side) and never goes
/// backwards. The route is planned again only when the player is well off it or a moving target walked away from its end,
/// at most every few seconds and silently. The planners are the walks' own (OverworldNav / StairsPlan); this class is
/// only the line and the progress.
/// </summary>
internal sealed class RouteGuide
{
    internal const float Lookahead = 250f;          // the sound sits this far along the route ahead of you
    private const float Back = 150f, Ahead = 600f;  // the progress window along the line
    private const float OffRoute = 300f;            // farther off the line than this → plan again
    private const float Moved = 300f;               // a moving target this far from the route's end → plan again
    private const long ReplanMs = 2500, FollowReplanMs = 1500;

    private (float x, float z)[]? _pts;
    private float[] _along = System.Array.Empty<float>();
    private float _progress;
    private long _lastPlanMs = long.MinValue / 2;
    private float _failX = float.NaN, _failZ = float.NaN;

    internal bool HasRoute => _pts != null;
    internal int Count => _pts?.Length ?? 0;
    internal float Length => _pts == null ? 0f : _along[^1];
    /// <summary>The route left to walk (world units).</summary>
    internal float Remaining => _pts == null ? 0f : MathF.Max(0f, _along[^1] - _progress);

    internal void Clear()
    {
        _pts = null;
        _progress = 0f;
        _lastPlanMs = long.MinValue / 2;
        _failX = _failZ = float.NaN;
    }

    /// <summary>A new route: the player, the planned points, and the target itself as the last point (a plan can end at
    /// the reachable spot next to it).</summary>
    internal void Set(IReadOnlyList<(float x, float z)> pts, float px, float pz, float tx, float tz, long now)
    {
        var l = new List<(float x, float z)>(pts.Count + 2) { (px, pz) };
        foreach (var p in pts) if (Dist(l[^1].x, l[^1].z, p.x, p.z) > 1f) l.Add(p);
        if (Dist(l[^1].x, l[^1].z, tx, tz) > 30f) l.Add((tx, tz));
        _pts = l.ToArray();
        _along = new float[_pts.Length];
        for (int i = 1; i < _pts.Length; i++) _along[i] = _along[i - 1] + Dist(_pts[i - 1].x, _pts[i - 1].z, _pts[i].x, _pts[i].z);
        _progress = 0f;
        _progress = Project(px, pz, 0f, Ahead).along;
        _lastPlanMs = now;
        _failX = _failZ = float.NaN;
    }

    /// <summary>No plan from here: the beacon points straight at the target, and plans again once the player has moved
    /// <see cref="OffRoute"/> away from this spot (no retry loop, no log spam while standing still).</summary>
    internal void Failed(float px, float pz, long now)
    {
        _pts = null;
        _progress = 0f;
        _lastPlanMs = now;
        _failX = px; _failZ = pz;
    }

    /// <summary>Whether to plan again now (target = where it is now).</summary>
    internal bool ShouldReplan(float px, float pz, float tx, float tz, long now)
    {
        if (_pts == null)
            return float.IsNaN(_failX)
                || (now - _lastPlanMs >= ReplanMs && Dist(px, pz, _failX, _failZ) > OffRoute);
        if (now - _lastPlanMs >= ReplanMs && Project(px, pz, _progress - Back, _progress + Ahead).off > OffRoute) return true;
        var end = _pts[^1];
        return now - _lastPlanMs >= FollowReplanMs && Dist(end.x, end.z, tx, tz) > Moved;
    }

    /// <summary>One poll: moves the progress (never backwards) and returns the point the sound sits on.</summary>
    internal (float x, float z) Carrot(float px, float pz)
    {
        if (_pts == null) return (px, pz);
        _progress = MathF.Max(_progress, Project(px, pz, _progress - Back, _progress + Ahead).along);
        return PointAt(_progress + Lookahead);
    }

    private (float x, float z) PointAt(float along)
    {
        var p = _pts!;
        if (along <= 0f) return p[0];
        for (int i = 0; i < p.Length - 1; i++)
            if (along <= _along[i + 1])
            {
                float seg = _along[i + 1] - _along[i], t = seg <= 0f ? 0f : (along - _along[i]) / seg;
                return (p[i].x + (p[i + 1].x - p[i].x) * t, p[i].z + (p[i + 1].z - p[i].z) * t);
            }
        return p[^1];
    }

    /// <summary>The nearest point of the line to (x,z) among the legs within [from, to] along it: its distance along the
    /// line and how far off the line (x,z) is.</summary>
    private (float along, float off) Project(float x, float z, float from, float to)
    {
        var p = _pts!;
        if (p.Length == 1) return (0f, Dist(p[0].x, p[0].z, x, z));
        float best = float.MaxValue, at = Math.Clamp(from, 0f, _along[^1]);
        for (int i = 0; i < p.Length - 1; i++)
        {
            if (_along[i + 1] < from || _along[i] > to) continue;
            float ax = p[i].x, az = p[i].z, dx = p[i + 1].x - ax, dz = p[i + 1].z - az, l2 = dx * dx + dz * dz;
            float t = l2 == 0f ? 0f : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0f, 1f);
            float d = Dist(ax + dx * t, az + dz * t, x, z);
            if (d < best) { best = d; at = _along[i] + t * (_along[i + 1] - _along[i]); }
        }
        if (best == float.MaxValue) { var q = PointAt(at); best = Dist(q.x, q.z, x, z); }
        return (at, best);
    }

    private static float Dist(float ax, float az, float bx, float bz)
        => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
}
