using Il2Cpp;
using UnityEngine;
using NoteFlags = Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique.EnumNPublicSealedvaNoChDiExChHoStTaSoUnique;

namespace Gh3Skin;

internal sealed partial class GameplaySpriteRenderer
{
    private readonly Dictionary<IntPtr, int> _sustainFrames = new();
    private readonly Dictionary<IntPtr, float> _sustainWidths = new();
    private readonly Dictionary<IntPtr, SustainBend> _sustainBends = new();
    private readonly MaterialPropertyBlock _sustainProperties = new();
    private readonly List<Material> _sustainMaterials = new();
    private readonly HashSet<IntPtr> _sustainMaterialIds = new();
    private readonly WhammyHistory _whammy = new();
    private bool _whammyReady;
    private GuitarNoteRenderer? _noteRenderer;
    private readonly StarWhammy[] _starWhammy = { new(), new(), new(), new(), new() };
    private float _starWhammyTime;
    private float _highwayEndZ;

    private void TrackSustainMaterial(SpriteRenderer renderer)
    {
        var material = renderer.sharedMaterial;
        if (material == null || !_sustainMaterialIds.Add(material.Pointer)) return;
        _sustainMaterials.Add(material);
        FadeLikeHighway(material);
    }

    // Clone Hero changes the sustain sprite only when the held state changes, so the last native frame and the native
    // width are kept per renderer. GH3 draws the bar texture at full strength (WhammyBar_UI: texture * vertex colour),
    // so the native lane tint is replaced with white. The held sprite is wider because it includes GH3's glow.
    private void UpdateSustain(Transform transform, SpriteRenderer renderer, string name, bool starPowerActive)
    {
        var key = renderer.Pointer;
        if (SustainState.TryFrame(name, out var frame))
        {
            _sustainFrames[key] = frame;
            _sustainWidths.TryAdd(key, renderer.size.x);
        }
        else if (!_sustainFrames.TryGetValue(key, out frame))
            return;
        var lane = NearestLane(transform.localPosition.x);
        var variant = SustainState.Variant(frame, lane, starPowerActive || _starWhammy[lane].Star);
        var sprite = frame == 0 ? _assets.HeldSustain(variant) : _assets.Sustain(variant);
        if (renderer.sprite?.Pointer != sprite.Pointer)
        {
            renderer.sprite = sprite;
            var size = renderer.size;
            renderer.size = new Vector2(_sustainWidths[key] * sprite.rect.width / SkinAssets.SustainSpriteWidth, size.y);
        }
        var color = renderer.color;
        if (color.r < 1 || color.g < 1 || color.b < 1 || color.a < 1)
            renderer.color = Color.white;
        _sustainBends.TryGetValue(key, out var bend);
        var fret = _frets[lane];
        // Clone Hero spawns notes at the far end of the visible highway, which is GH3's highway line 0.
        var highwayLength = fret != null ? _highwayEndZ - fret.position.z : 0;
        if (frame == 0 && _whammyReady && highwayLength > 0)
        {
            if (bend == null)
                _sustainBends[key] = bend = new SustainBend(renderer);
            bend.Update(renderer, _sustainProperties, _whammy, SustainState.WhammyChannel(lane), fret!.position.z, highwayLength);
        }
        else
            bend?.Hide(renderer);
    }

    // GH3 spawns star_whammy for each held Star Power phrase sustain and steps it once per 60 Hz game frame.
    private void UpdateStarWhammy()
    {
        var held = 0;
        if (_noteRenderer != null)
            foreach (var note in _noteRenderer.HeldSustainNotes)
                if ((note.field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0 & NoteFlags.StarPower) != 0)
                    held |= note.field_Public_UInt16_0;
        _starWhammyTime += Time.deltaTime;
        var ticks = Math.Min((int)(_starWhammyTime * StarWhammy.FramesPerSecond), 10);
        _starWhammyTime = Math.Min(_starWhammyTime - ticks / StarWhammy.FramesPerSecond, 1 / StarWhammy.FramesPerSecond);
        for (var lane = 0; lane < _starWhammy.Length; lane++)
        {
            var state = _starWhammy[lane];
            var channel = SustainState.WhammyChannel(lane);
            if ((held & (1 << channel)) == 0 || !_whammyReady)
            {
                state.Reset();
                continue;
            }
            for (var tick = ticks - 1; tick >= 0; tick--)
            {
                var age = tick / StarWhammy.FramesPerSecond;
                state.Step(_whammy.Value(channel, age) - _whammy.Value(channel, age + 1 / StarWhammy.FramesPerSecond));
            }
        }
    }
}
