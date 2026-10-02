#!/usr/bin/env python3
"""Build the overworld navigation catalog (database/overworld_catalog.json).

For every field/pack/fd{major}_{sub}.arc (data_e):
  - decode every f{maj}_{min}.HBN  -> trigger boxes (cat 13) + follower points (cat 14)
  - pair with h{maj}_{min}.bin     -> row i binds box i to a BF procedure index
    (positional binding RUNTIME-VERIFIED 2026-06-12: Daidara box3->equip_shop,
     save point box8->save_point; see database/OVERWORLD.md)
  - resolve procedure names from the arc's f{maj}.bf label section (type-0
    section, 32-byte entries: char[24] name + u32 instr + u32 pad)
  - decode every .FBN placement set (cat-3 models, world XYZ) for reference

All coordinates are WORLD SPACE (same space as the live unit registry and the
player position chain via 0x140EC0FF0 — NOT the old sub_obj+0xD0 space).

Output JSON shape:
{
  "areas": {
    "008_002": {
      "source": "fd008_002.arc",
      "interactables": [ {"box": 8, "id": "0x3408", "x":..,"y":..,"z":..,
                          "ext": [..], "proc": "save_point",
                          "name": "Save Point", "kind": 2}, ... ],
      "followPoints":  [ {"id": "0x3805", "idx": 5, "x":..,"y":..,"z":..,"rot":..}, ... ],
      "placements":    [ {"file": "f008_002_000.FBN", "id": "0x0c18",
                          "x":..,"y":..,"z":..}, ... ],
      "warnings": []
    }, ...
  }
}

Usage: python build_overworld_catalog.py [--no-placements]
"""
import json
import re
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent          # database/tools
DB = HERE.parent                                 # database/
PACK = DB / "extract" / "data_e" / "field" / "pack"
TABLE = DB / "extract" / "data_main" / "field" / "table"
OUT = DB / "overworld_catalog.json"

sys.path.insert(0, str(HERE))
from AreaArcUnpack import walk            # noqa: E402
import decode_hbn                         # noqa: E402

# (major, minor) -> area name, ported from FieldTracker.GetAreaName (the
# in-game banner names). Used to resolve exit destinations.
AREA_NAMES = {
    (6, 1): "School, Front Entrance", (6, 3): "School Hallway",
    (6, 4): "School Hallway, Second Floor", (6, 5): "School Courtyard",
    (6, 6): "Classroom 2-2", (6, 7): "Classroom 2-1", (6, 8): "Classroom 2-3",
    (6, 10): "School Library", (6, 11): "School Gym",
    (6, 12): "Home Economics Room", (6, 13): "Practice Building",
    (6, 14): "School Rooftop", (6, 15): "School Gate",
    (7, 1): "Dojima Residence", (7, 2): "Dojima Residence, Living Room",
    (7, 3): "Your Room", (7, 4): "Dojima Residence, Hallway",
    (8, 1): "Shopping District North", (8, 2): "Shopping District South",
    (8, 3): "Souzai Daigaku", (8, 4): "Daidara, Outside",
    (8, 5): "Daidara, Inside", (8, 6): "Shopping District Side Alley",
    (8, 9): "Tatsuhime Shrine",
    (9, 1): "Junes Food Court", (9, 2): "Junes Entrance",
    (9, 3): "Junes Electronics", (9, 4): "Junes West Side",
    (10, 1): "Samegawa Flood Plain", (10, 2): "Samegawa Riverbank",
    # AUDIT 2026-06-12: 11 = Okina City (city_* procs), 18 = ski-trip area
    # (entrance_lodge/snowmountain_*), 17 = farm (farmshop proc); the shrine
    # is 8/9 and Moel is inside the shopping district map.
    (11, 0): "Okina City", (12, 0): "Shiroku Store",
    (13, 0): "Daidara Metalworks",
    (15, 0): "Hanamura Residence", (17, 0): "Farm area",
    (18, 1): "Ski Lodge", (18, 0): "Snow Mountain",
}


def area_name(maj, mino):
    return AREA_NAMES.get((maj, mino)) or AREA_NAMES.get((maj, 0)) \
        or f"area {maj}-{mino}"


