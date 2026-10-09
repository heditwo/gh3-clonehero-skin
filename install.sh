#!/usr/bin/env bash
# Usage: install.sh [--release <folder>]
#   GH3_CLONE_HERO_DIR selects the game folder (Linux or Windows Clone Hero). The mod builds against its MelonLoader assemblies.
#   --release <folder> writes only the Mods folder of a release to <folder> (no test charts). scripts/build-release.sh uses it.
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"
release_dir=""
while (( $# )); do
  case "$1" in
    --release) release_dir="${2:?--release needs a folder}"; shift ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
  esac
  shift
done
target_dir="${release_dir:-$game_dir}"

if [[ ! -d "$game_dir/MelonLoader/Il2CppAssemblies" ]]; then
  printf 'No MelonLoader assemblies in %s. Install MelonLoader 0.7.3, start the game one time, and close it.\n' "$game_dir" >&2
  exit 1
fi
build=(dotnet build "$project_dir/src/Gh3Skin.csproj" -c Release --nologo -p:GameRoot="$game_dir")
# A release DLL must not contain the path of the checkout that built it.
[[ -z "$release_dir" ]] || build+=(-p:PathMap="$project_dir=/gh3-skin" -p:Deterministic=true)
"${build[@]}"
mkdir -p "$target_dir/Mods/GH3Skin/assets"
cp "$project_dir/src/bin/Release/net6.0/Gh3Skin.dll" "$target_dir/Mods/Gh3Skin.dll"
# The skin folders of assets/README.md. Mods/GH3Skin/assets mirrors them (src/SkinAssetPaths.cs). Earlier installs put
# these files flat in Mods/GH3Skin/assets, the HUD tuning in Mods/GH3Skin, and the venues and the band library in
# assets/stages and assets/libraries (the packages replace them); remove them so that no old file stays.
# This list is the only list: the release install scripts copy the Mods folder that this script writes.
skin_folders=(gems sustains highway hud fonts hit-effects)
find "$target_dir/Mods/GH3Skin/assets" -maxdepth 1 -type f -delete
rm -f "$target_dir/Mods/GH3Skin/counter-tuning.json"
rm -rf "$target_dir/Mods/GH3Skin/assets/stages" "$target_dir/Mods/GH3Skin/assets/libraries"
for folder in "${skin_folders[@]}"; do
  rm -rf "$target_dir/Mods/GH3Skin/assets/$folder"
  cp -r "$project_dir/assets/$folder" "$target_dir/Mods/GH3Skin/assets/"
done
# Packages (docs/PACKAGE-FORMAT.md): the venues, the band library, and the GH3 characters. The mod imports them into its cache.
shopt -s nullglob
packages=("$project_dir/assets/packages/"*.chstage "$project_dir/assets/packages/"*.chlib "$project_dir/assets/packages/"gh3.*.chchar)
mkdir -p "$target_dir/Mods/GH3Skin/Packages"
rm -f "$target_dir/Mods/GH3Skin/Packages/"*.chstage "$target_dir/Mods/GH3Skin/Packages/"*.chlib "$target_dir/Mods/GH3Skin/Packages/"*.chchar
if (( ${#packages[@]} )); then
  cp --reflink=auto --preserve=timestamps "${packages[@]}" "$target_dir/Mods/GH3Skin/Packages/"
else
  printf 'No packages in %s/assets/packages: the skin shows the stock background. Run scripts/regenerate.sh.\n' "$project_dir" >&2
fi
# The shader bundles depend on the graphics API and the Unity version, so the mod installation supplies them, not a package.
render="$project_dir/assets/libraries/gh3-band-v1/render"
rm -rf "$target_dir/Mods/GH3Skin/render"
if [[ -f "$render/stage.bundle" ]]; then
  mkdir -p "$target_dir/Mods/GH3Skin/render"
  cp "$render/"*.bundle "$render/stage-templates.json" "$target_dir/Mods/GH3Skin/render/"
fi
shopt -u nullglob
if [[ -n "$release_dir" ]]; then
  printf 'Wrote the Mods folder of the GH3 gameplay skin to %s\n' "$release_dir"
  exit 0
fi
for chart in "$project_dir/charts/"*; do
  destination="$game_dir/PlayerData/Songs/$(basename "$chart")"
  mkdir -p "$destination"
  cp "$chart/"{song.ini,notes.chart,song.ogg} "$destination/"
  if [[ -f "$chart/stage-events.json" ]]; then
    cp "$chart/stage-events.json" "$destination/"
  fi
done
printf 'Installed GH3 gameplay skin in %s\n' "$game_dir"
