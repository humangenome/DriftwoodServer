#!/usr/bin/env bash
# Does the host mod still fit a game build? Run this against every new How to Fish build before
# pinning it, and against the build servers run today, so one DriftwoodHost.dll serves both.
#
#   tools/ci/check-game-build.sh "<game>/How to Fish_Data/Managed" ["<older game>/.../Managed" ...]
#
# For each Managed directory it compiles the host mod against that build's own assemblies
# (-p:ManagedDir), then resolves every member the compiled DLL binds into the game, by name and
# exact signature, against EVERY directory given. A member the game renamed or re-shaped is not
# a compile error on a server: it is a MissingFieldException or MissingMethodException the first
# time the method holding it runs. Game 1.1.0 did exactly that to IslandManager.TotalIslands
# inside the readiness sampler, and 0.1.8 never reported a world as running on that build.
#
# Exit 0 when every build compiles and every static reference resolves on every build. String-
# named reflection targets are listed for reading, not failed on: several are deliberate
# old-shape/new-shape pairs (see tools/memberdiff/host-reflection-targets.txt).
#
# Needs the .NET 10 SDK and the game's Managed folders, which are never committed. CI cannot run it.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
[ "$#" -ge 1 ] || { echo "usage: $0 <Managed dir> [<Managed dir> ...]" >&2; exit 2; }

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

labels=()
i=0
for managed in "$@"; do
  [ -f "$managed/Assembly-CSharp.dll" ] || { echo "no Assembly-CSharp.dll in $managed" >&2; exit 2; }
  i=$((i + 1))
  labels+=("build$i=$managed")
done

dotnet build "$root/tools/memberdiff/memberdiff.csproj" -c Release -nologo -v q -o "$work/memberdiff" >/dev/null

fail=0
i=0
for managed in "$@"; do
  i=$((i + 1))
  out="$work/host$i"
  echo "== compile against build$i ($managed)"
  if ! dotnet build "$root/host-mod/DriftwoodHost/DriftwoodHost.csproj" -c Release --no-incremental \
        -p:ManagedDir="$managed" -o "$out" -nologo -v q > "$work/build$i.log" 2>&1; then
    grep -E 'error' "$work/build$i.log" | sed "s#$root/##" | sort -u
    echo "FAIL: the host mod does not compile against build$i"
    fail=1
    continue
  fi
  echo "   compiled"
  report="$(dotnet "$work/memberdiff/memberdiff.dll" "$out/DriftwoodHost.dll" \
    "$root/tools/memberdiff/host-reflection-targets.txt" "${labels[@]}" || true)"
  echo "$report" | sed 's/^/   /'
  if echo "$report" | grep -q 'MISSING \(method\|field\|type\)'; then
    echo "FAIL: the DLL built against build$i binds a member another build does not have"
    fail=1
  fi
done

[ "$fail" -eq 0 ] && echo "OK: one host DLL fits every build given." || exit 1
