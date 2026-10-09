using UnityEngine;

namespace Gh3Skin;

internal sealed class StageParticles : IDisposable
{
    private static readonly int CameraForwardId = Shader.PropertyToID("_Gh3CameraForward");
    private static readonly uint TextureRibbonTrailKey = Gh3Checksum.Value("TextureRibbonTrail");
    private static readonly uint FlatRibbonTrailKey = Gh3Checksum.Value("FlatRibbonTrail");
    private static readonly uint VTypeKey = Gh3Checksum.Value("v");
    private static readonly uint XTypeKey = Gh3Checksum.Value("x");

    private readonly StageScripts _scripts;
    private readonly StageAssets _assets;
    private readonly GameObject _root;
    private readonly Camera _camera;
    private readonly int _layer;
    private readonly Dictionary<(uint Id, bool Linear), Texture2D> _textures = new();
    private readonly AssetBundle _bundle;
    private readonly Shader _shader;
    private readonly StageParticleTechniques _techniques;
    private readonly DepthTextureMode _originalDepthMode;
    private readonly List<Material> _materials = new();
    private readonly Dictionary<(uint Emitter, uint Texture, StageParticleMaterial Material, bool Lit), List<StageEffects.ParticleBatch>> _batches = new();
    private readonly Dictionary<uint, StageParticleLightSet> _lighting = new();
    public IReadOnlyDictionary<uint, StageParticleLightSet> Lighting => _lighting;
    private readonly StageTeslaRenderer _tesla;
    public int ActiveParticles { get; private set; }
    public uint[] RenderedEmitters { get; private set; } = Array.Empty<uint>();
    public uint[] DrawOrder { get; private set; } = Array.Empty<uint>();
    public Dictionary<uint, uint> RenderKeys { get; } = new();
    public int RenderedQuads { get; private set; }

    public StageParticles(StageScripts scripts, StageAssets assets, GameObject root, Camera camera, int layer, string shaderPath)
    {
        _scripts = scripts;
        _assets = assets;
        _root = root;
        _camera = camera;
        _layer = layer;
        _techniques = StageParticleTechniques.Load(Path.Combine(Path.GetDirectoryName(shaderPath)!, "particle-materials.json"));
        _originalDepthMode = camera.depthTextureMode;
        _bundle = AssetBundle.LoadFromFile(shaderPath) ?? throw new InvalidDataException("Stage particle shader bundle did not load");
        try
        {
            _shader = StageShader.Load(_bundle, "the stage particle shader");
            _tesla = new(assets, root, camera, layer, Path.GetDirectoryName(shaderPath)!);
            camera.depthTextureMode |= DepthTextureMode.Depth;
        }
        catch { _bundle.Unload(true); throw; }
    }

