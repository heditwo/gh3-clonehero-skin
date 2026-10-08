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
        public Transform Person = null!;
        public System.Numerics.Vector2 Cell;
        public bool Turned;
    }
    // AddCrowdModelCam (GH3.exe 0x433c20) gives each crowd camera the orientation of the bg_viewport camera, aims it at
    // the point 1 unit above the ped, and, for rows without roty, turns it about world Y by 25 degrees times the x part
    // of the view axis.
    private const float TurnDegreesPerAxis = 25;
    private const float CameraDistance = 5;
    private static readonly System.Numerics.Vector3 FixedGroupOffset = new(50, 0, 0);
    public const int Layer = 29;
    private int Columns = 8;
    private int Rows = 3;
    private float CellWidth = 1.4f;
    private float CellHeight = 2.4f;
    // GH3 crowd cards are 1.15 x 2.58 m and show one model's render across the whole card (AddCrowdModelCam).
    private const float CardAspect = 1.15f / 2.58f;
    private const float RoleCellHeight = 2.3f;
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
    private Camera? _fixedCamera;
    private Transform? _stageRoot;
    private float _lift;

    public void Load(string directory, StageAssets stage, GameObject root, Camera stageCamera, StageCrowdMember[] members, Shader? surfaceShader = null)
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
        // GH3 renders the crowd atlas from lit 3D peds (D_3ChkBox_Pass0, Crowd light group). Its crowd script parks ped n
        // at (-500, -200 - 100n, 0), next to the venue's Crowd ambient and directional lights, and lights it there.
        var lit = surfaceShader != null;
        if (alphaShader == null) throw new InvalidOperationException("The crowd alpha shader is not available");
        foreach (var model in definition.Models)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, model, "animation.bin")));
            animations.Add(model, new BandAnimation(reader));
        }
        var roles = stage.CrowdRoles.Values.Distinct().OrderBy(role => role, StringComparer.Ordinal).ToArray();
        if (roles.Length > 0)
        {
            Columns = roles.Length;
            Rows = 1;
            CellHeight = RoleCellHeight;
            CellWidth = RoleCellHeight * CardAspect;
        }
        for (var cell = 0; cell < Columns * Rows; cell++)
        {
            var member = roles.Length > 0 ? members.FirstOrDefault(m => m.Role == roles[cell]) :
                members.Length == 0 ? null : members[cell % members.Length];
            var role = roles.Length > 0 ? roles[cell] : member?.Role ?? "crowd" + (cell % 8 + 1);
            var model = definition.Version == 2 ? member?.Model ?? definition.Members[role].Model : definition.Models[cell % definition.Models.Length];
            if (!animations.ContainsKey(model)) throw new InvalidDataException($"The crowd library has no model '{model}'. Run prepare-crowd.py.");
            var animation = animations[model];
            var person = new GameObject($"GH3 Crowd {cell}") { layer = Layer };
            person.transform.SetParent(_root.transform, false);
            _lift = CellHeight * 0.5f - (roles.Length > 0 ? 0.02f : 0.1f);
            var cellCenter = new System.Numerics.Vector2((cell % Columns - (Columns - 1) * 0.5f) * CellWidth,
                ((Rows - 1) * 0.5f - cell / Columns) * CellHeight);
            // GH3 parks every crowd ped with quat (0, 1, 0); only rows with roty call Obj_SetOrientation and keep an unturned camera.
            var yaw = member?.Yaw ?? 0;
            var turned = yaw == 0;
            person.transform.localRotation = Quaternion.Euler(0, turned ? 180 : yaw, 0);
            var bones = new Transform[animation.Parents.Length];
            for (var i = 0; i < bones.Length; i++)
                bones[i] = new GameObject($"GH3 Crowd {cell} Bone {i}") { layer = Layer }.transform;
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(animation.Parents[i] < 0 ? person.transform : bones[animation.Parents[i]], false);
                Apply(bones[i], animation.BindPose[i]);
            }
            System.Numerics.Vector3? lightAt = lit ? new System.Numerics.Vector3(-500, -200 - 100 * RoleIndex(role), 0) : null;
            var assets = new StageAssets();
            _assets.Add(assets);
            assets.Load(Path.Combine(directory, model), person, Layer, bones, lit, Gh3Checksum.Of("Crowd"), lit ? surfaceShader : null, lightAt);
            stage.ShadedSurfaces.AddRange(assets.ShadedSurfaces);
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
                Pose = new BandPose[bones.Length], Previous = new BandPose[bones.Length], Person = person.transform, Cell = cellCenter, Turned = turned };
            foreach (var hand in definition.Hands)
            {
                var item = new GameObject("GH3 Crowd " + hand) { layer = Layer };
                item.transform.SetParent(person.transform, false);
                var handAssets = new StageAssets();
                _assets.Add(handAssets);
                handAssets.Load(Path.Combine(directory, "hands", hand), item, Layer, bones, lit, Gh3Checksum.Of("Crowd"), lit ? surfaceShader : null, lightAt);
                stage.ShadedSurfaces.AddRange(handAssets.ShadedSurfaces);
                Alpha(handAssets, item);
                item.SetActive(hand.EndsWith("-clap", StringComparison.Ordinal));
                player.Hands.Add(hand, item);
            }
            _players.Add(player);
        }
        _atlas = new RenderTexture(Columns * 128, Rows * (roles.Length > 0 ? 288 : 256), 24, RenderTextureFormat.ARGB32)
        {
            name = "GH3 Animated Crowd Atlas",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        if (!_atlas.Create()) throw new InvalidOperationException("Could not create the crowd atlas");
        Camera AtlasCamera(string name, float depth, CameraClearFlags clear)
        {
            var cameraObject = new GameObject(name) { layer = Layer };
            cameraObject.transform.SetParent(_root.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.cullingMask = 1 << Layer;
            camera.depth = depth;
            camera.clearFlags = clear;
            camera.backgroundColor = Color.clear;
            camera.orthographic = true;
            camera.orthographicSize = Rows * CellHeight * 0.5f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = CameraDistance * 2;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.targetTexture = _atlas;
            camera.aspect = Columns * CellWidth / (Rows * CellHeight);
            return camera;
        }
        _stageRoot = root.transform;
        _camera = AtlasCamera("GH3 Crowd Camera", stageCamera.depth - 1, CameraClearFlags.SolidColor);
        // Peds with a fixed camera stand far enough away that the turned camera cannot see them, and the reverse.
        if (_players.Any(player => !player.Turned))
            _fixedCamera = AtlasCamera("GH3 Fixed Crowd Camera", stageCamera.depth - 0.5f, CameraClearFlags.Depth);
        Place(System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI));
        foreach (var material in stage.CrowdMaterials)
        {
            material.mainTexture = _atlas;
            if (roles.Length > 0)
            {
                var tile = Array.IndexOf(roles, stage.CrowdRoles.GetValueOrDefault(material, roles[0]));
                material.mainTextureScale = new Vector2 { x = 1f / Columns, y = 1 };
                material.mainTextureOffset = new Vector2 { x = (float)Math.Max(0, tile) / Columns, y = 0 };
            }
            // The atlas already contains alpha-blended color against a transparent background.
            if (material.shader.name != "GH3/Stage") material.SetInt("_SrcBlend", (int)BlendMode.One);
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

    private static int RoleIndex(string role) =>
        role.StartsWith("crowd", StringComparison.Ordinal) && int.TryParse(role.AsSpan(5), out var number) ? number - 1 : 0;

    private static readonly int CamRightId = Shader.PropertyToID("_Gh3CamRight");
    private static readonly int CamUpId = Shader.PropertyToID("_Gh3CamUp");

    // GH3 crowd cards are billboards (Billboard_Fast offsets the corners along g_InvView), and each card shows its ped
    // as seen from the view camera's direction.
    public void Face(Camera view)
    {
        if (_stage == null) return;
        if (_stageRoot != null)
        {
            var local = Quaternion.Inverse(_stageRoot.rotation) * view.transform.rotation;
            Place(new System.Numerics.Quaternion(local.x, local.y, local.z, local.w));
        }
        var right = view.transform.right;
        var up = view.transform.up;
        var r = new Vector4 { x = right.x, y = right.y, z = right.z, w = 1 };
        var u = new Vector4 { x = up.x, y = up.y, z = up.z, w = 0 };
        foreach (var material in _stage.CrowdMaterials)
        {
            if (material.shader.name != "GH3/Stage") continue;
            material.SetVector(CamRightId, r);
            material.SetVector(CamUpId, u);
        }
    }

    private void Place(System.Numerics.Quaternion view)
    {
        var forward = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, view);
        var turned = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY,
            TurnDegreesPerAxis * MathF.PI / 180 * forward.X) * view;
        Aim(_camera, turned, System.Numerics.Vector3.Zero);
        Aim(_fixedCamera, view, FixedGroupOffset);
        foreach (var player in _players)
        {
            var orientation = player.Turned ? turned : view;
            var target = (player.Turned ? System.Numerics.Vector3.Zero : FixedGroupOffset) +
                System.Numerics.Vector3.Transform(new System.Numerics.Vector3(player.Cell.X, player.Cell.Y, 0), orientation);
            player.Person.localPosition = new Vector3(target.X, target.Y - _lift, target.Z);
        }
    }

    private static void Aim(Camera? camera, System.Numerics.Quaternion orientation, System.Numerics.Vector3 anchor)
    {
        if (camera == null) return;
        var position = anchor + System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, 0, -CameraDistance), orientation);
        camera.transform.localPosition = new Vector3(position.X, position.Y, position.Z);
        camera.transform.localRotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
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
        if (_fixedCamera != null) _fixedCamera.targetTexture = null;
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
        _fixedCamera = null;
        _stageRoot = null;
        _atlas = null;
    }
}
