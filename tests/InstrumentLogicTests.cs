using System.Text;
using Gh3Skin.Packages;
using GlbBuilder = PickerLogicTests.GlbBuilder;

// Instrument packages (PACKAGE-FORMAT.md section 9.4): the import of a guitar on a fixed profile rig, the rule that every
// mesh part follows the instrument joints, the highway image, the manifest kind, and the saved instrument choices.
internal static class InstrumentLogicTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh3-instrument-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckManifest();
            CheckChoices(root);
            CheckImport(root);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Instrument packages, the instrument-joint rule, highways, and instrument choices checks passed.");
    }

    private static void CheckManifest()
    {
        SemanticVersion.TryParse("0.9.8", out var version);
        var manifest = PickerLogicTests.Manifest("chinst/1.0", "test.guitar", "{\"model\":\"instrument.glb\",\"instrument\":\"instrument.json\"}");
        Check(PackageManifest.Parse(Encoding.UTF8.GetBytes(manifest), PackageKind.Instrument, version).Kind == PackageKind.Instrument, "a chinst/1.0 manifest is read");
        Check(PackageManifest.KindOf("x/gh3.demonik.chinst") == PackageKind.Instrument && PackageManifest.Extension(PackageKind.Instrument) == ".chinst", "the .chinst extension");
        string Rejection(string text, PackageKind kind)
        {
            try { PackageManifest.Parse(Encoding.UTF8.GetBytes(text), kind, version); return ""; }
            catch (PackageException error) { return error.Message; }
        }
        Check(Rejection(manifest.Replace("chinst/1.0", "chinst/1.1"), PackageKind.Instrument).Contains("needs a newer mod"), "chinst/1.1 asks for a newer mod");
        Check(Rejection(manifest, PackageKind.Character).Contains("does not agree"), "the extension selects the kind");
        Check(Rejection(manifest.Replace("\"instrument\":\"instrument.json\"", "\"character\":\"instrument.json\""), PackageKind.Instrument).Contains("needs 'instrument'"),
            "an instrument needs the instrument entry");
        Check(PackageManager.CustomFolders.Contains("Instruments"), "Custom/Instruments is scanned");
    }

    private static void CheckChoices(string root)
    {
        var path = Path.Combine(root, "choices.json");
        var choices = PlayerChoices.Load(path, out _);
        Check(choices.Instrument("guitarist") == "default" && choices.Instrument("bassist") == "default", "the default instrument without a file");
        choices.Instruments["guitarist"] = "gh3.demonik";
        choices.Save(path);
        var read = PlayerChoices.Load(path, out _);
        Check(read.Instrument("guitarist") == "gh3.demonik" && read.Instrument("bassist") == "default", "instrument choices round-trip");
        File.WriteAllText(path, "{\"instruments\": {\"bassist\": \"Bad Id\", \"drummer\": \"gh3.sticks\"}}");
        read = PlayerChoices.Load(path, out _);
        Check(read.Instrument("bassist") == "default" && !read.Instruments.ContainsKey("drummer"), "invalid instrument values and roles are ignored");
        var ids = new[] { "default", "gh3.es335-red", "gh3.es335-black", "gh3.demonik" };
        string Family(string id) => id.StartsWith("gh3.es335", StringComparison.Ordinal) ? "es335" : id;
        Check(PlayerChoices.Jump(ids, 1, 1, Family) == "gh3.demonik" && PlayerChoices.Jump(ids, 3, -1, Family) == "gh3.es335-red", ">> and << jump over the finishes of a model");
    }

    // A guitarist rig: 0 root, 1 the body (no pelvis: the body starts at joint 1), 2 an instrument joint under the root.
    private static string Library(string root)
    {
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(Path.Combine(library, "band", "guitarist"));
        File.WriteAllText(Path.Combine(library, "catalog.json"), "{\"version\":1,\"library\":\"gh3-band-v1\",\"members\":{\"guitarist\":{\"profile\":\"gh3-guitarist-axel-v1\",\"bones\":[\"0000000a\",\"0000000b\",\"0000000c\"]}," +
            "\"bassist\":{\"profile\":\"gh3-bassist-v1\",\"bones\":[\"0000000a\"]}}}");
        using var writer = new BinaryWriter(File.Create(Path.Combine(library, "band", "guitarist", "animation.bin")));
        writer.Write(Encoding.ASCII.GetBytes("GH3ANM01"));
        writer.Write(3);
        writer.Write(0);
        foreach (var (parent, y) in new[] { (-1, 0f), (0, 1f), (0, 0.5f) })
        {
            writer.Write(parent);
            foreach (var value in new[] { 0, y, 0, 0, 0, 0, 1f }) writer.Write(value);
        }
        return library;
    }

    private static byte[] Glb(byte joint)
    {
        var glb = new GlbBuilder();
        var position = glb.Floats(new float[] { 0, 0.5f, 0, 0.1f, 0.5f, 0, 0, 0.7f, 0 }, "VEC3", 3);
        var normal = glb.Floats(new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, "VEC3", 3);
        var joints = glb.Bytes(new byte[] { joint, 0, 0, 0, joint, 0, 0, 0, joint, 0, 0, 0 }, "VEC4", 3);
        var weights = glb.Floats(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }, "VEC4", 3);
        var indices = glb.Indices(new[] { 0, 1, 2 });
        var ibm = glb.Floats(new float[]
        {
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -0.5f, 0, 1,
        }, "MAT4", 3);
        glb.Json["materials"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "wood" });
        glb.Json["meshes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["primitives"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["attributes"] = new System.Text.Json.Nodes.JsonObject { ["POSITION"] = position, ["NORMAL"] = normal, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights },
                ["indices"] = indices, ["material"] = 0,
            }),
        });
        glb.Json["nodes"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["name"] = "Armature", ["children"] = new System.Text.Json.Nodes.JsonArray(1, 4) },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "bone-0000000a", ["children"] = new System.Text.Json.Nodes.JsonArray(2, 3) },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "bone-0000000b", ["translation"] = new System.Text.Json.Nodes.JsonArray(0, 1, 0) },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "bone-0000000c", ["translation"] = new System.Text.Json.Nodes.JsonArray(0, 0.5, 0) },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "guitar", ["mesh"] = 0, ["skin"] = 0 });
        glb.Json["skins"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            { ["joints"] = new System.Text.Json.Nodes.JsonArray(1, 2, 3), ["inverseBindMatrices"] = ibm });
        glb.Json["scenes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["nodes"] = new System.Text.Json.Nodes.JsonArray(0) });
        return glb.Build();
    }

    private static string Package(string root, string name, byte[] glb, string extra = "", byte[]? highway = null)
    {
        var folder = Path.Combine(root, name + ".chinst");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "instrument.glb"), glb);
        if (highway != null) File.WriteAllBytes(Path.Combine(folder, "highway.png"), highway);
        File.WriteAllText(Path.Combine(folder, "instrument.json"),
            "{\"version\":1,\"library\":\"gh3-band-v1\",\"profile\":\"gh3-guitarist-axel-v1\",\"kind\":\"guitar\",\"brand\":\"Test\"" + extra + "}");
        File.WriteAllText(Path.Combine(folder, "manifest.json"), PickerLogicTests.Manifest("chinst/1.0", "test." + name, "{\"model\":\"instrument.glb\",\"instrument\":\"instrument.json\"}"));
        return folder;
    }

    private static string Import(string package, string library, string output, List<string>? warnings = null)
    {
        SemanticVersion.TryParse("0.9.8", out var version);
        var source = PackageSource.At(package);
        using var verified = new VerifiedPackage(source, PackageManifest.Parse(source.ReadAll("manifest.json"), PackageKind.Instrument, version));
        var importer = new InstrumentImporter(verified, new TextureJobs(false, 1), library);
        try
        {
            importer.Import(output);
            return "";
        }
        catch (PackageException error) { return error.Message; }
        finally { warnings?.AddRange(importer.Warnings); }
    }

    private static void CheckImport(string root)
    {
        var library = Library(root);
        var output = Path.Combine(root, "guitar-cache");
        var warnings = new List<string>();
        var error = Import(Package(root, "guitar", Glb(2), ",\"highway\":\"highway.png\"", PickerLogicTests.Png4()), library, output, warnings);
        Check(error == "", "a guitar on the instrument joint imports: " + error);
        var model = InstrumentModel.Load(output);
        Check(model.Kind == "guitar" && model.Role == "guitarist" && model.Joints.SequenceEqual(new[] { "0000000a", "0000000b", "0000000c" }),
            "the instrument keeps its kind and joint names");
        Check(model.Highway == Path.Combine(output, "highway.png") && File.Exists(model.Highway), "the highway image goes to the cache");
        Check(warnings.Any(w => w.Contains("512 x 1024")), "a highway that is not 1:2 gives a warning");
        Check(File.Exists(Path.Combine(output, "meshes.bin")) && File.Exists(Path.Combine(output, "stage.json")), "the instrument is a band model folder");
        error = Import(Package(root, "body", Glb(1)), library, Path.Combine(root, "body-cache"));
        Check(error.Contains("follows the body joints"), "a mesh on the body joints is rejected: " + error);
        error = Import(Package(root, "nohighway", Glb(2), ",\"highway\":\"missing.png\""), library, Path.Combine(root, "nohighway-cache"));
        Check(error.Contains("missing"), "a missing highway file is rejected: " + error);
        error = Import(Package(root, "plain", Glb(2)), library, Path.Combine(root, "plain-cache"));
        Check(error == "" && InstrumentModel.Load(Path.Combine(root, "plain-cache")).Highway == "", "no highway: the character's highway stays");
        var kind = Package(root, "kazoo", Glb(2));
        File.WriteAllText(Path.Combine(kind, "instrument.json"), "{\"version\":1,\"library\":\"gh3-band-v1\",\"profile\":\"gh3-guitarist-axel-v1\",\"kind\":\"kazoo\"}");
        Check(Import(kind, library, Path.Combine(root, "kazoo-cache")).Contains("guitar or bass"), "only guitars and basses");
    }
}
