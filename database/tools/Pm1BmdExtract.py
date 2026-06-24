"""
Pull the embedded MSG1 BMD chunk out of a PM1 event-bundle file.

PM1 is a section-indexed bundle (magic 'PMD1' at +0x08) that mixes model
refs, effect refs, and one embedded MSG1 BMD with all the event's dialog.

Heuristic that works without a full PM1 spec: locate the MSG1 magic, then
look at the 4 bytes immediately BEFORE it — in BMD's standard 8-byte
file header those are the file size (with the 4 leading bytes being zero
or compression/UserId). The chunk runs `size` bytes starting from those
8 header bytes.

If the chunk doesn't parse cleanly, fall back to "MSG1 to end of file"
which is wasteful but recoverable downstream.
"""
import struct, sys
from pathlib import Path

if len(sys.argv) < 3:
    print("Usage: Pm1BmdExtract.py <event_dir_root> <out_dir>")
    sys.exit(1)

root = Path(sys.argv[1])
out  = Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)

MAGIC = b"MSG1"
n_ok = n_bad = n_no_msg = 0
for pm in sorted(root.rglob("*.PM1")):
    data = pm.read_bytes()
    idx = data.find(MAGIC)
    if idx < 0:
        n_no_msg += 1
        continue

    # BMD standard file header: 4 bytes (zero/comp/UserId) + 4 bytes filesize
    # + 8 bytes magic "MSG1\0\0\0\0". The MSG1 ASCII we located is at the
    # start of those 8 magic bytes — so the file header begins at idx-8.
    hdr_start = idx - 8
    if hdr_start < 0:
        # Magic too close to start of file, just copy from idx to EOF.
        chunk = data[idx:]
    else:
        file_size = struct.unpack_from("<I", data, hdr_start + 4)[0]
        end = hdr_start + file_size
        if 0 < file_size < len(data) and end <= len(data):
            chunk = data[hdr_start:end]
        else:
            # Fall back: pre-pend a synthetic 8-byte file header and dump
            # MSG1-onwards. BmdDecompiler already auto-handles bare MSG1.
            chunk = data[idx:]

    # Mirror the source folder structure (event/eXYZ/foo.PM1 → eXYZ/foo.bmd)
    rel = pm.relative_to(root).with_suffix(".bmd")
    target = out / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(chunk)
    n_ok += 1

print(f"Extracted {n_ok} BMDs from {n_ok + n_no_msg} PM1s. {n_no_msg} had no MSG1.")
