using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// C# port of pipeline/stagepack/pack.py compile_stage and actor.py compile_actor: a venue package becomes the compiled
// stage folder that the mod loads (GH3STG04 meshes, stage.json, cameras, lighting, scripts, fixtures, actors). Textures
// go to `textures/<name>.tex` (TextureCodec) instead of PNG. pipeline/package/cachecheck.py compares the output with the
// Python compiler.
internal sealed class StageCompiler
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

    private static void Transform(JsonNode? value, string what)
    {
        Fields(value, what, new[] { "position", "rotation" }, "position", "rotation");
        Vector(value!["position"], 3, what);
        Rotation(value["rotation"], what);
    }

    private static (string Role, string Bone) Anchor(string name, JsonObject band)
    {
        if (name == "world") return ("world", "00000000");
        var dot = name.IndexOf('.');
        var role = dot < 0 ? name : name[..dot];
        var part = dot < 0 ? "" : name[(dot + 1)..];
        if (!band.ContainsKey(role)) throw new PackageException($"camera refers to absent performer: {name}");
        switch (part)
        {
            case "root": return (role, "00000000");
            case "head": return (role, Key("bone_head"));
            case "hand_left": return (role, Key("Bone_Palm_L"));
            case "hand_right": return (role, Key("Bone_Palm_R"));
        }
        if (part.StartsWith("bone."))
        {
            var bone = part[5..];
            return (role, Regex.IsMatch(bone, @"\A[0-9a-f]{8}\z") ? bone : Key(bone));
        }
        throw new PackageException($"invalid actor anchor: {name}");
    }

    private static string CameraArray(string group) => group == "default" ? "cameras" : group == "guitarist" ? "cameras_guitar_closeup" : "cameras_" + group.Replace("-", "_");

    private static JsonObject CompileCameras(JsonNode data, JsonObject band, JsonObject waypoints)
    {
        Fields(data, "cameras.json", new[] { "version", "shots", "groups", "notes" }, "version", "shots", "groups");
        if (!IsInteger(data["version"]) || Number(data["version"], "version") != 1 || data["groups"]?["wide"] is not JsonArray { Count: > 0 })
            throw new PackageException("a stage needs a wide camera group");
        var shots = new Dictionary<string, JsonObject>();
        string[] shotFields = { "type", "control", "points", "orbitAngle", "stepTime", "stepCount", "stepDirection", "handCam", "disableOrbit1", "disableOrbit2", "waypoints", "tag", "angularSpeed", "heightEase", "times", "interpolation" };
        foreach (var (name, shot) in (JsonObject)data["shots"]!)
        {
            Fields(shot, "camera shot " + name, shotFields, "type", "control", "points");
            var control = Text(shot!["control"]) ?? "";
            var points = shot["points"] as JsonArray ?? throw new PackageException($"invalid camera points: {name}");
            var keyframes = control == "Keyframes";
            if (!Controls.Contains(control) || points.Count < 1 || points.Count > (keyframes ? 64 : 4)) throw new PackageException($"invalid camera control: {name}");
            int? count = control switch { "ThreePos" => 3, "FourPos" => 4, _ => null };
            if (count != null && points.Count != count || count == null && !keyframes && points.Count > 2) throw new PackageException($"invalid camera point count: {name}");
            if (keyframes)
            {
                var times = (shot["times"] as JsonArray ?? new JsonArray()).Select(t => Number(t, 0, 86400, "keyframe time")).ToArray();
                if (times.Length != points.Count || times.Length < 2 || times[0] != 0) throw new PackageException("keyframe times must start at zero and match the camera points");
                for (var i = 1; i < times.Length; i++) if (times[i] <= times[i - 1]) throw new PackageException("keyframe times must increase");
            }
            if (Text(shot["interpolation"]) is { } interpolation && interpolation is not ("linear" or "smooth")) throw new PackageException("invalid camera interpolation");
            foreach (var flag in new[] { "handCam", "disableOrbit1", "disableOrbit2" })
                if (shot[flag] != null && Bool(shot[flag]) == null) throw new PackageException($"invalid camera flag: {flag}");
            if (shot["orbitAngle"] != null) Number(shot["orbitAngle"], -36000, 36000, "orbitAngle");
            if (shot["stepTime"] != null) Number(shot["stepTime"], 0.001, 86400, "stepTime");
            if (shot["stepCount"] != null) Number(shot["stepCount"], 0, 10000, "stepCount");
            if (shot["stepDirection"] != null) Vector(shot["stepDirection"], 3, "stepDirection");
            if (shot["angularSpeed"] != null) Number(shot["angularSpeed"], -3600, 3600, "angularSpeed");
            if (shot["heightEase"] != null) Number(Vector(shot["heightEase"], 3, "heightEase")[2], 0, 100, "heightEase");
            if (control == "OrbitContinuous" && (points.Count != 1 || points[0]?["lookAt"] == null)) throw new PackageException("continuous orbit needs one point and a target");
            var nodes = shot["waypoints"] as JsonArray ?? new JsonArray();
            if (nodes.Any(n => !waypoints.ContainsKey(Text(n) ?? ""))) throw new PackageException($"unknown camera waypoint: {name}");
            var row = new JsonObject
            {
                ["name"] = Clone(shot["tag"]) ?? name, ["type"] = Clone(shot["type"]), ["control"] = control, ["points"] = new JsonArray(), ["nodes"] = Clone(nodes),
            };
            foreach (var property in new[] { "orbitAngle", "stepTime", "stepCount", "stepDirection", "handCam", "disableOrbit1", "disableOrbit2", "angularSpeed", "heightEase", "times", "interpolation" })
                if (shot[property] != null) row[property] = Clone(shot[property]);
            foreach (var point in points)
            {
                Fields(point, "camera point", new[] { "position", "rotation", "horizontalFov", "anchor", "lookAt", "screenOffset" }, "position", "horizontalFov", "anchor");
                var (role, bone) = Anchor(Text(point!["anchor"]) ?? "", band);
                var (look, lookBone) = point["lookAt"] != null ? Anchor(Text(point["lookAt"]) ?? "", band) : ("", "00000000");
                if (look.Length == 0 && point["rotation"] == null) throw new PackageException("camera point needs rotation or lookAt");
                var rotation = point["rotation"] != null ? Clone(point["rotation"])! : new JsonArray(0, 0, 0, 1);
                Rotation(rotation, "camera rotation");
                var fov = Number(point["horizontalFov"], 0, 170, "horizontalFov");
                if (fov is 0 or 170) throw new PackageException("camera FOV must be greater than zero and less than 170");
                Vector(point["position"], 3, "camera position");
                if (point["screenOffset"] != null) Vector(point["screenOffset"], 2, "screenOffset");
                ((JsonArray)row["points"]!).Add(new JsonObject
                {
                    ["position"] = Clone(point["position"]), ["rotation"] = rotation, ["fieldOfView"] = Clone(point["horizontalFov"]),
                    ["lockTo"] = role, ["lockBone"] = bone, ["lookAt"] = look, ["lookBone"] = lookBone,
                    ["screenOffset"] = Clone(point["screenOffset"]) ?? new JsonArray(0, 0),
                });
            }
            shots[name] = row;
        }
        var arrays = new JsonObject();
        foreach (var (group, names) in (JsonObject)data["groups"]!)
        {
            if (names is not JsonArray list || list.Count == 0 || list.Any(n => !shots.ContainsKey(Text(n) ?? ""))) throw new PackageException($"invalid camera group: {group}");
            arrays[CameraArray(group)] = new JsonArray(list.Select(n => Clone(shots[Text(n)!])).ToArray());
        }
        if (!((JsonArray)arrays["cameras_wide"]!).Any(shot => ((JsonArray)shot!["nodes"]!).Count == 0)) throw new PackageException("wide group needs an unrestricted shot");
        if (!arrays.ContainsKey("cameras")) arrays["cameras"] = Clone(arrays["cameras_wide"]);
        foreach (var group in new[] { "guitarist", "bassist", "singer", "drummer", "orbit" })
            if (!arrays.ContainsKey(CameraArray(group))) arrays[CameraArray(group)] = Clone(arrays["cameras_wide"]);
        var notes = new JsonObject();
        if (data["notes"] is JsonObject selectors)
            foreach (var (pitch, selector) in selectors)
            {
                Fields(selector, "camera note", new[] { "group", "tag", "type", "enable" });
                if (!pitch.All(char.IsDigit) || pitch.Length == 0 || int.Parse(pitch) > 127) throw new PackageException("invalid camera note");
                var group = Text(selector!["group"]);
                if (!string.IsNullOrEmpty(group) && !arrays.ContainsKey(CameraArray(group))) throw new PackageException($"missing note-selected camera group: {group}");
                notes[pitch] = new JsonObject
                {
                    ["array"] = string.IsNullOrEmpty(group) ? "" : CameraArray(group), ["name"] = Clone(selector["tag"]) ?? "",
                    ["type"] = Clone(selector["type"]) ?? "", ["enable"] = Clone(selector["enable"]) ?? true,
                };
            }
        return new JsonObject { ["version"] = 1, ["arrays"] = arrays, ["notes"] = notes };
    }

    private static JsonNode BasicLighting()
    {
        JsonObject Setting() => new() { ["color"] = new JsonArray(1, 1, 1), ["intensity"] = 0.65, ["near"] = 0, ["far"] = 200 };
        return new JsonObject
        {
            ["version"] = 1,
            ["lights"] = new JsonArray(new[] { "Stage", "Band" }.Select(g => (JsonNode?)new JsonObject
            {
                ["id"] = g, ["group"] = g, ["type"] = "Ambient", ["position"] = new JsonArray(0, 0, 0), ["rotation"] = new JsonArray(0, 0, 0, 1),
            }).ToArray()),
            ["snapshots"] = new JsonObject { ["fill"] = new JsonObject { ["lights"] = new JsonArray(Setting(), Setting()), ["beams"] = new JsonObject() } },
            ["moods"] = new JsonObject { ["intro"] = new JsonArray("fill"), ["blackout"] = new JsonArray("fill") },
            ["colors"] = new JsonObject(),
        };
    }

    private static JsonObject CompileLighting(JsonNode source)
    {
        var data = (JsonObject)Clone(source)!;
        Fields(data, "lighting.json", new[] { "version", "lights", "snapshots", "moods", "colors" }, "version", "lights", "snapshots", "moods", "colors");
        if (!IsInteger(data["version"]) || Number(data["version"], "version") != 1 || data["snapshots"] is not JsonObject { Count: > 0 } snapshots ||
            data["lights"] is not JsonArray { Count: > 0 } lights)
            throw new PackageException("invalid lighting bank");
        foreach (var light in lights)
        {
            Fields(light, "light", new[] { "id", "object", "group", "type", "position", "rotation", "overrideColor", "hotspot", "field" }, "id", "group", "type", "position", "rotation");
            if (light!["object"] != null && Text(light["object"]) is not { Length: >= 1 and <= 256 }) throw new PackageException("invalid light object identity");
            if (Text(light["type"]) is not ("Ambient" or "Dir" or "Point" or "Spot")) throw new PackageException("unsupported light type");
            Vector(light["position"], 3, "light position");
            Rotation(light["rotation"], "light rotation");
            if (light["overrideColor"] != null && Bool(light["overrideColor"]) == null) throw new PackageException("invalid light color override flag");
            if (light["hotspot"] != null) Number(light["hotspot"], 0, 180, "hotspot");
            if (light["field"] != null) Number(light["field"], 0, 180, "field");
            light["group"] = Key(Text(light["group"]) ?? throw new PackageException("invalid light group"));
            light["id"] = Key(Text(light["id"]) ?? throw new PackageException("invalid light id"));
        }
        if (lights.Select(l => Text(l!["id"])).Distinct().Count() != lights.Count || lights.Count > 128) throw new PackageException("duplicate or excess lights");
        HashSet<string>? beamIds = null;
        foreach (var (_, snapshotNode) in snapshots)
        {
            Fields(snapshotNode, "snapshot", new[] { "lights", "beams" }, "lights", "beams");
            var snapshot = (JsonObject)snapshotNode!;
            if (snapshot["lights"] is not JsonArray settings || settings.Count != lights.Count) throw new PackageException("snapshot light count differs");
            var beams = snapshot["beams"] as JsonObject ?? throw new PackageException("invalid snapshot beams");
            var ids = beams.Select(pair => pair.Key).ToHashSet();
            if (beamIds != null && !beamIds.SetEquals(ids)) throw new PackageException("snapshot beam IDs differ");
            beamIds = ids;
            foreach (var setting in settings)
            {
                Fields(setting, "light setting", new[] { "color", "intensity", "near", "far", "specular", "inherit" }, "color", "intensity", "near", "far");
                Vector(setting!["color"], 3, "light color");
                Number(setting["intensity"], -100, 100, "intensity");
                var near = Number(setting["near"], 0, 100000, "near");
                Number(setting["far"], near, 100001, "far");
                if (setting["inherit"] != null && Bool(setting["inherit"]) == null) throw new PackageException("invalid light inheritance flag");
            }
            var renamed = new JsonObject();
            foreach (var (id, beam) in beams.ToList())
            {
                Fields(beam, "beam", new[] { "position", "rotation", "color", "density", "startRadius", "endRadius", "range", "inherit" }, "position", "rotation", "color", "density", "startRadius", "endRadius", "range");
                Vector(beam!["position"], 3, "beam position");
                Rotation(beam["rotation"], "beam rotation");
                Vector(beam["color"], 3, "beam color");
                foreach (var property in new[] { "density", "startRadius", "endRadius", "range" }) Number(beam[property], 0, 100000, property);
                if (beam["inherit"] != null && Bool(beam["inherit"]) == null) throw new PackageException("invalid beam inheritance flag");
                beams.Remove(id);
                renamed[Key(id)] = beam;
            }
            snapshot["beams"] = renamed;
        }
        var moods = data["moods"] as JsonObject ?? throw new PackageException("invalid lighting moods");
        foreach (var mood in new[] { "intro", "blackout" })
            if (moods[mood] is not JsonArray { Count: > 0 }) throw new PackageException($"missing lighting mood: {mood}");
        foreach (var mood in new[] { "verse", "chorus", "solo", "flare", "strobe" })
            if (!moods.ContainsKey(mood)) moods[mood] = Clone(moods["intro"]);
        if (moods.Any(pair => ((JsonArray)pair.Value!).Any(n => !snapshots.ContainsKey(Text(n) ?? "")))) throw new PackageException("unknown lighting snapshot");
        foreach (var (_, color) in (JsonObject)data["colors"]!) Vector(color, 3, "lighting color");
        return data;
    }

    private static JsonObject MaterialObject(Dictionary<string, JsonObject> materials) =>
        new(materials.Select(pair => KeyValuePair.Create(pair.Key, Clone(pair.Value))));

    private static void WriteModelDefinition(string folder, IReadOnlyList<MeshPart> meshes, Dictionary<string, JsonObject> materials, bool skinned)
    {
        StageGeometry.WriteMeshes(Path.Combine(folder, "meshes.bin"), meshes, skinned);
        Write(Path.Combine(folder, "stage.json"), new JsonObject
        {
            ["version"] = 1, ["name"] = Path.GetFileName(folder), ["cameraPosition"] = new JsonArray(0, 3, -10), ["cameraTarget"] = new JsonArray(0, 1, 0),
            ["fieldOfView"] = 55, ["materials"] = MaterialObject(materials), ["lightGroups"] = new JsonArray(),
        });
    }

    // pipeline/stagepack/pack.py texture_reference: '#<image name>', a package PNG path, or (older packs) a texture index.
    private string TextureReference(GlbDocument glb, JsonNode? reference, string textureFolder, HashSet<string> images)
    {
        if (reference == null || Text(reference) == "" || IsInteger(reference) && Number(reference, "texture") == -1) return "";
        int index;
        if (IsInteger(reference))
        {
            index = (int)Number(reference, "texture");
            if (index < 0 || index >= glb.List("textures").Count) throw new PackageException("invalid material texture index");
        }
        else if (Text(reference) is { } text && text.StartsWith('#'))
        {
            if (glb.NamedTexture(text[1..]) is { } named) index = named;
            else
            {
                // A named image without a glTF texture (Blender keeps sampler-only images so): name it by its image index.
                var image = glb.NamedImage(text[1..]) ?? throw new PackageException($"no GLB image named {text[1..]}");
                var imageName = Key("image-" + image);
                if (images.Add(imageName)) _textures.Add(Path.Combine(textureFolder, imageName), () => glb.Image(image));
                return imageName;
            }
        }
        else if (Text(reference) is { } path && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            var name = Key("file-" + path);
            if (images.Add(name))
            {
                _sources.Add(path);
                var png = _package.Read(path);
                _textures.Add(Path.Combine(textureFolder, name), () => png);
            }
            return name;
        }
        else throw new PackageException("invalid material texture reference");
        return GlbTexture(glb, index, textureFolder, images);
    }

    private string GlbTexture(GlbDocument glb, int index, string textureFolder, HashSet<string> images)
    {
        var name = Key("texture-" + index);
        if (images.Add(name)) _textures.Add(Path.Combine(textureFolder, name), () => glb.TextureImage(index));
        return name;
    }

    private JsonObject Gh3ShaderMaterial(GlbDocument glb, JsonNode data, string textureFolder, HashSet<string> images)
    {
        string[] all = { "template", "vertexConstants", "pixelConstants", "textures", "addressing", "flags", "blend", "alphaRef", "drawOrder" };
        Fields(data, "gh3Shader", all, all);
        if (!Regex.IsMatch(Text(data["template"]) ?? "", @"\A[0-9a-f]{8}\z")) throw new PackageException("invalid GH3 template");
        var result = new JsonObject { ["template"] = Text(data["template"]) };
        foreach (var bank in new[] { "vertexConstants", "pixelConstants" })
        {
            if (data[bank] is not JsonArray rows || rows.Count > 64) throw new PackageException("invalid GH3 constant bank");
            result[bank] = new JsonArray(rows.Select(row => (JsonNode?)Array(Vector(row, 4, "GH3 constant").Select(v => Number(JsonValue.Create(v), -1e9, 1e9, "GH3 constant")))).ToArray());
        }
        if (data["textures"] is not JsonArray textures || textures.Count > 16 || data["addressing"] is not JsonArray addressing || addressing.Count != textures.Count)
            throw new PackageException("invalid GH3 sampler list");
        result["textures"] = new JsonArray(textures.Select(reference => (JsonNode?)TextureReference(glb, reference, textureFolder, images)).ToArray());
        foreach (var value in addressing) Integer(value, 0, 255, "GH3 sampler addressing");
        result["addressing"] = Clone(addressing);
        result["flags"] = Integer(data["flags"], 0, 0xFFFFFFFF, "GH3 flags");
        result["blend"] = Integer(data["blend"], 0, 12, "GH3 blend");
        result["alphaRef"] = Integer(data["alphaRef"], 0, 255, "GH3 alphaRef");
        result["drawOrder"] = Number(data["drawOrder"], -1e6, 1e6, "GH3 drawOrder");
        return result;
    }

    // pipeline/stagepack/pack.py import_materials. Returns the materials by runtime ID, the glTF material index to ID map
    // (the last index is the default material), and the texture names.
    private (Dictionary<string, JsonObject>, Dictionary<int, string>, HashSet<string>) ImportMaterials(GlbDocument glb, string folder)
    {
        var textureFolder = Path.Combine(folder, "textures");
        Directory.CreateDirectory(textureFolder);
        var materials = new Dictionary<string, JsonObject>();
        var images = new HashSet<string>();
        var ids = new Dictionary<int, string>();
        var list = glb.List("materials").Select(m => m as JsonObject ?? new JsonObject()).Append(new JsonObject()).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var material = list[i];
            var name = Key("material-" + i);
            var pbr = material["pbrMetallicRoughness"] as JsonObject ?? new JsonObject();
            var extras = material["extras"] as JsonObject ?? new JsonObject();
            var texture = "";
            if (pbr["baseColorTexture"] is JsonObject info)
            {
                if (GlbDocument.Int(info, "texCoord", 0) != 0) throw new PackageException("stage materials use TEXCOORD_0");
                texture = GlbTexture(glb, info["index"]!.GetValue<int>(), textureFolder, images);
            }
            var mode = Text(material["alphaMode"]) ?? "OPAQUE";
            if (mode is not ("OPAQUE" or "MASK" or "BLEND")) throw new PackageException("unsupported material alpha mode");
            var additive = Bool(extras["gh3Additive"]) == true;
            var row = new JsonObject
            {
                ["texture"] = texture,
                ["blend"] = additive ? 1 : mode == "BLEND" ? 2 : 0,
                ["cutoff"] = mode == "MASK" ? (material["alphaCutoff"] == null ? 0.5 : Number(material["alphaCutoff"], "alphaCutoff")) :
                    mode == "BLEND" ? (extras["gh3AlphaCutoff"] == null ? 0 : Number(extras["gh3AlphaCutoff"], 0, 1, "gh3AlphaCutoff")) : 0,
                ["crowd"] = Bool(extras["gh3CrowdAtlas"]) == true,
                ["sortedTransparency"] = Bool(extras["gh3SortedTransparency"]) == true,
            };
            var shading = Text(extras["gh3Shading"]) ?? "";
            if (shading.Length > 0 && !Shading.Contains(shading)) throw new PackageException("unknown material shading mode");
            row["shading"] = shading;
            var lightmap = "";
            if (material["occlusionTexture"] is JsonObject occlusion)
            {
                if (GlbDocument.Int(occlusion, "texCoord", 0) != 1) throw new PackageException("stage lightmaps use TEXCOORD_1");
                lightmap = GlbTexture(glb, occlusion["index"]!.GetValue<int>(), textureFolder, images);
            }
            row["lightmap"] = lightmap;
            row["doubleSided"] = Bool(material["doubleSided"]) == true;
            row["colorScale"] = extras["gh3ColorScale"] == null ? 1.0 : Number(extras["gh3ColorScale"], 1, 1000, "gh3ColorScale");
            var source = extras["gh3SourceMaterial"] != null ? Text(extras["gh3SourceMaterial"]) : Text(material["name"]) ?? "material-" + i;
            if (source is not { Length: >= 1 and <= 256 }) throw new PackageException("invalid material identity");
            row["source"] = source;
            row["movieSlot"] = extras["gh3MovieSlot"] == null ? -1 : Integer(extras["gh3MovieSlot"], -1, 31, "gh3MovieSlot");
            if (extras["gh3TextureAnimation"] is { } animation)
            {
                Fields(animation, "gh3TextureAnimation", new[] { "columns", "rows", "fps", "offset" }, "columns", "rows", "fps", "offset");
                Integer(animation["columns"], 1, 256, "texture atlas columns");
                Integer(animation["rows"], 1, 256, "texture atlas rows");
                Number(animation["fps"], -100000, 100000, "texture animation rate");
                Number(animation["offset"], -100000, 100000, "texture animation offset");
                row["animation"] = Clone(animation);
            }
            foreach (var (field, extra) in new[] { ("mask", "gh3MaskTexture"), ("layer", "gh3LayerTexture") })
            {
                if (!extras.ContainsKey(extra)) continue;
                var image = TextureReference(glb, extras[extra], textureFolder, images);
                if (image.Length == 0) throw new PackageException("invalid material layer texture");
                row[field] = image;
            }
            if (row.ContainsKey("layer"))
                row["layerColor"] = Array(Vector(extras["gh3LayerColor"], 4, "gh3LayerColor").Select(v => Number(JsonValue.Create(v), 0, 1000, "gh3LayerColor")));
            if (extras["gh3UvWibble"] != null)
                row["uvWibble"] = Array(Vector(extras["gh3UvWibble"], 11, "gh3UvWibble").Select(v => Number(JsonValue.Create(v), -1000, 1000, "gh3UvWibble")));
            if (extras["gh3Shader"] is { } shader)
            {
                row["gh3"] = Gh3ShaderMaterial(glb, shader, textureFolder, images);
                // Vertex colours of template materials exclude the base colour; the fallback surface model applies it instead.
                row["color"] = Array(Vector(pbr["baseColorFactor"] ?? new JsonArray(1, 1, 1, 1), 4, "baseColorFactor").Select(v => Number(JsonValue.Create(v), 0, 1, "baseColorFactor")));
            }
            if (Bool(row["crowd"]) == true)
            {
                row["blend"] = 3;
                row["cutoff"] = 0.3;
            }
            if (Bool(extras["gh3Billboard"]) == true) row["billboard"] = true;
            var role = Text(extras["gh3CrowdRole"]) ?? "";
            if (role.Length > 0 && !Regex.IsMatch(role, @"\Acrowd[1-8]\z")) throw new PackageException("invalid crowd card role");
            if (role.Length > 0) row["crowdRole"] = role;
            materials[name] = row;
            ids[i] = name;
        }
        return (materials, ids, images);
    }

    private void CopyScripts(string path, string folder)
    {
        var scripts = ReadJson(path) as JsonObject ?? throw new PackageException("scripts.json must be an object");
        Fields(scripts, "scripts.json", new[] { "version", "definitions", "startup", "nodes", "textures", "performanceChanged", "prefixes", "lightVolumes", "movies" }, "version", "definitions");
        if (Number(scripts["version"], "version") != 1 || scripts["definitions"] is not JsonObject definitions || definitions.Count > 50000)
            throw new PackageException("invalid stage script definitions");
        foreach (var (name, value) in definitions)
            if (!Regex.IsMatch(name, @"\A[0-9a-f]{8}\z") || value is not JsonArray { Count: 2 }) throw new PackageException("invalid typed script value");
        if (scripts["startup"] is JsonArray startup && (startup.Count > 256 || startup.Any(n => Text(n) is not { Length: <= 256 }))) throw new PackageException("invalid stage startup scripts");
        Write(Path.Combine(folder, "scripts.json"), scripts);
        if (scripts["textures"] is JsonObject textures)
        {
            if (textures.Count > 2048) throw new PackageException("invalid script texture library");
            foreach (var (name, file) in textures)
            {
                if (name.Length is 0 or > 256 || Text(file) is not { } package || !_package.Contains(package)) throw new PackageException("invalid script texture");
                var target = Path.Combine(folder, package.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) throw new PackageException("script texture replaces another pack file");
                _sources.Add(package);
                _package.CopyTo(package, target);
                Png.DecodeRgba(File.ReadAllBytes(target), out _, out _);
            }
        }
        if (scripts["movies"] is JsonObject movies)
        {
            if (movies.Count > 256) throw new PackageException("invalid stage movie library");
            foreach (var (name, file) in movies)
            {
                if (name.Length is 0 or > 256 || Text(file) is not { } package || !_package.Contains(package)) throw new PackageException("invalid stage movie");
                var target = Path.Combine(folder, package.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) throw new PackageException("stage movie replaces another pack file");
                _sources.Add(package);
                _package.CopyTo(package, target);
                using var stream = File.OpenRead(target);
                var magic = new byte[8];
                if (stream.Read(magic, 0, 8) != 8 || System.Text.Encoding.ASCII.GetString(magic) != "GH3VID01") throw new PackageException("invalid movie frame bank");
            }
        }
    }

    private JsonObject CompileActorEntry(string name, JsonNode actor, string folder)
    {
        Fields(actor, "actor " + name, new[] { "model", "transform", "clip", "lightGroup", "playback", "role", "initiallyVisible", "actions", "object", "script", "created", "scale", "animationEvents" }, "model", "transform");
        Transform(actor["transform"], "actor transform");
        var id = Key(name);
        var model = Text(actor["model"]) ?? throw new PackageException("invalid actor model");
        _sources.Add(model);
        var clips = CompileActor(model, Path.Combine(folder, "actors", id));
        var clip = Text(actor["clip"]) ?? clips[0];
        if (!clips.Contains(clip)) throw new PackageException($"actor {name} has no clip {clip}");
        var playback = Text(actor["playback"]) ?? "loop";
        if (playback is not ("loop" or "hold")) throw new PackageException("invalid actor playback mode");
        var role = actor["role"] == null ? "" : Text(actor["role"]) ?? throw new PackageException("invalid actor role");
        var visible = actor["initiallyVisible"] == null || (Bool(actor["initiallyVisible"]) ?? throw new PackageException("invalid actor visibility"));
        var actions = actor["actions"] as JsonObject ?? new JsonObject();
        var sourceObject = actor["object"] == null ? "" : Text(actor["object"]) ?? throw new PackageException("invalid actor script binding");
        var script = actor["script"] == null ? "" : Text(actor["script"]) ?? throw new PackageException("invalid actor script binding");
        var created = actor["created"] == null || (Bool(actor["created"]) ?? throw new PackageException("invalid actor script binding"));
        if (sourceObject.Length > 256 || script.Length > 256) throw new PackageException("invalid actor script binding");
        if (script.Length > 0 && sourceObject.Length == 0) throw new PackageException("a scripted actor needs an object name");
        var events = actor["animationEvents"] == null ? "" : Text(actor["animationEvents"]) ?? throw new PackageException("invalid animation event table");
        var scale = actor["scale"] == null ? new JsonArray(1, 1, 1) : Clone(actor["scale"])!;
        if (Vector(scale, 3, "actor scale").Any(v => v <= 0)) throw new PackageException("actor scale must be positive");
        if (role.Length > 128 || actions.Count > 256) throw new PackageException("invalid actor role or actions");
        var actionRows = new JsonObject();
        foreach (var (action, choices) in actions)
        {
            if (action.Length == 0 || choices is not JsonArray list || list.Count is < 1 or > 256 || list.Any(c => !clips.Contains(Text(c) ?? "\0")))
                throw new PackageException($"invalid actor action: {action}");
            actionRows[action] = new JsonArray(list.Select(c => (JsonNode?)clips.IndexOf(Text(c)!)).ToArray());
        }
        return new JsonObject
        {
            ["id"] = id, ["clip"] = clips.IndexOf(clip), ["clips"] = new JsonObject(clips.Select((c, i) => KeyValuePair.Create(c, (JsonNode?)i))),
            ["actions"] = actionRows, ["role"] = role, ["initiallyVisible"] = visible, ["playback"] = playback, ["transform"] = Clone(actor["transform"]),
            ["object"] = sourceObject, ["script"] = script, ["created"] = created, ["scale"] = scale, ["animationEvents"] = events,
            ["lightGroup"] = Key(Text(actor["lightGroup"]) ?? "Stage"),
        };
    }

    // pipeline/stagepack/actor.py compile_actor: skinned GH3BND02 meshes, bones.json, and GH3ANM01 clips sampled at 60 fps.
    private List<string> CompileActor(string path, string folder)
    {
        var glb = new GlbDocument(_package.Read(path));
        var nodes = glb.Nodes().ToList();
        var skins = glb.List("skins");
        if (skins.Count != 1) throw new PackageException("an actor GLB needs one skin");
        var skin = (JsonObject)skins[0]!;
        var joints = ((JsonArray)skin["joints"]!).Select(j => j!.GetValue<int>()).ToArray();
        if (joints.Length is < 1 or > 255 || joints.Distinct().Count() != joints.Length) throw new PackageException("invalid actor joints");
        var world = nodes.ToDictionary(n => n.Index, n => n.World);
        if (joints.Any(j => !world.ContainsKey(j))) throw new PackageException("actor joint is outside the scene");
        var parentNodes = new Dictionary<int, int>();
        foreach (var (index, node, _) in nodes)
            if (node["children"] is JsonArray children)
                foreach (var child in children) parentNodes[child!.GetValue<int>()] = index;
        var parents = joints.Select(j => parentNodes.TryGetValue(j, out var p) ? System.Array.IndexOf(joints, p) : -1).ToArray();
        var bind = new double[joints.Length][];
        for (var i = 0; i < joints.Length; i++)
        {
            var local = parents[i] >= 0 ? world[joints[parents[i]]].Inverse() * world[joints[i]] : world[joints[i]];
            var m = local.Upper3();
            for (var a = 0; a < 3; a++)
                for (var b = 0; b < 3; b++)
                {
                    var dot = m[a] * m[b] + m[3 + a] * m[3 + b] + m[6 + a] * m[6 + b];
                    if (Math.Abs(dot - (a == b ? 1 : 0)) > 1e-4 + 1e-5 * (a == b ? 1 : 0)) throw new PackageException("apply actor joint scale before export");
                }
            bind[i] = local.Translation.Concat(Matrix4d.Quaternion(m)).ToArray();
        }
        var inverseValues = glb.Accessor(skin["inverseBindMatrices"]!.GetValue<int>());
        if (inverseValues.Count != joints.Length || inverseValues.Columns != 16) throw new PackageException("actor inverse bind count differs");
        var inverse = Enumerable.Range(0, joints.Length).Select(i =>
        {
            var m = new double[16];
            for (var k = 0; k < 16; k++) m[(k % 4) * 4 + k / 4] = inverseValues.Values[i * 16 + k];
            return new Matrix4d(m);
        }).ToArray();
        var (materials, materialIds, _) = ImportMaterials(glb, folder);
        var meshes = new List<MeshPart>();
        foreach (var (_, node, matrix) in nodes)
        {
            if (node["mesh"] == null) continue;
            if (node["skin"] is not JsonValue skinIndex || skinIndex.GetValue<int>() != 0) throw new PackageException("all actor meshes must use its skin");
            for (var i = 0; i < joints.Length; i++)
                if (!(world[joints[i]] * Matrix4d.Reflection * inverse[i] * Matrix4d.Reflection).Close(matrix, 1e-4))
                    throw new PackageException("actor inverse bind matrices differ from its rig");
            foreach (var primitive in (JsonArray)glb.Item("meshes", node["mesh"]!.GetValue<int>())["primitives"]!)
            {
                var data = StageGeometry.Primitive(glb, (JsonObject)primitive!, matrix);
                var attributes = (JsonObject)primitive!["attributes"]!;
                var weights = glb.Accessor(attributes["WEIGHTS_0"]!.GetValue<int>());
                var indices = glb.Accessor(attributes["JOINTS_0"]!.GetValue<int>());
                if (indices.Count != data.Count || indices.Columns != 4 || weights.Count != data.Count || weights.Columns != 4 ||
                    indices.Values.Any(v => v >= joints.Length) || weights.Values.Any(v => v < 0) ||
                    Enumerable.Range(0, data.Count).Any(v => Math.Abs(weights[v, 0] + weights[v, 1] + weights[v, 2] + weights[v, 3] - 1) > 1e-4 + 1e-5))
                    throw new PackageException("invalid actor skin weights");
                double[]? tangents = null;
                if (attributes["_GH3_TANGENT"] is JsonValue tangentIndex)
                {
                    var source = glb.Accessor(tangentIndex.GetValue<int>());
                    if (source.Count != data.Count || source.Columns != 3) throw new PackageException("invalid GH3 tangent stream");
                    var linear = (matrix * Matrix4d.Reflection).Upper3();
                    tangents = new double[data.Count * 3];
                    for (var v = 0; v < data.Count; v++)
                        for (var r = 0; r < 3; r++)
                            tangents[v * 3 + r] = source[v, 0] * linear[r * 3] + source[v, 1] * linear[r * 3 + 1] + source[v, 2] * linear[r * 3 + 2];
                }
                var material = materialIds[primitive["material"] is JsonValue mi ? mi.GetValue<int>() : materialIds.Count - 1];
                var packed = StageGeometry.PackSkinned(data, indices.Values.Select(v => (int)v).ToArray(), weights.Values, tangents);
                if (data.Faces.Length > 0) meshes.AddRange(StageGeometry.Split(material, packed, StageGeometry.SkinnedStride, data.Faces));
            }
        }
        if (meshes.Count == 0) throw new PackageException("actor has no visible geometry");
        foreach (var mesh in meshes)
        {
            var material = materials[mesh.Material];
            if (Text(material["texture"]) is { Length: > 0 } && Number(material["cutoff"], "cutoff") > 0 && Bool(material["crowd"]) != true && material["gh3"] == null)
                Warnings.Add($"actor material {Text(material["source"])}: alpha-mask mesh trimming is not done by the C# importer");
        }
        _textures.Flush();
        WriteModelDefinition(folder, meshes, materials, skinned: true);
        var bones = new JsonObject();
        for (var index = 0; index < joints.Length; index++)
        {
            var name = Text(glb.Item("nodes", joints[index])["name"]) ?? $"joint-{index}";
            var checksum = Regex.IsMatch(name, @"\Abone-[0-9a-f]{8}\z") ? name[5..] : Key(name);
            if (bones.ContainsKey(checksum)) throw new PackageException("actor bone names have the same checksum");
            bones[checksum] = index;
        }
        Write(Path.Combine(folder, "bones.json"), bones);
        var animations = glb.List("animations");
        if (animations.Count > 256) throw new PackageException("actor has too many animation clips");
        var names = animations.Select((a, i) => Text(a?["name"]) ?? $"clip-{i}").ToList();
        if (names.Distinct().Count() != names.Count) throw new PackageException("duplicate actor animation names");
        var sampled = new List<(double Duration, double[,,] Poses)>();
        foreach (var animation in animations)
            sampled.Add(SampleActorClip(glb, (JsonObject)animation!, joints, parents, parentNodes, world, bind));
        if (sampled.Count == 0)
        {
            names = new List<string> { "bind" };
            var poses = new double[2, joints.Length, 7];
            for (var f = 0; f < 2; f++) for (var b = 0; b < joints.Length; b++) for (var c = 0; c < 7; c++) poses[f, b, c] = bind[b][c];
            sampled.Add((1.0, poses));
        }
        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "animation.bin"))))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3ANM01"));
            writer.Write(joints.Length);
            writer.Write(sampled.Count);
            for (var b = 0; b < joints.Length; b++)
            {
                writer.Write(parents[b]);
                foreach (var value in bind[b]) writer.Write((float)value);
            }
            foreach (var (duration, poses) in sampled)
            {
                writer.Write((float)duration);
                writer.Write(poses.GetLength(0));
                foreach (var value in poses) writer.Write((float)value);
            }
        }
        return names;
    }

    private static (double, double[,,]) SampleActorClip(GlbDocument glb, JsonObject track, int[] joints, int[] parents, Dictionary<int, int> parentNodes,
        Dictionary<int, Matrix4d> world, double[][] bind)
    {
        var samplers = (JsonArray)track["samplers"]!;
        var tracks = new Dictionary<(int, string), (double[] Times, GlbAccessor Values, string Mode)>();
        var duration = 0.0;
        foreach (var channel in (JsonArray)track["channels"]!)
        {
            var target = (JsonObject)channel!["target"]!;
            var sampler = (JsonObject)samplers[channel["sampler"]!.GetValue<int>()]!;
            var node = target["node"]!.GetValue<int>();
            var property = Text(target["path"]) ?? "";
            var mode = Text(sampler["interpolation"]) ?? "LINEAR";
            if (property == "scale" && joints.Contains(node) && mode is "LINEAR" or "STEP")
            {
                var error = glb.Accessor(sampler["output"]!.GetValue<int>()).Values.Select(v => Math.Abs(v - 1)).DefaultIfEmpty(0).Max();
                if (error > 1e-4) throw new PackageException($"actor clip '{Text(track["name"])}' scales a joint (|scale - 1| = {error:g3}); animate only joint translation and rotation");
                continue;
            }
            if (!joints.Contains(node) || property is not ("translation" or "rotation") || mode is not ("LINEAR" or "STEP"))
                throw new PackageException("bake actor animation to joint translation/rotation with LINEAR or STEP interpolation");
            if (tracks.ContainsKey((node, property))) throw new PackageException("duplicate actor animation channel");
            var times = glb.Accessor(sampler["input"]!.GetValue<int>()).Values;
            var values = glb.Accessor(sampler["output"]!.GetValue<int>());
            for (var i = 1; i < times.Length; i++) if (times[i] <= times[i - 1]) throw new PackageException("invalid actor key times");
            if (times[0] < 0 || times.Length != values.Count) throw new PackageException("invalid actor key times");
            if (values.Columns != (property == "rotation" ? 4 : 3)) throw new PackageException("invalid actor key values");
            if (property == "rotation")
                for (var i = 0; i < values.Count; i++)
                {
                    var length = Math.Sqrt(values[i, 0] * values[i, 0] + values[i, 1] * values[i, 1] + values[i, 2] * values[i, 2] + values[i, 3] * values[i, 3]);
                    if (Math.Abs(length - 1) > 0.001 + 1e-5) throw new PackageException("invalid actor key rotation");
                }
            tracks[(node, property)] = (times, values, mode);
            duration = Math.Max(duration, times[^1]);
        }
        if (duration is <= 0 or > 120) throw new PackageException("actor clip must be longer than zero and at most 120 seconds");
        duration = Math.Max(0.5, duration);
        var count = Math.Max(2, (int)Math.Round(duration * 60) + 1);
        var step = duration / (count - 1);
        var samples = Enumerable.Range(0, count).Select(i => i * step).ToArray();
        samples[^1] = duration;
        var poses = new double[count, joints.Length, 7];
        for (var f = 0; f < count; f++) for (var b = 0; b < joints.Length; b++) for (var c = 0; c < 7; c++) poses[f, b, c] = bind[b][c];
        for (var bone = 0; bone < joints.Length; bone++)
        {
            var joint = joints[bone];
            Matrix4d? parentTransform = parents[bone] < 0 && parentNodes.TryGetValue(joint, out var parentNode) ? world[parentNode] : null;
            if (parentTransform != null)
            {
                var local = Matrix4d.Reflect(Matrix4d.FromNode(glb.Item("nodes", joint)));
                var q = Matrix4d.Quaternion(local.Upper3());
                for (var f = 0; f < count; f++)
                {
                    for (var c = 0; c < 3; c++) poses[f, bone, c] = local[c, 3];
                    for (var c = 0; c < 4; c++) poses[f, bone, 3 + c] = q[c];
                }
            }
            foreach (var property in new[] { "translation", "rotation" })
            {
                if (!tracks.TryGetValue((joint, property), out var key)) continue;
                var columns = key.Values.Columns;
                var values = (double[])key.Values.Values.Clone();
                if (property == "rotation")
                    for (var i = 1; i < key.Values.Count; i++)
                    {
                        double dot = 0;
                        for (var c = 0; c < 4; c++) dot += values[(i - 1) * 4 + c] * values[i * 4 + c];
                        if (dot < 0) for (var c = 0; c < 4; c++) values[i * 4 + c] *= -1;
                    }
                var result = new double[columns];
                for (var f = 0; f < count; f++)
                {
                    if (key.Mode == "STEP")
                    {
                        var index = Math.Max(0, UpperBound(key.Times, samples[f]) - 1);
                        for (var c = 0; c < columns; c++) result[c] = values[index * columns + c];
                    }
                    else
                        for (var c = 0; c < columns; c++) result[c] = Interpolate(samples[f], key.Times, values, columns, c);
                    if (property == "rotation")
                    {
                        var length = Math.Sqrt(result.Sum(v => v * v));
                        poses[f, bone, 3] = -result[0] / length;
                        poses[f, bone, 4] = -result[1] / length;
                        poses[f, bone, 5] = result[2] / length;
                        poses[f, bone, 6] = result[3] / length;
                    }
                    else
                    {
                        poses[f, bone, 0] = result[0];
                        poses[f, bone, 1] = result[1];
                        poses[f, bone, 2] = -result[2];
                    }
                }
            }
            if (parentTransform is { } parent)
            {
                var m = parent.Upper3();
                var t = parent.Translation;
                var q = Matrix4d.Quaternion(m);
                for (var f = 0; f < count; f++)
                {
                    double px = poses[f, bone, 0], py = poses[f, bone, 1], pz = poses[f, bone, 2];
                    for (var r = 0; r < 3; r++) poses[f, bone, r] = px * m[r * 3] + py * m[r * 3 + 1] + pz * m[r * 3 + 2] + t[r];
                    double vx = poses[f, bone, 3], vy = poses[f, bone, 4], vz = poses[f, bone, 5], w = poses[f, bone, 6];
                    poses[f, bone, 3] = q[3] * vx + w * q[0] + (q[1] * vz - q[2] * vy);
                    poses[f, bone, 4] = q[3] * vy + w * q[1] + (q[2] * vx - q[0] * vz);
                    poses[f, bone, 5] = q[3] * vz + w * q[2] + (q[0] * vy - q[1] * vx);
                    poses[f, bone, 6] = q[3] * w - (q[0] * vx + q[1] * vy + q[2] * vz);
                }
            }
        }
        return (duration, poses);
    }

    private static int UpperBound(double[] values, double x)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (values[mid] <= x) low = mid + 1; else high = mid;
        }
        return low;
    }

    // numpy.interp for one column.
    private static double Interpolate(double x, double[] xp, double[] fp, int columns, int column)
    {
        var n = xp.Length;
        if (x <= xp[0]) return fp[column];
        if (x >= xp[n - 1]) return fp[(n - 1) * columns + column];
        var j = UpperBound(xp, x) - 1;
        if (xp[j] == x) return fp[j * columns + column];
        var slope = (fp[(j + 1) * columns + column] - fp[j * columns + column]) / (xp[j + 1] - xp[j]);
        var value = slope * (x - xp[j]) + fp[j * columns + column];
        return double.IsNaN(value) ? slope * (x - xp[j + 1]) + fp[(j + 1) * columns + column] : value;
    }
}
