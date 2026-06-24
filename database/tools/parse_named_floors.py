"""
Parse the named-floor door/box procs in dungeon.flow into structured rows.

For door scripts (e.g. castle_05F_door):
  each `if/else if (var?? == DOOR_ID)` branch is a door. We capture:
    - door_id
    - BIT_CHK / BIT_ON / BIT_OFF flags inside that branch
    - whether CALL_BATTLE(N, M) appears (boss door, with battle id)
    - whether MSG(DOOR_LOCK) / MSG(LOCK_BOX) appears (locked-door semantics)
    - whether GET_KEY is mentioned (door yields a key on first interaction)
    - whether CALL_DUNGEON(N, M) appears (door teleports to another floor)

For box scripts (e.g. box_open_010_1):
  one chest each. We capture:
    - chest internal id (sVar3 == N from dng_tbox call site, recovered from grep)
    - any SET_ITEM / SET_YEN / item slot references
    - BITs touched

Output: a single dungeon_named_floor_map.json with two tables: doors[], boxes[].
"""
import re, json
from pathlib import Path

ROOT = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/extract/dungeon_scripts")
FLOW = (ROOT / "dungeon.flow").read_text(encoding="utf-8")

# floor_id -> proc-name mapping, harvested from dng_door's dispatch table
FLOOR_TO_DOOR_PROC = {
    10:  "castle_05F_door",
    23:  "sauna_03F_door",
    27:  "sauna_07F_door",
    43:  "playhouse_03F_door",
    47:  "playhouse_07F_door",
    67:  "gameworld_07F_door",
    84:  "base_04F_door",
    86:  "base_06F_door",
    104: "heaven_04F_door",
    107: "heaven_07F_door",
    122: "city_02F_door",
    125: "city_05F_door",
    128: "city_08F_door",  # 0x80
    143: "last_03F_door",
    146: "last_06F_door",
}

# Reverse: get proc name -> floor id
PROC_TO_FLOOR = {v: k for k, v in FLOOR_TO_DOOR_PROC.items()}

# floor_id -> { chest sVar3 id : box_open_proc_name }, recovered from dng_tbox
# (sVar3 values are the per-floor chest internal IDs)
FLOOR_TO_BOX = {
    10:  {11262: "box_open_010_1", 11261: "box_open_010_2"},
    23:  {11262: "box_open_023_1", 11260: "box_open_023_2"},
    67:  {11261: "box_open_067"},
    84:  {11261: "box_open_084"},
    86:  {11261: "box_open_086"},
    161: {11262: "box_open_161_1", 11261: "box_open_161_2",
          11260: "box_open_161_3", 11259: "box_open_161_4"},
}

# floor_id -> per-chest BIT_CHK flags from dng_tbox()'s skip-if-opened gates
FLOOR_CHEST_BITS = {
    (10,  11262): 3691, (10,  11261): 3692,
    (23,  11262): 3716, (23,  11260): 3717,
    (67,  11261): 3780,
    (84,  11261): 3810,
    (86,  11261): 3809,
    (161, 11262): 3920, (161, 11261): 3921,
    (161, 11260): 3922, (161, 11259): 3923,
}


def extract_proc(name):
    """Pull the body of a top-level `void <name>() { ... }` from FLOW."""
    m = re.search(rf"^void {re.escape(name)}\s*\(\)\s*\{{", FLOW, re.M)
    if not m:
        return None
    i = m.end() - 1  # the '{'
    depth = 0
    start = i
    while i < len(FLOW):
        ch = FLOW[i]
        if ch == '{': depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                return FLOW[start:i+1]
        i += 1
    return None


def split_branches(body):
    """Split an `if/else if` chain into (door_ids, branch_text) tuples,
    where door_ids is the list of numeric values matched by `var?? == N` in
    the if's CONDITION (handles OR chains like `(var == A) || (var == B)`).

    Only top-level branches are considered — we track brace depth from the
    proc body's opening brace, so inner `if (var40 == 1)` checks don't leak
    into the door table.
    """
    # find the proc's own opening brace and start scanning after it
    first_brace = body.find('{')
    if first_brace < 0:
        return []
    depth = 0   # depth of brace nesting RELATIVE TO proc-body interior
    branches = []
    cur_start = None
    cur_door_ids = None
    i = first_brace + 1
    while i < len(body):
        ch = body[i]
        # Detect a top-level if/else-if header only when depth == 0.
        if depth == 0:
            m = re.match(r"\s*(?:if|else\s+if)\s*\(", body[i:])
            if m:
                # capture the condition up to the matching ')'
                cond_start = i + m.end()
                paren_depth = 1
                j = cond_start
                while j < len(body) and paren_depth:
                    if body[j] == '(': paren_depth += 1
                    elif body[j] == ')': paren_depth -= 1
                    j += 1
                cond = body[cond_start:j-1]
                # find every `var?? == NUMBER` in the condition
                door_ids = [int(x) for x in re.findall(r"var\d+\s*==\s*(\d+)", cond)]
                # door IDs in P4G look like 4-5 digit numbers (10240, 10248, ...) —
                # filter out 0/1 false positives from boolean checks
                door_ids = [d for d in door_ids if d > 100]
                if door_ids:
                    # close the previous branch
                    if cur_start is not None:
                        branches.append((cur_door_ids, body[cur_start:i]))
                    cur_start = i
                    cur_door_ids = door_ids
                else:
                    # a top-level if with no door check (e.g. an outer state guard) —
                    # leave depth tracking alone and keep walking inside it
                    pass
                i = j
                continue
        if ch == '{': depth += 1
        elif ch == '}':
            depth -= 1
            if depth < 0:
                break  # past the proc body
        i += 1
    if cur_start is not None:
        branches.append((cur_door_ids, body[cur_start:i]))
    return branches


