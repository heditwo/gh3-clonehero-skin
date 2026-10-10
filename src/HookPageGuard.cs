using System.Globalization;
using System.Runtime.InteropServices;

namespace Gh3Skin;

// MelonLoader 0.7.3 hooks Il2Cpp functions with Dobby (LavaGang/Dobby 0651926). On Linux, Dobby puts the jump address of a
// hook (and the relocated code) on a page that it maps with MAP_FIXED at (hook target - 2 GB) or (target - 1 GB), and it does
// not check what is mapped there. In Clone Hero that address is often inside CoreCLR memory (ReadyToRun code of framework
// assemblies, loader heaps, JIT code) or another reservation. Dobby then erases live memory, or the owner later writes over
// the jump address, and the game crashes at random places. The guard sends the mmap calls of MelonLoader.Bootstrap.so through
// a stub. The stub gives only Dobby's request (one page, read and execute, MAP_FIXED, anonymous) to Place, which maps the page
// over the relocation table of GameAssembly.so (or another library that stays loaded), or else on a free page, in jump range
// of the target. Evidence: docs/STATUS.md#stability.
internal static class HookPageGuard
{
    private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4;
    private const int MapPrivate = 0x02, MapFixed = 0x10, MapAnonymous = 0x20, MapFixedNoReplace = 0x100000;
    private const long PageSize = 4096;
    private const ulong Window = 0x7fff0000;
    private static readonly string[] Symbols = { "mmap", "mmap64" };
    private static readonly IntPtr Failed = new(-1);
    // Native code calls this delegate for the life of the process, so a static field keeps it from collection.
    private static MapFunction? _place;
    private static readonly List<string> Placed = new();
    private static readonly Dictionary<string, (ulong Address, ulong Size)> RelocationTables = new();
    private static readonly HashSet<ulong> Requested = new();
    private static readonly HashSet<string> Pinned = new() { "GameAssembly.so", "UnityPlayer.so", "libcoreclr.so", "MelonLoader.Bootstrap.so" };

    private delegate IntPtr MapFunction(IntPtr address, UIntPtr length, int protection, int flags, int fd, IntPtr offset);

