using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

var managed = Path.GetFullPath(args[0]);
var probePath = Path.GetFullPath(args[1]);
var originalPath = Path.Combine(managed, "Assembly-CSharp.dll");
var backup = originalPath + ".original";
if (File.Exists(backup)) throw new IOException("This copy has already been instrumented.");
if (Hash(originalPath) != "d303b7d8226d9fbedc3491d2aeac334f5b96081dd5456b2813bc54e68c6f3191")
    throw new InvalidDataException("Expected the original Assembly-CSharp.dll from the official 1.5.13 Linux Universal release.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.GetDirectoryName(probePath)!);
using var original = AssemblyDefinition.ReadAssembly(originalPath, new ReaderParameters { AssemblyResolver = resolver });
using var probe = AssemblyDefinition.ReadAssembly(probePath, new ReaderParameters { AssemblyResolver = resolver });
var update = original.MainModule.Types.Single(t => t.Name == "ChartEditor").Methods.Single(m => m.Name == "Update");
var run = probe.MainModule.Types.Single(t => t.Name == "MoonscraperPreservationProbe").Methods.Single(m => m.Name == "Run");
var processor = update.Body.GetILProcessor();
processor.InsertBefore(update.Body.Instructions[0], processor.Create(OpCodes.Call, original.MainModule.ImportReference(run)));
var instrumentedPath = originalPath + ".instrumented";
original.Write(instrumentedPath);

using var before = AssemblyDefinition.ReadAssembly(originalPath, new ReaderParameters { AssemblyResolver = resolver });
using var after = AssemblyDefinition.ReadAssembly(instrumentedPath, new ReaderParameters { AssemblyResolver = resolver });
var beforeMethods = Methods(before.MainModule.Types).ToDictionary(m => m.FullName, Body);
var afterMethods = Methods(after.MainModule.Types).ToDictionary(m => m.FullName, Body);
var changed = beforeMethods.Keys.Union(afterMethods.Keys).Where(k => beforeMethods.GetValueOrDefault(k) != afterMethods.GetValueOrDefault(k)).ToArray();
if (!changed.SequenceEqual(new[] { update.FullName })) throw new InvalidOperationException("Unexpected method changes: " + string.Join(", ", changed));
var report = new
{
    sourceSha256 = Hash(originalPath), instrumentedSha256 = Hash(instrumentedPath),
    probeSha256 = Hash(probePath), changedMethods = changed, unchangedMethodCount = beforeMethods.Count - changed.Length
};
File.Move(originalPath, backup);
File.Move(instrumentedPath, originalPath);
File.Copy(probePath, Path.Combine(managed, Path.GetFileName(probePath)));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

static IEnumerable<MethodDefinition> Methods(IEnumerable<TypeDefinition> types) => types.SelectMany(t => t.Methods.Concat(Methods(t.NestedTypes)));
static string Body(MethodDefinition method) => method.HasBody ? string.Join("\n", method.Body.Instructions.Select(i => i.ToString())) : "";
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
