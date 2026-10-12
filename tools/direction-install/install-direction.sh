#!/bin/sh
# Copies each stage-events.json of this GH3 direction archive into the song folder whose chart has the SHA-256 of
# gh3-direction-charts.sha256. It looks in the songs folder and in all its subfolders. It never replaces a file.
# Usage: sh install-direction.sh <songs folder> [--dry-run | --yes]
#   It shows the list first, then asks before it copies. --dry-run shows the list only; --yes copies without the question.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
index="$here/gh3-direction-charts.sha256"
sidecar='stage-events.json'
tab=$(printf '\t')

fail() {
  printf 'ERROR: %s\n' "$1" >&2
  exit 1
}

songs=''
mode=ask
for argument in "$@"; do
  case $argument in
    --dry-run) mode=dry ;;
    --yes) mode=yes ;;
    -h | --help) sed -n '2,5p' "$0"; exit 0 ;;
    -*) fail "Unknown option $argument. See --help." ;;
    *) songs=$argument ;;
  esac
done
[ -n "$songs" ] || fail 'Give the songs folder. Example: sh install-direction.sh "$HOME/.clonehero/Songs"'
[ -d "$songs" ] || fail "The folder $songs does not exist."
[ -f "$index" ] || fail "$index is missing. Run the script from the extracted archive."

if command -v sha256sum > /dev/null 2>&1; then
  sha256() { sha256sum < "$1" | cut -d' ' -f1; }
elif command -v shasum > /dev/null 2>&1; then
  sha256() { shasum -a 256 < "$1" | cut -d' ' -f1; }
else
  fail 'This script needs sha256sum or shasum.'
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
trap 'exit 1' INT TERM

# The chart file names of the archive (for the GH3 packs only notes.mid), then every chart with that name in the songs
# folder as "<SHA-256><tab><folder>".
sed 's|^.*/||' "$index" | sort -u > "$work/names"
: > "$work/charts"
while IFS= read -r name; do
  find "$songs" -type f -iname "$name" > "$work/found"
  while IFS= read -r chart; do
    printf '%s\t%s\n' "$(sha256 "$chart")" "$(dirname -- "$chart")" >> "$work/charts"
  done < "$work/found"
done < "$work/names"
printf 'Checked %s charts in %s\n\n' "$(wc -l < "$work/charts" | tr -d ' ')" "$songs"

# The plan: "<archive file><tab><target file>" for each copy.
: > "$work/plan"
copy=0 present=0 kept=0 missing=0
while IFS= read -r line; do
  hash=${line%%  *}
  entry=${line#*  }
  folder=${entry%/*}
  source="$here/$folder/$sidecar"
  [ -f "$source" ] || fail "$folder/$sidecar is missing in the archive."
  grep "^$hash$tab" "$work/charts" | cut -f2- > "$work/targets" || true
  if [ ! -s "$work/targets" ]; then
    missing=$((missing + 1))
    printf 'Not found:       %s (no chart with its SHA-256)\n' "$folder"
    continue
  fi
  while IFS= read -r target; do
    if [ -e "$target/$sidecar" ]; then
      if cmp -s "$source" "$target/$sidecar"; then
        present=$((present + 1))
        printf 'Already there:   %s\n' "$target"
      else
        kept=$((kept + 1))
        printf 'Kept (another %s): %s\n' "$sidecar" "$target"
      fi
    else
      copy=$((copy + 1))
      printf 'Copy:            %s -> %s\n' "$folder" "$target"
      printf '%s\t%s\n' "$source" "$target/$sidecar" >> "$work/plan"
    fi
  done < "$work/targets"
done < "$index"
printf '\n%s to copy, %s already there, %s kept (another file), %s songs not found.\n' "$copy" "$present" "$kept" "$missing"

[ "$copy" -gt 0 ] || exit 0
case $mode in
  dry) printf 'Dry run: no file was copied.\n'; exit 0 ;;
  ask)
    if [ ! -t 0 ]; then
      printf 'No file was copied. Add --yes to copy.\n'
      exit 0
    fi
    printf 'Copy %s files? Type y and push Enter: ' "$copy"
    read -r answer || answer=''
    case $answer in
      y | Y | yes | YES) ;;
      *) printf 'No file was copied.\n'; exit 0 ;;
    esac
    ;;
esac

copied=0
while IFS="$tab" read -r source target; do
  # Another program can make the file after the plan; do not replace it.
  if [ -e "$target" ]; then
    printf 'Kept (made since the check): %s\n' "$target"
    continue
  fi
  cp -- "$source" "$target"
  copied=$((copied + 1))
done < "$work/plan"
printf 'Copied %s files. In Clone Hero, scan the songs again.\n' "$copied"
