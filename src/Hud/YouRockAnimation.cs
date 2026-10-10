using System.Numerics;

namespace Gh3Skin;

// GH3's "You Rock!" text after a won song: split_text_into_array_elements, jiggle_text_array_elements, and
// explode_text_array_elements (guitar_hud.qb) as started by GuitarEvent_SongWon_Spawned. Units are GH3 screen units
// (1280 x 720); angles are degrees, clockwise on screen.
internal sealed class YouRockAnimation
{
    public readonly record struct Letter(char Character, Vector2 Position, float Scale, float Rotation, bool Shadow);

    public const string Text = "You Rock!";
    private static readonly Vector2 Center = new(640, 360);
    private const float SpaceBetween = 40;
    private const float FitPerCharacter = 23;
    private const float MaxFitWidth = 350;
    public const float JiggleTime = 3;
    public const float ExplodeTime = 1;
    // jiggle_text_array_elements holds each random pose for 0.075 s and the rest pose for one 60 Hz game frame.
    private const float JiggleHold = 0.075f;
    private const float Frame = 1f / 60;
    // The shadow element is the same character in black at 1.3 x 1.2 the text scale, one z step below the text.
    public static readonly Vector2 ShadowScale = new(1.3f, 1.2f);
    // Any fixed seed gives a repeatable show; this is GH3's boot seed (Gh3Random.BootSeed).
    private const uint DefaultSeed = 0x6680bf3;
    // GH3: explode_text_array_elements draws the target of each letter in its corner: x and y ranges in screen units,
    // and a rotation of 0-180 degrees.
    private const float LeftX0 = -2000f, LeftX1 = 0f, RightX0 = 1280f, RightX1 = 3280f;
    private const float TopY0 = -2000f, TopY1 = -200f, BottomY0 = 920f, BottomY1 = 2720f;
    private const float MaximumTargetRotation = 180;
    // GH3: jiggle_text_array_elements offsets each letter by -10..10 x and -10 y, turns it 0-3 degrees, and scales it
    // 0.7-1.3.
    private const float JiggleOffset = 10;
    private const float MaximumJiggleRotation = 3;
    private const float MinimumJiggleScale = 0.7f;
    private const float MaximumJiggleScale = 1.3f;
    // Primes that give each jiggle step and letter its own random stream.
    private const int StepPrime = 7919;
    private const int LetterPrime = 104729;

    private readonly Vector2[] _rest;
    private readonly (Vector2 Position, float Rotation)[] _targets;
    private readonly uint _seed;
    public float BaseScale { get; }
    public int Count => Text.Length;

    // fitWidth is the width of the text in the fit font (text_a1) at scale 1, from GetScreenElementDims.
    public YouRockAnimation(float fitWidth, uint seed = DefaultSeed)
    {
        var fit = Math.Min(Text.Length * FitPerCharacter, MaxFitWidth);
        BaseScale = fitWidth > 0 ? fit / fitWidth : 1;
        var space = SpaceBetween * BaseScale;
        var x = Center.X - (Text.Length - 1) * space / 2;
        _rest = Enumerable.Range(0, Text.Length).Select(i => new Vector2(x + i * space, Center.Y)).ToArray();
        _seed = seed == 0 ? 1 : seed;
        var random = _seed;
        // explode_text_array_elements gives each text and shadow pair one target, cycling through the four
        // off-screen corners: up-left, up-right, down-right, down-left.
        _targets = new (Vector2, float)[Text.Length];
        for (var i = 0; i < Text.Length; i++)
        {
            var (x0, x1, y0, y1) = (i % 4) switch
            {
                0 => (LeftX0, LeftX1, TopY0, TopY1),
                1 => (RightX0, RightX1, TopY0, TopY1),
                2 => (RightX0, RightX1, BottomY0, BottomY1),
                _ => (LeftX0, LeftX1, BottomY0, BottomY1),
            };
            _targets[i] = (new(Range(ref random, x0, x1), Range(ref random, y0, y1)), Range(ref random, 0, MaximumTargetRotation));
        }
    }

    public bool Visible(double age) => age >= 0 && age < JiggleTime + ExplodeTime;

    public IEnumerable<Letter> Sample(double age)
    {
        if (!Visible(age)) yield break;
        var cycle = JiggleHold + Frame;
        var jiggling = age < JiggleTime;
        var step = (int)(Math.Min(age, JiggleTime - 1e-6) / cycle);
        var held = jiggling && age - step * cycle < JiggleHold;
        for (var i = 0; i < Text.Length; i++)
        {
            var random = _seed ^ (uint)(step * StepPrime + i * LetterPrime + 1);
            XorShift32.Next(ref random);
            var offset = new Vector2(MathF.Round(Range(ref random, -JiggleOffset, JiggleOffset)), -JiggleOffset);
            var rotation = MathF.Round(Range(ref random, 0, MaximumJiggleRotation));
            var scale = Range(ref random, MinimumJiggleScale, MaximumJiggleScale);
            var position = _rest[i];
            var letterScale = BaseScale;
            if (held)
            {
                position += offset;
                letterScale *= scale;
            }
            if (!jiggling)
            {
                var t = (float)Math.Clamp((age - JiggleTime) / ExplodeTime, 0, 1);
                var target = _targets[i];
                // The text moves with ease_out; its shadow, the second element of the pair, moves linearly.
                yield return new(Text[i], Vector2.Lerp(position, target.Position, t), letterScale, rotation + (target.Rotation - rotation) * t, true);
                yield return new(Text[i], Vector2.Lerp(position, target.Position, t * (2 - t)), letterScale, rotation + (target.Rotation - rotation) * t * (2 - t), false);
                continue;
            }
            yield return new(Text[i], position, letterScale, rotation, true);
            yield return new(Text[i], position, letterScale, rotation, false);
        }
    }

    private static float Range(ref uint state, float min, float max) => min + (max - min) * (XorShift32.Next(ref state) / (float)uint.MaxValue);
}
