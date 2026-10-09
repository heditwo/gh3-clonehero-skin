using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageScreenEffects : IDisposable
{
    private readonly StageScripts _scripts;
    private readonly Camera _camera;
    private readonly AssetBundle _bundle;
    private readonly Shader _shader;
    private readonly CommandBuffer _commands = new() { name = "GH3 Screen Effects" };
    private readonly Dictionary<StageScreenTarget, Material> _materials = new();
    private readonly int _temporary = Shader.PropertyToID("_Gh3ScreenSource");
    private readonly uint _viewport = Gh3Checksum.Value("bg_viewport");

    public StageScreenEffects(StageScripts scripts, Camera camera, string path)
    {
        _scripts = scripts;
        _camera = camera;
        _bundle = AssetBundle.LoadFromFile(path) ?? throw new InvalidDataException("Stage screen shader bundle did not load");
        try
        {
            _shader = StageShader.Load(_bundle, "the stage screen shader");
            _camera.AddCommandBuffer(CameraEvent.AfterEverything, _commands);
        }
        catch
        {
            _commands.Dispose();
            _bundle.Unload(true);
            throw;
        }
    }

    public void Update(double time)
    {
        var effects = _scripts.State.RenderControls.ScreenEffects;
        _commands.Clear();
        foreach (var target in _materials.Keys.Where(target => !effects.ContainsKey(target)).ToArray())
        {
            UnityEngine.Object.Destroy(_materials[target]);
            _materials.Remove(target);
        }
        var allocated = false;
        foreach (var (target, effect) in effects)
        {
            if (target.Viewport != _viewport || effect.To.Field("Type")?.Integer != StageScreenColor.Type) continue;
            var matrix = StageScreenColor.Matrix(effect.Sample(time));
            if (matrix == System.Numerics.Matrix4x4.Identity) continue;
            if (!_materials.TryGetValue(target, out var material))
                _materials.Add(target, material = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
            material.SetVector("_Gh3Red", new(matrix.M11, matrix.M12, matrix.M13, matrix.M14));
            material.SetVector("_Gh3Green", new(matrix.M21, matrix.M22, matrix.M23, matrix.M24));
            material.SetVector("_Gh3Blue", new(matrix.M31, matrix.M32, matrix.M33, matrix.M34));
            if (!allocated)
            {
                _commands.GetTemporaryRT(_temporary, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.Default);
                allocated = true;
            }
            var temporary = new RenderTargetIdentifier(BuiltinRenderTextureType.PropertyName) { m_NameID = _temporary };
            _commands.Blit(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget), temporary);
            _commands.Blit(temporary, new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget), material);
        }
        if (allocated) _commands.ReleaseTemporaryRT(_temporary);
    }

    public void Dispose()
    {
        if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.AfterEverything, _commands);
        _commands.Dispose();
        foreach (var material in _materials.Values) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _bundle.Unload(true);
    }
}
