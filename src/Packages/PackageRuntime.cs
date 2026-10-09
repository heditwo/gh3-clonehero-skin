using System.Collections;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Gh3Skin.Packages;

// Starts the package importer when Clone Hero's content folders are known, and writes its log lines on the main thread.
internal static class PackageRuntime
{
    public static PackageManager? Manager { get; private set; }
    public static string ChoicesPath => Path.Combine(MelonEnvironment.UserDataDirectory, "GH3Skin", "choices.json");
    private static PlayerChoices? _choices;

    // The picker's choices; read once, then kept in memory and saved by the picker.
    public static PlayerChoices Choices
    {
        get
        {
            if (_choices != null) return _choices;
            _choices = PlayerChoices.Load(ChoicesPath, out var problem);
            if (problem.Length > 0) MelonLogger.Warning("GH3 picker: " + problem);
            return _choices;
        }
    }

    public static void SaveChoices()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChoicesPath)!);
            Choices.Save(ChoicesPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MelonLogger.Warning($"GH3 picker: the choices could not be saved to {ChoicesPath} ({error.Message})");
        }
    }
    public static bool Disabled => Environment.GetEnvironmentVariable("GH3_SKIN_PACKAGES") == "off";
    private static int _frames;

    public static string ModDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "GH3Skin");

    public static void Update()
    {
        if (Disabled) return;
        if (Manager == null)
        {
            var custom = CustomContentPaths();
            // GlobalVariables is ready in the first scene; start without the Custom folders if it never is.
            if (custom == null && ++_frames < 600) return;
            Start(custom ?? new List<string>());
        }
        while (Manager!.TryDequeueLog(out var line))
            if (line.Warning) MelonLogger.Warning(line.Text);
            else MelonLogger.Msg(line.Text);
    }

    private static void Start(List<string> custom)
    {
        SemanticVersion.TryParse(ModInfo.Version, out var version);
        var compressed = SystemInfo.SupportsTextureFormat(TextureFormat.DXT1) && SystemInfo.SupportsTextureFormat(TextureFormat.DXT5);
        var settings = new PackageSettings(
            Path.Combine(ModDirectory, "Packages"),
            custom,
            Path.Combine(MelonEnvironment.UserDataDirectory, "GH3Skin", "Cache"),
            compressed,
            Application.platform.ToString(),
            SystemInfo.graphicsDeviceType.ToString(),
            version,
            Environment.GetEnvironmentVariable("GH3_SKIN_STAGE") ?? (Choices.Stage == PlayerChoices.SongStage ? PlayerChoices.FallbackStage : Choices.Stage),
            Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        MelonLogger.Msg($"GH3 packages: scanning {string.Join(", ", custom.SelectMany(c => new[] { Path.Combine(c, "Stages"), Path.Combine(c, "Characters") }).Append(settings.ModPackages))}; " +
            $"cache {settings.CacheRoot}; textures {(compressed ? "BC1/BC3" : "RGBA32 (BC1/BC3 not supported)")}; {settings.Platform} {settings.GraphicsApi}");
        Manager = new PackageManager(settings);
        Manager.Start();
        Update();
    }

    // GlobalVariables.uniqueCustomContentPaths: the Custom folder and the custom_content_directories of settings.ini.
    private static List<string>? CustomContentPaths()
    {
        try
        {
            var type = typeof(Il2Cpp.GlobalVariables);
            var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
            var instance = type.GetProperty("instance", flags)?.GetValue(null);
            var property = type.GetProperty("uniqueCustomContentPaths", flags);
            if (property == null) return new List<string>();
            var value = property.GetMethod!.IsStatic ? property.GetValue(null) : instance == null ? null : property.GetValue(instance);
            if (value == null) return null;
            var result = new List<string>();
            if (value is IEnumerable sequence)
                foreach (var item in sequence) if (item?.ToString() is { Length: > 0 } text) result.Add(text);
            if (result.Count == 0 && value.GetType().GetProperty("Count")?.GetValue(value) is int count)
            {
                var indexer = value.GetType().GetMethod("get_Item", new[] { typeof(int) });
                for (var i = 0; i < count && indexer != null; i++)
                    if (indexer.Invoke(value, new object[] { i })?.ToString() is { Length: > 0 } text) result.Add(text);
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception error)
        {
            MelonLogger.Warning($"GH3 packages: Clone Hero content folders are not available ({error.Message}); only Mods/GH3Skin/Packages is scanned");
            return new List<string>();
        }
    }
}
