using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

// Writes near depth over the highway's screen area after stage geometry, so particle effects drawn later
// never show through the highway, including its transparent far end.
internal sealed class StageHighwayMask
{    // The mask quad sits just past the stage camera's near plane, and the neck test just inside the player's far plane.
    private const float NearPlaneOffset = 1.5f;
    private const float FarPlaneShare = 0.999f;

    public const int Queue = 3005;
    private readonly Camera _stageCamera;
    private readonly Mesh _mesh;
    private readonly GameObject _object;
    private readonly Il2CppStructArray<Vector3> _vertices = new(4);
    private SpriteRenderer? _neck;
    private bool _searched;

    public StageHighwayMask(StageAssets assets, Camera stageCamera, Shader surfaceShader, int layer)
    {
        _stageCamera = stageCamera;
        _mesh = assets.Own(new Mesh { name = "GH3 Highway Effect Mask" });
        _mesh.MarkDynamic();
        _mesh.vertices = _vertices;
        _mesh.triangles = new Il2CppStructArray<int>(new[] { 0, 1, 2, 0, 2, 3 });
        _mesh.bounds = new Bounds(Vector3.zero, new Vector3 { x = 1000, y = 1000, z = 1000 });
        var material = assets.Own(new Material(surfaceShader));
        material.SetInt("_SrcBlend", (int)BlendMode.Zero);
        material.SetInt("_DstBlend", (int)BlendMode.One);
        material.SetInt("_ZWrite", 1);
        material.SetInt("_Cull", (int)CullMode.Off);
        material.SetVector("_Gh3Surface", new Vector4 { w = 1 });
        material.SetColor("_Color", Color.white);
        material.renderQueue = Queue;
        _object = new GameObject("GH3 Highway Effect Mask") { layer = layer };
        _object.transform.SetParent(stageCamera.transform, false);
        _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
        var renderer = _object.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        _object.SetActive(false);
    }

    public void Update(BasePlayer player)
    {
        if (!_searched)
        {
            _searched = true;
            foreach (var renderer in player.GetComponentsInChildren<SpriteRenderer>(true))
                if (renderer.name == "Neck") { _neck = renderer; break; }
            if (_neck == null) MelonLoader.MelonLogger.Warning("GH3 highway mask: the highway renderer was not found");
        }
        var playerCamera = player.mainCamera;
        if (_neck == null || playerCamera == null || !_neck.gameObject.activeInHierarchy || !_neck.enabled)
        {
            if (_object.activeSelf) _object.SetActive(false);
            return;
        }
        var bounds = _neck.localBounds;
        var min = bounds.min;
        var max = bounds.max;
        var transform = _neck.transform;
        var rect = _stageCamera.pixelRect;
        var depth = _stageCamera.nearClipPlane * NearPlaneOffset;
        var height = MathF.Tan(_stageCamera.fieldOfView * MathF.PI / 360) * depth;
        var width = height * _stageCamera.aspect;
        var far = playerCamera.farClipPlane * FarPlaneShare;
        for (var side = 0; side < 2; side++)
        {
            var x = side == 0 ? min.x : max.x;
            var near = transform.TransformPoint(new Vector3 { x = x, y = min.y });
            var end = transform.TransformPoint(new Vector3 { x = x, y = max.y });
            var nearScreen = playerCamera.WorldToScreenPoint(near);
            var endScreen = playerCamera.WorldToScreenPoint(end);
            // The highway beyond the player camera's far plane is not drawn, so the mask stops there too.
            if (endScreen.z > far && endScreen.z > nearScreen.z)
            {
                var t = (far - nearScreen.z) / (endScreen.z - nearScreen.z);
                endScreen = playerCamera.WorldToScreenPoint(new Vector3 {
                    x = near.x + (end.x - near.x) * t, y = near.y + (end.y - near.y) * t, z = near.z + (end.z - near.z) * t });
            }
            _vertices[side == 0 ? 0 : 1] = Corner(nearScreen, side, true);
            _vertices[side == 0 ? 3 : 2] = Corner(endScreen, side, false);
        }
        _mesh.vertices = _vertices;
        if (!_object.activeSelf) _object.SetActive(true);

        Vector3 Corner(Vector3 screen, int side, bool bottom)
        {
            // Corners behind the player camera project mirrored; push them far off screen instead.
            var u = screen.z > 0 ? (screen.x - rect.x) / rect.width * 2 - 1 : side == 0 ? -100 : 100;
            var v = screen.z > 0 ? (screen.y - rect.y) / rect.height * 2 - 1 : bottom ? -100 : 100;
            return new Vector3 { x = u * width, y = v * height, z = depth };
        }
    }
}
