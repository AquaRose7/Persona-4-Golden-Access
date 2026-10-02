#!/usr/bin/env python3
"""Build database/overworld_walkgrid.json: per-area walkable grids for A*.

For every overworld area `0XX_YYY` we:
  1. take the extracted `field/pack/f0XX_YYY.arc` (run CpkExtractor first; see below),
  2. decode its collision hit-mesh (`h0XX_YYY.AMD`) via decode_collision.parse(),
  3. split triangles into FLOOR (|normal.y| > 0.7) and WALL (everything else),
  4. rasterize to a grid at cellSize = 60 world units in the SAME world XZ space
     used by the catalog / WorldPlayerPos. The pipeline is OVERLAP -> CLOSE ->
     SUBTRACT WALLS (deliberately generous, per the A* safety note: an
     over-marked walkable area is fine, a sealed corridor breaks A* completely):
       * FLOOR pass: a cell is WALKABLE if a FLOOR triangle's XZ projection
         OVERLAPS the cell at all -- tested by 5 sample points (center + 4
         quarter-points) AND a triangle-vs-cell-AABB overlap test, so a floor
         triangle that merely clips a cell edge still marks it. This fills the
         center-miss gaps/seams that used to disconnect corridors.
       * MORPHOLOGICAL CLOSE (dilate 1 then erode 1) on the walkable mask to
         seal 1-cell pinholes/seams without widening into the void. Close
         preserves wall thickness >= 2 cells, so real walls survive.
       * WALL pass (LAST): a cell is BLOCKED (cleared from walkable) if a WALL
         triangle's XZ edge passes near its center. Re-applied AFTER the close
         so doorways stay open but real walls remain.
  5. emit a base64 1-bit-per-cell row-major WALKABLE bitmask.

COORDINATE CONVENTION (also recorded in the JSON `_convention` field):
  Coordinates are WorldPlayerPos space (== overworld_catalog x/z). Ground plane
  axes +X (right) and +Z. The grid origin is the floor-AABB min corner snapped
  DOWN to a multiple of `cell`. For a world point (x, z):
      col = floor((x - origin[0]) / cell)      # col indexes the X axis
      row = floor((z - origin[1]) / cell)      # row indexes the Z axis
  bit index = row * cols + col, MSB-first within each byte. bit==1 -> WALKABLE.

Usage:
  # one-time: extract the field arcs (skip if /tmp/p4g_field_scratch already has them)
  CpkExtractor.exe "<gameroot>\\data.cpk" /tmp/p4g_field_scratch "pack\\f"
  python build_walkgrid.py                 # build all areas
  python build_walkgrid.py --scratch DIR   # arc source dir (default /tmp/p4g_field_scratch)
"""
import base64
import json
import math
import re
import sys
from pathlib import Path

import decode_collision as dc

CELL = 60.0
FLOOR_NY = 0.7          # |normalized normal.y| above this => floor, else wall

HERE = Path(__file__).resolve().parent
DB = HERE.parent                                   # .../database
OUT = DB / "overworld_walkgrid.json"
DEFAULT_SCRATCH = Path("/tmp/p4g_field_scratch/field/pack")

AREA_RE = re.compile(r"^f(\d{3}_\d{3})\.arc$")


def tri_normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    l = math.sqrt(nx * nx + ny * ny + nz * nz)
    if l == 0:
        return 0.0
    return abs(ny / l)


def split_tris(tris):
    floor, wall = [], []
    for (a, b, c) in tris:
        if tri_normal(a, b, c) > FLOOR_NY:
            floor.append((a, b, c))
        else:
            wall.append((a, b, c))
    return floor, wall


def point_in_tri2d(px, pz, a, b, c):
    """Point-in-triangle test on the XZ projection (barycentric sign test)."""
    ax, az = a[0], a[2]
    bx, bz = b[0], b[2]
    cx, cz = c[0], c[2]
    d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz)
    d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz)
    d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az)
    has_neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
    has_pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
    return not (has_neg and has_pos)


