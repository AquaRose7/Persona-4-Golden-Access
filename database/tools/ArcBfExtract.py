"""
Extract just the BF flowscript from every .arc in a directory.
The BF entry's header is always at offset 0..47:
  u32 leading_field
  char[32] name  (e.g. "f020.bf")
  u32 size       (BF byte length, FLW0 starts immediately after)
  char[8] pad
Then `size` bytes of FLW0 data.
"""
import struct
import sys
from pathlib import Path

if len(sys.argv) < 3:
    print("Usage: ArcBfExtract.py <pack_dir> <out_dir>")
    sys.exit(1)

pack = Path(sys.argv[1])
out = Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)

n_ok = 0
n_bad = 0
for arc in sorted(pack.glob("*.arc")):
    data = arc.read_bytes()
    if len(data) < 64:
        n_bad += 1; continue
    name = data[4:36].rstrip(b'\x00').decode('ascii', errors='replace')
    if not name.endswith('.bf'):
        n_bad += 1; continue
    size = struct.unpack('<I', data[36:40])[0]
    if data[48:52] != b'FLW0':
        n_bad += 1; continue
    if 48 + size > len(data):
        n_bad += 1; continue
    # AtlusScriptLibrary expects an 8-byte file header before FLW0:
    #   u32 type/compression/userid  (zero here)
    #   u32 file_size                 (inner size, used by reader)
    # The .arc strips this header, so we prepend it ourselves.
    bf_body = data[48:48+size]
    header = struct.pack('<II', 0, size + 8)
    out_name = f"{arc.stem}__{name}"
    (out / out_name).write_bytes(header + bf_body)
    n_ok += 1

print(f"Extracted {n_ok} BF files to {out}  ({n_bad} archives skipped)")
