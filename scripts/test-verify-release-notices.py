#!/usr/bin/env python3
"""Tests for scripts/verify-release-notices.py and scripts/stage-dotnet-notices.py (Linux, no build).

Builds zips shaped like the release packages from the check's own table: a complete one must
pass, and taking away, emptying or changing any one notice (or the file it sits beside) must fail.
Then stages the .NET notices from a stand-in NuGet folder.

    python scripts/test-verify-release-notices.py
"""

import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import zipfile

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
VERIFY = os.path.join(HERE, "verify-release-notices.py")
STAGE = os.path.join(HERE, "stage-dotnet-notices.py")

spec = importlib.util.spec_from_file_location("vrn", VERIFY)
vrn = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vrn)

failures = []
count = 0


def expect(name, got, want):
    global count
    count += 1
    if got != want:
        failures.append("%s: exit %s, wanted %s" % (name, got, want))


def good_text(how, what, crlf=False):
    if how in ("same", "upstream", "notices"):
        with open(os.path.join(ROOT, what), "rb") as f:
            data = f.read()
        return data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n") if crlf else data
    return ("Header line\n%s\nmore text\n" % what).encode("utf-8")


def make_zip(path, kind, drop=None, empty=None, wrong=None, crlf=False):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        needs = [n for n, _ in vrn.REQUIRED[kind]]
        for covered in sorted({vrn.BESIDE[n] for n in needs if n in vrn.BESIDE}):
            if covered != drop:
                z.writestr(covered.replace("/", "\\") if crlf else covered, b"MZ stand-in")
        for need, (how, what) in vrn.REQUIRED[kind]:
            if need == drop:
                continue
            data = good_text(how, what, crlf)
            if need == empty:
                data = b""
            if need == wrong:
                data = b"Some other licence\n"
            # crlf zips also use Windows separators, as Compress-Archive on older PowerShell wrote them
            z.writestr(need.replace("/", "\\") if crlf else need, data)
        z.writestr("other/file.dll", b"MZ")


def run(script, *args, env=None):
    return subprocess.run([sys.executable, "-I", script] + list(args), capture_output=True, text=True, env=env).returncode


