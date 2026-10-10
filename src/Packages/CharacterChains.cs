using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// character.json `bones`, `colliders`, and `chains` (PACKAGE-FORMAT.md section 9.5): bones that a character adds to its
// profile rig and the physics of the custom hair, skirt, and tail chains on them. The importer checks them as the Python
// validator does, adds the bones to the rig for the GLB check, and writes the cache rows (bone names as checksums) that
// CharacterModel reads. Bones and chains name bones, not rig indices, so they also work on a skeleton of the character.
internal static class CharacterChains
{
    public const int MaxBones = 64;
    public const int MaxJoints = 64;
    public const int MaxColliders = 32;
    private static readonly string[] JointFields =
    {
        "bone", "parent", "length", "direction", "radius", "mass", "friction", "angularDamping", "maxAngularVelocity", "type", "limits",
        "twistAxis", "coneAxis", "planeAxis", "axis", "range", "stiffness", "drag", "gravity",
    };

    public sealed record Result(string[] Bones, int[] Parents, float[][] Bind, JsonArray AddedBones, JsonArray Colliders, JsonArray Joints);

    // `chainBones`: for a character with its own skeleton (section 9.6), the skeleton joints that can be chain joints;
    // null: the joints must be added bones.
    public static Result Read(JsonObject character, string[] rigBones, int[] rigParents, float[][] rigBind, ISet<string>? chainBones = null)
    {
        var bones = rigBones.ToList();
        var parents = rigParents.ToList();
        var bind = rigBind.ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < bones.Count; i++) index.TryAdd(bones[i], i);
        var added = new JsonArray();
        foreach (var row in List(character["bones"], "bones", MaxBones))
        {
            Fields(row, "bones row", new[] { "name", "parent", "bind" }, "name", "parent", "bind");
            var name = Name(row["name"], "bone name");
            var key = CharacterImporter.JointChecksum(name);
            var parent = CharacterImporter.JointChecksum(Name(row["parent"], "bone parent"));
            if (index.ContainsKey(key)) throw new PackageException($"bone '{name}' has the name of a profile bone or of another bone");
            if (!index.TryGetValue(parent, out var parentIndex)) throw new PackageException($"bone '{name}': the parent is not a bone of the rig");
            var values = Vector(row["bind"], 7, $"bone '{name}' bind");
            Rotation(Array(values.Skip(3)), $"bone '{name}' bind rotation");
            if (Math.Sqrt(values.Take(3).Sum(v => v * v)) > 5) throw new PackageException($"bone '{name}' is more than 5 m from its parent");
            index[key] = bones.Count;
            bones.Add(key); parents.Add(parentIndex); bind.Add(values.Select(v => (float)v).ToArray());
            added.Add(new JsonObject { ["name"] = key, ["parent"] = parent, ["bind"] = Array(values) });
        }
        var addedKeys = chainBones ?? added.Select(b => Text(b!["name"])!).ToHashSet();
        var chainRows = List(character["chains"], "chains", 16);
        var jointBones = new List<string>();
        foreach (var chain in chainRows)
            foreach (var joint in List(chain["joints"], "chain joints", 16))
                jointBones.Add(CharacterImporter.JointChecksum(Name(joint["bone"], "joint bone")));
        if (jointBones.Count > MaxJoints) throw new PackageException($"the chains have {jointBones.Count} joints (limit {MaxJoints})");
        var colliders = new JsonArray();
        var colliderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in List(character["colliders"], "colliders", MaxColliders))
        {
            Fields(row, "collider", new[] { "name", "bone", "radius", "a", "b" }, "name", "bone", "radius", "a");
            var name = Name(row["name"], "collider name");
            var bone = CharacterImporter.JointChecksum(Name(row["bone"], "collider bone"));
            if (!colliderIndex.TryAdd(name, colliderIndex.Count)) throw new PackageException($"two colliders have the name '{name}'");
            if (!index.ContainsKey(bone)) throw new PackageException($"collider '{name}': the bone is not a bone of the rig");
            if (jointBones.Contains(bone)) throw new PackageException($"collider '{name}' is on a chain bone");
            var radius = Number(row["radius"], 1e-6, 5, $"collider '{name}' radius");
            var a = Vector(row["a"], 3, $"collider '{name}' a");
            var b = row["b"] == null ? a : Vector(row["b"], 3, $"collider '{name}' b");
            if (Math.Sqrt(a.Sum(v => v * v)) > 10 || Math.Sqrt(b.Sum(v => v * v)) > 10) throw new PackageException($"collider '{name}' is more than 10 m from its bone");
            colliders.Add(new JsonObject { ["bone"] = bone, ["radius"] = radius, ["a"] = Array(a), ["b"] = Array(b) });
        }
        var joints = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chainNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in chainRows)
        {
            Fields(chain, "chain", new[] { "name", "colliders", "bendLimit", "joints" }, "name", "joints");
            var chainName = Name(chain["name"], "chain name");
            var bendLimit = chain["bendLimit"] == null ? 0 : Number(chain["bendLimit"], 1e-6, Math.PI, $"chain '{chainName}' bendLimit");
            if (!chainNames.Add(chainName)) throw new PackageException($"two chains have the name '{chainName}'");
            long mask = chain["colliders"] == null ? (1L << colliders.Count) - 1 : 0;
            foreach (var name in List(chain["colliders"], "chain colliders", MaxColliders).Select(n => Text(n)))
                mask |= 1L << (name != null && colliderIndex.TryGetValue(name, out var c) ? c : throw new PackageException($"chain '{chainName}': no collider '{name}'"));
            foreach (var joint in List(chain["joints"], "chain joints", 16))
            {
                Fields(joint, "chain joint", JointFields, "bone", "length", "mass", "friction", "angularDamping");
                var key = CharacterImporter.JointChecksum(Name(joint["bone"], "joint bone"));
                var label = $"chain '{chainName}' joint {Text(joint["bone"])}";
                if (!addedKeys.Contains(key))
                    throw new PackageException(chainBones == null ? $"{label}: the joint bone must be a bone of 'bones'" :
                        $"{label}: the joint bone must be a skeleton joint without a humanoid role, not a parent of one, and not a follower");
                if (!seen.Add(key)) throw new PackageException($"{label}: the bone is a joint of an earlier chain or joint");
                var parent = joint["parent"] == null ? bones[parents[index[key]]] : CharacterImporter.JointChecksum(Name(joint["parent"], "joint parent"));
                if (!index.ContainsKey(parent) || parent == key) throw new PackageException($"{label}: the parent is not another bone of the rig");
                if (jointBones.Contains(parent) && !seen.Contains(parent)) throw new PackageException($"{label}: the parent joint must come before it");
                var row = new JsonObject
                {
                    ["bone"] = key, ["parent"] = parent, ["colliderMask"] = mask,
                    ["length"] = Number(joint["length"], 0.005, 1, $"{label} length"),
                    ["mass"] = Number(joint["mass"], 1e-6, 100, $"{label} mass"),
                    ["friction"] = Number(joint["friction"], 0, 100, $"{label} friction"),
                    ["angularDamping"] = Number(joint["angularDamping"], 0, 100, $"{label} angularDamping"),
                    ["maxAngularVelocity"] = joint["maxAngularVelocity"] == null ? 0 : Number(joint["maxAngularVelocity"], 0, 1000, $"{label} maxAngularVelocity"),
                    ["radius"] = joint["radius"] == null ? 0 : Number(joint["radius"], 0, 0.5, $"{label} radius"),
                    ["direction"] = Array(joint["direction"] == null ? new double[] { 0, 1, 0 } : Unit(joint["direction"], $"{label} direction")),
                    ["stiffness"] = joint["stiffness"] == null ? 0 : Number(joint["stiffness"], 0, StageHair.MaxStiffness, $"{label} stiffness"),
                    ["drag"] = joint["drag"] == null ? 0 : Number(joint["drag"], 0, 100, $"{label} drag"),
                    ["gravity"] = joint["gravity"] == null ? 1 : Number(joint["gravity"], 0, 2, $"{label} gravity"),
                    ["bendLimit"] = bendLimit,
                };
                var type = Text(joint["type"]) ?? "ragdoll";
                if (type == "hinge")
                {
                    if (joint["limits"] != null || joint["twistAxis"] != null) throw new PackageException($"{label}: a hinge has 'axis' and 'range', not the ragdoll limits");
                    var range = Vector(joint["range"], 2, $"{label} range");
                    if (range[0] > range[1] || range[0] < -Math.PI || range[1] > Math.PI) throw new PackageException($"{label}: the hinge range must be in -pi to pi, minimum first");
                    row["type"] = "hinge"; row["axis"] = Array(Unit(joint["axis"], $"{label} axis")); row["range"] = Array(range);
                }
                else if (type == "ragdoll")
                {
                    if (joint["axis"] != null || joint["range"] != null) throw new PackageException($"{label}: 'axis' and 'range' are for hinge joints");
                    Fields(joint["limits"], $"{label} limits", new[] { "twistLimit", "coneLimit", "planesLimit" }, "coneLimit");
                    var limits = (JsonObject)joint["limits"]!;
                    var cone = Vector(limits["coneLimit"], 2, $"{label} coneLimit");
                    if (cone[1] is <= 0 or > Math.PI / 2) throw new PackageException($"{label}: the cone limit must be more than 0 and at most pi/2");
                    var copy = new JsonObject { ["coneLimit"] = Array(cone) };
                    if (limits["planesLimit"] != null)
                    {
                        var planes = Vector(limits["planesLimit"], 2, $"{label} planesLimit");
                        if (planes[0] > planes[1] || planes[0] < -Math.PI / 2 || planes[1] > Math.PI / 2) throw new PackageException($"{label}: planesLimit must be in -pi/2 to pi/2, minimum first");
                        copy["planesLimit"] = Array(planes);
                    }
                    row["type"] = "ragdoll"; row["limits"] = copy;
                    var frames = new[] { "twistAxis", "coneAxis", "planeAxis" };
                    var present = frames.Count(f => joint[f] != null);
                    if (present is not (0 or 3) || present == 3 && limits["planesLimit"] == null)
                        throw new PackageException($"{label}: twistAxis, coneAxis, and planeAxis come together, with planesLimit");
                    if (present == 3) foreach (var f in frames) row[f] = Array(Unit(joint[f], $"{label} {f}"));
                }
                else throw new PackageException($"{label}: unknown joint type '{type}'");
                joints.Add(row);
            }
        }
        return new Result(bones.ToArray(), parents.ToArray(), bind.ToArray(), added, colliders, joints);
    }

    private static IEnumerable<JsonNode> List(JsonNode? node, string what, int maximum)
    {
        if (node == null) return System.Array.Empty<JsonNode>();
        if (node is not JsonArray array || array.Count > maximum || array.Any(n => n == null)) throw new PackageException($"{what} must be a list of at most {maximum} items");
        return array!;
    }

    private static string Name(JsonNode? node, string what)
    {
        var text = Text(node) ?? throw new PackageException($"{what} must be a text");
        // Bone names of a character with its own skeleton can be in any script (MMD names are Japanese).
        if (!Regex.IsMatch(text, @"\A[^\u0000-\u001f]{1,64}\z")) throw new PackageException($"invalid {what} '{text}'");
        return text;
    }

    private static double[] Unit(JsonNode? node, string what)
    {
        var v = Vector(node, 3, what);
        if (Math.Abs(Math.Sqrt(v.Sum(x => x * x)) - 1) > 0.01) throw new PackageException($"{what} must have unit length");
        return v;
    }
}
