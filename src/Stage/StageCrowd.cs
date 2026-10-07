using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageCrowd : IDisposable
{
    private sealed class Player
    {
        public string Role = "";
        public Transform[] Bones = Array.Empty<Transform>();
        public BandAnimation Animation = null!;
        public double Phase;
        public BandPose[] Pose = Array.Empty<BandPose>();
        public BandPose[] Previous = Array.Empty<BandPose>();
        public CrowdAnimationPlayer? Performance;
        public Dictionary<string, GameObject> Hands = new();
    }
    public const int Layer = 29;
    private const int Columns = 8;
    private const int Rows = 3;
    private const float CellWidth = 1.4f;
    private const float CellHeight = 2.4f;
    private readonly List<StageAssets> _assets = new();
    private readonly List<Player> _players = new();
    private readonly Dictionary<string, PerformanceClip> _clips = new();
    private StageCrowdState? _state;
    private CrowdDefinition? _definition;
    private StageAssets? _stage;
    private string _directory = "";
    private GameObject? _root;
    private RenderTexture? _atlas;
    private Camera? _camera;

    public void Load(string directory, StageAssets stage, GameObject root, Camera stageCamera, StageCrowdMember[] members)
    {
        _stage = stage;
        var path = Path.Combine(directory, "crowd.json");
        if (stage.CrowdMaterials.Count == 0) return;
        if (!File.Exists(path))
        {
            MelonLogger.Warning("GH3 crowd animations are missing. Run prepare-crowd.py and install.sh.");
            return;
        }
        var definition = CrowdDefinition.Load(path);
        _definition = definition;
        _directory = directory;
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
            var member = members.Length == 0 ? null : members[cell % members.Length];
            var role = member?.Role ?? "crowd" + (cell % 8 + 1);
            var model = definition.Version == 2 ? member?.Model ?? definition.Members[role].Model : definition.Models[cell % definition.Models.Length];
            if (!animations.ContainsKey(model)) throw new InvalidDataException($"The crowd library has no model '{model}'. Run prepare-crowd.py.");
            var animation = animations[model];
            var person = new GameObject($"GH3 Crowd {cell}") { layer = Layer };
            person.transform.SetParent(_root.transform, false);
            person.transform.localPosition = new Vector3((cell % Columns - (Columns - 1) * 0.5f) * CellWidth,
                (Rows - 1 - cell / Columns) * CellHeight + 0.1f, 0);
            person.transform.localRotation = Quaternion.Euler(0, member?.Yaw ?? 0, 0);
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
            void Alpha(StageAssets owner, GameObject target)
            {
                foreach (var skin in target.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    // The particle shader writes RGB only; this pass supplies the atlas silhouette.
                    var alpha = owner.Own(new Material(alphaShader));
                    alpha.mainTexture = skin.sharedMaterial.mainTexture;
                    alpha.SetInt("_ColorMask", (int)ColorWriteMask.Alpha);
                    alpha.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
                    alpha.renderQueue = 3100;
                    skin.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { skin.sharedMaterial, alpha });
                }
            }
            Alpha(assets, person);
            var player = new Player { Role = role, Bones = bones, Animation = animation, Phase = cell * 0.371,
                Pose = new BandPose[bones.Length], Previous = new BandPose[bones.Length] };
            foreach (var hand in definition.Hands)
            {
                var item = new GameObject("GH3 Crowd " + hand) { layer = Layer };
                item.transform.SetParent(person.transform, false);
                var handAssets = new StageAssets();
                _assets.Add(handAssets);
                handAssets.Load(Path.Combine(directory, "hands", hand), item, Layer, bones);
                Alpha(handAssets, item);
                item.SetActive(hand.EndsWith("-clap", StringComparison.Ordinal));
                player.Hands.Add(hand, item);
            }
            _players.Add(player);
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
        Update(0, 0.5f);
        MelonLogger.Msg($"GH3 crowd loaded: {definition.Models.Length} models; {_players.Count} animation groups; original GH3 clips");
    }

    public void SetTimeline(StageTimeline timeline)
    {
        _state = new StageCrowdState(timeline);
        if (_definition?.Version != 2)
        {
            if (timeline.PlaybackCues.Any(cue => cue.Type.StartsWith("crowd-", StringComparison.Ordinal)))
                MelonLogger.Warning("Crowd direction needs the version 2 crowd library. Run prepare-crowd.py and install.sh.");
            return;
        }
        for (var index = 0; index < _players.Count; index++)
        {
            var player = _players[index];
            if (player.Bones.Length != _definition.Bones.Length) throw new InvalidDataException("Crowd skeleton does not match the animation library.");
            player.Performance = new CrowdAnimationPlayer(_definition, player.Role, index, timeline, _state);
        }
    }

    public void Update(double time, float rock)
    {
        _state?.Update(time, rock);
        _stage?.SetVisible("crowd-lighters", _state?.VisibleAt(time));
        foreach (var player in _players)
        {
            if (player.Performance != null && _state != null)
            {
                player.Performance.Sample(time, player.Pose, player.Previous, player.Animation.BindPose, Clip);
                for (var bone = 0; bone < player.Bones.Length; bone++) Apply(player.Bones[bone], player.Pose[bone]);
                var index = StageCrowdState.Member(player.Role);
                var left = "left-" + _state.HandAt(index, false, time);
                var right = "right-" + _state.HandAt(index, true, time);
                foreach (var (hand, item) in player.Hands)
                {
                    var active = hand == left || hand == right;
                    if (item.activeSelf != active) item.SetActive(active);
                }
            }
            else
            {
                var sample = player.Animation.At(Math.Max(0, time) + player.Phase);
                for (var bone = 0; bone < player.Bones.Length; bone++) Apply(player.Bones[bone], player.Animation.Sample(sample, bone));
            }
        }
    }

    private PerformanceClip Clip(string id)
    {
        if (_clips.TryGetValue(id, out var clip)) return clip;
        using var stream = File.OpenRead(Path.Combine(_directory, "clips", id + ".bin.gz"));
        _clips.Add(id, clip = new PerformanceClip(stream, _definition!.Bones.Length));
        return clip;
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
        _clips.Clear();
        _state = null;
        _definition = null;
        _stage = null;
        _root = null;
        _camera = null;
        _atlas = null;
    }
}
