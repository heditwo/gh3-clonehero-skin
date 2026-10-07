#!/usr/bin/env bash
tools="$HOME/games/gh3-test-tools"
WINEPREFIX="$tools/prefix-wow64" "$tools/wine-root/usr/bin/wineserver" -k > /dev/null 2>&1 || true
for f in wine xvfb; do
  [[ -f /home/hedi/.cache/gh3ref/$f.pid ]] && kill "$(cat /home/hedi/.cache/gh3ref/$f.pid)" 2> /dev/null || true
done
