#!/usr/bin/env python3
"""Decode P4G .HBN hit/placement-point files (format cracked 2026-06-11).

Layout:
    u32 magic   0x45678901
    u32 version 0x00010003
    8 x (u32 count, u32 record_size)   -- section TOC
    sections follow back-to-back in TOC order

Known section record shapes (by record_size):
    0x20 (32B): trigger boxes, cat-13 handles (0x3400+):
        u32 id, u32 tag(a80000ff), f32 x, y, z, f32 a, b, c (extents/2nd corner)
    0x14 (20B): points, cat-14 handles (0x3800+):
        u32 id, f32 x, y, z, f32 rotation_degrees   (spawn/entry points)

A single .HBN file may contain multiple concatenated blobs (day/night?);
we parse all blobs found.

Usage: python decode_hbn.py <file.HBN> [--json out.json]
"""
import json
import struct
import sys
from pathlib import Path

MAGIC = 0x45678901


def parse_blob(d, off):
    magic, ver = struct.unpack_from("<2I", d, off)
    if magic != MAGIC:
        return None, off
    toc = []
    p = off + 8
    for _ in range(8):
        if p + 8 > len(d):
            return {"version": f"{ver:#x}", "sections": [], "truncated": True}, len(d)
        cnt, rsz = struct.unpack_from("<2I", d, p)
        toc.append((cnt, rsz))
        p += 8
    sections = []
    for si, (cnt, rsz) in enumerate(toc):
        recs = []
        for i in range(cnt):
            base = p + i * rsz
            if base + rsz > len(d):
                break
            uid, = struct.unpack_from("<I", d, base)
            rec = {"id": uid, "cat": uid >> 10, "idx": uid & 0x3FF}
            if rsz == 0x20:
                vals = struct.unpack_from("<6f", d, base + 8)
                rec.update(x=vals[0], y=vals[1], z=vals[2], ext=list(vals[3:]))
            elif rsz == 0x14:
                x, y, z, rot = struct.unpack_from("<4f", d, base + 4)
                rec.update(x=x, y=y, z=z, rot=rot)
            else:
                rec["raw"] = d[base:base + rsz].hex()
            recs.append(rec)
        if recs:
            sections.append({"section": si, "record_size": rsz, "records": recs})
        p += cnt * rsz
    return {"version": f"{ver:#x}", "sections": sections}, p


def parse(path):
    d = Path(path).read_bytes()
    blobs = []
    off = 0
    while off + 8 <= len(d):
        blob, off2 = parse_blob(d, off)
        if blob is None:
            break
        blobs.append(blob)
        off = off2
    return blobs


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    blobs = parse(sys.argv[1])
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        Path(out).write_text(json.dumps(blobs, indent=1))
        print(f"wrote {out}")
        return 0
    for bi, blob in enumerate(blobs):
        print(f"-- blob {bi} (ver {blob['version']})")
        for sec in blob["sections"]:
            print(f"  section {sec['section']} rsz={sec['record_size']:#x} n={len(sec['records'])}")
            for r in sec["records"]:
                extra = f" rot={r['rot']:.1f}" if "rot" in r else (f" ext={['%.1f' % e for e in r['ext']]}" if "ext" in r else "")
                if "x" in r:
                    print(f"    id={r['id']:#06x} (c{r['cat']}/{r['idx']:3})  ({r['x']:9.2f},{r['y']:7.2f},{r['z']:9.2f}){extra}")
                else:
                    print(f"    id={r['id']:#06x} raw={r['raw'][:48]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
