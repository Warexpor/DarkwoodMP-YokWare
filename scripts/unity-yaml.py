#!/usr/bin/env python3
"""Query the AssetRipper YAML export of Darkwood (scenes, prefabs, data assets).

Resolves script GUIDs to class names, local fileID refs to GameObject paths,
external GUIDs to asset paths, and int enum fields to their C# member names
(field types read from the exported Assembly-CSharp sources).

  unity-yaml.py find door_underground            # every GameObject with that name
  unity-yaml.py find 'door_bunker.*' -r --in dreams
  unity-yaml.py tree dream_bunker_underground_01 --root door_underground -d 2 -c
  unity-yaml.py show dream_bunker_underground_01 onLeaveDoorDialogue_dream_underground
  unity-yaml.py refs dream_bunker_underground_01 door_bunker_ch1_01_open
  unity-yaml.py uses DoorSpawner --paths
  unity-yaml.py enum GameEvent.Type

FILE args take a path, a path relative to Assets/, or a bare file name
(".unity" / ".prefab" / ".asset" optional). Export root: $DARKWOOD_EXPORT.
"""
import argparse
import json
import os
import re
import subprocess
import sys

import yaml

EXPORT = os.environ.get(
    "DARKWOOD_EXPORT",
    "/home/warexpor/Archive/Windows-Desktop/Dev/Darkwood DECOMPILED/Project/ExportedProject",
)
ASSETS = os.path.join(EXPORT, "Assets")
SCRIPTS = os.path.join(ASSETS, "Scripts")
INDEX_PATH = os.path.join(os.path.dirname(EXPORT), "yokware-index.json")
INDEX_VERSION = 1
DATA_EXT = (".unity", ".prefab", ".asset")

Loader = getattr(yaml, "CSafeLoader", yaml.SafeLoader)
Dumper = getattr(yaml, "CSafeDumper", yaml.SafeDumper)

# ---------------------------------------------------------------- C# index

CS_CLEAN = re.compile(
    r'//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])*\'', re.S
)
CS_TOKEN = re.compile(r"[{};]")
CS_TYPE = re.compile(
    r"\b(class|struct|interface|enum)\s+(\w+)\s*(?:<[^{]*?>)?\s*(?::\s*([^{]*?))?\s*(?:\bwhere\b[^{]*)?$",
    re.S,
)
CS_NS = re.compile(r"\bnamespace\s+([\w.]+)\s*$")
CS_ATTR = re.compile(r"\[[^\[\]]*\]")
CS_MODS = {
    "public", "private", "protected", "internal", "readonly", "volatile", "new",
    "unsafe", "extern", "override", "virtual", "abstract", "sealed", "partial",
}


def _strip_attrs(s):
    for _ in range(3):
        s = CS_ATTR.sub(" ", s)
    return s


def _eval_enum(expr, known):
    expr = re.sub(r"\b(0[xX][0-9a-fA-F]+|\d+)[uUlL]+\b", r"\1", expr)
    expr = re.sub(r"\b\w+\.(\w+)\b", r"\1", expr)
    if not re.fullmatch(r"[\w\s()|&~^<>+\-*]+", expr):
        return None
    try:
        v = eval(expr, {"__builtins__": {}}, dict(known))
        return v if isinstance(v, int) else None
    except Exception:
        return None


