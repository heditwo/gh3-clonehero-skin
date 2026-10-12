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
internal sealed partial class Gh3ScriptRuntime : IDisposable
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
    // Tuned: the mod's own seed for the script random generators. GH3 seeds one shared generator at boot and every script
    // draws from it, so GH3's script choices depend on everything that ran before and cannot be reproduced. A fixed seed
    // gives the same show each time a song plays.
    internal const int ScriptRandomSeed = 1;
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

    public void Dispose()
    {
        foreach (var task in _tasks.Values) { task.Cancelled = true; task.Steps.Dispose(); }
        _tasks.Clear();
        _blocked.Clear();
        _queue.Clear();
        _randomHistory.Clear();
    }
}
