using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed class GameplayStage
{
    private readonly StageNoteHits _noteHits = new();
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
    private StageHighwayMask? _highwayMask;
    private static readonly int EyeId = Shader.PropertyToID("_Gh3Eye");
    private StageTimeline? _timeline;
    private StageBand? _band;
    private StageCrowd? _crowd;
    private StageFixtures? _fixtures;
    private StageActors? _actors;
    private StageAudio? _audio;
    private StageScripts? _scripts;
    private StageParticles? _particles;
    private StageMovies? _movies;
    private StageScreenEffects? _screenEffects;
    private StageOutlines? _outlines;
    private AssetBundle? _surfaceBundle;
    private StageCameraPath? _cameraPath;
    private double _lastTime = double.NaN;
    private int _lastCombo = -1;
    private float _lastRock = float.NaN;
    private Camera? _playerCamera;
    private CameraClearFlags _playerClearFlags;
    private int _playerCullingMask;
    private bool _attempted;
    private bool _leadInExtended;
    private float _highwayRise;
    public double WinTime { get; private set; } = double.PositiveInfinity;
    public bool ShowFlow { get; private set; }

    public GameplayStage(string assetsDirectory)
    {
        var name = Environment.GetEnvironmentVariable("GH3_SKIN_STAGE") ?? "gh3.budokan";
        _directory = name == "off" || !StagePack.ValidId(name) ? "" : Path.Combine(assetsDirectory, "stages", name);
        _library = Path.Combine(assetsDirectory, "libraries");
        _events = Path.Combine(assetsDirectory, "stages", "events");
    }

    public void Reset()
    {
        _outlines?.Dispose();
        _outlines = null;
        _screenEffects?.Dispose();
        _screenEffects = null;
        _noteHits.Reset();
        if (_playerCamera != null)
        {
            _playerCamera.clearFlags = _playerClearFlags;
            _playerCamera.cullingMask = _playerCullingMask;
            _playerCamera.ResetProjectionMatrix();
        }
        _highwayRise = 0;
        _playerCamera = null;
        WinTime = double.PositiveInfinity;
        ShowFlow = false;
        _leadInExtended = false;
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
        _audio?.Dispose();
        _audio = null;
        _scripts?.Dispose();
        _scripts = null;
        _particles?.Dispose();
        _particles = null;
        _movies = null;
        _cameraPath = null;
        _assets?.Dispose();
        _assets = null;
        _surfaceBundle?.Unload(true);
        _surfaceBundle = null;
        _effects = null;
        _handFire = null;
        _highwayMask = null;
        _timeline = null;
        _lastTime = double.NaN;
        _lastCombo = -1;
        _lastRock = float.NaN;
        _camera = null;
        _root = null;
        _attempted = false;
    }

    public void Update(BasePlayer player, int combo, float rock)
    {
        if (!_attempted) Create(player);
        if (_effects == null || _timeline == null) return;
        ExtendLeadIn(player.gameManager);
        var time = player.gameManager.songTime;
        if (!double.IsFinite(time)) return;
        try
        {
            if (_playerCamera != null && ShowFlow) ShowHighway(time);
            _audio?.Update(time, rock, time == _lastTime);
            _highwayMask?.Update(player);
            if (time == _lastTime && combo == _lastCombo && rock == _lastRock) return;
            _lastTime = time;
            _lastCombo = combo;
            _lastRock = rock;
            FrameProfile.Measure("band", () => _band?.Update(time));
            FrameProfile.Measure("scripts", () => _scripts?.Update(time, rock, _noteHits.Update(player, time)));
            _assets?.UpdateUvWibble(time);
            FrameProfile.Measure("actors", () => _actors?.Update(time));
            FrameProfile.Measure("crowd", () => _crowd?.Update(time, rock));
            if (_cameraPath != null && _camera != null && _root != null)
            {
                var pose = _cameraPath.Sample(_timeline, time);
                _camera.transform.localPosition = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
                if (pose.Rotation is { } q) _camera.transform.localRotation = new Quaternion(q.X, q.Y, q.Z, q.W);
                else _camera.transform.LookAt(_root.transform.position + new Vector3(pose.Target.X, pose.Target.Y, pose.Target.Z));
                _camera.fieldOfView = pose.FieldOfView;
            }
            if (_camera != null) _crowd?.Face(_camera);
            if (_camera != null && _assets != null && _assets.CharacterMaterials.Count > 0)
            {
                var eye = _camera.transform.position;
                var eyeVector = new Vector4 { x = eye.x, y = eye.y, z = eye.z, w = 1 };
                foreach (var material in _assets.CharacterMaterials) material.SetVector(EyeId, eyeVector);
            }
            FrameProfile.Measure("effects", () => _effects.Update(_timeline, time));
            FrameProfile.Measure("particles", () => _particles?.Update(time));
            FrameProfile.Measure("movies", () => _movies?.Update(time));
            FrameProfile.Measure("screen", () => _screenEffects?.Update(time));
            FrameProfile.Measure("outlines", () => _outlines?.Update(time));
            FrameProfile.Measure("handfire", () => _handFire?.Update(time, combo));
            FrameProfile.Measure("transparency", () =>
            {
                if (_camera != null && _assets != null)
                    foreach (var mesh in _assets.TransparentMeshes) mesh.Update(_camera);
            });
        }
        catch (Exception error)
        {
            Reset();
            _attempted = true;
            MelonLogger.Warning($"GH3 stage stopped: {error.Message}");
        }
    }

    // Clone Hero holds the song clock at -1 s while the song loads. GH3's intro transition starts 8 s before the song,
    // so the clock starts there instead; the audio still starts at song time 0.
    private void ExtendLeadIn(GameManager manager)
    {
        if (_leadInExtended || GlobalVariables.instance?.isPracticeEnabled == true) return;
        var time = manager.songTime;
        if (manager.isSongPlaying || time >= 0) { _leadInExtended = true; return; }
        if (!manager.isPaused || time <= -StageShowFlow.IntroLength) return;
        // The song clock (GameManager.sync) restarts from its own start time when playback begins.
        var clock = manager.sync;
        if (clock == null || clock.field_Private_Double_2 != time) return;
        clock.field_Private_Double_2 = clock.field_Private_Double_3 = -StageShowFlow.IntroLength;
        manager.songTime = -StageShowFlow.IntroLength;
        _leadInExtended = true;
    }

    // GH3 draws the highway in a screen element that rises from below before the song (move_highway_2d) and hides it
    // 0.5 s after the song is won (waitAndKillHighway). A vertical projection offset moves Clone Hero's highway the same way.
    private void ShowHighway(double time)
    {
        var camera = _playerCamera!;
        var hidden = time >= WinTime + StageShowFlow.HighwayHideDelay || IntroHudAnimation.HighwayOffset(time) >= 720;
        camera.cullingMask = hidden ? 0 : _playerCullingMask;
        var rise = IntroHudAnimation.HighwayOffset(time);
        if (rise == _highwayRise) return;
        _highwayRise = rise;
        camera.ResetProjectionMatrix();
        if (rise <= 0) return;
        var projection = camera.projectionMatrix;
        projection.m12 += rise / 360;
        camera.projectionMatrix = projection;
    }

    private static double? LastNoteEnd(BasePlayer player)
    {
        var notes = player.engine?.prop_List_1_ObjectPublicObInObDoSiDoUIInBoInUnique_0;
        if (notes == null || notes.Count == 0) return null;
        var end = double.NegativeInfinity;
        for (var index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            // The second time is the end of a sustain; it equals the note time for other notes.
            end = Math.Max(end, Math.Max(note.field_Public_Double_0, note.field_Public_Double_1));
        }
        return double.IsFinite(end) ? end : null;
    }

    private Shader? LoadSurfaceShader(string library)
    {
        var path = Path.Combine(library, "render", "stage.bundle");
        if (!File.Exists(path))
        {
            MelonLogger.Warning("GH3 stage surface shader is missing. Run prepare-stage-shaders.py.");
            return null;
        }
        _surfaceBundle = AssetBundle.LoadFromFile(path) ?? throw new InvalidDataException("Stage surface shader bundle did not load");
        var shader = _surfaceBundle.mainAsset?.Cast<Shader>() ?? throw new InvalidDataException("Stage surface shader is missing");
        shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        if (!shader.isSupported) throw new InvalidDataException("Stage surface shader is not supported by this graphics API");
        return shader;
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
            var surfaceShader = LoadSurfaceShader(library);
            _assets.Load(_directory, _root, Layer, lighting: lighting != null, surfaceShader: surfaceShader);
            _band = new StageBand();
            _band.Load(Path.Combine(library, "band"), _assets, _root, Layer, lighting != null, pack.Band, surfaceShader);
            _fixtures = new StageFixtures();
            _fixtures.Load(Path.Combine(_directory, "fixtures"), _root, Layer);
            _actors = new StageActors();
            _actors.Load(_directory, _assets, _root, Layer, surfaceShader);
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
            _fixtures.View = _camera;
            _camera.cullingMask = 1 << Layer;
            _camera.depth = player.mainCamera.depth - 0.1f;
            _playerCamera = player.mainCamera;
            _playerClearFlags = _playerCamera.clearFlags;
            _playerCullingMask = _playerCamera.cullingMask;
            _playerCamera.clearFlags = CameraClearFlags.Depth;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.006f, 0.008f, 0.015f, 1);
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 180;
            _camera.fieldOfView = _assets.Definition.FieldOfView;
            _camera.transform.localPosition = StageAssets.Vector(_assets.Definition.CameraPosition);
            _camera.transform.LookAt(_root.transform.position + StageAssets.Vector(_assets.Definition.CameraTarget));
            _crowd = new StageCrowd();
            _crowd.Load(Path.Combine(library, "crowd"), _assets, _root, _camera, pack.Crowd, surfaceShader);
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
            if (GlobalVariables.instance?.isPracticeEnabled != true)
            {
                var lastNote = LastNoteEnd(player);
                if (lastNote is { } end) WinTime = StageShowFlow.WinTime(end);
                _timeline = StageShowFlow.Apply(_timeline, pack.Id, lastNote == null ? null : WinTime);
                ShowFlow = true;
                var manager = player.gameManager;
                // Clone Hero ends the song at songLength; the win transition needs its full 8 s first.
                if (double.IsFinite(WinTime) && manager.songLength < WinTime + StageShowFlow.WinLength)
                    manager.songLength = WinTime + StageShowFlow.WinLength;
            }
            _band.SetTimeline(Path.Combine(library, "direction"), _timeline, pack.Waypoints, performance);
            _actors.SetTimeline(_timeline);
            _audio = StageAudio.Load(Path.Combine(library, "audio"), _timeline, pack.Id, GlobalVariables.instance?.isPracticeEnabled == true);
            var stageLighting = lighting == null ? null : new StageLighting(lighting, _timeline);
            _scripts = StageScripts.Load(_directory, library, _assets, _fixtures, _actors, stageLighting, _timeline, pack.Id);
            if (_scripts != null)
            {
                var viewTime = double.NaN;
                var viewPlanes = Array.Empty<System.Numerics.Vector4>();
                var viewPosition = System.Numerics.Vector3.Zero;
                _scripts.State.ParticleView = (position, visibility, time) =>
                {
                    if (time != viewTime)
                    {
                        var prior = _cameraPath.Sample(_timeline, time - 1.0 / 60);
                        viewPlanes = StageParticleVisibility.Planes(prior, _cameraPath.Aspect, _camera.nearClipPlane, _camera.farClipPlane);
                        viewPosition = _cameraPath.Sample(_timeline, time).Position;
                        viewTime = time;
                    }
                    return (System.Numerics.Vector3.Distance(position, viewPosition), visibility.Test(viewPlanes));
                };
                _particles = new(_scripts, _assets, _root, _camera, Layer, Path.Combine(library, "render", "particle.bundle"));
                _movies = new(_assets, _scripts);
                var shaders = Path.Combine(library, "render", "screen-color.bundle");
                if (File.Exists(shaders)) _screenEffects = new(_scripts, _camera, shaders);
                else MelonLogger.Warning("GH3 stage screen shaders are missing. Run prepare-stage-shaders.py.");
                var outlines = Path.Combine(library, "render", "outline.bundle");
                if (File.Exists(outlines)) _outlines = new(_scripts, _camera, _assets, _actors, outlines);
            }
            _crowd.SetTimeline(_timeline);
            _effects = new StageEffects(_assets, _root, _camera, Layer,
                stageLighting, _fixtures, pack.Effects) { SourcePyro = _scripts?.State.SourcePyro == true };
            if (surfaceShader != null) _highwayMask = new StageHighwayMask(_assets, _camera, surfaceShader, Layer);
            _handFire = StageHandFire.Load(Path.Combine(library, "hand-fire"), _assets, _root, _camera, Layer, _band);
            MelonLogger.Msg($"GH3 stage loaded: {_assets.Definition.Name}");
            MelonLogger.Msg($"GH3 stage cues: {_timeline.CueCount} from {_timeline.Source}; {_timeline.Effects.Length} effects");
            foreach (var diagnostic in _timeline.Diagnostics) MelonLogger.Msg(diagnostic);
            if (_timeline.NativeSong is { } nativeSong)
                MelonLogger.Msg($"GH3 native cameras (first 8): {string.Join(", ", nativeSong.Notes("cameras").Take(8).Select(note => $"{note.Time}:{note.Note}:{note.Length}"))}");
        }
        catch (Exception error)
        {
            Reset();
            _attempted = true;
            MelonLogger.Warning($"GH3 stage could not load: {error}");
        }
    }
}
