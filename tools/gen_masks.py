"""Armory Unbound - generate head-only skin masks for every melee weapon and tool (AJ 2026-11-02).

Reads the merged UL config (ConfigsDump/items.xml), finds each melee/tool item's model in Undead Legacy's bundles or the
game's items.bundle, and writes Skins/Masks/<item>.<texture>.png (white = skin, black = stock) plus a preview tile.
  HEAD rule  (axes, picks, hammers, sledges, shovels, wrenches, hoes, sickles, spears): the head is skinned; the handle
             stays stock; on axes/hatchets/sickles/cleavers the cutting edge stays stock too.
  BODY rule  (clubs, bats, maces, batons, pipes, rackets, knives, machetes, swords, ...): the main body piece is skinned;
             the grip (bottom GRIP of the length) and every separate add-on piece (nails, spikes, wire, wraps) stay stock.
Read-only on the game. Usage: python gen_masks.py <ConfigsDump/items.xml> <game dir> <out dir>
"""
import sys, os, re, math, json, collections, traceback
import xml.etree.ElementTree as ET
import numpy as np
import UnityPy
from PIL import Image, ImageDraw, ImageFont

ITEMS, GAME, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
# Optional flags: --only=<file with one item name per line>  --parts (numbered-pieces sheets, no masks)
#                 --overrides=<json> (default: mask_overrides.json next to this script)
FLAGS = {a.split("=", 1)[0]: (a.split("=", 1)[1] if "=" in a else True) for a in sys.argv[4:]}
ONLY = None
if "--only" in FLAGS:
    ONLY = {l.strip() for l in open(FLAGS["--only"], encoding="utf-8-sig") if l.strip()}
PARTS_MODE = "--parts" in FLAGS
OVR_PATH = FLAGS.get("--overrides", os.path.join(os.path.dirname(os.path.abspath(__file__)), "mask_overrides.json"))
# Per-item overrides (item -> {...}):
#   "rule":     force HEAD / CLUB / BLADE / KNUCKLE / HOOK
#   "skin":     piece numbers (from a --parts sheet) that get the skin; every other piece stays stock
#   "grip_end": "low" or "high" - which end of the long axis is the handle (the grip cut is taken from it)
#   "grip":     fraction of the length that is grip (default 0.30; 0 = no grip cut)
OVR = json.load(open(OVR_PATH, encoding="utf-8")) if os.path.exists(OVR_PATH) else {}
OVR = {k: v for k, v in OVR.items() if not k.startswith("_")}
MASKS = os.path.join(OUT, "masks"); PREV = os.path.join(OUT, "previews")
os.makedirs(MASKS, exist_ok=True); os.makedirs(PREV, exist_ok=True)
GRIP = 0.30                 # body rule: bottom 30% of the main piece's length is the grip
HEAD, CORE, EDGE = 0.28, 0.11, 0.18
FAMILY = ("DynamicMelee", "ULM_MeleeAoE", "Prospect", "DensityHoe")
SKIP = ("meleeHand", "Torch", "Flashlight", "Admin", "ulmMeleeAttack", "Zombie", "Animal")
HEAD_RX = re.compile(r"(?i)axe|hatchet|pick|hammer|sledge|shovel|spade|wrench|hoe|mattock|spear|trowel|fork|trident|boathook|glaive|lance|polearm|halberd")
HOOK_RX = re.compile(r"(?i)sickle|scythe")            # curved blade: everything past the handle is skinned
BLADE_RX = re.compile(r"(?i)knife|blade|machete|sword|katana|cleaver|dagger|saber|kunai|saw|zweihander|shiv|bayonet")
KNUCKLE_RX = re.compile(r"(?i)knuckle")
EDGE_RX = re.compile(r"(?i)axe|hatchet|sickle|scythe|cleaver")
ARMS_RX = re.compile(r"(?i)glove|hand_raw|armormod|handwarp|porch|arms?$|hands?$")
LOD_RX = re.compile(r"(?i)lod[1-9]")

# ---------------------------------------------------------------- items
root = ET.parse(ITEMS).getroot()
raw = {}
for it in root.findall("item"):
    p, a0 = {}, None
    for q in it.findall("property"):
        if q.get("class") == "Action0":
            for z in q.findall("property"):
                if z.get("name") == "Class": a0 = z.get("value")
        elif q.get("name"): p[q.get("name")] = q.get("value")
    raw[it.get("name")] = (p, a0)
