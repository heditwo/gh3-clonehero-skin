using UnityEngine;

namespace Gh3Skin;

internal sealed class BeatlineVisual
{
    private const float ReferenceCenterX = 803.5f;
    private const float ReferenceVanishingY = 212;
    private const float ReferenceFadeStartY = 396;
    private const float ReferenceFadeLength = 45;
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
        if (existing != null)
            _display = existing.GetComponent<SpriteRenderer>();
        else
        {
            var display = new GameObject("GH3 Beat Line");
            display.layer = _object.layer;
            display.transform.SetParent(transform, false);
            _display = display.AddComponent<SpriteRenderer>();
            _display.sharedMaterial = assets.BeatlineMaterial;
            _display.sortingLayerID = SortingLayer.NameToID("HighwayOverlay");
            _display.sortingOrder = -201;
        }
        _displayTransform = _display.transform;
    }

    public void Update(Camera camera)
    {
        if (_object == null || !_object.activeInHierarchy)
            return;
        _source.enabled = false;
        var scale = _transform.localScale;
        _display.sprite = scale.y > 1.5f ? _bar : scale.y < 0.8f ? _halfBeat : _beat;
        var position = camera.WorldToScreenPoint(_transform.position);
        var rect = camera.pixelRect;
        var viewport = HudViewport.ForHighway(Screen.height, rect.x, rect.y, rect.width, rect.height, camera.fieldOfView);
        var referenceScale = viewport.Scale * 1.2f;
        var referenceY = (Screen.height - position.y - viewport.CenterOffsetY) / referenceScale;
        _display.color = new Color(1, 1, 1,
            Mathf.SmoothStep(0, 1, (referenceY - ReferenceFadeStartY) / ReferenceFadeLength));
        // GH3 line ends follow the rail perspective in the 1600 x 900 source capture.
        var width = Math.Max(0, referenceY - ReferenceVanishingY) * referenceScale;
        var right = camera.WorldToScreenPoint(_transform.position + _transform.parent.right);
        var pixelsPerUnit = right.x - position.x;
        var worldWidth = width / Math.Abs(pixelsPerUnit);
        var center = viewport.OffsetX + ReferenceCenterX * referenceScale;
        _displayTransform.localPosition = new Vector3((center - position.x) / pixelsPerUnit / scale.x, 0, 0);
        _displayTransform.localScale = new Vector3(worldWidth / scale.x, worldWidth / scale.y, 1);
    }
}
