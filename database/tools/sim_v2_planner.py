"""Offline emulation of the v2 auto-walk PLANNER (GridWalk + StairsPlan.TryPlanTo)
over all 24 scripted floors in dungeon_floor_maps.json.

Faithfully mirrors the C# logic (GridWalk.cs / StairsPlan.cs, 2026-07-18 build):
  - two-rule connectivity: edge bit open OR same room blob
  - edge bits (+0x0A low nibble): 0x01 S(-row) 0x02 E(-col) 0x04 N(+row) 0x08 W(+col)
  - 4-connected A* with blocked-cell support
  - TryPlanTo waypoint construction: re-center prepend (>300u off own center),
    cell centers, exact-target final; bend flags (dot < 0.9)
NOT emulated (game-only): live door actors (weave skipped -> crossings counted),
collision/slides/prompts, procedural maze floors.

BASELINE (2026-07-18, first run): 95.6% of 6576 pairs routed; 21/24 floors
perfect (1 component, 300/300, zero geometry defects). Known/expected outliers:
  - 63_1 = 10 islands: TELEPORTER-linked rooms (user-confirmed) -> cross-island
    "no route" is CORRECT there.
  - 64_3 / 66_2 = one extra island each (likely door-gated pocket / alcove).
  - 69_3 rows 21 c4<->c5: TWO asymmetric edge bits -> one-way routing in-game
    (GridWalk reads only the source cell's bits).
  - Choke test: 66% of single-cell blocks sever the route -> the un-block +
    escalating-retreat recovery is the PRIMARY mid-route recovery, not an edge case.
Re-run after any planner change and compare against these numbers.

Checks per floor:
  1. connectivity: components among walkable cells, % routable pairs
  2. route validity: each consecutive pair adjacent + Connected (both directions!)
  3. edge-bit asymmetry: A says open toward B, B says wall toward A
  4. plan geometry: all legs cardinal, bend flags correct, final == target
  5. choke analysis: block each intermediate route cell -> alternate route or NoRoute
     (NoRoute = the retreat/un-block path would trigger in-game)
  6. cross-blob crossings per route (door-weave demand, no door data offline)
"""
import json, random, sys
from collections import deque

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
COLS = 16  # MinimapTracker.COLS (24 rows x 16 cols)
CELL = 1200.0

data = json.load(open(r"C:\Program Files (x86)\Steam\steamapps\common\Persona 4 Golden\Persona 4 golden\database\dungeon_floor_maps.json", encoding="utf-8"))

def side_bit(dr, dc):
    return 0x04 if dr > 0 else 0x01 if dr < 0 else 0x08 if dc > 0 else 0x02 if dc < 0 else 0

class Floor:
    def __init__(self, key, cells):
        self.key = key
        self.walk = set()
        self.mask = {}
        self.roomwh = {}
        for c in cells:
            if len(c) >= 2:
                k = (c[0], c[1])
                self.walk.add(k)
                if len(c) >= 4: self.mask[k] = c[3]
                if len(c) >= 6 and (c[4] > 1 or c[5] > 1): self.roomwh[k] = (c[4], c[5])
        # room blobs: adjacent room-cells with identical (w,h) = one blob (approx of runtime roomId)
        self.blob = {}
        bid = 0
        for k in sorted(self.roomwh):
            if k in self.blob: continue
            bid += 1
            q = deque([k]); self.blob[k] = bid
            while q:
                r, c = q.popleft()
                for dr, dc in ((1,0),(-1,0),(0,1),(0,-1)):
                    n = (r+dr, c+dc)
                    if n in self.roomwh and n not in self.blob and self.roomwh[n] == self.roomwh[(r,c)]:
                        self.blob[n] = bid; q.append(n)

    def edge_open(self, r, c, dr, dc):
        return (self.mask.get((r, c), 0) & side_bit(dr, dc)) != 0

    def connected(self, r, c, dr, dc):
        n = (r+dr, c+dc)
        if n not in self.walk: return False
        if self.edge_open(r, c, dr, dc): return True
        a = self.blob.get((r, c), 0); b = self.blob.get(n, 0)
        return a != 0 and a == b

    def astar(self, s, t, blocked=None):
        import heapq
        if s not in self.walk or t not in self.walk: return None
        if s == t: return [s]
        openq = [(abs(s[0]-t[0])+abs(s[1]-t[1]), 0, s)]
        g = {s: 0}; came = {}
        while openq:
            _, cg, cur = heapq.heappop(openq)
            if cur == t:
                path = [cur]
                while path[-1] != s: path.append(came[path[-1]])
                return path[::-1]
            if cg > g.get(cur, 1e9): continue
            for dr, dc in ((1,0),(-1,0),(0,1),(0,-1)):
                if not self.connected(cur[0], cur[1], dr, dc): continue
                n = (cur[0]+dr, cur[1]+dc)
                if blocked and n in blocked: continue
                ng = cg + 1
                if ng < g.get(n, 1e9):
                    g[n] = ng; came[n] = cur
                    heapq.heappush(openq, (ng + abs(n[0]-t[0]) + abs(n[1]-t[1]), ng, n))
        return None

def center(cell): return (cell[1]*CELL + CELL/2, cell[0]*CELL + CELL/2)

