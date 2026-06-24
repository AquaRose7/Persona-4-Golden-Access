#!/usr/bin/env python3
"""Unpack P4G field/pack/fd*.arc bundles.

Layout (confirmed 2026-06-11 on fd008_001.arc — simpler than the old guess):
    [u32 entry_count]
    repeat:
        [char[32] name, NUL-padded]
        [u32 size]
        [size bytes of data]
No alignment padding between entries. The first entry is the area BF
(f0XX.bf — FLW0 with its own 8-byte pre-header, which is what the old
unpacker mistook for "8 bytes pad"), followed by h*/n* registry .bin
files, .ENV lighting, .HBN hit-box sets and per-object .FBN placement
files.

Usage:
    python AreaArcUnpack.py <arc> [outdir]      # extract all
    python AreaArcUnpack.py <arc> --list        # list entries only
"""
import struct
import sys
from pathlib import Path


def walk(data: bytes):
    (count,) = struct.unpack_from("<I", data, 0)
    off = 4
    n = 0
    while off + 36 <= len(data) and n < count:
        name = data[off : off + 32].split(b"\0")[0].decode("ascii", "replace")
        (size,) = struct.unpack_from("<I", data, off + 32)
        start = off + 36
        if not name or start + size > len(data):
            print(f"  WARNING: walk stopped at 0x{off:x} (entry {n}/{count})")
        yield name, start, size
        off = start + size
        n += 1


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    arc = Path(sys.argv[1])
    data = arc.read_bytes()
    entries = list(walk(data))
    print(f"{arc.name}: {len(entries)} entries ({len(data)} bytes)")
    if "--list" in sys.argv:
        for name, start, size in entries:
            print(f"  {name:<32} off=0x{start:06x} size={size}")
        return 0
    outdir = Path(sys.argv[2]) if len(sys.argv) > 2 and not sys.argv[2].startswith("--") else arc.with_suffix("")
    outdir.mkdir(parents=True, exist_ok=True)
    for name, start, size in entries:
        (outdir / name).write_bytes(data[start : start + size])
    print(f"extracted {len(entries)} files to {outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