def eff(n, k, seen=None):
    seen = seen or set()
    if n not in raw or n in seen: return None
    seen.add(n); p, a = raw[n]
    if k == "a0" and a: return a
    if k != "a0" and k in p: return p[k]
    e = p.get("Extends"); return eff(e, k, seen) if e else None
items = []
for n in raw:
    a = (eff(n, "a0") or "").split(",")[0].strip()
    if a not in FAMILY or any(s in n for s in SKIP): continue
    items.append((n, eff(n, "Meshfile") or "", eff(n, "Tags") or ""))

def locate(mesh):
    if mesh.startswith("#Mods/UndeadLegacy/") and "?" in mesh:
        b, pf = mesh.split("?", 1)
        return os.path.join(GAME, "Mods", *b[len("#Mods/"):].split("/")), pf
    if mesh.startswith("@:"):
        return os.path.join(GAME, "Data", "Addressables", "Standalone", "automatic_assets_other", "items.bundle"), os.path.splitext(os.path.basename(mesh))[0]
    return None, None

# ---------------------------------------------------------------- bundle helpers
envs, gos = {}, {}
def env_of(path):
    if path not in envs:
        print("loading", path, flush=True)
        e = UnityPy.load(path); envs[path] = e
        d = collections.defaultdict(list)
        for o in e.objects:
            if o.type.name == "GameObject":
                try: d[o.read().m_Name].append(o)
                except Exception: pass
        gos[path] = d
    return envs[path]
def comp(c): return c.component if hasattr(c, "component") else c
def tr_of(go):
    for c in go.m_Components:
        o = comp(c)
        if o.type.name in ("Transform", "RectTransform"): return o.read()
def quat_m(q):
    x, y, z, w = q.x, q.y, q.z, q.w
    return np.array([[1 - 2*(y*y + z*z), 2*(x*y - z*w), 2*(x*z + y*w)],
                     [2*(x*y + z*w), 1 - 2*(x*x + z*z), 2*(y*z - x*w)],
                     [2*(x*z - y*w), 2*(y*z + x*w), 1 - 2*(x*x + y*y)]])
def local_m(t):
    m = np.eye(4); s = t.m_LocalScale; p = t.m_LocalPosition
    m[:3, :3] = quat_m(t.m_LocalRotation) @ np.diag([s.x, s.y, s.z]); m[:3, 3] = [p.x, p.y, p.z]
    return m
def tex_name(mat):
    try:
        mm = mat.read()
        for k, v in mm.m_SavedProperties.m_TexEnvs:
            if k == "_MainTex" and v.m_Texture.path_id: return v.m_Texture.read().m_Name
    except Exception: pass
    return None

def collect(path, prefab):
    """All LOD0 render meshes under the prefab root, in prefab space: list of (V[n,3], T[n,2], [(tri idx x3, texname)])."""
    env_of(path)
    cands = gos[path].get(prefab, [])
    root_go = None
    for o in cands:
        g = o.read(); t = tr_of(g)
        if t is not None and not t.m_Father.path_id: root_go = g; break
    if root_go is None and cands: root_go = cands[0].read()
    if root_go is None: return None
    out = []
    def walk(t, M):
        g = t.m_GameObject.read()
        Mw = M @ local_m(t)
        if LOD_RX.search(g.m_Name) or ARMS_RX.search(g.m_Name):
            return
        mesh, mats = None, []
        for c in g.m_Components:
            o = comp(c); tn = o.type.name
            if tn == "MeshFilter":
                try: mesh = o.read().m_Mesh.read()
                except Exception: pass
            elif tn == "MeshRenderer":
                mats = o.read().m_Materials
            elif tn == "SkinnedMeshRenderer":
                r = o.read(); mats = r.m_Materials
                try: mesh = r.m_Mesh.read()
                except Exception: pass
        if mesh is not None and mats:
            V, T, F, sub = [], [], [], -1
            for ln in mesh.export().splitlines():
                p = ln.split()
                if not p: continue
                if p[0] == "v": V.append([float(x) for x in p[1:4]])
                elif p[0] == "vt": T.append([float(x) for x in p[1:3]])
                elif p[0] in ("g", "usemtl", "o") and p[0] != "o": sub += 1 if p[0] == "g" else 0
                elif p[0] == "f":
                    F.append(([int(v.split("/")[0]) - 1 for v in p[1:4]], [int(v.split("/")[1]) - 1 if len(v.split("/")) > 1 and v.split("/")[1] else -1 for v in p[1:4]], max(sub, 0)))
            if V and T:
                V = np.array(V); V[:, 0] *= -1   # UnityPy's OBJ export mirrors x; undo so the transforms line up
                V = (Mw @ np.c_[V, np.ones(len(V))].T).T[:, :3]
                names = [tex_name(m) for m in mats]
                tris = [(vi, ti, names[min(s, len(names) - 1)]) for (vi, ti, s) in F]
                out.append((V, np.array(T), tris))
        for ch in t.m_Children: walk(ch.read(), Mw)
    walk(tr_of(root_go), np.eye(4))
    return out

