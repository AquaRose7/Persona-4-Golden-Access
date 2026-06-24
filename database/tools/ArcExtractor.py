"""
P4G .arc extractor.

Format (deduced from fd020_001.arc):
  u32 leading_field        # 0x1D for fd020_001 — meaning unclear (count?)
  repeated entry:
    char[32] name          # NUL-padded
    u32 size               # bytes of data
    u32 extra_a            # zero for some, non-zero metadata for others
    u32 extra_b            # ditto
    bytes data[size]

Validation: for fd020_001.arc, first entry (f020.bf, 40,736 bytes) ends
exactly at the start of the second entry header (".CMR" name).
"""

import struct
import sys
from pathlib import Path

def extract_arc(arc_path, out_dir, dry=False):
    data = Path(arc_path).read_bytes()
    out = Path(out_dir)
    if not dry:
        out.mkdir(parents=True, exist_ok=True)

    offset = 4  # skip leading u32 header field
    entries = []
    while offset < len(data) - 44:
        name_bytes = data[offset:offset+32]
        nul = name_bytes.find(b'\x00')
        if nul == -1:
            nul = 32
        name = name_bytes[:nul].decode('ascii', errors='replace')

        size = struct.unpack('<I', data[offset+32:offset+36])[0]
        extra_a = struct.unpack('<I', data[offset+36:offset+40])[0]
        extra_b = struct.unpack('<I', data[offset+40:offset+44])[0]

        # sanity guard: if name is empty or size implausibly large, stop
        if not name or size > len(data):
            break

        data_start = offset + 44
        data_end = data_start + size
        if data_end > len(data):
            print(f"  WARN: entry '{name}' size {size} overruns archive, stopping")
            break

        entries.append((name, size, extra_a, extra_b, data_start))

        if not dry:
            # strip leading dot if present (some entries have ".CMR" — keep as ".CMR" though)
            safe_name = name.replace('/', '_').replace('\\', '_')
            if safe_name.startswith('.'):
                safe_name = '_' + safe_name[1:]
            (out / safe_name).write_bytes(data[data_start:data_end])

        offset = data_end

    return entries

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: ArcExtractor.py <input.arc> [output_dir]")
        print("       ArcExtractor.py --list <input.arc>")
        sys.exit(1)

    if sys.argv[1] == '--list':
        arc = sys.argv[2]
        entries = extract_arc(arc, '/tmp/never', dry=True)
        print(f"{arc}: {len(entries)} entries")
        for name, size, ea, eb, off in entries:
            print(f"  {off:8x}  {size:10,}  ea={ea:08x}  eb={eb:08x}  {name}")
    else:
        arc = sys.argv[1]
        out = sys.argv[2] if len(sys.argv) > 2 else Path(arc).stem + '_extracted'
        entries = extract_arc(arc, out)
        print(f"Extracted {len(entries)} entries from {arc} -> {out}")
        for name, size, ea, eb, off in entries:
            print(f"  {size:10,}  {name}")
