using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed class PreviewCapture
{
    private readonly SkinOptions _options;
    private float _gameplayStart;
    private bool _captured;
    private int _captureIndex;

    public PreviewCapture(SkinOptions options) => _options = options;

    public void Reset()
    {
        _gameplayStart = Time.time;
        _captured = false;
        _captureIndex = 0;
    }

    public void Update()
    {
        if (_options.CaptureAfter > 0 && !_captured && Time.time - _gameplayStart > _options.CaptureAfter)
        {
            var screenshot = "/home/hedi/Work/gh3-clonehero-skin/preview.png";
            ScreenCapture.CaptureScreenshot(screenshot);
            MelonLogger.Msg($"Captured {screenshot}");
            _captured = true;
        }
        if (_captureIndex < _options.CaptureTimes.Count &&
            Time.time - _gameplayStart > _options.CaptureTimes[_captureIndex])
        {
            var screenshot = Path.Combine(_options.CaptureDirectory, $"frame-{_captureIndex:00}.png");
            ScreenCapture.CaptureScreenshot(screenshot);
            MelonLogger.Msg($"Captured {screenshot} at {_options.CaptureTimes[_captureIndex]}");
            _captureIndex++;
        }
    }
}
