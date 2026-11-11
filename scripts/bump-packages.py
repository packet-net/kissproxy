#!/usr/bin/env python3
"""Bump minor/patch NuGet pins and pin vulnerable packages. Stdlib only.

Usage: python3 scripts/bump-packages.py [--vulnerable-only] [--exclude-prefix PREFIX ...] [TARGET]
Run from the repo root. TARGET is an optional .sln/.slnx/.csproj for dotnet.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

RESTORE_FLAGS = ["-p:TreatWarningsAsErrors=false", "-p:NuGetAudit=true"]
# SDK-bundled (PublishAot/trim): CPM rejects a PackageVersion (NU1009); only a newer SDK fixes them.
SDK_IMPLICIT = {"microsoft.dotnet.ilcompiler", "microsoft.net.illink.tasks"}
STABLE = re.compile(r"^\d+(\.\d+){1,3}$")
PV_TAG = re.compile(r"<PackageVersion\b[^>]*>")
PR_TAG = re.compile(r"<PackageReference\b[^>]*>")
COMMENT = re.compile(r"<!--.*?-->", re.S)
PIN_LINE = "<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>"
PIN_RE = re.compile(r"<CentralPackageTransitivePinningEnabled>\s*([^<]*?)\s*</CentralPackageTransitivePinningEnabled>", re.I)
SKIP_DIRS = {"bin", "obj", ".git"}
cache, changes, seen, notfixed = {}, [], set(), {}


def warn(msg):
    print("warning: " + msg, file=sys.stderr)


def die(msg):
    print("error: " + msg, file=sys.stderr)
    sys.exit(1)


def change(line):
    if line not in seen:
        seen.add(line)
        changes.append(line)
        print(line, flush=True)


def vkey(v):
    m = re.match(r"\d+(?:\.\d+)*", v)
    parts = [int(x) for x in m.group(0).split(".")][:4] if m else []
    return tuple(parts + [0] * (4 - len(parts)))


def find_files(name=None, suffix=None):
    out = []
    for root, dirs, files in os.walk("."):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        out += [os.path.join(root, f) for f in files if f == name or (suffix and f.endswith(suffix))]
    return sorted(out)


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def attrs(tag):
    return dict(re.findall(r'(\w+)="([^"]*)"', tag))


def set_version(tag, new):
    return re.sub(r'\bVersion="[^"]*"', 'Version="%s"' % new, tag, count=1)


def live_tags(text, tag_re):
    """Tag matches outside XML comments."""
    comments = [m.span() for m in COMMENT.finditer(text)]
    return [m for m in tag_re.finditer(text) if not any(a <= m.start() < b for a, b in comments)]


def rewrite(text, tag_re, fn):
    """fn(tag) returns a replacement tag, or None to leave it alone."""
    out, pos = [], 0
    for m in live_tags(text, tag_re):
        new = fn(m.group(0))
        if new is not None and new != m.group(0):
            out += [text[pos:m.start()], new]
            pos = m.end()
    return "".join(out) + text[pos:]


def refs(paths, tag_re, pid):
    """(path, attrs) for every uncommented tag whose Include is pid."""
    out = []
    for path in paths:
        for m in live_tags(read(path), tag_re):
            a = attrs(m.group(0))
            if a.get("Include", "").lower() == pid.lower():
                out.append((path, a))
    return out


def set_pins(paths, tag_re, pid, new):
    """Raise literal pins of pid that are below new."""
    for path in paths:
        text = read(path)

        def fn(tag):
            a = attrs(tag)
            if a.get("Include", "").lower() == pid.lower() and STABLE.match(a.get("Version", "")) \
                    and vkey(a["Version"]) < vkey(new):
                return set_version(tag, new)
            return None

        new_text = rewrite(text, tag_re, fn)
        if new_text != text:
            write(path, new_text)


def fetch(pid):
    """Version list from nuget.org, cached per run."""
    key = pid.lower()
    if key not in cache:
        url = "https://api.nuget.org/v3-flatcontainer/%s/index.json" % key
        err = None
        for attempt in range(3):
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "bump-packages"})
                with urllib.request.urlopen(req, timeout=30) as r:
                    cache[key] = json.load(r).get("versions", [])
                    break
            except urllib.error.HTTPError as e:
                if e.code == 404:
                    warn("%s not found on nuget.org, skipped" % pid)
                    cache[key] = []
                    break
                err = e
            except (urllib.error.URLError, OSError, ValueError) as e:
                err = e
            time.sleep(2)
        else:
            die("cannot fetch %s after 3 tries: %s" % (url, err))
    return cache[key]


def newer(pid, current):
    """Highest stable version above current with the same major, or None."""
    cur = vkey(current)
    best = None
    for v in fetch(pid):
        if STABLE.match(v) and cur < vkey(v) and vkey(v)[0] == cur[0]:
            if best is None or vkey(v) > vkey(best):
                best = v
    return best


def bump_direct(scope, cs, prefixes):
    """Step 1: raise plain stable pins to the newest same-major release."""
    for path, tag_re in [(p, PV_TAG) for p in scope] + [(c, PR_TAG) for c in cs]:
        text = read(path)

        def fn(tag):
            a = attrs(tag)
            if "Include" not in a or not STABLE.match(a.get("Version", "")):
                return None
            if any(a["Include"].lower().startswith(p.lower()) for p in prefixes):
                return None
            new = newer(a["Include"], a["Version"])
            if new is None:
                return None
            change("%s %s -> %s" % (a["Include"], a["Version"], new))
            return set_version(tag, new)

        new_text = rewrite(text, tag_re, fn)
        if new_text != text:
            write(path, new_text)


def audit(target):
    """Restore, then list: (vulnerable id(lower) -> info, restored project paths)."""
    p = subprocess.run(["dotnet", "restore", *target, *RESTORE_FLAGS], capture_output=True, text=True)
    if p.returncode:
        die("restore failed:\n" + "\n".join((p.stdout + p.stderr).splitlines()[-30:]))
    p = subprocess.run(["dotnet", "list", *target, "package", "--vulnerable", "--include-transitive",
                        "--format", "json", "--no-restore"], capture_output=True, text=True)
    if p.returncode:
        die("dotnet list failed:\n" + p.stderr[-2000:])
    data = json.loads(p.stdout)
    for prob in data.get("problems", []):
        if prob.get("level") == "error":
            die("dotnet list: " + prob.get("text", json.dumps(prob)))
        warn("dotnet list: " + prob.get("text", json.dumps(prob)))
    graph, vulns = set(), {}
    for proj in data.get("projects", []):
        path = os.path.abspath(proj["path"])
        graph.add(path)
        for fw in proj.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for pkg in fw.get(kind, []):
                    ent = vulns.setdefault(pkg["id"].lower(), {
                        "id": pkg["id"], "resolved": "0.0.0", "versions": set(), "direct": False, "projects": set()})
                    ent["versions"].add(pkg["resolvedVersion"])
                    if vkey(pkg["resolvedVersion"]) > vkey(ent["resolved"]):
                        ent["resolved"] = pkg["resolvedVersion"]
                    ent["direct"] = ent["direct"] or kind == "topLevelPackages"
                    ent["projects"].add(path)
    return vulns, graph


def insert_after(text, pos, indent, line, nl):
    """Insert an indented line after the line holding pos."""
    eol = text.find("\n", pos)
    if eol < 0:
        return text + nl + indent + line
    return text[:eol + 1] + indent + line + nl + text[eol + 1:]


def add_item(path, name, pid, ver):
    """Add <name Include Version> after the last one in the first ItemGroup that has that kind."""
    text = read(path)
    nl = "\r\n" if "\r\n" in text else "\n"
    line = '<%s Include="%s" Version="%s" />' % (name, pid, ver)
    tag_re = re.compile(r"<%s\b[^>]*>" % name)
    for g in re.finditer(r"<ItemGroup\b[^>]*>.*?</ItemGroup>", text, re.S):
        hits = live_tags(g.group(0), tag_re)
        if hits:
            m = hits[-1]
            rel = m.end() if m.group(0).endswith("/>") else g.group(0).find("</%s>" % name, m.end()) + len(name) + 3
            ls = text.rfind("\n", 0, g.start() + m.start()) + 1
            indent = re.match(r"[ \t]*", text[ls:]).group(0)
            write(path, insert_after(text, g.start() + rel, indent, line, nl))
            return
    block = "  <ItemGroup>" + nl + "    " + line + nl + "  </ItemGroup>" + nl
    write(path, text.replace("</Project>", block + "</Project>", 1))


def ensure_pinning(path):
    """Make CentralPackageTransitivePinningEnabled true in the props file. Returns True if edited."""
    text = read(path)
    nl = "\r\n" if "\r\n" in text else "\n"
    m = PIN_RE.search(text)
    if m:
        if m.group(1).lower() == "true":
            return False
        text = text[:m.start()] + PIN_LINE + text[m.end():]
    elif re.search(r"<PropertyGroup\b", text):
        pg = re.search(r"<PropertyGroup\b[^>]*>", text)
        ls = text.rfind("\n", 0, pg.start()) + 1
        indent = re.match(r"[ \t]*", text[ls:]).group(0) + "  "
        text = insert_after(text, pg.end(), indent, PIN_LINE, nl)
    else:
        block = "  <PropertyGroup>" + nl + "    " + PIN_LINE + nl + "  </PropertyGroup>" + nl
        text = text.replace("</Project>", block + "</Project>", 1)
    write(path, text)
    return True


def props_in_scope(props, graph):
    """Directory.Packages.props files above at least one restored project."""
    dirs = [os.path.dirname(g) for g in graph]
    out = []
    for p in props:
        d = os.path.dirname(os.path.abspath(p))
        if any(x == d or x.startswith(d + os.sep) for x in dirs):
            out.append(p)
    return out


def choose_props(props, target):
    """The props file nearest above the target (or the cwd)."""
    base = os.path.dirname(os.path.abspath(target)) if target else os.getcwd()
    cands = [p for p in props if base == os.path.dirname(os.path.abspath(p))
             or base.startswith(os.path.dirname(os.path.abspath(p)) + os.sep)]
    return max(cands, key=lambda p: len(os.path.dirname(os.path.abspath(p)))) if cands else props[0]


def fix_one(ent, scope, cs, target, prefixes):
    """Step 2 for one vulnerable package. Returns True if a file changed."""
    pid, old = ent["id"], ent["resolved"]

    def skip(reason):
        notfixed[pid.lower()] = "not fixed: %s %s (%s)" % (pid, old, reason)
        warn(notfixed[pid.lower()])
        return False

    if any(pid.lower().startswith(p.lower()) for p in prefixes):
        return skip("excluded by prefix")
    if pid.lower() in SDK_IMPLICIT:
        return skip("bundled with the SDK; needs a newer .NET SDK")
    cpm = bool(scope)
    tag_re, paths = (PV_TAG, scope) if cpm else (PR_TAG, cs)
    pins = [(p, a["Version"]) for p, a in refs(paths, tag_re, pid) if "Version" in a]
    if any(not STABLE.match(v) for _, v in pins):
        return skip("pin is not a plain version (property, range or prerelease)")
    top = max((v for _, v in pins), key=vkey) if pins else None
    if pins:
        same = [v for v in ent["versions"] if vkey(v)[0] == vkey(top)[0]]
        if not same:
            return skip("no vulnerable version in the pin's major")
        base = max(same, key=vkey)
    else:
        base = old
    fix = newer(pid, base)
    if fix is None:
        return skip("no fixed version in the same major")
    label = "direct pin" if pins or ent["direct"] else "transitive pin"

    if any(vkey(v) < vkey(fix) for _, v in pins):
        set_pins(paths, tag_re, pid, fix)
        change("%s %s -> %s (vulnerable, %s)" % (pid, top, fix, label))
        return True
    if pins:
        # Pin already fixed; a project still resolves the vulnerable version transitively.
        if cpm:
            if any([ensure_pinning(p) for p in sorted({p for p, _ in pins})]):
                change("%s %s (vulnerable, pinning enabled)" % (pid, top))
                return True
            return skip("pin already fixed and transitive pinning already on")
        added = False
        for proj in sorted(ent["projects"]):
            if not refs([proj], PR_TAG, pid):
                add_item(proj, "PackageReference", pid, top)
                added = True
        if added:
            change("%s %s (vulnerable, transitive pin)" % (pid, top))
        return added or skip("pin already fixed in every project")
    if cpm:
        path = choose_props(scope, target)
        add_item(path, "PackageVersion", pid, fix)
        ensure_pinning(path)
        change("%s %s -> %s (vulnerable, transitive pin)" % (pid, old, fix))
        return True
    added = False
    for proj in sorted(ent["projects"]):
        if not refs([proj], PR_TAG, pid):
            add_item(proj, "PackageReference", pid, fix)
            added = True
    if added:
        change("%s %s -> %s (vulnerable, transitive pin)" % (pid, old, fix))
    return added


def warn_overrides(files):
    """Warn (do not edit) on VersionOverride and on PackageReference Version in props/targets."""
    for path in files:
        text = read(path)
        for m in live_tags(text, PR_TAG):
            a = attrs(m.group(0))
            if "VersionOverride" in a:
                warn("%s: VersionOverride on %s, not handled" % (path, a.get("Include")))
            elif "Version" in a and path.endswith((".props", ".targets")):
                warn("%s: PackageReference %s with Version in a props/targets file, not handled" % (path, a.get("Include")))


def main():
    ap = argparse.ArgumentParser(description="Bump NuGet pins and pin vulnerable packages.")
    ap.add_argument("--exclude-prefix", action="append", default=[], metavar="PREFIX")
    ap.add_argument("--vulnerable-only", action="store_true", help="skip step 1 (direct bumps)")
    ap.add_argument("target", nargs="?", help=".sln/.slnx/.csproj for dotnet (default: cwd)")
    args = ap.parse_args()
    target = [args.target] if args.target else []
    props_all = find_files(name="Directory.Packages.props")
    csprojs_all = find_files(suffix=".csproj")
    warn_overrides(find_files(suffix=(".csproj", ".props", ".targets")))

    vulns, graph = audit(target)
    scope = props_in_scope(props_all, graph)
    cs = [p for p in csprojs_all if os.path.abspath(p) in graph]
    if not args.vulnerable_only:
        bump_direct(scope, cs, args.exclude_prefix)
        vulns, graph = audit(target)
    for _ in range(3):
        todo = [e for e in vulns.values() if e["id"].lower() not in notfixed]
        if not todo:
            break
        changed = False
        for ent in sorted(todo, key=lambda e: e["id"].lower()):
            changed = fix_one(ent, scope, cs, args.target, args.exclude_prefix) or changed
        if not changed:
            break
        vulns, graph = audit(target)

    for line in notfixed.values():
        print(line, flush=True)
    remaining = [e for e in vulns.values() if e["id"].lower() not in notfixed]
    if remaining:
        for ent in remaining:
            print("still vulnerable: %s %s" % (ent["id"], ent["resolved"]), file=sys.stderr)
        sys.exit(1)

    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        lines = changes + list(notfixed.values())
        with open(out, "a", encoding="utf-8") as f:
            f.write("summary<<BUMP_PACKAGES_EOF\n" + "".join(c + "\n" for c in lines) + "BUMP_PACKAGES_EOF\n")
            f.write("changed=%s\n" % ("true" if changes else "false"))


if __name__ == "__main__":
    main()
