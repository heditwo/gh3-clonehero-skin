using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageTeslaRenderer : IDisposable
{
    private sealed record Slot(Mesh Mesh, Material Material, MeshRenderer Renderer);
    private readonly Slot?[] _slots = new Slot?[StageTeslaState.Capacity];
    private readonly StageAssets _assets;
    private readonly GameObject _root;
    private readonly Camera _camera;
    private readonly int _layer;
    private readonly AssetBundle _bundle;
    private readonly Shader _shader;
    private readonly Texture2D _texture;
    public int Effects { get; private set; }

    public StageTeslaRenderer(StageAssets assets, GameObject root, Camera camera, int layer, string directory, string effectsDirectory)
    {
        _assets = assets;
        _root = root;
        _camera = camera;
        _layer = layer;
        _bundle = AssetBundle.LoadFromFile(Path.Combine(directory, "tesla.bundle")) ?? throw new InvalidDataException("Tesla shader bundle did not load");
        try
        {
            _shader = StageShader.Load(_bundle, "the Tesla shader");
            _texture = assets.Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
            if (!ImageConversion.LoadImage(_texture, File.ReadAllBytes(Path.Combine(effectsDirectory, "tesla-noise.png")), true))
                throw new InvalidDataException("Tesla noise texture did not load");
            _texture.wrapMode = TextureWrapMode.Repeat;
            _texture.filterMode = FilterMode.Bilinear;
        }
        catch { _bundle.Unload(true); throw; }
    }

    public void Update(StageTeslaState state, double time)
    {
        var forward = _root.transform.InverseTransformDirection(_camera.transform.forward);
        Effects = 0;
        foreach (var slot in _slots) if (slot != null) slot.Renderer.enabled = false;
        foreach (var effect in state.Sample(time))
        {
            var draw = StageTeslaRender.Build(effect, new(forward.x, forward.y, forward.z), unchecked((uint)(long)(time * 1000)));
            var slot = _slots[effect.Slot] ??= Create(effect.Slot);
            slot.Mesh.vertices = new Il2CppStructArray<Vector3>(draw.Positions.Select(value => new Vector3(value.X, value.Y, value.Z)).ToArray());
            slot.Mesh.normals = new Il2CppStructArray<Vector3>(draw.Coordinates.Select(value => new Vector3(value.X, value.Y, value.Z)).ToArray());
            slot.Mesh.RecalculateBounds();
            foreach (var (name, value) in new[] { ("_Gh3TeslaAnimation", draw.Animation), ("_Gh3TeslaShape", draw.Shape), ("_Gh3TeslaColor", draw.Color) })
                slot.Material.SetVector(name, new(value.X, value.Y, value.Z, value.W));
            var definition = new StageParticleMaterial(Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("BlendMode"), new(Gh3QbType.Checksum, -1, effect.Blend)) }));
            StageParticles.ConfigureBlend(slot.Material, definition.PackedBlend);
            slot.Renderer.enabled = true;
            Effects++;
        }
    }

    private Slot Create(int index)
    {
        var mesh = _assets.Own(new Mesh { name = "GH3 Tesla Effects" });
        mesh.vertices = new Il2CppStructArray<Vector3>(new Vector3[8]);
        mesh.triangles = new Il2CppStructArray<int>(new[] { 0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 4, 5, 6, 6, 5, 7 });
        var material = _assets.Own(new Material(_shader));
        material.mainTexture = _texture;
        material.renderQueue = StageEffects.EffectQueue;
        material.SetInt("_Cull", 0);
        var obj = new GameObject(mesh.name) { layer = _layer };
        obj.transform.SetParent(_root.transform, false);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = obj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingOrder = index;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return new(mesh, material, renderer);
    }

    public void Dispose() => _bundle.Unload(true);
}