# Known proc -> friendly English label. Anything not here falls back to the
# romaji proc name so nothing is ever nameless. Extend freely.
# NOTE: the call_* family are AREA TRANSITIONS (exits) — their destination is
# resolved from CALL_FIELD in the decompiled flow (see resolve_exits); the
# entries here are only fallbacks for call_* procs without a parsable body.
FRIENDLY = {
    "equip_shop": "Daidara Metalworks",
    "item_shop": "Shiroku Store",
    "velvet_room": "Velvet Room",
    "basutei": "Bus Stop",
    "bookstore": "Yomenaido Bookstore",
    "bookstore_poster": "Bookstore poster",
    "tyuuka": "Aiya Chinese Diner",
    "save_point": "Save Point",
    "tatumiya": "Tatsumi Textiles",
    "touhuya": "Marukyu Tofu",
    "jinjya": "Tatsuhime Shrine",
    "jinjya_saisenbako": "Shrine offering box",
    "jinjya_yashiro": "Shrine main hall",
    "jinjya_ema": "Shrine ema plaques",
    "jinjya_mikuzikake": "Shrine fortune",
    "sakaya": "Konishi Liquors",
    "souzai": "Souzai Daigaku",
    "post": "Mailbox",
    "post2": "Mailbox",
    "jihan": "Vending machine",
    "jihan2": "Vending machine",
    "jihanatari": "Vending machines",
    "mokei": "Model shop",
    "yakuten": "Pharmacy",
    "denki": "Electronics store",
    "tokoya": "Barber shop",
    "gatyagatya": "Capsule machine",
    "keijiban": "Bulletin board",
    # call_lmap behaviour is DATE-GATED: on story days it runs the main-quest
    # prompt ("Go to Junes?"), otherwise it opens the town travel map. The
    # destination is announced by the game's own dialog, so the label stays
    # generic.
    "call_lmap": "Street exit",
    # 2026-09-30 (nav pt2 item 8): the Shrine's bug-catching spot (checks the three bug nets)
    "musi_ap_shrine": "Bug-catching spot",
    "parttime_teacher": "Part-time job: tutoring",
    "parttime_hospital": "Part-time job: hospital",
    "parttime_junes": "Part-time job: Junes",
    "kouban": "Police box",
    "gas_station": "Moel Gas Station",
}


FLOWS = DB / "extract" / "bf_per_area"
_flow_cache: dict = {}


def flow_proc_bodies(major: int):
    """proc name -> body text from the major's decompiled flow (first sub)."""
    if major in _flow_cache:
        return _flow_cache[major]
    bodies = {}
    for f in sorted(FLOWS.glob(f"fd{major:03}_*.flow")):
        text = f.read_text(errors="replace")
        for m in re.finditer(r"^void (\w+)\(\)\s*\{(.*?)^\}", text,
                             re.M | re.S):
            bodies[m.group(1)] = m.group(2)
        break
    _flow_cache[major] = bodies
    return bodies


def resolve_exit_name(major: int, proc: str):
    """For call_* transition procs, derive 'Exit to <area>' from the first
    CALL_FIELD in the proc body (the user-visible destination)."""
    if proc == "call_lmap":
        return FRIENDLY["call_lmap"]
    body = flow_proc_bodies(major).get(proc, "")
    m = re.search(r"CALL_FIELD\((\d+),\s*(\d+),\s*(\d+)", body)
    if m:
        return f"Exit to {area_name(int(m.group(1)), int(m.group(2)))}"
    return FRIENDLY.get(proc, proc)


def bf_proc_names(bf: bytes):
    """Return {proc_index: name} from a FLW0 BF's type-0 label section."""
    if bf[8:12] != b"FLW0":
        return {}
    names = {}
    # section descriptors start at 0x10, 16 bytes each:
    # (u32 type, u32 elemsize, u32 count, u32 offset)
    for i in range(8):
        base = 0x10 + i * 16
        if base + 16 > len(bf):
            break
        t, esz, cnt, off = struct.unpack_from("<4I", bf, base)
        if t == 0 and esz == 32 and 0 < cnt < 4096 and 0 < off < len(bf):
            for p in range(cnt):
                e = bf[off + p * esz: off + (p + 1) * esz]
                if len(e) < esz:
                    break
                names[p] = e[:24].split(b"\0")[0].decode("ascii", "replace")
            return names
    return names


