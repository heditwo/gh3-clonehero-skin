using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// The highway: GH3 board texture, rails, and the highway scroll.

internal sealed partial class GameplaySpriteRenderer
{
    private Transform? _neck;
    private bool _highwayApplied;
    private HighwayScroll? _scroll;
    private float _stockNeckHeight;
    private float _speedSet;
    private bool _noteRenderersSet;
    private SpriteRenderer? _neckRenderer;
    private readonly List<BeatlineVisual> _activeBeatlines = new();
    private readonly List<SpriteRenderer> _thinBeatlines = new();
    private Transform? _thinBeatlineRoot;
    private TrackFadeManager? _trackFade;
    private bool _beatlineLimitLogged;
    private static readonly int FadeParams = Shader.PropertyToID("_FadeParams");

    // Clone Hero's own custom-highway path: HighwayScroll sizes the scroll to the texture, so the board moves with the notes.
    private void ApplyHighway(GameObject neck)
    {
        if (_highwayApplied || _assets.HighwayTexture == null) return;
        _highwayApplied = true;
        var scroll = neck.GetComponent<HighwayScroll>() ?? neck.GetComponentInParent<HighwayScroll>() ?? _player?.GetComponentInChildren<HighwayScroll>(true);
        if (scroll == null) { MelonLogger.Warning("GH3 highway: HighwayScroll was not found"); return; }
        CloneHeroBindings.SetHighwayTexture(scroll, _assets.HighwayTexture);
        _scroll = scroll;
        _appliedHighway = "";
        MelonLogger.Msg("Applied GH3 highway through Clone Hero's highway scroll");
    }

    private string _appliedHighway = "";

    // The stage chooses the highway of the player's band member when the band loads (HighwayChoice).
    private void FollowHighwayChoice()
    {
        if (_scroll == null || HighwayChoice.Path == _appliedHighway) return;
        var texture = HighwayChoice.Path.Length == 0 ? _assets.HighwayTexture : HighwayChoice.Texture(HighwayChoice.Path);
        _appliedHighway = HighwayChoice.Path;
        if (texture == null) return;
        CloneHeroBindings.SetHighwayTexture(_scroll, texture);
        MelonLogger.Msg($"GH3 highway: applied {HighwayChoice.Source}");
    }

    // HighwayScroll sizes the texture for the stock neck. The skin sets GH3's repeat length and scrolls it with the notes.
    private void KeepHighwayAtNoteSpeed()
    {
        if (_scroll == null || _stockNeckHeight <= 0) return;
        var tiling = _scroll.field_Private_Vector4_0;
        if (tiling.y != HighwayView.TextureTiling)
        {
            tiling.y = HighwayView.TextureTiling;
            _scroll.field_Private_Vector4_0 = tiling;
        }
        var speed = _scroll.field_Private_Single_1;
        if (Math.Abs(speed - _speedSet) < 1e-6f || speed <= 0) return;
        _speedSet = HighwayView.ScrollSpeed(speed);
        _scroll.field_Private_Single_1 = _speedSet;
    }

    // Clone Hero spawns gems 7.87 units ahead; GH3 shows them up to the top of its highway.
    private void ShowNotesToHighwayTop()
    {
        if (_noteRenderersSet) return;
        foreach (var renderer in UnityEngine.Object.FindObjectsOfType<GuitarNoteRenderer>())
        {
            renderer.noteZPosFarLimit = HighwayView.NeckEndZ;
            renderer.field_Protected_Double_0 = renderer.noteZPosFarLimit / renderer.noteSpeed;
            _noteRenderer = renderer;
            _noteRenderersSet = true;
        }
    }

    // TrackFadeManager sends each fade receiver its far and near limits (x and z of its entry) every frame; the beat line
    // spawner stops at x (7.87). The skin moves the beat line entry to the highway top, as for the gems.
    private void ShowBeatlinesToHighwayTop()
    {
        if (_trackFade == null) _trackFade = _player?.GetComponentInChildren<TrackFadeManager>() ?? UnityEngine.Object.FindObjectOfType<TrackFadeManager>();
        if (_trackFade == null) return;
        var receivers = _trackFade.field_Private_List_1_InterfacePublicAbstract18Vo2818Unique_0;
        var limits = _trackFade.field_Private_List_1_ValueTypePublicSealedSiSiSiSiUnique_0;
        if (receivers == null || limits == null) return;
        var found = false;
        for (var index = 0; index < receivers.Count && index < limits.Count; index++)
        {
            if (receivers[index]?.TryCast<BeatRenderer>() == null) continue;
            found = true;
            var limit = limits[index];
            if (limit.field_Public_Single_0 == HighwayView.NeckEndZ) continue;
            if (!_beatlineLimitLogged)
                MelonLogger.Msg($"GH3 beat lines: spawn limit {limit.field_Public_Single_0:0.##} -> {HighwayView.NeckEndZ:0.##} (highway top)");
            _beatlineLimitLogged = true;
            limit.field_Public_Single_0 = HighwayView.NeckEndZ;
            limits[index] = limit;
        }
        if (!found && !_beatlineLimitLogged && receivers.Count > 0)
        {
            MelonLogger.Warning("GH3 beat lines: Clone Hero's track fade has no beat line entry; beat lines stop below the highway top");
            _beatlineLimitLogged = true;
        }
    }

