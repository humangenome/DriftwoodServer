#!/usr/bin/env python3
"""Source archives of the LGPL components we ship, for each release to carry beside its zips.

The LGPL lets us ship a library's binary as long as its complete source goes with it, so every
release that ships one also carries <name>-source.zip: the project's own source at the exact
version we bundle. It is built from the pinned upstream commit with `git archive`, minus the
project's .github folder (CI files, which our published-file scanner refuses in any archive), and
it must hold exactly the files pinned here (each file's name, CRC-32 and size; compression and
timestamps do not count, so a rebuild on another machine checks the same).

Usage:
    python3 scripts/lgpl-source.py build <out dir> [<name> ...]   fetch, archive and check (all by default)
    python3 scripts/lgpl-source.py check <file or dir> ...        check archives already built
    python3 scripts/lgpl-source.py pin <name> <repo url> <commit> print the table entry for a new version

`check` on a directory requires every pinned archive in it (a release folder missing one fails),
and nothing named *-source.zip that is not pinned. Exit 0 = good, 1 = missing or wrong, 2 = usage.
"""

import hashlib
import os
import re
import subprocess
import sys
import tempfile
import zipfile

COMPONENTS = {
    # UnityDoorstop 4.5.0 (LGPL-2.1): winhttp.dll in the bundle is byte for byte the x64 winhttp.dll of its own release.
    "UnityDoorstop-4.5.0": {"repo": "https://github.com/NeighTools/UnityDoorstop.git",
        "commit": "33dab9a6733862eb81869ff08431d9478b28784b",
        "files": 54, "digest": "3a8b2793258c74e8eb4ac4d5eabd93143724e6db0a3bcb3d4e492f4bfeeb4417", "licence": "LGPL-2.1"},
}

# Never in a shipped archive (the published-file scanner's CI-file rule).
REFUSED = re.compile(r"(?i)(^|/)(\.github|tools/ci)/")


def digest(path):
    """(sha256 over the sorted 'name<TAB>crc<TAB>size' lines of the files, file count, problems)"""
    problems = []
    with zipfile.ZipFile(path) as z:
        bad = z.testzip()
        if bad is not None:
            problems.append("corrupt entry %s" % bad)
        rows = []
        for i in z.infolist():
            name = i.filename.replace("\\", "/")
            if REFUSED.search(name):
                problems.append("carries %s (CI files never ship)" % name)
            if not name.endswith("/"):
                rows.append("%s\t%08x\t%d" % (name, i.CRC, i.file_size))
    rows.sort()
    return hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest(), len(rows), problems


def check_file(path):
    base = os.path.basename(path)
    if not base.endswith("-source.zip"):
        return ["%s: not named <component>-source.zip" % base]
    name = base[: -len("-source.zip")]
    if name not in COMPONENTS:
        return ["%s: %s is not a pinned component (%s)" % (base, name, ", ".join(sorted(COMPONENTS)))]
    want = COMPONENTS[name]
    try:
        got, files, problems = digest(path)
    except (OSError, zipfile.BadZipFile) as e:
        return ["%s: cannot read: %s" % (base, e)]
    if files != want["files"] or got != want["digest"]:
        problems.append("holds %d files with digest %s; the pinned source of %s at %s is %d files, %s"
                        % (files, got, name, want["commit"][:12], want["files"], want["digest"]))
    return ["%s: %s" % (base, p) for p in problems]


def check(paths):
    problems = []
    for p in paths:
        if os.path.isdir(p):
            have = sorted(f for f in os.listdir(p) if f.endswith("-source.zip"))
            for name in sorted(COMPONENTS):
                if name + "-source.zip" not in have:
                    problems.append("%s: missing %s-source.zip (the %s source every release carries)"
                                    % (p, name, COMPONENTS[name]["licence"]))
            for f in have:
                problems.extend(check_file(os.path.join(p, f)))
        else:
            problems.extend(check_file(p))
    return problems


def git(*args, cwd=None):
    return subprocess.run(["git"] + list(args), cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


def build_one(name, out_dir, work):
    c = COMPONENTS[name]
    repo = os.path.join(work, name)
    git("init", "-q", repo)
    git("fetch", "-q", "--depth", "1", c["repo"], c["commit"], cwd=repo)
    head = git("rev-parse", "FETCH_HEAD^{commit}", cwd=repo)
    if head != c["commit"]:
        raise SystemExit("lgpl-source: %s fetched %s, pinned %s" % (name, head, c["commit"]))
    out = os.path.join(out_dir, name + "-source.zip")
    git("archive", "--format=zip", "--prefix=%s/" % name, "-o", out, c["commit"], "--", ".", ":(exclude).github",
        cwd=repo)
    return out


def main(argv):
    if len(argv) >= 3 and argv[1] == "build":
        out_dir = argv[2]
        names = argv[3:] or sorted(COMPONENTS)
        unknown = [n for n in names if n not in COMPONENTS]
        if unknown:
            print("lgpl-source: not pinned: %s" % ", ".join(unknown), file=sys.stderr)
            return 2
        os.makedirs(out_dir, exist_ok=True)
        with tempfile.TemporaryDirectory() as work:
            built = [build_one(n, out_dir, work) for n in names]
        problems = check(built)
        for p in problems:
            print("lgpl-source: " + p, file=sys.stderr)
        if problems:
            return 1
        for b in built:
            print("lgpl-source: built %s (%s)" % (b, COMPONENTS[os.path.basename(b)[: -len("-source.zip")]]["licence"]))
        return 0
    if len(argv) >= 3 and argv[1] == "check":
        problems = check(argv[2:])
        for p in problems:
            print("lgpl-source: " + p, file=sys.stderr)
        if problems:
            return 1
        print("lgpl-source: every source archive is the pinned upstream source")
        return 0
    if len(argv) == 5 and argv[1] == "pin":
        name, url, commit = argv[2:5]
        with tempfile.TemporaryDirectory() as work:
            COMPONENTS[name] = {"repo": url, "commit": commit, "files": 0, "digest": "", "licence": "?"}
            out = build_one(name, work, work)
            got, files, problems = digest(out)
        for p in problems:
            print("lgpl-source: " + p, file=sys.stderr)
        print('    "%s": {"repo": "%s", "commit": "%s",\n        "files": %d, "digest": "%s", "licence": "?"},'
              % (name, url, commit, files, got))
        return 1 if problems else 0
    print(__doc__.strip().split("\n\n")[2], file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
