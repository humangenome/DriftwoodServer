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

internal='sspanel|passrcon|gameserverid|b-cdn\.net|bitbucket\.org|survivalservers\.com/games/|[0-9]{1,3}(\.[0-9]{1,3}){3}:[0-9]{2,5}'
clone='stormforge|Stormforge|lodestone|Lodestone|delverium|Delverium|witchspire|Witchspire|grounded2|Grounded2|bellwright|Bellwright|solarpunk|Solarpunk|Beryllium|libbulletc|EOSSDK|Hercules-Win64-Shipping'

while IFS= read -r -d '' artefact; do
  for pattern in "$internal" "$clone"; do
    if strings -a "$artefact" | grep -aEq "$pattern"; then
      printf 'FAIL: %s contains a forbidden string matching /%s/\n' "$artefact" "$pattern"
      strings -a "$artefact" | grep -aE "$pattern" | sort -u | head -5 | sed 's/^/    /'
      fail=1
    fi
  done
done < <(find "$root" -path '*/bin/Release/*' \( -name 'Driftwood*.dll' -o -name 'Driftwood*.exe' \) -print0)

# Source-level clone residue, which a strings pass over binaries cannot see in comments that were
# compiled away.
if grep -rniE "$clone" --include='*.cs' --include='*.ps1' --include='*.sh' --include='*.csproj' \
     "$root/src" "$root/host-mod" "$root/tools" 2>/dev/null \
   | grep -v 'check-leaks.sh' | grep -vE '^\s*[^:]+:[0-9]+:\s*//' | grep -q .; then
  printf 'FAIL: clone residue in source outside comments:\n'
  grep -rniE "$clone" --include='*.cs' --include='*.ps1' --include='*.sh' --include='*.csproj' \
    "$root/src" "$root/host-mod" "$root/tools" 2>/dev/null \
    | grep -v 'check-leaks.sh' | grep -vE '^\s*[^:]+:[0-9]+:\s*//' | head -10 | sed 's/^/    /'
  fail=1
fi

[ "$fail" -eq 0 ] && printf 'leak check OK\n' || true
exit "$fail"

# The frame limiter is dead code unless something calls it. That exact failure shipped once: the
# class existed, the build was green, and every "capped" run was uncapped because the wiring edit
# never landed. A grep is enough and it costs nothing.
if [ -f "$root/host-mod/DriftwoodHost/FrameLimiter.cs" ]; then
  grep -q 'FrameLimiter\.Apply(' "$root/host-mod/DriftwoodHost/Plugin.cs" \
    || { printf 'FAIL: FrameLimiter exists but Plugin.cs never calls FrameLimiter.Apply - every capped run would be uncapped\n'; exit 1; }
  grep -q 'FrameLimiter\.SetIdleFrameRate(' "$root/host-mod/DriftwoodHost/Plugin.cs" \
    || { printf 'FAIL: FrameLimiter.SetIdleFrameRate is never called - the idle-rate lever would be inert\n'; exit 1; }
fi
printf 'wiring check OK\n'
