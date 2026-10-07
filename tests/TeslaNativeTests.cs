using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class TeslaNativeTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_TESLA_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var actions = 0;
        var states = 0;
        foreach (var scene in document.RootElement.GetProperty("scenes").EnumerateArray())
        {
            using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue>(), (_, _, _) => null);
            var tesla = new StageTeslaState(runtime, _ => { });
            foreach (var row in scene.GetProperty("actions").EnumerateArray())
            {
                void Check(bool valid, string field)
                {
                    if (!valid) throw new Exception($"Native Tesla {field} differs at action {actions}");
                }
                if (row.TryGetProperty("delta", out var delta)) tesla.Update(delta.GetSingle());
                else
                {
                    var command = Gh3Checksum.Value(row.GetProperty("command").GetString()!);
                    var parameters = Gh3QbText.Decode(row.GetProperty("parameters").GetRawText());
                    var result = tesla.Execute(new(command, row.GetProperty("target").GetUInt32(), parameters.Fields), command, parameters);
                    Check(result?.Success == row.GetProperty("result").GetBoolean(), "result");
                }
                var expected = row.GetProperty("effects").EnumerateArray().ToArray();
                Check(tesla.Effects.Count(effect => effect != null) == expected.Length, "active count");
                foreach (var value in expected)
                {
                    var slot = value.GetProperty("slot").GetInt32();
                    var actual = tesla.Effects[slot];
                    Check(actual != null && actual.Slot == slot && actual.Id == value.GetProperty("id").GetUInt32(), "slot or ID");
                    foreach (var (name, field) in new[] { ("remaining", actual!.Remaining), ("speed", actual.Speed), ("scale", actual.Scale),
                        ("width", actual.Width), ("lightWidth", actual.LightWidth), ("fadeOut", actual.FadeOut),
                        ("amplitude", actual.Amplitude), ("falloffAlpha", actual.FalloffAlpha) })
                        Check(Math.Abs(field - value.GetProperty(name).GetSingle()) < .000001f, name);
                    Check(actual.Blend == value.GetProperty("blend").GetUInt32(), "blend");
                    foreach (var (name, vector, reflect) in new[] { ("start", actual.From, true), ("end", actual.To, true), ("color", actual.Color, false) })
                    {
                        var array = value.GetProperty(name);
                        Check(Vector3.Distance(vector, new(array[0].GetSingle(), array[1].GetSingle(), array[2].GetSingle() * (reflect ? -1 : 1))) < .000001f, name);
                    }
                    states++;
                }
                actions++;
            }
        }
        Console.WriteLine($"Native Tesla pool, defaults, parameters, duplicate names, lookup, deletion, and expiry passed: {actions} actions, {states} states.");
    }
}
