namespace Gh3Skin;

// GH3: checksums of GH3 names that the code needs as compile-time constants (switch labels, field keys). Each value is
// Gh3Checksum.Value of the name in its comment; tests/ScriptLogicTests.cs checks them. Checksums whose names are not
// known stay as hex literals with a GH3: note where they are used.
internal static class Gh3Keys
{
    public const uint R = 0x93f60062; // r
    public const uint G = 0xfe2be489; // g
    public const uint B = 0x8e411006; // b
    public const uint Intensity = 0x164d476d; // intensity
    public const uint SpecularIntensity = 0xb9832749; // specularintensity
    public const uint ShadowIntensity = 0xa0bad95d; // shadowintensity
    public const uint Linear = 0xf221474c; // linear
    public const uint Smooth = 0x8addac1f; // smooth
    public const uint EaseIn = 0xb3eaf72e; // ease_in
    public const uint EaseOut = 0x26b6e535; // ease_out
    public const uint Global = 0x17fa747c; // global
    public const uint Enabled = 0xaf06447b; // enabled
    public const uint Dynamic = 0xb454f54b; // dynamic
    public const uint Shadow = 0x8a897dd2; // shadow
    public const uint Direction = 0xc1b52e4c; // direction
    public const uint DefaultGroup = 0x8aebf027; // defaultgroup
    public const uint Band = 0xb7205d14; // band
    public const uint Point = 0x485a0cdb; // point
    public const uint Spot = 0x46cd858c; // spot
    public const uint Ambient = 0xbcee1d37; // ambient

    public static readonly (uint Key, string Name)[] Names =
    {
        (R, "r"), (G, "g"), (B, "b"), (Intensity, "intensity"), (SpecularIntensity, "specularintensity"),
        (ShadowIntensity, "shadowintensity"), (Linear, "linear"), (Smooth, "smooth"), (EaseIn, "ease_in"), (EaseOut, "ease_out"),
        (Global, "global"), (Enabled, "enabled"), (Dynamic, "dynamic"), (Shadow, "shadow"), (Direction, "direction"),
        (DefaultGroup, "defaultgroup"), (Band, "band"), (Point, "point"), (Spot, "spot"), (Ambient, "ambient"),
    };
}

// GH3: bone checksums as the string keys of the band rigs (Gh3Checksum.Of of the bone name).
internal static class Gh3Bones
{
    public const string Head = "ddec28af"; // Bone_Head
}
