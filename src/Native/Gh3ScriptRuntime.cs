using System.Globalization;
using System.Text.RegularExpressions;

namespace Gh3Skin;

internal sealed record Gh3ScriptWait(double Seconds, uint[]? Events = null, uint? Target = null, Gh3ScriptContext? Context = null, Gh3ComponentWait? ComponentWait = null);
internal sealed record Gh3ScriptCallResult(bool Success, IReadOnlyList<Gh3QbField>? Values = null, double Wait = 0, uint[]? Events = null, Func<bool>? Waiting = null, Gh3ComponentWait? ComponentWait = null);

internal sealed class Gh3ScriptContext
{
    public uint Script { get; }
    public uint Object { get; }
    public Dictionary<uint, Gh3QbValue> Locals { get; } = new();
    public List<Gh3QbField> Parameters { get; }
    public Gh3ScriptCallResult Result { get; set; } = new(true);
    public Gh3QbValue Arguments => Gh3ScriptRuntime.Structure(Parameters.Where(parameter => parameter.Name == 0)
        .Concat(Locals.Select(pair => new Gh3QbField(pair.Key, pair.Value))));

    public Gh3ScriptContext(uint script, uint target, IReadOnlyList<Gh3QbField> parameters)
    {
        Script = script;
        Object = target;
        Parameters = parameters.ToList();
        foreach (var field in parameters.Where(field => field.Name != 0)) Locals[field.Name] = field.Value;
    }
}

// The QB script interpreter: script tasks on the song clock, waits, events, spawned scripts, and the builtin commands
// that GH3 implements in the engine (PARITY-NOTES.md#script-runtime).
internal sealed class Gh3ScriptRuntime : IDisposable
{
    // Interpreter limits against runaway or corrupt scripts; GH3's venue scripts stay far below them.
    private const int MaximumTasks = 10000;
    private const int MaximumCallDepth = 64;
    private const int MaximumExpressionDepth = 64;
    private const int MaximumStepsPerAdvance = 1000000;
    private const int MaximumArguments = 10000;
    private const int MaximumArrayItems = 100000;
    private const double MaximumTime = 86400;
    // GH3 keeps the random-choice history of RandomNoRepeat and RandomPermute in a reusable pool of 100 entries.
    private const int RandomHistoryPool = 100;
    private const int RandomNoRepeatAttempts = 100;
    // Swaps per item when RandomPermute shuffles its order.
    private const int ShuffleSwapsPerItem = 10;
    // Default resolution of GetRandomValue without "integer".
    private const int RandomValueResolution = 1000;
    // Tolerance of a component task's resume time against the clock, for rounding of the frame times.
    private const double ResumeTolerance = .000000001;
    // Seed of the script random generators. Source not documented (commit 44b355a); GH3's boot seed is Gh3Random.BootSeed.
    private const int ScriptRandomSeed = 0x6e624eb7;
    // Expression binding strength. Member and Unary bind the operand of '.', '[', ':', '$', '-' and 'not'.
    private const int OrPrecedence = 10;
    private const int AndPrecedence = 20;
    private const int ComparisonPrecedence = 30;
    private const int AdditivePrecedence = 40;
    private const int MultiplicativePrecedence = 50;
    private const int UnaryPrecedence = 80;
    private const int MemberPrecedence = 100;
    // Beats per minute for "Wait n beats" before the song sets its tempo.
    private const double DefaultTempo = 120;

    private sealed class Task
    {
        public long Sequence { get; init; }
        public uint Script { get; init; }
        public uint Object { get; init; }
        public uint Id { get; init; }
        public bool Callback { get; init; }
        public bool Component { get; init; }
        public double ResumeAt { get; set; }
        public Gh3ComponentWait? Wait { get; set; }
        public IEnumerator<Gh3ScriptWait> Steps { get; init; } = null!;
        public bool Cancelled { get; set; }
    }
    private readonly Dictionary<uint, Gh3ScriptCode> _scripts = new();
    private readonly Dictionary<uint, Gh3QbValue> _globals;
    private readonly Dictionary<uint, string> _names;
    private readonly Func<Gh3ScriptContext, uint, Gh3QbValue, Gh3ScriptCallResult?> _native;
    private readonly Dictionary<long, Task> _tasks = new();
    private readonly PriorityQueue<Task, (double Time, long Order)> _queue = new();
    private sealed class RandomHistory
    {
        public uint Script { get; init; }
        public int Offset { get; init; }
        public int Count { get; init; }
        public byte Op { get; init; }
        public Task? Owner { get; set; }
        public int Last { get; set; }
        public int[] Order { get; set; } = Array.Empty<int>();
        public int Index { get; set; }
    }
    private readonly List<RandomHistory> _randomHistory = new();
    private sealed record Listener(uint Target, uint Event, uint Script, uint Group, uint Response, Gh3QbValue Parameters);
    private readonly List<Listener> _listeners = new();
    private readonly Dictionary<long, (Task Task, Gh3ScriptWait Wait)> _blocked = new();
    private Task? _running;
    private long _sequence;
    private long _order;
    private readonly Gh3Random _random = new(ScriptRandomSeed);
    private readonly Gh3Random _random2 = new(ScriptRandomSeed);
    private readonly Gh3CrtRandom _crtRandom = new();
    private int _work;
    private int _depth;
    public double Time { get; private set; }
    public double FrameDuration { get; set; } = 1.0 / 60;
    public double? UnscaledFrameDuration { get; set; }
    private double _onePerFrameTime = double.NegativeInfinity;
    private uint Milliseconds => unchecked((uint)(long)Math.Truncate(Time * Gh3Time.MillisecondsPerSecond));
    private bool AdvanceComponentWait(Gh3ComponentWait wait) => wait.Advance((float)FrameDuration, (float)(UnscaledFrameDuration ?? FrameDuration), Milliseconds, TakeComponentFrame);
    private bool TakeComponentFrame()
    {
        if (_onePerFrameTime == Time) return false;
        _onePerFrameTime = Time;
        return true;
    }
    public int NextRandom(int limit) => _random.Next(limit);
    public int NextCrtRandom() => _crtRandom.Next();
    public Func<double, double> Tempo { get; set; } = _ => DefaultTempo;
    public Action<uint, int, byte>? Trace { get; set; }
    public Action<uint, Exception>? Failed { get; set; }
    public Action<uint, uint?, Gh3QbValue>? EventRaised { get; set; }
    public IReadOnlyDictionary<uint, Gh3QbValue> Globals => _globals;
    public int Pending => _tasks.Count;

