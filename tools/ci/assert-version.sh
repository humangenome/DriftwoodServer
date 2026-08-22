#!/usr/bin/env bash
# One script, runnable locally before tagging and from CI. Checks that the version stamped in
# Directory.Build.props, the plugin's own constant, the git tag and the CHANGELOG all agree.
#
# This exists because three siblings published a binary that reports a different version from its
# tag - HearthServer v0.1.82 points at the 0.1.81 commit, and Lantern v0.1.20 at the 0.1.19 bump.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail=0
note() { printf '%s\n' "$*"; }
bad()  { printf 'FAIL: %s\n' "$*"; fail=1; }

props="$root/Directory.Build.props"
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$props" | head -1)"
[ -n "$version" ] || { bad "no <Version> in Directory.Build.props"; exit 1; }
note "Directory.Build.props version: $version"

assembly="$(sed -n 's:.*<AssemblyVersion>\([^<]*\)</AssemblyVersion>.*:\1:p' "$props" | head -1)"
file="$(sed -n 's:.*<FileVersion>\([^<]*\)</FileVersion>.*:\1:p' "$props" | head -1)"
[ "$assembly" = "$version.0" ] || bad "AssemblyVersion $assembly does not match $version.0"
[ "$file" = "$version.0" ]     || bad "FileVersion $file does not match $version.0"

plugin="$root/host-mod/DriftwoodHost/Plugin.cs"
constant="$(sed -n 's:.*public const string Version = "\([^"]*\)".*:\1:p' "$plugin" | head -1)"
[ "$constant" = "$version" ] || bad "Plugin.cs Version constant '$constant' does not match $version"
note "Plugin.cs version constant: $constant"

if [ -n "${GITHUB_REF_NAME:-}" ] && [ "${GITHUB_REF_TYPE:-}" = "tag" ]; then
  tag="${GITHUB_REF_NAME#v}"
  [ "$tag" = "$version" ] || bad "tag ${GITHUB_REF_NAME} does not match version $version"
  note "tag: ${GITHUB_REF_NAME}"
fi

changelog="$root/CHANGELOG.md"
if [ -f "$changelog" ]; then
  grep -qE "^##+[[:space:]]*\[?v?${version}\]?\b" "$changelog" \
    || bad "CHANGELOG.md has no heading for $version"
fi

# Family decision (2026-08-03): plugin ids are com.humangenome.<product>.*,
# matching ValheimOne. This is checked in the BUILD rather than remembered, because a BepInEx plugin
# id becomes a config filename on every player's disk - renaming it after a release orphans every
# config that exists, and it is free only before the first tag.
if grep -rn 'com\.survivalservers\.' "$root/src" "$root/host-mod" "$root/bench" "$root/protocol" \
     "$root/docs" "$root/README.md" 2>/dev/null | grep -q .; then
  bad "plugin/config ids must be com.humangenome.*, not com.survivalservers.*"
  grep -rn 'com\.survivalservers\.' "$root/src" "$root/host-mod" "$root/bench" "$root/protocol" \
    "$root/docs" "$root/README.md" 2>/dev/null | head -5 | sed 's/^/    /'
fi

# The plugin id and the config path the supervisor writes must be the same string, or the panel
# writes a file the mod never reads and every setting silently falls back to a default.
plugin_id="$(sed -n 's:.*public const string Guid = "\([^"]*\)".*:\1:p' "$plugin" | head -1)"
config_path="$(grep -o 'com\.humangenome\.driftwood\.host\.cfg' "$root/src/DriftwoodServer/HostOptions.cs" | head -1)"
[ "$config_path" = "$plugin_id.cfg" ] || bad "supervisor writes '$config_path' but the plugin id is '$plugin_id'"
note "plugin id: $plugin_id"

[ "$fail" -eq 0 ] && note "version check OK" || true
exit "$fail"
