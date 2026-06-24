"""
Generic BF + BMD ripper. Scans any binary file for embedded FLW0/MSG1
magics and extracts each chunk using the 8-byte file-level header that
precedes the magic (4 zero/flags + 4 file_size).

Works on PM1, boss e2XX.bin, battle_pack.bin, init_free.bin, and probably
anything else Atlus packs together.

Usage:
    RipBfBmd.py <input_root> <out_dir> [pattern_glob]

The output directory mirrors the input layout, with .bf / .bmd suffixes
chosen by which magic was found. Tries the 8-byte-back interpretation first;
falls back to "from magic to EOF" + lets the decompilers auto-prepend the
header.
"""
import struct, sys, fnmatch
from pathlib import Path

if len(sys.argv) < 3:
    print("Usage: RipBfBmd.py <input_root> <out_dir> [pattern_glob]")
    sys.exit(1)

root = Path(sys.argv[1])
out  = Path(sys.argv[2])
pat  = sys.argv[3] if len(sys.argv) > 3 else "*"
out.mkdir(parents=True, exist_ok=True)

MAGIC_BF  = b"FLW0"
MAGIC_BMD = b"MSG1"

def rip_one(blob_path):
    data = blob_path.read_bytes()
    rel = blob_path.relative_to(root).with_suffix("")
    base = out / rel
    base.parent.mkdir(parents=True, exist_ok=True)

    found = []
    for magic, ext in [(MAGIC_BF, "bf"), (MAGIC_BMD, "bmd")]:
        i = 0
        while True:
            j = data.find(magic, i)
            if j < 0: break
            # Try 8-byte-prefix interpretation.
            hdr = j - 8
            chunk = None
            if hdr >= 0:
                file_size = struct.unpack_from("<I", data, hdr + 4)[0]
                end = hdr + file_size
                if 0 < file_size < len(data) and end <= len(data):
                    chunk = data[hdr:end]
            if chunk is None:
                # Fall back: magic-to-EOF (decompilers will re-pad).
                # To avoid scooping past the next magic, cap at the next one.
                k = data.find(magic, j + 4)
                chunk = data[j:k] if k > 0 else data[j:]
            found.append((j, ext, chunk))
            i = j + 4

    for idx, (pos, ext, chunk) in enumerate(sorted(found)):
        suffix = f"_{idx:03d}.{ext}" if len(found) > 1 else f".{ext}"
        target = base.with_suffix(suffix) if len(found) == 1 else Path(str(base) + suffix)
        target.write_bytes(chunk)
    return len(found)

total_files = total_chunks = 0
for p in sorted(root.rglob("*")):
    if not p.is_file(): continue
    if not fnmatch.fnmatch(p.name, pat): continue
    n = rip_one(p)
    if n:
        total_files += 1
        total_chunks += n

print(f"Ripped {total_chunks} BF/BMD chunks from {total_files} source files into {out}")
