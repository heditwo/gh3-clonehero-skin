using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

// Song effect cues (pyro, sparks, fireworks, smoke, flash) on the stage emitters, and the fallback light beams and
// surface tint of a stage without GH3 lighting. The procedural effects stand in for stages without GH3 particle
// scripts; their shapes are tuned by eye (no GH3 source).
internal sealed class StageEffects
{
    private const int SoftDotSize = 32;
    private const int SparkCapacity = 2048;
    private const int SmokeCapacity = 256;
    private const int FallbackBeams = 8;
    // Effects older than this (seconds) cannot be visible: the longest smoke lasts its duration plus 4 s.
    private const double EffectLookBack = 34;
    // Tuned: default durations (seconds) of effects without a length, the extra time of their particles, and the
    // flash fade.
    private const float DefaultSmokeDuration = 3;
    private const float DefaultEffectDuration = 0.6f;
    private const float ParticleAfterlife = 4;
    private const float FlashFade = 0.35f;
    private const float FlashOverlayAlpha = 0.3f;
    private readonly StageAssets _assets;
    private readonly Camera _camera;
    private readonly Dictionary<string, (Vector3 Position, Quaternion Rotation)[]> _emitters;
    private readonly List<(Transform Transform, Material Material, Vector3 Origin, int Index)> _beams = new();
    private readonly StageParticleBatch _sparks;
    private readonly StageParticleBatch _smoke;
    private readonly StageLightingRenderer? _lighting;
    public int ActiveParticles { get; private set; }
    public bool SourcePyro { get; set; }

    public StageEffects(StageAssets assets, GameObject root, Camera camera, int layer, StageLighting? lighting = null, StageFixtures? fixtures = null,
        IReadOnlyDictionary<string, StagePlacement[]>? emitters = null)
    {
        _assets = assets;
        _camera = camera;
        _emitters = (emitters ?? new Dictionary<string, StagePlacement[]>()).ToDictionary(e => e.Key,
            e => e.Value.Select(p => (StageAssets.Vector(p.Position), new Quaternion(p.Rotation[0], p.Rotation[1], p.Rotation[2], p.Rotation[3]))).ToArray());
        // A soft round dot: alpha falls off with the square of the distance from the center.
        const float radius = (SoftDotSize - 1) / 2f;
        var texture = assets.Own(new Texture2D(SoftDotSize, SoftDotSize, TextureFormat.RGBA32, false));
        var pixels = new Color32[SoftDotSize * SoftDotSize];
        for (var y = 0; y < SoftDotSize; y++)
            for (var x = 0; x < SoftDotSize; x++)
            {
                var distance = MathF.Sqrt(MathF.Pow((x - radius) / radius, 2) + MathF.Pow((y - radius) / radius, 2));
                var alpha = (byte)(255 * MathF.Pow(Math.Max(0, 1 - distance), 2));
                pixels[y * SoftDotSize + x] = new Color32(255, 255, 255, alpha);
            }
        texture.SetPixels32(new Il2CppStructArray<Color32>(pixels));
        texture.Apply(false, true);
        texture.wrapMode = TextureWrapMode.Clamp;
        var shader = Shader.Find("Particles/Standard Unlit");
        _sparks = new StageParticleBatch(assets, root, layer, shader, texture, true, SparkCapacity);
        _smoke = new StageParticleBatch(assets, root, layer, shader, texture, false, SmokeCapacity);
        if (lighting != null)
        {
            _lighting = new StageLightingRenderer(lighting, assets, root, layer, fixtures);
            return;
        }
        var cone = assets.Own(CreateCone());
        for (var i = 0; i < FallbackBeams; i++)
        {
            var obj = new GameObject("GH3 Stage Beam " + i) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            // Tuned: a row of beams 24 units wide, 12 up, 7 back.
            var origin = new Vector3(-12 + i * 24f / 7, 12, 7);
            obj.transform.localPosition = origin;
            obj.AddComponent<MeshFilter>().sharedMesh = cone;
            var material = assets.Own(StageAssets.MakeMaterial(shader, true, true));
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _beams.Add((obj.transform, material, origin, i));
        }
    }

