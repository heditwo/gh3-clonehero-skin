#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"

dotnet build "$project_dir/src/Gh3Skin.csproj" -c Release --nologo -p:GameRoot="$game_dir"
mkdir -p "$game_dir/Mods/GH3Skin/assets"
cp "$project_dir/src/bin/Release/net6.0/Gh3Skin.dll" "$game_dir/Mods/Gh3Skin.dll"
cp "$project_dir/assets/"*.{png,dds} "$game_dir/Mods/GH3Skin/assets/"
cp "$project_dir/assets/ready-animation.json" "$game_dir/Mods/GH3Skin/assets/"
cp "$project_dir/counter-tuning.json" "$game_dir/Mods/GH3Skin/counter-tuning.json"
if [[ -d "$project_dir/assets/stages" ]]; then
  cp -a "$project_dir/assets/stages" "$game_dir/Mods/GH3Skin/assets/"
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
