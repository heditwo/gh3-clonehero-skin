using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// C# port of pipeline/stagepack/pack.py compile_stage and actor.py compile_actor: a venue package becomes the compiled
// stage folder that the mod loads (GH3STG04 meshes, stage.json, cameras, lighting, scripts, fixtures, actors). The
// pre-encoded textures go to `textures/<name>.tex` (TextureCodec) instead of PNG. pipeline/package/cachecheck.py compares the output with the
// Python compiler.
internal sealed partial class StageCompiler
{
    private static readonly string[] Roles = { "guitarist", "bassist", "singer", "drummer" };
    private static readonly string[] Controls = { "TwoCam", "HandCam", "Orbit", "OrbitAndMorphTwoCam", "OrbitContinuous", "Keyframes", "Pan", "ThreePos", "FourPos", "SpecialStepMove" };
    private static readonly string[] Effects = { "pyro", "sparks", "fireworks", "smoke" };
    private static readonly string[] Shading = { "lit", "ambient", "unlit" };
    private readonly VerifiedPackage _package;
    private readonly TextureJobs _textures;
    private readonly JsonObject? _catalog;
    private readonly List<string> _sources = new();
    public List<string> Warnings { get; } = new();

    public StageCompiler(VerifiedPackage package, TextureJobs textures, JsonObject? libraryCatalog)
    {
        _package = package;
        _textures = textures;
        _catalog = libraryCatalog?["members"] as JsonObject;
    }

    private static string Key(string text) => Gh3Checksum.Of(text);

    private JsonNode ReadJson(string name)
    {
        _sources.Add(name);
        return Parse(_package.Read(name));
    }