    public void Update(StageTimeline timeline, double time)
    {
        var light = timeline.Lighting(time);
        var flash = 0f;
        _sparks.Begin(_camera.transform.right, _camera.transform.up);
        _smoke.Begin(_camera.transform.right, _camera.transform.up);
        var first = timeline.FirstEffectAfter(time - EffectLookBack);
        for (var i = first; i < timeline.Effects.Length && timeline.Effects[i].Time <= time; i++)
        {
            var cue = timeline.Effects[i];
            if (SourcePyro && cue.Type == "pyro") continue;
            var age = (float)(time - cue.Time);
            var duration = cue.Duration > 0 ? (float)cue.Duration : cue.Type == "smoke" ? DefaultSmokeDuration : DefaultEffectDuration;
            if (age > duration + ParticleAfterlife) continue;
            if (cue.Type is "flash" or "pyro" or "fireworks") flash = Math.Max(flash, Math.Max(0, 1 - age / FlashFade));
            if (_lighting != null && cue.Type == "flash" && age < FlashFade)
            {
                var height = MathF.Tan(_camera.fieldOfView * MathF.PI / 360);
                _sparks.Add(_camera.transform.position + _camera.transform.forward, height * _camera.aspect, height,
                    new Color(1, 1, 1, (1 - age / FlashFade) * FlashOverlayAlpha));
            }
            var emitters = _emitters.GetValueOrDefault(cue.Type, Array.Empty<(Vector3, Quaternion)>());
            for (var emitter = 0; emitter < emitters.Length; emitter++)
            {
                var (origin, rotation) = emitters[emitter];
                var seed = i * PcgHash.CuePrime + emitter * PcgHash.EmitterPrime;
                if (cue.Type == "fireworks") Firework(origin, rotation, age, seed, emitter);
                else if (cue.Type is "pyro" or "sparks") Fountain(origin, rotation, age, duration, seed, cue.Type == "pyro");
                if (cue.Type is "smoke" or "pyro") Smoke(origin, rotation, age, duration, seed, cue.Type == "pyro");
            }
        }
        FrameProfile.Measure("e.lighting", () => _lighting?.Update(time));
        if (_lighting == null) foreach (var material in _assets.SurfaceMaterials)
        {
            var strength = Math.Max(light.R, Math.Max(light.G, light.B));
            // Tuned: ambient base, light strength, light color, and flash weights of the fallback surface tint.
            material.color = new Color(0.035f + strength * 0.32f + light.R * 0.34f + flash * 0.2f,
                0.035f + strength * 0.32f + light.G * 0.34f + flash * 0.12f,
                0.045f + strength * 0.32f + light.B * 0.34f + flash * 0.05f, 1);
        }
        foreach (var (transform, material, origin, index) in _beams)
        {
            // Tuned: the beams sweep a 22 x 8 area on the floor.
            var target = new Vector3(MathF.Sin((float)time * 0.55f + index * 1.7f) * 11, 0,
                -3 + MathF.Cos((float)time * 0.4f + index) * 4);
            var direction = target - origin;
            transform.localRotation = Quaternion.FromToRotation(Vector3.forward, direction.normalized);
            // Tuned: beam width and opacity.
            transform.localScale = new Vector3(1.6f, 1.6f, direction.magnitude);
            // Tuned: beam opacity.
            material.color = new Color(light.R, light.G, light.B, 0.08f);
        }
        _sparks.End();
        _smoke.End();
        ActiveParticles = _sparks.Count + _smoke.Count;
    }

    // Tuned: a spark fountain (80 sparks over the duration, 0.8-2 s lives, 6-13 units/s up, 0.3-1.8 spread, gravity
    // 9 units/s²), and for pyro a flame column (28 puffs, 0.7 s each, rising 8 units/s).
    private const int FountainSparks = 80;
    private const int FlamePuffs = 28;
    private const float FlameLife = 0.7f;

    private void Fountain(Vector3 origin, Quaternion rotation, float age, float duration, int seed, bool flame)
    {
        for (var i = 0; i < FountainSparks; i++)
        {
            // Tuned: the spark random streams, lives, speeds, and spreads.
            var start = Random(seed + i * 13) * duration;
            var t = age - start;
            // Tuned: spark life 0.8-2 s.
            var life = 0.8f + Random(seed + i * 17) * 1.2f;
            if (t < 0 || t > life) continue;
            // Tuned: launch direction.
            var angle = Random(seed + i * 19) * MathF.PI * 2;
            // Tuned: launch speed 6-13 units/s.
            var speed = 6 + Random(seed + i * 23) * 7;
            // Tuned: spread 0.3-1.8.
            var spread = 0.3f + Random(seed + i * 29) * 1.5f;
            // Tuned: half of 9 units/s² of gravity.
            var offset = new Vector3(MathF.Cos(angle) * spread * t, speed * t - 4.5f * t * t, MathF.Sin(angle) * spread * t);
            if (offset.y < 0) continue;
            var position = origin + rotation * offset;
            var fade = 1 - t / life;
            // Tuned: spark size and color (yellow to red as it fades).
            _sparks.Add(position, 0.035f, 0.16f, new Color(1, 0.5f + fade * 0.5f, 0.15f + fade * 0.55f, fade));
        }
        if (!flame) return;
        for (var i = 0; i < FlamePuffs; i++)
        {
            var t = age - i * duration / FlamePuffs;
            if (t < 0 || t > FlameLife) continue;
            // Tuned: puff wobble and rise.
            var position = origin + rotation * new Vector3(MathF.Sin(seed + i) * t * 0.3f, t * 8, 0);
            var fade = 1 - t / FlameLife;
            // Tuned: puff size and color.
            _sparks.Add(position, 0.25f + t * 0.2f, 0.8f, new Color(1, 0.25f + fade * 0.55f, fade * 0.15f, fade));
        }
    }