    public void Update(double time)
    {
        System.Numerics.Vector3 Numeric(Vector3 value) => new(value.x, value.y, value.z);
        System.Numerics.Vector3 Reflect(System.Numerics.Vector3 value) => new(value.X, value.Y, -value.Z);
        var right = Numeric(_root.transform.InverseTransformDirection(_camera.transform.right));
        var up = Numeric(_root.transform.InverseTransformDirection(_camera.transform.up));
        var mark = System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string name)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameProfile.Add(name, (now - mark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            mark = now;
        }
        ActiveParticles = 0;
        _lighting.Clear();
        var bound = new HashSet<Material>();
        var forward = _camera.transform.forward;
        var cameraForward = new Vector4 { x = forward.x, y = forward.y, z = forward.z };
        foreach (var material in _materials) material.SetVector(CameraForwardId, cameraForward);
        foreach (var batch in _batches.Values.SelectMany(batches => batches)) batch.Begin(_camera.transform.right, _camera.transform.up);
        var emitters = _scripts.State.Particles.Emitters;
        RenderedEmitters = StageParticleRenderList.Select(emitters.Keys, _ => true, id => emitters[id].Live.Count > 0).ToArray();
        RenderKeys.Clear();
        var eye = Numeric(_root.transform.InverseTransformPoint(_camera.transform.position));
        var distanceScale = StageParticleRenderOrder.DistanceScale(_camera.nearClipPlane, _camera.farClipPlane);
        foreach (var id in RenderedEmitters)
        {
            var material = _scripts.State.Particles.Emitters[id].Definition.Material;
            var technique = _techniques.Select(material);
            RenderKeys.Add(id, StageParticleRenderOrder.Key(material.RenderSortGroup, material.RenderDrawOrder,
                material.RenderBlend != 0 || (technique.Flags & 0x800) != 0, technique.SortId, _scripts.State.Particles.Position(id, time), eye, distanceScale));
        }
        DrawOrder = StageParticleRenderOrder.Sort(RenderedEmitters.Select(id => RenderKeys[id]).ToArray()).Select(index => RenderedEmitters[index]).ToArray();

        Mark("p.order");
        var submission = new List<StageEffects.ParticleBatch>();
        var current = new Dictionary<(uint Emitter, uint Texture, bool Lit), StageEffects.ParticleBatch>();
        var submitted = new HashSet<StageEffects.ParticleBatch>();
        (uint, uint, bool) lastKey = default;
        StageEffects.ParticleBatch? lastBatch = null;
        StageEffects.ParticleBatch ParticleBatch(uint texture, in StageParticleSample particle, StageParticleAxes lighting, bool allowLighting = true)
        {
            var definition = particle.Definition;
            var lit = definition.Material.Lighting && allowLighting;
            var key = (particle.Emitter, texture, lit);
            StageEffects.ParticleBatch batch;
            if (lastBatch != null && key == lastKey && !lastBatch.Full) batch = lastBatch;
            else
            {
                if (!current.TryGetValue(key, out batch!) || batch.Full) current[key] = batch = Batch(particle.Emitter, texture, definition, lit);
                if (submitted.Add(batch)) submission.Add(batch);
                lastKey = key;
                lastBatch = batch;
            }
            if (lit && bound.Add(batch.Material))
            {
                if (!_lighting.TryGetValue(particle.Emitter, out var lights))
                {
                    var emitter = _scripts.State.Particles.Emitters[particle.Emitter];
                    lights = _scripts.State.LightScene.Sample(Reflect(_scripts.State.Particles.Position(emitter.Id, time)), emitter.LightGroup).Lighting;
                    _lighting.Add(emitter.Id, lights);
                }
                BindLighting(batch.Material, lights);
            }
            batch.SoftEdgeWidth = particle.Size.X;
            batch.LightNormal = (Reflect(lighting.Depth));
            batch.LightTangent = (Reflect(lighting.Right));
            batch.LightBitangent = (Reflect(lighting.Up));
            return batch;
        }
        foreach (var source in DrawOrder.SelectMany(id => emitters[id].Live))
        {
            var particle = StageParticleState.Sample(source);
            var definition = particle.Definition;
            var p = particle.Position;
            var c = particle.Color;
            var lighting = StageParticleGeometry.FlatLighting(Reflect(particle.Velocity), Reflect(right), Reflect(up), definition.AlignWithPath,
                definition.PathFollowTime, particle.Angle, definition.Rotate3d && definition.RotationSpeed != System.Numerics.Vector2.Zero,
                particle.RandomIndex, particle.ColorMix, definition.AlignTo);
            ActiveParticles++;
            if (definition.Type == TextureRibbonTrailKey)
            {
                foreach (var quad in StageParticleGeometry.Ribbon(particle.Position, particle.Size.X, particle.History, particle.HistoryIndex,
                    right, up, definition.AtlasColumns, definition.AtlasRows, particle.Frame, -Reflect(definition.AlignTo)))
                {
                    lighting = new(Reflect(quad.Lighting.Right), Reflect(quad.Lighting.Up), -Reflect(quad.Lighting.Depth));
                    ParticleBatch(definition.Texture, particle, lighting).AddQuadVertices((quad.A), (quad.B), (quad.C), (quad.D), new(c.X, c.Y, c.Z, c.W), new(quad.Uv.X, quad.Uv.W, quad.Uv.Z, quad.Uv.Y));
                }
                continue;
            }
            if (definition.Type == FlatRibbonTrailKey)
                foreach (var quad in StageParticleGeometry.FlatRibbon(particle.Position, particle.Size.X, particle.History, particle.HistoryIndex, right, up, -Reflect(definition.AlignTo)))
                    ParticleBatch(0, particle, lighting, false).AddQuadVertices((quad.A), (quad.B), (quad.C), (quad.D),
                        new(c.X, c.Y, c.Z, StageParticleGeometry.TrailAlpha(c.W, quad.StartAlpha)),
                        new(c.X, c.Y, c.Z, StageParticleGeometry.TrailAlpha(c.W, quad.EndAlpha)), new(quad.Uv.X, quad.Uv.W, quad.Uv.Z, quad.Uv.Y));
            if (definition.Type == VTypeKey || definition.Type == XTypeKey)
            {
                var split = definition.Type == XTypeKey;
                var axes = StageParticleGeometry.CrossBasis(new(particle.Size.X, particle.Size.Y, particle.Depth), Reflect(particle.Velocity),
                    Reflect(right), Reflect(up), definition.AlignWithPath, particle.Angle, split,
                    definition.Rotate3d && definition.RotationSpeed != System.Numerics.Vector2.Zero, particle.RandomIndex, particle.ColorMix, definition.AlignTo);
                if (axes.Visible)
                {
                    var lightAxes = StageParticleGeometry.CrossBasis(new(2, 2, 2), Reflect(particle.Velocity), Reflect(right), Reflect(up), definition.AlignWithPath,
                        particle.Angle, split, definition.Rotate3d && definition.RotationSpeed != System.Numerics.Vector2.Zero, particle.RandomIndex, particle.ColorMix, definition.AlignTo);
                    var side = 0;
                    foreach (var quad in StageParticleGeometry.Cross(Reflect(particle.Position), axes.Right, axes.Up, axes.Depth, split,
                         StageParticleGeometry.TextureBounds(definition.AtlasColumns, definition.AtlasRows, particle.Frame)))
                    {
                        lighting = StageParticleGeometry.CrossLighting(lightAxes, split, side++);
                        ParticleBatch(definition.Texture, particle, lighting).AddQuadVertices((Reflect(quad.A)), (Reflect(quad.B)), (Reflect(quad.C)), (Reflect(quad.D)),
                            new(c.X, c.Y, c.Z, c.W), new(quad.Uv.X, quad.Uv.W, quad.Uv.Z, quad.Uv.Y));
                    }
                }
                continue;
            }
            var basis = StageParticleGeometry.Basis(particle.Size, particle.Velocity, right, up, definition.AlignWithPath, definition.PathFollowTime, particle.Angle);
            if (!definition.AlignWithPath && (definition.AlignTo != System.Numerics.Vector3.Zero || definition.Rotate3d))
            {
                var axes = StageParticleGeometry.CrossBasis(new(particle.Size.X, particle.Size.Y, 0), Reflect(particle.Velocity), Reflect(right), Reflect(up), false,
                    particle.Angle, false, definition.Rotate3d && definition.RotationSpeed != System.Numerics.Vector2.Zero, particle.RandomIndex, particle.ColorMix, definition.AlignTo);
                basis = new(Reflect(axes.Right), Reflect(axes.Up));
            }
            if (!basis.Visible) continue;
            var uv = StageParticleGeometry.TextureBounds(definition.AtlasColumns, definition.AtlasRows, particle.Frame);
            ParticleBatch(definition.Texture, particle, lighting).AddQuad(p, basis.Right, basis.Up, c, uv);
        }
        Mark("p.build");
        foreach (var batch in _batches.Values.SelectMany(batches => batches)) batch.End();
        Mark("p.upload");
        for (var index = 0; index < submission.Count; index++) submission[index].SortingOrder = index;
        RenderedQuads = _batches.Values.Sum(batches => batches.Sum(batch => batch.Count));
        _tesla.Update(_scripts.State.Tesla, time);
        Mark("p.tesla");
    }

