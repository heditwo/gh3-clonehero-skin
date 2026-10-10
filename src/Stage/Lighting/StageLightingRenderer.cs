using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Vec = System.Numerics.Vector3;

namespace Gh3Skin;

internal sealed class StageLightingRenderer
{
    private const int BeamBounds = 200;
    // Beams below this density are off.
    private const float MinimumBeamDensity = 0.001f;
    // Tuned: the opacity of a beam at density 1.
    private const float BeamOpacity = 0.08f;
    private sealed class Surface
    {
        public StageAssets.LitMesh Source = null!;
        public int[] Lights = Array.Empty<int>();
        public Vec[] Positions = Array.Empty<Vec>();
        public Vec[] Normals = Array.Empty<Vec>();
        public Vec[] Lighting = Array.Empty<Vec>();
        public float[]?[] Weights = Array.Empty<float[]?>();
        public Il2CppStructArray<Color> Colors = null!;
        public Matrix4x4 Matrix;
    }
    private sealed class BandSurface
    {
        public StageAssets.LitBandMesh Source = null!;
        public Mesh Pose = null!;
        public Il2CppStructArray<Vector3> Positions = null!;
        public Il2CppStructArray<Vector3> Normals = null!;
        public Il2CppSystem.Array PositionBuffer = null!;
        public Il2CppSystem.Array NormalBuffer = null!;
        public Il2CppStructArray<Color> Colors = null!;
    }
    private sealed class ActorLights
    {
        public int[] Ambient = Array.Empty<int>();
        public int[] Directional = Array.Empty<int>();
        public StageLightSample[] Samples = Array.Empty<StageLightSample>();
        public Vec Fill;
        public int Count;
    }
    private readonly StageLighting _lighting;
    private readonly StageFixtures? _fixtures;
    private readonly List<Surface> _surfaces = new();
    private readonly List<BandSurface> _bandMeshes = new();
    private readonly Dictionary<string, ActorLights> _actorLights = new();
    private readonly List<(GameObject Object, Mesh Mesh, Material Material, Il2CppStructArray<Vector3> Vertices)> _beams = new();
    private StageLightingFrame? _frame;
    private float _amount = -1;
    private readonly Dictionary<int, StageBeamValue> _beamValues = new();
    private bool[] _lightVisibility = Array.Empty<bool>();
    private StageLightValue[] _lightValues = Array.Empty<StageLightValue>();
    private string[] _lightGroups;
    private StageLightPose?[] _lightPoses;
    private readonly GameObject _root;
    private readonly List<(StageAssets.ShadedSurface Surface, MaterialPropertyBlock Properties, Vec Center)> _shaded;
    private readonly Dictionary<string, uint> _nativeGroups = new();

