#!/usr/bin/env bash
# Run Windows Clone Hero under Wine in a nested Xwayland. Usage: run-win.sh <seconds> <log> [game args...]
# GRAPHICS=dxvk (default) or wined3d selects the D3D11 implementation. GH3_WINDOWS_TEST_DIR selects the test copy.
set -uo pipefail
here="${GH3_WINDOWS_TEST_DIR:-$HOME/games/clonehero-win-spike}"
source "$here/env.sh"
seconds="$1"; log="$2"; shift 2
display_number="$((100 + $$ % 10000))"
width="${WIDTH:-1600}" height="${HEIGHT:-900}"
WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" Xwayland ":$display_number" -noreset -geometry "${width}x${height}" > "$log.xwayland" 2>&1 &
xwayland_pid=$!
# Xwayland inherits the record lock from flock; never leave it running.
trap 'kill -TERM "$xwayland_pid" 2>/dev/null' EXIT
for ((i = 0; i < 50; i++)); do DISPLAY=":$display_number" xrandr --query > /dev/null 2>&1 && break; sleep 0.2; done
# Float the Xwayland window at the full size, so that the Hyprland tiling layout does not change the game resolution.
if instance="$(hyprctl instances -j 2>/dev/null | jq -r '.[0].instance')" && [[ -n "$instance" ]]; then
  for ((i = 0; i < 30; i++)); do
    address="$(hyprctl -i "$instance" clients -j | jq -r --argjson pid "$xwayland_pid" '.[] | select(.pid == $pid) | .address')"
    [[ -z "$address" ]] || break
    sleep 0.2
  done
  if [[ -n "$address" ]]; then
    hyprctl -i "$instance" dispatch "hl.dsp.window.float({ action = 'on', window = 'address:$address' })" > /dev/null
    hyprctl -i "$instance" dispatch "hl.dsp.window.resize({ x = $width, y = $height, window = 'address:$address' })" > /dev/null
  fi
fi
overrides="version=n,b"
[[ "${GRAPHICS:-dxvk}" == dxvk ]] && overrides="$overrides;d3d11,dxgi=n"
cd "$here/game"
# SHOTS="40,55" takes X screenshots (seconds after start) into $(dirname log)/shot-<s>.png.
SHOTS="${SHOTS:-}"
for shot in ${SHOTS//,/ }; do
  (sleep "$shot"; DISPLAY=":$display_number" magick import -window root "$(dirname "$log")/shot-$shot.png" 2>/dev/null) &
done
WAYLAND_DISPLAY='' DISPLAY=":$display_number" WINEDLLOVERRIDES="$overrides" DXVK_LOG_PATH="$(dirname "$log")" \
  timeout -k 10 "$seconds" "$WINE" "Clone Hero.exe" --melonloader.disablestartscreen "$@" > "$log" 2>&1
status=$?
WINEPREFIX="$WINEPREFIX" "$here/../gh3-test-tools/wine-root/usr/bin/wineserver" -k 2>/dev/null
kill -TERM "$xwayland_pid" 2>/dev/null; wait "$xwayland_pid" 2>/dev/null
exit $status