    private StageEffects.ParticleBatch Batch(uint emitter, uint textureId, StageParticleDefinition particle, bool lit)
    {
        var definition = particle.Material;
        var quads = particle.Type == TextureRibbonTrailKey || particle.Type == FlatRibbonTrailKey ? particle.History + 1 :
            particle.Type == XTypeKey ? 4 : particle.Type == VTypeKey ? 2 : 1;
        var capacity = (int)Math.Clamp((long)particle.Maximum * quads, 1, 8192);
        var key = (emitter, textureId, definition, lit);
        if (!_batches.TryGetValue(key, out var batches))
        {
            var reusable = _batches.Keys.FirstOrDefault(value => value.Texture == textureId && value.Material == definition && value.Lit == lit &&
                !_scripts.State.Particles.Exists(value.Emitter));
            if (reusable.Material != null)
            {
                batches = _batches[reusable];
                _batches.Remove(reusable);
            }
            else batches = new();
            _batches.Add(key, batches);
        }
        foreach (var batch in batches) if (!batch.Full && batch.Capacity >= capacity) return batch;
        var texture = Texture(textureId, false);
        var material = _assets.Own(new Material(_shader));
        Configure(material, definition);
        if (lit)
        {
            material.SetVector("_Gh3Lighting", new(.4f, 1, .5f, 1));
            material.SetVector("_Gh3AmbientWeights", new(1, 0, 0, 0));
            material.SetVector("_Gh3Maps", new(definition.NormalMap != 0 ? 1 : 0, definition.SpecularPower != 0 ? 1 : 0,
                definition.SpecularTexture != 0 ? 1 : 0, definition.SpecularPower));
            if (definition.NormalMap != 0) material.SetTexture("_Gh3NormalTex", Texture(definition.NormalMap, true));
            if (definition.SpecularTexture != 0) material.SetTexture("_Gh3SpecularTex", Texture(definition.SpecularTexture, true));
        }
        var forward = _camera.transform.forward;
        material.SetVector("_Gh3CameraForward", new(forward.x, forward.y, forward.z, 0));
        _materials.Add(material);
        var created = new StageEffects.ParticleBatch(_assets, _root, _layer, _shader, texture, definition.Blend == Gh3Checksum.Value("add"), capacity,
            "GH3 Source Particles " + textureId.ToString("x8"), sourceMaterial: material);
        created.Begin(_camera.transform.right, _camera.transform.up);
        batches.Add(created);
        return created;
    }

