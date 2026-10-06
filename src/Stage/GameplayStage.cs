using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed class GameplayStage
{
    private const int Layer = 30;
    private readonly string _directory;
    private readonly string _library;
    private readonly string _events;
    private readonly List<(Camera Camera, int Mask)> _cameras = new();
    private GameObject? _root;
    private StageAssets? _assets;
    private Camera? _camera;
    private StageEffects? _effects;
    private StageHandFire? _handFire;
    private StageTimeline? _timeline;
    private StageBand? _band;
    private StageCrowd? _crowd;
    private StageFixtures? _fixtures;
    private StageActors? _actors;
    private StageCameraPath? _cameraPath;
    private double _lastTime = double.NaN;
    private int _lastCombo = -1;
    private Camera? _playerCamera;
    private CameraClearFlags _playerClearFlags;
    private bool _attempted;

    public GameplayStage(string assetsDirectory)
    {
        var name = Environment.GetEnvironmentVariable("GH3_SKIN_STAGE") ?? "gh3.budokan";
        _directory = name == "off" || !StagePack.ValidId(name) ? "" : Path.Combine(assetsDirectory, "stages", name);
        _library = Path.Combine(assetsDirectory, "libraries");
        _events = Path.Combine(assetsDirectory, "stages", "events");
    }

    public void Reset()
    {
        if (_playerCamera != null) _playerCamera.clearFlags = _playerClearFlags;
        _playerCamera = null;
        foreach (var (camera, mask) in _cameras)
            if (camera != null) camera.cullingMask = mask;
        _cameras.Clear();
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        _band?.Dispose();
        _band = null;
        _crowd?.Dispose();
        _crowd = null;
        _fixtures?.Dispose();
        _fixtures = null;
        _actors?.Dispose();
        _actors = null;
        _cameraPath = null;
        _assets?.Dispose();
        _assets = null;
        _effects = null;
        _handFire = null;
        _timeline = null;
        _lastTime = double.NaN;
        _lastCombo = -1;
        _camera = null;
        _root = null;
        _attempted = false;
    }

    public void Update(BasePlayer player, int combo)
    {
        if (!_attempted) Create(player);
        var time = player.gameManager.songTime;
        if (_effects == null || _timeline == null || !double.IsFinite(time) || time == _lastTime && combo == _lastCombo) return;
        _lastTime = time;
        _lastCombo = combo;
        try
        {
            _band?.Update(time);
            _actors?.Update(time);
            _crowd?.Update(time);
            if (_cameraPath != null && _camera != null && _root != null)
            {
                var pose = _cameraPath.Sample(_timeline, time);
                _camera.transform.localPosition = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
                if (pose.Rotation is { } q) _camera.transform.localRotation = new Quaternion(q.X, q.Y, q.Z, q.W);
                else _camera.transform.LookAt(_root.transform.position + new Vector3(pose.Target.X, pose.Target.Y, pose.Target.Z));
                _camera.fieldOfView = pose.FieldOfView;
            }
            _effects.Update(_timeline, time);
            _handFire?.Update(time, combo);
            if (_camera != null && _assets != null)
                foreach (var mesh in _assets.TransparentMeshes) mesh.Update(_camera);
        }
        catch (Exception error)
        {
            Reset();
            _attempted = true;
            MelonLogger.Warning($"GH3 stage stopped: {error.Message}");
        }
    }

    private void Create(BasePlayer player)
    {
        _attempted = true;
        if (_directory.Length == 0) return;
        if (!File.Exists(Path.Combine(_directory, "pack.json")))
        {
            MelonLogger.Warning("Stage pack is missing. Run import-stage.py and install.sh.");
            return;
        }
        try
        {
            var pack = StagePack.Load(_directory);
            var library = Path.Combine(_library, pack.Library);
            _root = new GameObject("GH3 Stage") { layer = Layer };
            var lighting = StageLightingBank.Load(_directory);
            _assets = new StageAssets();
            _assets.Load(_directory, _root, Layer, lighting: lighting != null);
            _band = new StageBand();
            _band.Load(Path.Combine(library, "band"), _assets, _root, Layer, lighting != null, pack.Band);
            _fixtures = new StageFixtures();
            _fixtures.Load(Path.Combine(_directory, "fixtures"), _root, Layer);
            _actors = new StageActors();
            _actors.Load(_directory, _assets, _root, Layer);
            var definition = _assets.Definition;
            _cameraPath = new StageCameraPath(new StageCameraPose(
                new System.Numerics.Vector3(definition.CameraPosition[0], definition.CameraPosition[1], definition.CameraPosition[2]),
                new System.Numerics.Vector3(definition.CameraTarget[0], definition.CameraTarget[1], definition.CameraTarget[2]),
                definition.FieldOfView, "fixed"),
                _band.Positions.ToDictionary(pair => pair.Key, pair => new System.Numerics.Vector3(pair.Value.x, pair.Value.y, pair.Value.z)),
                Environment.GetEnvironmentVariable("GH3_SKIN_CAMERA") ?? "auto",
                StageCameraBank.Load(Path.Combine(_directory, "cameras.json")), _band.Anchor)
                { Aspect = player.mainCamera.aspect };
            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                _cameras.Add((camera, camera.cullingMask));
                camera.cullingMask &= ~((1 << Layer) | (1 << StageCrowd.Layer));
            }
            var cameraObject = new GameObject("GH3 Stage Camera");
            cameraObject.transform.SetParent(_root.transform, false);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.cullingMask = 1 << Layer;
            _camera.depth = player.mainCamera.depth - 0.1f;
            _playerCamera = player.mainCamera;
            _playerClearFlags = _playerCamera.clearFlags;
            _playerCamera.clearFlags = CameraClearFlags.Depth;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.006f, 0.008f, 0.015f, 1);
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 180;
            _camera.fieldOfView = _assets.Definition.FieldOfView;
            _camera.transform.localPosition = StageAssets.Vector(_assets.Definition.CameraPosition);
            _camera.transform.LookAt(_root.transform.position + StageAssets.Vector(_assets.Definition.CameraTarget));
            _crowd = new StageCrowd();
            _crowd.Load(Path.Combine(library, "crowd"), _assets, _root, _camera);
            var performance = StagePerformance.Load(Path.Combine(library, "direction", "performance.json"));
            var entry = GlobalVariables.instance?.currentSongEntry;
            try
            {
                _timeline = entry == null ? new StageTimeline(Array.Empty<StageCue>(), "song path unavailable") :
                    StageChartReader.Load(entry.folderPath, entry.ChartPath, _events, entry.Name_StrippedTags, entry.Artist_StrippedTags, performance);
            }
            catch (Exception error)
            {
                _timeline = new StageTimeline(Array.Empty<StageCue>(), "invalid venue cues");
                MelonLogger.Warning($"GH3 stage cues could not load: {error.Message}");
            }
            _band.SetTimeline(Path.Combine(library, "direction"), _timeline, pack.Waypoints, performance);
            _effects = new StageEffects(_assets, _root, _camera, Layer,
                lighting == null ? null : new StageLighting(lighting, _timeline), _fixtures, pack.Effects);
            _handFire = StageHandFire.Load(Path.Combine(library, "hand-fire"), _assets, _root, _camera, Layer, _band);
            MelonLogger.Msg($"GH3 stage loaded: {_assets.Definition.Name}");
            MelonLogger.Msg($"GH3 stage cues: {_timeline.CueCount} from {_timeline.Source}; {_timeline.Effects.Length} effects");
        }
        catch (Exception error)
        {
            Reset();
            _attempted = true;
            MelonLogger.Warning($"GH3 stage could not load: {error.Message}");
        }
    }
}
