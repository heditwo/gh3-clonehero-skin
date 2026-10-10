using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// The venue's materials and textures: GLB materials with their GH3 records, and the texture references of the cache.
internal sealed partial class StageCompiler
{
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

    // pipeline/stagepack/pack.py texture_reference: '#<image name>', a package texture path (.tex), or (older packs) a texture index.
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
        else if (Text(reference) is { } path && path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            var name = Key("file-" + path);
            if (images.Add(name))
            {
                _sources.Add(path);
                var data = _package.Read(path);
                _textures.Add(Path.Combine(textureFolder, name), () => data);
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
}
