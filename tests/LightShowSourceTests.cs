using Gh3Skin;

internal static class LightShowSourceTests
{
    private static uint Key(string name) => Gh3Checksum.Value(name);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));

    public static void Run(string root)
    {
        var directory = Path.Combine(root, "stages", "gh3.budokan");
        var bank = StageScriptBank.Load(Path.Combine(directory, "scripts.json"), Path.Combine(root, "libraries", "gh3-band-v1", "scripts.json"));
        var lighting = new StageLighting(StageLightingBank.Load(directory)!, new StageTimeline(Array.Empty<StageCue>(), "source lightshow"));
        var transitions = bank.Values[Key("z_budokan_lightshow_mapping")].Field("transitions")!.Fields
            .SelectMany(field => field.Value.Items).Select(value => value.Integer).ToHashSet();
        var changes = new List<(double Time, Gh3QbValue Parameters)>();
        StageLightShowState? show = null;
        using var runtime = new Gh3ScriptRuntime(bank.Values, (_, command, parameters) =>
        {
            if (transitions.Contains(command)) { changes.Add((lighting.At(double.MaxValue).Time, parameters)); return new(true); }
            if (command == Key("GetPakManCurrent")) return new(true, Params(("pak", Gh3ScriptRuntime.Key("z_budokan"))).Fields);
            return show?.Execute(command, parameters);
        });
        show = new(bank, runtime, lighting, "z_budokan", gap => throw new Exception(gap));
        show.SetPerformance("medium");
        Check(changes[0].Parameters.Field("previousperformance")?.Integer == 0 && double.IsNegativeInfinity(changes[0].Time),
            "The initial performance callback must precede the processor and include a zero previous-performance checksum.");
        show.Note(75);
        var before = lighting.At(0);
        runtime.Advance(1);
        show.Note(57);
        var fixedPosition = lighting.At(1);
        Check(fixedPosition.Time == 1 && ReferenceEquals(before.Motion, fixedPosition.Motion),
            "Source note 57 must pass usesnapshotpositions=false through its blocked event and preserve housing motion.");
        runtime.Advance(2);
        show.Note(58);
        Check(lighting.At(2).Time == 2 && !ReferenceEquals(fixedPosition.Motion, lighting.At(2).Motion),
            "Source note 58 must resume the same processor with usesnapshotpositions=true.");
        runtime.Advance(3);
        show.SetPerformance("poor");
        var poor = lighting.At(3);
        runtime.Advance(4);
        show.Note(58);
        Check(ReferenceEquals(poor, lighting.At(4)), "The original poor processor must consume snapshot events without changing the snapshot.");
        runtime.Advance(5);
        show.Note(68);
        Check(ReferenceEquals(poor, lighting.At(5)), "Native color override changes must wait for the next snapshot application.");
        runtime.Advance(6);
        show.SetPerformance("good");
        var good = lighting.At(6);
        Check(changes.Last().Parameters.Field("previousperformance")?.Integer == Key("poor") && changes.Last().Time == 3,
            "The performance transition must receive the prior performance before the new processor starts.");
        Check(good.To.Lights.Where((_, i) => lighting.Bank.Lights[i].OverrideColor && !lighting.Bank.Snapshots[good.Snapshot].Lights[i].Inherit)
            .All(light => light.Energy.Y == 0 && light.Energy.Z == 0), "The next source snapshot must apply the red override.");
        runtime.Advance(7);
        show.Note(70);
        Check(lighting.At(7).Blend == 0, "The source strobe enter script must disable blending.");
        runtime.Advance(8);
        show.Note(60);
        Check(lighting.At(8).Snapshot == good.Snapshot && lighting.At(8).Blend == 0 && ReferenceEquals(good.Motion, lighting.At(8).Motion),
            "The source strobe script must restore the saved snapshot while housing motion continues.");
        runtime.Advance(9);
        show.Note(75);
        Check(lighting.At(9).Blend == Gh3CommandDispatch.DefaultLightBlend, "The strobe exit script must restore blending.");
        show.Execute(Key("LightShow_SetActive"), Params(("active", Gh3ScriptRuntime.Key("false"))));
        var inactive = lighting.At(9);
        runtime.Advance(10);
        show.Note(74);
        show.Note(58);
        Check(ReferenceEquals(inactive, lighting.At(10)) && runtime.Pending == 0, "Inactive lighting must not create a processor or change the rendered frame.");
        show.Execute(Key("LightShow_SetActive"), Params(("active", Gh3ScriptRuntime.Key("true"))));
        show.Note(74);
        Check(lighting.At(10).Time == 10 && runtime.Pending == 1, "Reactivation must start exactly one event processor after SetParams.");
        var soundcheckDirectory = Path.Combine(root, "stages", "gh3.soundcheck");
        var soundcheckBank = StageScriptBank.Load(Path.Combine(soundcheckDirectory, "scripts.json"), Path.Combine(root, "libraries", "gh3-band-v1", "scripts.json"));
        var staticLights = new StageLighting(StageLightingBank.Load(soundcheckDirectory)!, new StageTimeline(Array.Empty<StageCue>(), "static source lighting"));
        StageLightShowState? staticShow = null;
        using var staticRuntime = new Gh3ScriptRuntime(soundcheckBank.Values, (_, command, parameters) => staticShow?.Execute(command, parameters));
        staticShow = new(soundcheckBank, staticRuntime, staticLights, "z_soundcheck", gap => throw new Exception(gap));
        staticShow.SetPerformance("medium");
        staticRuntime.Advance(1);
        Check(staticShow.Cue(new(1, "lighting", "verse")) && staticLights.At(1).Snapshot == "static" && staticRuntime.Pending == 0,
            "A mood absent from the source mapping must stop its processor and retain static lighting instead of treating the mood as a color.");
        Console.WriteLine("Original lightshow processors, prior-performance payloads, snapshot events, strobe exits, and active-state checks passed.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
