"""
Full shadow.bin decoder.

Layout:
  Section 1 (offset 0x000..0x167, 360 bytes):
    9 records of 40 bytes each:
      8 bytes  zeros
      u32 id        (1..10 with a gap at 6)
      u32 attr_a    (always 90)
      f32 val_a
      u32 flag_a    (always 1)
      u32 zero
      u32 id_again
      u32 attr_b    (always 150)
      f32 val_b

  Section 2 (offset 0x168..end, ~5144 bytes):
    20-byte records, ~257 of them:
      u32 flag1     (always 1)
      u32 flag2     (always 1)
      u32 id        (1..324)
      u32 attr      (= an "attribute kind" enum: 0x5A, 0x96, 0xAA, 0x78, 0xC8, ...)
      f32 val       (multiplier-shaped: typically 1.0..2.0)

What the attr enum likely means: an *attribute* the shadow has (element,
resistance, immunity, attack-type, level-cap, ...). Without the EXE-side
name table that maps `attr` codes to attribute names, the stats are
preserved as raw numbers; pairing with names is a follow-up Ghidra task.

Output: a JSON keyed by shadow_id with both sections merged. Each shadow
gets `section1` (if present) and `section2[]` (list of attr/val pairs).
"""
import struct, json
from pathlib import Path
from collections import defaultdict

SRC = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/extract/data_main/field/table/shadow.bin")
data = SRC.read_bytes()

S1_END = 0x168
shadows = defaultdict(lambda: {"section1": None, "section2": []})

# Section 1
off = 0
while off + 40 <= S1_END:
    pad = data[off:off+8]
    if pad != b"\x00" * 8:
        break
    id_a, attr_a = struct.unpack_from("<II", data, off+8)
    val_a       = struct.unpack_from("<f", data, off+16)[0]
    flag_a      = struct.unpack_from("<I", data, off+20)[0]
    mid         = struct.unpack_from("<I", data, off+24)[0]
    id_b, attr_b = struct.unpack_from("<II", data, off+28)
    val_b       = struct.unpack_from("<f", data, off+36)[0]
    shadows[id_a]["section1"] = {
        "attr_a": attr_a, "val_a": round(val_a, 4), "flag_a": flag_a,
        "attr_b": attr_b, "val_b": round(val_b, 4),
    }
    off += 40

s1_end_real = off
print(f"Section 1: read up to offset 0x{s1_end_real:x} ({s1_end_real} bytes)")

# Section 2
off = S1_END
n_s2 = 0
while off + 20 <= len(data):
    flag1, flag2, sid, attr = struct.unpack_from("<IIII", data, off)
    val = struct.unpack_from("<f", data, off+16)[0]
    # Sanity: if all-zeros run, stop (end-of-data padding)
    if flag1 == 0 and flag2 == 0 and sid == 0 and attr == 0 and val == 0:
        break
    shadows[sid]["section2"].append({
        "attr": attr, "val": round(val, 4),
        "flag1": flag1, "flag2": flag2,
    })
    off += 20
    n_s2 += 1

print(f"Section 2: {n_s2} records, ended at offset 0x{off:x}")
print(f"Trailing bytes: {len(data) - off}  ({data[off:].hex(' ')[:50]}...)")
print(f"Distinct shadow_ids encountered: {len(shadows)}")
print(f"  id range: {min(shadows)}..{max(shadows)}")

# Distinct attribute codes in section 2
from collections import Counter
attr_counts = Counter()
for s in shadows.values():
    for r in s["section2"]:
        attr_counts[r["attr"]] += 1
print(f"\nAttribute codes in section 2 (top 12):")
for attr, n in attr_counts.most_common(12):
    print(f"  0x{attr:02x} = {attr:3d}: {n} records")

# Save as JSON
out = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/shadow_table.json")
serializable = {
    "source": "data.cpk → field/table/shadow.bin",
    "note": "attr codes are enum values (probably element/resistance kinds). Names not yet mapped — likely need EXE extraction.",
    "shadows": {str(sid): rec for sid, rec in sorted(shadows.items())},
}
out.write_text(json.dumps(serializable, indent=2))
print(f"\nWrote {out} ({out.stat().st_size:,} bytes)")
