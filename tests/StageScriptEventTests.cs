using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class StageScriptEventTests
{
    private static uint Key(string text) => Gh3Checksum.Value(text);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));

    public static void Run()
    {
        var values = new Dictionary<uint, Gh3QbValue> {
            [Key("start")] = ScriptLogicTests.Script("GameObj_PlayAnim", "anim", (byte)7, "clip", "AnimEvents", (byte)7, "On", (byte)1, (byte)0x24),
            [Key("stop")] = ScriptLogicTests.Script("actor", (byte)0x42, "GameObj_PlayAnim", "anim", (byte)7, "clip", (byte)1, (byte)0x24),
            [Key("loop")] = ScriptLogicTests.Script("actor", (byte)0x42, "GameObj_PlayAnim", "anim", (byte)7, "clip", "cycle", "AnimEvents", (byte)7, "On", (byte)1, (byte)0x24),
            [Key("events")] = Params(("clip", new(12, -1, new[] {
                Params(("time", Gh3ScriptRuntime.Number(.25)), ("scr", Gh3ScriptRuntime.Key("Hide"))),
                Params(("time", Gh3ScriptRuntime.Number(.75)), ("scr", Gh3ScriptRuntime.Key("Unhide"))) })))
        };
        if (Environment.GetEnvironmentVariable("GH3_SCRIPT_SOURCE") is { } source)
            foreach (var pair in new Gh3Qb(File.ReadAllBytes(Path.Combine(source, "lightshow.qb"))).Sections) values[pair.Key] = pair.Value;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, definitions = values.ToDictionary(pair => pair.Key.ToString("x8"),
                pair => JsonDocument.Parse(Gh3QbText.Encode(pair.Value)).RootElement.Clone()) }));
            var bank = StageScriptBank.Load(path);
            var placements = new Dictionary<string, StageObjectPlacement> { [Key("actor").ToString("x8")] = new() { Position = new[] { 0f, 0, 0 }, Rotation = new[] { 0f, 0, 0, 1 }, Script = "start" } };
            var actor = new StageActorDefinition { Object = "actor", Script = "start", AnimationEvents = "events", Clips = new() { ["clip"] = 0 } };
            var actors = new Dictionary<uint, StageScriptActorDefinition> { [Key("actor")] = new(actor, new[] { 1.0 }) };
            StageCue Call(double time, string command) => new(time, "gh3-script", new Gh3ScriptEvent("performance", Params(("time", Gh3ScriptRuntime.Number(time * 1000)), ("scr", Gh3ScriptRuntime.Key(command)))).Encode());
            using var state = new StageScriptState(bank, placements, new(new[] { Call(.5, "stop"), Call(1, "loop") }, "animation events"), "example.studio", actors: actors);
            state.Update(.24);
            Check(state.Sample(Key("actor"), .24).Visible, "Animation events do not run before their source time.");
            state.Update(.25);
            Check(!state.Sample(Key("actor"), .25).Visible, "Animation events retain object context and run at their exact clip time.");
            state.Update(.8);
            Check(!state.Sample(Key("actor"), .8).Visible, "Replacing animation cancels its future events even when the next clip disables events.");
            state.Update(1.8);
            Check(state.Sample(Key("actor"), 1.8).Visible, "Enabled animation events resume for a new clip.");
            state.Update(2.3);
            Check(!state.Sample(Key("actor"), 2.3).Visible, "Looped animation events repeat at the source clip duration.");
            state.Update(2.8);
            state.Update(2.3);
            Check(!state.Sample(Key("actor"), 2.3).Visible && state.Diagnostics.Count == 0, "Backward seek rebuilds events and cancellation.");
            if (values.ContainsKey(Key("Venue_PulseRed")))
            {
                placements[Key("actor").ToString("x8")].Script = "Venue_PulseRed";
                using var pulse = new StageScriptState(bank, placements, new(Array.Empty<StageCue>(), "note events"), "example.studio");
                pulse.Update(1);
                pulse.Raise(Key("hitnote_green"));
                Check(pulse.Sample(Key("actor"), 1).Scale == Vector3.One, "Other note lanes do not pulse a red-note listener.");
                pulse.Raise(Key("hitnote_red"));
                Check(Math.Abs(pulse.Sample(Key("actor"), 1).Scale.X - 1.12) < .00001, "Live note events reach the original venue pulse script.");
                pulse.Update(1.05);
                var expected = pulse.Sample(Key("actor"), 1.05);
                pulse.Update(1.2);
                pulse.Update(1.05);
                Check(pulse.Sample(Key("actor"), 1.05) == expected, "Seek reconstructs already received note pulses.");
                pulse.Update(.5);
                pulse.Update(1.05);
                Check(pulse.Sample(Key("actor"), 1.05).Scale == Vector3.One && pulse.Diagnostics.Count == 0, "Seeking before a live hit removes future input history.");
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("Animation event timing, object context, cancellation, looping, note pulses, and input seek checks passed.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
