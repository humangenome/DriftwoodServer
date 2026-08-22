#!/usr/bin/env bash
# Build the DriftwoodServer overlay bundle -- the artifact a hosted How to Fish
# server downloads and installs over its game files.
#
#   scripts/package-server-overlay.sh v0.1.0
#   scripts/package-server-overlay.sh v0.1.0 --skip-version-check   # dev packaging
#   DRY_RUN=1 scripts/package-server-overlay.sh v0.1.0              # build+stage+assert, write no zip
#
# Produces, under dist/release/<tag>/:
#
#   DriftwoodServer-<version>.zip   the overlay bundle
#
# What it does NOT do: publish. Uploading is a separate, private step.
#
# ---------------------------------------------------------------------------
# Layout contract -- read this before changing a single path
# ---------------------------------------------------------------------------
# The zip is extracted whole into the shared per-tag cache on the host
# (C:\Driftwood\_cache\<tag>\), and the host then
# stages exactly ONE subtree out of it into the customer's game directory:
#
#   DriftwoodServer/bepinex/winhttp.dll                        -> <gamedir>\winhttp.dll
#   DriftwoodServer/bepinex/doorstop_config.ini                -> <gamedir>\doorstop_config.ini
#   DriftwoodServer/bepinex/BepInEx/core/**                    -> <gamedir>\BepInEx\core\**
#   DriftwoodServer/bepinex/BepInEx/plugins/DriftwoodHost.dll  -> <gamedir>\BepInEx\plugins\...
#
# Those paths are read by name by the hosting endpoint, in its cache-staging
# and per-instance install steps. A bundle that
# builds and uploads but is missing one of them installs "successfully" and then
# does nothing -- the game boots vanilla, binds LOOPBACK, and writes every
# instance's world into one shared save directory. All of that is silent and all
# of it looks healthy in the log, which is why every path below is asserted by
# name rather than assumed from a build that exited 0.
#
# The destination is the GAME DIRECTORY, which also holds the game itself and the
# customer's saves, so the host never mirrors the root -- only BepInEx\ is
# mirrored and the two loader files are copied individually. Anything this bundle
# adds at the bepinex root that is not one of those two files will simply never
# be installed.
#
# BepInEx/config/ is deliberately absent. That directory is written per server per
# start by the hosting endpoint, and shipping one here would fight it -- a
# mirror would restore our defaults over a customer's settings on every bump.
#
# ---------------------------------------------------------------------------
# Why there is no dotnet/ and no BepInEx/interop/ here
# ---------------------------------------------------------------------------
# How to Fish is Unity MONO. BepInEx 5 on Mono runs plugins on the game's own
# runtime, so BepInEx.dll IS the core and nothing else is needed. The IL2CPP
# siblings stage a private CoreCLR in dotnet\ and a
# generated Il2CppInterop set in BepInEx\interop\ because their games have no
# managed runtime to borrow. Cloning that here would stage ~119 MB of files
# nothing loads, so both are asserted ABSENT below, not merely omitted.
#
# ---------------------------------------------------------------------------
# Where the BepInEx runtime comes from
# ---------------------------------------------------------------------------
# It is a third-party redistributable, not our source, so it is not committed.
# Drop the contents of BepInEx_win_x64_5.4.23.5.zip into dist/bepinex-mono/
# (or point BEPINEX_DIR at it). That directory must contain winhttp.dll,
# doorstop_config.ini and BepInEx/core/.

set -euo pipefail

TAG="${1:-}"
if [[ -z "$TAG" ]]; then
  echo "usage: $0 <tag e.g. v0.1.0> [--skip-version-check]" >&2
  exit 2
fi
shift || true

SKIP_VERSION_CHECK=0
for arg in "$@"; do
  case "$arg" in
    --skip-version-check) SKIP_VERSION_CHECK=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

VERSION="${TAG#v}"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "tag '$TAG' does not look like vX.Y.Z -- the host-side FileVersion compare needs a 3-part numeric version" >&2
  exit 2