# ---------------------------------------------------------------- segmentation
def merge(parts):
    Vs, Ts, tris, off_v, off_t = [], [], [], 0, 0
    for V, T, tr in parts:
        Vs.append(V); Ts.append(T)
        for vi, ti, tx in tr: tris.append(([v + off_v for v in vi], [t + off_t if t >= 0 else -1 for t in ti], tx))
        off_v += len(V); off_t += len(T)
    return np.vstack(Vs), np.vstack(Ts), tris

def pieces(V, tris):
    key = {}; canon = np.zeros(len(V), int)
    for i, v in enumerate(np.round(V, 4)):
        canon[i] = key.setdefault(tuple(v), i)
    par = list(range(len(V)))
    def find(a):
        while par[a] != a: par[a] = par[par[a]]; a = par[a]
        return a
    for vi, _, _ in tris:
        a = find(canon[vi[0]])
        for v in vi[1:]: par[find(canon[v])] = a
    lab = np.array([find(canon[i]) for i in range(len(V))])
    return lab

def classify_head(V, n_axis, name):
    ext = V.max(0) - V.min(0); ax = int(np.argmax(ext))
    s = (V[:, ax] - V[:, ax].min()) / max(ext[ax], 1e-6)
    oth = [i for i in range(3) if i != ax]
    lo_w = np.ptp(V[s < 0.15][:, oth], axis=0).max() if (s < 0.15).any() else 0
    hi_w = np.ptp(V[s > 0.85][:, oth], axis=0).max() if (s > 0.85).any() else 0
    if lo_w > hi_w: s = 1 - s
    head0 = s > 1 - HEAD
    hv = V[head0]
    bx = oth[int(np.argmax(np.ptp(hv[:, oth], axis=0)))]
    band = V[(s > 0.3) & (s < 0.6)][:, bx]
    if len(band):
        pad = (band.max() - band.min()) * 0.15
        outside = (V[:, bx] < band.min() - pad) | (V[:, bx] > band.max() + pad)
    else:
        outside = np.ones(len(V), bool)
    head = (s > 1 - CORE) | (head0 & outside)
    edge = np.zeros(len(V), bool)
    if EDGE_RX.search(name) and head.any():
        hv = V[head]; c = np.median(hv[:, bx]); lo, hi = hv[:, bx].min(), hv[:, bx].max()
        side = 1 if (hi - c) > (c - lo) else -1
        t = (V[:, bx] - c) * side / max((hi - c) if side > 0 else (c - lo), 1e-6)
        edge = head & (t > 1 - EDGE)
    return head & ~edge, ax, bx

def classify_body(V, tris, mode="club"):
    """club: grip = the narrow end; blade: grip = the THICK end (a knife tip is narrower than its handle);
    knuckle: the main piece is skinned whole. Other pieces (nails, spikes, wire, wraps) always stay stock.
    hook (sickle): every vertex past the handle end is skinned, all pieces."""
    lab = pieces(V, tris)
    ext = V.max(0) - V.min(0); ax = int(np.argmax(ext)); L = max(ext[ax], 1e-6)
    oth = [i for i in range(3) if i != ax]
    bx = oth[int(np.argmax(ext[oth]))]
    tri_count = collections.Counter(lab[vi[0]] for vi, _, _ in tris)
    best, bestscore = None, -1
    for p, n in tri_count.items():
        pv = V[lab == p]; span = np.ptp(pv[:, ax]) / L
        score = span * math.sqrt(n)                      # long AND substantial (a thin wire can't win)
        if score > bestscore: best, bestscore = p, score
    main = lab == best
    if mode == "knuckle":
        return main, ax, bx
    ref = V if mode == "hook" else V[main]
    s = (V[:, ax] - ref[:, ax].min()) / max(np.ptp(ref[:, ax]), 1e-6)
    sr = s if mode == "hook" else s[main]
    def wid(sel):   # club/hook: widest cross extent; blade: thickness (smallest cross extent)
        if not sel.any(): return 0
        e = np.ptp(ref[sel][:, oth], axis=0)
        return e.min() if mode == "blade" else e.max()
    lo, hi = wid(sr < 0.15), wid(sr > 0.85)
    if mode == "blade":
        if lo < hi: s = 1 - s                             # grip = the thicker end
    elif lo > hi: s = 1 - s                               # grip = the narrower end
    if mode == "hook":
        return s >= 0.35, ax, bx
    return main & (s >= GRIP), ax, bx

