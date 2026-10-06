using System.Numerics;
using Gh3Skin;

internal static class HandFireLogicTests
{
    public static void Run()
    {
        var definition = new HandFireDefinition { Emitters = new[] { Emitter(24, 0.3f, 0.4f, 2, 20), Emitter(64, 0.5f, 0.8f, 1, 0) } };
        if (Environment.GetEnvironmentVariable("GH3_DIRECTION_ASSETS") is { } assets)
        {
            definition = HandFireDefinition.Load(Path.Combine(assets, "budokan", "hand-fire", "hand-fire.json"))!;
            Check(definition.Bones.SequenceEqual(new[] { Gh3Checksum.Of("Bone_Hand_Middle_Mid_L"), Gh3Checksum.Of("Bone_Hand_Middle_Mid_R") }),
                "hand fire uses the original middle-finger attachments");
            Check(definition.Emitters[0].Rate == 24 && definition.Emitters[1].Rate == 64 && definition.Emitters[0].Atlas == 2 && definition.Emitters[0].Fps == 20,
                "hand fire uses the original emission rates and animated texture layout");
        }
        var state = new StageHandFireState(definition);
        var left = Pose(0, 0);
        var right = Pose(0, 1);
        state.Update(0, 99, left, right);
        Check(!state.Active && state.Particles.Count == 0, "99 notes do not light the hands");
        state.Update(0.1, 100, left, right);
        Check(state.Active && state.Particles.Count == 4, "100 notes start flames and sparks on both hands");
        var flame = state.Particles.First(p => p.Emitter == 0 && p.Hand == 0);
        Check(flame.Origin == left.Position, "flames begin at the animated finger position");
        var snapshot = state.Particles.ToArray();
        for (var i = 0; i < 60; i++) state.Update(0.1, 100, left, right);
        Check(state.Particles.SequenceEqual(snapshot), "a paused song does not emit or age particles");
        state.Update(0.2, 200, left, right);
        Check(state.Particles.Contains(flame), "later streak milestones do not restart existing fire");
        var count = state.Particles.Count;
        state.Update(0.2, 0, left, right);
        Check(!state.Active && state.Particles.Count == count, "a streak break stops emission and keeps the fade-out particles");
        for (var i = 3; i <= 11; i++) state.Update(i / 10.0, 0, left, right);
        Check(state.Particles.Count == 0, "flames and sparks expire after a streak break");
        state.Update(1.2, 101, left, right);
        Check(state.Active && state.Particles.Count == 4, "a new streak starts even if a frame skips over 100");
        state.Update(30, 105, left, right);
        Check(state.Particles.Count == 4 && state.Particles.All(p => p.Birth == 30), "forward seeks clear old trails without a burst");
        state.Update(3, 99, left, right);
        Check(state.Particles.Count == 0 && !state.Active, "backward seeks below the threshold clear the fire");
        state.Update(3.1, 100, left, right);
        state.Reset();
        Check(!state.Active && state.Particles.Count == 0, "song changes clear streak effects");
        state.Update(double.NaN, 100, left, right);
        Check(!state.Active && state.Particles.Count == 0, "invalid song clocks cannot emit particles");

        var reference = Playback(definition, 120);
        foreach (var rate in new[] { 30, 60, 144 })
        {
            var sampled = Playback(definition, rate);
            Check(sampled.Length == reference.Length, "hand-fire emission count is independent of display frame rate");
            for (var i = 0; i < sampled.Length; i++)
            {
                Check(Vector3.Distance(sampled[i].Position, reference[i].Position) < 0.00001f && sampled[i].Frame == reference[i].Frame &&
                    Vector4.Distance(sampled[i].Color, reference[i].Color) < 0.00001f, "particle trails, atlas frames, and fades follow song time at every display rate");
            }
        }
        Console.WriteLine("Hand-fire threshold, both-hand attachments, fades, frame rates, pause, and seek checks passed.");
    }

    private static HandFireSample[] Playback(HandFireDefinition definition, int rate)
    {
        var state = new StageHandFireState(definition);
        for (var frame = 0; frame <= 3 * rate; frame++)
        {
            var time = frame / (double)rate;
            state.Update(time, 100, Pose(time, 0), Pose(time, 1));
            Check(state.Particles.Count <= 260 && state.Particles.Count(p => p.Emitter == 0) <= 132 && state.Particles.Count(p => p.Emitter == 1) <= 132,
                "emission stays within the fixed render buffers");
        }
        return state.Particles.OrderBy(p => p.Emitter).ThenBy(p => p.Birth).ThenBy(p => p.Hand).Select(p => state.Sample(p, 3)).ToArray();
    }

    private static BandPose Pose(double time, int hand) => new(new((float)time, hand + 1, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)time * 0.1f));

    private static HandFireEmitter Emitter(float rate, float min, float max, int atlas, int fps) => new()
    {
        Rate = rate, Life = new[] { min, max }, Speed = new[] { 0.3f, 0.6f }, Size = new[] { new[] { 0.2f, 0.2f }, new[] { 0f, 0f } },
        Spread = 45, Range = new[] { 0f, 0f, 0f }, Force = new[] { 0f, 0.2f, 0f },
        Color = new[] { 1f, 0.3f, 0f, 1f }, Colors = new[] { new[] { 1f, 1f, 1f, 1f }, new[] { 1f, 1f, 1f, 1f }, new[] { 1f, 1f, 1f, 0f }, new[] { 1f, 1f, 1f, 0f } },
        Atlas = atlas, Fps = fps,
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
