using UnityEngine;

namespace Gh3Skin;

internal sealed class ScorePanelHud
{
    private readonly AmpHud _amp;
    private readonly ComboHud _combo;

    public RectTransform Root { get; }

    public ScorePanelHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures)
    {
        Root = HudVisual.Group("Score panel", parent);
        _amp = new AmpHud(Root, textures);
        HudVisual.Position(_amp.Root, new Vector2(tuning.ScoreOffsetX, tuning.ScoreOffsetY));
        _combo = new ComboHud(Root, tuning, textures);
        HudVisual.Position(_combo.Root, new Vector2(30.12f + tuning.CounterOffsetX, 145 + tuning.CounterOffsetY));
    }

    public void Update(GameplayState state, float time)
    {
        _amp.Update(state);
        _combo.Update(state.Combo, time);
    }
}
