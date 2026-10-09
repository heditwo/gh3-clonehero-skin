tools="$HOME/games/gh3-test-tools"
# Template: copy it to <test copy>/env.sh and set WINEPREFIX to the prefix of that copy.
export WINEPREFIX="$HOME/games/clonehero-win-spike/prefix" WINEDLLPATH="$tools/wine-root/usr/lib/wine" WINEDEBUG=-all
export WINE="$tools/wine-root/usr/bin/wine"