fi

# The leading v is required, not cosmetic. The git tag is vX.Y.Z, the packaged
# bundle lands in dist/release/<tag>/ and the publish step looks for it
# under the same <tag>, so packaging as "0.1.0" and publishing as "v0.1.0" simply
# does not find the file. The host's shared cache directory is keyed on the tag
# string too.
if [[ "$TAG" != v* ]]; then
  echo "tag '$TAG' must start with 'v' (the git tag is v$TAG)" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DRY_RUN="${DRY_RUN:-0}"
RUNTIME_SRC="${BEPINEX_DIR:-$ROOT/dist/bepinex-mono}"
MODS_DIR="$ROOT/host-mod"
OUT_DIR="$ROOT/dist/release/$TAG"
STAGE="$ROOT/dist/stage/$TAG-server"
PACK="$STAGE/DriftwoodServer/bepinex"

# The BepInEx core the host mod was compiled against. A bundle that ships a
# different core than the plugin references loads the plugin and then throws on
# the first API that moved -- inside the game process, where the only symptom is
# a server that never becomes ready.
BEPINEX_EXPECTED="${BEPINEX_EXPECTED:-5.4.23.5}"

say() { printf '==> %s\n' "$*"; }
die() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 0. Preflight
# ---------------------------------------------------------------------------
command -v dotnet  >/dev/null || die "dotnet SDK not on PATH"
command -v zip     >/dev/null || die "zip not on PATH"
command -v unzip   >/dev/null || die "unzip not on PATH"
command -v python3 >/dev/null || die "python3 not on PATH (needed for the FileVersion assert)"

if [[ ! -d "$RUNTIME_SRC" ]]; then
  die "BepInEx runtime source missing at $RUNTIME_SRC
     Unpack BepInEx_win_x64_${BEPINEX_EXPECTED}.zip into that directory, or set BEPINEX_DIR.
     It is a third-party redistributable and is deliberately not committed."
fi
for f in winhttp.dll doorstop_config.ini; do
  [[ -f "$RUNTIME_SRC/$f" ]] || die "runtime source missing $f -- without the doorstop proxy next to the exe the game boots vanilla with no log and no error"
done
[[ -d "$RUNTIME_SRC/BepInEx/core" ]] || die "runtime source missing BepInEx/core"
[[ -f "$RUNTIME_SRC/BepInEx/core/BepInEx.dll" ]] || die "runtime source missing BepInEx/core/BepInEx.dll"
[[ -f "$RUNTIME_SRC/BepInEx/core/BepInEx.Preloader.dll" ]] || die "runtime source missing BepInEx/core/BepInEx.Preloader.dll -- doorstop's target_assembly points at it, so without it the proxy loads and returns and the game runs unmodded"

core_version="$(python3 "$ROOT/scripts/pe-file-version.py" "$RUNTIME_SRC/BepInEx/core/BepInEx.dll")"
if [[ "$core_version" != "$BEPINEX_EXPECTED" ]]; then
  die "BepInEx core is $core_version but the host mod is built against $BEPINEX_EXPECTED
     Override with BEPINEX_EXPECTED only after rebuilding the mod against the new core."
fi
say "BepInEx core $core_version"

# The tree must be clean. This repo is a shared worktree: another lane's
# half-finished files sit next to yours and get compiled into the release without
# appearing anywhere in it. Build a release from a checkout of the tag.
#
# scripts/ is in the list because this script and its publisher ARE build inputs:
# an uncommitted edit here changes what gets packed and what gets uploaded, and it
# is the one input whose drift leaves no trace in the artifact at all.
if [[ "${ALLOW_DIRTY:-0}" != "1" ]] && git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  dirty=$(git -C "$ROOT" status --porcelain -- host-mod src tools scripts Directory.Build.props Directory.Packages.props global.json || true)
  if [[ -n "$dirty" ]]; then
    printf 'FAIL: the tree has uncommitted changes under the build inputs:\n%s\n' "$dirty" >&2
    echo "Package from a clean checkout (git worktree add <dir> <tag>), or set ALLOW_DIRTY=1 for a dev build." >&2
    exit 1
  fi
