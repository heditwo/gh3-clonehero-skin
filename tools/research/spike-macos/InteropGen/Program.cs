// Runs the same Il2CppInterop generation step as MelonLoader 0.7.3 (Packages/Il2CppInterop.cs) outside the game,
// so interop names of a build can be compared without running that build.
using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using Microsoft.Extensions.Logging;

var (cpp2il, unityLibs, output) = (args[0], args[1], args[2]);
var resolver = new Resolver();
var inputs = Directory.GetFiles(cpp2il, "*.dll")
    .Select(f => ModuleDefinition.FromFile(f, new ModuleReaderParameters { ModuleResolver = resolver }))
    .Select(m => { resolver.Add(m); return m.Assembly; })
    .ToList();
var options = new GeneratorOptions
{
    GameAssemblyPath = args.Length > 3 ? args[3] : null,
    Source = inputs,
    OutputDir = output,
    UnityBaseLibsDir = unityLibs,
    Parallel = true,
    Il2CppPrefixMode = GeneratorOptions.PrefixMode.OptOut,
};
Il2CppInteropGenerator.Create(options).AddLogger(new ConsoleLogger()).AddInteropAssemblyGenerator().Run();

class Resolver : INetModuleResolver
{
    private readonly Dictionary<string, ModuleDefinition> _cache = new();
    public void Add(ModuleDefinition module) => _cache[module.Name] = module;
    public ModuleDefinition Resolve(string name) => _cache.GetValueOrDefault(name);
    public void Dispose() => _cache.Clear();
}

class ConsoleLogger : ILogger
{
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> format)
    {
        if (level >= LogLevel.Information) Console.WriteLine(format(state, error));
    }
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
}
