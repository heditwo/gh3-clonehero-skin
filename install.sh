#!/usr/bin/env bash
# Usage: install.sh [--packages-only] [--release <folder>]
#   GH3_CLONE_HERO_DIR selects the game folder (Linux or Windows Clone Hero). The mod builds against its MelonLoader assemblies.
#   --packages-only removes the older install (assets/stages, assets/libraries) and does not copy it.
#   --release <folder> writes the mod files and the Windows install scripts to <folder> (packages only, no test charts).
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"
packages_only=0
release_dir=""
while (( $# )); do
  case "$1" in
    --packages-only) packages_only=1 ;;
    --release) release_dir="${2:?--release needs a folder}"; packages_only=1; shift ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
  esac
  shift
done
target_dir="${release_dir:-$game_dir}"

if [[ ! -d "$game_dir/MelonLoader/Il2CppAssemblies" ]]; then
  printf 'No MelonLoader assemblies in %s. Install MelonLoader 0.7.3, start the game one time, and close it.\n' "$game_dir" >&2
  exit 1
fi
dotnet build "$project_dir/src/Gh3Skin.csproj" -c Release --nologo -p:GameRoot="$game_dir"
mkdir -p "$target_dir/Mods/GH3Skin/assets"
cp "$project_dir/src/bin/Release/net6.0/Gh3Skin.dll" "$target_dir/Mods/Gh3Skin.dll"
# The skin folders of assets/README.md. Mods/GH3Skin/assets mirrors them (src/SkinAssetPaths.cs). Earlier installs put
# these files flat in Mods/GH3Skin/assets and the HUD tuning in Mods/GH3Skin; remove them so that no old file stays.
# This list is the only list: install-windows.ps1 copies the Mods folder of a release that this script writes.
skin_folders=(gems sustains highway hud fonts hit-effects)
find "$target_dir/Mods/GH3Skin/assets" -maxdepth 1 -type f -delete
rm -f "$target_dir/Mods/GH3Skin/counter-tuning.json"
for folder in "${skin_folders[@]}"; do
  rm -rf "$target_dir/Mods/GH3Skin/assets/$folder"
  cp -r "$project_dir/assets/$folder" "$target_dir/Mods/GH3Skin/assets/"
done
if (( packages_only )); then
  rm -rf "$target_dir/Mods/GH3Skin/assets/stages" "$target_dir/Mods/GH3Skin/assets/libraries"
else
  if [[ -d "$project_dir/assets/stages" ]]; then
    cp -a "$project_dir/assets/stages" "$target_dir/Mods/GH3Skin/assets/"
  fi
  if [[ -d "$project_dir/assets/libraries" ]]; then
    cp -a "$project_dir/assets/libraries" "$target_dir/Mods/GH3Skin/assets/"
  fi
fi
# Packages (docs/PACKAGE-FORMAT.md): the venues and the library as zip packages. The mod imports them into its cache and
# prefers them to assets/stages and assets/libraries above, which stay as a fallback until a later cleanup.
shopt -s nullglob
packages=("$project_dir/assets/packages/"*.chstage "$project_dir/assets/packages/"*.chlib)
if (( ${#packages[@]} )); then
  mkdir -p "$target_dir/Mods/GH3Skin/Packages"
  rm -f "$target_dir/Mods/GH3Skin/Packages/"*.chstage "$target_dir/Mods/GH3Skin/Packages/"*.chlib
  cp --reflink=auto --preserve=timestamps "${packages[@]}" "$target_dir/Mods/GH3Skin/Packages/"
fi
# The shader bundles depend on the graphics API and the Unity version, so the mod installation supplies them, not a package.
render="$project_dir/assets/libraries/gh3-band-v1/render"
if [[ -f "$render/stage.bundle" ]]; then
  mkdir -p "$target_dir/Mods/GH3Skin/render"
  cp "$render/"*.bundle "$render/stage-templates.json" "$target_dir/Mods/GH3Skin/render/"
fi
shopt -u nullglob
if [[ -n "$release_dir" ]]; then
  cp "$project_dir/install-windows.ps1" "$project_dir/install-windows.bat" "$release_dir/"
  printf 'Wrote the GH3 gameplay skin release files to %s\n' "$release_dir"
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
