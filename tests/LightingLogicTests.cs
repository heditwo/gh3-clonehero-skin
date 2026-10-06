using System.Numerics;
using Gh3Skin;

internal static class LightingLogicTests
{
    public static void Run()
    {
        var ambient = new StageLightDefinition { Id = "ambient", Group = "band", Type = "Ambient", Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 } };
        var direct = new StageLightDefinition { Id = "direct", Group = "band", Type = "Dir", Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 }, OverrideColor = true };
        StageLightingSnapshot Snapshot(float intensity) => new() { Lights = new[] {
            new StageLightSetting { Color = new float[] { 1, 0.9f, 0.8f }, Intensity = 0.25f, Far = 200 },
            new StageLightSetting { Color = new float[] { 1, 1, 1 }, Intensity = intensity, Far = 200 }
        }, Beams = new() { ["beam"] = new StageBeamSetting { Position = new[] { intensity, 5, 0 },
            Rotation = new float[] { 0, 0, 0, 1 }, Color = new float[] { 1, 1, 1 }, Density = intensity, Range = 8 } } };
        var bank = new StageLightingBank { Version = 1, Lights = new[] { ambient, direct },
            Snapshots = new() { ["intro"] = Snapshot(1), ["dark"] = Snapshot(0.2f), ["verse"] = Snapshot(0.8f), ["verse2"] = Snapshot(0.6f) },
            Moods = new() { ["intro"] = new[] { "intro" }, ["blackout"] = new[] { "dark" }, ["strobe"] = new[] { "dark" }, ["verse"] = new[] { "verse", "verse2" } },
            Colors = new() { ["red"] = new float[] { 1, 0, 0 } } };
        var lighting = new StageLighting(bank, new StageTimeline(new[] { new StageCue(1, "gh3-note", "72"),
            new StageCue(2, "gh3-note", "68"), new StageCue(3, "gh3-note", "75"), new StageCue(4, "gh3-note", "70"),
            new StageCue(4.137, "gh3-note", "60") }, "test"));
        var dark = lighting.At(1.5).Sample(1.5);
        Check(dark.Lights[0].Energy.X == 0.25f && Math.Abs(dark.Lights[1].Energy.X - 0.2f) < 0.00001f, "blackout keeps authored fill and direct lights");
        var half = lighting.At(1.15).Sample(1.15);
        Check(Math.Abs(half.Lights[1].Energy.X - 0.6f) < 0.0001f, "blackout follows the light blend time");
        var red = lighting.At(2.5).Sample(2.5);
        Check(red.Lights[0].Energy == dark.Lights[0].Energy && red.Lights[1].Energy.Y == 0, "color overrides preserve excluded fill lights");
        Check(lighting.At(4.05).Sample(4.05).Lights[1].Energy.X == 0.2f, "strobe starts with the authored blackout snapshot");
        var on = lighting.At(4.15).Sample(4.15);
        Check(on.Lights[1].Energy.X == 1 && lighting.At(4.3).Sample(4.3).Lights[1].Energy == on.Lights[1].Energy,
            "strobe follows note toggles instead of a free-running timer");
        Check(on.Beams[0].Position.X == 0.8f && lighting.At(4.05).Sample(4.05).Beams[0].Position == on.Beams[0].Position,
            "strobe preserves the beam positions");
        var positions = new StageLighting(bank, new StageTimeline(new[] { new StageCue(1, "gh3-note", "75"),
            new StageCue(2, "gh3-note", "57"), new StageCue(3, "lighting", "blackout"), new StageCue(4, "lighting", "red") }, "test"));
        Check(positions.At(2.5).Sample(2.5).Beams[0].Position.X == 0.8f && positions.At(2.5).Sample(2.5).Beams[0].Density == 0.6f,
            "note 57 changes beam density but preserves the beam positions");
        Check(positions.At(4.5).Sample(4.5).Lights[1].Energy.X == 1, "custom color cues leave blackout");
        _ = lighting.At(100).Sample(100);
        Check(lighting.At(1.5).Sample(1.5).Lights.SequenceEqual(dark.Lights), "seek restores the authored light state");
        Check(StageLighting.Weight(direct, dark.Lights[1], Vector3.Zero, -Vector3.UnitZ) == 1 &&
            StageLighting.Weight(direct, dark.Lights[1], Vector3.Zero, Vector3.UnitZ) == 0, "directional illumination follows the surface normal");
        if (Environment.GetEnvironmentVariable("GH3_LIGHTING_ASSETS") is { } directory)
        {
            var original = StageLightingBank.Load(directory)!;
            var timeline = StageChartReader.Load("", "", Path.Combine(Path.GetDirectoryName(directory)!, "events"), "My Curse", "Killswitch Engage");
            var actual = new StageLighting(original, timeline);
            var frame = actual.At(3.8).Sample(3.8);
            var index = Array.FindIndex(original.Lights, l => l.Id == Gh3Checksum.Of("Z_Budokan_GFX_L_Band_Ambient01"));
            Check(frame.Lights[index].Energy.X == 0.25f, "My Curse blackout keeps the source band ambient intensity");
            Check(frame.Beams.All(b => b.Density == 0), "original blackout disables beam housings");
            var spot = original.Lights.Single(l => l.Type == "Spot");
            Check(Vector3.Dot(Vector3.Transform(Vector3.UnitZ, spot.Orientation), -Vector3.UnitY) > 0.99f,
                "the overhead guitarist spotlight points down onto the performer");
            for (var i = 0; i < original.Lights.Length; i++)
            {
                var sample = new StageLightSample(original.Lights[i], frame.Lights[i]);
                foreach (var position in new[] { Vector3.Zero, new Vector3(-3, 1, 2), new Vector3(2, 0.1f, -4) })
                    foreach (var normal in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
                        Check(Math.Abs(sample.Weight(position, normal) - StageLighting.Weight(original.Lights[i], frame.Lights[i], position, normal)) < 0.00001f,
                            "prepared light samples preserve source attenuation and direction");
            }
            Check(actual.At(3).Snapshot == Gh3Checksum.Of("Blackout01") && Math.Abs(actual.At(3).Blend - 1.6) < 0.00001,
                "the original blackout cue selects Blackout01 with the song's 1.6-second fade");
            foreach (var time in Enumerable.Range(0, 2530).Select(i => i / 10.0))
            {
                var sample = actual.At(time).Sample(time);
                Check(sample.Lights.All(l => float.IsFinite(l.Energy.LengthSquared())), "all song light values are finite");
            }
            Console.WriteLine($"Validated {original.Lights.Length} source lights, {original.Snapshots.Count} snapshots, and My Curse lighting.");
        }
        Console.WriteLine("Authored lighting, blackout blends, color exclusions, and strobe timing checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
