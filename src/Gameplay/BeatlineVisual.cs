using UnityEngine;

namespace Gh3Skin;

internal sealed class BeatlineVisual
{
    // Below the gems and the strings on the HighwayOverlay layer.
    private const int SortingOrder = -201;
    // Clone Hero scales its measure lines up and its half-beat lines down; these bounds tell them apart.
    private const float BarScale = 1.5f;
    private const float HalfBeatScale = 0.8f;
    // Reference pixels (1600 x 900 GH3 recording) per HUD canvas unit of 1080 lines: 1080 / 900.
    private const float ReferenceScale = 1.2f;
    private const float ReferenceCenterX = 800;
    private const float ReferenceFadeStartY = HighwayView.FadeStartY;
    private const float ReferenceFadeLength = HighwayView.FadeLength;
    // GH3 beat lines end at 0.955 of the rail half-width.
    private const float ReferenceWidthPerY = 2 * 0.955f * HighwayView.RailSlope;
    private readonly Transform _transform;
    private readonly GameObject _object;
    private readonly SpriteRenderer _source;
    private readonly SpriteRenderer _display;
    private readonly Transform _displayTransform;
    private readonly Sprite _bar;
    private readonly Sprite _beat;
    private readonly Sprite _halfBeat;

    public BeatlineVisual(Transform transform, SkinAssets assets)
    {
        _transform = transform;
        _object = transform.gameObject;
        _source = transform.GetComponent<SpriteRenderer>();
        _bar = assets.GetSprite("beatline");
        _beat = assets.GetSprite("beatline-beat");
        _halfBeat = assets.GetSprite("beatline-half");
        var existing = transform.Find("GH3 Beat Line");
        _display = existing != null ? existing.GetComponent<SpriteRenderer>() : CreateDisplay(transform, "GH3 Beat Line", assets);
        _displayTransform = _display.transform;
    }

    public bool Active => _object != null && _object.activeInHierarchy;
    public Transform Transform => _transform;

    public static SpriteRenderer CreateDisplay(Transform parent, string name, SkinAssets assets)
    {
        var display = new GameObject(name);
        display.layer = parent.gameObject.layer;
        display.transform.SetParent(parent, false);
        var renderer = display.AddComponent<SpriteRenderer>();
        renderer.sharedMaterial = assets.BeatlineMaterial;
        renderer.sortingLayerID = SortingLayer.NameToID("HighwayOverlay");
        renderer.sortingOrder = SortingOrder;
        return renderer;
    }

    public void Update(Camera camera)
    {
        if (!Active)
            return;
        _source.enabled = false;
        var scale = _transform.localScale;
        _display.sprite = scale.y > BarScale ? _bar : scale.y < HalfBeatScale ? _halfBeat : _beat;
        Place(camera, _display, _displayTransform, _transform.position, _transform.parent.right, 0, 0, 0, scale.x, scale.y);
    }

    // Places a line sprite at a highway point: GH3 width and distance fade, centered on the highway. local is the point in
    // the display's parent space, and scaleX, scaleY are the scale of that parent relative to the highway.
    public static void Place(Camera camera, SpriteRenderer display, Transform displayTransform, Vector3 world, Vector3 highwayRight,
        float localX, float localY, float localZ, float scaleX, float scaleY)
    {
        var position = camera.WorldToScreenPoint(world);
        var rect = camera.pixelRect;
        var viewport = HudViewport.ForHighway(Screen.height, rect.x, rect.y, rect.width, rect.height, camera.fieldOfView);
        var referenceScale = viewport.Scale * ReferenceScale;
        var referenceY = (Screen.height - position.y - viewport.CenterOffsetY) / referenceScale;
        display.color = new Color(1, 1, 1,
            Mathf.SmoothStep(0, 1, (referenceY - ReferenceFadeStartY) / ReferenceFadeLength));
        var width = Math.Max(0, referenceY - HighwayView.VanishingY) * ReferenceWidthPerY * referenceScale;
        var right = camera.WorldToScreenPoint(world + highwayRight);
        var pixelsPerUnit = right.x - position.x;
        var worldWidth = width / Math.Abs(pixelsPerUnit);
        var center = viewport.OffsetX + ReferenceCenterX * referenceScale;
        displayTransform.localPosition = new Vector3(localX + (center - position.x) / pixelsPerUnit / scaleX, localY, localZ);
        displayTransform.localScale = new Vector3(worldWidth / scaleX, worldWidth / scaleY, 1);
    }
}
