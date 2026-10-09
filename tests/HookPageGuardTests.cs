using Gh3Skin;

internal static class HookPageGuardTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    public static void Run()
    {
        CheckStub();
        CheckMemoryMap();
        CheckFreePage();
        CheckElf();
    }

    private static void CheckStub()
    {
        var stub = HookPageGuard.Stub(0x1122334455667788, 0x99aabbccddeeff00);
        Check(stub.Length == HookPageGuard.StubLength, "stub length");
        // cmp rsi, 0x1000 / cmp edx, 5 / flags & 0x30 == 0x30, then movabs rax and jmp rax twice
        Check(Convert.ToHexString(stub, 0, 0x18) == "4881FE00100000751B83FA05751689C883E03083F830750C", "stub test");
        Check(BitConverter.ToUInt64(stub, 0x1a) == 0x1122334455667788 && stub[0x18] == 0x48 && stub[0x19] == 0xb8, "stub handler");
        Check(stub[0x22] == 0xff && stub[0x23] == 0xe0, "stub handler jump");
        Check(BitConverter.ToUInt64(stub, 0x26) == 0x99aabbccddeeff00 && stub[0x2e] == 0xff && stub[0x2f] == 0xe0, "stub mmap jump");
    }

    private static void CheckMemoryMap()
    {
        var file = MemoryMap.Parse("7f2f5cb00000-7f2f5cce9000 r-xp 00010000 103:02 4521   /games/clone hero/System.Private.CoreLib.dll");
        Check(file.Start == 0x7f2f5cb00000 && file.End == 0x7f2f5cce9000 && file.Protection == 5 && file.Offset == 0x10000, "map fields");
        Check(file.Path == "/games/clone hero/System.Private.CoreLib.dll", "map path with spaces");
        var anonymous = MemoryMap.Parse("7f2f5bcc1000-7f2f5bcc2000 rw-p 00000000 00:00 0");
        Check(anonymous.Path == "" && anonymous.Protection == 3 && anonymous.Contains(0x7f2f5bcc1fff) && !anonymous.Contains(0x7f2f5bcc2000), "anonymous map");
    }

    private static void CheckFreePage()
    {
        var maps = new List<MemoryMap>
        {
            new(0x10000, 0x20000, 0, 0, ""),
            new(0x20000, 0x21000, 3, 0, ""),
            new(0x23000, 0x30000, 5, 0, "/lib/a.so"),
            new(0x30000, 0x40000, 1, 0, "/lib/b.so"),
        };
        Check(HookPageGuard.FreePage(maps, 0x10000, 0x100000) == 0x21000, "first gap after used pages");
        Check(HookPageGuard.FreePage(maps, 0x22000, 0x100000) == 0x22000, "request in a gap");
        Check(HookPageGuard.FreePage(maps, 0x23000, 0x100000) == 0x40000, "gap after the last map");
        Check(HookPageGuard.FreePage(maps, 0x23000, 0x40000) == 0, "no gap before the limit");
    }

    private static void CheckElf()
    {
        if (!OperatingSystem.IsLinux()) return;
        var library = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "libSystem.Native.so");
        if (!File.Exists(library)) return;
        var slots = Elf.JumpSlots(library, new[] { "mmap", "mmap64" });
        Check(slots.Count > 0 && slots.Values.All(offset => offset > 0), "libSystem.Native.so imports mmap");
        var table = Elf.Section(library, ".rela.dyn");
        Check(table.Address > 0 && table.Size > 0 && table.Size % 24 == 0, "libSystem.Native.so .rela.dyn");
        Check(Elf.Section(library, ".no-such-section") == (0, 0), "missing section");
    }
}
