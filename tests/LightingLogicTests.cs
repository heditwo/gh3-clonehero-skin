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
        var half = lighting.At(1.075).Sample(1.075);
        Check(lighting.At(1).Blend == 0.15 && Math.Abs(half.Lights[1].Energy.X - 0.6f) < 0.0001f, "blackout follows the original 0.15-second default blend time");
        var red = lighting.At(2.5).Sample(2.5);
        Check(red.Lights[0].Energy == dark.Lights[0].Energy && red.Lights[1].Energy.Y == 0, "color overrides preserve excluded fill lights");
        Check(Math.Abs(lighting.At(4.05).Sample(4.05).Lights[1].Energy.X - 0.2f) < .000001f, "strobe starts with the authored blackout snapshot");
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
        CheckMotion(bank);
        var source = new StageLighting(bank, new StageTimeline(Array.Empty<StageCue>(), "independent source fades"));
        source.BeginScriptControl();
        var empty = Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>());
        source.PlaySnapshot("verse", 1, 2, null, empty, true);
        var preserved = source.At(1);
        bank.Snapshots["dark"].Lights[1].Inherit = true;
        bank.Snapshots["dark"].Beams["beam"].Inherit = true;
        source.PlaySnapshot("dark", 1.5, .15, null, Gh3ScriptRuntime.Structure(new[] {
            new Gh3QbField(Gh3Checksum.Value("intensity"), Gh3ScriptRuntime.Number(.3)) }), true);
        Check(source.At(2).Sample(2).Lights[1] == preserved.Sample(2).Lights[1] && source.At(3).SampleBeam(0, 3) == preserved.SampleBeam(0, 3),
            "Absent source rows must keep their running light fade and housing movement when another snapshot appends intensity.");
        Check(Math.Abs(source.At(1.65).Sample(1.65).Lights[0].Energy.X - .3f) < .000001f,
            "Snapshot append parameters must apply only to the source rows that are present.");
        source.SetBeam(0, 2, source.At(2).SampleBeam(0, 2) with { Density = .75f });
        Check(source.At(2).SampleBeam(0, 2).Density == .75f && source.At(2.5).Sample(2.5).Lights[1] == preserved.Sample(2.5).Lights[1],
            "A source volume update must change its component without interrupting other light fades.");
        source.PlaySnapshot("verse", 3, 0, null, empty, false);
        Check(source.At(3).SampleBeam(0, 3).Density == .8f, "A later source snapshot must replace a prior scripted volume parameter.");
        if (Environment.GetEnvironmentVariable("GH3_LIGHTING_ASSETS") is { } directory)
        {
            var original = StageLightingBank.Load(directory)!;
            var timeline = StageChartReader.Load("", "", Path.Combine(Path.GetDirectoryName(directory)!, "events"), "My Curse", "Killswitch Engage");
            var actual = new StageLighting(original, timeline);
            var frame = actual.At(3.8).Sample(3.8);
            var index = Array.FindIndex(original.Lights, l => l.Id == Gh3Checksum.Of("Z_Budokan_GFX_L_Band_Ambient01"));
            Check(frame.Lights[index].Energy.X == 0.25f, "My Curse blackout keeps the source band ambient intensity");
            Check(frame.Beams.All(b => b.Density == 0), "original blackout disables beams");
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
            foreach (var time in timeline.Cues.Select(c => c.Time).Distinct())
            {
                var before = actual.At(time - 0.000001);
                var after = actual.At(time);
                for (var i = 0; i < actual.BeamIds.Length; i++)
                {
                    var a = before.Motion[i].Sample(time);
                    var b = after.Motion[i].Sample(time);
                    Check(Vector3.Distance(a.Position, b.Position) < 0.00001f && RotationDistance(a.Rotation, b.Rotation) < 0.00001f,
                        "every original cue preserves housing position and rotation continuity");
                    Check(Vector3.Distance(a.Velocity, b.Velocity) < 0.00001f && RotationDistance(a.RotationVelocity, b.RotationVelocity) < 0.00001f,
                        "every original cue preserves housing velocity continuity");
                }
            }
            Console.WriteLine($"Validated {original.Lights.Length} source lights, {original.Snapshots.Count} snapshots, and My Curse lighting.");
        }
        Console.WriteLine("Authored lighting, blackout blends, color exclusions, and strobe timing checks passed.");
    }

    private static float RotationDistance(Quaternion a, Quaternion b) => Math.Min((a - b).Length(), (a + b).Length());

    private static void CheckMotion(StageLightingBank bank)
    {
        void Rotation(string snapshot, float angle)
        {
            var q = Quaternion.CreateFromYawPitchRoll(angle, 0.25f, 0);
            bank.Snapshots[snapshot].Beams["beam"].Rotation = new[] { q.X, q.Y, q.Z, q.W };
        }
        Rotation("verse", MathF.PI / 2);
        Rotation("dark", -MathF.PI / 3);
        bank.Snapshots["dark"].Beams["beam"].Density = 0;
        var lighting = new StageLighting(bank, new StageTimeline(new[] {
            new StageCue(0, "blend", "0"), new StageCue(1, "lighting", "verse"), new StageCue(1.05, "color", "red"),
            new StageCue(1.1, "gh3-note", "57"), new StageCue(1.2, "lighting", "blackout"),
            new StageCue(1.21, "lighting", "strobe"), new StageCue(1.22, "strobe-toggle"),
            new StageCue(1.3, "blend", "2"), new StageCue(2, "lighting", "verse") }, "motion"));
        StageBeamValue Beam(double time) => lighting.At(time).SampleBeam(0, time);
        Check(Beam(1).Position == Beam(0).Position && RotationDistance(Beam(1).Rotation, Beam(0).Rotation) == 0 && Beam(1).Density == 0.8f,
            "an instant light change starts housing travel from the current pose");
        Check(lighting.At(1).Motion[0] == lighting.At(1.1).Motion[0], "color and note 57 cues let existing housing travel continue");
        Check(Vector3.Distance(Beam(1.15).Position, Beam(1).Position) > 0.001f, "housings still move after a zero-duration light fade");
        var before = lighting.At(1.19).Motion[0].Sample(1.2);
        var after = lighting.At(1.2).Motion[0].Sample(1.2);
        Check(Vector3.Distance(before.Position, after.Position) < 0.000001f && RotationDistance(before.Rotation, after.Rotation) < 0.000001f &&
            Vector3.Distance(before.Velocity, after.Velocity) < 0.000001f && RotationDistance(before.RotationVelocity, after.RotationVelocity) < 0.000001f,
            "interrupted housing moves preserve pose and velocity");
        Check(Beam(1.2).Density == 0 && RotationDistance(Beam(1.2).Rotation, Beam(1.205).Rotation) > 0.00001f,
            "unlit housings move without a default-rotation reset");
        Check(lighting.At(1.2).Motion[0] == lighting.At(1.22).Motion[0] && Beam(1.21).Density == 0 && Beam(1.22).Density == 0.6f,
            "strobe toggles change intensity without restarting housing travel");
        var endpoint = lighting.At(2).Motion[0].Sample(4.1);
        Check(Vector3.Distance(Beam(4.1).Position, lighting.At(2).To.Beams[0].Position) == 0 && endpoint.Velocity == Vector3.Zero &&
            endpoint.RotationVelocity == default, "slow moves settle at the authored target with no residual velocity");
        var saved = Beam(1.17);
        foreach (var fps in new[] { 30, 60, 144 })
        {
            for (var frame = 0; frame <= 5 * fps; frame++)
            {
                var sample = Beam((double)frame / fps);
                Check(float.IsFinite(sample.Position.LengthSquared()) && Math.Abs(sample.Rotation.LengthSquared() - 1) < 0.00001f,
                    "housing movement has finite positions and unit rotations");
            }
            Check(Beam(1.17) == saved, "frame rate, pause, and backward seeks preserve the housing pose");
        }
        var value = Beam(0);
        var stationary = new StageBeamMotion(value);
        Check(stationary.Retarget(1, 0, value with { Rotation = -value.Rotation }) == stationary,
            "equivalent quaternion signs do not restart housing movement");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
