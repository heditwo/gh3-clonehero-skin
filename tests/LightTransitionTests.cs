using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class LightTransitionTests
{
    public static void Run()
    {
        CheckSharedPlayback();
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_LIGHT_UPDATES_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        void Check(StageLightTransition actual, JsonElement expected)
        {
            float[] Channels(StageLightChannels value) => new[] { value.Color.X, value.Color.Y, value.Color.Z, value.Color.W, value.Intensity, value.Specular, value.Shadow };
            foreach (var (name, data) in new[] { ("value", Channels(actual.Value)), ("target", Channels(actual.Target)) })
                if (data.Zip(expected.GetProperty(name).EnumerateArray(), (a, b) => Math.Abs(a-b.GetSingle()) < .000001f).Any(match => !match))
                    throw new Exception($"Native light transition {count} differs: {name}");
            if (!new uint[] { (uint)actual.Motion, actual.Start, actual.Duration, actual.Elapsed }.SequenceEqual(expected.GetProperty("clock").EnumerateArray().Select(value => value.GetUInt32())) ||
                actual.Complete != expected.GetProperty("complete").GetBoolean()) throw new Exception($"Native light clock {count} differs");
            count++;
        }
        StageLightTransition Create(JsonElement before) => new(new(new(before[0].GetSingle(), before[1].GetSingle(), before[2].GetSingle(), before[3].GetSingle()),
            before[4].GetSingle(), before[5].GetSingle(), before[6].GetSingle()));
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var transition = Create(row.GetProperty("before"));
            transition.Begin(Gh3QbText.Decode(row.GetProperty("parameters").GetRawText()), row.GetProperty("time").GetUInt32());
            Check(transition, row.GetProperty("initial"));
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                transition.Update(sample.GetProperty("time").GetUInt32());
                Check(transition, sample);
            }
        }
        foreach (var row in document.RootElement.GetProperty("sequences").EnumerateArray())
        {
            var transition = Create(row.GetProperty("before"));
            var first = true;
            foreach (var step in row.GetProperty("steps").EnumerateArray())
            {
                if (!first) transition.Update(step.GetProperty("time").GetUInt32());
                first = false;
                transition.Begin(Gh3QbText.Decode(step.GetProperty("parameters").GetRawText()), step.GetProperty("time").GetUInt32());
                Check(transition, step.GetProperty("initial"));
                transition.Update(step.GetProperty("sampledTime").GetUInt32());
                Check(transition, step.GetProperty("sampled"));
            }
        }
        var commands = 0;
        foreach (var row in document.RootElement.GetProperty("commands").EnumerateArray())
        {
            var scene = new StageLightScene(new[] { Gh3QbText.Decode(row.GetProperty("source").GetRawText()) });
            var first = true;
            foreach (var action in row.GetProperty("actions").EnumerateArray())
            {
                var time = action.GetProperty("time").GetUInt32();
                if (!first) scene.Update(time);
                first = false;
                var result = scene.Execute(Gh3Checksum.Value(action.GetProperty("command").GetString()!), Gh3QbText.Decode(action.GetProperty("parameters").GetRawText()), time);
                if (result?.Success != action.GetProperty("result").GetBoolean()) throw new Exception($"Native light command {commands} result differs");
                foreach (var field in action.GetProperty("returned").EnumerateObject())
                {
                    var expected = Gh3QbText.Decode(field.Value.GetRawText());
                    var actual = result?.Values?.FirstOrDefault(value => value.Name == Convert.ToUInt32(field.Name, 16))?.Value;
                    if (actual == null || Math.Abs(Gh3ScriptRuntime.Numeric(actual)-Gh3ScriptRuntime.Numeric(expected)) > .000001)
                        throw new Exception($"Native light command {commands} return field {field.Name} differs");
                }
                void CheckValue(JsonElement expected)
                {
                    var value = scene.Channels(0x12345678);
                    var channels = new[] { value.Color.X, value.Color.Y, value.Color.Z, value.Color.W, value.Intensity, value.Specular, value.Shadow };
                    if (channels.Zip(expected.GetProperty("value").EnumerateArray(), (a, b) => Math.Abs(a-b.GetSingle()) < .000001f).Any(match => !match))
                        throw new Exception($"Native light command {commands} channels differ");
                }
                CheckValue(action.GetProperty("initial"));
                scene.Update(action.GetProperty("sampledTime").GetUInt32());
                CheckValue(action.GetProperty("sampled"));
                commands++;
            }
        }
        Console.WriteLine($"Native light parameter traversal, clock quantization, easing, completion, and replacement passed: {count} states, {commands} commands.");
    }

    private static void CheckSharedPlayback()
    {
        var nodes = new[] { 1u, 2u }.Select(id => Gh3ScriptRuntime.Structure(new[] {
            new Gh3QbField(Gh3Checksum.Value("Class"), Gh3ScriptRuntime.Key("LevelLight")),
            new Gh3QbField(Gh3Checksum.Value("Name"), new(13, -1, id)),
            new Gh3QbField(0, Gh3ScriptRuntime.Key("CreatedAtStart"))
        })).ToArray();
        StageLightingSnapshot Snapshot(bool inherit) => new() { Lights = new[] {
            new StageLightSetting { Color = new float[] { 1, 1, 1 }, Intensity = inherit ? 0 : 1, Specular = .25f, Far = 200 },
            new StageLightSetting { Color = new float[] { 1, 1, 1 }, Intensity = 1, Specular = .25f, Far = 200, Inherit = inherit }
        } };
        var bank = new StageLightingBank { Version = 1,
            Lights = new[] { 1, 2 }.Select(id => new StageLightDefinition { Id = id.ToString("x8"), Object = "0x"+id.ToString("x8"), Group = "8aebf027", Type = "Ambient",
                Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 } }).ToArray(),
            Snapshots = new() { ["initial"] = Snapshot(false), ["00000003"] = Snapshot(true) }, Moods = new() { ["intro"] = new[] { "initial" } } };
        var lighting = new StageLighting(bank, new StageTimeline(Array.Empty<StageCue>(), "shared light components"));
        lighting.BeginScriptControl();
        StageLightScene scene = null!;
        using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue>(), (_, command, parameters) => scene.Execute(command, parameters)) { FrameDuration = .25 };
        scene = new(nodes, runtime, lighting, Gh3QbText.Decode("[10,[[\"0x00000003\",[12,[[10,[[\"LightChecksum\",[13,\"0x00000001\"]]]]]]]]]"));
        var empty = Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>());
        void Call(string command, string parameters) => runtime.Start(Gh3Checksum.Value(command), Gh3QbText.Decode(parameters));
        runtime.Advance(1);
        Call("SetLightColor", "[10,[[\"name\",[13,\"0x00000001\"]],[\"r\",[1,128]],[\"specularintensity\",[2,0.7]],[\"shadowintensity\",[1,4]]]]");
        Call("SetLightIntensity", "[10,[[\"name\",[13,\"0x00000002\"]],[\"intensity\",[2,0.2]],[\"time\",[1,1]]]]");
        lighting.PlaySnapshot("00000003", 1, 1, null, empty, false);
        runtime.Advance(1.5);
        var first = scene.Channels(1);
        if (Math.Abs(first.Intensity-.5f) > .000001f || first.Specular != .7f || first.Shadow != 4 || Math.Abs(scene.Channels(2).Intensity-.6f) > .000001f)
            throw new Exception($"Shared snapshot playback did not retain direct values and inherited fades: {first}, {scene.Channels(2)}");
        Call("SetLightGroupIntensity", "[10,[[\"name\",[13,\"0x8aebf027\"]],[\"i\",[2,0.5]]]]");
        var rendered = scene.Apply(1, lighting.At(1.5).Sample(1.5).Lights[0]);
        if (rendered.Intensity != .25f || rendered.Specular != .35f || rendered.SpecularPower != 4 || scene.Channels(1) != first)
            throw new Exception("Shared group intensity changed component channels or lost the rendered values");
        Call("SetLightIntensity", "[10,[[\"name\",[13,\"0x00000001\"]],[\"intensity\",[2,0.8]],[\"time\",[2,0.5]],[\"motion\",[13,\"ease_in\"]]]]");
        runtime.Advance(1.75);
        if (Math.Abs(scene.Channels(1).Intensity-.575f) > .000001f) throw new Exception("Direct light command did not replace a snapshot fade");
        lighting.PlaySnapshot("00000003", 1.75, 0, null, empty, false);
        if (scene.Channels(1).Intensity != 0 || scene.Channels(1).Shadow != 4) throw new Exception("Immediate snapshot did not replace the direct fade");
    }
}
