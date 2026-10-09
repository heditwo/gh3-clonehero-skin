#!/usr/bin/env bash
# Installs MelonLoader 0.7.3 and the GH3 gameplay skin into Linux Clone Hero v1.1.0.6142-final.
# Run it from the release folder (the folder with Mods/Gh3Skin.dll). See PLAYER-GUIDE.md, section "Install on Linux".
# Usage: install-linux.sh [<Clone Hero folder>] [--melonloader-zip <MelonLoader.Linux.x64.zip>]
set -euo pipefail

game_version='v1.1.0.6142-final'
melon_version='0.7.3'
melon_url="https://github.com/LavaGang/MelonLoader/releases/download/v$melon_version/MelonLoader.Linux.x64.zip"
melon_sha256='519f25639cde796a749ea0670daedfa251a45d0217608ac6e12c1bc6aa9e114f'
# MelonLoader.Bootstrap.so of the 0.7.3 Linux zip: it tells an installed 0.7.3 from other versions.
bootstrap_sha256='b6419b27aab6d9c71a8bfb29bfd059ac91c0567f867b254d2a43c9494695fdca'
launcher='clonehero-melonloader.sh'
source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

fail() {
  printf 'ERROR: %s\n' "$1" >&2
  exit 1
}

game_dir=""
melon_zip=""
while (( $# )); do
  case "$1" in
    --melonloader-zip) melon_zip="${2:?--melonloader-zip needs a file}"; shift ;;
    -h | --help) sed -n '2,4p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) game_dir="$1" ;;
  esac
  shift
done

[[ -f "$source_dir/Mods/Gh3Skin.dll" ]] || fail "Mods/Gh3Skin.dll is not next to this script. Run the script from the release folder."
if [[ -z "$game_dir" ]]; then
  if [[ -x ./clonehero ]]; then game_dir="$PWD"
  else read -r -p 'Clone Hero folder (the folder with the clonehero file): ' game_dir
  fi
fi
game_dir="${game_dir/#\~/$HOME}"
[[ -d "$game_dir" ]] || fail "The folder $game_dir does not exist."
game_dir="$(cd -- "$game_dir" && pwd)"
[[ -f "$game_dir/clonehero" && -d "$game_dir/clonehero_Data" ]] ||
  fail "$game_dir does not have clonehero and clonehero_Data. Give the Linux Clone Hero folder."

# The game writes its version string (Application.version) into globalgamemanagers.
grep -aqF "$game_version" "$game_dir/clonehero_Data/globalgamemanagers" ||
  fail "This Clone Hero is not $game_version. The mod works only with $game_version."

for pid in $(pgrep -x clonehero || true); do
  [[ "$(readlink "/proc/$pid/exe" 2> /dev/null || true)" == "$game_dir/clonehero" ]] &&
    fail 'Clone Hero is running. Close the game, then run this script again.'
done

installed=""
[[ -f "$game_dir/MelonLoader.Bootstrap.so" ]] && installed="$(sha256sum "$game_dir/MelonLoader.Bootstrap.so" | cut -d' ' -f1)"
if [[ "$installed" != "$bootstrap_sha256" || ! -f "$game_dir/MelonLoader/net6/MelonLoader.dll" ]]; then
  [[ -z "$installed" ]] || printf 'Another MelonLoader version is installed. The mod needs %s; installing %s.\n' "$melon_version" "$melon_version"
  temporary="$(mktemp -d)"
  trap 'rm -rf "$temporary"' EXIT
  if [[ -z "$melon_zip" ]]; then
    melon_zip="$temporary/MelonLoader.Linux.x64.zip"
    printf 'Downloading MelonLoader %s from %s\n' "$melon_version" "$melon_url"
    if command -v curl > /dev/null; then curl -fL --retry 3 -o "$melon_zip" "$melon_url"
    else wget -O "$melon_zip" "$melon_url"
    fi
  fi
  [[ -f "$melon_zip" ]] || fail "$melon_zip does not exist."
  hash="$(sha256sum "$melon_zip" | cut -d' ' -f1)"
  [[ "$hash" == "$melon_sha256" ]] || fail "$melon_zip is not the MelonLoader $melon_version Linux x64 release (SHA-256 $hash)."
  if command -v unzip > /dev/null; then unzip -qo "$melon_zip" -d "$game_dir"
  else python3 -m zipfile -e "$melon_zip" "$game_dir"
  fi
  printf 'Installed MelonLoader %s\n' "$melon_version"
else
  printf 'MelonLoader %s is installed\n' "$melon_version"
fi

# Without this setting, MelonLoader contacts api.melonloader.com at each start, and the open connection can crash the game.
# The first start needs the network to download the generator files, so set it only after MelonLoader made its assemblies.
generated=0
[[ -d "$game_dir/MelonLoader/Il2CppAssemblies" ]] && generated=1
config="$game_dir/UserData/Loader.cfg"
if (( generated )) && [[ -f "$config" ]] && grep -qE '^force_offline_generation\s*=\s*false' "$config"; then
  sed -i -E 's/^force_offline_generation\s*=\s*false/force_offline_generation = true/' "$config"
  printf 'Set force_offline_generation = true in UserData/Loader.cfg\n'
fi

# The release Mods folder is complete, so remove the old skin files first: earlier versions had other asset folders, flat
# files, counter-tuning.json, and the older install (assets/stages, assets/libraries) that the packages replace. Player
# choices and the cache are in UserData and stay.
skin="$game_dir/Mods/GH3Skin"
rm -rf "$skin/assets" "$skin/Packages" "$skin/render" "$skin/counter-tuning.json"
mkdir -p "$game_dir/Mods"
cp -R "$source_dir/Mods/." "$game_dir/Mods/"
count="$(find "$source_dir/Mods" -type f | wc -l)"
printf 'Installed the GH3 gameplay skin (%s files) in %s/Mods\n' "$count" "$game_dir"

# MelonLoader on Linux loads through LD_PRELOAD. The launcher starts the game with it.
cat > "$game_dir/$launcher" << 'LAUNCHER'
#!/usr/bin/env bash
# Starts Clone Hero with MelonLoader (written by the GH3 gameplay skin installer).
cd -- "$(dirname -- "$(readlink -f -- "${BASH_SOURCE[0]}")")"
export LD_LIBRARY_PATH="$PWD${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export LD_PRELOAD="$PWD/MelonLoader.Bootstrap.so${LD_PRELOAD:+:$LD_PRELOAD}"
exec ./clonehero "$@"
LAUNCHER
chmod +x "$game_dir/$launcher"
printf 'Start the game with %s/%s\n' "$game_dir" "$launcher"

if (( ! generated )); then
  printf 'The first start takes some minutes: MelonLoader prepares its files (this needs the internet), and the mod imports the stages.\n'
  printf 'After the first start, close the game and run this script again. It then stops the MelonLoader network check at each start.\n'
else
  printf 'The first start after an update imports the stages, which takes some time.\n'
fi
