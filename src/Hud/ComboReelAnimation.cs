namespace Gh3Skin;

// GH3: the curves of DoMorph's "motion" parameter, from the screen-element morph code in GH3.exe (0x55b010).
internal enum Gh3Motion
{
    Linear,
    Smooth,
    EaseIn,
    EaseOut
}

internal readonly record struct Gh3Morph(float Time, float Value, Gh3Motion Motion = Gh3Motion.Linear);

internal static class Gh3Morphs
{
    public static float Ease(Gh3Motion motion, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return motion switch
        {
            // GH3: smooth is 3t² - 2t³.
            Gh3Motion.Smooth => (t - 1.5f) * t * t * -2,
            Gh3Motion.EaseIn => t * t,
            Gh3Motion.EaseOut => 2 * t - t * t,
            _ => t
        };
    }

    // A script that runs its DoMorph calls one after another, each starting from the previous end value.
    public static float Sample(float start, IReadOnlyList<Gh3Morph> morphs, float elapsed)
    {
        var value = start;
        foreach (var morph in morphs)
        {
            if (elapsed < morph.Time)
                return value + (morph.Value - value) * Ease(morph.Motion, elapsed / morph.Time);
            elapsed -= morph.Time;
            value = morph.Value;
        }
        return value;
    }

    public static float Duration(IReadOnlyList<Gh3Morph> morphs) => morphs.Sum(morph => morph.Time);
}

// Layout of the note-streak counter in GH3 units of its container (HUD_counter_body at the origin). GH3 has four
// digit places; streaks of 10000 and more insert extra 37-unit reels at the counter's left side.
// GH3: create_2d_hud_elements (guitar_hud_2d.qb) puts digit i (from 1 at the right) at x = 222 - 37i.
internal static class ComboReelLayout
{
    public const int Gh3DigitPlaces = 4;
    public const int MinimumShownDigits = 3;
    public const float FirstDigitRight = 222;
    // Tuned: x of the highest digit of a long streak, on the first reel.
    public const float FirstReelDigitX = 74;
    // Tuned: canvas units per container unit of the lamp column, and the gap to the counter body.
    private const float LampScale = 1.05f;
    private const float LampGap = 2;
    public const int MaxDigits = 7;
    public const int MaxCombo = 9999999;
    public const float Pitch = 37;
    // Column where the counter opens: the boundary between the first and second drum reels.
    public const float Split = 95;
    public const float DrumX = 4;
    public const float BodyLeft = 50;
    public const float BodyRight = 221;
    public const float CenterY = 72;

    // Converts the lamp column's right edge, relative to the container origin in canvas units, to a limit for the
    // body's left edge with a two-unit gap.
    public static float LeftLimit(float lampRight) => lampRight / LampScale + LampGap;

    public static int DigitCount(int combo) => Math.Max(MinimumShownDigits, Math.Clamp(combo, 0, MaxCombo).ToString().Length);

    public static int ExtraReels(int digits) => Math.Max(0, digits - Gh3DigitPlaces);

    public static float Width(int digits) => ExtraReels(digits) * Pitch;

    // The highest digit of a long streak sits on the first reel, which moves left as the counter opens.
    public static float DigitX(int place, int digits, float width) =>
        place >= MinimumShownDigits && place == digits - 1 ? FirstReelDigitX - width : FirstDigitRight - Pitch * (place + 1);

    // Visible part of extra reel k (0 next to the split) when the counter is open by width units.
    public static (float X, float Width) Reel(int k, float width)
    {
        var visible = Math.Clamp(width - Pitch * k, 0, Pitch);
        return (Split - Pitch * k - visible, visible);
    }

    // Uniform scale about the right edge that keeps the body's left edge at or right of leftLimit.
    public static float FitScale(float width, float leftLimit) =>
        Math.Min(1, (BodyRight - leftLimit) / (BodyRight - BodyLeft + Math.Max(0, width)));

    public static float LeftEdge(float width, float leftLimit) =>
        BodyRight - (BodyRight - BodyLeft + Math.Max(0, width)) * FitScale(width, leftLimit);
}