def summarise_branch(text):
    """Extract the actions we care about from a single door branch."""
    out = {}
    bit_chk = re.findall(r"BIT_CHK\((\d+)\)\s*==\s*(\d)", text)
    bit_on  = re.findall(r"BIT_ON\((\d+)\)", text)
    bit_off = re.findall(r"BIT_OFF\((\d+)\)", text)
    call_battle = re.findall(r"CALL_BATTLE\((\d+),\s*(\d+)\)", text)
    call_dungeon = re.findall(r"CALL_DUNGEON\((\d+),\s*(\d+)\)", text)
    msgs = re.findall(r"\bMSG\(\s*(\w+)\b", text)
    get_key = "GET_KEY" in text
    door_lock = "DOOR_LOCK" in msgs or "LOCK_BOX" in msgs
    if bit_chk:  out["bit_chk"]  = [{"flag": int(f), "expected": int(e)} for f, e in bit_chk]
    if bit_on:   out["bit_on"]   = [int(x) for x in bit_on]
    if bit_off:  out["bit_off"]  = [int(x) for x in bit_off]
    if call_battle:  out["call_battle"]  = [{"id": int(a), "arg": int(b)} for a, b in call_battle]
    if call_dungeon: out["call_dungeon"] = [{"floor": int(a), "arg": int(b)} for a, b in call_dungeon]
    if msgs:     out["msgs"]     = sorted(set(msgs))
    if get_key:  out["yields_key"] = True
    if door_lock: out["locked"] = True
    # classify
    if call_battle:    out["kind"] = "boss_door"
    elif call_dungeon: out["kind"] = "transit_door"
    elif "locked" in out: out["kind"] = "locked_door"
    else: out["kind"] = "scripted_door"
    return out


doors_rows = []
for floor_id, proc in FLOOR_TO_DOOR_PROC.items():
    body = extract_proc(proc)
    if not body:
        print(f"  missing: {proc}")
        continue
    for door_ids, branch in split_branches(body):
        # one row per door id, sharing the same branch summary
        summary = summarise_branch(branch)
        for door_id in door_ids:
            row = {"floor_id": floor_id, "proc": proc, "door_id": door_id}
            row.update(summary)
            doors_rows.append(row)


def summarise_box(floor_id, chest_id, proc):
    body = extract_proc(proc) or ""
    bit_on  = re.findall(r"BIT_ON\((\d+)\)", body)
    bit_off = re.findall(r"BIT_OFF\((\d+)\)", body)
    set_item = re.findall(r"SET_ITEM\(([^,]+),\s*([^)]+)\)", body)
    add_yen  = re.findall(r"ADD_YEN\(([^)]+)\)", body)
    msgs = re.findall(r"\bMSG\(\s*(\w+)\b", body)
    row = {"floor_id": floor_id, "proc": proc, "chest_internal_id": chest_id}
    skip_bit = FLOOR_CHEST_BITS.get((floor_id, chest_id))
    if skip_bit is not None:
        row["skip_if_bit"] = skip_bit
    if bit_on:   row["bit_on"]   = [int(x) for x in bit_on]
    if bit_off:  row["bit_off"]  = [int(x) for x in bit_off]
    if set_item: row["set_item"] = [{"slot_expr": a.strip(), "value_expr": b.strip()} for a, b in set_item]
    if add_yen:  row["add_yen_exprs"] = [a.strip() for a in add_yen]
    if msgs:     row["msgs"] = sorted(set(msgs))
    return row


box_rows = []
for floor_id, by_chest in FLOOR_TO_BOX.items():
    for chest_id, proc in by_chest.items():
        box_rows.append(summarise_box(floor_id, chest_id, proc))


out = {
    "source": "field/script/dungeon.bf (decompiled)",
    "named_doors": doors_rows,
    "named_boxes": box_rows,
}
out_path = ROOT.parent.parent / "dungeon_named_floor_map.json"
out_path.write_text(json.dumps(out, indent=2), encoding="utf-8")
print(f"Wrote {out_path}")
print(f"  doors: {len(doors_rows)}  boxes: {len(box_rows)}")

# quick per-floor summary
from collections import Counter
print("\ndoors per floor:")
for fid, n in sorted(Counter(d["floor_id"] for d in doors_rows).items()):
    print(f"  floor {fid:3d}: {n}")
print("\nboxes per floor:")
for fid, n in sorted(Counter(b["floor_id"] for b in box_rows).items()):
    print(f"  floor {fid:3d}: {n}")