with tempfile.TemporaryDirectory() as t:
    for kind, rules in vrn.REQUIRED.items():
        z = os.path.join(t, kind + ".zip")
        make_zip(z, kind)
        expect("%s complete" % kind, run(VERIFY, kind, z), 0)
        make_zip(z, kind, crlf=True)
        expect("%s complete, CRLF and backslashes" % kind, run(VERIFY, kind, z), 0)
        for need, (how, _) in rules:
            make_zip(z, kind, drop=need)
            expect("%s without %s" % (kind, need), run(VERIFY, kind, z), 1)
            make_zip(z, kind, empty=need)
            expect("%s with empty %s" % (kind, need), run(VERIFY, kind, z), 1)
            make_zip(z, kind, wrong=need)
            expect("%s with the wrong text in %s" % (kind, need), run(VERIFY, kind, z), 1)
        for need, covered in vrn.BESIDE.items():
            if any(n == need for n, _ in rules):
                make_zip(z, kind, drop=covered)
                expect("%s without %s (beside %s)" % (kind, covered, need), run(VERIFY, kind, z), 1)
    # A repository copy of an upstream licence that no longer matches its pin fails every zip that carries it.
    for kind, rules in vrn.REQUIRED.items():
        for need, (how, what) in rules:
            if how != "upstream":
                continue
            z = os.path.join(t, kind + ".zip")
            make_zip(z, kind)
            url, pin = vrn.UPSTREAM[what]
            vrn.UPSTREAM[what] = (url, "0" * 64)
            count += 1
            if not vrn.check(kind, z):
                failures.append("%s: %s passed with a changed pin for %s" % (kind, need, what))
            vrn.UPSTREAM[what] = (url, pin)
            count += 1
            if vrn.check(kind, z):
                failures.append("%s: complete zip fails in-process: %s" % (kind, vrn.check(kind, z)))
    # A THIRD-PARTY-NOTICES file that no longer holds one of its pinned licence texts fails every zip that carries it.
    for kind, rules in vrn.REQUIRED.items():
        if not any(how == "notices" for _, (how, _w) in rules):
            continue
        z = os.path.join(t, kind + ".zip")
        make_zip(z, kind)
        for key in sorted(vrn.NOTICE_TEXTS):
            url, pin, n, first = vrn.NOTICE_TEXTS[key]
            vrn.NOTICE_TEXTS[key] = (url, "0" * 64, n, first)
            count += 1
            if not vrn.check(kind, z):
                failures.append("%s: passed with the %s text missing from the notices" % (kind, key))
            vrn.NOTICE_TEXTS[key] = (url, pin, n, first)
    junk = os.path.join(t, "junk.zip")
    with open(junk, "wb") as f:
        f.write(b"not a zip")
    expect("unreadable zip", run(VERIFY, next(iter(vrn.REQUIRED)), junk), 2)
    expect("unknown kind", run(VERIFY, "nope", junk), 2)

    # stage-dotnet-notices.py against a stand-in NuGet folder (repos whose zips ship the .NET runtime)
    if os.path.exists(STAGE):
        nuget = os.path.join(t, "nuget")
        packs = {
            "microsoft.netcore.app.runtime.win-x64/10.0.99": {"LICENSE.TXT": "The MIT License (MIT)\n", "THIRD-PARTY-NOTICES.TXT": ".NET Runtime uses third-party libraries or other resources that may be\n"},
            "microsoft.aspnetcore.app.runtime.win-x64/10.0.99": {"LICENSE.txt": "The MIT License (MIT)\n", "THIRD-PARTY-NOTICES.TXT": ".NET Core uses third-party libraries or other resources that may be\n"},
        }
        for rel, files in packs.items():
            os.makedirs(os.path.join(nuget, rel))
            for n, body in files.items():
                with open(os.path.join(nuget, rel, n), "w") as f:
                    f.write(body)
        env = dict(os.environ, NUGET_PACKAGES=nuget)

        def publish(name, libs):
            d = os.path.join(t, name)
            os.makedirs(d)
            with open(os.path.join(d, "App.deps.json"), "w") as f:
                json.dump({"libraries": {k: {"type": v} for k, v in libs.items()}}, f)
            return d

        d = publish("pub-ok", {"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.99": "runtimepack",
                               "runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.99": "runtimepack",
                               "Some.Package/1.0.0": "package"})
        expect("stage: self-contained publish", run(STAGE, d, env=env), 0)
        for n in ("dotnet-LICENSE.txt", "dotnet-THIRD-PARTY-NOTICES.txt", "aspnetcore-LICENSE.txt", "aspnetcore-THIRD-PARTY-NOTICES.txt"):
            count += 1
            if not os.path.isfile(os.path.join(d, n)):
                failures.append("stage: %s not written" % n)
        expect("stage: pack not in the NuGet folder",
               run(STAGE, publish("pub-missing", {"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.98": "runtimepack"}), env=env), 1)
        expect("stage: framework-dependent publish", run(STAGE, publish("pub-fdd", {"Some.Package/1.0.0": "package"}), env=env), 1)
        os.remove(os.path.join(nuget, "microsoft.netcore.app.runtime.win-x64/10.0.99", "THIRD-PARTY-NOTICES.TXT"))
        expect("stage: pack without its notices",
               run(STAGE, publish("pub-nonotice", {"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.99": "runtimepack"}), env=env), 1)
        expect("stage: no folder", run(STAGE, os.path.join(t, "nope"), env=env), 1)

for f in failures:
    print("FAIL " + f, file=sys.stderr)
print("test-verify-release-notices: %d checks, %d failed" % (count, len(failures)))
sys.exit(1 if failures else 0)
