using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using PlayerSlot = Il2Cpp.ObjectPublicObPlInPlBoBoObBoObBoUnique;
using PlayerProfile = Il2Cpp.ObjectPublicObBoObAcBo1ObBoObObUnique;

namespace Gh3Skin;

// The highway image of the player's band member. GH3 create_guitarist sets player_status.highway_material to
// sys_<character>_1_highway: the highway follows the character, not the instrument (docs/PARITY-NOTES.md#instruments-and-highways).
// An instrument package can also have a highway; the player's choice of that instrument then wins. Without a highway,
// or without a chosen character, the default GH3 highway (Axel's) stays.
internal static class HighwayChoice
{
    private static readonly Dictionary<string, Texture2D> Textures = new();
    private static bool? _bound;

    // The image file, "" for the default highway.
    public static string Path { get; private set; } = "";
    public static string Source { get; private set; } = "the default highway";

    public static void Clear()
    {
        Path = "";
        Source = "the default highway";
    }

    public static void Choose(string role, IReadOnlyDictionary<string, Packages.CharacterModel> characters,
        IReadOnlyDictionary<string, Packages.InstrumentModel> instruments)
    {
        Clear();
        if (instruments.TryGetValue(role, out var instrument) && instrument.Highway.Length > 0 && File.Exists(instrument.Highway))
            (Path, Source) = (instrument.Highway, $"the highway of instrument {instrument.Id}");
        else if (characters.TryGetValue(role, out var character) && character.Highway.Length > 0 && File.Exists(character.Highway))
            (Path, Source) = (character.Highway, $"the highway of character {character.Id}");
        MelonLogger.Msg($"GH3 highway: {Source} (the player is the {role})");
    }

    // The band role of the Clone Hero player: the bassist for a bass or rhythm chart (GH3 co-op gives the bass and the
    // rhythm part to the bassist), else the guitarist. The two fields are checked by their metadata names, which are the
    // same in the Linux and Windows builds (docs/PARITY-NOTES.md#clone-hero-bindings).
    public static string PlayerRole(BasePlayer player)
    {
        try
        {
            _bound ??= Check(typeof(PlayerSlot), nameof(PlayerSlot.field_Public_Object1PublicObBoObStBoObObObObUnique_0), "ʲʼʽʺʿʹʽʻʵʺʶ") &&
                Check(typeof(PlayerProfile), nameof(PlayerProfile.field_Public_EnumPublicSealedvaGuBaRhSiGuDrSiKePrUnique_0), "ʷʻʺʼʴʶʴʳʽʺʿ");
            if (_bound != true) return "guitarist";
            var part = (int)player.player.field_Public_Object1PublicObBoObStBoObObObObUnique_0.field_Public_EnumPublicSealedvaGuBaRhSiGuDrSiKePrUnique_0;
            // Clone Hero instruments: 0 guitar, 1 bass, 2 rhythm.
            return part is 1 or 2 ? "bassist" : "guitarist";
        }
        catch (Exception error)
        {
            MelonLogger.Warning($"GH3 highway: the player's instrument could not be read ({error.Message}); the guitarist's highway is used");
            _bound = false;
            return "guitarist";
        }
    }

    private static bool Check(Type type, string member, string native)
    {
        var pointer = type.GetField("NativeFieldInfoPtr_" + member, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (pointer == null) return Missing($"{type.Name}.{member} is missing");
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        var field = (IntPtr)pointer.GetValue(null)!;
        var name = field == IntPtr.Zero ? null : IL2CPP.il2cpp_field_get_name_(field);
        return name == native || Missing($"{type.Name}.{member} is \"{name}\", expected \"{native}\"");
    }

    private static bool Missing(string text)
    {
        MelonLogger.Warning($"GH3 highway: {text}; the guitarist's highway is used");
        return false;
    }

    // Decoded once for each file, as SkinAssets loads the default highway.
    public static Texture2D? Texture(string path)
    {
        if (Textures.TryGetValue(path, out var cached) && cached != null) return cached;
        try
        {
            var texture = SkinAssets.LoadTexture(path);
            texture.wrapMode = TextureWrapMode.Repeat;
            Textures[path] = texture;
            return texture;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MelonLogger.Warning($"GH3 highway: {path} could not load ({error.Message}); the default highway stays");
            return null;
        }
    }
}
