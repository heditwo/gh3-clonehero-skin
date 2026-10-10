using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// The venue's camera bank and lighting snapshots, checked and converted to the cache format.
internal sealed partial class StageCompiler
{
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
}
