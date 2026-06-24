"""
nEntry.arc has an index of 3 entries but the data layout is unusual:
- 0x00..0x03: u32 count = 3
- 0x04..0x2B: entry 0 = "mNameTable.bin"   size 0x1260 (= 4704)
- 0x2C..0x53: entry 1 = "m_name_keychar.ctd" size 0x1A0 (= 416)
- 0x54..0x5F: 12 bytes, mostly zeros (a 3rd entry that's null/empty)
- 0x60..    : data area

Empirically:
- 0x60..0x1FF (416 bytes): SJIS single-character table = m_name_keychar.ctd
- 0x200..0x14BF (4800 bytes ≈ entry 0 size): an offset table then text
- 0x14C0..EOF (8.48 MB): the bulk — likely the third "unnamed" entry, which
  is a giant Shift-JIS name DB (each name suffixed with \r\n).

This script splits the file into those three chunks so we can read the text.
"""
import struct, sys, io
from pathlib import Path
# Force UTF-8 stdout so SJIS-decoded names print without cp1256 errors.
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

src = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/extract/data_e/dict/nEntry.arc")
out = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/extract/nEntry_unpacked")
out.mkdir(parents=True, exist_ok=True)

data = src.read_bytes()
print(f"file size: {len(data):,} bytes")

count = struct.unpack_from("<I", data, 0)[0]
print(f"index count: {count}")
for i in range(min(count, 3)):
    off = 4 + i * 40
    name = data[off:off+32].rstrip(b"\x00").decode("ascii", errors="replace")
    size = struct.unpack_from("<I", data, off+32)[0]
    extra = struct.unpack_from("<I", data, off+36)[0]
    print(f"  entry {i}: name={name!r} size={size} (0x{size:X}) extra={extra}")

# Split per the deduced layout.
chunks = [
    ("m_name_keychar_ctd",        data[0x60 : 0x200]),
    ("mNameTable_bin_index_area", data[0x200 : 0x14C0]),
    ("entry2_bulk_namedb",        data[0x14C0 :]),
]
for name, blob in chunks:
    p = out / f"{name}.bin"
    p.write_bytes(blob)
    print(f"  wrote {p.name}  {len(blob):,} bytes")

# Quick content peek of the bulk: try decoding as SJIS line-by-line
print("\n--- first 30 lines of entry2_bulk decoded as Shift-JIS ---")
try:
    text = chunks[2][1].decode("shift_jis", errors="replace")
    for i, line in enumerate(text.split("\r\n")[:30]):
        print(f"  {i:3d}: {line!r}")
except Exception as e:
    print(f"  decode error: {e}")
