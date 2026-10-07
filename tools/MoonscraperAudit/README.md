# Moonscraper preservation checks

This probe runs the official Moonscraper **1.5.13 Linux Universal** import/export methods inside its Unity player. It loads small MIDI/chart files, saves them, opens the saved files, records the in-memory data, and exits. Python/Mido independently reads the exported MIDI messages.

The instrumenter adds one call to `ChartEditor.Update` to start the probe. It checks that this is the only changed method body. The chart models, parsers, writers, sorting code, and Unity runtime come from the release. There are no replacement parsing or timing functions. This checks the file-processing code; it does not automate the editor's menu dialogs.

## Run

Requirements: Linux, .NET 10 SDK, Python with `mido` (or `uv`), and a working X11 display. A temporary Xvfb display with VirtualGL also works.

Extract a fresh copy of the [official release](https://github.com/FireFox2000000/Moonscraper-Chart-Editor/releases/tag/1.5.13) into a test folder. The release archive is `Moonscraper.Chart.Editor.v1.5.13.Linux.Universal.tar.gz`, SHA-256 `feff20925a4b4be6f3cff4b9173328d5f665d19707bafdc6a0d2559529831687`.

From the repository root, with `app` set to that extracted application directory:

```bash
export MISE_QUIET=1
app='/path/to/test/Moonscraper Chart Editor v1.5.13 Linux (Universal)'
results='/path/to/test/results'
managed="$app/Moonscraper Chart Editor_Data/Managed"
uv run --with mido python tools/MoonscraperAudit/fixtures.py create "$results"
dotnet build tools/MoonscraperAudit/Probe/Probe.csproj -c Release -p:MoonscraperManaged="$managed"
dotnet build tools/MoonscraperAudit/Instrument/Instrument.csproj -c Release
dotnet tools/MoonscraperAudit/Instrument/bin/Release/net10.0/Instrument.dll \
  "$managed" tools/MoonscraperAudit/Probe/bin/Release/netstandard2.0/MoonscraperPreservationProbe.dll \
  > "$results/../instrumentation.json"
mkdir -p "$results/../home"
(
  cd "$app"
  HOME="$results/../home" XDG_CONFIG_HOME="$results/../home/.config" \
    MOONSCRAPER_AUDIT_OUTPUT="$results" \
    './Moonscraper Chart Editor.x86_64' -force-glcore -screen-fullscreen 0 \
    -logFile "$results/../player.log"
)
uv run --with mido python tools/MoonscraperAudit/fixtures.py report "$results"
```

Use absolute paths and a fresh results folder. The instrumenter keeps `Assembly-CSharp.dll.original` and refuses to instrument a copy twice. The probe uses a separate home/config directory.

`report.json` records input/output messages, in-memory snapshots, file hashes, save warnings, and checks against the observed 1.5.13 results. A `true` reproduction check means the described behavior occurred, including the unwanted changes. It does not mean the editor preserves that field correctly.

The fixture also exposes a MIDI header count mismatch when an empty unknown track is imported. The independent reader records declared and actual track counts and decodes each complete track chunk. It does not repair the exported file. A strict whole-file Mido read rejects that file with `EOFError`.
