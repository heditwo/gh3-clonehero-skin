using UnityEngine;

namespace Gh3Skin;

// Gem sprites: GH3 gems, HOPOs, taps, and Star Power gems on Clone Hero's notes.

internal sealed partial class GameplaySpriteRenderer
{
    private void ReplaceGem(GemVisual note, bool starPowerActive, int frame)
    {
        var name = SpriteName(note.Head.sprite);
        var kind = GemKind.FromSprites(name, SpriteName(note.Body?.sprite));
        if (kind == GemKind.Stock)
        {
            if (note.Display != null)
            {
                note.Display.enabled = false;
                note.Head.enabled = true;
                if (note.Body != null) note.Body.enabled = note.Body.sprite != null;
                if (note.Anim != null) note.Anim.enabled = note.Anim.sprite != null;
                if (note.OpenHopo != null) note.OpenHopo.enabled = note.OpenHopo.sprite != null;
            }
            return;
        }
        if (note.Display == null)
        {
            var gem = new GameObject("GH3 Gem");
            gem.layer = note.Head.gameObject.layer;
            gem.transform.SetParent(note.Transform, false);
            // Tuned: lifts the gem above Clone Hero's note head.
            gem.transform.localPosition = new Vector3(0, 0.008f, 0);
            note.Display = gem.AddComponent<SpriteRenderer>();
            note.Display.sortingLayerID = note.Head.sortingLayerID;
            note.Display.sortingOrder = 10;
        }
        note.Display.enabled = true;
        note.Display.sprite = _assets.Gem(NearestLane(note.Transform.localPosition.x), kind, frame, starPowerActive);
        note.Display.color = new Color(1, 1, 1, HighwayView.FadeAt(note.Transform.localPosition.z));
        note.Head.enabled = false;
        if (note.Body != null) note.Body.enabled = false;
        if (note.Anim != null) note.Anim.enabled = false;
        if (note.OpenHopo != null) note.OpenHopo.enabled = false;
    }

    private int NearestLane(float x)
    {
        var lane = 0;
        var distance = float.PositiveInfinity;
        for (var candidate = 0; candidate < _frets.Length; candidate++)
        {
            var fret = _frets[candidate];
            if (fret == null)
                continue;
            var candidateDistance = Math.Abs(x - fret.localPosition.x);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                lane = candidate;
            }
        }
        return lane;
    }

    private sealed class GemVisual
    {
        public Transform Transform { get; }
        public GameObject Object { get; }
        public SpriteRenderer Head { get; }
        public SpriteRenderer? Body { get; }
        public SpriteRenderer? Anim { get; }
        public SpriteRenderer? OpenHopo { get; }
        public SpriteRenderer? Display { get; set; }

        public GemVisual(Transform transform)
        {
            Transform = transform;
            Object = transform.gameObject;
            Head = transform.Find("Head").GetComponent<SpriteRenderer>();
            Body = transform.Find("Body")?.GetComponent<SpriteRenderer>();
            Anim = transform.Find("Anim")?.GetComponent<SpriteRenderer>();
            OpenHopo = transform.Find("Open_HOPO")?.GetComponent<SpriteRenderer>();
            Display = transform.Find("GH3 Gem")?.GetComponent<SpriteRenderer>();
        }
    }
}
