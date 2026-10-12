namespace Gh3Skin;

// Every environment variable that the mod reads. They are for tests, captures, and checks; normal play needs none of
// them. docs/VERIFICATION.md#runtime-variables describes the values.
internal static class SkinEnvironment
{
    // Choices that override the picker.
    public const string Stage = "GH3_SKIN_STAGE";
    // Followed by the role in upper case: GH3_SKIN_CHARACTER_GUITARIST, GH3_SKIN_INSTRUMENT_BASSIST, ...
    public const string CharacterPrefix = "GH3_SKIN_CHARACTER_";
    public const string InstrumentPrefix = "GH3_SKIN_INSTRUMENT_";
    public const string VenueTrack = "GH3_SKIN_VENUE_TRACK";
    public const string GeneratedDirection = "GH3_SKIN_GENERATED_DIRECTION";
    public const string ReduceFlashing = "GH3_SKIN_REDUCE_FLASHING";
    public const string LipSync = "GH3_SKIN_LIPSYNC";
    public const string Camera = "GH3_SKIN_CAMERA";
    public const string CameraShot = "GH3_SKIN_CAMERA_SHOT";

    // Captures and display overrides (SkinOptions).
    public const string CaptureAfter = "GH3_SKIN_CAPTURE_AFTER";
    public const string CaptureTimes = "GH3_SKIN_CAPTURE_TIMES";
    public const string CaptureClock = "GH3_SKIN_CAPTURE_CLOCK";
    public const string CaptureDirectory = "GH3_SKIN_CAPTURE_DIR";
    public const string Screenshot = "GH3_SKIN_SCREENSHOT";
    public const string PreviewPower = "GH3_SKIN_PREVIEW_POWER";
    public const string PreviewRock = "GH3_SKIN_PREVIEW_ROCK";
    public const string ForcePower = "GH3_SKIN_FORCE_POWER";
    public const string ForceRock = "GH3_SKIN_FORCE_ROCK";
    public const string ForcePowerAtCombo = "GH3_SKIN_FORCE_POWER_AT_COMBO";
    public const string ForceCombo = "GH3_SKIN_FORCE_COMBO";
    public const string ForceComboBreak = "GH3_SKIN_FORCE_COMBO_BREAK";
    public const string ForceWhammy = "GH3_SKIN_FORCE_WHAMMY";
    public const string ForceStarPowerActive = "GH3_SKIN_FORCE_STAR_POWER_ACTIVE";
    public const string KeepStockGems = "GH3_SKIN_KEEP_STOCK_GEMS";
    public const string KeepStockFlames = "GH3_SKIN_KEEP_STOCK_FLAMES";

    // Debug views of the stage.
    public const string DebugView = "GH3_SKIN_DEBUG_VIEW";
    public const string DebugViewSeconds = "GH3_SKIN_DEBUG_VIEW_SECONDS";
    public const string DebugViewHead = "GH3_SKIN_DEBUG_VIEW_HEAD";
    public const string DebugViewRole = "GH3_SKIN_DEBUG_VIEW_ROLE";
    public const string DebugShow = "GH3_SKIN_DEBUG_SHOW";
    public const string DebugHairLayer = "GH3_SKIN_DEBUG_HAIR_LAYER";

    // Measurements and checks.
    public const string Seed = "GH3_SKIN_SEED";
    public const string Trace = "GH3_SKIN_TRACE";
    public const string TraceCharge = "GH3_SKIN_TRACE_CHARGE";
    public const string Timing = "GH3_SKIN_TIMING";
    public const string TimingReloads = "GH3_SKIN_TIMING_RELOADS";
    public const string Profile = "GH3_SKIN_PROFILE";
    public const string PickerTest = "GH3_SKIN_PICKER_TEST";

    // Workarounds for crash tests and other installs.
    public const string HookGuard = "GH3_SKIN_HOOK_GUARD";
    public const string KeepBhaptics = "GH3_SKIN_KEEP_BHAPTICS";
    // The game folder when the working folder is not the game folder (the BASS search).
    public const string GameDirectory = "GH3_CLONE_HERO_DIR";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    // The convention of the test switches: "1" turns them on.
    public static bool IsOne(string name) => Get(name) == "1";

    // The convention of the features that are on by default: "off" or "0" turns them off.
    public static bool IsNotOff(string name) => Get(name) is not ("off" or "0");
}
