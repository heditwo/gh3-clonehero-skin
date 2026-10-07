#!/usr/bin/env bash
# Start original GH3 PC on a private Xvfb display.
# Usage: start-gh3.sh <display> [trace-filter-regex]
set -euo pipefail
export MISE_QUIET=1
display="${1:-:9301}"
filter="${2:-}"
tools="$HOME/games/gh3-test-tools"
game="$HOME/games/gh3-matched-capture/Guitar Hero III"
work="$HOME/.cache/gh3ref"
mkdir -p "$work"
export WINEPREFIX="$tools/prefix-wow64"
export WINEDLLPATH="$tools/wine-root/usr/lib/wine"
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=8
unset WAYLAND_DISPLAY
/tmp/opencode/gh3-stage-research/xvfb/usr/bin/Xvfb "$display" -screen 0 1600x900x24 -nolisten tcp > "$work/xvfb.log" 2>&1 &
echo $! > "$work/xvfb.pid"
sleep 3
cd "$game"
if [[ -n "$filter" ]]; then
  export WINEDEBUG=+d3d
  ( DISPLAY="$display" "$tools/wine-root/usr/bin/wine" explorer /desktop=GH3,1600x900 GH3.exe 2>&1 | LC_ALL=C grep --line-buffered -E "$filter" > "$work/trace.log" ) &
else
  export WINEDEBUG=-all
  DISPLAY="$display" "$tools/wine-root/usr/bin/wine" explorer /desktop=GH3,1600x900 GH3.exe > "$work/wine.log" 2>&1 &
fi
echo $! > "$work/wine.pid"
