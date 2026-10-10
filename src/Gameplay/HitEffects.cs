using System.Numerics;

namespace Gh3Skin;

// One Create2DParticleSystem parameter set. Colours are 0-255 RGBA; velocity is GH3 units per game frame.
internal sealed record Gh3ParticleParams(
    HitEffectTexture Texture,
    Vector4 StartColor, Vector4 EndColor,
    Vector2 StartScale, Vector2 EndScale,
    float StartAngleSpread, float MinRotation, float MaxRotation,
    float EmitStartRadius, float EmitRadius, float EmitRate, float EmitDirection, float EmitSpread,
    float Velocity, Vector2 Friction, float Time)
{
    // hit_particle_params, star_hit_particle_params, and whammy_particle_params in scripts/guitar/guitar_events.qb.
    public static readonly Gh3ParticleParams Hit = Sparks(new(255, 128, 0, 255), new(255, 0, 0, 0), 0.25f);
    public static readonly Gh3ParticleParams StarHit = Sparks(new(0, 255, 255, 255), new(0, 255, 255, 0), 0.25f);
    public static readonly Gh3ParticleParams Whammy = Sparks(new(255, 128, 0, 255), new(255, 0, 0, 0), 0.5f);

    // The three systems that GuitarEvent_StarSequenceBonus creates at each button of the phrase's last note.
    public static readonly Gh3ParticleParams BonusStars = new(HitEffectTexture.Star, new(0, 128, 255, 255), new(0, 128, 128, 0),
        new(0.55f, 0.55f), new(0.25f, 0.25f), 360, -120, 240, 0, 2, 0.04f, 0, 44, 24, new(0, 66), 2);
    public static readonly Gh3ParticleParams BonusWhiteStars = new(HitEffectTexture.Star2, new(255, 255, 255, 255), new(128, 128, 128, 0),
        new(0.5f, 0.5f), new(0.25f, 0.25f), 360, -120, 508, 0, 2, 0.04f, 0, 28, 22, new(0, 55), 2);
    public static readonly Gh3ParticleParams BonusSparks = new(HitEffectTexture.Spark, new(0, 255, 255, 255), new(0, 255, 255, 0),
        new(1.5f, 1.5f), new(0.25f, 0.25f), 360, -500, 500, 0, 2, 0.04f, 0, 180, 12, new(0, 0), 1);

    // GH3: the shared fields of the three spark parameter sets (guitar_events.qb).
    private static Gh3ParticleParams Sparks(Vector4 start, Vector4 end, float time) => new(HitEffectTexture.Spark, start, end,
        new(1, 1), new(0.5f, 0.5f), 0, 0, 0, 0, 1, 0.02f, 0, 160, 10, new(0, 50), time);
}

internal enum HitEffectTexture { NoteHit, NoteHitStar, WhammyHold, BigBolt, Spark, Star, Star2 }

internal enum HitEvent { Note, Whammy, Bonus }

internal struct Gh3Particle
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 Scale;
    public Vector4 Color;
    public float Rotation;
    public float RotationSpeed;
    public float Age;
}

// Update2DParticleSystems (GH3.exe 0x428ba0): emission, motion, and colour and scale fades of one 2D particle system.
internal sealed class Gh3ParticleSystem
{
    public const int Capacity = 64;
    private readonly Gh3Particle[] _particles = new Gh3Particle[Capacity];
    private float _elapsed;
    private float _lastEmit;

    public Gh3ParticleParams Params { get; }
    public Vector2 Origin { get; }
    public bool Killed { get; private set; }
    public int Count { get; private set; }
    public bool Finished => Killed && Count == 0;
    public ReadOnlySpan<Gh3Particle> Particles => _particles.AsSpan(0, Count);

    public Gh3ParticleSystem(Gh3ParticleParams parameters, Vector2 origin)
    {
        Params = parameters;
        Origin = origin;
    }

    // Destroy2DParticleSystem kill_when_empty: stop emitting and keep the live particles.
    public void Kill() => Killed = true;