    public Gh3ScriptRuntime(IReadOnlyDictionary<uint, Gh3QbValue> globals,
        Func<Gh3ScriptContext, uint, Gh3QbValue, Gh3ScriptCallResult?> native, IReadOnlyDictionary<uint, string>? names = null, double time = 0)
    {
        _globals = new(globals);
        _names = names == null ? new() : new(names);
        _native = native;
        Time = time;
    }

    public void Set(uint key, Gh3QbValue value) => _globals[key] = value;
    public bool HasScript(uint key) => _globals.TryGetValue(key, out var value) && value.Type == Gh3QbType.Script;
    public string Name(uint key) => _names.GetValueOrDefault(key, "0x" + key.ToString("x8"));
    public uint Intern(string name) { var key = Gh3Checksum.Value(name); _names.TryAdd(key, name); return key; }
    public static Gh3QbValue Number(double number) => double.IsFinite(number) && number >= int.MinValue && number <= int.MaxValue && number == Math.Truncate(number) ?
        new(Gh3QbType.Integer, -1, unchecked((uint)(int)number)) : float.IsFinite((float)number) ? new(Gh3QbType.Float, -1, (float)number) : throw new InvalidDataException("Invalid script arithmetic result");
    public static Gh3QbValue Key(string text) => new(Gh3QbType.Checksum, -1, Gh3Checksum.Value(text));
    private static Gh3QbValue Missing => new(Gh3QbType.None, -1, 0u);
    public static Gh3QbValue Structure(IEnumerable<Gh3QbField> fields) => new(Gh3QbType.Structure, -1, fields.ToArray());
    private static Gh3QbValue EvaluatedStructure(IEnumerable<Gh3QbField> fields)
    {
        var result = new List<Gh3QbField>();
        foreach (var field in fields)
        {
            if (field.Value.Type == Gh3QbType.None) continue;
            if (field.Name != 0) result.RemoveAll(previous => previous.Name == field.Name);
            result.Add(field);
        }
        return Structure(result);
    }
    public static double Numeric(Gh3QbValue value) => value.Type == Gh3QbType.Integer ? unchecked((int)value.Integer) : value.Type == Gh3QbType.Float ? value.Number : throw new InvalidDataException("Expected script number");
    public static bool Truth(Gh3QbValue value) => value.Type == Gh3QbType.None ? false : value.Type is Gh3QbType.Integer or Gh3QbType.Float ? Numeric(value) != 0 : value.Type is Gh3QbType.Checksum or Gh3QbType.GlobalReference ?
        value.Integer != Gh3Checksum.Value("false") && value.Integer != Gh3Checksum.Value("none") && value.Integer != Gh3Checksum.Value("undefined") : true;

    public long Start(uint script, Gh3QbValue? parameters = null, uint target = 0, uint id = 0, bool immediate = true, bool optional = false)
    {
        var context = new Gh3ScriptContext(script, target, parameters?.Fields ?? Array.Empty<Gh3QbField>());
        var task = new Task { Sequence = _sequence++, Script = script, Object = target, Id = id, Steps = Invoke(context, script, Structure(context.Parameters), optional: optional).GetEnumerator() };
        if (_tasks.Count >= MaximumTasks) throw new InvalidDataException("Too many active GH3 scripts");
        _tasks.Add(task.Sequence, task);
        if (immediate) Resume(task);
        else _queue.Enqueue(task, (Time + FrameDuration, _order++));
        return task.Sequence;
    }

    public void Schedule(double time, Action callback, uint target = 0, uint id = 0)
    {
        if (!double.IsFinite(time) || time < Time) throw new ArgumentOutOfRangeException(nameof(time));
        if (_tasks.Count >= MaximumTasks) throw new InvalidDataException("Too many active GH3 scripts");
        IEnumerable<Gh3ScriptWait> Run() { callback(); yield break; }
        var task = new Task { Sequence = _sequence++, Object = target, Id = id, Callback = true, Steps = Run().GetEnumerator() };
        _tasks.Add(task.Sequence, task);
        _queue.Enqueue(task, (time, _order++));
    }

    public long CreateComponent(uint script, Gh3QbValue? parameters, uint target)
    {
        if (_tasks.Count >= MaximumTasks) throw new InvalidDataException("Too many active GH3 scripts");
        var context = new Gh3ScriptContext(script, target, parameters?.Fields ?? Array.Empty<Gh3QbField>());
        var task = new Task { Sequence = _sequence++, Script = script, Object = target, Component = true, ResumeAt = Time,
            Steps = Invoke(context, script, Structure(context.Parameters)).GetEnumerator() };
        _tasks.Add(task.Sequence, task);
        return task.Sequence;
    }

    public void UpdateComponent(long sequence)
    {
        if (!_tasks.TryGetValue(sequence, out var task) || !task.Component || _blocked.ContainsKey(sequence)) return;
        if (task.Wait is { } wait)
        {
            if (!AdvanceComponentWait(wait)) return;
            task.Wait = null;
        }
        else if (task.ResumeAt > Time + ResumeTolerance) return;
        Resume(task);
    }

    public void CancelComponent(long sequence)
    {
        if (_tasks.TryGetValue(sequence, out var task) && task.Component) Cancel(task);
    }

    public void Cancel(uint script = 0, uint id = 0, uint? target = null)
    {
        foreach (var task in _tasks.Values.Where(task => !task.Callback && !task.Component && (script == 0 || task.Script == script) && (id == 0 || task.Id == id) && (target == null || task.Object == target)).ToArray())
            Cancel(task);
    }

    public void CancelScheduled(uint id = 0, uint? target = null)
    {
        foreach (var task in _tasks.Values.Where(task => task.Callback && (id == 0 || task.Id == id) && (target == null || task.Object == target)).ToArray()) Cancel(task);
    }

    private void Cancel(Task task)
    {
        task.Cancelled = true;
        _tasks.Remove(task.Sequence);
        ReleaseRandomHistory(task);
        if ((_blocked.Remove(task.Sequence) || task.Component) && task != _running) task.Steps.Dispose();
    }

    public void RemoveObject(uint target)
    {
        Cancel(target: target);
        foreach (var task in _tasks.Values.Where(task => task.Component && task.Object == target).ToArray()) Cancel(task);
        CancelScheduled(target: target);
        _listeners.RemoveAll(listener => listener.Target == target);
    }

    public void Advance(double time)
    {
        if (!double.IsFinite(time) || time < Time || time > MaximumTime) throw new ArgumentOutOfRangeException(nameof(time));
        _work = 0;
        while (_queue.TryPeek(out _, out var next) && next.Time <= time)
        {
            Time = next.Time;
            Resume(_queue.Dequeue());
        }
        Time = time;
    }

