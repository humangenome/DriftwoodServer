#!/usr/bin/env python3
"""Fail if a shipped exe is marked CET-compatible.

.NET 9 and later mark the apphost CET-compatible (hardware-enforced shadow stacks) unless the project sets
<CETCompat>false</CETCompat>. A CET-compatible .NET 10 process refuses to start on a machine whose CPU has CET but whose
Windows build does not fully support it ("Fatal error. Your Windows doesn't fully support CET."). The flag lives in the
PE file's debug directory: an IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS (20) entry whose DWORD carries
IMAGE_DLLCHARACTERISTICS_EX_CET_COMPAT (0x01). No such entry, or the bit clear, means the flag is off.

Usage: check-cet-compat.py [--expect on|off] PATH [PATH ...]   (default --expect off)
  PATH is an .exe file, or ARCHIVE.zip!member/path.exe for an exe inside a zip (either slash matches).
Every PATH must exist and must read as expected, or the check fails.
"""
import struct
import sys
import zipfile

DEBUG_TYPE_EX_DLLCHARACTERISTICS = 20
CET_COMPAT = 0x01


def cet_flag(pe: bytes) -> bool:
    if pe[:2] != b"MZ":
        raise ValueError("not a PE file (no MZ header)")
    nt = struct.unpack_from("<I", pe, 0x3C)[0]
    if pe[nt:nt + 4] != b"PE\0\0":
        raise ValueError("not a PE file (no PE signature)")
    coff = nt + 4
    nsections, = struct.unpack_from("<H", pe, coff + 2)
    opt_size, = struct.unpack_from("<H", pe, coff + 16)
    opt = coff + 20
    magic, = struct.unpack_from("<H", pe, opt)
    dirs = opt + (112 if magic == 0x20B else 96)
    debug_rva, debug_size = struct.unpack_from("<II", pe, dirs + 6 * 8)
    if debug_rva == 0 or debug_size == 0:
        return False
    sections = opt + opt_size

    def rva_to_off(rva: int) -> int:
        for i in range(nsections):
            s = sections + i * 40
            vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", pe, s + 8)
            if vaddr <= rva < vaddr + max(vsize, rawsize):
                return rawptr + (rva - vaddr)
        raise ValueError("RVA 0x%x is in no section" % rva)

    base = rva_to_off(debug_rva)
    for i in range(debug_size // 28):
        _, _, _, _, dtype, dsize, _, dptr = struct.unpack_from("<IIHHIIII", pe, base + i * 28)
        if dtype == DEBUG_TYPE_EX_DLLCHARACTERISTICS and dsize >= 4:
            flags, = struct.unpack_from("<I", pe, dptr)
            return bool(flags & CET_COMPAT)
    return False


def read_pe(path: str) -> bytes:
    if "!" in path:
        archive, member = path.split("!", 1)
        want = member.replace("\\", "/").lstrip("./")
        with zipfile.ZipFile(archive) as z:
            # Compress-Archive on Windows can write entry names with backslashes.
            for name in z.namelist():
                if name.replace("\\", "/").lstrip("./") == want:
                    return z.read(name)
        raise KeyError("%s is not in %s" % (member, archive))
    with open(path, "rb") as f:
        return f.read()


def main(argv) -> int:
    expect = "off"
    paths = []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--expect" and i + 1 < len(argv):
            expect = argv[i + 1]
            i += 2
            continue
        if a.startswith("--expect="):
            expect = a.split("=", 1)[1]
        elif a in ("-h", "--help"):
            print(__doc__)
            return 2
        else:
            paths.append(a)
        i += 1
    if not paths or expect not in ("on", "off"):
        print(__doc__)
        return 2
    failed = 0
    for path in paths:
        try:
            on = cet_flag(read_pe(path))
        except (OSError, KeyError, ValueError, struct.error, zipfile.BadZipFile) as e:
            print("%s: cannot read (%s)" % (path, e))
            failed += 1
            continue
        print("%s: CET-compatible flag %s" % (path, "ON" if on else "off"))
        if on != (expect == "on"):
            print("FAIL: expected the flag %s" % expect)
            failed += 1
    if failed:
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
