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

    private readonly Vector2[] _rest;
    private readonly (Vector2 Position, float Rotation)[] _targets;
    private readonly uint _seed;
    public float BaseScale { get; }
    public int Count => Text.Length;

    // fitWidth is the width of the text in the fit font (text_a1) at scale 1, from GetScreenElementDims.
    public YouRockAnimation(float fitWidth, uint seed = 0x6680bf3)
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
                0 => (-2000f, 0f, -2000f, -200f),
                1 => (1280f, 3280f, -2000f, -200f),
                2 => (1280f, 3280f, 920f, 2720f),
                _ => (-2000f, 0f, 920f, 2720f),
            };
            _targets[i] = (new(Range(ref random, x0, x1), Range(ref random, y0, y1)), Range(ref random, 0, 180));
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
            var random = _seed ^ (uint)(step * 7919 + i * 104729 + 1);
            Next(ref random);
            var offset = new Vector2(MathF.Round(Range(ref random, -10, 10)), -10);
            var rotation = MathF.Round(Range(ref random, 0, 3));
            var scale = Range(ref random, 0.7f, 1.3f);
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

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static float Range(ref uint state, float min, float max) => min + (max - min) * (Next(ref state) / (float)uint.MaxValue);
}
