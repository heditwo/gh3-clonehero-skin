using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed class GameplaySpriteRenderer
{
    private const float Gh3NoteStep = 1.75f;
    private readonly SkinAssets _assets;
    private readonly SkinOptions _options;
    private float _nextInspection;

    public GameplaySpriteRenderer(SkinAssets assets, SkinOptions options)
    {
        _assets = assets;
        _options = options;
    }

    public void Update()
    {
        foreach (var player in UnityEngine.Object.FindObjectsOfType<BasePlayer>())
        {
            PositionHighway(player.transform);
            break;
        }

        var renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
        var changed = 0;
        foreach (var renderer in renderers)
        {
            var original = renderer.sprite;
            if (original != null && original.name.StartsWith("GH3_", StringComparison.Ordinal))
                continue;

            var name = original?.name ?? "";
            var objectName = renderer.gameObject.name;
            if (objectName == "Head" && renderer.transform.parent?.name == "Note(Clone)")
            {
                ReplaceGem(renderer, name);
                continue;
            }
            if (objectName is "Sustain(Clone)" or "Sustain Glow" && name.StartsWith("spr_sustain_strip6_", StringComparison.Ordinal))
            {
                var color = renderer.color;
                renderer.color = new Color(color.r, color.g, color.b, Math.Min(color.a, 0.55f));
            }
            if (objectName == "Neck" && original != null && renderer.transform.parent?.name == "Guitar Player(Clone)")
            {
                renderer.sprite = _assets.Highway(original);
                continue;
            }
            if (ReplaceEffect(renderer, name, objectName))
                changed++;
        }

        if (changed > 0 && Time.time >= _nextInspection)
        {
            MelonLogger.Msg($"Replaced {changed} active GH3 gameplay sprites");
            _nextInspection = Time.time + 8;
        }
    }

    private void ReplaceGem(SpriteRenderer renderer, string name)
    {
        if (_options.KeepStockGems)
            return;
        var note = renderer.transform.parent;
        var open = name.StartsWith("spr_open_notes_strip5_", StringComparison.Ordinal);
        var standard = name.StartsWith("spr_newnotes_strip4_", StringComparison.Ordinal);
        var star = name.StartsWith("spr_star_notes_", StringComparison.Ordinal);
        if (!open && !standard && !star)
            return;
        var lane = Math.Clamp((int)MathF.Round((renderer.transform.position.x + 0.39f) / 0.195f), 0, 4);
        var notePosition = note.localPosition;
        var body = note.Find("Body")?.GetComponent<SpriteRenderer>();
        var anim = note.Find("Anim")?.GetComponent<SpriteRenderer>();
        var openHopo = note.Find("Open_HOPO")?.GetComponent<SpriteRenderer>();
        var gemTransform = note.Find("GH3 Gem");
        var tap = body?.sprite?.name == "spr_newnotes_strip4_3";
        if (open || tap)
        {
            if (gemTransform != null)
            {
                gemTransform.GetComponent<SpriteRenderer>().enabled = false;
                renderer.enabled = true;
                if (body != null) body.enabled = body.sprite != null;
                if (anim != null) anim.enabled = anim.sprite != null;
                if (openHopo != null) openHopo.enabled = openHopo.sprite != null;
            }
            return;
        }
        if (gemTransform == null)
        {
            var gem = new GameObject("GH3 Gem");
            gem.layer = renderer.gameObject.layer;
            gem.transform.SetParent(note, false);
            var gemRenderer = gem.AddComponent<SpriteRenderer>();
            gemRenderer.sortingLayerID = renderer.sortingLayerID;
            gemRenderer.sortingOrder = 10;
            gemTransform = gem.transform;
        }
        var laneName = SkinAssets.Lane(lane);
        var visualLead = Math.Clamp(notePosition.z, 0, Gh3NoteStep);
        gemTransform.localPosition = new Vector3(0, 0.008f, visualLead);
        var size = 1 - 0.17f * visualLead / Gh3NoteStep;
        gemTransform.localScale = new Vector3(size, size, 1);
        var display = gemTransform.GetComponent<SpriteRenderer>();
        display.enabled = true;
        display.sprite = star ? _assets.GetSprite($"{laneName}-{(name == "spr_star_notes_strip4_1" ? "star-glow" : "star")}-{(int)(Time.time * 12) % 16}")
            : _assets.GetSprite($"{laneName}-{(name == "spr_newnotes_strip4_1" ? "glow" : "note")}");
        renderer.enabled = false;
        if (body != null) body.enabled = false;
        if (anim != null) anim.enabled = false;
        if (openHopo != null) openHopo.enabled = false;
    }

    private bool ReplaceEffect(SpriteRenderer renderer, string name, string objectName)
    {
        string? replacement = null;

        if (renderer.transform.position.z < -4.5f && renderer.transform.position.x > 4)
            replacement = "clear";

        else if (objectName == "Beatline(Clone)")
            replacement = "beatline";
        else if (!_options.KeepStockFlames && objectName == "SustainSpark" && name.StartsWith("sustain_spark_anim_color_", StringComparison.Ordinal))
        {
            renderer.enabled = false;
            return false;
    }
        else if (!_options.KeepStockFlames && objectName == "SustainSpark Overlay" &&
                 name.StartsWith("sustain_spark_anim_overlay_", StringComparison.Ordinal) &&
                 int.TryParse(name[27..], out var sustainFrame))
            replacement = $"sustain-flame-{sustainFrame:00}";
        else if (!_options.KeepStockFlames && objectName == "SPSparks" && name.StartsWith("hit_spark_", StringComparison.Ordinal) &&
                 int.TryParse(name[10..], out var sparkFrame))
            replacement = $"sp-spark-{sparkFrame:00}";
        else if (!_options.KeepStockFlames && name.StartsWith("hitflame_anim_16_", StringComparison.Ordinal) && int.TryParse(name[17..], out var frame))
            replacement = $"hit-{Math.Clamp(frame, 0, 15):00}";

        if (replacement == null || !_assets.TryGetSprite(replacement, out var sprite))
            return false;

        renderer.sprite = sprite;
        if (replacement.StartsWith("hit-", StringComparison.Ordinal))
            renderer.color = new Color(0.765f, 0.336f, 0, 1);
        else if (replacement != "clear" && !replacement.StartsWith("sp-spark-", StringComparison.Ordinal))
            renderer.color = Color.white;
        return true;
    }

    private static void PositionHighway(Transform player)
    {
        var camera = player.Find("PlayerCamera");
        if (camera != null)
            camera.localPosition = new Vector3(-0.005f, 1, -3.97f);

        var neck = player.Find("Neck");
        if (neck != null)
            neck.localScale = new Vector3(0.96f, 5.05f, 1);

        foreach (var name in new[] { "sidebar_l", "sidebar_r" })
        {
            var sidebar = player.Find(name)?.GetComponent<SpriteRenderer>();
            if (sidebar != null)
                sidebar.enabled = false;
        }
    }
}
