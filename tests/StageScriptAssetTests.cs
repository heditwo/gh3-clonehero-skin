using System.Text.Json;
using Gh3Skin;

internal static class StageScriptAssetTests
{
    private sealed class Scene
    {
        public Dictionary<string, StageObjectPlacement> Objects { get; set; } = new();
    }

    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") is not { } root) return;
        LightShowSourceTests.Run(root);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var reports = new List<object>();
        var unexpected = new List<string>();
        var unresolvedBindings = new[] { "shadow" }.Select(name => "Missing script 0x" + Gh3Checksum.Of(name)).ToArray();
        foreach (var directory in Directory.GetDirectories(Path.Combine(root, "stages"), "gh3.*").Order())
        {
            if (!File.Exists(Path.Combine(directory, "scripts.json"))) continue;
            var venue = Path.GetFileName(directory);
            var placements = JsonSerializer.Deserialize<Scene>(File.ReadAllText(Path.Combine(directory, "stage.json")), options)!.Objects;
            var actors = new Dictionary<uint, StageScriptActorDefinition>();
            if (File.Exists(Path.Combine(directory, "actors.json")))
                foreach (var actor in StageActorBank.Load(Path.Combine(directory, "actors.json")).Actors)
                {
                    using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "actors", actor.Id, "animation.bin")));
                    var animation = new BandAnimation(reader);
                    if (actor.Object.Length == 0 || actor.Role.StartsWith("stage-diver/")) continue;
                    var id = Gh3QbText.Key(actor.Object);
                    actors.Add(id, new(actor, animation.Clips.Select(clip => (double)clip.Duration).ToArray(), animation,
                        animation.LoadBoneNames(Path.Combine(directory, "actors", actor.Id, "bones.json"))));
                    placements.Add(id.ToString("x8"), new() { Position = actor.Transform.Position, Rotation = actor.Transform.Rotation,
                        Script = actor.Script, Created = actor.Created, Scale = actor.Scale });
                }
            var beams = new Dictionary<uint, StageBeamValue>();
            if (File.Exists(Path.Combine(directory, "fixtures", "fixtures.json")))
            {
                using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "fixtures", "fixtures.json")));
                foreach (var row in fixtures.RootElement.GetProperty("housings").EnumerateArray())
                {
                    var id = row.TryGetProperty("object", out var source) ? Gh3QbText.Key(source.GetString()!) : Convert.ToUInt32(row.GetProperty("id").GetString(), 16);
                    var placement = JsonSerializer.Deserialize<StageObjectPlacement>(row.GetRawText(), options)!;
                    placements.Add(id.ToString("x8"), placement);
                    beams.Add(id, new() { Density = 1 });
                }
            }
            var bank = StageScriptBank.Load(Path.Combine(directory, "scripts.json"), Path.Combine(root, "libraries", "gh3-band-v1", "scripts.json"));
            var timeline = new StageTimeline(new[] { 76, 75, 58, 57, 74, 73, 72, 71, 70, 60, 60, 69, 68, 67, 66, 65, 64, 63, 62, 61, 76 }
                .Select((note, index) => new StageCue(4 + index * .5, "gh3-note", note.ToString())), "script assets");
            var lighting = new StageLighting(StageLightingBank.Load(directory)!, timeline);
            using var state = new StageScriptState(bank, placements, timeline, venue, (id, time) => {
                var binding = bank.LightVolumes.FirstOrDefault(pair => Gh3QbText.Key(pair.Key) == id).Value;
                var index = binding == null ? -1 : Array.IndexOf(lighting.BeamIds, Gh3QbText.Key(binding).ToString("x8"));
                return index < 0 ? beams.GetValueOrDefault(id) : lighting.At(time).SampleBeam(index, time);
            }, actors, lighting);
            state.Update(0, .1f);
            state.Update(1, .1f);
            if (Environment.GetEnvironmentVariable("GH3_LIGHT_DUMP") == venue)
                foreach (var position in new System.Numerics.Vector3[] { new(0, 1, 0), new(3, 1, 2), new(-3, 1, 4), new(0, 4, 8), new(0, 0, -5) })
                {
                    var set = state.LightScene.Sample(position, Gh3Checksum.Value("None")).Lighting;
                    Console.WriteLine($"{venue} {position}: ambient {set.Ambient} key {set.Color} dir {set.Direction} sh0 {set.Harmonics[0]} sh1 {set.Harmonics[1]}");
                }
            state.Update(3, .9f);
            CheckNodeFlags(venue, bank, placements, directory, beams, actors);
            if (venue == "gh3.prison")
            {
                state.Update(7.6, .9f);
                var flare = Gh3Checksum.Value("Z_Prison_Flare_Skeleton01");
                var skeleton = Gh3Checksum.Value("Z_Prison_Skeleton01");
                var controls = state.RenderControls;
                var flash = new StageScreenTarget(Gh3Checksum.Value("bg_viewport"), Gh3Checksum.Value("FlashBS"));
                if (!state.Objects[flare].Exists || state.Objects[skeleton].Exists || !controls.Outlines[flare].Enabled || controls.OutlineColors[0].W != 1 || Math.Abs(controls.Outline.Min - .002f) > .000001f)
                    throw new Exception("The original Prison flare must switch skeletons and retain its outline parameters.");
                if (!controls.ScreenEffects.TryGetValue(flash, out var effect) || Gh3ScriptRuntime.Numeric(effect.Sample(7.6).Field("Brightness")!) != 2.5)
                    throw new Exception("The original delayed Prison flash did not start with its source brightness.");
                state.Update(8.1, .9f);
                if (state.Objects[flare].Exists || !state.Objects[skeleton].Exists || controls.Outlines.ContainsKey(flare))
                    throw new Exception("The original flare completion must restore the skeleton and remove its outline state.");
                var brightness = Gh3ScriptRuntime.Numeric(controls.ScreenEffects[flash].Sample(8.1).Field("Brightness")!);
                if (brightness <= 1 || brightness >= 2.5) throw new Exception("The source flash must return toward neutral after its hold.");
                var matrix = StageScreenColor.Matrix(controls.ScreenEffects[flash].Sample(8.1));
                state.Update(8.1, .9f);
                if (StageScreenColor.Matrix(state.RenderControls.ScreenEffects[flash].Sample(8.1)) != matrix)
                    throw new Exception("A paused source flash must retain its exact color transform.");
                state.Update(7.6, .9f);
                state.Update(8.1, .9f);
                if (StageScreenColor.Matrix(state.RenderControls.ScreenEffects[flash].Sample(8.1)) != matrix)
                    throw new Exception("Backward seek must reconstruct the original flash transform.");
                state.Update(9, .9f);
                if (state.RenderControls.ScreenEffects.ContainsKey(flash)) throw new Exception("The original flash-off script must remove its viewport effect.");
            }
            state.Update(20, .9f);
            if (venue == "gh3.video" && (!state.Movies.Slots.TryGetValue(0, out var movie) || movie.Name != "video_shoot_good_xen"))
                throw new Exception("The PC performance script must select its original good movie.");
            var expected = state.Objects.Keys.ToDictionary(id => id, id => state.Sample(id, 20));
            var actorPoses = state.Actors.ToDictionary(pair => pair.Key, pair => pair.Value.Sample(20));
            state.Update(4, .9f);
            state.Update(20, .9f);
            foreach (var (id, pose) in expected)
                if (state.Sample(id, 20) != pose) throw new Exception($"Object seek differs: {venue}/{id:x8}");
            foreach (var (id, pose) in actorPoses)
                if (state.Actors[id].Sample(20) != pose) throw new Exception($"Actor seek differs: {venue}/{id:x8}");
            foreach (var (id, actor) in actors.Where(pair => pair.Value.Actor.Script.Length > 0))
            {
                var sample = state.Actors[id].Sample(20);
                if (sample.Clip < 0 || sample.Clip >= actor.Durations.Length || sample.Time < 0 || sample.Time > actor.Durations[sample.Clip])
                    throw new Exception("Script actor sample exceeds its original clip");
            }
            var zone = "z_" + venue.Substring(4);
            var encoreCues = new List<StageCue>();
            if (bank.Values.TryGetValue(Gh3Checksum.Value(zone + "_Encore_Transition"), out var transition))
            {
                encoreCues.Add(new(.9, "gh3-script", new Gh3ScriptEvent("performance", Gh3ScriptRuntime.Structure(new[] {
                    new Gh3QbField(Gh3Checksum.Value("time"), Gh3ScriptRuntime.Number(0)),
                    new Gh3QbField(Gh3Checksum.Value("scr"), Gh3ScriptRuntime.Key("ChangeNodeFlag")),
                    new Gh3QbField(Gh3Checksum.Value("params"), Gh3ScriptRuntime.Structure(new[] {
                        new Gh3QbField(0, Gh3ScriptRuntime.Key("LS_ENCORE_POST")), new Gh3QbField(0, Gh3ScriptRuntime.Number(1)) })) })).Encode()));
                foreach (var row in transition.Field("ScriptTable")!.Items)
                    encoreCues.Add(new(1 + Gh3ScriptRuntime.Numeric(row.Field("time")!) / 1000, "gh3-script", new Gh3ScriptEvent("performance", row).Encode()));
                var end = Gh3Checksum.Value(zone + "_Encore_TransitionEnd");
                if (bank.Values.ContainsKey(end)) encoreCues.Add(new(1 + Gh3ScriptRuntime.Numeric(transition.Field("time")!) / 1000, "gh3-script",
                    new Gh3ScriptEvent("performance", Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("scr"), new(13, -1, end)),
                        new Gh3QbField(Gh3Checksum.Value("time"), transition.Field("time")!) })).Encode()));
            }
            var encoreTimeline = new StageTimeline(encoreCues, "source encore table");
            var encoreLighting = new StageLighting(StageLightingBank.Load(directory)!, encoreTimeline);
            using var encore = new StageScriptState(bank, placements, encoreTimeline, venue, (id, time) => {
                var binding = bank.LightVolumes.FirstOrDefault(pair => Gh3QbText.Key(pair.Key) == id).Value;
                var index = binding == null ? -1 : Array.IndexOf(encoreLighting.BeamIds, Gh3QbText.Key(binding).ToString("x8"));
                return index < 0 ? beams.GetValueOrDefault(id) : encoreLighting.At(time).SampleBeam(index, time);
            }, actors, encoreLighting);
            encore.Update(0, .9f);
            if (encoreCues.Count > 0)
            {
                encore.Update(.95, .9f);
                foreach (var (id, node) in bank.NodeValues.Where(pair => pair.Value.Field("CreatedOnProgress")?.Integer == Gh3Checksum.Value("LS_ENCORE_POST")))
                    if (encore.Objects.TryGetValue(id, out var obj) && !obj.Exists)
                        throw new Exception($"Encore node flag did not create its object: {venue}/{id:x8}");
            }
            for (var time = 1.0; time <= 60; time += .5) encore.Update(time, .9f);
            foreach (var diagnostic in state.Diagnostics.Concat(encore.Diagnostics).Distinct())
                if (!diagnostic.StartsWith("Particle visibility,") && !diagnostic.StartsWith("Tesla shared render scheduling,") && !unresolvedBindings.Any(diagnostic.EndsWith) &&
                    diagnostic is not ("Source shadow-map rendering is incomplete" or "Source outline shader rendering is incomplete"))
                    unexpected.Add(venue + ": " + diagnostic);
            reports.Add(new { venue, objects = state.Objects.Count, actors = actors.Count, clips = actors.Values.Sum(actor => actor.Durations.Length),
                diagnostics = state.Diagnostics.ToArray(), encoreCalls = encoreCues.Count, encoreDiagnostics = encore.Diagnostics.ToArray(),
                actorSamples = actorPoses.Select(pair => new { id = pair.Key.ToString("x8"), pair.Value.Clip, pair.Value.Time }).ToArray() });
            Console.WriteLine($"Script assets checked: {venue}; {actors.Count} actors; {state.Diagnostics.Count} unresolved behavior diagnostics");
        }
        if (Environment.GetEnvironmentVariable("GH3_STAGE_SCRIPT_REPORT") is { } output)
            File.WriteAllText(output, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (unexpected.Count > 0) throw new Exception("New source-script failures: " + string.Join("; ", unexpected));
    }

    // GH3.exe applies the node-flag table after each lightshow mood or performance change; every flag-driven visual node must follow it.
    private static void CheckNodeFlags(string venue, StageScriptBank bank, Dictionary<string, StageObjectPlacement> placements, string directory,
        Dictionary<uint, StageBeamValue> beams, Dictionary<uint, StageScriptActorDefinition> actors)
    {
        uint Flag(Gh3QbValue node) => node.Field("CreatedFromVariable")?.Integer ?? node.Field("CreatedOnProgress")?.Integer ?? 0;
        var driven = bank.NodeValues.Where(pair => Flag(pair.Value) != 0).ToDictionary(pair => pair.Key, pair => Flag(pair.Value));
        // Not visual: LevelLight nodes (light objects, not geometry) and a model-less GameObject.
        var missing = driven.Keys.Where(id => !placements.ContainsKey(id.ToString("x8")) &&
            bank.NodeValues[id].Field("Class")?.Integer != Gh3Checksum.Value("LevelLight") &&
            !(bank.NodeValues[id].Field("Model")?.Data is string name && name.Equals("none", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0) throw new Exception($"Flag-driven nodes are not exported: {venue}: {string.Join(", ", missing.Select(id => id.ToString("x8")))}");
        if (driven.Count == 0) return;
        var timeline = new StageTimeline(new[] { (1.0, 76), (2, 71), (3, 70), (4, 72), (5, 74) }
            .Select(cue => new StageCue(cue.Item1, "gh3-note", cue.Item2.ToString())), "node flags");
        var lighting = new StageLighting(StageLightingBank.Load(directory)!, timeline);
        using var state = new StageScriptState(bank, placements, timeline, venue, (id, time) => beams.GetValueOrDefault(id), actors, lighting);
        void Expect(double time, float rock, string flag, bool exists)
        {
            state.Update(time, rock);
            foreach (var (id, value) in driven.Where(pair => pair.Value == Gh3Checksum.Value(flag) && state.Objects.ContainsKey(pair.Key)))
                if (state.Objects[id].Exists != exists) throw new Exception($"{venue}/{id:x8} ({flag}) must {(exists ? "exist" : "be killed")} at {time} s, rock {rock}");
        }
        state.Update(0, .9f);
        Expect(1.5, .9f, "LS_PERF_GOOD", true);
        Expect(1.5, .9f, "LS_MOOD_FLARE", false);
        Expect(2.5, .9f, "LS_MOOD_FLARE", true);
        Expect(3.5, .9f, "LS_MOOD_FLARE", false);
        Expect(4.5, .9f, "LS_MOOD_BLACKOUT", true);
        Expect(5.5, .5f, "LS_MOOD_BLACKOUT", false);
        Expect(5.5, .5f, "LS_PERF_GOOD", false);
        Expect(5.5, .5f, "LS_PERF_MEDIUM", true);
        Expect(6.5, .1f, "LS_PERF_POOR", true);
        Expect(6.5, .1f, "LS_PERF_MEDIUM", false);
        Expect(6.5, .1f, "LS_ENCORE_PRE", true);
        Expect(6.5, .1f, "LS_ENCORE_POST", false);
        Console.WriteLine($"Node flags checked: {venue}; {driven.Count} flag-driven nodes");
    }
}
