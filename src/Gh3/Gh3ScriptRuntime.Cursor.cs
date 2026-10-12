namespace Gh3Skin;

// The interpreter of one script call: statements, conditions, loops, switches, and expressions over the token list.
internal sealed partial class Gh3ScriptRuntime
{
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
}
