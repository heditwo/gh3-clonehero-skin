using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class AmpHud
{
    private const float FrameLeftEdge = 36;
    private readonly IReadOnlyDictionary<string, Texture2D> _textures;
    private readonly LampColumnHud _lights;
    private readonly ScoreDigitsHud _score;
    private readonly RawImage _multiplier;
    private (int Combo, int Multiplier, bool Active)? _previous;

    public RectTransform Root { get; }

    public AmpHud(Transform parent, IReadOnlyDictionary<string, Texture2D> textures)
    {
        _textures = textures;
        Root = HudVisual.Group("Amp", parent);
        Root.sizeDelta = new Vector2(264, 256);
        var background = HudVisual.Group("Amp background", Root);
        var foreground = HudVisual.Group("Amp foreground", Root);
        _lights = new LampColumnHud(background, foreground, new Vector2(FrameLeftEdge - LampColumnHud.Width, 78), textures);
        var frame = new Rect(0, 0, Root.rect.width, Root.rect.height);
        HudVisual.Image(background, "scoreBody", textures["HUD_score_body"], frame);
        HudVisual.Image(foreground, "scoreFrame", textures["HUD_score_frame"], frame);
        _score = new ScoreDigitsHud(foreground, new Rect(50, 35, 174, 37), textures);
        _multiplier = HudVisual.Image(foreground, "multiplier", textures["HUD_score_nixie_1a_wide"], new Rect(75, 93, 118, 118));
        HudVisual.Image(foreground, "scoreRingFront", textures["HUD_score_ring_front"], frame);
        HudVisual.Image(foreground, "scoreX", textures["HUD_score_x"], new Rect(68, 132, 48, 43));
    }

    public void Update(GameplayState state)
    {
        _score.Update(state.Score);
        var current = (state.Combo, state.Multiplier, state.StarPowerActive);
        if (_previous == current) return;
        _previous = current;
        _multiplier.texture = _textures[HudState.MultiplierTexture(state.Multiplier, state.StarPowerActive)];
        _lights.Update(state.Combo, HudState.BaseMultiplier(state.Multiplier, state.StarPowerActive), state.StarPowerActive);
    }
}
