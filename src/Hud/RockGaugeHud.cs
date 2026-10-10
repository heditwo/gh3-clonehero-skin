using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// The rock meter gauge: the three backgrounds, the lights, and the needle. GH3: the HUD_rock_* sprites, laid out in
// texels of their 256 x 256 body.
internal sealed class RockGaugeHud
{
    private readonly RockMeterState _state = new();
    private readonly RawImage[] _backgrounds = new RawImage[3];
    private readonly RawImage[] _lights = new RawImage[3];
    private readonly RawImage _needle;
    private bool _first = true;

    public RectTransform Root { get; }

    public RockGaugeHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures)
    {
        Root = HudVisual.Group("Rock gauge", parent);
        Rect Scale(float x, float y, float width, float height) =>
            new(x * tuning.RockScale, y * tuning.RockVerticalScale, width * tuning.RockScale, height * tuning.RockVerticalScale);
        var colors = new[] { "red", "yellow", "green" };
        // Tuned: the HUD_rock_* sprite rectangles in texels of the 256 x 256 meter body, calibrated against GH3 frames.
        var body = Scale(0, 0, 256, 256);
        for (var index = 0; index < colors.Length; index++)
            _backgrounds[index] = HudVisual.Image(Root, "rockBackground" + colors[index], textures["HUD_rock_BG_" + colors[index]], body);
        HudVisual.Image(Root, "rockLights", textures["HUD_rock_lights_all"], Scale(0, 0, 256, 128));
        // Tuned: the light sprites overlap.
        _lights[2] = HudVisual.Image(Root, "rockLightgreen", textures["HUD_rock_lights_green"], Scale(128, 0, 128, 128));
        // Tuned: the yellow light.
        _lights[1] = HudVisual.Image(Root, "rockLightyellow", textures["HUD_rock_lights_yellow"], Scale(62, 0, 128, 128));
        _lights[0] = HudVisual.Image(Root, "rockLightred", textures["HUD_rock_lights_red"], Scale(0, 0, 128, 128));
        // Tuned: the needle pivots at its bottom center.
        _needle = HudVisual.Image(Root, "rockNeedle", textures["HUD_rock_needle"], Scale(128, 165, 16, 114), new Vector2(0.5f, 0));
        HudVisual.Image(Root, "rockBody", textures["HUD_rock_body"], body);
    }

    public void Update(float rock)
    {
        if (_first) _state.Reset(rock);
        if (!_first && !_state.Update(rock)) return;
        _first = false;
        var red = _state.BackgroundRed;
        var green = _state.BackgroundGreen;
        SetBackground(_backgrounds[0], red > 0 ? 1 : 0);
        SetBackground(_backgrounds[1], green < 1 ? 1 - red : 0);
        SetBackground(_backgrounds[2], green);
        var lit = _state.Color == "red" ? 0 : _state.Color == "yellow" ? 1 : 2;
        for (var index = 0; index < _lights.Length; index++) _lights[index].enabled = index == lit;
        _needle.rectTransform.localEulerAngles = new Vector3(0, 0, _state.Angle);
    }

    private static void SetBackground(RawImage image, float alpha)
    {
        image.enabled = alpha > 0;
        image.color = new Color(1, 1, 1, alpha);
    }
}
