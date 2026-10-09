#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"

dotnet build "$project_dir/src/Gh3Skin.csproj" -c Release --nologo -p:GameRoot="$game_dir"
mkdir -p "$game_dir/Mods/GH3Skin/assets"
cp "$project_dir/src/bin/Release/net6.0/Gh3Skin.dll" "$game_dir/Mods/Gh3Skin.dll"
# The skin folders of assets/README.md. Mods/GH3Skin/assets mirrors them (src/SkinAssetPaths.cs). Earlier installs put
# these files flat in Mods/GH3Skin/assets and the HUD tuning in Mods/GH3Skin; remove them so that no old file stays.
skin_folders=(gems sustains highway hud fonts hit-effects)
find "$game_dir/Mods/GH3Skin/assets" -maxdepth 1 -type f -delete
rm -f "$game_dir/Mods/GH3Skin/counter-tuning.json"
for folder in "${skin_folders[@]}"; do
  rm -rf "$game_dir/Mods/GH3Skin/assets/$folder"
  cp -r "$project_dir/assets/$folder" "$game_dir/Mods/GH3Skin/assets/"
done
if [[ -d "$project_dir/assets/stages" ]]; then
  cp -a "$project_dir/assets/stages" "$game_dir/Mods/GH3Skin/assets/"
fi
if [[ -d "$project_dir/assets/libraries" ]]; then
  cp -a "$project_dir/assets/libraries" "$game_dir/Mods/GH3Skin/assets/"
fi
# Packages (docs/PACKAGE-FORMAT.md): the venues and the library as zip packages. The mod imports them into its cache and
# prefers them to assets/stages and assets/libraries above, which stay as a fallback until a later cleanup.
shopt -s nullglob
packages=("$project_dir/assets/packages/"*.chstage "$project_dir/assets/packages/"*.chlib)
if (( ${#packages[@]} )); then
  mkdir -p "$game_dir/Mods/GH3Skin/Packages"
  rm -f "$game_dir/Mods/GH3Skin/Packages/"*.chstage "$game_dir/Mods/GH3Skin/Packages/"*.chlib
  cp --reflink=auto --preserve=timestamps "${packages[@]}" "$game_dir/Mods/GH3Skin/Packages/"
fi
# The shader bundles depend on the graphics API and the Unity version, so the mod installation supplies them, not a package.
render="$project_dir/assets/libraries/gh3-band-v1/render"
if [[ -f "$render/stage.bundle" ]]; then
  mkdir -p "$game_dir/Mods/GH3Skin/render"
  cp "$render/"*.bundle "$render/stage-templates.json" "$game_dir/Mods/GH3Skin/render/"
fi
shopt -u nullglob
for chart in "$project_dir/charts/"*; do
  destination="$game_dir/PlayerData/Songs/$(basename "$chart")"
  mkdir -p "$destination"
  cp "$chart/"{song.ini,notes.chart,song.ogg} "$destination/"
  if [[ -f "$chart/stage-events.json" ]]; then
    cp "$chart/stage-events.json" "$destination/"
  fi
done
printf 'Installed GH3 gameplay skin in %s\n' "$game_dir"
