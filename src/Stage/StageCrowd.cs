using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class CrowdDefinition
{
    public int Version { get; set; }
    public string[] Models { get; set; } = Array.Empty<string>();
}

internal sealed class StageCrowd : IDisposable
{
    public const int Layer = 29;
    private const int Columns = 8;
    private const int Rows = 3;
    private const float CellWidth = 1.4f;
    private const float CellHeight = 2.4f;
    private readonly List<StageAssets> _assets = new();
    private readonly List<(Transform[] Bones, BandAnimation Animation, double Phase)> _players = new();
    private GameObject? _root;
    private RenderTexture? _atlas;
    private Camera? _camera;

    public void Load(string directory, StageAssets stage, GameObject root, Camera stageCamera)
    {
        var path = Path.Combine(directory, "crowd.json");
        if (stage.CrowdMaterials.Count == 0) return;
        if (!File.Exists(path))
        {
            MelonLogger.Warning("GH3 crowd animations are missing. Run prepare-crowd.py and install.sh.");
            return;
        }
        var definition = JsonSerializer.Deserialize<CrowdDefinition>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (definition?.Version != 1 || definition.Models.Length is < 1 or > 8 ||
            definition.Models.Distinct().Count() != definition.Models.Length ||
            definition.Models.Any(name => name.Length is < 1 or > 64 || name.Any(c => !char.IsLetterOrDigit(c) && c != '_')))
            throw new InvalidDataException("Invalid crowd definition");
        _root = new GameObject("GH3 Crowd Atlas") { layer = Layer };
        _root.transform.SetParent(root.transform, false);
        _root.transform.localPosition = new Vector3(0, 256, 0);
        var animations = new Dictionary<string, BandAnimation>();
        var alphaShader = Shader.Find("UI/Default");
        if (alphaShader == null) throw new InvalidOperationException("The crowd alpha shader is not available");
        foreach (var model in definition.Models)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, model, "animation.bin")));
            animations.Add(model, new BandAnimation(reader));
        }
        for (var cell = 0; cell < Columns * Rows; cell++)
        {
            var model = definition.Models[cell % definition.Models.Length];
            var animation = animations[model];
            var person = new GameObject($"GH3 Crowd {cell}") { layer = Layer };
            person.transform.SetParent(_root.transform, false);
            person.transform.localPosition = new Vector3((cell % Columns - (Columns - 1) * 0.5f) * CellWidth,
                (Rows - 1 - cell / Columns) * CellHeight + 0.1f, 0);
            var bones = new Transform[animation.Parents.Length];
            for (var i = 0; i < bones.Length; i++)
                bones[i] = new GameObject($"GH3 Crowd {cell} Bone {i}") { layer = Layer }.transform;
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(animation.Parents[i] < 0 ? person.transform : bones[animation.Parents[i]], false);
                Apply(bones[i], animation.BindPose[i]);
            }
            var assets = new StageAssets();
            _assets.Add(assets);
            assets.Load(Path.Combine(directory, model), person, Layer, bones);
            foreach (var skin in person.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                // The particle shader writes RGB only; this pass supplies the atlas silhouette.
                var alpha = assets.Own(new Material(alphaShader));
                alpha.mainTexture = skin.sharedMaterial.mainTexture;
                alpha.SetInt("_ColorMask", (int)ColorWriteMask.Alpha);
                alpha.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
                alpha.renderQueue = 3100;
                skin.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { skin.sharedMaterial, alpha });
            }
            _players.Add((bones, animation, cell * 0.371));
        }
        _atlas = new RenderTexture(Columns * 128, Rows * 256, 24, RenderTextureFormat.ARGB32)
        {
            name = "GH3 Animated Crowd Atlas",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        if (!_atlas.Create()) throw new InvalidOperationException("Could not create the crowd atlas");
        var cameraObject = new GameObject("GH3 Crowd Camera") { layer = Layer };
        cameraObject.transform.SetParent(_root.transform, false);
        cameraObject.transform.localPosition = new Vector3(0, Rows * CellHeight * 0.5f, -5);
        _camera = cameraObject.AddComponent<Camera>();
        _camera.cullingMask = 1 << Layer;
        _camera.depth = stageCamera.depth - 1;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = Color.clear;
        _camera.orthographic = true;
        _camera.orthographicSize = Rows * CellHeight * 0.5f;
        _camera.nearClipPlane = 0.1f;
        _camera.farClipPlane = 10;
        _camera.allowHDR = false;
        _camera.allowMSAA = false;
        _camera.useOcclusionCulling = false;
        _camera.targetTexture = _atlas;
        _camera.aspect = Columns * CellWidth / (Rows * CellHeight);
        foreach (var material in stage.CrowdMaterials)
        {
            material.mainTexture = _atlas;
            // The atlas already contains alpha-blended color against a transparent background.
            material.SetInt("_SrcBlend", (int)BlendMode.One);
        }
        Update(0);
        MelonLogger.Msg($"GH3 crowd loaded: {definition.Models.Length} models; {_players.Count} animation groups; original GH3 clips");
    }

    public void Update(double time)
    {
        foreach (var (bones, animation, phase) in _players)
        {
            var sample = animation.At(Math.Max(0, time) + phase);
            for (var i = 0; i < bones.Length; i++) Apply(bones[i], animation.Sample(sample, i));
        }
    }

    private static void Apply(Transform bone, BandPose pose)
    {
        bone.localPosition = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        bone.localRotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
    }

    public void Dispose()
    {
        if (_camera != null) _camera.targetTexture = null;
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        if (_atlas != null)
        {
            _atlas.Release();
            UnityEngine.Object.Destroy(_atlas);
        }
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _players.Clear();
        _root = null;
        _camera = null;
        _atlas = null;
    }
}
