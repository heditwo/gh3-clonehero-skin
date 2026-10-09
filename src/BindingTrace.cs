using Il2Cpp;
using MelonLoader;

namespace Gh3Skin;

// Verification only (GH3_SKIN_TRACE=1): logs every value the skin reads through obfuscated Clone Hero members twice per
// song second, so runs on different platforms can be compared at the same song times.
internal static class BindingTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("GH3_SKIN_TRACE") == "1";
    public static int StageNoteEvents;
    public static int FlameLanes;
    private static double _next = double.NegativeInfinity;
    private static bool _paused;
    private static bool _charged;
    private static float _logged;
    // The command-line bot earns no Star Power; this fills the native charge once so both platforms can be compared.
    private static readonly double? Charge = double.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_TRACE_CHARGE"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var charge) ? charge : null;

    public static void Reset()
    {
        _next = double.NegativeInfinity;
        _paused = false;
        _charged = false;
        StageNoteEvents = 0;
        FlameLanes = 0;
    }

    public static void Update(BasePlayer player, GameplayState state, double songTime, string audio)
    {
        if (Charge is { } fraction && !_charged && songTime >= 20)
        {
            player.engine.field_Public_Int64_0 = (long)(fraction * player.engine.field_Public_Int64_2);
            _charged = true;
            MelonLogger.Msg($"[trace] charge set to {fraction} at {songTime:F3}");
        }
        var paused = player.gameManager.isPaused;
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (songTime < _next && paused == _paused && !(paused && now > _logged + 1)) return;
        _paused = paused;
        _logged = now;
        _next = Math.Floor(songTime * 2 + 1) / 2;
        var notes = CloneHeroBindings.Notes(player.engine);
        int count = notes?.Count ?? 0, hit = 0, lanes = 0;
        for (var index = 0; index < count; index++)
        {
            var note = notes![index];
            if ((note.field_Public_EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique_0 & ObjectPublicObInObDoSiDoUIInBoInUnique.EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique.Hit) == 0) continue;
            hit++;
            lanes += CloneHeroBindings.NoteLanes(note);
        }
        MelonLogger.Msg($"[trace] t={songTime:F3} score={state.Score} streak={state.Combo} x{state.Multiplier} rock={state.Rock:F4} " +
            $"power={state.StarPower:F4} active={state.StarPowerActive} notes={count} hit={hit} lanes={lanes} " +
            $"stageEvents={StageNoteEvents} flameLanes={FlameLanes} paused={paused} audio={audio}");
    }
}