# ── The game's own CHECK-prompt labels (2026-09-29) ──────────────────────────
# h-row u16 @+0x0E ("a") is the LABEL ID; the text lives in P4G.exe: 9 per-language
# pointer tables at 0x140910630 (lang id 0 jp · 1 en · … 8 es), each entry a char* to a
# glyph-encoded C string (English = ASCII). Id 0 = no label (town-map exits, triggers).
# The mod resolves labelId at RUNTIME in the game's language; "gameLabel" here is the
# English text for tooling/logs only.
EXE_PATH = Path(__file__).resolve().parents[3] / "P4G.exe"
_EXE = None


def _exe_label_en(label_id: int):
    global _EXE
    if not label_id:
        return None
    if _EXE is None:
        d = EXE_PATH.read_bytes()
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        nsec = struct.unpack_from("<H", d, pe + 6)[0]
        optsz = struct.unpack_from("<H", d, pe + 20)[0]
        base = struct.unpack_from("<Q", d, pe + 24 + 24)[0]
        secs, o = [], pe + 24 + optsz
        for _ in range(nsec):
            vsz, va, rsz, raw = struct.unpack_from("<IIII", d, o + 8)
            secs.append((va, vsz, raw, rsz)); o += 40
        _EXE = (d, base, secs)
    d, base, secs = _EXE

    def off(v):
        r = v - base
        for va, vsz, raw, rsz in secs:
            if va <= r < va + max(vsz, rsz) and r - va < rsz:
                return raw + r - va
        return None
    tbl = struct.unpack_from("<Q", d, off(0x140910630 + 1 * 8))[0]
    o = off(tbl + label_id * 8)
    if o is None:
        return None
    p = struct.unpack_from("<Q", d, o)[0]
    po = off(p)
    if po is None:
        return None
    raw = d[po:d.find(bytes(1), po)]
    try:
        t = raw.decode("ascii")
    except UnicodeDecodeError:
        return None
    return t if t and all(32 <= ord(c) < 127 for c in t) else None


