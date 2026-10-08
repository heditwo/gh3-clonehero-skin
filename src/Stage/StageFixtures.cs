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
    public string Object { get; set; } = "";
    public bool Created { get; set; } = true;
}

internal sealed class StageFixtures : IDisposable
{
    private readonly List<StageAssets> _assets = new();
    private readonly Dictionary<string, (Transform Transform, bool FixedPosition)> _housings = new();
    private readonly Dictionary<string, List<Renderer>> _flares = new();
    private readonly MaterialPropertyBlock _flareProperties = new();
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    public Camera? View { get; set; }
    public Dictionary<string, StageObjectPlacement> Placements { get; } = new();
    public Dictionary<uint, string> ObjectBeams { get; } = new();
    public StageScriptState? Scripts { get; set; }
    private readonly Dictionary<string, StageHousingRotation> _rotations = new();

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
                // GH3 housings carry an additive "Flare" lens card; it glows with the beam when facing the camera.
                if (material.GetInt("_DstBlend") == (int)BlendMode.One)
                {
                    if (!_flares.TryGetValue(housing.Id, out var flares)) _flares[housing.Id] = flares = new();
                    flares.Add(renderer);
                }
            }
            obj.SetActive(housing.Created);
            _housings.Add(housing.Id, (obj.transform, housing.FixedPosition));
            var source = housing.Object.Length == 0 ? Convert.ToUInt32(housing.Id, 16) : Gh3QbText.Key(housing.Object);
            if (!Placements.TryAdd(source.ToString("x8"), new() { Position = housing.Position, Rotation = housing.Rotation, Created = housing.Created }))
                throw new InvalidDataException($"Duplicate light housing object {source:x8} in {path}: housing {housing.Id}, source '{housing.Object}', prior beams {string.Join(", ", ObjectBeams.Select(pair => $"{pair.Key:x8}={pair.Value}"))}");
            ObjectBeams.Add(source, housing.Id);
        }
        MelonLogger.Msg($"GH3 light housings: {_housings.Count} fixtures; {models.Count} shared models");
    }

    public void Update(string id, StageBeamValue value)
    {
        if (!_housings.TryGetValue(id, out var housing)) return;
        if (!housing.FixedPosition) housing.Transform.localPosition = new(value.Position.X, value.Position.Y, value.Position.Z);
        var q = value.Rotation;
        housing.Transform.localRotation = new(q.X, q.Y, q.Z, q.W);
        if (View == null || !_flares.TryGetValue(id, out var flares)) return;
        var forward = housing.Transform.forward;
        var from = housing.Transform.position;
        var eye = View.transform.position;
        var dx = eye.x - from.x; var dy = eye.y - from.y; var dz = eye.z - from.z;
        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        var facing = length > 0.0001f ? Math.Max(0, (forward.x * dx + forward.y * dy + forward.z * dz) / length) : 0;
        var strength = Math.Min(1, value.Density) * facing * facing * facing * facing;
        _flareProperties.SetColor(ColorId, new Color { r = value.Color.X * strength, g = value.Color.Y * strength, b = value.Color.Z * strength, a = strength });
        foreach (var flare in flares)
        {
            flare.enabled = strength > 0.002f;
            if (flare.enabled) flare.SetPropertyBlock(_flareProperties);
        }
    }

    public StageBeamValue Resolve(string id, StageBeamValue value, double time)
    {
        if (Scripts == null) return value;
        foreach (var (source, beam) in ObjectBeams)
        {
            if (beam != id || !Scripts.Objects.TryGetValue(source, out var state)) continue;
            value = Scripts.Beam(source, value);
            var pose = Scripts.Sample(source, time);
            if (!_rotations.TryGetValue(id, out var housingRotation)) _rotations[id] = housingRotation = new();
            var rotation = Scripts.Attached(source) ? pose.Rotation :
                housingRotation.Update(time, value.Rotation, pose.Rotation, state.RotationChanged, state.RotationTime);
            if (_housings.TryGetValue(id, out var housing))
            {
                housing.Transform.gameObject.SetActive(pose.Visible);
                housing.Transform.localScale = new(pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
                if (state.PositionChanged || Scripts.Attached(source)) housing.Transform.localPosition = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
            }
            return value with { Position = state.PositionChanged || Scripts.Attached(source) ? pose.Position : value.Position,
                Rotation = rotation, Density = pose.Visible ? value.Density : 0 };
        }
        return value;
    }

    public void Dispose()
    {
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _housings.Clear();
        _flares.Clear();
        Placements.Clear();
        ObjectBeams.Clear();
        _rotations.Clear();
        Scripts = null;
    }
}