def _seg_overlaps_box(x0, z0, x1, z1, bminx, bminz, bmaxx, bmaxz):
    """Liang-Barsky: does segment (x0,z0)->(x1,z1) intersect the AABB?"""
    dx = x1 - x0
    dz = z1 - z0
    p = [-dx, dx, -dz, dz]
    q = [x0 - bminx, bmaxx - x0, z0 - bminz, bmaxz - z0]
    u0, u1 = 0.0, 1.0
    for pi, qi in zip(p, q):
        if pi == 0:
            if qi < 0:
                return False        # parallel and outside this slab
        else:
            t = qi / pi
            if pi < 0:
                if t > u1:
                    return False
                if t > u0:
                    u0 = t
            else:
                if t < u0:
                    return False
                if t < u1:
                    u1 = t
    return True


def tri_overlaps_cell(a, b, c, cminx, cminz, cmaxx, cmaxz):
    """True if floor triangle (XZ projection) overlaps the cell AABB at all.
    Covers three cases: (1) any cell sample point inside the triangle,
    (2) any triangle vertex inside the cell, (3) any triangle edge crossing
    the cell. Generous on purpose -- a clipped edge still marks the cell."""
    # (1) sample points: center + 4 quarter-points
    qx = (cmaxx - cminx) * 0.25
    qz = (cmaxz - cminz) * 0.25
    cx = (cminx + cmaxx) * 0.5
    cz = (cminz + cmaxz) * 0.5
    for sx, sz in ((cx, cz),
                   (cx - qx, cz - qz), (cx + qx, cz - qz),
                   (cx - qx, cz + qz), (cx + qx, cz + qz)):
        if point_in_tri2d(sx, sz, a, b, c):
            return True
    # (2) any triangle vertex inside the cell
    for v in (a, b, c):
        if cminx <= v[0] <= cmaxx and cminz <= v[2] <= cmaxz:
            return True
    # (3) any triangle edge crosses the cell AABB
    for p, qv in ((a, b), (b, c), (c, a)):
        if _seg_overlaps_box(p[0], p[2], qv[0], qv[2],
                             cminx, cminz, cmaxx, cmaxz):
            return True
    return False


def morph_close(walk, cols, rows):
    """Dilate by 1 (8-connected) then erode by 1 -> fills 1-cell pinholes and
    hairline seams without widening into the void. Real walls (thickness >= 2
    cells) survive: the dilate eats 1 cell of a wall, the erode restores it."""
    n = cols * rows

    def dilate(src):
        out = bytearray(n)
        for r in range(rows):
            base = r * cols
            for col in range(cols):
                i = base + col
                if src[i]:
                    out[i] = 1
                    continue
                hit = 0
                for dr in (-1, 0, 1):
                    rr = r + dr
                    if rr < 0 or rr >= rows:
                        continue
                    rb = rr * cols
                    for dc_ in (-1, 0, 1):
                        cc = col + dc_
                        if 0 <= cc < cols and src[rb + cc]:
                            hit = 1
                            break
                    if hit:
                        break
                out[i] = hit
        return out

    def erode(src):
        out = bytearray(n)
        for r in range(rows):
            base = r * cols
            for col in range(cols):
                i = base + col
                if not src[i]:
                    continue
                keep = 1
                for dr in (-1, 0, 1):
                    rr = r + dr
                    for dc_ in (-1, 0, 1):
                        cc = col + dc_
                        # treat off-grid as empty -> border cells erode away,
                        # which is desirable (don't grow past the AABB edge).
                        if rr < 0 or rr >= rows or cc < 0 or cc >= cols \
                                or not src[rr * cols + cc]:
                            keep = 0
                            break
                    if not keep:
                        break
                out[i] = keep
        return out

    return erode(dilate(walk))


