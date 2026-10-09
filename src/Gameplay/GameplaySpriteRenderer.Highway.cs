using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed partial class GameplaySpriteRenderer
{
    private Transform? _neck;
    private bool _highwayApplied;
    private HighwayScroll? _scroll;
    private float _stockNeckHeight;
    private float _speedSet;
    private bool _noteRenderersSet;
    private SpriteRenderer? _neckRenderer;
    private static readonly int FadeParams = Shader.PropertyToID("_FadeParams");

    // Clone Hero's own custom-highway path: HighwayScroll sizes the scroll to the texture, so the board moves with the notes.
    private void ApplyHighway(GameObject neck)
    {
        if (_highwayApplied || _assets.HighwayTexture == null) return;
        _highwayApplied = true;
        var scroll = neck.GetComponent<HighwayScroll>() ?? neck.GetComponentInParent<HighwayScroll>() ?? _player?.GetComponentInChildren<HighwayScroll>(true);
        if (scroll == null) { MelonLogger.Warning("GH3 highway: HighwayScroll was not found"); return; }
        scroll.Method_Public_Void_Texture2D_0(_assets.HighwayTexture);
        _scroll = scroll;
        MelonLogger.Msg("Applied GH3 highway through Clone Hero's highway scroll");
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
            renderer.noteZPosFarLimit = HighwayView.NeckStartZ + HighwayView.NeckLength * HighwayView.NeckSpriteLength;
            renderer.field_Protected_Double_0 = renderer.noteZPosFarLimit / renderer.noteSpeed;
            _noteRenderer = renderer;
            _noteRenderersSet = true;
        }
    }

    // The track shader fades between two viewport heights (_FadeParams z and w, from the top). Clone Hero's
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
        if (fade.z == start && fade.w == end) return;
        fade.z = start;
        fade.w = end;
        material.SetVector(FadeParams, fade);
    }
}
