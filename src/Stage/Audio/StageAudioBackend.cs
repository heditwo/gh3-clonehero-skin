using System.Runtime.InteropServices;

namespace Gh3Skin;

// Clone Hero's BASS library: finds it and wraps the calls that the stage audio uses.

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
    [DllImport(Library, EntryPoint = "BASS_ChannelGetInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Info(uint handle, out ChannelInfo info);
    [DllImport(Library, EntryPoint = "BASS_ChannelGetData")]
    private static extern int Data(uint handle, float[] buffer, uint length);

    // BASS_CHANNELINFO.
    [StructLayout(LayoutKind.Sequential)]
    private struct ChannelInfo
    {
        public uint Frequency, Channels, Flags, Type, OriginalResolution, Plugin, Sample;
        public IntPtr FileName;
    }

    // bass.h: BASS_SAMPLE_FLOAT, BASS_STREAM_DECODE, BASS_UNICODE, BASS_DATA_FLOAT.
    private const uint SampleFloat = 0x100;
    private const uint StreamDecode = 0x200000;
    private const uint Unicode = 0x80000000;
    private const uint DataFloat = 0x40000000;
    private const int DecodeBlockFrames = 8192;
    private const int MaximumChannels = 32;

    // Ensures that the static constructor (which reads Unity's data path) has run on the main thread.
    public static bool Available => Handle != IntPtr.Zero;

    // Decodes an audio file (any format that Clone Hero's BASS and its loaded plugins read: Ogg Vorbis, Opus, MP3, WAV)
    // to mono float samples. Safe on a worker thread: a decoding channel does not play.
    public static void DecodeMono(string path, Func<int, Action<float[], int>> sink)
    {
        var name = OperatingSystem.IsWindows() ? Marshal.StringToHGlobalUni(path) : Marshal.StringToCoTaskMemUTF8(path);
        uint handle;
        try { handle = CreateFile(false, name, 0, 0, StreamDecode | SampleFloat | (OperatingSystem.IsWindows() ? Unicode : 0)); }
        finally
        {
            if (OperatingSystem.IsWindows()) Marshal.FreeHGlobal(name);
            else Marshal.FreeCoTaskMem(name);
        }
        if (handle == 0) throw new InvalidDataException($"Could not decode {Path.GetFileName(path)} (BASS {Error()})");
        try
        {
            if (!Info(handle, out var info) || info.Channels is 0 or > MaximumChannels || info.Frequency == 0) throw new InvalidDataException($"Could not read {Path.GetFileName(path)} (BASS {Error()})");
            var channels = (int)info.Channels;
            var add = sink((int)info.Frequency);
            var buffer = new float[DecodeBlockFrames * channels];
            var mono = new float[DecodeBlockFrames];
            while (true)
            {
                var bytes = Data(handle, buffer, (uint)(buffer.Length * sizeof(float)) | DataFloat);
                if (bytes <= 0) break;
                var frames = bytes / sizeof(float) / channels;
                for (var frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    for (var channel = 0; channel < channels; channel++) sum += buffer[frame * channels + channel];
                    mono[frame] = sum / channels;
                }
                add(mono, frames);
            }
        }
        finally { FreeStream(handle); }
    }

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
