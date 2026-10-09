using Il2Cpp;
using UnityEngine;
using UnityEngine.UI;
using Note = Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique;
using NoteFlags = Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique.EnumNPublicSealedvaNoChDiExChHoStTaSoUnique;
using NoteState = Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique.EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique;

namespace Gh3Skin;

// Draws GH3's 2D hit effects in GH3 screen units (1280 x 720) on the highway layer, which maps 1920 x 1080 canvas units
// onto the player camera. Clone Hero's own hit effects are hidden by GameplaySpriteRenderer.
internal sealed class HitEffectsHud
{
    private const float CanvasScale = 1920f / 1280;
    private readonly HitEffectState _state = new();
    private readonly NoteHitScanner _scanner = new();
    private readonly Dictionary<HitEffectTexture, Texture2D> _textures = new();
    private readonly Material _additive;
    private readonly RectTransform _bolts;
    private readonly RectTransform _particles;
    private readonly RectTransform _sprites;
    private readonly List<RawImage> _boltPool = new();
    private readonly List<RawImage> _particlePool = new();
    private readonly List<RawImage> _spritePool = new();
    private GuitarNoteRenderer? _noteRenderer;
    private int _heldLanes;

    public RectTransform Root { get; }

    public HitEffectsHud(Transform parent, SkinAssets assets)
    {
        Root = HudVisual.Group("Hit effects", parent);
        // Drawing order follows the z_priority of the GH3 elements: bolts 6, particles 8, hit and held flames 10.
        _bolts = HudVisual.Group("Bolts", Root);
        _particles = HudVisual.Group("Particles", Root);
        _sprites = HudVisual.Group("Flames", Root);
        foreach (var (texture, name) in new[]
        {
            (HitEffectTexture.NoteHit, "gh3-note-hit"), (HitEffectTexture.NoteHitStar, "gh3-note-hit-star"),
            (HitEffectTexture.WhammyHold, "gh3-whammy-hold"), (HitEffectTexture.BigBolt, "gh3-big-bolt"),
            (HitEffectTexture.Spark, "gh3-particle-spark"), (HitEffectTexture.Star, "gh3-particle-star"),
            (HitEffectTexture.Star2, "gh3-particle-star2"),
        })
            _textures[texture] = assets.Textures[name];
        // The sys_* effect materials blend additively (source alpha, one). This shader doubles colour and tint.
        _additive = new Material(Shader.Find("Legacy Shaders/Particles/Additive")) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        _additive.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
    }

    public void Reset()
    {
        _state.Reset();
        _scanner.Reset();
        _noteRenderer = null;
        _heldLanes = 0;
    }

    public void Update(BasePlayer player, GameplayState state, double songTime)
    {
        Track(player, state, songTime);
        _state.Advance(Time.deltaTime);
        Draw();
    }

    private void Track(BasePlayer player, GameplayState state, double songTime)
    {
        foreach (var (lanes, phraseComplete) in _scanner.Update(player, songTime))
        {
            for (var lane = 0; lane < HitEffectState.Lanes; lane++)
                if ((lanes & (1 << lane)) != 0) { _state.Hit(lane, state.StarPowerActive); BindingTrace.FlameLanes++; }
            if (phraseComplete) _state.StarBonus(lanes);
        }

        if (_noteRenderer == null)
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<GuitarNoteRenderer>())
                _noteRenderer = renderer;
        var held = 0;
        if (_noteRenderer != null)
            foreach (var note in _noteRenderer.HeldSustainNotes)
                held |= (note.field_Public_UInt16_0 >> 1) & 31;
        for (var lane = 0; lane < HitEffectState.Lanes; lane++)
            if (((held ^ _heldLanes) & (1 << lane)) != 0) _state.SetWhammy(lane, (held & (1 << lane)) != 0);
        _heldLanes = held;
    }

    private void Draw()
    {
        int bolts = 0, sprites = 0, particles = 0;
        foreach (var sprite in _state.Sprites)
        {
            var bolt = sprite.Texture == HitEffectTexture.BigBolt;
            var image = Take(bolt ? _boltPool : _spritePool, bolt ? _bolts : _sprites, bolt ? bolts++ : sprites++, new Vector2(0.5f, 0));
            var (columns, rows) = sprite.Texture switch
            {
                HitEffectTexture.BigBolt => (8, 1),
                _ => (4, 4),
            };
            var cell = sprite.Cell;
            image.texture = _textures[sprite.Texture];
            image.uvRect = new Rect((float)(cell % columns) / columns, 1 - (float)(cell / columns + 1) / rows, 1f / columns, 1f / rows);
            image.color = Color.white;
            Place(image, sprite.Position, sprite.Size, sprite.Rotation);
        }
        foreach (var system in _state.Systems)
        {
            var texture = _textures[system.Params.Texture];
            var size = new System.Numerics.Vector2(texture.width, texture.height);
            foreach (var particle in system.Particles)
            {
                var image = Take(_particlePool, _particles, particles++, new Vector2(0.5f, 0.5f));
                image.texture = texture;
                image.uvRect = new Rect(0, 0, 1, 1);
                image.color = new Color(particle.Color.X / 255, particle.Color.Y / 255, particle.Color.Z / 255, particle.Color.W / 255);
                Place(image, particle.Position, size * particle.Scale, particle.Rotation);
            }
        }
        Hide(_boltPool, bolts);
        Hide(_spritePool, sprites);
        Hide(_particlePool, particles);
    }

    private RawImage Take(List<RawImage> pool, RectTransform parent, int index, Vector2 pivot)
    {
        if (index == pool.Count)
        {
            var image = HudVisual.Image(parent, "effect", Texture2D.whiteTexture, new Rect(), pivot);
            image.material = _additive;
            pool.Add(image);
        }
        var result = pool[index];
        if (!result.enabled) result.enabled = true;
        return result;
    }

    private static void Hide(List<RawImage> pool, int used)
    {
        for (var index = used; index < pool.Count; index++)
            if (pool[index].enabled) pool[index].enabled = false;
    }

    // GH3 rot_angle turns clockwise on the screen.
    private static void Place(RawImage image, System.Numerics.Vector2 position, System.Numerics.Vector2 size, float rotation)
    {
        var rect = image.rectTransform;
        HudVisual.Position(rect, new Vector2(position.X * CanvasScale, position.Y * CanvasScale));
        rect.sizeDelta = new Vector2(size.X * CanvasScale, size.Y * CanvasScale);
        rect.localEulerAngles = new Vector3(0, 0, -rotation);
    }
}

