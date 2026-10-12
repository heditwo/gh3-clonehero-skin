namespace Gh3Skin;

// GH3 PC engine commands with constant results (from the PC command registry), for scripts that call them.

internal static class Gh3PcCommands
{
    private const string TrueCommands =
        "PrintStruct PrintStructToFile Printf ScriptUsageSoftAssert PrintScriptInfo PrintNodeArrayParsingStatus OnReload " +
        "GetCurrentGeomStats GetCurrentParticleTimes GetCurrentAnimTimes GetTime GetDate PrintConfig ResetStopwatch PrintStopwatchTime " +
        "CardIsFormatted StorageSelectorFinished NewShowStorageSelector MC_StartPS3ForceDelete MC_IsPS3ForceDeleteFinished AutoListDeleteFinished " +
        "LaunchPanelMessage LaunchLocalPanelMessage KillMessages ChangeLocalMessage LaunchConsoleMessage SetShaderOverride DumpTextures " +
        "SetDynamicLightModulationFactor Occlude_EnableDebugDraw Occlude_SetViewPos Occlude_EnableFixedView Occlude_EnableViewportCull " +
        "Occlude_SetDebugFlags ToggleMemMetrics LoadSound GetSoundBussEffects IsFmodEnabled IsNetAllowed DumpNodeFlags PlaySkaterCamAnim " +
        "PlayObjectAnim UseOnePadInFrontEnd CheckAndWriteMakerAchievement CancelLogon DumpProximTriggers OutputFModCPUUsage DumpSounds " +
        "ChangeObserverMode ForceChangeObservingState BroadcastOmnigon LastBroadcastedOmnigonWas SendChatMessage GetMemProfileSize " +
        "EnableCompactPoolProfile DisableCompactPoolProfile DumpCompactPoolProfile ClearCompactPoolProfile DumpFragments PrintEventLog " +
        "DumpCompositeObjects DumpScreenElements LoadDBG IsWinPort NotIsMacPort TogglePass EnableKeyboard DisableKeyboard GetCurrentFeelerStats " +
        "ShowCamOffset AnalyzeGlobalComponentUsage Test EnableStrictStringQuotes ProfileTime PrintObjectProfiler ResetObjectProfiler " +
        "StartObjectProfiler StopObjectProfiler ProfileHavok PrintPrefixedNodes StartProfiler StopProfiler Anim_SetDumpJobQueueThreshold " +
        "MoveProfileCursor DumpProfileStart DumpProfile DumpProfileEnd WriteAchievements ss_Test1 ss_SaveRegion ss_HideLocation ss_HidePos " +
        "ReQBRAll PrintTime PrintInteractNodeUsers GetButtonsReleased GetButtonsHeld RefreshSigninStatus UnBindGuitarControllerStatus " +
        "OutputGlobalTags DumpOpen DumpWrite DumpClose";

    private const string FalseCommands =
        "SetSaveFileName GetMostRecentSave GetMemCardSpaceAvailable GetMemCardSpaceRequired FormatCard GetCardName OverwriteCorruptMemcardContent " +
        "GetMaxTHPS4FilesAllowed ShowStorageSelector AutoListDeleteStart GetCCallstack GetLightCurrentColor AddSoundBanks ClearSoundBank " +
        "RemoveAllSoundBanks RemoveSoundGroup ClearSoundGroups SetDefaultSoundPriority SetSfxDropoff LoadTerrainSounds PauseMusic StopMusic " +
        "LoadMusicHeader LoadStreamHeader PauseStream LoadSoundRAM LoadStreamFrameAmp FreeStreamFrameAmp SetPositionalSoundSilenceOverrideMode " +
        "IsObserving LoadAsset UnloadAsset LoadSkeleton";

    public static IReadOnlyDictionary<uint, (string Name, bool Result)> Constants { get; } =
        TrueCommands.Split(' ').Select(name => (Name: name, Result: true))
            .Concat(FalseCommands.Split(' ').Select(name => (Name: name, Result: false)))
            .ToDictionary(command => Gh3Checksum.Value(command.Name));

    public static Gh3ScriptCallResult? Execute(uint command) => Constants.TryGetValue(command, out var value) ? new(value.Result) : null;
}
