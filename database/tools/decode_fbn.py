#!/usr/bin/env python3
"""Decode P4G .FBN field placement files (format cracked 2026-06-11).

Layout:
    u32 magic   0x12345678
    u32 version 0x00010002
    u32 count
    u32 record_size (0x140 seen)
    u32 zero
    u32 sub_offset (0x60 seen — meaning TBD)
    records at 0x18, each record_size bytes:
        +0x00 u32 model ref/hash
        +0x04 u32 flags (low u16 0x0005 seen)
        +0x08 u32 unit handle (cat = id>>10; cat 3 = field models/NPCs)
        +0x0C f32 (60.0 seen)
        +0x20..+0x4C rotation matrix
        +0x50 f32 X, +0x54 f32 Y, +0x58 f32 Z

Usage: python decode_fbn.py <file.FBN> [...] [--json out.json]
"""
import json
import struct
import sys
from pathlib import Path

MAGIC = 0x12345678


def parse(path):
    d = Path(path).read_bytes()
    magic, ver, count, rsz, _z, sub = struct.unpack_from("<6I", d, 0)
    if magic != MAGIC:
        return None
    recs = []
    for i in range(count):
        base = 0x18 + i * rsz
        if base + 0x5C > len(d):
            break
        h0, fl, uid = struct.unpack_from("<3I", d, base)
        x, y, z = struct.unpack_from("<3f", d, base + 0x50)
        recs.append({"id": uid, "cat": uid >> 10, "idx": uid & 0x3FF,
                     "model": f"{h0:#010x}", "flags": f"{fl:#010x}",
                     "x": x, "y": y, "z": z})
    return {"file": Path(path).name, "version": f"{ver:#x}", "count": count,
            "record_size": rsz, "records": recs}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 1
    out = []
    for p in args:
        r = parse(p)
        if r is None:
            print(f"{p}: bad magic")
            continue
        out.append(r)
        print(f"{r['file']}: {len(r['records'])} records")
        for rec in r["records"]:
            print(f"  id={rec['id']:#06x} (c{rec['cat']}/{rec['idx']:3}) model={rec['model']}  "
                  f"({rec['x']:9.2f},{rec['y']:7.2f},{rec['z']:9.2f})")
    if "--json" in sys.argv:
        path = sys.argv[sys.argv.index("--json") + 1]
        Path(path).write_text(json.dumps(out, indent=1))
        print(f"wrote {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
