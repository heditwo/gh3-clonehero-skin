using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class LampColumnHud
{
    // Lamp sprites are 32 units high and overlap by one unit.
    private const int LampPitch = 31;
    private const float LampHeight = 32;
    public const float Width = 32;
    private readonly IReadOnlyDictionary<string, Texture2D> _textures;
    private readonly RawImage[] _lamps = new RawImage[5];
    private readonly RawImage[] _glows = new RawImage[5];

    public LampColumnHud(Transform background, Transform foreground, Vector2 mount,
        IReadOnlyDictionary<string, Texture2D> textures)
    {
        _textures = textures;
        var back = HudVisual.Group("Lamp column background", background);
        var front = HudVisual.Group("Lamp column glow", foreground);
        HudVisual.Position(back, mount);
        HudVisual.Position(front, mount);
        for (var light = 0; light < _lamps.Length; light++)
        {
            var layout = new Rect(0, light * LampPitch, Width, LampHeight);
            _lamps[light] = HudVisual.Image(back, $"scoreLight{light}", textures["HUD_score_light_0"], layout);
            _glows[light] = HudVisual.Image(front, $"scoreLightGlow{light}", textures["HUD_score_light_0"], layout);
        }
    }

    public void Update(int combo, int multiplier, bool active)
    {
        var color = active ? "_blue" : multiplier == 4 ? "_purple" : multiplier == 3 ? "_green" : "";
        var progress = multiplier == 4 || combo > 0 && combo % 10 == 0 ? 10 : combo % 10;
        for (var light = 0; light < _lamps.Length; light++)
        {
            var fill = progress - (4 - light) * 2;
            var state = fill >= 2 ? 2 : fill == 1 ? 1 : 0;
            var texture = _textures[$"HUD_score_light_{state}{color}"];
            _lamps[light].texture = _glows[light].texture = texture;
            _glows[light].enabled = state > 0;
        }
    }
}