def ranked_pieces(V, tris):
    """Piece number per vertex: pieces numbered by size (0 = most triangles). Stable for a given model."""
    lab = pieces(V, tris)
    cnt = collections.Counter(lab[vi[0]] for vi, _, _ in tris)
    rank = {p: i for i, (p, _) in enumerate(cnt.most_common())}
    return np.array([rank.get(l, -1) for l in lab]), cnt

def apply_override(V, tris, o):
    pid, _ = ranked_pieces(V, tris)
    ext = V.max(0) - V.min(0); ax = int(np.argmax(ext))
    oth = [i for i in range(3) if i != ax]; bx = oth[int(np.argmax(ext[oth]))]
    sk = o.get("skin", "all")
    skin = np.ones(len(V), bool) if sk == "all" else np.isin(pid, sk)
    if "stock" in o: skin &= ~np.isin(pid, o["stock"])
    ref = V[skin] if skin.any() else V
    s = (V[:, ax] - ref[:, ax].min()) / max(np.ptp(ref[:, ax]), 1e-6)
    if "band" in o:                                    # only the middle stretch (e.g. a rolling pin's barrel)
        skin &= (s >= o["band"][0]) & (s <= o["band"][1])
    g = float(o.get("grip", 0.0 if "skin" in o else GRIP))
    if g > 0 and "grip_end" in o:
        if o["grip_end"] == "high": s = 1 - s
        skin = skin & (s >= g)
    return skin, ax, bx

PALETTE = [(230, 190, 40), (80, 170, 240), (240, 110, 90), (120, 210, 120), (200, 120, 230), (240, 160, 60),
           (90, 220, 210), (230, 230, 120), (170, 140, 100), (240, 120, 180), (140, 160, 250), (160, 220, 80)]

def parts_tile(name, V, tris, rule):
    pid, cnt = ranked_pieces(V, tris)
    ext = V.max(0) - V.min(0); ax = int(np.argmax(ext)); oth = [i for i in range(3) if i != ax]
    bx = oth[int(np.argmax(ext[oth]))]; third = [i for i in oth if i != bx][0]
    S, pad = 640, 20
    img = Image.new("RGB", (S, S + 70), (18, 18, 22)); d = ImageDraw.Draw(img)
    mn = V.min(0); sc = (S - 2 * pad) / max(ext[ax], ext[bx], 1e-6)
    P = lambda v: (pad + (V[v][ax] - mn[ax]) * sc, pad + (V[v][bx] - mn[bx]) * sc)
    for k in sorted(range(len(tris)), key=lambda k: V[tris[k][0]][:, third].mean()):
        vi = tris[k][0]; p = pid[vi[0]]
        d.polygon([P(v) for v in vi], fill=PALETTE[p % len(PALETTE)] if p < 40 else (90, 90, 100), outline=(0, 0, 0))
    for p in range(min(40, len(cnt))):
        vs = np.where(pid == p)[0]
        if not len(vs): continue
        c = V[vs].mean(0); x, y = pad + (c[ax] - mn[ax]) * sc, pad + (c[bx] - mn[bx]) * sc
        d.rectangle([x - 9, y - 7, x + 11, y + 7], fill=(0, 0, 0)); d.text((x - 6, y - 6), str(p), fill=(255, 255, 255))
    lo = "low end = LEFT" ; hi = "high end = RIGHT"
    d.text((8, S + 4), f"{name}   ({rule})   {lo}, {hi}", fill=(235, 235, 235))
    d.text((8, S + 20), "pieces by size: " + ", ".join(f"{i}:{n}" for i, (_, n) in enumerate(cnt.most_common(18))), fill=(160, 160, 170))
    d.text((8, S + 36), f"{len(cnt)} pieces in all (40+ grey)", fill=(160, 160, 170))
    return img

