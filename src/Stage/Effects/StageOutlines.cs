using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageOutlines : IDisposable
{
    private sealed record Surface(uint Object, Renderer Renderer, Mesh Mesh, bool Skinned);
    private readonly StageScripts _scripts;
    private readonly Camera _camera;
    private readonly AssetBundle _bundle;
    private readonly Shader _shader;
    private readonly Material _mask;
    private readonly CommandBuffer _commands = new() { name = "GH3 Outlines" };
    private readonly Dictionary<uint, Material> _materials = new();
    private readonly List<Surface> _surfaces = new();

    public StageOutlines(StageScripts scripts, Camera camera, StageAssets assets, StageActors actors, string path)
    {
        _scripts = scripts;
        _camera = camera;
        _bundle = AssetBundle.LoadFromFile(path) ?? throw new InvalidDataException("Stage outline shader bundle did not load");
        try
        {
            _shader = StageShader.Load(_bundle, "the stage outline shader");
            _mask = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            foreach (var (id, root) in assets.Objects.Select(pair => (Convert.ToUInt32(pair.Key, 16), pair.Value)).Concat(actors.Objects.Select(pair => (pair.Key, pair.Value))))
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                    var mesh = skinned != null ? new Mesh { name = "GH3 Outline Pose", hideFlags = HideFlags.HideAndDontSave } : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh != null) _surfaces.Add(new(id, renderer, mesh, skinned != null));
                }
            _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _commands);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Update(double time)
    {
        _commands.Clear();
        var state = _scripts.State.RenderControls;
        _mask.SetVector("_Gh3ViewZ", _camera.worldToCameraMatrix.GetRow(2));
        _mask.SetVector("_Gh3Clock", new(0, state.Outline.FillDistance, 1, 0));
        foreach (var id in _materials.Keys.Where(id => !state.Outlines.ContainsKey(id)).ToArray())
        {
            UnityEngine.Object.Destroy(_materials[id]);
            _materials.Remove(id);
        }
        foreach (var group in _surfaces.Where(surface => state.Outlines.TryGetValue(surface.Object, out var outline) && outline.Enabled &&
            surface.Renderer.enabled && surface.Renderer.gameObject.activeInHierarchy).GroupBy(surface => surface.Object))
        {
            if (!_materials.TryGetValue(group.Key, out var material))
                _materials.Add(group.Key, material = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
            var color = state.OutlineColors.GetValueOrDefault(state.Outlines[group.Key].Color);
            material.SetVector("_Gh3Color", new(color.X, color.Y, color.Z, color.W));
            material.SetVector("_Gh3Outline", new(state.Outline.Min, state.Outline.Max, state.Outline.Rate, state.Outline.ZScale));
            material.SetVector("_Gh3ViewZ", _camera.worldToCameraMatrix.GetRow(2));
            material.SetVector("_Gh3Clock", new((float)time, 0, 0, 0));
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)(state.Outline.BlendMode == 1 ? BlendMode.OneMinusSrcAlpha : BlendMode.One));
            foreach (var surface in group)
                if (surface.Skinned) surface.Renderer.Cast<SkinnedMeshRenderer>().BakeMesh(surface.Mesh);
            for (var pass = 0; pass < 3; pass++)
                foreach (var surface in group)
                    for (var submesh = 0; submesh < surface.Mesh.subMeshCount; submesh++)
                        _commands.Internal_DrawMesh(surface.Mesh, surface.Renderer.localToWorldMatrix, pass == 1 ? material : _mask, submesh, pass, null);
        }
    }

    public void Dispose()
    {
        if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _commands);
        _commands.Dispose();
        foreach (var surface in _surfaces.Where(surface => surface.Skinned)) UnityEngine.Object.Destroy(surface.Mesh);
        _surfaces.Clear();
        foreach (var material in _materials.Values) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        if (_mask != null) UnityEngine.Object.Destroy(_mask);
        _bundle.Unload(true);
    }
}
