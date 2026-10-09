using Gh3Skin;
using System.Buffers.Binary;
using System.Text.Json;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{name}: expected {expected}, got {actual}");
}

if (Environment.GetEnvironmentVariable("GH3_BENCH") is { } benchVenue)
{
    ScriptBench.Run(benchVenue);
    return;
}

var animation = new ReadyAnimationTrigger();
ComboReelLogicTests.Run();
HookPageGuardTests.Run();
StageLogicTests.Run();
NativeSongLogicTests.Run();
Gh3EventLogicTests.Run();
CrowdLogicTests.Run();
ActorLogicTests.Run();
AudioLogicTests.Run();
ScriptLogicTests.Run();
PcCommandTests.Run();
NativeRandomLogicTests.Run();
StageScriptLogicTests.Run();
StageScriptEventTests.Run();
ParticleLogicTests.Run();
ParticleCrossTests.Run();
ParticleCallbackTests.Run();
ParticleLodTests.Run();
ParticleMaterialTests.Run();
ParticleRenderOrderTests.Run();
ParticleRenderListTests.Run();
ParticleTechniqueTests.Run();
ParticleLightTests.Run();
LightSpatialTests.Run();
LightNativeTests.Run();
LightSceneTests.Run();
LightTransitionTests.Run();
WindLogicTests.Run();
StageVisualControlTests.Run();
TeslaNativeTests.Run();
TeslaRenderTests.Run();
StageScriptAssetTests.Run();
SongDirectionLogicTests.Run();
VenueTrackLogicTests.Run();
BandLogicTests.Run();
HairLogicTests.Run();
DirectionLogicTests.Run();
ShowFlowLogicTests.Run();
HitEffectLogicTests.Run();
DrumHandLogicTests.Run();
HandFireLogicTests.Run();
LightingLogicTests.Run();
StagePackLogicTests.Run();
PackageLogicTests.Run();
Equal(-1, animation.Frame(0, 126), "inactive before threshold");
animation.Update(0.49f, 10);
Equal(-1, animation.Frame(10, 126), "inactive below threshold");
animation.Update(0.5f, 11);
Equal(0, animation.Frame(11, 126), "starts on threshold crossing");
animation.Update(1, 12);
Equal(60, animation.Frame(12, 126), "does not restart while ready");
Equal(-1, animation.Frame(13.1f, 126), "ends while power remains ready");
animation.Update(0.25f, 14);
Equal(-1, animation.Frame(14, 126), "hidden below threshold");
animation.Update(0.5f, 15);
Equal(0, animation.Frame(15, 126), "restarts after another crossing");
animation.Reset();
Equal(-1, animation.Frame(15, 126), "reset on gameplay scene load");
animation.Update(0.75f, 20);
animation.Update(0.7f, 20.2f, active: true);
Equal(-1, animation.Frame(20.2f, 126), "activation hides the ready banner");
animation.Update(0.6f, 21, active: true);
Equal(-1, animation.Frame(21, 126), "active charge cannot trigger ready");
animation.Update(0, 22);
animation.Update(0.5f, 23);
Equal(0, animation.Frame(23, 126), "earned recharge starts another banner");

var bulbs = new StarPowerBulbAnimation();
bulbs.Update(0.49f, false, 0);
Equal(1f, bulbs.Extension(2), "small bulbs stay out before Star Power is ready");
Equal(0f, bulbs.Extension(3), "large bulbs stay inside the housing below half charge");
foreach (var fps in new[] { 30, 60, 144 })
{
    bulbs.Reset();
    for (var frame = 0; frame <= fps; frame++)
    {
        var time = frame / (float)fps;
        bulbs.Update(0.5f, false, time);
        if (bulbs.Extension(3) < bulbs.Extension(4) || bulbs.Extension(4) < bulbs.Extension(5))
            throw new Exception("Bulbs must emerge in order from the center to the right");
        if (time <= 0.2f && bulbs.Extension(4) > 0 || time <= 0.4f && bulbs.Extension(5) > 0)
            throw new Exception("Later bulbs must wait for their staggered starts");
        if (time == 0.5f && Math.Abs(bulbs.Extension(4) - 0.75f) > 0.001f)
            throw new Exception("GH3's middle bulb has one quarter of its travel left after half a second");
    }
    for (var bulb = 3; bulb < 6; bulb++)
        Equal(1f, bulbs.Extension(bulb), "all large bulbs settle at every frame rate");
    Equal(RockMeterPose.Rest, bulbs.Pose, "the meter settles without changing its layout");
}
bulbs.Reset();
bulbs.Update(0.5f, false, 0);
bulbs.Update(0.5f, false, 0.15f);
if (Math.Abs(bulbs.Pose.Scale - 1.4453f) > 0.001f || Math.Abs(bulbs.Pose.Angle - 8.88f) > 0.01f)
    throw new Exception("Meter lift must match the measured GH3 peak");
