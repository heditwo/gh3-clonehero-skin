using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class StageVisualControlTests
{
    private static uint Key(string text) => Gh3Checksum.Value(text);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));
    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Name(string value) => Gh3ScriptRuntime.Key(value);
    private static Gh3QbValue Vector(float x, float y, float z) => new(6, -1, new[] { x, y, z });
    private static Gh3QbValue Array(params int[] values) => new(12, -1, values.Select(value => Number(value)).ToArray());

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gh3-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "movie.gh3video");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3VID01"));
                foreach (var value in new[] { 1, 1, 4, 2, 1 }) writer.Write(value);
                foreach (var value in new long[] { 68, 69, 70, 71, 72 }) writer.Write(value);
                writer.Write(new byte[] { 10, 20, 30, 40 });
            }
            var scriptPath = Path.Combine(directory, "scripts.json");
            File.WriteAllText(scriptPath, JsonSerializer.Serialize(new { version = 1, definitions = new { }, movies = new { movie = "movie.gh3video" } }));
            var bank = StageScriptBank.Load(scriptPath);
            var gaps = new HashSet<string>();
            using var movies = new StageMovieState(bank, gap => gaps.Add(gap));
            StageTeslaState? tesla = null;
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
            Gh3ScriptCallResult Movie(string command, Gh3QbValue parameters) => movies.Execute(Key(command), parameters, runtime)!;
            Gh3QbValue MovieParameters(bool wait, bool loop) => Gh3ScriptRuntime.Structure(Params(("movie", new(3, -1, "movie")), ("textureSlot", Number(0))).Fields.Concat(
                (wait ? System.Array.Empty<Gh3QbField>() : new[] { new Gh3QbField(0, Name("noWait")) }).Concat(loop ? System.Array.Empty<Gh3QbField>() : new[] { new Gh3QbField(0, Name("no_looping")) })));
            var call = Movie("PlayMovie", MovieParameters(true, false));
            var playback = movies.Slots[0];
            Check(playback.Frame(0) == 0 && playback.Frame(.5) == 1 && playback.Frame(1.999) == 3 && playback.Frame(3) == 3, "Movie start, frame boundaries, and final-frame hold differ.");
            Check(playback.Clip.ReadFrame(2).SequenceEqual(new byte[] { 30 }), "Movie frame index reads the wrong bytes.");
            Check(call.Waiting!(), "PlayMovie must wait unless noWait is present.");
            runtime.Advance(2);
            Check(!call.Waiting(), "A non-looping movie must release its script at completion.");
            call = Movie("PlayMovie", MovieParameters(true, true));
            Check(movies.Slots[0].Frame(4) == 0 && movies.Slots[0].Frame(4.5) == 1, "Movie loops do not use the source frame rate.");
            Movie("KillMovie", Params(("textureSlot", Number(0))));
            Check(!call.Waiting!() && movies.Slots.Count == 0, "KillMovie must release the waiting caller and clear its slot.");
            tesla = new(runtime, gap => gaps.Add(gap));
            Gh3ScriptCallResult Tesla(string command, Gh3QbValue parameters) => tesla.Execute(new(Key(command), 0, parameters.Fields), Key(command), parameters)!;
            var bolt = Params(("Name", Name("bolt")), ("start", Vector(1, 2, 3)), ("end", Vector(4, 5, 6)), ("time", Number(1)));
            Check(Tesla("CreateTeslaEffectObject", bolt).Success && Tesla("CreateTeslaEffectObject", bolt).Success, "Tesla creation must update an existing slot with the same ID.");
            var segments = tesla.Sample(2.25).ToArray();
            Check(segments.Length > 0 && segments[0].From == new Vector3(1, 2, -3) && segments[^1].To == new Vector3(4, 5, -6), "Tesla endpoints must retain reflected source coordinates.");
            Check(segments.SequenceEqual(tesla.Sample(2.25)), "Tesla sampling must remain stable during pause.");
            runtime.Advance(2.5);
            Tesla("SetTeslaEffectObject", Params(("Name", Name("bolt")), ("time", Number(1.75)), ("end", Vector(7, 8, 9))));
            runtime.Advance(3.25);
            Check(tesla.Exists(Key("bolt")) && tesla.Sample(3.25).Last().To == new Vector3(7, 8, -9), "Tesla updates must replace expiration and endpoints.");
            runtime.Advance(4.5);
            Check(!tesla.Exists(Key("bolt")), "Tesla lifetime must release its name.");
            Tesla("CreateTeslaEffectObject", bolt);
            Check(Tesla("DeleteTeslaEffectObject", Params(("Name", Name("bolt")))).Success && !tesla.Sample(4.5).Any(), "The source DeleteTeslaEffectObject command must remove all geometry.");
            var materials = new StageMaterialState(bank, runtime, gap => gaps.Add(gap));
            Gh3QbValue Material(Gh3QbValue value) => Params(("object", Name("panel")), ("material", Name("neon")), ("parameter", Name("m_psPass0MaterialColor")), ("value", value));
            materials.Execute(Key("UpdateMaterialProperty"), Material(Array(255, 128, 0, 0)));
            var target = new StageMaterialTarget(Key("panel"), Key("neon"), StageMaterialState.ColorParameter);
            Check(materials.Properties[target] == new Vector4(1, 128 * (1f / 255), 0, 0), "Material arrays must use native byte-color conversion.");
            materials.Execute(Key("UpdateMaterialProperty"), Material(Array(94, 255, 94)));
            Check(materials.Properties[target].W == 1, "A three-component material color must reset alpha to one.");
            materials.Execute(Key("UpdateMaterialProperty"), Material(Number(4)));
            Check(materials.Properties[target] == new Vector4(4, 0, 0, 0), "Scalar material parameters must retain their numeric value.");
            var temporary = Gh3ScriptRuntime.Structure(Material(Array(255, 0, 0)).Fields.Concat(new[] { new Gh3QbField(Key("time"), Number(1.9)) }));
            materials.Execute(Key("UpdateMaterialProperty"), temporary);
            runtime.Advance(5);
            Check(materials.Properties[target] == new Vector4(1, 0, 0, 1), "A timed material change must hold its value without interpolation.");
            materials.Execute(Key("UpdateMaterialProperty"), Material(Array(0, 255, 0)));
            runtime.Advance(5.499);
            Check(materials.Properties[target] == new Vector4(0, 1, 0, 1), "A later material change must apply immediately.");
            runtime.Advance(5.5);
            Check(materials.Properties[target] == new Vector4(4, 0, 0, 0), "Native material time truncates to whole seconds and restores the captured value even after a later update.");
            if (Environment.GetEnvironmentVariable("GH3_NATIVE_MATERIAL_ORACLE") is { } oraclePath)
            {
                using var oracle = JsonDocument.Parse(File.ReadAllText(oraclePath));
                foreach (var record in oracle.RootElement.GetProperty("records").EnumerateArray())
                {
                    using var clock = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
                    var original = new Vector4(.25f, .5f, .75f, 1);
                    var held = new StageMaterialState(bank, clock, gap => throw new Exception(gap), _ => original);
                    held.Execute(Key("UpdateMaterialProperty"), Gh3ScriptRuntime.Structure(Material(Array(255, 0, 0)).Fields.Concat(new[] {
                        new Gh3QbField(Key("time"), Number(record.GetProperty("seconds").GetDouble())) })));
                    foreach (var sample in record.GetProperty("samples").EnumerateArray())
                    {
                        clock.Advance(sample.GetProperty("milliseconds").GetDouble() / 1000);
                        var values = sample.GetProperty("value").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                        Check(held.Properties[target] == new Vector4(values[0], values[1], values[2], values[3]), "Material hold or restore differs from the original executable.");
                    }
                }
            }
            var animation = new StageTextureAnimation { Columns = 1, Rows = 2, Fps = 3, Offset = 1 };
            Check(animation.Transform(0) == new Vector4(1, .5f, 0, 0) && animation.Transform(.5) == new Vector4(1, .5f, 0, .5f), "The original Budokan atlas must follow its shader constants.");
            Check(animation.Transform(10, 0) == animation.Transform(0), "Poor performance must stop the Budokan atlas at its source offset.");
            Check(animation.Transform(.25, 2) != animation.Transform(.25, 4), "Medium and good performance must use their source atlas rates.");
            // GH3.exe material bind: mode 0 opaque, 1 additive (SRCALPHA, ONE), 3 alpha blend; flags bit 6 disables the alpha test,
            // bit 5 disables depth writes, bit 3 disables culling.
            var opaque = StageGh3RenderState.From(0x40, 0, 0, 0);
            Check(opaque is { SrcBlend: 1, DstBlend: 0, BlendOp: 0, ZWrite: true, AlphaTest: false, Cull: 2, Blended: false, Queue: 2000 }, "GH3 opaque materials draw without blending or alpha test.");
            var additive = StageGh3RenderState.From(0x08, 1, 1, 30);
            Check(additive is { SrcBlend: 5, DstBlend: 1, BlendOp: 0, ZWrite: true, AlphaTest: true, AlphaRef: 1, Cull: 0, Blended: true, Queue: 3030 },
                "GH3 additive materials add source alpha × colour, keep the alpha test, and sort by their draw order.");
            Check(StageGh3RenderState.From(1, 1, 32, 1, billboard: true) is { ZWrite: false, AlphaTest: true, Queue: 3001 } &&
                StageGh3RenderState.From(1, 3, 32, 1, billboard: true).ZWrite && StageGh3RenderState.From(1, 1, 32, 1).ZWrite,
                "An additive Flare billboard does not write depth, so it cannot remove the band's hair behind it.");
            var blended = StageGh3RenderState.From(0x20, 3, 20, 10);
            Check(blended is { SrcBlend: 5, DstBlend: 10, ZWrite: false, AlphaTest: true, AlphaRef: 20, Queue: 3010 }, "GH3 blend mode 3 is source-alpha blending.");
            Check(StageGh3RenderState.From(0, 4, 0, 0) is { SrcBlend: 0, DstBlend: 5, BlendOp: 0 } && StageGh3RenderState.From(0, 12, 0, 0) is { SrcBlend: 1, DstBlend: 1, BlendOp: 2 },
                "GH3 modulate and subtractive modes keep their D3D factors and operation.");
            Check(StageGh3RenderState.Address(3) == (true, true) && StageGh3RenderState.Address(0) == (false, false), "GH3 sampler address bits clamp U and V.");
            var templates = StageShaderTemplates.Parse("""{"version":1,"templates":{"9893d156":{"index":3,"name":"AnimatedTexture","shading":"unlit"}},"untranslated":{"12345678":"x: Dynamic branches"}}""");
            Check(templates.TryGet("9893d156", out var template) && template.Index == 3 && !templates.TryGet("12345678", out _) && templates.Untranslated.Count == 1,
                "The stage template table lists translated and untranslated GH3 templates.");
            var turn = new StageUvWibble(new[] { 0f, 0, 0, 0, 0, 0, 0, 0, .25f, .5f, .5f });
            var turned = turn.Transform(2);
            Check(MathF.Abs(turned.X - MathF.Cos(.5f)) < 1e-6f && MathF.Abs(turned.Y - MathF.Sin(.5f)) < 1e-6f && turned.Z == 0 && turned.W == 0 && turn.PivotU == .5f,
                "Wibble_Simple must turn the UV at c8 radians per second about its pivot.");
            var scroll = new StageUvWibble(new[] { .5f, .2f, 2, 1, -.1f, 0, 0, 0, 0, 0, 0 }).Transform(20);
            Check(MathF.Abs(scroll.Z - (10 + .2f * MathF.Sin(41)) % 8) < 1e-5f && MathF.Abs(scroll.W + 2) < 1e-5f && scroll.X == 1,
                "Wibble_Simple must scroll by c0·t + c1·sin(c2·t + c3) and wrap to ±8 like the vertex shader.");
            var controls = new StageRenderControlState(null, _ => true, gap => gaps.Add(gap));
            Gh3ScriptCallResult Control(string command, Gh3QbValue parameters) => controls.Execute(new(Key(command), Key("panel"), parameters.Fields), Key(command), parameters, runtime.Time)!;
            Control("SetOutlineShaderParams", Params(("min", Number(.002)), ("max", Number(.004)), ("rate", Number(1)), ("filldist", Number(200))));
            Control("SetOutlineShaderParams", Params(("max", Number(.25))));
            Check(controls.Outline == new StageOutlineSettings(0, .25f, 0, 0, 40, 0), "Each native outline parameter call must restore omitted values to its defaults.");
            Control("SetOutlineShaderColor", Params(("index", Number(2)), ("red", Number(17)), ("green", Number(34)), ("blue", Number(51)), ("alpha", Number(68))));
            Check(controls.OutlineColors[2] == new Vector4(17f / 255, 34f / 255, 51f / 255, 68f / 255), "Outline colors must retain native RGBA channels.");
            var screenTarget = new StageScreenTarget(Key("bg_viewport"), Key("test"));
            Gh3QbValue Screen(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(Params(
                ("viewport", Name("bg_viewport")), ("name", Name("test")), ("Type", Name("bright_sat"))).Fields.Concat(Params(fields).Fields));
            Control("ScreenFX_AddFXInstance", Screen(("hue", Number(.9)), ("red_mix", Array(1, 0, 0))));
            Control("ScreenFX_UpdateFXInstanceParams", Screen(("time", Number(2.0009)), ("hue", Number(.1)), ("red_mix", Array(0, 1, 0))));
            var screen = controls.ScreenEffects[screenTarget];
            var middle = screen.Sample(runtime.Time + 1);
            var hue = Gh3ScriptRuntime.Numeric(middle.Field("hue")!);
            Check(screen.Duration == 2 && Math.Min(Math.Abs(hue), Math.Abs(hue - 1)) < .000001 &&
                middle.Field("red_mix")!.Items.Select(Gh3ScriptRuntime.Numeric).SequenceEqual(new[] { .5, .5, 0 }),
                "Screen effects must use millisecond duration, shortest-path hue, and channel-mix interpolation.");
            Check(StageScreenColor.Matrix(Params(("ON", Number(0)), ("brightness", Number(4)))) == Matrix4x4.Identity,
                "A disabled color effect must leave every source pixel unchanged.");
            if (Environment.GetEnvironmentVariable("GH3_NATIVE_RENDER_ORACLE") is { } renderOracle)
            {
                using var oracle = JsonDocument.Parse(File.ReadAllText(renderOracle));
                foreach (var row in oracle.RootElement.GetProperty("outline").EnumerateArray())
                {
                    Control("SetOutlineShaderParams", Gh3ScriptRuntime.Structure(row.GetProperty("parameters").EnumerateObject().Select(field => new Gh3QbField(Key(field.Name), Number(field.Value.GetDouble())))));
                    var actual = new[] { controls.Outline.Min, controls.Outline.Max, controls.Outline.Rate, controls.Outline.ZScale, controls.Outline.FillDistance, controls.Outline.BlendMode };
                    Check(actual.SequenceEqual(row.GetProperty("values").EnumerateArray().Select(value => value.GetSingle())), "Outline defaults or replacement differs from the original executable.");
                }
            }
            if (Environment.GetEnvironmentVariable("GH3_NATIVE_SCREEN_ORACLE") is { } screenOracle)
            {
                using var oracle = JsonDocument.Parse(File.ReadAllText(screenOracle));
                foreach (var row in oracle.RootElement.GetProperty("records").EnumerateArray())
                {
                    var fields = new[] { "brightness", "contrast", "saturation", "hue" }.Select(name => new Gh3QbField(Key(name), Number(row.GetProperty(name).GetDouble()))).ToList();
                    var mixes = row.GetProperty("mix").EnumerateArray().ToArray();
                    foreach (var (name, index) in new[] { ("red_mix", 0), ("green_mix", 1), ("blue_mix", 2) })
                        fields.Add(new(Key(name), new(12, -1, mixes[index].EnumerateArray().Take(3).Select(value => Number(value.GetDouble())).ToArray())));
                    var matrix = StageScreenColor.Matrix(Gh3ScriptRuntime.Structure(fields));
                    var actual = new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                        matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
                    var expected = row.GetProperty("matrix").EnumerateArray().SelectMany(values => values.EnumerateArray()).Select(value => value.GetSingle());
                    Check(actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < .000002f), "Screen-color matrix differs from the original executable.");
                }
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Movie frame boundaries, loops, waits, Tesla lifetime, source endpoints, and material value checks passed.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