fi

if [[ $SKIP_VERSION_CHECK -eq 0 ]]; then
  # Pass the tag through the same env CI uses, so the tag-vs-<Version> compare in
  # assert-version.sh actually runs locally instead of being skipped.
  GITHUB_REF_NAME="$TAG" GITHUB_REF_TYPE=tag "$ROOT/tools/ci/assert-version.sh"
else
  say "SKIPPING version assert (dev packaging) -- never do this for a real release"
fi

# ---------------------------------------------------------------------------
# 1. The bundle's plugins, DECLARED and enforced in both directions
# ---------------------------------------------------------------------------
# SERVER_PLUGINS is the required-files list for everything a hosted SERVER
# receives, and it is enforced two ways:
#
#   forward  - every declared plugin must build and land in the bundle.
#   backward - every plugin project under host-mod/ must be declared here.
#
# The backward check is the half people leave out, and it is the one that catches
# a NEW plugin: on a sibling product a second plugin was written days after the
# bundle's contents were last thought about, was never added to the list, and so
# no server ever received it. Nothing errored -- the bundle built, published,
# installed and loaded exactly what it listed. The feature was simply dead.
SERVER_PLUGINS=(DriftwoodHost)

for dir in "$MODS_DIR"/*/; do
  [[ -d "$dir" ]] || continue
  name=$(basename "$dir")
  [[ -f "$dir/$name.csproj" ]] || continue
  declared=0
  for m in "${SERVER_PLUGINS[@]}"; do [[ "$m" == "$name" ]] && declared=1; done
  [[ $declared -eq 1 ]] || die "host-mod/$name exists but is not in SERVER_PLUGINS -- no server would ever receive it"
done

# Stamped from the TAG rather than from Directory.Build.props: the host-side
# install gate compares the shipped DLL's FileVersion against the pinned tag, and
# a props value that lags the tag makes every start decide the install has
# drifted and re-copy it, forever.
for m in "${SERVER_PLUGINS[@]}"; do
  proj="$MODS_DIR/$m/$m.csproj"
  [[ -f "$proj" ]] || die "declared plugin $m has no project at $proj"

  say "dotnet build $m $VERSION (Release, full rebuild)"
  # Delete obj/ and bin/ outright rather than trusting -t:Rebuild. Rebuild is
  # Clean+Build, and Clean only removes what the last build recorded it wrote, so
  # state carried over from a build at a different commit can survive it.
  rm -rf "$MODS_DIR/$m/obj" "$MODS_DIR/$m/bin"
  # -t:Rebuild, not plain build. MSBuild's up-to-date check keys on source
  # timestamps, NOT on the properties passed in, so a plugin already compiled from
  # an earlier command is reused verbatim and the -p:Version overrides below are
  # silently ignored.
  build_args=(
    -c Release -t:Rebuild
    -p:Version="$VERSION"
    -p:FileVersion="$VERSION.0"
    -p:AssemblyVersion="$VERSION.0"
    -p:InformationalVersion="$VERSION"
    --nologo -v minimal
  )
  # The mod compiles against the game's own assemblies. host-mod/<m>/refs holds a
  # gitignored local copy; GAME_MANAGED_DIR points at a real install instead.
  [[ -n "${GAME_MANAGED_DIR:-}" ]] && build_args+=(-p:ManagedDir="$GAME_MANAGED_DIR")
  dotnet build "$proj" "${build_args[@]}" >/dev/null || die "$m failed to build"

  dll="$MODS_DIR/$m/bin/Release/netstandard2.1/$m.dll"
  [[ -f "$dll" ]] || die "$m produced no DLL at $dll"
  python3 "$ROOT/scripts/pe-file-version.py" "$dll" --expect "$VERSION.0" \
    || die "$m FileVersion is not $VERSION.0 -- the host-side install gate would re-copy the whole loader tree on every start, forever"
done

# A published binary is not private, and clone residue that silently no-ops is
# the expensive kind. Runs over the freshly built plugin.
say "leak + clone-residue scan"
"$ROOT/tools/ci/check-leaks.sh" || die "leak check failed -- nothing here ships"

# ---------------------------------------------------------------------------
# 2. Stage
# ---------------------------------------------------------------------------
rm -rf "$STAGE"
mkdir -p "$PACK/BepInEx/plugins"

say "staging BepInEx runtime"
cp -a "$RUNTIME_SRC/winhttp.dll"         "$PACK/winhttp.dll"
cp -a "$RUNTIME_SRC/doorstop_config.ini" "$PACK/doorstop_config.ini"
cp -a "$RUNTIME_SRC/BepInEx/core"        "$PACK/BepInEx/core"

# config/ is the host side's to write, per start. Never ship one.
rm -rf "$PACK/BepInEx/config"

say "staging plugins"
for m in "${SERVER_PLUGINS[@]}"; do
  cp -a "$MODS_DIR/$m/bin/Release/netstandard2.1/$m.dll" "$PACK/BepInEx/plugins/$m.dll"
done

# Dev droppings must not ride along. .pdb in particular: PathMap keeps the build
# path out of them, but they are still debug artifacts nobody downloads on purpose.
find "$PACK" \( -name '*.log' -o -name 'LogOutput.log' -o -name '*.pdb' -o -name '.dev-build' \) -delete 2>/dev/null || true

# ---------------------------------------------------------------------------
# 3. Assert the layout the host side actually reads, path by path
# ---------------------------------------------------------------------------
say "asserting layout"
REQUIRED=(
  "DriftwoodServer/bepinex/winhttp.dll"
  "DriftwoodServer/bepinex/doorstop_config.ini"
  "DriftwoodServer/bepinex/BepInEx/core/BepInEx.dll"
  "DriftwoodServer/bepinex/BepInEx/core/BepInEx.Preloader.dll"
  "DriftwoodServer/bepinex/BepInEx/plugins/DriftwoodHost.dll"
)
for rel in "${REQUIRED[@]}"; do
  [[ -f "$STAGE/$rel" ]] || die "staged bundle is missing $rel"
done

# Negative asserts. Each of these is a thing a clone of the IL2CPP siblings would
# add, and each installs cleanly and does nothing.
[[ ! -d "$PACK/BepInEx/config"  ]] || die "bundle carries BepInEx/config -- that is the host side's, written per start, and mirroring it would overwrite customer settings on every version bump"
[[ ! -d "$PACK/BepInEx/interop" ]] || die "bundle carries BepInEx/interop -- that is an IL2CPP artifact; How to Fish is Mono and nothing would load it"
[[ ! -d "$PACK/dotnet"          ]] || die "bundle carries dotnet/ -- that is the IL2CPP siblings' private CoreCLR; on Mono the plugins run on the game's own runtime"

# doorstop_config.ini names its entry point. If that file is not in the bundle the
# proxy loads, finds nothing, and returns -- the game then boots perfectly,
# vanilla, with no mod, no error and no log line.
target_assembly="$(sed -n 's/^[[:space:]]*target_assembly[[:space:]]*=[[:space:]]*//p' "$PACK/doorstop_config.ini" | head -1 | tr -d '\r')"
[[ -n "$target_assembly" ]] || die "doorstop_config.ini declares no target_assembly"
target_rel="$(printf '%s' "$target_assembly" | tr '\\' '/')"
[[ -f "$PACK/$target_rel" ]] || die "doorstop_config.ini points at '$target_assembly', which is not in the bundle -- the proxy would load, find nothing, and the game would run unmodded and silent"
say "doorstop target_assembly -> $target_assembly (present)"

