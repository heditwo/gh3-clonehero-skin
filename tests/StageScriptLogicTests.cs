using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class StageScriptLogicTests
{
    private static Gh3QbValue Params(params Gh3QbField[] fields) => Gh3ScriptRuntime.Structure(fields);
    private static Gh3QbField Vector(string name, float x, float y, float z) => new(Gh3Checksum.Value(name), new(6, -1, new[] { x, y, z }));
    private static Gh3QbField Number(string name, double value) => new(Gh3Checksum.Value(name), Gh3ScriptRuntime.Number(value));

    public static void Run()
    {
        var obj = new StageObjectState(new() { Position = new[] { 1f, 2, 3 }, Rotation = new[] { 0f, 0, 0, 1 } });
        obj.Execute(Gh3Checksum.Value("Obj_MoveToPos"), Params(Vector("position", 5, 6, -7), Number("time", 2)), 1);
        Check(obj.Sample(2).Position == new Vector3(3, 4, 5), "Source object motion changes handedness and keeps its duration.");
        obj.Execute(Gh3Checksum.Value("Obj_StopMoving"), Params(), 2);
        Check(obj.Sample(20).Position == new Vector3(3, 4, 5), "Stopping movement retains the current position.");
        obj.Execute(Gh3Checksum.Value("Obj_Rotate"), Params(Vector("absolute", 0, 0, 20), Number("time", 2)), 1);
        var point = Vector3.Transform(Vector3.UnitX, obj.Sample(2).Rotation);
        Check(Math.Abs(point.Y - Math.Sin(Math.PI / 18)) < .00001, "Scenery rotates around its source pivot.");
        obj.Execute(Gh3Checksum.Value("Hide"), Params(), 2);
        Check(!obj.Sample(2).Visible, "A source hide does not delete the object.");
        obj.Execute(Gh3Checksum.Value("Unhide"), Params(), 2);
        Check(obj.Sample(2).Visible, "An unhide restores the object.");
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var rotated = new StageObjectState(new() { Position = new[] { 0f, 0, 0 }, Rotation = new[] { yaw.X, yaw.Y, yaw.Z, yaw.W } });
        rotated.Execute(Gh3Checksum.Value("Obj_Rotate"), Params(Vector("absolute", 0, 0, 0), Number("time", 2)), 0);
        Check(Math.Abs(Vector3.Transform(Vector3.UnitX, rotated.Sample(1).Rotation).Z + Math.Sqrt(.5)) < .00001,
            "An absolute rotation starts from the original non-zero orientation.");
        var fan = new StageObjectState(new() { Position = new[] { 0f, 0, 0 }, Rotation = new[] { yaw.X, yaw.Y, yaw.Z, yaw.W } });
        fan.Execute(Gh3Checksum.Value("Obj_RotX"), Params(Number("speed", -90)), 1);
        var spinning = fan.Sample(2).Rotation;
        Check(Vector3.Distance(Vector3.Transform(Vector3.UnitY, spinning), Vector3.UnitX) < .00001,
            "An endless signed axis rotation uses the object's local axis and source handedness.");
        fan.Execute(Gh3Checksum.Value("Obj_RotY"), Params(Number("speed", 30), Number("angle", -60)), 2);
        Check(fan.Execute(Gh3Checksum.Value("Obj_WaitRotate"), Params(), 3)!.Wait == 1, "A finite axis rotation exposes its remaining duration.");
        Check(fan.Sample(4).Rotation == fan.Sample(50).Rotation, "Finite rotation stops at its endpoint.");
        if (Environment.GetEnvironmentVariable("GH3_SCRIPT_SOURCE") is { } directory) { Original(directory); OriginalAxis(directory); }
        Console.WriteLine("Scenery motion, source pivots, visibility, source-script scheduling, pause, and seek checks passed.");
    }

    private static void Original(string directory)
    {
        var values = new Gh3Qb(File.ReadAllBytes(Path.Combine(directory, "z_budokan_scripts.qb"))).Sections;
        var path = Path.GetTempFileName();
        StageScriptBank bank;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, definitions = values.ToDictionary(pair => pair.Key.ToString("x8"),
                pair => JsonDocument.Parse(Gh3QbText.Encode(pair.Value)).RootElement.Clone()) }));
            bank = StageScriptBank.Load(path);
        }
        finally { File.Delete(path); }
        var key = Gh3Checksum.Value("Z_Budokan_G_RMM_Monster_02");
        var eyes = Gh3Checksum.Value("Z_Budokan_G_Monster_eyes_02");
        var placements = new Dictionary<string, StageObjectPlacement> {
            [key.ToString("x8")] = new() { Position = new[] { 13.51f, -.12127101f, 14.4814f }, Rotation = new[] { 0f, 0, 0, 1 } },
            [eyes.ToString("x8")] = new() { Position = new[] { 0f, 0, 0 }, Rotation = new[] { 0f, 0, 0, 1 }, Created = false } };
        var call = new Gh3ScriptEvent("performance", Params(Number("time", 1000),
            new(Gh3Checksum.Value("scr"), Gh3ScriptRuntime.Key("Z_Budokan_Encore_MoveMonster02"))));
        var cue = new StageCue(1, "gh3-script", call.Encode());
        using var state = new StageScriptState(bank, placements, new(new[] { cue }, "native"), "z_budokan");
        using var authored = new StageScriptState(bank, placements,
            StageChartReader.ReadChart("[Song]\nResolution = 1000\n[SyncTrack]\n0 = B 60000\n[Events]\n1000 = E gh3 " + call.Encode()), "z_budokan");
        state.Update(-2);
        state.Update(1.5);
        Check(Math.Abs(state.Objects[key].Sample(1.5).Position.X - (13.51 + 3.95618) / 2) < .00001, "Original monster movement runs from a song command.");
        state.Update(2);
        Check(Math.Abs(state.Objects[key].Sample(2).Position.X - 3.95618) < .00001, "Original monster movement reaches its source destination.");
        state.Update(3.5);
        var before = state.Objects[key].Sample(3.5);
        state.Update(3.5);
        Check(state.Objects[key].Sample(3.5) == before, "Held song time does not advance spawned scripts.");
        for (var i = 0; i <= 210; i++) authored.Update(i / 60.0);
        Check(authored.Objects[key].Sample(3.5) == before, "Authored and original scripts share execution across different update steps.");
        state.Update(.5);
        state.Update(3.5);
        Check(state.Objects[key].Sample(3.5) == before, "Backward seek rebuilds source script tasks and random motion.");
        Check(state.Diagnostics.Count == 0 && authored.Diagnostics.Count == 0, "Original monster movement and idle dependencies resolve: " + string.Join("; ", state.Diagnostics));
        var attach = new Gh3ScriptEvent("performance", Params(Number("time", 0),
            new(Gh3Checksum.Value("scr"), Gh3ScriptRuntime.Key("Z_Budokan_Encore_MonsterEyes02"))));
        using var attached = new StageScriptState(bank, placements, new(new[] { cue, new StageCue(2, "gh3-script", attach.Encode()) }, "attachments"), "gh3.budokan");
        attached.Update(2.5);
        var parent = attached.Sample(key, 2.5);
        var child = attached.Sample(eyes, 2.5);
        Check(child.Visible && Vector3.Distance(child.Position, parent.Position + Vector3.Transform(new(0, 0, -.1f), parent.Rotation)) < .00001,
            "Original monster-eye script creates an object and follows its moving source pivot: " + string.Join("; ", attached.Diagnostics));
        attached.Update(3.5);
        Check(!attached.Sample(eyes, 3.5).Visible, "Original monster-eye script removes the object after its source wait.");
        attached.Update(1);
        Check(!attached.Sample(eyes, 1).Visible, "Backward seek removes future object attachments and restores dormant visibility.");
        attached.Update(2.5);
        Check(attached.Sample(eyes, 2.5) == child && attached.Diagnostics.Count == 0, "Seek rebuilds the original object attachment.");
    }

    private static void OriginalAxis(string directory)
    {
        var values = new Gh3Qb(File.ReadAllBytes(Path.Combine(directory, "z_party.qb"))).Sections;
        var obj = new StageObjectState(new() { Position = new[] { 0f, 0, 0 }, Rotation = new[] { 0f, 0, 0, 1 } });
        Gh3ScriptRuntime? runtime = null;
        using var moving = runtime = new Gh3ScriptRuntime(values, (_, command, parameters) => obj.Execute(command, parameters, runtime!.Time));
        moving.Start(Gh3Checksum.Value("Z_Party_GlobeRot01Script"), target: 1);
        moving.Advance(.25);
        Check(Math.Abs(Vector3.Transform(Vector3.UnitY, obj.Sample(.25).Rotation).Z + Math.Sin(2.5 * Math.PI / 180)) < .00001,
            "The original globe script rotates 5 degrees at 10 degrees per second.");
        moving.Advance(.75);
        Check(Math.Abs(Vector3.Transform(Vector3.UnitY, obj.Sample(.75).Rotation).Z + Math.Sin(2.5 * Math.PI / 180)) < .00001,
            "The original globe script waits and reverses direction.");
        moving.Advance(10);
        Check(Math.Abs(obj.Sample(10).Rotation.X) < .00001 && moving.Pending == 1, "Source rocking loops retain one task and return to their initial orientation.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
