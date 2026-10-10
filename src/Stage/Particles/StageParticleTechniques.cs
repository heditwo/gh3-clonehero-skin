using System.Text.Json;

namespace Gh3Skin;

internal sealed class StageParticleTechnique
{
    public string Name { get; set; } = "";
    public byte SortId { get; set; }
    public uint Flags { get; set; }
}

internal sealed class StageParticleTechniques
{
    public int Version { get; set; }
    public StageParticleTechnique[] Lit { get; set; } = Array.Empty<StageParticleTechnique>();
    public StageParticleTechnique[] Unlit { get; set; } = Array.Empty<StageParticleTechnique>();

    public static StageParticleTechniques Load(string path)
    {
        var value = JsonSerializer.Deserialize<StageParticleTechniques>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
        if (value == null || value.Version != 1 || value.Lit.Length != 128 || value.Unlit.Length != 16)
            throw new InvalidDataException("Invalid particle material techniques: " + path);
        return value;
    }

    public StageParticleTechnique Select(StageParticleMaterial material) => (material.Lighting ? Lit : Unlit)[material.ShaderVariant];
}