// The counter's open/close animation, built from GH3's HUD scripts:
// - opening follows hud_move_note_scorebar's entrance: overshoot with ease_out, settle with ease_in in a third of
//   the time, then a small ease_out/ease_in nudge of 0.1 s each;
// - closing follows its exit: an anticipation hop with ease_out, then the move away;
// - the new digit wobbles like hud_show_note_streak_combo's "Note Streak!" text, at twice the speed;
// - the flash is hud_glowburst_alert's Char_Select_Hilite1 streak.
// Tuned: the overshoot sizes, the squash amounts, and the morph tables below are calibrated by eye against GH3 frames.
internal sealed class ComboReelAnimation
{
    private const float OpenTime = 0.3f;
    private const float Overshoot = 12;
    private const float PopSize = 0.35f;
    public const float PopDelay = 0.1f;
    private const float NudgeTime = 0.1f;
    private const float CloseHopTime = 0.15f;
    private const float CloseMoveTime = 0.3f;
    private const float DigitFadeTime = 0.1f;
    // GH3: hud_glowburst_alert starts at scale (15, 1).
    private const float FlashStartWidth = 15;
    private static readonly Gh3Morph[] OpenSquash =
    {
        new(OpenTime, 0.88f, Gh3Motion.EaseOut), new(OpenTime / 3, 1.05f, Gh3Motion.EaseIn),
        new(0.1f, 0.98f, Gh3Motion.EaseOut), new(0.1f, 1, Gh3Motion.EaseIn)
    };
    private static readonly Gh3Morph[] CloseSquash =
    {
        new(0.15f, 1.06f, Gh3Motion.EaseOut), new(0.3f, 0.9f, Gh3Motion.EaseIn),
        new(0.1f, 1.04f, Gh3Motion.EaseOut), new(0.1f, 1, Gh3Motion.EaseIn)
    };
    private static readonly Gh3Morph[] PopScale =
    {
        new(0.2f, 1 + PopSize, Gh3Motion.EaseOut), new(0.2f, 1, Gh3Motion.EaseIn),
        new(0.15f, 1 + PopSize / 1.5f, Gh3Motion.EaseOut), new(0.15f, 1, Gh3Motion.EaseIn),
        new(0.1f, 1 + PopSize / 2, Gh3Motion.EaseOut), new(0.1f, 1, Gh3Motion.EaseIn),
        new(0.05f, 1 + PopSize / 2.5f, Gh3Motion.EaseOut), new(0.05f, 1, Gh3Motion.EaseIn)
    };
    private static readonly Gh3Morph[] PopAngle =
    {
        new(0.2f, 3, Gh3Motion.EaseOut), new(0.2f, 2, Gh3Motion.EaseIn), new(0.15f, -2, Gh3Motion.EaseOut),
        new(0.15f, -1, Gh3Motion.EaseIn), new(0.1f, 2, Gh3Motion.EaseOut), new(0.1f, 1, Gh3Motion.EaseIn),
        new(0.05f, -1, Gh3Motion.EaseOut), new(0.05f, 0, Gh3Motion.EaseIn)
    };
    private static readonly Gh3Morph[] FlashWidth =
        { new(0.1f, 20, Gh3Motion.EaseOut), new(0.1f, 15, Gh3Motion.EaseOut), new(0.8f, 80, Gh3Motion.EaseIn) };
    private static readonly Gh3Morph[] FlashHeight =
        { new(0.1f, 5, Gh3Motion.EaseOut), new(0.1f, 0.5f, Gh3Motion.EaseOut), new(0.8f, 0, Gh3Motion.EaseIn) };
    private static readonly Gh3Morph[] FlashAlpha =
        { new(0.1f, 0.5f, Gh3Motion.EaseOut), new(0.1f, 0.5f, Gh3Motion.EaseOut), new(0.8f, 0, Gh3Motion.EaseIn) };

    private Gh3Morph[] _width = Array.Empty<Gh3Morph>();
    private Gh3Morph[] _squash = Array.Empty<Gh3Morph>();
    private float _from;
    private float _started = float.NegativeInfinity;
    private bool _opening;

    public float Target { get; private set; }
    public bool Closing { get; private set; }

    public void Reset(float width)
    {
        Target = _from = width;
        _width = _squash = Array.Empty<Gh3Morph>();
        _started = float.NegativeInfinity;
        _opening = Closing = false;
    }

    public void Open(float width, float time)
    {
        _from = Width(time);
        Target = width;
        _width = new Gh3Morph[]
        {
            new(OpenTime, width + Overshoot, Gh3Motion.EaseOut), new(OpenTime / 3, width, Gh3Motion.EaseIn),
            new(NudgeTime, width - Overshoot / 6, Gh3Motion.EaseOut), new(NudgeTime, width, Gh3Motion.EaseIn)
        };
        _squash = OpenSquash;
        _started = time;
        _opening = true;
        Closing = false;
    }

    public void Close(float time)
    {
        _from = Width(time);
        Target = 0;
        _width = new Gh3Morph[] { new(CloseHopTime, _from + Overshoot / 2, Gh3Motion.EaseOut), new(CloseMoveTime, 0, Gh3Motion.EaseIn) };
        _squash = CloseSquash;
        _started = time;
        _opening = false;
        Closing = true;
    }

    public bool Active(float time) => time - _started < Math.Max(Gh3Morphs.Duration(_squash), Gh3Morphs.Duration(PopScale) + PopDelay);

    public bool Closed(float time) => Closing && time - _started >= Gh3Morphs.Duration(_squash);

    public float Width(float time) => Math.Max(0, Gh3Morphs.Sample(_from, _width, time - _started));

    public float Squash(float time) => Gh3Morphs.Sample(1, _squash, time - _started);

    // The old digits vanish at once, as hud_flip_note_streak_num hides a digit before it flips.
    public float DigitAlpha(float time) => Closing ? Math.Clamp(1 - (time - _started) / DigitFadeTime, 0, 1) : 1;

    public (float Scale, float Angle) Pop(float time)
    {
        if (!_opening) return (1, 0);
        var elapsed = time - _started - PopDelay;
        return elapsed < 0 ? (0, 0) : (Gh3Morphs.Sample(0, PopScale, elapsed), Gh3Morphs.Sample(0, PopAngle, elapsed));
    }

    // glowburst scale is in multiples of the 64-texel sprite, starting from (15, 1).
    public (float Width, float Height, float Alpha) Flash(float time)
    {
        var elapsed = time - _started;
        if (!_opening || elapsed < 0 || elapsed >= Gh3Morphs.Duration(FlashAlpha)) return (0, 0, 0);
        return (Gh3Morphs.Sample(FlashStartWidth, FlashWidth, elapsed), Gh3Morphs.Sample(1, FlashHeight, elapsed),
            Gh3Morphs.Sample(1, FlashAlpha, elapsed));
    }
}
