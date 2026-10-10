using System.Numerics;

namespace Gh3Skin;

// A particle material: blend mode, texture atlas, and render flags.

internal sealed record StageParticleMaterial
{
    public uint Layout { get; }
    public uint Blend { get; }
    public uint Texture { get; }
    public uint SpecularTexture { get; }
    public uint NormalMap { get; }
    public int SpecularPower { get; }
    public int AlphaCutoff { get; }
    public int Fps { get; }
    public int SortGroup { get; }
    public float DrawOrder { get; }
    public bool SoftEdge { get; }
    public float SoftEdgeScale { get; }
    public bool Lighting { get; }
    public bool TwoSide { get; }
    public bool StaticShadow { get; }
    public bool DynamicShadow { get; }
    public bool Burn { get; }
    public float BurnValue { get; }
    public bool Bloom { get; }
    public bool FillBurst { get; }
    public uint PackedBlend { get; }
    public int ShaderVariant => (Texture != 0 ? Lighting ? 64 : 8 : 0) | (Lighting && SpecularTexture != 0 ? 32 : 0) |
        (Lighting && NormalMap != 0 ? 16 : 0) | (Lighting && SpecularPower != 0 ? 8 : 0) |
        (SoftEdge ? 4 : 0) | (StaticShadow ? 2 : 0) | (DynamicShadow ? 1 : 0);
    public byte RenderSortGroup => SortGroup == -1 ? (byte)3 : unchecked((byte)SortGroup);
    public float RenderDrawOrder => SortGroup == -1 ? 10 : DrawOrder;
    public int RenderBlend => Blend == Gh3Checksum.Value("add") ? 1 : Blend == Gh3Checksum.Value("sub") || Blend == Gh3Checksum.Value("subtract") ? 2 :
        Blend == Gh3Checksum.Value("blend") ? 3 : Blend == Gh3Checksum.Value("modulate") ? 4 : Blend == Gh3Checksum.Value("brighten") ? 5 : 0;
    public int AtlasColumns => Layout == Gh3Checksum.Value("Layout2x2") || Layout == Gh3Checksum.Value("Layout1x2") ? 2 : 1;
    public int AtlasRows => Layout == Gh3Checksum.Value("Layout2x2") || Layout == Gh3Checksum.Value("Layout2x1") ? 2 : 1;

    public StageParticleMaterial(Gh3QbValue? source)
    {
        bool Flag(string name) => source?.Flag(name) == true;
        uint Key(string name, uint fallback = 0) => source?.Field(name)?.Integer ?? fallback;
        int Integer(string name, int fallback = 0) => unchecked((int)Key(name, unchecked((uint)fallback)));
        float Number(string name, float fallback = 0) => source == null ? fallback : (float)StageParticleDefinition.Number(source, name, fallback, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude);
        Layout = Key("TextureLayout", Gh3Checksum.Value(Flag("QuadTextures") ? "Layout2x2" : "Layout1x1"));
        Blend = Key("BlendMode", Gh3Checksum.Value("add"));
        Texture = Flag("DiffuseTextureEnabled") ? Key("DiffuseTexture") : 0;
        SpecularTexture = Flag("SpecularTextureEnabled") ? Key("SpecularTexture") : 0;
        NormalMap = Flag("NormalMapEnabled") ? Key("NormalMap") : 0;
        SpecularPower = Integer("SpecularPower");
        AlphaCutoff = Integer("AlphaCutoff");
        Fps = Integer("QuadAnimationFPS");
        SortGroup = Integer("SortGroup", -1);
        DrawOrder = Number("DrawOrder");
        SoftEdge = Flag("SoftEdge");
        SoftEdgeScale = Number("SoftEdgeScale", 1);
        Lighting = Flag("Lighting");
        TwoSide = Flag("TwoSide");
        StaticShadow = Flag("StaticShadow");
        DynamicShadow = Flag("DynamicShadow");
        Burn = Flag("Burn");
        BurnValue = Number("BurnValue");
        Bloom = Flag("Bloom");
        FillBurst = Flag("FillBurst");
        // GH3: the packed blend words of the blend modes (factor and operation bytes, see StageParticles.Blend).
        PackedBlend = Burn ? 0x01220622u : Blend == Gh3Checksum.Value("add") ? 0x02250225u :
            Blend == Gh3Checksum.Value("sub") || Blend == Gh3Checksum.Value("subtract") ? 0x02650265u :
            Blend == Gh3Checksum.Value("blend") ? 0x06250625u : Blend == Gh3Checksum.Value("modulate") ? 0x05210521u :
            Blend == Gh3Checksum.Value("brighten") ? 0x02290229u : 0x01220122u;
    }

    public static Vector4 Shade(Vector4 color, Vector4 texture, float burn, float boost = 1)
    {
        var value = color * texture;
        value = new(value.X * Math.Max(1, boost), value.Y * Math.Max(1, boost), value.Z * Math.Max(1, boost), value.W);
        return burn == 0 ? value : new(value.X * value.W, value.Y * value.W, value.Z * value.W, value.W * (1 - burn));
    }
}
