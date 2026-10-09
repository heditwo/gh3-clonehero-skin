#!/usr/bin/env bash
# Builds the release artifacts from a clean export of a Git commit and the generated packages and shader bundles:
#   gh3-skin-<version>-linux.zip, gh3-skin-<version>-windows.zip, and chstage_tools-<add-on version>.zip (Blender add-on).
# Usage: scripts/build-release.sh [<git ref>]   (default HEAD; uncommitted changes are not in the release)
#   GH3_RELEASE_OUT    output folder (default ~/.cache/gh3-skin-release); keep it outside the repository
#   GH3_CLONE_HERO_DIR game folder whose MelonLoader assemblies the mod builds against (one DLL works on Linux and Windows)
# The zips are reproducible: sorted entries, the commit time as the file time, fixed permissions.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
ref="${1:-HEAD}"
out="${GH3_RELEASE_OUT:-$HOME/.cache/gh3-skin-release}"
game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"
commit="$(git -C "$project_dir" rev-parse --verify "$ref^{commit}")"
if [[ "$ref" == HEAD && -n "$(git -C "$project_dir" status --porcelain --untracked-files=no)" ]]; then
  printf 'Warning: the worktree has uncommitted changes; the release uses commit %s only.\n' "$commit" >&2
fi
# src/Packages/ModInfo.cs is the only place of the mod version.
version="$(git -C "$project_dir" show "$commit:src/Packages/ModInfo.cs" | sed -n 's/.*Version = "\([^"]*\)";.*/\1/p')"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { printf 'No version in src/Packages/ModInfo.cs\n' >&2; exit 1; }
epoch="$(git -C "$project_dir" show -s --format=%ct "$commit")"

# Generated inputs (not in Git): the packages and the shader bundles of the render step (docs/PIPELINE.md).
shopt -s nullglob
packages=("$project_dir/assets/packages/"*.chstage "$project_dir/assets/packages/"*.chlib)
render="$project_dir/assets/libraries/gh3-band-v1/render"
render_files=("$render/"*.bundle "$render/stage-templates.json")
shopt -u nullglob
(( ${#packages[@]} )) || { printf 'No packages in assets/packages. Run scripts/regenerate.sh.\n' >&2; exit 1; }
[[ -f "$render/stage.bundle" && -f "$render/stage-templates.json" ]] ||
  { printf 'No shader bundles in %s. Run scripts/regenerate.sh render.\n' "$render" >&2; exit 1; }

mkdir -p "$out"
work="$(mktemp -d "$out/.work.XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/source"
git -C "$project_dir" archive "$commit" | tar -x -C "$work/source"
mkdir -p "$work/source/assets/packages" "$work/source/assets/libraries/gh3-band-v1/render"
cp --reflink=auto "${packages[@]}" "$work/source/assets/packages/"
cp --reflink=auto "${render_files[@]}" "$work/source/assets/libraries/gh3-band-v1/render/"
GH3_CLONE_HERO_DIR="$game_dir" bash "$work/source/install.sh" --release "$work/mods" > "$work/build.log" 2>&1 ||
  { cat "$work/build.log" >&2; exit 1; }

for platform in linux windows; do
  name="gh3-skin-$version-$platform"
  stage="$work/$platform/$name"
  mkdir -p "$stage/images"
  cp -R --reflink=auto "$work/mods/Mods" "$stage/"
  case "$platform" in
    linux) cp "$work/source/install-linux.sh" "$stage/" ;;
    windows) cp "$work/source/install-windows.ps1" "$work/source/install-windows.bat" "$stage/" ;;
  esac
  cp "$work/source/docs/PLAYER-GUIDE.md" "$stage/"
  cp "$work/source/docs/images/picker.png" "$stage/images/"
  for file in "$work/source/"{CREDITS,LICENSE,COPYING}*; do
    [[ -f "$file" ]] && cp "$file" "$stage/"
  done
  cat > "$stage/release.json" << JSON
{
  "name": "GH3 Gameplay Skin",
  "version": "$version",
  "platform": "$platform",
  "commit": "$commit",
  "cloneHero": "v1.1.0.6142-final",
  "melonLoader": "0.7.3"
}
JSON
  (cd "$stage" && find . -type f ! -name SHA256SUMS -printf '%P\n' | LC_ALL=C sort | xargs -d '\n' sha256sum > SHA256SUMS)
  rm -f "$out/$name.zip"
  python3 - "$work/$platform" "$name" "$out/$name.zip" "$epoch" << 'PY'
import shutil, sys, time, zipfile
from pathlib import Path

root, name, target, epoch = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3]), int(sys.argv[4])
date = time.gmtime(max(epoch, 315532800))[:6]
files = sorted((p for p in (root/name).rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix())
with zipfile.ZipFile(target, "w") as archive:
    for path in files:
        info = zipfile.ZipInfo(path.relative_to(root).as_posix(), date)
        info.create_system = 3
        info.external_attr = (0o100755 if path.suffix == ".sh" else 0o100644) << 16
        # Packages are zip files already; a second compression saves almost nothing.
        info.compress_type = zipfile.ZIP_STORED if path.suffix in (".chstage", ".chlib") else zipfile.ZIP_DEFLATED
        info.file_size = path.stat().st_size
        with path.open("rb") as source, archive.open(info, "w", force_zip64=info.file_size > 0x7fffffff) as sink:
            shutil.copyfileobj(source, sink, 1 << 20)
PY
done

python3 "$work/source/blender/build.py" --output "$work/blender" > /dev/null
addon="$(find "$work/blender" -name 'chstage_tools-*.zip' -printf '%f\n')"
cp "$work/blender/$addon" "$out/$addon"

artifacts=("gh3-skin-$version-linux.zip" "gh3-skin-$version-windows.zip" "$addon")
(cd "$out" && sha256sum "${artifacts[@]}" > "SHA256SUMS-$version")
printf 'Release %s (commit %s) in %s:\n' "$version" "${commit:0:12}" "$out"
(cd "$out" && for artifact in "${artifacts[@]}"; do printf '  %s  %s bytes\n' "$artifact" "$(stat -c %s "$artifact")"; done)