    public void Step(float deltaTime, Gh3Random random)
    {
        var p = Params;
        _elapsed += deltaTime;
        if (!Killed)
            while (_elapsed - _lastEmit > p.EmitRate)
            {
                _lastEmit += p.EmitRate;
                if (Count < Capacity) _particles[Count++] = Spawn(random);
            }
        for (var index = 0; index < Count; index++)
        {
            ref var particle = ref _particles[index];
            particle.Age += deltaTime;
            if (particle.Age > p.Time)
            {
                _particles[index--] = _particles[--Count];
                continue;
            }
            // Positions move by the stored velocity once per game frame; friction is an acceleration in units per second.
            particle.Position += particle.Velocity;
            particle.Velocity += p.Friction * deltaTime;
            var t = particle.Age * (1 / p.Time);
            // GH3: Update2DParticleSystems (0x428ba0) fades the color in 12-bit fixed point.
            var fixedT = (int)(t * FixedPointOne);
            particle.Color = new Vector4(Fade(p.StartColor.X, p.EndColor.X, fixedT), Fade(p.StartColor.Y, p.EndColor.Y, fixedT),
                Fade(p.StartColor.Z, p.EndColor.Z, fixedT), Fade(p.StartColor.W, p.EndColor.W, fixedT));
            particle.Scale = p.StartScale + (p.EndScale - p.StartScale) * t;
            particle.Rotation += particle.RotationSpeed * deltaTime;
        }
    }

    private const int FixedPointOne = 4096;
    private const int FixedPointShift = 12;

    private static float Fade(float start, float end, int fixedT) => (byte)((int)start + (((int)end - (int)start) * fixedT >> FixedPointShift));

    private Gh3Particle Spawn(Gh3Random random)
    {
        var p = Params;
        var angle = (p.EmitDirection - p.EmitSpread * 0.5f + random.Next((int)(p.EmitSpread * 64)) / 64f) * (MathF.PI / 180);
        var direction = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
        var radius = random.Next((int)(p.EmitRadius * 64)) / 64f;
        var rotation = p.StartAngleSpread > 0 ? random.Next((int)(p.StartAngleSpread * 64)) / 64f : 0;
        var rotationSpeed = random.Next((int)((p.MaxRotation - p.MinRotation) * 64)) / 64f + p.MinRotation;
        return new Gh3Particle
        {
            Position = Origin + direction * (p.EmitStartRadius + radius),
            Velocity = direction * p.Velocity,
            Scale = p.StartScale,
            Color = p.StartColor,
            Rotation = rotation,
            RotationSpeed = rotationSpeed,
        };
    }
}

// An animated sprite element. Frame counts start at 0 in the game frame that created it.
internal sealed class HitEffectSprite
{
    public HitEffectTexture Texture { get; init; }
    public int Lane { get; init; }
    public Vector2 Position { get; init; }
    public Vector2 Size { get; init; }
    public float Rotation { get; init; }
    public int Frames { get; set; }
    public int RemoveAt { get; set; } = int.MaxValue;
    public Gh3ParticleSystem? Particles { get; set; }
    public int KillParticlesAt { get; set; } = int.MaxValue;
    public Gh3ParticleSystem[] BonusParticles { get; set; } = Array.Empty<Gh3ParticleSystem>();

    // AnimatedTexture_UI cells, row by row from the top left: (columns, rows, frames per second) from the sys_* material.
    // GH3: the note hit is 4 x 4 cells at 60 fps and stops on its last cell, the held glow 4 x 4 at 30 fps, and the bolt
    // 8 x 1 at 15 fps (PARITY-NOTES.md#hit-effects).
    private const int LastNoteHitCell = 15;

    public int Cell => Texture switch
    {
        HitEffectTexture.NoteHit or HitEffectTexture.NoteHitStar => Math.Min(Frames, LastNoteHitCell),
        HitEffectTexture.WhammyHold => Frames / 2 % 16,
        HitEffectTexture.BigBolt => Frames / 4 % 8,
        _ => 0,
    };
}

