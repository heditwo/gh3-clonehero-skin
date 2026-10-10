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
        public bool Turned;
        public Camera Camera = null!;
    }
    // AddCrowdModelCam (GH3.exe 0x433c20) gives each crowd camera the orientation of the bg_viewport camera, places it
    // 3.5 units back from the point 1 unit above the ped, and, for rows without roty, turns it about world Y by 25 degrees
    // times the x part of the view axis. create_crowd_models gives the camera a 20 degree horizontal field of view, a far
    // clip of 20, and the imposter_rendering viewport (128 x 256), whose texture the venue's crowd cards show whole.
    private const float TurnDegreesPerAxis = 25;
    private const float CameraDistance = 3.5f;
    private const float CameraLift = 1;
    private const float HorizontalFov = 20;
    private const float FarClip = 20;
    private const int CellPixelsWide = 128;
    private const int CellPixelsHigh = 256;
    // create_crowd_models parks ped n at (-500, -200 - 100n, 0); the atlas keeps that spacing so no camera sees another ped.
    private const float PedSpacing = 100;
    private const float ParkX = -500, ParkY = -200;
    public const int Layer = 29;
    // The alpha pass draws after the transparent queue.
    private const int AlphaPassQueueOffset = 100;
    // Tuned: each crowd cell starts its clips at another phase.
    private const double PhasePerCell = 0.371;
    private const float NearClip = 0.1f;
    private int Columns = 8;
    private int Rows = 3;
    private readonly List<StageAssets> _assets = new();
    private readonly List<Player> _players = new();
    private PerformanceClipCache? _clips;
    private StageCrowdState? _state;
    private CrowdDefinition? _definition;
    private StageAssets? _stage;
    private string _directory = "";
    private GameObject? _root;
    // One render target per crowd row, like GH3's viewport texture overrides (tw_billboardNN): each card shows its row's ped whole.
    private readonly List<RenderTexture> _targets = new();
    private Transform? _stageRoot;

    public void Load(string directory, StageAssets stage, GameObject root, Camera stageCamera, StageCrowdMember[] members, Shader? surfaceShader = null)
    {
        _stage = stage;
        var path = Path.Combine(directory, "crowd.json");
        if (stage.CrowdMaterials.Count == 0) return;
        if (!File.Exists(path))
        {
            MelonLogger.Warning("GH3 crowd animations are missing. Run scripts/regenerate.sh library and install.sh.");
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
        }
        for (var cell = 0; cell < Columns * Rows; cell++)
        {
            var member = roles.Length > 0 ? members.FirstOrDefault(m => m.Role == roles[cell]) :
                members.Length == 0 ? null : members[cell % members.Length];
            var role = roles.Length > 0 ? roles[cell] : member?.Role ?? "crowd" + (cell % 8 + 1);
            var model = definition.Version == 2 ? member?.Model ?? definition.Members[role].Model : definition.Models[cell % definition.Models.Length];
            if (!animations.ContainsKey(model)) throw new InvalidDataException($"The crowd library has no model '{model}'. Run scripts/regenerate.sh library.");
            var animation = animations[model];
            var person = new GameObject($"GH3 Crowd {cell}") { layer = Layer };
            person.transform.SetParent(_root.transform, false);
            person.transform.localPosition = new Vector3(0, ParkY - PedSpacing * cell, 0);
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
            System.Numerics.Vector3? lightAt = lit ? new System.Numerics.Vector3(ParkX, ParkY - PedSpacing * RoleIndex(role), 0) : null;
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
                    alpha.renderQueue = RenderQueue.Transparent + AlphaPassQueueOffset;
                    skin.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { skin.sharedMaterial, alpha });
                }
            }
            Alpha(assets, person);
            var player = new Player { Role = role, Bones = bones, Animation = animation, Phase = cell * PhasePerCell,
                Pose = new BandPose[bones.Length], Previous = new BandPose[bones.Length], Person = person.transform, Turned = turned };
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
        _stageRoot = root.transform;
        for (var cell = 0; cell < _players.Count; cell++)
        {
            var cameraObject = new GameObject("GH3 Crowd Camera " + cell) { layer = Layer };
            cameraObject.transform.SetParent(_root.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.cullingMask = 1 << Layer;
            camera.depth = stageCamera.depth - 1;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.nearClipPlane = NearClip;
            camera.farClipPlane = FarClip;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            var target = new RenderTexture(CellPixelsWide, CellPixelsHigh, 24, RenderTextureFormat.ARGB32)
            {
                name = "GH3 Crowd Viewport " + _players[cell].Role, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            if (!target.Create()) throw new InvalidOperationException("Could not create a crowd viewport texture");
            _targets.Add(target);
            camera.targetTexture = target;
            camera.aspect = (float)CellPixelsWide / CellPixelsHigh;
            camera.fieldOfView = 2 * MathF.Atan(MathF.Tan(HorizontalFov * MathF.PI / 360) / camera.aspect) * 180 / MathF.PI;
            _players[cell].Camera = camera;
        }
        Place(System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI));
        foreach (var material in stage.CrowdMaterials)
        {
            var tile = roles.Length > 0 ? Math.Max(0, Array.IndexOf(roles, stage.CrowdRoles.GetValueOrDefault(material, roles[0]))) : 0;
            material.mainTexture = _targets[tile];
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
                MelonLogger.Warning("Crowd direction needs the version 2 crowd library. Run scripts/regenerate.sh library and install.sh.");
            return;
        }
        for (var index = 0; index < _players.Count; index++)
        {
            var player = _players[index];
            if (player.Bones.Length != _definition.Bones.Length) throw new InvalidDataException("Crowd skeleton does not match the animation library.");
            player.Performance = new CrowdAnimationPlayer(_definition, player.Role, index, timeline, _state);
        }
        if (_players.Count > 0)
        {
            _clips = new PerformanceClipCache(Path.Combine(_directory, "clips"), _definition.Bones.Length, "crowd");
            _clips.Prefetch(_definition.PerformanceClips.Keys);
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
        foreach (var player in _players)
        {
            if (player.Camera == null) continue;
            var orientation = player.Turned ? turned : view;
            var ped = player.Person.localPosition;
            var position = new System.Numerics.Vector3(ped.x, ped.y + CameraLift, ped.z) +
                System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, 0, -CameraDistance), orientation);
            player.Camera.transform.localPosition = new Vector3(position.X, position.Y, position.Z);
            player.Camera.transform.localRotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
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

    private PerformanceClip Clip(string id) => (_clips ??= new PerformanceClipCache(Path.Combine(_directory, "clips"), _definition!.Bones.Length, "crowd")).Get(id);

    public PerformanceClipCache? Clips => _clips;

    private static void Apply(Transform bone, BandPose pose)
    {
        bone.localPosition = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        bone.localRotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
    }

    public void Dispose()
    {
        foreach (var player in _players)
            if (player.Camera != null) player.Camera.targetTexture = null;
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        foreach (var target in _targets)
        {
            target.Release();
            UnityEngine.Object.Destroy(target);
        }
        _targets.Clear();
        foreach (var assets in _assets) assets.Dispose();
        _assets.Clear();
        _players.Clear();
        _clips = null;
        _state = null;
        _definition = null;
        _stage = null;
        _root = null;
        _stageRoot = null;
    }
}