# Enabled, or the proxy is inert even with every file in place.
if grep -qiE '^[[:space:]]*enabled[[:space:]]*=[[:space:]]*false' "$PACK/doorstop_config.ini"; then
  die "doorstop_config.ini has enabled=false -- the loader would never run"
fi

# The two loader files are copied INDIVIDUALLY by the host side; anything else at
# the bepinex root is never installed, so shipping it is dead weight that reads as
# intentional to whoever finds it next.
while IFS= read -r stray; do
  case "$(basename "$stray")" in
    winhttp.dll|doorstop_config.ini) ;;
    *) die "bundle has '$(basename "$stray")' at the bepinex root; the host side copies only winhttp.dll and doorstop_config.ini individually, so it would never be installed" ;;
  esac
done < <(find "$PACK" -maxdepth 1 -type f)

core_count=$(find "$PACK/BepInEx/core" -name '*.dll' | wc -l)
(( core_count >= 5 )) || die "only $core_count DLLs in BepInEx/core -- expected the full BepInEx 5 core set"

# ---------------------------------------------------------------------------
# 4. Pack, reproducibly
# ---------------------------------------------------------------------------
# The pin is a sha256, so "same inputs produce the same hash" is what lets anyone
# re-derive and check a published bundle. Normalize mtimes and fix member order.
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-1767225600}"   # 2026-01-01T00:00:00Z
find "$STAGE" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +

