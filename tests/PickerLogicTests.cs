using System.Text;
using System.Text.Json.Nodes;
using Gh3Skin;
using Gh3Skin.Packages;

// Stage and character pickers (docs/PLAYER-GUIDE.md): the saved choices, the stage selection, character import, the
// cache clean-up, and the texture reference rules of PACKAGE-FORMAT.md section 6.6.
internal static class PickerLogicTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh3-picker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckChoices(root);
            CheckSelection(root);
            CheckSuggestions(root);
            CheckNativeVenues();
            CheckCharacters(root);
            CheckSamplerOnlyImage(root);
            CheckCacheCleanUp(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckChoices(string root)
    {
        var path = Path.Combine(root, "choices.json");
        var defaults = PlayerChoices.Load(path, out var problem);
        Check(defaults.Stage == "song" && defaults.SongSuggestions && defaults.VenueTrack && defaults.GeneratedDirection && !defaults.ReduceFlashing && defaults.Character("bassist") == "default" &&
            problem == "", "no file gives the defaults");
        defaults.Stage = PlayerChoices.RandomStage;
        defaults.SongSuggestions = false;
        defaults.VenueTrack = false;
        defaults.ReduceFlashing = true;
        defaults.Characters["bassist"] = "gh3.axel";
        defaults.Save(path);
        var read = PlayerChoices.Load(path, out problem);
        Check(read.Stage == "random" && !read.SongSuggestions && !read.VenueTrack && read.GeneratedDirection && read.ReduceFlashing && read.Character("bassist") == "gh3.axel" &&
            read.Character("singer") == "default", "choices round-trip");
        File.WriteAllText(path, "{\"stage\": \"gh3.budokan\", \"songSuggestions\": true}");
        read = PlayerChoices.Load(path, out _);
        Check(read.Stage == "gh3.budokan" && read.VenueTrack && read.GeneratedDirection && !read.ReduceFlashing, "an older file keeps its stage and gets the direction defaults");
        File.WriteAllText(path, "{\"stage\": \"../evil\", \"characters\": {\"drummer\": \"Bad Id\"}}");
        read = PlayerChoices.Load(path, out _);
        Check(read.Stage == "song" && read.Character("drummer") == "default", "invalid values are ignored");
        File.WriteAllText(path, "{not json");
        read = PlayerChoices.Load(path, out problem);
        Check(read.Stage == "song" && problem.Contains("defaults"), "a damaged file gives the defaults and a message");
        Check(PlayerChoices.Switch(null, true) && !PlayerChoices.Switch(null, false) && !PlayerChoices.Switch("off", true) && !PlayerChoices.Switch("0", true) &&
            PlayerChoices.Switch("on", false), "environment switches override the choices");
    }

    private static void CheckSelection(string root)
    {
        var installed = new[] { "gh3.budokan", "gh3.dive", "gh3.hell" };
        var random = new Random(1);
        var choices = new PlayerChoices { Stage = "gh3.hell" };
        var suggestion = new StageSuggestion("gh3.dive", "", "song.ini gh3_stage");
        Check(StageSelection.Choose(choices, null, installed, null, random).StageId == "gh3.hell", "a picked stage plays");
        Check(StageSelection.Choose(choices, null, installed, suggestion, random).StageId == "gh3.dive", "a song suggestion wins when suggestions are on");
        choices.SongSuggestions = false;
        Check(StageSelection.Choose(choices, null, installed, suggestion, random).StageId == "gh3.hell", "suggestions off keeps the picked stage");
        choices.SongSuggestions = true;
        Check(StageSelection.Choose(choices, "gh3.budokan", installed, suggestion, random).StageId == "gh3.budokan", "GH3_SKIN_STAGE with an ID wins");
        Check(StageSelection.Choose(choices, "off", installed, suggestion, random).StageId == "", "GH3_SKIN_STAGE=off turns the stage off");
        choices.Stage = PlayerChoices.SongStage;
        choices.SongSuggestions = false;
        Check(StageSelection.Choose(choices, null, installed, suggestion, random).StageId == "gh3.dive", "song's choice uses the suggestion");
        var fallback = StageSelection.Choose(choices, null, installed, null, random);
        Check(fallback.StageId == "gh3.budokan" && fallback.Reason.Contains("no stage"), "song's choice without a suggestion plays the default stage");
        var noBudokan = StageSelection.Choose(choices, null, new[] { "gh3.dive", "gh3.hell" }, null, random);
        Check(noBudokan.StageId is "gh3.dive" or "gh3.hell" && noBudokan.Reason.Contains("random"), "song's choice without the default stage is random");
        var missing = StageSelection.Choose(choices, null, installed, new StageSuggestion("other.stage", "", "song.ini gh3_stage"), random);
        Check(installed.Contains(missing.StageId) && missing.Reason.Contains("not installed"), "an uninstalled suggestion is skipped with a reason");
        var package = StageSelection.Choose(choices, null, installed, new StageSuggestion("", "/songs/x/venue.chstage", "venue.chstage"), random);
        Check(package.PackagePath == "/songs/x/venue.chstage", "a song venue package is selected by its path");
        choices.Stage = PlayerChoices.RandomStage;
        var seen = new HashSet<string>();
        var previous = "";
        for (var i = 0; i < 60; i++)
        {
            var pick = StageSelection.Choose(choices, null, installed, null, random, previous).StageId;
            Check(pick != previous, "random does not repeat the previous stage");
            seen.Add(pick);
            previous = pick;
        }
        Check(seen.SetEquals(installed), "random uses all installed stages");
        choices.Stage = "removed.stage";
        Check(StageSelection.Choose(choices, null, installed, null, random).StageId == "gh3.budokan", "a removed stage falls back to the default stage");
        Check(StageSelection.Choose(choices, "random", new[] { "only.one" }, null, random, "only.one").StageId == "only.one", "random with one stage");
    }

    private static void CheckSuggestions(string root)
    {
        var song = Path.Combine(root, "song");
        Directory.CreateDirectory(song);
        Check(StageSelection.Suggestion(song, "", "", null) == null, "a plain song has no suggestion");
        Check(StageSelection.SongIniValue(new[] { "[Song]", "name = X", "GH3_Stage = gh3.dive ", "[other]", "gh3_stage = no" }, "gh3_stage") == "gh3.dive",
            "song.ini keys are case-insensitive and only in [song]");
        File.WriteAllText(Path.Combine(song, "song.ini"), "[song]\nname = Slow Ride\nartist = Foghat\ngh3_stage = gh3.hell\n");
        Check(StageSelection.Suggestion(song, "Slow Ride", "Foghat", null)?.StageId == "gh3.hell", "song.ini gh3_stage suggests a stage");
        File.WriteAllText(Path.Combine(song, "song.ini"), "[song]\nname = Slow Ride\n");
        File.WriteAllBytes(Path.Combine(song, "slowride_song.pak.xen"), new byte[] { 0 });
        Check(StageSelection.Suggestion(song, "", "", null)?.StageId == "gh3.party", "a native career song suggests its GH3 venue");
        File.Delete(Path.Combine(song, "slowride_song.pak.xen"));
        var index = Path.Combine(root, "index.json");
        File.WriteAllText(index, "{\"version\":1,\"songs\":[{\"title\":\"Raining Blood\",\"artist\":\"Slayer\",\"file\":\"rainingblood.json\"}]}");
        Check(StageSelection.Suggestion(song, "Raining  Blood", "SLAYER", index)?.StageId == "gh3.hell", "an original GH3 song found by title suggests its venue");
        Check(StageSelection.Suggestion(song, "My Curse", "Killswitch Engage", index) == null, "a GH3 bonus song has no venue");
        Directory.CreateDirectory(Path.Combine(song, "venue.chstage"));
        Check(StageSelection.Suggestion(song, "", "", index)?.PackagePath == Path.Combine(song, "venue.chstage"), "venue.chstage in the song folder comes first");
    }

    private static void CheckNativeVenues()
    {
        Check(NativeVenues.Songs.Count == 42 && NativeVenues.Of("bossdevil") == "gh3.hell" && NativeVenues.Of("mycurse") == null, "the 42 GH3 career songs have a venue");
        // GH3_NATIVE_DATA=<GH3 DATA folder>: the table equals GH3_Career_Songs in qb.pak.xen.
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_DATA") is not { } data) return;
        var pak = Path.Combine(data, "PAK", "qb.pak.xen");
        var bytes = File.ReadAllBytes(pak).Concat(File.ReadAllBytes(Path.ChangeExtension(pak, null)[..^4] + ".pab.xen")).ToArray();
        foreach (var (level, songs) in NativeVenues.Table)
        {
            var levelKey = BitConverter.GetBytes(Convert.ToUInt32(Gh3Checksum.Of(level), 16)).Reverse().ToArray();
            Check(IndexOf(bytes, levelKey) >= 0, $"{level} is in the GH3 career data");
            foreach (var song in songs)
                Check(IndexOf(bytes, BitConverter.GetBytes(Convert.ToUInt32(Gh3Checksum.Of(song), 16)).Reverse().ToArray()) >= 0, $"{song} is in the GH3 data");
        }
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }

    // A small glTF binary with the accessors that the tests need.
    private sealed class GlbBuilder
    {
        private readonly List<byte> _bin = new();
        public JsonObject Json { get; } = new()
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" }, ["bufferViews"] = new JsonArray(), ["accessors"] = new JsonArray(), ["scene"] = 0,
        };

        private int View(byte[] data)
        {
            while (_bin.Count % 4 != 0) _bin.Add(0);
            var views = (JsonArray)Json["bufferViews"]!;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Count, ["byteLength"] = data.Length });
            _bin.AddRange(data);
            return views.Count - 1;
        }

        public int Floats(float[] values, string type, int count)
        {
            var data = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, data, 0, data.Length);
            var accessors = (JsonArray)Json["accessors"]!;
            accessors.Add(new JsonObject { ["bufferView"] = View(data), ["componentType"] = 5126, ["count"] = count, ["type"] = type });
            return accessors.Count - 1;
        }

        public int Bytes(byte[] values, string type, int count, int componentType = 5121)
        {
            var accessors = (JsonArray)Json["accessors"]!;
            accessors.Add(new JsonObject { ["bufferView"] = View(values), ["componentType"] = componentType, ["count"] = count, ["type"] = type });
            return accessors.Count - 1;
        }

        public int Indices(int[] values) => Bytes(values.SelectMany(BitConverter.GetBytes).ToArray(), "SCALAR", values.Length, 5125);

        public void Image(string name, byte[] png)
        {
            Json["images"] ??= new JsonArray();
            ((JsonArray)Json["images"]!).Add(new JsonObject { ["name"] = name, ["mimeType"] = "image/png", ["bufferView"] = View(png) });
        }

        public byte[] Build()
        {
            while (_bin.Count % 4 != 0) _bin.Add(0);
            Json["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Count });
            var text = Encoding.UTF8.GetBytes(Json.ToJsonString());
            var padded = text.Concat(Enumerable.Repeat((byte)' ', (4 - text.Length % 4) % 4)).ToArray();
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(0x46546c67u); writer.Write(2u); writer.Write(12 + 8 + padded.Length + 8 + _bin.Count);
            writer.Write(padded.Length); writer.Write(0x4e4f534au); writer.Write(padded);
            writer.Write(_bin.Count); writer.Write(0x004e4942u); writer.Write(_bin.ToArray());
            return output.ToArray();
        }
    }

    private static byte[] Png4() =>
        PackageLogicTests.PngFile(4, 4, 6, 8, Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 0 }.Concat(Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(p => p))).ToArray());

    private static string Manifest(string format, string id, string entry, string requires = "") =>
        $"{{\"format\":\"{format}\",\"id\":\"{id}\",\"version\":\"1.0.0\",\"name\":\"{id}\",\"authors\":[{{\"name\":\"T\"}}],\"license\":\"CC0-1.0\"," +
        $"\"permissions\":{{\"redistribute\":true,\"modify\":true,\"commercial\":true}},{requires}\"entry\":{entry},\"files\":{{}}}}";

    // Library cache with a two-bone singer rig (0000000a at the root, 0000000b one metre up) and a bassist rig without 0000000b.
    private static string Library(string root)
    {
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(Path.Combine(library, "band", "singer"));
        File.WriteAllText(Path.Combine(library, "catalog.json"),
            "{\"version\":1,\"members\":{\"singer\":{\"bones\":[\"0000000a\",\"0000000b\"]},\"bassist\":{\"bones\":[\"0000000a\",\"0000000c\"]}}}");
        using var writer = new BinaryWriter(File.Create(Path.Combine(library, "band", "singer", "animation.bin")));
        writer.Write(Encoding.ASCII.GetBytes("GH3ANM01"));
        writer.Write(2);
        writer.Write(0);
        foreach (var (parent, y) in new[] { (-1, 0f), (0, 1f) })
        {
            writer.Write(parent);
            foreach (var value in new[] { 0, y, 0, 0, 0, 0, 1f }) writer.Write(value);
        }
        return library;
    }

    private static byte[] CharacterGlb(float jointOffset = 0, float bindOffset = 0)
    {
        var glb = new GlbBuilder();
        var position = glb.Floats(new float[] { 0, 0, 0, 1, 0, 0, 0, 2, 0 }, "VEC3", 3);
        var normal = glb.Floats(new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, "VEC3", 3);
        var uv = glb.Floats(new float[] { 0, 0, 1, 0, 0, 1 }, "VEC2", 3);
        var joints = glb.Bytes(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0 }, "VEC4", 3);
        var weights = glb.Floats(new float[] { 1, 0, 0, 0, 0.5f, 0.5f, 0, 0, 1, 0, 0, 0 }, "VEC4", 3);
        var indices = glb.Indices(new[] { 0, 1, 2 });
        // Column-major inverse bind matrices: identity, and a translation by -(1 + bindOffset) on y.
        var ibm = glb.Floats(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1 - bindOffset, 0, 1 }, "MAT4", 2);
        glb.Image("skin", Png4());
        glb.Json["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        glb.Json["materials"] = new JsonArray(new JsonObject
        {
            ["name"] = "body", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } },
        });
        glb.Json["meshes"] = new JsonArray(new JsonObject
        {
            ["primitives"] = new JsonArray(new JsonObject
            {
                ["attributes"] = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights },
                ["indices"] = indices, ["material"] = 0,
            }),
        });
        glb.Json["nodes"] = new JsonArray(
            new JsonObject { ["name"] = "Armature", ["children"] = new JsonArray(1, 3) },
            new JsonObject { ["name"] = "bone-0000000a", ["children"] = new JsonArray(2) },
            new JsonObject { ["name"] = "bone-0000000b", ["translation"] = new JsonArray(0, 1 + jointOffset, 0) },
            new JsonObject { ["name"] = "body", ["mesh"] = 0, ["skin"] = 0 });
        glb.Json["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1, 2), ["inverseBindMatrices"] = ibm });
        glb.Json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        return glb.Build();
    }

    private static string CharacterPackage(string root, string name, byte[] glb, string attachments = "")
    {
        var folder = Path.Combine(root, name + ".chchar");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "character.glb"), glb);
        File.WriteAllText(Path.Combine(folder, "character.json"), "{\"version\":1,\"library\":\"gh3-band-v1\",\"profile\":\"gh3-singer-male-v1\",\"hairMaterials\":[\"body\"]" +
            (attachments.Length > 0 ? ",\"attachments\":" + attachments : "") + "}");
        File.WriteAllText(Path.Combine(folder, "manifest.json"), Manifest("chchar/1.0", "test." + name, "{\"model\":\"character.glb\",\"character\":\"character.json\"}"));
        return folder;
    }

    private static string ImportCharacter(string package, string library, string output, List<string>? warnings = null)
    {
        SemanticVersion.TryParse("0.9.8", out var version);
        var source = PackageSource.At(package);
        var manifest = PackageManifest.Parse(source.ReadAll("manifest.json"), PackageKind.Character, version);
        using var verified = new VerifiedPackage(source, manifest);
        var importer = new CharacterImporter(verified, new TextureJobs(false, 1), library);
        try
        {
            importer.Import(output);
            return "";
        }
        catch (PackageException error) { return error.Message; }
        finally { warnings?.AddRange(importer.Warnings); }
    }

    private static void CheckCharacters(string root)
    {
        var library = Library(root);
        var output = Path.Combine(root, "character-cache");
        var error = ImportCharacter(CharacterPackage(root, "good", CharacterGlb()), library, output);
        Check(error == "", "a character on the profile rig imports: " + error);
        var model = CharacterModel.Load(output);
        Check(model.Role == "singer" && model.Joints.SequenceEqual(new[] { "0000000a", "0000000b" }), "the character keeps its joint names and role");
        // Unity coordinates: the second inverse bind matrix moves 1 m down (row-major m13 = -1).
        Check(Math.Abs(model.Bindposes[1][7] + 1) < 1e-6 && Math.Abs(model.Bindposes[1][5] - 1) < 1e-6 && Math.Abs(model.Bindposes[0][7]) < 1e-6, "bind poses come from the GLB");
        Check(model.Map(new[] { "0000000b", "0000000a", "0000000c" })!.SequenceEqual(new[] { 1, 0 }), "joints map to a rig by name");
        Check(model.Map(new[] { "0000000a", "0000000c" }) == null, "a rig without a joint cannot take the character");
        var stage = PackageJson.Object(File.ReadAllBytes(Path.Combine(output, "stage.json")));
        var material = (JsonObject)((JsonObject)stage["materials"]!)[Gh3Checksum.Of("material-body")]!;
        Check(PackageJson.Text(material["texture"]) == Gh3Checksum.Of("image-0") && PackageJson.Bool(material["sortedTransparency"]) == true,
            "creator names get 8-digit runtime names; hair materials sort");
        Check(File.Exists(Path.Combine(output, "textures", Gh3Checksum.Of("image-0") + ".tex")), "the texture goes to the cache");
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(output, "meshes.bin"))))
            Check(Encoding.ASCII.GetString(reader.ReadBytes(8)) == "GH3BND02" && reader.ReadInt32() == 1, "the mesh is GH3BND02");
        var moved = ImportCharacter(CharacterPackage(root, "moved", CharacterGlb(jointOffset: 0.001f, bindOffset: 0.001f)), library, Path.Combine(root, "moved-cache"));
        Check(moved.Contains("joint positions differ"), "a joint 1 mm off the profile is rejected: " + moved);
        var unbound = ImportCharacter(CharacterPackage(root, "unbound", CharacterGlb(bindOffset: 0.01f)), library, Path.Combine(root, "unbound-cache"));
        Check(unbound.Contains("inverse bind matrices differ"), "inverse bind matrices that do not fit the joints are rejected: " + unbound);
        Check(model.Instrument == "", "no attachments: the default instrument");
        File.WriteAllText(Path.Combine(library, "band", "band.json"), "{\"version\":1,\"members\":[{\"role\":\"singer\",\"instrument\":\"Microphone001\"}]}");
        var warnings = new List<string>();
        error = ImportCharacter(CharacterPackage(root, "named", CharacterGlb(), "{\"instrument\":\"Microphone001\"}"), library, Path.Combine(root, "named-cache"), warnings);
        Check(error == "" && warnings.Count == 0 && CharacterModel.Load(Path.Combine(root, "named-cache")).Instrument == "Microphone001", "a named library instrument goes to the cache");
        error = ImportCharacter(CharacterPackage(root, "unknown", CharacterGlb(), "{\"instrument\":\"Kazoo\"}"), library, Path.Combine(root, "unknown-cache"), warnings);
        Check(error == "" && warnings.Any(w => w.Contains("Kazoo")) && CharacterModel.Load(Path.Combine(root, "unknown-cache")).Instrument == "",
            "an unknown instrument gives a warning and the default");
        error = ImportCharacter(CharacterPackage(root, "badslot", CharacterGlb(), "{\"hat\":\"x\"}"), library, Path.Combine(root, "badslot-cache"));
        Check(error.Contains("hat"), "an unknown attachment slot is rejected: " + error);
    }

    private static void CheckSamplerOnlyImage(string root)
    {
        var folder = Path.Combine(root, "stage.chstage");
        Directory.CreateDirectory(folder);
        var glb = new GlbBuilder();
        var position = glb.Floats(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, "VEC3", 3);
        var normal = glb.Floats(new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, "VEC3", 3);
        var indices = glb.Indices(new[] { 0, 1, 2 });
        glb.Image("base", Png4());
        glb.Image("sampler-only", Png4());
        glb.Json["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        glb.Json["materials"] = new JsonArray(new JsonObject
        {
            ["name"] = "masked", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } },
            ["extras"] = new JsonObject { ["gh3Shading"] = "lit", ["gh3MaskTexture"] = "#sampler-only" },
        });
        glb.Json["meshes"] = new JsonArray(new JsonObject
        {
            ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normal }, ["indices"] = indices, ["material"] = 0 }),
        });
        glb.Json["nodes"] = new JsonArray(new JsonObject { ["name"] = "floor", ["mesh"] = 0 });
        glb.Json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        File.WriteAllBytes(Path.Combine(folder, "stage.glb"), glb.Build());
        File.WriteAllText(Path.Combine(folder, "placements.json"), "{\"version\":1,\"band\":{\"guitarist\":{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}}}");
        File.WriteAllText(Path.Combine(folder, "cameras.json"),
            "{\"version\":1,\"shots\":{\"w\":{\"type\":\"wide\",\"control\":\"TwoCam\",\"points\":[{\"position\":[0,1,-3],\"horizontalFov\":60,\"anchor\":\"world\",\"lookAt\":\"guitarist.root\"}]}},\"groups\":{\"wide\":[\"w\"]}}");
        File.WriteAllText(Path.Combine(folder, "manifest.json"), Manifest("chstage/1.0", "test.sampler", "{\"model\":\"stage.glb\",\"placements\":\"placements.json\",\"cameras\":\"cameras.json\"}"));
        SemanticVersion.TryParse("0.9.8", out var version);
        var source = PackageSource.At(folder);
        using var verified = new VerifiedPackage(source, PackageManifest.Parse(source.ReadAll("manifest.json"), PackageKind.Stage, version));
        var output = Path.Combine(root, "stage-cache");
        new StageCompiler(verified, new TextureJobs(false, 1), null).Compile(output);
        var stage = PackageJson.Object(File.ReadAllBytes(Path.Combine(output, "stage.json")));
        var material = ((JsonObject)stage["materials"]!).Select(pair => (JsonObject)pair.Value!).First(m => PackageJson.Text(m["source"]) == "masked");
        var name = Gh3Checksum.Of("image-1");
        Check(PackageJson.Text(material["mask"]) == name && File.Exists(Path.Combine(output, "textures", name + ".tex")),
            "a '#name' reference to an image without a glTF texture compiles (as pipeline/stagepack/pack.py)");
    }

    private static void CheckCacheCleanUp(string root)
    {
        var mod = Path.Combine(root, "cleanup-packages");
        var cache = Path.Combine(root, "cleanup-cache");
        Directory.CreateDirectory(mod);
        var used = new string('a', 64);
        var known = new string('b', 64);
        foreach (var name in new[] { $"{used}-i{PackageManager.ImporterVersion}", $"{used}-i0", $"{new string('c', 64)}-i{PackageManager.ImporterVersion}",
                     $"{known}-i{PackageManager.ImporterVersion}", "keep-me", $"{new string('d', 64)}-i1x" })
        {
            Directory.CreateDirectory(Path.Combine(cache, name));
            File.WriteAllText(Path.Combine(cache, name, "cache.json"), "{}");
        }
        var songPackage = Path.Combine(root, "song-venue.chstage");
        Directory.CreateDirectory(songPackage);
        var gone = Path.Combine(root, "gone.chstage");
        File.WriteAllText(Path.Combine(cache, "index.json"),
            new JsonObject
            {
                [songPackage] = new JsonObject { ["signature"] = "x", ["sha256"] = known },
                [gone] = new JsonObject { ["signature"] = "x", ["sha256"] = new string('e', 64) },
            }.ToJsonString());
        SemanticVersion.TryParse("0.9.8", out var version);
        var manager = new PackageManager(new PackageSettings(mod, Array.Empty<string>(), cache, false, "Test", "None", version, "", 1));
        manager.DiscoverOnly();
        manager.CleanCache();
        var left = Directory.EnumerateDirectories(cache).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Check(left.SequenceEqual(new[] { $"{new string('b', 64)}-i{PackageManager.ImporterVersion}", $"{new string('d', 64)}-i1x", "keep-me" }),
            "the clean-up keeps the caches that an existing package file uses and other folders: " + string.Join(", ", left));
        var index = PackageJson.Object(File.ReadAllBytes(Path.Combine(cache, "index.json")));
        Check(index.ContainsKey(songPackage) && !index.ContainsKey(gone), "index.json forgets packages that are gone");
    }
}
