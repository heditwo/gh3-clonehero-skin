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

var variables = new[]
{
    "GH3_SKIN_CAPTURE_AFTER", "GH3_SKIN_CAPTURE_TIMES", "GH3_SKIN_PREVIEW_POWER",
    "GH3_SKIN_PREVIEW_ROCK", "GH3_SKIN_FORCE_POWER", "GH3_SKIN_FORCE_ROCK",
    "GH3_SKIN_FORCE_POWER_AT_COMBO", "GH3_SKIN_KEEP_STOCK_GEMS",
    "GH3_SKIN_KEEP_STOCK_FLAMES", "GH3_SKIN_CAPTURE_DIR"
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