var pausedPose = bulbs.Pose;
var pausedBulb = bulbs.Extension(3);
bulbs.Update(0.5f, false, 0.15f);
Equal(pausedPose, bulbs.Pose, "pause holds the meter pose");
Equal(pausedBulb, bulbs.Extension(3), "pause holds bulb travel");
bulbs.Update(0.4f, true, 0.3f);
if (Math.Abs(bulbs.Extension(3) - 0.75f) > 0.001f || bulbs.Pose.Scale >= 0.61f)
    throw new Exception("Immediate activation must continue the entrance and meter rebound");
bulbs.Update(0.1f, true, 2);
Equal(1f, bulbs.Extension(5), "active drain below half charge keeps large bulbs out");
bulbs.Update(0, false, 3);
Equal(1f, bulbs.Extension(5), "deactivation begins retraction without a jump");
bulbs.Update(0, false, 3.2f);
if (bulbs.Extension(5) >= bulbs.Extension(3))
    throw new Exception("Retraction must start with the rightmost bulb");
var interrupted = bulbs.Extension(5);
bulbs.Update(0.5f, false, 3.2f);
Equal(interrupted, bulbs.Extension(5), "recharge preserves position during interrupted retraction");
bulbs.Update(0.5f, false, 4.1f);
Equal(1f, bulbs.Extension(5), "recharge completes another entrance");
bulbs.Update(0, false, 5);
bulbs.Update(0, false, 6);
Equal(0f, bulbs.Extension(3), "all large bulbs retract after charge ends");
Equal(0f, bulbs.Extension(5), "rightmost bulb retracts after charge ends");
bulbs.Reset();
Equal(RockMeterPose.Rest, bulbs.Pose, "scene reset clears the meter pulse");
Equal(0f, bulbs.Extension(3), "scene reset retracts large bulbs");
bulbs.Update(0.1f, true, 10);
Equal(1f, bulbs.Extension(5), "joining active gameplay starts with a settled meter");
Equal(RockMeterPose.Rest, bulbs.Pose, "joining active gameplay does not create a ready pulse");

