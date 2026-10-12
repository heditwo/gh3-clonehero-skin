using System.Globalization;
using System.Text.RegularExpressions;

namespace Gh3Skin;

// The commands that GH3 runs in the engine (waits, spawned scripts, events, parameters, arrays, random values, text).
internal sealed partial class Gh3ScriptRuntime
{
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
}
