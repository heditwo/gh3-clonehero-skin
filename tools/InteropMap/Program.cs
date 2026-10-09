using Mono.Cecil;
using Mono.Cecil.Cil;

// Maps Il2CppInterop names to the obfuscated metadata names, field offsets, and method addresses of one Clone Hero install.
// It reads MelonLoader's interop assemblies and the Cpp2IL dummy assemblies (MelonLoader/Dependencies/.../cpp2il_out).
//   refs <Gh3Skin.dll> <game> [<game> ...]   every obfuscated member that the mod references, per install
//   type <game> <assembly> <interop type>     every field and method of one type
// <assembly> is CloneHero or StrikeCore.
if (args.Length < 3 || args[0] is not ("refs" or "type"))
{
    Console.Error.WriteLine("usage: refs <Gh3Skin.dll> <game>... | type <game> <CloneHero|StrikeCore> <type>");
    return 1;
}
var cache = new Dictionary<string, AssemblyDefinition>();
if (args[0] == "type")
{
    var (interop, dummy) = Install(args[1], args[2]);
    var type = AllTypes(interop.MainModule).First(t => t.FullName == args[3] || t.Name == args[3]);
    var native = NativeType(type, dummy);
    foreach (var field in type.Fields.Where(f => f.Name.StartsWith(FieldPrefix)))
        Console.WriteLine($"field  {field.Name[FieldPrefix.Length..]} => {DescribeField(type, native, field.Name)}");
    foreach (var field in type.Fields.Where(f => f.Name.StartsWith(MethodPrefix)))
        Console.WriteLine($"method {field.Name[MethodPrefix.Length..]} => {DescribeMethod(type, native, field.Name)}");
    return 0;
}
var mod = AssemblyDefinition.ReadAssembly(args[1]);
var references = new SortedSet<(string Assembly, string Type, string Member)>();
foreach (var type in AllTypes(mod.MainModule))
foreach (var method in type.Methods.Where(m => m.HasBody))
foreach (var instruction in method.Body.Instructions)
    if (instruction.Operand is MemberReference { DeclaringType: { } declaring } member && member.Name != ".ctor" &&
        declaring.Scope.Name is "Il2CppCloneHero" or "Il2CppStrikeCore" && Obfuscated(declaring.Name + "." + member.Name))
        references.Add((declaring.Scope.Name["Il2Cpp".Length..], declaring.GetElementType().FullName, member.Name));
foreach (var (assembly, typeName, member) in references)
{
    var columns = new List<string> { typeName.Replace("Il2Cpp.", ""), member };
    foreach (var game in args.Skip(2))
    {
        try
        {
            var (interop, dummy) = Install(game, assembly);
            var type = AllTypes(interop.MainModule).First(t => t.FullName == typeName);
            columns.Add(Describe(type, NativeType(type, dummy), member));
        }
        catch (Exception error) { columns.Add("error: " + error.Message); }
    }
    Console.WriteLine(string.Join(" | ", columns));
}
return 0;

string Describe(TypeDefinition type, TypeDefinition native, string member)
{
    var name = member.StartsWith("get_") || member.StartsWith("set_") ? member[4..] : member;
    for (var owner = type; owner != null; owner = owner.BaseType?.Resolve())
        if (owner.Fields.Any(f => f.Name == FieldPrefix + name))
            return DescribeField(owner, NativeType(owner, Dummy(native)), FieldPrefix + name);
    return DescribeMethod(type, native, MethodPrefix + member);
}

string DescribeField(TypeDefinition type, TypeDefinition native, string pointer)
{
    var name = CctorOperand(type, pointer, OpCodes.Ldstr) as string;
    var field = native.Fields.FirstOrDefault(f => f.Name == name);
    return field == null ? $"field {name}?" : $"field {name} #{native.Fields.IndexOf(field)} {field.FieldType.Name} @{Attribute(field, "FieldOffsetAttribute", "Offset")}";
}

string DescribeMethod(TypeDefinition type, TypeDefinition native, string pointer)
{
    var token = Convert.ToInt64(CctorOperand(type, pointer, OpCodes.Ldc_I8) ?? CctorOperand(type, pointer, OpCodes.Ldc_I4) ?? -1L);
    var method = native.Methods.FirstOrDefault(m => Attribute(m, "TokenAttribute", "Token") == $"0x{token:X}");
    return method == null ? $"method token 0x{token:X}?" :
        $"method {method.Name} {method.ReturnType.Name}({string.Join(",", method.Parameters.Select(p => p.ParameterType.Name))}) " +
        $"rva {Attribute(method, "AddressAttribute", "RVA")} length {Attribute(method, "AddressAttribute", "Length")}";
}

(AssemblyDefinition Interop, AssemblyDefinition Dummy) Install(string game, string assembly) =>
    (Load($"{game}/MelonLoader/Il2CppAssemblies/Il2Cpp{assembly}.dll"),
     Load($"{game}/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/{assembly}.dll"));

AssemblyDefinition Load(string path) => cache.TryGetValue(path, out var loaded) ? loaded : cache[path] = AssemblyDefinition.ReadAssembly(path);

AssemblyDefinition Dummy(TypeDefinition native) => native.Module.Assembly;

static TypeDefinition NativeType(TypeDefinition type, AssemblyDefinition dummy)
{
    var name = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "ObfuscatedNameAttribute")?.ConstructorArguments[0].Value as string ?? type.Name;
    return AllTypes(dummy.MainModule).Single(t => t.Name == name && (t.DeclaringType == null) == (type.DeclaringType == null));
}

static object? CctorOperand(TypeDefinition type, string pointer, OpCode opcode)
{
    var body = type.Methods.First(m => m.Name == ".cctor").Body.Instructions;
    for (var index = 0; index < body.Count; index++)
        if (body[index].OpCode == OpCodes.Stsfld && ((FieldReference)body[index].Operand).Name == pointer)
            for (var back = index; back >= 0; back--)
                if (body[back].OpCode == opcode) return body[back].Operand;
    return null;
}

static bool Obfuscated(string name) => name.Contains("field_") || name.Contains("prop_") || name.Contains("Method_") || name.Contains("Unique.");
static string Attribute(ICustomAttributeProvider provider, string attribute, string name) =>
    provider.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == attribute)?.Fields.FirstOrDefault(f => f.Name == name).Argument.Value?.ToString() ?? "?";
static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) => module.Types.SelectMany(Nested);
static IEnumerable<TypeDefinition> Nested(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Nested));

partial class Program
{
    private const string FieldPrefix = "NativeFieldInfoPtr_";
    private const string MethodPrefix = "NativeMethodInfoPtr_";
}
