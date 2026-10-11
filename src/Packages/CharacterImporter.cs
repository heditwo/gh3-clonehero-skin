using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// Character package (`.chchar`, PACKAGE-FORMAT.md section 9) to a band model folder that StageAssets draws as the library
// members: GH3BND02 meshes, stage.json, and textures, plus the joint list (character.json) and the GLB inverse bind matrices
// (bindposes.bin). The material rules are the library rules (LibraryImporter.ImportModel), so a library member exported as a
// character gives the same meshes and materials. On a fixed profile of the band library, the band role's rig and clips
// drive it; joints map by name. On a profile of a character library, its own rig, animation set, hair, and IK drive it
// (StageBand).
internal sealed class CharacterImporter
{
    public const double JointPosition = 1e-4;
    public const double JointRotation = 0.01;
    public const double JointScale = 1e-4;
    public const double BindTolerance = 1e-4;
    public static readonly IReadOnlyDictionary<string, string> Profiles = new Dictionary<string, string>
    {
        ["gh3-guitarist-axel-v1"] = "guitarist", ["gh3-bassist-v1"] = "bassist", ["gh3-singer-male-v1"] = "singer", ["gh3-drummer-v1"] = "drummer",
    };
    private readonly VerifiedPackage _package;
    private readonly TextureJobs _textures;
    private readonly string _library;
    private readonly IReadOnlyDictionary<string, string> _libraries;
    public List<string> Warnings { get; } = new();

    // `libraries`: the cache folders of the required libraries by ID; the band library is `libraryCache`.
    public CharacterImporter(VerifiedPackage package, TextureJobs textures, string libraryCache, IReadOnlyDictionary<string, string>? libraries = null)
    {
        _package = package;
        _textures = textures;
        _library = libraryCache;
        _libraries = libraries ?? new Dictionary<string, string>();
    }

    private static string Key(string text) => Gh3Checksum.Of(text);

    public static string JointChecksum(string name) => Regex.IsMatch(name, @"\Abone-[0-9a-f]{8}\z") ? name[5..] : Key(name);

    public (string Role, int Joints, int Meshes, int Vertices) Import(string folder)
    {
        var entry = _package.Manifest.Entry;
        var character = Object(_package.Read(entry["character"]));
        Fields(character, "character.json", new[] { "version", "library", "profile", "animations", "hairMaterials", "attachments", "highway",
            "bones", "colliders", "chains", "skeleton", "face" }, "version", "library", "profile");
        if (Number(character["version"], "version") != 1) throw new PackageException("character.json version must be 1");
        var profile = Text(character["profile"]) ?? "";
        var libraryId = Text(character["library"]) ?? "";
        var animations = "";
        string role;
        string[] rigBones;
        int[] rigParents;
        float[][] rigBind;
        if (Profiles.TryGetValue(profile, out var fixedRole))
        {
            role = fixedRole;
            (rigBones, rigParents, rigBind) = Rig(_library, role);
            libraryId = "";
        }
        else
        {
            // A profile of a required character library (PACKAGE-FORMAT.md section 10.1).
            var library = _libraries.TryGetValue(libraryId, out var libraryFolder) && CharacterLibrary.Has(libraryFolder) ? CharacterLibrary.Load(libraryFolder)
                : throw new PackageException($"unknown character profile '{profile}' (library {libraryId} is not a required character library)");
            var own = library.Profiles.TryGetValue(profile, out var found) ? found : throw new PackageException($"library {libraryId} has no profile '{profile}'");
            animations = Text(character["animations"]) ?? "";
            if (character["animations"] != null && !library.Animations.Contains(animations))
                throw new PackageException($"library {libraryId} has no animation set '{animations}'");
            if (character["animations"] != null && !library.SetClipRigs[animations].Bones.SequenceEqual(own.Bones))
                throw new PackageException($"animation set '{animations}' is for another rig than profile '{profile}'");
            (role, rigBones, rigParents, rigBind) = (own.Role, own.Bones, own.Parents, own.Bind);
        }
        var hair = (character["hairMaterials"] as JsonArray ?? new JsonArray()).Select(n => Text(n) ?? throw new PackageException("invalid hair material")).ToHashSet();
        var instrument = Instrument(character["attachments"]);
        var highway = Highway(character["highway"], folder);
        string[] names;
        List<MeshPart> parts;
        JsonNode? added = null, colliders, joints, skeletonRow = null, faceRow = null;
        if (character["skeleton"] != null)
        {
            // Its own skeleton (section 9.6): the GLB joints are free; the humanoid map drives them with the profile's clips.
            if (character["bones"] != null) throw new PackageException("a character with its own skeleton has no 'bones': its chain bones are skeleton joints");
            var glb = new GlbDocument(_package.Read(entry["model"]));
            var skeleton = CharacterSkeleton.Read(glb, character["skeleton"]!, Warnings);
            var chains = CharacterChains.Read(character, skeleton.Keys, skeleton.Parents, skeleton.Bind, skeleton.ChainBones);
            var face = CharacterSkeleton.Face(character["face"]);
            (names, parts) = ImportModel(folder, entry["model"], profile, skeleton.Keys, skeleton.Parents, skeleton.Bind, hair, glb, ownSkeleton: true, face: face);
            skeletonRow = skeleton.Cache(names);
            faceRow = new JsonArray(face.Select(f => (JsonNode?)f).ToArray());
            (colliders, joints) = (chains.Colliders, chains.Joints);
        }
        else
        {
            if (character["face"] != null) throw new PackageException("'face' needs 'skeleton': characters on a GH3 rig move the face with bones");
            // Custom chains (section 9.5): the added bones join the rig for the GLB check.
            var chains = CharacterChains.Read(character, rigBones, rigParents, rigBind);
            (rigBones, rigParents, rigBind) = (chains.Bones, chains.Parents, chains.Bind);
            (names, parts) = ImportModel(folder, entry["model"], profile, rigBones, rigParents, rigBind, hair);
            (added, colliders, joints) = (chains.AddedBones, chains.Colliders, chains.Joints);
        }
        var info = new JsonObject
        {
            ["version"] = 1, ["id"] = _package.Manifest.Id, ["name"] = _package.Manifest.Name, ["profile"] = profile, ["role"] = role,
            ["instrument"] = instrument, ["joints"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()),
            ["library"] = libraryId, ["animations"] = animations, ["highway"] = highway,
            ["bones"] = added ?? new JsonArray(), ["colliders"] = colliders, ["chains"] = joints,
        };
        if (skeletonRow != null) (info["skeleton"], info["face"]) = (skeletonRow, faceRow);
        Write(Path.Combine(folder, "character.json"), info);
        return (role, names.Length, parts.Count, parts.Sum(p => p.Count));
    }

