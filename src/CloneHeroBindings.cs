using System.Reflection;
using System.Runtime.CompilerServices;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using Engine = Il2Cpp.ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique;
using Note = Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique;
using NoteList = Il2CppSystem.Collections.Generic.List<Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique>;
using SongClock = Il2Cpp.ObjectPublicDoSiBoSiDoBoSiDoSiDoUnique;
using WhammyManager = Il2Cpp.ObjectPublicAbstractSealedInBoInObDoInSiDoInDoUnique;

namespace Gh3Skin;

// Il2CppInterop names obfuscated methods by their order and caller count, so the same name can point to another method in
// another build (Linux and Windows 1.1.0.6142 differ). The obfuscated metadata names are the same in both builds, so the
// skin binds methods by those names and checks that each obfuscated field it reads has the expected metadata name.
// Evidence: docs/PARITY-NOTES.md#clone-hero-bindings.
internal static class CloneHeroBindings
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static Func<Engine, int> Multiplier { get; private set; } = null!;
    public static Func<Engine, float> StarPowerCharge { get; private set; } = null!;
    public static Func<Engine, NoteList> Notes { get; private set; } = null!;
    public static Func<Note, ushort> NoteLanes { get; private set; } = null!;
    public static Action<HighwayScroll, Texture2D> SetHighwayTexture { get; private set; } = null!;
    public static Action<int, ushort, byte> SetWhammy { get; private set; } = null!;
    public static Action<TrackFadeManager> RecalculateTrackFade { get; private set; } = null!;

    private static readonly (Type Type, string Member, string Native)[] Fields =
    {
        (typeof(ScoreManager), nameof(ScoreManager.field_Private_Int32_2), "ʼʳʽʺʴʺʴʴˁʵʶ"),
        (typeof(ScoreManager), nameof(ScoreManager.field_Private_Int32_4), "ʸʶʻʲʲˀʷʻʳʸʲ"),
        (typeof(Engine), nameof(Engine.field_Protected_Single_0), "ʺʼʻʳʼʳʲˀʼʸʺ"),
        (typeof(Engine), nameof(Engine.field_Public_Boolean_0), "ˁʸʴʵʳʾʽʿʹʷʿ"),
        (typeof(Note), nameof(Note.field_Public_Double_0), "ʲʸʺʺʼʷʵʵʶʵˁ"),
        (typeof(Note), nameof(Note.field_Public_Double_1), "ʲʸʷʽʵʷʶʲʿʽˀ"),
        (typeof(Note), nameof(Note.field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0), "ˀʷˁʵʹʳʿʵʼʹˀ"),
        (typeof(Note), nameof(Note.field_Public_UInt16_0), "ʿˀʸʻʺʸʳʺʴʻʿ"),
        (typeof(Note), nameof(Note.field_Public_EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique_0), "ʺʺʴʶʽʴʳʵˀʽʳ"),
        (typeof(HighwayScroll), nameof(HighwayScroll.field_Private_Single_1), "ʷʻʺˀʿʵʵʹʽˁʴ"),
        (typeof(HighwayScroll), nameof(HighwayScroll.field_Private_Vector4_0), "ʳˀʳʽʻʳʺˁʲʵʽ"),
        (typeof(WhammyManager), nameof(WhammyManager.field_Private_Static_Int32_0), "ʿʴˀʴʹʷʺʻʸʳʽ"),
        (typeof(WhammyManager), nameof(WhammyManager.field_Private_Static_Double_2), "ʻʳʷʶˀʻʻʳʶʻʹ"),
        (typeof(SongClock), nameof(SongClock.field_Private_Double_2), "ʲʻʺʹʺʲʷʾʻʺʹ"),
        (typeof(SongClock), nameof(SongClock.field_Private_Double_3), "ˁʵˁʺʳʻʸʶʼʽʺ"),
        (typeof(GuitarNoteRenderer), nameof(GuitarNoteRenderer.field_Protected_Double_0), "ʲʾʴʾʹʵʴʺʶʿʸ"),
    };

    public static string? Bind()
    {
        try
        {
            foreach (var (type, member, native) in Fields) CheckField(type, member, native);
            Multiplier = Method(typeof(Engine), "ʼʺʹʷʵʺʸʺʻʼʶ", 0).CreateDelegate<Func<Engine, int>>();
            StarPowerCharge = Method(typeof(Engine), "ʶˀʲʵʳˁʾʾˀʼʻ", 0).CreateDelegate<Func<Engine, float>>();
            Notes = Method(typeof(Engine), "ʸʲʾʵʸʹʻʿʽʸʲ", 0).CreateDelegate<Func<Engine, NoteList>>();
            NoteLanes = Method(typeof(Note), "ˀʶˀʼʲʾʸʴʴʶʿ", 0).CreateDelegate<Func<Note, ushort>>();
            SetHighwayTexture = Method(typeof(HighwayScroll), "ʳʷʽʴʺʻʷʳʻʼʷ", 1).CreateDelegate<Action<HighwayScroll, Texture2D>>();
            SetWhammy = Method(typeof(WhammyManager), "ʲʽʻʷʻʽʼʷˀʾʾ", 3).CreateDelegate<Action<int, ushort, byte>>();
            RecalculateTrackFade = Method(typeof(TrackFadeManager), "ʲˁʴʾʸʿʴʿˁʾˀ", 0).CreateDelegate<Action<TrackFadeManager>>();
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private static void CheckField(Type type, string member, string native)
    {
        for (var owner = type; owner != null; owner = owner.BaseType)
        {
            var pointer = owner.GetField("NativeFieldInfoPtr_" + member, Members);
            if (pointer == null) continue;
            RuntimeHelpers.RunClassConstructor(owner.TypeHandle);
            var field = (IntPtr)pointer.GetValue(null)!;
            var name = field == IntPtr.Zero ? null : IL2CPP.il2cpp_field_get_name_(field);
            if (name != native) throw new MissingFieldException($"{type.Name}.{member} is \"{name}\", expected \"{native}\"");
            return;
        }
        throw new MissingFieldException($"{type.Name}.{member} is missing");
    }

    private static MethodInfo Method(Type type, string native, int parameters)
    {
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        const string prefix = "NativeMethodInfoPtr_";
        var matches = type.GetFields(Members)
            .Where(field => field.IsStatic && field.FieldType == typeof(IntPtr) && field.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Where(field => (IntPtr)field.GetValue(null)! is var method && method != IntPtr.Zero &&
                IL2CPP.il2cpp_method_get_name_(method) == native && IL2CPP.il2cpp_method_get_param_count(method) == parameters)
            .SelectMany(field => type.GetMethods(Members).Where(method =>
                method.Name == field.Name[prefix.Length..] && method.GetParameters().Length == parameters))
            .ToList();
        if (matches.Count != 1) throw new MissingMethodException($"{type.Name} has {matches.Count} methods named \"{native}\" with {parameters} parameters");
        return matches[0];
    }
}
