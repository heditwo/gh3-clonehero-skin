using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal static class HudVisual
{
    // GH3 lays out its career HUD in 1280 x 720 units at 0.7 scale (career_hud_2d_elements), so one GH3 unit is
    // 1.05 pixels of this 1080-line canvas.
    public const float Gh3Scale = 1.05f;

    public static RectTransform Group(string name, Transform parent)
    {
        var rect = new GameObject("GH3 " + name).AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = Vector2.zero;
        return rect;
    }

    public static RawImage Image(Transform parent, string name, Texture2D texture, Rect layout, Vector2? pivot = null)
    {
        var rect = Group(name, parent);
        if (pivot.HasValue) rect.pivot = pivot.Value;
        var image = rect.gameObject.AddComponent<RawImage>();
        image.texture = texture;
        image.raycastTarget = false;
        Layout(image, layout);
        return image;
    }

    public static void Layout(RawImage image, Rect layout)
    {
        Position(image.rectTransform, new Vector2(layout.x, layout.y));
        image.rectTransform.sizeDelta = layout.size;
    }

    public static void Position(RectTransform rect, Vector2 position) =>
        rect.anchoredPosition = new Vector2(position.x, -position.y);

    public static void Viewport(RectTransform root, HudViewport viewport, float offsetY)
    {
        root.anchoredPosition = new Vector2(viewport.OffsetX, -offsetY);
        root.localScale = new Vector3(viewport.Scale, viewport.Scale, 1);
    }
}
