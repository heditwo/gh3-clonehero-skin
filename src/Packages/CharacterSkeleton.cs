using System.Numerics;
using System.Text.Json.Nodes;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// character.json `skeleton` and `face` (PACKAGE-FORMAT.md section 9.6): a character that keeps its own skeleton. The GLB
// skin joints are the skeleton; the humanoid map names the joint of each role, and the mod retargets the clips of the
// profile to it. The importer checks the map, the hierarchy, and the rest pose as the Python validator does
// (pipeline/package/skeleton.py), and writes the cache rows that CharacterModel reads.
internal sealed class CharacterSkeleton
{
    // Blink, the vowel mouth shapes, the consonant visemes (VRChat and Oculus names), and the brow and eye shapes of the
    // lip sync (StageLipSync). The order is the order of the morph slots in the cache (morphs.bin).
    public static readonly string[] FaceSlots = { "blink", "a", "i", "u", "e", "o", "pp", "ff", "th", "dd", "kk", "ch", "ss", "nn", "rr", "browUp", "browDown", "squint" };
    public const int MaxFollowers = 128;
    public string[] Names { get; private init; } = System.Array.Empty<string>();
    public string[] Keys { get; private init; } = System.Array.Empty<string>();
    public int[] Parents { get; private init; } = System.Array.Empty<int>();
    public float[][] Bind { get; private init; } = System.Array.Empty<float[]>();
    public Dictionary<string, int> Humanoid { get; private init; } = new(StringComparer.Ordinal);
    public (int Bone, int Source, double Weight)[] Followers { get; private init; } = System.Array.Empty<(int, int, double)>();
    public int InstrumentBone { get; private init; } = -1;
    public double[] InstrumentOffset { get; private init; } = { 0, 0, 0 };
    public double InstrumentScale { get; private init; }
    // The joints that can be chain joints: not a mapped bone, not a parent of one, not a follower.
    public HashSet<string> ChainBones { get; private init; } = new(StringComparer.Ordinal);

