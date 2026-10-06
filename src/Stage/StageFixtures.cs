using System.Text.Json;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageFixtureDefinition
{
    public int Version { get; set; }
    public StageHousing[] Housings { get; set; } = Array.Empty<StageHousing>();
}

internal sealed class StageHousing
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();
    public bool FixedPosition { get; set; }
}

internal sealed class StageFixtures : IDisposable
{
    private readonly List<StageAssets> _assets = new();
    private readonly Dictionary<string, (Transform Transform, bool FixedPosition)> _housings = new();

    public void Load(string directory, GameObject root, int layer)
    {
        var path = Path.Combine(directory, "fixtures.json");
        if (!File.Exists(path)) return;
        var definition = JsonSerializer.Deserialize<StageFixtureDefinition>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (definition?.Version != 1 || definition.Housings.Length is < 1 or > 256 ||
            definition.Housings.Select(h => h.Id).Distinct().Count() != definition.Housings.Length ||
            definition.Housings.Any(h => h.Id.Length != 8 || !h.Id.All(Uri.IsHexDigit) || h.Model.Length != 8 || !h.Model.All(Uri.IsHexDigit) ||
                h.Position.Length != 3 || !h.Position.All(float.IsFinite) || h.Rotation.Length != 4 || !h.Rotation.All(float.IsFinite) ||
                Math.Abs(h.Rotation.Sum(v => v * v) - 1) > 0.01))
            throw new InvalidDataException("Invalid light housing definitions");
        var models = new Dictionary<string, (Mesh Mesh, Material Material)[]>();
        foreach (var model in definition.Housings.Select(h => h.Model).Distinct())
        {
            var prototype = new GameObject("GH3 Housing Model " + model) { layer = layer };
            prototype.transform.SetParent(root.transform, false);
            var assets = new StageAssets();
            _assets.Add(assets);
            assets.Load(Path.Combine(directory, model), prototype, layer);
            models.Add(model, prototype.GetComponentsInChildren<MeshFilter>()
                .Select(filter => (filter.sharedMesh, filter.GetComponent<MeshRenderer>().sharedMaterial)).ToArray());
            prototype.SetActive(false);
        }
        foreach (var housing in definition.Housings)
        {
            var obj = new GameObject("GH3 Light Housing " + housing.Id) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            obj.transform.localPosition = StageAssets.Vector(housing.Position);
            var q = housing.Rotation;
            obj.transform.localRotation = new Quaternion(q[0], q[1], q[2], q[3]);
            foreach (var (mesh, material) in models[housing.Model])
            {
                var part = new GameObject(mesh.name) { layer = layer };
                part.transform.SetParent(obj.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            _housings.Add(housing.Id, (obj.transform, housing.FixedPosition));
        }
        MelonLogger.Msg($"GH3 light housings: {_housings.Count} fixtures; {models.Count} shared models");
    }

    public void Update(string id, StageBeamValue value)
    {
        if (!_housings.TryGetValue(id, out var housing)) return;
        if (!housing.FixedPosition) housing.Transform.localPosition = new(value.Position.X, value.Position.Y, value.Position.Z);
        var q = value.Rotation;
        housing.Transform.localRotation = new(q.X, q.Y, q.Z, q.W);
    }

    public void Dispose()
    {
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _housings.Clear();
    }
}
