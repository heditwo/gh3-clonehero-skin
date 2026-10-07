# Moonscraper 1.5.13 preservation results

Confirmed on **2026-10-06**, using the official **1.5.13 Linux Universal** release in **Unity 2018.4.23f1**.

## Results

Each fixture was imported and saved without editing its events. MIDI uses 480 ticks per beat at 120 BPM, so 960 ticks equal one second.

| Check | Input | Measured output | Result |
| --- | --- | --- | --- |
| MIDI camera timing | `CAMERAS` note 89, start 10 s, length 5 s | Imported and saved at 15 s, length 5 s | Start time changes to the original note-off time. Reopening the exported MIDI moves it again to 20 s. |
| Short MIDI drum duration | `DRUMS` note 70, length 12 ticks (12.5 ms) | In-memory length is zero; MIDI export emits a one-tick note | The importer removes the source length. |
| MIDI direction velocity | Drum velocities 45 and 75; camera velocity 75 | Saved velocity 100 | Source velocity is lost. |
| Same-time global text | `z_first`, then `a_second`, both at tick 480 | `a_second`, then `z_first` | Import sorts the events by text. Confirmed for MIDI and chart input. |
| Same-time local text | `z_first`, then `a_second`, both at tick 1920 | `a_second`, then `z_first` | Track-local text also changes order. |
| Local text exported to `.chart` | `lightshow_settime 0.25` | `lightshow_settime_0.25`, with a warning | The exported text changes. |
| Local text saved normally | `lightshow_settime 0.25` | Writer selects `.msce`; reopening preserves the space | The native editor format preserves this text. This is a chart-format limitation, not loss in every save mode. |

The first two problems occur in the MIDI importer for tracks that Moonscraper does not recognize as playable instruments.

## Controls that passed

- A `.chart` camera note kept its start at 10 seconds and length of five seconds through save/reopen.
- A `.chart` drum note kept its start and 12-tick length through save/reopen.
- A playable `PART GUITAR` MIDI note kept its start at 10 seconds and length of five seconds.
- A global text event, `stage camera drummer`, kept its spaces in chart export.
- The local text event `lightshow_settime 0.25` kept its space through automatic `.msce` save/reopen.

These results narrow the required editor fixes. They do not show a timing problem with ordinary chart note rows.

## Additional finding

The fixture has a named `TEMPO` track with no notes. Moonscraper imports it as an empty unknown chart. Its MIDI writer counts that chart in the header but omits the empty track body. The output declares six tracks and contains five. A strict Mido read raises `EOFError`.

The independent report reads complete MIDI chunks and records both counts. It does not change the output file. The released Moonscraper importer accepts its own output, which lets the probe measure the second camera-time shift as well.

## Test method

The probe calls the release's `MidReader.ReadMidi`, `MidWriter.WriteToFile`, `ChartReader.ReadChart`, and `ChartWriter.Write`. It records the in-memory song after import and after reopening. Mido independently decodes the input/output MIDI messages. The saved chart text and writer warnings are also recorded.

To start the automated checks inside the Unity player, a temporary application copy has one call added to `ChartEditor.Update`. The instrumenter compares all method bodies before and after: only `ChartEditor.Update` changes; the other **4,693 methods** are unchanged. The parser, writer, model, and sorting methods are the shipped methods. Menu dialogs are not part of this automated test.

The test ran in a temporary home/config directory on an Xvfb display with VirtualGL. No user charts were used. The helper projects build with zero warnings/errors. All **13 reproduction/control checks** completed with the expected results.

| Artifact | SHA-256 |
| --- | --- |
| Official release archive | `feff20925a4b4be6f3cff4b9173328d5f665d19707bafdc6a0d2559529831687` |
| Original `Assembly-CSharp.dll` | `d303b7d8226d9fbedc3491d2aeac334f5b96081dd5456b2813bc54e68c6f3191` |
| Instrumented assembly | `87b40fc1f57f94df183bfad11c2ea7721a96a252ad58b0bee1fc34d895d6fcfb` |
| Probe assembly | `f10107f2bba6db0fcfdbbf3b6855d2d4efd29fe7f4d84c3f44542fb49b11fc29` |

Reproduction code and commands: [tools/MoonscraperAudit](../tools/MoonscraperAudit/README.md).

Local evidence: `/tmp/opencode/moonscraper-preservation/verified/`. The `results/` folder contains original fixtures, saved MIDI/chart/MSCE files, model snapshots, writer warnings, and `report.json`. The parent folder contains the Unity log and `instrumentation.json`.
