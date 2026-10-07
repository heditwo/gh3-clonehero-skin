using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleMaterialTests
{
    public static void Run()
    {
        CheckBindings();
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_MATERIAL_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in document.RootElement.GetProperty("materials").EnumerateArray())
        {
            var material = new StageParticleMaterial(Gh3QbText.Decode(row.GetProperty("material").GetRawText()));
            var flags = new[] { material.Layout == Gh3Checksum.Value("Layout2x2"), material.SoftEdge, material.Lighting, material.TwoSide,
                material.StaticShadow, material.DynamicShadow, material.Burn, material.Bloom, material.FillBurst };
            Check(flags.SequenceEqual(row.GetProperty("flags").EnumerateArray().Select(value => value.GetInt32() != 0)), "Particle material flags differ");
            var integers = new[] { material.Layout, material.Blend, unchecked((uint)material.SpecularPower), unchecked((uint)material.AlphaCutoff),
                unchecked((uint)material.Fps), material.Texture, material.SpecularTexture, material.NormalMap, unchecked((uint)material.SortGroup) };
            Check(integers.SequenceEqual(row.GetProperty("integers").EnumerateArray().Select(value => value.GetUInt32())), "Particle material fields differ");
            Check(new[] { material.SoftEdgeScale, material.BurnValue, material.DrawOrder }.SequenceEqual(row.GetProperty("floats").EnumerateArray().Select(value => value.GetSingle())), "Particle material constants differ");
            Check(material.PackedBlend == row.GetProperty("packedBlend").GetUInt32(), "Native particle blend factors differ");
        }
        foreach (var row in document.RootElement.GetProperty("pixels").EnumerateArray())
        {
            Vector4 Value(string name)
            {
                var values = row.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray();
                return new(values[0], values[1], values[2], values[3]);
            }
            var output = StageParticleMaterial.Shade(Value("color"), Value("texture"), row.GetProperty("burn").GetSingle(), row.GetProperty("boost").GetSingle());
            Check(Vector4.Distance(output, Value("output")) < .000001f, "Native particle burn shader differs");
        }
        Console.WriteLine("Native particle material flags, fields, blend factors, and burn shader checks passed.");
    }

    private static void CheckBindings()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_MATERIAL_BINDINGS_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var material = new StageParticleMaterial(Gh3QbText.Decode(row.GetProperty("source").GetRawText()));
            var variant = row.GetProperty("variant");
            Check(material.ShaderVariant == variant.GetProperty("index").GetInt32() && material.Lighting == variant.GetProperty("lit").GetBoolean(), "Particle material shader selection differs");
            Check(material.RenderSortGroup == row.GetProperty("sortGroup").GetByte() && material.RenderDrawOrder == row.GetProperty("drawOrder").GetSingle(), "Particle material render order differs");
            Check(material.RenderBlend == row.GetProperty("blendMode").GetInt32(), "Particle material render blend mode differs");
            var textures = new[] { material.Texture, material.SpecularTexture, material.NormalMap };
            Check(textures.Where(id => id != 0).SequenceEqual(row.GetProperty("lookups").EnumerateArray().Select(value => value.GetUInt32())), "Particle texture lookup order differs");
            var missing = row.GetProperty("missing").EnumerateArray().Select(value => value.GetUInt32()).ToHashSet();
            var slots = textures.Select((id, slot) => (id, slot)).Where(item => item.id != 0 && !missing.Contains(item.id)).ToArray();
            var bindings = row.GetProperty("bindings").EnumerateArray().ToArray();
            Check(slots.Length == bindings.Length && slots.Zip(bindings).All(pair => pair.First.slot == pair.Second[0].GetInt32() && pair.First.id == pair.Second[1].GetUInt32()), "Native particle texture slots differ");
            count++;
        }
        Console.WriteLine($"Native particle material creation, shader selection, texture slots, and sort fields passed: {count} cases.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