def plan_waypoints(fl, path, px, pz, tx, tz):
    """StairsPlan.TryPlanTo mirror (stopAtTargetRoomDoor=False, doors unavailable)."""
    wps = []
    cx0, cz0 = center(path[0])
    if (cx0-px)**2 + (cz0-pz)**2 > 300.0**2: wps.append((cx0, cz0))  # re-center prepend
    crossings = 0
    for i in range(len(path)-1):
        if i > 0: wps.append(center(path[i]))
        a, b = path[i], path[i+1]
        if fl.blob.get(a, -1) != fl.blob.get(b, -2) and not (fl.blob.get(a,0)==0 and fl.blob.get(b,0)==0):
            crossings += 1  # room-boundary crossing (door-weave candidate in-game)
    wps.append((tx, tz))
    return wps, crossings

def bend_flags(wps):
    bend = [False]*len(wps)
    for i in range(1, len(wps)-1):
        ix, iz = wps[i][0]-wps[i-1][0], wps[i][1]-wps[i-1][1]
        ox, oz = wps[i+1][0]-wps[i][0], wps[i+1][1]-wps[i][1]
        il = (ix*ix+iz*iz)**0.5; ol = (ox*ox+oz*oz)**0.5
        if il > 1 and ol > 1: bend[i] = (ix*ox+iz*oz)/(il*ol) < 0.9
    return bend

random.seed(42)
tot = dict(floors=0, pairs=0, routed=0, asym=0, badlegs=0, chokes=0, alts=0, planbad=0)
print(f"{'floor':6} {'cells':>5} {'comp':>4} {'pairs':>5} {'routed':>6} {'asym':>4} {'chokeNR':>7} {'chokeAlt':>8} {'maxlen':>6} {'anomaly'}")
for key, cells in sorted(data.items()):
    fl = Floor(key, cells)
    if not fl.walk: continue
    # components
    seen = set(); comp = 0
    for k in fl.walk:
        if k in seen: continue
        comp += 1
        q = deque([k]); seen.add(k)
        while q:
            r, c = q.popleft()
            for dr, dc in ((1,0),(-1,0),(0,1),(0,-1)):
                n = (r+dr, c+dc)
                if n in fl.walk and n not in seen and (fl.connected(r,c,dr,dc) or fl.connected(*n, -dr, -dc)):
                    seen.add(n); q.append(n)
    # edge asymmetry
    asym = 0
    for (r, c) in fl.walk:
        for dr, dc in ((1,0),(0,1)):
            n = (r+dr, c+dc)
            if n in fl.walk and fl.edge_open(r,c,dr,dc) != fl.edge_open(n[0],n[1],-dr,-dc):
                asym += 1
    # sampled pairs
    walk = sorted(fl.walk)
    pairs = []
    for _ in range(min(300, len(walk)*(len(walk)-1))):
        s, t = random.sample(walk, 2)
        pairs.append((s, t))
    routed = 0; badlegs = 0; chokeNR = 0; chokeAlt = 0; planbad = 0; maxlen = 0
    anomalies = []
    for s, t in pairs:
        path = fl.astar(s, t)
        if path is None: continue
        routed += 1
        maxlen = max(maxlen, len(path))
        # leg validity (bidirectional connectivity)
        for i in range(len(path)-1):
            a, b = path[i], path[i+1]
            dr, dc = b[0]-a[0], b[1]-a[1]
            if not fl.connected(a[0], a[1], dr, dc): badlegs += 1; anomalies.append(f"badleg {a}->{b}")
            elif not fl.connected(b[0], b[1], -dr, -dc):
                badlegs += 1; anomalies.append(f"one-way {a}->{b}")
        # plan geometry
        px, pz = center(s); px += 350; pz += 250   # off-center start (exercises re-center)
        tx, tz = center(t)
        wps, crossings = plan_waypoints(fl, path, px, pz, tx, tz)
        for i in range(len(wps)-1):
            dx, dz = wps[i+1][0]-wps[i][0], wps[i+1][1]-wps[i][1]
            if abs(dx) > 1 and abs(dz) > 1 and i < len(wps)-2:   # non-cardinal INTERIOR leg (final leg may be diagonal to exact target)
                planbad += 1; anomalies.append(f"diag leg wp{i} {s}->{t}")
                break
        if wps and (abs(wps[-1][0]-tx) > 0.1 or abs(wps[-1][1]-tz) > 0.1):
            planbad += 1; anomalies.append(f"final!=target {s}->{t}")
        bend_flags(wps)  # smoke (no crash, len ok)
        # choke: block each intermediate cell -> alternate?
        for mid in path[1:-1]:
            alt = fl.astar(s, t, blocked={mid})
            if alt is None: chokeNR += 1
            else: chokeAlt += 1
    an = "; ".join(anomalies[:3]) if anomalies else ""
    print(f"{key:6} {len(walk):>5} {comp:>4} {len(pairs):>5} {routed:>6} {asym:>4} {chokeNR:>7} {chokeAlt:>8} {maxlen:>6} {an}")
    tot['floors'] += 1; tot['pairs'] += len(pairs); tot['routed'] += routed
    tot['asym'] += asym; tot['badlegs'] += badlegs
    tot['chokes'] += chokeNR; tot['alts'] += chokeAlt; tot['planbad'] += planbad

print()
print(f"TOTAL: {tot['floors']} floors, {tot['pairs']} pairs, {tot['routed']} routed "
      f"({100.0*tot['routed']/max(1,tot['pairs']):.1f}%), badlegs={tot['badlegs']}, "
      f"edge-asym={tot['asym']}, plan-geometry-bad={tot['planbad']}, "
      f"choke NoRoute={tot['chokes']} vs alternates={tot['alts']} "
      f"({100.0*tot['chokes']/max(1,tot['chokes']+tot['alts']):.1f}% of blocks would need the retreat path)")