def build_grid(floor, wall):
    """Return (origin_x, origin_z, cols, rows, walkable_set) or None if no floor."""
    if not floor:
        return None
    pts = [v for t in floor for v in t]
    minx = min(p[0] for p in pts)
    maxx = max(p[0] for p in pts)
    minz = min(p[2] for p in pts)
    maxz = max(p[2] for p in pts)
    ox = math.floor(minx / CELL) * CELL
    oz = math.floor(minz / CELL) * CELL
    cols = int(math.ceil((maxx - ox) / CELL)) + 1
    rows = int(math.ceil((maxz - oz) / CELL)) + 1
    if cols <= 0 or rows <= 0 or cols * rows > 6_000_000:
        return None

    walk = bytearray(cols * rows)   # 1 = walkable

    # FLOOR pass: mark a cell WALKABLE if a floor triangle OVERLAPS it at all
    # (5-sample + vertex-in-cell + edge-cross test). Fills the center-miss gaps
    # that used to disconnect corridors. Iterate over each tri's cell-AABB only.
    for (a, b, c) in floor:
        tminx = min(a[0], b[0], c[0]); tmaxx = max(a[0], b[0], c[0])
        tminz = min(a[2], b[2], c[2]); tmaxz = max(a[2], b[2], c[2])
        c0 = max(0, int((tminx - ox) / CELL))
        c1 = min(cols - 1, int((tmaxx - ox) / CELL))
        r0 = max(0, int((tminz - oz) / CELL))
        r1 = min(rows - 1, int((tmaxz - oz) / CELL))
        for r in range(r0, r1 + 1):
            cminz = oz + r * CELL
            cmaxz = cminz + CELL
            for col in range(c0, c1 + 1):
                i = r * cols + col
                if walk[i]:
                    continue
                cminx = ox + col * CELL
                cmaxx = cminx + CELL
                if tri_overlaps_cell(a, b, c, cminx, cminz, cmaxx, cmaxz):
                    walk[i] = 1

    # MORPHOLOGICAL CLOSE: seal 1-cell pinholes / hairline seams between
    # adjacent floor triangles WITHOUT widening into the void (close = dilate
    # then erode, which preserves wall thickness >= 2 cells). We UNION the
    # close result with the original floor mask so close can only ADD cells,
    # never erode the outer ring of a thin corridor away (close-as-fill, not
    # close-as-shrink) -- this keeps the grid strictly >= the floor-overlap mask.
    closed = morph_close(walk, cols, rows)
    for i in range(cols * rows):
        if closed[i]:
            walk[i] = 1

    # WALL pass: clear ONLY cells whose CENTER a wall edge passes close to.
    # Sample densely along each wall edge; block a cell only when the sample
    # lands within WALL_NEAR of that cell's center. A wall running along a cell
    # boundary (center distance ~CELL/2) is therefore IGNORED, so doorways and
    # corridors stay open (deliberate under-marking, per the A* safety note).
    WALL_NEAR = CELL * float(__import__("os").environ.get("WALL_NEAR_FRAC", "0.35"))

    def block_edge(x0, z0, x1, z1):
        ex, ez = x1 - x0, z1 - z0
        length = math.hypot(ex, ez)
        steps = max(1, int(length / (CELL * 0.4)) + 1)
        for s in range(steps + 1):
            t = s / steps
            x = x0 + ex * t
            z = z0 + ez * t
            col = int((x - ox) / CELL)
            row = int((z - oz) / CELL)
            if not (0 <= col < cols and 0 <= row < rows):
                continue
            cx = ox + (col + 0.5) * CELL
            cz = oz + (row + 0.5) * CELL
            if abs(x - cx) <= WALL_NEAR and abs(z - cz) <= WALL_NEAR:
                walk[row * cols + col] = 0

    for (a, b, c) in wall:
        block_edge(a[0], a[2], b[0], b[2])
        block_edge(b[0], b[2], c[0], c[2])
        block_edge(c[0], c[2], a[0], a[2])

    walkset = walk
    return ox, oz, cols, rows, walkset


REACH_MIN_WALL_H = 40.0     # wall pieces lower than this (steps, curbs) never stop the reach flood