    // Role pairs where the first joint must be a parent (at any depth) of the second.
    private static IEnumerable<(string Above, string Below)> Hierarchy()
    {
        yield return ("hips", "leftUpperLeg");
        yield return ("hips", "rightUpperLeg");
        yield return ("neck", "head");
        foreach (var top in new[] { "spine", "chest", "upperChest" })
            foreach (var below in new[] { "neck", "head", "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm" })
                yield return (top, below);
        yield return ("spine", "chest");
        yield return ("spine", "upperChest");
        yield return ("chest", "upperChest");
        yield return ("head", "jaw");
        yield return ("head", "leftEye");
        yield return ("head", "rightEye");
        foreach (var side in new[] { "left", "right" })
        {
            var chain = new[] { "Shoulder", "UpperArm", "UpperArmTwist", "LowerArm", "LowerArmTwist", "Hand" };
            for (var i = 0; i < chain.Length; i++) for (var j = i + 1; j < chain.Length; j++) yield return (side + chain[i], side + chain[j]);
            yield return (side + "UpperLeg", side + "LowerLeg");
            yield return (side + "LowerLeg", side + "Foot");
            yield return (side + "Foot", side + "Toes");
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                var parts = finger == "Thumb" ? new[] { "Metacarpal", "Proximal", "Distal" } : new[] { "Proximal", "Intermediate", "Distal" };
                yield return (side + "Hand", side + finger + parts[0]);
                yield return (side + finger + parts[0], side + finger + parts[1]);
                yield return (side + finger + parts[1], side + finger + parts[2]);
            }
        }
    }

    public static CharacterSkeleton Read(GlbDocument glb, JsonNode node, List<string> warnings)
    {
        if (node is not JsonObject skeleton) throw new PackageException("character.json skeleton must be an object");
        Fields(skeleton, "skeleton", new[] { "humanoid", "followers", "instrument" }, "humanoid");
        var skins = glb.List("skins");
        if (skins.Count != 1) throw new PackageException("a character GLB needs one skin");
        var joints = ((JsonArray)skins[0]!["joints"]!).Select(j => j!.GetValue<int>()).ToArray();
        if (joints.Length is < 1 or > 255 || joints.Distinct().Count() != joints.Length) throw new PackageException("the skeleton needs 1 to 255 distinct joints");
        var nodes = glb.List("nodes");
        var names = joints.Select(j => Text(nodes[j]?["name"]) is { Length: > 0 } name ? name : throw new PackageException("skeleton joints need names")).ToArray();
        var keys = names.Select(CharacterImporter.JointChecksum).ToArray();
        if (keys.Distinct().Count() != keys.Length) throw new PackageException("two skeleton joints have the same name");
        var parentNodes = glb.Parents();
        var parents = joints.Select(j => parentNodes.TryGetValue(j, out var p) ? System.Array.IndexOf(joints, p) : -1).ToArray();
        var bind = new float[joints.Length][];
        for (var i = 0; i < joints.Length; i++)
        {
            var local = Matrix4d.Reflect(Matrix4d.FromNode(glb.Item("nodes", joints[i])));
            var m = local.Upper3();
            for (var c = 0; c < 3; c++)
                if (Math.Abs(Math.Sqrt(m[c] * m[c] + m[3 + c] * m[3 + c] + m[6 + c] * m[6 + c]) - 1) > CharacterImporter.JointScale)
                    throw new PackageException($"joint {names[i]} has a scale; apply the scale before export");
            var q = Matrix4d.Quaternion(m);
            var t = local.Translation;
            bind[i] = new[] { (float)t[0], (float)t[1], (float)t[2], (float)q[0], (float)q[1], (float)q[2], (float)q[3] };
        }
        int Joint(JsonNode? value, string what)
        {
            var name = Text(value) ?? throw new PackageException($"{what} must be a joint name");
            var index = System.Array.IndexOf(names, name);
            if (index < 0) index = System.Array.IndexOf(keys, CharacterImporter.JointChecksum(name));
            return index >= 0 ? index : throw new PackageException($"{what}: the skeleton has no joint '{name}'");
        }

        if (skeleton["humanoid"] is not JsonObject map) throw new PackageException("skeleton.humanoid must be an object");
        var known = HumanoidRoles.All.ToHashSet(StringComparer.Ordinal);
        var humanoid = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (role, value) in map)
        {
            if (!known.Contains(role)) throw new PackageException($"skeleton.humanoid: unknown role '{role}'");
            var bone = Joint(value, $"skeleton.humanoid.{role}");
            if (humanoid.ContainsValue(bone)) throw new PackageException($"skeleton.humanoid: joint '{names[bone]}' has two roles");
            humanoid[role] = bone;
        }
        var missing = HumanoidRoles.Required.Where(r => !humanoid.ContainsKey(r)).ToArray();
        if (missing.Length > 0) throw new PackageException($"skeleton.humanoid needs the roles {string.Join(", ", missing)}");
        bool Above(int above, int bone)
        {
            for (var parent = parents[bone]; parent >= 0; parent = parents[parent]) if (parent == above) return true;
            return false;
        }
        foreach (var (above, below) in Hierarchy())
            if (humanoid.TryGetValue(above, out var a) && humanoid.TryGetValue(below, out var b) && !Above(a, b))
                throw new PackageException($"skeleton.humanoid: {above} ('{names[a]}') must be a parent of {below} ('{names[b]}')");
        var fingers = HumanoidRoles.Gh3.Keys.Count(r => r.Contains("Proximal") || r.Contains("Intermediate") || r.Contains("Distal") || r.Contains("Metacarpal"));
        var mappedFingers = humanoid.Keys.Count(r => r.Contains("Proximal") || r.Contains("Intermediate") || r.Contains("Distal") || r.Contains("Metacarpal"));
        if (mappedFingers < fingers) warnings.Add($"the skeleton maps {mappedFingers} of {fingers} finger bones; unmapped fingers keep their rest pose");

        // Rest pose: upright, left side on +X (runtime coordinates), front toward -Z, a plausible height.
        var world = World(parents, bind);
        Vector3 P(string role) => world[humanoid[role]];
        var feet = Math.Min(P("leftFoot").Y, P("rightFoot").Y);
        var height = P("head").Y - feet;
        if (!(height is > 0.2f and < 5)) throw new PackageException($"the rest pose is not upright: the head is {height:0.##} m above the feet (export with +Y up)");
        if (!(P("hips").Y > feet && P("hips").Y < P("head").Y)) throw new PackageException("the rest pose is not upright: the hips must be between the feet and the head");
        if (P("leftUpperArm").X <= P("rightUpperArm").X || P("leftUpperLeg").X <= P("rightUpperLeg").X)
            throw new PackageException("the left and right roles are swapped: the character's left side must be on +X in the GLB (the model faces +Z in glTF)");
        if (humanoid.ContainsKey("leftToes") && humanoid.ContainsKey("rightToes") &&
            P("leftToes").Z + P("rightToes").Z > P("leftFoot").Z + P("rightFoot").Z)
            throw new PackageException("the toes are behind the feet: the model must face +Z in glTF (Blender: -Y)");
        foreach (var side in new[] { "left", "right" })
        {
            var arm = Vector3.Normalize(P(side + "Hand") - P(side + "UpperArm"));
            var below = MathF.Asin(Math.Clamp(-arm.Y, -1, 1)) * 180 / MathF.PI;
            if (below is < -10 or > 80) warnings.Add($"the {side} arm points {below:0}° below the horizontal at rest; a T-pose or an A-pose works best");
        }

        var followers = new List<(int, int, double)>();
        if (skeleton["followers"] is JsonNode rows)
        {
            if (rows is not JsonArray list || list.Count > MaxFollowers) throw new PackageException($"skeleton.followers must be a list of at most {MaxFollowers} rows");
            foreach (var row in list)
            {
                Fields(row, "skeleton.followers row", new[] { "bone", "source", "weight" }, "bone", "source", "weight");
                var bone = Joint(row!["bone"], "skeleton.followers bone");
                var source = Joint(row["source"], "skeleton.followers source");
                if (bone == source) throw new PackageException($"skeleton.followers: '{names[bone]}' follows itself");
                if (humanoid.ContainsValue(bone)) throw new PackageException($"skeleton.followers: '{names[bone]}' has a humanoid role; the retarget drives it");
                if (followers.Any(f => f.Item1 == bone)) throw new PackageException($"skeleton.followers: '{names[bone]}' follows two bones");
                followers.Add((bone, source, Number(row["weight"], -4, 4, "skeleton.followers weight")));
            }
            for (var start = 0; start < followers.Count; start++)
            {
                var bone = followers[start].Item1;
                for (var steps = 0; steps <= followers.Count; steps++)
                {
                    var next = followers.FindIndex(f => f.Item1 == followers.First(g => g.Item1 == bone).Item2);
                    if (next < 0) break;
                    bone = followers[next].Item1;
                    if (bone == followers[start].Item1 || steps == followers.Count) throw new PackageException("skeleton.followers has a cycle");
                }
            }
        }

        var instrumentBone = -1;
        var offset = new double[] { 0, 0, 0 };
        var scale = 0.0;
        if (skeleton["instrument"] is JsonNode instrument)
        {
            Fields(instrument, "skeleton.instrument", new[] { "bone", "offset", "scale" });
            if (instrument["scale"] != null) scale = Number(instrument["scale"], 0.5, 1.5, "skeleton.instrument scale");
            if (instrument["bone"] != null) instrumentBone = Joint(instrument["bone"], "skeleton.instrument bone");
            if (instrument["offset"] != null)
            {
                offset = Vector(instrument["offset"], 3, "skeleton.instrument offset");
                if (Math.Sqrt(offset.Sum(v => v * v)) > 1) throw new PackageException("skeleton.instrument offset must be at most 1 m");
            }
        }

        var blocked = new HashSet<int>(humanoid.Values);
        foreach (var bone in humanoid.Values)
            for (var parent = parents[bone]; parent >= 0; parent = parents[parent]) blocked.Add(parent);
        foreach (var (bone, _, _) in followers) blocked.Add(bone);
        return new CharacterSkeleton
        {
            Names = names, Keys = keys, Parents = parents, Bind = bind, Humanoid = humanoid, Followers = followers.ToArray(),
            InstrumentBone = instrumentBone, InstrumentOffset = offset, InstrumentScale = scale,
            ChainBones = Enumerable.Range(0, keys.Length).Where(i => !blocked.Contains(i)).Select(i => keys[i]).ToHashSet(StringComparer.Ordinal),
        };
    }

    // character.json face: the morph target name of each slot ("" for a slot that the character does not have).
    public static string[] Face(JsonNode? node)
    {
        var result = FaceSlots.Select(_ => "").ToArray();
        if (node == null) return result;
        if (node is not JsonObject face) throw new PackageException("character.json face must be an object");
        Fields(face, "face", FaceSlots);
        for (var i = 0; i < FaceSlots.Length; i++)
            if (face[FaceSlots[i]] != null)
                result[i] = Text(face[FaceSlots[i]]) is { Length: > 0 and <= 64 } name ? name : throw new PackageException($"face.{FaceSlots[i]} must be a morph target name");
        return result;
    }

    // The cache row: the joints in skin order (`joints` of the cache are their checksums), parents, local bind poses in
    // runtime coordinates, the humanoid map as joint indices, the followers, and the instrument bone.
    public JsonObject Cache(string[] skinOrder)
    {
        if (!skinOrder.SequenceEqual(Keys)) throw new PackageException("the skeleton joints changed order");
        var humanoid = new JsonObject();
        foreach (var (role, bone) in Humanoid.OrderBy(p => p.Key, StringComparer.Ordinal)) humanoid[role] = bone;
        return new JsonObject
        {
            ["names"] = new JsonArray(Names.Select(n => (JsonNode?)n).ToArray()),
            ["parents"] = new JsonArray(Parents.Select(p => (JsonNode?)p).ToArray()),
            ["bind"] = new JsonArray(Bind.Select(b => (JsonNode?)Array(b.Select(v => (double)v))).ToArray()),
            ["humanoid"] = humanoid,
            ["followers"] = new JsonArray(Followers.Select(f => (JsonNode?)new JsonArray(f.Bone, f.Source, f.Weight)).ToArray()),
            ["instrumentBone"] = InstrumentBone,
            ["instrumentOffset"] = Array(InstrumentOffset),
            ["instrumentScale"] = InstrumentScale,
        };
    }

    private static Vector3[] World(int[] parents, float[][] bind)
    {
        var position = new Vector3[parents.Length];
        var rotation = new Quaternion[parents.Length];
        var done = new bool[parents.Length];
        void Visit(int bone, int depth)
        {
            if (done[bone]) return;
            if (depth > parents.Length) throw new PackageException("the skeleton has a cycle");
            var b = bind[bone];
            var local = new Vector3(b[0], b[1], b[2]);
            var turn = Quaternion.Normalize(new Quaternion(b[3], b[4], b[5], b[6]));
            var parent = parents[bone];
            if (parent >= 0)
            {
                Visit(parent, depth + 1);
                local = position[parent] + Vector3.Transform(local, rotation[parent]);
                turn = rotation[parent] * turn;
            }
            position[bone] = local;
            rotation[bone] = turn;
            done[bone] = true;
        }
        for (var i = 0; i < parents.Length; i++) Visit(i, 0);
        return position;
    }
}
