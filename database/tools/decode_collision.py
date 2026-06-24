#!/usr/bin/env python3
"""Decode P4G overworld FIELD COLLISION geometry (the "atari" hit meshes).

Discovery (2026-06-21): each overworld area's walkable/collision geometry lives in
`field/pack/f0XX_YYY.arc`, entry `h0XX_YYY.AMD` (the "h" = hit model). The main
`f0XX_YYY.AMD` in the same arc is the visible render model; `_rmd.arc` holds room
sub-models. NONE of fd0XX_YYY.arc (the small script/trigger arc) has geometry.

The .AMD is Atlus OMG ("OMG.00.1PSP", CHNK/MODEL_DATA wrapper). The collision is
a set of meshes named `atari_NN_0_Mesh` (atari = Japanese "collision/hit") with a
sibling `atari_NN_0_Arrays` (vertex buffer) and `atari_NN_Bone` (transform/bbox).

Node format (flat, walkable): each node = ASCIIZ name (padded), then a stream of
tagged fields `[u32 tag][u32 size][payload]`:
  tag 0x8066  : index buffer.  payload = [u32 flag][u32 a][u32 b][u32 ntri]
                [u16 idx...]; ntri triangles, 3 u16 indices each (triangle LIST).
  tag 0x180011e0 (in Arrays node, "ArrayDescriptor"):
                header = [u32 tag][u32 nverts][u32 flag][u32 pad] (16 bytes),
                then nverts * 24 bytes: 3 f32 normal (unit) + 3 f32 position
                (world XYZ).
  tag 0x8014  : bbox (in Bone node) = 2 * (3 f32) min/max corner.
  tag 0x8048/0x8049/0x804c : translate / rotate / scale (bones are identity for
                f006_001 — mesh positions are already world-space).

WORLD SPACE: positions match the overworld catalog/WorldPlayerPos space on X, but the
collision **Z axis is FLIPPED** — parse() negates Z so the decoded geometry lands in the
catalog/WorldPlayerPos frame (X,Z ground plane; Y = up, runs 0 .. -wallheight). Verified
2026-06-21 against the catalog interactables across 42 areas (Z-flip aligns 37 of them;
the un-flipped decode left ~40% mis-framed, e.g. 006_004 routed A* off the map).

Usage:
  python decode_collision.py <h0XX_YYY.AMD>            # print summary + AABB
  python decode_collision.py <h0XX_YYY.AMD> --tris     # dump all triangles
  python decode_collision.py <h0XX_YYY.AMD> --json out.json
  python decode_collision.py --arc <f0XX_YYY.arc>      # pull h*.AMD from arc first
"""
import json
import struct
import sys
from pathlib import Path

VTAG = 0x180011E0   # vertex-array descriptor
ITAG = 0x8066       # index buffer
BTAG = 0x8014       # bbox
STRIDE = 24


def read_cstr(d, o):
    e = d.index(b"\0", o)
    return d[o:e].decode("latin1"), e + 1


def extract_h_amd_from_arc(arc_path):
    """Pull the h0XX_YYY.AMD entry out of a field/pack/f*.arc.
    arc layout: [u32 count] then per-entry [char[32] name][u32 size][AMD data]."""
    d = Path(arc_path).read_bytes()
    cnt = struct.unpack_from("<I", d, 0)[0]
    off = 4
    out = {}
    for _ in range(cnt):
        name = d[off:off + 32].split(b"\0")[0].decode("latin1")
        size = struct.unpack_from("<I", d, off + 32)[0]
        data = d[off + 36:off + 36 + size]
        out[name] = data
        off += 36 + size
    for name, data in out.items():
        if name.startswith("h") and name.endswith(".AMD") and "_cam" not in name:
            return name, data
    return None, None


def find_fields(d, tag):
    """Yield (offset, size) of every occurrence of a u32 tag whose following
    u32 size keeps the payload inside the buffer. Cheap but robust for these
    small hit models (no false positives observed in f006_001)."""
    o = 0
    tb = struct.pack("<I", tag)
    while True:
        o = d.find(tb, o)
        if o < 0 or o + 8 > len(d):
            break
        size = struct.unpack_from("<I", d, o + 4)[0]
        if 0 < size <= len(d) - (o + 8):
            yield o, size
        o += 4