def wall_segments(wall):
    """XZ segments of the TALL wall triangles (a vertical quad projects to a line)."""
    segs = []
    for (a, b, c) in wall:
        ys = (a[1], b[1], c[1])
        if max(ys) - min(ys) < REACH_MIN_WALL_H:
            continue
        for p, q in ((a, b), (b, c), (c, a)):
            if math.hypot(q[0] - p[0], q[2] - p[2]) > 1.0:
                segs.append((p[0], p[2], q[0], q[2]))
    return segs


def _cross(ax, az, bx, bz, cx, cz):
    return (bx - ax) * (cz - az) - (bz - az) * (cx - ax)


def seg_hits(p0x, p0z, p1x, p1z, s):
    """Does the move p0→p1 cross wall segment s (touching counts: conservative)?"""
    qx, qz, rx, rz = s
    d1 = _cross(qx, qz, rx, rz, p0x, p0z)
    d2 = _cross(qx, qz, rx, rz, p1x, p1z)
    d3 = _cross(p0x, p0z, p1x, p1z, qx, qz)
    d4 = _cross(p0x, p0z, p1x, p1z, rx, rz)
    return (d1 * d2 <= 0) and (d3 * d4 <= 0) and not (d1 == 0 and d2 == 0)


def reach_filter(walk, ox, oz, cols, rows, segs, seeds):
    """THE REACH RULE (2026-09-29, Okina/Shrine): a cell is walkable only if a body can get there from
    a place the player REALLY stood without crossing a tall wall. Several town hit models are a closed
    WALL OUTLINE over one big FLOOR RECTANGLE (Okina: 81 floor tris spanning the whole map) — the old
    floor-overlap mask marked everything outside the outline walkable and routes leaked out through
    the raster gaps of diagonal walls. Moves between 4-neighbour cell centres are tested EXACTLY
    against the wall segments (no raster gaps). A seed only counts when the straight line from the
    seed point to its cell centre crosses no wall (a seed hugging a wall must not flood the outside).
    Returns (new_walk, stats) or (walk, None) when no seed survived (area left unchanged)."""
    B = CELL * 4
    buckets = {}
    for i, s in enumerate(segs):
        x0, x1 = sorted((s[0], s[2])); z0, z1 = sorted((s[1], s[3]))
        for bx in range(int(math.floor(x0 / B)), int(math.floor(x1 / B)) + 1):
            for bz in range(int(math.floor(z0 / B)), int(math.floor(z1 / B)) + 1):
                buckets.setdefault((bx, bz), []).append(i)

    def blocked(ax, az, bx, bz):
        seen = set()
        for gx in range(int(math.floor(min(ax, bx) / B)), int(math.floor(max(ax, bx) / B)) + 1):
            for gz in range(int(math.floor(min(az, bz) / B)), int(math.floor(max(az, bz) / B)) + 1):
                for i in buckets.get((gx, gz), ()):
                    if i in seen:
                        continue
                    seen.add(i)
                    if seg_hits(ax, az, bx, bz, segs[i]):
                        return True
        return False

    def center(c, r):
        return ox + (c + 0.5) * CELL, oz + (r + 0.5) * CELL

    reached = bytearray(cols * rows)
    queue = []
    used = 0
    for (sx, sz) in seeds:
        c0, r0 = int((sx - ox) // CELL), int((sz - oz) // CELL)
        best = None
        for dr in (0, -1, 1):
            for dc_ in (0, -1, 1):
                c, r = c0 + dc_, r0 + dr
                if not (0 <= c < cols and 0 <= r < rows) or not walk[r * cols + c]:
                    continue
                cx, cz = center(c, r)
                if blocked(sx, sz, cx, cz):
                    continue
                d = (cx - sx) ** 2 + (cz - sz) ** 2
                if best is None or d < best[0]:
                    best = (d, c, r)
        if best is None:
            continue
        i = best[2] * cols + best[1]
        used += 1
        if not reached[i]:
            reached[i] = 1
            queue.append((best[1], best[2]))
    if not queue:
        return walk, None
    while queue:
        c, r = queue.pop()
        ax, az = center(c, r)
        for dc_, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nc, nr = c + dc_, r + dr
            if not (0 <= nc < cols and 0 <= nr < rows):
                continue
            j = nr * cols + nc
            if reached[j] or not walk[j]:
                continue
            bx, bz = center(nc, nr)
            if blocked(ax, az, bx, bz):
                continue
            reached[j] = 1
            queue.append((nc, nr))
    before = sum(1 for v in walk if v)
    after = sum(1 for v in reached if v)
    return reached, {"seeds": len(seeds), "seeds_used": used, "before": before, "after": after}


def area_seeds():
    """{area: [(x,z)...]} — places the player (or the game's followers) REALLY stood: catalog
    followPoints, learned check spots (mapped to their area by the catalog box), and every walker
    position in the Reloaded logs (area from the walk START line). Same truth as walkgrid_audit.py."""
    import glob, os
    cat = json.loads((DB / "overworld_catalog.json").read_text(encoding="utf-8"))["areas"]
    seeds = {}
    for k, ar in cat.items():
        seeds.setdefault(k, []).extend((p["x"], p["z"]) for p in ar.get("followPoints", []))
    boxes = {}
    for k, ar in cat.items():
        for it in ar.get("interactables", []):
            boxes[(round(it["x"]), round(it["z"]))] = k
    # The mod dual-writes its learned spots to database/; P4G_MOD_DIR (the live mod folder) is an optional extra source.
    mod_dir = os.environ.get("P4G_MOD_DIR")
    for calp in ([Path(mod_dir) / "overworld_calibration.json"] if mod_dir else []) + [DB / "overworld_calibration.json"]:
        try:
            cal = json.loads(calp.read_text(encoding="utf-8"))
        except Exception:
            continue
        for key, v in cal.items():
            m = re.match(r"(-?\d+)_(-?\d+)_", key)
            if not m:
                continue
            bx, bz = int(m.group(1)), int(m.group(2))
            for dx in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    k = boxes.get((bx + dx, bz + dz))
                    if k:
                        seeds.setdefault(k, []).append((v[0], v[1]))
    logdir = os.path.join(os.environ.get("APPDATA", ""), "Reloaded-Mod-Loader-II", "Logs")
    for lp in glob.glob(os.path.join(logdir, "*P4G.txt")):
        area = None
        for line in open(lp, encoding="utf-8", errors="replace"):
            m = re.search(r"walk START .* area=(\d+)_(\d+)", line)
            if m:
                area = "%03d_%03d" % (int(m.group(1)), int(m.group(2)))
                continue
            if "Area -> major" in line:
                area = None
                continue
            m = re.search(r"\[OverworldNav\] (?:move|walk DONE \([^)]*\) at) \((-?\d+),(-?\d+)\)", line)
            if m and area:
                seeds.setdefault(area, []).append((int(m.group(1)), int(m.group(2))))
    for k in seeds:
        seeds[k] = sorted(set((round(x), round(z)) for x, z in seeds[k]))
    return seeds


def pack_bits(walk, cols, rows):
    n = cols * rows
    out = bytearray((n + 7) // 8)
    for i in range(n):
        if walk[i]:
            out[i >> 3] |= (0x80 >> (i & 7))
    return base64.b64encode(bytes(out)).decode("ascii")


def main():
    scratch = DEFAULT_SCRATCH
    if "--scratch" in sys.argv:
        scratch = Path(sys.argv[sys.argv.index("--scratch") + 1])

    arcs = sorted(p for p in scratch.glob("f*.arc")
                  if AREA_RE.match(p.name))
    print(f"found {len(arcs)} area arcs in {scratch}")

    areas = {}
    failed = []
    use_reach = "--no-reach" not in sys.argv
    seeds = area_seeds() if use_reach else {}
    # MAP VARIANTS (2026-09-30, nav pt2 item 9): 8_10/8_12/8_13, 17_5, 18_3, 4_2, 6_16 … are the same
    # places at another time / date with NO recorded stand points, so they skipped the reach filter and
    # kept the leaky floor-overlap grid. A variant whose hit model is IDENTICAL (same triangle set) to a
    # seeded area borrows that area's seeds; different geometry never borrows (a seed in another
    # variant's void would flood the outside).
    geo_key = {}
    if use_reach:
        import hashlib
        for arc in arcs:
            area = AREA_RE.match(arc.name).group(1)
            try:
                _, amd = dc.extract_h_amd_from_arc(str(arc))
                if amd is None:
                    continue
                tris = dc.world_triangles(dc.parse(amd, apply_bones="--no-bones" not in sys.argv))
                key = hashlib.sha1(repr(sorted(tuple(round(c) for v in t for c in v) for t in tris)).encode()).hexdigest()
                geo_key[area] = key
            except Exception:
                pass
        by_key = {}
        for area, key in geo_key.items():
            by_key.setdefault(key, []).append(area)
        for area, key in geo_key.items():
            if seeds.get(area):
                continue
            donors = [a for a in by_key[key] if a != area and seeds.get(a)]
            if donors:
                seeds[area] = sorted({p for a in donors for p in seeds[a]})
                print(f"  seeds {area}: borrowed {len(seeds[area])} from identical geometry {', '.join(donors)}")
    for arc in arcs:
        area = AREA_RE.match(arc.name).group(1)
        try:
            name, amd = dc.extract_h_amd_from_arc(str(arc))
            if amd is None:
                failed.append((area, "no h*.AMD in arc"))
                continue
            meshes = dc.parse(amd, apply_bones="--no-bones" not in sys.argv)
            tris = dc.world_triangles(meshes)
            if not tris:
                failed.append((area, "0 triangles decoded"))
                continue
            floor, wall = split_tris(tris)
            # STEPS AND CURBS ARE NOT WALLS (2026-09-29): wall pieces lower than REACH_MIN_WALL_H are
            # stepped over in the game — the Shrine's stone-path edges (15-30u) were crossed 22 times in
            # the walk logs, yet they cut the grid into three strips (the Main Hall detours).
            if "--low-walls" not in sys.argv:
                wall = [t for t in wall if max(v[1] for v in t) - min(v[1] for v in t) >= REACH_MIN_WALL_H]
            grid = build_grid(floor, wall)
            if grid is None:
                failed.append((area, "no floor triangles / empty grid"))
                continue
            ox, oz, cols, rows, walk = grid
            if use_reach and seeds.get(area):
                walk2, st = reach_filter(walk, ox, oz, cols, rows, wall_segments(wall), seeds[area])
                if st:
                    walk = walk2
                    print(f"  reach {area}: seeds {st['seeds_used']}/{st['seeds']}  walkable "
                          f"{st['before']} -> {st['after']}")
            wc = sum(1 for b in walk if b)
            areas[area] = {
                "origin": [round(ox, 2), round(oz, 2)],
                "cell": int(CELL),
                "cols": cols,
                "rows": rows,
                "walkable": wc,
                "bits": pack_bits(walk, cols, rows),
            }
        except Exception as e:  # noqa: BLE001
            failed.append((area, f"{type(e).__name__}: {e}"))

    doc = {
        "_convention": (
            "Coords are WorldPlayerPos space (== overworld_catalog x/z). "
            "Ground plane axes +X and +Z. origin = floor-AABB min snapped down "
            "to a multiple of cell. col = floor((x-origin[0])/cell) indexes X; "
            "row = floor((z-origin[1])/cell) indexes Z. bit index = row*cols+col, "
            "MSB-first per byte, base64. bit==1 => WALKABLE."),
        "cell": int(CELL),
        "areas": areas,
    }
    out_path = Path(sys.argv[sys.argv.index("--out") + 1]) if "--out" in sys.argv else OUT
    out_path.write_text(json.dumps(doc, separators=(",", ":")))
    size = out_path.stat().st_size

    print(f"wrote {out_path}")
    print(f"areas: {len(areas)}  failed: {len(failed)}  size: {size:,} bytes "
          f"({size/1024:.1f} KiB)")
    for area, err in failed:
        print(f"  FAIL {area}: {err}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