    private void Resume(Task task)
    {
        if (task.Cancelled) { task.Steps.Dispose(); return; }
        if (++_depth > MaximumCallDepth) throw new InvalidDataException("GH3 script call depth exceeds its limit");
        var previous = _running;
        _running = task;
        try
        {
            if (task.Steps.MoveNext() && !task.Cancelled)
            {
                if (task.Steps.Current.Events != null)
                {
                    _blocked.Add(task.Sequence, (task, task.Steps.Current));
                    return;
                }
                var seconds = task.Steps.Current.Seconds;
                if (task.Steps.Current.ComponentWait is { } wait) { task.Wait = wait; return; }
                if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Invalid GH3 script wait");
                task.ResumeAt = Time + Math.Max(FrameDuration, seconds);
                if (!task.Component) _queue.Enqueue(task, (task.ResumeAt, _order++));
            }
            else { _tasks.Remove(task.Sequence); ReleaseRandomHistory(task); task.Steps.Dispose(); }
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            task.Cancelled = true;
            _tasks.Remove(task.Sequence);
            ReleaseRandomHistory(task);
            task.Steps.Dispose();
            var failure = new InvalidDataException($"Script {Name(task.Script)}: {error.Message}", error);
            if (Failed == null) throw failure;
            Failed(task.Script, failure);
        }
        finally { _depth--; _running = previous; }
    }

    private RandomHistory RandomChoiceHistory(uint script, Gh3ScriptToken token, out bool created)
    {
        var history = _randomHistory.FirstOrDefault(item => item.Script == script && item.Offset == token.Offset && item.Op == token.Op &&
            item.Count == token.Targets!.Length && (item.Owner == null || item.Owner == _running));
        created = history == null;
        if (history == null)
        {
            history = new() { Script = script, Offset = token.Offset, Count = token.Targets!.Length, Op = token.Op };
            if (_randomHistory.Count == RandomHistoryPool) _randomHistory.RemoveAt(RandomHistoryPool - 1);
            _randomHistory.Insert(0, history);
        }
        history.Owner = _running;
        return history;
    }

    private void ReleaseRandomHistory(Task task)
    {
        foreach (var history in _randomHistory)
            if (history.Owner == task) history.Owner = null;
    }

    private Gh3ScriptCode Code(uint key)
    {
        if (_scripts.TryGetValue(key, out var code)) return code;
        if (!_globals.TryGetValue(key, out var value) || value.Type != Gh3QbType.Script) throw new InvalidDataException("Missing script " + Name(key));
        _scripts.Add(key, code = new(value));
        return code;
    }

    private IEnumerable<Gh3ScriptWait> Invoke(Gh3ScriptContext caller, uint key, Gh3QbValue parameters, uint? target = null, int depth = 0, bool optional = false)
    {
        if (depth > MaximumCallDepth) throw new InvalidDataException("GH3 script call depth exceeds its limit");
        var context = new Gh3ScriptContext(key, target ?? caller.Object, parameters.Fields);
        var result = Builtin(context, caller, key, parameters) ?? _native(context, key, parameters) ?? Gh3PcCommands.Execute(key);
        if (result == null && optional && !HasScript(key)) result = new(false);
        if (result != null)
        {
            context.Result = result;
            if (result.Events != null) yield return new(0, result.Events, context.Object == 0 ? null : context.Object, caller);
            if (result.Wait > 0) yield return new(result.Wait);
            if (result.ComponentWait is { } wait && !AdvanceComponentWait(wait)) yield return new(0, ComponentWait: wait);
            while (result.Waiting?.Invoke() == true) yield return new(FrameDuration);
        }
        else
        {
            var code = Code(key);
            var cursor = new Cursor(this, context, code, depth);
            foreach (var wait in cursor.Run()) yield return wait;
        }
        caller.Result = context.Result;
        if (context.Result.Values != null)
            foreach (var field in context.Result.Values.Where(field => field.Name != 0)) caller.Locals[field.Name] = field.Value;
    }

    private Gh3QbValue Resolve(Gh3QbValue value, int depth = 0)
    {
        if (depth > MaximumCallDepth) throw new InvalidDataException("Recursive script value reference");
        if (value.Type == Gh3QbType.GlobalReference) return _globals.TryGetValue(value.Integer, out var global) ? Resolve(global, depth + 1) : Missing;
        if (value.Type == Gh3QbType.Structure)
        {
            var fields = new List<Gh3QbField>();
            foreach (var field in value.Fields)
            {
                var item = Resolve(field.Value, depth + 1);
                if (field.Name == 0 && item.Type == Gh3QbType.Structure) fields.AddRange(item.Fields);
                else fields.Add(new(field.Name, item));
            }
            return EvaluatedStructure(fields);
        }
        return value;
    }