def parse(amd):
    """Return list of meshes: {name, verts:[(x,y,z)...], tris:[(a,b,c)...]}.
    Pairs each index buffer (ITAG) with the next vertex array (VTAG) after it,
    in file order — they alternate Mesh-node / Arrays-node per atari piece."""
    # collect index + vertex blocks in file order
    iblocks = []
    for o, size in find_fields(amd, ITAG):
        flag, a, b, ntri = struct.unpack_from("<4I", amd, o + 8)
        idx_off = o + 8 + 16
        need = ntri * 3 * 2
        if idx_off + need > len(amd) or ntri > 100000:
            continue
        idx = struct.unpack_from("<%dH" % (ntri * 3), amd, idx_off)
        tris = [tuple(idx[i:i + 3]) for i in range(0, len(idx), 3)]
        iblocks.append((o, tris))
    vblocks = []
    for o, size in find_fields(amd, VTAG):
        nverts = struct.unpack_from("<I", amd, o + 4)[0]
        vo = o + 16  # tag(4)+nverts(4)+flag(4)+pad(4)
        if nverts == 0 or nverts > 200000:
            continue
        if vo + nverts * STRIDE > len(amd):
            continue
        verts = []
        for i in range(nverts):
            x, y, z = struct.unpack_from("<3f", amd, vo + i * STRIDE + 12)
            # The collision mesh Z axis is FLIPPED vs WorldPlayerPos / overworld_catalog space.
            # Verified empirically 2026-06-21: Z-negation aligns the decoded floor with the catalog
            # interactables in 37/42 catalog-checkable areas (helps 22, harmless on 15); identity-only
            # had ~40% of areas mis-framed (006_004 sat entirely outside its own grid → A* routed off
            # the map). Near-Z-symmetric areas (e.g. 006_001) hid the bug. Negate here so every
            # consumer (aabb / world_triangles / build_walkgrid) shares the world frame the mod uses.
            verts.append((x, y, -z))
        vblocks.append((o, verts))
    # name lookup: nearest preceding "atari_NN_0_..." string for each index block
    meshes = []
    vi = 0
    for io, tris in iblocks:
        # find the vertex block that comes after this index block (same piece)
        verts = None
        while vi < len(vblocks) and vblocks[vi][0] < io:
            vi += 1
        if vi < len(vblocks):
            verts = vblocks[vi][1]
            vi += 1
        # name = the "atari_NN_0_Mesh" string preceding io
        name = None
        p = amd.rfind(b"_Mesh\0", 0, io)
        if p > 0:
            s = p
            while s > 0 and 32 <= amd[s - 1] < 127:
                s -= 1
            name = amd[s:p + 5].decode("latin1")
        meshes.append({"name": name, "tris": tris,
                       "verts": verts or []})
    return meshes


def aabb(meshes):
    pts = [v for m in meshes for v in m["verts"]]
    if not pts:
        return None
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]; zs = [p[2] for p in pts]
    return (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs))


def world_triangles(meshes):
    """Flatten to a list of (v0,v1,v2) world-space XYZ triangles."""
    out = []
    for m in meshes:
        v = m["verts"]
        for (a, b, c) in m["tris"]:
            if a < len(v) and b < len(v) and c < len(v):
                out.append((v[a], v[b], v[c]))
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__); return 1
    if "--arc" in sys.argv:
        name, amd = extract_h_amd_from_arc(args[0])
        if amd is None:
            print("no h*.AMD in arc"); return 1
        print(f"hit model: {name} ({len(amd)} bytes)")
    else:
        amd = Path(args[0]).read_bytes()
    meshes = parse(amd)
    tris = world_triangles(meshes)
    box = aabb(meshes)
    print(f"meshes: {len(meshes)}  total tris: {len(tris)}")
    for m in meshes:
        print(f"  {m['name']}: {len(m['verts'])} verts, {len(m['tris'])} tris")
    if box:
        print(f"AABB  X[{box[0]:.1f},{box[1]:.1f}]  "
              f"Y[{box[2]:.1f},{box[3]:.1f}]  Z[{box[4]:.1f},{box[5]:.1f}]")
    if "--tris" in sys.argv:
        for t in tris:
            print("  T", " ".join(f"({p[0]:.1f},{p[1]:.1f},{p[2]:.1f})" for p in t))
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        Path(out).write_text(json.dumps(
            {"aabb": box, "triangles": tris}, indent=1))
        print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