    public const string HighwayFile = "highway.png";

    // A highway image (character.json or instrument.json `highway`): a PNG of the package, copied to the cache as
    // highway.png. The game decodes it when the song loads. Returns the cache file name, or "" without a highway.
    public string Highway(JsonNode? reference, string folder)
    {
        if (reference == null) return "";
        var path = Text(reference) ?? throw new PackageException("highway must be a package path");
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !_package.Contains(path)) throw new PackageException($"the highway file {path} is missing");
        var png = _package.Read(path);
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P') throw new PackageException($"the highway {path} is not a PNG file");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new PackageException($"the highway {path} must be 1 to 4096 pixels on each side");
        if (width * 2 != height) Warnings.Add($"the highway is {width} x {height} pixels; GH3 highways are 512 x 1024 (other sizes stretch)");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, HighwayFile), png);
        return HighwayFile;
    }

    // The skinned GLB `model` on a rig profile, as a band model folder (meshes.bin, stage.json, textures, bindposes.bin).
    // Characters and instruments share it. Returns the GH3 bone name of each skin joint and the mesh parts.
    // `ownSkeleton`: the rig is the GLB skeleton itself (CharacterSkeleton), so the joints are not checked against a profile.
    // `face`: the morph target names of the face slots (CharacterSkeleton.FaceSlots order; "" for none) to keep (morphs.bin).
    public (string[] Names, List<MeshPart> Parts) ImportModel(string folder, string model, string profile, string[] rigBones, int[] rigParents, float[][] rigBind,
        ISet<string> hair, GlbDocument? document = null, bool ownSkeleton = false, string[]? face = null)
    {
        var glb = document ?? new GlbDocument(_package.Read(model));
        if (glb.List("animations").Count > 0) Warnings.Add("the GLB animations are ignored; the library supplies the clips");
        var skins = glb.List("skins");
        if (skins.Count != 1) throw new PackageException("a character GLB needs one skin");
        var skin = (JsonObject)skins[0]!;
        var joints = ((JsonArray)skin["joints"]!).Select(j => j!.GetValue<int>()).ToArray();
        if (joints.Length is < 1 or > 255 || joints.Distinct().Count() != joints.Length) throw new PackageException("invalid character joints");
        var nodes = glb.List("nodes");
        var names = joints.Select(j => JointChecksum(Text(nodes[j]?["name"]) ?? throw new PackageException("character joints need names"))).ToArray();
        if (names.Distinct().Count() != names.Length) throw new PackageException("two character joints have the same GH3 bone name");
        var missing = rigBones.Where(b => !names.Contains(b)).ToArray();
        var extra = names.Where(b => !rigBones.Contains(b)).ToArray();
        if (missing.Length > 0) throw new PackageException($"{missing.Length} joints of profile {profile} are missing, for example bone-{missing[0]}");
        if (extra.Length > 0) throw new PackageException($"{extra.Length} joints are not in profile {profile}, for example bone-{extra[0]}");
        var parentNodes = glb.Parents();
        var world = glb.Nodes().ToDictionary(n => n.Index, n => n.World);
        if (joints.Any(j => !world.ContainsKey(j))) throw new PackageException("a character joint is outside the scene");
        var worst = (Position: 0.0, Rotation: 0.0, Scale: 0.0);
        for (var i = 0; i < joints.Length; i++)
        {
            var bone = System.Array.IndexOf(rigBones, names[i]);
            var parent = parentNodes.TryGetValue(joints[i], out var p) ? System.Array.IndexOf(joints, p) : -1;
            var expected = rigParents[bone];
            if ((parent < 0) != (expected < 0) || parent >= 0 && names[parent] != rigBones[expected])
                throw new PackageException($"joint bone-{names[i]} has another parent than profile {profile}");
            for (var above = parent < 0 && parentNodes.TryGetValue(joints[i], out var first) ? first : -1; above >= 0;
                 above = parentNodes.TryGetValue(above, out var next) ? next : -1)
                if (!Matrix4d.FromNode(glb.Item("nodes", above)).Close(Matrix4d.Identity, BindTolerance))
                    throw new PackageException("the nodes above the root joint must have no transform (apply the armature transform before export)");
            var local = Matrix4d.Reflect(Matrix4d.FromNode(glb.Item("nodes", joints[i])));
            var m = local.Upper3();
            var scale = new double[3];
            for (var c = 0; c < 3; c++) scale[c] = Math.Sqrt(m[c] * m[c] + m[3 + c] * m[3 + c] + m[6 + c] * m[6 + c]);
            for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) m[r * 3 + c] /= scale[c];
            var q = Matrix4d.Quaternion(m);
            var t = local.Translation;
            var b = rigBind[bone];
            var position = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (t[c] - b[c]) * (t[c] - b[c])));
            // Chord form 4 asin(|a - b| / 2): acos of a dot product near 1 loses the precision of the float32 rig.
            var length = Math.Sqrt(b[3] * (double)b[3] + b[4] * (double)b[4] + b[5] * (double)b[5] + b[6] * (double)b[6]);
            var sign = q[0] * b[3] + q[1] * b[4] + q[2] * b[5] + q[3] * b[6] < 0 ? -1 : 1;
            var chord = Math.Sqrt(Enumerable.Range(0, 4).Sum(c => Math.Pow(q[c] - sign * b[3 + c] / length, 2)));
            var angle = 4 * Math.Asin(Math.Min(1, chord / 2)) * 180 / Math.PI;
            worst = (Math.Max(worst.Position, position), Math.Max(worst.Rotation, angle), Math.Max(worst.Scale, scale.Max(s => Math.Abs(s - 1))));
        }
        if (ownSkeleton) worst = default;
        if (worst.Position > JointPosition) throw new PackageException($"joint positions differ from profile {profile} by {worst.Position * 1000:0.###} mm (limit 0.1 mm)");
        if (worst.Rotation > JointRotation) throw new PackageException($"joint rotations differ from profile {profile} by {worst.Rotation:0.###}° (limit 0.01°)");
        if (worst.Scale > JointScale) throw new PackageException($"joint scale differs from 1 by {worst.Scale:g3} (limit 1e-4)");

        var inverseValues = glb.Accessor(skin["inverseBindMatrices"] is JsonValue ibm ? ibm.GetValue<int>() : throw new PackageException("the character skin needs inverse bind matrices"));
        if (inverseValues.Count != joints.Length || inverseValues.Columns != 16) throw new PackageException("the inverse bind matrix count differs from the joints");
        var bindposes = new Matrix4d[joints.Length];
        for (var i = 0; i < joints.Length; i++)
        {
            var m = new double[16];
            for (var k = 0; k < 16; k++) m[(k % 4) * 4 + k / 4] = inverseValues.Values[i * 16 + k];
            bindposes[i] = Matrix4d.Reflect(new Matrix4d(m));
        }
        var meshNodes = glb.Nodes().Where(n => n.Node["mesh"] != null).ToList();
        if (meshNodes.Count == 0) throw new PackageException("the character has no mesh");
        foreach (var (_, node, meshWorld) in meshNodes)
        {
            if (node["skin"] is not JsonValue skinIndex || skinIndex.GetValue<int>() != 0) throw new PackageException("all character meshes must use its skin");
            for (var i = 0; i < joints.Length; i++)
                if (!(world[joints[i]] * bindposes[i]).Close(meshWorld, BindTolerance))
                    throw new PackageException("the inverse bind matrices differ from the joints (export in the rest position)");
        }

        Directory.CreateDirectory(folder);
        var materials = glb.List("materials");
        var imageNames = ImageNames(glb);
        var materialIds = new Dictionary<int, string>();
        for (var i = 0; i < materials.Count; i++)
        {
            var name = Text(materials[i]?["name"]) ?? "";
            materialIds[i] = Regex.IsMatch(name, @"\A[0-9a-f]{8}\z") ? name : Key("material-" + (name.Length > 0 ? name : i.ToString()));
        }
        var fallbackMaterial = Key("material-default");
        var parts = new List<MeshPart>();
        var partMorphs = new List<float[]?[]>();
        var streamless = new HashSet<string>();
        var morphs = 0;
        var faceFound = new bool[face?.Length ?? 0];
        foreach (var (_, node, _) in meshNodes)
        {
            var mesh = glb.Item("meshes", node["mesh"]!.GetValue<int>());
            var targetNames = (mesh["extras"]?["targetNames"] as JsonArray)?.Select(n => Text(n) ?? "").ToArray() ?? System.Array.Empty<string>();
            foreach (var primitive in (JsonArray)mesh["primitives"]!)
            {
                if (GlbDocument.Int((JsonObject)primitive!, "mode", 4) != 4) throw new PackageException("triangulate the character meshes before export");
                if (primitive!["targets"] is JsonArray { Count: > 0 }) morphs++;
                var material = primitive["material"] is JsonValue mi ? materialIds[mi.GetValue<int>()] : fallbackMaterial;
                var (vertices, count, hasStreams) = Vertices(glb, (JsonObject)primitive["attributes"]!, joints.Length);
                if (!hasStreams) streamless.Add(material);
                var faces = primitive["indices"] is JsonValue indices ? glb.Accessor(indices.GetValue<int>(), normalize: false).Values.Select(i => (int)i).ToArray() :
                    Enumerable.Range(0, count).ToArray();
                if (faces.Length % 3 != 0 || faces.Any(f => f < 0 || f >= count)) throw new PackageException("invalid character triangles");
                for (var i = 0; i < faces.Length; i += 3) (faces[i + 1], faces[i + 2]) = (faces[i + 2], faces[i + 1]);
                if (faces.Length == 0) continue;
                var shapes = face == null ? System.Array.Empty<float[]?>() : Morphs(glb, (JsonObject)primitive, targetNames, face, count, faceFound);
                if (count <= 65535)
                {
                    parts.Add(new MeshPart(material, count, vertices, faces));
                    partMorphs.Add(shapes);
                }
                else
                {
                    var split = StageGeometry.Split(material, vertices, StageGeometry.SkinnedStride, faces).ToList();
                    parts.AddRange(split);
                    foreach (var order in StageGeometry.SplitOrders(faces))
                        partMorphs.Add(shapes.Select(shape => shape == null ? null : order.SelectMany(v => new[] { shape[v * 3], shape[v * 3 + 1], shape[v * 3 + 2] }).ToArray()).ToArray());
                }
            }
        }
        if (face == null && morphs > 0) Warnings.Add($"{morphs} primitives have blend shapes; only a character with its own skeleton and 'face' uses them");
        for (var slot = 0; slot < faceFound.Length; slot++)
            if (face![slot].Length > 0 && !faceFound[slot]) throw new PackageException($"face.{CharacterSkeleton.FaceSlots[slot]}: no mesh has the morph target '{face[slot]}'");
        if (parts.Count == 0) throw new PackageException("the character has no triangles");
        StageGeometry.WriteMeshes(Path.Combine(folder, "meshes.bin"), parts, skinned: true);
        if (face != null) WriteMorphs(Path.Combine(folder, MorphFile), partMorphs);

        var rows = new JsonObject();
        var referenced = new Dictionary<string, Func<byte[]>>();
        string Texture(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return "";
            if (reference.StartsWith('#'))
            {
                var image = glb.NamedImage(reference[1..]) ?? throw new PackageException($"no GLB image named {reference[1..]}");
                referenced.TryAdd(imageNames[image], () => glb.Image(image));
                return imageNames[image];
            }
            if (!reference.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || !_package.Contains(reference)) throw new PackageException($"invalid texture reference {reference}");
            var name = Key("file-" + reference);
            // Read on this thread: the texture jobs run in parallel and the package source is not thread-safe.
            if (!referenced.ContainsKey(name))
            {
                var data = _package.Read(reference);
                referenced.Add(name, () => data);
            }
            return name;
        }
        var used = parts.Select(p => p.Material).ToHashSet();
        for (var i = 0; i <= materials.Count; i++)
        {
            var id = i < materials.Count ? materialIds[i] : fallbackMaterial;
            if (rows.ContainsKey(id) || i == materials.Count && !used.Contains(id)) continue;
            var material = i < materials.Count ? (JsonObject)materials[i]! : new JsonObject();
            var pbr = material["pbrMetallicRoughness"] as JsonObject ?? new JsonObject();
            var extras = material["extras"] as JsonObject ?? new JsonObject();
            var shader = extras["gh3Shader"] as JsonObject;
            if (shader != null && streamless.Contains(id))
            {
                Warnings.Add($"material {Text(material["name"])} has a GH3 shader record but its mesh has no _GH3_* streams; it uses the surface model");
                shader = null;
            }
            var mode = Text(material["alphaMode"]) ?? "OPAQUE";
            var baseTexture = "";
            if (pbr["baseColorTexture"] is JsonObject info)
            {
                if (GlbDocument.Int(info, "texCoord", 0) != 0) throw new PackageException("character materials use TEXCOORD_0");
                var image = GlbDocument.Int(glb.Item("textures", info["index"]!.GetValue<int>()), "source", -1);
                if (image < 0 || image >= imageNames.Length) throw new PackageException("invalid character texture");
                baseTexture = imageNames[image];
                referenced.TryAdd(baseTexture, () => glb.Image(image));
            }
            var row = new JsonObject
            {
                ["blend"] = shader != null ? Clone(shader["blend"]) : Bool(extras["gh3Additive"]) == true ? 1 : mode == "BLEND" ? 2 : 0,
                ["cutoff"] = mode == "MASK" ? Clone(material["alphaCutoff"]) ?? 0.5 : Clone(extras["gh3AlphaCutoff"]) ?? 0,
                ["doubleSided"] = Bool(material["doubleSided"]) == true,
                ["texture"] = baseTexture,
            };
            if (shader != null)
            {
                var gh3 = (JsonObject)Clone(shader)!;
                gh3["textures"] = new JsonArray(((JsonArray)shader["textures"]!).Select(reference => (JsonNode?)Texture(Text(reference))).ToArray());
                row["gh3"] = gh3;
            }
            if (extras["gh3Shading"] != null) row["shading"] = Clone(extras["gh3Shading"]);
            if (Text(extras["gh3Shading"]) == "toon") row["toon"] = Toon(extras["gh3Toon"], Texture);
            else if (extras["gh3Toon"] != null) Warnings.Add($"material {Text(material["name"])} has gh3Toon but its gh3Shading is not toon; the toon settings are ignored");
            if (extras["gh3HairLayer"] != null)
            {
                var layer = Text(extras["gh3HairLayer"]);
                if (layer is not ("overHair" or "hair")) throw new PackageException($"material {Text(material["name"])}: gh3HairLayer must be overHair or hair");
                if (extras["gh3Shading"] == null) Warnings.Add($"material {Text(material["name"])} has gh3HairLayer without gh3Shading; the mod ignores the hair layer");
                row["hairLayer"] = layer;
            }
            if (Bool(extras["gh3SortedTransparency"]) == true || hair.Contains(Text(material["name"]) ?? "\0")) row["sortedTransparency"] = true;
            if (pbr["baseColorFactor"] is JsonArray factor)
            {
                var scale = extras["gh3ColorScale"] == null ? 1 : Number(extras["gh3ColorScale"], "gh3ColorScale");
                row["color"] = Array(new[] { Number(factor[0], "color") * scale, Number(factor[1], "color") * scale, Number(factor[2], "color") * scale, Number(factor[3], "color") });
            }
            rows[id] = row;
        }
        foreach (var name in hair.Where(name => !materials.Any(m => Text(m?["name"]) == name)))
            Warnings.Add($"hair material {name} is not in the GLB");
        Write(Path.Combine(folder, "stage.json"), new JsonObject
        {
            ["version"] = 1, ["name"] = _package.Manifest.Name, ["cameraPosition"] = new JsonArray(0, 0, 0), ["cameraTarget"] = new JsonArray(0, 1, 0), ["materials"] = rows,
        });
        foreach (var (name, png) in referenced) _textures.Add(Path.Combine(folder, "textures", name), png);
        _textures.Flush();

        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "bindposes.bin"))))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3BPS01"));
            writer.Write(joints.Length);
            foreach (var matrix in bindposes)
                for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) writer.Write((float)matrix[r, c]);
        }
        return (names, parts);
    }

    public const string MorphFile = "morphs.bin";

    // The position deltas (runtime coordinates) of the face slots in one primitive; null for a slot that it does not have
    // or that does not move it.
    private static float[]?[] Morphs(GlbDocument glb, JsonObject primitive, string[] targetNames, string[] face, int count, bool[] found)
    {
        var result = new float[]?[face.Length];
        if (primitive["targets"] is not JsonArray targets) return result;
        for (var slot = 0; slot < face.Length; slot++)
        {
            var index = face[slot].Length == 0 ? -1 : System.Array.IndexOf(targetNames, face[slot]);
            if (index < 0 || index >= targets.Count || targets[index]?["POSITION"] is not JsonValue position) continue;
            found[slot] = true;
            var deltas = glb.Accessor(position.GetValue<int>());
            if (deltas.Count != count || deltas.Columns != 3) throw new PackageException($"invalid morph target '{face[slot]}'");
            var values = new float[count * 3];
            var moves = false;
            for (var v = 0; v < count; v++)
            {
                values[v * 3] = (float)deltas[v, 0];
                values[v * 3 + 1] = (float)deltas[v, 1];
                values[v * 3 + 2] = -(float)deltas[v, 2];
                moves |= values[v * 3] != 0 || values[v * 3 + 1] != 0 || values[v * 3 + 2] != 0;
            }
            if (moves) result[slot] = values;
        }
        return result;
    }

    // morphs.bin (magic GH3MRP01): the part count, then for each mesh part the count of its shapes and, for each shape,
    // the face slot (byte), the vertex count of the part, and the position deltas of all its vertices (float32 x, y, z).
    private static void WriteMorphs(string path, List<float[]?[]> parts)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3MRP01"));
        writer.Write(parts.Count);
        foreach (var shapes in parts)
        {
            writer.Write(shapes.Count(s => s != null));
            for (var slot = 0; slot < shapes.Length; slot++)
            {
                if (shapes[slot] is not { } values) continue;
                writer.Write((byte)slot);
                writer.Write(values.Length / 3);
                foreach (var value in values) writer.Write(value);
            }
        }
    }

    // gh3Toon (PACKAGE-FORMAT.md section 6.5) as the stage.json `toon` row (StageToon): the texture references become runtime
    // texture names.
    private static JsonObject Toon(JsonNode? node, Func<string?, string> texture)
    {
        var toon = node as JsonObject ?? new JsonObject();
        if (node != null && node is not JsonObject) throw new PackageException("gh3Toon must be an object");
        Fields(toon, "gh3Toon", new[] { "threshold", "softness", "shadeTexture", "shadeColor", "shadeMaskTexture", "rimColor", "rimWidth", "sphereTexture", "sphereMode", "outline" });
        JsonArray Color(JsonNode? value, double[] fallback, string what) =>
            Array(value == null ? fallback : Vector(value, 3, what).Select(v => Number(JsonValue.Create(v), 0, 4, what)));
        string Reference(string key) => toon[key] == null ? "" : texture(Text(toon[key]) ?? throw new PackageException($"gh3Toon.{key} must be a texture reference"));
        var mode = Text(toon["sphereMode"]) ?? "add";
        if (mode is not ("add" or "multiply")) throw new PackageException("gh3Toon.sphereMode must be add or multiply");
        var row = new JsonObject
        {
            ["threshold"] = toon["threshold"] == null ? 0.5 : Number(toon["threshold"], 0, 1, "gh3Toon.threshold"),
            ["softness"] = toon["softness"] == null ? 0.05 : Number(toon["softness"], 0, 0.5, "gh3Toon.softness"),
            ["shadeTexture"] = Reference("shadeTexture"),
            ["shadeColor"] = Color(toon["shadeColor"], new[] { 1.0, 1, 1 }, "gh3Toon.shadeColor"),
            ["shadeMaskTexture"] = Reference("shadeMaskTexture"),
            ["rimColor"] = Color(toon["rimColor"], new[] { 0.0, 0, 0 }, "gh3Toon.rimColor"),
            ["rimWidth"] = toon["rimWidth"] == null ? 0 : Number(toon["rimWidth"], 0, 1, "gh3Toon.rimWidth"),
            ["sphereTexture"] = Reference("sphereTexture"),
            ["sphereMode"] = mode,
        };
        if (toon["outline"] is JsonObject outline)
        {
            Fields(outline, "gh3Toon.outline", new[] { "width", "color", "textured" }, "width");
            row["outline"] = new JsonObject
            {
                ["width"] = Number(outline["width"], 0, 0.05, "gh3Toon.outline.width"),
                ["color"] = Color(outline["color"], new[] { 0.0, 0, 0 }, "gh3Toon.outline.color"),
                ["textured"] = Bool(outline["textured"]) == true,
            };
        }
        else if (toon["outline"] != null) throw new PackageException("gh3Toon.outline must be an object");
        return row;
    }

    // attachments.instrument: a library instrument ID (band/band.json `instrument`), or "" for the default. StageBand
    // adds the instrument when the character plays (BandAttachments.Choose).
    private string Instrument(JsonNode? attachments)
    {
        if (attachments == null) return "";
        if (attachments is not JsonObject slots) throw new PackageException("character.json attachments must be an object");
        Fields(slots, "attachments", new[] { "instrument" });
        if (slots["instrument"] == null) return "";
        var id = Text(slots["instrument"]) ?? throw new PackageException("attachments.instrument must be a string");
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_]{1,64}\z")) throw new PackageException($"invalid attachments.instrument '{id}'");
        var band = Path.Combine(_library, "band", "band.json");
        var known = File.Exists(band) ? (Object(File.ReadAllBytes(band))["members"] as JsonArray ?? new JsonArray())
            .Select(m => Text(m?["instrument"]) ?? "").Where(i => i.Length > 0).ToHashSet() : new HashSet<string>();
        if (!known.Contains(id))
        {
            Warnings.Add($"attachments.instrument {id} is not an instrument of the library ({string.Join(", ", known.OrderBy(i => i, StringComparer.Ordinal))}); the role's instrument plays");
            return "";
        }
        return id;
    }

    // Runtime texture names are 8 hex digits: the image name when it is one (GH3 texture checksums), else a checksum of
    // the image index.
    private static string[] ImageNames(GlbDocument glb)
    {
        var images = glb.List("images");
        var result = new string[images.Count];
        var taken = new HashSet<string>();
        for (var i = 0; i < images.Count; i++)
            if (Text(images[i]?["name"]) is { } name && Regex.IsMatch(name, @"\A[0-9a-f]{8}\z") && taken.Add(name)) result[i] = name;
        for (var i = 0; i < images.Count; i++)
            if (result[i] == null)
            {
                var name = Key("image-" + i);
                while (!taken.Add(name)) name = Key("image-" + name);
                result[i] = name;
            }
        return result;
    }

    // GH3BND02 vertices (pipeline/package/roundtrip.py model_runtime): the standard attributes in Unity coordinates, then the
    // optional original GH3 streams.
    private static (byte[] Vertices, int Count, bool Streams) Vertices(GlbDocument glb, JsonObject attributes, int jointCount)
    {
        GlbAccessor? Optional(string name, bool normalize = false) => attributes[name] is JsonValue index ? glb.Accessor(index.GetValue<int>(), normalize) : null;
        GlbAccessor Required(string name, bool normalize = false) => Optional(name, normalize) ?? throw new PackageException($"character meshes need {name}");
        var positions = Required("POSITION");
        var count = positions.Count;
        var normals = Required("NORMAL");
        var uv = Optional("TEXCOORD_0", normalize: true);
        var colorIndex = attributes["COLOR_0"] is JsonValue ci ? ci.GetValue<int>() : -1;
        // Unsigned bytes stay as stored (the library GLBs); other types scale from 0-1 to 0-255.
        var rawColor = colorIndex >= 0 && glb.Item("accessors", colorIndex)["componentType"]!.GetValue<int>() == 5121;
        var color = colorIndex >= 0 ? glb.Accessor(colorIndex, normalize: !rawColor) : (GlbAccessor?)null;
        var joint = Required("JOINTS_0");
        var weightIndex = attributes["WEIGHTS_0"] is JsonValue wi ? wi.GetValue<int>() : throw new PackageException("character meshes need WEIGHTS_0");
        var weight = glb.Accessor(weightIndex, normalize: true);
        if (attributes.ContainsKey("JOINTS_1") || attributes.ContainsKey("WEIGHTS_1")) throw new PackageException("characters have at most four joint influences for each vertex");
        foreach (var (name, accessor, columns) in new[] { ("NORMAL", normals, 3), ("JOINTS_0", joint, 4), ("WEIGHTS_0", weight, 4) })
            if (accessor.Count != count || accessor.Columns != columns) throw new PackageException($"invalid character {name}");
        if (positions.Columns != 3 || uv is { } u && (u.Count != count || u.Columns != 2) || color is { } k && (k.Count != count || k.Columns is not (3 or 4)))
            throw new PackageException("invalid character vertex attributes");
        var streams = attributes.ContainsKey("_GH3_UV0");
        var gh3Uv = streams ? Enumerable.Range(0, 6).Select(i => Required($"_GH3_UV{i}")).ToArray() : null;
        var binormal = streams ? Required("_GH3_BINORMAL") : default;
        var tangent = streams ? Required("_GH3_TANGENT") : default;
        var vertices = new byte[count * StageGeometry.SkinnedStride];
        for (var v = 0; v < count; v++)
        {
            var offset = v * StageGeometry.SkinnedStride;
            var row = vertices.AsSpan(offset, StageGeometry.SkinnedStride);
            void F(int at, double value) => BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(offset + at), (float)value);
            F(SkinnedVertex.Position, positions[v, 0]); F(SkinnedVertex.Position + 4, positions[v, 1]); F(SkinnedVertex.Position + 8, -positions[v, 2]);
            F(SkinnedVertex.Normal, normals[v, 0]); F(SkinnedVertex.Normal + 4, normals[v, 1]); F(SkinnedVertex.Normal + 8, -normals[v, 2]);
            if (uv is { } texture) { F(SkinnedVertex.Uv, texture[v, 0]); BinaryPrimitives.WriteSingleLittleEndian(row[SkinnedVertex.UvV..], 1f - (float)texture[v, 1]); }
            for (var c = 0; c < 4; c++)
                row[SkinnedVertex.Color + c] = color is not { } paint ? (byte)255 : c >= paint.Columns ? (byte)255 :
                    rawColor ? (byte)paint[v, c] : (byte)Math.Clamp(Math.Round(paint[v, c] * 255), 0, 255);
            double sum = 0;
            for (var c = 0; c < 4; c++)
            {
                var index = joint[v, c];
                if (index < 0 || index >= jointCount || weight[v, c] < 0) throw new PackageException("invalid character skin weights");
                row[SkinnedVertex.Joints + c] = (byte)index;
                sum += weight[v, c];
            }
            if (Math.Abs(sum - 1) > 0.01) throw new PackageException("character skin weights must add up to 1");
            // Normalized integer weights round; float weights stay as exported.
            var renormalize = Math.Abs(sum - 1) > 1e-6;
            for (var c = 0; c < 4; c++) F(SkinnedVertex.Weights + c * 4, renormalize ? weight[v, c] / sum : weight[v, c]);
            if (gh3Uv != null)
            {
                for (var i = 0; i < 6; i++) { F(SkinnedVertex.Gh3Uv + i * SkinnedVertex.Gh3UvSize, gh3Uv[i][v, 0]); F(SkinnedVertex.Gh3Uv + 4 + i * SkinnedVertex.Gh3UvSize, gh3Uv[i][v, 1]); }
                F(SkinnedVertex.Binormal, binormal[v, 0]); F(SkinnedVertex.Binormal + 4, binormal[v, 1]); F(SkinnedVertex.Binormal + 8, -binormal[v, 2]);
                F(SkinnedVertex.Tangent, tangent[v, 0]); F(SkinnedVertex.Tangent + 4, tangent[v, 1]); F(SkinnedVertex.Tangent + 8, -tangent[v, 2]);
            }
        }
        return (vertices, count, streams);
    }

    // The profile rig of a band role in the library cache: bone checksums (catalog.json), parents and bind pose (animation.bin).
    public static (string[] Bones, int[] Parents, float[][] Bind) Rig(string libraryCache, string role)
    {
        var catalog = Object(File.ReadAllBytes(Path.Combine(libraryCache, "catalog.json")));
        var bones = (catalog["members"]?[role]?["bones"] as JsonArray ?? throw new PackageException($"the library has no {role} rig"))
            .Select(b => Text(b) ?? throw new PackageException("invalid library bone")).ToArray();
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(libraryCache, "band", role, "animation.bin")));
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3ANM01") throw new PackageException("invalid library rig");
        var count = reader.ReadInt32();
        reader.ReadInt32();
        if (count != bones.Length) throw new PackageException($"the library {role} rig and catalog differ");
        var parents = new int[count];
        var bind = new float[count][];
        for (var i = 0; i < count; i++)
        {
            parents[i] = reader.ReadInt32();
            bind[i] = Enumerable.Range(0, 7).Select(_ => reader.ReadSingle()).ToArray();
        }
        return (bones, parents, bind);
    }
}