// GH3's note-hit (NoteFX), held-note (WhammyFXOn/Off), and Star Power phrase (GuitarEvent_StarSequenceBonus) effects
// in GH3's 1280 x 720 screen units, stepped once per 60 Hz game frame.
internal sealed class HitEffectState
{
    public const float FrameTime = 1f / 60;
    public const int Lanes = 5;
    // GH3: generate_pos_table, highway_top_width1 160, widthOffsetFactor1 2.2, highway_height1 350, highway_playline1 655.
    private const int HighwayTopWidth = 160;
    private const int HighwayHeight = 350;
    private const float BottomWidth = HighwayTopWidth + HighwayTopWidth * 2.2f;
    public const float PlayLine = 655;
    // star_power_bolt_scale (1.0, 1.5) and star_power_bolt_time 0.3 s (guitar_tweaks.qb).
    private static readonly Vector2 BoltScale = new(1, 1.5f);
    private const int BoltFrames = 18;
    // GH3: hit_note_fx kills the particles after 6 game frames and destroys the sprite after 10 more; particles start
    // 20 units above the button, the phrase-bonus sparks 15 units higher than the stars.
    private const int NoteHitParticleFrames = 6;
    private const int NoteHitFrames = 16;
    private const int ParticleRise = 20;
    private const int BonusSparkRise = 15;
    // GH3: sprite sizes in screen units: note hit 128 x 256, held glow 128 x 128, bolt 128 x 512 before its scale.
    private const int SpriteWidth = 128;
    private const int NoteHitHeight = 256;
    private const int BoltHeight = 512;
    private const float ScreenCenterX = HudCanvas.Gh3Width / 2;
    private readonly List<HitEffectSprite> _sprites = new();
    private readonly List<Gh3ParticleSystem> _systems = new();
    private readonly List<(HitEvent Kind, int Lane, bool Flag, int Delay)> _events = new();
    private readonly HitEffectSprite?[] _whammy = new HitEffectSprite?[Lanes];
    private readonly Gh3Random _random = new(Gh3Random.BootSeed);
    private float _time;

    public IReadOnlyList<HitEffectSprite> Sprites => _sprites;
    public IReadOnlyList<Gh3ParticleSystem> Systems => _systems;

    public static float LaneX(int lane) => ScreenCenterX - BottomWidth / 2 + BottomWidth / 5 * (lane + 0.5f);

    // button_models left_angle, which GuitarEvent_StarSequenceBonus uses for right-handed players: the mirrored
    // lane's Atan2(highway_height, start_x - end_x), in degrees clockwise.
    public static float BoltAngle(int lane)
    {
        var mirrored = Lanes - 1 - lane;
        var start = ScreenCenterX - HighwayTopWidth / 2f + HighwayTopWidth / 5f * (mirrored + 0.5f);
        return MathF.Atan2(start - LaneX(mirrored), HighwayHeight) * 180 / MathF.PI;
    }

    public void Reset()
    {
        _sprites.Clear();
        _systems.Clear();
        _events.Clear();
        Array.Clear(_whammy);
        _time = 0;
    }

    // Events take effect in the next game frame that Advance runs, which is the frame of the hit.
    // GuitarEvent_HitNote_Spawned waits one more game frame before hit_note_fx runs NoteFX.
    public void Hit(int lane, bool starPowerActive) => _events.Add((HitEvent.Note, lane, starPowerActive, 2));

    public void SetWhammy(int lane, bool held) => _events.Add((HitEvent.Whammy, lane, held, 1));

    // GuitarEvent_StarSequenceBonus runs for each button of the phrase's last note.
    public void StarBonus(int laneMask)
    {
        for (var lane = 0; lane < Lanes; lane++)
            if ((laneMask & (1 << lane)) != 0) _events.Add((HitEvent.Bonus, lane, false, 1));
    }

    // Runs whole 60 Hz game frames for the elapsed time; returns the number of frames run. Frame times near 1/60 s
    // run exactly one game frame, so render-time jitter does not drop or double a frame.
    public int Advance(float deltaTime)
    {
        _time = Math.Min(_time + Math.Max(deltaTime, 0) / FrameTime, 10);
        var frames = (int)MathF.Round(_time);
        _time -= frames;
        for (var frame = 0; frame < frames; frame++) Step();
        return frames;
    }