    public StageLightingRenderer(StageLighting lighting, StageAssets assets, GameObject root, int layer, StageFixtures? fixtures = null)
    {
        _lighting = lighting;
        _fixtures = fixtures;
        _root = root;
        _shaded = assets.ShadedSurfaces.Select(surface => (surface, new MaterialPropertyBlock(), new Vec(float.NaN))).ToList();
        if (_shaded.Count > 0)
        {
            _lightData = assets.Own(new Texture2D(7, _shaded.Count, TextureFormat.RGBAHalf, false, true));
            _lightData.filterMode = FilterMode.Point;
            _lightData.wrapMode = TextureWrapMode.Clamp;
            _lightRows = new System.Numerics.Vector4[7 * _shaded.Count];
            _lightUpload = new Il2CppStructArray<Color>(_lightRows.Length);
            foreach (var material in _shaded.Select(entry => entry.Surface.Renderer.sharedMaterial).Distinct()) material.SetTexture(LightDataId, _lightData);
            for (var i = 0; i < _shaded.Count; i++)
            {
                var (surface, properties, _) = _shaded[i];
                surface.Renderer.GetPropertyBlock(properties);
                properties.SetVector(LightIndexId, new Vector4 { x = i });
                properties.SetTexture(LightDataId, _lightData);
                surface.Renderer.SetPropertyBlock(properties);
            }
        }
        _lightGroups = lighting.Bank.Lights.Select(light => light.Group).ToArray();
        _lightPoses = new StageLightPose?[lighting.Bank.Lights.Length];
        foreach (var source in assets.LitMeshes)
        {
            var count = source.Positions.Length;
            var surface = new Surface { Source = source,
                Lights = Enumerable.Range(0, lighting.Bank.Lights.Length).Where(i => lighting.Bank.Lights[i].Group == source.Group).ToArray(),
                Positions = source.Positions.Select(Numeric).ToArray(), Normals = source.Normals.Select(n => n.sqrMagnitude > 0.000001f ? Vec.Normalize(Numeric(n)) : Vec.UnitY).ToArray(),
                Lighting = new Vec[count], Colors = new Il2CppStructArray<Color>(count), Matrix = source.Transform.localToWorldMatrix };
            surface.Weights = new float[]?[surface.Lights.Length];
            for (var slot = 0; slot < surface.Lights.Length; slot++)
            {
                var i = surface.Lights[slot];
                var range = lighting.Bank.Snapshots.First().Value.Lights[i];
                if (lighting.Bank.Snapshots.Values.Any(s => s.Lights[i].Near != range.Near || s.Lights[i].Far != range.Far)) continue;
                var weights = new float[count];
                var value = new StageLightValue(Vec.Zero, range.Near, range.Far);
                for (var v = 0; v < count; v++)
                    weights[v] = StageLighting.Weight(lighting.Bank.Lights[i], value, surface.Positions[v], surface.Normals[v]);
                surface.Weights[slot] = weights;
            }
            _surfaces.Add(surface);
        }
        foreach (var source in assets.BandMeshes)
        {
            if (!_actorLights.ContainsKey(source.Group))
            {
                var lights = Enumerable.Range(0, lighting.Bank.Lights.Length).Where(i => lighting.Bank.Lights[i].Group == source.Group).ToArray();
                _actorLights[source.Group] = new() { Ambient = lights.Where(i => lighting.Bank.Lights[i].Type == "Ambient").ToArray(),
                    Directional = lights.Where(i => lighting.Bank.Lights[i].Type != "Ambient").ToArray(), Samples = new StageLightSample[lights.Length] };
            }
            var surface = new BandSurface { Source = source, Pose = assets.Own(new Mesh { name = "GH3 Band Lighting Pose" }),
                Positions = new(source.Colors.Length), Normals = new(source.Colors.Length), Colors = new(source.Colors.Length) };
            surface.PositionBuffer = new(surface.Positions.Pointer);
            surface.NormalBuffer = new(surface.Normals.Pointer);
            _bandMeshes.Add(surface);
        }
        foreach (var id in lighting.BeamIds)
        {
            var obj = new GameObject("GH3 Authored Beam " + id) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            var mesh = assets.Own(new Mesh { name = obj.name });
            mesh.MarkDynamic();
            // A beam is an open cone of 12 sides: a ring at the lens and a ring at the far end, two triangles per side.
            const int sides = 12;
            var vertices = new Il2CppStructArray<Vector3>(2 * sides);
            var colors = new Color[2 * sides];
            var triangles = new int[6 * sides];
            for (var side = 0; side < sides; side++)
            {
                colors[side] = Color.white;
                colors[side + 12] = new(1, 1, 1, 0);
                var next = (side + 1) % 12;
                var at = side * 6;
                triangles[at] = side; triangles[at + 1] = next; triangles[at + 2] = side + 12;
                triangles[at + 3] = next; triangles[at + 4] = next + 12; triangles[at + 5] = side + 12;
            }
            mesh.vertices = vertices;
            mesh.colors = new Il2CppStructArray<Color>(colors);
            mesh.triangles = new Il2CppStructArray<int>(triangles);
            // Larger than any stage, so that Unity never culls a beam.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * BeamBounds);
            var material = assets.Own(StageAssets.MakeMaterial(Shader.Find("Particles/Standard Unlit"), true, true));
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _beams.Add((obj, mesh, material, vertices));
        }
        MelonLoader.MelonLogger.Msg($"GH3 authored lighting: {lighting.Bank.Lights.Length} lights, {lighting.Bank.Snapshots.Count} snapshots, {_surfaces.Count} grouped meshes");
    }

