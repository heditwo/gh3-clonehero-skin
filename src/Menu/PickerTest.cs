using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Test aid for the picker without a mouse (scripts/capture-menu.sh). GH3_SKIN_PICKER_TEST is a list of steps separated by
// ';': `ready` (wait for the package import), `wait <seconds>`, `open`, `close`, `apply stage=<id> <role>=<id> venueTrack=on|off generatedDirection=on|off ...`,
// `capture <png path>`, `quit`. The steps run in Clone Hero's menus only. Off when the variable is not set.
internal sealed class PickerTest
{
    private readonly Queue<string> _steps;
    private float _until;

    private PickerTest(IEnumerable<string> steps) => _steps = new Queue<string>(steps);

    public static PickerTest? FromEnvironment() =>
        Environment.GetEnvironmentVariable("GH3_SKIN_PICKER_TEST") is { Length: > 0 } text
            ? new PickerTest(text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : null;

    public void Update(PickerOverlay picker)
    {
        if (!picker.Menus || Time.realtimeSinceStartup < _until || _steps.Count == 0) return;
        var step = _steps.Peek();
        var words = step.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (words[0])
        {
            case "ready":
                if (Packages.PackageRuntime.Manager?.Finished != true) return;
                break;
            case "wait":
                _until = Time.realtimeSinceStartup + float.Parse(words[1], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "open":
                picker.Show();
                break;
            case "close":
                picker.Close();
                break;
            case "apply":
                var pairs = words[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
                picker.Apply(pairs.FirstOrDefault(p => p[0] == "stage")?[1] ?? "",
                    pairs.Where(p => p[0] != "stage").ToDictionary(p => p[0], p => p[1]));
                break;
            case "capture":
                ScreenCapture.CaptureScreenshot(words[1]);
                MelonLogger.Msg($"GH3 picker test: captured {words[1]}");
                _until = Time.realtimeSinceStartup + 1;
                break;
            case "quit":
                MelonLogger.Msg("GH3 picker test: quit");
                Application.Quit();
                break;
            default:
                MelonLogger.Warning($"GH3 picker test: unknown step '{step}'");
                break;
        }
        _steps.Dequeue();
    }
}