def parse_cs(src, types):
    src = CS_CLEAN.sub(lambda m: " " if m.group(0).startswith("/") else '""', src)
    stack, buf, pos = [], "", 0

    def cur_type():
        for f in reversed(stack):
            if f["kind"] == "type":
                return f["full"]
        return None

    for m in CS_TOKEN.finditer(src):
        buf += src[pos:m.start()]
        pos = m.end()
        tok = m.group(0)
        top = stack[-1] if stack else None
        in_code = top is not None and top["kind"] == "block"
        if tok == "{":
            decl = buf.split("\x00")[-1]
            if in_code:
                stack.append({"kind": "block", "saved": None})
            elif CS_NS.search(decl):
                stack.append({"kind": "ns"})
            elif (md := CS_TYPE.search(_strip_attrs(decl))) and (top is None or top["kind"] != "type" or top["tkind"] != "enum"):
                outer = cur_type()
                full = f"{outer}.{md.group(2)}" if outer else md.group(2)
                bases = [b.strip() for b in re.split(r",(?![^<]*>)", md.group(3) or "") if b.strip()]
                stack.append({"kind": "type", "tkind": md.group(1), "full": full})
                if full not in types:
                    types[full] = {"k": md.group(1), "b": bases if md.group(1) != "enum" else [],
                                   "f": {}, "fl": bool(re.search(r"\bFlags\b", decl.split(md.group(1))[0]))}
            else:
                stack.append({"kind": "block", "saved": buf if top and top["kind"] == "type" else None})
            buf = ""
        elif tok == ";":
            if top and top["kind"] == "type" and top["tkind"] in ("class", "struct"):
                parts = buf.split("\x00")
                stmt = parts[-2] if len(parts) > 1 and not parts[-1].strip() else parts[-1]
                _add_field(stmt, types.get(top["full"]))
            buf = ""
        else:
            if not stack:
                buf = ""
                continue
            f = stack.pop()
            if f["kind"] == "type" and f["tkind"] == "enum":
                _add_enum(buf, types.get(f["full"]))
            buf = (f["saved"] + "\x00") if f["kind"] == "block" and f.get("saved") is not None else ""


def _add_field(stmt, t):
    if t is None:
        return
    s = _strip_attrs(stmt)
    if "=>" in s:
        return
    s = s.split("=")[0]
    if "(" in s or ")" in s:
        return
    words = s.split()
    if any(w in ("static", "const", "event", "using", "delegate") for w in words):
        return
    s = " ".join(w for w in words if w not in CS_MODS)
    m = re.match(r"^(.+?)\s+(\w+(?:\s*,\s*\w+)*)$", s)
    if not m:
        return
    for name in re.split(r"\s*,\s*", m.group(2)):
        t["f"].setdefault(name, m.group(1).replace(" ", ""))


def _add_enum(body, t):
    if t is None:
        return
    members, known, nxt = [], {}, 0
    for raw in _strip_attrs(body).split(","):
        raw = raw.strip()
        if not raw:
            continue
        name, _, expr = raw.partition("=")
        name = name.strip()
        if not re.fullmatch(r"\w+", name):
            continue
        val = _eval_enum(expr.strip(), known) if expr.strip() else nxt
        if val is not None:
            known[name] = val
            members.append([name, val])
            nxt = val + 1
        else:
            nxt = None
    t["e"] = members