    // GH3 adds a thin fretbar halfway to the next beat (HighwayView.HasThinFretbar). Clone Hero has beat lines only, so the
    // skin takes the beat length from the next active line, or from the previous beat for the farthest line.
    private void UpdateBeatlines(Camera camera)
    {
        ShowBeatlinesToHighwayTop();
        _activeBeatlines.Clear();
        foreach (var line in _beatlines)
        {
            line.Update(camera);
            if (line.Active)
                _activeBeatlines.Add(line);
        }
        _activeBeatlines.Sort((a, b) => a.Transform.localPosition.z.CompareTo(b.Transform.localPosition.z));
        var noteSpeed = _noteRenderer != null && _noteRenderer.noteSpeed > 0 ? _noteRenderer.noteSpeed : HighwayView.NoteSpeed;
        var beatLength = 0f;
        var used = 0;
        for (var index = 0; index < _activeBeatlines.Count; index++)
        {
            var line = _activeBeatlines[index].Transform;
            var position = line.localPosition;
            if (index + 1 < _activeBeatlines.Count)
                beatLength = _activeBeatlines[index + 1].Transform.localPosition.z - position.z;
            if (!HighwayView.HasThinFretbar(beatLength / noteSpeed))
                continue;
            var thin = ThinBeatline(line.parent, used++);
            var z = position.z + beatLength / 2;
            var parent = line.parent;
            BeatlineVisual.Place(camera, thin, thin.transform, parent.TransformPoint(new Vector3(position.x, position.y, z)), parent.right,
                position.x, position.y, z, 1, 1);
            thin.enabled = true;
        }
        for (var index = used; index < _thinBeatlines.Count; index++)
            if (_thinBeatlines[index] != null)
                _thinBeatlines[index].enabled = false;
    }

    private SpriteRenderer ThinBeatline(Transform highway, int index)
    {
        if (_thinBeatlineRoot == null) _thinBeatlines.Clear();
        while (_thinBeatlines.Count <= index)
        {
            if (_thinBeatlineRoot == null)
            {
                _thinBeatlineRoot = new GameObject("GH3 Thin Beat Lines").transform;
                _thinBeatlineRoot.gameObject.layer = highway.gameObject.layer;
                _thinBeatlineRoot.SetParent(highway, false);
            }
            var line = BeatlineVisual.CreateDisplay(_thinBeatlineRoot, "GH3 Thin Beat Line", _assets);
            line.sprite = _assets.GetSprite("beatline-half");
            _thinBeatlines.Add(line);
        }
        return _thinBeatlines[index];
    }

    // The track shader fades between two viewport heights (_FadeParams z and w, from the top in OpenGL). Clone Hero's
    // TrackFadeManager gives the sustain material its own, lower fade rows and sets them again when it recalculates
    // (for example after the intro rise). GH3 fades the sustains with the board, so the skin keeps all of them on GH3's rows.
    private void FadeHighwayTop()
    {
        if (_neckRenderer == null) _neckRenderer = _neck?.GetComponent<SpriteRenderer>();
        FadeLikeHighway(_neckRenderer?.sharedMaterial);
        foreach (var material in _sustainMaterials)
            FadeLikeHighway(material);
    }

    private static void FadeLikeHighway(Material? material)
    {
        if (material == null || !material.HasProperty(FadeParams)) return;
        var fade = material.GetVector(FadeParams);
        var start = HighwayView.FadeStartY / HighwayView.ScreenHeight;
        var end = (HighwayView.FadeStartY + HighwayView.FadeLength) / HighwayView.ScreenHeight;
        // Where the viewport starts at the top (Direct3D 11), TrackFadeManager writes 1 - v for its own rows; the shader expects the same.
        if (SystemInfo.graphicsUVStartsAtTop)
        {
            start = 1 - start;
            end = 1 - end;
        }
        if (fade.z == start && fade.w == end) return;
        fade.z = start;
        fade.w = end;
        material.SetVector(FadeParams, fade);
    }
}
