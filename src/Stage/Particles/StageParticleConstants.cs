namespace Gh3Skin;

// GH3 particle constants shared by the particle files. GH3: the values of GH3.exe's particle code; the particle tests
// compare the port with the native oracles (tests/Particle*Tests.cs, PARITY-NOTES.md#particles-and-tesla).
internal static class StageParticleConstants
{
    // One draw of the random generator is Rnd(10000), scaled by 0.0001 to 0-1.
    public const int RandomSteps = 10000;
    public const float RandomScale = .0001f;
    // The light group of a particle system without one (exported default_light_group None).
    public const uint DefaultLightGroup = 0xe3714dc1;
    // At most 8,192 quads per mesh.
    public const int MaximumBatchQuads = 8192;
    public const int MaximumEmitters = 10000;
    // The texture index of a new emitter is Rnd(64) and cycles through 64 values; the seed is Rnd(65536).
    public const int TextureIndexCount = 64;
    public const int SeedRange = 65536;
    // The render key: 12 bits of depth (0-4095) over the camera's near-to-far range.
    public const float DepthSteps = 4096f;
    public const uint MaximumDepth = 4095u;
    public const uint TransparentKeyBits = 0x30000000u;
    // Visibility spheres are stored in 1/32 units within -1023..1023.
    public const int VisibilitySteps = 32;
    public const int VisibilityLimit = 1023;
}
