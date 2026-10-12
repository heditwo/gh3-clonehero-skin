using Gh3Skin;

// A creator stage with a script bank keeps its own lights (lighting.json) unless the bank has GH3 light objects.
internal static class LightOwnershipTests
{
    private static string Node(string name, string type, string extra = "") =>
        $"[10, [[\"{Gh3QbText.Key(Gh3Checksum.Value("Name"))}\", [13, \"{Gh3QbText.Key(Gh3Checksum.Value(name))}\"]], " +
        $"[\"{Gh3QbText.Key(Gh3Checksum.Value("Class"))}\", [13, \"{Gh3QbText.Key(Gh3Checksum.Value(type))}\"]]{extra}]]";

    private static string Bank(params string[] nodes) => nodes.Length == 0 ? "{\"version\": 1, \"definitions\": {}}" :
        $"{{\"version\": 1, \"nodes\": [\"club_NodeArray\"], \"definitions\": {{\"{Gh3Checksum.Value("club_NodeArray"):x8}\": [12, [{string.Join(", ", nodes)}]]}}}}";

    public static void Run(string assets)
    {
        var library = Path.Combine(assets, "libraries", "gh3-band-v1", "scripts.json");
        var folder = Directory.CreateTempSubdirectory("gh3-light-ownership");
        try
        {
            bool Drives(string json)
            {
                var path = Path.Combine(folder.FullName, "scripts.json");
                File.WriteAllText(path, json);
                using var state = new StageScriptState(StageScriptBank.Load(path, library), new Dictionary<string, StageObjectPlacement>(),
                    new StageTimeline(Array.Empty<StageCue>(), "light ownership"), "");
                if (!state.LightScene.DrivesLighting)
                {
                    var command = state.LightScene.Execute(Gh3Checksum.Value("SetLightColor"), Gh3ScriptRuntime.Structure(new[] {
                        new Gh3QbField(Gh3Checksum.Value("name"), Gh3ScriptRuntime.Key("club_light")) }));
                    if (command?.Success != false || state.LightScene.DrivesLighting) throw new Exception("A light command must not make a light");
                }
                return state.LightScene.DrivesLighting;
            }
            if (Drives(Bank())) throw new Exception("An empty script bank must keep the stage lights");
            if (Drives(Bank(Node("club_box", "GameObject")))) throw new Exception("A script bank without LevelLight nodes must keep the stage lights");
            var ambient = $", [\"{Gh3QbText.Key(Gh3Checksum.Value("Type"))}\", [13, \"{Gh3QbText.Key(Gh3Checksum.Value("Ambient"))}\"]]";
            if (!Drives(Bank(Node("club_box", "GameObject"), Node("club_light", "LevelLight", ambient))))
                throw new Exception("A script bank with a LevelLight node must light the stage through the GH3 light scene");
        }
        finally { folder.Delete(true); }
        var venues = 0;
        foreach (var directory in Directory.GetDirectories(Path.Combine(assets, "stages"), "gh3.*"))
        {
            var bank = StageScriptBank.Load(Path.Combine(directory, "scripts.json"), library);
            if (!new StageLightScene(bank.NodeValues.Values).DrivesLighting) throw new Exception("A GH3 venue must keep its GH3 light scene: " + directory);
            venues++;
        }
        if (venues != 10) throw new Exception($"Expected the ten GH3 venues, found {venues}");
        Console.WriteLine($"Light ownership passed: script banks without LevelLight nodes keep the stage lights; {venues} GH3 venues keep the GH3 light scene.");
    }
}