    public (int Meshes, int Vertices, int Materials, int Fixtures, int Actors) Compile(string folder)
    {
        var manifest = _package.Manifest;
        var entry = manifest.Entry;
        var placements = ReadJson(entry["placements"]) as JsonObject ?? throw new PackageException("placements.json must be an object");
        var band = placements["band"] as JsonObject ?? throw new PackageException("placements.json needs 'band'");
        if (band.Count == 0 || band.Any(pair => !Roles.Contains(pair.Key))) throw new PackageException("a stage needs one to four known performer placements");
        foreach (var (role, placement) in band) Transform(placement, "band " + role);
        var crowd = placements["crowd"] as JsonArray ?? new JsonArray();
        if (crowd.Count > 8) throw new PackageException("invalid crowd members");
        var crowdRoles = new HashSet<string>();
        foreach (var member in crowd)
        {
            Fields(member, "crowd member", new[] { "role", "model", "yaw" }, "role", "model", "yaw");
            var role = Text(member!["role"]) ?? "";
            if (!Regex.IsMatch(role, @"\Acrowd[1-8]\z") || !crowdRoles.Add(role)) throw new PackageException("invalid or repeated crowd role");
            if (!Regex.IsMatch(Text(member["model"]) ?? "", @"\A[a-zA-Z0-9_]{1,64}\z")) throw new PackageException("invalid crowd model");
            Number(member["yaw"], -360, 360, "crowd yaw");
        }
        var waypoints = new JsonObject();
        foreach (var (role, placement) in band) waypoints[role + "_start"] = Clone(placement);
        if (placements["waypoints"] is JsonObject extra)
            foreach (var (name, placement) in extra)
            {
                if (waypoints.ContainsKey(name)) throw new PackageException("stage cannot replace a reserved start waypoint");
                Transform(placement, "waypoint " + name);
                waypoints[name] = Clone(placement);
            }
        var effects = placements["effects"] as JsonObject ?? new JsonObject();
        foreach (var (name, list) in effects)
        {
            if (!Effects.Contains(name) || list is not JsonArray emitters || emitters.Count > 64) throw new PackageException("invalid stage effect");
            foreach (var placement in emitters) Transform(placement, "effect " + name);
        }
        var cameras = CompileCameras(ReadJson(entry["cameras"]), band, waypoints);
        if (_catalog != null)
            foreach (var (_, shots) in (JsonObject)cameras["arrays"]!)
                foreach (var shot in (JsonArray)shots!)
                    foreach (var point in (JsonArray)shot!["points"]!)
                        foreach (var (role, bone) in new[] { (Text(point!["lockTo"])!, Text(point["lockBone"])!), (Text(point["lookAt"])!, Text(point["lookBone"])!) })
                            if (role is not ("" or "world") && bone != "00000000" &&
                                !((_catalog[role]?["bones"] as JsonArray)?.Any(b => Text(b) == bone) ?? false))
                                throw new PackageException($"camera anchor is not in the shared rig: {role}/{bone}");
        var lighting = CompileLighting(entry.TryGetValue("lighting", out var lightingPath) ? ReadJson(lightingPath) : BasicLighting());
        _sources.Add(entry["model"]);
        var glb = new GlbDocument(_package.Read(entry["model"]));
        Directory.CreateDirectory(folder);
        var textureFolder = Path.Combine(folder, "textures");
        var (materials, materialIds, _) = ImportMaterials(glb, folder);
        if (glb.List("animations").Count > 0) throw new PackageException("put stage animations in an actor GLB");
        var fallback = materialIds.Count - 1;
        var meshes = new List<MeshPart>();
        var groups = new List<string>();
        var visibility = new List<string>();
        var hidden = new HashSet<string>();
        var visibilityDefaults = new Dictionary<string, bool>();
        var fixtures = new JsonArray();
        var fixtureModels = new Dictionary<string, List<MeshPart>>();
        var objectIds = new List<string>();
        var objects = new JsonObject();
        var firstBeams = (JsonObject)((JsonObject)lighting["snapshots"]!).First().Value!["beams"]!;
        foreach (var (_, node, world) in glb.Nodes())
        {
            if (node["skin"] != null) throw new PackageException("skinned stage objects require an actor definition");
            var extras = node["extras"] as JsonObject ?? new JsonObject();
            var fixture = Text(extras["gh3FixtureId"]);
            var objectName = extras["gh3Object"] == null ? "" : Text(extras["gh3Object"]) ?? throw new PackageException("invalid scenery object name");
            if (objectName.Length > 128) throw new PackageException("invalid scenery object name");
            var objectId = Regex.IsMatch(objectName, @"\A0x[0-9a-fA-F]{8}\z") ? objectName[2..].ToLowerInvariant() : objectName.Length > 0 ? Key(objectName) : "";
            if (objectName.Length > 0)
            {
                var pivot = Vector(extras["gh3ObjectPivot"] ?? new JsonArray(0, 0, 0), 3, "object pivot");
                var orientation = Rotation(extras["gh3ObjectRotation"] ?? new JsonArray(0, 0, 0, 1), "object rotation");
                var created = extras["gh3ObjectCreated"] == null || (Bool(extras["gh3ObjectCreated"]) ?? throw new PackageException("invalid scenery object startup"));
                var script = extras["gh3ObjectScript"] == null ? "" : Text(extras["gh3ObjectScript"]) ?? throw new PackageException("invalid scenery object startup");
                if (script.Length > 256) throw new PackageException("invalid scenery object startup");
                var placement = new JsonObject
                {
                    ["position"] = Clone(extras["gh3ObjectPivot"]) ?? Array(pivot), ["rotation"] = Clone(extras["gh3ObjectRotation"]) ?? Array(orientation),
                    ["created"] = created, ["script"] = script,
                };
                if (objects[objectId] is JsonObject existing)
                {
                    if (!Vector(existing["position"], 3, "pivot").SequenceEqual(pivot) || !Vector(existing["rotation"], 4, "rotation").SequenceEqual(orientation) ||
                        Bool(existing["created"]) != created || Text(existing["script"]) != script)
                        throw new PackageException("a scenery object must have one pivot");
                }
                else objects[objectId] = placement;
            }
            if (node["mesh"] == null) continue;
            var visibilityGroup = extras["gh3VisibilityGroup"] == null ? "" : Text(extras["gh3VisibilityGroup"]) ?? throw new PackageException("invalid scenery visibility group");
            var initiallyVisible = extras["gh3InitiallyVisible"] == null || (Bool(extras["gh3InitiallyVisible"]) ?? throw new PackageException("invalid scenery visibility group"));
            if (visibilityGroup.Length > 128) throw new PackageException("invalid scenery visibility group");
            if (!initiallyVisible && visibilityGroup.Length == 0) throw new PackageException("hidden scenery needs a visibility group");
            if (visibilityGroup.Length > 0)
            {
                if (fixture != null) throw new PackageException("visibility groups apply to scenery, not fixtures");
                if (visibilityDefaults.TryGetValue(visibilityGroup, out var state) && state != initiallyVisible) throw new PackageException("a visibility group must have one initial state");
                visibilityDefaults[visibilityGroup] = initiallyVisible;
                if (!initiallyVisible) hidden.Add(visibilityGroup);
            }
            var geometry = world;
            double[]? fixtureRotation = null;
            double[] scale = { 1, 1, 1 };
            if (fixture != null)
            {
                var m = world.Upper3();
                for (var c = 0; c < 3; c++) scale[c] = Math.Sqrt(m[c] * m[c] + m[3 + c] * m[3 + c] + m[6 + c] * m[6 + c]);
                var orientation = new double[9];
                for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) orientation[r * 3 + c] = m[r * 3 + c] / scale[c];
                var det = new Matrix4d(new double[16]).WithUpper3(orientation, new double[3]).Determinant3();
                if (det < 0)
                {
                    scale[2] *= -1;
                    for (var r = 0; r < 3; r++) orientation[r * 3 + 2] *= -1;
                }
                for (var a = 0; a < 3; a++)
                    for (var b = 0; b < 3; b++)
                    {
                        var dot = orientation[a] * orientation[b] + orientation[3 + a] * orientation[3 + b] + orientation[6 + a] * orientation[6 + b];
                        if (Math.Abs(dot - (a == b ? 1 : 0)) > 1e-5 + 1e-5 * (a == b ? 1 : 0)) throw new PackageException("apply fixture shear before export");
                    }
                geometry = Matrix4d.Diagonal(scale[0], scale[1], scale[2], 1);
                fixtureRotation = Matrix4d.Quaternion(orientation);
            }
            var local = new List<MeshPart>();
            var meshIndex = node["mesh"]!.GetValue<int>();
            foreach (var primitive in (JsonArray)glb.Item("meshes", meshIndex)["primitives"]!)
            {
                var material = materialIds[primitive!["material"] is JsonValue mi ? mi.GetValue<int>() : fallback];
                var data = StageGeometry.Primitive(glb, (JsonObject)primitive, geometry);
                var definition = materials[material];
                if (Number(definition["cutoff"], "cutoff") > 0 && Text(definition["texture"]) is { Length: > 0 } && Bool(definition["crowd"]) != true &&
                    Text(definition["shading"]) is "" && definition["gh3"] == null)
                    Warnings.Add($"material {Text(definition["source"])}: alpha-mask mesh trimming is not done by the C# importer (the shader discards the same pixels)");
                if (data.Faces.Length > 0) local.AddRange(StageGeometry.Split(material, StageGeometry.PackStatic(data), StageGeometry.StaticStride, data.Faces));
            }
            if (fixture != null)
            {
                var id = Key(fixture);
                if (!firstBeams.ContainsKey(id)) throw new PackageException($"fixture has no lighting beam: {fixture}");
                if (fixtures.Any(f => Text(f!["id"]) == id)) throw new PackageException($"duplicate light fixture: {fixture}");
                var model = Key($"mesh-{meshIndex}-{PythonList(scale)}");
                if (!fixtureModels.ContainsKey(model)) fixtureModels[model] = local;
                var row = new JsonObject
                {
                    ["id"] = id, ["model"] = model, ["position"] = Array(world.Translation), ["rotation"] = Array(fixtureRotation!),
                    ["fixedPosition"] = Bool(extras["gh3FixtureFixedPosition"]) ?? false,
                    ["object"] = Clone(extras["gh3FixtureObject"]) ?? fixture,
                };
                if (Bool(extras["gh3FixtureCreated"]) == false) row["created"] = false;
                if (Text(row["object"]) is not { Length: >= 1 and <= 256 }) throw new PackageException("invalid fixture object name");
                fixtures.Add(row);
            }
            else
            {
                meshes.AddRange(local);
                var group = Key(Text(extras["gh3LightGroup"]) ?? "Stage");
                foreach (var _ in local)
                {
                    groups.Add(group);
                    visibility.Add(visibilityGroup);
                    objectIds.Add(objectId);
                }
            }
        }
        if (meshes.Count == 0) throw new PackageException("stage has no visible scenery");
        _textures.Flush();
        StageGeometry.WriteMeshes(Path.Combine(folder, "meshes.bin"), meshes, skinned: false);
        var start = Vector(band["guitarist"]?["position"] ?? band.First().Value!["position"], 3, "band position");
        var definitionJson = new JsonObject
        {
            ["version"] = 1, ["name"] = manifest.Name, ["cameraPosition"] = Array(new[] { start[0], start[1] + 3, start[2] - 10 }),
            ["cameraTarget"] = Array(new[] { start[0], start[1] + 1, start[2] }), ["fieldOfView"] = 55, ["materials"] = MaterialObject(materials),
            ["lightGroups"] = new JsonArray(groups.Select(g => (JsonNode?)g).ToArray()),
            ["visibilityGroups"] = new JsonArray(visibility.Select(g => (JsonNode?)g).ToArray()),
            ["hiddenGroups"] = new JsonArray(hidden.Intersect(visibility).OrderBy(g => g, StringComparer.Ordinal).Select(g => (JsonNode?)g).ToArray()),
            ["objectIds"] = new JsonArray(objectIds.Select(g => (JsonNode?)g).ToArray()), ["objects"] = objects,
        };
        Write(Path.Combine(folder, "stage.json"), definitionJson);
        if (entry.TryGetValue("scripts", out var scriptsPath)) CopyScripts(scriptsPath, folder);
        foreach (var (name, parts) in fixtureModels)
        {
            var modelFolder = Path.Combine(folder, "fixtures", name);
            var used = new Dictionary<string, JsonObject>();
            foreach (var part in parts) used.TryAdd(part.Material, materials[part.Material]);
            WriteModelDefinition(modelFolder, parts, used, skinned: false);
            Directory.CreateDirectory(Path.Combine(modelFolder, "textures"));
            foreach (var texture in used.Values.Select(m => Text(m["texture"])!).Where(t => t.Length > 0).Distinct())
                File.Copy(Path.Combine(textureFolder, texture + ".tex"), Path.Combine(modelFolder, "textures", texture + ".tex"));
        }
        if (fixtures.Count > 0) Write(Path.Combine(folder, "fixtures", "fixtures.json"), new JsonObject { ["version"] = 1, ["housings"] = fixtures });
        var actors = new JsonArray();
        if (entry.TryGetValue("actors", out var actorsPath))
        {
            var actorBank = ReadJson(actorsPath)["actors"] as JsonObject ?? new JsonObject();
            foreach (var (name, actor) in actorBank) actors.Add(CompileActorEntry(name, actor!, folder));
        }
        if (actors.Count > 0) Write(Path.Combine(folder, "actors.json"), new JsonObject { ["version"] = 1, ["actors"] = actors });
        Write(Path.Combine(folder, "cameras.json"), cameras);
        Write(Path.Combine(folder, "lighting.json"), lighting);
        var pack = new JsonObject
        {
            ["version"] = 1, ["id"] = manifest.Id, ["name"] = manifest.Name, ["library"] = manifest.Requires.FirstOrDefault(r => r.Id.StartsWith("gh3-band-")).Id ?? "gh3-band-v1",
            ["band"] = Clone(band), ["waypoints"] = waypoints, ["effects"] = Clone(effects), ["crowd"] = Clone(crowd),
            ["package"] = new JsonObject { ["id"] = manifest.Id, ["version"] = manifest.Version.ToString() },
            ["sources"] = new JsonObject(_sources.Distinct().Where(manifest.Files.ContainsKey).Select(name =>
                KeyValuePair.Create(name, (JsonNode?)manifest.Files[name].Sha256))),
        };
        Write(Path.Combine(folder, "pack.json"), pack);
        return (meshes.Count, meshes.Sum(m => m.Count), materials.Count, fixtures.Count, actors.Count);
    }

}