OVERLAY_ZIP="$OUT_DIR/DriftwoodServer-$VERSION.zip"

if [[ "$DRY_RUN" == "1" ]]; then
  cat <<EOF

==> DRY RUN -- built, staged and asserted; no zip written.

  would pack   $OVERLAY_ZIP
  from         $STAGE
  files        $(find "$STAGE" -type f | wc -l)
  size         $(du -sh "$STAGE" | cut -f1)

  bundle contents (top two levels):
$(cd "$STAGE" && find DriftwoodServer -maxdepth 3 | LC_ALL=C sort | sed 's/^/    /')

Re-run without DRY_RUN=1 to produce the bundle.
EOF
  exit 0
fi

mkdir -p "$OUT_DIR"
say "packing -> $(basename "$OVERLAY_ZIP")"
rm -f "$OVERLAY_ZIP"
( cd "$STAGE" && find DriftwoodServer -type f | LC_ALL=C sort | zip -q -X -9 "$OVERLAY_ZIP" -@ )

SHA=$(sha256sum "$OVERLAY_ZIP" | awk '{print $1}')

# Prove the archive round-trips before anyone uploads it. A zip that packs fine
# and cannot be read is caught here rather than by every host on the fleet.
unzip -tqq "$OVERLAY_ZIP" >/dev/null || die "the packed zip does not verify"
unzip -l "$OVERLAY_ZIP" | grep -q 'DriftwoodServer/bepinex/BepInEx/plugins/DriftwoodHost.dll' \
  || die "packed zip does not contain the host plugin at the path the cache reads"

# Fold into the release checksum file when one already exists.
if [[ -f "$OUT_DIR/checksums.txt" ]] && ! grep -q "DriftwoodServer-$VERSION.zip" "$OUT_DIR/checksums.txt"; then
  printf '%s  %s\n' "$SHA" "DriftwoodServer-$VERSION.zip" >> "$OUT_DIR/checksums.txt"
fi

cat <<EOF

Built, not shipped.

  bundle   $OVERLAY_ZIP
  size     $(du -h "$OVERLAY_ZIP" | cut -f1)
  files    $(unzip -l "$OVERLAY_ZIP" | tail -1 | awk '{print $2}')
  sha256   $SHA

That sha256 is NOT the value to pin. Pin the hash measured by fetching the file
back from the CDN edge -- the publish step prints it, and the two
agreeing is the only proof the edge serves what was built.

Next:
  1. Publish:  upload the bundle to wherever your hosts download it from.
  2. Pin it:   set the release tag and hash to the values measured by fetching
               the published file back from the edge.
EOF