def build_index():
    guid2path, cls2guids, files = {}, {}, []
    for root, _, names in os.walk(ASSETS):
        for n in names:
            full = os.path.join(root, n)
            rel = os.path.relpath(full, ASSETS)
            if n.endswith(".meta"):
                with open(full, encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        if line.startswith("guid:"):
                            g = line.split(":", 1)[1].strip()
                            guid2path[g] = rel[:-5]
                            if n.endswith(".cs.meta"):
                                cls2guids.setdefault(n[:-8], []).append(g)
                            break
            elif n.endswith(DATA_EXT):
                files.append(rel)
    types = {}
    for root, _, names in os.walk(SCRIPTS):
        for n in names:
            if n.endswith(".cs"):
                with open(os.path.join(root, n), encoding="utf-8", errors="replace") as fh:
                    parse_cs(fh.read(), types)
    idx = {"version": INDEX_VERSION, "guid2path": guid2path, "cls2guids": cls2guids,
           "files": sorted(files), "types": types}
    with open(INDEX_PATH, "w", encoding="utf-8") as fh:
        json.dump(idx, fh)
    return idx


class Index:
    def __init__(self, rebuild=False):
        idx = None
        if not rebuild and os.path.exists(INDEX_PATH):
            with open(INDEX_PATH, encoding="utf-8") as fh:
                idx = json.load(fh)
            if idx.get("version") != INDEX_VERSION:
                idx = None
        if idx is None:
            print(f"[building index -> {INDEX_PATH}]", file=sys.stderr)
            idx = build_index()
        self.guid2path = idx["guid2path"]
        self.cls2guids = idx["cls2guids"]
        self.files = idx["files"]
        self.types = idx["types"]

    def script_name(self, guid, fid=None):
        p = self.guid2path.get(guid)
        if p and p.endswith(".cs"):
            return os.path.basename(p)[:-3]
        if p and p.endswith(".dll"):
            return f"{os.path.basename(p)[:-4]}:{fid}"
        return f"script:{guid}"

    def resolve(self, tstr, scope, depth=0):
        t = tstr.replace(" ", "")
        if t.startswith("global::"):
            t = t[8:]
        t = t.rstrip("?")
        if t.endswith("[]"):
            return "list:" + (self.resolve(t[:-2], scope, depth) or "?")
        m = re.fullmatch(r"(?:System\.Collections\.Generic\.)?List<(.+)>", t)
        if m:
            return "list:" + (self.resolve(m.group(1), scope, depth) or "?")
        if "<" in t:
            return None
        chain, s = [], scope
        while s:
            chain.append(s)
            s = s.rpartition(".")[0]
        if depth < 4:
            for c in list(chain):
                for b in self.types.get(c, {}).get("b", []):
                    bt = self.resolve(b, c.rpartition(".")[0], depth + 1)
                    if bt and not bt.startswith("list:"):
                        chain.append(bt)
        for c in chain:
            if f"{c}.{t}" in self.types:
                return f"{c}.{t}"
        parts = t.split(".")
        for i in range(len(parts)):
            cand = ".".join(parts[i:])
            if cand in self.types:
                return cand
        return None

    def field_type(self, tname, key, depth=0):
        t = self.types.get(tname)
        if not t or t["k"] == "enum" or depth > 8:
            return None
        if key in t["f"]:
            return self.resolve(t["f"][key], tname)
        for b in t["b"]:
            bt = self.resolve(b, tname.rpartition(".")[0])
            if bt and not bt.startswith("list:"):
                r = self.field_type(bt, key, depth + 1)
                if r:
                    return r
        return None

    def enum_label(self, tname, v):
        t = self.types.get(tname)
        if not t or t["k"] != "enum" or not isinstance(v, int) or isinstance(v, bool):
            return None
        for n, val in t.get("e", []):
            if val == v:
                return f"{v} ({n})"
        if t.get("fl") and v > 0:
            bits = [n for n, val in t.get("e", []) if val and val & (val - 1) == 0 and v & val]
            if bits:
                return f"{v} ({'|'.join(bits)})"
        return f"{v} (?)"

    def find_file(self, arg):
        if os.path.isfile(arg):
            return os.path.abspath(arg)
        p = os.path.join(ASSETS, arg)
        if os.path.isfile(p):
            return p
        hits = [f for f in self.files if os.path.basename(f) == arg
                or os.path.splitext(os.path.basename(f))[0] == arg]
        if not hits:
            hits = [f for f in self.files if f.endswith(arg) or os.path.splitext(f)[0].endswith(arg)]
        scenes = [f for f in hits if f.endswith(".unity")]
        if len(hits) > 1 and len(scenes) == 1 and not os.path.splitext(arg)[1]:
            others = ", ".join(h for h in hits if h != scenes[0])
            print(f"[{arg}: using {scenes[0]}; also {others}]", file=sys.stderr)
            hits = scenes
        if len(hits) == 1:
            return os.path.join(ASSETS, hits[0])
        if not hits:
            sys.exit(f"no file matches {arg!r}")
        sys.exit(f"{arg!r} is ambiguous:\n  " + "\n  ".join(hits[:30]))


# ---------------------------------------------------------------- Unity YAML file

HDR = re.compile(r"^--- !u!(\d+) &(-?\d+)( stripped)?[ \t]*$", re.M)
RX_NAME = re.compile(r"^  m_Name: ?(.*)$", re.M)
RX_COMP = re.compile(r"component: \{fileID: (-?\d+)\}")
RX_GO = re.compile(r"^  m_GameObject: \{fileID: (-?\d+)\}", re.M)
RX_FATHER = re.compile(r"^  m_Father: \{fileID: (-?\d+)\}", re.M)
RX_CHILDREN = re.compile(r"^  m_Children:\n((?:  - \{fileID: -?\d+\}\n)*)", re.M)
RX_SCRIPT = re.compile(r"m_Script: \{fileID: (-?\d+), guid: ([0-9a-f]+)")
RX_ACTIVE = re.compile(r"^  m_IsActive: (\d)", re.M)
RX_ROOTORDER = re.compile(r"^  m_RootOrder: (-?\d+)", re.M)
RX_VEC = {k: re.compile(r"^  %s: \{([^}]*)\}" % k, re.M)
          for k in ("m_LocalPosition", "m_LocalRotation", "m_LocalScale")}
RX_FILEID = re.compile(r"fileID: (-?\d+)")
NOISE = {"m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
         "m_GameObject", "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Script", "serializedVersion"}


def _scalar(s):
    s = s.strip()
    if s[:1] in ("'", '"'):
        try:
            return str(yaml.load(s, Loader=Loader))
        except Exception:
            return s.strip("'\"")
    return s


def _vec(body, key, default):
    m = RX_VEC[key].search(body)
    if not m:
        return default
    d = {k.strip(): v for k, _, v in (kv.partition(":") for kv in m.group(1).split(","))}
    try:
        return tuple(float(d[k]) for k in ("x", "y", "z", "w")[:len(default)])
    except (KeyError, ValueError):
        return default


def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + y * tz - z * ty, vy + w * ty + z * tx - x * tz, vz + w * tz + x * ty - y * tx)


