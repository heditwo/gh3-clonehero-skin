using UnityEngine;

namespace Gh3Skin;

internal sealed class StageScripts : IDisposable
{
    private readonly StageScriptState _state;
    private readonly Dictionary<uint, Transform> _objects;
    private readonly HashSet<string> _reported = new();
    private readonly StageAssets _assets;
    private readonly Dictionary<Renderer, System.Numerics.Vector4> _materialColors = new();
    private readonly Dictionary<Renderer, System.Numerics.Vector4> _materialUvs = new();
    public StageScriptState State => _state;
    public IReadOnlyDictionary<uint, string> TexturePaths { get; }

    private StageScripts(StageScriptBank bank, StageAssets assets, StageFixtures fixtures, StageActors actors, StageLighting? lighting, StageTimeline timeline, string venue)
    {
        TexturePaths = bank.TexturePaths;
        _assets = assets;
        foreach (var (id, beam) in bank.LightVolumes) fixtures.ObjectBeams.TryAdd(Gh3QbText.Key(id), Gh3QbText.Key(beam).ToString("x8"));
        var placements = new Dictionary<string, StageObjectPlacement>(assets.Definition.Objects);
        foreach (var pair in fixtures.Placements) placements.Add(pair.Key, pair.Value);
        foreach (var pair in actors.Placements) placements.Add(pair.Key, pair.Value);
        _state = new(bank, placements, timeline, venue, (id, time) =>
        {
            if (lighting == null || !fixtures.ObjectBeams.TryGetValue(id, out var beam)) return null;
            var index = Array.IndexOf(lighting.BeamIds, beam);
            return index < 0 ? null : lighting.At(time).SampleBeam(index, time);
        }, actors.ScriptDefinitions, lighting, target =>
        {
            var surface = assets.MaterialSurfaces.FirstOrDefault(surface => surface.Object == target.Object && surface.Material == target.Material);
            if (surface.Definition == null) return null;
            if (target.Parameter == StageMaterialState.ColorParameter) return System.Numerics.Vector4.One;
            return target.Parameter == StageMaterialState.FpsParameter && surface.Definition.Animation is { } animation ? new(animation.Fps, 0, 0, 0) : null;
        });
        fixtures.Scripts = _state;
        actors.Scripts = _state;
        _objects = assets.Objects.ToDictionary(pair => Convert.ToUInt32(pair.Key, 16), pair => pair.Value);
    }

    public static StageScripts? Load(string directory, string library, StageAssets assets, StageFixtures fixtures, StageActors actors, StageLighting? lighting, StageTimeline timeline, string venue)
    {
        var path = Path.Combine(directory, "scripts.json");
        return File.Exists(path) ? new(StageScriptBank.Load(path, Path.Combine(library, "scripts.json")), assets, fixtures, actors, lighting, timeline, venue) : null;
    }

    public void Update(double time, float rock, IReadOnlyList<uint>? events = null)
    {
        _state.Update(time, rock);
        if (events != null) foreach (var type in events) _state.Raise(type);
        foreach (var (id, obj) in _state.Objects)
        {
            var pose = _state.Sample(id, time);
            if (!_objects.TryGetValue(id, out var target)) continue;
            target.localPosition = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
            target.localRotation = new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
            target.localScale = new(pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
            target.gameObject.SetActive(pose.Visible);
        }
        foreach (var surface in _assets.MaterialSurfaces)
        {
            var color = _state.Materials.Properties.GetValueOrDefault(new(surface.Object, surface.Material, StageMaterialState.ColorParameter), System.Numerics.Vector4.One);
            var fps = _state.Materials.Properties.TryGetValue(new(surface.Object, surface.Material, StageMaterialState.FpsParameter), out var speed) ? speed.X : (float?)null;
            var uv = surface.Definition.Animation?.Transform(time, fps) ?? new System.Numerics.Vector4(1, 1, 0, 0);
            if (_materialColors.GetValueOrDefault(surface.Renderer, System.Numerics.Vector4.One) == color &&
                _materialUvs.GetValueOrDefault(surface.Renderer, new(1, 1, 0, 0)) == uv) continue;
            var properties = new MaterialPropertyBlock();
            surface.Renderer.GetPropertyBlock(properties);
            properties.SetColor("_Color", new(color.X, color.Y, color.Z, color.W));
            properties.SetVector("_MainTex_ST", new(uv.X, uv.Y, uv.Z, uv.W));
            surface.Renderer.SetPropertyBlock(properties);
            _materialColors[surface.Renderer] = color;
            _materialUvs[surface.Renderer] = uv;
        }
        foreach (var diagnostic in _state.Diagnostics)
            if (_reported.Add(diagnostic)) MelonLoader.MelonLogger.Warning("GH3 stage script: " + diagnostic);
    }

    public void Dispose() => _state.Dispose();
}
