using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Vec = System.Numerics.Vector3;

namespace Gh3Skin;

internal sealed class StageLightingRenderer
{
    private sealed class Surface
    {
        public StageAssets.LitMesh Source = null!;
        public int[] Lights = Array.Empty<int>();
        public Vec[] Positions = Array.Empty<Vec>();
        public Vec[] Normals = Array.Empty<Vec>();
        public Vec[] From = Array.Empty<Vec>();
        public Vec[] To = Array.Empty<Vec>();
        public float[]?[] Weights = Array.Empty<float[]?>();
        public Il2CppStructArray<Color> Colors = null!;
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
    private readonly StageLighting _lighting;
    private readonly StageFixtures? _fixtures;
    private readonly List<Surface> _surfaces = new();
    private readonly List<BandSurface> _bandMeshes = new();
    private readonly int[] _bandLights;
    private readonly int[] _bandAmbient;
    private readonly StageLightSample[] _bandSamples;
    private readonly List<(GameObject Object, Mesh Mesh, Material Material, Il2CppStructArray<Vector3> Vertices)> _beams = new();
    private StageLightingFrame? _frame;
    private float _amount = -1;
    private readonly string _band = Gh3Checksum.Of("Band");

    public StageLightingRenderer(StageLighting lighting, StageAssets assets, GameObject root, int layer, StageFixtures? fixtures = null)
    {
        _lighting = lighting;
        _fixtures = fixtures;
        foreach (var source in assets.LitMeshes)
        {
            var count = source.Positions.Length;
            var surface = new Surface { Source = source,
                Lights = Enumerable.Range(0, lighting.Bank.Lights.Length).Where(i => lighting.Bank.Lights[i].Group == source.Group).ToArray(),
                Positions = source.Positions.Select(Numeric).ToArray(), Normals = source.Normals.Select(n => n.sqrMagnitude > 0.000001f ? Vec.Normalize(Numeric(n)) : Vec.UnitY).ToArray(),
                From = new Vec[count], To = new Vec[count], Colors = new Il2CppStructArray<Color>(count) };
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
        var bandLights = Enumerable.Range(0, lighting.Bank.Lights.Length).Where(i => lighting.Bank.Lights[i].Group == _band).ToArray();
        _bandLights = bandLights.Where(i => lighting.Bank.Lights[i].Type != "Ambient").ToArray();
        _bandAmbient = bandLights.Where(i => lighting.Bank.Lights[i].Type == "Ambient").ToArray();
        _bandSamples = new StageLightSample[_bandLights.Length];
        foreach (var source in assets.BandMeshes)
        {
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
            var vertices = new Il2CppStructArray<Vector3>(24);
            var colors = new Color[24];
            var triangles = new int[72];
            for (var side = 0; side < 12; side++)
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
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200);
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
        var frame = _lighting.At(time);
        var amount = frame.Amount(time);
        var state = frame.Sample(time);
        ShadeBand(state);
        var lightChanged = _frame != frame || _amount != amount;
        if (_frame != frame)
        {
            foreach (var surface in _surfaces)
            {
                Shade(surface, frame.From, surface.From);
                Shade(surface, frame.To, surface.To);
            }
        }
        if (lightChanged) foreach (var surface in _surfaces)
        {
            var colors = surface.Colors.AsSpan();
            for (var v = 0; v < colors.Length; v++)
            {
                var rgb = Vec.Lerp(surface.From[v], surface.To[v], amount);
                colors[v] = new Color(rgb.X, rgb.Y, rgb.Z, surface.Source.Colors[v].a / 255f);
            }
            surface.Source.Mesh.colors = surface.Colors;
        }
        for (var i = 0; i < _beams.Count; i++)
        {
            var value = state.Beams[i];
            _fixtures?.Update(_lighting.BeamIds[i], value);
            var beam = _beams[i];
            if (lightChanged) beam.Object.SetActive(value.Density > 0.001f);
            if (value.Density <= 0.001f) continue;
            beam.Object.transform.localPosition = Unity(value.Position);
            var q = value.Rotation;
            beam.Object.transform.localRotation = new(q.X, q.Y, q.Z, q.W);
            if (!lightChanged) continue;
            for (var side = 0; side < 12; side++)
            {
                var angle = side * MathF.PI / 6;
                var x = MathF.Cos(angle);
                var y = MathF.Sin(angle);
                beam.Vertices[side] = new Vector3(x * value.StartRadius, y * value.StartRadius, 0);
                beam.Vertices[side + 12] = new Vector3(x * value.EndRadius, y * value.EndRadius, value.Range);
            }
            beam.Mesh.vertices = beam.Vertices;
            beam.Material.color = new(value.Color.X, value.Color.Y, value.Color.Z, value.Density * 0.08f);
        }
        _frame = frame;
        _amount = amount;
    }

    private void ShadeBand(StageLightingValues state)
    {
        var ambient = Vec.Zero;
        foreach (var i in _bandAmbient) ambient += state.Lights[i].Energy;
        var count = 0;
        foreach (var i in _bandLights)
            if (state.Lights[i].Energy != Vec.Zero)
                _bandSamples[count++] = new(_lighting.Bank.Lights[i], state.Lights[i]);
        foreach (var surface in _bandMeshes)
        {
            var source = surface.Source;
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
                var energy = ambient;
                for (var i = 0; i < count; i++)
                    energy += _bandSamples[i].Energy * _bandSamples[i].Weight(position, normal);
                var vertex = source.Colors[v];
                colors[v] = new(energy.X * vertex.r / 255f, energy.Y * vertex.g / 255f, energy.Z * vertex.b / 255f, vertex.a / 255f);
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
                var weight = surface.Weights[slot] is { } weights ? weights[v] :
                    StageLighting.Weight(_lighting.Bank.Lights[i], state.Lights[i], surface.Positions[v], surface.Normals[v]);
                color += state.Lights[i].Energy * weight;
            }
            var vertex = surface.Source.Colors[v];
            output[v] = color * new Vec(vertex.r / 255f, vertex.g / 255f, vertex.b / 255f);
        }
    }

    private static Vec Numeric(Vector3 v) => new(v.x, v.y, v.z);
    private static Vector3 Unity(Vec v) => new(v.X, v.Y, v.Z);
}
