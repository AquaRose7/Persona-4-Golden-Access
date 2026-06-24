"""
opmap.bin decoder.

Layout:
  u32 record_count = 6 (header at 0x00-0x07, 8 bytes; second u32 is zero)
  Records: 40 bytes each starting at offset 0x08:
    u16 type      (always 0x1F so far)
    u16 sub       (incrementing: 2, 3, 4, 5, ...)
    f32  v1        (small, e.g. 23.0)
    f32  v2        (medium, e.g. 101.0/104.0)
    f32  v3        (often 0)
    f32  v4        (often around -98..-446)
    f32  v5        (constant 90.0 in early records)
    f32  v6        (constant -600.0)
    f32  v7        (constant 0)
    f32  v8        (constant -3510.0)

Working theory: this is the overworld-map UI region table. Each record
likely defines one clickable map region. The first three floats may be
the region's bounding box on the 2D map; the trailing four are the
3D anchor in world space (constant per area).
"""
import struct, json
from pathlib import Path

SRC = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/extract/data_main/field/table/opmap.bin")
data = SRC.read_bytes()

HDR = 8
REC = 40
n = (len(data) - HDR) // REC
print(f"file: {len(data)} bytes; {HDR}-byte header + {REC}-byte records => {n} records "
      f"({(len(data) - HDR) % REC} trailing)")

count_field = struct.unpack_from("<I", data, 0)[0]
print(f"header u32 (declared count?): {count_field}")
print()

print(f"{'idx':>4}  {'off':>5}  {'type':>4}  {'sub':>5}  "
      + "  ".join(f"{'v'+str(i):>8}" for i in range(1, 9)))

records = []
empty_run = 0
for i in range(n):
    off = HDR + i * REC
    type_, sub = struct.unpack_from("<HH", data, off)
    fs = struct.unpack_from("<8f", data, off+4)
    if type_ == 0 and sub == 0 and all(f == 0.0 for f in fs):
        empty_run += 1
        if empty_run > 3:
            continue
    else:
        empty_run = 0
    rec = {"idx": i, "off": off, "type": type_, "sub": sub, "f": [round(f, 3) for f in fs]}
    records.append(rec)
    if i < 16 or (i % 30 == 0):
        print(f"  {i:3d}  {off:5x}  {type_:4d}  {sub:5d}  " + "  ".join(f"{f:8.2f}" for f in fs))

print(f"\nKept {len(records)} non-empty records, dropped {n - len(records)} all-zero ones")

# Group by `type` to see what kinds of regions exist
from collections import Counter
print(f"\ndistinct type/sub values:")
print("  types:", Counter(r["type"] for r in records).most_common())
print("  subs (top 10):", Counter(r["sub"] for r in records).most_common(10))

out = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/opmap_table.json")
out.write_text(json.dumps({
    "source": "data.cpk -> field/table/opmap.bin",
    "header_count": count_field,
    "note": "Each record likely defines an overworld-map clickable region. Field meanings unverified; floats look like a 2D bbox + a 3D world anchor.",
    "records": records,
}, indent=2))
print(f"\nWrote {out} ({out.stat().st_size:,} bytes)")
