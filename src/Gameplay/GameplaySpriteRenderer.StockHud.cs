using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Clone Hero's stock HUD is replaced by the GH3 HUD: the screen HUD (HUDCam/HUD: score, streak, star rating, song
// progress) and the HUD of the player's highway (BasePlayer: the Star Power bar beside the highway, the multiplier ball,
// the rock meter, the full-combo indicator, the accuracy text). Every renderer and UI graphic under them is
// kept off, also inactive ones and ones that Clone Hero creates or turns on later. The other HUDCam features (input
// viewer, lyrics, leaderboard, drag handles) and the player's countdown and solo counter stay.

internal sealed partial class GameplaySpriteRenderer
{
    private const string StockHudName = "HUD";
    private const float StockHudScanSeconds = 1;
    private const float StockHudTraceSeconds = 2;
    private readonly List<Renderer> _stockHud = new();
    private readonly List<UnityEngine.UI.Graphic> _stockHudGraphics = new();
    private readonly HashSet<IntPtr> _stockHudIds = new();
    private readonly SortedSet<string> _stockHudNames = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _keptHudNames = new(StringComparer.Ordinal);
    private float _nextStockHudScan;
    private float _nextStockHudTrace;
    private int _loggedStockHud;
    private int _hiddenLayer = -1;
    // Unity has 32 layers; 0 to 7 are its built-in layers.
    private const int LastLayer = 31;
    private const int FirstUserLayer = 8;

    private void ResetStockHud()
    {
        _stockHud.Clear();
        _stockHudGraphics.Clear();
        _stockHudIds.Clear();
        _nextStockHudScan = 0;
        _nextStockHudTrace = 0;
        _stockHudNames.Clear();
        _keptHudNames.Clear();
        _loggedStockHud = 0;
        _hiddenLayer = -1;
    }

    private void HideStockHud(Il2Cpp.BasePlayer player)
    {
        if (Time.unscaledTime >= _nextStockHudScan)
        {
            _nextStockHudScan = Time.unscaledTime + StockHudScanSeconds;
            ScanPlayerHud(player);
            ScanStockHud();
            _hiddenLayer = HiddenLayer(_hiddenLayer);
        }
        // Clone Hero sets forceRenderingOff of its HUD again on some systems (measured on Linux: the score panel showed),
        // so the parts also move to a layer that no camera draws.
        foreach (var renderer in _stockHud)
        {
            if (renderer == null) continue;
            if (!renderer.forceRenderingOff) renderer.forceRenderingOff = true;
            if (_hiddenLayer >= 0 && renderer.gameObject.layer != _hiddenLayer) renderer.gameObject.layer = _hiddenLayer;
        }
        foreach (var graphic in _stockHudGraphics)
            if (graphic != null && !graphic.canvasRenderer.cull)
                graphic.canvasRenderer.cull = true;
        // isVisible tells whether a camera drew the part in the last frame, so the first check waits.
        if (BindingTrace.Enabled && _nextStockHudTrace == 0) _nextStockHudTrace = Time.unscaledTime + StockHudTraceSeconds;
        if (BindingTrace.Enabled && Time.unscaledTime >= _nextStockHudTrace)
        {
            _nextStockHudTrace = Time.unscaledTime + StockHudTraceSeconds;
            TraceStockHud();
        }
    }

    // The highest layer that no camera draws, kept while it stays free.
    private static int HiddenLayer(int current)
    {
        var drawn = 0;
        foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>()) drawn |= camera.cullingMask;
        if (current >= 0 && (drawn & (1 << current)) == 0) return current;
        for (var layer = LastLayer; layer >= FirstUserLayer; layer--)
            if ((drawn & (1 << layer)) == 0) return layer;
        return -1;
    }

    private void ScanPlayerHud(Il2Cpp.BasePlayer player)
    {
        CollectPlayerHud(player.spBar, "spBar");
        CollectPlayerHud(player.comboCounter, "comboCounter");
        CollectPlayerHud(player.healthContainer, "healthContainer");
        CollectPlayerHud(player.FCIndicator?.transform, "FCIndicator");
        CollectPlayerHud(player.AccuracyObject?.transform, "AccuracyObject");
    }

    private void CollectPlayerHud(Component? part, string field)
    {
        if (part == null) return;
        var root = part.transform;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            if (_stockHudIds.Add(renderer.Pointer))
            {
                _stockHud.Add(renderer);
                _stockHudNames.Add($"{field}:{renderer.name}");
            }
        foreach (var graphic in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            if (_stockHudIds.Add(graphic.Pointer))
            {
                _stockHudGraphics.Add(graphic);
                _stockHudNames.Add($"{field}:{graphic.name}");
            }
    }

    // GameObject.Find sees only active objects, so the search repeats until HUDCam exists, and each scan also finds
    // stock parts that Clone Hero creates later.
    private void ScanStockHud()
    {
        var camera = GameObject.Find("HUDCam")?.transform;
        if (camera == null) return;
        for (var child = 0; child < camera.childCount; child++)
        {
            var part = camera.GetChild(child);
            if (part.name == StockHudName)
                CollectStockHud(part, part.name);
            else
                _keptHudNames.Add(part.name);
        }
        if (_stockHudNames.Count + _keptHudNames.Count == _loggedStockHud) return;
        _loggedStockHud = _stockHudNames.Count + _keptHudNames.Count;
        MelonLogger.Msg($"Clone Hero stock HUD hidden ({_stockHudNames.Count}): {string.Join(", ", _stockHudNames)}; " +
            $"HUDCam features kept: {string.Join(", ", _keptHudNames)}");
    }

    private void CollectStockHud(Transform transform, string path)
    {
        foreach (var renderer in transform.GetComponents<Renderer>())
            if (_stockHudIds.Add(renderer.Pointer))
            {
                _stockHud.Add(renderer);
                _stockHudNames.Add(path);
            }
        foreach (var graphic in transform.GetComponents<UnityEngine.UI.Graphic>())
            if (_stockHudIds.Add(graphic.Pointer))
            {
                _stockHudGraphics.Add(graphic);
                _stockHudNames.Add(path);
            }
        for (var child = 0; child < transform.childCount; child++)
        {
            var next = transform.GetChild(child);
            CollectStockHud(next, path + "/" + next.name);
        }
    }

    // Verification: the stock HUD parts that would draw (must be none), and the stock player rails.
    private void TraceStockHud()
    {
        var shown = new List<string>();
        var active = new List<string>();
        foreach (var renderer in _stockHud)
            if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
            {
                if (renderer.isVisible) shown.Add(renderer.name);
                else active.Add(renderer.name);
            }
        foreach (var graphic in _stockHudGraphics)
            if (graphic != null && graphic.enabled && graphic.gameObject.activeInHierarchy)
            {
                if (!graphic.canvasRenderer.cull) shown.Add(graphic.name);
                else active.Add(graphic.name);
            }
        foreach (var entry in _effects)
            if (entry.Name is "sidebar_l" or "sidebar_r" && entry.Renderer != null && entry.Renderer.enabled && entry.Object.activeInHierarchy)
                shown.Add(entry.Name);
        MelonLogger.Msg($"[trace] stock hud parts={_stockHud.Count + _stockHudGraphics.Count} layer={_hiddenLayer} shown={shown.Count}" +
            (shown.Count > 0 ? " " + string.Join(",", shown) : "") + $" hiddenActive={active.Count}");
    }
}