    public void Update(double time)
    {
        var mark = System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string name)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameProfile.Add(name, (now - mark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            mark = now;
        }
        var scene = _fixtures?.Scripts?.LightScene is { DrivesLighting: true } native ? native : null;
        var poses = _lighting.Bank.Lights.Select(light => light.Object.Length > 0 ? scene?.RenderPose(Gh3QbText.Key(light.Object)) : null).ToArray();
        var posesChanged = !poses.SequenceEqual(_lightPoses);
        if (posesChanged)
        {
            _lightPoses = poses;
            foreach (var surface in _surfaces) Array.Clear(surface.Weights, 0, surface.Weights.Length);
        }
        var groups = _lighting.Bank.Lights.Select(light => scene != null && light.Object.Length > 0 && scene.Contains(Gh3QbText.Key(light.Object)) ?
            scene.Group(Gh3QbText.Key(light.Object)).ToString("x8") : light.Group).ToArray();
        var groupsChanged = !groups.SequenceEqual(_lightGroups);
        if (groupsChanged)
        {
            _lightGroups = groups;
            foreach (var surface in _surfaces)
            {
                surface.Lights = Enumerable.Range(0, groups.Length).Where(index => groups[index] == surface.Source.Group).ToArray();
                surface.Weights = new float[]?[surface.Lights.Length];
            }
            foreach (var (group, actorLights) in _actorLights)
            {
                var indices = Enumerable.Range(0, groups.Length).Where(index => groups[index] == group).ToArray();
                actorLights.Ambient = indices.Where(index => _lighting.Bank.Lights[index].Type == "Ambient").ToArray();
                actorLights.Directional = indices.Where(index => _lighting.Bank.Lights[index].Type != "Ambient").ToArray();
                actorLights.Samples = new StageLightSample[indices.Length];
            }
        }
        Mark("l.prepare");
        var frame = _lighting.At(time);
        var amount = frame.Amount(time);
        var state = frame.Sample(time);
        var visibility = _lighting.Bank.Lights.Select(light => light.Object.Length == 0 || _fixtures?.Scripts is not { } scripts ||
            !scripts.Objects.TryGetValue(Gh3QbText.Key(light.Object), out var obj) || obj.Exists && obj.Visible).ToArray();
        var visibilityChanged = !visibility.SequenceEqual(_lightVisibility);
        _lightVisibility = visibility;
        for (var i = 0; i < state.Lights.Length; i++)
        {
            if (scene != null && _lighting.Bank.Lights[i].Object is { Length: > 0 } id)
                state.Lights[i] = scene.Apply(Gh3QbText.Key(id), state.Lights[i]);
            if (!visibility[i]) state.Lights[i] = state.Lights[i] with { Intensity = 0, Specular = 0 };
        }
        Mark("l.sample");
        ShadeBand(state);
        Mark("l.band");
        ShadeSurfaces(state, scene);
        Mark("l.surfaces");
        var lightChanged = _frame != frame || _amount != amount || visibilityChanged || groupsChanged || posesChanged || !state.Lights.SequenceEqual(_lightValues);
        _lightValues = state.Lights;
        foreach (var surface in _surfaces)
        {
            var matrix = surface.Source.Transform.localToWorldMatrix;
            var moved = matrix != surface.Matrix;
            if (moved)
            {
                surface.Matrix = matrix;
                var normalMatrix = matrix.inverse.transpose;
                for (var v = 0; v < surface.Positions.Length; v++)
                {
                    surface.Positions[v] = Numeric(matrix.MultiplyPoint3x4(surface.Source.Positions[v]));
                    var normal = Numeric(normalMatrix.MultiplyVector(surface.Source.Normals[v]));
                    surface.Normals[v] = normal.LengthSquared() > 0.000001f ? Vec.Normalize(normal) : Vec.UnitY;
                }
                Array.Clear(surface.Weights, 0, surface.Weights.Length);
            }
            if (!lightChanged && !moved) continue;
            Shade(surface, state, surface.Lighting);
            var colors = surface.Colors.AsSpan();
            for (var v = 0; v < colors.Length; v++)
            {
                var rgb = surface.Lighting[v];
                colors[v] = new Color { r = rgb.X, g = rgb.Y, b = rgb.Z, a = surface.Source.Colors[v].a / 255f };
            }
            surface.Source.Mesh.colors = surface.Colors;
        }
        Mark("l.legacy");
        for (var i = 0; i < _beams.Count; i++)
        {
            var value = _fixtures?.Resolve(_lighting.BeamIds[i], state.Beams[i], time) ?? state.Beams[i];
            _fixtures?.Update(_lighting.BeamIds[i], value);
            var beam = _beams[i];
            beam.Object.SetActive(value.Density > MinimumBeamDensity);
            if (value.Density <= MinimumBeamDensity) continue;
            beam.Object.transform.localPosition = Unity(value.Position);
            var q = value.Rotation;
            beam.Object.transform.localRotation = new(q.X, q.Y, q.Z, q.W);
            if (_beamValues.TryGetValue(i, out var previousBeam) && previousBeam == value) continue;
            _beamValues[i] = value;
            for (var side = 0; side < 12; side++)
            {
                var angle = side * MathF.PI / 6;
                var x = MathF.Cos(angle);
                var y = MathF.Sin(angle);
                beam.Vertices[side] = new Vector3(x * value.StartRadius, y * value.StartRadius, 0);
                beam.Vertices[side + 12] = new Vector3(x * value.EndRadius, y * value.EndRadius, value.Range);
            }
            beam.Mesh.vertices = beam.Vertices;
            beam.Material.color = new(value.Color.X, value.Color.Y, value.Color.Z, value.Density * BeamOpacity);
        }
        Mark("l.beams");
        _frame = frame;
        _amount = amount;
    }