    private void Step()
    {
        for (var index = _sprites.Count - 1; index >= 0; index--)
        {
            var sprite = _sprites[index];
            sprite.Frames++;
            if (sprite.Frames >= sprite.KillParticlesAt)
            {
                sprite.Particles?.Kill();
                foreach (var system in sprite.BonusParticles) system.Kill();
            }
            if (sprite.Frames >= sprite.RemoveAt) _sprites.RemoveAt(index);
        }
        var bonusOrder = 0;
        for (var index = 0; index < _events.Count; index++)
        {
            var (kind, lane, flag, delay) = _events[index];
            if (--delay > 0)
            {
                _events[index] = (kind, lane, flag, delay);
                continue;
            }
            _events.RemoveAt(index--);
            if (kind == HitEvent.Note) NoteFx(lane, flag);
            else if (kind == HitEvent.Whammy) Whammy(lane, flag);
            else Bonus(lane, bonusOrder++);
        }
        foreach (var system in _systems) system.Step(FrameTime, _random);
        _systems.RemoveAll(system => system.Finished);
    }

    // NoteFX (GH3.exe 0x42add0): sys_note_hit01 (sys_note_hit_blue_01 in Star Power), bottom-centred on the button, and
    // hit_particle_params (star_hit_particle_params) 20 units higher. hit_note_fx kills the particles after 6 game
    // frames and destroys the sprite after 10 more.
    private void NoteFx(int lane, bool star)
    {
        _sprites.Add(new HitEffectSprite
        {
            Texture = star ? HitEffectTexture.NoteHitStar : HitEffectTexture.NoteHit, Lane = lane,
            Position = new(LaneX(lane), PlayLine), Size = new(SpriteWidth, NoteHitHeight), RemoveAt = NoteHitFrames, KillParticlesAt = NoteHitParticleFrames,
            Particles = Add(new Gh3ParticleSystem(star ? Gh3ParticleParams.StarHit : Gh3ParticleParams.Hit,
                new(LaneX(lane), PlayLine - ParticleRise))),
        });
    }

    private void Whammy(int lane, bool held)
    {
        var current = _whammy[lane];
        if (held == (current != null)) return;
        if (held)
        {
            // WhammyFXOn (0x42b110): sys_whammy_hold01 at the button, whammy_particle_params 20 units higher.
            var sprite = new HitEffectSprite
            {
                Texture = HitEffectTexture.WhammyHold, Lane = lane, Position = new(LaneX(lane), PlayLine), Size = new(SpriteWidth, SpriteWidth),
                Particles = Add(new Gh3ParticleSystem(Gh3ParticleParams.Whammy, new(LaneX(lane), PlayLine - ParticleRise))),
            };
            _sprites.Add(sprite);
            _whammy[lane] = sprite;
            return;
        }
        // WhammyFXOff: the sprite goes at once; its particles finish their lives.
        _sprites.Remove(current!);
        current!.Particles?.Kill();
        _whammy[lane] = null;
    }

    // A lightning bolt and three particle systems at the button. The script waits star_power_bolt_time, then removes
    // the bolts one game frame apart.
    private void Bonus(int lane, int order)
    {
        var button = new Vector2(LaneX(lane), PlayLine);
        _sprites.Add(new HitEffectSprite
        {
            Texture = HitEffectTexture.BigBolt, Lane = lane, Position = button, Size = new Vector2(SpriteWidth, BoltHeight) * BoltScale,
            Rotation = BoltAngle(lane), RemoveAt = BoltFrames + order, KillParticlesAt = BoltFrames + order,
            BonusParticles = new[]
            {
                Add(new Gh3ParticleSystem(Gh3ParticleParams.BonusStars, button)),
                Add(new Gh3ParticleSystem(Gh3ParticleParams.BonusWhiteStars, button)),
                Add(new Gh3ParticleSystem(Gh3ParticleParams.BonusSparks, button - new Vector2(0, BonusSparkRise))),
            },
        });
    }

    private Gh3ParticleSystem Add(Gh3ParticleSystem system)
    {
        _systems.Add(system);
        return system;
    }
}
