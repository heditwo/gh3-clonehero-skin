# Clone Hero game-state bindings

These bindings apply only to Linux `v1.1.0.6142-final`. `GameCompatibility` checks the game version before the mod reads these fields. Generated names can change on another game build.

| Value | Generated member | Evidence |
| --- | --- | --- |
| Total score | `ScoreManager.field_Private_Int32_4` | Existing capture mapping and live score display |
| Streak | `ScoreManager.field_Private_Int32_2` | Live bot run, including multiplier transitions |
| Rock | `engine.field_Protected_Single_0` | Native offset `0x70`; native hit/miss routine changes this value |
| Charge | `engine.prop_Single_0` | Native getter `0x5556790` divides charge at `0x68` by capacity at `0x1b0` |
| Actual multiplier | `engine.prop_Int32_0` | Native getter `0x5557270` reads `0x48` |
| Base multiplier | `engine.prop_Int32_1` | Native getter `0x5557020` halves the actual multiplier when active |
| Star Power active | `engine.field_Public_Boolean_0` | Native offset `0x42`; activation sets it and doubles `0x48`; expiry clears it and halves `0x48` |

`engine` is `Il2Cpp.ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique` in the generated `Il2CppStrikeCore.dll`. Native addresses above are relative to `GameAssembly.so`.

The HUD reads the engine charge directly. `SPBar.field_Private_Single_0` is a display cache and can be `-1`; it is not the charge source.

## Native health colors

`HealthContainer.field_Private_Static_Single_0` is `1f / 3`; `field_Private_Static_Single_1` is `2f / 3`. The native classifier `Method_Private_EnumNPrivateSealedvaHiMeLo4vUnique_Single_PDM_1` returns `Low` at or below the first threshold, `Medium` above the first and at or below the second, and `High` above the second. Other similarly named generated methods are obfuscation variants and do not return the same results.

The classifier was checked against the glow colors produced by the native `Method_Public_Void_Single_0` health update. The inspection output is `/tmp/opencode/gh3-health-colors/result.json`. The skin maps these native thirds to the red/yellow and yellow/green boundaries of the GH3 dial.

The bot's native charge-award routine (`0x5556510`) returns without an award for engine modes 1 and 2. Thus a bot capture with a forced visual charge cannot prove that a player earned charge. Integration tests can temporarily allow native awards in this routine, then call the native activation routine (`0x55556a0`). Such tests must be identified as instrumented runs.

The native hit/miss health routine is `Method_Protected_Void_Boolean_0` at `0x5558de0`. A hit adds `0x74`, or `0x7c` while Star Power is active; a miss subtracts `0x78` from rock at `0x70`. The miss routine `Method_Public_Virtual_New_Void_Boolean_Boolean_0` at `0x5558c80` also resets the streak and calls this health routine. The temporary meter test uses this miss routine. The skin does not call it.

Scene changes clear cached Unity objects and destroy the old HUD. A backward song-time jump resets the HUD and renderer state for a restart or practice seek. The mod finds the game manager during setup, then uses its player and score references. Renderer discovery runs on setup and when the player's pool size changes.

## Stage bindings

`GameplayStage` uses `GameManager.songTime` in seconds. It does not apply a second song-speed factor. The cue reader gets `folderPath`, `ChartPath`, `Name_StrippedTags`, and `Artist_StrippedTags` from `GlobalVariables.instance.currentSongEntry`. A live test matched **My Curse / Killswitch Engage** to the imported original lightshow through these fields.

The stage camera draws before `BasePlayer.mainCamera`. Stage setup saves the player's camera clear flags and sets them to `Depth`. This stops the stage depth buffer from hiding the highway. Teardown restores the saved flags and camera layer masks. See [stage checks](STAGES.md#checks-on-2026-10-05) for the clock and scene tests.
