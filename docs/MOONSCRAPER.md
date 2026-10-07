# GH3 authoring in Moonscraper

## Target

Use Moonscraper's normal chart-editing workflow: tracks, timed notes, text events, an inspector, and undo/redo. Preserve all GH3 data needed for playback, including features that the current mod has not implemented. Original imports and custom charts must use the same playback model.

The authoring file does not need to match GH3's binary container. `.chart` and MIDI already differ from compiled `.mid.qb` files. The requirement is to preserve meaning, timing, parameters, and references through import, edit, save, and playback. A reduced set of generic camera or lighting commands is not sufficient.

Native PAK loading and the [one-camera edit tool](NATIVE-GH3.md) are compatibility experiments. Authors do not need a PAK beside their chart. JSON stage-event files are an existing runtime feature, not the planned Moonscraper authoring requirement.

## What existing tools do

The source audit uses Moonscraper 1.x commit `7b4a7eab62e286b2a7bc2b21d2c22d4f65ff44fd`. Source links below are relative to `Moonscraper Chart Editor/Assets/Scripts/Game/Charts/` in [that revision](https://github.com/FireFox2000000/Moonscraper-Chart-Editor/tree/7b4a7eab62e286b2a7bc2b21d2c22d4f65ff44fd).

The findings below have now been reproduced with the official **1.5.13 Linux Universal** import/export code running inside Unity. See [the measured results](MOONSCRAPER-PRESERVATION.md) and [the reproducible probe](../tools/MoonscraperAudit/README.md).

| Data | Existing representation | Finding |
| --- | --- | --- |
| Chart timing | `[Song]` resolution/offset and `[SyncTrack]` tempo/time signatures | Events use ticks; GH3 QB note rows use milliseconds. Conversion must use the tempo map. |
| Direction notes | Named chart sections or MIDI tracks | Moonscraper reads unfamiliar tracks into `unrecognisedCharts` and writes them back. This preserves raw note numbers rather than mapping them to playable frets. |
| Global text events | `[Events]`, `tick = E "text"` | Quoted text is an existing chart feature. Same-tick events are sorted by text. |
| Track-local text events | `tick = E token` in `.chart`; text meta-events in MIDI | Stock `.chart` local events are single tokens. Export replaces spaces with underscores; a general save can switch to `.msce`. Arbitrary parameter strings cannot be assumed to survive. |
| MIDI direction velocity | MIDI note-on velocity | Moonscraper's unknown-track note model discards it; the writer emits `100`. |
| MIDI unknown-note timing | Note-on and note-off messages | Confirmed: a camera note at 10 seconds with a five-second length imports at 15 seconds. A 12-tick drum note imports with zero length. |
| Compiled scripts and nested parameters | Native QB structures and bytecode | Stock note/text-event support is not a general QB document model. |

Evidence:

- `IO/Chart/ChartReader.cs`, `LoadUnrecognisedChart` and `LoadChart`: named unknown tracks, raw `N` rows, single-token local `E` events.
- `IO/Chart/ChartWriter.cs`, unknown-chart loop and `AppendNoteData`: raw note numbers and lengths are written as `N number length`.
- `IO/Chart/ChartWriter.cs`, `AppendChartEventData`: local-event space replacement and `.msce` handling.
- `IO/Midi/MidReader.cs`, `ProcessNoteEvent`: the unknown-track branch uses `absoluteTick` at note-off and calls `SanitiseSustainLength`. Both changes were measured in the released editor's importer.
- `IO/Midi/MidWriter.cs`, `GetUnrecognisedChartNoteBytes`: writes the constant `VELOCITY`, defined as `100` in `MidIOHelper.cs`.
- `Events/Event.cs` and `Events/ChartEvent.cs`, `LessThan`: same-tick text comparison.

[GH-Toolkit-NET](https://github.com/AddyMills/GH-Toolkit-NET) supplies useful existing conversion conventions. `MIDI/MidiDefs.cs` names `CAMERAS`, `LIGHTSHOW`, `ANIMS`, `DRUMS`, and `CROWD`. `SongQbFile.cs` dispatches those tracks and recognizes text commands such as `stance_a`, `jump`, `band_playfacialanim`, and `lightshow_settime`. Reuse established conventions where they preserve the source command. Its supported text-command list is also a subset, not proof of full GH3 coverage.

## Implemented track reading

Build **0.8.113** reads these direction notes directly from `.chart` and MIDI:

| Track | Runtime use |
| --- | --- |
| `CAMERAS` | Original camera note mappings |
| `LIGHTSHOW` | Original lighting/effect note mappings |
| `ANIMS` | Original fret-animation note mappings |
| `DRUMS` | Original drum-animation note mappings |
| `CROWD` | Retained note cues; crowd-note playback remains incomplete |

Legacy `ANIM`, `PERFORMANCE`, and `GH3 …` names remain accepted. Playable `PART DRUMS` or `ExpertDrums` notes are distinct from `DRUMS` animation notes.

Example using existing chart syntax:

```text
[Song]
{
  Resolution = 192
}
[SyncTrack]
{
  0 = B 120000
}
[CAMERAS]
{
  192 = N 89 384
}
```

This requests camera note `89` at 0.5 seconds with a length of one second. GH3's `CameraCuts_NoteMapping` maps `89` to `CameraCuts_SetArrayPrefix` with `prefix = cameras_drummer` and `changenow`. The editor can show “Drummer” as a label while keeping note `89`. The active venue and camera-selection rules determine the shot.

The runtime retains note lengths across tempo changes and MIDI note-on velocities. Velocity does not yet change animation strength. Unmapped light note numbers remain in the direct timeline. This parser is a playback reader, not a lossless chart editor: it does not retain every text event or every MIDI message. Channel resolution also remains limited to the current supported cue set.

The mod's regression checks compare equivalent chart and MIDI direction tracks. They cover tempo changes, chart offsets, overlapping MIDI notes on separate channels, velocity, and the separation between playable and animation drums. A separate released-editor probe confirms that standard `.chart` camera/drum note timing survives save/reopen. It also reproduces the MIDI and text-event limitations above.

## Data needed for complete support

The editor model must retain:

- Track identity, note number, start, length, and optional velocity.
- Script identity, target, typed parameters, unnamed flags, and nested arrays/structures.
- Distinct calls at the same time and their execution order.
- Known names and unresolved checksums, with their original types.
- Song-specific animation and other asset references.
- Unknown commands and fields through an unrelated edit.
- Original integer milliseconds for unedited imported events, with an explicit rule for tempo edits and tick conversion. Repeated saves must not shift untouched events.

Standard chart note rows have no velocity field. Existing text presets cannot represent every native command and nested parameter. The extended editor therefore needs a richer in-memory document and a tested serialization design for data that standard chart/MIDI conventions do not cover. Do not add an ignored fourth `N` field, encode ordering in event names, or replace original camera IDs with broad categories. The final extension syntax has not been selected.

Keeping unknown data is necessary but does not implement its behavior. Full playback also requires the original mappings and rules: performance-dependent camera and lighting choices, camera sequences and transitions, crowd actions, stage scripts, character/instrument profiles, animation layers, and effects. The current four performers and prepared stage packs are the implemented subset.

## Next implementation gate

1. **Completed:** reproduce the editor's unknown-track timing, velocity, local-text, and same-tick-order cases with its actual read/write code. The checks also cover ordinary chart-note timing, a playable guitar note, global text with spaces, and `.msce` local text as controls.
2. Fix preservation before adding direction editing controls. Add tracks and inspectors to Moonscraper's existing selection and undo model.
3. Import an original song into that model, save/reopen it, change one camera note, undo it, and compare semantic data for every track and script call. A tick-based file need not be byte-identical to a QB archive.
4. Confirm that original imports and custom authoring reach the same runtime command representation. Record every unimplemented command/parameter and its source occurrence.
5. Validate each command against source mappings and controlled playback. A different-looking frame alone is not proof of a correct camera rule.

Moonscraper 1.x has no general plugin interface in the inspected source. Its editing UI will likely require a maintained source fork. No fork changes or editor UI are included in build 0.8.113.
