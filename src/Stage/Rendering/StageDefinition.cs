namespace Gh3Skin;

// stage.json of a compiled stage or model folder: the camera, the materials, and the light and object groups.
internal sealed class StageDefinition
{
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public float[] CameraPosition { get; set; } = Array.Empty<float>();
    public float[] CameraTarget { get; set; } = Array.Empty<float>();
    public float FieldOfView { get; set; } = 55; // Tuned: the default camera of a stage without one
    public float[][] PyroPositions { get; set; } = Array.Empty<float[]>();
    public Dictionary<string, StageMaterial> Materials { get; set; } = new();
    public string[] LightGroups { get; set; } = Array.Empty<string>();
    public string[] VisibilityGroups { get; set; } = Array.Empty<string>();
    public string[] HiddenGroups { get; set; } = Array.Empty<string>();
    public string[] ObjectIds { get; set; } = Array.Empty<string>();
    public Dictionary<string, StageObjectPlacement> Objects { get; set; } = new();
}

internal sealed class StageMaterial
{
    public string Source { get; set; } = "";
    public int MovieSlot { get; set; } = -1;
    public StageTextureAnimation? Animation { get; set; }
    public string Texture { get; set; } = "";
    public int Blend { get; set; }
    public float Cutoff { get; set; }
    public bool Crowd { get; set; }
    public bool Billboard { get; set; }
    public bool SortedTransparency { get; set; }
    public string Shading { get; set; } = "";
    public string Lightmap { get; set; } = "";
    public float ColorScale { get; set; } = 1;
    public bool? DoubleSided { get; set; }
    public string CrowdRole { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Layer { get; set; } = "";
    public float[] LayerColor { get; set; } = Array.Empty<float>();
    public float[] UvWibble { get; set; } = Array.Empty<float>();
    public StageGh3Material? Gh3 { get; set; }
    public float[] Color { get; set; } = Array.Empty<float>();
    public StageToon? Toon { get; set; }
    public string HairLayer { get; set; } = "";
    public int ShadingMode => Shading switch { "unlit" => 0, "ambient" => 1, "lit" => 2, "toon" => 3, "" => -1, _ => throw new InvalidDataException("Unknown stage shading mode") };
}