# ---------------------------------------------------------------- run
font = ImageFont.load_default()
report, tiles = [], []
for name, mesh, tags in sorted(items):
    if ONLY is not None and name not in ONLY: continue
    path, prefab = locate(mesh)
    if not path or not os.path.exists(path):
        report.append((name, "SKIP", "model not in UL/vanilla bundles: " + mesh)); continue
    try:
        parts = collect(path, prefab)
        if not parts:
            report.append((name, "SKIP", "prefab '" + prefab + "' not found")); continue
        V, T, tris = merge(parts)
        if HOOK_RX.search(name): rule = "HOOK"
        elif KNUCKLE_RX.search(name): rule = "KNUCKLE"
        elif BLADE_RX.search(name): rule = "BLADE"
        elif HEAD_RX.search(name): rule = "HEAD"
        else: rule = "CLUB"
        o = OVR.get(name, {})
        rule = o.get("rule", rule)
        if PARTS_MODE:
            os.makedirs(os.path.join(OUT, "parts"), exist_ok=True)
            parts_tile(name, V, tris, rule).save(os.path.join(OUT, "parts", name + ".png"))
            report.append((name, "PARTS", "written")); print(report[-1], flush=True); continue
        if "skin" in o or "grip_end" in o or "band" in o or "stock" in o:
            skin, ax, bx = apply_override(V, tris, o); rule = "OVERRIDE"
        else:
            skin, ax, bx = classify_head(V, 0, name) if rule == "HEAD" else classify_body(V, tris, rule.lower())
        bytex = collections.defaultdict(list)
        for vi, ti, tx in tris:
            if tx and min(ti) >= 0: bytex[tx].append((vi, ti))
        S, pad = 300, 12
        tile = Image.new("RGB", (S, S + 28), (22, 22, 26)); dt = ImageDraw.Draw(tile)
        mn = V.min(0); ext = V.max(0) - V.min(0); sc = (S - 2 * pad) / max(ext[ax], ext[bx], 1e-6)
        third = [i for i in range(3) if i not in (ax, bx)][0]
        order = sorted(range(len(tris)), key=lambda k: V[tris[k][0]][:, third].mean())
        for k in order:
            vi = tris[k][0]
            col = (230, 190, 40) if all(skin[v] for v in vi) else (95, 95, 115)
            dt.polygon([(pad + (V[v][ax] - mn[ax]) * sc, pad + (V[v][bx] - mn[bx]) * sc) for v in vi], fill=col, outline=(10, 10, 10))
        dt.text((6, S + 4), name[:46], fill=(235, 235, 235), font=font)
        dt.text((6, S + 15), rule + " / " + ", ".join(bytex.keys())[:40], fill=(150, 150, 160), font=font)
        tile.save(os.path.join(PREV, name + ".png")); tiles.append(name)
        for tx, tt in bytex.items():
            W = H = 2048
            img = Image.new("L", (W, H), 0); d = ImageDraw.Draw(img)
            for vi, ti in tt:
                if all(skin[v] for v in vi): d.polygon([(T[t][0] % 1 * W, (1 - T[t][1] % 1) * H) for t in ti], fill=255)
            for vi, ti in tt:   # stock triangles drawn last: shared UV areas stay stock
                if not all(skin[v] for v in vi): d.polygon([(T[t][0] % 1 * W, (1 - T[t][1] % 1) * H) for t in ti], fill=0)
            img.save(os.path.join(MASKS, name + "." + tx + ".png"))
        frac = float(np.mean([all(skin[v] for v in vi) for vi, _, _ in tris]))
        report.append((name, rule, f"{len(tris)} tris, {frac:.0%} skinned, textures: {', '.join(bytex.keys())}"))
    except Exception as e:
        report.append((name, "ERROR", repr(e)[:200])); traceback.print_exc()
    print(report[-1], flush=True)

# contact sheets, 6 x 4 per page
COLS, ROWS, S = 6, 4, 300
for pg in range(0, len(tiles), COLS * ROWS):
    page = Image.new("RGB", (COLS * S, ROWS * (S + 28)), (10, 10, 12))
    for i, n in enumerate(tiles[pg:pg + COLS * ROWS]):
        page.paste(Image.open(os.path.join(PREV, n + ".png")), ((i % COLS) * S, (i // COLS) * (S + 28)))
    page.save(os.path.join(OUT, f"sheet_{pg // (COLS * ROWS) + 1}.png"))
with open(os.path.join(OUT, "report.txt"), "w") as f:
    for r in report: f.write(" | ".join(r) + "\n")
print("done:", collections.Counter(r[1] for r in report))
