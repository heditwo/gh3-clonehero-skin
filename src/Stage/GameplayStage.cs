using System.Runtime;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// The 3D stage behind the highway: loads it for each song and updates its parts on the song clock in order.

internal sealed partial class GameplayStage
{    private const float ThroughViewAim = 0.2f;
    // The stage camera draws just before the player camera.
    private const float CameraDepthBelow = 0.1f;
    private const float NearClip = 0.1f;
    private const double BytesPerMegabyte = 1e6;

    private readonly StageNoteHits _noteHits = new();
    private const int Layer = 30;
    private readonly string _assetsDirectory;
    private string _stageId = "";
    private Packages.StageDecision? _decision;
    private Packages.InstalledPackage? _songPackage;
    private readonly Dictionary<string, Packages.InstalledPackage> _characterPackages = new();
    private readonly Dictionary<string, Packages.InstalledPackage> _instrumentPackages = new();
    private static string _previousRandom = "";
    private static readonly System.Random StageRandom = new();
    private string _directory = "";
    private string _library = "";
    private string _events = "";
    private string _render = "";
    private string _effectsDirectory = "";
    private string _waitingFor = "";
    private readonly List<(Camera Camera, int Mask)> _cameras = new();
    private GameObject? _root;
    private StageAssets? _assets;
    private Camera? _camera;
    private StageEffects? _effects;
    private StageHandFire? _handFire;
    private StageHighwayMask? _highwayMask;
    private StageTimeline? _timeline;
    private System.Diagnostics.Stopwatch? _vocalClock;
    private StageBand? _band;
    private StageCrowd? _crowd;
    private StageFixtures? _fixtures;
    private StageActors? _actors;
    private StageAudio? _audio;
    public string AudioVoices => _audio?.Voices ?? "none";
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
        _assetsDirectory = assetsDirectory;
        PerformanceClipCache.Report = message => MelonLogger.Warning("GH3 clips: " + message);
    }

    private enum SourceState { Ready, Waiting, Missing, Failed }

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
        _waitingFor = "";
        _decision = null;
        _songPackage = null;
        _characterPackages.Clear();
        _instrumentPackages.Clear();
        HighwayChoice.Clear();
        if (PerformanceClipCache.LateLoads > 0)
            MelonLogger.Warning($"GH3 clips: {PerformanceClipCache.LateLoads} clips loaded on the main thread during play ({PerformanceClipCache.LateMilliseconds:0.0} ms)");
        PerformanceClipCache.LateLoads = 0;
        PerformanceClipCache.LateMilliseconds = 0;
    }

    public void Update(BasePlayer player, int combo, float rock)
    {
        if (!_attempted) Create(player);
        if (_effects == null || _timeline == null) return;
        ReportVocalEnvelope();
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
            if (_camera != null && _assets != null) ApplyDebugView(_camera, _assets);
            if (_camera != null) SetTemplateGlobals(_camera, time);
            if (_camera != null) _assets?.ScaleToonOutlines(_camera.fieldOfView);
            if (_camera != null) _crowd?.Face(_camera);
            if (_camera != null) _assets?.FaceBillboards(_camera);
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

    private static readonly int TimeId = Shader.PropertyToID("_Gh3Time");
    private static readonly int ViewRightId = Shader.PropertyToID("_Gh3ViewRight");
    private static readonly int ViewUpId = Shader.PropertyToID("_Gh3ViewUp");
    private static readonly int ViewForwardId = Shader.PropertyToID("_Gh3ViewForward");
    private static readonly int ViewEyeId = Shader.PropertyToID("_Gh3ViewEye");

    // Engine constants of the translated GH3 templates: c155.x time, c120-c122 camera axes, c123 eye (world space).
    // GH3's shader clock never runs negative; atlas templates with clamped samplers rely on that, so it starts with the intro.
    private static void SetTemplateGlobals(Camera camera, double time)
    {
        static Vector4 V(Vector3 v, float w) => new() { x = v.x, y = v.y, z = v.z, w = w };
        var view = camera.transform;
        Shader.SetGlobalVector(TimeId, new Vector4 { x = (float)Math.Max(0, time + StageShowFlow.IntroLength) });
        Shader.SetGlobalVector(ViewRightId, V(view.right, 0));
        Shader.SetGlobalVector(ViewUpId, V(view.up, 0));
        Shader.SetGlobalVector(ViewForwardId, V(view.forward, 0));
        Shader.SetGlobalVector(ViewEyeId, V(view.position, 1));
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
        var hidden = time >= WinTime + StageShowFlow.HighwayHideDelay || IntroHudAnimation.HighwayOffset(time) >= IntroHudAnimation.HighwayDrop;
        camera.cullingMask = hidden ? 0 : _playerCullingMask;
        var rise = IntroHudAnimation.HighwayOffset(time);
        if (rise == _highwayRise) return;
        _highwayRise = rise;
        camera.ResetProjectionMatrix();
        if (rise <= 0)
        {
            // Clone Hero computed the track fade of its "Fade Instance" materials (sustains, gem bodies, hit flames)
            // through the raised projection, one viewport below the highway, which hides them. Compute it again.
            foreach (var fade in UnityEngine.Object.FindObjectsOfType<TrackFadeManager>())
                CloneHeroBindings.RecalculateTrackFade(fade);
            return;
        }
        var projection = camera.projectionMatrix;
        projection.m12 += rise / 360;
        camera.projectionMatrix = projection;
    }

    private static double? LastNoteEnd(BasePlayer player)
    {
        var notes = player.engine == null ? null : CloneHeroBindings.Notes(player.engine);
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

    private Shader? LoadSurfaceShader(string render)
    {
        var path = Path.Combine(render, "stage.bundle");
        if (!File.Exists(path))
        {
            MelonLogger.Warning("GH3 stage surface shader is missing. Run scripts/regenerate.sh render.");
            return null;
        }
        _surfaceBundle = AssetBundle.LoadFromFile(path) ?? throw new InvalidDataException("Stage surface shader bundle did not load");
        var shader = StageShader.Load(_surfaceBundle, "the stage surface shader");
        var templates = Path.Combine(render, "stage-templates.json");
        StageAssets.Templates = File.Exists(templates) ? StageShaderTemplates.Parse(File.ReadAllText(templates)) : null;
        if (StageAssets.Templates == null) MelonLogger.Warning("GH3 material template table is missing. Run scripts/regenerate.sh render.");
        return shader;
    }

    // The model folders that this stage load builds: the stage, its fixtures and actors, the band, the crowd, and the
    // characters and instruments. Worker threads read them while the main thread builds the scene in order.
    private void PrefetchModels()
    {
        StageModelPrefetch.Start(_directory, skinned: false);
        StageModelPrefetch.StartAll(Path.Combine(_directory, "fixtures"), skinned: false);
        StageModelPrefetch.StartAll(Path.Combine(_directory, "actors"), skinned: true);
        if (Directory.Exists(Path.Combine(_directory, "actors")))
            foreach (var actor in Directory.EnumerateDirectories(Path.Combine(_directory, "actors")))
                StageModelPrefetch.StartAnimation(Path.Combine(actor, "animation.bin"));
        StageModelPrefetch.StartAll(Path.Combine(_library, "band"), skinned: true);
        foreach (var package in _characterPackages.Values.Concat(_instrumentPackages.Values))
            if (package.State == Packages.PackageState.Ready) StageModelPrefetch.Start(package.CacheDirectory, skinned: true);
        StageModelPrefetch.StartAll(Path.Combine(_library, "crowd"), skinned: true);
        StageModelPrefetch.StartAll(Path.Combine(_library, "crowd", "hands"), skinned: true);
    }

    // The vocal stem loudness for the lip sync, decoded on a worker thread while the stage loads (VocalEnvelope).
    private void StartVocalEnvelope(string? folder)
    {
        VocalEnvelope.Current = null;
        _vocalClock = null;
        if (!StageLipSync.Enabled || folder == null || _timeline == null || _timeline.Track("face", "singer").Length > 0 || _timeline.Track("sing", "singer").Length == 0) return;
        try
        {
            if (!StageAudioBackend.Available) return;
        }
        catch (Exception error) when (error is TypeInitializationException or FileNotFoundException or DllNotFoundException)
        {
            MelonLogger.Warning($"GH3 lip sync: no vocal stem loudness ({error.GetBaseException().Message})");
            return;
        }
        var cache = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "GH3Skin", "Cache", "vocals");
        VocalEnvelope.Current = VocalEnvelope.Start(folder, cache, StageAudioBackend.DecodeMono);
        _vocalClock = VocalEnvelope.Current == null ? null : System.Diagnostics.Stopwatch.StartNew();
        if (VocalEnvelope.Current == null) MelonLogger.Msg("GH3 lip sync: no vocal stem in the chart folder; the mouth follows the notes only");
    }

    private void ReportVocalEnvelope()
    {
        if (_vocalClock == null || VocalEnvelope.Current is not { IsCompleted: true } task) return;
        var elapsed = _vocalClock.Elapsed.TotalMilliseconds;
        _vocalClock = null;
        if (task.IsCompletedSuccessfully && task.Result is { } envelope)
            MelonLogger.Msg($"GH3 lip sync: vocal stem loudness from {envelope.Source} ({envelope.Seconds:0.0} s) ready {elapsed:0} ms after the chart was read");
        else MelonLogger.Warning($"GH3 lip sync: the vocal stem could not be decoded ({task.Exception?.GetBaseException().Message ?? "no audio"}); the mouth follows the notes only");
    }

    private void Create(BasePlayer player)
    {
        var state = ResolveSources();
        if (state == SourceState.Waiting) return;
        _attempted = true;
        if (state == SourceState.Failed) return;
        if (state == SourceState.Missing)
        {
            if (_stageId.Length > 0) MelonLogger.Warning($"Stage {_stageId} is missing: no installed package. Run the installer again.");
            return;
        }
        var loadClock = System.Diagnostics.Stopwatch.StartNew();
        var steps = new LoadTiming.Steps();
        LoadTiming.Mark("stage load starts");
        var texturesBefore = (StageAssets.TexturesUploaded, StageAssets.TextureMilliseconds);
        Task<StageScriptBank>? scriptBank = null;
        try
        {
            scriptBank = StageScripts.ReadBank(_directory, _library);
            PrefetchModels();
            if (StageLipSync.Enabled) Task.Run(LyricPhonemes.Preload);
            var pack = StagePack.Load(_directory);
            var library = _library;
            _root = new GameObject("GH3 Stage") { layer = Layer };
            var lighting = StageLightingBank.Load(_directory);
            steps.Step("pack and lighting");
            _assets = new StageAssets();
            var surfaceShader = LoadSurfaceShader(_render);
            steps.Step("surface shader");
            _assets.Load(_directory, _root, Layer, lighting: lighting != null, surfaceShader: surfaceShader);
            steps.Step("stage meshes and textures");
            _band = new StageBand();
            var characters = LoadCharacters();
            var instruments = LoadInstruments();
            steps.Step("character and instrument models");
            _band.Load(Path.Combine(library, "band"), _assets, _root, Layer, lighting != null, pack.Band, surfaceShader, characters, instruments);
            steps.Step("band");
            HighwayChoice.Choose(HighwayChoice.PlayerRole(player), characters, instruments);
            _fixtures = new StageFixtures();
            _fixtures.Load(Path.Combine(_directory, "fixtures"), _root, Layer);
            steps.Step("fixtures");
            _actors = new StageActors();
            _actors.Load(_directory, _assets, _root, Layer, surfaceShader);
            steps.Step("actors");
            var definition = _assets.Definition;
            _cameraPath = new StageCameraPath(new StageCameraPose(
                new System.Numerics.Vector3(definition.CameraPosition[0], definition.CameraPosition[1], definition.CameraPosition[2]),
                new System.Numerics.Vector3(definition.CameraTarget[0], definition.CameraTarget[1], definition.CameraTarget[2]),
                definition.FieldOfView, "fixed"),
                _band.Positions.ToDictionary(pair => pair.Key, pair => new System.Numerics.Vector3(pair.Value.x, pair.Value.y, pair.Value.z)),
                SkinEnvironment.Get(SkinEnvironment.Camera) ?? "auto",
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
            _camera.depth = player.mainCamera.depth - CameraDepthBelow;
            _playerCamera = player.mainCamera;
            _playerClearFlags = _playerCamera.clearFlags;
            _playerCullingMask = _playerCamera.cullingMask;
            _playerCamera.clearFlags = CameraClearFlags.Depth;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            // Tuned: a near-black blue behind the stage.
            _camera.backgroundColor = new Color(0.006f, 0.008f, 0.015f, 1);
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = 180;
            _camera.fieldOfView = _assets.Definition.FieldOfView;
            _camera.transform.localPosition = StageAssets.Vector(_assets.Definition.CameraPosition);
            _camera.transform.LookAt(_root.transform.position + StageAssets.Vector(_assets.Definition.CameraTarget));
            _crowd = new StageCrowd();
            _crowd.Load(Path.Combine(library, "crowd"), _assets, _root, _camera, pack.Crowd, surfaceShader);
            steps.Step("cameras and crowd");
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
            steps.Step("chart and cues");
            StartVocalEnvelope(entry?.folderPath);
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
            // The main thread waits here while worker threads decode the song's clips, so no other managed work runs
            // on the main thread at the same time (see docs/ARCHITECTURE.md, packages).
            steps.Step("band timeline");
            var (clips, clipWorker, clipWait) = _band.WaitForClips();
            steps.Step("band clips");
            _actors.SetTimeline(_timeline);
            _audio = StageAudio.Load(Path.Combine(library, "audio"), _timeline, pack.Id, GlobalVariables.instance?.isPracticeEnabled == true);
            steps.Step("audio");
            var stageLighting = lighting == null ? null : new StageLighting(lighting, _timeline);
            steps.Step("lighting");
            _scripts = StageScripts.Load(scriptBank, _assets, _fixtures, _actors, stageLighting, _timeline, pack.Id);
            steps.Step("scripts");
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
                _particles = new(_scripts, _assets, _root, _camera, Layer, Path.Combine(_render, "particle.bundle"), _effectsDirectory);
                _movies = new(_assets, _scripts);
                var shaders = Path.Combine(_render, "screen-color.bundle");
                if (File.Exists(shaders)) _screenEffects = new(_scripts, _camera, shaders);
                else MelonLogger.Warning("GH3 stage screen shaders are missing. Run scripts/regenerate.sh render.");
                var outlines = Path.Combine(_render, "outline.bundle");
                if (File.Exists(outlines)) _outlines = new(_scripts, _camera, _assets, _actors, outlines);
            }
            steps.Step("particles, movies, screen effects, outlines");
            _crowd.SetTimeline(_timeline);
            clipWait += _crowd.Clips?.Wait() ?? 0;
            _effects = new StageEffects(_assets, _root, _camera, Layer,
                stageLighting, _fixtures, pack.Effects) { SourcePyro = _scripts?.State.SourcePyro == true };
            if (surfaceShader != null) _highwayMask = new StageHighwayMask(_assets, _camera, surfaceShader, Layer);
            _handFire = StageHandFire.Load(Path.Combine(library, "hand-fire"), _assets, _root, _camera, Layer, _band);
            StageModelPrefetch.Clear();
            steps.Step("crowd clips, effects, hand fire");
            // The read-ahead buffers (meshes, JSON, clips) are garbage now; a compacting collection gives the memory back
            // before the song instead of at a random frame during it.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            steps.Step("collection");
            steps.Finish("stage loaded");
            LoadTiming.FlushSections("stage load");
            MelonLogger.Msg($"GH3 stage loaded: {_assets.Definition.Name}");
            MelonLogger.Msg($"GH3 stage load time: {loadClock.Elapsed.TotalMilliseconds:0} ms on the main thread " +
                $"({StageAssets.TexturesUploaded - texturesBefore.Item1} cache textures in {StageAssets.TextureMilliseconds - texturesBefore.Item2:0} ms; " +
                $"{clips} band clips decoded in {clipWorker:0} ms on worker threads while the main thread waited {clipWait:0} ms; managed heap {GC.GetTotalMemory(false) / BytesPerMegabyte:0} MB)");
            MelonLogger.Msg($"GH3 stage cues: {_timeline.CueCount} from {_timeline.Source}; {_timeline.Effects.Length} effects");
            foreach (var diagnostic in _timeline.Diagnostics) MelonLogger.Msg(diagnostic);
            if (_timeline.NativeSong is { } nativeSong)
                MelonLogger.Msg($"GH3 native cameras (first 8): {string.Join(", ", nativeSong.Notes("cameras").Take(8).Select(note => $"{note.Time}:{note.Note}:{note.Length}"))}");
        }
        catch (StageShaderUnsupportedException error)
        {
            StageModelPrefetch.Clear();
            Reset();
            _attempted = true;
            MelonLogger.Error(error.Message);
        }
        catch (Exception error)
        {
            StageModelPrefetch.Clear();
            Reset();
            _attempted = true;
            MelonLogger.Warning($"GH3 stage could not load: {error}");
        }
    }
}
