using UnityEngine;

namespace Gh3Skin;

internal sealed class ScorePanelHud
{
    private readonly AmpHud _amp;
    private readonly ComboHud _combo;
    private readonly Vector2 _ampMount;

    public RectTransform Root { get; }

    public ScorePanelHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures, Material ampMaterial)
    {
        Root = HudVisual.Group("Score panel", parent);
        _amp = new AmpHud(Root, textures, ampMaterial);
        _ampMount = new Vector2(-7 + tuning.ScoreOffsetX, -20.5f + tuning.ScoreOffsetY);
        _combo = new ComboHud(Root, tuning, textures);
        HudVisual.Position(_combo.Root, new Vector2(30.12f + tuning.CounterOffsetX, 145 + tuning.CounterOffsetY));
    }

    public void Layout(float pixelScale)
    {
        // D3D9's pixel centers are half a screen pixel from Unity's pixel edges.
        var pixelCenter = .5f / pixelScale;
        HudVisual.Position(_amp.Root, _ampMount + new Vector2(pixelCenter, pixelCenter));
    }

    public void Update(GameplayState state, float time)
    {
        _amp.Update(state);
        _combo.Update(state.Combo, time);
    }
}