    private void ShadeBand(StageLightingValues state)
    {
        foreach (var group in _actorLights.Values)
        {
            group.Fill = group.Samples.Length == 0 ? Vec.One : Vec.Zero;
            foreach (var i in group.Ambient) group.Fill += state.Lights[i].Energy;
            group.Count = 0;
            foreach (var i in group.Directional)
                if (state.Lights[i].Energy != Vec.Zero)
                    group.Samples[group.Count++] = new(_lighting.Bank.Lights[i], state.Lights[i], _lightPoses[i]);
        }
        foreach (var surface in _bandMeshes)
        {
            var source = surface.Source;
            var light = _actorLights[source.Group];
            source.Renderer.BakeMesh(surface.Pose);
            surface.Pose.GetArrayFromChannelImpl(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, surface.PositionBuffer);
            surface.Pose.GetArrayFromChannelImpl(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, surface.NormalBuffer);
            var vertices = surface.Positions.AsSpan();
            var normals = surface.Normals.AsSpan();
            var colors = surface.Colors.AsSpan();
            var transform = source.Renderer.transform;
            var offset = Numeric(transform.position);
            var q = transform.rotation;
            var rotation = new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
            for (var v = 0; v < colors.Length; v++)
            {
                var position = Vec.Transform(Numeric(vertices[v]), rotation) + offset;
                var normal = Vec.Transform(Numeric(normals[v]), rotation);
                normal = normal.LengthSquared() > 0.000001f ? Vec.Normalize(normal) : Vec.UnitY;
                var energy = light.Fill;
                for (var i = 0; i < light.Count; i++)
                    energy += light.Samples[i].Energy * light.Samples[i].Weight(position, normal);
                var vertex = source.Colors[v];
                colors[v] = new Color { r = energy.X * vertex.r / 255f, g = energy.Y * vertex.g / 255f, b = energy.Z * vertex.b / 255f, a = vertex.a / 255f };
            }
            source.Renderer.sharedMesh.colors = surface.Colors;
        }
    }

    private void Shade(Surface surface, StageLightingValues state, Vec[] output)
    {
        for (var v = 0; v < output.Length; v++)
        {
            var color = surface.Lights.Length == 0 ? Vec.One : Vec.Zero;
            for (var slot = 0; slot < surface.Lights.Length; slot++)
            {
                var i = surface.Lights[slot];
                if (!_lightVisibility[i]) continue;
                var weight = surface.Weights[slot] is { } weights ? weights[v] :
                    StageLighting.Weight(_lighting.Bank.Lights[i], state.Lights[i], surface.Positions[v], surface.Normals[v], _lightPoses[i]);
                color += state.Lights[i].Energy * weight;
            }
            var vertex = surface.Source.Colors[v];
            output[v] = color * new Vec(vertex.r / 255f, vertex.g / 255f, vertex.b / 255f);
        }
    }

    private uint NativeGroup(StageLightScene scene, string label)
    {
        if (_nativeGroups.Count == 0)
            foreach (var group in scene.Groups.Keys.Append(Gh3Checksum.Value("None")))
            {
                var name = group == Gh3Checksum.Value("None") ? "Stage" : group == Gh3Checksum.Value("Band") ? "Band" :
                    group == Gh3Checksum.Value("Crowd") ? "Crowd" : $"group-{group:x8}";
                _nativeGroups[Gh3Checksum.Of(name)] = group;
            }
        return _nativeGroups.TryGetValue(label, out var native) ? native : Gh3Checksum.Value("None");
    }