    // Returns a log line, or null when the guard does not apply (not Linux).
    public static string? Install()
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (SkinEnvironment.Get(SkinEnvironment.HookGuard) == "off") return "off (GH3_SKIN_HOOK_GUARD=off)";
        if (Environment.OSVersion.Version < new Version(4, 17)) return $"off (Linux {Environment.OSVersion.Version} has no MAP_FIXED_NOREPLACE)";
        var maps = MemoryMap.Read("/proc/self/maps");
        var bootstrap = maps.FirstOrDefault(entry => entry.Path.EndsWith("/MelonLoader.Bootstrap.so", StringComparison.Ordinal) && entry.Offset == 0);
        if (bootstrap == null) return "off (MelonLoader.Bootstrap.so is not loaded)";
        var slots = Elf.JumpSlots(bootstrap.Path, Symbols);
        if (slots.Count == 0) return "off (MelonLoader.Bootstrap.so does not import mmap)";
        var libc = NativeLibrary.Load("libc.so.6");
        _place = Place;
        var handler = Marshal.GetFunctionPointerForDelegate(_place);
        var stubs = mmap(IntPtr.Zero, (UIntPtr)PageSize, ProtRead | ProtWrite, MapPrivate | MapAnonymous, -1, IntPtr.Zero);
        if (stubs == Failed) return $"off (mmap failed: {Marshal.GetLastWin32Error()})";
        var patches = new List<(IntPtr Slot, IntPtr Stub)>();
        foreach (var (symbol, offset) in slots)
        {
            var slot = new IntPtr((long)bootstrap.Start + (long)offset);
            var target = Marshal.ReadIntPtr(slot);
            if (target != NativeLibrary.GetExport(libc, symbol)) continue;
            var stub = stubs + patches.Count * 64;
            Marshal.Copy(Stub((ulong)handler, (ulong)target), 0, stub, StubLength);
            patches.Add((slot, stub));
        }
        if (patches.Count == 0) return "off (the mmap imports of MelonLoader.Bootstrap.so are already redirected)";
        if (mprotect(stubs, (UIntPtr)PageSize, ProtRead | ProtExec) != 0) return $"off (mprotect failed: {Marshal.GetLastWin32Error()})";
        foreach (var (slot, stub) in patches)
        {
            var page = new IntPtr(slot.ToInt64() & ~(PageSize - 1));
            var protection = maps.FirstOrDefault(entry => entry.Contains((ulong)slot.ToInt64()))?.Protection ?? ProtRead;
            if (mprotect(page, (UIntPtr)PageSize, protection | ProtWrite) != 0) return $"off (mprotect failed: {Marshal.GetLastWin32Error()})";
            Marshal.WriteIntPtr(slot, stub);
            mprotect(page, (UIntPtr)PageSize, protection);
        }
        return $"on ({string.Join(", ", slots.Keys)} of MelonLoader.Bootstrap.so)";
    }

    // The hook pages that Place mapped so far, as "requested -> placed".
    public static string[] TakePlaced()
    {
        lock (Placed)
        {
            var result = Placed.ToArray();
            Placed.Clear();
            return result;
        }
    }

    private static IntPtr Place(IntPtr address, UIntPtr length, int protection, int flags, int fd, IntPtr offset)
    {
        try
        {
            var requested = (ulong)address.ToInt64();
            // Dobby asks for the first page above (target - range): 2 GB for jump addresses, 1 GB for relocated code. A page
            // in (request, request + 2 GB) is in range of the target for both. If Dobby asks for the same page again, the
            // page did not suit it, so it gets the original behavior.
            lock (Requested)
            {
                if (!Requested.Add(requested)) return mmap(address, length, protection, flags, fd, offset);
                var maps = MemoryMap.Read("/proc/self/maps");
                var from = requested + 0x10000;
                var limit = requested + Window;
                var spare = SparePage(maps, from, limit);
                var free = spare != 0 ? 0 : FreePage(maps, from, limit);
                var page = spare != 0 ? spare : free;
                if (page != 0)
                {
                    var result = mmap(new IntPtr((long)page), length, protection, spare != 0 ? flags : flags & ~MapFixed | MapFixedNoReplace, fd, offset);
                    if (result != Failed)
                    {
                        lock (Placed) Placed.Add($"{requested:x} -> {page:x}{(spare != 0 ? " (relocation table)" : "")}");
                        return result;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Never let an exception cross into native code.
        }
        // No better page: the original request.
        return mmap(address, length, protection, flags, fd, offset);
    }

    // A page of the dynamic relocation table (.rela.dyn) of a library in [from, limit) that the game never unloads. The
    // dynamic loader reads that table only while it loads the library, so the page is unused after that.
    private static ulong SparePage(IReadOnlyList<MemoryMap> maps, ulong from, ulong limit)
    {
        foreach (var image in maps.Where(entry => entry.Offset == 0 && Pinned.Contains(Path.GetFileName(entry.Path)) && entry.End > from && entry.Start < limit))
        {
            if (!RelocationTables.TryGetValue(image.Path, out var table)) RelocationTables[image.Path] = table = RelocationTable(image.Path);
            if (table.Size == 0) continue;
            var first = Math.Max(from, (image.Start + table.Address + (ulong)PageSize - 1) & ~((ulong)PageSize - 1));
            var last = Math.Min(limit, (image.Start + table.Address + table.Size) & ~((ulong)PageSize - 1));
            for (var page = first; page + (ulong)PageSize <= last; page += (ulong)PageSize)
            {
                var holder = maps.FirstOrDefault(entry => entry.Contains(page));
                // Still the original read-only file mapping: no earlier hook page took it.
                if (holder != null && holder.Path == image.Path && holder.Protection == ProtRead) return page;
            }
        }
        return 0;
    }

    private static (ulong Address, ulong Size) RelocationTable(string path)
    {
        try
        {
            return File.Exists(path) ? Elf.Section(path, ".rela.dyn") : (0, 0);
        }
        catch (Exception)
        {
            return (0, 0); // device files and unreadable files
        }
    }

    // The first page in [from, limit) that no mapping uses. Returns 0 when there is none.
    public static ulong FreePage(IReadOnlyList<MemoryMap> maps, ulong from, ulong limit)
    {
        var candidate = from;
        foreach (var entry in maps.OrderBy(entry => entry.Start))
        {
            if (entry.End <= candidate) continue;
            if (entry.Start >= candidate + (ulong)PageSize) break;
            candidate = entry.End;
        }
        return candidate + (ulong)PageSize <= limit ? candidate : 0;
    }

    public const int StubLength = 48;

    // x86-64: if (length == 4096 && prot == PROT_READ|PROT_EXEC && (flags & (MAP_FIXED|MAP_ANONYMOUS)) == both)
    //   jump to handler; else jump to mmap. The arguments stay in their registers.
    public static byte[] Stub(ulong handler, ulong mmap)
    {
        var code = Convert.FromHexString("4881FE00100000" + "751B" + "83FA05" + "7516" + "89C8" + "83E030" + "83F830" + "750C" +
                                         "48B8" + "0000000000000000" + "FFE0" + "48B8" + "0000000000000000" + "FFE0");
        BitConverter.GetBytes(handler).CopyTo(code, 0x1a);
        BitConverter.GetBytes(mmap).CopyTo(code, 0x26);
        return code;
    }

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr address, UIntPtr length, int protection, int flags, int fd, IntPtr offset);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int mprotect(IntPtr address, UIntPtr length, int protection);
}

internal sealed record MemoryMap(ulong Start, ulong End, int Protection, ulong Offset, string Path)
{
    public bool Contains(ulong address) => Start <= address && address < End;

    public static List<MemoryMap> Read(string path) => File.ReadLines(path).Select(Parse).ToList();

    public static MemoryMap Parse(string line)
    {
        var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        var range = fields[0].Split('-');
        var protection = (fields[1][0] == 'r' ? 1 : 0) | (fields[1][1] == 'w' ? 2 : 0) | (fields[1][2] == 'x' ? 4 : 0);
        return new MemoryMap(ulong.Parse(range[0], NumberStyles.HexNumber), ulong.Parse(range[1], NumberStyles.HexNumber), protection,
            ulong.Parse(fields[2], NumberStyles.HexNumber), fields.Length > 5 ? fields[5].Trim() : "");
    }
}

// Reads the section table of an ELF64 shared library.
internal static class Elf
{
    private const uint ShtRela = 4;
    private const uint JumpSlot = 7;

    private readonly record struct SectionHeader(uint Name, uint Type, ulong Address, long Offset, long Size, uint Link);

    // The GOT offsets (R_X86_64_JUMP_SLOT relocations) of imported functions.
    public static Dictionary<string, ulong> JumpSlots(string path, IReadOnlyCollection<string> symbols)
    {
        var result = new Dictionary<string, ulong>();
        using var file = File.OpenRead(path);
        var sections = Sections(file);
        foreach (var rela in sections.Where(section => section.Type == ShtRela && section.Link < sections.Count))
        {
            var symbolTable = sections[(int)rela.Link];
            var symbols64 = Read(file, symbolTable.Offset, symbolTable.Size);
            var strings = Read(file, sections[(int)symbolTable.Link].Offset, sections[(int)symbolTable.Link].Size);
            var entries = Read(file, rela.Offset, rela.Size);
            for (var entry = 0; entry + 24 <= entries.Length; entry += 24)
            {
                var info = BitConverter.ToUInt64(entries, entry + 8);
                if ((info & 0xffffffff) != JumpSlot) continue;
                var name = Text(strings, BitConverter.ToUInt32(symbols64, (int)(info >> 32) * 24));
                if (symbols.Contains(name)) result[name] = BitConverter.ToUInt64(entries, entry);
            }
        }
        return result;
    }

    // The address (relative to the load base) and size of a section, or (0, 0).
    public static (ulong Address, ulong Size) Section(string path, string name)
    {
        using var file = File.OpenRead(path);
        var sections = Sections(file);
        if (sections.Count == 0) return (0, 0);
        var header = Read(file, 0, 64);
        var names = sections[BitConverter.ToUInt16(header, 0x3e)];
        var text = Read(file, names.Offset, names.Size);
        foreach (var section in sections)
            if (Text(text, section.Name) == name) return (section.Address, (ulong)section.Size);
        return (0, 0);
    }

    private static List<SectionHeader> Sections(FileStream file)
    {
        var result = new List<SectionHeader>();
        var header = Read(file, 0, 64);
        if (header.Length < 64 || header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' || header[4] != 2) return result;
        var size = BitConverter.ToUInt16(header, 0x3a);
        var table = Read(file, (long)BitConverter.ToUInt64(header, 0x28), (long)size * BitConverter.ToUInt16(header, 0x3c));
        for (var at = 0; at + size <= table.Length; at += size)
            result.Add(new SectionHeader(BitConverter.ToUInt32(table, at), BitConverter.ToUInt32(table, at + 4), BitConverter.ToUInt64(table, at + 0x10),
                (long)BitConverter.ToUInt64(table, at + 0x18), (long)BitConverter.ToUInt64(table, at + 0x20), BitConverter.ToUInt32(table, at + 0x28)));
        return result;
    }

    private static byte[] Read(FileStream file, long offset, long count)
    {
        var buffer = new byte[Math.Max(0, Math.Min(count, file.Length - offset))];
        file.Position = offset;
        var done = 0;
        while (done < buffer.Length)
        {
            var read = file.Read(buffer, done, buffer.Length - done);
            if (read == 0) break;
            done += read;
        }
        return buffer;
    }

    private static string Text(byte[] data, long start)
    {
        if (start >= data.Length) return "";
        var end = Array.IndexOf(data, (byte)0, (int)start);
        return System.Text.Encoding.ASCII.GetString(data, (int)start, (end < 0 ? data.Length : end) - (int)start);
    }
}
