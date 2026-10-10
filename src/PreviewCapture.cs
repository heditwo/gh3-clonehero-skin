using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Timed screenshots for captures (GH3_SKIN_CAPTURE_TIMES, on the scene or the song clock).

internal sealed class PreviewCapture
{
    private readonly SkinOptions _options;
    private float _gameplayStart;
    private bool _captured;
    private int _captureIndex;
    private double _earliest = double.PositiveInfinity;

    public PreviewCapture(SkinOptions options)
    {
        _options = options;
        if (options.CaptureTimes.Count > 0)
            Directory.CreateDirectory(options.CaptureDirectory);
        if (options.CaptureAfter > 0)
            Directory.CreateDirectory(Path.GetDirectoryName(options.ScreenshotPath)!);
    }

    public void Reset()
    {
        _gameplayStart = Time.time;
        _captured = false;
        _captureIndex = 0;
        _earliest = double.PositiveInfinity;
    }

    public void Update(double songTime)
    {
        var elapsed = _options.CaptureSongClock ? songTime : Time.time - _gameplayStart;
        _earliest = Math.Min(_earliest, elapsed);
        if (_options.CaptureAfter > 0 && !_captured && Time.time - _gameplayStart > _options.CaptureAfter)
        {
            var screenshot = _options.ScreenshotPath;
            ScreenCapture.CaptureScreenshot(screenshot);
            MelonLogger.Msg($"Captured {screenshot}");
            _captured = true;
        }
        // A negative song time is captured after the clock passed it from below: Clone Hero holds the clock at -1 s while
        // the song loads, before the stage moves it back to the start of the intro.
        if (_captureIndex < _options.CaptureTimes.Count && elapsed >= _options.CaptureTimes[_captureIndex] && _earliest <= _options.CaptureTimes[_captureIndex])
        {
            var screenshot = Path.Combine(_options.CaptureDirectory, $"frame-{_captureIndex:00}.png");
            ScreenCapture.CaptureScreenshot(screenshot);
            MelonLogger.Msg($"Captured {screenshot} at {_options.CaptureTimes[_captureIndex]} (song time {songTime:0.000})");
            _captureIndex++;
        }
    }
}
