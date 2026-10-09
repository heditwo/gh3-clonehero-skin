#!/usr/bin/env bash
# Run Windows Clone Hero under Wine in a nested Xwayland. Usage: run-win.sh <seconds> <log> [game args...]
# GRAPHICS=dxvk (default) or wined3d selects the D3D11 implementation.
set -uo pipefail
here="$HOME/games/clonehero-win-spike"
source "$here/env.sh"
seconds="$1"; log="$2"; shift 2
display_number="$((100 + $$ % 10000))"
width="${WIDTH:-1600}" height="${HEIGHT:-900}"
WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" Xwayland ":$display_number" -noreset -geometry "${width}x${height}" > "$log.xwayland" 2>&1 &
xwayland_pid=$!
for ((i = 0; i < 50; i++)); do DISPLAY=":$display_number" xrandr --query > /dev/null 2>&1 && break; sleep 0.2; done
overrides="version=n,b"
[[ "${GRAPHICS:-dxvk}" == dxvk ]] && overrides="$overrides;d3d11,dxgi=n"
cd "$here/game"
WAYLAND_DISPLAY='' DISPLAY=":$display_number" WINEDLLOVERRIDES="$overrides" DXVK_LOG_PATH="$(dirname "$log")" \
  timeout -k 10 "$seconds" "$WINE" "Clone Hero.exe" --melonloader.disablestartscreen "$@" > "$log" 2>&1
status=$?
WINEPREFIX="$WINEPREFIX" "$here/../gh3-test-tools/wine-root/usr/bin/wineserver" -k 2>/dev/null
kill -TERM "$xwayland_pid" 2>/dev/null; wait "$xwayland_pid" 2>/dev/null
exit $status
