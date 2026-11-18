#!/usr/bin/env python3
"""Tests for scripts/lgpl-source.py (Linux; fetches each pinned upstream commit from GitHub).

Builds every pinned source archive, which must pass; then a copy with a CI file added, one with a
file taken out, one with a file changed, a release folder missing an archive and an archive that is
not pinned must each fail.

    python3 scripts/test-lgpl-source.py
"""

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.join(HERE, "lgpl-source.py")
spec = importlib.util.spec_from_file_location("lgpl", TOOL)
lgpl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lgpl)

failures, count = [], 0


def run(*args):
    return subprocess.run([sys.executable, "-I", TOOL] + list(args), capture_output=True, text=True).returncode


def expect(name, got, want):
    global count
    count += 1
    if got != want:
        failures.append("%s: exit %s, wanted %s" % (name, got, want))


def rewrite(src, dst, drop=None, add=None, change=None):
    with zipfile.ZipFile(src) as a, zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as b:
        for i in a.infolist():
            if i.filename == drop:
                continue
            data = a.read(i)
            if i.filename == change:
                data += b"\n"
            b.writestr(i, data)
        if add:
            b.writestr(add, b"name: ci\n")


with tempfile.TemporaryDirectory() as t:
    good = os.path.join(t, "release")
    expect("build every pinned archive", run("build", good), 0)
    expect("check the release folder", run("check", good), 0)
    for name in sorted(lgpl.COMPONENTS):
        z = os.path.join(good, name + "-source.zip")
        with zipfile.ZipFile(z) as zz:
            first = next(i.filename for i in zz.infolist() if not i.filename.endswith("/"))
        bad = os.path.join(t, "bad")
        os.makedirs(bad, exist_ok=True)
        out = os.path.join(bad, name + "-source.zip")
        rewrite(z, out, add=name + "/.github/workflows/build.yml")
        expect("%s with a CI file" % name, run("check", out), 1)
        rewrite(z, out, drop=first)
        expect("%s without %s" % (name, first), run("check", out), 1)
        rewrite(z, out, change=first)
        expect("%s with %s changed" % (name, first), run("check", out), 1)
        missing = os.path.join(t, "missing-" + name)
        shutil.copytree(good, missing)
        os.remove(os.path.join(missing, name + "-source.zip"))
        expect("release folder without %s" % name, run("check", missing), 1)
    stray = os.path.join(t, "stray")
    shutil.copytree(good, stray)
    shutil.copyfile(os.path.join(good, sorted(lgpl.COMPONENTS)[0] + "-source.zip"), os.path.join(stray, "Other-1.0-source.zip"))
    expect("an archive that is not pinned", run("check", stray), 1)
    expect("usage", run("nope"), 2)

for f in failures:
    print("FAIL " + f, file=sys.stderr)
print("test-lgpl-source: %d checks, %d failed" % (count, len(failures)))
sys.exit(1 if failures else 0)