    private Texture2D Texture(uint textureId, bool linear)
    {
        if (!_textures.TryGetValue((textureId, linear), out var texture))
        {
            texture = _assets.Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, linear));
            if (textureId == 0)
            {
                for (var x = 0; x < 2; x++) for (var y = 0; y < 2; y++) texture.SetPixel(x, y, Color.white);
                texture.Apply(false, true);
            }
            else
            {
                if (!_scripts.TexturePaths.TryGetValue(textureId, out var file)) throw new InvalidDataException("Particle texture is missing: " + Gh3QbText.Key(textureId));
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(file), true)) throw new InvalidDataException("Invalid particle texture: " + file);
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            _textures.Add((textureId, linear), texture);
        }
        return texture;
    }

    private void BindLighting(Material material, StageParticleLightSet lights)
    {
        Vector3 Direction(float x, float y, float z) => _root.transform.TransformDirection(new(x, y, -z));
        var direction = Direction(lights.Direction.X, lights.Direction.Y, lights.Direction.Z);
        material.SetVector("_Gh3LightDirection", new(direction.x, direction.y, direction.z, lights.Direction.W));
        material.SetVector("_Gh3LightColor", new(lights.Color.X, lights.Color.Y, lights.Color.Z, lights.Color.W));
        var h = lights.Harmonics;
        material.SetVector("_Gh3SH0", new(h[0].X, h[0].Y, h[0].Z, h[0].W));
        var red = Direction(h[1].X, h[2].X, h[3].X);
        var green = Direction(h[1].Y, h[2].Y, h[3].Y);
        var blue = Direction(h[1].Z, h[2].Z, h[3].Z);
        var alpha = Direction(h[1].W, h[2].W, h[3].W);
        material.SetVector("_Gh3SH1", new(red.x, green.x, blue.x, alpha.x));
        material.SetVector("_Gh3SH2", new(red.y, green.y, blue.y, alpha.y));
        material.SetVector("_Gh3SH3", new(red.z, green.z, blue.z, alpha.z));
        var eye = _camera.transform.position;
        material.SetVector("_Gh3Eye", new(eye.x, eye.y, eye.z, 1));
    }

    public static void Configure(Material material, StageParticleMaterial definition)
    {
        ConfigureBlend(material, definition.PackedBlend);
        material.SetInt("_Cull", definition.TwoSide ? 0 : 2);
        material.SetVector("_Gh3Particle", new(definition.Burn ? definition.BurnValue : 0, (byte)definition.AlphaCutoff / 255f, 1,
            !definition.Burn && definition.AlphaCutoff > 0 ? 1 : 0));
        material.SetVector("_Gh3SoftEdge", new(definition.SoftEdge ? 1 : 0, definition.SoftEdgeScale, 0, 0));
        material.renderQueue = 3000;
    }

    public static void ConfigureBlend(Material material, uint packed)
    {
        static int Factor(uint value) => value switch { 1 => 0, 2 => 1, 3 => 3, 4 => 7, 5 => 5, 6 => 10, 7 => 6, 8 => 8, 9 => 2, 10 => 4, 11 => 9, _ => throw new InvalidDataException("Invalid particle blend factor") };
        material.SetInt("_SrcBlend", Factor(packed & 31));
        material.SetInt("_DstBlend", Factor((packed >> 8) & 255));
        material.SetInt("_SrcBlendAlpha", Factor((packed >> 16) & 31));
        material.SetInt("_DstBlendAlpha", Factor(packed >> 24));
        material.SetInt("_BlendOp", (int)((packed >> 5) & 7) - 1);
        material.SetInt("_BlendOpAlpha", (int)((packed >> 21) & 7) - 1);
    }

    public void Dispose()
    {
        _camera.depthTextureMode = _originalDepthMode;
        _tesla.Dispose();
        _bundle.Unload(true);
    }
}
