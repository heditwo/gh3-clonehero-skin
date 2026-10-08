using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Gh3Skin;

// GH3_BENCH=<venue> GH3_BENCH_SONG=<song folder> GH3_PACK_ASSETS=<assets> runs a venue's scripts and particles
// against a real chart at 144 Hz and reports CPU time and managed allocations per second of song time.
internal static class ScriptBench
{
    private sealed class Scene
    {
        public Dictionary<string, StageObjectPlacement> Objects { get; set; } = new();
    }

    public static void Run(string venue)
    {
        var root = Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") ?? "assets";
        var song = Environment.GetEnvironmentVariable("GH3_BENCH_SONG") ?? throw new InvalidOperationException("GH3_BENCH_SONG is not set");
        var start = double.Parse(Environment.GetEnvironmentVariable("GH3_BENCH_START") ?? "0");
        var end = double.Parse(Environment.GetEnvironmentVariable("GH3_BENCH_END") ?? "64");
        var directory = Path.Combine(root, "stages", venue);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var placements = JsonSerializer.Deserialize<Scene>(File.ReadAllText(Path.Combine(directory, "stage.json")), options)!.Objects;
        var actors = new Dictionary<uint, StageScriptActorDefinition>();
        if (File.Exists(Path.Combine(directory, "actors.json")))
            foreach (var actor in StageActorBank.Load(Path.Combine(directory, "actors.json")).Actors)
            {
                using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "actors", actor.Id, "animation.bin")));
                var animation = new BandAnimation(reader);
                if (actor.Object.Length == 0 || actor.Role.StartsWith("stage-diver/")) continue;
                var id = Gh3QbText.Key(actor.Object);
                actors.Add(id, new(actor, animation.Clips.Select(clip => (double)clip.Duration).ToArray(), animation,
                    animation.LoadBoneNames(Path.Combine(directory, "actors", actor.Id, "bones.json"))));
                placements.Add(id.ToString("x8"), new() { Position = actor.Transform.Position, Rotation = actor.Transform.Rotation,
                    Script = actor.Script, Created = actor.Created, Scale = actor.Scale });
            }
        if (File.Exists(Path.Combine(directory, "fixtures", "fixtures.json")))
        {
            using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "fixtures", "fixtures.json")));
            foreach (var row in fixtures.RootElement.GetProperty("housings").EnumerateArray())
            {
                var id = row.TryGetProperty("object", out var source) ? Gh3QbText.Key(source.GetString()!) : Convert.ToUInt32(row.GetProperty("id").GetString(), 16);
                placements.TryAdd(id.ToString("x8"), JsonSerializer.Deserialize<StageObjectPlacement>(row.GetRawText(), options)!);
            }
        }
        var library = Path.Combine(root, "libraries", "gh3-band-v1");
        var bank = StageScriptBank.Load(Path.Combine(directory, "scripts.json"), Path.Combine(library, "scripts.json"));
        var performance = StagePerformance.Load(Path.Combine(library, "direction", "performance.json"));
        var ini = File.ReadLines(Path.Combine(song, "song.ini")).Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].Trim().ToLowerInvariant(), pair => pair[1].Trim());
        var timeline = StageChartReader.Load(song, Path.Combine(song, "notes.chart"), Path.Combine(root, "stages", "events"),
            ini.GetValueOrDefault("name", ""), ini.GetValueOrDefault("artist", ""), performance);
        var lighting = new StageLighting(StageLightingBank.Load(directory)!, timeline);
        using var state = new StageScriptState(bank, placements, timeline, venue, (id, time) => null, actors, lighting);
        using var sampler = Environment.GetEnvironmentVariable("GH3_BENCH_ALLOC") == "1" ? new AllocationSampler() : null;
        var step = 1.0 / 144;
        for (var time = -1.0; time < start; time += step) state.Update(time, .5f);
        if (sampler != null) sampler.Active = true;
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var second = Math.Floor(start);
        var frames = 0;
        double busy = 0, peakParticles = 0;
        for (var time = start; time < end; time += step)
        {
            var before = Stopwatch.GetTimestamp();
            state.Update(time, .5f);
            foreach (var (id, _) in state.Objects) state.Sample(id, time);
            busy += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            peakParticles = Math.Max(peakParticles, state.Particles.Count);
            frames++;
            if (Math.Floor(time + step) == second) continue;
            var now = GC.GetTotalAllocatedBytes(true);
            Console.WriteLine($"{venue} {second:0}s: {busy / frames:0.00} ms/frame, {(now - allocated) / 1024.0 / frames:0.0} KB/frame, particles {peakParticles}");
            allocated = now;
            busy = 0;
            frames = 0;
            peakParticles = 0;
            second = Math.Floor(time + step);
        }
        Console.WriteLine($"{venue} total {clock.Elapsed.TotalSeconds:0.00}s");
        if (Environment.GetEnvironmentVariable("GH3_BENCH_PROBE") is { } probe)
        {
            var parts = probe.Split(',').Select(float.Parse).ToArray();
            foreach (var group in new[] { "None", "Band", "Crowd", "Stage" })
            {
                var set = state.LightScene.Sample(new(parts[0], parts[1], -parts[2]), Gh3Checksum.Value(group)).Lighting;
                Console.WriteLine($"  {group}: ambient {set.Ambient} key {set.Color} sh0 {set.Harmonics[0]}");
            }
        }
        if (sampler == null) return;
        sampler.Active = false;
        foreach (var (type, count) in sampler.Types.OrderByDescending(pair => pair.Value).Take(25)) Console.WriteLine($"  {count,6} {type}");
    }

    private sealed class AllocationSampler : EventListener
    {
        public readonly Dictionary<string, int> Types = new();
        public volatile bool Active;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (!Active || data.EventName != "GCAllocationTick_V4" || data.Payload == null) return;
            var name = data.Payload[data.PayloadNames!.IndexOf("TypeName")]?.ToString() ?? "?";
            lock (Types) Types[name] = Types.GetValueOrDefault(name) + 1;
        }
    }
}