// Finds the notes that Clone Hero marked as hit since the previous frame. GH3 sends star_sequence_bonus when the last
// note of a Star Power phrase is hit and no note of the phrase was missed.
internal sealed class NoteHitScanner
{
    private Note[] _notes = Array.Empty<Note>();
    private NoteFlags[] _flags = Array.Empty<NoteFlags>();
    private bool[] _seen = Array.Empty<bool>();
    private IntPtr _engine;
    private int _first;
    private double _time = double.NaN;

    public void Reset()
    {
        _notes = Array.Empty<Note>();
        _flags = Array.Empty<NoteFlags>();
        _seen = Array.Empty<bool>();
        _engine = IntPtr.Zero;
        _first = 0;
        _time = double.NaN;
    }

    public List<(int Lanes, bool PhraseComplete)> Update(BasePlayer player, double time)
    {
        var hits = new List<(int, bool)>();
        if (player.engine == null || player.TryCast<GuitarPlayer>() == null) return hits;
        var notes = CloneHeroBindings.Notes(player.engine);
        if (notes == null) return hits;
        if (_engine != player.engine.Pointer || _notes.Length != notes.Count)
        {
            Reset();
            _engine = player.engine.Pointer;
            _notes = Enumerable.Range(0, notes.Count).Select(index => notes[index]).ToArray();
            // Clone Hero clears StarPowerEnd during play, so the phrase flags are kept from the start.
            _flags = _notes.Select(note => note.field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0).ToArray();
            _seen = new bool[_notes.Length];
        }
        if (time < _time)
        {
            Array.Clear(_seen, 0, _seen.Length);
            _first = 0;
            while (_first < _notes.Length && _notes[_first].field_Public_Double_0 < time - 1) _first++;
        }
        for (var index = _first; index < _notes.Length; index++)
        {
            var note = _notes[index];
            if (note.field_Public_Double_0 > time + 1) break;
            var state = note.field_Public_EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique_0;
            var hit = (state & NoteState.Hit) != 0;
            // Bit 0 is the open note, which GH3 does not have; bits 1-5 are green to orange.
            if (hit && !_seen[index] && time >= _time)
                hits.Add(((CloneHeroBindings.NoteLanes(note) >> 1) & 31, PhraseComplete(index)));
            _seen[index] = hit;
            if (index == _first && (hit || (state & NoteState.Missed) != 0 || note.field_Public_Double_0 < time - 1)) _first++;
        }
        _time = time;
        return hits;
    }

    private bool PhraseComplete(int end)
    {
        if (!InPhrase(end) || ((_flags[end] & NoteFlags.StarPowerEnd) == 0 && end + 1 < _flags.Length && InPhrase(end + 1))) return false;
        for (var index = end - 1; index >= 0 && InPhrase(index) && (_flags[index] & NoteFlags.StarPowerEnd) == 0; index--)
            if ((_notes[index].field_Public_EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique_0 & NoteState.Hit) == 0) return false;
        return true;
    }

    private bool InPhrase(int index) => (_flags[index] & NoteFlags.StarPower) != 0;
}
