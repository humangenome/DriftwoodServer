#!/usr/bin/env python3
"""Print the Win32 FileVersion of a PE binary (e.g. DriftwoodHost.dll).

Why this exists on the SERVER side: the host install gate reads the staged
DriftwoodHost.dll's FileVersion on every start
(it reads (Get-Item ...).VersionInfo.FileVersion) and
requires it to equal the pinned tag. If the build did not bake the tag in, the
marker says one thing and the binary says another forever: every start decides
the install drifted and re-copies the whole loader tree, on every server, in
silence. So the packaging script asserts the stamp before anything ships, and
it has to do that from Linux where FileVersionInfo isn't available.

The failure is not hypothetical. With <GenerateAssemblyInfo>false</...> the
compiler emits no version resource at all and this reports 0.0.0.0 no matter
what -p:FileVersion says -- which is how that csproj was configured until this
check was wired up.

Parses VS_FIXEDFILEINFO out of the .rsrc version resource: locates the
0xFEEF04BD signature and reads dwFileVersionMS/LS, which is exactly the
numeric tuple Windows reports as FileVersion. Falls back to nothing -- an
unparseable binary is an error, never a silent pass.

Usage:
    scripts/pe-file-version.py path/to/Foo.exe            # prints 0.1.0.0
    scripts/pe-file-version.py path/to/Foo.exe --expect 0.1.0.0
"""
import argparse
import struct
import sys

FIXED_FILE_INFO_SIGNATURE = 0xFEEF04BD


def read_file_version(path: str) -> str:
    with open(path, "rb") as fh:
        data = fh.read()

    sig = struct.pack("<I", FIXED_FILE_INFO_SIGNATURE)
    at = data.find(sig)
    while at >= 0:
        # VS_FIXEDFILEINFO: dwSignature, dwStrucVersion, dwFileVersionMS,
        # dwFileVersionLS, dwProductVersionMS, dwProductVersionLS, ...
        block = data[at:at + 24]
        if len(block) == 24:
            _, struc_version, fv_ms, fv_ls = struct.unpack("<IIII", block[:16])
            # dwStrucVersion is 0x00010000 for every version resource Windows
            # has ever emitted; use it to reject a coincidental byte match.
            if struc_version == 0x00010000:
                return "{}.{}.{}.{}".format(
                    (fv_ms >> 16) & 0xFFFF,
                    fv_ms & 0xFFFF,
                    (fv_ls >> 16) & 0xFFFF,
                    fv_ls & 0xFFFF,
                )
        at = data.find(sig, at + 4)

    raise SystemExit("error: no VS_FIXEDFILEINFO version resource in {}".format(path))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("binary")
    ap.add_argument("--expect", help="fail unless FileVersion equals this")
    args = ap.parse_args()

    version = read_file_version(args.binary)
    print(version)

    if args.expect and version != args.expect:
        print(
            "error: {} reports FileVersion {}, expected {} -- the build did not "
            "take the -p:Version override, so the shipped binary would "
            "disagree with the manifest".format(args.binary, version, args.expect),
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