    // Tuned: a firework rises for 0.7 s at 16 units/s, then bursts into 100 sparks on a Fibonacci sphere (golden angle
    // 2.399963 rad) that live 3 s.
    private const float FireworkRise = 0.7f;
    private const float FireworkLife = 3;
    private const int FireworkSparks = 100;
    private const float GoldenAngle = 2.399963f;

    private void Firework(Vector3 origin, Quaternion rotation, float age, int seed, int emitter)
    {
        if (age < FireworkRise)
        {
            // Tuned: the rocket's speed, size, and color.
            _sparks.Add(origin + rotation * Vector3.up * (age * 16), 0.08f, 0.7f, new Color(1, 0.8f, 0.3f, 1));
            return;
        }
        var t = age - FireworkRise;
        if (t > FireworkLife) return;
        var color = StageColor.Named(emitter % 2 == 0 ? "orange" : "cyan");
        for (var i = 0; i < FireworkSparks; i++)
        {
            var azimuth = i * GoldenAngle;
            var vertical = 1 - 2 * (i + 0.5f) / FireworkSparks;
            var horizontal = MathF.Sqrt(1 - vertical * vertical);
            var direction = new Vector3(MathF.Cos(azimuth) * horizontal, vertical, MathF.Sin(azimuth) * horizontal);
            // Tuned: burst height 11.2, radius 5 with drag 0.9, and fall 1.2 units/s².
            var position = origin + rotation * (Vector3.up * 11.2f + direction * (5 * (1 - MathF.Exp(-t * 0.9f))) + Vector3.down * t * t * 1.2f);
            // Tuned: fade curve.
            var fade = MathF.Pow(1 - t / FireworkLife, 1.5f);
            // Tuned: spark size.
            var size = 0.06f + Random(seed + i) * 0.03f;
            _sparks.Add(position, size, size * 2, new Color(color.R, color.G, color.B, fade));
        }
    }

    // Tuned: 12 smoke puffs over the duration (0.3 s later after pyro), each 4 s long.
    private const int SmokePuffs = 12;
    private const float PyroSmokeDelay = 0.3f;
    private const float SmokeLife = 4;

    private void Smoke(Vector3 origin, Quaternion rotation, float age, float duration, int seed, bool aftermath)
    {
        for (var i = 0; i < SmokePuffs; i++)
        {
            var t = age - i * duration / SmokePuffs - (aftermath ? PyroSmokeDelay : 0);
            if (t < 0 || t > SmokeLife) continue;
            // Tuned: drift, rise, and depth of a puff.
            var position = origin + rotation * new Vector3((Random(seed + i * 31) - 0.5f) * t, t * 1.5f, t * 0.2f);
            // Tuned: fade in over 0.5 s, fade out, peak alpha.
            var alpha = Math.Min(t * 2, 1) * (1 - t / SmokeLife) * 0.35f;
            // Tuned: puff size growth and gray-blue color.
            _smoke.Add(position, 0.4f + t * 0.55f, 0.5f + t * 0.65f, new Color(0.35f, 0.4f, 0.5f, alpha));
        }
    }

    private static float Random(int seed) => PcgHash.Unit(seed);

    private static Mesh CreateCone()
    {
        const int sides = 24;
        const float apexAlpha = 0.65f;
        var vertices = new Vector3[sides + 1];
        var colors = new Color[sides + 1];
        var triangles = new int[sides * 3];
        colors[0] = new Color(1, 1, 1, apexAlpha);
        for (var i = 0; i < sides; i++)
        {
            var angle = i * MathF.PI * 2 / sides;
            vertices[i + 1] = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 1);
            colors[i + 1] = new Color(1, 1, 1, 0);
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = (i + 1) % sides + 1;
        }
        var mesh = new Mesh { name = "GH3 Stage Light Cone" };
        mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
        mesh.colors = new Il2CppStructArray<Color>(colors);
        mesh.triangles = new Il2CppStructArray<int>(triangles);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Particle effects draw after the highway mask (StageHighwayMask.Queue).
    public const int EffectQueue = StageHighwayMask.Queue + 5;
}