    private StageParticleLightSet PackLights(StageLightingValues state, Vec position, string group)
    {
        var lights = new List<StageParticleLight>();
        for (var i = 0; i < state.Lights.Length; i++)
        {
            var definition = _lighting.Bank.Lights[i];
            if (definition.Group != group || !_lightVisibility[i]) continue;
            var value = state.Lights[i];
            var pose = _lightPoses[i];
            var location = pose?.Position ?? definition.Location;
            var forward = pose?.Forward ?? definition.Forward;
            var far = value.Far > 0 ? value.Far : 1000;
            // GH3: shader light types (5 ambient, 1 directional, 2 point), and the falloff guard of StageLightNative.
            lights.Add(new(definition.Type switch { "Ambient" => 5, "Dir" => 1, _ => 2 }, new(value.Color, 1), new(location, 1), new(forward, 0),
                value.Intensity, 1, 1, far * far, 1 / (far * far - value.Near * value.Near + .0001f), 2, new System.Numerics.Vector4[9]));
            if (lights.Count == 32) break;
        }
        return StageParticleLights.Select(lights, new(position, 1));
    }

    private static readonly int LightDataId = Shader.PropertyToID("_Gh3LightData");
    private static readonly int LightIndexId = Shader.PropertyToID("_Gh3LightIndex");
    private readonly Texture2D? _lightData;
    private readonly System.Numerics.Vector4[] _lightRows = Array.Empty<System.Numerics.Vector4>();
    private readonly Il2CppStructArray<Color>? _lightUpload;
    private long _lightSignature = long.MinValue;

    private long LightSignature(StageLightingValues state, StageLightScene? scene)
    {
        if (scene != null) return scene.Signature();
        var hash = new HashCode();
        foreach (var light in state.Lights) hash.Add(light);
        foreach (var visible in _lightVisibility) hash.Add(visible);
        foreach (var pose in _lightPoses) hash.Add(pose);
        return hash.ToHashCode();
    }

    private void ShadeSurfaces(StageLightingValues state, StageLightScene? scene)
    {
        if (_shaded.Count == 0) return;
        var signature = LightSignature(state, scene);
        var lightsChanged = signature != _lightSignature;
        _lightSignature = signature;
        var root = _root.transform;
        var q = root.rotation;
        var rotation = new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
        var origin = root.position;
        var inverse = System.Numerics.Quaternion.Inverse(rotation);
        var native = scene != null;
        Vec Direction(float x, float y, float z) => Vec.Transform(new Vec(x, y, native ? -z : z), rotation);
        var changed = false;
        for (var i = 0; i < _shaded.Count; i++)
        {
            var (surface, properties, center) = _shaded[i];
            var local = center;
            if (surface.LightAt is { } fixedAt) local = fixedAt;
            else if (surface.Dynamic || float.IsNaN(center.X))
            {
                var world = surface.Renderer.bounds.center;
                local = Vec.Transform(new Vec(world.x - origin.x, world.y - origin.y, world.z - origin.z), inverse);
            }
            if (!lightsChanged && local == center) continue;
            _shaded[i] = (surface, properties, local);
            var lights = native ? scene!.Sample(new(local.X, local.Y, -local.Z), NativeGroup(scene, surface.Group)).Lighting :
                PackLights(state, local, surface.Group);
            var row = _lightRows.AsSpan(i * 7, 7);
            row[0] = lights.Ambient;
            var direction = Direction(lights.Direction.X, lights.Direction.Y, lights.Direction.Z);
            row[1] = new(direction, 0);
            row[2] = lights.Color;
            var h = lights.Harmonics;
            row[3] = h[0];
            var red = Direction(h[1].X, h[2].X, h[3].X);
            var green = Direction(h[1].Y, h[2].Y, h[3].Y);
            var blue = Direction(h[1].Z, h[2].Z, h[3].Z);
            row[4] = new(red.X, green.X, blue.X, 0);
            row[5] = new(red.Y, green.Y, blue.Y, 0);
            row[6] = new(red.Z, green.Z, blue.Z, 0);
            changed = true;
        }
        if (!changed || _lightData == null || _lightUpload == null) return;
        System.Runtime.InteropServices.MemoryMarshal.Cast<System.Numerics.Vector4, Color>(_lightRows).CopyTo(_lightUpload.AsSpan());
        _lightData.SetPixels(_lightUpload);
        _lightData.Apply(false);
    }

    private static Vec Numeric(Vector3 v) => new(v.x, v.y, v.z);
    private static Vector3 Unity(Vec v) => new(v.X, v.Y, v.Z);
}
