using System.Runtime.InteropServices;

namespace Gh3Skin;

internal static class StageAudioBackend
{
    private const string Library = "gh3_stage_bass";
    private static readonly IntPtr Handle;

    static StageAudioBackend()
    {
        var name = OperatingSystem.IsWindows() ? "bass.dll" : OperatingSystem.IsMacOS() ? "libbass.dylib" : "libbass.so";
        // The data folder is clonehero_Data on Linux and Clone Hero_Data on Windows; Windows keeps plugins in x86_64.
        var data = UnityEngine.Application.dataPath;
        var root = SkinEnvironment.Get(SkinEnvironment.GameDirectory) ?? Environment.CurrentDirectory;
        var candidates = new[]
        {
            Path.Combine(data, "Plugins", name), Path.Combine(data, "Plugins", "x86_64", name),
            Path.Combine(root, "clonehero_Data", "Plugins", name), Path.Combine(root, "clonehero_Data", "Plugins", "x86_64", name), Path.Combine(root, name)
        };
        var path = candidates.FirstOrDefault(File.Exists);
        // The game has already loaded BASS, so the loader can also find it by name.
        if (path != null) Handle = NativeLibrary.Load(path);
        else if (!NativeLibrary.TryLoad(name, out Handle)) throw new FileNotFoundException($"Clone Hero's BASS audio library ({name}) was not found");
        MelonLoader.MelonLogger.Msg($"Stage audio uses BASS from {path ?? name}");
        NativeLibrary.SetDllImportResolver(typeof(StageAudioBackend).Assembly, (library, _, _) => library == Library ? Handle : IntPtr.Zero);
    }

    [DllImport(Library, EntryPoint = "BASS_StreamCreateFile")]
    private static extern uint CreateFile([MarshalAs(UnmanagedType.Bool)] bool memory, IntPtr file, long offset, long length, uint flags);
    [DllImport(Library, EntryPoint = "BASS_StreamFree")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeStream(uint handle);
    [DllImport(Library, EntryPoint = "BASS_ChannelPlay")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlayChannel(uint handle, [MarshalAs(UnmanagedType.Bool)] bool restart);
    [DllImport(Library, EntryPoint = "BASS_ChannelPause")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PauseChannel(uint handle);
    [DllImport(Library, EntryPoint = "BASS_ChannelSetAttribute")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Attribute(uint handle, uint attribute, float value);
    [DllImport(Library, EntryPoint = "BASS_ChannelSetPosition")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Position(uint handle, long position, uint mode);
    [DllImport(Library, EntryPoint = "BASS_ChannelGetPosition")]
    private static extern long Position(uint handle, uint mode);
    [DllImport(Library, EntryPoint = "BASS_ChannelIsActive")]
    public static extern uint State(uint handle);
    [DllImport(Library, EntryPoint = "BASS_ErrorGetCode")]
    private static extern int Error();

    public static uint Create(IntPtr data, int length, bool loop)
    {
        var handle = CreateFile(true, data, 0, length, loop ? 4u : 0);
        if (handle == 0) throw new InvalidDataException($"Could not create stage audio stream (BASS {Error()})");
        return handle;
    }

    public static void Free(uint handle) => Check(FreeStream(handle));
    // At application quit Clone Hero may already have released BASS and every stream handle.
    public static void Release(uint handle) { if (!FreeStream(handle) && Error() != 5) Check(false); }
    public static void Play(uint handle) => Check(PlayChannel(handle, false));
    public static void Pause(uint handle) { if (State(handle) == 1) Check(PauseChannel(handle)); }
    public static void Volume(uint handle, float volume) => Check(Attribute(handle, 2, volume));
    public static void Pitch(uint handle, float frequency) => Check(Attribute(handle, 1, frequency));
    public static void Pan(uint handle, float pan) => Check(Attribute(handle, 3, pan));
    public static long Frame(uint handle, int channels) => Position(handle, 0) / (channels * 2);
    public static void Frame(uint handle, int channels, long frame) => Check(Position(handle, checked(frame * channels * 2), 0));
    private static void Check(bool success) { if (!success) throw new InvalidDataException($"Stage audio operation failed (BASS {Error()})"); }
}
