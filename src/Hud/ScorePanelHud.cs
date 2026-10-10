using UnityEngine;

namespace Gh3Skin;

// The score panel: the amp and the streak counter in their GH3 positions.

internal sealed class ScorePanelHud
{
    // Tuned: the amp position in the panel, from GH3 frames.
    private const float AmpX = -7, AmpY = -20.5f;
    // GH3: career_hud_2d_elements, counter_pos (330, 810) minus score_pos (300, 650).
    private const float ComboX = 30, ComboY = 160;
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
        _ampMount = new Vector2(AmpX + tuning.ScoreOffsetX, AmpY + tuning.ScoreOffsetY);
        var comboOffset = new Vector2(ComboX * HudVisual.Gh3Scale, ComboY * HudVisual.Gh3Scale);
        _comboMount = _ampMount + comboOffset;
        // The lamp column ends at the amp frame's left edge; long streaks must not cover it.
        _combo = new ComboHud(Root, tuning, textures, counterFont,
            ComboReelLayout.LeftLimit(AmpHud.FrameLeftEdge - comboOffset.x));
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