var variables = new[]
{
    "GH3_SKIN_CAPTURE_AFTER", "GH3_SKIN_CAPTURE_TIMES", "GH3_SKIN_PREVIEW_POWER",
    "GH3_SKIN_PREVIEW_ROCK", "GH3_SKIN_FORCE_POWER", "GH3_SKIN_FORCE_ROCK",
    "GH3_SKIN_FORCE_POWER_AT_COMBO", "GH3_SKIN_KEEP_STOCK_GEMS",
    "GH3_SKIN_KEEP_STOCK_FLAMES", "GH3_SKIN_CAPTURE_DIR", "GH3_SKIN_SCREENSHOT"
};
var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    foreach (var name in variables)
        Environment.SetEnvironmentVariable(name, null);
    var normal = SkinOptions.FromEnvironment();
    Equal<float?>(null, normal.DisplayPower, "normal play uses real power");
    Equal<float?>(null, normal.DisplayRock, "normal play uses real rock");
    Environment.SetEnvironmentVariable("GH3_SKIN_PREVIEW_POWER", "0.5");
    Equal<float?>(null, SkinOptions.FromEnvironment().DisplayPower, "preview override needs capture");
    Environment.SetEnvironmentVariable("GH3_SKIN_CAPTURE_AFTER", "7");
    Environment.SetEnvironmentVariable("GH3_SKIN_CAPTURE_TIMES", "2,bad,3.5");
    Equal(2, SkinOptions.FromEnvironment().CaptureTimes.Count, "valid capture times");
    Environment.SetEnvironmentVariable("GH3_SKIN_FORCE_POWER", "1.5");
    Environment.SetEnvironmentVariable("GH3_SKIN_FORCE_POWER_AT_COMBO", "20");
    Environment.SetEnvironmentVariable("GH3_SKIN_FORCE_ROCK", "0.8");
    var forced = SkinOptions.FromEnvironment();
    Equal<float?>(1, forced.DisplayPower, "force overrides preview and clamps");
    Equal<float?>(0.8f, forced.DisplayRock, "forced rock");
    Equal(20, forced.ForcePowerAtCombo, "combo gate");
    Environment.SetEnvironmentVariable("GH3_SKIN_CAPTURE_TIMES", "3.5,2,2,NaN,Infinity,-1,bad");
    var times = SkinOptions.FromEnvironment().CaptureTimes;
    Equal(2, times.Count, "only unique finite positive capture times");
    Equal(2f, times[0], "capture times are chronological");
    Environment.SetEnvironmentVariable("GH3_SKIN_FORCE_POWER", "NaN");
    Environment.SetEnvironmentVariable("GH3_SKIN_FORCE_ROCK", "Infinity");
    Environment.SetEnvironmentVariable("GH3_SKIN_CAPTURE_AFTER", "0");
    Equal<float?>(0.5f, SkinOptions.FromEnvironment().DisplayPower, "timed captures accept preview values; invalid force is ignored");
    Equal<float?>(null, SkinOptions.FromEnvironment().DisplayRock, "nonfinite override is ignored");
    Environment.SetEnvironmentVariable("GH3_SKIN_SCREENSHOT", "capture-test.png");
    Equal(Path.GetFullPath("capture-test.png"), SkinOptions.FromEnvironment().ScreenshotPath, "portable screenshot path");
}
finally
{
    foreach (var (name, value) in previous)
        Environment.SetEnvironmentVariable(name, value);
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var tuning = HudTuning.Load(Path.Combine(root, "assets", "counter-tuning.json"));
Equal(tuning.RollAlphas.Length, tuning.RollOffsets.Length, "matched animation arrays");
foreach (var (width, height, expectedScale, expectedX, expectedCenterY, expectedBottomY) in new[]
{
    (1600, 900, 1600f / 1920, 0f, 0f, 0f),
    (2560, 1440, 2560f / 1920, 0f, 0f, 0f),
    (1920, 1200, 1f, 0f, 60f, 120f),
    (1600, 1200, 1600f / 1920, 0f, 150f, 300f),
    (3440, 1440, 1440f / 1080, 440f, 0f, 0f)
})
{
    var viewport = HudViewport.For(width, height);
    if (Math.Abs(viewport.Scale - expectedScale) > 0.001f ||
        Math.Abs(viewport.OffsetX - expectedX) > 0.001f ||
        Math.Abs(viewport.CenterOffsetY - expectedCenterY) > 0.001f ||
        Math.Abs(viewport.BottomOffsetY - expectedBottomY) > 0.001f)
        throw new Exception($"Incorrect HUD viewport at {width} x {height}: {viewport}");
}

foreach (var (width, height) in new[] { (1600, 900), (2560, 1440), (1600, 1200), (1920, 1200), (3440, 1440) })
{
    var rails = HudViewport.ForHighway(height, 0, 0, width, height, HighwayView.FieldOfView);
    if (Math.Abs(1080 * rails.Scale - height) > 0.001f ||
        Math.Abs(rails.OffsetX + 960 * rails.Scale - width / 2f) > 0.001f ||
        Math.Abs(rails.CenterOffsetY) > 0.001f)
        throw new Exception($"Highway projection must preserve its aspect ratio at {width} x {height}");
}
if (HudViewport.ForHighway(900, 0, 0, 1600, 900, 40).Scale >= HudViewport.ForHighway(900, 0, 0, 1600, 900, HighwayView.FieldOfView).Scale)
    throw new Exception("Wider camera field of view must reduce rail size");
var letterbox = HudViewport.ForHighway(1200, 0, 0, 1600, 900, HighwayView.FieldOfView);
Equal(300f, letterbox.CenterOffsetY, "rails follow the bottom-aligned game camera at 4:3");
Equal(0f, letterbox.OffsetX, "4:3 camera preserves horizontal rail placement");
Equal(1600f / 1920, letterbox.Scale, "rails use camera height, not full screen height");

(float X, float Y) ProjectHighway(float x, float z)
{
    var pitch = HighwayView.PitchRadians;
    var dy = -HighwayView.CameraHeight;
    var dz = z - HighwayView.CameraZ;
    var depth = dz * MathF.Cos(pitch) - dy * MathF.Sin(pitch);
    var up = dy * MathF.Cos(pitch) + dz * MathF.Sin(pitch);
    return (800 + HighwayView.Focal * x / depth, 450 - HighwayView.Focal * up / depth);
}
foreach (var time in new[] { -0.1f, 0, 0.5f, 1, 1.4f })
{
    var z = HighwayView.StrikeZ + HighwayView.NoteSpeed * time;
    var (_, y) = ProjectHighway(0, z);
    if (Math.Abs(y - HighwayView.ScreenY(time)) > 0.05f)
        throw new Exception($"Player camera puts a gem {time} s ahead at y {y}, GH3 at {HighwayView.ScreenY(time)}");
}
var (strikeLeft, strikeY) = ProjectHighway(-2 * HighwayView.LaneWidth, HighwayView.StrikeZ);
var (strikeRight, _) = ProjectHighway(2 * HighwayView.LaneWidth, HighwayView.StrikeZ);
if (Math.Abs(strikeY - HighwayView.StrikeY) > 0.05f || Math.Abs((strikeRight - strikeLeft) / 4 - HighwayView.LaneSpacing) > 0.05f)
    throw new Exception($"GH3 frets are at y {HighwayView.StrikeY}, {HighwayView.LaneSpacing} px apart; camera gives {strikeY}, {(strikeRight - strikeLeft) / 4}");
var (_, topY) = ProjectHighway(0, HighwayView.NeckStartZ + HighwayView.NeckLength * HighwayView.NeckSpriteLength);
Equal(MathF.Round(HighwayView.TopY, 1), MathF.Round(topY, 1), "the board ends at GH3's highway top");
Equal(6f, MathF.Round(HighwayView.ScrollSpeed(7), 4), "GH3's board moves one repeat per second at the default note speed");

foreach (var active in new[] { false, true })
    for (var multiplier = 1; multiplier <= 4; multiplier++)
    {
        var actual = active ? multiplier * 2 : multiplier;
        Equal(multiplier, HudState.BaseMultiplier(actual, active), "streak multiplier is separate from Star Power");
        var texture = HudState.MultiplierTexture(actual, active);
        if (!File.Exists(Path.Combine(root, "assets", texture + ".dds")))
            throw new Exception($"Missing multiplier texture: {texture}");
    }
Equal("HUD_score_nixie_8b", HudState.MultiplierTexture(8, true), "active 8x art");
Equal(0f, HudState.UnitValue(float.NaN), "invalid game value is finite");
Equal(0f, HudState.RockNeedleAngle(0.5f), "neutral rock points up");
Equal(43f, HudState.RockNeedleAngle(-0.1f), "failed rock stops at the red endpoint");
Equal(-43f, HudState.RockNeedleAngle(1.025f), "native rock overshoot stops at the green endpoint");
var meter = new RockMeterState();
var highwayAnimation = new StarPowerHighwayAnimation();
highwayAnimation.Update(false, 1);
Equal(-1, highwayAnimation.Frame, "charged but inactive Star Power has no highway effect");
highwayAnimation.Update(true, 2);
Equal(0, highwayAnimation.Frame, "activation starts the highway lightning");
highwayAnimation.Update(true, 2.21f);
Equal(3, highwayAnimation.Frame, "GH3 highway lightning uses 15 frames per second");
highwayAnimation.Update(true, 2.21f);
Equal(3, highwayAnimation.Frame, "paused game time holds the lightning frame");
highwayAnimation.Update(true, 2 + 8f / 15 + 0.001f);
Equal(0, highwayAnimation.Frame, "highway lightning loops after eight frames");
highwayAnimation.Update(false, 3);
Equal(-1, highwayAnimation.Frame, "drained Star Power removes the highway effect immediately");
highwayAnimation.Update(true, 4);
Equal(0, highwayAnimation.Frame, "second activation restarts the effect");
highwayAnimation.Reset();
Equal(-1, highwayAnimation.Frame, "scene reset hides the effect");
highwayAnimation.Update(true, 10);
highwayAnimation.Update(true, 5);
Equal(0, highwayAnimation.Frame, "a backward clock starts a new effect");
foreach (var (health, expectedRed, expectedGreen) in new[]
{
    (0f, 1f, 0f), (0.4f, 0.78f, 0f), (0.45f, 0.39f, 0f),
    (0.5f, 0f, 0f), (0.55f, 0f, 0.37f), (0.6f, 0f, 0.74f), (1f, 0f, 1f)
})
{
    meter.Reset(health);
    if (Math.Abs(meter.BackgroundRed - expectedRed) > 0.0001f ||
        Math.Abs(meter.BackgroundGreen - expectedGreen) > 0.0001f)
        throw new Exception("Background must blend across the center of the dial");
}
meter.Reset(0.58f);
Equal("yellow", meter.Color, "the previous green threshold still points into yellow");
meter.Reset(0.7f);
Equal("green", meter.Color, "70 percent health is green in Clone Hero");
meter.Reset(0.73f);
Equal("green", meter.Color, "needle is green after the green divider");
meter.Reset(0.29f);
Equal("red", meter.Color, "29 percent health is red in Clone Hero");
meter.Reset(0.26f);
Equal("red", meter.Color, "needle is red after the red divider");
Equal(19.5f, HudState.RockNeedleAngle(1f / 3), "Clone Hero's lower threshold meets the red divider");
Equal(-18.5f, HudState.RockNeedleAngle(2f / 3), "Clone Hero's upper threshold meets the green divider");
foreach (var (health, expectedColor) in new[]
{
    (MathF.BitDecrement(1f / 3), "red"), (1f / 3, "red"), (MathF.BitIncrement(1f / 3), "yellow"),
    (MathF.BitDecrement(2f / 3), "yellow"), (2f / 3, "yellow"), (MathF.BitIncrement(2f / 3), "green")
})
{
    meter.Update(health);
    Equal(expectedColor, meter.Color, "exact native threshold inclusivity");
}
var lastMappedAngle = 43f;
for (var index = 0; index <= 10000; index++)
{
    var health = index / 10000f;
    var mappedAngle = HudState.RockNeedleAngle(health);
    if (mappedAngle > lastMappedAngle || lastMappedAngle - mappedAngle > 0.012f)
        throw new Exception("Calibrated needle mapping must be continuous and monotonic");
    lastMappedAngle = mappedAngle;
    meter.Update(health);
    Equal(health <= 1f / 3 ? "red" : health <= 2f / 3 ? "yellow" : "green", meter.Color,
        "needle agrees with native health on each update across the full range");
}
meter.Reset(0);
Equal(true, meter.Update(1.04f), "a health change updates the needle immediately");
Equal(-43f, meter.Angle, "full health reaches the green endpoint in one update");
Equal("green", meter.Color, "the lamp follows the current health immediately");
Equal(1f, meter.BackgroundGreen, "the background follows the current needle position");
Equal(false, meter.Update(1.04f), "unchanged health holds the needle still");
Equal(true, meter.Update(0), "a reversal updates the needle immediately");
Equal(43f, meter.Angle, "a reversal reaches the red endpoint in one update");
Equal("red", meter.Color, "the lamp follows a health reversal immediately");
Equal(1f, meter.BackgroundRed, "the background follows a health reversal");
meter.Reset();
Equal(0f, meter.Angle, "restart restores the neutral needle");
Equal(false, meter.Update(0.5f), "neutral health holds the reset needle still");
foreach (var power in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
{
    var fills = Enumerable.Range(0, 6).Select(bulb => HudState.StarPowerBulbFill(power, bulb)).ToArray();
    Equal(power * 6, fills.Sum(), "bulbs show the complete native charge range");
    for (var bulb = 1; bulb < fills.Length; bulb++)
        if (fills[bulb] > 0 && fills[bulb - 1] != 1)
            throw new Exception("Star Power bulbs must fill from left to right");
}
Equal(true, GameCompatibility.Supports("v1.1.0.6142-final"), "verified game version");
Equal(false, GameCompatibility.Supports("v1.1.0.6143-final"), "unverified fields are not used on another version");
Equal(true, GameCompatibility.Problem("v1.1.0.6142-final", "LinuxPlayer") == null, "verified Linux build");
Equal(true, GameCompatibility.Problem("1.1.0.6142", "WindowsPlayer") == null, "verified Windows build");
Equal(true, GameCompatibility.Problem("v1.1.0.6142-final", "OSXPlayer") != null, "unverified macOS build");
Equal(true, GameCompatibility.Problem("v1.1.0.6143-final", "WindowsPlayer") != null, "unverified Windows version");
Equal(new GemKind(0), GemKind.FromSprites("spr_newnotes_strip4_0", "spr_newnotes_strip4_2"), "regular note");
Equal(new GemKind(1), GemKind.FromSprites("spr_newnotes_strip4_1", "spr_newnotes_strip4_2"), "HOPO");
Equal(new GemKind(2), GemKind.FromSprites("spr_star_notes_cap_strip4_0", "spr_star_notes_strip1_3"), "phrase note");
Equal(new GemKind(3), GemKind.FromSprites("spr_star_notes_cap_strip4_2", "spr_star_notes_strip1_3"), "phrase HOPO");
Equal(new GemKind(4), GemKind.FromSprites("spr_newnotes_strip4_0", "spr_newnotes_strip4_3"), "tap uses BetterGH3 art");
Equal(new GemKind(4), GemKind.FromSprites("spr_newnotes_strip4_1", "spr_newnotes_strip4_3"), "tap takes priority over HOPO cap");
for (var frame = 0; frame < 16; frame++)
    Equal(new GemKind(5), GemKind.FromSprites("spr_star_notes_cap_strip4_0", $"spr_star_notes_strip2_{frame}"), "animated phrase tap");
Equal(GemKind.Stock, GemKind.FromSprites("spr_open_notes_strip5_0", ""), "pooled open note restores stock sprites");
Equal(GemKind.Stock, GemKind.FromSprites("spr_open_notes_strip5_0", "spr_newnotes_strip4_3"), "open tap keeps stock art");
Equal(GemKind.Stock, GemKind.FromSprites("", ""), "empty pooled note hides the custom gem");
Equal(GemKind.Stock, GemKind.FromSprites("", "spr_newnotes_strip4_3"), "stale tap body does not revive an empty note");
Equal(true, SustainState.TryFrame("spr_sustain_strip6_0", out var heldFrame) && heldFrame == 0, "held sustain frame");
Equal(false, SustainState.TryFrame("GH3_sustain-green", out _), "replacement sustain sprite keeps the last native frame");
Equal(false, SustainState.TryFrame("spr_sustain_strip6_6", out _), "unknown sustain frame");
Equal(SustainVariant.Blue, SustainState.Variant(1, 3, false), "unheld sustain uses its lane texture");
Equal(SustainVariant.Star, SustainState.Variant(0, 3, true), "Star Power sustain uses the star whammy texture");
Equal(SustainVariant.Dead, SustainState.Variant(5, 3, true), "dropped sustain uses the dead whammy texture");
var starWhammy = new StarWhammy();
starWhammy.Step(0.5f);
Equal(false, starWhammy.Star, "star whammy ignores the whammy position on its first frame");
for (var frame = 0; frame < 4; frame++) starWhammy.Step(0.05f);
Equal(false, starWhammy.Star, "star whammy waits for 5 moving frames");
starWhammy.Step(0.05f);
Equal(true, starWhammy.Star, "star whammy turns the bar blue after 5 moving frames");
for (var frame = 0; frame < 8; frame++) starWhammy.Step(0);
Equal(true, starWhammy.Star, "star whammy holds while whammy_on counts down and 4 still frames pass");
starWhammy.Step(0);
Equal(false, starWhammy.Star, "star whammy returns to the lane texture after 5 still frames");
starWhammy.Step(0.02f);
Equal(false, starWhammy.Star, "changes of 0.03 or less do not count as whammy");
Equal(1f, SustainState.WhammyWidth(0), "resting whammy keeps the bar width");
Equal(2f, SustainState.WhammyWidth(1), "full whammy doubles the bar width (whammy_scale = len + 1)");
Equal(1.5f, SustainState.WhammyWidth(0.5f), "half whammy widens the bar by half");
Equal(1, SustainState.WhammyChannel(0), "green lane reads whammy channel 1");
Equal(5, SustainState.WhammyChannel(4), "orange lane reads whammy channel 5");
foreach (var (time, frame) in new[] { (0.0, 0), (0.033, 0), (0.034, 1), (0.5, 15), (0.534, 0), (1.0, 14) })
    Equal(frame, GemAnimation.Frame(time), "GH3 star animation uses a 16-frame atlas at 30 frames per second");
foreach (var fps in new[] { 30, 60, 144 })
{
    var frames = Enumerable.Range(0, fps + 1).Select(frame => GemAnimation.Frame(frame / (double)fps)).ToArray();
    Equal(14, frames[^1], "gem animation has the same speed at each display frame rate");
    Equal(16, frames.Distinct().Count(), "gem animation uses every atlas frame");
}

var gemTextures = new List<string> { "active-note", "active-glow", "active-tap" };
foreach (var variant in new[] { "star", "star-glow", "star-tap" })
    for (var frame = 0; frame < GemAnimation.FrameCount; frame++)
        gemTextures.Add($"active-{variant}-{frame}");
foreach (var lane in new[] { "green", "red", "yellow", "blue", "orange" })
{
    gemTextures.Add($"{lane}-tap");
    for (var frame = 0; frame < 16; frame++)
        gemTextures.Add($"{lane}-star-tap-{frame}");
}
var gemHeader = new byte[24];
foreach (var texture in gemTextures)
{
    using var image = File.OpenRead(Path.Combine(root, "assets", texture + ".png"));
    image.ReadExactly(gemHeader);
    Equal(128, BinaryPrimitives.ReadInt32BigEndian(gemHeader.AsSpan(16)), "gem frame width");
    Equal(64, BinaryPrimitives.ReadInt32BigEndian(gemHeader.AsSpan(20)), "gem frame height");
}

using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets/ready-animation.json")));
Equal(60, manifest.RootElement.GetProperty("fps").GetInt32(), "ready animation frame rate");
Span<byte> header = stackalloc byte[24];
foreach (var (sequence, prefix, expected) in new[]
{
    ("frames", "ready-animation", 126),
    ("lightning", "ready-lightning", 51)
})
{
    var frames = manifest.RootElement.GetProperty(sequence).EnumerateArray().ToArray();
    Equal(expected, frames.Length, $"{sequence} frame count");
    for (var index = 0; index < frames.Length; index++)
    {
        using var image = File.OpenRead(Path.Combine(root, "assets", $"{prefix}-{index:000}.png"));
        image.ReadExactly(header);
        Equal("89504e470d0a1a0a", Convert.ToHexStringLower(header[..8]), "PNG signature");
        Equal(frames[index].GetProperty("width").GetInt32(), BinaryPrimitives.ReadInt32BigEndian(header[16..20]), "frame width");
        Equal(frames[index].GetProperty("height").GetInt32(), BinaryPrimitives.ReadInt32BigEndian(header[20..24]), "frame height");
    }
}
Console.WriteLine("Skin logic tests passed");
