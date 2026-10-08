using Gh3Skin;

internal static class HitEffectLogicTests
{
    public static void Run()
    {
        Check(Math.Abs(HitEffectState.LaneX(0) - 435.2f) < 1e-3 && Math.Abs(HitEffectState.LaneX(4) - 844.8f) < 1e-3,
            "buttons sit at generate_pos_table's bottom lane centres");
        Check(Math.Abs(HitEffectState.BoltAngle(0) + HitEffectState.BoltAngle(4)) < 1e-4 && HitEffectState.BoltAngle(2) == 0,
            "bonus bolts lean along the mirrored lane angles");

        var state = new HitEffectState();
        state.Hit(1, false);
        Check(state.Advance(1 / 60f) == 1 && state.Sprites.Count == 0, "hit_note_fx waits one game frame after the hit");
        state.Advance(1 / 60f);
        var hit = state.Sprites.Single();
        Check(hit.Texture == HitEffectTexture.NoteHit && hit.Cell == 0 && hit.Position.Y == HitEffectState.PlayLine,
            "NoteFX puts sys_note_hit01 on the button and starts at cell 0");
        Check(state.Systems.Single().Origin.Y == HitEffectState.PlayLine - 20, "hit sparks start 20 units above the button");
        for (var frame = 0; frame < 15; frame++) state.Advance(1 / 60f);
        Check(hit.Cell == 15 && state.Sprites.Count == 1, "the 60 fps hit animation reaches its last cell");
        state.Advance(1 / 60f);
        Check(state.Sprites.Count == 0, "hit_note_fx destroys the sprite 16 game frames after NoteFX");
        Check(state.Systems.Count == 1 && state.Systems[0].Killed, "the sparks outlive the sprite until their time ends");
        for (var frame = 0; frame < 20; frame++) state.Advance(1 / 60f);
        Check(state.Systems.Count == 0, "killed particle systems are removed when empty");

        state.Hit(0, true);
        state.Advance(2 / 60f);
        Check(state.Sprites.Single().Texture == HitEffectTexture.NoteHitStar &&
              state.Systems.Single().Params == Gh3ParticleParams.StarHit, "Star Power hits use the blue flame and cyan sparks");

        state.Reset();
        state.SetWhammy(4, true);
        state.Advance(1 / 60f);
        var held = state.Sprites.Single();
        Check(held.Texture == HitEffectTexture.WhammyHold && held.Cell == 0, "WhammyFXOn shows sys_whammy_hold01");
        for (var frame = 0; frame < 33; frame++) state.Advance(1 / 60f);
        Check(held.Cell == 0 && held.Frames == 33, "the held flame loops 16 cells at 30 fps");
        state.SetWhammy(4, false);
        state.Advance(1 / 60f);
        Check(state.Sprites.Count == 0 && state.Systems.Single().Killed, "WhammyFXOff removes the flame and stops its sparks");

        state.Reset();
        state.StarBonus(0b10001);
        state.Advance(1 / 60f);
        Check(state.Sprites.Count == 2 && state.Systems.Count == 6, "each phrase-end button gets a bolt and three particle systems");
        for (var frame = 0; frame < 17; frame++) state.Advance(1 / 60f);
        Check(state.Sprites.Count == 2, "bolts stay for star_power_bolt_time");
        state.Advance(1 / 60f);
        Check(state.Sprites.Count == 1, "bolts go after star_power_bolt_time, one game frame apart");

        var timing = new HitEffectState();
        timing.Hit(2, false);
        var steps = 0;
        foreach (var delta in new[] { 0.0166f, 0.0167f, 0.0165f, 0.0168f }) steps += timing.Advance(delta);
        Check(steps == 4, "frame times near 1/60 s run one game frame each");

        var sparks = new Gh3ParticleSystem(Gh3ParticleParams.Hit, new(0, 0));
        var random = new Gh3Random(1);
        for (var frame = 0; frame < 6; frame++) sparks.Step(1 / 60f, random);
        Check(sparks.Count is >= 4 and <= 6, "hit sparks emit every 0.02 s");
        var newest = sparks.Particles[sparks.Count - 1];
        Check(newest.Velocity.Y < 0 && Math.Abs(newest.Velocity.Length() - 10) < 0.5f, "sparks start upwards at 10 units per frame");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
