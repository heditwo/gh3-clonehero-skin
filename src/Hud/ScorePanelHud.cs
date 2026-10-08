using UnityEngine;

namespace Gh3Skin;

internal sealed class ScorePanelHud
{
    private readonly AmpHud _amp;
    private readonly ComboHud _combo;
    private readonly Vector2 _ampMount;
    private readonly Vector2 _comboMount;

    public RectTransform Root { get; }

    public ScorePanelHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures, Material ampMaterial,
        HudFont scoreFont, HudFont counterFont)
    {
        Root = HudVisual.Group("Score panel", parent);
        _amp = new AmpHud(Root, textures, ampMaterial, scoreFont);
        _ampMount = new Vector2(-7 + tuning.ScoreOffsetX, -20.5f + tuning.ScoreOffsetY);
        // career_hud_2d_elements: counter_pos (330, 810) minus score_pos (300, 650).
        _comboMount = _ampMount + new Vector2(30 * HudVisual.Gh3Scale, 160 * HudVisual.Gh3Scale);
        _combo = new ComboHud(Root, tuning, textures, counterFont);
    }

    public void Layout(float pixelScale)
    {
        // D3D9's pixel centers are half a screen pixel from Unity's pixel edges.
        var pixelCenter = .5f / pixelScale;
        HudVisual.Position(_amp.Root, _ampMount + new Vector2(pixelCenter, pixelCenter));
        HudVisual.Position(_combo.Root, _comboMount + new Vector2(pixelCenter, pixelCenter));
    }

    public void Update(GameplayState state, float time)
    {
        _amp.Update(state);
        _combo.Update(state.Combo, time);
    }
}
