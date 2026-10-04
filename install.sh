#!/usr/bin/env bash
set -euo pipefail
export MISE_QUIET=1

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
game_dir="${GH3_CLONE_HERO_DIR:-$HOME/games/clonehero-gh3-melon}"

dotnet build "$project_dir/src/Gh3Skin.csproj" -c Release --nologo -p:GameRoot="$game_dir"
mkdir -p "$game_dir/Mods/GH3Skin/assets"
cp "$project_dir/src/bin/Release/net6.0/Gh3Skin.dll" "$game_dir/Mods/Gh3Skin.dll"
cp "$project_dir/assets/"*.png "$game_dir/Mods/GH3Skin/assets/"
cp "$project_dir/assets/ready-animation.json" "$game_dir/Mods/GH3Skin/assets/"
cp "$project_dir/counter-tuning.json" "$game_dir/Mods/GH3Skin/counter-tuning.json"
mkdir -p "$game_dir/PlayerData/Songs/GH3 Gem Matrix"
cp "$project_dir/charts/GH3 Gem Matrix/"{song.ini,notes.chart,song.ogg} "$game_dir/PlayerData/Songs/GH3 Gem Matrix/"
mkdir -p "$game_dir/PlayerData/Songs/GH3 Combo Preview"
cp "$project_dir/charts/GH3 Combo Preview/"{song.ini,notes.chart,song.ogg} "$game_dir/PlayerData/Songs/GH3 Combo Preview/"
printf 'Installed GH3 gameplay skin in %s\n' "$game_dir"
