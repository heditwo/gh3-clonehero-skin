# Native GH3 content

## Direction

See [GH3 event coverage](GH3-EVENT-COVERAGE.md) for the current source inventory, shared command path, typed event data, and remaining playback work.

The compatibility target is GH3's data and behavior. Authoring belongs in Moonscraper's chart workflow. Original and custom content must reach the same playback model, but their file containers can differ. Native archives provide reference data, import sources, and a possible game export target. They are not a required authoring format. See [MOONSCRAPER.md](MOONSCRAPER.md).

Build **0.8.113** implements the first song compatibility step:

- Read standalone GH3 PC `<song>_song.pak.xen` archives.
- Inspect all eight native direction sections and their note arrays.
- Change the note number in an existing camera row.
- Export the original archive with only that four-byte field updated.
- Load native song direction directly in the mod.

The editor and runtime share the readers in `src/Native/`. The editor copies the archive and changes an existing scalar. It does not rebuild the archive or compile a new QB file. Unknown entries, scripts, fields, checksums, padding, and animation files keep their original bytes.

## Native storage

Song PAKs contain `<song>.mid.qb`, optional song scripts, and song-specific assets. The `.mid.qb` file is compiled QB data. Its direction sections are `scripts`, `anim`, `triggers`, `cameras`, `lightshow`, `crowd`, `drums`, and `performance`.

Each section can contain a `<song>_<section>_notes` array and a `<song>_<section>` script-call array. Notes contain start time, note number, length, and optional velocity. Start time and length use integer milliseconds. Script calls contain `time`, a script checksum in `scr`, and optional `params`. The parameters retain QB types and unnamed flags.

Venue PAKs contain native scenery, textures, node arrays, cameras, snapshots, and scripts. Character profiles reference native meshes, skeletons, animation packages, and instruments. Shared QB scripts supply note mappings and playback rules. Full compatibility requires those rules as well as file readers.

## Inspect and edit a song

The tool needs the .NET 10 SDK. Run it from the repository root:

```bash
MISE_QUIET=1 dotnet run --project tools/Gh3SongTool -c Release -- \
  inspect /path/to/DATA/SONGS/mycurse_song.pak.xen

MISE_QUIET=1 dotnet run --project tools/Gh3SongTool -c Release -- \
  camera /path/to/DATA/SONGS/mycurse_song.pak.xen \
  1 84 89 /path/to/edited/mycurse_song.pak.xen
```

The camera arguments are the zero-based row index, expected old note, and new note. The example changes the cue at 12,453 ms from note 84 to note 89. GH3 maps note 89 to the drummer camera bank. The tool requires the expected value to match and creates a new output file. Keep the original filename in a separate output folder.

The output report contains the source and output SHA-256 hashes, changed byte offsets, and the number of unchanged archive entries. A no-op edit produces a byte-identical file. Reversing the edit restores the original bytes.

## Play the native song direction

Put exactly one `<song>_song.pak.xen` in the Clone Hero song folder, beside its playable chart and audio. Keep the native filename. No event export is required.

The native package supplies direction, original Expert guitar/bass gestures, and fretbar timing. It takes priority over `stage-events.json`, chart direction events, and the title/artist cue-library lookup. Clone Hero still reads its playable notes and audio from its supported song files. The audio and chart must use the native song's time origin. This first native path does not add an audio-edit offset or channel mixing.

This optional runtime path is a compatibility prototype. The runtime uses the native note arrays and supported script calls for cameras, lighting, body actions, movement, faces, hands, drums, and effects. It retains the complete native document. The log identifies unhandled script IDs and some inactive tracks; it is not a complete behavior-coverage report.

## Current limits

- The editor changes one camera note number. Insertion, removal, retiming, and a Moonscraper UI require further work.
- Split PAK/PAB song archives and console variants are outside this first reader's scope.
- Native venue, character, and animation-package loading require further work. The runtime still uses prepared stage, rig, and animation assets.
- There is no general QB script interpreter. Known commands use the existing playback implementation. Retained crowd/performance note tracks and unknown commands can remain inactive.
- Native velocity remains in both the loaded document and runtime note cues; the existing animation player does not yet use it. Retaining native camera definitions and behavior also requires further work.

## Checks

```bash
MISE_QUIET=1 GH3_NATIVE_DATA=/path/to/DATA \
  dotnet run --project tests/SkinLogic.Tests.csproj -c Release
```

The normal checks use a synthetic PAK with unknown content, optional velocity, and invalid pointers. With `GH3_NATIVE_DATA`, the checks also inspect the installed song archives and verify an exact My Curse camera-edit round trip. Source game files are read-only inputs.

For the local My Curse fixture:

- Original PAK: 3,186,688 bytes, nine entries.
- Original SHA-256: `8dd2ae200a90e58fd0a2602036ca7043c6639dc413b8c5a5516aa3b33a85e856`.
- Edited SHA-256: `303d18b955b12a46c96c59b335aa315d94d111856f2dcc866f3ecb049492b430`.
- Exactly one byte changes, at offset 102,135: `84` becomes `89`.
- All eight other entries and all other chart bytes are identical.
- GH-Toolkit independently decompiles both QB files; the text differs only in that camera value.
- The native reader inspects direction in all 159 song PAKs in the local installation.

The mod played both packages in Budokan. The edited package selected a drummer shot. Captures are in `/tmp/opencode/gh3-native-roundtrip/`. Playback of the edit in original GH3 was not verified. These results establish the one-field archive edit and the mod's read path, not general GH3 behavior parity or a complete QB writer.
