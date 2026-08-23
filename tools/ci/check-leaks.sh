#!/usr/bin/env bash
# Family standard 7a row 7: a published binary is NOT private. Lantern's whole patch map leaked out
# of .rdata log literals. Runs `strings` over every built artefact and fails on anything internal.
#
# Also covers 7a row 12: clone residue. Every sibling is built by cloning the previous one, and the
# dangerous residue is the identifier that silently NO-OPS rather than crashing - a process image
# name, an exe name, a port offset, a plugin id, a CDN path.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail=0
bad() { printf 'FAIL: %s\n' "$*"; fail=1; }

internal='sspanel|passrcon|gameserverid|b-cdn\.net|bitbucket\.org|survivalservers\.com/games/|[0-9]{1,3}(\.[0-9]{1,3}){3}:[0-9]{2,5}'
clone='stormforge|Stormforge|lodestone|Lodestone|delverium|Delverium|witchspire|Witchspire|grounded2|Grounded2|bellwright|Bellwright|solarpunk|Solarpunk|Beryllium|libbulletc|EOSSDK|Hercules-Win64-Shipping'

# BOTH encodings, and the second one is the whole point. A .NET assembly stores its string
# literals in the #US heap as UTF-16, so a plain ASCII `strings` pass reads none of them: this
# check ran green over a DriftwoodHost.dll that contained "SurvivalServers control panel" twice.
# An ASCII-only scan of a managed binary is not a weak check, it is a blind one.
artefact_strings() { { strings -a "$1"; strings -a -e l "$1"; }; }
while IFS= read -r -d '' artefact; do
  for pattern in "$internal" "$clone"; do
    if artefact_strings "$artefact" | grep -aEq "$pattern"; then
      printf 'FAIL: %s contains a forbidden string matching /%s/\n' "$artefact" "$pattern"
      artefact_strings "$artefact" | grep -aE "$pattern" | sort -u | head -5 | sed 's/^/    /'
      fail=1
    fi
  done
done < <(find "$root" -path '*/bin/Release/*' \( -name 'Driftwood*.dll' -o -name 'Driftwood*.exe' \) -print0)

# Source-level INTERNAL references. The `internal` pattern above was only ever applied to built
# artefacts, so this tree could carry - and did carry, until the history scrub - panel install
# paths, a CDN storage endpoint and hosting-endpoint function names in plain source, and still
# pass this very check. A published repo is not private either; source is the part people
# actually read. Loopback and documentation-range addresses are excluded on purpose: the bench rig
# legitimately talks to 127.0.0.1, and only a REAL address is a leak.
# Match the SHAPE of a local-machine path, never a person's name: writing a real username
# into this file to detect that username would publish the very thing it looks for.
local_paths='/tmp/claude|scratchpad|/home/[a-z0-9_.-]+/|-home-[a-z0-9_.-]+-'
internal_src="$internal|howtofish[A-Z][A-Za-z]*|\.inc\.php|$local_paths"
internal_hits="$(grep -rniE "$internal_src" \
     --exclude-dir=obj --exclude-dir=bin --exclude-dir=dist --exclude-dir=.git \
     --include='*.cs' --include='*.ps1' --include='*.sh' --include='*.csproj' --include='*.md' \
     --include='*.json' --include='*.py' --include='*.yml' \
     "$root/src" "$root/host-mod" "$root/tools" "$root/scripts" "$root/bench" "$root/protocol" \
     "$root/docs" "$root/README.md" "$root/CHANGELOG.md" 2>/dev/null \
   | grep -v 'check-leaks.sh' \
   | grep -vE '(127\.0\.0\.1|0\.0\.0\.0|localhost|203\.0\.113\.[0-9]{1,3}|198\.51\.100\.[0-9]{1,3}|192\.0\.2\.[0-9]{1,3}):' \
   || true)"
if [ -n "$internal_hits" ]; then
  printf 'FAIL: internal reference in source:\n'
  printf '%s\n' "$internal_hits" | head -10 | sed 's/^/    /'
  fail=1
fi

# Source-level clone residue, which a strings pass over binaries cannot see in comments that were
# compiled away. scripts/ is scanned too: it carries the packaging and publishing chain -- CDN
# paths, the storage-zone prefix, the overlay layout -- which is exactly where a sibling's name
# survives a clone and points a whole fleet at the wrong bucket.
if grep -rniE "$clone" --include='*.cs' --include='*.ps1' --include='*.sh' --include='*.csproj' \
     "$root/src" "$root/host-mod" "$root/tools" "$root/scripts" 2>/dev/null \
   | grep -v 'check-leaks.sh' | grep -vE '^\s*[^:]+:[0-9]+:\s*//' | grep -q .; then
  printf 'FAIL: clone residue in source outside comments:\n'
  grep -rniE "$clone" --include='*.cs' --include='*.ps1' --include='*.sh' --include='*.csproj' \
    "$root/src" "$root/host-mod" "$root/tools" "$root/scripts" 2>/dev/null \
    | grep -v 'check-leaks.sh' | grep -vE '^\s*[^:]+:[0-9]+:\s*//' | head -10 | sed 's/^/    /'
  fail=1
fi


# The frame limiter is dead code unless something CALLS it. That exact failure shipped once: the
# class existed, the build was green, and every "capped" run was uncapped because the wiring edit
# never landed. A grep is enough and it costs nothing.
if [ -f "$root/host-mod/DriftwoodHost/FrameLimiter.cs" ]; then
  # Comment lines do not count as calls. Checking that without stripping them is how a check
  # passes on the exact edit it exists to catch - commenting the call out.
  live_plugin="$(grep -v '^[[:space:]]*//' "$root/host-mod/DriftwoodHost/Plugin.cs")"
  printf '%s' "$live_plugin" | grep -q 'FrameLimiter\.Apply(' \
    || bad "FrameLimiter exists but Plugin.cs never calls FrameLimiter.Apply - every capped run would be uncapped"
  printf '%s' "$live_plugin" | grep -q 'FrameLimiter\.SetIdleFrameRate(' \
    || bad "FrameLimiter.SetIdleFrameRate is never called - the idle-rate lever would be inert"
fi
if [ -f "$root/host-mod/DriftwoodHost/EmptyWorldPause.cs" ]; then
  grep -v '^[[:space:]]*//' "$root/host-mod/DriftwoodHost/Plugin.cs" | grep -q 'EmptyWorldPause\.Update(' \
    || bad "EmptyWorldPause exists but is never driven - the empty-world lever would be inert"
fi

[ "$fail" -eq 0 ] && printf 'leak check OK\n' || true
exit "$fail"