class UFile:
    def __init__(self, path, idx):
        self.file, self.idx = path, idx
        with open(path, encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        self.docs = {}
        hs = list(HDR.finditer(text))
        for i, m in enumerate(hs):
            end = hs[i + 1].start() if i + 1 < len(hs) else len(text)
            self.docs[int(m.group(2))] = (int(m.group(1)), text[m.end():end].lstrip("\n"))
        self.gos, self.tf, self.go_tf, self.comp_go = {}, {}, {}, {}
        for did, (cls, body) in self.docs.items():
            if cls == 1:
                nm = RX_NAME.search(body)
                act = RX_ACTIVE.search(body)
                self.gos[did] = {"name": _scalar(nm.group(1)) if nm else "?",
                                 "comps": [int(c) for c in RX_COMP.findall(body)],
                                 "active": not act or act.group(1) == "1"}
            else:
                g = RX_GO.search(body)
                if g:
                    self.comp_go[did] = int(g.group(1))
                if cls in (4, 224):
                    fa = RX_FATHER.search(body)
                    ch = RX_CHILDREN.search(body)
                    ro = RX_ROOTORDER.search(body)
                    self.tf[did] = {"go": int(g.group(1)) if g else 0,
                                    "father": int(fa.group(1)) if fa else 0,
                                    "children": [int(c) for c in RX_FILEID.findall(ch.group(1))] if ch else [],
                                    "order": int(ro.group(1)) if ro else 0, "body": body}
                    if g:
                        self.go_tf[int(g.group(1))] = did
        self._paths, self._world = {}, {}

    def type_name(self, did):
        cls, body = self.docs[did]
        if cls == 114:
            m = RX_SCRIPT.search(body)
            return self.idx.script_name(m.group(2), m.group(1)) if m else "MonoBehaviour"
        return body.split(":", 1)[0].strip()

    def path(self, go):
        if go in self._paths:
            return self._paths[go]
        name = self.gos.get(go, {}).get("name", f"#{go}")
        t = self.tf.get(self.go_tf.get(go))
        parent = self.tf.get(t["father"], {}).get("go") if t and t["father"] else None
        p = f"{self.path(parent)}/{name}" if parent else name
        self._paths[go] = p
        return p

    def world(self, tfid):
        if tfid in self._world:
            return self._world[tfid]
        t = self.tf[tfid]
        lp = _vec(t["body"], "m_LocalPosition", (0.0, 0.0, 0.0))
        lr = _vec(t["body"], "m_LocalRotation", (0.0, 0.0, 0.0, 1.0))
        ls = _vec(t["body"], "m_LocalScale", (1.0, 1.0, 1.0))
        if t["father"] in self.tf:
            pp, pr, ps = self.world(t["father"])
            sp = (ps[0] * lp[0], ps[1] * lp[1], ps[2] * lp[2])
            r = _qrot(pr, sp)
            res = ((pp[0] + r[0], pp[1] + r[1], pp[2] + r[2]), _qmul(pr, lr),
                   (ps[0] * ls[0], ps[1] * ls[1], ps[2] * ls[2]))
        else:
            res = (lp, lr, ls)
        self._world[tfid] = res
        return res

    def go_pos(self, go):
        t = self.go_tf.get(go)
        if t is None:
            return ""
        p = self.world(t)[0]
        return f"({p[0]:.1f}, {p[1]:.1f}, {p[2]:.1f})"

    def describe(self, did):
        if did in self.gos:
            return f"GO {self.path(did)}"
        if did in self.docs:
            go = self.comp_go.get(did)
            return f"{self.type_name(did)} @ {self.path(go)}" if go else f"{self.type_name(did)} #{did}"
        return f"#{did}"

    def match(self, target):
        if target.startswith("#"):
            did = int(target[1:])
            return [did] if did in self.gos else []
        if "/" in target:
            return [g for g in self.gos if self.path(g) == target or self.path(g).endswith("/" + target)]
        return [g for g in self.gos if self.gos[g]["name"] == target]

    def parse(self, did):
        cls, body = self.docs[did]
        try:
            d = yaml.load(body, Loader=Loader)
        except yaml.YAMLError as e:
            return None, f"<yaml error: {e}>"
        if isinstance(d, dict) and len(d) == 1:
            return next(iter(d.values())), None
        return d, None


# ---------------------------------------------------------------- formatting

def fmt_ref(v, uf, idx):
    fid = v.get("fileID", 0)
    guid = v.get("guid")
    if isinstance(guid, int):
        guid = f"{guid:032d}"
    if guid and guid.strip("0") not in ("", "e", "f"):
        p = idx.guid2path.get(guid)
        if p is None:
            return f"asset:{guid}#{fid}"
        if p.endswith(".cs") or p.endswith(".dll"):
            return f"script:{idx.script_name(guid, fid)}"
        return f"asset:{p}" + ("" if fid in (100100000, 11400000, 2800000, 2100000, 0) else f"#{fid}")
    if guid:
        return f"builtin#{fid}"
    if not fid:
        return None
    return uf.describe(fid) if uf else f"#{fid}"


def annotate(v, tname, uf, idx, full):
    if isinstance(v, dict):
        if "fileID" in v and set(v) <= {"fileID", "guid", "type"}:
            return fmt_ref(v, uf, idx)
        out = {}
        for k, x in v.items():
            if k in NOISE or (k == "m_Name" and x in ("", None)):
                continue
            ft = idx.field_type(tname, k) if tname and not tname.startswith("list:") else None
            out[k] = annotate(x, ft, uf, idx, full)
        return out
    if isinstance(v, list):
        et = tname[5:] if tname and tname.startswith("list:") and tname != "list:?" else None
        items = [annotate(x, et, uf, idx, full) for x in (v if full else v[:40])]
        if not full and len(v) > 40:
            items.append(f"... ({len(v) - 40} more)")
        return items
    if tname:
        lab = idx.enum_label(tname, v)
        if lab:
            return lab
    if isinstance(v, str) and not full and len(v) > 400:
        return v[:400] + f"... ({len(v)} chars)"
    return v


def dump(obj):
    return yaml.dump(obj, Dumper=Dumper, sort_keys=False, allow_unicode=True, width=160,
                     default_flow_style=None)


def show_component(uf, did, idx, full):
    cls, _ = uf.docs[did]
    tname = uf.type_name(did)
    data, err = uf.parse(did)
    print(f"  == {tname}  (#{did})")
    if err:
        print("    " + err)
        return
    out = annotate(data, tname if cls == 114 else None, uf, idx, full)
    if cls in (4, 224):
        out = {k: out[k] for k in ("m_LocalPosition", "m_LocalRotation", "m_LocalScale") if k in out}
    for line in dump(out).rstrip().splitlines():
        print("    " + line)


# ---------------------------------------------------------------- commands

def grep_files(pattern, exts, fixed=False, scope=None):
    args = ["grep", "-rl", "-F" if fixed else "-E", "-e", pattern]
    args += [f"--include=*{e}" for e in exts]
    args.append(ASSETS)
    res = subprocess.run(args, capture_output=True, text=True)
    files = [f for f in res.stdout.splitlines() if f]
    if scope:
        files = [f for f in files if scope.lower() in os.path.relpath(f, ASSETS).lower()]
    return sorted(files)


def cmd_find(a, idx):
    if a.regex:
        rx = re.compile(a.name)
        pat = "m_Name: .*" + a.name.lstrip("^").rstrip("$")
    else:
        rx = None
        pat = "m_Name: ['\"]?" + re.escape(a.name)
    exts = [".unity"] if a.scenes else [".prefab"] if a.prefabs else [".unity", ".prefab"]
    total = 0
    for f in grep_files(pat, exts, scope=a.in_):
        uf = UFile(f, idx)
        hits = [g for g, info in uf.gos.items()
                if (rx.search(info["name"]) if rx else info["name"] == a.name)]
        for g in sorted(hits, key=uf.path):
            flag = "" if uf.gos[g]["active"] else "  [inactive]"
            print(f"{os.path.relpath(f, ASSETS)} :: {uf.path(g)}  {uf.go_pos(g)}  #{g}{flag}")
            total += 1
            if total >= a.limit:
                print(f"... limit {a.limit} reached")
                return


def cmd_tree(a, idx):
    uf = UFile(idx.find_file(a.file), idx)
    if a.root:
        roots = [uf.go_tf[g] for g in uf.match(a.root) if g in uf.go_tf]
        if not roots:
            sys.exit(f"no GameObject {a.root!r}")
    else:
        roots = sorted((t for t, info in uf.tf.items() if not info["father"]), key=lambda t: uf.tf[t]["order"])

    def walk(t, depth):
        go = uf.tf[t]["go"]
        info = uf.gos.get(go, {"name": f"#{go}", "comps": [], "active": True})
        extra = ""
        if a.comps:
            names = [uf.type_name(c) for c in info["comps"] if c in uf.docs and uf.docs[c][0] not in (4, 224)]
            extra = f"  [{', '.join(names)}]" if names else ""
        pos = f"  {uf.go_pos(go)}" if a.pos else ""
        flag = "" if info["active"] else "  (inactive)"
        print("  " * depth + info["name"] + extra + pos + flag)
        if a.depth is None or depth < a.depth:
            for c in uf.tf[t]["children"]:
                if c in uf.tf:
                    walk(c, depth + 1)

    for r in roots:
        if a.root:
            print(f"# {uf.path(uf.tf[r]['go'])}")
        walk(r, 0)


def cmd_show(a, idx):
    uf = UFile(idx.find_file(a.file), idx)
    if not a.target:
        if uf.gos and not a.all:
            sys.exit("file has GameObjects: give a TARGET (or --all to dump every document)")
        for did in uf.docs:
            show_component(uf, did, idx, a.full)
        return
    gos = uf.match(a.target)
    if not gos:
        sys.exit(f"no GameObject {a.target!r}")
    if len(gos) > 5 and not a.all:
        for g in gos:
            print(f"{uf.path(g)}  {uf.go_pos(g)}  #{g}")
        sys.exit(f"{len(gos)} matches: narrow with a longer path, '#fileID', or --all")
    for g in gos:
        info = uf.gos[g]
        flag = "" if info["active"] else "  [inactive]"
        print(f"GO {uf.path(g)}  {uf.go_pos(g)}  #{g}{flag}")
        _, body = uf.docs[g]
        for k in ("m_Layer", "m_TagString"):
            m = re.search(rf"^  {k}: (.*)$", body, re.M)
            if m and m.group(1) not in ("0", "Untagged"):
                print(f"  {k}: {m.group(1)}")
        for c in info["comps"]:
            if c in uf.docs and (a.transform or uf.docs[c][0] not in (4, 224)):
                show_component(uf, c, idx, a.full)
        if a.children:
            t = uf.go_tf.get(g)
            kids = [uf.tf[c]["go"] for c in uf.tf[t]["children"] if c in uf.tf] if t else []
            if kids:
                print("  children: " + ", ".join(uf.gos.get(k, {}).get("name", f"#{k}") for k in kids))
        print()


def _ref_paths(v, ids, path=""):
    if isinstance(v, dict):
        if "fileID" in v and set(v) <= {"fileID", "guid", "type"}:
            if v["fileID"] in ids and not v.get("guid"):
                yield path, v["fileID"]
            return
        for k, x in v.items():
            yield from _ref_paths(x, ids, f"{path}.{k}" if path else k)
    elif isinstance(v, list):
        for i, x in enumerate(v):
            yield from _ref_paths(x, ids, f"{path}[{i}]")


def cmd_refs(a, idx):
    uf = UFile(idx.find_file(a.file), idx)
    gos = uf.match(a.target)
    if not gos:
        sys.exit(f"no GameObject {a.target!r}")
    ids = set()
    for g in gos:
        ids.add(g)
        ids.update(uf.gos[g]["comps"])
    tfs = {uf.go_tf[g] for g in gos if g in uf.go_tf}
    found = 0
    for did, (cls, body) in uf.docs.items():
        if cls == 1 or did in ids:
            continue
        if not ids.intersection(int(x) for x in RX_FILEID.findall(body)):
            continue
        data, err = uf.parse(did)
        if err:
            continue
        for p, ref in _ref_paths(data, ids):
            if cls in (4, 224) and (p == "m_Father" or p.startswith("m_Children")) and ref in tfs:
                continue
            if p == "m_GameObject":
                continue
            print(f"{uf.describe(did)}  .{p}  -> {uf.describe(ref)}")
            found += 1
    if not found:
        print("no in-file references")


def cmd_uses(a, idx):
    guids = idx.cls2guids.get(a.cls)
    if not guids:
        sys.exit(f"no script class {a.cls!r}")
    files = []
    for g in guids:
        files += grep_files(g, list(DATA_EXT), fixed=True, scope=a.in_)
    files = sorted(set(files))
    for f in files[: a.limit]:
        rel = os.path.relpath(f, ASSETS)
        if not a.paths:
            print(rel)
            continue
        uf = UFile(f, idx)
        hits = [d for d, (cls, _) in uf.docs.items() if cls == 114 and uf.type_name(d) == a.cls]
        for d in hits:
            go = uf.comp_go.get(d)
            print(f"{rel} :: {uf.path(go) if go else '(asset)'}  {uf.go_pos(go) if go else ''}  #{d}")
    if len(files) > a.limit:
        print(f"... {len(files) - a.limit} more files (raise --limit)")
    print(f"[{len(files)} files]", file=sys.stderr)


def cmd_grep(a, idx):
    rx = re.compile(a.pattern)
    exts = [".unity"] if a.scenes else [".prefab"] if a.prefabs else [".asset"] if a.assets else list(DATA_EXT)
    total = 0
    for f in grep_files(a.pattern, exts, scope=a.in_):
        uf = UFile(f, idx)
        rel = os.path.relpath(f, ASSETS)
        for did, (cls, body) in uf.docs.items():
            lines = [ln.strip() for ln in body.splitlines()[1:] if rx.search(ln)]
            if not lines or (cls == 1 and all(ln.startswith("m_Name:") for ln in lines) and not a.names):
                continue
            go = did if cls == 1 else uf.comp_go.get(did)
            where = uf.describe(did)
            pos = f"  {uf.go_pos(go)}" if go else ""
            for ln in lines[:5]:
                print(f"{rel} :: {where}{pos}  | {ln[:200]}")
            total += 1
            if total >= a.limit:
                print(f"... limit {a.limit} reached")
                return


def cmd_enum(a, idx):
    t = idx.resolve(a.name, "")
    if not t or idx.types[t]["k"] != "enum":
        cands = [k for k, v in idx.types.items() if v["k"] == "enum" and (k == a.name or k.endswith("." + a.name))]
        if len(cands) != 1:
            sys.exit(f"enum {a.name!r}: " + (", ".join(cands) if cands else "not found"))
        t = cands[0]
    print(f"enum {t}" + ("  [Flags]" if idx.types[t].get("fl") else ""))
    for n, v in idx.types[t].get("e", []):
        print(f"  {v:>6}  {n}")


def cmd_fields(a, idx):
    t = idx.resolve(a.name, "")
    if not t:
        sys.exit(f"type {a.name!r} not found")
    chain = []
    while t and t not in chain:
        chain.append(t)
        info = idx.types[t]
        print(f"{info['k']} {t}" + (f" : {', '.join(info['b'])}" if info["b"] else ""))
        for n, ft in info["f"].items():
            r = idx.resolve(ft, t)
            print(f"  {n}: {ft}" + (f"  -> {r}" if r and r != ft else ""))
        nxt = None
        for b in info["b"]:
            bt = idx.resolve(b, t.rpartition(".")[0])
            if bt and idx.types.get(bt, {}).get("k") == "class":
                nxt = bt
                break
        t = nxt


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--reindex", action="store_true", help="rebuild the guid/type index first")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("find", help="GameObjects by name across scenes/prefabs")
    p.add_argument("name")
    p.add_argument("-r", "--regex", action="store_true")
    p.add_argument("--in", dest="in_", help="only files whose Assets-relative path contains this")
    p.add_argument("--scenes", action="store_true")
    p.add_argument("--prefabs", action="store_true")
    p.add_argument("--limit", type=int, default=200)

    p = sub.add_parser("tree", help="hierarchy of a scene/prefab")
    p.add_argument("file")
    p.add_argument("--root", help="start at this GameObject (name, path suffix, or #fileID)")
    p.add_argument("-d", "--depth", type=int)
    p.add_argument("-c", "--comps", action="store_true", help="list component types")
    p.add_argument("-p", "--pos", action="store_true", help="world positions")

    p = sub.add_parser("show", help="components + field values of a GameObject (or every doc of an asset)")
    p.add_argument("file")
    p.add_argument("target", nargs="?")
    p.add_argument("--all", action="store_true", help="show every match / every document")
    p.add_argument("--full", action="store_true", help="no list/string truncation")
    p.add_argument("--transform", action="store_true", help="include Transform components")
    p.add_argument("--children", action="store_true", help="list child names")

    p = sub.add_parser("refs", help="what in the same file references a GameObject or its components")
    p.add_argument("file")
    p.add_argument("target")

    p = sub.add_parser("uses", help="files that use a MonoBehaviour class")
    p.add_argument("cls")
    p.add_argument("--paths", action="store_true", help="list the GameObjects too")
    p.add_argument("--in", dest="in_")
    p.add_argument("--limit", type=int, default=100)

    p = sub.add_parser("grep", help="component fields whose YAML line matches a regex (e.g. 'name: door_underground$')")
    p.add_argument("pattern")
    p.add_argument("--in", dest="in_")
    p.add_argument("--scenes", action="store_true")
    p.add_argument("--prefabs", action="store_true")
    p.add_argument("--assets", action="store_true")
    p.add_argument("--names", action="store_true", help="also report GameObject m_Name hits")
    p.add_argument("--limit", type=int, default=200)

    p = sub.add_parser("enum", help="members of a C# enum (e.g. GameEvent.Type)")
    p.add_argument("name")

    p = sub.add_parser("fields", help="serialized-candidate fields of a C# type (with bases)")
    p.add_argument("name")

    a = ap.parse_args()
    idx = Index(rebuild=a.reindex)
    {"find": cmd_find, "tree": cmd_tree, "show": cmd_show, "refs": cmd_refs, "uses": cmd_uses,
     "grep": cmd_grep, "enum": cmd_enum, "fields": cmd_fields}[a.cmd](a, idx)


if __name__ == "__main__":
    try:
        main()
    except BrokenPipeError:
        sys.exit(0)