def parse_h_rows(d: bytes):
    """h*.bin rows: 44 bytes; GATE i32[3]@0, type u16@12 (0x0201), A u16@14, procIdx u16@16,
    kind u32@32.
    GATE (2026-09-29): -1,-1,-1 = always live; otherwise gate[0] is a FlowScript BIT id and the trigger
    is live only while that bit is ON (live-proven on slot 10: Calendar 3240=1 live; Ema/Fortune Hanger
    3263=0 and fld_debug_menu 3403=0 dead; Velvet Room 3239=1 and Street exit 1 3243=1 live). The
    3-value rows (Cushion 3254,12,1621) have unknown semantics — the mod treats them as live."""
    rows = []
    for i in range(len(d) // 44):
        r = d[i * 44: (i + 1) * 44]
        gate = list(struct.unpack_from("<3i", r, 0))
        a, proc = struct.unpack_from("<2H", r, 14)
        kind, = struct.unpack_from("<I", r, 32)
        rows.append({"a": a, "proc": proc, "kind": kind, "gate": gate})
    return rows


def parse_fbn(blob: bytes, fname: str):
    if len(blob) < 0x18 or struct.unpack_from("<I", blob, 0)[0] != 0x12345678:
        return []
    _, _, count, rsz = struct.unpack_from("<4I", blob, 0)
    out = []
    for i in range(count):
        base = 0x18 + i * rsz
        if base + 0x5C > len(blob):
            break
        uid, = struct.unpack_from("<I", blob, base + 8)
        x, y, z = struct.unpack_from("<3f", blob, base + 0x50)
        out.append({"file": fname, "id": f"{uid:#06x}",
                    "x": round(x, 2), "y": round(y, 2), "z": round(z, 2)})
    return out


def name_town_map_exits(areas):
    """2026-09-30 (nav pt2 item 8): every call_lmap trigger opens the TOWN MAP ("Leave the shopping
    district?" -> the map), so "Street exit 1/2/3" told the player nothing. Name each by the end of the
    area it sits at: the compass direction from the area's interactable centroid, in the OVERWORLD
    frame (+X = east, +Z = south — OverworldNav.WorldDirection). Duplicates get a number."""
    for key, area in areas.items():
        items = area.get("interactables", [])
        maps = [i for i in items if i.get("proc") == "call_lmap"]
        if not maps:
            continue
        pts = [(i["x"], i["z"]) for i in items if i.get("proc")]
        cx = sum(p[0] for p in pts) / len(pts)
        cz = sum(p[1] for p in pts) / len(pts)
        for i in maps:
            dx, dz = i["x"] - cx, i["z"] - cz
            ns = "south" if dz > 0 else "north"
            ew = "east" if dx > 0 else "west"
            if abs(dz) > abs(dx) * 2:
                d = ns
            elif abs(dx) > abs(dz) * 2:
                d = ew
            else:
                d = ns + ew
            i["name"] = "Town map" if len(maps) == 1 else f"Town map, {d} end"
        seen = {}
        for i in maps:
            seen.setdefault(i["name"], []).append(i)
        for nm, lst in seen.items():
            if len(lst) > 1:
                for n, i in enumerate(lst, 1):
                    i["name"] = f"{nm} {n}"


def main():
    with_placements = "--no-placements" not in sys.argv
    areas = {}
    arc_count = 0
    for arc in sorted(PACK.glob("fd*.arc")):
        data = arc.read_bytes()
        entries = {n: (s, sz) for n, s, sz in walk(data)}
        arc_count += 1

        # the major BF carries all procedure names
        bf_entry = next((n for n in entries if re.fullmatch(r"f\d+\.bf", n)), None)
        procs = {}
        if bf_entry:
            s, sz = entries[bf_entry]
            procs = bf_proc_names(data[s:s + sz])

        for name, (s, sz) in entries.items():
            m = re.fullmatch(r"f(\d+)_(\d+)\.HBN", name)
            if not m:
                continue
            maj, mino = int(m.group(1)), int(m.group(2))
            key = f"{maj:03}_{mino:03}"
            area = areas.setdefault(key, {
                "source": arc.name, "interactables": [],
                "followPoints": [], "placements": [], "warnings": []})

            blobs = []
            blob_bytes = data[s:s + sz]
            off = 0
            while off + 8 <= len(blob_bytes):
                b, off2 = decode_hbn.parse_blob(blob_bytes, off)
                if b is None:
                    break
                blobs.append(b)
                off = off2

            boxes, special, points, extra_pts, zones = [], [], [], [], []
            for b in blobs:
                for sec in b.get("sections", []):
                    # section 0 = interaction trigger boxes; idx >= 1000
                    # (0x37fe/0x37ff) are boundary/system triggers, NOT h-bound.
                    # section 1 = follower points (cat 14); sections 2/3 =
                    # cat-15/16 point families (semantics TBD); section 5 =
                    # large zone volumes (camera/audio regions).
                    if sec["section"] == 0 and sec["record_size"] == 0x20:
                        for r in sec["records"]:
                            (special if r["idx"] >= 1000 else boxes).append(r)
                    elif sec["section"] == 1 and sec["record_size"] == 0x14:
                        points.extend(sec["records"])
                    elif sec["record_size"] == 0x14:
                        extra_pts.extend(sec["records"])
                    elif sec["record_size"] == 0x20:
                        zones.extend(sec["records"])

            # pair with the h registry (prefer in-arc copy, fall back to table/)
            hname = f"h{maj:03}_{mino:03}.bin"
            if hname in entries:
                hs, hsz = entries[hname]
                hdata = data[hs:hs + hsz]
            else:
                hp = TABLE / hname
                hdata = hp.read_bytes() if hp.exists() else b""
            rows = parse_h_rows(hdata) if hdata else []

            live_rows = [r for r in rows if r["proc"] != 0xFFFF]
            if live_rows and len(live_rows) != len(boxes):
                area["warnings"].append(
                    f"h-rows ({len(live_rows)}) != normal boxes ({len(boxes)})")
            lmap_total = sum(1 for r in rows if r["proc"] != 0xFFFF
                             and procs.get(r["proc"]) == "call_lmap")
            lmap_seen = 0
            for i, box in enumerate(boxes):
                row = rows[i] if i < len(rows) else None
                if row and row["proc"] == 0xFFFF:
                    row = None
                proc_name = procs.get(row["proc"]) if row else None
                if proc_name and proc_name.startswith("call_"):
                    label = resolve_exit_name(maj, proc_name)
                    # several town-map prompts per street: number them so the
                    # browser entries are distinguishable
                    if proc_name == "call_lmap" and lmap_total > 1:
                        lmap_seen += 1
                        label = f"{label} {lmap_seen}"
                else:
                    label = FRIENDLY.get(proc_name, proc_name)
                area["interactables"].append({
                    "box": i, "id": f"{box['id']:#06x}",
                    "x": round(box["x"], 2), "y": round(box["y"], 2),
                    "z": round(box["z"], 2),
                    "ext": [round(e, 2) for e in box.get("ext", [])],
                    "proc": proc_name,
                    "name": label,
                    "kind": row["kind"] if row else None,
                    "labelId": row["a"] if row else 0,
                    "gameLabel": _exe_label_en(row["a"]) if row else None,
                    **({"gate": row["gate"]} if row and row["gate"] != [-1, -1, -1] else {}),
                })
            for box in special:
                area["interactables"].append({
                    "box": None, "id": f"{box['id']:#06x}",
                    "x": round(box["x"], 2), "y": round(box["y"], 2),
                    "z": round(box["z"], 2),
                    "ext": [round(e, 2) for e in box.get("ext", [])],
                    "proc": None, "name": "(boundary trigger)",
                    "kind": None,
                })
            for pt in extra_pts:
                area.setdefault("extraPoints", []).append({
                    "id": f"{pt['id']:#06x}", "cat": pt["cat"], "idx": pt["idx"],
                    "x": round(pt["x"], 2), "y": round(pt["y"], 2),
                    "z": round(pt["z"], 2), "rot": round(pt.get("rot", 0), 1)})
            for pt in points:
                area["followPoints"].append({
                    "id": f"{pt['id']:#06x}", "idx": pt["idx"],
                    "x": round(pt["x"], 2), "y": round(pt["y"], 2),
                    "z": round(pt["z"], 2), "rot": round(pt.get("rot", 0), 1)})
            for zn in zones:
                area.setdefault("zones", []).append({
                    "id": f"{zn['id']:#08x}",
                    "x": round(zn["x"], 2), "y": round(zn["y"], 2),
                    "z": round(zn["z"], 2),
                    "ext": [round(e, 2) for e in zn.get("ext", [])]})

        if with_placements:
            for name, (s, sz) in entries.items():
                m = re.fullmatch(r"f(\d+)_(\d+)_(\d+)\.FBN", name)
                if not m:
                    continue
                key = f"{int(m.group(1)):03}_{int(m.group(2)):03}"
                area = areas.setdefault(key, {
                    "source": arc.name, "interactables": [],
                    "followPoints": [], "placements": [], "warnings": []})
                area["placements"].extend(parse_fbn(data[s:s + sz], name))

    name_town_map_exits(areas)
    n_int = sum(len(a["interactables"]) for a in areas.values())
    n_named = sum(1 for a in areas.values() for i in a["interactables"] if i["proc"])
    n_pts = sum(len(a["followPoints"]) for a in areas.values())
    n_pl = sum(len(a["placements"]) for a in areas.values())
    warn = [(k, w) for k, a in areas.items() for w in a["warnings"]]
    OUT.write_text(json.dumps({"areas": areas}, indent=1))
    print(f"{arc_count} arcs -> {len(areas)} areas")
    print(f"interactables: {n_int} ({n_named} with proc names)")
    print(f"followPoints: {n_pts}, placements: {n_pl}")
    print(f"warnings: {len(warn)}")
    for k, w in warn[:20]:
        print(f"  {k}: {w}")
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
