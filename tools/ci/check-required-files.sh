#!/usr/bin/env bash
# Family standard 7a row 13: every artefact a player or a server receives is a DECLARED list the
# build enforces - plus the reverse check, which is the one that actually catches things.
#
# The failure this prevents: a plugin is written, tested and committed, and never added to the pack,
# so nobody ever receives it. Nothing errors. The pack builds, publishes, installs and loads exactly
# what it lists. On the sibling product that silently cost every player the chat feature for days,
# and it was found by a human pressing the chat key.
#
# So there are two checks:
#   FORWARD  - every declared file exists after a build.
#   REVERSE  - every plugin in the source tree is declared SOMEWHERE (shipped or explicitly not).
#              This is what catches a NEW plugin, and it is the half people leave out.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail=0
bad() { printf 'FAIL: %s\n' "$*"; fail=1; }

# --- the declaration -------------------------------------------------------------------------
# Anything a SERVER receives.
server_bundle=(
  "host-mod/DriftwoodHost/bin/Release/netstandard2.1/DriftwoodHost.dll"
  "src/DriftwoodServer/bin/Release/net8.0/DriftwoodServer.dll"
)
# Plugin projects that are deliberately NOT shipped. Listing them here is the declaration; see
# bench/NOT-SHIPPED.md.
not_shipped=(
  "bench/DriftwoodBenchClient"
)

# --- forward ---------------------------------------------------------------------------------
for relative in "${server_bundle[@]}"; do
  [ -f "$root/$relative" ] || bad "declared server-bundle file is missing after the build: $relative"
done

# --- reverse ---------------------------------------------------------------------------------
# Every BepInEx plugin project in the tree must be accounted for: either its output is in the
# bundle, or its directory is on the not-shipped list.
while IFS= read -r -d '' project; do
  directory="$(dirname "${project#"$root/"}")"
  name="$(basename "$project" .csproj)"
  declared=0
  for relative in "${server_bundle[@]}"; do
    case "$relative" in *"/$name.dll") declared=1 ;; esac
  done
  for skip in "${not_shipped[@]}"; do
    [ "$directory" = "$skip" ] && declared=1
  done
  [ "$declared" -eq 1 ] || bad "plugin project '$directory' is in the source tree but is declared nowhere - add it to the server bundle or to the not-shipped list in this script"
done < <(grep -rlZ --include='*.csproj' 'BepInEx.dll' "$root/host-mod" "$root/bench" 2>/dev/null)

# --- the not-shipped list must be honest -------------------------------------------------------
[ -f "$root/bench/NOT-SHIPPED.md" ] || bad "bench/NOT-SHIPPED.md is missing, so the not-shipped declaration has no explanation"

[ "$fail" -eq 0 ] && printf 'required-files check OK\n' || true
exit "$fail"