    private Gh3ScriptCallResult? Builtin(Gh3ScriptContext context, Gh3ScriptContext caller, uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        Gh3QbValue? Field(string name) => parameters.Field(name);
        var unnamed = parameters.Fields.Where(field => field.Name == 0).Select(field => field.Value).ToArray();
        Gh3ScriptCallResult Result(string name, Gh3QbValue value) => new(true, new[] { new Gh3QbField(Intern(name), value) });
        if (Is("Wait"))
        {
            if (_running?.Component == true) return new(true, ComponentWait: new(parameters, Milliseconds));
            var amount = unnamed.FirstOrDefault(value => value.Type is Gh3QbType.Integer or Gh3QbType.Float) ?? Number(1);
            var seconds = Numeric(amount);
            if (parameters.Flag("Seconds") || parameters.Flag("Second")) { }
            else if (parameters.Flag("Milliseconds") || parameters.Flag("Millisecond")) seconds /= Gh3Time.MillisecondsPerSecond;
            else if (parameters.Flag("Beats") || parameters.Flag("Beat")) seconds *= 60 / Tempo(Time);
            else seconds *= FrameDuration;
            if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Invalid script wait duration");
            return new(true, Wait: Math.Max(FrameDuration, seconds));
        }
        if (Is("Block"))
        {
            var events = Field("anytypes")?.Items.Select(value => value.Integer).ToArray() ?? unnamed.Where(value => value.Type == Gh3QbType.Checksum).Select(value => value.Integer).ToArray();
            if (events.Length == 0) throw new InvalidDataException("Block requires event types");
            return new(true, Events: events);
        }
        if (Is("SpawnScriptNow") || Is("SpawnScriptLater") || Is("Obj_SpawnScriptNow") || Is("Obj_SpawnScriptLater"))
        {
            var name = Field("name") ?? unnamed.FirstOrDefault(value => value.Type == Gh3QbType.Checksum) ?? throw new InvalidDataException("SpawnScript needs a script reference");
            Start(name.Integer, Field("params"), Is("Obj_SpawnScriptNow") || Is("Obj_SpawnScriptLater") ? context.Object : 0,
                Field("id")?.Integer ?? 0, Is("SpawnScriptNow") || Is("Obj_SpawnScriptNow"));
            return new(true);
        }
        if (Is("KillSpawnedScript") || Is("Obj_KillSpawnedScript"))
        {
            Cancel(Field("name")?.Integer ?? 0, Field("id")?.Integer ?? 0, Is("Obj_KillSpawnedScript") ? context.Object : null);
            return new(true);
        }
        if (Is("Obj_SwitchScript") || Is("SwitchScript"))
        {
            var name = Field("name") ?? unnamed.FirstOrDefault(value => value.Type == Gh3QbType.Checksum) ?? throw new InvalidDataException("SwitchScript needs a script reference");
            if (Is("Obj_SwitchScript")) Cancel(target: context.Object);
            else if (_running != null) Cancel(_running);
            Start(name.Integer, Field("params"), context.Object, immediate: false);
            return new(true);
        }
        if (Is("MangleChecksums"))
        {
            var a = Field("a")?.Integer ?? throw new InvalidDataException("Checksum mangling needs a");
            var b = Field("b")?.Integer ?? throw new InvalidDataException("Checksum mangling needs b");
            var value = Field("subtract") is { } subtract && Truth(subtract) ? unchecked(a - b) : unchecked(a + b);
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("mangled_id"), new(Gh3QbType.Checksum, -1, value)) });
        }
        if (Is("GetDistance"))
        {
            if (Field("posA")?.Data is not float[] { Length: 3 } a || Field("posB")?.Data is not float[] { Length: 3 } b || a.Concat(b).Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("GetDistance needs two finite positions");
            var distance = Math.Sqrt(a.Zip(b, (x, y) => (double)(x - y) * (x - y)).Sum());
            return Result("dist_AToB", Number(distance));
        }
        if (Is("SetEventHandler"))
        {
            var type = Field("event") ?? throw new InvalidDataException("SetEventHandler needs an event");
            _listeners.Add(new(context.Object, type.Integer, Field("Scr")?.Integer ?? 0, Field("group")?.Integer ?? 0,
                Field("response")?.Integer ?? Gh3Checksum.Value("call_script"), Field("params") ?? Structure(Array.Empty<Gh3QbField>())));
            return new(true);
        }
        if (Is("ClearEventHandlerGroup"))
        {
            var group = Field("group") ?? unnamed.FirstOrDefault();
            _listeners.RemoveAll(listener => listener.Target == context.Object && (group == null || listener.Group == group.Integer));
            return new(true);
        }
        if (Is("ResetEventHandlersFromTable"))
        {
            var reference = unnamed.FirstOrDefault() ?? throw new InvalidDataException("Event table reference is missing");
            var table = reference.Type == Gh3QbType.Array ? reference : _globals.GetValueOrDefault(reference.Integer) ?? throw new InvalidDataException("Event table is missing");
            var group = Field("group")?.Integer ?? 0;
            _listeners.RemoveAll(listener => listener.Target == context.Object && listener.Group == group);
            foreach (var row in table.Items)
                Builtin(context, caller, Gh3Checksum.Value("SetEventHandler"), Structure(row.Fields.Concat(new[] { new Gh3QbField(Gh3Checksum.Value("group"), new(Gh3QbType.Checksum, -1, group)) })));
            return new(true);
        }
        if (Is("LaunchEvent") || Is("BroadcastEvent"))
        {
            var type = Field("Type") ?? throw new InvalidDataException("An event needs a type");
            var target = Field("target")?.Integer;
            EventRaised?.Invoke(type.Integer, target, Field("Data") ?? Structure(Array.Empty<Gh3QbField>()));
            foreach (var (sequence, blocked) in _blocked.Where(pair => pair.Value.Wait.Events!.Contains(type.Integer) &&
                (target == null || pair.Value.Wait.Target == null || pair.Value.Wait.Target == target)).ToArray())
            {
                _blocked.Remove(sequence);
                if (blocked.Wait.Context is { } waiting)
                    foreach (var field in Field("Data")?.Fields ?? Array.Empty<Gh3QbField>())
                        if (field.Name != 0) waiting.Locals[field.Name] = field.Value;
                blocked.Task.ResumeAt = Time;
                if (!blocked.Task.Component) _queue.Enqueue(blocked.Task, (Time, _order++));
            }
            foreach (var listener in _listeners.Where(listener => listener.Event == type.Integer && (target == null || listener.Target == target)).ToArray())
            {
                if (listener.Response == Gh3Checksum.Value("ignore")) continue;
                if (listener.Response != Gh3Checksum.Value("call_script")) throw new InvalidDataException("Unsupported event response " + Name(listener.Response));
                var fields = listener.Parameters.Fields.Concat(Field("Data")?.Fields ?? Array.Empty<Gh3QbField>());
                Start(listener.Script, Structure(fields), listener.Target);
            }
            return new(true);
        }
        if (Is("AddParams"))
        {
            foreach (var field in parameters.Fields.Where(field => field.Name != 0)) caller.Locals[field.Name] = field.Value;
            foreach (var value in unnamed.Where(value => value.Type == Gh3QbType.Structure))
                foreach (var field in value.Fields.Where(field => field.Name != 0)) caller.Locals[field.Name] = field.Value;
            return new(true);
        }
        if (Is("GotParam")) return new(unnamed.Any(value => value.Type == Gh3QbType.Checksum && (caller.Locals.ContainsKey(value.Integer) || caller.Parameters.Any(field => field.Name == 0 && field.Value.Type == Gh3QbType.Checksum && field.Value.Integer == value.Integer))));
        if (Is("RequireParams"))
        {
            var required = unnamed.SelectMany(value => value.Type == Gh3QbType.Array ? value.Items : value.Type == Gh3QbType.Checksum ? new[] { value } : Array.Empty<Gh3QbValue>())
                .Where(value => value.Type == Gh3QbType.Checksum && value.Integer != Gh3Checksum.Value("all") && value.Integer != Gh3Checksum.Value("any")).ToArray();
            var present = required.Select(value => caller.Locals.ContainsKey(value.Integer) || caller.Parameters.Any(item => item.Name == 0 && item.Value.Type == Gh3QbType.Checksum && item.Value.Integer == value.Integer)).ToArray();
            if (parameters.Flag("any") ? !present.Any(value => value) : present.Any(value => !value))
                throw new InvalidDataException("A required script parameter is missing");
            return new(true);
        }
        if (Is("SetSpawnInstanceLimits"))
        {
            var maximum = (int)Numeric(Field("Max") ?? Number(1));
            if (maximum < 1) throw new InvalidDataException("Invalid script instance limit");
            var tasks = _tasks.Values.Where(task => task.Script == caller.Script && task.Object == caller.Object).OrderBy(task => task.Sequence).ToArray();
            var mode = Field("management")?.Integer ?? Gh3Checksum.Value("kill_oldest");
            var remove = mode == Gh3Checksum.Value("kill_oldest") ? tasks.Take(Math.Max(0, tasks.Length - maximum)) : tasks.Skip(maximum);
            foreach (var task in remove.ToArray()) Cancel(task);
            return new(true);
        }
        if (Is("CastToInteger")) return new(true, unnamed.Select(value => new Gh3QbField(value.Integer,
            Number(Math.Truncate(Numeric(caller.Locals.GetValueOrDefault(value.Integer, Number(0))))))).ToArray());
        if (Is("GlobalExists")) return new(Field("Name") is { } name && _globals.ContainsKey(name.Integer));
        if (Is("ScriptExists")) return new((Field("Name") ?? unnamed.FirstOrDefault()) is { } script && HasScript(script.Integer));
        if (Is("StructureContains")) return new(Field("structure")?.Type == Gh3QbType.Structure && (Field("name") ?? unnamed.FirstOrDefault()) is { } name && Field("structure")!.Field(name.Integer) != null);
        if (Is("GetArraySize"))
        {
            var array = unnamed.FirstOrDefault(value => value.Type == Gh3QbType.Array) ?? Field("array");
            return Result("array_size", Number(array?.Items.Count ?? 0));
        }
        if (Is("SetArrayElement"))
        {
            var name = Field("arrayname")?.Integer ?? throw new InvalidDataException("Array name is missing");
            var local = caller.Locals.TryGetValue(name, out var array);
            array ??= _globals.GetValueOrDefault(name);
            if (array?.Type != Gh3QbType.Array) throw new InvalidDataException("Array value is missing");
            var index = checked((int)Numeric(Field("index") ?? throw new InvalidDataException("Array index is missing")));
            if (index < 0 || index >= array.Items.Count) throw new InvalidDataException("Array index exceeds its bounds");
            var items = array.Items.ToArray();
            items[index] = Field("newvalue") ?? throw new InvalidDataException("Array replacement value is missing");
            var updated = new Gh3QbValue(Gh3QbType.Array, -1, items);
            if (!local) _globals[name] = updated;
            return new(true, local ? new[] { new Gh3QbField(name, updated) } : null);
        }
        if (Is("Obj_GetID")) return Result("objID", new(Gh3QbType.Checksum, -1, context.Object));
        if (Is("ChecksumEquals") || Is("LocalizedStringEquals")) return new(Equal(Field("a"), Field("b")));
        if (Is("Change") || Is("Set"))
        {
            var fields = parameters.Fields.Where(field => field.Name != Gh3Checksum.Value("StructureName") && field.Name != 0).ToArray();
            if (Field("StructureName") is { } name)
            {
                var previous = _globals.GetValueOrDefault(name.Integer) ?? Structure(Array.Empty<Gh3QbField>());
                _globals[name.Integer] = Structure(previous.Fields.Where(field => !fields.Any(next => field.Name == next.Name)).Concat(fields));
            }
            else foreach (var field in fields) _globals[field.Name] = field.Value;
            return new(true);
        }
        if (Is("GetRandomValue"))
        {
            var low = Numeric(Field("a") ?? Number(0)); var high = Numeric(Field("b") ?? Number(1));
            Gh3QbValue value;
            if (parameters.Flag("integer")) value = _random.Range((float)low, (float)high, true);
            else
            {
                var resolution = checked((int)Numeric(Field("resolution") ?? Number(RandomValueResolution)));
                if (resolution < 2) throw new InvalidDataException("Random resolution must be at least two");
                value = new(Gh3QbType.Float, -1, _random.Next(resolution) * ((float)high - (float)low) / (resolution - 1) + (float)low);
            }
            return new(true, new[] { new Gh3QbField((Field("name") ?? Key("value")).Integer, value) });
        }
        if (Is("GetRandomSeeds"))
        {
            var seeds = new[] { _random.Seed1, _random.Seed2, _random.Seed3, _random2.Seed1, _random2.Seed2, _random2.Seed3 };
            return new(true, seeds.Select((seed, index) => new Gh3QbField(Intern("seed" + (index + 1)), Number(seed))).ToArray());
        }
        if (Is("SetRandomSeeds"))
        {
            var seeds = Enumerable.Range(1, 6).Select(index => checked((int)Numeric(Field("seed" + index) ?? throw new InvalidDataException("Random seed is missing")))).ToArray();
            (_random.Seed1, _random.Seed2, _random.Seed3) = (seeds[0], seeds[1], seeds[2]);
            (_random2.Seed1, _random2.Seed2, _random2.Seed3) = (seeds[3], seeds[4], seeds[5]);
            return new(true);
        }
        if (Is("ExtendCRC"))
        {
            var key = unnamed.FirstOrDefault(value => value.Type == Gh3QbType.Checksum) ?? throw new InvalidDataException("ExtendCRC needs a checksum");
            var text = unnamed.FirstOrDefault(value => value.Data is string)?.Data as string ?? "";
            return new(true, new[] { new Gh3QbField((Field("out") ?? Key("checksum")).Integer, new(Gh3QbType.Checksum, -1, Gh3Checksum.Append(key.Integer, text))) });
        }
        if (Is("FormatText"))
        {
            var format = unnamed.FirstOrDefault(value => value.Data is string)?.Data as string ?? throw new InvalidDataException("FormatText needs a format string");
            var text = Regex.Replace(format, "%([a-zA-Z%])", match => match.Groups[1].Value == "%" ? "%" : Text(Field(match.Groups[1].Value) ?? Key("undefined")));
            var name = Field("checksumName") ?? Field("textName") ?? throw new InvalidDataException("FormatText needs an output name");
            return new(true, new[] { new Gh3QbField(name.Integer, Field("checksumName") != null ? new(Gh3QbType.Checksum, -1, Intern(text)) : new(Gh3QbType.String, -1, text)) });
        }
        return null;
    }

    private string Text(Gh3QbValue value) => value.Data is string text ? text : value.Type is Gh3QbType.Integer or Gh3QbType.Float ? Numeric(value).ToString(CultureInfo.InvariantCulture) : Name(value.Integer);
    private static bool Equal(Gh3QbValue? a, Gh3QbValue? b) => a == null || b == null ? a == b : a.Type is Gh3QbType.Integer or Gh3QbType.Float && b.Type is Gh3QbType.Integer or Gh3QbType.Float ? Numeric(a) == Numeric(b) :
        a.Data is string aa && b.Data is string bb ? string.Equals(aa, bb, StringComparison.OrdinalIgnoreCase) : Gh3QbText.Encode(a) == Gh3QbText.Encode(b);

    private sealed class Cursor
    {
        private readonly Gh3ScriptRuntime _runtime;
        private readonly Gh3ScriptContext _context;
        private readonly Gh3ScriptCode _code;
        private readonly int _depth;
        private readonly Stack<(int Start, int Iteration)> _loops = new();
        private readonly Stack<(Gh3QbValue Value, bool Matched, bool Body)> _switches = new();
        private int _at;
        private int _expressionDepth;
        private byte Op => _at < _code.Tokens.Length ? _code.Tokens[_at].Op : Gh3ScriptOp.EndScript;
        private Gh3ScriptToken Take() => _code.Tokens[_at++];
        private void Jump(int offset) => _at = _code.Offsets[offset];

        public Cursor(Gh3ScriptRuntime runtime, Gh3ScriptContext context, Gh3ScriptCode code, int depth) { _runtime = runtime; _context = context; _code = code; _depth = depth; }

        public IEnumerable<Gh3ScriptWait> Run()
        {
            if (Op == Gh3ScriptOp.InlineStructure)
            {
                foreach (var field in _runtime.Resolve(Take().Value!).Fields)
                    if (field.Name != 0 && !_context.Locals.ContainsKey(field.Name))
                    { _context.Locals.Add(field.Name, field.Value); _context.Parameters.Add(field); }
            }
            var evaluateElseIf = false;
            while (_at < _code.Tokens.Length)
            {
                _runtime.Trace?.Invoke(_context.Script, _code.Tokens[_at].Offset, Op);
                if (_runtime._running?.Cancelled == true) yield break;
                if (++_runtime._work > MaximumStepsPerAdvance) throw new InvalidDataException("GH3 script execution exceeds its step limit");
                switch (Op)
                {
                    case Gh3ScriptOp.EndOfFile: case Gh3ScriptOp.EndScript: yield break;
                    case Gh3ScriptOp.EndOfLine: _at++; continue;
                    case Gh3ScriptOp.FastIf: case Gh3ScriptOp.ElseIf:
                        var branch = Take();
                        if (branch.Op == Gh3ScriptOp.ElseIf && !evaluateElseIf) { Jump(branch.Targets![1]); continue; }
                        evaluateElseIf = false;
                        if (!Condition())
                        {
                            Jump(branch.Targets![0]);
                            evaluateElseIf = Op == Gh3ScriptOp.ElseIf;
                            if (Op == Gh3ScriptOp.FastElse) _at++;
                        }
                        continue;
                    case Gh3ScriptOp.ShortBreak:
                        var jump = Take();
                        if (_switches.Count > 0)
                        {
                            if (!_switches.Peek().Body && Op is Gh3ScriptOp.Case or Gh3ScriptOp.Default) continue;
                            _switches.Pop();
                        }
                        Jump(jump.Targets![0]);
                        continue;
                    case Gh3ScriptOp.FastElse: case Gh3ScriptOp.Jump: Jump(Take().Targets![0]); continue;
                    case Gh3ScriptOp.EndIf: _at++; continue;
                    case Gh3ScriptOp.Begin: _at++; _loops.Push((_at, 0)); continue;
                    case Gh3ScriptOp.Repeat:
                        _at++;
                        var loop = _loops.Pop();
                        var count = Op is Gh3ScriptOp.EndOfLine or Gh3ScriptOp.EndScript ? double.PositiveInfinity : Numeric(Expression());
                        if (loop.Iteration + 1 < count) { _loops.Push((loop.Start, loop.Iteration + 1)); _at = loop.Start; }
                        continue;
                    case Gh3ScriptOp.Break:
                        _at++;
                        var depth = 0;
                        while (_at < _code.Tokens.Length)
                        {
                            if (Op == Gh3ScriptOp.Begin) depth++;
                            if (Op == Gh3ScriptOp.Repeat && depth-- == 0) { _at++; if (Op != Gh3ScriptOp.EndOfLine) _ = Expression(); break; }
                            _at++;
                        }
                        _loops.Pop();
                        continue;
                    case Gh3ScriptOp.Switch: _at++; _switches.Push((Expression(), false, false)); continue;
                    case Gh3ScriptOp.Case: case Gh3ScriptOp.Default:
                        var isDefault = Take().Op == Gh3ScriptOp.Default;
                        var next = Take();
                        if (next.Op != Gh3ScriptOp.ShortBreak) throw new InvalidDataException("Invalid QB switch branch");
                        var item = _switches.Pop();
                        var match = isDefault || Equal(item.Value, Expression());
                        if (!match && !item.Matched) Jump(next.Targets![0]);
                        _switches.Push((item.Value, item.Matched || match, false));
                        continue;
                    case Gh3ScriptOp.EndSwitch: _at++; _switches.Pop(); continue;
                    case Gh3ScriptOp.Return:
                        _at++;
                        var result = Parameters();
                        var first = result.Fields.FirstOrDefault(field => field.Name == 0)?.Value;
                        _context.Result = new(first == null || Truth(first), result.Fields.Where(field => field.Name != 0).ToArray());
                        yield break;
                    default:
                        if (_switches.Count > 0)
                        {
                            var active = _switches.Pop();
                            _switches.Push((active.Value, active.Matched, true));
                        }
                        var target = _context.Object;
                        var name = Expression(MemberPrecedence);
                        if (Op == Gh3ScriptOp.EqualSign)
                        {
                            _at++;
                            var assigned = Expression();
                            if (assigned.Type == Gh3QbType.None) _context.Locals.Remove(name.Integer);
                            else _context.Locals[name.Integer] = assigned;
                            continue;
                        }
                        if (Op == Gh3ScriptOp.Colon) { _at++; target = name.Integer; name = Expression(MemberPrecedence); }
                        var args = Parameters();
                        foreach (var wait in _runtime.Invoke(_context, name.Integer, args, target, _depth + 1)) yield return wait;
                        continue;
                }
            }
        }

        private bool Condition()
        {
            if (Op == Gh3ScriptOp.Not) { _at++; return !Condition(); }
            if (Op == Gh3ScriptOp.OpenParenthesis && _at + 3 < _code.Tokens.Length && _code.Tokens[_at + 1].Op == Gh3ScriptOp.Name && _code.Tokens[_at + 2].Op == Gh3ScriptOp.CloseParenthesis)
            {
                _at++;
                var name = Take().Value!.Integer;
                _at++;
                using var steps = _runtime.Invoke(_context, name, Structure(Array.Empty<Gh3QbField>()), depth: _depth + 1).GetEnumerator();
                if (steps.MoveNext()) throw new InvalidDataException("A script condition cannot wait");
                return _context.Result.Success;
            }
            if (Op == Gh3ScriptOp.Name && (_at + 1 == _code.Tokens.Length || _code.Tokens[_at + 1].Op is not (Gh3ScriptOp.EqualSign or Gh3ScriptOp.NotEqual or Gh3ScriptOp.LessThan or Gh3ScriptOp.LessThanEqual or Gh3ScriptOp.GreaterThan or Gh3ScriptOp.GreaterThanEqual)))
            {
                var name = Take().Value!.Integer;
                var parameters = Parameters();
                using var steps = _runtime.Invoke(_context, name, parameters, depth: _depth + 1).GetEnumerator();
                if (steps.MoveNext()) throw new InvalidDataException("A script condition cannot wait");
                return _context.Result.Success;
            }
            return Truth(Expression());
        }

        private Gh3QbValue Parameters(byte end = Gh3ScriptOp.EndOfLine)
        {
            var fields = new List<Gh3QbField>();
            while (_at < _code.Tokens.Length && Op != end && Op != Gh3ScriptOp.EndScript && Op != Gh3ScriptOp.CloseParenthesis && Op != Gh3ScriptOp.EndIf && Op != Gh3ScriptOp.ElseIf && Op != Gh3ScriptOp.FastElse)
            {
                if (Op == Gh3ScriptOp.Comma || end != Gh3ScriptOp.EndOfLine && Op == Gh3ScriptOp.EndOfLine) { _at++; continue; }
                if (Op == Gh3ScriptOp.AllArguments) { _at++; fields.AddRange(_context.Arguments.Fields); continue; }
                if (Op == Gh3ScriptOp.InlineStructure) { fields.AddRange(_runtime.Resolve(Take().Value!).Fields); continue; }
                var name = Op == Gh3ScriptOp.Name && _at + 1 < _code.Tokens.Length && _code.Tokens[_at + 1].Op == Gh3ScriptOp.EqualSign ? Take().Value!.Integer : 0;
                if (name != 0) _at++;
                var value = Expression();
                if (name == 0 && value.Type == Gh3QbType.Structure) fields.AddRange(value.Fields);
                else fields.Add(new(name, value));
                if (fields.Count > MaximumArguments) throw new InvalidDataException("Too many script arguments");
            }
            return EvaluatedStructure(fields);
        }

        private Gh3QbValue Expression(int minimum = 0)
        {
            if (++_expressionDepth > MaximumExpressionDepth) throw new InvalidDataException("Script expression exceeds its nesting limit");
            try
            {
                // Compiled QB keeps line breaks inside parentheses, for example after "||" (Progression_SetProgressionNodeFlags).
                while (Op == Gh3ScriptOp.EndOfLine && _at > 0 && _code.Tokens[_at - 1].Op is Gh3ScriptOp.OpenParenthesis or Gh3ScriptOp.Or or Gh3ScriptOp.And) _at++;
                var token = Take();
                Gh3QbValue value;
                switch (token.Op)
                {
                    case Gh3ScriptOp.Argument:
                        var local = Take().Value!.Integer;
                        value = minimum == MemberPrecedence && Op == Gh3ScriptOp.EqualSign ? new(Gh3QbType.Checksum, -1, local) : _context.Locals.GetValueOrDefault(local, Missing);
                        break;
                    case Gh3ScriptOp.GlobalReference:
                        var reference = Expression(MemberPrecedence);
                        value = _runtime._globals.GetValueOrDefault(reference.Integer, Missing);
                        break;
                    case Gh3ScriptOp.OpenParenthesis:
                        value = Expression();
                        if (value.Type == Gh3QbType.Checksum && (Op != Gh3ScriptOp.CloseParenthesis || _runtime.HasScript(value.Integer)))
                        {
                            using var call = _runtime.Invoke(_context, value.Integer, Parameters(Gh3ScriptOp.CloseParenthesis), depth: _depth + 1).GetEnumerator();
                            if (call.MoveNext()) throw new InvalidDataException("A script expression cannot wait");
                            value = Number(_context.Result.Success ? 1 : 0);
                        }
                        Require(Gh3ScriptOp.CloseParenthesis);
                        break;
                    case Gh3ScriptOp.StartStructure:
                        value = Parameters(Gh3ScriptOp.EndStructure);
                        Require(Gh3ScriptOp.EndStructure);
                        break;
                    case Gh3ScriptOp.StartArray:
                        var items = new List<Gh3QbValue>();
                        while (Op != Gh3ScriptOp.EndArray)
                        {
                            if (Op is Gh3ScriptOp.EndOfLine or Gh3ScriptOp.Comma) _at++;
                            else items.Add(Expression());
                            if (items.Count > MaximumArrayItems) throw new InvalidDataException("Script array exceeds its limit");
                        }
                        Require(Gh3ScriptOp.EndArray);
                        value = new(Gh3QbType.Array, -1, items.ToArray());
                        break;
                    case Gh3ScriptOp.Minus: value = Number(-Numeric(Expression(UnaryPrecedence))); break;
                    case Gh3ScriptOp.Not: value = Number(Truth(Expression(UnaryPrecedence)) ? 0 : 1); break;
                    case Gh3ScriptOp.RandomRange: case Gh3ScriptOp.RandomRange2: case Gh3ScriptOp.RandomFloat: case Gh3ScriptOp.RandomInteger:
                        var range = Expression(MemberPrecedence).Data as float[] ?? throw new InvalidDataException("RandomRange needs a pair");
                        if (range.Length != 2) throw new InvalidDataException("RandomRange needs two endpoints");
                        value = (token.Op == Gh3ScriptOp.RandomRange2 ? _runtime._random2 : _runtime._random).Range(range[0], range[1], token.Op is Gh3ScriptOp.RandomRange or Gh3ScriptOp.RandomRange2 ? null : token.Op == Gh3ScriptOp.RandomInteger);
                        break;
                    case Gh3ScriptOp.Random: case Gh3ScriptOp.Random2: case Gh3ScriptOp.RandomNoRepeat: case Gh3ScriptOp.RandomPermute:
                        var choices = token.Targets!;
                        int selected;
                        if (token.Op == Gh3ScriptOp.RandomNoRepeat)
                        {
                            var history = _runtime.RandomChoiceHistory(_context.Script, token, out var created);
                            if (created) history.Last = _runtime._random.Next(choices.Length);
                            selected = _runtime._random.Next(choices.Length);
                            for (var attempt = 0; selected == history.Last && attempt < RandomNoRepeatAttempts; attempt++) selected = _runtime._random.Next(choices.Length);
                            history.Last = selected;
                        }
                        else if (token.Op == Gh3ScriptOp.RandomPermute)
                        {
                            var history = _runtime.RandomChoiceHistory(_context.Script, token, out var created);
                            if (created)
                            {
                                history.Order = Enumerable.Range(0, choices.Length).ToArray();
                                Shuffle(history, false);
                            }
                            if (++history.Index >= choices.Length) { Shuffle(history, true); history.Index = 0; }
                            selected = history.Order[history.Index];
                        }
                        else
                        {
                            var total = token.Weights!.Sum(weight => (int)weight);
                            var selectedWeight = (token.Op == Gh3ScriptOp.Random2 ? _runtime._random2 : _runtime._random).Next(total);
                            selected = choices.Length - 1;
                            for (var index = 0; index < choices.Length; index++)
                                if ((selectedWeight -= token.Weights![index]) < 0) { selected = index; break; }
                        }
                        Jump(choices[selected]);
                        value = Expression(minimum);
                        if (Op == Gh3ScriptOp.Jump) Jump(Take().Targets![0]);
                        break;
                    default:
                        value = token.Value == null ? throw new InvalidDataException($"Unsupported expression opcode {token.Op:x2}") : _runtime.Resolve(token.Value);
                        break;
                }
                while (_at < _code.Tokens.Length)
                {
                    if (Op == Gh3ScriptOp.Dot && minimum < MemberPrecedence)
                    {
                        _at++;
                        var field = Expression(MemberPrecedence);
                        if (value.Data is float[] vector && field.Data is float[] other)
                        {
                            if (vector.Length != other.Length) throw new InvalidDataException("Script dot-product dimensions differ");
                            value = new(Gh3QbType.Float, -1, vector.Zip(other, (a, b) => a * b).Sum());
                        }
                        else value = value.Type == Gh3QbType.Structure ? value.Field(field.Integer) ?? Missing : Missing;
                        continue;
                    }
                    if (Op == Gh3ScriptOp.StartArray && value.Type == Gh3QbType.Array && minimum < MemberPrecedence)
                    {
                        _at++;
                        var index = checked((int)Numeric(Expression()));
                        Require(Gh3ScriptOp.EndArray);
                        value = index >= 0 && index < value.Items.Count ? value.Items[index] : Missing;
                        continue;
                    }
                    var priority = Op switch
                    {
                        Gh3ScriptOp.Or or Gh3ScriptOp.KeywordOr => OrPrecedence,
                        Gh3ScriptOp.And or Gh3ScriptOp.KeywordAnd => AndPrecedence,
                        Gh3ScriptOp.EqualSign or Gh3ScriptOp.NotEqual or Gh3ScriptOp.LessThan or Gh3ScriptOp.LessThanEqual or Gh3ScriptOp.GreaterThan
                            or Gh3ScriptOp.GreaterThanEqual => ComparisonPrecedence,
                        Gh3ScriptOp.Minus or Gh3ScriptOp.Add => AdditivePrecedence,
                        Gh3ScriptOp.Divide or Gh3ScriptOp.Multiply => MultiplicativePrecedence,
                        _ => -1
                    };
                    if (priority < minimum) break;
                    var op = Take().Op;
                    var right = Expression(priority + 1);
                    value = Binary(value, op, right);
                }
                return value;
            }
            finally { _expressionDepth--; }
        }

        private void Shuffle(RandomHistory history, bool avoidLast)
        {
            var order = history.Order;
            var last = order[^1];
            for (var count = 0; count < order.Length * ShuffleSwapsPerItem; count++)
            {
                var a = _runtime._random.Next(order.Length);
                var b = _runtime._random.Next(order.Length);
                (order[a], order[b]) = (order[b], order[a]);
            }
            if (avoidLast && order[0] == last)
            {
                var next = _runtime._random.Next(order.Length - 1) + 1;
                if (next < order.Length) (order[0], order[next]) = (order[next], order[0]);
            }
        }

        private static Gh3QbValue Binary(Gh3QbValue left, byte op, Gh3QbValue right)
        {
            if (op is Gh3ScriptOp.EqualSign or Gh3ScriptOp.NotEqual) return Number(Equal(left, right) == (op == Gh3ScriptOp.EqualSign) ? 1 : 0);
            if (op is Gh3ScriptOp.Or or Gh3ScriptOp.KeywordOr) return Number(Truth(left) || Truth(right) ? 1 : 0);
            if (op is Gh3ScriptOp.And or Gh3ScriptOp.KeywordAnd) return Number(Truth(left) && Truth(right) ? 1 : 0);
            if (left.Data is float[] || right.Data is float[])
            {
                var vector = left.Data as float[] ?? (float[])right.Data;
                var a = left.Data as float[] ?? Enumerable.Repeat((float)Numeric(left), vector.Length).ToArray();
                var b = right.Data as float[] ?? Enumerable.Repeat((float)Numeric(right), vector.Length).ToArray();
                if (a.Length != b.Length) throw new InvalidDataException("Script vector sizes differ");
                return new(vector.Length == 2 ? Gh3QbType.Pair : Gh3QbType.Vector, -1, a.Zip(b, (x, y) => (float)Numeric(Binary(Number(x), op, Number(y)))).ToArray());
            }
            var x = Numeric(left); var y = Numeric(right);
            return Number(op switch
            {
                Gh3ScriptOp.Add => x + y, Gh3ScriptOp.Minus => x - y, Gh3ScriptOp.Divide => x / y, Gh3ScriptOp.Multiply => x * y,
                Gh3ScriptOp.LessThan => x < y ? 1 : 0, Gh3ScriptOp.LessThanEqual => x <= y ? 1 : 0,
                Gh3ScriptOp.GreaterThan => x > y ? 1 : 0, Gh3ScriptOp.GreaterThanEqual => x >= y ? 1 : 0,
                _ => throw new InvalidDataException("Unsupported script operator")
            });
        }

        private void Require(byte op) { if (Op != op) throw new InvalidDataException($"Expected script token {op:x2}"); _at++; }
    }

    public void Dispose()
    {
        foreach (var task in _tasks.Values) { task.Cancelled = true; task.Steps.Dispose(); }
        _tasks.Clear();
        _blocked.Clear();
        _queue.Clear();
        _randomHistory.Clear();
    }
}
