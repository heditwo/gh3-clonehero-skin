using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// The score amp: the score digits, the multiplier nixie tube, and the lamp column (PARITY-NOTES.md#hud).
internal sealed class AmpHud
{
    public const float FrameLeftEdge = 36;
    // GH3: the amp at uniform scale 268.8 at 1920 x 1080; the multiplier image at (70, 90), 128 x 128.
    private const float AmpSize = 268.8f;
    private const float MultiplierX = 70, MultiplierY = 90, MultiplierSize = 128;
    // GH3: D3D9 trace, 0.001 rad clockwise.
    private const float FaceRotationRadians = -0.001f;
    // Tuned: lamp column top, from GH3 frames.
    private const float LampTop = 78;
    // GH3: guitar_hud_2d.qb, num_a9 at (222, 70), just {right right}, scale 1.1, font_spacing 5.
    private const float ScoreX = 222, ScoreY = 70, ScoreScale = 1.1f, ScoreSpacing = 5;
    // Measured: the width limit of six-digit scores in GH3 frames.
    private const float ScoreWidthLimit = 176;
    private const int MaximumScore = 99999999;
    private readonly IReadOnlyDictionary<string, Texture2D> _textures;
    private readonly LampColumnHud _lights;
    private readonly HudText _score;
    private readonly RawImage _multiplier;
    private (int Combo, int Multiplier, bool Active)? _previous;

    public RectTransform Root { get; }

    public AmpHud(Transform parent, IReadOnlyDictionary<string, Texture2D> textures, Material material, HudFont font)
    {
        _textures = textures;
        Root = HudVisual.Group("Amp", parent);
        Root.sizeDelta = new Vector2(AmpSize, AmpSize);
        var background = HudVisual.Group("Amp background", Root);
        var foreground = HudVisual.Group("Amp foreground", Root);
        _lights = new LampColumnHud(background, foreground, new Vector2(FrameLeftEdge - LampColumnHud.Width, LampTop), textures);
        var body = textures["HUD_score_body"];
        var artwork = HudVisual.Group("Amp artwork", background);
        artwork.sizeDelta = new Vector2(body.width, body.height);
        artwork.localScale = new Vector3(Root.rect.width / body.width, Root.rect.height / body.height, 1);
        var face = HudVisual.Group("Amp face", artwork);
        // GH3 retains this rotation at both the container and sprite levels when script rotation is zero.
        face.localRotation = Quaternion.Euler(0, 0, FaceRotationRadians * Mathf.Rad2Deg);
        _multiplier = HudVisual.Image(face, "multiplier", textures["HUD_score_nixie_1a"], new Rect(MultiplierX, MultiplierY, MultiplierSize, MultiplierSize));
        _multiplier.uvRect = new Rect(0, 1, 1, -1);
        _multiplier.material = material;
        _multiplier.rectTransform.localRotation = face.localRotation;
        var frame = HudVisual.Image(face, "scoreBody", body, new Rect(0, 0, body.width, body.height));
        frame.uvRect = _multiplier.uvRect;
        frame.material = material;
        frame.rectTransform.localRotation = face.localRotation;
        var score = HudVisual.Group("Score", foreground);
        score.localScale = new Vector3(HudVisual.Gh3Scale, HudVisual.Gh3Scale, 1);
        _score = new HudText(score, "Score digits", font, new Vector2(ScoreX, ScoreY), Vector2.one, ScoreScale, ScoreSpacing, ScoreWidthLimit);
    }

    public void Update(GameplayState state)
    {
        _score.Text = Math.Clamp(state.Score, 0, MaximumScore).ToString();
        var current = (state.Combo, state.Multiplier, state.StarPowerActive);
        if (_previous == current) return;
        _previous = current;
        _multiplier.texture = _textures[HudState.MultiplierTexture(state.Multiplier, state.StarPowerActive)];
        _lights.Update(state.Combo, HudState.BaseMultiplier(state.Multiplier, state.StarPowerActive), state.StarPowerActive);
    }
}
