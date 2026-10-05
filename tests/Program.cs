using Gh3Skin;
using System.Buffers.Binary;
using System.Text.Json;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{name}: expected {expected}, got {actual}");
}

var animation = new ReadyAnimationTrigger();
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
var tuning = HudTuning.Load(Path.Combine(root, "counter-tuning.json"));
Equal(39, tuning.LastReelWidth, "last reel width");
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
    var rails = HudViewport.ForHighway(height, 0, 0, width, height, 20);
    if (Math.Abs(1080 * rails.Scale - height) > 0.001f ||
        Math.Abs(rails.OffsetX + 960 * rails.Scale - width / 2f) > 0.001f ||
        Math.Abs(rails.CenterOffsetY) > 0.001f)
        throw new Exception($"Highway projection must preserve its aspect ratio at {width} x {height}");
}
if (HudViewport.ForHighway(900, 0, 0, 1600, 900, 40).Scale >= HudViewport.ForHighway(900, 0, 0, 1600, 900, 20).Scale)
    throw new Exception("Wider camera field of view must reduce rail size");
var letterbox = HudViewport.ForHighway(1200, 0, 0, 1600, 900, 20);
Equal(300f, letterbox.CenterOffsetY, "rails follow the bottom-aligned game camera at 4:3");
Equal(0f, letterbox.OffsetX, "4:3 camera preserves horizontal rail placement");
Equal(1600f / 1920, letterbox.Scale, "rails use camera height, not full screen height");

foreach (var active in new[] { false, true })
    for (var multiplier = 1; multiplier <= 4; multiplier++)
    {
        var actual = active ? multiplier * 2 : multiplier;
        Equal(multiplier, HudState.BaseMultiplier(actual, active), "streak multiplier is separate from Star Power");
        var texture = HudState.MultiplierTexture(actual, active);
        if (!File.Exists(Path.Combine(root, "assets", texture + ".png")))
            throw new Exception($"Missing multiplier texture: {texture}");
    }
Equal("HUD_score_nixie_8b", HudState.MultiplierTexture(8, true), "active 8x art");
Equal(0f, HudState.UnitValue(float.NaN), "invalid game value is finite");
Equal(0f, HudState.RockNeedleAngle(0.5f), "neutral rock points up");
Equal(43f, HudState.RockNeedleAngle(-0.1f), "failed rock stops at the red endpoint");
Equal(-43f, HudState.RockNeedleAngle(1.025f), "native rock overshoot stops at the green endpoint");
var meter = new RockMeterState();
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
    meter.Reset(health);
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
    meter.Reset(health);
    Equal(health <= 1f / 3 ? "red" : health <= 2f / 3 ? "yellow" : "green", meter.Color,
        "settled needle agrees with native health across the full range");
}
var halfSecondAngles = new List<float>();
foreach (var fps in new[] { 30, 60, 144 })
{
    meter.Reset(0);
    var intermediate = 0;
    for (var frame = 0; frame < fps * 2; frame++)
    {
        var previousAngle = meter.Angle;
        meter.Update(1.04f, 1f / fps);
        if (meter.Angle > previousAngle || meter.Angle < -43 || previousAngle - meter.Angle > 90f / fps)
            throw new Exception($"Needle jumps or overshoots at {fps} fps");
        if (meter.Angle < 40 && meter.Angle > -40)
            intermediate++;
        if (meter.Color == "green" && meter.Angle > -18.5f)
            throw new Exception("Green must follow the displayed needle, not raw health");
        if (frame == fps / 2 - 1)
            halfSecondAngles.Add(meter.Angle);
    }
    if (intermediate < fps / 2 || Math.Abs(meter.Angle + 43) > 0.001f)
        throw new Exception($"Needle must traverse the dial and settle at {fps} fps");
}
if (halfSecondAngles.Max() - halfSecondAngles.Min() > 0.15f)
    throw new Exception("Needle response must remain consistent across frame rates");
meter.Reset();
meter.Update(1, 1f / 60);
Equal("yellow", meter.Color, "full native health does not turn the dial green before the needle arrives");
var pausedAngle = meter.Angle;
meter.Update(1, 0);
Equal(pausedAngle, meter.Angle, "paused needle stays still");
meter.Update(1, 2);
if (pausedAngle - meter.Angle > 3)
    throw new Exception("A stalled frame must not jump the needle to the end");
meter.Reset();
Equal(0f, meter.Angle, "restart restores the neutral needle");
Equal(false, meter.Update(0.5f, 1f / 60), "restart clears needle momentum");
for (var frame = 0; frame < 120; frame++)
{
    var previousAngle = meter.Angle;
    meter.Update(frame < 15 ? 1 : 0, 1f / 60);
    if (Math.Abs(meter.Angle - previousAngle) > 1.5f || Math.Abs(meter.Angle) > 43)
        throw new Exception("Needle reversals must remain smooth and inside the dial");
}
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
Equal(new GemKind(0), GemKind.FromSprites("spr_newnotes_strip4_0", "spr_newnotes_strip4_2"), "regular note");
Equal(new GemKind(1), GemKind.FromSprites("spr_newnotes_strip4_1", "spr_newnotes_strip4_2"), "HOPO");
Equal(new GemKind(2), GemKind.FromSprites("spr_star_notes_cap_strip4_0", "spr_star_notes_strip1_3"), "phrase note");
Equal(new GemKind(3), GemKind.FromSprites("spr_star_notes_cap_strip4_1", "spr_star_notes_strip1_3"), "phrase HOPO");
Equal(GemKind.Stock, GemKind.FromSprites("spr_newnotes_strip4_0", "spr_newnotes_strip4_3"), "pooled tap restores stock sprites");
Equal(GemKind.Stock, GemKind.FromSprites("spr_open_notes_strip5_0", ""), "pooled open note restores stock sprites");
Equal(GemKind.Stock, GemKind.FromSprites("", ""), "empty pooled note hides the custom gem");

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
